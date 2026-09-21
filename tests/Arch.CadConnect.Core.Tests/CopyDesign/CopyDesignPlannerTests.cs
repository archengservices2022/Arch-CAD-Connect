using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.CopyDesign.CopyDesignFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign;

public class CopyDesignPlannerTests
{
    private static readonly string RootPath = P("Design", "10073-AS210.iam");
    private static readonly string DestRoot = P("Dest");
    private static readonly IDestinationNameRule TokenRule = new TokenReplaceNameRule("10073", "10137");

    private static CopyDesignPlan Plan(
        CadReferenceScan scan,
        IDestinationNameRule? rule = null,
        string? destRoot = null,
        IComponentClassificationSource? classification = null,
        IDrawingAssociationSource? drawings = null,
        Func<string, bool>? destinationExists = null,
        IReadOnlyDictionary<string, CopyDesignAction>? explicitDecisions = null,
        bool acknowledgeModelFilesOnly = false) =>
        CopyDesignPlanner.Plan(scan, rule ?? TokenRule, destRoot ?? DestRoot, classification,
            // ROUND 7: the DEFAULT drawing source for this test file is
            // "complete but empty" (Found, zero drawings, for ANY query) -
            // NOT the production NoDrawingAssociationSource default. Round 7
            // correctly makes an UNAVAILABLE eligible-model lookup mark the
            // WHOLE plan non-executable (drawingAssociationComplete), and
            // EVERY scan root is itself an eligible Iam/Ipt query target -
            // so a test that has nothing to do with drawing-association
            // availability/completeness must not be spuriously broken by
            // that root query going unanswered. Tests that specifically
            // exercise "no source"/"incomplete authority" pass their own
            // explicit `drawings:` value instead.
            drawings ?? AlwaysFoundEmptyDrawingSource.Instance, destinationExists, explicitDecisions,
            acknowledgeModelFilesOnly);

    /// <summary>A "complete but empty" drawing authority - reports
    ///  <see cref="DrawingAssociationOutcome.Found"/> with zero drawings for
    ///  ANY cadDocumentId. This is this file's DEFAULT (see <see cref="Plan"/>)
    ///  so ordinary model/identity/naming tests are never spuriously marked
    ///  non-executable merely because they never answered for the (always
    ///  eligible) root's own drawing-association query.</summary>
    private sealed class AlwaysFoundEmptyDrawingSource : IDrawingAssociationSource
    {
        public static readonly AlwaysFoundEmptyDrawingSource Instance = new();

        public DrawingAssociationResult GetAssociatedDrawings(string cadDocumentId) =>
            new(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>());
    }

    // ---- 1. simple IAM + IPT plan -------------------------------------

    [Fact]
    public void A_simple_IAM_plus_IPT_plan_produces_a_root_node_and_one_child_COPY_node()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partPath, "cad_p1", "fv_p1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification);

