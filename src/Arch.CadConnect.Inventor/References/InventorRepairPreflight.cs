using System.Security.Cryptography;

using Arch.CadConnect.Api;
using Arch.CadConnect.Core.Files;
using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.References;

/// <summary>
/// The Inventor-side <see cref="IReferenceRepairPreflight"/>: it performs the
/// POST-CONFIRMATION authoritative re-check (fresh COM scan + authenticated,
/// bounded HTTP + fail-closed writability probe + verified-manifest re-hash)
/// that the COM-free <see cref="ReferenceRepairCoordinator"/> runs AFTER the
/// engineer confirmed and IMMEDIATELY before any Inventor mutation.
///
/// Any mismatch, missing information, timeout, read failure, auth failure,
/// server failure, or drift yields <see cref="RepairPreflightVerdict.Authorized"/>
/// == false, and the coordinator makes ZERO COM mutation. It never checks a
/// file out, never clears read-only protection, never saves, and never runs a
/// broad Get Latest.
/// </summary>
internal sealed class InventorRepairPreflight : IReferenceRepairPreflight
{
    private static readonly TimeSpan HttpBudget = TimeSpan.FromSeconds(15);

    private readonly InventorApi.Application _application;
    private readonly ArchConnectionManager _connection;
    private readonly IReadOnlyList<string> _roots;

    public InventorRepairPreflight(
        InventorApi.Application application,
        ArchConnectionManager connection,
        IReadOnlyList<string> roots)
    {
        _application = application;
        _connection = connection;
        _roots = roots;
    }

