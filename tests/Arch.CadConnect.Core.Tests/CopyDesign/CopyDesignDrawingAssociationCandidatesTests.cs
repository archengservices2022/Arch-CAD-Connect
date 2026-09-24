using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.CopyDesign.CopyDesignFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign;

/// <summary>
/// P6D GET LATEST DRAWING ASSOCIATION AUTHORITY FIX: pure tests for
/// <see cref="CopyDesignDrawingAssociationCandidates"/> - the caller-side
/// derivation of WHICH model ids to prefetch a real drawing-association
/// authority for, before <c>CopyDesignPlanner.Plan</c> runs (its
/// <c>IDrawingAssociationSource</c> is synchronous).
/// </summary>
public class CopyDesignDrawingAssociationCandidatesTests
{
    private static readonly string IdwPath = P("Design", "P6D-REAL-ROOT.idw");
    private static readonly string IamPath = P("Design", "P6D-REAL-ROOT.iam");
    private static readonly string PartAPath = P("Design", "P6D-REAL-PART-A.ipt");
    private static readonly string PartBPath = P("Design", "P6D-REAL-PART-B.ipt");

    /// <summary>The EXACT P6D live-acceptance shape: a scan rooted at the
    ///  IDW, referencing its IAM model (DrawingModel), which in turn
    ///  references two IPT components.</summary>
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

    [Fact]
    public void P6D_scan_rooted_at_the_IDW_yields_exactly_the_three_model_candidate_ids()
    {
        var candidates = CopyDesignDrawingAssociationCandidates.From(P6dScan());

        Assert.Equal(new HashSet<string> { "cad_root_iam", "cad_part_a", "cad_part_b" }, candidates);
    }

    [Fact]
    public void The_drawing_itself_is_never_a_candidate_it_is_not_a_model()
    {
        var candidates = CopyDesignDrawingAssociationCandidates.From(P6dScan());

        Assert.DoesNotContain("cad_root_idw", candidates);
    }

    [Fact]
    public void A_scan_rooted_at_the_IAM_model_includes_the_root_itself_as_a_candidate()
    {
        var scan = Scan(
            Root(IamPath, cadDocumentId: "cad_root_iam", fv: "fv_iam_1", type: CadDocumentType.Iam),
            new[]
            {
                Managed(IamPath, PartAPath, "cad_part_a", "fv_a1"),
            });

        var candidates = CopyDesignDrawingAssociationCandidates.From(scan);

        Assert.Contains("cad_root_iam", candidates);
        Assert.Contains("cad_part_a", candidates);
    }

    [Fact]
    public void An_unmanaged_reference_with_no_manifest_identity_contributes_no_candidate()
    {
        var scan = Scan(
            Root(IamPath, cadDocumentId: "cad_root_iam", fv: "fv_iam_1", type: CadDocumentType.Iam),
            new[] { Unmanaged(IamPath, PartAPath) });

        var candidates = CopyDesignDrawingAssociationCandidates.From(scan);

        Assert.Equal(new HashSet<string> { "cad_root_iam" }, candidates);
    }

    [Fact]
    public void An_unresolved_reference_contributes_no_candidate()
    {
        var scan = Scan(
            Root(IamPath, cadDocumentId: "cad_root_iam", fv: "fv_iam_1", type: CadDocumentType.Iam),
            new[] { Unresolved(IamPath, "missing.ipt") });

        var candidates = CopyDesignDrawingAssociationCandidates.From(scan);

        Assert.Equal(new HashSet<string> { "cad_root_iam" }, candidates);
    }

    [Fact]
    public void An_unmanaged_root_no_manifest_identity_at_all_contributes_no_root_candidate()
    {
        var scan = Scan(Root(IamPath, cadDocumentId: null, type: CadDocumentType.Iam), Array.Empty<CadReference>());

        var candidates = CopyDesignDrawingAssociationCandidates.From(scan);

        Assert.Empty(candidates);
    }

    [Fact]
    public void A_reference_to_another_drawing_DWG_is_never_a_candidate_only_IAM_IPT_are_models()
    {
        var dwgPath = P("Design", "other.dwg");
        var scan = Scan(
            Root(IamPath, cadDocumentId: "cad_root_iam", fv: "fv_iam_1", type: CadDocumentType.Iam),
            new[]
            {
                Managed(IamPath, dwgPath, "cad_other_dwg", "fv_dwg1",
                    type: CadDocumentType.Dwg, kind: CadRelationshipKind.Other),
            });

        var candidates = CopyDesignDrawingAssociationCandidates.From(scan);

        Assert.DoesNotContain("cad_other_dwg", candidates);
    }

    [Fact]
    public void Duplicate_references_to_the_same_model_collapse_to_one_candidate()
    {
        var scan = Scan(
            Root(IamPath, cadDocumentId: "cad_root_iam", fv: "fv_iam_1", type: CadDocumentType.Iam),
            new[]
            {
                Managed(IamPath, PartAPath, "cad_part_a", "fv_a1"),
                Managed(IamPath, PartAPath, "cad_part_a", "fv_a1"),
            });

        var candidates = CopyDesignDrawingAssociationCandidates.From(scan);

        Assert.Equal(new HashSet<string> { "cad_root_iam", "cad_part_a" }, candidates);
    }

    [Fact]
    public void Candidates_are_never_derived_from_a_file_name_only_from_manifest_bound_stable_ids()
    {
        // Two references resolve to files with WILDLY different names than
        // their stable ids - proving the derivation never looks at path/name.
        var scan = Scan(
            Root(P("Design", "totally-different-name.iam"), cadDocumentId: "cad_root_iam", fv: "fv_iam_1", type: CadDocumentType.Iam),
            new[]
            {
                Managed(IamPath, P("Design", "renamed-part.ipt"), "cad_part_a", "fv_a1"),
            });

        var candidates = CopyDesignDrawingAssociationCandidates.From(scan);

        Assert.Equal(new HashSet<string> { "cad_root_iam", "cad_part_a" }, candidates);
    }
}
