using Arch.CadConnect.Core.Documents;
using Arch.CadConnect.Core.Files;
using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Workspace;

/// <summary>
/// Ties one P4C server operation to the local file state + workspace manifest,
/// honouring:
///
///   - ORDERING: server state changes first; the local file / manifest change
///     only after a confirmed server outcome.
///   - AUTHORITY: the local checkout marker alone NEVER authorizes a
///     destructive operation. Check-In and Undo both re-check the
///     authoritative server checkout status (`GET .../checkout`) first, and
///     stop unless it says "mine" for this session.
///   - OBSERVABLE PERSISTENCE: a manifest write failure is surfaced
///     (`ManifestUpdated` / `ReconcileNeeded` / `ManifestReconciled`), never
///     swallowed - the server stays authoritative and recovery is a Get Latest.
///   - AMBIGUITY: an Undo that gets 409 (checkout gone) does NOT restore the
///     captured base - the reason is unknown (lost response, admin unlock, or
///     a check-in that already created a newer version).
///
/// Identity: the target is always an EXACT manifest-bound absolute path
/// (<c>root + relativePath</c>). The entry's <c>cadDocumentId</c> is
/// authoritative; a filename is never used as identity.
/// </summary>
public sealed class CheckoutOrchestrator
{
    private readonly ArchServerUri _server;
    private readonly IArchSession _session;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IEditorDocumentProbe _editorProbe;

    public CheckoutOrchestrator(
        ArchServerUri server,
        IArchSession session,
        HttpClient http,
        TimeSpan? timeout = null,
        Func<DateTimeOffset>? clock = null,
        IEditorDocumentProbe? editorProbe = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _editorProbe = editorProbe ?? NoEditorProbe.Instance;
    }

    private CheckoutHttpClient Checkout() => new(_server, _session, _http, _timeout);
    private CheckInClient CheckIn() => new(_server, _session, _http, TimeSpan.FromMinutes(10));
    private ManagedFileRestorer Restorer() => new(new HttpContentDownloader(_server, _session, _http));

    // ================================================================
    // CHECKOUT
    // ================================================================

    public async Task<CheckoutOperationResult> CheckoutAsync(
        string workspaceRoot, string absoluteFilePath, CancellationToken ct = default)
    {
        var (manifest, entry) = ResolveManaged(workspaceRoot, absoluteFilePath);

        if (entry.State != WorkspaceManifestEntryState.Verified)
        {
            throw new NotVerifiedException();
        }

        // server first (throws CheckoutConflictException / ArchApiException) --
        var result = await Checkout().RequestCheckoutAsync(entry.CadDocumentId, ct).ConfigureAwait(false);

        // then local: record the exact base binding, then make writable. The
        // manifest write is OBSERVABLE - a failure -> ReconcileNeeded, never a
        // false success. The server checkout stays authoritative regardless.
        var binding = new WorkspaceCheckoutBinding
        {
            CheckoutId = result.CheckoutId,
            BaseFileVersionId = result.BaseFileVersionId,
            BaseVersionNumber = result.BaseVersionNumber,
            BaseChecksum = entry.Checksum,
            BaseFileSize = entry.FileSize,
            CheckedOutAtUtc = _clock(),
        };
        var manifestUpdated = TrySave(() => manifest.MarkCheckedOut(absoluteFilePath, binding));
        var madeWritable = ManagedFileGuard.SetWritable(absoluteFilePath);

        var reconcileNeeded = !manifestUpdated || !madeWritable;
        var message = reconcileNeeded
            ? "Checkout is HELD on the server, but the local file could not be fully updated"
              + (!manifestUpdated ? " (workspace record not saved)" : "")
              + (!madeWritable ? " (could not clear read-only)" : "")
              + ". Close and reopen the file, or run Get Latest, to reconcile."
            : result.Kind == CheckoutOutcome.AlreadyMine
                ? "You already hold this checkout. The local file is editable."
                : "Checked out. The local file is now editable.";

        return new CheckoutOperationResult(
            entry.CadDocumentId, result.BaseFileVersionId, result.BaseVersionNumber,
            MadeWritable: madeWritable, ManifestUpdated: manifestUpdated,
            ReconcileNeeded: reconcileNeeded, Message: message);
    }

