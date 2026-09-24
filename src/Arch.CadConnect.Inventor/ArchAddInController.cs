using System.Windows.Forms;

using Arch.CadConnect.Api;
using Arch.CadConnect.Api.Workspace;
using Arch.CadConnect.Core;
using Arch.CadConnect.Core.Connection;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.Documents;
using Arch.CadConnect.Core.Files;
using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Ribbon;
using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;
using Arch.CadConnect.Inventor.Documents;
using Arch.CadConnect.Inventor.References;
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

    /// <summary>P6C: ONE guard for the controller's whole lifetime - a
    ///  double-click or a second "Apply Copy Design" invocation while one is
    ///  already running must be refused, not queued. In-process only; see
    ///  <see cref="Core.CopyDesign.Apply.CopyDesignApplyOperationGuard"/>'s own
    ///  doc comment for why a single global guard is sufficient here.</summary>
    private readonly Core.CopyDesign.Apply.CopyDesignApplyOperationGuard _copyDesignApplyGuard = new();

    /// <summary>P6D ROUND 2 (RESUME): the LAST Apply Copy Design attempt that
    ///  ended <see cref="Core.CopyDesign.Apply.CopyDesignApplyOperationResult.ReservedButUnmaterialized"/>
    ///  - i.e. genuinely resumable - remembered ONLY in this add-in
    ///  session's memory (never persisted to disk; does not survive an
    ///  Inventor restart - see the P6D report's "remaining limitations").
    ///  <see cref="ArchCommand.CopyDesignResume"/> is the ONLY thing that
    ///  ever reads this, and ONLY on an explicit, deliberate user action -
    ///  an ordinary "Apply Copy Design" never silently reinterprets itself
    ///  as a resume. Cleared once an attempt (fresh or resumed) fully
    ///  succeeds.</summary>
    private (Core.CopyDesign.CopyDesignPlan Plan, string IdempotencyKey, string? Label)? _resumableCopyDesignAttempt;

    /// <summary>P6D ROUND 3, item F: the LAST Apply Copy Design attempt whose
    ///  reservation call itself never returned a trustworthy answer (the
    ///  server may or may not have committed it - genuinely UNKNOWN),
    ///  remembered ONLY in this session's memory, exactly like
    ///  <see cref="_resumableCopyDesignAttempt"/> but for a CATEGORICALLY
    ///  DIFFERENT situation: here there is no confirmed
    ///  <c>copyDesignOperationId</c> at all, so the idempotency key ALONE is
    ///  the recovery handle. <see cref="ArchCommand.CopyDesignRecover"/> is
    ///  the ONLY thing that ever reads this, and ONLY on an explicit,
    ///  deliberate user action - never auto-retried beyond the orchestrator's
    ///  own existing bounded transport retry. Mutually exclusive with
    ///  <see cref="_resumableCopyDesignAttempt"/> for the SAME attempt (an
    ///  attempt is either "reservation confirmed, something later failed" or
    ///  "reservation itself unconfirmed" - never both); see
    ///  <see cref="ExecuteCopyDesignAttempt"/>'s <c>onDone</c> for the single
    ///  place both are written. Cleared once an attempt (fresh, resumed, or
    ///  recovered) fully succeeds, OR once a retry establishes a confirmed
    ///  reservation (which then becomes resumable instead, if still
    ///  unmaterialized).</summary>
    private (Core.CopyDesign.CopyDesignPlan Plan, string IdempotencyKey, string? Label)? _uncertainCopyDesignAttempt;

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
            _connection.State, doc, role,
            hasRememberedUndoTarget: _undoTarget.Current is not null,
            hasResumableCopyDesignAttempt: _resumableCopyDesignAttempt is not null,
            hasUncertainCopyDesignAttempt: _uncertainCopyDesignAttempt is not null);
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

            case ArchCommand.ScanReferences:
                RunScanReferences();
                break;

            case ArchCommand.ReferenceHealth:
                RunReferenceHealth();
                break;

            case ArchCommand.RepairReference:
                RunRepairReference();
                break;

            case ArchCommand.CopyDesignPreview:
                RunCopyDesignPreview();
                break;

            case ArchCommand.CopyDesignResume:
                RunCopyDesignResume();
                break;

            case ArchCommand.CopyDesignRecover:
                RunCopyDesignRecover();
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

    // ---- P5A / P5B-A: read-only reference intelligence --------------

    /// <summary>
    /// Report the references Inventor knows about for the active document
    /// (P5A). Purely observational: it reads
    /// <c>ReferencedDocumentDescriptors</c> and the local workspace manifest,
    /// and shows a text report. It never opens, activates, saves, or repairs
    /// any document, and never touches the server.
    /// </summary>
    private void RunScanReferences()
    {
        if (!TryScanActiveDocument("Scan References", out var scan))
        {
            return;
        }

        var text = CadReferenceScanTextReport.Render(scan);
        var status = scan.IsComplete ? "COMPLETE" : "PARTIAL";
        using var dialog = new ScanResultDialog(
            $"{ArchAddInInfo.DisplayName} - Scan References ({status})", text);
        dialog.ShowDialog(new Win32Owner(SafeMainHwnd()));
    }

    /// <summary>
    /// Diagnose every observed reference of the active document across two
    /// read-only dimensions:
    ///
    ///  - P5B-A LOCAL health: resolved / missing, inside / outside the
    ///    workspace, managed / unmanaged (exact manifest match only);
    ///  - P5B-B AUTHORITATIVE version status: CURRENT / STALE / UNKNOWN VERSION,
    ///    established ONLY by comparing the exact stable cadDocumentId + the
    ///    pinned local fileVersionId against the authenticated authoritative
    ///    Arch PLM server. Any unsafe / unavailable case fails closed to
    ///    UNKNOWN VERSION - CURRENT is never guessed.
    ///
    /// Read-only throughout: no document is opened, saved or repaired, no CAD
    /// reference is changed, nothing is checked out or checked in.
    /// </summary>
    private void RunReferenceHealth()
    {
        if (!TryScanActiveDocument("Reference Health", out var scan))
        {
            return;
        }

        var local = ReferenceHealthDiagnoser.Diagnose(scan);

        // The distinct stable ids of every exactly-managed reference edge - the
        // ONLY edges an authoritative version check applies to.
        var managedIds = local.Entries
            .Where(e => e.Management == ReferenceManagement.Managed
                && e.ManagedIdentity is { CadDocumentId.Length: > 0 })
            .Select(e => e.ManagedIdentity!.CadDocumentId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (managedIds.Length == 0)
        {
            ShowReferenceVersionReport(ReferenceVersionReport.Build(
                local, LatestVersionLookup.WholeFailure(LatestVersionOutcome.NotAttempted)));
            return;
        }

        LatestVersionLookup? lookup = null;
        RunBackground(
            async ct => lookup = await _connection.GetLatestVersionsAsync(managedIds, ct),
            "Checking versions",
            onDone: () => ShowReferenceVersionReport(ReferenceVersionReport.Build(
                local, lookup ?? LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable))),
            timeout: TimeSpan.FromMinutes(2));
    }

    private void ShowReferenceVersionReport(ReferenceVersionReport report)
    {
        var text = ReferenceVersionTextReport.Render(report);
        using var dialog = new ScanResultDialog(
            $"{ArchAddInInfo.DisplayName} - Reference Health ({report.OverallStatusLabel})", text);
        dialog.ShowDialog(new Win32Owner(SafeMainHwnd()));
    }

    // ---- P6A: Copy Design plan + preview (READ-ONLY, zero mutation) -----

    /// <summary>
    /// Builds a P6A Copy Design plan for the active document and shows it as a
    /// read-only preview. This command NEVER copies, renames, saves, replaces
    /// a reference, checks anything out/in, or creates a FileVersion / CAD
    /// document - it only reuses the EXISTING P5A scanner (COM read-only) and
    /// hands the result to the COM-free <see cref="CopyDesignPlanner"/>. There
    /// is no "Execute Copy Design" command in P6A.
    /// </summary>
    private void RunCopyDesignPreview()
    {
        if (!TryScanActiveDocument("Copy Design Preview", out var scan))
        {
            return;
        }

        using var input = new CopyDesignPreviewDialog();
        if (input.ShowDialog(new Win32Owner(SafeMainHwnd())) != DialogResult.OK)
        {
            return;
        }

        IDestinationNameRule nameRule;
        try
        {
            nameRule = new TokenReplaceNameRule(input.SourceToken, input.DestinationToken);
        }
        catch (ArgumentException ex)
        {
            Error($"Copy Design Preview could not build a naming rule: {ex.Message}");
            return;
        }

        // P6D DRAWING ASSOCIATION AUTHORITY: fetch the REAL server authority
        // (DRAWING_REFERENCE rows) for every candidate model BEFORE planning -
        // CopyDesignPlanner's IDrawingAssociationSource is synchronous, so the
        // one network round trip must happen here, not inside Plan(). Never
        // guesses from a scanner-discovered drawing node or a file name; a
        // network/auth/contract failure fails every candidate closed to
        // NotAvailable, exactly like the previous NoDrawingAssociationSource
        // stub - this only REPLACES "always unavailable" with "actually
        // asked, and unavailable only when the ask itself failed."
        var candidateIds = CopyDesignDrawingAssociationCandidates.From(scan);
        var destinationFolder = input.DestinationFolder;

        if (candidateIds.Count == 0)
        {
            ContinueCopyDesignPreview(scan, nameRule, destinationFolder,
                new PrefetchedDrawingAssociationSource(new Dictionary<string, DrawingAssociationResult>()));
            return;
        }

        var localManifest = WorkspaceRootLocator.FindRootForFile(_tracker.Current.FullPath) is { } localRoot
            ? WorkspaceManifest.LoadOrEmpty(localRoot)
            : null;

        IReadOnlyDictionary<string, DrawingAssociationResult>? associations = null;
        RunBackground(
            async ct => associations = await _connection.GetDrawingAssociationsAsync(candidateIds, localManifest, ct),
            "Checking drawing associations",
            onDone: () => ContinueCopyDesignPreview(scan, nameRule, destinationFolder,
                new PrefetchedDrawingAssociationSource(
                    associations ?? new Dictionary<string, DrawingAssociationResult>())),
            timeout: TimeSpan.FromSeconds(60));
    }

    private void ContinueCopyDesignPreview(
        CadReferenceScan scan,
        IDestinationNameRule nameRule,
        string destinationFolder,
        IDrawingAssociationSource drawingSource)
    {
        // The ONLY filesystem read this command performs: a best-effort,
        // read-only existence check of each proposed destination, scoped to
        // the destination folder the engineer just chose. Never a write.
        var plan = CopyDesignPlanner.Plan(
            scan,
            nameRule,
            destinationFolder,
            drawingSource: drawingSource,
            destinationExists: SafeFileExists);

        // Round 6: give the engineer the ONLY supported way to resolve a
        // NeedsDecision node whose sole blocker is an unavailable library/
        // shared classification signal - never applied automatically, never
        // pre-selected (see CopyDesignDecisionsDialog). Every OTHER
        // NeedsDecision reason (unresolved, unmanaged, unverified, a
        // conflict, an unsafe drawing association, an unsafe destination)
        // has no eligible node here and stays exactly as before: shown in
        // the read-only report, with no apply surface.
        IReadOnlyDictionary<string, CopyDesignAction>? appliedDecisions = null;
        if (!plan.IsExecutable)
        {
            var eligible = plan.Nodes.Where(CopyDesignExplicitDecisionEligibility.IsEligibleForExplicitDecision).ToArray();
            if (eligible.Length > 0)
            {
                using var decisions = new CopyDesignDecisionsDialog(eligible);
                if (decisions.ShowDialog(new Win32Owner(SafeMainHwnd())) == DialogResult.OK)
                {
                    appliedDecisions = decisions.Decisions.AsDictionary();
                    plan = CopyDesignPlanner.Plan(
                        scan,
                        nameRule,
                        destinationFolder,
                        drawingSource: drawingSource,
                        destinationExists: SafeFileExists,
                        explicitDecisions: appliedDecisions);
                }
            }
        }

        // Round 8: if the ONLY remaining blocker is drawing-association
        // completeness (drawings cannot be proven), offer the EXPLICIT
        // "model files only" acknowledgement. Probing with
        // acknowledgeModelFilesOnly is safe - CopyDesignPlanner.Plan is pure
        // and performs no mutation - and since it waives ONLY that one
        // blocker (see CopyDesignPlanner's own doc comment), a probe plan
        // that becomes executable PROVES completeness was the sole
        // remaining problem; any OTHER unresolved problem (NeedsDecision, a
        // collision, an unsafe edge, a missing identity) leaves the probe
        // non-executable too, and no acknowledgement is offered at all.
        if (!plan.IsExecutable)
        {
            var probe = CopyDesignPlanner.Plan(
                scan,
                nameRule,
                destinationFolder,
                drawingSource: drawingSource,
                destinationExists: SafeFileExists,
                explicitDecisions: appliedDecisions,
                acknowledgeModelFilesOnly: true);
            if (probe.IsExecutable)
            {
                using var modelOnly = new CopyDesignModelFilesOnlyDialog();
                if (modelOnly.ShowDialog(new Win32Owner(SafeMainHwnd())) == DialogResult.OK)
                {
                    plan = probe;
                }
            }
        }

        var text = CopyDesignPlanTextReport.Render(plan);
        var status = plan.IsExecutable ? "PREVIEW" : "PREVIEW - NOT EXECUTABLE";
        using (var dialog = new ScanResultDialog($"{ArchAddInInfo.DisplayName} - Copy Design Preview ({status})", text))
        {
            dialog.ShowDialog(new Win32Owner(SafeMainHwnd()));
        }

        // P6C: no apply surface at all for a non-executable plan - the
        // engineer must fix the plan (rename tokens, resolve NeedsDecision,
        // etc.) and preview again.
        if (!plan.IsExecutable)
        {
            return;
        }

        RunCopyDesignApply(plan);
    }

    /// <summary>
    /// P6C: the MANDATORY explicit confirmation + apply step, reachable ONLY
    /// from an EXECUTABLE preview (see the caller). Never invoked
    /// automatically. A fresh idempotency key is minted for THIS confirmed
    /// attempt (never reused across a genuinely new confirmation) via
    /// <see cref="Core.CopyDesign.Apply.CopyDesignApplyAttempt.StartNewAttempt"/>.
    /// All orchestration (mapping, revalidation, reservation, response
    /// validation, execution order, journal/cleanup, verification,
    /// materialization) is <see cref="Core.CopyDesign.Apply.CopyDesignApplyOrchestrator"/>
    /// - this method only wires the REAL Inventor/HTTP adapters to it and
    /// shows the result. Ends at READY FOR MANUAL INVENTOR TEST (or a clear
    /// failure/cleanup report) - it never auto-accepts or releases anything.
    /// </summary>
    private void RunCopyDesignApply(CopyDesignPlan plan)
    {
        using var confirm = new CopyDesignApplyConfirmDialog(plan);
        if (confirm.ShowDialog(new Win32Owner(SafeMainHwnd())) != DialogResult.OK)
        {
            return;
        }

        if (_connection.State != ConnectionState.Connected)
        {
            Error("Sign in to Arch PLM before applying a Copy Design plan.");
            return;
        }

        // A fresh, explicit confirmation ALWAYS mints a brand-new
        // idempotency key - this is precisely what distinguishes an
        // ordinary Apply from a RESUME/RECOVER (see RunCopyDesignResume /
        // RunCopyDesignRecover below) and is never silently reinterpreted
        // either way. P6D ROUND 3: a brand-new key also means a normal Apply
        // can NEVER accidentally consume an outstanding
        // _uncertainCopyDesignAttempt's key - that key is only ever reused by
        // an explicit Recover.
        var attempt = Core.CopyDesign.Apply.CopyDesignApplyAttempt.StartNewAttempt();
        ExecuteCopyDesignAttempt(plan, attempt.IdempotencyKey, label: null, CopyDesignAttemptKind.Fresh);
    }

    /// <summary>
    /// P6D ROUND 2, HIGH fix (RESUME): the deliberate, explicit, controlled
    /// retry of the LAST Apply Copy Design attempt. Two paths, chosen ONLY
    /// by whether this session still remembers one in memory:
    ///
    ///   - <see cref="_resumableCopyDesignAttempt"/> present (the ORIGINAL,
    ///     unchanged in-session path): reuses the EXACT SAME plan and
    ///     idempotencyKey as the failed attempt (never a fresh key - see
    ///     <see cref="Core.CopyDesign.Apply.CopyDesignApplyAttempt.Resume"/>).
    ///
    ///   - P6D PRODUCTION RECOVERY: no in-memory attempt (e.g. Inventor was
    ///     restarted since the failure) - <see cref="RunDurableCopyDesignResume"/>
    ///     offers a DURABLE resume by explicit operation id, reconstructing
    ///     the SAME attempt from the server's own authoritative status (see
    ///     <see cref="Core.CopyDesign.Apply.CopyDesignResumeAttemptReconstructor"/>'s
    ///     own doc comment) rather than being lost with the session.
    ///
    /// Either way, the server's already-idempotent reservation resolves to
    /// the SAME operation/entries/resultingCadDocumentIds, never a duplicate
    /// CadDocument - see <see cref="Core.CopyDesign.Apply.CopyDesignApplyOrchestrator"/>'s
    /// own class doc comment ("RESUME") for the full mechanism.
    /// </summary>
    private void RunCopyDesignResume()
    {
        if (_resumableCopyDesignAttempt is not { } resumable)
        {
            RunDurableCopyDesignResume();
            return;
        }

        using var confirm = new CopyDesignResumeConfirmDialog(resumable.Plan, resumable.IdempotencyKey);
        if (confirm.ShowDialog(new Win32Owner(SafeMainHwnd())) != DialogResult.OK)
        {
            return;
        }

        if (_connection.State != ConnectionState.Connected)
        {
            Error("Sign in to Arch PLM before resuming a Copy Design attempt.");
            return;
        }

        ExecuteCopyDesignAttempt(resumable.Plan, resumable.IdempotencyKey, resumable.Label, CopyDesignAttemptKind.Resume);
    }

    /// <summary>
    /// P6D PRODUCTION RECOVERY: the DURABLE resume path - reachable ONLY
    /// when this session has no in-memory <see cref="_resumableCopyDesignAttempt"/>.
    /// Collects an EXPLICIT operation id + source workspace + destination
    /// folder (never a silent scan of every operation, never a guess - see
    /// <see cref="CopyDesignDurableResumeDialog"/>'s own doc comment), then
    /// queries the server's authoritative status for EXACTLY that operation
    /// id and hands it to <see cref="Core.CopyDesign.Apply.CopyDesignResumeAttemptReconstructor"/>.
    /// Fails closed (a plain, honest message - never a guess, never a
    /// partial reconstruction) at any point the server status, the source
    /// workspace, or the drawing-dependency authority cannot fully prove the
    /// attempt is safe to continue.
    /// </summary>
    private void RunDurableCopyDesignResume()
    {
        if (_connection.State != ConnectionState.Connected)
        {
            Error("Sign in to Arch PLM before resuming a Copy Design attempt.");
            return;
        }

        using var input = new CopyDesignDurableResumeDialog();
        if (input.ShowDialog(new Win32Owner(SafeMainHwnd())) != DialogResult.OK)
        {
            return;
        }

        // Load the SOURCE workspace manifest up front - local, synchronous,
        // read-only I/O - so an obviously wrong folder fails fast, before
        // any network call at all.
        WorkspaceManifest sourceManifest;
        try
        {
            sourceManifest = WorkspaceManifest.LoadOrEmpty(input.SourceWorkspaceRoot);
        }
        catch (Exception ex) when (ex is IOException or WorkspaceRootException or UnauthorizedAccessException)
        {
            Error($"Could not read the source workspace folder: {ex.Message}");
            return;
        }
        if (sourceManifest.Entries.Count == 0)
        {
            Error("The source workspace folder has no recognized managed workspace manifest "
                + "(.arch\\workspace.json) - choose the folder Get Latest originally materialized these "
                + "documents into.");
            return;
        }

        var operationId = input.OperationId;
        var destinationFolder = input.DestinationFolder;

        Core.CopyDesign.Apply.CopyDesignDurableResumeStatusResult? status = null;
        RunBackground(
            async ct => status = await _connection.GetDurableCopyDesignOperationStatusAsync(operationId, ct),
            "Looking up Copy Design operation",
            onDone: () => ContinueDurableCopyDesignResume(status, sourceManifest, destinationFolder),
            timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>Second step of the durable resume: the operation's
    ///  authoritative status is now known - fetch the AUTHORITATIVE drawing
    ///  dependency mapping (the SAME drawing-association authority the P6D
    ///  live-blocker fix already uses) for whichever model entries this
    ///  operation actually reserved, so a pending drawing entry's rewiring
    ///  target is never guessed from a file name.</summary>
    private void ContinueDurableCopyDesignResume(
        Core.CopyDesign.Apply.CopyDesignDurableResumeStatusResult? status,
        WorkspaceManifest sourceManifest,
        string destinationFolder)
    {
        if (status is null || !status.Success)
        {
            Error("Could not obtain the authoritative status of this Copy Design operation "
                + $"({status?.Outcome.ToString() ?? "unknown"}) - nothing was resumed. Confirm the operation id and "
                + "try again.");
            return;
        }

        var modelSourceIds = (status.Entries ?? Array.Empty<Core.CopyDesign.Apply.CopyDesignDurableResumeEntry>())
            .Where(e => e.Action == "COPY" && e.SourceCadDocumentId is not null && e.OriginalDocumentType is "IAM" or "IPT")
            .Select(e => e.SourceCadDocumentId!)
            .Distinct()
            .ToArray();

        IReadOnlyDictionary<string, Core.CopyDesign.DrawingAssociationResult>? associations = null;
        RunBackground(
            async ct => associations = modelSourceIds.Length == 0
                ? new Dictionary<string, Core.CopyDesign.DrawingAssociationResult>()
                : await _connection.GetDrawingAssociationsAsync(modelSourceIds, sourceManifest, ct),
            "Checking drawing associations",
            onDone: () => FinishDurableCopyDesignResume(status, sourceManifest, destinationFolder, associations),
            timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>Final step: reconstruct the exact original attempt (fails
    ///  closed with an honest reason on anything unsafe/ambiguous) and, on
    ///  success, run it through the EXACT SAME confirmation + execution path
    ///  as an in-session resume - never a separate, less-safe path.</summary>
    private void FinishDurableCopyDesignResume(
        Core.CopyDesign.Apply.CopyDesignDurableResumeStatusResult status,
        WorkspaceManifest sourceManifest,
        string destinationFolder,
        IReadOnlyDictionary<string, Core.CopyDesign.DrawingAssociationResult>? associations)
    {
        var operationModelSourceIds = new HashSet<string>(
            (status.Entries ?? Array.Empty<Core.CopyDesign.Apply.CopyDesignDurableResumeEntry>())
                .Where(e => e.Action == "COPY" && e.SourceCadDocumentId is not null && e.OriginalDocumentType is "IAM" or "IPT")
                .Select(e => e.SourceCadDocumentId!));

        // Invert "model -> its drawings" (what the authority answers) into
        // "drawing -> the models (from THIS operation only) it depends on" -
        // what the reconstructor needs. A drawing/model pair the authority
        // names that is NOT part of this operation is silently excluded
        // here (never trusted) - the reconstructor itself still fails
        // closed if a PENDING drawing ends up with no confirmed dependency.
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (associations is not null)
        {
            foreach (var (modelSourceId, result) in associations)
            {
                if (result.Outcome != Core.CopyDesign.DrawingAssociationOutcome.Found
                    || !operationModelSourceIds.Contains(modelSourceId))
                {
                    continue;
                }
                foreach (var drawing in result.Drawings)
                {
                    if (drawing.CadDocumentId is null)
                    {
                        continue;
                    }
                    if (!dependencies.TryGetValue(drawing.CadDocumentId, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        dependencies[drawing.CadDocumentId] = set;
                    }
                    set.Add(modelSourceId);
                }
            }
        }

        var reconstruction = Core.CopyDesign.Apply.CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, sourceManifest, destinationFolder,
            dependencies.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal));
        if (!reconstruction.Success)
        {
            Error($"Could not safely reconstruct this Copy Design attempt: {reconstruction.FailureReason}");
            return;
        }

        // P6D DURABLE RESUME LIVE BLOCKER fix: an EARLY, clear, itemized
        // check for the single most common way a durable resume fails -
        // the destination folder the user just typed does not actually
        // hold the file(s) the server already reports MATERIALIZED. Left
        // to the real orchestrator alone, this surfaces later as a single,
        // terse UnexpectedMaterializationState reason for only the FIRST
        // such entry, inside a result dialog whose text box does not wrap
        // long lines - easy to misread as truncated. This check changes
        // NOTHING about what is allowed to proceed: it is purely additive,
        // read-only, and the orchestrator's own authoritative destination +
        // hash/size re-verification (immediately before any mutation) still
        // runs completely unchanged right after this.
        var missingMaterialized = Core.CopyDesign.Apply.CopyDesignResumeAttemptReconstructor.FindMissingMaterializedDestinations(
            status, destinationFolder, File.Exists);
        if (missingMaterialized.Count > 0)
        {
            var list = string.Join(Environment.NewLine, missingMaterialized.Select(m => $"  - {m.OriginalFileName}"
                + Environment.NewLine + $"      expected at: {m.ExpectedPath}"));
            Error("The server already reports the following file(s) as materialized by this Copy Design operation, "
                + $"but they were not found in the destination folder you entered (\"{destinationFolder}\"):"
                + Environment.NewLine + Environment.NewLine + list
                + Environment.NewLine + Environment.NewLine
                + "This almost always means the destination folder does not match the one the original attempt "
                + "copied into. Re-enter the exact original destination folder and try again - nothing was changed.");
            return;
        }

        using var confirm = new CopyDesignResumeConfirmDialog(reconstruction.Plan!, reconstruction.IdempotencyKey!);
        if (confirm.ShowDialog(new Win32Owner(SafeMainHwnd())) != DialogResult.OK)
        {
            return;
        }
        if (_connection.State != ConnectionState.Connected)
        {
            Error("Sign in to Arch PLM before resuming a Copy Design attempt.");
            return;
        }

        ExecuteCopyDesignAttempt(reconstruction.Plan!, reconstruction.IdempotencyKey!, label: null, CopyDesignAttemptKind.Resume);
    }

    /// <summary>
    /// P6D ROUND 3, item F: the deliberate, explicit, controlled RETRY of the
    /// LAST Apply Copy Design attempt whose reservation call itself never
    /// returned a trustworthy answer (<see cref="_uncertainCopyDesignAttempt"/>) -
    /// reachable ONLY via its own dedicated ribbon command, NEVER offered or
    /// triggered automatically. Replays the EXACT SAME plan and
    /// idempotencyKey (never a fresh key - the idempotency key alone is the
    /// recovery handle even though no operationId was ever confirmed): if the
    /// server had already committed the reservation, the SAME operation is
    /// returned; if not, it is safely created now. A normal Apply never
    /// consumes this key (it always mints its own), so recovering never
    /// collides with unrelated work.
    /// </summary>
    private void RunCopyDesignRecover()
    {
        if (_uncertainCopyDesignAttempt is not { } uncertain)
        {
            Info("There is no Copy Design attempt to recover in this session. Recover is only offered after an "
                + "Apply Copy Design attempt's reservation call itself failed without a confirmed answer from the "
                + "server (the outcome is genuinely unknown).");
            return;
        }

        using var confirm = new CopyDesignRecoverConfirmDialog(uncertain.Plan, uncertain.IdempotencyKey);
        if (confirm.ShowDialog(new Win32Owner(SafeMainHwnd())) != DialogResult.OK)
        {
            return;
        }

        if (_connection.State != ConnectionState.Connected)
        {
            Error("Sign in to Arch PLM before recovering a Copy Design attempt.");
            return;
        }

        ExecuteCopyDesignAttempt(uncertain.Plan, uncertain.IdempotencyKey, uncertain.Label, CopyDesignAttemptKind.Recover);
    }

    /// <summary>Distinguishes the THREE ways <see cref="ExecuteCopyDesignAttempt"/>
    ///  can be entered - purely for status text / dialog titles and for
    ///  choosing which of <see cref="_resumableCopyDesignAttempt"/> /
    ///  <see cref="_uncertainCopyDesignAttempt"/> the triggering command read
    ///  from; the orchestration itself is IDENTICAL for all three (same
    ///  idempotency-key-based safety).</summary>
    private enum CopyDesignAttemptKind { Fresh, Resume, Recover }

    /// <summary>Shared orchestration for a fresh Apply, an explicit Resume,
    ///  and an explicit Recover - the ONLY difference between them is which
    ///  <paramref name="idempotencyKey"/> is supplied (fresh vs. reused) and
    ///  which confirmation dialog the caller already showed. Wires the REAL
    ///  Inventor/HTTP adapters (including, since P6D ROUND 2/3, the
    ///  operation-status client that makes RESUME/RECOVER safe for EVERY
    ///  call - a fresh attempt's reservation has nothing materialized yet, so
    ///  a well-formed status response naturally finds nothing to skip) and
    ///  shows the result. Ends at READY FOR MANUAL INVENTOR TEST (or a clear
    ///  failure/cleanup report) - it never auto-accepts or releases
    ///  anything.</summary>
    private void ExecuteCopyDesignAttempt(CopyDesignPlan plan, string idempotencyKey, string? label, CopyDesignAttemptKind kind)
    {
        var orchestrator = new Core.CopyDesign.Apply.CopyDesignApplyOrchestrator(
            _copyDesignApplyGuard,
            new ApplyCopyDesignReservationAdapter(_connection),
            new Inventor.CopyDesign.InventorCopyDesignPhysicalCopier(_application, new Core.CopyDesign.Apply.CopyDesignFileHasher()),
            new Inventor.CopyDesign.InventorCopyDesignReferenceRewirer(_application),
            new Inventor.CopyDesign.InventorCopyDesignVerifier(_application, new Core.CopyDesign.Apply.CopyDesignFileHasher()),
            new MaterializeCopyDesignAdapter(_connection),
            new Core.CopyDesign.Apply.CopyDesignFileHasher(),
            sourceExists: File.Exists,
            destinationExists: File.Exists,
            deleteFile: path => File.Delete(path),
            // HIGH 2 fix: Core has no HTTP dependency, so the ONLY place
            // that can classify a reservation-call exception as TRANSIENT
            // (worth the orchestrator's bounded, same-key, same-request
            // retry) versus a deterministic business/4xx rejection (never
            // retried) is here, where the real ArchApiException is visible.
            isTransientReservationFailure: ex => ex is ArchApiException apiEx && apiEx.IsServerUnavailable,
            maxReservationAttempts: 3,
            // P6D: the SAME InventorApi.Application drives the drawing
            // adapters too - the physical copier above already handles
            // IDW/DWG (see its own doc comment), so only reference rewiring
            // and verification need drawing-specific adapters (a drawing has
            // no ComponentOccurrence - see InventorCopyDesignDrawingReferenceRewirer's
            // doc comment for the exact API used instead).
            drawingRewirer: new Inventor.CopyDesign.InventorCopyDesignDrawingReferenceRewirer(_application),
            drawingVerifier: new Inventor.CopyDesign.InventorCopyDesignDrawingVerifier(_application, new Core.CopyDesign.Apply.CopyDesignFileHasher()),
            // P6D ROUND 3: always wired, for BOTH a fresh apply and an
            // explicit resume - a fresh reservation's targets have nothing
            // materialized yet, so a well-formed status response finds
            // nothing to skip and behavior is unaffected; it is the ONLY
            // thing that lets a LATER resume of THIS attempt skip already-
            // completed work. UNLIKE round 2's probe, a failure to obtain
            // this status now FAILS CLOSED (see
            // Core.CopyDesign.Apply.CopyDesignApplyOrchestrator's step 6b) -
            // so an ordinary fresh apply is only ever affected by this if the
            // status call itself fails immediately after a successful
            // reservation (a genuine transport/auth problem worth surfacing,
            // not silently swallowed).
            operationStatusClient: new CopyDesignOperationStatusAdapter(_connection));

        Core.CopyDesign.Apply.CopyDesignApplyOperationResult? result = null;
        RunBackground(
            async ct => result = await orchestrator.ExecuteAsync(plan, idempotencyKey, label, ct),
            kind switch
            {
                CopyDesignAttemptKind.Resume => "Resuming Copy Design",
                CopyDesignAttemptKind.Recover => "Recovering Copy Design",
                _ => "Applying Copy Design",
            },
            onDone: () =>
            {
                if (result is null)
                {
                    return;
                }

                // P6D ROUND 3, item F: the SINGLE place BOTH recovery fields
                // are ever written, so they stay mutually exclusive for the
                // SAME attempt and are never left stale:
                //   - a CONFIRMED reservation that did not fully materialize
                //     -> resumable (operationId known, entry-level recovery);
                //   - the reservation call itself never returned a
                //     trustworthy answer -> uncertain (no operationId at
                //     all, idempotency-key-only recovery);
                //   - anything else (including full success, or a failure
                //     BEFORE the reservation call was even attempted, which
                //     has nothing to safely retry via replay) -> clears
                //     whichever of the two applied to THIS attempt's prior
                //     state, since a fresh outcome supersedes it.
                if (result.ReservedButUnmaterialized)
                {
                    _resumableCopyDesignAttempt = (plan, idempotencyKey, label);
                    _uncertainCopyDesignAttempt = null;
                }
                else if (result.Outcome == Core.CopyDesign.Apply.CopyDesignApplyOutcome.ReservationCallFailed)
                {
                    _uncertainCopyDesignAttempt = (plan, idempotencyKey, label);
                    _resumableCopyDesignAttempt = null;
                }
                else
                {
                    _resumableCopyDesignAttempt = null;
                    _uncertainCopyDesignAttempt = null;
                }

                var text = Core.CopyDesign.Apply.CopyDesignApplyResultTextReport.Render(result);
                var verb = kind switch
                {
                    CopyDesignAttemptKind.Resume => "Resume Copy Design",
                    CopyDesignAttemptKind.Recover => "Recover Copy Design",
                    _ => "Apply Copy Design",
                };
                var title = $"{ArchAddInInfo.DisplayName} - {verb} ({result.Outcome})";
                using var dialog = new ScanResultDialog(title, text);
                dialog.ShowDialog(new Win32Owner(SafeMainHwnd()));
            },
            // P6D fix: Resume/Recover enablement depends on
            // _resumableCopyDesignAttempt / _uncertainCopyDesignAttempt,
            // which onDone above just updated - without this, the ribbon
            // keeps showing whatever it computed before this attempt ran
            // (e.g. both disabled) until some UNRELATED event happens to
            // trigger RefreshEnablement (a document change, a connection
            // state change). onSettled runs in RunBackground's `finally`, so
            // this refresh happens for every outcome - success, a thrown
            // exception, or a timeout - never only the happy path.
            onSettled: RefreshEnablement,
            timeout: TimeSpan.FromMinutes(30));
    }

    /// <summary>Thin adapter: <see cref="ArchConnectionManager"/> already owns
    ///  the current session/API factory - this just satisfies the Core-level
    ///  seam without Core ever depending on the Api project's connection
    ///  machinery.</summary>
    private sealed class ApplyCopyDesignReservationAdapter(ArchConnectionManager connection)
        : Core.CopyDesign.Apply.ICopyDesignReservationClient
    {
        public Task<Core.CopyDesign.Apply.CopyDesignReservationResponse> ApplyAsync(
            Core.CopyDesign.Apply.CopyDesignApplyRequest request, CancellationToken ct) =>
            connection.ApplyCopyDesignAsync(request, ct);
    }

    private sealed class MaterializeCopyDesignAdapter(ArchConnectionManager connection)
        : Core.CopyDesign.Apply.ICopyDesignMaterializer
    {
        public Task<Core.CopyDesign.Apply.CopyDesignMaterializationResult> MaterializeFirstFileVersionAsync(
            Core.CopyDesign.Apply.CopyDesignMaterializeRequest request, CancellationToken ct) =>
            connection.MaterializeFirstFileVersionAsync(request, ct);
    }

    /// <summary>P6D ROUND 3, HIGH fix (items D/E): thin adapter over
    ///  <see cref="ArchConnectionManager.GetCopyDesignOperationStatusAsync"/> -
    ///  the ONLY thing that lets the orchestrator's RESUME classification
    ///  consult the authoritative per-operation status endpoint.</summary>
    private sealed class CopyDesignOperationStatusAdapter(ArchConnectionManager connection)
        : Core.CopyDesign.Apply.ICopyDesignOperationStatusClient
    {
        public Task<Core.CopyDesign.Apply.CopyDesignOperationStatusResult> GetStatusAsync(
            Core.CopyDesign.Apply.CopyDesignOperationStatusExpectation expectation, CancellationToken ct) =>
            connection.GetCopyDesignOperationStatusAsync(expectation, ct);
    }

    // ---- P5C: controlled reference repair --------------------------

    /// <summary>
    /// Repair ONE stale managed reference of the active document through an
    /// explicit preview + confirmation:
    ///
    ///   scan -> diagnose (P5B-A) -> authoritative version status (P5B-B)
    ///     -> pick a STALE managed reference with an exact stable identity
    ///     -> resolve the authoritative latest FileVersion target from an
    ///        EXISTING verified managed-workspace copy (never a filename guess,
    ///        never a broad Get Latest)
    ///     -> preview -> explicit confirmation -> replace ONE reference via the
    ///        Inventor API -> rescan -> verify.
    ///
    /// P5C never saves, checks out, checks in, undoes, creates a FileVersion or
    /// revision, or runs a broad Get Latest. If the exact target cannot be
    /// proven, or the referencing document is not already writable, it fails
    /// closed and explains what the engineer must do.
    /// </summary>
    private void RunRepairReference()
    {
        if (!TryScanActiveDocument("Repair Reference", out var scan))
        {
            return;
        }

        var local = ReferenceHealthDiagnoser.Diagnose(scan);

        var managedIds = local.Entries
            .Where(e => e.Management == ReferenceManagement.Managed
                && e.ManagedIdentity is { CadDocumentId.Length: > 0 })
            .Select(e => e.ManagedIdentity!.CadDocumentId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (managedIds.Length == 0)
        {
            Info("Repair Reference found no managed references on this document. Nothing to repair.");
            return;
        }

        var activePath = _tracker.Current.FullPath!;
        var roots = WorkspaceRootsForDocument(activePath).Where(r => r is not null).Select(r => r!).ToList();
        var parentIds = local.Entries
            .Select(e => e.Reference.ParentIdentity?.CadDocumentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        LatestVersionLookup? lookup = null;
        IReadOnlyDictionary<string, ServerCheckoutStatus?> checkoutStatuses =
            new Dictionary<string, ServerCheckoutStatus?>();
        RunBackground(
            async ct =>
            {
                lookup = await _connection.GetLatestVersionsAsync(managedIds, ct);
                checkoutStatuses = await LoadCheckoutStatusesAsync(parentIds, ct);
            },
            "Checking versions",
            onDone: () => ContinueRepairReference(
                local,
                lookup ?? LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable),
                roots,
                checkoutStatuses),
            timeout: TimeSpan.FromMinutes(2));
    }

    private void ContinueRepairReference(
        ReferenceHealthReport local,
        LatestVersionLookup lookup,
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, ServerCheckoutStatus?> checkoutStatuses)
    {
        var report = ReferenceVersionReport.Build(local, lookup);

        var stale = report.Assessments
            .Where(a => a.Applicable && a.Status == PlmVersionStatus.Stale)
            .ToArray();

        if (stale.Length == 0)
        {
            Info("Repair Reference: no STALE managed reference was found. "
                + "Only a reference whose authoritative Arch PLM version is behind the latest can be repaired here.");
            return;
        }

        var locator = new WorkspaceManifestRepairTargetLocator(roots);
        var plans = stale
            .Select(a => ReferenceRepairPlanner.Plan(
                a,
                locator.Locate(
                    a.CadDocumentId ?? "",
                    a.AuthoritativeLatestFileVersionId ?? "",
                    a.AuthoritativeTargetFileSize,
                    a.AuthoritativeTargetSha256 ?? ""),
                BuildReferencingContext(a.Entry.Reference.ParentAbsolutePath, roots, checkoutStatuses)))
            .ToArray();

        ReferenceRepairPlan chosen;
        using (var dialog = new RepairReferenceDialog(
            $"{ArchAddInInfo.DisplayName} - Repair Reference", plans))
        {
            if (dialog.ShowDialog(new Win32Owner(SafeMainHwnd())) != DialogResult.OK
                || dialog.SelectedPlan is not { } selected)
            {
                return; // cancelled - zero CAD changes
            }
            chosen = selected;
        }

        if (!chosen.CanProceed)
        {
            Error(chosen.EligibilityLabel + " - " + chosen.EligibilityDetail);
            return;
        }

        BeginFinalRepairPreflight(chosen, roots);
    }

    private void BeginFinalRepairPreflight(ReferenceRepairPlan previewed, IReadOnlyList<string> roots)
    {
        var parentEntry = FindManifestEntry(previewed.ReferencingDocumentPath, roots);
        var parentIds = parentEntry is { CadDocumentId.Length: > 0 }
            ? new[] { parentEntry.CadDocumentId }
            : Array.Empty<string>();

        LatestVersionLookup? latest = null;
        IReadOnlyDictionary<string, ServerCheckoutStatus?> checkoutStatuses =
            new Dictionary<string, ServerCheckoutStatus?>();
        RunBackground(
            async ct =>
            {
                latest = await _connection.GetLatestVersionsAsync(new[] { previewed.CadDocumentId! }, ct);
                checkoutStatuses = await LoadCheckoutStatusesAsync(parentIds, ct);
            },
            "Revalidating repair",
            onDone: () => CompleteFinalRepairPreflight(
                previewed,
                roots,
                latest ?? LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable),
                checkoutStatuses),
            timeout: TimeSpan.FromMinutes(2));
    }

    private void CompleteFinalRepairPreflight(
        ReferenceRepairPlan previewed,
        IReadOnlyList<string> roots,
        LatestVersionLookup latest,
        IReadOnlyDictionary<string, ServerCheckoutStatus?> checkoutStatuses)
    {
        CadReferenceScan freshScan;
        try
        {
            freshScan = new InventorReferenceScanner(_application, roots)
                .Scan(previewed.ReferencingDocumentPath);
        }
        catch
        {
            Error("Repair Reference could not freshly re-scan the selected referencing document. No mutation was attempted.");
            return;
        }

        var matchingEdges = freshScan.References.Where(r =>
            PathEquals(r.ParentAbsolutePath, previewed.ReferencingDocumentPath)
            && PathEquals(r.ResolvedAbsolutePath, previewed.CurrentReferencePath)
            && r.RelationshipKind == previewed.RelationshipKind
            && r.ManifestIdentity is { IsVerified: true } identity
            && string.Equals(identity.CadDocumentId, previewed.CadDocumentId, StringComparison.Ordinal)
            && string.Equals(identity.FileVersionId, previewed.CurrentPinnedFileVersionId, StringComparison.Ordinal))
            .ToArray();

        if (matchingEdges.Length != 1)
        {
            Error("Repair Reference detected preview drift or an ambiguous reference. The exact parent/reference "
                + "relationship and stable identity no longer match; no mutation was attempted.");
            return;
        }

        var local = ReferenceHealthDiagnoser.Diagnose(freshScan);
        var freshEntry = local.Entries.Single(e => Equals(e.Reference, matchingEdges[0]));
        var assessment = ReferenceVersionClassifier.Assess(freshEntry, latest);
        if (assessment.Status != PlmVersionStatus.Stale
            || !string.Equals(assessment.AuthoritativeLatestFileVersionId,
                previewed.AuthoritativeTargetFileVersionId, StringComparison.Ordinal))
        {
            Error("Repair Reference detected an authoritative version change since preview. No mutation was attempted.");
            return;
        }

        // Re-read the manifest (stable local path mapping only) and re-hash the
        // target's current bytes against the SERVER-authoritative integrity
        // metadata immediately before the final confirmation. Persisted Verified
        // state and the mutable manifest checksum are never enough.
        var target = new WorkspaceManifestRepairTargetLocator(roots).Locate(
            assessment.CadDocumentId ?? "",
            assessment.AuthoritativeLatestFileVersionId ?? "",
            assessment.AuthoritativeTargetFileSize,
            assessment.AuthoritativeTargetSha256 ?? "");
        var freshPlan = ReferenceRepairPlanner.Plan(
            assessment,
            target,
            BuildReferencingContext(previewed.ReferencingDocumentPath, roots, checkoutStatuses));

        if (!freshPlan.CanProceed || !SamePreviewIdentity(previewed, freshPlan))
        {
            Error("Repair Reference final authorization failed or the previewed operation changed. "
                + freshPlan.EligibilityLabel + " - " + freshPlan.EligibilityDetail);
            return;
        }

        var result = ReferenceRepairCoordinator.Execute(
            freshPlan,
            new MessageBoxRepairConfirmation(this),
            new InventorRepairPreflight(_application, _connection, roots),
            new InventorReferenceReplacer(_application, roots),
            () => new InventorReferenceScanner(_application, roots).Scan(freshPlan.ReferencingDocumentPath));

        // The reference graph / dirty state may have changed - re-derive even
        // for an uncertain/failed mutation result.
        try { _documents.RefreshFromActiveDocument(); } catch { /* COM teardown */ }
        RefreshEnablement();

        if (result.Succeeded)
        {
            Info(result.Message);
        }
        else if (result.Outcome != ReferenceRepairOutcome.CancelledByUser)
        {
            Error(result.Message);
        }
    }

    private async Task<IReadOnlyDictionary<string, ServerCheckoutStatus?>> LoadCheckoutStatusesAsync(
        IEnumerable<string> cadDocumentIds, CancellationToken ct)
    {
        var statuses = new Dictionary<string, ServerCheckoutStatus?>(StringComparer.Ordinal);
        foreach (var id in cadDocumentIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                statuses[id] = await _connection.GetCheckoutStatusAsync(id, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                statuses[id] = null; // unavailable/rejected is fail-closed
            }
        }
        return statuses;
    }

    /// <summary>
    /// Whether the document that OWNS the reference may already be modified +
    /// saved under existing Arch rules. Derived from the verified workspace
    /// manifest binding (checked out by me?) + the on-disk read-only attribute.
    /// P5C never checks a document out and never clears read-only protection.
    /// </summary>
    private static RepairReferencingContext BuildReferencingContext(
        string parentAbsolutePath,
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, ServerCheckoutStatus?> checkoutStatuses)
    {
        var entry = FindManifestEntry(parentAbsolutePath, roots);
        ServerCheckoutStatus? serverStatus = null;
        if (entry is { CadDocumentId.Length: > 0 })
        {
            checkoutStatuses.TryGetValue(entry.CadDocumentId, out serverStatus);
        }

        // Fail-closed writability: only an AFFIRMATIVE Writable result (file
        // present, no read-only attribute, can actually be opened for write)
        // authorizes repair. A missing file, an attribute-probe failure, an
        // access-denied error, or any inability to establish write access is
        // NOT writable - never authorize by negating a helper whose failure
        // also returns "not read-only".
        var writability = LocalWritabilityProbe.Probe(parentAbsolutePath);
        var state = CheckoutStateMachine.Evaluate(
            entry, fileExists: writability != LocalWritability.Missing, serverStatus);
        return ReferenceRepairWritability.Evaluate(
            parentAbsolutePath,
            state,
            onDiskWritable: LocalWritabilityProbe.IsAffirmativelyWritable(writability),
            ReferenceRepairWritability.AuthoritativeCheckoutMatches(entry, serverStatus));
    }

    private static WorkspaceManifestEntry? FindManifestEntry(string absolutePath, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            try
            {
                var entry = WorkspaceManifest.LoadOrEmpty(root).FindByAbsolutePath(absolutePath);
                if (entry is not null) return entry;
            }
            catch (Exception ex) when (ex is IOException or WorkspaceRootException or UnauthorizedAccessException)
            {
                // Try the next known root; no binding means unmanaged/fail-closed.
            }
        }
        return null;
    }

    private static bool SamePreviewIdentity(ReferenceRepairPlan previewed, ReferenceRepairPlan fresh) =>
        PathEquals(previewed.ReferencingDocumentPath, fresh.ReferencingDocumentPath)
        && PathEquals(previewed.CurrentReferencePath, fresh.CurrentReferencePath)
        && PathEquals(previewed.ProposedTargetPath, fresh.ProposedTargetPath)
        && previewed.RelationshipKind == fresh.RelationshipKind
        && string.Equals(previewed.CadDocumentId, fresh.CadDocumentId, StringComparison.Ordinal)
        && string.Equals(previewed.CurrentPinnedFileVersionId, fresh.CurrentPinnedFileVersionId, StringComparison.Ordinal)
        && string.Equals(previewed.AuthoritativeTargetFileVersionId,
            fresh.AuthoritativeTargetFileVersionId, StringComparison.Ordinal);

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The explicit engineer confirmation gate, shown IMMEDIATELY
    ///  before any Inventor mutation. Declining makes zero CAD changes.</summary>
    private sealed class MessageBoxRepairConfirmation(ArchAddInController owner) : IReferenceRepairConfirmation
    {
        public bool Confirm(ReferenceRepairPlan plan)
        {
            // Disclosure-only, READ-ONLY, best-effort live occurrence count -
            // NEVER an authorization input (Prepare's fail-closed enumeration
            // and the pre-mutation live-set equality check are the sole
            // authorities for what actually gets mutated). Codex round-3
            // finding: the old fixed wording ("Only this one reference will
            // change") reads as "one occurrence" and is misleading whenever
            // the same file backs more than one placement in the assembly.
            var occurrenceCount = InventorReferenceReplacer.TryCountLiveMatchingOccurrencesForDisclosure(
                owner._application, plan.ReferencingDocumentPath, plan.CurrentReferencePath);
            var scopeLine = RepairOccurrenceDisclosureText.ScopeSentence(occurrenceCount);

            var message =
                "Replace this reference now?" + Environment.NewLine + Environment.NewLine
                + "Reference : " + plan.ObservedReferenceName + Environment.NewLine
                + "New target: " + plan.ProposedTargetPath + Environment.NewLine + Environment.NewLine
                + scopeLine + Environment.NewLine + Environment.NewLine
                + "The document will become MODIFIED in memory - you must Save it yourself. "
                + "P5C will not save, check in, or check out.";
            return MessageBox.Show(
                new Win32Owner(owner.SafeMainHwnd()),
                message,
                ArchAddInInfo.DisplayName,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) == DialogResult.OK;
        }
    }

    /// <summary>
    /// Validate the active document and run the P5A reference scan on it.
    /// COM reads run inline on the UI thread (the caller is already on it); a
    /// local metadata scan needs no background work.
    /// </summary>
    private bool TryScanActiveDocument(string commandLabel, out CadReferenceScan scan)
    {
        scan = null!;
        var doc = _tracker.Current;

        if (string.IsNullOrEmpty(doc.FullPath) || !doc.HasBeenSavedToDisk)
        {
            Info($"Open and save an assembly, part or drawing before running {commandLabel}.");
            return false;
        }
        if (doc.DocumentType is not (CadDocumentType.Iam or CadDocumentType.Ipt
            or CadDocumentType.Idw or CadDocumentType.Dwg))
        {
            Info($"{commandLabel} supports Inventor assemblies (.iam), parts (.ipt) and drawings (.idw / .dwg).");
            return false;
        }

        try
        {
            var scanner = new InventorReferenceScanner(_application, WorkspaceRootsForDocument(doc.FullPath!));
            scan = scanner.Scan(doc.FullPath!);
            return true;
        }
        catch (Exception)
        {
            Error($"{commandLabel} could not be completed.");
            return false;
        }
    }

    /// <summary>
    /// The managed-workspace roots to diagnose an active document against. The
    /// authoritative one is the workspace that ACTUALLY contains the document,
    /// discovered from its own <c>.arch/workspace.json</c> marker
    /// (<see cref="WorkspaceRootLocator"/>) - not whatever folder was last typed
    /// into the Get Latest dialog. The last Get Latest target is still included
    /// as an extra root so a reference resolved into a different managed
    /// workspace is also recognised; the scanner de-duplicates.
    /// </summary>
    private static IEnumerable<string?> WorkspaceRootsForDocument(string? activeDocumentPath)
    {
        var containing = WorkspaceRootLocator.FindRootForFile(activeDocumentPath);
        if (!string.IsNullOrWhiteSpace(containing))
        {
            yield return containing;
        }

        var lastGetLatest = SafeLastWorkspaceRoot();
        if (!string.IsNullOrWhiteSpace(lastGetLatest))
        {
            yield return lastGetLatest;
        }
    }

    private void ShowGetLatestDialog()
    {
        string documentNumber;
        string documentType;
        string workspaceRoot;
        using (var dialog = new GetLatestDialog())
        {
            if (dialog.ShowDialog(new Win32Owner(SafeMainHwnd())) != DialogResult.OK)
            {
                return;
            }
            documentNumber = dialog.RootDocumentNumber;
            documentType = dialog.RootDocumentType;
            workspaceRoot = dialog.WorkspaceRoot;
        }

        var request = new GetLatestRequest
        {
            Root = CadDocumentLookup.ByNumber(documentNumber: documentNumber, documentType: documentType),
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
