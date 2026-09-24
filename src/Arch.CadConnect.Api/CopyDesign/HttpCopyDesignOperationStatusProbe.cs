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
    /// <summary>A stable identifier is opaque - leading / trailing whitespace is
    ///  a contract violation, NOT something to normalise away into a valid
    ///  identity.</summary>
    public static bool HasSurroundingWhitespace(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
}

/// <summary>
/// P6D ROUND 3, HIGH fix (items D/E): <see cref="ICopyDesignOperationStatusClient"/>
/// over the authenticated Arch PLM HTTP API - REPLACES round 2's
/// <c>CopyDesignMaterializationStatusHttpProbe</c> (which reused the generic
/// `latest-versions` endpoint). Reuses the established desktop-client
/// transport rules exactly like <see cref="Arch.CadConnect.Api.References.HttpLatestVersionProbe"/>:
/// origin-bound bearer, ALL redirects refused, a single timeout across
/// connect + headers + body, a defensively capped response, no logging.
///
/// FAIL CLOSED: this method NEVER throws for a transport, auth, contract, or
/// local request-construction failure - every such case becomes a
/// <see cref="CopyDesignOperationStatusOutcome"/> OTHER than <see cref="CopyDesignOperationStatusOutcome.Found"/>,
/// which the orchestrator treats as "cannot safely determine resume state -
/// fail closed" (never "proceed as fresh" - see
/// <see cref="CopyDesignApplyOrchestrator"/>'s step 6b for the full contrast
/// with round 2's more permissive behavior).
///
/// ---------------------------------------------------------------------------
/// SERVER CONTRACT (P6D ROUND 3): implemented in the Arch PLM web repository
/// as the authenticated, tenant-scoped, READ-ONLY endpoint below.
///
///   GET /api/desktop/copy-design/operations/{operationId}/status
///
///   Auth : Authorization: Bearer &lt;desktop session token&gt;.
///   Scope: READ-ONLY. Never creates, updates, or deletes anything.
///
///   200 response body:
///   {
///     "contract": "arch-plm.copy-design-operation-status.v1",
///     "copyDesignOperationId": "&lt;id&gt;",
///     "idempotencyKey": "&lt;key&gt;",
///     "performedAt": "&lt;ISO 8601&gt;",
///     "entries": [
///       { "ordinal": 0, "action": "COPY", "state": "PENDING",
///         "sourceCadDocumentId": "&lt;id&gt;", "resultingCadDocumentId": "&lt;id&gt;",
///         "originalDocumentNumber": "1001", "originalFileName": "1001.ipt",
///         "originalDocumentType": "IPT" },
///       { "ordinal": 1, "action": "COPY", "state": "MATERIALIZED", ...,
///         "fileVersionId": "&lt;fvId&gt;", "versionNumber": 1,
///         "sha256": "&lt;64 lowercase hex&gt;", "fileSize": 12345 },
///       { "ordinal": 2, "action": "REUSE", "state": "REUSED", ... }
///     ]
///   }
///
///   - "action" MUST be exactly "COPY" or "REUSE"; anything else is malformed.
///   - "state" MUST be one of "PENDING" / "MATERIALIZED" / "INVALID" (COPY
///     only) or "REUSED" (REUSE only); a state inconsistent with the entry's
///     action, or any other value, is malformed.
///   - "state": "MATERIALIZED" REQUIRES a non-blank "fileVersionId", exactly
///     "versionNumber": 1, and canonical "sha256"/"fileSize" (the SAME
///     canonical shape latest-versions requires) - any missing/malformed
///     field makes that entry MalformedResponse (the WHOLE lookup fails
///     closed - see below).
///   - 401 / 403 -&gt; AuthenticationFailed. 404 -&gt; NotFound (unknown
///     operationId, including one in a different tenant). 5xx / timeout /
///     redirect -&gt; ServerUnavailable. Any other non-200, or a body that
///     fails contract/shape validation, -&gt; MalformedResponse.
///   - UNLIKE latest-versions' per-id degrade: a malformed ENTRY anywhere in
///     the response fails the WHOLE lookup closed (MalformedResponse) rather
///     than degrading just that one entry - a caller resuming a specific
///     operation must never partially trust a response it cannot fully
///     validate.
///   - P6D ROUND 4, item E: a "PENDING" / "INVALID" (COPY) or "REUSED"
///     (REUSE) entry that ALSO carries ANY of fileVersionId / versionNumber /
///     sha256 / fileSize is CONTRADICTORY and makes the WHOLE lookup
///     MalformedResponse - those fields are meaningful ONLY for
///     "MATERIALIZED", never silently ignored when present elsewhere.
///   - P6D ROUND 4, item D: a duplicate "ordinal" across two entries is ALSO
///     internally inconsistent and fails the WHOLE lookup closed, exactly
///     like a duplicate "resultingCadDocumentId". Full cross-validation
///     against the CALLER's own confirmed reservation (entry count, ordinal
///     contiguity/order, immutable-snapshot exactness) is the ORCHESTRATOR's
///     responsibility (see <see cref="CopyDesignApplyOrchestrator"/>'s step
///     6b) - this probe alone cannot know what the caller actually reserved.
///   - P6D ROUND 5, item A: RESPONSE ORDER is enforced, not merely the SET of
///     ordinals - the entry at response index i must carry the expectation's
///     ordinal at index i. A shuffled/reversed/reordered-but-otherwise-valid
///     response (every ordinal present exactly once, every field otherwise
///     correct) is STILL MalformedResponse.
///   - P6D ROUND 5, item B: "state": "MATERIALIZED" additionally requires
///     "fileSize" to be STRICTLY POSITIVE (never 0) - a materialized
///     FileVersion 1 is a real physically-copied binary.
/// ---------------------------------------------------------------------------
/// </summary>
public sealed class HttpCopyDesignOperationStatusProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>STRICT UTF-8: invalid bytes throw <see cref="DecoderFallbackException"/>
    ///  rather than being silently repaired with U+FFFD, so corrupt bytes can
    ///  never reach the JSON parser as trusted authoritative data.</summary>
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Defensive cap on how much of a response body we buffer. An
    ///  over-cap body FAILS - it is never truncated and parsed as a prefix.</summary>
    private const int MaxResponseBodyBytes = 256 * 1024;

    private readonly ArchServerUri _server;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public HttpCopyDesignOperationStatusProbe(ArchServerUri server, HttpClient http, TimeSpan? timeout = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<CopyDesignOperationStatusResult> LookupAsync(
        IArchSession session, CopyDesignOperationStatusExpectation expectation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(expectation);
        var operationId = expectation.OperationId;

        if (!_server.MatchesOrigin(session.Server.Origin))
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
        }
        if (string.IsNullOrWhiteSpace(operationId) || OpaqueId.HasSurroundingWhitespace(operationId))
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
        }

        try
        {
            return await SendAndInterpretAsync(session, expectation, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.ServerUnavailable);
        }
        catch (HttpRequestException)
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.ServerUnavailable);
        }
        catch (SocketException)
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.ServerUnavailable);
        }
        catch (IOException)
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.ServerUnavailable);
        }
        catch (Exception ex) when (
            ex is UriFormatException or FormatException or ArgumentException
               or InvalidOperationException or NotSupportedException
               or JsonException or InvalidDataException or OverflowException
               or ObjectDisposedException or DecoderFallbackException)
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
        }
    }

    private async Task<CopyDesignOperationStatusResult> SendAndInterpretAsync(
        IArchSession session, CopyDesignOperationStatusExpectation expectation, CancellationToken ct)
    {
        var url = _server.ResolvePath("/api/desktop/copy-design/operations/" + Uri.EscapeDataString(expectation.OperationId) + "/status");
        if (!_server.MatchesOrigin(url))
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
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
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.AuthenticationFailed);
        }
        if (code is >= 300 and < 400)
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.ServerUnavailable);
        }
        if (code == 404)
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.NotFound);
        }

        var body = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
        return Interpret(expectation, response.StatusCode, body);
    }

    private static CopyDesignOperationStatusResult Interpret(
        CopyDesignOperationStatusExpectation expectation, HttpStatusCode status, string body)
    {
        var requestedOperationId = expectation.OperationId;
        var code = (int)status;

        if (code is >= 300 and < 400)
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.ServerUnavailable);
        }
        if (code is 401 or 403)
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.AuthenticationFailed);
        }
        if (code == 404)
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.NotFound);
        }
        if (code != 200)
        {
            return CopyDesignOperationStatusResult.Failure(
                code is >= 500 and < 600
                    ? CopyDesignOperationStatusOutcome.ServerUnavailable
                    : CopyDesignOperationStatusOutcome.MalformedResponse);
        }

        StatusResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<StatusResponseDto>(body, Json);
        }
        catch (JsonException)
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
        }

        if (dto is null
            || dto.Contract != "arch-plm.copy-design-operation-status.v1"
            || string.IsNullOrWhiteSpace(dto.CopyDesignOperationId)
            || dto.CopyDesignOperationId != requestedOperationId
            || string.IsNullOrWhiteSpace(dto.IdempotencyKey)
            // P6D ROUND 4, item D: "idempotencyKey exact" - compared against
            // the CALLER's own expectation, never merely "non-blank".
            || dto.IdempotencyKey != expectation.IdempotencyKey
            || dto.Entries is null
            // P6D ROUND 4, item D: "entry count exact" - a response with
            // more or fewer entries than the caller's confirmed reservation
            // can NEVER be trusted, regardless of what the extra/missing
            // entries look like individually.
            || dto.Entries.Count != expectation.Entries.Count)
        {
            return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
        }

        // P6D ROUND 4, item D: every response entry must correspond to
        // EXACTLY one expected ordinal - built once, consumed (never
        // re-matched) as each response entry is validated below, so a
        // response entry naming an ordinal the caller never reserved at all
        // is caught as "extra/unexpected ordinal" via the failed dictionary
        // lookup, and (combined with the entry-count-exact check above and
        // the duplicate-ordinal check below) an omitted expected ordinal is
        // caught too - the two counts being equal, plus every response
        // ordinal being a valid, DISTINCT expected ordinal, together prove a
        // complete bijection.
        var expectedByOrdinal = expectation.Entries.ToDictionary(x => x.Ordinal);

        var entries = new List<CopyDesignOperationStatusEntry>();
        var seenResultingIds = new HashSet<string>(StringComparer.Ordinal);
        var seenOrdinals = new HashSet<int>();
        for (var i = 0; i < dto.Entries.Count; i++)
        {
            var e = dto.Entries[i];
            if (e is null
                || string.IsNullOrWhiteSpace(e.ResultingCadDocumentId)
                || OpaqueId.HasSurroundingWhitespace(e.ResultingCadDocumentId)
                || string.IsNullOrWhiteSpace(e.OriginalDocumentNumber)
                || string.IsNullOrWhiteSpace(e.OriginalFileName)
                || string.IsNullOrWhiteSpace(e.OriginalDocumentType))
            {
                return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
            }
            if (!seenResultingIds.Add(e.ResultingCadDocumentId))
            {
                // Duplicate resultingCadDocumentId - internally inconsistent
                // response. The WHOLE lookup fails closed, never "last wins".
                return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
            }
            // P6D ROUND 4, item D: ordinals must be unique - a duplicate
            // ordinal is ALSO internally inconsistent (two entries cannot
            // both occupy the SAME position in the original request array),
            // regardless of whether their resultingCadDocumentId differs.
            if (!seenOrdinals.Add(e.Ordinal))
            {
                return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
            }
            // P6D ROUND 5, item A: REQUEST ORDER is enforced, not merely
            // ordinal uniqueness/contiguity - the entry at response index i
            // MUST carry the SAME ordinal as the expectation's entry at
            // index i (which is itself in stable, immutable operation/
            // request order - see CopyDesignOperationStatusExpectation's own
            // doc comment). A response that names every correct ordinal but
            // in the WRONG row order (shuffled/reversed/one entry moved) is
            // just as untrustworthy as a missing/extra/duplicate ordinal -
            // it is never merely re-sorted and accepted.
            if (i >= expectation.Entries.Count || expectation.Entries[i].Ordinal != e.Ordinal)
            {
                return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
            }
            // P6D ROUND 4, item D: this ordinal must be one the caller
            // actually reserved, and EVERY field the server claims for it
            // must match EXACTLY what the caller's own confirmed reservation
            // decided - never merely "an entry exists for some id I asked
            // about" (round 3's weaker check).
            //
            // OriginalDescription is checked for COPY entries only: a COPY
            // entry's description is something the CLIENT itself submitted
            // in the original request, so it is genuine, independently-known
            // ground truth. A REUSE entry's description reflects the
            // EXISTING reused document's CURRENT description at the moment
            // the reservation decided to reuse it - the client's REUSE
            // request never carried this value at all (only a
            // cadDocumentId), so there is no independently-known expectation
            // to check it against here; `expected.OriginalDescription` is
            // always null for a REUSE entry and is deliberately NOT
            // compared.
            if (!expectedByOrdinal.TryGetValue(e.Ordinal, out var expected)
                || expected.IsCopy != (e.Action == "COPY")
                || expected.ResultingCadDocumentId != e.ResultingCadDocumentId
                || expected.SourceCadDocumentId != e.SourceCadDocumentId
                || expected.OriginalDocumentNumber != e.OriginalDocumentNumber
                || expected.OriginalFileName != e.OriginalFileName
                || expected.OriginalDocumentType != e.OriginalDocumentType
                || (expected.IsCopy && expected.OriginalDescription != e.OriginalDescription))
            {
                return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
            }

            bool isCopy;
            if (e.Action == "COPY") { isCopy = true; }
            else if (e.Action == "REUSE") { isCopy = false; }
            else { return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse); }

            // P6D ROUND 4, item E: a state's metadata SHAPE is REJECTED
            // outright (never silently ignored) when it carries
            // materialization fields it has no business carrying - a
            // PENDING/INVALID/REUSED entry that ALSO carries a
            // fileVersionId/versionNumber/sha256/fileSize is an internally
            // CONTRADICTORY response, not a harmless extra field.
            bool CarriesMaterializationMetadata() =>
                e.FileVersionId is not null || e.VersionNumber is not null || e.Sha256 is not null || e.FileSize is not null;

            if (isCopy)
            {
                if (string.IsNullOrWhiteSpace(e.SourceCadDocumentId) || OpaqueId.HasSurroundingWhitespace(e.SourceCadDocumentId))
                {
                    return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
                }

                CopyDesignOperationEntryState state;
                if (e.State == "PENDING")
                {
                    if (CarriesMaterializationMetadata())
                    {
                        return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
                    }
                    state = CopyDesignOperationEntryState.Pending;
                    entries.Add(new CopyDesignOperationStatusEntry(
                        e.Ordinal, IsCopy: true, state, e.SourceCadDocumentId, e.ResultingCadDocumentId,
                        e.OriginalDocumentNumber, e.OriginalFileName, e.OriginalDocumentType, e.OriginalDescription));
                }
                else if (e.State == "INVALID")
                {
                    if (CarriesMaterializationMetadata())
                    {
                        return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
                    }
                    state = CopyDesignOperationEntryState.Invalid;
                    entries.Add(new CopyDesignOperationStatusEntry(
                        e.Ordinal, IsCopy: true, state, e.SourceCadDocumentId, e.ResultingCadDocumentId,
                        e.OriginalDocumentNumber, e.OriginalFileName, e.OriginalDocumentType, e.OriginalDescription));
                }
                else if (e.State == "MATERIALIZED")
                {
                    // P6D ROUND 5, item B: fileSize MUST be POSITIVE - a
                    // materialized FileVersion 1 is a real, physically
                    // copied/verified binary, which can never legitimately
                    // be zero bytes. `IsRepresentableFileSize` alone permits
                    // 0 (it is a SHARED primitive also used by contexts -
                    // e.g. the P5B-B latest-version contract - that
                    // intentionally do not rule out an empty file); the
                    // stricter `> 0` requirement is enforced HERE, locally,
                    // for THIS contract only, rather than weakening the
                    // shared primitive for everyone else.
                    if (string.IsNullOrWhiteSpace(e.FileVersionId) || OpaqueId.HasSurroundingWhitespace(e.FileVersionId)
                        || e.VersionNumber != 1
                        || e.FileSize is not { } fileSize || fileSize <= 0 || !FileVersionIntegrity.IsRepresentableFileSize(fileSize)
                        || !FileVersionIntegrity.IsCanonicalSha256(e.Sha256))
                    {
                        return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
                    }
                    entries.Add(new CopyDesignOperationStatusEntry(
                        e.Ordinal, IsCopy: true, CopyDesignOperationEntryState.Materialized, e.SourceCadDocumentId,
                        e.ResultingCadDocumentId, e.OriginalDocumentNumber, e.OriginalFileName, e.OriginalDocumentType,
                        e.OriginalDescription, e.FileVersionId, e.VersionNumber, e.Sha256, e.FileSize));
                }
                else
                {
                    return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
                }
            }
            else
            {
                if (e.State != "REUSED" || e.SourceCadDocumentId is not null || CarriesMaterializationMetadata())
                {
                    return CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);
                }
                entries.Add(new CopyDesignOperationStatusEntry(
                    e.Ordinal, IsCopy: false, CopyDesignOperationEntryState.Reused, SourceCadDocumentId: null,
                    e.ResultingCadDocumentId, e.OriginalDocumentNumber, e.OriginalFileName, e.OriginalDocumentType,
                    e.OriginalDescription));
            }
        }

        return new CopyDesignOperationStatusResult(
            CopyDesignOperationStatusOutcome.Found, dto.CopyDesignOperationId, dto.IdempotencyKey, entries);
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