    // ================================================================
    // CHECK IN
    // ================================================================

    public async Task<CheckInOperationResult> CheckInAsync(
        string workspaceRoot, string absoluteFilePath, CancellationToken ct = default)
    {
        var (manifest, entry) = ResolveManaged(workspaceRoot, absoluteFilePath);

        // AUTHORITATIVE gate: the local marker alone does not authorize an
        // upload. Throws CheckoutConflictException (locked by other) /
        // StaleCheckoutException (server says available, or a different
        // checkout) - and reconciles the stale local marker in passing.
        await RequireMineAsync(manifest, entry, absoluteFilePath, ct).ConfigureAwait(false);

        if (!File.Exists(absoluteFilePath))
        {
            throw new ArchApiException(ArchApiFailureKind.BadRequest, "The local file to check in does not exist.");
        }

        CheckInOutcome outcome;
        try
        {
            outcome = await CheckIn()
                .CheckInAsync(entry.CadDocumentId, absoluteFilePath, entry.FileName, ct)
                .ConfigureAwait(false);
        }
        catch (CheckInVerificationException ex)
        {
            // Server 201: the version WAS created and the lock IS released. The
            // manifest must NEVER be left on the old version. Rebind to the new
            // one as Unverified; recovery is a Get Latest.
            bool rebound;
            if (ex.FileVersionId is { Length: > 0 } && ex.VersionNumber is > 0)
            {
                rebound = TrySave(() => manifest.RebindToNewVersion(
                    absoluteFilePath, ex.FileVersionId, ex.VersionNumber.Value,
                    checksum: "", fileSize: 0, nowUtc: _clock(),
                    state: WorkspaceManifestEntryState.Unverified));
            }
            else
            {
                rebound = TrySave(() => manifest.MarkUnverified(absoluteFilePath, clearCheckout: true));
            }
            ManagedFileGuard.SetControlled(absoluteFilePath);
            return new CheckInOperationResult(
                Verified: false, NewFileVersionId: ex.FileVersionId, NewVersionNumber: ex.VersionNumber ?? 0,
                ManifestUpdated: rebound, MadeControlled: true,
                Message: "Checked in, but the uploaded copy could not be verified"
                    + (rebound ? "" : " and the local workspace record could not be saved")
                    + ". The new server version is authoritative - run Get Latest to sync.");
        }

        // verified success --
        var reboundOk = TrySave(() => manifest.RebindToNewVersion(
            absoluteFilePath, outcome.FileVersionId, outcome.VersionNumber,
            outcome.Checksum, outcome.FileSize, _clock(),
            state: WorkspaceManifestEntryState.Verified));
        var controlled = ManagedFileGuard.SetControlled(absoluteFilePath);

        var message = !reboundOk
            ? $"Checked in as version {outcome.VersionNumber}, but the local workspace record could not be saved. "
              + "The new version is authoritative - run Get Latest to reconcile."
            : !controlled
                ? $"Checked in as version {outcome.VersionNumber} (could not restore the read-only guard - close and reopen the file)."
                : $"Checked in as version {outcome.VersionNumber}. The local file is read-only again.";

        return new CheckInOperationResult(
            Verified: true, NewFileVersionId: outcome.FileVersionId, NewVersionNumber: outcome.VersionNumber,
            ManifestUpdated: reboundOk, MadeControlled: controlled, Message: message);
    }

    // ================================================================
    // UNDO CHECKOUT  (verify server "mine" -> stage+verify base -> release ->
    //                 replace -> read-only -> manifest)
    // ================================================================

