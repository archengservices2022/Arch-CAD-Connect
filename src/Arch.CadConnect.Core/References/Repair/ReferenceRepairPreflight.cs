using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.References;

/// <summary>
/// The verdict of the POST-CONFIRMATION authorization re-check - run AFTER the
/// engineer confirmed and IMMEDIATELY before any Inventor mutation. The
/// confirmation dialog is NOT itself authorization to mutate stale state: while
/// it was open the target bytes, the target size, the authoritative checkout,
/// or the checkout identity may all have drifted.
///
/// <see cref="Authorized"/> is true ONLY when a fresh re-check of everything -
/// authoritative checkout is still <c>Mine</c> with the exact checkout id and
/// base fileVersionId, the referencing document is still affirmatively writable,
/// the intended repair target still re-resolves to the same
/// <c>cadDocumentId</c> + authoritative <c>fileVersionId</c>, and the target
/// file's current size + SHA-256 still match the SERVER-authoritative FileVersion
/// integrity metadata (never the mutable local manifest) - all pass. Any
/// mismatch, missing information, timeout, read failure, auth failure, server
/// failure, or drift returns <see cref="Authorized"/> == false, and the
/// coordinator makes ZERO COM mutation.
/// </summary>
public sealed record RepairPreflightVerdict(
    bool Authorized,
    string Detail,
    /// <summary>The SERVER-authoritative canonical byte size of the target
    ///  FileVersion, captured during the re-check for the protected-lease and
    ///  post-mutation integrity checks. &lt; 0 when not established (then the
    ///  repair is aborted). NEVER the local manifest value.</summary>
    long TrustedTargetSize = -1,
    /// <summary>The SERVER-authoritative canonical SHA-256 (64 lowercase hex) of
    ///  the target FileVersion. Null / blank when not established (then the
    ///  repair is aborted). NEVER the local manifest value.</summary>
    string? TrustedTargetSha256 = null,
    /// <summary>The IMMUTABLE authorization identity captured during this
    ///  re-check. The final checkout read and the final manifest consistency
    ///  check are compared to THIS - a manifest reloaded at the mutation
    ///  boundary can never redefine it. Null when not established (then the
    ///  repair is aborted).</summary>
    RepairAuthorizationSnapshot? Snapshot = null,
    /// <summary>How many edges of the referencing document ALREADY resolved to
    ///  the authoritative target (path + full stable identity) at re-check time,
    ///  BEFORE the mutation. Post-mutation verification requires the count to
    ///  rise by EXACTLY ONE, so a pre-existing target edge can never impersonate
    ///  the repaired selected edge. &lt; 0 when not captured (then verification
    ///  fails closed).</summary>
    int PreMutationTargetEdgeCount = -1,
    /// <summary>ROUND 4 (Codex HIGH - whole-reference-set proof): an IMMUTABLE,
    ///  COM-free fingerprint of EVERY direct managed reference of the
    ///  referencing document, captured at the SAME moment as
    ///  <see cref="PreMutationTargetEdgeCount"/>. Post-mutation verification
    ///  proves the fresh scan's fingerprint equals this one EXCEPT for exactly
    ///  the one authorized old-&gt;target transition - so an unrelated managed
    ///  reference changing at the same time can never be hidden inside a
    ///  "Repaired" result. Null when not captured (then verification fails
    ///  closed).</summary>
    IReadOnlyList<ManagedReferenceFingerprint>? PreMutationReferenceFingerprint = null)
{
    public static RepairPreflightVerdict Deny(string detail) => new(false, detail);
}

