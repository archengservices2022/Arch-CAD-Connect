using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.References.Repair.RepairFixtures;

namespace Arch.CadConnect.Core.Tests.References.Repair;

/// <summary>
/// Post-repair verification works ONLY from a fresh scan's observed facts and
/// PRE-MUTATION evidence: the count of edges of the referencing document that
/// resolve to the authoritative target with the full stable identity must have
/// risen by EXACTLY ONE - so a pre-existing target edge can never impersonate
/// the repaired selected edge - the old (selected) path must be gone, the new
/// edge must be managed as the expected document + FileVersion, and (ROUND 4)
/// the WHOLE multiset of the document's direct managed references must equal
/// the pre-mutation baseline exactly except for that one authorized transition.
/// </summary>
public class ReferenceRepairVerifierTests
{
    private static readonly string Parent = RootIam;
    private static readonly string OldPath = P("Arch", "Job1", "PART-A.ipt");
    private static readonly string NewPath = P("Arch", "Latest", "PART-A.ipt");
    private static readonly string OtherTargetName = P("Arch", "Latest", "PART-B.ipt");

    private static ReferenceRepairPlan EligiblePlan() =>
        ReferenceRepairPlanner.Plan(
            Stale(cad: "cad_a", pinned: "fv_v1", latest: "fv_v3"),
            VerifiedTarget(cad: "cad_a", fv: "fv_v3", path: NewPath),
            WritableReferencing(Parent));

    // The old (pre-mutation, selected-stale) and new (authoritative target)
    // fingerprints matching EligiblePlan()'s own identity exactly.
    private static readonly ManagedReferenceFingerprint OldFp =
        new(OldPath, CadRelationshipKind.Component, "cad_a", "fv_v1", IsVerified: true);
    private static readonly ManagedReferenceFingerprint NewFp =
        new(NewPath, CadRelationshipKind.Component, "cad_a", "fv_v3", IsVerified: true);

    /// <summary>Verify with a pre-mutation target-edge count of 0 (the default
    ///  case: nothing was at the target before the repair) and a pre-mutation
    ///  whole-reference-set fingerprint of JUST the old (selected) edge - i.e.
    ///  "no other managed reference exists" - the implicit assumption of every
    ///  test in this file that does not explicitly say otherwise.</summary>
    private static ReferenceRepairVerification Verify(
        ReferenceRepairPlan plan, CadReferenceScan scan, int preCount = 0,
        IReadOnlyList<ManagedReferenceFingerprint>? preFingerprint = null)
        => ReferenceRepairVerifier.Verify(plan, scan, preCount, preFingerprint ?? new[] { OldFp });

    private static CadReferenceScan Scan(params CadReference[] refs) => new(
        new CadReferenceRoot(Parent, CadDocumentType.Iam, null),
        refs,
        new[] { new CadReferenceNode(Parent, CadDocumentType.Iam, true) },
        Now);

    private static CadReference Edge(
        string resolved, CadManifestIdentity? identity, string? parent = null,
        CadRelationshipKind kind = CadRelationshipKind.Component) => new()
        {
            ParentAbsolutePath = parent ?? Parent,
            InventorReportedName = Path.GetFileName(resolved),
            ResolvedAbsolutePath = resolved,
            ReferenceType = CadDocumentType.Ipt,
            RelationshipKind = kind,
            Resolution = CadReferenceResolution.Resolved,
            Scope = ReferenceWorkspaceScope.InsideWorkspace,
            ManifestIdentity = identity,
        };

    private static CadManifestIdentity TargetId => new("cad_a", "fv_v3", "DOC-cad_a", "PART-A.ipt");

    [Fact]
    public void Verified_when_the_selected_reference_transitions_to_the_target_as_the_expected_managed_document()
    {
        var scan = Scan(Edge(NewPath, TargetId));

        Assert.True(Verify(EligiblePlan(), scan).Verified);
    }

