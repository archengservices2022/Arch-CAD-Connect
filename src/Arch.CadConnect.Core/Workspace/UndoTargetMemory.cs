namespace Arch.CadConnect.Core.Workspace;

/// <summary>
/// A single remembered "checked-out workspace file" so Undo Checkout stays
/// reachable from the ribbon AFTER the user closes the document (the P4C
/// safety rule refuses byte replacement while a document is open, so the
/// close-then-undo path must exist).
///
/// It is a snapshot of an EXACT workspace-manifest checkout binding - stable
/// identities only, NEVER a filename. Before Undo actually runs, the
/// orchestrator still re-checks everything against the manifest and the
/// authoritative server checkout; this record only keeps the ribbon command
/// enabled and lets the confirmation dialog name the exact target.
/// </summary>
public sealed record RememberedCheckoutTarget(
    ManagedFileRef File,
    string CadDocumentId,
    string CheckoutId,
    string BaseFileVersionId,
    string DocumentNumber)
{
    /// <summary>
    /// True only when this remembered target is STILL an exact, currently
    /// checked-out manifest binding in the CURRENT workspace - same
    /// cadDocumentId, same checkoutId, same base FileVersion. Any drift ->
    /// false, and the caller must forget it.
    /// </summary>
    public bool IsStillUndoable(WorkspaceManifestEntry? currentEntry, string? currentWorkspaceRoot)
    {
        if (currentEntry?.Checkout is not { } checkout)
        {
            return false; // entry gone, or the local checkout marker was cleared
        }
        if (!RootsEqual(File.WorkspaceRoot, currentWorkspaceRoot))
        {
            return false; // the workspace changed - this target belongs to a different root
        }
        return string.Equals(currentEntry.CadDocumentId, CadDocumentId, StringComparison.Ordinal)
            && string.Equals(checkout.CheckoutId, CheckoutId, StringComparison.Ordinal)
            && string.Equals(checkout.BaseFileVersionId, BaseFileVersionId, StringComparison.Ordinal);
    }

    private static bool RootsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }
        static string Norm(string p)
        {
            try { p = Path.GetFullPath(p); } catch { /* keep as-is */ }
            return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Holds at most ONE remembered Undo target (option B: the most-recently
/// active exact checkout target - never a guess across multiple checkouts).
/// Pure: the Inventor layer feeds it the active-document context and a manifest
/// lookup; it never does I/O itself.
/// </summary>
public sealed class UndoTargetMemory
{
    public RememberedCheckoutTarget? Current { get; private set; }

    /// <summary>
    /// If <paramref name="activeDoc"/> is a managed file this session currently
    /// holds checked out, remember it as the Undo target. Otherwise leave the
    /// existing target untouched (it must survive the document closing).
    /// </summary>
    public void Observe(CadDocumentContext activeDoc)
    {
        if (activeDoc.CheckoutState == LocalCheckoutState.CheckedOutByMe
            && activeDoc.PlmIdentity is { } id
            && activeDoc.CheckoutBinding is { } checkout
            && !string.IsNullOrEmpty(activeDoc.FullPath)
            && !string.IsNullOrEmpty(activeDoc.WorkspaceRoot)
            && !string.IsNullOrEmpty(checkout.CheckoutId)
            && !string.IsNullOrEmpty(checkout.BaseFileVersionId))
        {
            Current = new RememberedCheckoutTarget(
                ManagedFileRef.Create(activeDoc.WorkspaceRoot!, activeDoc.FullPath!),
                id.CadDocumentId,
                checkout.CheckoutId,
                checkout.BaseFileVersionId,
                string.IsNullOrEmpty(id.DocumentNumber) ? id.CadDocumentId : id.DocumentNumber!);
        }
    }

    /// <summary>
    /// Forget the remembered target unless it is still an exact,
    /// currently-checked-out manifest binding in the current workspace whose
    /// local file is present on disk.
    /// </summary>
    public void Revalidate(
        Func<ManagedFileRef, WorkspaceManifestEntry?> entryLookup,
        Func<string, bool> fileExists,
        string? currentWorkspaceRoot)
    {
        if (Current is null)
        {
            return;
        }
        var stillBound = Current.IsStillUndoable(entryLookup(Current.File), currentWorkspaceRoot);
        if (!stillBound || !fileExists(Current.File.AbsoluteFilePath))
        {
            Current = null;
        }
    }

    /// <summary>Unconditionally forget (sign-out, explicit undo/check-in completion).</summary>
    public void Clear() => Current = null;
}