    public async Task<UndoOperationResult> UndoAsync(
        string workspaceRoot, string absoluteFilePath, string? reason, CancellationToken ct = default)
    {
        // (1) resolve the exact manifest target.
        var (manifest, entry) = ResolveManaged(workspaceRoot, absoluteFilePath);

        var binding = entry.Checkout
            ?? throw new NotCheckedOutLocallyException(
                "This file is not marked as checked out by you. Nothing to undo. Run Get Latest to restore it.");
        if (string.IsNullOrEmpty(binding.BaseFileVersionId)
            || string.IsNullOrEmpty(binding.BaseChecksum) || binding.BaseFileSize <= 0)
        {
            throw new NotCheckedOutLocallyException(
                "The local checkout record is incomplete. Run Get Latest to restore this file.");
        }

        // (2) the exact manifest-bound target must still exist as a regular
        // file. If it is gone, Undo must NOT release the checkout or recreate
        // the file - a missing target means the local workspace is in a state
        // only Get Latest should repair. (First of two checks - TOCTOU.)
        if (!File.Exists(absoluteFilePath))
        {
            throw new UndoTargetMissingException();
        }

        // (3) the working file must not be open in the editor, and (4) it must
        // be replaceable in place. BEFORE any server call.
        if (_editorProbe.IsOpenForEditing(absoluteFilePath) || !ManagedFileGuard.CanReplaceInPlace(absoluteFilePath))
        {
            throw new DocumentOpenException(
                "Close this document in Inventor and run Undo Checkout again - the local file cannot be replaced while it is open.");
        }

        // (5) AUTHORITATIVE gate: a stale marker must not authorize a base
        // restore. Throws Conflict / Stale (and reconciles the marker).
        var status = await RequireMineAsync(manifest, entry, absoluteFilePath, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(status.BaseFileVersionId)
            && !string.Equals(status.BaseFileVersionId, binding.BaseFileVersionId, StringComparison.Ordinal))
        {
            TryReconcileStaleMarker(manifest, absoluteFilePath);
            throw new StaleCheckoutException(
                "The server checkout's base version does not match your local record. Run Get Latest to sync.");
        }

        // (6) download + fully verify the base version into staging. Any
        // failure throws here and the server checkout is NEVER released.
        var staged = await Restorer()
            .StageBaseVersionAsync(workspaceRoot, binding.BaseFileVersionId, binding.BaseChecksum, binding.BaseFileSize, ct)
            .ConfigureAwait(false);

        // (7) RE-CHECK the target still exists IMMEDIATELY before the server
        // release - it may have been deleted since check (2). If so: discard
        // the staged bytes, do NOT release the checkout, do NOT recreate the
        // file, leave the manifest marker and read-only state untouched.
        if (!File.Exists(absoluteFilePath))
        {
            ManagedFileRestorer.Discard(staged);
            throw new UndoTargetMissingException();
        }

        // (8) release the server checkout.
        try
        {
            await Checkout().UndoAsync(entry.CadDocumentId, reason, ct).ConfigureAwait(false);
        }
        catch (ArchApiException ex) when (ex.Kind == ArchApiFailureKind.Conflict)
        {
            // 409 = the checkout no longer exists, but WHY is AMBIGUOUS: a
            // lost prior-undo response, an admin unlock, or a check-in that
            // already created a newer version. Restoring the captured base
            // could overwrite the user's file with a stale version. So:
            // do NOT restore, do NOT claim success - retain the verified
            // staged bytes, reconcile the marker, direct to Get Latest.
            var recovery = TryPreserveRecovery(workspaceRoot, staged, absoluteFilePath);
            TryReconcileStaleMarker(manifest, absoluteFilePath);
            return new UndoOperationResult(
                Restored: false, RestoredToVersionNumber: 0, RecoveryFilePath: recovery, ManifestReconciled: false,
                Message: "The server shows this document is no longer checked out - it may have been checked in "
                    + "by someone, released, or admin-unlocked. Your local file was NOT changed. "
                    + (recovery is null ? "" : "A verified copy of the version you had checked out is at \"" + recovery + "\". ")
                    + "Run Get Latest to sync to the authoritative current version.");
        }
        catch
        {
            ManagedFileRestorer.Discard(staged);
            throw;
        }

        // (9/10) atomically replace the working file with the verified base,
        // then mark it controlled.
        try
        {
            ManagedFileRestorer.Promote(staged, absoluteFilePath);
        }
        catch (RestorePromoteException ex)
        {
            // The server released the checkout but the file could not be
            // written. It must NOT stay labelled managed-current.
            var recovery = TryPreserveRecovery(workspaceRoot, staged, absoluteFilePath);
            TryReconcileStaleMarker(manifest, absoluteFilePath);
            return new UndoOperationResult(
                Restored: false, RestoredToVersionNumber: 0, RecoveryFilePath: recovery, ManifestReconciled: false,
                Message: "Your checkout was released on the server, but the authoritative version could not be "
                    + "written to disk (" + ex.Message + "). The local file may still contain your edits and is NOT "
                    + "the managed current version. "
                    + (recovery is null ? "Run Get Latest to restore it."
                        : "A verified copy of the base version is at \"" + recovery + "\". Run Get Latest to restore it."));
        }

        // (11) manifest -> base version, Verified, checkout cleared. Observable.
        var reconciled = TrySave(() => manifest.RebindToNewVersion(
            absoluteFilePath, binding.BaseFileVersionId, binding.BaseVersionNumber,
            binding.BaseChecksum, binding.BaseFileSize, _clock(),
            state: WorkspaceManifestEntryState.Verified));

        return new UndoOperationResult(
            Restored: true, RestoredToVersionNumber: binding.BaseVersionNumber, RecoveryFilePath: null,
            ManifestReconciled: reconciled,
            Message: reconciled
                ? $"Checkout released. The local file was restored to version {binding.BaseVersionNumber} and is read-only again."
                : $"The local file was restored to version {binding.BaseVersionNumber} and made read-only, but the "
                  + "workspace record could not be saved. Run Get Latest to reconcile.");
    }

