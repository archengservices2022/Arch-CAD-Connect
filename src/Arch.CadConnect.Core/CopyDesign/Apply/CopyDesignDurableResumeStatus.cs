namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6D PRODUCTION RECOVERY: a RAW, structurally-validated (but NOT
/// expectation-cross-validated) authoritative Copy Design operation status
/// lookup, used ONLY for DURABLE RESUME DISCOVERY after this session has no
/// <c>_resumableCopyDesignAttempt</c> in memory (e.g. after an Inventor
/// restart).
///
/// DELIBERATELY SEPARATE from <see cref="ICopyDesignOperationStatusClient"/>:
/// that interface requires a <see cref="CopyDesignOperationStatusExpectation"/>
/// built from an ALREADY-CONFIRMED reservation - which does not exist yet
/// here (discovering/reconstructing that reservation IS the point of this
/// lookup - a chicken-and-egg the expectation-based client cannot resolve).
/// This client instead does ONLY structural/shape validation (contract id,
/// well-formed entries, no duplicate/missing ordinals, no state/action
/// contradiction) - exactly the validation
/// <c>HttpCopyDesignOperationStatusProbe</c>'s own class doc comment already
/// describes as "this probe alone can do" independent of any caller
/// expectation.
///
/// Backed by the SAME existing, unmodified, read-only server endpoint
/// (<c>GET /api/desktop/copy-design/operations/:operationId/status</c>) -
/// only its response DTO gained one additive field
/// (<c>sourceFileVersionId</c>) needed for safe reconstruction; nothing
/// about the endpoint's contract, auth, or scope changed.
///
/// The EXISTING <see cref="ICopyDesignOperationStatusClient"/> /
/// <c>HttpCopyDesignOperationStatusProbe</c> / orchestrator resume-skip
/// pipeline are completely UNTOUCHED by this addition - once a durable-
/// resume plan is reconstructed and handed to
/// <c>CopyDesignApplyOrchestrator.ExecuteAsync</c>, THAT existing,
/// already-reviewed machinery re-queries status itself (via the
/// expectation-based client) immediately before any mutation, exactly as it
/// already does for an in-session Resume.
/// </summary>
public enum CopyDesignDurableResumeStatusOutcome
{
    /// <summary>A well-formed, internally-consistent status for this EXACT
    ///  operationId.</summary>
    Found,

    /// <summary>No operation with this id exists in the caller's
    ///  organization (404) - including one that exists in a different
    ///  organization, which is deliberately indistinguishable.</summary>
    NotFound,

    /// <summary>The server could not be reached, timed out, or returned a
    ///  5xx / unexpected redirect.</summary>
    ServerUnavailable,

    /// <summary>No session, or the server rejected the credentials (401/403).</summary>
    AuthenticationFailed,

    /// <summary>The response was malformed or internally inconsistent
    ///  (missing contract, duplicate/missing ordinal, a state inconsistent
    ///  with its action, a MATERIALIZED entry with an incomplete/non-
    ///  canonical FileVersion identity, or a PENDING/INVALID/REUSED entry
    ///  contradictorily carrying materialization fields).</summary>
    MalformedResponse,
}

/// <summary>One entry of a raw durable-resume status lookup. Mirrors
///  <see cref="CopyDesignOperationStatusEntry"/>'s shape plus the one
///  additional field this discovery flow needs:
///  <see cref="SourceFileVersionId"/> (COPY only) - the EXACT source
///  FileVersion this entry's reservation was originally made against,
///  needed to (a) prove the local source workspace still binds the same
///  version, and (b) exactly reproduce the original reservation request so
///  an idempotent replay is recognized as the SAME request, never a
///  materially different one.</summary>
public sealed record CopyDesignDurableResumeEntry(
    int Ordinal,
    /// <summary>"COPY" or "REUSE" - kept as the raw wire string (not the
    ///  shared <c>CopyDesignApplyEntryAction</c> enum) so a genuinely
    ///  unrecognized value is reported as malformed by the RECONSTRUCTOR,
    ///  never silently coerced.</summary>
    string Action,
    /// <summary>"PENDING" / "MATERIALIZED" / "INVALID" (COPY) or "REUSED"
    ///  (REUSE) - raw wire string, same reasoning as <see cref="Action"/>.</summary>
    string State,
    string? SourceCadDocumentId,
    string? SourceFileVersionId,
    string ResultingCadDocumentId,
    string OriginalDocumentNumber,
    string OriginalFileName,
    string OriginalDocumentType,
    string? OriginalDescription,
    string? FileVersionId = null,
    int? VersionNumber = null,
    string? Sha256 = null,
    long? FileSize = null);

public sealed record CopyDesignDurableResumeStatusResult(
    CopyDesignDurableResumeStatusOutcome Outcome,
    string? OperationId = null,
    string? IdempotencyKey = null,
    IReadOnlyList<CopyDesignDurableResumeEntry>? Entries = null)
{
    public bool Success => Outcome == CopyDesignDurableResumeStatusOutcome.Found;
}

/// <summary>The seam a real HTTP implementation (Api layer) and tests both
///  implement. Never throws for a transport/auth/contract failure - always a
///  non-<see cref="CopyDesignDurableResumeStatusOutcome.Found"/> outcome.</summary>
public interface IDurableCopyDesignOperationStatusClient
{
    Task<CopyDesignDurableResumeStatusResult> GetRawStatusAsync(
        Arch.CadConnect.Core.Session.IArchSession session, string operationId, CancellationToken ct);
}
