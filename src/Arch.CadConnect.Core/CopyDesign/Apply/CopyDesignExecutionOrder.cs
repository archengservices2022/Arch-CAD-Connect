using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.CopyDesign.Apply;

public sealed record CopyDesignExecutionOrderResult(bool Success, string? FailureReason, IReadOnlyList<CopyDesignNode> Order)
{
    public static CopyDesignExecutionOrderResult Fail(string reason) => new(false, reason, Array.Empty<CopyDesignNode>());
}

/// <summary>
/// P6C: computes a DETERMINISTIC, dependency-aware execution order for a
/// plan's in-scope (IAM/IPT) nodes - every CHILD strictly before its PARENT
/// (a topological sort over the <see cref="CopyDesignEdge"/> COMPONENT
/// edges), so that by the time any IAM's references are rewired, every
/// child it could point at has already been physically copied. Ties (nodes
/// with no ordering constraint between them) are broken ordinally by
/// CadDocumentId so the order is 100% reproducible across runs. PURE.
///
/// Two full phases (copy every node in this order, THEN rewire every copied
/// IAM in this same order) means the copy step itself does not strictly
/// depend on this order for correctness - but making it dependency-aware
/// anyway is defensive (a future phase may interleave copy+rewire per node)
/// and makes the order auditable/reproducible, which is required regardless.
///
/// FAILS CLOSED (never guesses a partial order) if the edge set describes a
/// cycle - a well-formed CAD reference graph is a DAG; a cycle means the
/// input is not trustworthy.
/// </summary>
public static class CopyDesignExecutionOrderPlanner
{
    public static CopyDesignExecutionOrderResult ComputeOrder(IReadOnlyList<CopyDesignNode> nodes, IReadOnlyList<CopyDesignEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        // Key every node by its stable identity (guaranteed present - callers
        // only pass in-scope, already-decided COPY/REUSE nodes) and by its
        // absolute path (edges are path-keyed).
        var byPath = new Dictionary<string, CopyDesignNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            byPath[node.SourceAbsolutePath] = node;
        }

        // child -> set of parents that depend on it (an edge Parent->Child
        // means Child must be ready before Parent, i.e. Child has an
        // out-edge to Parent in the "must happen before" graph).
        var dependents = nodes.ToDictionary(n => n.SourceAbsolutePath, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var inDegree = nodes.ToDictionary(n => n.SourceAbsolutePath, _ => 0, StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            if (edge.RelationshipKind != CadRelationshipKind.Component)
            {
                continue; // drawing / other edges never constrain IAM/IPT copy order.
            }
            if (!byPath.ContainsKey(edge.ParentAbsolutePath) || !byPath.ContainsKey(edge.ChildAbsolutePath))
            {
                continue; // one side is out of scope (e.g. excluded/drawing) - no constraint to add.
            }
            if (string.Equals(edge.ParentAbsolutePath, edge.ChildAbsolutePath, StringComparison.OrdinalIgnoreCase))
            {
                continue; // self-edge is meaningless for ordering; never a valid dependency to add.
            }
            dependents[edge.ChildAbsolutePath].Add(edge.ParentAbsolutePath);
            inDegree[edge.ParentAbsolutePath]++;
        }

        // Kahn's algorithm, but the "ready" frontier is always processed in
        // a STABLE ordinal order (by CadDocumentId, falling back to path)
        // rather than whatever order it happened to become ready in.
        var ready = new SortedSet<string>(
            inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key),
            Comparer<string>.Create((a, b) => string.CompareOrdinal(OrderKey(byPath[a]), OrderKey(byPath[b]))));

        var order = new List<CopyDesignNode>(nodes.Count);
        var remainingInDegree = new Dictionary<string, int>(inDegree, StringComparer.OrdinalIgnoreCase);

        while (ready.Count > 0)
        {
            var path = ready.Min!;
            ready.Remove(path);
            order.Add(byPath[path]);

            foreach (var parentPath in dependents[path])
            {
                remainingInDegree[parentPath]--;
                if (remainingInDegree[parentPath] == 0)
                {
                    ready.Add(parentPath);
                }
            }
        }

        if (order.Count != nodes.Count)
        {
            return CopyDesignExecutionOrderResult.Fail(
                "The plan's dependency graph contains a cycle among in-scope IAM/IPT nodes - refusing to guess an execution order.");
        }

        return new CopyDesignExecutionOrderResult(true, null, order);
    }

    private static string OrderKey(CopyDesignNode node) =>
        !string.IsNullOrWhiteSpace(node.CadDocumentId) ? node.CadDocumentId : node.SourceAbsolutePath;
}