    // ================================================================
    // helpers
    // ================================================================

    private (WorkspaceManifest Manifest, WorkspaceManifestEntry Entry) ResolveManaged(
        string workspaceRoot, string absoluteFilePath)
    {
        SafeWorkspacePath.RequireAbsoluteRoot(workspaceRoot);
        if (string.IsNullOrWhiteSpace(absoluteFilePath) || !Path.IsPathFullyQualified(absoluteFilePath))
        {
            throw new NotManagedException();
        }
        var manifest = WorkspaceManifest.LoadOrEmpty(workspaceRoot);
        var entry = manifest.FindByAbsolutePath(absoluteFilePath)
            ?? throw new NotManagedException();
        return (manifest, entry);
    }

    /// <summary>
    /// The authoritative check before a destructive op: the SERVER must say
    /// this document is checked out by THIS session. A stale local marker is
    /// reconciled (-> Unverified, marker cleared) and the op is refused.
    /// </summary>
    private async Task<ServerCheckoutStatus> RequireMineAsync(
        WorkspaceManifest manifest, WorkspaceManifestEntry entry, string absoluteFilePath, CancellationToken ct)
    {
        ServerCheckoutStatus status = await Checkout().GetStatusAsync(entry.CadDocumentId, ct).ConfigureAwait(false);

        switch (status.State)
        {
            case ServerCheckoutState.Locked:
                TryReconcileStaleMarker(manifest, absoluteFilePath);
                throw new CheckoutConflictException(status.HolderName, status.HolderEmail);

            case ServerCheckoutState.Available:
                TryReconcileStaleMarker(manifest, absoluteFilePath);
                throw new StaleCheckoutException(
                    "The server shows this document is NOT checked out - your local checkout record was stale. "
                    + "Run Get Latest to sync.");

            case ServerCheckoutState.Mine:
                if (entry.Checkout is { CheckoutId.Length: > 0 } local
                    && status.CheckoutId is { Length: > 0 }
                    && !string.Equals(local.CheckoutId, status.CheckoutId, StringComparison.Ordinal))
                {
                    TryReconcileStaleMarker(manifest, absoluteFilePath);
                    throw new StaleCheckoutException(
                        "The server checkout does not match your local record (a different checkout). Run Get Latest to sync.");
                }
                return status;

            default:
                throw new StaleCheckoutException("Could not determine the server checkout status. Run Get Latest to sync.");
        }
    }

    /// <summary>Best-effort: downgrade a stale local marker to
    ///  Unverified + no checkout. Swallows a persistence failure (the caller
    ///  is already aborting with an error).</summary>
    private static void TryReconcileStaleMarker(WorkspaceManifest manifest, string absoluteFilePath)
    {
        try { manifest.MarkUnverified(absoluteFilePath, clearCheckout: true); }
        catch (WorkspaceManifestPersistException) { /* the abort message already tells the user to Get Latest */ }
    }

