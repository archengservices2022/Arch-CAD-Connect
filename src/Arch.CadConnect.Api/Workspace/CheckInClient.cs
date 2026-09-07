using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.Workspace;

/// <summary>
/// Uploads the saved local file to the existing P3C check-in endpoint
/// (<c>POST /api/cad-documents/:id/checkin</c>), bearer-authenticated (P4C).
///
///   - hashes the local file (SHA-256 + size) BEFORE the request;
///   - streams it as the RAW request body (never multipart, never buffered)
///     with <c>X-Original-Filename</c>;
///   - origin-bound bearer, redirects refused, one timeout across the whole
///     exchange;
///   - VERIFIES the server-returned checksum + size equal the local hash - a
///     mismatch (a proxy mangled the body) is treated as a failure to trust,
///     not a success.
///
/// <c>cadDocumentId</c> is authoritative. The filename is metadata only.
/// Check-in creates a FileVersion ONLY - never an Engineering Revision.
/// </summary>
public sealed class CheckInClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ArchServerUri _server;
    private readonly IArchSession _session;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public CheckInClient(ArchServerUri server, IArchSession session, HttpClient http, TimeSpan? timeout = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
    }

    public static CheckInClient Create(ArchServerUri server, IArchSession session, TimeSpan? timeout = null)
        => new(server, session, HardenedHttp.NewClient(), timeout);

    /// <summary>
    /// Check in <paramref name="localFilePath"/> for <paramref name="cadDocumentId"/>.
    /// </summary>
    /// <exception cref="CheckInVerificationException">
    /// the server returned 201 but its checksum/size did not match the local
    /// file - the version WAS created and the lock WAS released; the caller
    /// must reconcile (mark Unverified, prompt Get Latest), never claim success.
    /// </exception>
    /// <exception cref="ArchApiException">
    /// a pre-201 failure (403 / 404 / 409 / 413 / transport) - the checkout is
    /// intact and no version was created.
    /// </exception>
    public async Task<CheckInOutcome> CheckInAsync(
        string cadDocumentId,
        string localFilePath,
        string? originalFileName,
        CancellationToken ct = default)
    {
        if (!File.Exists(localFilePath))
        {
            throw new ArchApiException(ArchApiFailureKind.BadRequest, "The local file to check in does not exist.");
        }

        var (localSha, localSize) = await HashFileAsync(localFilePath, ct).ConfigureAwait(false);

        var url = _server.ResolvePath($"/api/cad-documents/{Uri.EscapeDataString(cadDocumentId)}/checkin");
        if (!_server.MatchesOrigin(url))
        {
            throw new ArchApiException(ArchApiFailureKind.BadRequest,
                "Refusing to send an authenticated request to a different server.");
        }

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        HttpStatusCode status;
        string text;
        try
        {
            await using var file = new FileStream(
                localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(_session.AuthorizationHeaderValue());
            request.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrWhiteSpace(originalFileName))
            {
                request.Headers.TryAddWithoutValidation("X-Original-Filename", originalFileName);
            }
            var content = new StreamContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = localSize;
            request.Content = content;

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new ArchApiException(ArchApiFailureKind.Server,
                    "Arch PLM returned an unexpected response.", (int)response.StatusCode, "unexpected redirect");
            }

            status = response.StatusCode;
            text = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ArchApiException(ArchApiFailureKind.Timeout, "The Arch PLM server did not respond in time.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ArchApiException(ArchApiFailureKind.Network, "The request was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            throw new ArchApiException(ArchApiFailureKind.Network,
                "Could not reach the Arch PLM server. Check the address and your network.",
                diagnostic: ex.GetType().Name, inner: ex);
        }
        catch (IOException ex)
        {
            throw new ArchApiException(ArchApiFailureKind.Network,
                "The connection to the Arch PLM server was lost.", diagnostic: ex.GetType().Name, inner: ex);
        }

        if (status != HttpStatusCode.Created)
        {
            throw CheckoutHttpClient.MapFailure((int)status, text);
        }

        CheckInResultDto? dto;
        try { dto = JsonSerializer.Deserialize<CheckInResultDto>(text, Json); }
        catch (JsonException) { dto = null; }

        if (dto is null || string.IsNullOrEmpty(dto.FileVersionId) || dto.VersionNumber <= 0)
        {
            // The server said 201 but the body is unreadable: the version was
            // almost certainly created and the lock released. Surface as a
            // verification failure so the caller reconciles rather than
            // assuming failure OR success.
            throw new CheckInVerificationException(
                fileVersionId: null, versionNumber: null,
                "Check-in reported success but the response could not be read.");
        }

        var serverSizeMatches = dto.FileSize == localSize;
        var serverShaMatches = string.Equals(dto.Checksum, localSha, StringComparison.OrdinalIgnoreCase);
        if (!serverSizeMatches || !serverShaMatches)
        {
            throw new CheckInVerificationException(
                dto.FileVersionId, dto.VersionNumber,
                "The version was created on the server, but its checksum/size did not match the uploaded file.");
        }

        return new CheckInOutcome(dto.FileVersionId, dto.VersionNumber, localSha, localSize);
    }

    private static async Task<(string Sha256, long Size)> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return (Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
    }

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        const int max = 256 * 1024;
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max) { buffer.Write(chunk, 0, max - (int)buffer.Length); break; }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record CheckInResultDto(
        [property: JsonPropertyName("fileVersionId")] string FileVersionId,
        [property: JsonPropertyName("versionNumber")] int VersionNumber,
        [property: JsonPropertyName("checksum")] string Checksum,
        [property: JsonPropertyName("fileSize")] long FileSize);
}

/// <summary>The verified result of a successful check-in: the caller rebinds
///  the manifest to <see cref="FileVersionId"/> and sets the file controlled.</summary>
public sealed record CheckInOutcome(string FileVersionId, int VersionNumber, string Checksum, long FileSize);

/// <summary>
/// The server created the FileVersion and released the checkout (201), but the
/// client could not confirm the uploaded bytes. Server success is
/// authoritative: the caller MUST rebind the manifest to the new version
/// (marking it Unverified) - never leave the manifest on the old version -
/// and tell the user to run Get Latest.
/// </summary>
public sealed class CheckInVerificationException(string? fileVersionId, int? versionNumber, string message)
    : Exception(message)
{
    public string? FileVersionId { get; } = fileVersionId;
    public int? VersionNumber { get; } = versionNumber;
}
