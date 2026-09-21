using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;

using static Arch.CadConnect.Core.Tests.CopyDesign.CopyDesignFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

/// <summary>P6C Round 6, items 16 and 17: the FULL round trip from a real
///  <see cref="CopyDesignPlanner.Plan"/> output - not a hand-built fixture
///  node - through <see cref="CopyDesignApplyRequestMapper.Map"/>, proving
///  Apply cannot run against a plan with an unresolved decision, and CAN run
///  once every decision has been explicitly resolved.</summary>
public class CopyDesignExplicitDecisionApplyIntegrationTests
{
    private static readonly string RootPath = P("Design", "10073-AS210.iam");
    private static readonly string DestRoot = P("Dest");
    private static readonly TokenReplaceNameRule TokenRule = new("10073", "10137");

    private sealed class AlwaysFoundEmptyDrawingSource : IDrawingAssociationSource
    {
        public static readonly AlwaysFoundEmptyDrawingSource Instance = new();

        public DrawingAssociationResult GetAssociatedDrawings(string cadDocumentId) =>
            new(DrawingAssociationOutcome.Found, Array.Empty<AssociatedDrawing>());
    }

    // ---- item 16: Apply cannot run with unresolved nodes -----------------

    [Fact]
    public void A_real_plan_with_an_unresolved_NeedsDecision_node_cannot_be_mapped_for_Apply()
    {
        var partPath = P("Design", "10073-P001.ipt");
        var scan = Scan(Root(RootPath), new[] { Managed(RootPath, partPath, "cad_p1", "fv_p1") });

        var plan = CopyDesignPlanner.Plan(scan, TokenRule, DestRoot, null, AlwaysFoundEmptyDrawingSource.Instance);

        Assert.False(plan.IsExecutable);
        var result = CopyDesignApplyRequestMapper.Map(plan, "key-round6-1", null);

        Assert.False(result.Success);
        Assert.Contains("not executable", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- item 17: Apply can consume a fully resolved executable plan -----

    [Fact]
    public void A_real_plan_fully_resolved_by_explicit_decisions_becomes_executable_and_maps_cleanly_for_Apply()
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
            ["cad_b"] = CopyDesignAction.Reuse,
        };

        var plan = CopyDesignPlanner.Plan(scan, TokenRule, DestRoot, null, AlwaysFoundEmptyDrawingSource.Instance, null, decisions);

        Assert.True(plan.IsExecutable);
        var result = CopyDesignApplyRequestMapper.Map(plan, "key-round6-2", null);

        Assert.True(result.Success);
        Assert.Equal(3, result.Request!.Entries.Count); // root + cad_a (Copy) + cad_b (Reuse)
        Assert.Contains(result.Request.Entries, e => e.Entry.Action == CopyDesignApplyEntryAction.Copy && e.Entry.Copy!.SourceCadDocumentId == "cad_a");
        Assert.Contains(result.Request.Entries, e => e.Entry.Action == CopyDesignApplyEntryAction.Reuse && e.Entry.Reuse!.CadDocumentId == "cad_b");
    }
}
