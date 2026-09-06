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
            }),
            store: new DpapiSessionStore());

        _connection.StateChanged += _ => OnUi(RefreshEnablement);
        _connection.SessionChanged += _ => OnUi(RefreshEnablement);

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
        var map = RibbonCommandPolicy.Evaluate(_connection.State, _tracker.Current);
        _ribbon.ApplyEnablement(map);
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

            default:
                // Never fake a PDM result. Say plainly it is not built yet.
                Info($"'{command.DisplayName()}' is not available yet. Coming in a later release.");
                break;
        }
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
