using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.CopyDesign.Apply.CopyDesignApplyFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

public class CopyDesignReferenceTargetResolverTests
{
    // 16. COPY child maps to copied destination
    [Fact]
    public void CopyChildMapsToItsOwnCopiedDestination()
    {
        var child = CopyNode("cad-child", @"C:\src\child.ipt", @"C:\dst\child-new.ipt");
        var parent = CopyNode("cad-parent", @"C:\src\parent.iam", @"C:\dst\parent-new.iam", CadDocumentType.Iam);
        var edge = ComponentEdge(parent, child, childIsCopy: true);

        var result = CopyDesignReferenceTargetResolver.Resolve(parent, new[] { edge }, new[] { parent, child });

        Assert.True(result.Success);
        var target = Assert.Single(result.Targets);
        Assert.True(target.ChildIsCopy);
        Assert.Equal(@"C:\src\child.ipt", target.OriginalChildAbsolutePath);
        Assert.Equal(@"C:\dst\child-new.ipt", target.ExpectedTargetAbsolutePath);
    }

    // 17. REUSE child maps to original path
    [Fact]
    public void ReuseChildMapsToItsOriginalUnchangedPath()
    {
        var child = ReuseNode("cad-child", @"C:\src\std-bolt.ipt");
        var parent = CopyNode("cad-parent", @"C:\src\parent.iam", @"C:\dst\parent-new.iam", CadDocumentType.Iam);
        var edge = ComponentEdge(parent, child, childIsCopy: false);

        var result = CopyDesignReferenceTargetResolver.Resolve(parent, new[] { edge }, new[] { parent, child });

        Assert.True(result.Success);
        var target = Assert.Single(result.Targets);
        Assert.False(target.ChildIsCopy);
        Assert.Equal(@"C:\src\std-bolt.ipt", target.ExpectedTargetAbsolutePath);
        Assert.Equal(target.OriginalChildAbsolutePath, target.ExpectedTargetAbsolutePath);
    }

    // 18. ambiguous mapping fails / EXCLUDE child fails closed
    [Fact]
    public void ExcludedChildFailsClosed()
    {
        var child = CopyNode("cad-child", @"C:\src\child.ipt", @"C:\dst\child.ipt") with { ProposedAction = CopyDesignAction.Exclude };
        var parent = CopyNode("cad-parent", @"C:\src\parent.iam", @"C:\dst\parent-new.iam", CadDocumentType.Iam);
        var edge = ComponentEdge(parent, child, childIsCopy: true);

        var result = CopyDesignReferenceTargetResolver.Resolve(parent, new[] { edge }, new[] { parent, child });

        Assert.False(result.Success);
    }

    [Fact]
    public void UnresolvedOrUnsafeDispositionFailsClosed()
    {
        var child = CopyNode("cad-child", @"C:\src\child.ipt", @"C:\dst\child.ipt");
        var parent = CopyNode("cad-parent", @"C:\src\parent.iam", @"C:\dst\parent-new.iam", CadDocumentType.Iam);
        var edge = new CopyDesignEdge(parent.SourceAbsolutePath, child.SourceAbsolutePath, CadRelationshipKind.Component,
            CopyDesignEdgeDisposition.UnresolvedOrUnsafe);

        var result = CopyDesignReferenceTargetResolver.Resolve(parent, new[] { edge }, new[] { parent, child });

        Assert.False(result.Success);
    }

    [Fact]
    public void ChildNotFoundInNodeSetFailsClosed()
    {
        var parent = CopyNode("cad-parent", @"C:\src\parent.iam", @"C:\dst\parent-new.iam", CadDocumentType.Iam);
        var edge = new CopyDesignEdge(parent.SourceAbsolutePath, @"C:\src\ghost.ipt", CadRelationshipKind.Component,
            CopyDesignEdgeDisposition.PointsToNewCopy);

        var result = CopyDesignReferenceTargetResolver.Resolve(parent, new[] { edge }, new[] { parent });

        Assert.False(result.Success);
    }

    // 19. basename-only coincidence is never accepted as identity
    [Fact]
    public void TwoDifferentIdentitiesWithTheSameFileNameAreNeverConflated()
    {
        // Two DIFFERENT children, in different folders, that happen to share
        // a bare file name - the resolver must key strictly by absolute path
        // + stable identity, never by name alone.
        var childA = CopyNode("cad-child-A", @"C:\src\folderA\part.ipt", @"C:\dst\folderA\part-new.ipt");
        var childB = ReuseNode("cad-child-B", @"C:\src\folderB\part.ipt");
        var parent = CopyNode("cad-parent", @"C:\src\parent.iam", @"C:\dst\parent-new.iam", CadDocumentType.Iam);
        var edges = new[]
        {
            ComponentEdge(parent, childA, childIsCopy: true),
            ComponentEdge(parent, childB, childIsCopy: false),
        };

        var result = CopyDesignReferenceTargetResolver.Resolve(parent, edges, new[] { parent, childA, childB });

        Assert.True(result.Success);
        Assert.Equal(2, result.Targets.Count);
        var targetA = result.Targets.Single(t => t.ChildCadDocumentId == "cad-child-A");
        var targetB = result.Targets.Single(t => t.ChildCadDocumentId == "cad-child-B");
        Assert.Equal(@"C:\dst\folderA\part-new.ipt", targetA.ExpectedTargetAbsolutePath);
        Assert.Equal(@"C:\src\folderB\part.ipt", targetB.ExpectedTargetAbsolutePath);
        Assert.NotEqual(targetA.ExpectedTargetAbsolutePath, targetB.ExpectedTargetAbsolutePath);
    }

    [Fact]
    public void ParentWithNoComponentEdgesResolvesToAnEmptyTargetSet()
    {
        var parent = CopyNode("cad-parent", @"C:\src\parent.iam", @"C:\dst\parent-new.iam", CadDocumentType.Iam);
        var result = CopyDesignReferenceTargetResolver.Resolve(parent, Array.Empty<CopyDesignEdge>(), new[] { parent });
        Assert.True(result.Success);
        Assert.Empty(result.Targets);
    }
}
