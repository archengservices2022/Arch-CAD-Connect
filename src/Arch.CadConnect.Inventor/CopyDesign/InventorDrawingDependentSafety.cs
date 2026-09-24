using System.Runtime.InteropServices;

using Arch.CadConnect.Core.CopyDesign.Apply;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor.CopyDesign;

/// <summary>
/// P6D ROUND 2, CRITICAL fix: the SHARED Inventor-side (COM) gathering half
/// of the dirty-dependent-save guard - used by BOTH
/// <see cref="InventorCopyDesignPhysicalCopier"/> (before SaveAs/
/// SaveAsInventorDWG) and <see cref="InventorCopyDesignDrawingReferenceRewirer"/>
/// (before its final Save2) so the exact same enumeration/fail-closed
/// discipline backs every drawing Save call site, never duplicated
/// ad-hoc. The actual PASS/FAIL decision is
/// <see cref="CopyDesignDrawingDependentSafetyGuard"/> (Core, PURE, no COM) -
/// this class only reads real COM state and hands it over.
/// </summary>
internal static class InventorDrawingDependentSafety
{
    /// <summary>Gathers the Dirty state of every document Inventor
    ///  currently has LOADED in <paramref name="document"/>'s COMPLETE
    ///  reference closure (<c>AllReferencedDocuments</c> - never merely the
    ///  direct set) and returns the PURE decision. A document that is not
    ///  currently loaded can never be Dirty (a purely in-memory concept), so
    ///  only loaded documents are ever reported - never treated as "no
    ///  dependents exist", only "nothing loaded could possibly be
    ///  dirty".</summary>
    public static CopyDesignDrawingDependentSafetyResult Evaluate(InventorApi.Document document)
    {
        var states = new List<CopyDesignDependentDocumentState>();
        try
        {
            foreach (InventorApi.Document dependent in document.AllReferencedDocuments)
            {
                string? path;
                bool dirty;
                try
                {
                    path = dependent.FullFileName;
                    dirty = dependent.Dirty;
                }
                catch (Exception ex) when (ex is COMException or InvalidComObjectException)
                {
                    // A dependent that cannot even be READ is never silently
                    // assumed safe - fail closed exactly like a Dirty one.
                    return new CopyDesignDrawingDependentSafetyResult(
                        false, "Could not read the Dirty/FullFileName state of a referenced document - refusing to proceed (fail closed).");
                }
                // P6D ROUND 3, CRITICAL fix (item C): a blank/unresolvable
                // FullFileName used to be silently SKIPPED - meaning a
                // dependent Inventor could not name a canonical physical
                // path for was invisible to the safety check entirely, even
                // if it was Dirty. A dependent with no canonical physical
                // path is UNSAFE for P6D mutation by definition (there is no
                // way to prove it will not be touched) - fail closed exactly
                // like a Dirty one, never silently excluded from the set.
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new CopyDesignDrawingDependentSafetyResult(
                        false, "A referenced document has no canonical physical path (blank FullFileName) - refusing to proceed (fail closed).");
                }
                states.Add(new CopyDesignDependentDocumentState(path, dirty));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return new CopyDesignDrawingDependentSafetyResult(
                false, "Inventor threw while enumerating this drawing's referenced documents - refusing to guess the complete dependent set (fail closed).");
        }
        return CopyDesignDrawingDependentSafetyGuard.Evaluate(states);
    }
}
