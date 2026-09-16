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

        // The ONLY filesystem read this command performs: a best-effort,
        // read-only existence check of each proposed destination, scoped to
        // the destination folder the engineer just chose. Never a write.
        var plan = CopyDesignPlanner.Plan(
            scan,
            nameRule,
            input.DestinationFolder,
            destinationExists: SafeFileExists);

        var text = CopyDesignPlanTextReport.Render(plan);
        var status = plan.IsExecutable ? "PREVIEW" : "PREVIEW - NOT EXECUTABLE";
        using var dialog = new ScanResultDialog(
            $"{ArchAddInInfo.DisplayName} - Copy Design Preview ({status})", text);
        dialog.ShowDialog(new Win32Owner(SafeMainHwnd()));
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
