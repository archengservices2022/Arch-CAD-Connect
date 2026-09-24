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

    // ======================================================================
    // P6D: CadRelationshipKind.DrawingModel - the SAME resolver, selecting a
    // DIFFERENT edge kind. The drawing is always the PARENT, the model
    // always the CHILD (see CadRelationshipKind.DrawingModel's own doc
    // comment) - every fail-closed rule below is identical to the Component
    // case, just exercised through the drawing edge kind.
    // ======================================================================

    // required test list item 5: IDW -> IAM relationship maps correctly.
    [Fact]
    public void DrawingModelKind_IdwToIam_CopyModelMapsToItsCopiedDestination()
    {
        var model = CopyNode("cad-model", @"C:\src\root.iam", @"C:\dst\root-new.iam", CadDocumentType.Iam);
        var drawing = CopyNode("cad-drawing", @"C:\src\root.idw", @"C:\dst\root-new.idw", CadDocumentType.Idw);
        var edge = DrawingModelEdge(drawing, model, modelIsCopy: true);

        var result = CopyDesignReferenceTargetResolver.Resolve(
            drawing, new[] { edge }, new[] { drawing, model }, CadRelationshipKind.DrawingModel);

        Assert.True(result.Success);
        var target = Assert.Single(result.Targets);
        Assert.True(target.ChildIsCopy);
        Assert.Equal(@"C:\src\root.iam", target.OriginalChildAbsolutePath);
        Assert.Equal(@"C:\dst\root-new.iam", target.ExpectedTargetAbsolutePath);
    }

    // required test list item 6: DWG -> IPT relationship maps correctly.
    [Fact]
    public void DrawingModelKind_DwgToIpt_ReuseModelMapsToItsOriginalUnchangedPath()
    {
        var model = ReuseNode("cad-model", @"C:\src\std-part.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.dwg", @"C:\dst\a-new.dwg", CadDocumentType.Dwg);
        var edge = DrawingModelEdge(drawing, model, modelIsCopy: false);

        var result = CopyDesignReferenceTargetResolver.Resolve(
            drawing, new[] { edge }, new[] { drawing, model }, CadRelationshipKind.DrawingModel);

        Assert.True(result.Success);
        var target = Assert.Single(result.Targets);
        Assert.False(target.ChildIsCopy);
        Assert.Equal(target.OriginalChildAbsolutePath, target.ExpectedTargetAbsolutePath);
    }

    // required test list item 7: multiple model owners are ALL retained.
    [Fact]
    public void DrawingModelKind_MultipleModelOwnersAreAllRetained()
    {
        var modelA = CopyNode("cad-model-a", @"C:\src\part-a.ipt", @"C:\dst\part-a-new.ipt");
        var modelB = ReuseNode("cad-model-b", @"C:\src\std-b.ipt");
        var modelC = CopyNode("cad-model-c", @"C:\src\part-c.ipt", @"C:\dst\part-c-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\root.idw", @"C:\dst\root-new.idw", CadDocumentType.Idw);
        var edges = new[]
        {
            DrawingModelEdge(drawing, modelA, modelIsCopy: true),
            DrawingModelEdge(drawing, modelB, modelIsCopy: false),
            DrawingModelEdge(drawing, modelC, modelIsCopy: true),
        };

        var result = CopyDesignReferenceTargetResolver.Resolve(
            drawing, edges, new[] { drawing, modelA, modelB, modelC }, CadRelationshipKind.DrawingModel);

        Assert.True(result.Success);
        Assert.Equal(3, result.Targets.Count);
        Assert.Equal(new[] { "cad-model-a", "cad-model-b", "cad-model-c" },
            result.Targets.Select(t => t.ChildCadDocumentId).OrderBy(x => x, StringComparer.Ordinal));
    }

    // required test list item 8: duplicate drawing/model evidence (the SAME
    // model referenced by more than one view/sheet, producing more than one
    // edge with the SAME child) dedupes safely rather than being treated as
    // a conflict or duplicated in the target set.
    [Fact]
    public void DrawingModelKind_DuplicateEdgesToTheSameModelDedupeSafely()
    {
        var model = CopyNode("cad-model", @"C:\src\part.ipt", @"C:\dst\part-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\root.idw", @"C:\dst\root-new.idw", CadDocumentType.Idw);
        // Two sheets/views of the drawing both reference the SAME model -
        // the scanner/association source may legitimately report this as
        // two edges with identical (parent, child, disposition).
        var edges = new[]
        {
            DrawingModelEdge(drawing, model, modelIsCopy: true),
            DrawingModelEdge(drawing, model, modelIsCopy: true),
        };

        var result = CopyDesignReferenceTargetResolver.Resolve(
            drawing, edges, new[] { drawing, model }, CadRelationshipKind.DrawingModel);

        Assert.True(result.Success);
        var target = Assert.Single(result.Targets); // deduped to ONE target, never two
        Assert.Equal("cad-model", target.ChildCadDocumentId);
    }

    // A Component-kind edge from a drawing (structurally shouldn't occur,
    // but never trusted blindly) is never picked up when resolving
    // DrawingModel - the two relationship kinds are fully independent.
    [Fact]
    public void DrawingModelKind_IgnoresAComponentKindEdgeFromTheSameParent()
    {
        var model = CopyNode("cad-model", @"C:\src\part.ipt", @"C:\dst\part-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\root.idw", @"C:\dst\root-new.idw", CadDocumentType.Idw);
        var wrongKindEdge = ComponentEdge(drawing, model, childIsCopy: true);

        var result = CopyDesignReferenceTargetResolver.Resolve(
            drawing, new[] { wrongKindEdge }, new[] { drawing, model }, CadRelationshipKind.DrawingModel);

        Assert.True(result.Success);
        Assert.Empty(result.Targets);
    }

    [Fact]
    public void DrawingModelKind_UnresolvedOrUnsafeDispositionFailsClosed()
    {
        var model = CopyNode("cad-model", @"C:\src\part.ipt", @"C:\dst\part.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\root.idw", @"C:\dst\root-new.idw", CadDocumentType.Idw);
        var edge = new CopyDesignEdge(drawing.SourceAbsolutePath, model.SourceAbsolutePath, CadRelationshipKind.DrawingModel,
            CopyDesignEdgeDisposition.UnresolvedOrUnsafe);

        var result = CopyDesignReferenceTargetResolver.Resolve(
            drawing, new[] { edge }, new[] { drawing, model }, CadRelationshipKind.DrawingModel);

        Assert.False(result.Success);
    }

    [Fact]
    public void DrawingModelKind_ExcludedModelFailsClosed()
    {
        var model = CopyNode("cad-model", @"C:\src\part.ipt", @"C:\dst\part.ipt") with { ProposedAction = CopyDesignAction.Exclude };
        var drawing = CopyNode("cad-drawing", @"C:\src\root.idw", @"C:\dst\root-new.idw", CadDocumentType.Idw);
        var edge = DrawingModelEdge(drawing, model, modelIsCopy: true);

        var result = CopyDesignReferenceTargetResolver.Resolve(
            drawing, new[] { edge }, new[] { drawing, model }, CadRelationshipKind.DrawingModel);

        Assert.False(result.Success);
    }
}
