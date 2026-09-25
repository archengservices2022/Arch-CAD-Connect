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
/// P6E-C: <see cref="ICopyDesignVerificationSupportClient"/> over the
/// authenticated Arch PLM HTTP API - the HTTP/API wiring for the P6E-A
/// "verification support" server contract, mapped into the P6E-B Core
/// verification-engine types. Follows the SAME established desktop-client
/// transport rules as <see cref="HttpDurableCopyDesignOperationStatusClient"/> /
/// <see cref="HttpDrawingAssociationClient"/> / <see cref="HttpCopyDesignOperationStatusProbe"/>:
/// origin-bound bearer, ALL redirects refused, a single timeout across
/// connect + headers + body, a defensively capped response, no logging, no
/// tenant id ever supplied by this client (the server alone scopes the
/// response to the caller's own organization).
///
/// FAIL CLOSED: this method NEVER throws for a transport, auth, contract, or
/// local request-construction failure - every such case becomes a
/// <see cref="CopyDesignVerificationSupportOutcome"/> other than
/// <see cref="CopyDesignVerificationSupportOutcome.Found"/>.
///
/// ---------------------------------------------------------------------------
/// SERVER CONTRACT (P6E-A, web repository, intentionally uncommitted):
///
///   GET /api/desktop/copy-design/operations/{operationId}/verification-support
///
///   Auth : Authorization: Bearer &lt;desktop session token&gt;.
///   Scope: READ-ONLY. Never creates, updates, or deletes anything.
///
///   200 response body:
///   {
///     "contract": "arch-plm.copy-design-verification-support.v1",
///     "copyDesignOperationId": "&lt;id&gt;",
///     "operationStatus": {
///       "contract": "arch-plm.copy-design-operation-status.v1",
///       "copyDesignOperationId": "&lt;id&gt;", "idempotencyKey": "&lt;key&gt;",
///       "performedAt": "&lt;ISO 8601&gt;",
///       "entries": [ ... EXACT same per-entry shape as the durable status
///                     endpoint, including "sourceFileVersionId" for COPY ... ]
///     },
///     "sourceFileVersionIntegrity": [
///       { "ordinal": 0, "sourceCadDocumentId": "&lt;id&gt;", "sourceFileVersionId": "&lt;id&gt;",
///         "state": "AVAILABLE", "sha256": "&lt;64 lowercase hex&gt;", "fileSize": 12345 },
///       { "ordinal": 1, ..., "state": "UNAVAILABLE" },
///       { "ordinal": 2, ..., "state": "INVALID", "reason": "&lt;human-readable&gt;" }
///     ],
///     "componentEdges": [
///       { "parentCadDocumentId": "&lt;id&gt;", "childCadDocumentId": "&lt;id&gt;" }
///     ]
///   }
///
///   - "operationStatus" embeds the SAME, unmodified operation-status payload
///     the durable/expectation-based status endpoints already return -
///     parsed here with the IDENTICAL per-entry structural rules
///     <see cref="HttpDurableCopyDesignOperationStatusClient"/> already
///     enforces (contract, action/state validity, required fields,
///     MATERIALIZED completeness/canonical-integrity, no contradictory
///     materialization metadata, no duplicate resultingCadDocumentId) PLUS
///     this endpoint's OWN additional structural rule: no duplicate/gapped
///     ordinals (the durable client leaves that to its caller; here there is
///     no separate caller to enforce it, so this client does).
///   - "sourceFileVersionIntegrity" carries EXACTLY one explicit evidence
///     record per COPY entry (keyed by "ordinal"), never a silent omission -
///     a REUSE entry NEVER gets one. "AVAILABLE" REQUIRES a canonical
///     sha256/fileSize and no "reason". "UNAVAILABLE"/"INVALID" must NEVER
///     carry sha256/fileSize. "INVALID" REQUIRES a non-blank "reason".
///     Evidence naming a source id that doesn't match its own COPY entry's
///     sourceCadDocumentId/sourceFileVersionId is rejected as internally
///     inconsistent.
///   - "componentEdges" is scoped to this operation's own TOPOLOGY IDENTITY
///     SET - the UNION of every COPY entry's sourceCadDocumentId and every
///     REUSE entry's resultingCadDocumentId. An edge naming an endpoint
///     outside that set, or a duplicate edge, is malformed.
///   - 401 / 403 -&gt; AuthenticationFailed. 404 -&gt; NotFound (unknown
///     operationId, including one in a different tenant). 5xx / timeout /
///     redirect -&gt; ServerUnavailable. Any other non-200, or a body that
///     fails contract/shape validation, -&gt; MalformedResponse.
/// ---------------------------------------------------------------------------
/// </summary>
public sealed class HttpCopyDesignVerificationSupportClient : ICopyDesignVerificationSupportClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private const int MaxResponseBodyBytes = 256 * 1024;

    private const string SupportContract = "arch-plm.copy-design-verification-support.v1";
    private const string OperationStatusContract = "arch-plm.copy-design-operation-status.v1";

    private readonly ArchServerUri _server;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public HttpCopyDesignVerificationSupportClient(ArchServerUri server, HttpClient http, TimeSpan? timeout = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<CopyDesignVerificationSupportResult> GetVerificationSupportAsync(
        IArchSession session, string operationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!_server.MatchesOrigin(session.Server.Origin))
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
        }
        if (string.IsNullOrWhiteSpace(operationId) || OpaqueId.HasSurroundingWhitespace(operationId))
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
        }

        try
        {
            return await SendAndInterpretAsync(session, operationId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.ServerUnavailable);
        }
        catch (HttpRequestException)
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.ServerUnavailable);
        }
        catch (SocketException)
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.ServerUnavailable);
        }
        catch (IOException)
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.ServerUnavailable);
        }
        catch (Exception ex) when (
            ex is UriFormatException or FormatException or ArgumentException
               or InvalidOperationException or NotSupportedException
               or JsonException or InvalidDataException or OverflowException
               or ObjectDisposedException or DecoderFallbackException)
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
        }
    }

    private async Task<CopyDesignVerificationSupportResult> SendAndInterpretAsync(
        IArchSession session, string operationId, CancellationToken ct)
    {
        var url = _server.ResolvePath(
            "/api/desktop/copy-design/operations/" + Uri.EscapeDataString(operationId) + "/verification-support");
        if (!_server.MatchesOrigin(url))
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
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
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.AuthenticationFailed);
        }
        if (code is >= 300 and < 400)
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.ServerUnavailable);
        }
        if (code == 404)
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.NotFound);
        }

        var body = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
        return Interpret(operationId, response.StatusCode, body);
    }

    private static CopyDesignVerificationSupportResult Interpret(string requestedOperationId, HttpStatusCode status, string body)
    {
        var code = (int)status;
        if (code is >= 300 and < 400)
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.ServerUnavailable);
        }
        if (code is 401 or 403)
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.AuthenticationFailed);
        }
        if (code == 404)
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.NotFound);
        }
        if (code != 200)
        {
            return new CopyDesignVerificationSupportResult(
                code is >= 500 and < 600
                    ? CopyDesignVerificationSupportOutcome.ServerUnavailable
                    : CopyDesignVerificationSupportOutcome.MalformedResponse);
        }

        VerificationSupportResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<VerificationSupportResponseDto>(body, Json);
        }
        catch (JsonException)
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
        }

        if (dto is null
            || dto.Contract != SupportContract
            || string.IsNullOrWhiteSpace(dto.CopyDesignOperationId)
            || dto.CopyDesignOperationId != requestedOperationId
            || dto.OperationStatus is null
            || dto.OperationStatus.Contract != OperationStatusContract
            || string.IsNullOrWhiteSpace(dto.OperationStatus.CopyDesignOperationId)
            || dto.OperationStatus.CopyDesignOperationId != dto.CopyDesignOperationId
            || string.IsNullOrWhiteSpace(dto.OperationStatus.IdempotencyKey)
            || dto.OperationStatus.Entries is null
            || dto.SourceFileVersionIntegrity is null
            || dto.ComponentEdges is null)
        {
            return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
        }

        // ---- operationStatus.entries: IDENTICAL per-entry structural rules to
        //      HttpDurableCopyDesignOperationStatusClient, PLUS this endpoint's
        //      own no-duplicate/no-gap ordinal requirement (that client leaves
        //      ordinal contiguity to its own caller; here there is none). -----
        var entries = new List<CopyDesignDurableResumeEntry>();
        var seenResultingIds = new HashSet<string>(StringComparer.Ordinal);
        var seenOrdinals = new HashSet<int>();
        foreach (var e in dto.OperationStatus.Entries)
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
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
            if (!seenResultingIds.Add(e.ResultingCadDocumentId))
            {
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
            if (!seenOrdinals.Add(e.Ordinal))
            {
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }

            bool CarriesMaterializationMetadata() =>
                e.FileVersionId is not null || e.VersionNumber is not null || e.Sha256 is not null || e.FileSize is not null;

            if (e.Action == "COPY")
            {
                if (string.IsNullOrWhiteSpace(e.SourceCadDocumentId) || OpaqueId.HasSurroundingWhitespace(e.SourceCadDocumentId)
                    || string.IsNullOrWhiteSpace(e.SourceFileVersionId) || OpaqueId.HasSurroundingWhitespace(e.SourceFileVersionId))
                {
                    return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
                }

                if (e.State is "PENDING" or "INVALID")
                {
                    if (CarriesMaterializationMetadata())
                    {
                        return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
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
                        return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
                    }
                    entries.Add(new CopyDesignDurableResumeEntry(
                        e.Ordinal, e.Action, e.State, e.SourceCadDocumentId, e.SourceFileVersionId, e.ResultingCadDocumentId,
                        e.OriginalDocumentNumber, e.OriginalFileName, e.OriginalDocumentType, e.OriginalDescription,
                        e.FileVersionId, e.VersionNumber, e.Sha256, e.FileSize));
                }
                else
                {
                    return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
                }
            }
            else if (e.Action == "REUSE")
            {
                if (e.State != "REUSED" || e.SourceCadDocumentId is not null || e.SourceFileVersionId is not null || CarriesMaterializationMetadata())
                {
                    return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
                }
                entries.Add(new CopyDesignDurableResumeEntry(
                    e.Ordinal, e.Action, e.State, null, null, e.ResultingCadDocumentId,
                    e.OriginalDocumentNumber, e.OriginalFileName, e.OriginalDocumentType, e.OriginalDescription));
            }
            else
            {
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
        }

        // ---- no duplicate/gapped ordinals: every ordinal 0..count-1 present
        //      exactly once (seenOrdinals.Add above already proved uniqueness;
        //      this proves contiguity/coverage). -----------------------------
        for (var i = 0; i < entries.Count; i++)
        {
            if (!seenOrdinals.Contains(i))
            {
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
        }

        var copyEntryByOrdinal = entries.Where(e => e.Action == "COPY").ToDictionary(e => e.Ordinal);
        var reuseOrdinals = new HashSet<int>(entries.Where(e => e.Action == "REUSE").Select(e => e.Ordinal));

        // ---- sourceFileVersionIntegrity: EXACTLY one record per COPY entry,
        //      never one for REUSE, never a duplicate, never fabricated. -----
        var sourceIntegrity = new List<CopyDesignSourceIntegrityEvidence>();
        var seenEvidenceOrdinals = new HashSet<int>();
        foreach (var ev in dto.SourceFileVersionIntegrity)
        {
            if (ev is null
                || string.IsNullOrWhiteSpace(ev.SourceCadDocumentId) || OpaqueId.HasSurroundingWhitespace(ev.SourceCadDocumentId)
                || string.IsNullOrWhiteSpace(ev.SourceFileVersionId) || OpaqueId.HasSurroundingWhitespace(ev.SourceFileVersionId)
                || string.IsNullOrWhiteSpace(ev.State))
            {
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
            if (reuseOrdinals.Contains(ev.Ordinal))
            {
                // Source-integrity evidence for a REUSE entry - the contract
                // says none should exist for it. Fail closed rather than
                // silently ignoring apparently-extra evidence.
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
            if (!copyEntryByOrdinal.TryGetValue(ev.Ordinal, out var owningEntry))
            {
                // Evidence naming an ordinal that is not any known COPY entry
                // at all - internally inconsistent.
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
            if (!seenEvidenceOrdinals.Add(ev.Ordinal))
            {
                // Duplicate evidence for the same COPY ordinal.
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
            if (ev.SourceCadDocumentId != owningEntry.SourceCadDocumentId || ev.SourceFileVersionId != owningEntry.SourceFileVersionId)
            {
                // Evidence references a DIFFERENT source identity than its own
                // owning entry claims - internally inconsistent, never trusted.
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }

            if (ev.State == "AVAILABLE")
            {
                if (ev.Reason is not null
                    || !FileVersionIntegrity.IsCanonicalSha256(ev.Sha256)
                    || ev.FileSize is not { } fileSize || !FileVersionIntegrity.IsRepresentableFileSize(fileSize))
                {
                    return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
                }
                sourceIntegrity.Add(new CopyDesignSourceIntegrityEvidence(
                    ev.Ordinal, ev.SourceCadDocumentId, ev.SourceFileVersionId,
                    CopyDesignSourceIntegrityState.Available, ev.Sha256, ev.FileSize));
            }
            else if (ev.State == "UNAVAILABLE")
            {
                if (ev.Sha256 is not null || ev.FileSize is not null || ev.Reason is not null)
                {
                    return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
                }
                sourceIntegrity.Add(new CopyDesignSourceIntegrityEvidence(
                    ev.Ordinal, ev.SourceCadDocumentId, ev.SourceFileVersionId,
                    CopyDesignSourceIntegrityState.Unavailable));
            }
            else if (ev.State == "INVALID")
            {
                if (ev.Sha256 is not null || ev.FileSize is not null || string.IsNullOrWhiteSpace(ev.Reason))
                {
                    return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
                }
                sourceIntegrity.Add(new CopyDesignSourceIntegrityEvidence(
                    ev.Ordinal, ev.SourceCadDocumentId, ev.SourceFileVersionId,
                    CopyDesignSourceIntegrityState.Invalid, Reason: ev.Reason));
            }
            else
            {
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
        }
        foreach (var ordinal in copyEntryByOrdinal.Keys)
        {
            if (!seenEvidenceOrdinals.Contains(ordinal))
            {
                // A COPY entry with no evidence record at all - never inferred.
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
        }

        // ---- componentEdges: scoped to the operation's own topology identity
        //      set - every COPY entry's sourceCadDocumentId UNION every REUSE
        //      entry's resultingCadDocumentId. -----------------------------
        var topologyIdentitySet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            topologyIdentitySet.Add(entry.Action == "COPY" ? entry.SourceCadDocumentId! : entry.ResultingCadDocumentId);
        }

        var componentEdges = new List<CopyDesignComponentEdge>();
        var seenEdges = new HashSet<(string Parent, string Child)>();
        foreach (var edge in dto.ComponentEdges)
        {
            if (edge is null
                || string.IsNullOrWhiteSpace(edge.ParentCadDocumentId) || OpaqueId.HasSurroundingWhitespace(edge.ParentCadDocumentId)
                || string.IsNullOrWhiteSpace(edge.ChildCadDocumentId) || OpaqueId.HasSurroundingWhitespace(edge.ChildCadDocumentId))
            {
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
            if (!topologyIdentitySet.Contains(edge.ParentCadDocumentId) || !topologyIdentitySet.Contains(edge.ChildCadDocumentId))
            {
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
            if (!seenEdges.Add((edge.ParentCadDocumentId, edge.ChildCadDocumentId)))
            {
                // An exact duplicate edge is redundant/inconsistent data the
                // server contract does not promise to send - fail closed
                // rather than silently deduplicating.
                return new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.MalformedResponse);
            }
            componentEdges.Add(new CopyDesignComponentEdge(edge.ParentCadDocumentId, edge.ChildCadDocumentId));
        }

        return new CopyDesignVerificationSupportResult(
            CopyDesignVerificationSupportOutcome.Found, dto.CopyDesignOperationId, dto.OperationStatus.IdempotencyKey,
            entries, sourceIntegrity, componentEdges);
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

    private sealed record VerificationSupportResponseDto(
        string Contract = "",
        string? CopyDesignOperationId = null,
        OperationStatusDto? OperationStatus = null,
        IReadOnlyList<SourceIntegrityDto>? SourceFileVersionIntegrity = null,
        IReadOnlyList<ComponentEdgeDto>? ComponentEdges = null);

    private sealed record OperationStatusDto(
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

    private sealed record SourceIntegrityDto(
        int Ordinal = 0,
        string? SourceCadDocumentId = null,
        string? SourceFileVersionId = null,
        string? State = null,
        string? Sha256 = null,
        long? FileSize = null,
        string? Reason = null);

    private sealed record ComponentEdgeDto(
        string? ParentCadDocumentId = null,
        string? ChildCadDocumentId = null);
}
