using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Arch.CadConnect.Api.Workspace;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.CopyDesign;

/// <summary>
/// P6C: the dedicated client for the server's first-FileVersion
/// materialization endpoint (<c>POST /api/desktop/copy-design/materialize/:cadDocumentId</c>).
/// Replaces the earlier <c>CheckIn</c>-based workaround now that the server
/// contract gap it existed for has been closed - see the P6C Round 2 report;
/// there is no more "server contract gap" outcome, only genuine, controlled
/// failures.
///
/// Same hardening as <see cref="CheckInClient"/> / <see cref="CheckoutHttpClient"/> /
/// <see cref="CopyDesignApplyHttpClient"/>: origin-bound bearer, redirects
/// refused, one timeout across the whole exchange, raw server error text
/// never surfaced past a mapped <see cref="ArchApiException"/>. Never
/// accesses PostgreSQL directly.
///
/// The file is streamed as the RAW request body (never multipart, never
/// buffered whole) with the metadata this endpoint needs beyond the URL's
/// <c>cadDocumentId</c> sent as headers, exactly mirroring the established
/// <c>X-Original-Filename</c> convention:
///   X-Copy-Design-Operation-Id, X-Source-Cad-Document-Id, X-Document-Type,
///   X-Expected-Sha256, X-Expected-File-Size, X-Original-Filename.
///
/// RESPONSE VALIDATION (never trust a 200/201 alone): the deserialized
/// response is checked against the EXACT request that was sent -
/// <c>cadDocumentId</c>, <c>copyDesignOperationId</c>, <c>versionNumber == 1</c>,
/// <c>sha256</c>, <c>fileSize</c> must all match, and <c>fileVersionId</c>
/// must be non-blank. ANY mismatch is reported as
/// <see cref="CopyDesignMaterializationOutcome.Failed"/> - NEVER as
/// materialized success, and NEVER by throwing an exception that would
/// escape this client uncontrolled (this interface's contract is that every
/// outcome, controlled or not, comes back as a result).
/// </summary>
public sealed class CopyDesignMaterializeHttpClient : ICopyDesignMaterializer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ArchServerUri _server;
    private readonly IArchSession _session;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public CopyDesignMaterializeHttpClient(ArchServerUri server, IArchSession session, HttpClient http, TimeSpan? timeout = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
    }

    public static CopyDesignMaterializeHttpClient Create(ArchServerUri server, IArchSession session, TimeSpan? timeout = null)
        => new(server, session, HardenedHttp.NewClient(), timeout);

    public async Task<CopyDesignMaterializationResult> MaterializeFirstFileVersionAsync(
        CopyDesignMaterializeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!File.Exists(request.LocalFilePath))
        {
            return Failed(request, "The verified local file no longer exists - refusing to materialize.");
        }

        // Fail-closed sanity check against stale metadata: the file's
        // CURRENT size must still match what local verification measured
        // moments ago. This never re-hashes (verification already proved
        // the hash; re-reading the whole file again here would only widen
        // the TOCTOU window, not close it) - it is a cheap last-mile check
        // that the file has not been truncated/replaced since verification.
        var currentLength = new FileInfo(request.LocalFilePath).Length;
        if (currentLength != request.ExpectedFileSize)
        {
            return Failed(request,
                $"The local file's current size ({currentLength}) no longer matches the locally verified size " +
                $"({request.ExpectedFileSize}) - refusing to materialize a file that changed after verification.");
        }

        string documentTypeWire;
        try
        {
            documentTypeWire = CopyDesignWireMapping.DocumentTypeWireName(request.DocumentType);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Failed(request, $"Unsupported document type for materialization: {ex.Message}");
        }

        var url = _server.ResolvePath($"/api/desktop/copy-design/materialize/{Uri.EscapeDataString(request.ResultingCadDocumentId)}");
        if (!_server.MatchesOrigin(url))
        {
            return Failed(request, "Refusing to send an authenticated request to a different server.");
        }

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        HttpStatusCode status;
        string text;
        try
        {
            await using var file = new FileStream(
                request.LocalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Authorization = AuthenticationHeaderValue.Parse(_session.AuthorizationHeaderValue());
            httpRequest.Headers.Accept.ParseAdd("application/json");
            httpRequest.Headers.TryAddWithoutValidation("X-Copy-Design-Operation-Id", request.CopyDesignOperationId);
            httpRequest.Headers.TryAddWithoutValidation("X-Source-Cad-Document-Id", request.SourceCadDocumentId);
            httpRequest.Headers.TryAddWithoutValidation("X-Document-Type", documentTypeWire);
            httpRequest.Headers.TryAddWithoutValidation("X-Expected-Sha256", request.ExpectedSha256);
            httpRequest.Headers.TryAddWithoutValidation("X-Expected-File-Size", request.ExpectedFileSize.ToString());
            if (!string.IsNullOrWhiteSpace(request.FileName))
            {
                httpRequest.Headers.TryAddWithoutValidation("X-Original-Filename", request.FileName);
            }

            var content = new StreamContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = currentLength;
            httpRequest.Content = content;

            using var response = await _http
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                return Failed(request, "Arch PLM returned an unexpected redirect response.");
            }

            status = response.StatusCode;
            text = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Failed(request, "The Arch PLM server did not respond in time.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failed(request, "The request was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            return Failed(request, $"Could not reach the Arch PLM server: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Failed(request, $"The connection to the Arch PLM server was lost, or the local file could not be read: {ex.Message}");
        }

        if (status is not (HttpStatusCode.OK or HttpStatusCode.Created))
        {
            ArchApiException mapped;
            try { mapped = CheckoutHttpClient.MapFailure((int)status, text); }
            catch (Exception ex) { return Failed(request, $"Materialization failed ({(int)status}): {ex.Message}"); }
            return Failed(request, mapped.Message);
        }

        WireResponseDto? dto;
        try { dto = JsonSerializer.Deserialize<WireResponseDto>(text, Json); }
        catch (JsonException) { dto = null; }

        if (dto is null)
        {
            return Failed(request, "Arch PLM returned an unexpected response body.");
        }

        // ---- MANDATORY post-response validation - never trust a 200/201 alone ----
        if (!string.Equals(dto.CadDocumentId, request.ResultingCadDocumentId, StringComparison.Ordinal))
        {
            return Failed(request, "The server's response named a different cadDocumentId than the one materialized - refusing to accept.");
        }
        if (!string.Equals(dto.CopyDesignOperationId, request.CopyDesignOperationId, StringComparison.Ordinal))
        {
            return Failed(request, "The server's response named a different copyDesignOperationId than expected - refusing to accept.");
        }
        if (dto.VersionNumber != 1)
        {
            return Failed(request, $"The server reported versionNumber {dto.VersionNumber}, not 1 - refusing to accept.");
        }
        if (string.IsNullOrWhiteSpace(dto.FileVersionId))
        {
            return Failed(request, "The server's response did not carry a fileVersionId - refusing to accept.");
        }
        if (!string.Equals(dto.Sha256, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Failed(request, "The server's reported SHA-256 does not match the locally verified final SHA-256 - refusing to accept.");
        }
        if (dto.FileSize != request.ExpectedFileSize)
        {
            return Failed(request, "The server's reported file size does not match the locally verified final file size - refusing to accept.");
        }

        return new CopyDesignMaterializationResult(
            request.ResultingCadDocumentId, CopyDesignMaterializationOutcome.Materialized,
            dto.FileVersionId, dto.VersionNumber, dto.Sha256, dto.FileSize,
            status == HttpStatusCode.OK ? "Materialized (idempotent replay verified)." : "Materialized.");
    }

    private static CopyDesignMaterializationResult Failed(CopyDesignMaterializeRequest request, string detail) =>
        new(request.ResultingCadDocumentId, CopyDesignMaterializationOutcome.Failed,
            FileVersionId: null, VersionNumber: null, Sha256: null, FileSize: null, detail);

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

    private sealed record WireResponseDto(
        [property: JsonPropertyName("cadDocumentId")] string? CadDocumentId,
        [property: JsonPropertyName("fileVersionId")] string? FileVersionId,
        [property: JsonPropertyName("versionNumber")] int VersionNumber,
        [property: JsonPropertyName("fileName")] string? FileName,
        [property: JsonPropertyName("fileSize")] long FileSize,
        [property: JsonPropertyName("sha256")] string? Sha256,
        [property: JsonPropertyName("copyDesignOperationId")] string? CopyDesignOperationId);
}
