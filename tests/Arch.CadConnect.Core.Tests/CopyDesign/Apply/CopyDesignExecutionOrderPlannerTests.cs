using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.CopyDesign.Apply.CopyDesignApplyFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

public class CopyDesignExecutionOrderPlannerTests
{
    // 15. nested IAM execution ordering deterministic
    [Fact]
    public void ChildrenAlwaysComeBeforeTheirParents_NestedThreeLevels()
    {
        var leaf = CopyNode("cad-leaf", @"C:\src\leaf.ipt", @"C:\dst\leaf.ipt");
        var mid = CopyNode("cad-mid", @"C:\src\mid.iam", @"C:\dst\mid.iam", CadDocumentType.Iam);
        var top = CopyNode("cad-top", @"C:\src\top.iam", @"C:\dst\top.iam", CadDocumentType.Iam, isRoot: true);

        var edges = new[]
        {
            ComponentEdge(top, mid, childIsCopy: true),
            ComponentEdge(mid, leaf, childIsCopy: true),
        };

        var result = CopyDesignExecutionOrderPlanner.ComputeOrder(new[] { top, mid, leaf }, edges);

        Assert.True(result.Success);
        var order = result.Order.Select(n => n.CadDocumentId).ToArray();
        Assert.True(Array.IndexOf(order, "cad-leaf") < Array.IndexOf(order, "cad-mid"));
        Assert.True(Array.IndexOf(order, "cad-mid") < Array.IndexOf(order, "cad-top"));
    }

    [Fact]
    public void OrderIsDeterministicAcrossRepeatedCalls()
    {
        var leaf1 = CopyNode("cad-leaf-1", @"C:\src\l1.ipt", @"C:\dst\l1.ipt");
        var leaf2 = CopyNode("cad-leaf-2", @"C:\src\l2.ipt", @"C:\dst\l2.ipt");
        var top = CopyNode("cad-top", @"C:\src\top.iam", @"C:\dst\top.iam", CadDocumentType.Iam, isRoot: true);
        var edges = new[] { ComponentEdge(top, leaf1, true), ComponentEdge(top, leaf2, true) };

        var first = CopyDesignExecutionOrderPlanner.ComputeOrder(new[] { top, leaf1, leaf2 }, edges);
        var second = CopyDesignExecutionOrderPlanner.ComputeOrder(new[] { leaf2, top, leaf1 }, edges); // different input order

        Assert.Equal(
            first.Order.Select(n => n.CadDocumentId),
            second.Order.Select(n => n.CadDocumentId));
    }

    [Fact]
    public void ACycleFailsClosedRatherThanGuessingAnOrder()
    {
        var a = CopyNode("cad-a", @"C:\src\a.iam", @"C:\dst\a.iam", CadDocumentType.Iam);
        var b = CopyNode("cad-b", @"C:\src\b.iam", @"C:\dst\b.iam", CadDocumentType.Iam);
        // a -> b -> a (never happens in a real CAD graph, but the planner must not hang or guess).
        var edges = new[] { ComponentEdge(a, b, true), ComponentEdge(b, a, true) };

        var result = CopyDesignExecutionOrderPlanner.ComputeOrder(new[] { a, b }, edges);

        Assert.False(result.Success);
    }
}
