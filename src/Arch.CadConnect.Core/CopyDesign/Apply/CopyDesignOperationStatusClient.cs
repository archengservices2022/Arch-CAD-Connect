namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6D ROUND 3, HIGH fix (items D/E): the per-entry state ONE
/// <see cref="CopyDesignOperationStatusResult"/> reports for ONE
/// <c>CopyDesignOperationEntry</c> - the small, closed enum the server's
/// dedicated status endpoint (never <c>latest-versions</c>) authoritatively
/// derives. <see cref="Reused"/> is REUSE-only; the other three are
/// COPY-only. Never inferred locally - always read directly off the server
/// response.
/// </summary>
public enum CopyDesignOperationEntryState
{
    /// <summary>COPY only: reserved (the resulting CadDocument and its
    ///  CadCopyLineage row exist), and it has EXACTLY ZERO FileVersion rows -
    ///  still safe to physically copy/rewire/verify/materialize.</summary>
    Pending,

    /// <summary>COPY only: EXACTLY ONE FileVersion row, versionNumber 1,
    ///  with canonical sha256/fileSize - already fully materialized by a
    ///  prior attempt. Never re-copied, re-rewired, or re-materialized.</summary>
    Materialized,

    /// <summary>COPY only: any other server-side state for this entry
    ///  (missing/mismatched lineage, more than one FileVersion row, a lone
    ///  row whose versionNumber isn't 1, or non-canonical integrity metadata
    ///  on that lone row) - ALWAYS fail closed, never guessed at, never
    ///  treated as PENDING or MATERIALIZED.</summary>
    Invalid,

    /// <summary>REUSE only: the existing reused CadDocument - no
    ///  materialization is ever expected for a REUSE entry.</summary>
    Reused,
}

/// <summary>One entry of an authoritative operation status response - the
///  Inventor-side mirror of the web repository's
///  `CopyDesignOperationStatusEntryDto` (see
///  `copy-design-operation-status-core.ts`). <see cref="SourceCadDocumentId"/>
///  is present for COPY entries only (null for REUSE, matching the server
///  contract and the CHECK constraint on `CopyDesignOperationEntry`).
///  <see cref="FileVersionId"/> / <see cref="VersionNumber"/> /
///  <see cref="Sha256"/> / <see cref="FileSize"/> are present ONLY when
///  <see cref="State"/> is <see cref="CopyDesignOperationEntryState.Materialized"/>.</summary>
public sealed record CopyDesignOperationStatusEntry(
    int Ordinal,
    bool IsCopy,
    CopyDesignOperationEntryState State,
    string? SourceCadDocumentId,
    string ResultingCadDocumentId,
    string OriginalDocumentNumber,
    string OriginalFileName,
    string OriginalDocumentType,
    /// <summary>P6D ROUND 4, item D: the immutable snapshot's optional
    ///  description - <c>null</c> when the original request entry carried
    ///  none (never conflated with an empty string, which would be a
    ///  different, false claim about what was originally decided).</summary>
    string? OriginalDescription = null,
    string? FileVersionId = null,
    int? VersionNumber = null,
    string? Sha256 = null,
    long? FileSize = null);

/// <summary>Why an authoritative operation status lookup is (or is not)
///  available. Every value except <see cref="Found"/> means the caller must
///  fail closed - NEVER treated as "nothing to resume" or "proceed as a
///  fresh apply". This is the categorical difference from round 2's
///  <c>LatestVersionOutcome</c>-based RESUME classification, which round-3
///  independent review found could not safely distinguish these states from
///  a genuinely PENDING fresh reservation.</summary>
public enum CopyDesignOperationStatusOutcome
{
    /// <summary>The authenticated server returned a well-formed,
    ///  internally-consistent status for this EXACT operationId.</summary>
    Found,

    /// <summary>No operation with this id exists in the caller's
    ///  organization (404) - including an operation that exists but belongs
    ///  to a different organization, which is deliberately
    ///  indistinguishable.</summary>
    NotFound,

    /// <summary>The Arch PLM server could not be reached, timed out, or
    ///  returned a 5xx / unexpected redirect.</summary>
    ServerUnavailable,

    /// <summary>Authentication was unavailable or rejected (no session, 401,
    ///  or 403).</summary>
    AuthenticationFailed,

    /// <summary>The response was malformed, internally inconsistent (e.g. a
    ///  duplicate `resultingCadDocumentId`, or a `copyDesignOperationId` that
    ///  does not match the one requested), or could not otherwise be safely
    ///  parsed/validated.</summary>
    MalformedResponse,
}