    /// <summary>Run a manifest mutation whose persistence is now observable.
    ///  Returns true only if an entry matched AND the durable save succeeded.</summary>
    private static bool TrySave(Func<bool> mutate)
    {
        try { return mutate(); }
        catch (WorkspaceManifestPersistException) { return false; }
    }

    /// <summary>
    /// Try to move the (verified) staged bytes into
    /// <c>.arch/.recovery</c> under a collision-resistant, contained name. If
    /// the move fails, KEEP the staged file where it is and return that path -
    /// never delete the only verified copy just to tidy up.
    /// </summary>
    private static string? TryPreserveRecovery(string workspaceRoot, StagedRestore staged, string targetPath)
    {
        if (!File.Exists(staged.StagedPath))
        {
            return null;
        }
        try
        {
            var recoveryDir = Path.Combine(Path.GetFullPath(workspaceRoot), ".arch", ".recovery");
            Directory.CreateDirectory(recoveryDir);

            var name = Path.GetFileName(targetPath);
            var stem = string.IsNullOrWhiteSpace(name) ? "recovered" : Path.GetFileNameWithoutExtension(name);
            var ext = string.IsNullOrWhiteSpace(name) ? "" : Path.GetExtension(name);
            var dest = Path.GetFullPath(Path.Combine(
                recoveryDir, stem + ".recovery-" + Guid.NewGuid().ToString("N") + ext));

            var guard = recoveryDir.EndsWith(Path.DirectorySeparatorChar)
                ? recoveryDir
                : recoveryDir + Path.DirectorySeparatorChar;
            if (!dest.StartsWith(guard, StringComparison.OrdinalIgnoreCase))
            {
                return staged.StagedPath; // never escape the recovery dir
            }

            File.Move(staged.StagedPath, dest, overwrite: false); // never clobber an existing recovery file
            return dest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the verified staged bytes exactly where they are.
            return File.Exists(staged.StagedPath) ? staged.StagedPath : null;
        }
    }
}

// ---- operation results -----------------------------------------------

public sealed record CheckoutOperationResult(
    string CadDocumentId, string BaseFileVersionId, int BaseVersionNumber,
    bool MadeWritable, bool ManifestUpdated, bool ReconcileNeeded, string Message);

public sealed record CheckInOperationResult(
    bool Verified, string? NewFileVersionId, int NewVersionNumber, string Message,
    bool ManifestUpdated = true, bool MadeControlled = true);

public sealed record UndoOperationResult(
    bool Restored, int RestoredToVersionNumber, string? RecoveryFilePath, string Message,
    bool ManifestReconciled = true);

// ---- orchestration errors -------------------------------------------

/// <summary>The target file is not bound in the verified workspace manifest.</summary>
public sealed class NotManagedException()
    : Exception("This file is not a managed CAD document in a known workspace. Run Get Latest first.");

/// <summary>The manifest entry is Unverified - no trustworthy base to check
///  out against.</summary>
public sealed class NotVerifiedException()
    : Exception("This file has not been verified against the server version. Run Get Latest before checking it out.");

/// <summary>No local checkout marker for this file (undo needs the base pin).</summary>
public sealed class NotCheckedOutLocallyException(string message) : Exception(message);

/// <summary>
/// The exact manifest-bound local file is not present on disk at Undo time
/// (checked before staging AND again immediately before the server release -
/// TOCTOU). Undo does NOT release the checkout and does NOT recreate the file;
/// the server checkout is preserved and the workspace is left untouched. Only
/// Get Latest should repair a missing managed file.
/// </summary>
public sealed class UndoTargetMissingException()
    : Exception("The local file to undo is missing from the workspace. Your checkout was NOT released. "
        + "Run Get Latest to restore the file, then Undo Checkout if you still need to.");

/// <summary>The authoritative server checkout status does not permit the
///  operation (available, or a different checkout) - the local marker was
///  stale and has been reconciled.</summary>
public sealed class StaleCheckoutException(string message) : Exception(message);

/// <summary>The working file is open in the editor / otherwise locked -
///  thrown BEFORE the server checkout is released.</summary>
public sealed class DocumentOpenException(string message) : Exception(message);
