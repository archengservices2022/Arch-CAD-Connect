using System.Windows.Forms;

using Arch.CadConnect.Api;
using Arch.CadConnect.Api.Workspace;
using Arch.CadConnect.Core;
using Arch.CadConnect.Core.Connection;
using Arch.CadConnect.Core.Documents;
using Arch.CadConnect.Core.Ribbon;
using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;
using Arch.CadConnect.Inventor.Documents;
using Arch.CadConnect.Inventor.Ribbon;
using Arch.CadConnect.Inventor.Ui;

using InventorApi = Inventor;

namespace Arch.CadConnect.Inventor;

/// <summary>
/// The add-in's brain. Owns the connection manager, the ribbon, and the
/// document observer, and keeps the ribbon in sync with
/// <see cref="ConnectionState"/> + active document via the pure
/// <see cref="RibbonCommandPolicy"/>.
///
/// All Inventor/COM/UI work happens on Inventor's UI thread. Network work runs
/// on the thread pool and its results are marshalled back through a hidden
/// WinForms control created on the UI thread.
/// </summary>
internal sealed class ArchAddInController : IDisposable
{
    private readonly InventorApi.Application _application;
    private readonly ActiveDocumentTracker _tracker = new();
    private readonly Control _uiMarshal = new();
    private readonly ArchConnectionManager _connection;
    private readonly RibbonFactory _ribbon;
    private readonly InventorDocumentObserver _documents;

    /// <summary>P4C: the most-recently-active exact checkout target, kept so
    ///  Undo stays reachable after its document is closed. Re-validated on
    ///  every ribbon refresh; the destructive path re-checks it fully.</summary>
    private readonly UndoTargetMemory _undoTarget = new();

    private bool _disposed;

    public ArchAddInController(InventorApi.Application application)
    {
        _application = application;

        // Force the hidden control's handle so BeginInvoke works.
        _ = _uiMarshal.Handle;

        _connection = new ArchConnectionManager(
            apiFactory: server => ArchApiClient.Create(server, new ArchApiClientOptions
            {
                ClientLabel = ClientLabel(),
                Timeout = TimeSpan.FromSeconds(30),
                EditorProbe = new InventorEditorDocumentProbe(_application),
            }),
            store: new DpapiSessionStore());

        _connection.StateChanged += _ => OnUi(RefreshEnablement);
        _connection.SessionChanged += session => OnUi(() =>
        {
            // Sign-out (null session) forgets any remembered Undo target.
            if (session is null)
            {
                _undoTarget.Clear();
            }
            RefreshEnablement();
        });

        _ribbon = new RibbonFactory(_application);
        _ribbon.CommandInvoked += OnCommandInvoked;
        _ribbon.Build();

        _documents = new InventorDocumentObserver(_application, _tracker, KnownWorkspaceRoots);
        _tracker.Changed += _ => OnUi(RefreshEnablement);

        RefreshEnablement();

        // Try to restore a prior session without blocking start-up.
        RunBackground(async ct => await _connection.TryRestoreAsync(ct), title: null);
    }

    // ---- ribbon <-> state --------------------------------------------

    private void RefreshEnablement()
    {
        var role = _connection.CurrentSession?.Identity.Role;
        var doc = _tracker.Current;

        // P4C: remember an exact checked-out target while it is active, and
        // drop it the moment it is no longer a live checkout in the current
        // workspace (successful check-in / undo clear the manifest marker;
        // a workspace change / identity drift / missing file also invalidate).
        _undoTarget.Observe(doc);
        _undoTarget.Revalidate(
            entryLookup: LoadManifestEntry,
            fileExists: SafeFileExists,
            currentWorkspaceRoot: SafeLastWorkspaceRoot());

        var map = RibbonCommandPolicy.Evaluate(
            _connection.State, doc, role, hasRememberedUndoTarget: _undoTarget.Current is not null);
        _ribbon.ApplyEnablement(map);
    }

