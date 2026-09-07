using System.Net;
using System.Security.Cryptography;
using System.Text;

using Arch.CadConnect.Api;
using Arch.CadConnect.Api.Workspace;
using Arch.CadConnect.Core.Documents;
using Arch.CadConnect.Core.Files;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Tests.Workspace;

public class CheckoutOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arch-cc-orch-co-" + Guid.NewGuid().ToString("N"));

    public CheckoutOrchestratorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { }
    }

    // ---- fixture --------------------------------------------------------

    private static readonly byte[] V1Bytes = Encoding.UTF8.GetBytes("version 1 authoritative bytes for part.ipt");
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private string PartPath => Path.Combine(_root, "part.ipt");
    private string ManifestPath => Path.Combine(_root, WorkspaceManifest.RelativeManifestPath);

    private void SeedControlled()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".arch"));
        File.WriteAllText(ManifestPath, $$"""
        {"schema":"arch-plm.workspace-manifest.v1","serverOrigin":"https://plm.example.com","organizationId":"org1",
         "organizationCode":"ORGA","rootCadDocumentId":"cad_part","rootDocumentNumber":"PRT-1","updatedAtUtc":"2026-09-05T00:00:00Z",
         "entries":[{"relativePath":"part.ipt","cadDocumentId":"cad_part","documentNumber":"PRT-1","fileName":"part.ipt",
           "cadType":"IPT","fileVersionId":"fv_1","versionNumber":1,"checksum":"{{Sha(V1Bytes)}}","fileSize":{{V1Bytes.Length}},
           "isRoot":false,"dependsOn":[],"state":"Verified","retrievedAtUtc":"2026-09-05T00:00:00Z"}]}
        """);
        File.WriteAllBytes(PartPath, V1Bytes);
        ManagedFileGuard.SetControlled(PartPath);
    }

    private WorkspaceManifestEntry Entry() => WorkspaceManifest.LoadOrEmpty(_root).FindByAbsolutePath(PartPath)!;

    private CheckoutOrchestrator Orchestrator(FakeHttpHandler handler, IEditorDocumentProbe? probe = null) =>
        new(WorkspaceTestSession.Server, WorkspaceTestSession.Make(), new HttpClient(handler),
            TimeSpan.FromSeconds(5), clock: () => new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero),
            editorProbe: probe);

    private static HttpResponseMessage Json(HttpStatusCode s, string body) => FakeHttpHandler.Json(s, body);

    private const string CheckoutCreated =
        """{"status":"created","checkout":{"id":"co_1","cadDocumentId":"cad_part","baseFileVersionId":"fv_1","baseVersionNumber":1}}""";

    private static string StatusBody(string state, string checkoutId = "co_1", string baseFv = "fv_1", string? holder = null)
    {
        if (state == "available")
        {
            return "{\"state\":\"available\"}";
        }
        return "{\"state\":\"" + state + "\",\"checkout\":{\"id\":\"" + checkoutId
            + "\",\"cadDocumentId\":\"cad_part\",\"baseFileVersionId\":\"" + baseFv
            + "\",\"baseVersionNumber\":1,\"checkedOutBy\":{\"name\":\"" + (holder ?? "Me")
            + "\",\"email\":\"x@x.com\"}}}";
    }

    /// <summary>One handler routing every P4C endpoint. `statusState` is what
    ///  `GET /checkout` reports (defaults to "mine" so the happy path works).</summary>
    private FakeHttpHandler Server(
        string statusState = "mine",
        string statusCheckoutId = "co_1",
        string statusBaseFv = "fv_1",
        string? statusHolder = null,
        HttpStatusCode checkoutStatus = HttpStatusCode.Created,
        string? checkoutBody = null,
        HttpStatusCode checkinStatus = HttpStatusCode.Created,
        string? checkinBody = null,
        HttpStatusCode undoStatus = HttpStatusCode.OK,
        byte[]? contentBytes = null,
        Action? onContentRequest = null,
        Action? onUndoRequest = null)
    {
        var content = contentBytes ?? V1Bytes;
        return new FakeHttpHandler(req =>
        {
            var p = req.RequestUri!.AbsolutePath;
            if (p == "/api/cad-documents/cad_part/checkout" && req.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, StatusBody(statusState, statusCheckoutId, statusBaseFv, statusHolder));
            switch (p)
            {
                case "/api/cad-documents/cad_part/checkout":
                    return Json(checkoutStatus, checkoutBody ?? CheckoutCreated);
                case "/api/cad-documents/cad_part/checkin":
                    return Json(checkinStatus,
                        checkinBody ?? $$"""{"fileVersionId":"fv_2","cadDocumentId":"cad_part","versionNumber":2,"originalFileName":"part.ipt","storageKey":"k","fileSize":{{ReadPartLength()}},"checksum":"{{ReadPartSha()}}","createdById":"u1"}""");
                case "/api/cad-documents/cad_part/checkout/undo":
                    onUndoRequest?.Invoke();
                    return Json(undoStatus, """{"cadDocumentId":"cad_part","checkoutId":"co_1","baseFileVersionId":"fv_1","previousOwnerId":"u1"}""");
                case "/api/file-versions/fv_1/content":
                    onContentRequest?.Invoke();
                    return ContentResponse(content);
                default:
                    return Json(HttpStatusCode.NotFound, """{"error":"no route"}""");
            }
        });
    }

    private long ReadPartLength() => File.Exists(PartPath) ? new FileInfo(PartPath).Length : 0;
    private string ReadPartSha() => File.Exists(PartPath) ? Sha(File.ReadAllBytes(PartPath)) : "";

    private static HttpResponseMessage ContentResponse(byte[] bytes)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        r.Headers.TryAddWithoutValidation("X-Content-SHA256", Sha(bytes));
        return r;
    }

    private async Task CheckoutFirst() => await Orchestrator(Server()).CheckoutAsync(_root, PartPath);

    private sealed class OpenProbe(bool open) : IEditorDocumentProbe
    {
        public bool IsOpenForEditing(string absoluteFilePath) => open;
    }

    // ================================================================
    // CHECKOUT
    // ================================================================

    [Fact]
    public async Task Checkout_makes_the_file_writable_and_records_the_base_binding()
    {
        SeedControlled();
        var result = await Orchestrator(Server()).CheckoutAsync(_root, PartPath);

        Assert.False(result.ReconcileNeeded);
        Assert.True(result.MadeWritable);
        Assert.True(result.ManifestUpdated);
        Assert.False(ManagedFileGuard.IsControlled(PartPath));

        var co = Entry().Checkout;
        Assert.NotNull(co);
        Assert.Equal("co_1", co!.CheckoutId);
        Assert.Equal("fv_1", co.BaseFileVersionId);
        Assert.Equal(Sha(V1Bytes), co.BaseChecksum);
        Assert.Equal(V1Bytes.Length, co.BaseFileSize);
    }

    [Fact]
    public async Task Checkout_conflict_leaves_the_file_controlled_with_no_marker()
    {
        SeedControlled();
        var handler = Server(checkoutStatus: HttpStatusCode.Conflict,
            checkoutBody: """{"error":"held","checkout":{"id":"co_x","cadDocumentId":"cad_part","baseFileVersionId":"fv_1","baseVersionNumber":1,"checkedOutBy":{"name":"Dana","email":"d@x.com"}}}""");

        var ex = await Assert.ThrowsAsync<CheckoutConflictException>(() => Orchestrator(handler).CheckoutAsync(_root, PartPath));
        Assert.Equal("Dana", ex.HolderName);
        Assert.True(ManagedFileGuard.IsControlled(PartPath));
        Assert.Null(Entry().Checkout);
    }

    [Fact]
    public async Task Checkout_refuses_an_unmanaged_file_before_any_request()
    {
        var handler = Server();
        await Assert.ThrowsAsync<NotManagedException>(
            () => Orchestrator(handler).CheckoutAsync(_root, Path.Combine(_root, "unmanaged.ipt")));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Checkout_refuses_an_Unverified_entry()
    {
        SeedControlled();
        WorkspaceManifest.LoadOrEmpty(_root).MarkUnverified(PartPath);
        var handler = Server();
        await Assert.ThrowsAsync<NotVerifiedException>(() => Orchestrator(handler).CheckoutAsync(_root, PartPath));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Checkout_server_ok_but_manifest_persist_fails_reports_ReconcileNeeded_not_failure()
    {
        SeedControlled();
        // hold the manifest open so the atomic replace cannot happen
        using var hold = new FileStream(ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await Orchestrator(Server()).CheckoutAsync(_root, PartPath);

        Assert.True(result.ReconcileNeeded);
        Assert.False(result.ManifestUpdated);
        Assert.True(result.MadeWritable);                 // server lock acknowledged
        Assert.Contains("HELD on the server", result.Message);
    }

    // ================================================================
    // CHECK IN  (+ live reconciliation)
    // ================================================================

    [Fact]
    public async Task CheckIn_creates_a_new_version_sets_read_only_and_rebinds_the_manifest()
    {
        SeedControlled();
        await CheckoutFirst();
        await File.WriteAllBytesAsync(PartPath, Encoding.UTF8.GetBytes("EDITED content for version 2"));

        var result = await Orchestrator(Server()).CheckInAsync(_root, PartPath);

        Assert.True(result.Verified);
        Assert.Equal(2, result.NewVersionNumber);
        Assert.True(result.ManifestUpdated);
        Assert.True(ManagedFileGuard.IsControlled(PartPath));
        var entry = Entry();
        Assert.Equal("fv_2", entry.FileVersionId);
        Assert.Equal(WorkspaceManifestEntryState.Verified, entry.State);
        Assert.Null(entry.Checkout);
    }

    [Fact]
    public async Task CheckIn_is_refused_when_the_server_says_the_document_is_not_checked_out()
    {
        SeedControlled();
        await CheckoutFirst();               // writes a local marker...
        await File.WriteAllBytesAsync(PartPath, Encoding.UTF8.GetBytes("edited"));

        // ...but the server now says it is available (released elsewhere)
        var handler = Server(statusState: "available");
        await Assert.ThrowsAsync<StaleCheckoutException>(() => Orchestrator(handler).CheckInAsync(_root, PartPath));

        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/checkin"));
        Assert.Equal(WorkspaceManifestEntryState.Unverified, Entry().State); // stale marker reconciled
        Assert.Null(Entry().Checkout);
    }

    [Fact]
    public async Task CheckIn_is_refused_when_the_server_says_locked_by_another_user()
    {
        SeedControlled();
        await CheckoutFirst();
        var handler = Server(statusState: "locked", statusHolder: "Priya");
        var ex = await Assert.ThrowsAsync<CheckoutConflictException>(() => Orchestrator(handler).CheckInAsync(_root, PartPath));
        Assert.Equal("Priya", ex.HolderName);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/checkin"));
    }

    [Fact]
    public async Task CheckIn_is_refused_when_the_server_checkout_id_differs_from_the_local_marker()
    {
        SeedControlled();
        await CheckoutFirst();
        var handler = Server(statusState: "mine", statusCheckoutId: "co_DIFFERENT");
        await Assert.ThrowsAsync<StaleCheckoutException>(() => Orchestrator(handler).CheckInAsync(_root, PartPath));
    }

    [Fact]
    public async Task CheckIn_pre_201_failure_keeps_the_checkout_marker_and_leaves_the_file_writable()
    {
        SeedControlled();
        await CheckoutFirst();
        await File.WriteAllBytesAsync(PartPath, Encoding.UTF8.GetBytes("edited"));

        var handler = Server(checkinStatus: HttpStatusCode.Conflict, checkinBody: """{"error":"stale base"}""");
        await Assert.ThrowsAsync<ArchApiException>(() => Orchestrator(handler).CheckInAsync(_root, PartPath));

        Assert.False(ManagedFileGuard.IsControlled(PartPath));
        var entry = Entry();
        Assert.NotNull(entry.Checkout);
        Assert.Equal("fv_1", entry.FileVersionId);
        Assert.Equal(WorkspaceManifestEntryState.Verified, entry.State);
    }

    [Fact]
    public async Task CheckIn_201_with_a_verification_mismatch_rebinds_Unverified_and_controls_the_file()
    {
        SeedControlled();
        await CheckoutFirst();
        await File.WriteAllBytesAsync(PartPath, Encoding.UTF8.GetBytes("edited"));

        var handler = Server(checkinBody:
            """{"fileVersionId":"fv_2","cadDocumentId":"cad_part","versionNumber":2,"originalFileName":"part.ipt","storageKey":"k","fileSize":999999,"checksum":"0000000000000000000000000000000000000000000000000000000000000000","createdById":"u1"}""");

        var result = await Orchestrator(handler).CheckInAsync(_root, PartPath);

        Assert.False(result.Verified);
        var entry = Entry();
        Assert.Equal("fv_2", entry.FileVersionId);
        Assert.Equal(WorkspaceManifestEntryState.Unverified, entry.State);
        Assert.Null(entry.Checkout);
        Assert.True(ManagedFileGuard.IsControlled(PartPath));
    }

    [Fact]
    public async Task CheckIn_201_but_manifest_persist_fails_never_claims_the_old_version_is_current()
    {
        SeedControlled();
        await CheckoutFirst();
        await File.WriteAllBytesAsync(PartPath, Encoding.UTF8.GetBytes("edited v2"));

        var handler = Server();
        using var hold = new FileStream(ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await Orchestrator(handler).CheckInAsync(_root, PartPath);

        Assert.True(result.Verified);                 // server 201 is authoritative
        Assert.False(result.ManifestUpdated);         // ...but not a false "saved"
        Assert.Contains("Get Latest", result.Message);
    }

    [Fact]
    public async Task CheckIn_proceeds_on_server_Mine_even_without_a_local_marker()
    {
        SeedControlled();
        // no checkout done locally, but the server says this session holds it
        File.SetAttributes(PartPath, FileAttributes.Normal);
        await File.WriteAllBytesAsync(PartPath, Encoding.UTF8.GetBytes("edited"));

        var handler = Server(statusState: "mine");
        var result = await Orchestrator(handler).CheckInAsync(_root, PartPath);
        Assert.True(result.Verified);
    }

    // ================================================================
    // UNDO
    // ================================================================

    [Fact]
    public async Task Undo_verifies_the_base_before_release_then_restores_and_controls()
    {
        SeedControlled();
        await CheckoutFirst();
        await File.WriteAllBytesAsync(PartPath, Encoding.UTF8.GetBytes("throwaway edits"));

        var result = await Orchestrator(Server()).UndoAsync(_root, PartPath, "done");

        Assert.True(result.Restored);
        Assert.True(result.ManifestReconciled);
        Assert.Equal(V1Bytes, await File.ReadAllBytesAsync(PartPath));
        Assert.True(ManagedFileGuard.IsControlled(PartPath));
        var entry = Entry();
        Assert.Null(entry.Checkout);
        Assert.Equal(WorkspaceManifestEntryState.Verified, entry.State);
    }

    [Fact]
    public async Task Undo_with_a_corrupt_base_download_NEVER_calls_the_server_undo()
    {
        SeedControlled();
        await CheckoutFirst();
        var handler = Server(contentBytes: Encoding.UTF8.GetBytes("corrupted base bytes not matching the checksum"));
        await Assert.ThrowsAsync<RestoreVerificationException>(() => Orchestrator(handler).UndoAsync(_root, PartPath, null));

        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/checkout/undo"));
        Assert.NotNull(Entry().Checkout);
    }

    [Fact]
    public async Task Undo_with_a_size_mismatch_NEVER_calls_the_server_undo()
    {
        SeedControlled();
        await CheckoutFirst();
        var handler = Server(contentBytes: Encoding.UTF8.GetBytes("short"));
        await Assert.ThrowsAsync<RestoreVerificationException>(() => Orchestrator(handler).UndoAsync(_root, PartPath, null));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/checkout/undo"));
    }

    [Fact]
    public async Task Undo_is_refused_before_staging_when_the_server_says_not_mine()
    {
        SeedControlled();
        await CheckoutFirst();
        var handler = Server(statusState: "available");
        await Assert.ThrowsAsync<StaleCheckoutException>(() => Orchestrator(handler).UndoAsync(_root, PartPath, null));

        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/content"));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/checkout/undo"));
        Assert.Equal(WorkspaceManifestEntryState.Unverified, Entry().State);
    }

    [Fact]
    public async Task Undo_is_refused_when_the_server_base_version_differs_from_the_local_record()
    {
        SeedControlled();
        await CheckoutFirst();
        var handler = Server(statusState: "mine", statusBaseFv: "fv_DIFFERENT");
        await Assert.ThrowsAsync<StaleCheckoutException>(() => Orchestrator(handler).UndoAsync(_root, PartPath, null));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/checkout/undo"));
    }

    [Fact]
    public async Task Undo_that_gets_409_does_NOT_overwrite_the_local_file_with_the_captured_base()
    {
        SeedControlled();
        await CheckoutFirst();
        var edited = Encoding.UTF8.GetBytes("the user's edits after check-in-by-someone-else created V2");
        await File.WriteAllBytesAsync(PartPath, edited);

        // server: status still "mine" (our marker matches), but the undo call
        // returns 409 - ambiguous (lost response / admin unlock / prior check-in).
        var handler = Server(undoStatus: HttpStatusCode.Conflict);
        var result = await Orchestrator(handler).UndoAsync(_root, PartPath, null);

        Assert.False(result.Restored);
        Assert.Equal(edited, await File.ReadAllBytesAsync(PartPath));   // NEVER overwritten
        Assert.Equal(WorkspaceManifestEntryState.Unverified, Entry().State);
        Assert.Null(Entry().Checkout);
        Assert.Contains("Get Latest", result.Message);
        // the verified base bytes were retained, not discarded
        Assert.NotNull(result.RecoveryFilePath);
        Assert.True(File.Exists(result.RecoveryFilePath!));
        Assert.Equal(V1Bytes, await File.ReadAllBytesAsync(result.RecoveryFilePath!));
    }

    [Fact]
    public async Task Undo_retry_after_a_lost_response_409_still_does_not_destructively_restore()
    {
        SeedControlled();
        await CheckoutFirst();
        var current = Encoding.UTF8.GetBytes("whatever is on disk now");
        await File.WriteAllBytesAsync(PartPath, current);

        var handler = Server(undoStatus: HttpStatusCode.Conflict);
        var result = await Orchestrator(handler).UndoAsync(_root, PartPath, null);

        Assert.False(result.Restored);
        Assert.Equal(current, await File.ReadAllBytesAsync(PartPath));
    }

    [Fact]
    public async Task Undo_refuses_before_any_server_call_when_the_document_is_open_in_the_editor()
    {
        SeedControlled();
        await CheckoutFirst();
        var handler = Server();
        await Assert.ThrowsAsync<DocumentOpenException>(
            () => Orchestrator(handler, new OpenProbe(true)).UndoAsync(_root, PartPath, null));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/content"));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/checkout/undo"));
        Assert.NotNull(Entry().Checkout);
    }

    [Fact]
    public async Task Undo_final_replace_failure_marks_Unverified_and_preserves_a_recovery_copy()
    {
        SeedControlled();
        await CheckoutFirst();

        // The target passes BOTH existence checks and the pre-flight, the
        // server undo succeeds, then the file is swapped for a directory
        // before Promote runs - a genuine post-release TOCTOU race.
        var handler = Server(onUndoRequest: () =>
        {
            File.Delete(PartPath);
            Directory.CreateDirectory(PartPath);
        });

        var result = await Orchestrator(handler).UndoAsync(_root, PartPath, null);

        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/checkout/undo")); // server WAS released
        Assert.False(result.Restored);
        Assert.False(result.ManifestReconciled);
        Assert.NotNull(result.RecoveryFilePath);
        Assert.True(File.Exists(result.RecoveryFilePath!));
        Assert.Equal(V1Bytes, await File.ReadAllBytesAsync(result.RecoveryFilePath!));
        Assert.Matches(@"\.recovery-[0-9a-f]{32}\.ipt$", result.RecoveryFilePath!);

        var entry = Entry();
        Assert.Equal(WorkspaceManifestEntryState.Unverified, entry.State);
        Assert.Null(entry.Checkout);
    }

    // ---- TOCTOU: the exact manifest-bound target file must exist at run time

    [Fact]
    public async Task Undo_target_missing_before_start_stops_before_the_server_and_keeps_the_checkout()
    {
        SeedControlled();
        await CheckoutFirst();
        File.Delete(PartPath);

        var handler = Server();
        await Assert.ThrowsAsync<UndoTargetMissingException>(() => Orchestrator(handler).UndoAsync(_root, PartPath, null));

        Assert.Empty(handler.Requests);                                  // no GET status, no content, no undo
        Assert.NotNull(Entry().Checkout);                                // checkout marker untouched
        Assert.Equal(WorkspaceManifestEntryState.Verified, Entry().State);
        Assert.False(File.Exists(PartPath));                             // NOT recreated
    }

    [Fact]
    public async Task A_missing_target_is_not_treated_as_replaceable_by_the_undo_path()
    {
        // ManagedFileGuard.CanReplaceInPlace returns true for a missing file;
        // UndoAsync's own existence check must still reject it.
        SeedControlled();
        await CheckoutFirst();
        File.Delete(PartPath);
        Assert.True(Arch.CadConnect.Core.Files.ManagedFileGuard.CanReplaceInPlace(PartPath));

        await Assert.ThrowsAsync<UndoTargetMissingException>(() => Orchestrator(Server()).UndoAsync(_root, PartPath, null));
    }

    [Fact]
    public async Task Undo_recovery_never_overwrites_an_existing_recovery_file_and_keeps_the_verified_staged_bytes()
    {
        SeedControlled();
        await CheckoutFirst();
        await File.WriteAllBytesAsync(PartPath, Encoding.UTF8.GetBytes("the user's edits"));

        // Pre-seed .arch/.recovery with an existing file, and also drop a
        // decoy file that a future recovery must never overwrite.
        var recoveryDir = Path.Combine(_root, ".arch", ".recovery");
        Directory.CreateDirectory(recoveryDir);
        var decoy = Path.Combine(recoveryDir, "someone-elses.recovery.ipt");
        await File.WriteAllBytesAsync(decoy, Encoding.UTF8.GetBytes("DO NOT TOUCH"));

        // Server undo returns 409 -> the recovery path runs. Recovery move
        // uses File.Move(overwrite:false) under a fresh GUID name, so it must
        // never clobber the decoy.
        var handler = Server(undoStatus: HttpStatusCode.Conflict);
        var result = await Orchestrator(handler).UndoAsync(_root, PartPath, null);

        Assert.False(result.Restored);
        Assert.False(result.ManifestReconciled);
        Assert.Equal(Encoding.UTF8.GetBytes("DO NOT TOUCH"), await File.ReadAllBytesAsync(decoy)); // untouched
        Assert.NotNull(result.RecoveryFilePath);
        Assert.True(File.Exists(result.RecoveryFilePath!));
        Assert.Equal(V1Bytes, await File.ReadAllBytesAsync(result.RecoveryFilePath!));   // verified base
        Assert.NotEqual(decoy, result.RecoveryFilePath);
        Assert.Matches(@"\.recovery-[0-9a-f]{32}\.ipt$", result.RecoveryFilePath!);       // GUID name kept
        Assert.Equal(WorkspaceManifestEntryState.Unverified, Entry().State);
        Assert.Null(Entry().Checkout);
    }

    [Fact]
    public async Task Undo_recovery_directory_move_failure_keeps_the_verified_staged_bytes_and_surfaces_that_path()
    {
        SeedControlled();
        await CheckoutFirst();

        // Block .arch/.recovery by putting a FILE where the directory must go,
        // so Directory.CreateDirectory throws and the move into recovery fails.
        var archDir = Path.Combine(_root, ".arch");
        Directory.CreateDirectory(archDir);
        await File.WriteAllTextAsync(Path.Combine(archDir, ".recovery"), "not a directory");

        var handler = Server(undoStatus: HttpStatusCode.Conflict);
        var result = await Orchestrator(handler).UndoAsync(_root, PartPath, null);

        Assert.False(result.Restored);
        Assert.False(result.ManifestReconciled);                       // no false "Verified"
        Assert.NotNull(result.RecoveryFilePath);                       // a surviving verified path IS surfaced
        Assert.True(File.Exists(result.RecoveryFilePath!));            // the verified bytes were NOT deleted
        Assert.Equal(V1Bytes, await File.ReadAllBytesAsync(result.RecoveryFilePath!));
        Assert.Contains(".staging", result.RecoveryFilePath!);         // it stayed in staging (the move failed)
        Assert.Equal(WorkspaceManifestEntryState.Unverified, Entry().State);
        Assert.Null(Entry().Checkout);
    }

    [Fact]
    public async Task Undo_target_deleted_during_the_base_download_is_caught_before_the_server_release()
    {
        SeedControlled();
        await CheckoutFirst();

        // exists at check (2), then deleted while the base version downloads,
        // so the re-check at (7) - immediately before POST /checkout/undo -
        // must catch it.
        var handler = Server(onContentRequest: () => File.Delete(PartPath));

        await Assert.ThrowsAsync<UndoTargetMissingException>(() => Orchestrator(handler).UndoAsync(_root, PartPath, null));

        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/content"));        // staged...
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/checkout/undo")); // ...but never released
        Assert.NotNull(Entry().Checkout);
        Assert.Equal(WorkspaceManifestEntryState.Verified, Entry().State);
        Assert.False(File.Exists(PartPath));                             // NOT recreated
        var staging = Path.Combine(_root, ".arch", ".staging");
        Assert.True(!Directory.Exists(staging) || Directory.GetFiles(staging).Length == 0); // staged bytes discarded
    }

    [Fact]
    public async Task Undo_success_but_manifest_persist_fails_never_claims_Verified()
    {
        SeedControlled();
        await CheckoutFirst();
        await File.WriteAllBytesAsync(PartPath, Encoding.UTF8.GetBytes("edits"));
        File.SetAttributes(PartPath, FileAttributes.Normal);

        var handler = Server();
        using var hold = new FileStream(ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await Orchestrator(handler).UndoAsync(_root, PartPath, null);

        Assert.True(result.Restored);                 // file WAS restored
        Assert.False(result.ManifestReconciled);      // ...but not a false "Verified"
        Assert.Equal(V1Bytes, await File.ReadAllBytesAsync(PartPath));
        Assert.Contains("Get Latest", result.Message);
    }

    [Fact]
    public async Task Undo_without_a_local_checkout_marker_is_refused()
    {
        SeedControlled();
        await Assert.ThrowsAsync<NotCheckedOutLocallyException>(() => Orchestrator(Server()).UndoAsync(_root, PartPath, null));
    }

    // ================================================================
    // MULTI-WORKSPACE
    // ================================================================

    [Fact]
    public async Task Same_cadDocument_in_a_second_workspace_root_cannot_use_a_stale_marker_to_undo()
    {
        SeedControlled();
        await CheckoutFirst(); // workspace A holds the checkout, marker written in A

        // workspace B: same cadDocumentId, its own manifest with a STALE marker
        var rootB = Path.Combine(Path.GetTempPath(), "arch-cc-orch-B-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(rootB, ".arch"));
        File.WriteAllText(Path.Combine(rootB, WorkspaceManifest.RelativeManifestPath), $$"""
        {"schema":"arch-plm.workspace-manifest.v1","serverOrigin":"https://plm.example.com","organizationId":"org1",
         "organizationCode":"ORGA","rootCadDocumentId":"cad_part","rootDocumentNumber":"PRT-1","updatedAtUtc":"2026-09-05T00:00:00Z",
         "entries":[{"relativePath":"part.ipt","cadDocumentId":"cad_part","documentNumber":"PRT-1","fileName":"part.ipt",
           "cadType":"IPT","fileVersionId":"fv_1","versionNumber":1,"checksum":"{{Sha(V1Bytes)}}","fileSize":{{V1Bytes.Length}},
           "isRoot":false,"dependsOn":[],"state":"Verified","retrievedAtUtc":"2026-09-05T00:00:00Z"}]}
        """);
        var partB = Path.Combine(rootB, "part.ipt");
        await File.WriteAllBytesAsync(partB, Encoding.UTF8.GetBytes("B's local edits"));
        WorkspaceManifest.LoadOrEmpty(rootB).MarkCheckedOut(partB, new WorkspaceCheckoutBinding
        {
            CheckoutId = "co_STALE_B", BaseFileVersionId = "fv_1", BaseVersionNumber = 1,
            BaseChecksum = Sha(V1Bytes), BaseFileSize = V1Bytes.Length,
            CheckedOutAtUtc = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        });

        try
        {
            // server status is "mine" but the checkout id is co_1, not co_STALE_B
            var handler = Server(statusState: "mine", statusCheckoutId: "co_1");
            await Assert.ThrowsAsync<StaleCheckoutException>(() => Orchestrator(handler).UndoAsync(rootB, partB, null));
            Assert.Equal(Encoding.UTF8.GetBytes("B's local edits"), await File.ReadAllBytesAsync(partB));
        }
        finally
        {
            try { foreach (var f in Directory.GetFiles(rootB, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(rootB, true); } catch { }
        }
    }
}
