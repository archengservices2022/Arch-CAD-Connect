using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// ONE reference this COPY assembly must end up pointing at, expressed
/// PURELY in terms of stable plan identity/paths - never a filename or
/// display name. <see cref="ChildIsCopy"/> distinguishes the two cases the
/// Inventor adapter must treat differently: COPY children require an actual
/// <c>ReplaceReference</c>/<c>ComponentOccurrence.Replace</c> mutation;
/// REUSE children require ONLY a read-only confirmation that the reference
/// still resolves to <see cref="ExpectedTargetAbsolutePath"/> (which already
/// equals the edge's original child path) - no mutation at all.
/// </summary>
public sealed record CopyDesignReferenceTarget(
    string ChildCadDocumentId,
    /// <summary>The child's CURRENT (source) absolute path - what the fresh
    ///  COPY's reference descriptor is expected to STILL resolve to,
    ///  immediately after the physical copy and before any rewiring (a plain
    ///  Save-Copy-As does not alter the in-memory reference set).</summary>
    string OriginalChildAbsolutePath,
    /// <summary>Where this reference must resolve to AFTER rewiring: the
    ///  child's NEW destination for a COPY child, or unchanged
    ///  (== <see cref="OriginalChildAbsolutePath"/>) for a REUSE child.</summary>
    string ExpectedTargetAbsolutePath,
    bool ChildIsCopy);

public sealed record CopyDesignReferenceMappingResult(bool Success, string? FailureReason, IReadOnlyList<CopyDesignReferenceTarget> Targets)
{
    public static CopyDesignReferenceMappingResult Fail(string reason) => new(false, reason, Array.Empty<CopyDesignReferenceTarget>());
}

/// <summary>
/// P6C: for ONE copied IAM node, computes exactly what every one of its
/// direct COMPONENT children must resolve to after rewiring - by STABLE plan
/// identity, never by basename/folder/display name. PURE - no COM.
///
/// FAILS CLOSED on:
///   - a child edge whose disposition is <see cref="CopyDesignEdgeDisposition.UnresolvedOrUnsafe"/>
///     (the planner itself could not safely establish this child's fate);
///   - a child node that cannot be found by the edge's ChildAbsolutePath;
///   - a child node whose OWN action is <see cref="CopyDesignAction.Exclude"/>
///     (P6C has no defined safe behavior for an excluded-but-referenced
///     child in this milestone - see the P6C report) or
///     <see cref="CopyDesignAction.NeedsDecision"/>;
///   - a COPY child that is missing its own proposed destination path
///     (structurally should already be caught by the request mapper, but
///     never trusted blindly here either);
///   - two edges from the SAME parent naming the SAME child identity with
///     DIFFERENT expected targets (should be structurally impossible - one
///     child has exactly one decision - but never assumed).
/// </summary>
public static class CopyDesignReferenceTargetResolver
{
    public static CopyDesignReferenceMappingResult Resolve(
        CopyDesignNode parent, IReadOnlyList<CopyDesignEdge> allEdges, IReadOnlyList<CopyDesignNode> allNodes)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(allEdges);
        ArgumentNullException.ThrowIfNull(allNodes);

        var nodesByPath = new Dictionary<string, CopyDesignNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in allNodes)
        {
            nodesByPath[node.SourceAbsolutePath] = node;
        }

        var childEdges = allEdges
            .Where(e => e.RelationshipKind == CadRelationshipKind.Component
                        && string.Equals(e.ParentAbsolutePath, parent.SourceAbsolutePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var targetsByChild = new Dictionary<string, CopyDesignReferenceTarget>(StringComparer.Ordinal);
        foreach (var edge in childEdges)
        {
            if (edge.Disposition == CopyDesignEdgeDisposition.UnresolvedOrUnsafe)
            {
                return CopyDesignReferenceMappingResult.Fail(
                    $"Reference from \"{parent.SourceFileName}\" to \"{Path.GetFileName(edge.ChildAbsolutePath)}\" " +
                    "could not be safely resolved by the plan - refusing to rewire (fail closed).");
            }

            if (!nodesByPath.TryGetValue(edge.ChildAbsolutePath, out var child))
            {
                return CopyDesignReferenceMappingResult.Fail(
                    $"Reference from \"{parent.SourceFileName}\" to \"{Path.GetFileName(edge.ChildAbsolutePath)}\" " +
                    "does not resolve to any node in the plan - refusing to guess (fail closed).");
            }

            if (child.ProposedAction is CopyDesignAction.Exclude or CopyDesignAction.NeedsDecision)
            {
                return CopyDesignReferenceMappingResult.Fail(
                    $"\"{parent.SourceFileName}\" references \"{child.SourceFileName}\", whose plan action is " +
                    $"{child.ProposedAction} - P6C has no defined safe behavior for this in the current milestone " +
                    "(IAM/IPT only); refusing to produce a possibly-broken assembly.");
            }

            if (string.IsNullOrWhiteSpace(child.CadDocumentId))
            {
                return CopyDesignReferenceMappingResult.Fail(
                    $"\"{child.SourceFileName}\" has no stable identity - refusing to map a reference to it.");
            }

            string expectedTarget;
            var childIsCopy = child.ProposedAction == CopyDesignAction.Copy;
            if (childIsCopy)
            {
                if (edge.Disposition != CopyDesignEdgeDisposition.PointsToNewCopy
                    || string.IsNullOrWhiteSpace(child.ProposedDestinationAbsolutePath))
                {
                    return CopyDesignReferenceMappingResult.Fail(
                        $"\"{child.SourceFileName}\" is a COPY child but has no usable destination - refusing to rewire.");
                }
                expectedTarget = child.ProposedDestinationAbsolutePath;
            }
            else
            {
                if (edge.Disposition != CopyDesignEdgeDisposition.RemainsOnReusedSource)
                {
                    return CopyDesignReferenceMappingResult.Fail(
                        $"\"{child.SourceFileName}\" is a REUSE child but its edge disposition is {edge.Disposition}, " +
                        "not RemainsOnReusedSource - refusing to guess.");
                }
                expectedTarget = edge.ChildAbsolutePath; // unchanged, original path
            }

            var target = new CopyDesignReferenceTarget(child.CadDocumentId, edge.ChildAbsolutePath, expectedTarget, childIsCopy);

            if (targetsByChild.TryGetValue(child.CadDocumentId, out var existing) && existing != target)
            {
                return CopyDesignReferenceMappingResult.Fail(
                    $"\"{parent.SourceFileName}\" has two conflicting expected targets for the same child " +
                    $"identity \"{child.CadDocumentId}\" - refusing to guess which is correct.");
            }
            targetsByChild[child.CadDocumentId] = target;
        }

        return new CopyDesignReferenceMappingResult(true, null, targetsByChild.Values.OrderBy(t => t.ChildCadDocumentId, StringComparer.Ordinal).ToArray());
    }
}