/// <summary>
/// The complete authoritative status of ONE Copy Design operation, or the
/// reason one could not be obtained. <see cref="Success"/> is the single
/// predicate a caller checks before trusting <see cref="Entries"/>.
/// </summary>
public sealed record CopyDesignOperationStatusResult(
    CopyDesignOperationStatusOutcome Outcome,
    string? CopyDesignOperationId = null,
    string? IdempotencyKey = null,
    IReadOnlyList<CopyDesignOperationStatusEntry>? Entries = null)
{
    public bool Success => Outcome == CopyDesignOperationStatusOutcome.Found;

    public static CopyDesignOperationStatusResult Failure(CopyDesignOperationStatusOutcome outcome) =>
        new(outcome == CopyDesignOperationStatusOutcome.Found ? CopyDesignOperationStatusOutcome.MalformedResponse : outcome);
}

/// <summary>P6D ROUND 4, item D: ONE entry of the CALLER's OWN confirmed
///  reservation, expressed as exactly what the operation-status response
///  MUST match at this ordinal - built ONLY from the validated P6B
///  reservation response the orchestrator already trusts, NEVER from
///  anything the status endpoint itself returns (that would be validating
///  the response against itself).</summary>
public sealed record CopyDesignOperationStatusExpectedEntry(
    int Ordinal,
    bool IsCopy,
    string? SourceCadDocumentId,
    string ResultingCadDocumentId,
    string OriginalDocumentNumber,
    string OriginalFileName,
    string OriginalDocumentType,
    string? OriginalDescription);

/// <summary>P6D ROUND 4, item D: the COMPLETE expected shape of an operation
///  status response, from the caller's point of view - the client
///  implementation MUST validate the response against EVERY field here
///  (operation id, idempotency key, entry count, per-ordinal identity and
///  immutable snapshot) BEFORE ever reporting <see cref="CopyDesignOperationStatusOutcome.Found"/>.
///  <see cref="Entries"/> is expected to already be in stable ordinal order
///  (0..N-1, contiguous) - exactly how the orchestrator's own confirmed
///  request/reservation entries are ordered.</summary>
public sealed record CopyDesignOperationStatusExpectation(
    string OperationId,
    string IdempotencyKey,
    IReadOnlyList<CopyDesignOperationStatusExpectedEntry> Entries);

/// <summary>
/// P6D ROUND 3, HIGH fix (items D/E), ROUND 4 (items D/E/F): the
/// authoritative per-OPERATION status oracle backing Copy Design RESUME -
/// REPLACES round 2's <c>ICopyDesignMaterializationStatusProbe</c> (a reuse
/// of the generic P5B-B `latest-versions` batch lookup), which independent
/// review proved could not safely distinguish a genuinely PENDING fresh
/// reservation (zero FileVersion rows, by design) from an unknown/malformed/
/// communication-failure state - both looked identical ("not recognized").
///
/// P6D ROUND 4: the client is now handed the CALLER's OWN
/// <see cref="CopyDesignOperationStatusExpectation"/> (built from the
/// already-confirmed P6B reservation) and MUST validate the COMPLETE
/// response against it - exact operation id, exact idempotency key, exact
/// entry count, and every entry's exact ordinal/identity/immutable snapshot -
/// before EVER reporting success. This closes the round-3 gap where the
/// orchestrator only checked "does a response entry exist for each id I
/// asked about", never "does the response contain EXACTLY my entries, no
/// more, no fewer, all field values exactly as reserved". An implementation
/// is backed by <c>GET /api/desktop/copy-design/operations/{operationId}/status</c>;
/// tests supply a deterministic fake.
/// </summary>
public interface ICopyDesignOperationStatusClient
{
    /// <summary>Never throws for a transport/auth/contract/validation
    ///  failure - reported via <see cref="CopyDesignOperationStatusResult.Outcome"/>
    ///  instead, so the caller can deliberately fail closed rather than via
    ///  an unstructured exception. An implementation MAY still throw for a
    ///  genuinely unexpected (non-transport) programming error - the caller
    ///  treats ANY exception from this call identically to a non-<see
    ///  cref="CopyDesignOperationStatusOutcome.Found"/> outcome (fail
    ///  closed), never as "empty" or "pending".</summary>
    Task<CopyDesignOperationStatusResult> GetStatusAsync(CopyDesignOperationStatusExpectation expectation, CancellationToken ct);
}