        Assert.Equal(2, plan.Nodes.Count);
        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal(CopyDesignAction.Copy, root.ProposedAction);
        var part = Assert.Single(plan.Nodes, n => !n.IsRoot);
        Assert.Equal("cad_p1", part.CadDocumentId);
        Assert.Equal(CadDocumentType.Ipt, part.DocumentType);
        Assert.Equal(CopyDesignAction.Copy, part.ProposedAction);
        Assert.True(plan.IsExecutable);
    }

    // ---- 2. nested IAM --------------------------------------------------

    [Fact]
    public void A_nested_IAM_is_discovered_and_planned_as_its_own_node()
    {
        var subIamPath = P("Design", "10073-SUB100.iam");
        var nestedPartPath = P("Design", "10073-P002.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, subIamPath, "cad_sub", "fv_sub1", CadDocumentType.Iam),
            Managed(subIamPath, nestedPartPath, "cad_p2", "fv_p2"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_sub"] = ComponentClassification.ProjectSpecific,
            ["cad_p2"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification);

        Assert.Equal(3, plan.Nodes.Count);
        var sub = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_sub");
        Assert.Equal(CadDocumentType.Iam, sub.DocumentType);
        Assert.Equal(CadRelationshipKind.Component, sub.RelationshipToParent);
        var nestedPart = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_p2");
        Assert.Equal(CopyDesignAction.Copy, nestedPart.ProposedAction);
        Assert.True(plan.IsExecutable);
    }

    // ---- 3. COPY mapping -------------------------------------------------

    [Fact]
    public void A_COPY_node_maps_its_destination_through_the_naming_rule_into_the_destination_root()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification);

        var part = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_p1");
        Assert.Equal("10137-P001.ipt", part.ProposedDestinationFileName);
        Assert.Equal(Path.Combine(DestRoot, "10137-P001.ipt"), part.ProposedDestinationAbsolutePath);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal("10137-AS210.iam", root.ProposedDestinationFileName);
    }

    // ---- 4. REUSE identity preservation ---------------------------------

    private sealed class StubClassification(IReadOnlyDictionary<string, ComponentClassification> map) : IComponentClassificationSource
    {
        public ComponentClassification Classify(string cadDocumentId) =>
            map.TryGetValue(cadDocumentId, out var c) ? c : ComponentClassification.Unknown;
    }

    [Fact]
    public void A_component_classified_as_library_or_shared_is_REUSED_and_keeps_its_own_identity_with_no_destination()
    {
        var partPath = P("Design", "STD-BOLT-M6.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_bolt", "fv_bolt1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_bolt"] = ComponentClassification.LibraryOrShared,
        });

        var plan = Plan(scan, classification: classification);

        var bolt = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_bolt");
        Assert.Equal(CopyDesignAction.Reuse, bolt.ProposedAction);
        Assert.Equal("cad_bolt", bolt.CadDocumentId); // identity preserved
        Assert.Equal("fv_bolt1", bolt.CurrentFileVersionId);
        Assert.Null(bolt.ProposedDestinationFileName);
        Assert.Null(bolt.ProposedDestinationAbsolutePath);
        Assert.True(plan.IsExecutable);
    }

    // ---- 5. EXCLUDE representation but never automatic exclusion -------

    [Fact]
    public void EXCLUDE_is_representable_in_the_data_model()
    {
        // The type supports it - a future manual-override UI could construct
        // this node shape.
        var node = new CopyDesignNode(
            "cad_x", "fv_x", CadDocumentType.Ipt, P("Design", "X.ipt"), "X.ipt",
            CadRelationshipKind.Component, IsManaged: true, IsVerified: true, IsResolved: true,
            CopyDesignAction.Exclude, null, null, new[] { "manually excluded" });

        Assert.Equal(CopyDesignAction.Exclude, node.ProposedAction);
    }

    [Theory]
    [InlineData(true)]  // managed + verified
    [InlineData(false)] // unmanaged
    public void The_planner_NEVER_automatically_selects_EXCLUDE(bool managed)
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            managed ? Managed(RootPath, partPath, "cad_p1", "fv_p1") : Unmanaged(RootPath, partPath),
        });

        var plan = Plan(scan);

        Assert.DoesNotContain(plan.Nodes, n => n.ProposedAction == CopyDesignAction.Exclude);
    }

    // ---- 6/7. naming: exact token rename + files without the token -----

    [Fact]
    public void A_file_name_containing_the_source_token_is_renamed_exactly()
    {
        Assert.Equal("10137-AS210.iam", TokenRule.Rename("10073-AS210.iam"));
        Assert.Equal("10137-P001.ipt", TokenRule.Rename("10073-P001.ipt"));
    }

    [Fact]
    public void A_file_name_without_the_source_token_is_left_completely_unchanged()
    {
        var partPath = P("Design", "STD-BOLT-M6.ipt"); // no "10073" token
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_bolt", "fv_bolt1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_bolt"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification);

        var bolt = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_bolt");
        Assert.Equal(CopyDesignAction.Copy, bolt.ProposedAction); // COPY (classified project-specific)
        Assert.Equal("STD-BOLT-M6.ipt", bolt.ProposedDestinationFileName); // unchanged name
    }

    // ---- 8. duplicate destination collision -----------------------------

    private sealed class FixedNameRule(string fixedName) : IDestinationNameRule
    {
        public string? Rename(string sourceFileName) => fixedName;
    }

    [Fact]
    public void Two_COPY_nodes_mapping_to_the_same_destination_make_the_plan_NOT_executable()
    {
        var partA = P("Design", "A.ipt");
        var partB = P("Design", "B.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partA, "cad_a", "fv_a1"),
            Managed(RootPath, partB, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, rule: new FixedNameRule("SAME.ipt"), classification: classification);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Duplicate proposed destination", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 9. unsafe/path traversal destination rejection ------------------

    [Fact]
    public void A_rename_that_would_escape_the_destination_root_is_rejected_and_fails_closed()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        // Substituting "10073" for a token containing a path separator and a
        // dot-segment produces an unsafe "file name" - SafeWorkspacePath must
        // catch this exactly as it does for the P4B materializer.
        var unsafeRule = new TokenReplaceNameRule("10073", "../evil");

        var plan = Plan(scan, rule: unsafeRule);

        var part = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_p1");
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction);
        Assert.Null(part.ProposedDestinationAbsolutePath);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_destination_collision_with_an_existing_local_file_is_rejected()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });

        var plan = Plan(scan, destinationExists: _ => true);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("already exists", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_destination_identical_to_the_source_path_is_rejected()
    {
        // Destination root == the source's own containing folder AND the
        // rule doesn't change the name => source == destination.
        var partPath = P("Design", "STD-BOLT-M6.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_bolt", "fv_bolt1") });

        var plan = Plan(scan, destRoot: P("Design"));

        var bolt = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_bolt");
        Assert.Equal(CopyDesignAction.NeedsDecision, bolt.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    // ---- 10. unresolved reference fail-closed ----------------------------

    [Fact]
    public void An_unresolved_reference_is_represented_as_NeedsDecision_and_fails_the_plan_closed()
    {
        var scan = Scan(Root(RootPath), new[] { Unresolved(RootPath, "MOTOR.ipt") });

        var plan = Plan(scan);

        var motor = Assert.Single(plan.Nodes, n => !n.IsRoot);
        Assert.False(motor.IsResolved);
        Assert.Equal(CopyDesignAction.NeedsDecision, motor.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    // ---- 11. unverified managed identity fail-closed ---------------------

    [Fact]
    public void An_unverified_managed_identity_is_NeedsDecision_and_fails_the_plan_closed()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1", verified: false) });

        var plan = Plan(scan);

        var part = Assert.Single(plan.Nodes, n => !n.IsRoot);
        Assert.False(part.IsVerified);
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void An_unmanaged_reference_with_no_stable_identity_is_NeedsDecision_and_fails_the_plan_closed()
    {
        var partPath = P("Design", "SCRATCH.ipt");
        var scan = Scan(Root(RootPath), new[] { Unmanaged(RootPath, partPath) });

        var plan = Plan(scan);

        var part = Assert.Single(plan.Nodes, n => !n.IsRoot);
        Assert.Null(part.CadDocumentId);
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    // ---- 11b. Unknown classification fail-closed (ARCHITECTURE CORRECTION) -

    [Fact]
    public void A_verified_managed_child_with_UNKNOWN_classification_is_NeedsDecision_not_an_automatic_COPY()
    {
        // ARCHITECTURE CORRECTION: an otherwise-perfect, verified, managed
        // child MUST NOT be auto-assigned COPY just because no library/shared
        // classification signal exists. It must surface NeedsDecision and the
        // whole plan must fail closed, exactly like the other identity gaps
        // above.
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });

        // No classification source supplied at all - Plan() defaults to
        // UnknownComponentClassificationSource, so every managed child is
        // classified Unknown.
        var plan = Plan(scan);

        var part = Assert.Single(plan.Nodes, n => !n.IsRoot);
        Assert.True(part.IsManaged);
        Assert.True(part.IsVerified);
        Assert.True(part.HasStableIdentity);
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction);
        Assert.Null(part.ProposedDestinationFileName);
        Assert.Null(part.ProposedDestinationAbsolutePath);
        Assert.False(plan.IsExecutable);

        // The non-authoritative suggestion may appear as plain text, but it
        // must never become the effective action.
        Assert.Contains(part.Reasons, r => r.Contains("Suggested: COPY", StringComparison.Ordinal));
    }

    [Fact]
    public void An_explicit_UNKNOWN_classification_from_a_real_source_is_also_NeedsDecision()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.Unknown,
        });

        var plan = Plan(scan, classification: classification);

        var part = Assert.Single(plan.Nodes, n => !n.IsRoot);
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    // ---- 12. incomplete scan fail-closed ----------------------------------

    [Fact]
    public void An_incomplete_scan_makes_the_plan_NOT_executable_even_if_every_discovered_node_is_fine()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(
            Root(RootPath),
            new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") },
            nodes: new[]
            {
                new CadReferenceNode(RootPath, CadDocumentType.Iam, true),
                new CadReferenceNode(partPath, CadDocumentType.Ipt, false), // NOT enumerated
            });

        var plan = Plan(scan);

        Assert.False(plan.ScanWasComplete);
        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("did not fully enumerate", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 13. deterministic ordering ----------------------------------------

    [Fact]
    public void Planning_the_SAME_scan_twice_produces_IDENTICAL_node_order()
    {
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, P("Design", "C.ipt"), "cad_c", "fv_c1"),
            Managed(RootPath, P("Design", "A.ipt"), "cad_a", "fv_a1"),
            Managed(RootPath, P("Design", "B.ipt"), "cad_b", "fv_b1"),
        });

        var plan1 = Plan(scan);
        var plan2 = Plan(scan);

        Assert.Equal(plan1.Nodes.Select(n => n.CadDocumentId), plan2.Nodes.Select(n => n.CadDocumentId));
        // BLOCKER 5 (P6A Round 4): the FINAL node order is a deterministic
        // total ordinal sort - NOT discovery/observation order - so it is
        // identical regardless of the order references were scanned in.
        Assert.Equal(new[] { "cad_a", "cad_b", "cad_c", "cad_root" }, plan1.Nodes.Select(n => n.CadDocumentId));
    }

    // ---- 14. shared dependency represented once by stable identity -------

    [Fact]
    public void A_dependency_reachable_from_two_parents_is_represented_ONCE_with_two_edges()
    {
        var subIamPath = P("Design", "10073-SUB100.iam");
        var sharedPartPath = P("Design", "SHARED.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, subIamPath, "cad_sub", "fv_sub1", CadDocumentType.Iam),
            Managed(RootPath, sharedPartPath, "cad_shared", "fv_s1"),
            Managed(subIamPath, sharedPartPath, "cad_shared", "fv_s1"),
        });

        var plan = Plan(scan);

        Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_shared");
        Assert.Equal(2, plan.Edges.Count(e => e.ChildAbsolutePath.Equals(sharedPartPath, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- 15. same filename but different cadDocumentId remains distinct --

    [Fact]
    public void Two_references_with_the_SAME_file_name_but_DIFFERENT_cadDocumentIds_remain_distinct_nodes()
    {
        var pathA = P("Design", "JobA", "PART-X.ipt");
        var pathB = P("Design", "JobB", "PART-X.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, pathA, "cad_a", "fv_a1"),
            Managed(RootPath, pathB, "cad_b", "fv_b1"),
        });

        var plan = Plan(scan);

        Assert.Equal(3, plan.Nodes.Count); // root + 2 distinct parts
        Assert.Contains(plan.Nodes, n => n.CadDocumentId == "cad_a");
        Assert.Contains(plan.Nodes, n => n.CadDocumentId == "cad_b");
    }

    // ---- 16/17. drawing association only when proven ----------------------

    private sealed class StubDrawingSource(
        IReadOnlyDictionary<string, DrawingAssociationResult> byId) : IDrawingAssociationSource
    {
        // ROUND 7: an id NOT explicitly listed falls back to "Found, zero
        // drawings" (complete authority, just nothing to report) - NOT
        // NotAvailable. This preserves every existing test's original intent
        // (most only bother listing the specific model(s) their scenario
        // cares about, never every eligible id including the root) while
        // still letting a test explicitly map a SPECIFIC id to
        // DrawingAssociationResult.NotAvailable when it deliberately wants to
        // exercise incomplete-authority behavior for that id.
        private static readonly DrawingAssociationResult FoundEmpty =
            new(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>());

        public DrawingAssociationResult GetAssociatedDrawings(string cadDocumentId) =>
            byId.TryGetValue(cadDocumentId, out var r) ? r : FoundEmpty;
    }

    [Fact]
    public void No_drawing_source_means_no_drawing_nodes_and_an_explicit_limitation_warning()
    {
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        // Explicitly the PRODUCTION default (this test file's own default is
        // "complete but empty" - see AlwaysFoundEmptyDrawingSource/Plan()).
        var plan = Plan(scan, drawings: NoDrawingAssociationSource.Instance);

        Assert.DoesNotContain(plan.Nodes, n => n.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg);
        Assert.False(plan.DrawingAssociationAvailable);
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing association could not be established", StringComparison.OrdinalIgnoreCase));
        // ROUND 7: an eligible model (the root itself) whose drawing-
        // association lookup never completed authoritatively means the
        // COMPLETE drawing-owner picture was never proven - the plan must
        // not be executable.
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_PROVEN_drawing_association_adds_a_drawing_node_mirroring_its_models_action()
    {
        var drawingPath = P("Design", "10073-AS210.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(
                DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        Assert.True(plan.DrawingAssociationAvailable);
        var drawing = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CadDocumentType.Idw, drawing.DocumentType);
        // BLOCKER 1 (P6A Round 4): DrawingModel means DRAWING -> MODEL - the
        // drawing has NO incoming edge under the corrected direction, so it
        // is never assigned DrawingModel as its OWN incoming relationship.
        Assert.Null(drawing.RelationshipToParent);
        Assert.Contains(plan.Edges, e => e.RelationshipKind == CadRelationshipKind.DrawingModel
            && e.ParentAbsolutePath.Equals(drawingPath, StringComparison.OrdinalIgnoreCase)
            && e.ChildAbsolutePath.Equals(RootPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(CopyDesignAction.Copy, drawing.ProposedAction); // mirrors the root's own COPY
    }

    [Fact]
    public void The_planner_NEVER_infers_a_drawing_association_from_a_similar_file_name()
    {
        // A drawing with a name that LOOKS associated sits right next to the
        // model on disk, but NO drawing source proves it - it must never
        // appear in the plan.
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan); // no drawing source at all - default NotAvailable

        Assert.DoesNotContain(plan.Nodes, n => n.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg);
    }

    // ---- 18. no mutation side effects ---------------------------------------

    [Fact]
    public void Planning_never_touches_the_filesystem_and_is_side_effect_free()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var existsCalls = 0;

        var plan1 = Plan(scan, destinationExists: p => { existsCalls++; return false; });
        var plan2 = Plan(scan, destinationExists: p => { existsCalls++; return false; });

        // Deterministic / idempotent - no hidden state accumulated between calls.
        Assert.Equal(plan1.Nodes.Count, plan2.Nodes.Count);
        Assert.Equal(plan1.IsExecutable, plan2.IsExecutable);
        Assert.True(existsCalls > 0); // the ONLY "filesystem" touch is the injected read-only predicate
    }

    // ==================================================================
    // ROUND 2 - CODEX SAFETY FIXES
    // ==================================================================

    // ---- 19. BLOCKER 1: root verification state must be preserved --------

    [Fact]
    public void A_verified_managed_root_with_complete_identity_is_COPY_and_executable()
    {
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal(CopyDesignAction.Copy, root.ProposedAction);
        Assert.True(plan.IsExecutable);
    }

    [Fact]
    public void An_UNVERIFIED_root_is_NeedsDecision_and_the_plan_can_NEVER_be_executable()
    {
        // The manifest entry the root's identity came from is Unverified -
        // a non-null Identity is NOT proof of a verified binding.
        var scan = Scan(Root(RootPath, verified: false), Array.Empty<CadReference>());

        var plan = Plan(scan);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.NotNull(root.CadDocumentId); // diagnostics still retain the identity
        Assert.False(root.IsVerified);
        Assert.Equal(CopyDesignAction.NeedsDecision, root.ProposedAction);
        Assert.Null(root.ProposedDestinationFileName);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void An_UNMANAGED_root_is_NeedsDecision_and_the_plan_can_NEVER_be_executable()
    {
        var scan = Scan(Root(RootPath, cadDocumentId: null), Array.Empty<CadReference>());

        var plan = Plan(scan);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Null(root.CadDocumentId);
        Assert.Equal(CopyDesignAction.NeedsDecision, root.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    // ---- 20. BLOCKER 2: stable identity requires BOTH ids -----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_root_with_an_incomplete_FileVersionId_is_NeedsDecision_and_the_plan_is_NOT_executable(string? fv)
    {
        var scan = Scan(Root(RootPath, fv: fv), Array.Empty<CadReference>());

        var plan = Plan(scan);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal(CopyDesignAction.NeedsDecision, root.ProposedAction);
        Assert.False(root.HasStableIdentity);
        Assert.False(plan.IsExecutable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_managed_verified_child_with_an_incomplete_FileVersionId_is_NeedsDecision_and_the_plan_is_NOT_executable(string? fv)
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", fv) });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification);

        var part = Assert.Single(plan.Nodes, n => !n.IsRoot);
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction);
        Assert.False(part.HasStableIdentity);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void LibraryOrShared_classification_does_NOT_produce_REUSE_when_FileVersionId_is_incomplete()
    {
        var partPath = P("Design", "STD-BOLT-M6.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_bolt", fv: null) });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_bolt"] = ComponentClassification.LibraryOrShared,
        });

        var plan = Plan(scan, classification: classification);

        var bolt = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_bolt");
        Assert.Equal(CopyDesignAction.NeedsDecision, bolt.ProposedAction); // NOT Reuse
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void ProjectSpecific_classification_does_NOT_produce_COPY_when_FileVersionId_is_incomplete()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", fv: null) });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification);

        var part = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_p1");
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction); // NOT Copy
        Assert.Null(part.ProposedDestinationFileName);
        Assert.False(plan.IsExecutable);
    }

    // ---- 21. BLOCKER 3: repeated cadDocumentId reconciliation -------------

    [Fact]
    public void Identical_observations_of_the_SAME_cadDocumentId_reconcile_into_one_node_with_all_edges_retained()
    {
        var subIamPath = P("Design", "10073-SUB100.iam");
        var sharedPartPath = P("Design", "SHARED.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, subIamPath, "cad_sub", "fv_sub1", CadDocumentType.Iam),
            Managed(RootPath, sharedPartPath, "cad_shared", "fv_s1"),
            Managed(subIamPath, sharedPartPath, "cad_shared", "fv_s1"), // SAME id/fv/type/path/verified
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_sub"] = ComponentClassification.ProjectSpecific,
            ["cad_shared"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification);

        var shared = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_shared");
        Assert.Equal(CopyDesignAction.Copy, shared.ProposedAction); // reconciled cleanly - NOT NeedsDecision
        Assert.Equal(2, plan.Edges.Count(e => e.ChildAbsolutePath.Equals(sharedPartPath, StringComparison.OrdinalIgnoreCase)));
        Assert.True(plan.IsExecutable);
    }

    public enum ConflictKind { FileVersionId, DocumentType, VerificationState, ResolvedPath, RootVsChild }

    /// <summary>Builds a scan with exactly ONE conflicting pair of observations
    ///  for <paramref name="kind"/>, in either order (permutation independence).</summary>
    private static CadReferenceScan BuildConflictScan(ConflictKind kind, bool reversed)
    {
        var subIamPath = P("Design", "10073-SUB100.iam");
        var pathA = P("Design", "SHARED.ipt");
        var pathB = P("Design", "SHARED-ELSEWHERE.ipt");

        CadReference obs1, obs2;
        switch (kind)
        {
            case ConflictKind.FileVersionId:
                obs1 = Managed(RootPath, pathA, "cad_shared", "fv_v1");
                obs2 = Managed(subIamPath, pathA, "cad_shared", "fv_v2");
                break;
            case ConflictKind.DocumentType:
                obs1 = Managed(RootPath, pathA, "cad_shared", "fv_1", CadDocumentType.Ipt);
                obs2 = Managed(subIamPath, pathA, "cad_shared", "fv_1", CadDocumentType.Iam);
                break;
            case ConflictKind.VerificationState:
                obs1 = Managed(RootPath, pathA, "cad_shared", "fv_1", verified: true);
                obs2 = Managed(subIamPath, pathA, "cad_shared", "fv_1", verified: false);
                break;
            case ConflictKind.ResolvedPath:
                obs1 = Managed(RootPath, pathA, "cad_shared", "fv_1");
                obs2 = Managed(subIamPath, pathB, "cad_shared", "fv_1");
                break;
            case ConflictKind.RootVsChild:
                // The "second observation" here is a child claiming the
                // ROOT's own cadDocumentId with an incompatible FileVersionId.
                obs1 = Managed(RootPath, pathA, "cad_root", "fv_root1"); // unused placeholder, overwritten below
                obs2 = Managed(RootPath, pathB, "cad_root", "fv_root2");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (kind == ConflictKind.RootVsChild)
        {
            // Root itself carries "cad_root"/"fv_root1"; the single reference
            // below claims the SAME cadDocumentId with a different FileVersionId.
            return Scan(Root(RootPath, cadDocumentId: "cad_root", fv: "fv_root1"), new[] { obs2 });
        }

        var sub = Managed(RootPath, subIamPath, "cad_sub", "fv_sub1", CadDocumentType.Iam);
        var refs = reversed ? new[] { sub, obs2, obs1 } : new[] { sub, obs1, obs2 };
        return Scan(Root(RootPath), refs);
    }

    [Theory]
    [InlineData(ConflictKind.FileVersionId, false)]
    [InlineData(ConflictKind.FileVersionId, true)]
    [InlineData(ConflictKind.DocumentType, false)]
    [InlineData(ConflictKind.DocumentType, true)]
    [InlineData(ConflictKind.VerificationState, false)]
    [InlineData(ConflictKind.VerificationState, true)]
    [InlineData(ConflictKind.ResolvedPath, false)]
    [InlineData(ConflictKind.ResolvedPath, true)]
    [InlineData(ConflictKind.RootVsChild, false)]
    [InlineData(ConflictKind.RootVsChild, true)]
    public void Conflicting_observations_of_the_SAME_cadDocumentId_are_NeedsDecision_regardless_of_order(
        ConflictKind kind, bool reversed)
    {
        var scan = BuildConflictScan(kind, reversed);

        var plan = Plan(scan);

        // RootVsChild merges into the ROOT's own single "id:cad_root" node -
        // there is no separate child node at all. Every other case produces
        // exactly one "cad_shared" node alongside the (unrelated, non-
        // conflicting) root and "cad_sub" nodes.
        var conflicted = kind == ConflictKind.RootVsChild
            ? Assert.Single(plan.Nodes)
            : Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_shared");
        Assert.Equal(CopyDesignAction.NeedsDecision, conflicted.ProposedAction);
        Assert.Contains(conflicted.Reasons, r => r.Contains("disagree on safety-relevant identity", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_reconciliation_conflict_still_preserves_every_observed_edge()
    {
        var subIamPath = P("Design", "10073-SUB100.iam");
        var pathA = P("Design", "SHARED.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, subIamPath, "cad_sub", "fv_sub1", CadDocumentType.Iam),
            Managed(RootPath, pathA, "cad_shared", "fv_v1"),
            Managed(subIamPath, pathA, "cad_shared", "fv_v2"), // conflicting FileVersionId
        });

        var plan = Plan(scan);

        // Still ONE node (not two silently-separate ones) but BOTH edges into
        // it remain visible for graph inspection - the conflict is not
        // resolved by dropping evidence.
        Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_shared");
        Assert.Equal(2, plan.Edges.Count(e => e.ChildAbsolutePath.Equals(pathA, StringComparison.OrdinalIgnoreCase)));
    }

    // ==================================================================
    // ROUND 3 - CODEX SAFETY FIXES
    // ==================================================================

    // ---- 22. BLOCKER 1: inverse path -> identity reconciliation ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_same_normalized_path_claimed_by_two_different_cadDocumentIds_fails_BOTH_nodes_closed(bool reversed)
    {
        var sharedPath = P("Design", "SHARED.ipt");
        var obsA = Managed(RootPath, sharedPath, "cad_a", "fv_a1");
        var obsB = Managed(RootPath, sharedPath, "cad_b", "fv_b1"); // SAME path, DIFFERENT cadDocumentId
        var refs = reversed ? new[] { obsB, obsA } : new[] { obsA, obsB };

        var plan = Plan(Scan(Root(RootPath), refs));

        Assert.Equal(2, plan.Nodes.Count(n => !n.IsRoot));
        var a = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_a");
        var b = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_b");
        Assert.Equal(CopyDesignAction.NeedsDecision, a.ProposedAction);
        Assert.Equal(CopyDesignAction.NeedsDecision, b.ProposedAction);
        Assert.Contains(a.Reasons, r => r.Contains("claimed by more than one identity", StringComparison.Ordinal));
        Assert.Contains(b.Reasons, r => r.Contains("claimed by more than one identity", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);

        // Edge mapping stays correct: BOTH edges into the shared path are
        // still present and resolved by KEY, never dropped or merged.
        Assert.Equal(2, plan.Edges.Count(e => e.ChildAbsolutePath.Equals(sharedPath, StringComparison.OrdinalIgnoreCase)));
        Assert.All(plan.Edges.Where(e => e.ChildAbsolutePath.Equals(sharedPath, StringComparison.OrdinalIgnoreCase)),
            e => Assert.Equal(CopyDesignEdgeDisposition.UnresolvedOrUnsafe, e.Disposition));
    }

    [Fact]
    public void A_ProjectSpecific_and_a_LibraryOrShared_alias_on_the_SAME_path_still_fails_closed()
    {
        var sharedPath = P("Design", "SHARED.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, sharedPath, "cad_a", "fv_a1"),
            Managed(RootPath, sharedPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.LibraryOrShared,
        });

        var plan = Plan(scan, classification: classification);

        // Classification never overrides a path-identity conflict.
        var a = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_a");
        var b = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_b");
        Assert.Equal(CopyDesignAction.NeedsDecision, a.ProposedAction);
        Assert.Equal(CopyDesignAction.NeedsDecision, b.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_managed_identity_and_an_unmanaged_alias_on_the_SAME_path_fails_closed()
    {
        var sharedPath = P("Design", "SHARED.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, sharedPath, "cad_a", "fv_a1"),
            Unmanaged(RootPath, sharedPath),
        });

        var plan = Plan(scan);

        Assert.Equal(2, plan.Nodes.Count(n => !n.IsRoot));
        var managed = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_a");
        var unmanaged = Assert.Single(plan.Nodes, n => n.CadDocumentId == null);
        Assert.Equal(CopyDesignAction.NeedsDecision, managed.ProposedAction);
        Assert.Equal(CopyDesignAction.NeedsDecision, unmanaged.ProposedAction);
        Assert.Contains(managed.Reasons, r => r.Contains("claimed by more than one identity", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void Compatible_same_path_same_identity_from_multiple_parents_still_succeeds()
    {
        var subIamPath = P("Design", "10073-SUB100.iam");
        var sharedPath = P("Design", "SHARED.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, subIamPath, "cad_sub", "fv_sub1", CadDocumentType.Iam),
            Managed(RootPath, sharedPath, "cad_shared", "fv_s1"),
            Managed(subIamPath, sharedPath, "cad_shared", "fv_s1"), // SAME identity, SAME path
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_sub"] = ComponentClassification.ProjectSpecific,
            ["cad_shared"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification);

        var shared = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_shared");
        Assert.Equal(CopyDesignAction.Copy, shared.ProposedAction);
        Assert.True(plan.IsExecutable);
    }

    // ---- 23. BLOCKER 2: drawings use the SAME evidence + reconciliation ---

    [Fact]
    public void An_UNVERIFIED_drawing_association_is_NeedsDecision_not_silently_mirrored()
    {
        var drawingPath = P("Design", "10073-AS210.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: false) }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var drawing = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.False(drawing.IsVerified);
        Assert.Equal(CopyDesignAction.NeedsDecision, drawing.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_drawing_association_with_no_cadDocumentId_is_NeedsDecision()
    {
        var drawingPath = P("Design", "UNKNOWN.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing(null, null, drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var drawing = Assert.Single(plan.Nodes, n => !n.IsRoot);
        Assert.Null(drawing.CadDocumentId);
        Assert.False(drawing.IsManaged);
        Assert.Equal(CopyDesignAction.NeedsDecision, drawing.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_drawing_association_with_no_FileVersionId_is_NeedsDecision()
    {
        var drawingPath = P("Design", "10073-AS210.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", null, drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var drawing = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, drawing.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_drawing_association_with_no_resolvable_path_is_NeedsDecision()
    {
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", null, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var drawing = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.False(drawing.IsResolved);
        Assert.Equal(CopyDesignAction.NeedsDecision, drawing.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_drawing_observations_with_the_SAME_cadDocumentId_but_DIFFERENT_FileVersionId_fail_closed(bool reversed)
    {
        var drawingPath = P("Design", "10073-AS210.idw");
        var d1 = new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true);
        var d2 = new AssociatedDrawing("cad_dwg", "fv_dwg2", drawingPath, CadDocumentType.Idw, IsVerified: true);
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                reversed ? new[] { d2, d1 } : new[] { d1, d2 }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var drawing = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, drawing.ProposedAction);
        Assert.Contains(drawing.Reasons, r => r.Contains("FileVersionId", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void Two_drawing_observations_with_the_SAME_cadDocumentId_but_DIFFERENT_path_fail_closed()
    {
        var pathA = P("Design", "A.idw");
        var pathB = P("Design", "B.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[]
            {
                new AssociatedDrawing("cad_dwg", "fv_dwg1", pathA, CadDocumentType.Idw, IsVerified: true),
                new AssociatedDrawing("cad_dwg", "fv_dwg1", pathB, CadDocumentType.Idw, IsVerified: true),
            }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var drawing = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, drawing.ProposedAction);
        Assert.False(plan.IsExecutable);
        // Both associated drawings still show up as edges - never silently
        // dropped just because they collapsed into one conflicted node.
        Assert.Equal(2, plan.Edges.Count(e => e.RelationshipKind == CadRelationshipKind.DrawingModel));
    }

    [Fact]
    public void Two_drawing_observations_with_the_SAME_identity_but_DIFFERENT_verification_state_fail_closed()
    {
        var drawingPath = P("Design", "10073-AS210.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[]
            {
                new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true),
                new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: false),
            }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var drawing = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, drawing.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void Two_drawing_observations_at_the_SAME_path_with_DIFFERENT_cadDocumentIds_fail_closed()
    {
        var drawingPath = P("Design", "SHARED.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[]
            {
                new AssociatedDrawing("cad_dwg_a", "fv_1", drawingPath, CadDocumentType.Idw, IsVerified: true),
                new AssociatedDrawing("cad_dwg_b", "fv_1", drawingPath, CadDocumentType.Idw, IsVerified: true),
            }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var a = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg_a");
        var b = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg_b");
        Assert.Equal(CopyDesignAction.NeedsDecision, a.ProposedAction);
        Assert.Equal(CopyDesignAction.NeedsDecision, b.ProposedAction);
        Assert.False(plan.IsExecutable);
        Assert.Equal(2, plan.Edges.Count(e => e.RelationshipKind == CadRelationshipKind.DrawingModel));
    }

    [Fact]
    public void A_drawing_claiming_the_SAME_cadDocumentId_as_an_existing_model_with_incompatible_facts_fails_closed()
    {
        var drawingPath = P("Design", "10073-AS210-OTHER.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            // Same cadDocumentId as the ROOT ("cad_root"), but an
            // incompatible FileVersionId and a different path - an authority
            // inconsistency that must never be silently accepted.
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_root", "fv_root_DIFFERENT", drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath, fv: "fv_root1"), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        // Root and "drawing" share cadDocumentId "cad_root" -> ONE reconciled,
        // conflicted node - never a silently-accepted second identity.
        var merged = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_root");
        Assert.Equal(CopyDesignAction.NeedsDecision, merged.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_PROVEN_complete_drawing_association_still_mirrors_its_models_action()
    {
        // Regression: the evidence-based pipeline must still let a COMPLETE,
        // verified drawing observation mirror its model's own COPY/REUSE.
        var drawingPath = P("Design", "10073-AS210.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var drawing = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.True(drawing.IsManaged);
        Assert.True(drawing.IsVerified);
        Assert.True(drawing.IsResolved);
        Assert.Equal(CopyDesignAction.Copy, drawing.ProposedAction); // mirrors the root's own COPY
        Assert.True(plan.IsExecutable);
    }

    // ---- 24. BLOCKER 3: canonical selection is a total deterministic order

    [Fact]
    public void Compatible_shared_dependency_observations_produce_the_EXACT_SAME_plan_regardless_of_order()
    {
        var subIamPath = P("Design", "10073-SUB100.iam");
        var sharedPartPath = P("Design", "SHARED.ipt");
        var sub = Managed(RootPath, subIamPath, "cad_sub", "fv_sub1", CadDocumentType.Iam);
        var sharedViaRoot = Managed(RootPath, sharedPartPath, "cad_shared", "fv_s1");
        var sharedViaSub = Managed(subIamPath, sharedPartPath, "cad_shared", "fv_s1");
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_sub"] = ComponentClassification.ProjectSpecific,
            ["cad_shared"] = ComponentClassification.ProjectSpecific,
        });

        var planNormal = Plan(Scan(Root(RootPath), new[] { sub, sharedViaRoot, sharedViaSub }), classification: classification);
        var planReversed = Plan(Scan(Root(RootPath), new[] { sharedViaSub, sharedViaRoot, sub }), classification: classification);

        AssertPlansAreEquivalent(planNormal, planReversed);
    }

    [Fact]
    public void Conflicting_FileVersionId_observations_produce_the_EXACT_SAME_plan_regardless_of_order()
    {
        var planNormal = Plan(BuildConflictScan(ConflictKind.FileVersionId, reversed: false));
        var planReversed = Plan(BuildConflictScan(ConflictKind.FileVersionId, reversed: true));

        AssertPlansAreEquivalent(planNormal, planReversed);
    }

    [Fact]
    public void Case_only_path_differences_produce_the_EXACT_SAME_plan_regardless_of_order()
    {
        var pathLower = P("Design", "shared.ipt");
        var pathUpper = P("Design", "SHARED.ipt");
        var obsLower = Managed(RootPath, pathLower, "cad_shared", "fv_1");
        var obsUpper = Managed(RootPath, pathUpper, "cad_shared", "fv_1");

        var planNormal = Plan(Scan(Root(RootPath), new[] { obsLower, obsUpper }));
        var planReversed = Plan(Scan(Root(RootPath), new[] { obsUpper, obsLower }));

        AssertPlansAreEquivalent(planNormal, planReversed);

        // The canonical choice is a REAL, specific, deterministic value - the
        // exact-case string that sorts first ordinally - not merely "the same
        // by coincidence of this particular run".
        var expectedCanonical = string.CompareOrdinal(pathLower, pathUpper) <= 0 ? pathLower : pathUpper;
        var node = Assert.Single(planNormal.Nodes, n => n.CadDocumentId == "cad_shared");
        Assert.Equal(expectedCanonical, node.SourceAbsolutePath);
    }

    [Fact]
    public void Case_only_FileVersionId_differences_are_treated_as_a_conflict_and_produce_the_EXACT_SAME_plan_regardless_of_order()
    {
        var sharedPath = P("Design", "SHARED.ipt");
        var subPath = P("Design", "10073-SUB100.iam");
        var obsA = Managed(RootPath, sharedPath, "cad_shared", "FV-ABC");
        var obsB = Managed(RootPath, sharedPath, "cad_shared", "fv-abc"); // same text, different case
        var sub = Managed(RootPath, subPath, "cad_sub", "fv_sub1", CadDocumentType.Iam);

        var planNormal = Plan(Scan(Root(RootPath), new[] { sub, obsA, obsB }));
        var planReversed = Plan(Scan(Root(RootPath), new[] { sub, obsB, obsA }));

        AssertPlansAreEquivalent(planNormal, planReversed);

        // FileVersionId is an opaque, case-sensitive persisted id (like a
        // cuid) - "FV-ABC" and "fv-abc" are DIFFERENT values, never silently
        // folded together as "the same version, different case".
        var shared = Assert.Single(planNormal.Nodes, n => n.CadDocumentId == "cad_shared");
        Assert.Equal(CopyDesignAction.NeedsDecision, shared.ProposedAction);
        Assert.Contains(shared.Reasons, r => r.Contains("FileVersionId", StringComparison.Ordinal));
        Assert.False(planNormal.IsExecutable);
    }

    [Fact]
    public void A_child_reached_via_two_DIFFERENT_relationship_kinds_has_no_arbitrary_representative_but_keeps_exact_edges()
    {
        var childPath = P("Design", "SHARED.ipt");
        var obsComponent = Managed(RootPath, childPath, "cad_shared", "fv_1", kind: CadRelationshipKind.Component);
        var obsDrawingModel = Managed(RootPath, childPath, "cad_shared", "fv_1", kind: CadRelationshipKind.DrawingModel);

        var planNormal = Plan(Scan(Root(RootPath), new[] { obsComponent, obsDrawingModel }));
        var planReversed = Plan(Scan(Root(RootPath), new[] { obsDrawingModel, obsComponent }));

        foreach (var plan in new[] { planNormal, planReversed })
        {
            var shared = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_shared");
            // Ambiguous - two DIFFERENT relationship kinds were observed, so
            // neither is arbitrarily chosen as "the" representative.
            Assert.Null(shared.RelationshipToParent);
            Assert.Contains(plan.Edges, e => e.RelationshipKind == CadRelationshipKind.Component
                && e.ChildAbsolutePath.Equals(childPath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(plan.Edges, e => e.RelationshipKind == CadRelationshipKind.DrawingModel
                && e.ChildAbsolutePath.Equals(childPath, StringComparison.OrdinalIgnoreCase));
        }

        AssertPlansAreEquivalent(planNormal, planReversed);
    }

    // ---- shared full-plan equivalence helper (order-insensitive on
    //      collections, but STRICT/exact on every scalar field so a
    //      deterministic-canonical-selection regression cannot hide behind
    //      "close enough") --------------------------------------------------

    private static string NodeSortKey(CopyDesignNode n) => (n.CadDocumentId ?? "\0") + "|" + n.SourceAbsolutePath;

    private static void AssertPlansAreEquivalent(CopyDesignPlan expected, CopyDesignPlan actual)
    {
        Assert.Equal(expected.RootAbsolutePath, actual.RootAbsolutePath);
        Assert.Equal(expected.IsExecutable, actual.IsExecutable);
        Assert.Equal(expected.ScanWasComplete, actual.ScanWasComplete);
        Assert.Equal(expected.DrawingAssociationAvailable, actual.DrawingAssociationAvailable);
        Assert.Equal(expected.ModelFilesOnlyAcknowledged, actual.ModelFilesOnlyAcknowledged);
        Assert.Equal(
            expected.Warnings.OrderBy(w => w, StringComparer.Ordinal),
            actual.Warnings.OrderBy(w => w, StringComparer.Ordinal));

        var expectedNodes = expected.Nodes.OrderBy(NodeSortKey, StringComparer.Ordinal).ToArray();
        var actualNodes = actual.Nodes.OrderBy(NodeSortKey, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedNodes.Length, actualNodes.Length);
        for (var i = 0; i < expectedNodes.Length; i++)
        {
            AssertNodesAreEquivalent(expectedNodes[i], actualNodes[i]);
        }

        var expectedEdges = expected.Edges
            .Select(e => $"{e.ParentAbsolutePath}|{e.ChildAbsolutePath}|{e.RelationshipKind}|{e.Disposition}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        var actualEdges = actual.Edges
            .Select(e => $"{e.ParentAbsolutePath}|{e.ChildAbsolutePath}|{e.RelationshipKind}|{e.Disposition}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedEdges, actualEdges);
    }

    private static void AssertNodesAreEquivalent(CopyDesignNode expected, CopyDesignNode actual)
    {
        Assert.Equal(expected.CadDocumentId, actual.CadDocumentId);
        Assert.Equal(expected.CurrentFileVersionId, actual.CurrentFileVersionId);
        Assert.Equal(expected.DocumentType, actual.DocumentType);
        Assert.Equal(expected.SourceAbsolutePath, actual.SourceAbsolutePath);
        Assert.Equal(expected.SourceFileName, actual.SourceFileName);
        Assert.Equal(expected.RelationshipToParent, actual.RelationshipToParent);
        Assert.Equal(expected.IsManaged, actual.IsManaged);
        Assert.Equal(expected.IsVerified, actual.IsVerified);
        Assert.Equal(expected.IsResolved, actual.IsResolved);
        Assert.Equal(expected.ProposedAction, actual.ProposedAction);
        Assert.Equal(expected.ProposedDestinationFileName, actual.ProposedDestinationFileName);
        Assert.Equal(expected.ProposedDestinationAbsolutePath, actual.ProposedDestinationAbsolutePath);
        Assert.Equal(expected.IsRoot, actual.IsRoot);
        Assert.Equal(
            expected.Reasons.OrderBy(r => r, StringComparer.Ordinal),
            actual.Reasons.OrderBy(r => r, StringComparer.Ordinal));
    }

    // ==================================================================
    // ROUND 4 - CODEX GRAPH CORRECTNESS + DETERMINISM FIXES
    // ==================================================================

    // ---- 25. BLOCKER 1: drawing edge direction (DRAWING -> MODEL) --------

    [Fact]
    public void An_associated_drawing_edge_points_FROM_the_drawing_TO_the_model()
    {
        var drawingPath = P("Design", "10073-AS210.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var edge = Assert.Single(plan.Edges, e => e.RelationshipKind == CadRelationshipKind.DrawingModel);
        Assert.Equal(drawingPath, edge.ParentAbsolutePath, ignoreCase: true);
        Assert.Equal(RootPath, edge.ChildAbsolutePath, ignoreCase: true);
    }

    [Fact]
    public void No_model_TO_drawing_DrawingModel_edge_is_ever_emitted()
    {
        var drawingPath = P("Design", "10073-AS210.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        Assert.DoesNotContain(plan.Edges, e => e.RelationshipKind == CadRelationshipKind.DrawingModel
            && e.ParentAbsolutePath.Equals(RootPath, StringComparison.OrdinalIgnoreCase)
            && e.ChildAbsolutePath.Equals(drawingPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_DrawingModel_edges_disposition_reflects_the_MODELs_action_not_the_drawings()
    {
        // Root -> COPY (verified, complete identity, selected root). The
        // drawing mirrors it, so the edge (drawing -> model) disposition must
        // say PointsToNewCopy because the TARGET (the model/root) is copied.
        var drawingPath = P("Design", "10073-AS210.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var edge = Assert.Single(plan.Edges, e => e.RelationshipKind == CadRelationshipKind.DrawingModel);
        Assert.Equal(CopyDesignEdgeDisposition.PointsToNewCopy, edge.Disposition);
    }

    [Fact]
    public void A_DrawingModel_edges_disposition_reflects_a_REUSED_model_correctly()
    {
        var partPath = P("Design", "STD-BOLT-M6.ipt");
        var drawingPath = P("Design", "STD-BOLT-M6.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_bolt"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_bolt", "fv_bolt1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_bolt"] = ComponentClassification.LibraryOrShared,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var bolt = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_bolt");
        Assert.Equal(CopyDesignAction.Reuse, bolt.ProposedAction);
        var edge = Assert.Single(plan.Edges, e => e.RelationshipKind == CadRelationshipKind.DrawingModel);
        Assert.Equal(CopyDesignEdgeDisposition.RemainsOnReusedSource, edge.Disposition);
    }

    [Fact]
    public void The_preview_report_renders_successfully_and_never_claims_a_model_TO_drawing_direction()
    {
        var drawingPath = P("Design", "10073-AS210.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);
        var text = CopyDesignPlanTextReport.Render(plan);

        // The report does not currently render edges/direction at all - only
        // one row per NODE - so there is nothing in it that could
        // misrepresent the dependency direction. This pins that fact so a
        // future change that DOES add direction text is forced to get it
        // right rather than silently inverting it.
        Assert.Contains("10073-AS210.idw", text);
        Assert.DoesNotContain("->", text);
    }

    // ---- 26. BLOCKER 2: multi-owner drawings require unanimous agreement --

    [Fact]
    public void A_drawing_with_TWO_COPY_owners_is_forced_to_COPY()
    {
        var pathA = P("Design", "A.ipt");
        var pathB = P("Design", "B.ipt");
        var drawingPath = P("Design", "SHARED.idw");
        var drawing = new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true);
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_m1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { drawing }),
            ["cad_m2"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { drawing }),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, pathA, "cad_m1", "fv_m1"),
            Managed(RootPath, pathB, "cad_m2", "fv_m2"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_m1"] = ComponentClassification.ProjectSpecific,
            ["cad_m2"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.Copy, dwg.ProposedAction);
        Assert.True(plan.IsExecutable);
        // Both owner edges preserved - never collapsed to one.
        Assert.Equal(2, plan.Edges.Count(e => e.RelationshipKind == CadRelationshipKind.DrawingModel));
    }

    [Fact]
    public void A_drawing_with_TWO_REUSE_owners_is_forced_to_REUSE()
    {
        var pathA = P("Design", "A.ipt");
        var pathB = P("Design", "B.ipt");
        var drawingPath = P("Design", "SHARED.idw");
        var drawing = new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true);
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_m1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { drawing }),
            ["cad_m2"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { drawing }),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, pathA, "cad_m1", "fv_m1"),
            Managed(RootPath, pathB, "cad_m2", "fv_m2"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_m1"] = ComponentClassification.LibraryOrShared,
            ["cad_m2"] = ComponentClassification.LibraryOrShared,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.Reuse, dwg.ProposedAction);
        Assert.True(plan.IsExecutable);
        Assert.Equal(2, plan.Edges.Count(e => e.RelationshipKind == CadRelationshipKind.DrawingModel));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_drawing_with_COPY_and_REUSE_owners_is_NeedsDecision_regardless_of_order(bool reversed)
    {
        var pathA = P("Design", "A.ipt");
        var pathB = P("Design", "B.ipt");
        var drawingPath = P("Design", "SHARED.idw");
        var drawing = new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true);
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_m1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { drawing }),
            ["cad_m2"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { drawing }),
        });
        var m1 = Managed(RootPath, pathA, "cad_m1", "fv_m1");
        var m2 = Managed(RootPath, pathB, "cad_m2", "fv_m2");
        var scan = Scan(Root(RootPath), reversed ? new[] { m2, m1 } : new[] { m1, m2 });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_m1"] = ComponentClassification.ProjectSpecific, // -> Copy
            ["cad_m2"] = ComponentClassification.LibraryOrShared, // -> Reuse
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, dwg.ProposedAction);
        Assert.False(plan.IsExecutable);
        Assert.Equal(2, plan.Edges.Count(e => e.RelationshipKind == CadRelationshipKind.DrawingModel));
    }

    [Fact]
    public void A_drawing_with_a_COPY_owner_and_a_NeedsDecision_owner_is_NeedsDecision()
    {
        var pathA = P("Design", "A.ipt");
        var pathB = P("Design", "B.ipt"); // left Unknown -> NeedsDecision
        var drawingPath = P("Design", "SHARED.idw");
        var drawing = new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true);
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_m1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { drawing }),
            ["cad_m2"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { drawing }),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, pathA, "cad_m1", "fv_m1"),
            Managed(RootPath, pathB, "cad_m2", "fv_m2"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_m1"] = ComponentClassification.ProjectSpecific, // -> Copy
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, dwg.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    // ---- 27. BLOCKER 3: unresolved drawing identity still reconciles by id

    [Fact]
    public void An_unresolved_drawing_observation_reconciles_against_a_resolved_observation_of_the_SAME_id_and_conflicts_if_incompatible()
    {
        var pathA = P("Design", "A.ipt");
        var pathB = P("Design", "B.ipt");
        var drawingPath = P("Design", "SHARED.idw");
        var unresolvedDrawing = new AssociatedDrawing("cad_dwg", "fv_dwg1", null, CadDocumentType.Idw, IsVerified: true);
        var resolvedDrawing = new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true);
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_m1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { unresolvedDrawing }),
            ["cad_m2"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { resolvedDrawing }),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, pathA, "cad_m1", "fv_m1"),
            Managed(RootPath, pathB, "cad_m2", "fv_m2"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_m1"] = ComponentClassification.ProjectSpecific,
            ["cad_m2"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        // ONE reconciled node for "cad_dwg" - the unresolved and resolved
        // observations shared the SAME id-based key, never two silently-
        // separate drawing nodes.
        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, dwg.ProposedAction);
        Assert.Contains(dwg.Reasons, r => r.Contains("resolved state", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unresolved_vs_resolved_same_drawing_id_conflict_is_order_independent(bool reversed)
    {
        var pathA = P("Design", "A.ipt");
        var pathB = P("Design", "B.ipt");
        var drawingPath = P("Design", "SHARED.idw");
        var unresolvedDrawing = new AssociatedDrawing("cad_dwg", "fv_dwg1", null, CadDocumentType.Idw, IsVerified: true);
        var resolvedDrawing = new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true);
        var drawingSource = reversed
            ? new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
            {
                ["cad_m2"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { resolvedDrawing }),
                ["cad_m1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { unresolvedDrawing }),
            })
            : new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
            {
                ["cad_m1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { unresolvedDrawing }),
                ["cad_m2"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { resolvedDrawing }),
            });
        var m1 = Managed(RootPath, pathA, "cad_m1", "fv_m1");
        var m2 = Managed(RootPath, pathB, "cad_m2", "fv_m2");
        var scan = Scan(Root(RootPath), reversed ? new[] { m2, m1 } : new[] { m1, m2 });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_m1"] = ComponentClassification.ProjectSpecific,
            ["cad_m2"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, dwg.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void Repeated_unresolved_drawing_observations_of_the_SAME_id_from_the_SAME_owner_reconcile_cleanly()
    {
        var drawingA = new AssociatedDrawing("cad_dwg", "fv_dwg1", null, CadDocumentType.Idw, IsVerified: true);
        var drawingB = new AssociatedDrawing("cad_dwg", "fv_dwg1", null, CadDocumentType.Idw, IsVerified: true);
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { drawingA, drawingB }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.False(dwg.IsResolved);
        // Unresolved -> NeedsDecision via the ordinary "unresolved" gate, but
        // NOT because of a reconciliation CONFLICT - both observations agree
        // on every fact and reconcile into one clean node.
        Assert.DoesNotContain(dwg.Reasons, r => r.Contains("disagree on safety-relevant identity", StringComparison.Ordinal));
    }

    [Fact]
    public void A_drawing_id_with_a_missing_path_still_participates_in_conflict_analysis()
    {
        var drawingV1 = new AssociatedDrawing("cad_dwg", "fv_1", null, CadDocumentType.Idw, IsVerified: true);
        var drawingV2 = new AssociatedDrawing("cad_dwg", "fv_2", null, CadDocumentType.Idw, IsVerified: true);
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { drawingV1, drawingV2 }),
        });
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, dwg.ProposedAction);
        Assert.Contains(dwg.Reasons, r => r.Contains("FileVersionId", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);
    }

    // ---- 28. BLOCKER 4: opaque exact IDs - padded values fail closed -----

    [Fact]
    public void A_padded_and_unpadded_cadDocumentId_on_the_SAME_path_fail_closed()
    {
        var sharedPath = P("Design", "SHARED.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, sharedPath, "cad_a", "fv_1"),
            Managed(RootPath, sharedPath, " cad_a ", "fv_1"), // padded - a DIFFERENT opaque value
        });

        var plan = Plan(scan);

        Assert.Equal(2, plan.Nodes.Count(n => !n.IsRoot));
        var clean = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_a");
        var padded = Assert.Single(plan.Nodes, n => n.CadDocumentId == " cad_a ");
        Assert.Equal(CopyDesignAction.NeedsDecision, clean.ProposedAction);
        Assert.Equal(CopyDesignAction.NeedsDecision, padded.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_padded_root_cadDocumentId_is_NeedsDecision()
    {
        var scan = Scan(Root(RootPath, cadDocumentId: " cad_root "), Array.Empty<CadReference>());

        var plan = Plan(scan);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal(CopyDesignAction.NeedsDecision, root.ProposedAction);
        Assert.Contains(root.Reasons, r => r.Contains("noncanonical", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_padded_child_cadDocumentId_is_NeedsDecision()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, " cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            [" cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification);

        var part = Assert.Single(plan.Nodes, n => !n.IsRoot);
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction); // NOT Copy, despite ProjectSpecific
        Assert.Contains(part.Reasons, r => r.Contains("noncanonical", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_padded_FileVersionId_is_NeedsDecision()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", " fv_p1 ") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification);

        var part = Assert.Single(plan.Nodes, n => !n.IsRoot);
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction);
        Assert.Contains(part.Reasons, r => r.Contains("FileVersionId is noncanonical", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void Exact_IDs_are_compared_ordinally_and_case_different_IDs_remain_distinct()
    {
        var sharedPath = P("Design", "SHARED.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, sharedPath, "CAD_A", "fv_1"),
            Managed(RootPath, sharedPath, "cad_a", "fv_1"), // different case - a DIFFERENT id
        });

        var plan = Plan(scan);

        Assert.Equal(2, plan.Nodes.Count(n => !n.IsRoot));
        Assert.Contains(plan.Nodes, n => n.CadDocumentId == "CAD_A");
        Assert.Contains(plan.Nodes, n => n.CadDocumentId == "cad_a");
        Assert.False(plan.IsExecutable); // same path, two distinct identities
    }

    // ---- 29. BLOCKER 5: exact deterministic order, no test-side sorting ---

    [Fact]
    public void Compatible_observations_produce_an_IDENTICAL_plan_across_permutations_no_test_side_sorting()
    {
        var subIamPath = P("Design", "10073-SUB100.iam");
        var sharedPartPath = P("Design", "SHARED.ipt");
        var sub = Managed(RootPath, subIamPath, "cad_sub", "fv_sub1", CadDocumentType.Iam);
        var sharedViaRoot = Managed(RootPath, sharedPartPath, "cad_shared", "fv_s1");
        var sharedViaSub = Managed(subIamPath, sharedPartPath, "cad_shared", "fv_s1");
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_sub"] = ComponentClassification.ProjectSpecific,
            ["cad_shared"] = ComponentClassification.ProjectSpecific,
        });

        var planNormal = Plan(Scan(Root(RootPath), new[] { sub, sharedViaRoot, sharedViaSub }), classification: classification);
        var planReversed = Plan(Scan(Root(RootPath), new[] { sharedViaSub, sharedViaRoot, sub }), classification: classification);

        AssertPlansAreIdentical(planNormal, planReversed);
        Assert.Equal(CopyDesignPlanTextReport.Render(planNormal), CopyDesignPlanTextReport.Render(planReversed));
    }

    [Fact]
    public void Conflicting_FileVersionId_observations_produce_an_IDENTICAL_plan_across_permutations()
    {
        var planNormal = Plan(BuildConflictScan(ConflictKind.FileVersionId, reversed: false));
        var planReversed = Plan(BuildConflictScan(ConflictKind.FileVersionId, reversed: true));

        AssertPlansAreIdentical(planNormal, planReversed);
        Assert.Equal(CopyDesignPlanTextReport.Render(planNormal), CopyDesignPlanTextReport.Render(planReversed));
    }

    [Fact]
    public void Case_only_path_differences_produce_an_IDENTICAL_plan_across_permutations()
    {
        var pathLower = P("Design", "shared.ipt");
        var pathUpper = P("Design", "SHARED.ipt");
        var obsLower = Managed(RootPath, pathLower, "cad_shared", "fv_1");
        var obsUpper = Managed(RootPath, pathUpper, "cad_shared", "fv_1");

        var planNormal = Plan(Scan(Root(RootPath), new[] { obsLower, obsUpper }));
        var planReversed = Plan(Scan(Root(RootPath), new[] { obsUpper, obsLower }));

        AssertPlansAreIdentical(planNormal, planReversed);
        Assert.Equal(CopyDesignPlanTextReport.Render(planNormal), CopyDesignPlanTextReport.Render(planReversed));
    }

    [Fact]
    public void Case_only_FileVersionId_differences_produce_an_IDENTICAL_plan_across_permutations()
    {
        var sharedPath = P("Design", "SHARED.ipt");
        var subPath = P("Design", "10073-SUB100.iam");
        var obsA = Managed(RootPath, sharedPath, "cad_shared", "FV-ABC");
        var obsB = Managed(RootPath, sharedPath, "cad_shared", "fv-abc");
        var sub = Managed(RootPath, subPath, "cad_sub", "fv_sub1", CadDocumentType.Iam);

        var planNormal = Plan(Scan(Root(RootPath), new[] { sub, obsA, obsB }));
        var planReversed = Plan(Scan(Root(RootPath), new[] { sub, obsB, obsA }));

        AssertPlansAreIdentical(planNormal, planReversed);
        Assert.Equal(CopyDesignPlanTextReport.Render(planNormal), CopyDesignPlanTextReport.Render(planReversed));
    }

    [Fact]
    public void Duplicate_destination_warnings_are_IDENTICAL_across_permutations()
    {
        var partA = P("Design", "A.ipt");
        var partB = P("Design", "B.ipt");
        var a = Managed(RootPath, partA, "cad_a", "fv_a1");
        var b = Managed(RootPath, partB, "cad_b", "fv_b1");
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });
        var fixedRule = new FixedNameRule("SAME.ipt");

        var planNormal = Plan(Scan(Root(RootPath), new[] { a, b }), rule: fixedRule, classification: classification);
        var planReversed = Plan(Scan(Root(RootPath), new[] { b, a }), rule: fixedRule, classification: classification);

        AssertPlansAreIdentical(planNormal, planReversed);
        Assert.Contains(planNormal.Warnings, w => w.Contains("Duplicate proposed destination", StringComparison.Ordinal));
        Assert.Equal(CopyDesignPlanTextReport.Render(planNormal), CopyDesignPlanTextReport.Render(planReversed));
    }

    [Fact]
    public void Multi_owner_drawing_plans_are_IDENTICAL_across_owner_discovery_order()
    {
        var pathA = P("Design", "A.ipt");
        var pathB = P("Design", "B.ipt");
        var drawingPath = P("Design", "SHARED.idw");
        var drawing = new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true);
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_m1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { drawing }),
            ["cad_m2"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[] { drawing }),
        });
        var m1 = Managed(RootPath, pathA, "cad_m1", "fv_m1");
        var m2 = Managed(RootPath, pathB, "cad_m2", "fv_m2");
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_m1"] = ComponentClassification.ProjectSpecific,
            ["cad_m2"] = ComponentClassification.ProjectSpecific,
        });

        var planNormal = Plan(Scan(Root(RootPath), new[] { m1, m2 }), classification: classification, drawings: drawingSource);
        var planReversed = Plan(Scan(Root(RootPath), new[] { m2, m1 }), classification: classification, drawings: drawingSource);

        AssertPlansAreIdentical(planNormal, planReversed);
        Assert.Equal(CopyDesignPlanTextReport.Render(planNormal), CopyDesignPlanTextReport.Render(planReversed));
    }

    // ---- shared EXACT (no re-sorting) full-plan equality helper -----------

    private static void AssertNodesAreIdentical(CopyDesignNode expected, CopyDesignNode actual)
    {
        Assert.Equal(expected.CadDocumentId, actual.CadDocumentId);
        Assert.Equal(expected.CurrentFileVersionId, actual.CurrentFileVersionId);
        Assert.Equal(expected.DocumentType, actual.DocumentType);
        Assert.Equal(expected.SourceAbsolutePath, actual.SourceAbsolutePath);
        Assert.Equal(expected.SourceFileName, actual.SourceFileName);
        Assert.Equal(expected.RelationshipToParent, actual.RelationshipToParent);
        Assert.Equal(expected.IsManaged, actual.IsManaged);
        Assert.Equal(expected.IsVerified, actual.IsVerified);
        Assert.Equal(expected.IsResolved, actual.IsResolved);
        Assert.Equal(expected.ProposedAction, actual.ProposedAction);
        Assert.Equal(expected.ProposedDestinationFileName, actual.ProposedDestinationFileName);
        Assert.Equal(expected.ProposedDestinationAbsolutePath, actual.ProposedDestinationAbsolutePath);
        Assert.Equal(expected.IsRoot, actual.IsRoot);
        Assert.Equal(expected.Reasons, actual.Reasons); // EXACT sequence - no re-sorting
    }

    private static void AssertPlansAreIdentical(CopyDesignPlan expected, CopyDesignPlan actual)
    {
        Assert.Equal(expected.RootAbsolutePath, actual.RootAbsolutePath);
        Assert.Equal(expected.IsExecutable, actual.IsExecutable);
        Assert.Equal(expected.ScanWasComplete, actual.ScanWasComplete);
        Assert.Equal(expected.DrawingAssociationAvailable, actual.DrawingAssociationAvailable);
        Assert.Equal(expected.ModelFilesOnlyAcknowledged, actual.ModelFilesOnlyAcknowledged);
        Assert.Equal(expected.Warnings, actual.Warnings); // EXACT sequence - no re-sorting

        Assert.Equal(expected.Nodes.Count, actual.Nodes.Count);
        for (var i = 0; i < expected.Nodes.Count; i++)
        {
            AssertNodesAreIdentical(expected.Nodes[i], actual.Nodes[i]);
        }

        // CopyDesignEdge has no list-typed fields, so record structural
        // equality already works for a direct sequence comparison.
        Assert.Equal(expected.Edges, actual.Edges);
    }

    // ==================================================================
    // ROUND 5 - FINAL DRAWING OWNER + AUTHORITY FIXES
    // ==================================================================

    private sealed class SpyDrawingSource(DrawingAssociationResult result) : IDrawingAssociationSource
    {
        public List<string> QueriedIds { get; } = new();

        public DrawingAssociationResult GetAssociatedDrawings(string cadDocumentId)
        {
            QueriedIds.Add(cadDocumentId);
            return result;
        }
    }

    // ---- 30. BLOCKER 1: drawing-owner consensus from ALL DrawingModel edges

    [Fact]
    public void A_drawing_ROOT_with_two_COPY_model_owners_via_SCANNER_edges_is_COPY()
    {
        var rootIdentity = new PlmIdentity("cad_root", null, "fv_root1");
        var modelAPath = P("Design", "A.ipt");
        var modelBPath = P("Design", "B.ipt");
        var scan = Scan(Root(RootPath, type: CadDocumentType.Idw), new[]
        {
            Managed(RootPath, modelAPath, "cad_a", "fv_a1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity),
            Managed(RootPath, modelBPath, "cad_b", "fv_b1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal(CopyDesignAction.Copy, root.ProposedAction);
        Assert.True(plan.IsExecutable);
    }

    [Fact]
    public void A_drawing_ROOT_with_two_REUSE_model_owners_via_SCANNER_edges_is_REUSE()
    {
        var rootIdentity = new PlmIdentity("cad_root", null, "fv_root1");
        var modelAPath = P("Design", "A.ipt");
        var modelBPath = P("Design", "B.ipt");
        var scan = Scan(Root(RootPath, type: CadDocumentType.Idw), new[]
        {
            Managed(RootPath, modelAPath, "cad_a", "fv_a1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity),
            Managed(RootPath, modelBPath, "cad_b", "fv_b1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.LibraryOrShared,
            ["cad_b"] = ComponentClassification.LibraryOrShared,
        });

        var plan = Plan(scan, classification: classification);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal(CopyDesignAction.Reuse, root.ProposedAction);
        Assert.True(plan.IsExecutable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_drawing_ROOT_with_COPY_and_REUSE_model_owners_via_SCANNER_edges_is_NeedsDecision(bool reversed)
    {
        var rootIdentity = new PlmIdentity("cad_root", null, "fv_root1");
        var modelAPath = P("Design", "A.ipt");
        var modelBPath = P("Design", "B.ipt");
        var a = Managed(RootPath, modelAPath, "cad_a", "fv_a1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity);
        var b = Managed(RootPath, modelBPath, "cad_b", "fv_b1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity);
        var scan = Scan(Root(RootPath, type: CadDocumentType.Idw), reversed ? new[] { b, a } : new[] { a, b });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.LibraryOrShared,
        });

        var plan = Plan(scan, classification: classification);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal(CopyDesignAction.NeedsDecision, root.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_drawing_ROOT_with_a_COPY_owner_and_a_NeedsDecision_owner_via_SCANNER_edges_is_NeedsDecision()
    {
        var rootIdentity = new PlmIdentity("cad_root", null, "fv_root1");
        var modelAPath = P("Design", "A.ipt");
        var modelBPath = P("Design", "B.ipt"); // left Unknown -> NeedsDecision
        var scan = Scan(Root(RootPath, type: CadDocumentType.Idw), new[]
        {
            Managed(RootPath, modelAPath, "cad_a", "fv_a1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity),
            Managed(RootPath, modelBPath, "cad_b", "fv_b1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal(CopyDesignAction.NeedsDecision, root.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void Drawing_root_owner_consensus_produces_the_EXACT_SAME_plan_regardless_of_scan_edge_order()
    {
        var rootIdentity = new PlmIdentity("cad_root", null, "fv_root1");
        var modelAPath = P("Design", "A.ipt");
        var modelBPath = P("Design", "B.ipt");
        var a = Managed(RootPath, modelAPath, "cad_a", "fv_a1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity);
        var b = Managed(RootPath, modelBPath, "cad_b", "fv_b1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity);
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var planNormal = Plan(Scan(Root(RootPath, type: CadDocumentType.Idw), new[] { a, b }), classification: classification);
        var planReversed = Plan(Scan(Root(RootPath, type: CadDocumentType.Idw), new[] { b, a }), classification: classification);

        AssertPlansAreIdentical(planNormal, planReversed);
    }

    [Fact]
    public void A_scanner_edge_and_an_association_edge_for_the_SAME_drawing_model_pair_do_not_double_influence_the_verdict()
    {
        // The scan discovers drawingX (IDW, reached as an ordinary reference
        // from the root) directly referencing ModelA - a scanner-native
        // DrawingModel edge. An authoritative association source ALSO
        // reports the SAME (drawingX, ModelA) pair when queried for ModelA's
        // cadDocumentId - redundant evidence for the SAME relationship via
        // two different mechanisms.
        var drawingXPath = P("Design", "X.idw");
        var modelAPath = P("Design", "A.ipt");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwgX", "fv_dwgX1", drawingXPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var drawingXIdentity = new PlmIdentity("cad_dwgX", null, "fv_dwgX1");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, drawingXPath, "cad_dwgX", "fv_dwgX1", CadDocumentType.Idw, kind: CadRelationshipKind.Other),
            Managed(drawingXPath, modelAPath, "cad_a", "fv_a1", kind: CadRelationshipKind.DrawingModel, parentIdentity: drawingXIdentity),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwgX = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwgX");
        Assert.Equal(CopyDesignAction.Copy, dwgX.ProposedAction);
        Assert.True(plan.IsExecutable);
        // Both mechanisms' edges are preserved for graph visibility - having
        // TWO edges for the same relationship does not double-count toward
        // the owner-consensus verdict (a HashSet, not a list, backs it).
        Assert.Equal(2, plan.Edges.Count(e => e.RelationshipKind == CadRelationshipKind.DrawingModel
            && e.ParentAbsolutePath.Equals(drawingXPath, StringComparison.OrdinalIgnoreCase)
            && e.ChildAbsolutePath.Equals(modelAPath, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- 31. BLOCKER 2: association authority type/shape validation ------

    [Theory]
    [InlineData(CadDocumentType.Iam, true)]
    [InlineData(CadDocumentType.Ipt, true)]
    [InlineData(CadDocumentType.Idw, false)]
    [InlineData(CadDocumentType.Dwg, false)]
    [InlineData(CadDocumentType.Unknown, false)]
    public void Drawing_association_query_scope_is_gated_by_document_type(CadDocumentType type, bool expectedQueried)
    {
        var childPath = P("Design", "CHILD.xyz");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, childPath, "cad_child", "fv_child1", type: type) });
        var spy = new SpyDrawingSource(DrawingAssociationResult.NotAvailable);

        Plan(scan, drawings: spy);

        Assert.Equal(expectedQueried, spy.QueriedIds.Contains("cad_child"));
    }

    [Fact]
    public void The_selected_root_is_queried_when_it_is_an_IAM()
    {
        var spy = new SpyDrawingSource(DrawingAssociationResult.NotAvailable);

        Plan(Scan(Root(RootPath), Array.Empty<CadReference>()), drawings: spy);

        Assert.Contains("cad_root", spy.QueriedIds);
    }

    [Fact]
    public void The_selected_root_is_NOT_queried_when_it_is_an_IDW()
    {
        var spy = new SpyDrawingSource(DrawingAssociationResult.NotAvailable);

        Plan(Scan(Root(RootPath, type: CadDocumentType.Idw), Array.Empty<CadReference>()), drawings: spy);

        Assert.DoesNotContain("cad_root", spy.QueriedIds);
    }

    [Theory]
    [InlineData(CadDocumentType.Idw, true)]
    [InlineData(CadDocumentType.Dwg, true)]
    [InlineData(CadDocumentType.Ipt, false)]
    [InlineData(CadDocumentType.Iam, false)]
    [InlineData(CadDocumentType.Unknown, false)]
    public void Associated_drawing_type_validation_accepts_only_IDW_or_DWG(CadDocumentType claimedType, bool expectedAccepted)
    {
        var partPath = P("Design", "10073-P001.ipt");
        var drawingPath = P("Design", "SOMETHING.ext");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_p1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, claimedType, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        if (expectedAccepted)
        {
            Assert.Contains(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
            Assert.True(plan.IsExecutable);
        }
        else
        {
            Assert.DoesNotContain(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
            Assert.False(plan.IsExecutable);
            Assert.Contains(plan.Warnings, w => w.Contains("unsupported document type", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_relative_path_is_rejected_as_resolved_evidence()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_p1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", "relative\\path\\SHEET.idw", CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.False(dwg.IsResolved);
        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("not a valid, fully-qualified absolute path", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Edges, e => e.RelationshipKind == CadRelationshipKind.DrawingModel
            && e.ParentAbsolutePath.Contains("relative", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_blank_path_is_treated_as_honestly_unresolved_with_NO_warning()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_p1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", null, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.False(dwg.IsResolved);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("not a valid, fully-qualified absolute path", StringComparison.Ordinal));
    }

    [Fact]
    public void A_malformed_absolute_path_is_rejected()
    {
        // An "absolute-looking" but syntactically invalid path (an embedded
        // NUL character) must never be treated as resolved.
        var partPath = P("Design", "10073-P001.ipt");
        var malformedPath = P("Design") + "\\Bad\0Path\\SHEET.idw";
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_p1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", malformedPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.False(dwg.IsResolved);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_valid_absolute_path_is_accepted_as_resolved()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var drawingPath = P("Design", "SHEET.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_p1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.True(dwg.IsResolved);
        Assert.Equal(CopyDesignAction.Copy, dwg.ProposedAction);
        Assert.True(plan.IsExecutable);
    }

    [Fact]
    public void Every_emitted_DrawingModel_edge_has_an_IDW_or_DWG_parent_and_an_IAM_or_IPT_child()
    {
        var modelAPath = P("Design", "A.ipt");
        var modelBPath = P("Design", "B.iam");
        var drawingBPath = P("Design", "B.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwgB", "fv_dwgB1", drawingBPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath, type: CadDocumentType.Idw), new[]
        {
            // Scanner-native: root (IDW) directly references model A.
            Managed(RootPath, modelAPath, "cad_a", "fv_a1", kind: CadRelationshipKind.DrawingModel),
            // Ordinary link so model B's cadDocumentId enters the plan for
            // the association lookup (NOT itself a DrawingModel edge).
            Managed(RootPath, modelBPath, "cad_b", "fv_b1", CadDocumentType.Iam, kind: CadRelationshipKind.Other),
        });

        var plan = Plan(scan, drawings: drawingSource);

        var drawingModelEdges = plan.Edges.Where(e => e.RelationshipKind == CadRelationshipKind.DrawingModel).ToArray();
        Assert.NotEmpty(drawingModelEdges);
        foreach (var edge in drawingModelEdges)
        {
            var parentNode = Assert.Single(plan.Nodes,
                n => n.SourceAbsolutePath.Equals(edge.ParentAbsolutePath, StringComparison.OrdinalIgnoreCase));
            var childNode = Assert.Single(plan.Nodes,
                n => n.SourceAbsolutePath.Equals(edge.ChildAbsolutePath, StringComparison.OrdinalIgnoreCase));
            Assert.True(parentNode.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg);
            Assert.True(childNode.DocumentType is CadDocumentType.Iam or CadDocumentType.Ipt);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Malformed_authoritative_association_evidence_makes_the_plan_non_executable_regardless_of_order(bool reversed)
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var badDrawingPath = P("Design", "BAD.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", badDrawingPath, CadDocumentType.Ipt /* invalid */, IsVerified: true) }),
        });
        var a = Managed(RootPath, partAPath, "cad_a", "fv_a1");
        var b = Managed(RootPath, partBPath, "cad_b", "fv_b1");
        var scan = Scan(Root(RootPath), reversed ? new[] { b, a } : new[] { a, b });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("unsupported document type", StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_association_evidence_produces_the_EXACT_SAME_plan_regardless_of_order()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var badDrawingPath = P("Design", "BAD.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", badDrawingPath, CadDocumentType.Ipt, IsVerified: true) }),
        });
        var a = Managed(RootPath, partAPath, "cad_a", "fv_a1");
        var b = Managed(RootPath, partBPath, "cad_b", "fv_b1");
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var planNormal = Plan(Scan(Root(RootPath), new[] { a, b }), classification: classification, drawings: drawingSource);
        var planReversed = Plan(Scan(Root(RootPath), new[] { b, a }), classification: classification, drawings: drawingSource);

        AssertPlansAreIdentical(planNormal, planReversed);
    }

    // ==================================================================
    // ROUND 6 - FINAL DRAWING TAINT + COVERAGE FIX
    // ==================================================================

    // ---- 32. HIGH: a rejected association claim taints that drawing id ---

    [Fact]
    public void An_invalid_type_claim_for_the_SAME_drawing_id_taints_it_even_with_a_valid_COPY_owner()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var validDrawingPath = P("Design", "SHARED.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", validDrawingPath, CadDocumentType.Idw, IsVerified: true) }),
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", validDrawingPath, CadDocumentType.Ipt /* invalid */, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific, // -> Copy
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        // ONE reconciled drawing node - the rejected claim never created a
        // second node, but it MUST still taint "cad_dwg".
        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, dwg.ProposedAction);
        Assert.Contains(dwg.Reasons, r => r.Contains("could not be validated", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void An_invalid_type_claim_for_the_SAME_drawing_id_taints_it_even_with_a_valid_REUSE_owner()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var validDrawingPath = P("Design", "SHARED.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", validDrawingPath, CadDocumentType.Idw, IsVerified: true) }),
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", validDrawingPath, CadDocumentType.Ipt /* invalid */, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.LibraryOrShared, // -> Reuse
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, dwg.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_drawing_ROOT_with_a_valid_scanner_owner_plus_a_rejected_claim_for_ITSELF_is_NeedsDecision_not_Copy()
    {
        // The taint MUST override root-Copy: root IS the drawing, has a real
        // valid scanner-native owner (Copy), but a DIFFERENT model's
        // association query also names the ROOT's own cadDocumentId with
        // invalid evidence - the taint must win over both the valid owner
        // AND the ordinary "selected root -> Copy" fallback.
        var rootIdentity = new PlmIdentity("cad_root", null, "fv_root1");
        var modelAPath = P("Design", "A.ipt");
        var modelBPath = P("Design", "B.ipt");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_root", "fv_root_BAD", RootPath, CadDocumentType.Ipt /* invalid */, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath, type: CadDocumentType.Idw), new[]
        {
            Managed(RootPath, modelAPath, "cad_a", "fv_a1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity),
            Managed(RootPath, modelBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal(CopyDesignAction.NeedsDecision, root.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void Drawing_taint_produces_the_EXACT_SAME_plan_regardless_of_owner_query_order()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var validDrawingPath = P("Design", "SHARED.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", validDrawingPath, CadDocumentType.Idw, IsVerified: true) }),
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", validDrawingPath, CadDocumentType.Ipt, IsVerified: true) }),
        });
        var a = Managed(RootPath, partAPath, "cad_a", "fv_a1");
        var b = Managed(RootPath, partBPath, "cad_b", "fv_b1");
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var planNormal = Plan(Scan(Root(RootPath), new[] { a, b }), classification: classification, drawings: drawingSource);
        var planReversed = Plan(Scan(Root(RootPath), new[] { b, a }), classification: classification, drawings: drawingSource);

        AssertPlansAreIdentical(planNormal, planReversed);
        Assert.Equal(CopyDesignPlanTextReport.Render(planNormal), CopyDesignPlanTextReport.Render(planReversed));
    }

    [Fact]
    public void A_rejected_claim_for_a_DIFFERENT_drawing_id_does_not_taint_an_unrelated_valid_drawing()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var validDrawingPath = P("Design", "SHARED.idw");
        var otherDrawingPath = P("Design", "OTHER.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", validDrawingPath, CadDocumentType.Idw, IsVerified: true) }),
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_other_dwg", "fv_other1", otherDrawingPath, CadDocumentType.Ipt /* invalid */, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.Copy, dwg.ProposedAction); // NOT tainted - unrelated identity
        Assert.DoesNotContain(plan.Nodes, n => n.CadDocumentId == "cad_other_dwg"); // the invalid one never becomes a node
        // The plan is STILL non-executable overall because of the unrelated
        // rejected evidence - but "cad_dwg" itself is not falsely tainted.
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void An_invalid_path_claim_for_the_SAME_drawing_id_taints_it_even_with_a_valid_COPY_owner()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var validDrawingPath = P("Design", "SHARED.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", validDrawingPath, CadDocumentType.Idw, IsVerified: true) }),
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", "relative\\path\\SHEET.idw", CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, dwg.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_drawing_ROOT_with_a_valid_scanner_owner_plus_a_rejected_PATH_claim_for_ITSELF_is_NeedsDecision()
    {
        var rootIdentity = new PlmIdentity("cad_root", null, "fv_root1");
        var modelAPath = P("Design", "A.ipt");
        var modelBPath = P("Design", "B.ipt");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_root", "fv_root1", "relative\\BAD.idw", CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath, type: CadDocumentType.Idw), new[]
        {
            Managed(RootPath, modelAPath, "cad_a", "fv_a1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity),
            Managed(RootPath, modelBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal(CopyDesignAction.NeedsDecision, root.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void A_rejected_claim_with_NO_usable_cadDocumentId_fails_closed_globally_without_tainting_any_existing_drawing()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var validDrawingPath = P("Design", "SHARED.idw");
        var noIdDrawingPath = P("Design", "NOID.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", validDrawingPath, CadDocumentType.Idw, IsVerified: true) }),
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing(null, null, noIdDrawingPath, CadDocumentType.Ipt /* invalid, no id */, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.Copy, dwg.ProposedAction); // NOT tainted - nothing usable to attribute it to
        Assert.False(plan.IsExecutable); // still fails closed globally via the warning
    }

    // ---- 33. MEDIUM: zero-owner drawing root + owner-forced drawing destination

    [Fact]
    public void A_standalone_verified_drawing_root_with_NO_owners_and_NO_rejected_evidence_is_COPY_and_executable()
    {
        // Genuine zero owners (never discovered any model dependent, and no
        // association evidence was ever rejected) - distinct from a drawing
        // whose owner evidence became unusable.
        var scan = Scan(Root(RootPath, type: CadDocumentType.Idw), Array.Empty<CadReference>());

        var plan = Plan(scan);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal(CopyDesignAction.Copy, root.ProposedAction);
        Assert.True(plan.IsExecutable);
    }

    [Fact]
    public void A_standalone_drawing_root_with_an_INCOMPLETE_scan_is_NOT_executable()
    {
        var scan = Scan(
            Root(RootPath, type: CadDocumentType.Idw),
            Array.Empty<CadReference>(),
            nodes: new[] { new CadReferenceNode(RootPath, CadDocumentType.Idw, false) }); // NOT enumerated

        var plan = Plan(scan);

        Assert.False(plan.ScanWasComplete);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void An_owner_forced_COPY_drawing_receives_a_naming_rule_derived_destination()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var drawingPath = P("Design", "10073-P001.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_p1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.Copy, dwg.ProposedAction);
        Assert.Equal("10137-P001.idw", dwg.ProposedDestinationFileName);
        Assert.Equal(Path.Combine(DestRoot, "10137-P001.idw"), dwg.ProposedDestinationAbsolutePath);
        Assert.True(plan.IsExecutable);
    }

    [Fact]
    public void Two_owner_forced_COPY_drawings_mapping_to_the_SAME_destination_make_the_plan_NOT_executable()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var drawingAPath = P("Design", "DWG-A.idw");
        var drawingBPath = P("Design", "DWG-B.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg_a", "fv_1", drawingAPath, CadDocumentType.Idw, IsVerified: true) }),
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg_b", "fv_1", drawingBPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, rule: new FixedNameRule("SAME.idw"), classification: classification, drawings: drawingSource);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Duplicate proposed destination", StringComparison.Ordinal));
        var dwgA = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg_a");
        var dwgB = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg_b");
        Assert.Equal(CopyDesignAction.Copy, dwgA.ProposedAction);
        Assert.Equal(CopyDesignAction.Copy, dwgB.ProposedAction);
    }

    [Fact]
    public void An_owner_forced_COPY_drawing_is_rejected_by_the_injected_local_destinationExists_collision_check()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var drawingPath = P("Design", "10073-P001.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_p1"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", drawingPath, CadDocumentType.Idw, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource, destinationExists: _ => true);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("10137-P001.idw", StringComparison.Ordinal)
            && w.Contains("already exists", StringComparison.Ordinal));
    }

    // ==================================================================
    // ROUND 7 - PARTIAL DRAWING ASSOCIATION AUTHORITY MUST FAIL CLOSED
    // ==================================================================

    private sealed class ThrowingForIdDrawingSource(string throwingId, DrawingAssociationResult otherwise) : IDrawingAssociationSource
    {
        public DrawingAssociationResult GetAssociatedDrawings(string cadDocumentId) =>
            cadDocumentId == throwingId
                ? throw new InvalidOperationException("Simulated drawing-association authority failure.")
                : otherwise;
    }

    private sealed class NullReturningDrawingSource : IDrawingAssociationSource
    {
        public DrawingAssociationResult GetAssociatedDrawings(string cadDocumentId) => null!;
    }

    [Fact]
    public void ModelA_Found_ModelB_NotAvailable_makes_the_plan_non_executable_with_an_incomplete_authority_warning()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>()),
            ["cad_b"] = DrawingAssociationResult.NotAvailable,
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.LibraryOrShared,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing association authority was unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void ModelA_Found_ModelB_throws_makes_the_plan_non_executable()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var drawingSource = new ThrowingForIdDrawingSource(
            "cad_b", new DrawingAssociationResult(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>()));
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing association authority was unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void ModelA_NotAvailable_ModelB_Found_makes_the_plan_non_executable()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = DrawingAssociationResult.NotAvailable,
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>()),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.LibraryOrShared,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing association authority was unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void All_eligible_model_lookups_Found_keeps_drawing_association_complete()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>()),
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>()),
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>()),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        Assert.True(plan.IsExecutable);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("Drawing association authority was unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void All_eligible_model_lookups_NotAvailable_makes_the_plan_non_executable()
    {
        var partAPath = P("Design", "A.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partAPath, "cad_a", "fv_a1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: NoDrawingAssociationSource.Instance);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing association authority was unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void A_null_lookup_result_fails_closed()
    {
        var scan = Scan(Root(RootPath), Array.Empty<CadReference>());

        var plan = Plan(scan, drawings: new NullReturningDrawingSource());

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing association authority was unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void Incomplete_drawing_authority_produces_the_EXACT_SAME_plan_regardless_of_model_order()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>()),
            ["cad_b"] = DrawingAssociationResult.NotAvailable,
        });
        var a = Managed(RootPath, partAPath, "cad_a", "fv_a1");
        var b = Managed(RootPath, partBPath, "cad_b", "fv_b1");
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.LibraryOrShared,
        });

        var planNormal = Plan(Scan(Root(RootPath), new[] { a, b }), classification: classification, drawings: drawingSource);
        var planReversed = Plan(Scan(Root(RootPath), new[] { b, a }), classification: classification, drawings: drawingSource);

        AssertPlansAreIdentical(planNormal, planReversed);
        Assert.Equal(CopyDesignPlanTextReport.Render(planNormal), CopyDesignPlanTextReport.Render(planReversed));
    }

    [Fact]
    public void Scanner_native_drawing_nodes_are_never_described_as_absent_when_association_authority_is_unavailable()
    {
        var rootIdentity = new PlmIdentity("cad_root", null, "fv_root1");
        var modelAPath = P("Design", "A.ipt");
        var scan = Scan(Root(RootPath, type: CadDocumentType.Idw), new[]
        {
            Managed(RootPath, modelAPath, "cad_a", "fv_a1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
        });

        // "cad_a" IS queried (Iam/Ipt-eligible) but the source knows nothing.
        var plan = Plan(scan, classification: classification, drawings: NoDrawingAssociationSource.Instance);

        Assert.Contains(plan.Nodes, n => n.DocumentType is CadDocumentType.Idw); // the root drawing itself
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("no drawings are represented in this plan", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing association authority was unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void Round_6_taint_behavior_remains_intact_alongside_the_NEW_completeness_check()
    {
        var partAPath = P("Design", "A.ipt");
        var partBPath = P("Design", "B.ipt");
        var validDrawingPath = P("Design", "SHARED.idw");
        var drawingSource = new StubDrawingSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", validDrawingPath, CadDocumentType.Idw, IsVerified: true) }),
            ["cad_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found,
                new[] { new AssociatedDrawing("cad_dwg", "fv_dwg1", validDrawingPath, CadDocumentType.Ipt /* invalid */, IsVerified: true) }),
        });
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: drawingSource);

        var dwg = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_dwg");
        Assert.Equal(CopyDesignAction.NeedsDecision, dwg.ProposedAction);
        Assert.Contains(dwg.Reasons, r => r.Contains("could not be validated", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);
        // Both cad_a and cad_b answered Found (even though cad_b's evidence
        // was malformed) - completeness itself is NOT violated here; the
        // failure is purely the Round 6 taint mechanism, distinct from the
        // NEW Round 7 completeness mechanism.
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("Drawing association authority was unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void Completeness_is_determined_purely_by_lookup_outcome_never_by_filename_or_path_similarity()
    {
        // A model file deliberately named to LOOK like a drawing does not
        // change query scope, taint, or completeness - only its ACTUAL
        // DocumentType and the association source's ACTUAL outcome matter.
        var suggestivelyNamedModelPath = P("Design", "LOOKS-LIKE-A-DRAWING.idw");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, suggestivelyNamedModelPath, "cad_a", "fv_a1", type: CadDocumentType.Ipt /* actually a PART */),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: NoDrawingAssociationSource.Instance);

        var part = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_a");
        Assert.Equal(CadDocumentType.Ipt, part.DocumentType); // never reclassified by its file name
        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing association authority was unavailable", StringComparison.Ordinal));
    }

    // ======================================================================
    // Round 6 (P6C manual acceptance follow-up): explicit COPY/REUSE/EXCLUDE
    // decisions for a NeedsDecision node whose ONLY blocker is an unavailable
    // library/shared classification signal - see CopyDesignExplicitDecisions.cs
    // for the shared eligibility/validation vocabulary, and
    // CopyDesignExplicitDecisionsTests.cs for its own dedicated tests.
    // ======================================================================

    // ---- Round 6, item 1: unresolved NeedsDecision => NOT EXECUTABLE ----

    [Fact]
    public void Round6_An_unresolved_classification_NeedsDecision_node_leaves_the_plan_NOT_executable()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });

        var plan = Plan(scan); // no classification source, no explicit decisions

        var part = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_p1");
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction);
        Assert.True(CopyDesignExplicitDecisionEligibility.IsEligibleForExplicitDecision(part));
        Assert.False(plan.IsExecutable);
    }

    // ---- Round 6, items 2, 9, 10, 11, 12: explicit COPY resolves
    //      NeedsDecision, and P4C -> P6C is deterministic for the real
    //      acceptance fixture's ROOT/PART-A/PART-B ----------------------

    [Fact]
    public void Round6_Explicit_COPY_decisions_resolve_all_ACCEPT_nodes_to_the_deterministic_P6C_destinations()
    {
        var rootPath = P("Design", "P4C-REAL-ROOT.iam");
        var partAPath = P("Design", "P4C-REAL-PART-A.ipt");
        var partBPath = P("Design", "P4C-REAL-PART-B.ipt");
        var scan = Scan(Root(rootPath), new[]
        {
            Managed(rootPath, partAPath, "cad_a", "fv_a1"),
            Managed(rootPath, partBPath, "cad_b", "fv_b1"),
        });
        var tokenRule = new TokenReplaceNameRule("P4C", "P6C");
        var explicitDecisions = new Dictionary<string, CopyDesignAction>
        {
            ["cad_a"] = CopyDesignAction.Copy,
            ["cad_b"] = CopyDesignAction.Copy,
        };

        var plan = Plan(scan, rule: tokenRule, explicitDecisions: explicitDecisions);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        var a = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_a");
        var b = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_b");
        Assert.Equal(CopyDesignAction.Copy, root.ProposedAction); // the root is always auto-COPY, never a decision
        Assert.Equal("P6C-REAL-ROOT.iam", root.ProposedDestinationFileName);
        Assert.Equal(CopyDesignAction.Copy, a.ProposedAction);
        Assert.Equal("P6C-REAL-PART-A.ipt", a.ProposedDestinationFileName);
        Assert.Contains(a.Reasons, r => r.Contains("Explicit engineer decision: Copy", StringComparison.Ordinal));
        Assert.Equal(CopyDesignAction.Copy, b.ProposedAction);
        Assert.Equal("P6C-REAL-PART-B.ipt", b.ProposedDestinationFileName);
        Assert.True(plan.IsExecutable);
    }

    // ---- Round 6, item 3: explicit REUSE resolves per existing safe
    //      semantics (identity preserved, no destination, no new CadDocument) -

    [Fact]
    public void Round6_Explicit_REUSE_decision_resolves_NeedsDecision_and_retains_the_existing_stable_identity_with_no_destination()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });

        var plan = Plan(scan, explicitDecisions: new Dictionary<string, CopyDesignAction> { ["cad_p1"] = CopyDesignAction.Reuse });

        var part = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_p1");
        Assert.Equal(CopyDesignAction.Reuse, part.ProposedAction);
        Assert.Equal("cad_p1", part.CadDocumentId); // the SAME stable identity - never a new CadDocument
        Assert.Equal("fv_p1", part.CurrentFileVersionId);
        Assert.Null(part.ProposedDestinationFileName);
        Assert.Null(part.ProposedDestinationAbsolutePath);
        Assert.True(plan.IsExecutable);
        var edge = Assert.Single(plan.Edges, e => e.ChildAbsolutePath == partPath);
        Assert.Equal(CopyDesignEdgeDisposition.RemainsOnReusedSource, edge.Disposition);
    }

    // ---- Round 6, item 4: explicit EXCLUDE resolves ONLY when safe ------

    [Fact]
    public void Round6_Explicit_EXCLUDE_resolves_the_NeedsDecision_itself_but_the_plan_stays_NOT_executable_while_a_surviving_parent_still_depends_on_it()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });

        var plan = Plan(scan, explicitDecisions: new Dictionary<string, CopyDesignAction> { ["cad_p1"] = CopyDesignAction.Exclude });

        var part = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_p1");
        // The decision itself was consumed - the node is no longer NeedsDecision...
        Assert.Equal(CopyDesignAction.Exclude, part.ProposedAction);
        Assert.Contains(part.Reasons, r => r.Contains("Explicit engineer decision: Exclude", StringComparison.Ordinal));
        // ...but the root's surviving (COPY) reference into it can never be
        // safely resolved, so reference-safety keeps the WHOLE plan blocked -
        // EXCLUDE is never "made to work" by weakening this rule.
        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Edges, e => e.ChildAbsolutePath == partPath
            && e.Disposition == CopyDesignEdgeDisposition.UnresolvedOrUnsafe);
        Assert.Contains(plan.Warnings, w => w.Contains("cannot be safely resolved", StringComparison.Ordinal));
    }

    // ---- Round 6, item 5: "Suggested: COPY" is never silently applied ---

    [Fact]
    public void Round6_Suggested_COPY_text_is_never_silently_applied_even_when_explicit_decisions_exist_for_OTHER_nodes()
    {
        var partAPath = P("Design", "10073-A.ipt");
        var partBPath = P("Design", "10073-B.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });

        // Only cad_a has an explicit decision - cad_b has none at all.
        var plan = Plan(scan, explicitDecisions: new Dictionary<string, CopyDesignAction> { ["cad_a"] = CopyDesignAction.Copy });

        var a = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_a");
        var b = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_b");
        Assert.Equal(CopyDesignAction.Copy, a.ProposedAction);
        Assert.Equal(CopyDesignAction.NeedsDecision, b.ProposedAction); // untouched
        Assert.Contains(b.Reasons, r => r.Contains("Suggested: COPY", StringComparison.Ordinal));
        Assert.False(plan.IsExecutable);
    }

    // ---- Round 6, item 13: a collision among EXPLICITLY decided nodes
    //      still leaves the plan NOT executable --------------------------

    [Fact]
    public void Round6_Two_explicitly_COPY_decided_nodes_mapping_to_the_same_destination_still_make_the_plan_NOT_executable()
    {
        var partA = P("Design", "A.ipt");
        var partB = P("Design", "B.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partA, "cad_a", "fv_a1"),
            Managed(RootPath, partB, "cad_b", "fv_b1"),
        });
        var decisions = new Dictionary<string, CopyDesignAction>
        {
            ["cad_a"] = CopyDesignAction.Copy,
            ["cad_b"] = CopyDesignAction.Copy,
        };

        var plan = Plan(scan, rule: new FixedNameRule("SAME.ipt"), explicitDecisions: decisions);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Duplicate proposed destination", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Round 6, item 14: a throwing destinationExists remains the
    //      pre-existing "best-effort, never a crash" fail-safe behavior ---

    [Fact]
    public void Round6_A_destinationExists_callback_that_throws_does_not_crash_planning()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var decisions = new Dictionary<string, CopyDesignAction> { ["cad_p1"] = CopyDesignAction.Copy };

        // Regression lock: a throwing destinationExists must never crash
        // Plan() or abort the whole computation - this pre-existing
        // "best-effort, never a guarantee" contract (see the planner's own
        // comment above anyLocalCollision) is unchanged by Round 6. A real
        // collision is still separately caught, fail-closed, by the offline
        // reservation/materialize path at Apply time.
        var plan = Plan(scan, explicitDecisions: decisions, destinationExists: _ => throw new InvalidOperationException("boom"));

        var part = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_p1");
        Assert.Equal(CopyDesignAction.Copy, part.ProposedAction);
        Assert.True(plan.IsExecutable);
    }

    // ---- Round 6, item 15: recomputation while editing decisions never
    //      touches the filesystem - Plan() stays a pure function ----------

    [Fact]
    public void Round6_Recomputing_a_plan_repeatedly_while_editing_decisions_never_touches_the_filesystem()
    {
        var tempDir = Directory.CreateTempSubdirectory("copydesign-round6-");
        try
        {
            var partPath = P("Design", "10073-P001.ipt");
            var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });

            // Recompute several times, exactly as the real UI workflow does
            // while the engineer edits decisions, targeting a REAL (empty)
            // directory as the destination workspace root.
            _ = CopyDesignPlanner.Plan(scan, TokenRule, tempDir.FullName, null, AlwaysFoundEmptyDrawingSource.Instance, null, null);
            _ = CopyDesignPlanner.Plan(scan, TokenRule, tempDir.FullName, null, AlwaysFoundEmptyDrawingSource.Instance, null,
                new Dictionary<string, CopyDesignAction> { ["cad_p1"] = CopyDesignAction.Copy });
            _ = CopyDesignPlanner.Plan(scan, TokenRule, tempDir.FullName, null, AlwaysFoundEmptyDrawingSource.Instance, null,
                new Dictionary<string, CopyDesignAction> { ["cad_p1"] = CopyDesignAction.Reuse });
            _ = CopyDesignPlanner.Plan(scan, TokenRule, tempDir.FullName, null, AlwaysFoundEmptyDrawingSource.Instance, null,
                new Dictionary<string, CopyDesignAction> { ["cad_p1"] = CopyDesignAction.Exclude });

            Assert.Empty(Directory.EnumerateFileSystemEntries(tempDir.FullName));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // ---- Round 6, item 18: decisions bind to stable cadDocumentId, never
    //      to display row, order, or path ---------------------------------

    [Fact]
    public void Round6_Explicit_decisions_bind_to_stable_cadDocumentId_not_to_row_order_or_path()
    {
        var partAPath = P("Design", "10073-A.ipt");
        var partBPath = P("Design", "10073-B.ipt");
        var decisions = new Dictionary<string, CopyDesignAction>
        {
            ["cad_a"] = CopyDesignAction.Copy,
            ["cad_b"] = CopyDesignAction.Reuse,
        };

        var scanNormal = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var scanReversed = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
        });

        var planNormal = Plan(scanNormal, explicitDecisions: decisions);
        var planReversed = Plan(scanReversed, explicitDecisions: decisions);

        AssertPlansAreEquivalent(planNormal, planReversed);
        Assert.Equal(CopyDesignAction.Copy, Assert.Single(planNormal.Nodes, n => n.CadDocumentId == "cad_a").ProposedAction);
        Assert.Equal(CopyDesignAction.Reuse, Assert.Single(planNormal.Nodes, n => n.CadDocumentId == "cad_b").ProposedAction);
    }

    // ---- Round 6, item 19: a decision for one node can never leak onto
    //      another node, even the only OTHER NeedsDecision node present ---

    [Fact]
    public void Round6_A_decision_keyed_to_an_UNRELATED_cadDocumentId_never_leaks_onto_the_actual_NeedsDecision_node()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });

        // A stale/unrelated id (e.g. left over from a previous scan) must be
        // ignored - it must NOT fall back to "the only NeedsDecision node".
        var plan = Plan(scan, explicitDecisions: new Dictionary<string, CopyDesignAction> { ["cad_UNRELATED"] = CopyDesignAction.Copy });

        var part = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_p1");
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    // ---- Round 6, item 20: repeated recomputation with the same inputs
    //      is deterministic -------------------------------------------------

    [Fact]
    public void Round6_Repeated_recomputation_with_the_same_inputs_and_explicit_decisions_is_deterministic()
    {
        var partAPath = P("Design", "10073-A.ipt");
        var partBPath = P("Design", "10073-B.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partAPath, "cad_a", "fv_a1"),
            Managed(RootPath, partBPath, "cad_b", "fv_b1"),
        });
        var decisions = new Dictionary<string, CopyDesignAction>
        {
            ["cad_a"] = CopyDesignAction.Copy,
            ["cad_b"] = CopyDesignAction.Exclude,
        };

        var plan1 = Plan(scan, explicitDecisions: decisions);
        var plan2 = Plan(scan, explicitDecisions: decisions);

        AssertPlansAreEquivalent(plan1, plan2);
    }

    // ======================================================================
    // Round 8 (P6C acceptance follow-up): explicit "model files only"
    // acknowledgement - the ONLY supported way to waive the drawing-
    // association-INCOMPLETE blocker for an otherwise-safe IAM/IPT plan. The
    // DEFAULT (acknowledgeModelFilesOnly omitted/false) remains exactly the
    // pre-Round-8 safe behavior everywhere else in this file.
    // ======================================================================

    /// <summary>Returns NotAvailable for ONE specific cadDocumentId (proving
    ///  GLOBAL incompleteness) and Found-empty for everything else.</summary>
    private sealed class NotAvailableForOneDrawingSource(string unavailableForCadDocumentId) : IDrawingAssociationSource
    {
        public DrawingAssociationResult GetAssociatedDrawings(string cadDocumentId) =>
            cadDocumentId == unavailableForCadDocumentId
                ? DrawingAssociationResult.NotAvailable
                : new DrawingAssociationResult(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>());
    }

    // ---- Round 8, item 1: unavailable authority + no acknowledgement =>
    //      NOT EXECUTABLE (the pre-existing, unchanged default) ------------

    [Fact]
    public void Round8_Drawing_authority_unavailable_with_NO_acknowledgement_leaves_the_plan_NOT_executable()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: NoDrawingAssociationSource.Instance);

        Assert.False(plan.IsExecutable);
        Assert.False(plan.ModelFilesOnlyAcknowledged);
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing association authority was unavailable", StringComparison.Ordinal));
    }

    // ---- Round 8, item 2: same plan + explicit acknowledgement => executable
    //      when that is the ONLY remaining blocker -------------------------

    [Fact]
    public void Round8_The_SAME_plan_with_explicit_model_only_acknowledgement_becomes_executable()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: NoDrawingAssociationSource.Instance,
            acknowledgeModelFilesOnly: true);

        Assert.True(plan.IsExecutable);
        Assert.True(plan.ModelFilesOnlyAcknowledged);
    }

    // ---- Round 8, item 3: the drawing-unavailable warning REMAINS visible -

    [Fact]
    public void Round8_The_drawing_authority_warning_remains_visible_even_once_acknowledged()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: NoDrawingAssociationSource.Instance,
            acknowledgeModelFilesOnly: true);

        Assert.True(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing association authority was unavailable", StringComparison.Ordinal));
    }

    // ---- Round 8, item 4: the final plan explicitly identifies MODEL FILES
    //      ONLY (both structured flag and warning text) -----------------

    [Fact]
    public void Round8_The_acknowledged_plan_explicitly_identifies_MODEL_FILES_ONLY_and_DRAWINGS_NOT_INCLUDED()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, classification: classification, drawings: NoDrawingAssociationSource.Instance,
            acknowledgeModelFilesOnly: true);

        Assert.True(plan.ModelFilesOnlyAcknowledged);
        Assert.Contains(plan.Warnings, w => w.Contains("MODE: MODEL FILES ONLY", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("DRAWINGS: NOT INCLUDED", StringComparison.Ordinal));
    }

    // (Item 5 - the mandatory Apply confirmation repeating this fact - is a
    // WinForms UI concern with no test harness in this repo; see
    // CopyDesignApplyConfirmDialog's own "Round 8" comment, and
    // CopyDesignPreviewZeroMutationSourceTests for this repo's established
    // source-level-guard pattern for untestable WinForms code.)

    // ---- Round 8, item 6: acknowledgement does NOT resolve NeedsDecision --

    [Fact]
    public void Round8_Acknowledgement_does_NOT_resolve_a_classification_NeedsDecision_node()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });

        // No classification source (Unknown) AND no explicit decision.
        var plan = Plan(scan, drawings: NoDrawingAssociationSource.Instance, acknowledgeModelFilesOnly: true);

        var part = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_p1");
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    // ---- Round 8, item 7: acknowledgement does NOT bypass a destination
    //      collision --------------------------------------------------------

    [Fact]
    public void Round8_Acknowledgement_does_NOT_bypass_a_duplicate_destination_collision()
    {
        var partA = P("Design", "A.ipt");
        var partB = P("Design", "B.ipt");
        var scan = Scan(Root(RootPath), new[]
        {
            Managed(RootPath, partA, "cad_a", "fv_a1"),
            Managed(RootPath, partB, "cad_b", "fv_b1"),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });

        var plan = Plan(scan, rule: new FixedNameRule("SAME.ipt"), classification: classification,
            drawings: NoDrawingAssociationSource.Instance, acknowledgeModelFilesOnly: true);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Duplicate proposed destination", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Round 8, item 8: acknowledgement does NOT bypass an unsafe
    //      (UnresolvedOrUnsafe) dependency edge ------------------------------

    [Fact]
    public void Round8_Acknowledgement_does_NOT_bypass_an_unresolved_or_unsafe_dependency_edge()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });

        var plan = Plan(scan, drawings: NoDrawingAssociationSource.Instance, acknowledgeModelFilesOnly: true,
            explicitDecisions: new Dictionary<string, CopyDesignAction> { ["cad_p1"] = CopyDesignAction.Exclude });

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Edges, e => e.ChildAbsolutePath == partPath
            && e.Disposition == CopyDesignEdgeDisposition.UnresolvedOrUnsafe);
    }

    // ---- Round 8, item 9: acknowledgement does NOT bypass a missing stable
    //      identity (unmanaged reference) ------------------------------------

    [Fact]
    public void Round8_Acknowledgement_does_NOT_bypass_a_missing_stable_identity()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Unmanaged(RootPath, partPath) });

        var plan = Plan(scan, drawings: NoDrawingAssociationSource.Instance, acknowledgeModelFilesOnly: true);

        var part = Assert.Single(plan.Nodes, n => !n.IsRoot);
        Assert.Null(part.CadDocumentId);
        Assert.Equal(CopyDesignAction.NeedsDecision, part.ProposedAction);
        Assert.False(plan.IsExecutable);
    }

    // ---- Round 8, item 10: a discovered drawing node is never silently
    //      dropped by model-only mode ----------------------------------------

    [Fact]
    public void Round8_A_scanner_discovered_drawing_node_is_never_dropped_when_model_only_mode_makes_the_plan_executable()
    {
        var rootIdentity = new PlmIdentity("cad_root", null, "fv_root1");
        var modelAPath = P("Design", "A.ipt");
        var modelBPath = P("Design", "B.ipt");
        var scan = Scan(Root(RootPath, type: CadDocumentType.Idw), new[]
        {
            Managed(RootPath, modelAPath, "cad_a", "fv_a1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity),
            Managed(RootPath, modelBPath, "cad_b", "fv_b1", kind: CadRelationshipKind.DrawingModel, parentIdentity: rootIdentity),
        });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_a"] = ComponentClassification.ProjectSpecific,
            ["cad_b"] = ComponentClassification.ProjectSpecific,
        });
        var drawingSource = new NotAvailableForOneDrawingSource("cad_a");

        var planBlocked = Plan(scan, classification: classification, drawings: drawingSource);
        Assert.False(planBlocked.IsExecutable);
        var rootBlocked = Assert.Single(planBlocked.Nodes, n => n.IsRoot);
        Assert.Equal(CadDocumentType.Idw, rootBlocked.DocumentType);
        Assert.Equal(CopyDesignAction.Copy, rootBlocked.ProposedAction); // decided already, not dropped, even while blocked

        var plan = Plan(scan, classification: classification, drawings: drawingSource, acknowledgeModelFilesOnly: true);
        Assert.True(plan.IsExecutable);
        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        Assert.Equal(CadDocumentType.Idw, root.DocumentType); // still present - never silently dropped/excluded
        Assert.Equal(CopyDesignAction.Copy, root.ProposedAction);
    }

    // ---- Round 8, item 11: preview/recomputation remains zero mutation ----

    [Fact]
    public void Round8_Recomputing_with_model_only_acknowledgement_never_touches_the_filesystem()
    {
        var tempDir = Directory.CreateTempSubdirectory("copydesign-round8-");
        try
        {
            var partPath = P("Design", "10073-P001.ipt");
            var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
            var classification = new StubClassification(new Dictionary<string, ComponentClassification>
            {
                ["cad_p1"] = ComponentClassification.ProjectSpecific,
            });

            _ = CopyDesignPlanner.Plan(scan, TokenRule, tempDir.FullName, classification, NoDrawingAssociationSource.Instance);
            _ = CopyDesignPlanner.Plan(scan, TokenRule, tempDir.FullName, classification, NoDrawingAssociationSource.Instance,
                acknowledgeModelFilesOnly: true);

            Assert.Empty(Directory.EnumerateFileSystemEntries(tempDir.FullName));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // ---- Round 8, item 12: repeated recomputation with the same inputs is
    //      deterministic -----------------------------------------------------

    [Fact]
    public void Round8_Repeated_recomputation_with_the_same_model_only_acknowledgement_is_deterministic()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        var plan1 = Plan(scan, classification: classification, drawings: NoDrawingAssociationSource.Instance,
            acknowledgeModelFilesOnly: true);
        var plan2 = Plan(scan, classification: classification, drawings: NoDrawingAssociationSource.Instance,
            acknowledgeModelFilesOnly: true);

        AssertPlansAreEquivalent(plan1, plan2);
    }

    // ---- Round 8, item 13: existing P6A safety behavior remains DEFAULT ---

    [Fact]
    public void Round8_Omitting_the_acknowledgement_parameter_entirely_preserves_the_pre_Round8_default_behavior()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });
        var classification = new StubClassification(new Dictionary<string, ComponentClassification>
        {
            ["cad_p1"] = ComponentClassification.ProjectSpecific,
        });

        // acknowledgeModelFilesOnly is not passed at all.
        var plan = CopyDesignPlanner.Plan(scan, TokenRule, DestRoot, classification, NoDrawingAssociationSource.Instance);

        Assert.False(plan.IsExecutable);
        Assert.False(plan.ModelFilesOnlyAcknowledged);
    }

    // ---- Round 8, item 14: the full ACCEPT fixture (ROOT/PART-A/PART-B)
    //      becomes executable under explicit model-only mode -----------------

    [Fact]
    public void Round8_The_full_ACCEPT_fixture_ROOT_A_B_becomes_executable_under_explicit_model_only_mode()
    {
        var rootPath = P("Design", "P4C-REAL-ROOT.iam");
        var partAPath = P("Design", "P4C-REAL-PART-A.ipt");
        var partBPath = P("Design", "P4C-REAL-PART-B.ipt");
        var scan = Scan(Root(rootPath), new[]
        {
            Managed(rootPath, partAPath, "cad_a", "fv_a1"),
            Managed(rootPath, partBPath, "cad_b", "fv_b1"),
        });
        var tokenRule = new TokenReplaceNameRule("P4C", "P6C");
        var explicitDecisions = new Dictionary<string, CopyDesignAction>
        {
            ["cad_a"] = CopyDesignAction.Copy,
            ["cad_b"] = CopyDesignAction.Copy,
        };

        // No drawing association authority at all - exactly the real
        // acceptance observation ("plan remains NOT EXECUTABLE solely
        // because drawing association authority is unavailable").
        var plan = Plan(scan, rule: tokenRule, explicitDecisions: explicitDecisions,
            drawings: NoDrawingAssociationSource.Instance, acknowledgeModelFilesOnly: true);

        var root = Assert.Single(plan.Nodes, n => n.IsRoot);
        var a = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_a");
        var b = Assert.Single(plan.Nodes, n => n.CadDocumentId == "cad_b");
        Assert.Equal(CopyDesignAction.Copy, root.ProposedAction);
        Assert.Equal("P6C-REAL-ROOT.iam", root.ProposedDestinationFileName);
        Assert.Equal(CopyDesignAction.Copy, a.ProposedAction);
        Assert.Equal("P6C-REAL-PART-A.ipt", a.ProposedDestinationFileName);
        Assert.Equal(CopyDesignAction.Copy, b.ProposedAction);
        Assert.Equal("P6C-REAL-PART-B.ipt", b.ProposedDestinationFileName);
        Assert.True(plan.IsExecutable);
        Assert.True(plan.ModelFilesOnlyAcknowledged);
        Assert.Contains(plan.Warnings, w => w.Contains("MODE: MODEL FILES ONLY", StringComparison.Ordinal));
    }
}