/// <summary>
/// The IMMUTABLE authorization identity the post-confirmation re-check verified
/// and the mutation boundary must match EXACTLY. Only stable Arch identifiers -
/// never a filename or a path. A manifest reloaded immediately before mutation
/// is allowed only as a fresh consistency check against this snapshot; it can
/// never redefine the expected checkout id / base fileVersionId.
/// </summary>
public sealed record RepairAuthorizationSnapshot(
    string ParentCadDocumentId,
    string ExpectedCheckoutId,
    string ExpectedBaseFileVersionId)
{
    /// <summary>True only when every field is a non-blank, non-whitespace-padded
    ///  stable identifier.</summary>
    public bool IsComplete =>
        Ok(ParentCadDocumentId) && Ok(ExpectedCheckoutId) && Ok(ExpectedBaseFileVersionId);

    private static bool Ok(string? v) =>
        !string.IsNullOrWhiteSpace(v) && !char.IsWhiteSpace(v[0]) && !char.IsWhiteSpace(v[^1]);

    /// <summary>Exact ordinal match of this snapshot against a fresh manifest
    ///  checkout binding AND a fresh authoritative server checkout status. Any
    ///  missing / blank / malformed / drifted value =&gt; false.
    ///
    ///  ROUND 4 (Codex HIGH - exact checkout/base/local-parent binding): the
    ///  fresh manifest entry's OWN currently-pinned <c>FileVersionId</c> - the
    ///  local parent document's own version, NOT merely its checkout marker's
    ///  recorded base - must ALSO equal <see cref="ExpectedBaseFileVersionId"/>
    ///  exactly. A checkout marker can (legitimately, per the codebase's own
    ///  Check-In flow) still show the old base while the local manifest has
    ///  already been rebound to a newer local FileVersion (e.g. a concurrent
    ///  Check-In elsewhere); that drift must abort here rather than being
    ///  silently accepted as "the checkout marker still agrees".</summary>
    public bool MatchesFinalState(
        WorkspaceManifestEntry? freshEntry, ServerCheckoutStatus? finalStatus)
    {
        if (!IsComplete)
        {
            return false;
        }

        // Fresh manifest: same parent cadDocumentId, same checkout binding, AND
        // the entry's OWN currently-pinned local FileVersion is STILL exactly
        // the checkout's base - never merely the checkout marker in isolation.
        if (freshEntry is null
            || !string.Equals(freshEntry.CadDocumentId, ParentCadDocumentId, StringComparison.Ordinal)
            || freshEntry.State != WorkspaceManifestEntryState.Verified
            || !string.Equals(freshEntry.FileVersionId, ExpectedBaseFileVersionId, StringComparison.Ordinal)
            || freshEntry.Checkout is not { } binding
            || !string.Equals(binding.CheckoutId, ExpectedCheckoutId, StringComparison.Ordinal)
            || !string.Equals(binding.BaseFileVersionId, ExpectedBaseFileVersionId, StringComparison.Ordinal))
        {
            return false;
        }

        // Fresh authoritative server checkout: still Mine, complete, exact.
        return finalStatus is { State: ServerCheckoutState.Mine }
            && string.Equals(finalStatus.CheckoutId, ExpectedCheckoutId, StringComparison.Ordinal)
            && string.Equals(finalStatus.BaseFileVersionId, ExpectedBaseFileVersionId, StringComparison.Ordinal);
    }
}

/// <summary>
/// ROUND 4 (Codex HIGH - final authoritative latest-version drift): a pure,
/// COM-free gate proving the server's CURRENT authoritative latest FileVersion
/// for the repair target's <c>cadDocumentId</c> - re-read WHILE the protected
/// target lease is held, immediately before the Inventor mutation - is EXACTLY
/// the same FileVersion (id + size + SHA-256) that was confirmed / revalidated
/// earlier. If a newer FileVersion (e.g. v5) became authoritative after v4 was
/// confirmed but before mutation, this fails closed rather than letting the
/// stale confirmed target proceed.
/// </summary>
public static class FinalTargetFreshnessGate
{
    /// <returns>True only when the fresh lookup is a canonical
    ///  <see cref="LatestVersionOutcome.Found"/> result whose FileVersionId,
    ///  FileSize, and SHA-256 all match the confirmed/prepared target exactly.
    ///  ANY other outcome (unavailable, auth failure, malformed, not found,
    ///  document-not-recognized, or a value mismatch) is false - never a
    ///  silent pass-through of the previously-confirmed target.</returns>
    public static bool StillMatches(
        LatestVersionResult? fresh,
        string expectedFileVersionId,
        long expectedFileSize,
        string? expectedSha256)
    {
        if (fresh is not { Outcome: LatestVersionOutcome.Found, Version: { } version })
        {
            return false;
        }
        if (!version.HasCanonicalIntegrity)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(expectedFileVersionId)
            || !FileVersionIntegrity.IsRepresentableFileSize(expectedFileSize)
            || !FileVersionIntegrity.IsCanonicalSha256(expectedSha256))
        {
            return false;
        }

