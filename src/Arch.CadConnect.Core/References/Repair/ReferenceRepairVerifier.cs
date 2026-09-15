namespace Arch.CadConnect.Core.References;

/// <summary>The result of verifying a repair against a FRESH P5A scan.</summary>
public sealed record ReferenceRepairVerification(bool Verified, IReadOnlyList<string> Reasons);

/// <summary>
/// ROUND 4 (Codex HIGH - whole-reference-set proof): a COM-free, stable
/// fingerprint of ONE direct reference edge of a referencing document that
/// carries a manifest-managed identity - enough to detect removal, addition, a
/// path change, a cadDocumentId change, a FileVersionId change, OR (ROUND 5) a
/// verification-state change for every OTHER managed reference while one
/// specific edge is being repaired. Path comparison is case-insensitive (see
/// <see cref="ManagedReferenceFingerprintComparer"/>); every other field
/// (including <see cref="IsVerified"/>) is exact/ordinal.
///
/// ROUND 5 (Codex HIGH): fingerprinted regardless of whether the reference's
/// manifest binding is Verified or Unverified - <see cref="ReferenceHealthDiagnoser"/>
/// already classifies ANY reference with an exact manifest-matched identity as
/// "managed" whether or not that binding happens to be Verified right now, and
/// an unrelated managed-but-Unverified reference must not be a blind spot just
/// because it isn't eligible as a REPAIR target/source itself.
/// </summary>
public sealed record ManagedReferenceFingerprint(
    string ResolvedAbsolutePath,
    CadRelationshipKind RelationshipKind,
    string CadDocumentId,
    string FileVersionId,
    bool IsVerified);

/// <summary>Case-insensitive-on-path equality for <see cref="ManagedReferenceFingerprint"/>,
///  used only for multiset bookkeeping - never for authorization decisions
///  elsewhere in P5C (those always use exact stable identity).</summary>
public sealed class ManagedReferenceFingerprintComparer : IEqualityComparer<ManagedReferenceFingerprint>
{
    public static readonly ManagedReferenceFingerprintComparer Instance = new();

    public bool Equals(ManagedReferenceFingerprint? x, ManagedReferenceFingerprint? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return string.Equals(x.ResolvedAbsolutePath, y.ResolvedAbsolutePath, StringComparison.OrdinalIgnoreCase)
            && x.RelationshipKind == y.RelationshipKind
            && string.Equals(x.CadDocumentId, y.CadDocumentId, StringComparison.Ordinal)
            && string.Equals(x.FileVersionId, y.FileVersionId, StringComparison.Ordinal)
            && x.IsVerified == y.IsVerified;
    }

    public int GetHashCode(ManagedReferenceFingerprint obj) =>
        HashCode.Combine(
            obj.ResolvedAbsolutePath.ToUpperInvariant(),
            obj.RelationshipKind,
            obj.CadDocumentId,
            obj.FileVersionId,
            obj.IsVerified);
}

/// <summary>
/// STRICT, PURE post-repair verification against a FRESH <see cref="CadReferenceScan"/>
/// of the referencing document. It confirms - from observed facts only - that
/// the EXPLICITLY SELECTED reference TRANSITIONED to the authoritative target:
///
///  1. the count of edges of the referencing document that resolve to the
///     intended target path with the intended relationship kind, a VERIFIED
///     manifest identity, the intended cadDocumentId, and the authoritative
///     target fileVersionId rose by EXACTLY ONE versus
///     <paramref name="preMutationTargetEdgeCount"/> (captured BEFORE the
///     mutation) - so a PRE-EXISTING edge already at the target can never be
///     accepted as proof the selected edge was repaired;
///  2. no edge of that document still resolves to the OLD (selected) reference
///     path;
///  3. (ROUND 4) the WHOLE multiset of the document's direct managed
///     references, captured before the mutation
///     (<paramref name="preMutationReferenceFingerprint"/>), equals the fresh
///     scan's multiset EXACTLY except for the one authorized old-&gt;target
///     transition - so an unrelated managed reference changing (removed,
///     added, or its path / cadDocumentId / FileVersionId altered) at the same
///     time can never be hidden inside a "Repaired" result.
///
/// A count that did not rise by exactly one (selected edge missing, an
/// impersonating pre-existing edge, an ambiguous multi-edge transition), a
/// wrong relationship kind, wrong parent, unverified identity, a cadDocumentId
/// / fileVersionId mismatch, an uncaptured pre-mutation count, or a
/// whole-reference-set mismatch is reported as NOT verified. The Inventor API
/// returning without an exception is never treated as success. (The target
/// FILE's current size / SHA-256 is re-checked separately by
/// <see cref="ReferenceRepairCoordinator"/> against the SERVER-authoritative
/// metadata.)
/// </summary>
public static class ReferenceRepairVerifier
{
    /// <summary>
    /// The count of edges of <paramref name="plan"/>'s referencing document that
    /// resolve to the authoritative target with the FULL intended stable
    /// identity. Used identically before the mutation (to snapshot the
    /// pre-existing target edges) and after (to prove exactly one new edge -
    /// the selected one - joined that set).
    /// </summary>
    public static int CountResolvedTargetEdges(ReferenceRepairPlan plan, CadReferenceScan scan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scan);

