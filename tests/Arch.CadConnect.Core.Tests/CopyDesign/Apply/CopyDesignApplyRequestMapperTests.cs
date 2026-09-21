using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.CopyDesign.Apply.CopyDesignApplyFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

public class CopyDesignApplyRequestMapperTests
{
    // 1. non-executable plan cannot apply
    [Fact]
    public void NonExecutablePlanCannotBeMapped()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node }, isExecutable: false);

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
        Assert.Contains("not executable", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // 3. COPY maps to P6B COPY request correctly
    [Fact]
    public void CopyNodeMapsToCopyEntryWithExactFields()
    {
        var node = CopyNode("cad-1", @"C:\src\10073-P001.ipt", @"C:\dst\10137-P001.ipt", fileVersionId: "fv-9");
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(result.Success);
        var entry = Assert.Single(result.Request!.Entries);
        Assert.Equal(CopyDesignApplyEntryAction.Copy, entry.Entry.Action);
        Assert.Equal("cad-1", entry.Entry.Copy!.SourceCadDocumentId);
        Assert.Equal("fv-9", entry.Entry.Copy.SourceFileVersionId);
        Assert.Equal("10137-P001", entry.Entry.Copy.NewDocumentNumber);
        Assert.Equal("10137-P001.ipt", entry.Entry.Copy.NewFileName);
        Assert.Equal(CadDocumentType.Ipt, entry.Entry.Copy.DocumentType);
    }

    // 4. REUSE maps correctly
    [Fact]
    public void ReuseNodeMapsToReuseEntry()
    {
        var node = ReuseNode("cad-2", @"C:\src\std-bolt.ipt");
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(result.Success);
        var entry = Assert.Single(result.Request!.Entries);
        Assert.Equal(CopyDesignApplyEntryAction.Reuse, entry.Entry.Action);
        Assert.Equal("cad-2", entry.Entry.Reuse!.CadDocumentId);
        Assert.Null(entry.Entry.Copy);
    }

    // 30. no drawing type can execute in P6C
    [Fact]
    public void DrawingNodesAreNeverMappedRegardlessOfProposedAction()
    {
        var copyIpt = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var drawing = new CopyDesignNode(
            "cad-drawing", "fv-1", CadDocumentType.Idw, @"C:\src\a.idw", "a.idw",
            CadRelationshipKind.DrawingModel, true, true, true,
            CopyDesignAction.Copy, "b.idw", @"C:\dst\b.idw", Array.Empty<string>());
        var plan = Plan(new[] { copyIpt, drawing });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.True(result.Success);
        Assert.Single(result.Request!.Entries);
        Assert.DoesNotContain(result.Request.Entries, e => e.Node.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg);
    }

    [Fact]
    public void OnlyDrawingsInPlanMeansNothingToApply()
    {
        var drawing = new CopyDesignNode(
            "cad-drawing", "fv-1", CadDocumentType.Idw, @"C:\src\a.idw", "a.idw",
            null, true, true, true, CopyDesignAction.Copy, "b.idw", @"C:\dst\b.idw", Array.Empty<string>(), IsRoot: true);
        var plan = Plan(new[] { drawing });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
        Assert.Contains("no in-scope", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NeedsDecisionNodeFailsClosedEvenOnANominallyExecutablePlan()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt") with { ProposedAction = CopyDesignAction.NeedsDecision };
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
    }

    [Fact]
    public void ExcludeNodeFailsClosed()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt") with { ProposedAction = CopyDesignAction.Exclude };
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
    }

    [Fact]
    public void CopyNodeMissingStableIdentityFailsClosed()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt") with { CurrentFileVersionId = null };
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "key-1", null);

        Assert.False(result.Success);
    }

    [Fact]
    public void BlankIdempotencyKeyIsRejected()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node });

        var result = CopyDesignApplyRequestMapper.Map(plan, "  ", null);

        Assert.False(result.Success);
    }
}