        return string.Equals(version.LatestFileVersionId, expectedFileVersionId, StringComparison.Ordinal)
            && version.FileSize == expectedFileSize
            && string.Equals(version.Sha256, expectedSha256, StringComparison.Ordinal);
    }
}

/// <summary>
/// A PROTECTED READ lease on the exact repair-target file, plus whether the
/// LAST authorization step (protected-bytes-vs-server + a fresh final
/// authoritative checkout read) passed. The coordinator holds this across the
/// single Inventor <c>ReplaceReference</c> call and disposes it immediately
/// afterwards. When <see cref="Acquired"/> is false the coordinator makes ZERO
/// COM mutation.
/// </summary>
public sealed record ProtectedRepairTargetLease(bool Acquired, string Detail, IDisposable? Handle) : IDisposable
{
    public void Dispose() => Handle?.Dispose();

    public static ProtectedRepairTargetLease Failed(string detail) => new(false, detail, null);
}

/// <summary>A fresh read of a repair-target file's current bytes, for the
///  strict post-mutation integrity check. An unreadable / missing file is a
///  safe non-<see cref="Ok"/> sentinel, never silently "matching".</summary>
public sealed record RepairTargetBinaryState(
    bool Ok,
    bool Exists = false,
    long Size = -1,
    string? Sha256 = null)
{
    public static readonly RepairTargetBinaryState Unreadable = new(false);
}

/// <summary>
/// The seam that lets the COM-free <see cref="ReferenceRepairCoordinator"/> run
/// the two mandatory boundary checks it cannot do itself:
///  1. <see cref="RevalidateBeforeMutation"/> - the full authoritative re-check
///     AFTER the engineer confirmed, BEFORE ReplaceReference;
///  2. <see cref="ReadTargetBinary"/> - a fresh size + SHA-256 read of the
///     target file for the strict post-mutation integrity comparison.
///
/// The Inventor adapter implements this (fresh COM scan + authenticated,
/// bounded HTTP + fail-closed writability probe + manifest re-hash); Core /
/// tests supply a deterministic fake.
/// </summary>
public interface IReferenceRepairPreflight
{
    RepairPreflightVerdict RevalidateBeforeMutation(ReferenceRepairPlan confirmedPlan);

    RepairTargetBinaryState ReadTargetBinary(string absolutePath);

    /// <summary>
    /// The LAST authorization step before COM mutation:
    ///  1. acquire a PROTECTED READ lease on the EXACT verified target path
    ///     (denies other-process write / delete / replace / rename; still lets
    ///     Inventor read it);
    ///  2. hash + size the bytes reachable through THAT held handle and require
    ///     an exact match with the SERVER-authoritative
    ///     <see cref="RepairPreflightVerdict.TrustedTargetSize"/> /
    ///     <see cref="RepairPreflightVerdict.TrustedTargetSha256"/>;
    ///  3. reload the manifest ONLY as a fresh consistency check and perform ONE
    ///     MORE authenticated authoritative checkout-status read, then require
    ///     BOTH to match the IMMUTABLE
    ///     <see cref="RepairPreflightVerdict.Snapshot"/> EXACTLY (parent
    ///     cadDocumentId + checkout id + base fileVersionId). The reloaded
    ///     manifest NEVER redefines the expected identity.
    ///
    /// Any request failure, auth failure, timeout, malformed / incomplete
    /// response, state change, checkout no longer <c>Mine</c>, checkout-id or
    /// base-fileVersionId change, parent-identity drift, manifest drift,
    /// lease-acquisition failure, or protected-bytes mismatch =&gt;
    /// <see cref="ProtectedRepairTargetLease.Acquired"/> == false, and the
    /// coordinator aborts with ZERO COM mutation. On success the coordinator
    /// holds the returned lease across <c>ReplaceReference</c> and disposes it
    /// immediately after.
    /// </summary>
    ProtectedRepairTargetLease AcquireProtectedTargetForMutation(
        ReferenceRepairPlan confirmedPlan, RepairPreflightVerdict verdict);
}
