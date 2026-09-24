using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.CopyDesign.CopyDesignFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign;

/// <summary>
/// P6D LIVE ACCEPTANCE BLOCKER - DRAWING ASSOCIATION AUTHORITY NOT AVAILABLE:
/// end-to-end proof, at the planner boundary, that the fix closes the loop.
///
/// ROOT CAUSE (see this round's report): <c>ArchAddInController.RunCopyDesignPreview</c>
/// never supplied a REAL <see cref="IDrawingAssociationSource"/> to
/// <c>CopyDesignPlanner.Plan</c> - it always defaulted to
/// <see cref="NoDrawingAssociationSource"/>, which unconditionally reports
/// <see cref="DrawingAssociationOutcome.NotAvailable"/>, even though the
/// server's <c>DRAWING_REFERENCE</c> row genuinely existed. These tests do
/// NOT touch <c>CopyDesignPlanner.cs</c> itself (unchanged, and its own
/// 3000+ line test suite already proves its reconciliation/completeness
/// logic) - they prove the NEW pieces (<see cref="PrefetchedDrawingAssociationSource"/>,
/// fed from a dictionary shaped exactly like the real HTTP response) make
/// that already-correct logic actually SEE the authority.
/// </summary>
public class CopyDesignPlannerP6DDrawingAuthorityTests
{
    private static readonly string IdwPath = P("Design", "P6D-REAL-ROOT.idw");
    private static readonly string IamPath = P("Design", "P6D-REAL-ROOT.iam");
    private static readonly string PartAPath = P("Design", "P6D-REAL-PART-A.ipt");
    private static readonly string PartBPath = P("Design", "P6D-REAL-PART-B.ipt");
    private static readonly string DestRoot = P("Dest");
    private static readonly IDestinationNameRule TokenRule = new TokenReplaceNameRule("P6D-REAL", "P6DNEW-REAL");

    /// <summary>Every model in this round's fixture is project-specific (a
    ///  COPY candidate) - this test file is about drawing-association
    ///  authority, not component classification, so classification is never
    ///  left "Unknown" here (that is its own, unrelated NeedsDecision path,
    ///  already covered by CopyDesignPlannerTests.cs).</summary>
    private sealed class AllProjectSpecific : IComponentClassificationSource
    {
        public static readonly AllProjectSpecific Instance = new();
        public ComponentClassification Classify(string cadDocumentId) => ComponentClassification.ProjectSpecific;
    }

    /// <summary>The EXACT P6D live-acceptance shape: a scan rooted at the
    ///  IDW, referencing its IAM model (DrawingModel), which in turn
    ///  references two IPT components - mirrors the fixture's authoritative
    ///  dependencies verbatim.</summary>
    private static CadReferenceScan P6dScan() => Scan(
        Root(IdwPath, cadDocumentId: "cad_root_idw", fv: "fv_idw_1", type: CadDocumentType.Idw),
        new[]
        {
            Managed(IdwPath, IamPath, "cad_root_iam", "fv_iam_1",
                type: CadDocumentType.Iam, kind: CadRelationshipKind.DrawingModel),
            Managed(IamPath, PartAPath, "cad_part_a", "fv_a1",
                type: CadDocumentType.Ipt, kind: CadRelationshipKind.Component,
                parentIdentity: new PlmIdentity("cad_root_iam", null, "fv_iam_1")),
            Managed(IamPath, PartBPath, "cad_part_b", "fv_b1",
                type: CadDocumentType.Ipt, kind: CadRelationshipKind.Component,
                parentIdentity: new PlmIdentity("cad_root_iam", null, "fv_iam_1")),
        });