    private static WorkspaceManifestEntry? LoadManifestEntry(ManagedFileRef file)
    {
        try
        {
            return WorkspaceManifest.LoadOrEmpty(file.WorkspaceRoot).FindByAbsolutePath(file.AbsoluteFilePath);
        }
        catch (Exception ex) when (ex is IOException or WorkspaceRootException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try { return !string.IsNullOrEmpty(path) && File.Exists(path); }
        catch { return false; }
    }

    private static string? SafeLastWorkspaceRoot()
    {
        try { return ConnectSettings.Load().LastWorkspaceRoot; }
        catch { return null; }
    }

    private void OnCommandInvoked(ArchCommand command)
    {
        switch (command)
        {
            case ArchCommand.SignIn:
                ShowSignInDialog();
                break;

            case ArchCommand.SignOut:
                RunBackground(async ct => await _connection.SignOutAsync(ct), "Signing out");
                break;

            case ArchCommand.ServerStatus:
                RunBackground(
                    async ct => await _connection.RefreshServerStatusAsync(ct),
                    "Checking server",
                    onDone: () => Info(ServerStatusText()));
                break;

            case ArchCommand.GetLatest:
                ShowGetLatestDialog();
                break;

            case ArchCommand.Checkout:
                RunCheckout();
                break;

            case ArchCommand.CheckIn:
                RunCheckIn();
                break;

            case ArchCommand.UndoCheckout:
                RunUndoCheckout();
                break;

            default:
                // Never fake a PDM result. Say plainly it is not built yet.
                Info($"'{command.DisplayName()}' is not available yet. Coming in a later release.");
                break;
        }
    }

    // ---- P4C: checkout / check-in / undo ----------------------------

    /// <summary>The active document as an exact managed-file reference, or null
    ///  (with a shown message) when it is not eligible.</summary>
    private ManagedFileRef? ActiveManagedFile()
    {
        var doc = _tracker.Current;
        if (doc.PlmIdentity is null || string.IsNullOrEmpty(doc.FullPath) || string.IsNullOrEmpty(doc.WorkspaceRoot))
        {
            Info("The active document is not a managed CAD document in a known workspace. Run Get Latest first.");
            return null;
        }
        return ManagedFileRef.Create(doc.WorkspaceRoot!, doc.FullPath!);
    }

    private void RunCheckout()
    {
        var file = ActiveManagedFile();
        if (file is null) return;

        CheckoutOperationResult? result = null;
        RunBackground(
            async ct => result = await _connection.CheckoutAsync(file, ct),
            "Checking out",
            onDone: () =>
            {
                if (result is not null)
                {
                    if (result.ReconcileNeeded) Error(result.Message); else Info(result.Message);
                }
            },
            onSettled: RefreshAfterOperation,
            timeout: TimeSpan.FromMinutes(2));
    }

    private void RunCheckIn()
    {
        var file = ActiveManagedFile();
        if (file is null) return;

        if (!_tracker.Current.IsSaved)
        {
            Info("Save the document in Inventor before checking it in.");
            return;
        }

        var confirm = MessageBox.Show(
            new Win32Owner(SafeMainHwnd()),
            "Upload the saved local file as a new version and release your checkout?",
            ArchAddInInfo.DisplayName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        CheckInOperationResult? result = null;
        RunBackground(
            async ct => result = await _connection.CheckInAsync(file, ct),
            "Checking in",
            onDone: () =>
            {
                if (result is not null)
                {
                    if (result.Verified) Info(result.Message); else Error(result.Message);
                }
            },
            onSettled: RefreshAfterOperation,
            timeout: TimeSpan.FromMinutes(15));
    }

    private void RunUndoCheckout()
    {
        var (file, label) = ResolveUndoTarget();
        if (file is null)
        {
            Info("There is no checked-out managed document to undo. Open the checked-out file, then try again.");
            return;
        }

        var confirm = MessageBox.Show(
            new Win32Owner(SafeMainHwnd()),
            $"Undo Checkout of \"{label}\" will DISCARD your local changes and restore the checked-out version. Continue?",
            ArchAddInInfo.DisplayName, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        UndoOperationResult? result = null;
        RunBackground(
            async ct => result = await _connection.UndoCheckoutAsync(file, reason: null, ct),
            "Undoing checkout",
            onDone: () =>
            {
                if (result is not null)
                {
                    if (result.Restored) Info(result.Message); else Error(result.Message);
                }
            },
            onSettled: RefreshAfterOperation,
            timeout: TimeSpan.FromMinutes(15));
    }

    /// <summary>Re-derive the active document and re-evaluate the ribbon after
    ///  any P4C operation (success OR failure). The manifest may have changed -
    ///  a released checkout marker, an Unverified downgrade - and
    ///  <see cref="RefreshEnablement"/> revalidates the remembered Undo target.</summary>
    private void RefreshAfterOperation()
    {
        try { _documents.RefreshFromActiveDocument(); } catch { /* COM teardown */ }
        RefreshEnablement();
    }

    /// <summary>
    /// The file Undo should act on: the active document if it is a managed file
    /// this session holds checked out; otherwise the most-recently-active
    /// remembered checkout target (so close-then-undo works). Never a filename
    /// guess - both come from an exact verified manifest binding, and the
    /// orchestrator re-checks identity + server state before acting.
    /// </summary>
    private (ManagedFileRef? File, string Label) ResolveUndoTarget()
    {
        var doc = _tracker.Current;
        if (doc.CheckoutState == LocalCheckoutState.CheckedOutByMe
            && doc.PlmIdentity is { } id
            && !string.IsNullOrEmpty(doc.FullPath) && !string.IsNullOrEmpty(doc.WorkspaceRoot))
        {
            return (ManagedFileRef.Create(doc.WorkspaceRoot!, doc.FullPath!),
                string.IsNullOrEmpty(id.DocumentNumber) ? id.CadDocumentId : id.DocumentNumber!);
        }
        if (_undoTarget.Current is { } target)
        {
            return (target.File, target.DocumentNumber);
        }
        return (null, "");
    }

    private void ShowGetLatestDialog()
    {
        string documentNumber;
        string workspaceRoot;
        using (var dialog = new GetLatestDialog())
        {
            if (dialog.ShowDialog(new Win32Owner(SafeMainHwnd())) != DialogResult.OK)
            {
                return;
            }
            documentNumber = dialog.RootDocumentNumber;
            workspaceRoot = dialog.WorkspaceRoot;
        }

        var request = new GetLatestRequest
        {
            Root = CadDocumentLookup.ByNumber(documentNumber),
            WorkspaceRoot = workspaceRoot,
        };

        MaterializationReport? report = null;
        RunBackground(
            async ct => report = await _connection.GetLatestAsync(request, ct),
            "Getting latest",
            onDone: () =>
            {
                // A now-managed open document should pick up its identity.
                _documents.RefreshFromActiveDocument();
                RefreshEnablement();
                if (report is not null)
                {
                    ShowGetLatestResult(report);
                }
            },
            timeout: TimeSpan.FromMinutes(15));
    }

    private void ShowGetLatestResult(MaterializationReport report)
    {
        var lines = new List<string> { report.ToSummaryLine() };

        var problems = report.Results
            .Where(r => r.Status is MaterializationStatus.Blocked or MaterializationStatus.Failed)
            .ToList();
        if (problems.Count > 0)
        {
            lines.Add("");
            foreach (var p in problems)
            {
                var name = p.RelativePath ?? p.DocumentNumber ?? p.CadDocumentId;
                lines.Add($"- {name}: {p.Status.ToString().ToLowerInvariant()}"
                    + (string.IsNullOrWhiteSpace(p.Reason) ? "" : $" ({p.Reason})"));
            }
        }

        var text = string.Join(Environment.NewLine, lines);
        if (report.ReachedValidState)
        {
            Info(text);
        }
        else
        {
            Error(text);
        }
    }

    private static IEnumerable<string?> KnownWorkspaceRoots()
    {
        var last = ConnectSettings.Load().LastWorkspaceRoot;
        return string.IsNullOrWhiteSpace(last) ? Array.Empty<string?>() : new[] { last };
    }

    private void ShowSignInDialog()
    {
        using var dialog = new SignInDialog(_connection);
        dialog.ShowDialog(new Win32Owner(SafeMainHwnd()));
        RefreshEnablement();
        if (_connection.State == ConnectionState.Connected)
        {
            Info($"Connected to {_connection.CurrentSession?.Identity.OrganizationName} as {_connection.CurrentSession?.Identity.Name}.");
        }
    }

    private string ServerStatusText() => _connection.State switch
    {
        ConnectionState.Connected =>
            $"Connected to {_connection.CurrentSession?.Server} as {_connection.CurrentSession?.Identity.Email}.",
        ConnectionState.Unauthorized => "Your session is no longer valid. Sign in again.",
        ConnectionState.ServerUnavailable => "The Arch PLM server could not be reached. Try again later.",
        _ => "Not signed in.",
    };

    // ---- threading helpers ------------------------------------------

    private void OnUi(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_uiMarshal.InvokeRequired)
            {
                _uiMarshal.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (ObjectDisposedException)
        {
            // Add-in unloaded mid-flight.
        }
        catch (InvalidOperationException)
        {
            // Handle not created / message loop gone.
        }
    }

    private void RunBackground(
        Func<CancellationToken, Task> work,
        string? title,
        Action? onDone = null,
        Action? onSettled = null,
        TimeSpan? timeout = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(45));
                await work(cts.Token).ConfigureAwait(false);
                if (onDone is not null)
                {
                    OnUi(onDone);
                }
            }
            catch (ArchApiException ex)
            {
                OnUi(() => Error(ex.Message));
            }
            catch (CheckoutConflictException ex)
            {
                OnUi(() => Error(ex.Message));
            }
            catch (NotManagedException ex)
            {
                OnUi(() => Info(ex.Message));
            }
            catch (NotVerifiedException ex)
            {
                OnUi(() => Info(ex.Message));
            }
            catch (NotCheckedOutLocallyException ex)
            {
                OnUi(() => Info(ex.Message));
            }
            catch (StaleCheckoutException ex)
            {
                OnUi(() => Error(ex.Message));
            }
            catch (DocumentOpenException ex)
            {
                OnUi(() => Error(ex.Message));
            }
            catch (UndoTargetMissingException ex)
            {
                OnUi(() => Error(ex.Message));
            }
            catch (RestoreVerificationException ex)
            {
                OnUi(() => Error("Undo Checkout was NOT performed - the base version could not be verified for restore. "
                    + "Your checkout is still active. (" + ex.Message + ")"));
            }
            catch (ContentDownloadRedirectException)
            {
                OnUi(() => Error("The server redirected a download request; refusing to follow. Your checkout is unchanged."));
            }
            catch (ContentDownloadHttpException ex)
            {
                OnUi(() => Error($"A download failed (HTTP {ex.Status}). Your checkout is unchanged."));
            }
            catch (ContentDownloadTimeoutException)
            {
                OnUi(() => Error("A download timed out. Your checkout is unchanged."));
            }
            catch (ArchServerUriException ex)
            {
                OnUi(() => Error(ex.Message));
            }
            catch (UnsafeWorkspacePlanException ex)
            {
                var detail = ex.Problems.Count > 0
                    ? Environment.NewLine + string.Join(Environment.NewLine, ex.Problems.Take(10).Select(p => $"- {p.Message}"))
                    : "";
                OnUi(() => Error(ex.Message + detail));
            }
            catch (UnsupportedWorkspaceContractException)
            {
                OnUi(() => Error("This Arch PLM server is a different version than this add-in supports."));
            }
            catch (WorkspaceRootException ex)
            {
                OnUi(() => Error(ex.Message));
            }
            catch (OperationCanceledException)
            {
                OnUi(() => Error("The operation timed out."));
            }
            catch (Exception)
            {
                OnUi(() => Error("Something went wrong. Please try again."));
            }
            finally
            {
                if (onSettled is not null)
                {
                    OnUi(onSettled);
                }
            }
        });
    }

    // ---- message boxes --------------------------------------------

    private void Info(string message) =>
        MessageBox.Show(new Win32Owner(SafeMainHwnd()), message, ArchAddInInfo.DisplayName,
            MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void Error(string message) =>
        MessageBox.Show(new Win32Owner(SafeMainHwnd()), message, ArchAddInInfo.DisplayName,
            MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private IntPtr SafeMainHwnd()
    {
        try
        {
            return new IntPtr(_application.MainFrameHWND);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static string ClientLabel()
    {
        try
        {
            return $"Inventor add-in on {Environment.MachineName}";
        }
        catch
        {
            return "Inventor add-in";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try { _documents.Dispose(); } catch { /* teardown */ }
        try { _ribbon.CommandInvoked -= OnCommandInvoked; _ribbon.Dispose(); } catch { /* teardown */ }
        try { _uiMarshal.Dispose(); } catch { /* teardown */ }
    }

    /// <summary>Wraps a Win32 HWND as an <see cref="IWin32Window"/> for dialog ownership.</summary>
    private sealed class Win32Owner(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
