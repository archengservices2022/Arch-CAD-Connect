namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6C: the SEAMS between the pure orchestrator (<see cref="CopyDesignApplyOrchestrator"/>)
/// and everything that actually touches the network or Inventor COM. Core
/// depends only on these interfaces; <c>Arch.CadConnect.Api</c> and
/// <c>Arch.CadConnect.Inventor</c> provide the real implementations, and
/// tests provide fakes - so the entire orchestration (guard, revalidation,
/// mapping, response validation, execution ordering, journal/cleanup,
/// verification-result evaluation, materialization gating) is testable with
/// NO Inventor and NO network.
/// </summary>
public interface ICopyDesignReservationClient
{
    /// <summary>POST the mapped request to the P6B server contract. Throws on
    ///  any transport/HTTP failure - the caller does not know whether the
    ///  server actually committed (P6B's idempotency key is what makes a
    ///  retry with the SAME request safe).</summary>
    Task<CopyDesignReservationResponse> ApplyAsync(CopyDesignApplyRequest request, CancellationToken ct);
}

public sealed record CopyDesignPhysicalCopyResult(
    bool Success,
    string? FailureReason,
    /// <summary>CODEX ROUND 3, MEDIUM fix: present ONLY on a failure that
    ///  left an operation-owned temporary artifact (see
    ///  <see cref="CopyDesignAtomicPromotion"/>) which automatic cleanup
    ///  could NOT confirm was removed - the exact path, so it can be
    ///  reported for manual cleanup. Always <c>null</c> on success, and
    ///  always <c>null</c> on a failure where no temp artifact remains.</summary>
    string? UnremovedTempPath = null);

public interface ICopyDesignPhysicalCopier
{
    /// <summary>Physically create the COPY destination for <paramref name="node"/>
    ///  (an Inventor Save-Copy-As or equivalent) - see the P6C report for the
    ///  exact Inventor API used. Must NEVER mutate the source document.
    ///  Returns a failure result rather than throwing for any DOCUMENTED
    ///  Inventor failure; only genuinely unexpected exceptions propagate.</summary>
    Task<CopyDesignPhysicalCopyResult> CopyAsync(CopyDesignNode node, CancellationToken ct);
}

public sealed record CopyDesignRewireResult(bool Success, string? FailureReason);

public interface ICopyDesignReferenceRewirer
{
    /// <summary>Rewire every reference of the ALREADY-COPIED IAM at
    ///  <paramref name="copiedIamNode"/>'s destination (never the source) so
    ///  each target in <paramref name="targets"/> resolves as expected -
    ///  mutating only COPY-child references; REUSE-child references are
    ///  confirmed, not mutated.</summary>
    Task<CopyDesignRewireResult> RewireAsync(
        CopyDesignNode copiedIamNode, IReadOnlyList<CopyDesignReferenceTarget> targets, CancellationToken ct);
}

public interface ICopyDesignVerifier
{
    /// <summary>Gather every fact <see cref="CopyDesignVerificationEvaluator"/>
    ///  needs for ONE COPY node, AFTER its physical copy (and, for an IAM,
    ///  its reference rewiring) has completed.</summary>
    Task<CopyDesignNodeVerificationFacts> GatherFactsAsync(
        CopyDesignNode node,
        IReadOnlyList<CopyDesignReferenceTarget> referenceTargets,
        string sourceSha256BeforeOperation,
        CancellationToken ct);
}

/// <summary>Computes a SHA-256 for a local file. Injected so the source
///  integrity capture-before/verify-after step is independently fakeable in
///  tests without touching a real file.</summary>
public interface ICopyDesignFileHasher
{
    string ComputeSha256(string absolutePath);
}

public enum CopyDesignMaterializationOutcome
{
    Materialized,

    /// <summary>Materialization did not complete - a controlled server
    ///  rejection (role/eligibility/conflict/integrity/concurrency), a
    ///  response that failed this client's own post-response validation
    ///  (see <see cref="CopyDesignMaterializeRequest"/>'s doc comment), or a
    ///  transport failure. The reservation (and physical, verified file)
    ///  remain intact and honest; only the FileVersion step did not
    ///  complete - see <see cref="CopyDesignMaterializationResult.Detail"/>
    ///  for the exact reason.</summary>
    Failed,
}

/// <summary>
/// Everything <see cref="ICopyDesignMaterializer"/> needs to materialize ONE
/// COPY node's first FileVersion - built ENTIRELY from authoritative P6C
/// state gathered during THIS apply attempt, never from stale pre-copy
/// metadata:
///   - <see cref="CopyDesignOperationId"/> / <see cref="ResultingCadDocumentId"/>
///     come from the validated P6B reservation response;
///   - <see cref="SourceCadDocumentId"/> / <see cref="DocumentType"/> come
///     from the confirmed plan node;
///   - <see cref="LocalFilePath"/> / <see cref="ExpectedSha256"/> /
///     <see cref="ExpectedFileSize"/> come from the LOCAL VERIFICATION facts
///     of the FINAL destination binary (<see cref="CopyDesignNodeVerificationFacts.ResultingSha256"/> /
///     <see cref="CopyDesignNodeVerificationFacts.ResultingFileSize"/>) -
///     gathered AFTER physical copy, reference rewiring, and verification
///     all succeeded, never before.
/// </summary>
public sealed record CopyDesignMaterializeRequest(
    string CopyDesignOperationId,
    string ResultingCadDocumentId,
    string SourceCadDocumentId,
    CadDocumentType DocumentType,
    string LocalFilePath,
    string FileName,
    string ExpectedSha256,
    long ExpectedFileSize);

public sealed record CopyDesignMaterializationResult(
    string CadDocumentId,
    CopyDesignMaterializationOutcome Outcome,
    string? FileVersionId,
    int? VersionNumber,
    string? Sha256,
    long? FileSize,
    string Detail);

public interface ICopyDesignMaterializer
{
    /// <summary>Attempt to create the FIRST FileVersion for
    ///  <paramref name="request"/>'s reserved target from its verified local
    ///  file. NEVER throws for a controlled server rejection or a response
    ///  that fails this client's own post-response validation - every
    ///  outcome comes back as a result, and a validation failure is ALWAYS
    ///  reported as <see cref="CopyDesignMaterializationOutcome.Failed"/>,
    ///  never as success.</summary>
    Task<CopyDesignMaterializationResult> MaterializeFirstFileVersionAsync(
        CopyDesignMaterializeRequest request, CancellationToken ct);
}