    /// <summary>Exactly what <c>HttpDrawingAssociationClient</c> would build
    ///  from the REAL server response captured against the live P6D fixture:
    ///  the IAM's only drawing is the IDW; the parts have none.</summary>
    private static PrefetchedDrawingAssociationSource RealP6dAuthority() =>
        new(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root_iam"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, new[]
            {
                new AssociatedDrawing(
                    CadDocumentId: "cad_root_idw",
                    CurrentFileVersionId: "fv_idw_1",
                    AbsolutePath: IdwPath,
                    DocumentType: CadDocumentType.Idw,
                    IsVerified: true,
                    AssociationEvidence: "Arch server DRAWING_REFERENCE"),
            }),
            ["cad_part_a"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>()),
            ["cad_part_b"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>()),
        });

    [Fact]
    public void REGRESSION_the_old_stub_reproduces_the_live_blocker_NOT_AVAILABLE_and_non_executable()
    {
        var plan = CopyDesignPlanner.Plan(
            P6dScan(), TokenRule, DestRoot, drawingSource: NoDrawingAssociationSource.Instance);

        Assert.False(plan.DrawingAssociationAvailable);
        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing association authority was unavailable"));
    }

    [Fact]
    public void FIX_the_real_authority_confirms_the_association_DrawingAssociationAvailable_true()
    {
        var plan = CopyDesignPlanner.Plan(
            P6dScan(), TokenRule, DestRoot,
            classificationSource: AllProjectSpecific.Instance, drawingSource: RealP6dAuthority());

        Assert.True(plan.DrawingAssociationAvailable);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("Drawing association authority was unavailable"));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("did not confirm any drawings"));
    }

    [Fact]
    public void FIX_the_full_four_node_plan_becomes_executable()
    {
        var plan = CopyDesignPlanner.Plan(
            P6dScan(), TokenRule, DestRoot,
            classificationSource: AllProjectSpecific.Instance, drawingSource: RealP6dAuthority());

        Assert.Equal(4, plan.Nodes.Count);
        Assert.True(plan.IsExecutable);
        Assert.False(plan.ModelFilesOnlyAcknowledged);
    }

    [Fact]
    public void FIX_the_IDW_node_is_proposed_as_the_root_IDW_copy_and_every_node_copies()
    {
        var plan = CopyDesignPlanner.Plan(
            P6dScan(), TokenRule, DestRoot,
            classificationSource: AllProjectSpecific.Instance, drawingSource: RealP6dAuthority());

        var idwNode = Assert.Single(plan.Nodes, n => n.SourceAbsolutePath == IdwPath);
        Assert.Equal(CopyDesignAction.Copy, idwNode.ProposedAction);
        Assert.All(plan.Nodes, n => Assert.Equal(CopyDesignAction.Copy, n.ProposedAction));
    }

    [Fact]
    public void An_id_the_prefetch_never_asked_about_still_fails_closed_never_Found()
    {
        // Proves PrefetchedDrawingAssociationSource's own contract: a model
        // id missing from the dictionary (a caller bug, or a candidate the
        // extraction missed) is NEVER silently "Found, zero drawings".
        var partial = new PrefetchedDrawingAssociationSource(new Dictionary<string, DrawingAssociationResult>
        {
            ["cad_root_iam"] = new DrawingAssociationResult(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>()),
            // cad_part_a / cad_part_b deliberately omitted.
        });

        var plan = CopyDesignPlanner.Plan(P6dScan(), TokenRule, DestRoot, drawingSource: partial);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("Drawing association authority was unavailable"));
    }

    [Fact]
    public void Existing_P6C_model_files_only_acknowledgement_still_waives_ONLY_the_completeness_blocker_when_authority_is_unavailable()
    {
        // Unchanged pre-existing behavior (P6C): even with the OLD stub, the
        // engineer's explicit acknowledgement still makes the plan
        // executable, proving this fix did not alter that escape hatch.
        var plan = CopyDesignPlanner.Plan(
            P6dScan(), TokenRule, DestRoot,
            classificationSource: AllProjectSpecific.Instance,
            drawingSource: NoDrawingAssociationSource.Instance,
            acknowledgeModelFilesOnly: true);

        Assert.True(plan.IsExecutable);
        Assert.True(plan.ModelFilesOnlyAcknowledged);
        Assert.Contains(plan.Warnings, w => w.Contains("MODE: MODEL FILES ONLY"));
    }
}
