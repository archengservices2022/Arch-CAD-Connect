namespace Arch.CadConnect.Core.CopyDesign;

/// <summary>
/// P6C manual acceptance follow-up (Round 6): the pure seam an explicit
/// engineer decision (COPY / REUSE / EXCLUDE) travels through to resolve a
/// <see cref="CopyDesignAction.NeedsDecision"/> node that exists SOLELY
/// because no library/shared classification signal was available -
/// <see cref="CopyDesignPlanner"/> is the only place that ever CONSUMES a
/// decision (see its own doc comment); this file only defines the shared
/// vocabulary (the exact reason-text marker, and the eligibility test) so a
/// caller - typically the Ribbon UI - never has to re-implement or guess at
/// which nodes a decision can actually affect.
/// </summary>
public static class CopyDesignExplicitDecisionEligibility
{
    /// <summary>The EXACT, stable prefix <see cref="CopyDesignPlanner"/>
    ///  writes into a node's <see cref="CopyDesignNode.Reasons"/> ONLY for
    ///  the classification-unknown case - the ONE case an explicit decision
    ///  can resolve. Never matched against any OTHER NeedsDecision reason
    ///  (unresolved, unmanaged, unverified, a conflict, an unsafe drawing
    ///  association, self-overwrite, an unsafe destination) - those are
    ///  structural safety failures no engineer "choice" can safely paper
    ///  over, and this prefix is never written for them.</summary>
    public const string ClassificationUnknownReasonPrefix =
        "No library/shared classification signal is available for this managed component";

    /// <summary>
    /// True ONLY for a node whose <see cref="CopyDesignAction.NeedsDecision"/>
    /// is the classification-unknown case AND whose document type is IAM or
    /// IPT - P6C's strict scope never surfaces a decision control for a
    /// drawing (P6D owns drawings), even though the underlying planner
    /// mechanism is type-agnostic. A caller (e.g. the Ribbon UI) should show
    /// a COPY/REUSE/EXCLUDE choice ONLY for a node where this returns true;
    /// supplying a decision for any OTHER node's cadDocumentId is harmless
    /// (the planner simply never consults it) but will not change anything,
    /// so surfacing a control for it would only confuse the user.
    /// </summary>
    public static bool IsEligibleForExplicitDecision(CopyDesignNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.ProposedAction == CopyDesignAction.NeedsDecision
            && node.DocumentType is CadDocumentType.Ipt or CadDocumentType.Iam
            && node.Reasons.Any(r => r.StartsWith(ClassificationUnknownReasonPrefix, StringComparison.Ordinal));
    }
}

/// <summary>
/// A validated, immutable set of explicit engineer decisions for ONE Copy
/// Design plan recomputation - keyed by STABLE cadDocumentId, never by
/// display row, path, or file name (see
/// <see cref="CopyDesignExplicitDecisionEligibility"/>'s own doc comment for
/// why that binding matters). Construction rejects anything that could not
/// possibly be a legitimate engineer choice; it never silently drops or
/// reinterprets an entry.
/// </summary>
public sealed class CopyDesignExplicitDecisionSet
{
    private readonly IReadOnlyDictionary<string, CopyDesignAction> _decisions;

    private CopyDesignExplicitDecisionSet(IReadOnlyDictionary<string, CopyDesignAction> decisions)
    {
        _decisions = decisions;
    }

    public static readonly CopyDesignExplicitDecisionSet Empty = new(new Dictionary<string, CopyDesignAction>(StringComparer.Ordinal));

    /// <summary>
    /// Builds a validated set from (cadDocumentId, action) pairs.
    /// </summary>
    /// <exception cref="ArgumentException">a cadDocumentId is blank, a
    ///  cadDocumentId is duplicated, or an action is anything other than
    ///  Copy, Reuse, or Exclude (in particular: NOT NeedsDecision - there is
    ///  no such thing as an explicit decision to "still not decide").</exception>
    public static CopyDesignExplicitDecisionSet Build(IEnumerable<(string CadDocumentId, CopyDesignAction Action)> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var map = new Dictionary<string, CopyDesignAction>(StringComparer.Ordinal);
        foreach (var (cadDocumentId, action) in decisions)
        {
            if (string.IsNullOrWhiteSpace(cadDocumentId))
            {
                throw new ArgumentException("An explicit decision must name a non-blank cadDocumentId.", nameof(decisions));
            }
            if (action is not (CopyDesignAction.Copy or CopyDesignAction.Reuse or CopyDesignAction.Exclude))
            {
                throw new ArgumentException(
                    $"An explicit decision for \"{cadDocumentId}\" must be Copy, Reuse, or Exclude - got {action}.",
                    nameof(decisions));
            }
            if (!map.TryAdd(cadDocumentId, action))
            {
                throw new ArgumentException($"Duplicate explicit decision for cadDocumentId \"{cadDocumentId}\".", nameof(decisions));
            }
        }
        return new CopyDesignExplicitDecisionSet(map);
    }

    public IReadOnlyDictionary<string, CopyDesignAction> AsDictionary() => _decisions;

    public int Count => _decisions.Count;
}
