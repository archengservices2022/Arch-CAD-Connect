using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.CopyDesign;

/// <summary>
/// ONE node of a Copy Design plan - one distinct managed (or unmanaged /
/// unresolved) document reachable from the selected root, deduplicated by
/// STABLE ARCH IDENTITY (never by path or file name - two nodes with the same
/// file name but different <see cref="CadDocumentId"/> are always distinct;
/// one cadDocumentId reachable via multiple parents is represented exactly
/// ONCE here, with every edge to it in <see cref="CopyDesignPlan.Edges"/>).
/// </summary>
public sealed record CopyDesignNode(
    /// <summary>The stable Arch document id, or <c>null</c> when this
    ///  reference carries no manifest-matched identity at all (unmanaged /
    ///  unresolved) - a plan containing any such node is NOT executable.</summary>
    string? CadDocumentId,
    /// <summary>The current/pinned FileVersion id, where known.</summary>
    string? CurrentFileVersionId,
    CadDocumentType DocumentType,
    string SourceAbsolutePath,
    string SourceFileName,
    /// <summary>The single relationship kind EVERY observed parent of this
    ///  node agrees on, or <c>null</c> for the root itself OR when this node
    ///  was reached via two OR MORE DIFFERENT relationship kinds (e.g. once as
    ///  an assembly component, once as a drawing's model) - never arbitrarily
    ///  picked by observation/traversal order (P6A Round 3, Blocker 3). Every
    ///  individual parent's own relationship kind remains exact and complete
    ///  in <see cref="CopyDesignPlan.Edges"/> regardless of this value.</summary>
    CadRelationshipKind? RelationshipToParent,
    bool IsManaged,
    bool IsVerified,
    bool IsResolved,
    CopyDesignAction ProposedAction,
    string? ProposedDestinationFileName,
    string? ProposedDestinationAbsolutePath,
    IReadOnlyList<string> Reasons,
    bool IsRoot = false)
{
    /// <summary>True only for a fully-identified, resolved, Verified managed
    ///  node whose identity is COMPLETE - both <see cref="CadDocumentId"/> AND
    ///  <see cref="CurrentFileVersionId"/> are present, nonblank, and
    ///  CANONICAL (each equals its own trimmed form - a padded id is opaque,
    ///  noncanonical evidence, never trimmed and accepted) - the minimum bar
    ///  for ANY automatic (non-NeedsDecision) action. A document id with no
    ///  pinned FileVersionId, or either id with stray padding, is not a
    ///  complete lineage source for a future P6B copy/reuse and must never
    ///  qualify.</summary>
    public bool HasStableIdentity =>
        IsResolved && IsManaged && IsVerified
        && !string.IsNullOrWhiteSpace(CadDocumentId) && CadDocumentId == CadDocumentId.Trim()
        && !string.IsNullOrWhiteSpace(CurrentFileVersionId) && CurrentFileVersionId == CurrentFileVersionId.Trim();
}

/// <summary>What a reference edge's disposition will be once the plan is
///  (later) executed - see requirement 8 of P6A: the plan must distinguish
///  these three cases so a future phase knows whether to repoint a reference
///  to a new copy, leave it exactly as-is, or refuse to touch it.</summary>
public enum CopyDesignEdgeDisposition
{
    /// <summary>The child is being COPIED - after execution this edge is
    ///  expected to be repointed at the NEW copy.</summary>
    PointsToNewCopy,

    /// <summary>The child is being REUSED - this edge is expected to remain
    ///  exactly as it is, pointing at the ORIGINAL (reused) document.</summary>
    RemainsOnReusedSource,

    /// <summary>The child's identity/action could not be safely established
    ///  (NeedsDecision, unmanaged, or unresolved). No future phase may treat
    ///  this edge as safe to act on.</summary>
    UnresolvedOrUnsafe,
}

/// <summary>ONE direct parent -&gt; child edge of the plan's dependency graph,
///  keyed by absolute path (always available, unlike identity) so every edge
///  - including ones between two nodes that share an identity-less/unmanaged
///  child - is representable.</summary>
public sealed record CopyDesignEdge(
    string ParentAbsolutePath,
    string ChildAbsolutePath,
    CadRelationshipKind RelationshipKind,
    CopyDesignEdgeDisposition Disposition);

/// <summary>
/// A deterministic, read-only Copy Design plan for one selected managed root.
/// Produced ONLY by <see cref="CopyDesignPlanner.Plan"/> from a
/// <see cref="CadReferenceScan"/> - never mutates anything itself.
/// </summary>
public sealed record CopyDesignPlan(
    string RootAbsolutePath,
    IReadOnlyList<CopyDesignNode> Nodes,
    IReadOnlyList<CopyDesignEdge> Edges,
    IReadOnlyList<string> Warnings,
    /// <summary>True ONLY when every node has a safe, unambiguous action and
    ///  every naming / collision / path-safety check passed. A future
    ///  execution phase must refuse to run an unexecutable plan.</summary>
    bool IsExecutable,
    /// <summary>False when the underlying reference scan could not fully
    ///  enumerate every document it visited - the graph below an
    ///  unenumerated node is UNKNOWN, so the plan can never be executable in
    ///  that state.</summary>
    bool ScanWasComplete,
    /// <summary>False when no authoritative drawing-association source was
    ///  available (P6A's shipped default) - the ABSENCE of drawing nodes in
    ///  <see cref="Nodes"/> must never be read as "this design has no
    ///  drawings"; it means the association could not be proven yet.</summary>
    bool DrawingAssociationAvailable)
{
    public static CopyDesignPlan Empty(string rootAbsolutePath, string reason) => new(
        rootAbsolutePath,
        Array.Empty<CopyDesignNode>(),
        Array.Empty<CopyDesignEdge>(),
        new[] { reason },
        IsExecutable: false,
        ScanWasComplete: false,
        DrawingAssociationAvailable: false);
}
