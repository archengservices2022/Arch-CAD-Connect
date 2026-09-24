using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.CopyDesign;

/// <summary>
/// P6D: pure derivation of the model <c>cadDocumentId</c>s a REAL drawing-
/// association authority should be asked about, computed BEFORE
/// <see cref="CopyDesignPlanner.Plan"/> runs its own reconciliation.
/// <see cref="IDrawingAssociationSource.GetAssociatedDrawings"/> is
/// synchronous, so an HTTP-backed source must prefetch every candidate
/// answer up front (see <see cref="PrefetchedDrawingAssociationSource"/>) -
/// this is the caller-side counterpart that decides WHICH ids to prefetch.
///
/// Deliberately OVER-inclusive: this need not exactly match the planner's own
/// "reconciled IAM/IPT model node" set (computing that would duplicate
/// reconciliation logic here) - an extra candidate id costs one unused
/// dictionary entry the planner never looks up, never a false or missing
/// answer for an id the planner DOES need. It is NEVER derived from a file
/// name - only from resolved, workspace-manifest-bound stable identities
/// (<see cref="CadManifestIdentity.CadDocumentId"/> / <see cref="PlmIdentity.CadDocumentId"/>),
/// exactly the same identity source the planner itself trusts.
/// </summary>
public static class CopyDesignDrawingAssociationCandidates
{
    public static IReadOnlySet<string> From(CadReferenceScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        var ids = new HashSet<string>(StringComparer.Ordinal);

        if (scan.Root.DocumentType is CadDocumentType.Iam or CadDocumentType.Ipt
            && scan.Root.Identity is { CadDocumentId.Length: > 0 } rootIdentity)
        {
            ids.Add(rootIdentity.CadDocumentId);
        }

        foreach (var reference in scan.References)
        {
            if (reference.ReferenceType is CadDocumentType.Iam or CadDocumentType.Ipt
                && reference.ManifestIdentity is { CadDocumentId.Length: > 0 } manifestIdentity)
            {
                ids.Add(manifestIdentity.CadDocumentId);
            }
        }

        return ids;
    }
}
