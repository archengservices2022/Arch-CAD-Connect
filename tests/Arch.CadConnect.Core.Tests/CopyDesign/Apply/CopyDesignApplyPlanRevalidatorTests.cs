using Arch.CadConnect.Core.CopyDesign.Apply;

using static Arch.CadConnect.Core.Tests.CopyDesign.Apply.CopyDesignApplyFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

public class CopyDesignApplyPlanRevalidatorTests
{
    // Deliberately built WITHOUT going through CopyDesignApplyRequestMapper.Map:
    // this suite tests the REVALIDATOR in isolation, including scenarios
    // (e.g. two entries sharing one destination PATH) that the mapper's own
    // duplicate-document-number check would now also reject before a request
    // could even be built - the revalidator must still independently defend
    // the same invariant for any request it is ever handed.
    private static CopyDesignApplyRequest RequestFor(params Core.CopyDesign.CopyDesignNode[] nodes)
    {
        var entries = nodes.Select(n => new CopyDesignApplyMappedEntry(
            n,
            new CopyDesignApplyEntry(
                CopyDesignApplyEntryAction.Copy,
                new CopyDesignApplyCopyEntry(
                    n.CadDocumentId!, n.CurrentFileVersionId!, "doc-" + n.CadDocumentId, n.ProposedDestinationFileName!,
                    n.DocumentType, Description: null),
                Reuse: null))).ToArray();
        return new CopyDesignApplyRequest("key-1", null, entries);
    }

    // 10. destination already exists => fail before mutation
    [Fact]
    public void ExistingDestinationFailsClosed()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var request = RequestFor(node);

        var result = CopyDesignApplyPlanRevalidator.Revalidate(request, sourceExists: _ => true, destinationExists: _ => true);

        Assert.False(result.Success);
        Assert.Contains("already exists", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // 11. destination existence callback throws => fail closed
    [Fact]
    public void DestinationExistenceCheckThrowingFailsClosed()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var request = RequestFor(node);

        var result = CopyDesignApplyPlanRevalidator.Revalidate(
            request, sourceExists: _ => true, destinationExists: _ => throw new IOException("disk unavailable"));

        Assert.False(result.Success);
    }

    [Fact]
    public void SourceExistenceCheckThrowingFailsClosed()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var request = RequestFor(node);

        var result = CopyDesignApplyPlanRevalidator.Revalidate(
            request, sourceExists: _ => throw new UnauthorizedAccessException(), destinationExists: _ => false);

        Assert.False(result.Success);
    }

    // 12. source missing => fail before mutation
    [Fact]
    public void MissingSourceFailsClosed()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var request = RequestFor(node);

        var result = CopyDesignApplyPlanRevalidator.Revalidate(request, sourceExists: _ => false, destinationExists: _ => false);

        Assert.False(result.Success);
        Assert.Contains("no longer exists", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // 13. source == destination => reject
    [Fact]
    public void SourceEqualToDestinationIsRejected()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\src\a.ipt");
        var request = RequestFor(node);

        var result = CopyDesignApplyPlanRevalidator.Revalidate(request, sourceExists: _ => true, destinationExists: _ => false);

        Assert.False(result.Success);
        Assert.Contains("identical", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // 14. duplicate destination => reject
    [Fact]
    public void TwoEntriesProposingTheSameDestinationAreRejected()
    {
        var a = CopyNode("cad-a", @"C:\src\a.ipt", @"C:\dst\same.ipt");
        var b = CopyNode("cad-b", @"C:\src\b.ipt", @"C:\dst\same.ipt");
        var request = RequestFor(a, b);

        var result = CopyDesignApplyPlanRevalidator.Revalidate(request, sourceExists: _ => true, destinationExists: _ => false);

        Assert.False(result.Success);
        Assert.Contains("same destination", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EverythingClearPasses()
    {
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var request = RequestFor(node);

        var result = CopyDesignApplyPlanRevalidator.Revalidate(request, sourceExists: _ => true, destinationExists: _ => false);

        Assert.True(result.Success);
    }
}
