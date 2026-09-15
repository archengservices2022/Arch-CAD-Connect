namespace Arch.CadConnect.Core.References;

/// <summary>
/// Why a repair target binary is (or is not) safely available on this machine
/// for one exact <c>(cadDocumentId, authoritative latest fileVersionId)</c>.
/// Every value except <see cref="Resolved"/> means P5C repair MUST NOT proceed
/// (fail closed - never substitute a similarly named file, never silently run
/// a broad Get Latest).
/// </summary>
public enum RepairTargetOutcome
{
    /// <summary>No target lookup was performed (malformed identity input).</summary>
    NotAttempted = 0,

    /// <summary>Exactly one VERIFIED local copy of the exact authoritative
    ///  FileVersion was found via an exact workspace-manifest binding, and the
    ///  file is present on disk.</summary>
    Resolved,

    /// <summary>No workspace manifest known to this scan binds that
    ///  <c>cadDocumentId</c> at all - the target document is not in a managed
    ///  workspace here.</summary>
    NoManagedCopyFound,

    /// <summary>A managed copy of the <c>cadDocumentId</c> exists locally, but
    ///  it is pinned to a DIFFERENT FileVersion than the authoritative latest -
    ///  the engineer must run Get Latest first (P5C never does it for them).</summary>
    TargetVersionNotLocal,

    /// <summary>A manifest entry matches the exact target FileVersion, but its
    ///  local bytes were never verified (a failed / partial Get Latest) - not a
    ///  trustworthy target.</summary>
    TargetCopyUnverified,

    /// <summary>A verified manifest entry matches the exact target FileVersion,
    ///  but the file is missing from disk.</summary>
    TargetFileMissing,

    /// <summary>The file exists but its current size/SHA-256 could not be read
    /// or does not match the SERVER-authoritative immutable FileVersion
    /// metadata.</summary>
    TargetBinaryVerificationFailed,

    /// <summary>More than one distinct local path claims the exact target
    ///  identity - ambiguous, so fail closed.</summary>
    AmbiguousTargets,

    /// <summary>The local workspace manifest's own recorded size / checksum for
    ///  the target FileVersion DISAGREE with the server-authoritative values.
    ///  The manifest is mutable and is not the integrity authority; a
    ///  disagreement is a fail-closed condition, never silently reconciled.</summary>
    TargetManifestServerMismatch,

    /// <summary>No canonical server-authoritative integrity metadata (size +
    ///  SHA-256) was supplied, so there is nothing trustworthy to verify the
    ///  local binary against.</summary>
    NoServerIntegrityMetadata,
}

/// <summary>
/// How a candidate repair-target path was identified. P5C only ever accepts
/// <see cref="WorkspaceManifestVerified"/> - a path proven by an exact
/// <c>root + relativePath</c> workspace-manifest binding whose
/// <c>cadDocumentId</c> AND <c>fileVersionId</c> both match and whose state is
/// Verified. Anything weaker is <see cref="Unproven"/> and is rejected.
/// </summary>
public enum RepairTargetIdentitySource
{
    Unproven = 0,
    WorkspaceManifestVerified,
}

/// <summary>One candidate repair target: an absolute local path plus the exact
///  stable identity that PROVES it is the intended FileVersion. Never carries a
///  filename / display-name / timestamp match.
///
///  <see cref="BinaryIntegrityVerified"/> is true ONLY when the file's CURRENT
///  on-disk bytes were hashed and match the SERVER-authoritative size + SHA-256
///  (not the local manifest). <see cref="ManifestAgreesWithServer"/> records
///  whether the mutable manifest's own recorded size/checksum happen to match
///  the server too - a cross-check, never the authority.</summary>
public sealed record RepairTargetCandidate(
    string CadDocumentId,
    string FileVersionId,
    string AbsolutePath,
    RepairTargetIdentitySource IdentitySource,
    bool ExistsOnDisk,
    string WorkspaceRoot,
    bool BinaryIntegrityVerified = false,
    bool ManifestAgreesWithServer = false,
    long ServerFileSize = -1,
    string ServerSha256 = "");

/// <summary>The outcome of resolving a repair target for one exact
///  <c>(cadDocumentId, authoritative latest fileVersionId)</c>.</summary>
public sealed record RepairTargetResolution(
    RepairTargetOutcome Outcome,
    RepairTargetCandidate? Candidate,
    IReadOnlyList<string> Notes)
{
    public static RepairTargetResolution Failure(RepairTargetOutcome outcome, params string[] notes) =>
        new(outcome, null, notes);

    public static RepairTargetResolution Found(RepairTargetCandidate candidate, params string[] notes) =>
        new(RepairTargetOutcome.Resolved, candidate, notes);
}

/// <summary>
/// Resolves the authoritative repair-target binary for one stable
/// <c>(cadDocumentId, fileVersionId)</c>. Implementations reuse the existing
/// managed-workspace architecture ONLY for the STABLE LOCAL PATH MAPPING - they
/// never search the filesystem by name and never trigger a download.
///
/// The <paramref name="serverFileSize"/> / <paramref name="serverSha256"/> are
/// the SERVER-authoritative canonical integrity metadata: the resolved local
/// file's CURRENT bytes must match THEM (never the mutable local manifest) for
/// <see cref="RepairTargetCandidate.BinaryIntegrityVerified"/> to be true. If
/// the manifest's own recorded values disagree with the server, that is
/// <see cref="RepairTargetOutcome.TargetManifestServerMismatch"/> (fail closed).
/// </summary>
public interface IRepairTargetLocator
{
    RepairTargetResolution Locate(
        string cadDocumentId,
        string authoritativeLatestFileVersionId,
        long serverFileSize,
        string serverSha256);
}