    public RepairPreflightVerdict RevalidateBeforeMutation(ReferenceRepairPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.CadDocumentId)
            || string.IsNullOrWhiteSpace(plan.CurrentPinnedFileVersionId)
            || string.IsNullOrWhiteSpace(plan.AuthoritativeTargetFileVersionId)
            || string.IsNullOrWhiteSpace(plan.CurrentReferencePath)
            || string.IsNullOrWhiteSpace(plan.ProposedTargetPath))
        {
            return RepairPreflightVerdict.Deny("the confirmed plan is missing a stable identity or a path.");
        }

        // 1. FRESH COM re-scan of the referencing document.
        CadReferenceScan scan;
        try
        {
            scan = new InventorReferenceScanner(_application, _roots).Scan(plan.ReferencingDocumentPath);
        }
        catch (Exception ex)
        {
            return RepairPreflightVerdict.Deny($"a fresh re-scan of the referencing document failed ({ex.GetType().Name}).");
        }

        // 2. The current reference must STILL be present exactly, with a
        //    verified manifest identity that matches the confirmed plan.
        var current = scan.References.Where(r =>
                PathEquals(r.ParentAbsolutePath, plan.ReferencingDocumentPath)
                && PathEquals(r.ResolvedAbsolutePath, plan.CurrentReferencePath)
                && r.RelationshipKind == plan.RelationshipKind
                && r.ManifestIdentity is { IsVerified: true } id
                && string.Equals(id.CadDocumentId, plan.CadDocumentId, StringComparison.Ordinal)
                && string.Equals(id.FileVersionId, plan.CurrentPinnedFileVersionId, StringComparison.Ordinal))
            .ToArray();
        if (current.Length != 1)
        {
            return RepairPreflightVerdict.Deny(
                "the current reference (its exact parent / relationship / verified stable identity) no longer "
                + "matches the confirmed preview.");
        }

        // 3. Fresh AUTHENTICATED, BOUNDED authoritative reads: checkout status
        //    for the referencing document + the latest FileVersion for the
        //    referenced document.
        var parentEntry = FindManifestEntry(plan.ReferencingDocumentPath);
        ServerCheckoutStatus? status;
        LatestVersionLookup latest;
        try
        {
            using var cts = new CancellationTokenSource(HttpBudget);
            var parentCadId = parentEntry?.CadDocumentId;
            status = string.IsNullOrWhiteSpace(parentCadId)
                ? null
                : RunBounded(() => _connection.GetCheckoutStatusAsync(parentCadId!, cts.Token));
            latest = RunBounded(() => _connection.GetLatestVersionsAsync(new[] { plan.CadDocumentId! }, cts.Token));
        }
        catch (Exception ex)
        {
            return RepairPreflightVerdict.Deny(
                $"the authoritative checkout / version re-check could not be completed ({ex.GetType().Name}).");
        }

        // 4. FAIL-CLOSED writability probe - only an affirmative Writable, and
        //    only a complete server-confirmed "mine" checkout, authorizes.
        var writability = LocalWritabilityProbe.Probe(plan.ReferencingDocumentPath);
        var checkoutState = CheckoutStateMachine.Evaluate(
            parentEntry, fileExists: writability != LocalWritability.Missing, status);
        var referencing = ReferenceRepairWritability.Evaluate(
            plan.ReferencingDocumentPath,
            checkoutState,
            onDiskWritable: LocalWritabilityProbe.IsAffirmativelyWritable(writability),
            authoritativeCheckoutConfirmed: ReferenceRepairWritability.AuthoritativeCheckoutMatches(parentEntry, status));
        if (!referencing.IsWritable)
        {
            return RepairPreflightVerdict.Deny(
                "the referencing document is no longer affirmatively writable / server-confirmed checked out: "
                + referencing.WritabilityDetail);
        }

        // 5. The reference must still be STALE with the SAME authoritative
        //    latest FileVersion AND canonical SERVER-authoritative integrity
        //    metadata (the mutable local manifest is NOT the authority).
        var freshEntry = ReferenceHealthDiagnoser.Evaluate(current[0], childEnumerated: true);
        var assessment = ReferenceVersionClassifier.Assess(freshEntry, latest);
        if (assessment.Status != PlmVersionStatus.Stale
            || !string.Equals(assessment.AuthoritativeLatestFileVersionId,
                plan.AuthoritativeTargetFileVersionId, StringComparison.Ordinal))
        {
            return RepairPreflightVerdict.Deny("the authoritative version status changed since confirmation.");
        }
        if (!assessment.HasAuthoritativeTargetIntegrity)
        {
            return RepairPreflightVerdict.Deny(
                "the authenticated server did not supply canonical FileVersion integrity metadata (size + SHA-256) "
                + "for the authoritative target.");
        }
        var serverSize = assessment.AuthoritativeTargetFileSize;
        var serverSha = assessment.AuthoritativeTargetSha256 ?? "";

        // Cross-check: the SAME server integrity that was previewed must still
        // hold now (the confirmed plan carries it).
        if (plan.HasServerAuthoritativeTargetIntegrity
            && (plan.ServerAuthoritativeTargetFileSize != serverSize
                || !string.Equals(plan.ServerAuthoritativeTargetSha256, serverSha, StringComparison.Ordinal)))
        {
            return RepairPreflightVerdict.Deny(
                "the server-authoritative target integrity metadata changed since the preview.");
        }

        // 6. Re-resolve (STABLE LOCAL PATH MAPPING from the verified manifest)
        //    + RE-HASH the target's current bytes against the SERVER metadata.
        var target = new WorkspaceManifestRepairTargetLocator(_roots)
            .Locate(plan.CadDocumentId!, plan.AuthoritativeTargetFileVersionId!, serverSize, serverSha);
        if (target.Outcome != RepairTargetOutcome.Resolved
            || target.Candidate is not { BinaryIntegrityVerified: true } candidate)
        {
            return RepairPreflightVerdict.Deny(
                "the authoritative target FileVersion could no longer be resolved to a single local copy whose "
                + "current bytes match the SERVER-authoritative size + SHA-256 (" + target.Outcome + ").");
        }
        if (!PathEquals(candidate.AbsolutePath, plan.ProposedTargetPath))
        {
            return RepairPreflightVerdict.Deny("the resolved target path changed since confirmation.");
        }

        // 7. Re-plan and require an EXACT identity match with the confirmed plan.
        var freshPlan = ReferenceRepairPlanner.Plan(assessment, target, referencing);
        if (!freshPlan.CanProceed || !SamePreviewIdentity(plan, freshPlan))
        {
            return RepairPreflightVerdict.Deny(
                "the re-planned repair no longer matches the confirmed preview ("
                + freshPlan.EligibilityLabel + " - " + freshPlan.EligibilityDetail + ").");
        }

        // 8. Capture the IMMUTABLE authorization snapshot from the VERIFIED
        //    checkout binding (stable Arch IDs only). The mutation boundary
        //    compares to THIS - a manifest reloaded then can never redefine it.
        var binding = parentEntry?.Checkout;
        if (parentEntry is null || binding is null
            || string.IsNullOrWhiteSpace(parentEntry.CadDocumentId)
            || string.IsNullOrWhiteSpace(binding.CheckoutId)
            || string.IsNullOrWhiteSpace(binding.BaseFileVersionId))
        {
            return RepairPreflightVerdict.Deny(
                "the referencing document's verified checkout binding (parent cadDocumentId + checkout id + base "
                + "fileVersionId) could not be captured as an immutable authorization snapshot - P5C requires an "
                + "authoritative checkout of the referencing document.");
        }
        var snapshot = new RepairAuthorizationSnapshot(
            parentEntry.CadDocumentId, binding.CheckoutId, binding.BaseFileVersionId);
        if (!snapshot.IsComplete
            || !snapshot.MatchesFinalState(parentEntry, status))
        {
            return RepairPreflightVerdict.Deny(
                "the verified checkout binding and the authoritative server checkout do not agree exactly - "
                + "cannot capture a trustworthy authorization snapshot.");
        }

        // 9. Pre-mutation evidence: how many edges ALREADY resolve to the target
        //    with the full stable identity. Post-mutation verification requires
        //    this to rise by EXACTLY ONE (the selected edge).
        var preTargetCount = ReferenceRepairVerifier.CountResolvedTargetEdges(freshPlan, scan);
        if (preTargetCount < 0)
        {
            return RepairPreflightVerdict.Deny(
                "the pre-mutation target-edge evidence could not be captured.");
        }

        // 10. ROUND 4 (Codex HIGH): capture the IMMUTABLE whole-reference-set
        //     fingerprint (every direct managed reference of the referencing
        //     document) at the SAME moment - so post-mutation verification can
        //     prove no OTHER managed reference changed alongside the repair.
        var preFingerprint = ReferenceRepairVerifier.CaptureManagedReferenceFingerprint(freshPlan, scan);

        // The TRUSTED size / SHA-256 handed to the coordinator are the
        // SERVER-authoritative values - never the local manifest.
        return new RepairPreflightVerdict(
            true, "revalidated after confirmation against server-authoritative integrity",
            serverSize, serverSha, snapshot, preTargetCount, preFingerprint);
    }

    public ProtectedRepairTargetLease AcquireProtectedTargetForMutation(
        ReferenceRepairPlan confirmedPlan, RepairPreflightVerdict verdict)
    {
        if (confirmedPlan is null || verdict is null || !verdict.Authorized
            || string.IsNullOrWhiteSpace(confirmedPlan.ProposedTargetPath)
            || verdict.TrustedTargetSize < 0
            || string.IsNullOrWhiteSpace(verdict.TrustedTargetSha256)
            || verdict.Snapshot is not { IsComplete: true } snapshot)
        {
            return ProtectedRepairTargetLease.Failed("the pre-mutation authorization inputs are incomplete.");
        }

        // 1. Acquire a PROTECTED READ lease on the EXACT verified target path.
        //    While it is held no other process can write, delete, replace, or
        //    rename the file; Inventor can still read it.
        var lease = ProtectedFileLease.Acquire(confirmedPlan.ProposedTargetPath);
        if (!lease.IsHeld)
        {
            return ProtectedRepairTargetLease.Failed("a protected read lease on the target could not be acquired: " + lease.Detail);
        }

        try
        {
            // 2. Prove the bytes THIS held handle protects match the
            //    SERVER-authoritative size + SHA-256.
            var (ok, size, sha) = lease.HashProtected();
            if (!ok
                || size != verdict.TrustedTargetSize
                || !string.Equals(sha, verdict.TrustedTargetSha256, StringComparison.OrdinalIgnoreCase))
            {
                lease.Dispose();
                return ProtectedRepairTargetLease.Failed(
                    "the bytes protected by the lease do not match the server-authoritative size / SHA-256.");
            }

            // 3. Final manifest + authoritative checkout re-check: reload the
            //    manifest ONLY as a fresh consistency check and perform ONE
            //    MORE authoritative checkout-status read - for the PARENT
            //    cadDocumentId from the IMMUTABLE snapshot, never a freshly
            //    loaded manifest id. Both the fresh manifest binding AND the
            //    server checkout must match the snapshot EXACTLY. ROUND 5
            //    (Codex HIGH - last-mile ordering): this now runs BEFORE the
            //    final latest-version proof, so that proof is the LAST
            //    server-authority action before mutation (see step 4 below) -
            //    not sandwiched between it and this checkout re-check.
            var freshEntry = FindManifestEntry(confirmedPlan.ReferencingDocumentPath);

            ServerCheckoutStatus? finalStatus;
            try
            {
                using var cts = new CancellationTokenSource(HttpBudget);
                finalStatus = RunBounded(() =>
                    _connection.GetCheckoutStatusAsync(snapshot.ParentCadDocumentId, cts.Token));
            }
            catch (Exception ex)
            {
                lease.Dispose();
                return ProtectedRepairTargetLease.Failed(
                    $"the final authoritative checkout read could not be completed ({ex.GetType().Name}).");
            }

            if (!snapshot.MatchesFinalState(freshEntry, finalStatus))
            {
                lease.Dispose();
                return ProtectedRepairTargetLease.Failed(
                    "the final manifest + authoritative checkout state do not EXACTLY match the immutable "
                    + "authorization snapshot (parent cadDocumentId / checkout id / base fileVersionId drift, or "
                    + "the checkout is no longer a complete 'Mine').");
            }

            // 4. ROUND 5 (Codex HIGH - final target freshness must be
            //    LAST-MILE): the FINAL server-authority action of the entire
            //    repair, deliberately ordered AFTER every other local/manifest/
            //    checkout check above (and, for an assembly, after the fresh
            //    live occurrence-set validation + representative selection
            //    already completed back in Prepare - see
            //    InventorReferenceReplacer). WHILE the protected lease is
            //    still held, re-read the authoritative latest FileVersion for
            //    the target's cadDocumentId ONE MORE TIME and require it to be
            //    EXACTLY the confirmed/prepared target (id + size + SHA-256).
            //    If a newer FileVersion became authoritative (e.g. v5 after v4
            //    was confirmed), or the lookup cannot be completed at all,
            //    abort BEFORE mutation. NOTHING that touches the network, the
            //    manifest, or re-enumerates COM state may run after this
            //    succeeds - only the prepared replacement's OWN minimal,
            //    no-network re-check of its already-selected pointer, then the
            //    single Inventor mutation call.
            LatestVersionLookup freshLatest;
            try
            {
                using var cts = new CancellationTokenSource(HttpBudget);
                freshLatest = RunBounded(() =>
                    _connection.GetLatestVersionsAsync(new[] { confirmedPlan.CadDocumentId! }, cts.Token));
            }
            catch (Exception ex)
            {
                lease.Dispose();
                return ProtectedRepairTargetLease.Failed(
                    $"the final authoritative latest-version re-check could not be completed ({ex.GetType().Name}).");
            }

            var freshTarget = freshLatest.Get(confirmedPlan.CadDocumentId!);
            if (!FinalTargetFreshnessGate.StillMatches(
                    freshTarget, confirmedPlan.AuthoritativeTargetFileVersionId!,
                    verdict.TrustedTargetSize, verdict.TrustedTargetSha256))
            {
                lease.Dispose();
                return ProtectedRepairTargetLease.Failed(
                    "the authoritative latest FileVersion for the target changed (fileVersionId, size, or SHA-256) "
                    + "immediately before mutation, or could not be freshly re-confirmed - refusing to replace a "
                    + "target that is no longer proven authoritative.");
            }

            // All gates passed, in the LAST-MILE order - hand the still-open
            // lease to the coordinator, which holds it across the single
            // Inventor mutation call and releases it the instant that returns.
            return new ProtectedRepairTargetLease(true,
                "protected target lease held; server integrity + final checkout + final latest-version (last-mile) re-verified", lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public RepairTargetBinaryState ReadTargetBinary(string absolutePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(absolutePath)
                || !Path.IsPathFullyQualified(absolutePath)
                || !File.Exists(absolutePath))
            {
                return new RepairTargetBinaryState(Ok: true, Exists: false);
            }
            using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var size = stream.Length;
            var sha = Convert.ToHexString(SHA256.HashData(stream));
            return new RepairTargetBinaryState(Ok: true, Exists: true, Size: size, Sha256: sha);
        }
        catch
        {
            return RepairTargetBinaryState.Unreadable;
        }
    }

    private static T RunBounded<T>(Func<Task<T>> start)
    {
        // The HTTP work runs on pool threads (the Api layer uses
        // ConfigureAwait(false) throughout); the CancellationToken passed into
        // `start` bounds it. Blocking here is acceptable for this one-shot
        // safety re-check.
        return start().GetAwaiter().GetResult();
    }

    private WorkspaceManifestEntry? FindManifestEntry(string absolutePath)
    {
        foreach (var root in _roots)
        {
            try
            {
                var entry = WorkspaceManifest.LoadOrEmpty(root).FindByAbsolutePath(absolutePath);
                if (entry is not null)
                {
                    return entry;
                }
            }
            catch (Exception ex) when (ex is IOException or WorkspaceRootException or UnauthorizedAccessException)
            {
                // next root
            }
        }
        return null;
    }

    private static bool SamePreviewIdentity(ReferenceRepairPlan a, ReferenceRepairPlan b) =>
        PathEquals(a.ReferencingDocumentPath, b.ReferencingDocumentPath)
        && PathEquals(a.CurrentReferencePath, b.CurrentReferencePath)
        && PathEquals(a.ProposedTargetPath, b.ProposedTargetPath)
        && a.RelationshipKind == b.RelationshipKind
        && string.Equals(a.CadDocumentId, b.CadDocumentId, StringComparison.Ordinal)
        && string.Equals(a.CurrentPinnedFileVersionId, b.CurrentPinnedFileVersionId, StringComparison.Ordinal)
        && string.Equals(a.AuthoritativeTargetFileVersionId, b.AuthoritativeTargetFileVersionId, StringComparison.Ordinal);

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
