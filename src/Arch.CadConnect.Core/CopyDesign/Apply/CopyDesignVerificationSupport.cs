namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6E-B: the Core-side, HTTP-free representation of the P6E-A
/// "verification support" server contract
/// (<c>GET /api/desktop/copy-design/operations/:operationId/verification-support</c>,
/// web repo, architecture-approved, intentionally uncommitted). Exactly
/// mirrors <see cref="CopyDesignDurableResumeStatusResult"/>'s own shape/
/// naming ("raw, structurally-validated, not yet expectation-cross-
/// validated") - this is the SAME kind of seam, for a DIFFERENT server
/// endpoint. A future P6E-C Api-layer HTTP client is the only thing that
/// would ever construct one of these from a real response; Core itself
/// never performs I/O to obtain one.
///
/// REUSE, NOT DUPLICATION: <see cref="CopyDesignVerificationSupportResult.Entries"/>
/// is typed as <see cref="CopyDesignDurableResumeEntry"/> - the EXACT P6D
/// type already representing one operation-status entry (ordinal, action,
/// state, source/resulting identity, immutable snapshot fields, and the
/// MATERIALIZED-only FileVersionId/VersionNumber/Sha256/FileSize) - because
/// the P6E-A contract embeds that SAME operation-status payload verbatim.
/// Nothing about entry parsing/classification is reimplemented here.
/// </summary>
public enum CopyDesignVerificationSupportOutcome
{
    /// <summary>A well-formed, internally-consistent verification-support
    ///  response for this EXACT operationId.</summary>
    Found,

    /// <summary>No operation with this id exists in the caller's
    ///  organization (404) - including one that exists in a different
    ///  organization, which is deliberately indistinguishable (matches
    ///  P6D's own operation-status behavior).</summary>
    NotFound,

    /// <summary>The server could not be reached, timed out, or returned a
    ///  5xx / unexpected redirect.</summary>
    ServerUnavailable,

    /// <summary>No session, or the server rejected the credentials (401/403).</summary>
    AuthenticationFailed,

    /// <summary>The response was malformed or internally inconsistent.</summary>
    MalformedResponse,
}

/// <summary>
/// Whether an authoritative source FileVersion's integrity could be proven
/// for ONE COPY entry - see P6E-A's own contract doc comment for the exact
/// server-side semantics this mirrors.
///
///   Available   - a canonical sha256/fileSize is available, from a
///                 FileVersion row proven to belong to the claimed source
///                 CadDocument.
///   Unavailable - no such FileVersion row could be found at all.
///   Invalid     - a row was found but is untrustworthy as evidence (non-
///                 canonical checksum/size, or it belongs to a DIFFERENT
///                 CadDocument than claimed).
///
/// NEVER fabricated: <see cref="CopyDesignSourceIntegrityEvidence.Sha256"/> /
/// <see cref="CopyDesignSourceIntegrityEvidence.FileSize"/> are populated
/// ONLY when <see cref="CopyDesignSourceIntegrityEvidence.State"/> is
/// <see cref="Available"/>.
/// </summary>
public enum CopyDesignSourceIntegrityState
{
    Available,
    Unavailable,
    Invalid,
}

/// <summary>ONE explicit source-integrity evidence record for ONE COPY
///  entry - P6E-A returns EXACTLY one of these per COPY entry, never a
///  silent omission (a REUSE entry never gets one at all - P6D has no
///  frozen historical FileVersion for REUSE; see
///  <see cref="CopyDesignVerificationOrchestrator"/>'s own REUSE handling
///  for how that limitation is reported, honestly, as NOT_PROVABLE rather
///  than silently skipped).</summary>
public sealed record CopyDesignSourceIntegrityEvidence(
    int Ordinal,
    string SourceCadDocumentId,
    string SourceFileVersionId,
    CopyDesignSourceIntegrityState State,
    /// <summary>Canonical lowercase-hex SHA-256, 64 characters - present
    ///  ONLY when <see cref="State"/> is <see cref="CopyDesignSourceIntegrityState.Available"/>.</summary>
    string? Sha256 = null,
    /// <summary>Present ONLY when <see cref="State"/> is
    ///  <see cref="CopyDesignSourceIntegrityState.Available"/>.</summary>
    long? FileSize = null,
    /// <summary>Human-readable, present ONLY when <see cref="State"/> is
    ///  <see cref="CopyDesignSourceIntegrityState.Invalid"/>.</summary>
    string? Reason = null);

/// <summary>ONE authoritative <c>CadDependency</c> COMPONENT edge among this
///  operation's own topology identity set (every COPY entry's
///  sourceCadDocumentId UNION every REUSE entry's resultingCadDocumentId -
///  see P6E-A's own contract doc comment) - <see cref="ParentCadDocumentId"/>
///  --COMPONENT--&gt; <see cref="ChildCadDocumentId"/> (assembly -&gt; its
///  direct component/subassembly). Stable CadDocument ids only.</summary>
public sealed record CopyDesignComponentEdge(string ParentCadDocumentId, string ChildCadDocumentId);

/// <summary>The complete, raw verification-support payload for one
///  operation - <see cref="Success"/> gates whether <see cref="Entries"/>/
///  <see cref="SourceIntegrity"/>/<see cref="ComponentEdges"/> are usable at
///  all, exactly like <see cref="CopyDesignDurableResumeStatusResult.Success"/>.</summary>
public sealed record CopyDesignVerificationSupportResult(
    CopyDesignVerificationSupportOutcome Outcome,
    string? OperationId = null,
    string? IdempotencyKey = null,
    IReadOnlyList<CopyDesignDurableResumeEntry>? Entries = null,
    IReadOnlyList<CopyDesignSourceIntegrityEvidence>? SourceIntegrity = null,
    IReadOnlyList<CopyDesignComponentEdge>? ComponentEdges = null)
{
    public bool Success => Outcome == CopyDesignVerificationSupportOutcome.Found;
}

/// <summary>P6E-C: the seam a real HTTP implementation (Api layer) and tests
///  both implement - mirrors <see cref="IDurableCopyDesignOperationStatusClient"/>'s
///  own shape exactly, for the SEPARATE verification-support endpoint. Never
///  throws for a transport/auth/contract failure - always a non-
///  <see cref="CopyDesignVerificationSupportOutcome.Found"/> outcome.</summary>
public interface ICopyDesignVerificationSupportClient
{
    Task<CopyDesignVerificationSupportResult> GetVerificationSupportAsync(
        Arch.CadConnect.Core.Session.IArchSession session, string operationId, CancellationToken ct);
}
