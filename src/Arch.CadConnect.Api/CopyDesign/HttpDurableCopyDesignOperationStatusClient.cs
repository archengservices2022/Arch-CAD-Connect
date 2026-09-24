using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.CopyDesign;

file static class OpaqueId
{
    public static bool HasSurroundingWhitespace(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
}

/// <summary>
/// P6D PRODUCTION RECOVERY: <see cref="IDurableCopyDesignOperationStatusClient"/>
/// over the authenticated Arch PLM HTTP API - a RAW, structurally-validated
/// (but NOT expectation-cross-validated) lookup of ONE Copy Design
/// operation's authoritative status, used ONLY for DURABLE RESUME DISCOVERY
/// (see <see cref="CopyDesignResumeAttemptReconstructor"/>'s own doc comment
/// for why this is a SEPARATE client from <see cref="HttpCopyDesignOperationStatusProbe"/>,
/// which that unchanged, existing class - and the orchestrator's own
/// resume-skip pipeline - continue to use exactly as before).
///
/// Hits the SAME, unmodified, read-only server endpoint
/// (<c>GET /api/desktop/copy-design/operations/{operationId}/status</c>) -
/// only its response DTO gained the additive <c>sourceFileVersionId</c>
/// field this discovery flow needs; the endpoint's contract, auth, and
/// scope are otherwise unchanged. Reuses the established desktop-client
/// transport rules: origin-bound bearer, ALL redirects refused, a single
/// timeout across connect + headers + body, a defensively capped response,
/// no logging.
///
/// FAIL CLOSED: NEVER throws for a transport, auth, contract, or local
/// request-construction failure - every such case becomes a
/// <see cref="CopyDesignDurableResumeStatusOutcome"/> other than
/// <see cref="CopyDesignDurableResumeStatusOutcome.Found"/>. Validates EVERY
/// entry's own internal shape (contract, action/state validity, required
/// fields, MATERIALIZED completeness/canonical-integrity, and that a
/// PENDING/INVALID/REUSED entry never contradictorily carries
/// materialization metadata) and rejects a duplicate resultingCadDocumentId
/// - the SAME per-entry structural rules <see cref="HttpCopyDesignOperationStatusProbe"/>
/// already enforces. UNLIKE that class, this one has no caller expectation
/// to cross-validate against (there is none yet - discovering one IS the
/// point) - ordinal contiguity/gap/order validation is the CALLER's
/// responsibility (<see cref="CopyDesignResumeAttemptReconstructor"/>).
/// </summary>
public sealed class HttpDurableCopyDesignOperationStatusClient : IDurableCopyDesignOperationStatusClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private const int MaxResponseBodyBytes = 256 * 1024;

    private readonly ArchServerUri _server;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public HttpDurableCopyDesignOperationStatusClient(ArchServerUri server, HttpClient http, TimeSpan? timeout = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<CopyDesignDurableResumeStatusResult> GetRawStatusAsync(
        IArchSession session, string operationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!_server.MatchesOrigin(session.Server.Origin))
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
        }
        if (string.IsNullOrWhiteSpace(operationId) || OpaqueId.HasSurroundingWhitespace(operationId))
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
        }

        try
        {
            return await SendAndInterpretAsync(session, operationId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.ServerUnavailable);
        }
        catch (HttpRequestException)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.ServerUnavailable);
        }
        catch (SocketException)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.ServerUnavailable);
        }
        catch (IOException)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.ServerUnavailable);
        }
        catch (Exception ex) when (
            ex is UriFormatException or FormatException or ArgumentException
               or InvalidOperationException or NotSupportedException
               or JsonException or InvalidDataException or OverflowException
               or ObjectDisposedException or DecoderFallbackException)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
        }
    }

    private async Task<CopyDesignDurableResumeStatusResult> SendAndInterpretAsync(
        IArchSession session, string operationId, CancellationToken ct)
    {
        var url = _server.ResolvePath("/api/desktop/copy-design/operations/" + Uri.EscapeDataString(operationId) + "/status");
        if (!_server.MatchesOrigin(url))
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
        }

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(session.AuthorizationHeaderValue());
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);

        var code = (int)response.StatusCode;
        if (code is 401 or 403)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.AuthenticationFailed);
        }
        if (code is >= 300 and < 400)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.ServerUnavailable);
        }
        if (code == 404)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.NotFound);
        }

        var body = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
        return Interpret(operationId, response.StatusCode, body);
    }

    private static CopyDesignDurableResumeStatusResult Interpret(string requestedOperationId, HttpStatusCode status, string body)
    {
        var code = (int)status;
        if (code is >= 300 and < 400)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.ServerUnavailable);
        }
        if (code is 401 or 403)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.AuthenticationFailed);
        }
        if (code == 404)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.NotFound);
        }
        if (code != 200)
        {
            return new CopyDesignDurableResumeStatusResult(
                code is >= 500 and < 600
                    ? CopyDesignDurableResumeStatusOutcome.ServerUnavailable
                    : CopyDesignDurableResumeStatusOutcome.MalformedResponse);
        }

        StatusResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<StatusResponseDto>(body, Json);
        }
        catch (JsonException)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
        }

        if (dto is null
            || dto.Contract != "arch-plm.copy-design-operation-status.v1"
            || string.IsNullOrWhiteSpace(dto.CopyDesignOperationId)
            || dto.CopyDesignOperationId != requestedOperationId
            || string.IsNullOrWhiteSpace(dto.IdempotencyKey)
            || dto.Entries is null)
        {
            return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
        }

        var entries = new List<CopyDesignDurableResumeEntry>();
        var seenResultingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in dto.Entries)
        {
            if (e is null
                || string.IsNullOrWhiteSpace(e.Action)
                || string.IsNullOrWhiteSpace(e.State)
                || string.IsNullOrWhiteSpace(e.ResultingCadDocumentId)
                || OpaqueId.HasSurroundingWhitespace(e.ResultingCadDocumentId)
                || string.IsNullOrWhiteSpace(e.OriginalDocumentNumber)
                || string.IsNullOrWhiteSpace(e.OriginalFileName)
                || string.IsNullOrWhiteSpace(e.OriginalDocumentType))
            {
                return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
            }
            if (!seenResultingIds.Add(e.ResultingCadDocumentId))
            {
                return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
            }

            bool CarriesMaterializationMetadata() =>
                e.FileVersionId is not null || e.VersionNumber is not null || e.Sha256 is not null || e.FileSize is not null;

            if (e.Action == "COPY")
            {
                if (string.IsNullOrWhiteSpace(e.SourceCadDocumentId) || OpaqueId.HasSurroundingWhitespace(e.SourceCadDocumentId)
                    || string.IsNullOrWhiteSpace(e.SourceFileVersionId) || OpaqueId.HasSurroundingWhitespace(e.SourceFileVersionId))
                {
                    return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
                }

                if (e.State is "PENDING" or "INVALID")
                {
                    if (CarriesMaterializationMetadata())
                    {
                        return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
                    }
                    entries.Add(new CopyDesignDurableResumeEntry(
                        e.Ordinal, e.Action, e.State, e.SourceCadDocumentId, e.SourceFileVersionId, e.ResultingCadDocumentId,
                        e.OriginalDocumentNumber, e.OriginalFileName, e.OriginalDocumentType, e.OriginalDescription));
                }
                else if (e.State == "MATERIALIZED")
                {
                    if (string.IsNullOrWhiteSpace(e.FileVersionId) || OpaqueId.HasSurroundingWhitespace(e.FileVersionId)
                        || e.VersionNumber != 1
                        || e.FileSize is not { } fileSize || fileSize <= 0 || !FileVersionIntegrity.IsRepresentableFileSize(fileSize)
                        || !FileVersionIntegrity.IsCanonicalSha256(e.Sha256))
                    {
                        return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
                    }
                    entries.Add(new CopyDesignDurableResumeEntry(
                        e.Ordinal, e.Action, e.State, e.SourceCadDocumentId, e.SourceFileVersionId, e.ResultingCadDocumentId,
                        e.OriginalDocumentNumber, e.OriginalFileName, e.OriginalDocumentType, e.OriginalDescription,
                        e.FileVersionId, e.VersionNumber, e.Sha256, e.FileSize));
                }
                else
                {
                    return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
                }
            }
            else if (e.Action == "REUSE")
            {
                if (e.State != "REUSED" || e.SourceCadDocumentId is not null || e.SourceFileVersionId is not null || CarriesMaterializationMetadata())
                {
                    return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
                }
                entries.Add(new CopyDesignDurableResumeEntry(
                    e.Ordinal, e.Action, e.State, null, null, e.ResultingCadDocumentId,
                    e.OriginalDocumentNumber, e.OriginalFileName, e.OriginalDocumentType, e.OriginalDescription));
            }
            else
            {
                return new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.MalformedResponse);
            }
        }

        return new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, dto.CopyDesignOperationId, dto.IdempotencyKey, entries);
    }

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxResponseBodyBytes)
        {
            throw new InvalidDataException("The Arch PLM response body exceeds the size cap.");
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBodyBytes)
            {
                throw new InvalidDataException("The Arch PLM response body exceeds the size cap.");
            }
            buffer.Write(chunk, 0, read);
        }

        return StrictUtf8.GetString(buffer.ToArray());
    }

    private sealed record StatusResponseDto(
        string Contract = "",
        string? CopyDesignOperationId = null,
        string? IdempotencyKey = null,
        string? PerformedAt = null,
        IReadOnlyList<StatusEntryDto>? Entries = null);

    private sealed record StatusEntryDto(
        int Ordinal = 0,
        string? Action = null,
        string? State = null,
        string? SourceCadDocumentId = null,
        string? SourceFileVersionId = null,
        string? ResultingCadDocumentId = null,
        string? OriginalDocumentNumber = null,
        string? OriginalFileName = null,
        string? OriginalDocumentType = null,
        string? OriginalDescription = null,
        string? FileVersionId = null,
        int? VersionNumber = null,
        string? Sha256 = null,
        long? FileSize = null);
}
