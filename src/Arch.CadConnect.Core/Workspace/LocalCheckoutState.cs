using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Core.Workspace;

/// <summary>
/// The client's view of one managed file's checkout situation. Drives ribbon
/// enablement and the operation guards. NEVER a security boundary - the server
/// re-authorizes every checkout / check-in / undo.
/// </summary>
public enum LocalCheckoutState
{
    /// <summary>Not bound to any workspace-manifest entry - the file is not a
    ///  managed CAD document (or identity was never established). All P4C
    ///  commands disabled.</summary>
    Unmanaged,

    /// <summary>Managed, manifest entry Verified, no local checkout marker -
    ///  the normal "you don't have it checked out" state. Read-only on disk.
    ///  Checkout is available.</summary>
    Controlled,

    /// <summary>This client holds the server checkout. Writable on disk.
    ///  Check-In (if saved) and Undo Checkout are available.</summary>
    CheckedOutByMe,

    /// <summary>The server reports the document checked out by another user.
    ///  Read-only; all mutation commands disabled; the holder's name/email may
    ///  be shown.</summary>
    CheckedOutByOther,

    /// <summary>Managed, but the local bytes are NOT a confirmed copy of the
    ///  pinned version (a failed Get Latest, or a failed Undo restore).
    ///  Checkout is refused until Get Latest re-establishes a trusted base.</summary>
    Unverified,
}

/// <summary>Server checkout status for one document, as returned by
///  <c>GET /api/cad-documents/:id/checkout</c>. This is the AUTHORITATIVE
///  source for whether a destructive P4C operation may proceed - the local
///  manifest marker alone never authorizes check-in or undo.</summary>
public sealed record ServerCheckoutStatus(
    ServerCheckoutState State,
    string? HolderName = null,
    string? HolderEmail = null,
    string? CheckoutId = null,
    string? BaseFileVersionId = null,
    int BaseVersionNumber = 0);

public enum ServerCheckoutState { Available, Mine, Locked }

/// <summary>
/// Pure evaluation of <see cref="LocalCheckoutState"/> and the per-command
/// guards. No I/O, no Inventor, no HTTP.
/// </summary>
public static class CheckoutStateMachine
{
    private const string WriteRole = "VIEWER"; // the ONLY role that cannot mutate today

    /// <summary>
    /// Combine the (P4B) manifest entry, an optional live server status, and
    /// whether the file physically exists into one state.
    /// </summary>
    public static LocalCheckoutState Evaluate(
        WorkspaceManifestEntry? entry,
        bool fileExists,
        ServerCheckoutStatus? serverStatus = null)
    {
        if (entry is null)
        {
            return LocalCheckoutState.Unmanaged;
        }

        // A live server "locked by someone else" always wins over a stale
        // local marker.
        if (serverStatus is { State: ServerCheckoutState.Locked })
        {
            return LocalCheckoutState.CheckedOutByOther;
        }

        var localSaysMine = entry.Checkout is not null;
        var serverSaysMine = serverStatus is { State: ServerCheckoutState.Mine };
        var serverSaysAvailable = serverStatus is { State: ServerCheckoutState.Available };

        if ((localSaysMine || serverSaysMine) && !serverSaysAvailable)
        {
            return LocalCheckoutState.CheckedOutByMe;
        }

        // Verified in the manifest but the file is gone -> the managed copy no
        // longer exists locally; not trustworthy as "current".
        if (entry.State == WorkspaceManifestEntryState.Unverified || !fileExists)
        {
            return LocalCheckoutState.Unverified;
        }

        // A local marker that says "mine" while the server says the doc is
        // free means the checkout was released elsewhere (e.g. admin unlock);
        // report Controlled and let the next operation clear the stale marker.
        return LocalCheckoutState.Controlled;
    }

    public static bool IsWriteRole(string? role) =>
        !string.Equals(role, WriteRole, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(role);

    public static bool CanCheckout(LocalCheckoutState state, string? role) =>
        IsWriteRole(role) && state == LocalCheckoutState.Controlled;

    /// <summary><paramref name="documentSaved"/> = no unsaved edits in Inventor
    ///  (<c>Document.Dirty == false</c>) - check-in uploads the saved file.</summary>
    public static bool CanCheckIn(LocalCheckoutState state, string? role, bool documentSaved) =>
        IsWriteRole(role) && state == LocalCheckoutState.CheckedOutByMe && documentSaved;

    public static bool CanUndo(LocalCheckoutState state, string? role) =>
        IsWriteRole(role) && state == LocalCheckoutState.CheckedOutByMe;

    /// <summary>Convenience for the Inventor layer: role string from a session.</summary>
    public static bool IsWriteRole(ArchIdentity? identity) => IsWriteRole(identity?.Role);
}