        var target = Normalize(plan.ProposedTargetPath);
        var parent = Normalize(plan.ReferencingDocumentPath);
        if (target is null || parent is null
            || string.IsNullOrWhiteSpace(plan.CadDocumentId)
            || string.IsNullOrWhiteSpace(plan.AuthoritativeTargetFileVersionId))
        {
            return -1;
        }

        return scan.References.Count(r =>
            Normalize(r.ParentAbsolutePath) is { } pp
            && string.Equals(pp, parent, StringComparison.OrdinalIgnoreCase)
            && IsRepairedTargetEdge(r, plan, target));
    }

    /// <summary>
    /// ROUND 4 (extended ROUND 5): capture the COM-free fingerprint of EVERY
    /// direct reference of <paramref name="plan"/>'s referencing document that
    /// carries a manifest-managed identity, as observed in
    /// <paramref name="scan"/> - whether that identity's binding is Verified
    /// OR Unverified (the same "managed" classification
    /// <see cref="ReferenceHealthDiagnoser"/> already uses; only a reference
    /// with NO manifest-matched identity at all is excluded - it carries no
    /// stable identity to track). Used identically before the mutation (the
    /// immutable pre-mutation baseline) and after (to prove nothing but the
    /// one authorized transition changed - including no unrelated reference's
    /// verification state silently flipping). An edge that becomes unresolved
    /// is correctly seen as "removed" by the multiset comparison.
    /// </summary>
    public static IReadOnlyList<ManagedReferenceFingerprint> CaptureManagedReferenceFingerprint(
        ReferenceRepairPlan plan, CadReferenceScan scan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scan);

        var parent = Normalize(plan.ReferencingDocumentPath);
        if (parent is null)
        {
            return Array.Empty<ManagedReferenceFingerprint>();
        }

        return scan.References
            .Where(r => Normalize(r.ParentAbsolutePath) is { } pp
                && string.Equals(pp, parent, StringComparison.OrdinalIgnoreCase))
            .Where(r => r.Resolution == CadReferenceResolution.Resolved
                && r.ManifestIdentity is not null)
            .Select(r => new ManagedReferenceFingerprint(
                Normalize(r.ResolvedAbsolutePath) ?? r.ResolvedAbsolutePath ?? "",
                r.RelationshipKind,
                r.ManifestIdentity!.CadDocumentId,
                r.ManifestIdentity!.FileVersionId,
                r.ManifestIdentity!.IsVerified))
            .ToArray();
    }

    private static bool IsRepairedTargetEdge(CadReference r, ReferenceRepairPlan plan, string normalizedTarget) =>
        r.Resolution == CadReferenceResolution.Resolved
        && Normalize(r.ResolvedAbsolutePath) is { } p
        && string.Equals(p, normalizedTarget, StringComparison.OrdinalIgnoreCase)
        && r.RelationshipKind == plan.RelationshipKind
        && r.ManifestIdentity is { IsVerified: true } id
        && string.Equals(id.CadDocumentId, plan.CadDocumentId, StringComparison.Ordinal)
        && string.Equals(id.FileVersionId, plan.AuthoritativeTargetFileVersionId, StringComparison.Ordinal);

    public static ReferenceRepairVerification Verify(
        ReferenceRepairPlan plan,
        CadReferenceScan rescan,
        int preMutationTargetEdgeCount,
        IReadOnlyList<ManagedReferenceFingerprint>? preMutationReferenceFingerprint)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rescan);

        var reasons = new List<string>();

        var target = Normalize(plan.ProposedTargetPath);
        var oldPath = Normalize(plan.CurrentReferencePath);
        var parent = Normalize(plan.ReferencingDocumentPath);

        if (target is null || parent is null)
        {
            return Fail("The repair plan does not carry the absolute paths needed to verify the result.");
        }
        if (string.IsNullOrWhiteSpace(plan.CadDocumentId)
            || string.IsNullOrWhiteSpace(plan.AuthoritativeTargetFileVersionId))
        {
            return Fail("The repair plan does not carry the exact stable target identity needed to verify the result.");
        }
        if (!plan.HasServerAuthoritativeTargetIntegrity)
        {
            return Fail("The repair plan does not carry canonical SERVER-authoritative target integrity metadata - "
                + "verification cannot confirm the result against a trusted authority.");
        }
        if (preMutationTargetEdgeCount < 0)
        {
            return Fail("The pre-mutation evidence needed to prove the SELECTED reference transitioned to the target "
                + "was not captured - cannot confirm the repair (fail closed).");
        }
        if (preMutationReferenceFingerprint is null)
        {
            return Fail("The pre-mutation whole-reference-set fingerprint was not captured - cannot prove no "
                + "unrelated managed reference changed (fail closed).");
        }

        var parentEdges = rescan.References
            .Where(r => Normalize(r.ParentAbsolutePath) is { } p
                && string.Equals(p, parent, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (parentEdges.Length == 0)
        {
            return Fail("The fresh scan reports no references at all for the referencing document.");
        }

        // The SELECTED edge must have TRANSITIONED into the target set: the
        // count of edges resolving to the target with the full stable identity
        // must be EXACTLY one higher than it was before the mutation. A
        // pre-existing edge already at the target (BEFORE mutation) can never be
        // accepted as proof the selected stale edge was repaired.
        var repaired = parentEdges.Where(r => IsRepairedTargetEdge(r, plan, target)).ToArray();
        var expected = preMutationTargetEdgeCount + 1;

        if (repaired.Length == 0)
        {
            // Explain the closest miss for a resolved edge to the target path.
            var nearest = parentEdges.FirstOrDefault(r =>
                r.Resolution == CadReferenceResolution.Resolved
                && Normalize(r.ResolvedAbsolutePath) is { } p
                && string.Equals(p, target, StringComparison.OrdinalIgnoreCase));

            if (nearest is null)
            {
                return Fail($"After the replacement, no reference of the document resolves to the target: {plan.ProposedTargetPath}");
            }
            if (nearest.RelationshipKind != plan.RelationshipKind)
            {
                return Fail($"An edge resolves to the target path but with the WRONG relationship kind "
                    + $"({nearest.RelationshipKind}, expected {plan.RelationshipKind}).");
            }
            if (nearest.ManifestIdentity is null)
            {
                return Fail("The new reference resolves to the target path but the fresh scan does not see it as a "
                    + "managed file - its stable identity could not be confirmed.");
            }
            if (!nearest.ManifestIdentity.IsVerified)
            {
                return Fail("The new reference is managed but its workspace-manifest binding is Unverified - "
                    + "an unverified binding cannot confirm a repair.");
            }
            if (!string.Equals(nearest.ManifestIdentity.CadDocumentId, plan.CadDocumentId, StringComparison.Ordinal))
            {
                return Fail($"The new reference is managed, but as a DIFFERENT document "
                    + $"(cadDocumentId {nearest.ManifestIdentity.CadDocumentId}, expected {plan.CadDocumentId}).");
            }
            return Fail($"The new reference is managed but pinned to FileVersion {nearest.ManifestIdentity.FileVersionId}, "
                + $"not the authoritative target {plan.AuthoritativeTargetFileVersionId}.");
        }

        if (repaired.Length != expected)
        {
            return Fail(
                $"The count of edges resolving to the authoritative target with the full stable identity is "
                + $"{repaired.Length}, but the SELECTED reference transitioning would make it exactly "
                + $"{expected} (it was {preMutationTargetEdgeCount} before the mutation). "
                + (repaired.Length <= preMutationTargetEdgeCount
                    ? "The selected reference was NOT repaired - a pre-existing target edge cannot stand in for it."
                    : "More than one edge changed - an ambiguous transition, treating as a FAILURE."));
        }

        reasons.Add($"The count of edges resolving to the target as the expected managed document rose by exactly "
            + $"one ({preMutationTargetEdgeCount} -> {repaired.Length}: cadDocumentId {plan.CadDocumentId}, "
            + $"fileVersionId {plan.AuthoritativeTargetFileVersionId}, relationship {plan.RelationshipKind}) - "
            + "the selected reference transitioned to the target.");

        if (oldPath is not null
            && !string.Equals(oldPath, target, StringComparison.OrdinalIgnoreCase)
            && parentEdges.Any(r => r.Resolution == CadReferenceResolution.Resolved
                && Normalize(r.ResolvedAbsolutePath) is { } p
                && string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase)))
        {
            return new ReferenceRepairVerification(false,
                reasons.Append($"A reference still resolves to the OLD path: {plan.CurrentReferencePath}").ToArray());
        }
        reasons.Add("No reference of the document still resolves to the old (selected) path.");

        // ROUND 4: prove nothing ELSE changed. The whole multiset of direct
        // managed references must equal the pre-mutation baseline exactly,
        // except for the one authorized old->target transition.
        if (oldPath is not null)
        {
            // The authorized OLD and NEW (target) edges must each be a
            // VERIFIED managed identity - already enforced by the checks
            // above (the selected reference required a verified manifest
            // identity to be eligible; the "new edge" checks above required
            // IsVerified: true too) - so both fingerprints here are recorded
            // as Verified. This is about protecting UNRELATED managed-but-
            // Unverified references, never about relaxing eligibility for the
            // repaired edge itself.
            var expectedOld = new ManagedReferenceFingerprint(
                oldPath, plan.RelationshipKind, plan.CadDocumentId!, plan.CurrentPinnedFileVersionId ?? "", IsVerified: true);
            var expectedNew = new ManagedReferenceFingerprint(
                target, plan.RelationshipKind, plan.CadDocumentId!, plan.AuthoritativeTargetFileVersionId!, IsVerified: true);
            var postFingerprint = CaptureManagedReferenceFingerprint(plan, rescan);

            var setResult = VerifyWholeReferenceSetTransition(
                preMutationReferenceFingerprint, postFingerprint, expectedOld, expectedNew);
            if (!setResult.Ok)
            {
                return new ReferenceRepairVerification(false,
                    reasons.Append(setResult.Reason ?? "The whole reference set changed beyond the one authorized transition.").ToArray());
            }
            reasons.Add("No OTHER direct managed reference of the document changed (whole-reference-set proof).");
        }

        return new ReferenceRepairVerification(true, reasons);
    }

    /// <summary>
    /// ROUND 4: pure multiset comparison proving <paramref name="post"/> equals
    /// <paramref name="pre"/> with EXACTLY one <paramref name="expectedOld"/>
    /// removed and EXACTLY one <paramref name="expectedNew"/> added - nothing
    /// else. Any other difference (an unrelated reference removed, added, or
    /// changed; the target's multiplicity inconsistent; the old reference
    /// still present) fails with a specific reason.
    /// </summary>
    private static (bool Ok, string? Reason) VerifyWholeReferenceSetTransition(
        IReadOnlyList<ManagedReferenceFingerprint> pre,
        IReadOnlyList<ManagedReferenceFingerprint> post,
        ManagedReferenceFingerprint expectedOld,
        ManagedReferenceFingerprint expectedNew)
    {
        var cmp = ManagedReferenceFingerprintComparer.Instance;
        var expectedPost = ToMultiset(pre, cmp);

        if (!TryRemoveOne(expectedPost, expectedOld, cmp))
        {
            return (false, "The authorized old reference was not present in the captured pre-mutation reference set "
                + "- cannot prove the whole-set transition (fail closed).");
        }
        AddOne(expectedPost, expectedNew, cmp);

        var actualPost = ToMultiset(post, cmp);
        if (MultisetsEqual(expectedPost, actualPost, cmp))
        {
            return (true, null);
        }

        var missing = Difference(expectedPost, actualPost, cmp); // expected after, but absent
        var extra = Difference(actualPost, expectedPost, cmp);   // present after, but not expected

        var reasons = new List<string>();
        if (missing.Any(m => cmp.Equals(m.Key, expectedNew)))
        {
            reasons.Add("the authoritative target reference is missing, or its multiplicity is inconsistent with "
                + "the expected file-level semantics, after the mutation");
        }
        if (extra.Any(m => cmp.Equals(m.Key, expectedOld)))
        {
            reasons.Add("the authorized old reference still remains after the mutation");
        }
        if (extra.Any(m => cmp.Equals(m.Key, expectedNew)))
        {
            reasons.Add("the authoritative target reference's multiplicity is inconsistent with the expected "
                + "file-level semantics after the mutation");
        }
        var otherMissing = missing.Where(m => !cmp.Equals(m.Key, expectedNew) && !cmp.Equals(m.Key, expectedOld)).ToArray();
        var otherExtra = extra.Where(m => !cmp.Equals(m.Key, expectedOld) && !cmp.Equals(m.Key, expectedNew)).ToArray();
        if (otherMissing.Length > 0)
        {
            reasons.Add($"{otherMissing.Sum(m => m.Value)} unrelated direct managed reference(s) are missing / "
                + "changed after the mutation, that were present before it");
        }
        if (otherExtra.Length > 0)
        {
            reasons.Add($"{otherExtra.Sum(m => m.Value)} unrelated direct managed reference(s) are present / "
                + "changed after the mutation, that were NOT present before it");
        }
        if (reasons.Count == 0)
        {
            reasons.Add("the whole reference set differs from the expected post-mutation state");
        }

        return (false, "the whole reference set changed beyond the one authorized transition: " + string.Join("; ", reasons) + ".");
    }

    private static Dictionary<ManagedReferenceFingerprint, int> ToMultiset(
        IReadOnlyList<ManagedReferenceFingerprint> items, IEqualityComparer<ManagedReferenceFingerprint> cmp)
    {
        var map = new Dictionary<ManagedReferenceFingerprint, int>(cmp);
        foreach (var item in items)
        {
            map[item] = map.TryGetValue(item, out var n) ? n + 1 : 1;
        }
        return map;
    }

    private static bool TryRemoveOne(
        Dictionary<ManagedReferenceFingerprint, int> multiset, ManagedReferenceFingerprint item,
        IEqualityComparer<ManagedReferenceFingerprint> cmp)
    {
        if (!multiset.TryGetValue(item, out var n) || n <= 0)
        {
            return false;
        }
        if (n == 1) multiset.Remove(item); else multiset[item] = n - 1;
        return true;
    }

    private static void AddOne(
        Dictionary<ManagedReferenceFingerprint, int> multiset, ManagedReferenceFingerprint item,
        IEqualityComparer<ManagedReferenceFingerprint> cmp)
    {
        multiset[item] = multiset.TryGetValue(item, out var n) ? n + 1 : 1;
    }

    private static bool MultisetsEqual(
        Dictionary<ManagedReferenceFingerprint, int> a, Dictionary<ManagedReferenceFingerprint, int> b,
        IEqualityComparer<ManagedReferenceFingerprint> cmp)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, count) in a)
        {
            if (!b.TryGetValue(key, out var otherCount) || otherCount != count) return false;
        }
        return true;
    }

    private static Dictionary<ManagedReferenceFingerprint, int> Difference(
        Dictionary<ManagedReferenceFingerprint, int> a, Dictionary<ManagedReferenceFingerprint, int> b,
        IEqualityComparer<ManagedReferenceFingerprint> cmp)
    {
        var result = new Dictionary<ManagedReferenceFingerprint, int>(cmp);
        foreach (var (key, count) in a)
        {
            var otherCount = b.TryGetValue(key, out var oc) ? oc : 0;
            if (count > otherCount)
            {
                result[key] = count - otherCount;
            }
        }
        return result;
    }

    private static ReferenceRepairVerification Fail(string reason) =>
        new(false, new[] { reason });

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }
        try { return Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
