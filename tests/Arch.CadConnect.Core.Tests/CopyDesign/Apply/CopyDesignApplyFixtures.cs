using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

/// <summary>Shared, minimal fixture builders for P6C apply tests - deliberately
///  independent of <c>CopyDesignFixtures</c> (P6A's own planner fixtures) so
///  every field an apply-time test cares about is explicit and obvious at the
///  call site.</summary>
internal static class CopyDesignApplyFixtures
{
    public static CopyDesignNode CopyNode(
        string cadDocumentId,
        string sourcePath,
        string destPath,
        CadDocumentType type = CadDocumentType.Ipt,
        string fileVersionId = "fv-1",
        bool isRoot = false) => new(
        CadDocumentId: cadDocumentId,
        CurrentFileVersionId: fileVersionId,
        DocumentType: type,
        SourceAbsolutePath: sourcePath,
        SourceFileName: Path.GetFileName(sourcePath),
        RelationshipToParent: isRoot ? null : CadRelationshipKind.Component,
        IsManaged: true,
        IsVerified: true,
        IsResolved: true,
        ProposedAction: CopyDesignAction.Copy,
        ProposedDestinationFileName: Path.GetFileName(destPath),
        ProposedDestinationAbsolutePath: destPath,
        Reasons: Array.Empty<string>(),
        IsRoot: isRoot);

    public static CopyDesignNode ReuseNode(
        string cadDocumentId, string sourcePath, CadDocumentType type = CadDocumentType.Ipt, string fileVersionId = "fv-1") => new(
        CadDocumentId: cadDocumentId,
        CurrentFileVersionId: fileVersionId,
        DocumentType: type,
        SourceAbsolutePath: sourcePath,
        SourceFileName: Path.GetFileName(sourcePath),
        RelationshipToParent: CadRelationshipKind.Component,
        IsManaged: true,
        IsVerified: true,
        IsResolved: true,
        ProposedAction: CopyDesignAction.Reuse,
        ProposedDestinationFileName: null,
        ProposedDestinationAbsolutePath: null,
        Reasons: Array.Empty<string>());

    public static CopyDesignEdge ComponentEdge(CopyDesignNode parent, CopyDesignNode child, bool childIsCopy) => new(
        parent.SourceAbsolutePath, child.SourceAbsolutePath, CadRelationshipKind.Component,
        childIsCopy ? CopyDesignEdgeDisposition.PointsToNewCopy : CopyDesignEdgeDisposition.RemainsOnReusedSource);

    /// <summary>P6D: a DRAWING -&gt; MODEL edge - <paramref name="drawing"/> is
    ///  always the PARENT, <paramref name="model"/> always the CHILD (see
    ///  <see cref="CadRelationshipKind.DrawingModel"/>'s own doc comment).</summary>
    public static CopyDesignEdge DrawingModelEdge(CopyDesignNode drawing, CopyDesignNode model, bool modelIsCopy) => new(
        drawing.SourceAbsolutePath, model.SourceAbsolutePath, CadRelationshipKind.DrawingModel,
        modelIsCopy ? CopyDesignEdgeDisposition.PointsToNewCopy : CopyDesignEdgeDisposition.RemainsOnReusedSource);

    public static CopyDesignPlan Plan(
        IReadOnlyList<CopyDesignNode> nodes,
        IReadOnlyList<CopyDesignEdge>? edges = null,
        bool isExecutable = true,
        string root = @"C:\root\root.iam") => new(
        RootAbsolutePath: root,
        Nodes: nodes,
        Edges: edges ?? Array.Empty<CopyDesignEdge>(),
        Warnings: Array.Empty<string>(),
        IsExecutable: isExecutable,
        ScanWasComplete: true,
        DrawingAssociationAvailable: true,
        ModelFilesOnlyAcknowledged: false);
}