    [Fact]
    public void Not_verified_when_the_pre_mutation_evidence_was_not_captured()
    {
        var scan = Scan(Edge(NewPath, TargetId));

        var v = ReferenceRepairVerifier.Verify(EligiblePlan(), scan, preMutationTargetEdgeCount: -1, new[] { OldFp });

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("pre-mutation evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Not_verified_when_the_pre_mutation_whole_reference_set_fingerprint_was_not_captured()
    {
        var scan = Scan(Edge(NewPath, TargetId));

        var v = ReferenceRepairVerifier.Verify(EligiblePlan(), scan, preMutationTargetEdgeCount: 0, preMutationReferenceFingerprint: null);

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("whole-reference-set fingerprint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Not_verified_when_no_reference_resolves_to_the_target()
    {
        var scan = Scan(Edge(OldPath, new CadManifestIdentity("cad_a", "fv_v1", "DOC-cad_a", "PART-A.ipt")));
        Assert.False(Verify(EligiblePlan(), scan).Verified);
    }

    [Fact]
    public void Not_verified_when_the_old_selected_path_is_still_referenced()
    {
        var scan = Scan(Edge(NewPath, TargetId), Edge(OldPath, null));
        Assert.False(Verify(EligiblePlan(), scan).Verified);
    }

    [Fact]
    public void Not_verified_when_the_new_edge_is_not_managed()
    {
        var scan = Scan(Edge(NewPath, identity: null));
        var v = Verify(EligiblePlan(), scan);
        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("stable identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Not_verified_when_the_new_edge_is_managed_as_a_different_document_or_version()
    {
        var wrongDoc = Scan(Edge(NewPath, new CadManifestIdentity("cad_OTHER", "fv_v3", "DOC", "PART-A.ipt")));
        Assert.False(Verify(EligiblePlan(), wrongDoc).Verified);

        var wrongFv = Scan(Edge(NewPath, new CadManifestIdentity("cad_a", "fv_v2", "DOC-cad_a", "PART-A.ipt")));
        Assert.False(Verify(EligiblePlan(), wrongFv).Verified);
    }

    [Fact]
    public void Not_verified_when_the_edge_to_the_target_has_the_WRONG_relationship_kind()
    {
        var scan = Scan(Edge(NewPath, TargetId, kind: CadRelationshipKind.DrawingModel));

        var v = Verify(EligiblePlan(), scan);
        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("relationship kind", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Not_verified_when_the_edges_belong_to_a_DIFFERENT_parent_document()
    {
        var otherParent = P("Arch", "Job1", "OTHER-ROOT.iam");
        var scan = new CadReferenceScan(
            new CadReferenceRoot(otherParent, CadDocumentType.Iam, null),
            new[] { Edge(NewPath, TargetId, parent: otherParent) },
            new[] { new CadReferenceNode(otherParent, CadDocumentType.Iam, true) },
            Now);

        var v = Verify(EligiblePlan(), scan);
        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("no references", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Not_verified_when_the_repaired_edge_binding_is_UNVERIFIED()
    {
        var scan = Scan(Edge(NewPath,
            new CadManifestIdentity("cad_a", "fv_v3", "DOC-cad_a", "PART-A.ipt", IsVerified: false)));

        var v = Verify(EligiblePlan(), scan);
        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("Unverified", StringComparison.OrdinalIgnoreCase));
    }

    // ---- FINDING 3: prove the SELECTED edge transitioned ----------

    [Fact]
    public void A_pre_existing_target_edge_cannot_impersonate_the_repaired_selected_edge()
    {
        // BEFORE: selected stale edge A -> old FileVersion; another edge B is
        // ALREADY at the target FileVersion (preCount = 1, pre-fingerprint has
        // both A-at-old and B-at-target).
        // AFTER faulty mutation: A disappeared/changed incorrectly; B remains.
        // The fresh scan shows exactly ONE target edge (B). That must NOT be
        // accepted as proof A was repaired.
        var scanAfterFault = Scan(Edge(NewPath, TargetId)); // just B

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scanAfterFault, preMutationTargetEdgeCount: 1, new[] { OldFp, NewFp });

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("pre-existing target edge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_selected_edge_correctly_transitioning_alongside_a_pre_existing_target_edge_is_verified()
    {
        // BEFORE: A -> old, B -> target (preCount = 1, pre-fingerprint has both).
        // AFTER correct mutation: A -> target, B -> target (count rose to 2).
        var scan = Scan(
            Edge(NewPath, TargetId),  // A, now at target
            Edge(NewPath, TargetId)); // B, still at target

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 1, new[] { OldFp, NewFp });

        Assert.True(v.Verified);
    }

    [Fact]
    public void Selected_edge_missing_after_mutation_is_a_failure()
    {
        // preCount = 0, and after the mutation still nothing at the target.
        var scan = Scan(Edge(OtherTargetName, new CadManifestIdentity("cad_z", "fv_z", "DOC-z", "PART-B.ipt")));

        Assert.False(Verify(EligiblePlan(), scan).Verified);
    }

    [Fact]
    public void An_ambiguous_multiple_edge_transition_is_a_failure()
    {
        // preCount = 0 but TWO edges are now at the target - an ambiguous
        // transition (only one reference should have moved).
        var scan = Scan(Edge(NewPath, TargetId), Edge(NewPath, TargetId));

        var v = Verify(EligiblePlan(), scan);
        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CountResolvedTargetEdges_counts_only_full_identity_matches()
    {
        var scan = Scan(
            Edge(NewPath, TargetId),                                                   // counts
            Edge(NewPath, new CadManifestIdentity("cad_a", "fv_v2", "DOC-cad_a", "x")), // wrong fv - no
            Edge(NewPath, TargetId, kind: CadRelationshipKind.DrawingModel),            // wrong kind - no
            Edge(NewPath, identity: null),                                             // unmanaged - no
            Edge(OldPath, TargetId));                                                  // wrong path - no

        Assert.Equal(1, ReferenceRepairVerifier.CountResolvedTargetEdges(EligiblePlan(), scan));
    }

    // ---- ROUND 4 (Codex HIGH): whole-reference-set proof -------------

    private static readonly ManagedReferenceFingerprint UnrelatedFp =
        new(P("Arch", "Job1", "PART-C.ipt"), CadRelationshipKind.Component, "cad_c", "fv_c1", IsVerified: true);
    private static readonly ManagedReferenceFingerprint UnrelatedFpChanged =
        new(P("Arch", "Latest", "PART-C.ipt"), CadRelationshipKind.Component, "cad_c", "fv_c2", IsVerified: true);

    private static CadReference UnrelatedEdge(ManagedReferenceFingerprint fp) => new()
    {
        ParentAbsolutePath = Parent,
        InventorReportedName = Path.GetFileName(fp.ResolvedAbsolutePath),
        ResolvedAbsolutePath = fp.ResolvedAbsolutePath,
        ReferenceType = CadDocumentType.Ipt,
        RelationshipKind = fp.RelationshipKind,
        Resolution = CadReferenceResolution.Resolved,
        Scope = ReferenceWorkspaceScope.InsideWorkspace,
        ManifestIdentity = new CadManifestIdentity(
            fp.CadDocumentId, fp.FileVersionId, "DOC-" + fp.CadDocumentId, "PART-C.ipt", IsVerified: fp.IsVerified),
    };

    [Fact]
    public void Exact_old_to_new_transition_only_with_an_unrelated_reference_UNCHANGED_is_verified()
    {
        var scan = Scan(Edge(NewPath, TargetId), UnrelatedEdge(UnrelatedFp));

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 0, new[] { OldFp, UnrelatedFp });

        Assert.True(v.Verified);
    }

    [Fact]
    public void An_unrelated_reference_DELETED_alongside_the_repair_is_a_failure()
    {
        // Pre: old (A) + unrelated (C). Post: only the new target - C vanished.
        var scan = Scan(Edge(NewPath, TargetId));

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 0, new[] { OldFp, UnrelatedFp });

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("whole reference set changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unrelated_reference_ADDED_alongside_the_repair_is_a_failure()
    {
        // Pre: only old (A). Post: new target + an unrelated reference that
        // was never there before.
        var scan = Scan(Edge(NewPath, TargetId), UnrelatedEdge(UnrelatedFp));

        var v = Verify(EligiblePlan(), scan); // pre-fingerprint defaults to [OldFp] only

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("whole reference set changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unrelated_reference_PATH_CHANGE_alongside_the_repair_is_a_failure()
    {
        // The unrelated reference's cadDocumentId/FileVersionId are unchanged
        // but it now resolves to a different path.
        var moved = UnrelatedFp with { ResolvedAbsolutePath = P("Arch", "Latest", "PART-C.ipt") };
        var scan = Scan(Edge(NewPath, TargetId), UnrelatedEdge(moved));

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 0, new[] { OldFp, UnrelatedFp });

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("whole reference set changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unrelated_reference_cadDocumentId_CHANGE_alongside_the_repair_is_a_failure()
    {
        var otherDoc = UnrelatedFp with { CadDocumentId = "cad_DIFFERENT" };
        var scan = Scan(Edge(NewPath, TargetId), UnrelatedEdge(otherDoc));

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 0, new[] { OldFp, UnrelatedFp });

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("whole reference set changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unrelated_reference_FileVersion_CHANGE_alongside_the_repair_is_a_failure()
    {
        var scan = Scan(Edge(NewPath, TargetId), UnrelatedEdge(UnrelatedFpChanged));

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 0, new[] { OldFp, UnrelatedFp });

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("whole reference set changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_authorized_old_reference_still_remaining_ALONGSIDE_the_new_one_is_a_failure()
    {
        // A duplicated somehow: both old and new paths now resolve as managed
        // edges (the earlier "old path still referenced" check already fails
        // this - this test proves the whole-set proof independently agrees).
        var scan = Scan(
            Edge(NewPath, TargetId),
            Edge(OldPath, new CadManifestIdentity("cad_a", "fv_v1", "DOC-cad_a", "PART-A.ipt")));

        var v = Verify(EligiblePlan(), scan);

        Assert.False(v.Verified);
    }

    [Fact]
    public void Target_missing_with_an_unrelated_reference_otherwise_unchanged_is_still_a_failure()
    {
        var scan = Scan(UnrelatedEdge(UnrelatedFp)); // no edge at the target at all

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 0, new[] { OldFp, UnrelatedFp });

        Assert.False(v.Verified);
    }

    // ---- ROUND 5 (Codex HIGH): UNVERIFIED managed references must ALSO be
    //      fingerprinted - ReferenceHealthDiagnoser classifies a reference
    //      with an exact manifest-matched identity as "managed" whether or
    //      not that binding happens to be Verified right now. -------------

    private static readonly ManagedReferenceFingerprint UnverifiedFp =
        new(P("Arch", "Job1", "PART-D.ipt"), CadRelationshipKind.Component, "cad_d", "fv_d1", IsVerified: false);

    private static CadReference UnverifiedEdge(ManagedReferenceFingerprint fp) => new()
    {
        ParentAbsolutePath = Parent,
        InventorReportedName = Path.GetFileName(fp.ResolvedAbsolutePath),
        ResolvedAbsolutePath = fp.ResolvedAbsolutePath,
        ReferenceType = CadDocumentType.Ipt,
        RelationshipKind = fp.RelationshipKind,
        Resolution = CadReferenceResolution.Resolved,
        Scope = ReferenceWorkspaceScope.InsideWorkspace,
        ManifestIdentity = new CadManifestIdentity(
            fp.CadDocumentId, fp.FileVersionId, "DOC-" + fp.CadDocumentId, "PART-D.ipt", IsVerified: fp.IsVerified),
    };

    [Fact]
    public void CaptureManagedReferenceFingerprint_includes_an_UNVERIFIED_managed_reference()
    {
        var scan = Scan(Edge(NewPath, TargetId), UnverifiedEdge(UnverifiedFp));

        var captured = ReferenceRepairVerifier.CaptureManagedReferenceFingerprint(EligiblePlan(), scan);

        Assert.Contains(captured, f => f.CadDocumentId == "cad_d" && !f.IsVerified);
    }

    [Fact]
    public void An_unrelated_UNVERIFIED_managed_reference_UNCHANGED_is_verified()
    {
        var scan = Scan(Edge(NewPath, TargetId), UnverifiedEdge(UnverifiedFp));

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 0, new[] { OldFp, UnverifiedFp });

        Assert.True(v.Verified);
    }

    [Fact]
    public void An_unrelated_UNVERIFIED_reference_PATH_CHANGE_alongside_the_repair_is_a_failure()
    {
        var moved = UnverifiedFp with { ResolvedAbsolutePath = P("Arch", "Latest", "PART-D.ipt") };
        var scan = Scan(Edge(NewPath, TargetId), UnverifiedEdge(moved));

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 0, new[] { OldFp, UnverifiedFp });

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("whole reference set changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unrelated_UNVERIFIED_reference_cadDocumentId_CHANGE_alongside_the_repair_is_a_failure()
    {
        var otherDoc = UnverifiedFp with { CadDocumentId = "cad_DIFFERENT" };
        var scan = Scan(Edge(NewPath, TargetId), UnverifiedEdge(otherDoc));

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 0, new[] { OldFp, UnverifiedFp });

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("whole reference set changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unrelated_UNVERIFIED_reference_FileVersionId_CHANGE_alongside_the_repair_is_a_failure()
    {
        var otherFv = UnverifiedFp with { FileVersionId = "fv_d2" };
        var scan = Scan(Edge(NewPath, TargetId), UnverifiedEdge(otherFv));

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 0, new[] { OldFp, UnverifiedFp });

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("whole reference set changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unrelated_reference_flipping_from_VERIFIED_to_UNVERIFIED_alongside_the_repair_is_a_failure()
    {
        // Same identity/path, but its verification state changed - this must
        // NOT be silently ignored (the fingerprint now tracks IsVerified).
        var stillSamePlace = UnrelatedFp with { }; // verified, in the PRE set
        var nowUnverified = new ManagedReferenceFingerprint(
            UnrelatedFp.ResolvedAbsolutePath, UnrelatedFp.RelationshipKind,
            UnrelatedFp.CadDocumentId, UnrelatedFp.FileVersionId, IsVerified: false);
        var scan = Scan(Edge(NewPath, TargetId), UnverifiedEdge(nowUnverified));

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 0, new[] { OldFp, stillSamePlace });

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("whole reference set changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unrelated_UNVERIFIED_reference_REMOVED_alongside_the_repair_is_a_failure()
    {
        var scan = Scan(Edge(NewPath, TargetId)); // the unverified D reference vanished

        var v = ReferenceRepairVerifier.Verify(
            EligiblePlan(), scan, preMutationTargetEdgeCount: 0, new[] { OldFp, UnverifiedFp });

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("whole reference set changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unrelated_UNVERIFIED_reference_ADDED_alongside_the_repair_is_a_failure()
    {
        // Pre: only old (A) - no D. Post: new target + a previously-absent
        // unverified managed reference.
        var scan = Scan(Edge(NewPath, TargetId), UnverifiedEdge(UnverifiedFp));

        var v = Verify(EligiblePlan(), scan); // pre-fingerprint defaults to [OldFp] only

        Assert.False(v.Verified);
        Assert.Contains(v.Reasons, r => r.Contains("whole reference set changed", StringComparison.OrdinalIgnoreCase));
    }
}
