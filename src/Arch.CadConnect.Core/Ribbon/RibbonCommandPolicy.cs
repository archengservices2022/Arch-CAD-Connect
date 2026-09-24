using Arch.CadConnect.Core.Connection;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Ribbon;

/// <summary>
/// Pure decision of whether a ribbon button is enabled, given the current
/// connection state and active document. This is a USABILITY layer only - it
/// disables buttons that could not possibly succeed. It is NOT a security
/// boundary: the server re-authorizes every real operation regardless of what
/// the ribbon allowed the user to click.
///
/// Rule: a command that is not implemented yet
/// (<see cref="ArchCommands.IsImplemented"/> == false) is ALWAYS shown
/// disabled, with a tooltip saying so - never enabled-then-fake-succeed.
/// </summary>
public static class RibbonCommandPolicy
{
    public static bool IsEnabled(
        ArchCommand command,
        ConnectionState connection,
        CadDocumentContext document,
        string? userRole = null,
        bool hasRememberedUndoTarget = false,
        bool hasResumableCopyDesignAttempt = false,
        bool hasUncertainCopyDesignAttempt = false)
    {
        // Not built yet -> visible but disabled, everywhere.
        if (!command.IsImplemented())
        {
            return false;
        }

        return command switch
        {
            ArchCommand.SignIn => connection.AllowsSignIn(),
            ArchCommand.SignOut => connection.AllowsSignOut(),
            ArchCommand.ServerStatus => connection is not ConnectionState.SignedOut and not ConnectionState.Connecting,

            // P4B: Get Latest resolves the root by an EXPLICIT document number
            // the user enters, so it does NOT need an open document - only a
            // live connection.
            ArchCommand.GetLatest => connection == ConnectionState.Connected,

            // P4C: document-level checkout. The state is derived from the
            // verified workspace-manifest binding; a VIEWER is always
            // disabled (and still server-enforced). Checkout needs a Verified
            // controlled file; Check-In additionally needs no unsaved edits.
            ArchCommand.Checkout => connection == ConnectionState.Connected
                && CheckoutStateMachine.CanCheckout(document.CheckoutState, userRole),
            ArchCommand.CheckIn => connection == ConnectionState.Connected
                && CheckoutStateMachine.CanCheckIn(document.CheckoutState, userRole, document.IsSaved),

            // P4C: Undo stays enabled after the checked-out document is CLOSED,
            // targeting the remembered exact-manifest checkout binding - so the
            // close-then-undo safe path (Undo refuses to replace bytes while
            // the document is open) is reachable. The remembered target is
            // still fully re-validated against the manifest + the authoritative
            // server checkout before anything destructive happens.
            ArchCommand.UndoCheckout => connection == ConnectionState.Connected
                && (CheckoutStateMachine.CanUndo(document.CheckoutState, userRole)
                    || (hasRememberedUndoTarget && CheckoutStateMachine.IsWriteRole(userRole))),

            // P5A: a READ-ONLY scan of the references Inventor already knows
            // about. Needs a live connection and a saved, supported CAD
            // document open. Deliberately NOT role-gated - it observes, it
            // never mutates, so VIEWER is allowed. It does NOT require a
            // checkout or a managed-workspace binding.
            ArchCommand.ScanReferences => connection == ConnectionState.Connected
                && IsScannableDocument(document),

            // P5B-A: READ-ONLY local reference-health diagnosis on top of the
            // P5A scan. Same gate as Scan References - connected + a saved,
            // supported document. Not role-gated (VIEWER may diagnose), no
            // checkout, no mutation.
            ArchCommand.ReferenceHealth => connection == ConnectionState.Connected
                && IsScannableDocument(document),

            // P5C: controlled repair of ONE stale managed reference. It mutates
            // the in-memory CAD reference graph, so - unlike the read-only
            // P5A/P5B commands - it is write-role gated (a VIEWER could never
            // check out the referencing document anyway). Same document gate as
            // Reference Health; the real eligibility (a stale managed reference,
            // an available authoritative target, a writable referencing
            // document) is decided in the preview after the click, and the
            // engineer must still explicitly confirm before anything changes.
            ArchCommand.RepairReference => connection == ConnectionState.Connected
                && IsScannableDocument(document)
                && CheckoutStateMachine.IsWriteRole(userRole),

            // P6A: read-only plan + preview, zero mutation - same gate as
            // Scan References / Reference Health (connected + a saved,
            // supported document). Not role-gated: nothing is written, so a
            // VIEWER may preview too. A future EXECUTION command (not part of
            // P6A) would need write-role gating; preview itself does not.
            ArchCommand.CopyDesignPreview => connection == ConnectionState.Connected
                && IsScannableDocument(document),

            // P6D ROUND 2/3 fix: these were UNCONDITIONALLY disabled before -
            // this switch had no case for either command at all, so they
            // silently fell through to `_ => false` regardless of whether an
            // attempt was genuinely resumable/recoverable. Deliberately does
            // NOT require an active document (see ArchCommand.RequiresActiveDocument's
            // own comment - the remembered attempt carries its own plan) -
            // only a live connection (Resume/Recover both replay an
            // authenticated server request).
            //
            // P6D PRODUCTION RECOVERY: CopyDesignResume no longer requires
            // hasResumableCopyDesignAttempt - a DURABLE resume (by known
            // CopyDesignOperationId, reconstructed from the server's
            // authoritative status - see CopyDesignResumeAttemptReconstructor)
            // is now offered whenever connected, even with NO in-memory
            // attempt (e.g. after an Inventor restart lost
            // _resumableCopyDesignAttempt). The controller decides WHICH
            // resume path to offer (in-session vs. durable-by-id) once the
            // command is actually invoked - this policy only decides whether
            // the button can do ANYTHING useful at all. CopyDesignRecover
            // stays conditioned on hasUncertainCopyDesignAttempt - it has no
            // durable-by-id equivalent (an uncertain reservation never
            // confirmed an operationId to recover BY - see ArchCommand.CopyDesignRecover's
            // own doc comment) and must never be conflated with durable resume.
            ArchCommand.CopyDesignResume => connection == ConnectionState.Connected,
            ArchCommand.CopyDesignRecover => connection == ConnectionState.Connected
                && hasUncertainCopyDesignAttempt,

            _ => false,
        };
    }

    /// <summary>Supported P5A/P5B-A scan roots: an Inventor assembly, part or
    ///  drawing that exists on disk. Managed status is irrelevant - an
    ///  unmanaged document still has references worth reporting.</summary>
    private static bool IsScannableDocument(CadDocumentContext document) =>
        document.HasBeenSavedToDisk
        && document.DocumentType is CadDocumentType.Iam
            or CadDocumentType.Ipt
            or CadDocumentType.Idw
            or CadDocumentType.Dwg;

    /// <summary>
    /// The enablement of EVERY command for a given state, ready to apply to the
    /// ribbon in one pass. Deterministic order (<see cref="ArchCommands.All"/>).
    /// </summary>
    public static IReadOnlyDictionary<ArchCommand, bool> Evaluate(
        ConnectionState connection,
        CadDocumentContext document,
        string? userRole = null,
        bool hasRememberedUndoTarget = false,
        bool hasResumableCopyDesignAttempt = false,
        bool hasUncertainCopyDesignAttempt = false)
    {
        var map = new Dictionary<ArchCommand, bool>();
        foreach (var command in ArchCommands.All)
        {
            map[command] = IsEnabled(command, connection, document, userRole, hasRememberedUndoTarget,
                hasResumableCopyDesignAttempt, hasUncertainCopyDesignAttempt);
        }
        return map;
    }
}
