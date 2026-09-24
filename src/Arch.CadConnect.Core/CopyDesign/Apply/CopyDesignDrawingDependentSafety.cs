namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6D ROUND 2 (CRITICAL fix): ONE document Inventor currently has LOADED
/// that a drawing's Save/SaveAs/SaveAsInventorDWG operation could cause
/// Inventor to consider a "dependent" it might cascade-save - gathered by
/// the Inventor adapter (COM), judged here (PURE, no COM). Only a LOADED
/// document can ever be <see cref="IsDirty"/> at all (dirty is purely an
/// in-memory concept), so the adapter only ever reports documents it found
/// actually open in the current Inventor session.
/// </summary>
public sealed record CopyDesignDependentDocumentState(string AbsolutePath, bool IsDirty);

public sealed record CopyDesignDrawingDependentSafetyResult(bool Safe, string? FailureReason)
{
    public static readonly CopyDesignDrawingDependentSafetyResult Ok = new(true, null);
}

/// <summary>
/// P6D ROUND 2 (CRITICAL fix): the SOLE decision point for "is it safe to
/// let Inventor perform a drawing Save/SaveAs/SaveAsInventorDWG right now".
/// PURE - no COM - so the fail-closed rule is independently unit-testable
/// with plain records, never requiring a live Inventor session to prove.
///
/// RULE: if ANY currently-loaded document this drawing operation could
/// cause Inventor to consider a dependent (its own referenced model(s), for
/// BOTH the pre-rewire physical-copy step and the post-rewire final-save
/// step - see <see cref="Inventor.CopyDesign.InventorCopyDesignPhysicalCopier"/>
/// and <see cref="Inventor.CopyDesign.InventorCopyDesignDrawingReferenceRewirer"/>'s
/// own doc comments for exactly where each call site invokes this) is
/// <see cref="CopyDesignDependentDocumentState.IsDirty"/>, the operation is
/// UNSAFE and must fail closed BEFORE any Save/SaveAs is attempted - this is
/// true regardless of whether that dependent is a COPY source (whose
/// captured pre-mutation SHA-256 baseline would otherwise silently describe
/// stale on-disk bytes if Inventor saved unsaved edits on top of it) or a
/// REUSE target (an document Copy Design NEVER mutates at all, by
/// definition - see <see cref="CopyDesignAction.Reuse"/>'s own doc comment).
///
/// This never assumes <c>Application.SilentOperation</c> prevents a
/// dependent save - <c>SilentOperation</c> only suppresses Inventor's UI
/// prompts; it says nothing about which documents an underlying Save engine
/// decides to write. The only guarantee this class relies on is its own:
/// nothing Dirty is ever allowed to reach a Save/SaveAs call in the first
/// place.
/// </summary>
public static class CopyDesignDrawingDependentSafetyGuard
{
    public static CopyDesignDrawingDependentSafetyResult Evaluate(IReadOnlyList<CopyDesignDependentDocumentState> dependents)
    {
        ArgumentNullException.ThrowIfNull(dependents);

        var dirty = dependents.Where(d => d.IsDirty).ToArray();
        if (dirty.Length == 0)
        {
            return CopyDesignDrawingDependentSafetyResult.Ok;
        }

        var names = string.Join(", ", dirty
            .Select(d => Path.GetFileName(d.AbsolutePath))
            .OrderBy(n => n, StringComparer.Ordinal));
        return new CopyDesignDrawingDependentSafetyResult(
            false,
            $"Referenced model document(s) with unsaved changes (Dirty) were found open in Inventor: {names}. "
                + "Refusing to proceed - an Inventor drawing Save/SaveAs/SaveAsInventorDWG can cascade-save a dirty "
                + "dependent, which would mutate a source or REUSE model. Save or close the listed document(s) "
                + "outside Copy Design, then retry.");
    }
}
