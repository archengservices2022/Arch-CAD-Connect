using System.Net;
using System.Text;

using Arch.CadConnect.Api;
using Arch.CadConnect.Api.Workspace;
using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Tests.Workspace;

public class GetLatestOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arch-cc-orch-" + Guid.NewGuid().ToString("N"));

    public GetLatestOrchestratorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort */ }
    }

    private static readonly byte[] AsmBytes = Encoding.UTF8.GetBytes("assembly-payload-1");
    private static readonly byte[] PartBytes = Encoding.UTF8.GetBytes("part-payload-longer-2");

    private const string PlanTemplate =
        """
        {"contract":{"id":"arch-plm.cad-workspace-plan","version":"1.0.0"},
         "root":{"cadDocumentId":"cad_root"},"generatedAt":"2026-09-06T00:00:00Z","documentCount":2,"safe":true,
         "entries":[
           {"cadDocumentId":"cad_root","documentNumber":"ASM-1","fileName":"asm.iam","cadType":"IAM","isRoot":true,
            "placement":{"generatedRelativePath":"asm.iam"},"dependsOn":[{"cadDocumentId":"cad_part","dependencyType":"COMPONENT"}],
            "canMaterialize":true,"blockedReasons":[],
            "version":{"fileVersionId":"fv_asm","versionNumber":4,"checksum":"__ASM_SHA__","fileSize":__ASM_LEN__,
                       "originalFileName":"asm.iam","contentPath":"/api/file-versions/fv_asm/content"}},
           {"cadDocumentId":"cad_part","documentNumber":"PRT-1","fileName":"part.ipt","cadType":"IPT","isRoot":false,
            "placement":{"generatedRelativePath":"part.ipt"},"dependsOn":[],
            "canMaterialize":true,"blockedReasons":[],
            "version":{"fileVersionId":"fv_part","versionNumber":2,"checksum":"__PART_SHA__","fileSize":__PART_LEN__,
                       "originalFileName":"part.ipt","contentPath":"/api/file-versions/fv_part/content"}}],
         "problems":[],"traversal":{"visitedCount":2,"edgeCount":1,"maxNodes":5000}}
        """;

    private static string PlanJson() => PlanTemplate
        .Replace("__ASM_SHA__", Sha(AsmBytes))
        .Replace("__ASM_LEN__", AsmBytes.Length.ToString())
        .Replace("__PART_SHA__", Sha(PartBytes))
        .Replace("__PART_LEN__", PartBytes.Length.ToString());

    private static string Sha(byte[] b) => FakeContentDownloader.Sha256Hex(b);

    /// <summary>One handler serving resolve + workspace-plan + both content endpoints.</summary>
    private FakeHttpHandler ServerHandler() => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        return path switch
        {
            "/api/desktop/cad-documents/resolve" => FakeHttpHandler.Json(HttpStatusCode.OK,
                """{"contract":"arch-plm.desktop-cad-document.v1","cadDocumentId":"cad_root","documentNumber":"ASM-1","fileName":"asm.iam","cadType":"IAM"}"""),
            "/api/cad-documents/cad_root/workspace-plan" => FakeHttpHandler.Json(HttpStatusCode.OK, PlanJson()),
            "/api/file-versions/fv_asm/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(AsmBytes) },
            "/api/file-versions/fv_part/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PartBytes) },
            _ => FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"error":"no route"}"""),
        };
    });

    private GetLatestOrchestrator Orchestrator(HttpClient http) => new(
        WorkspaceTestSession.Server,
        resolve: (s, lu, c) => GetLatestOrchestrator.ResolveViaHttpAsync(WorkspaceTestSession.Server, s, http, lu, TimeSpan.FromSeconds(5), c),
        planClientFactory: s => new WorkspacePlanClient(WorkspaceTestSession.Server, s, http, TimeSpan.FromSeconds(5)),
        downloaderFactory: s => new HttpContentDownloader(WorkspaceTestSession.Server, s, http));

    [Fact]
    public async Task End_to_end_materializes_the_pinned_package_and_writes_the_manifest()
    {
        var http = new HttpClient(ServerHandler());
        var request = new GetLatestRequest { Root = CadDocumentLookup.ByNumber("ASM-1"), WorkspaceRoot = _root };

        var report = await Orchestrator(http).RunAsync(WorkspaceTestSession.Make(), request);

        Assert.True(report.ReachedValidState);
        Assert.Equal(2, report.Summary.Downloaded);
        Assert.Equal(AsmBytes, await File.ReadAllBytesAsync(Path.Combine(_root, "asm.iam")));
        Assert.Equal(PartBytes, await File.ReadAllBytesAsync(Path.Combine(_root, "part.ipt")));

        // manifest: stable-id binding recorded, verified
        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        Assert.Equal(2, mf.Entries.Count);
        var rootEntry = mf.FindByAbsolutePath(Path.Combine(_root, "asm.iam"));
        Assert.NotNull(rootEntry);
        Assert.Equal("cad_root", rootEntry!.CadDocumentId);
        Assert.Equal("fv_asm", rootEntry.FileVersionId);
        Assert.Equal(WorkspaceManifestEntryState.Verified, rootEntry.State);
        Assert.True(rootEntry.IsRoot);
        Assert.Contains("cad_part", rootEntry.DependsOn);
    }

    [Fact]
    public async Task An_unsafe_plan_leaves_the_manifest_untouched()
    {
        // seed a prior manifest
        var http0 = new HttpClient(ServerHandler());
        await Orchestrator(http0).RunAsync(WorkspaceTestSession.Make(),
            new GetLatestRequest { Root = CadDocumentLookup.ByNumber("ASM-1"), WorkspaceRoot = _root });
        var before = await File.ReadAllTextAsync(Path.Combine(_root, WorkspaceManifest.RelativeManifestPath));

        // now the plan comes back unsafe
        var unsafeHandler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/desktop/cad-documents/resolve" => FakeHttpHandler.Json(HttpStatusCode.OK,
                """{"contract":"arch-plm.desktop-cad-document.v1","cadDocumentId":"cad_root","documentNumber":"ASM-1","fileName":"asm.iam","cadType":"IAM"}"""),
            "/api/cad-documents/cad_root/workspace-plan" => FakeHttpHandler.Json(HttpStatusCode.OK,
                PlanJson().Replace("\"safe\":true", "\"safe\":false")),
            _ => FakeHttpHandler.Json(HttpStatusCode.NotFound, "{}"),
        });

        await Assert.ThrowsAsync<UnsafeWorkspacePlanException>(
            () => Orchestrator(new HttpClient(unsafeHandler)).RunAsync(WorkspaceTestSession.Make(),
                new GetLatestRequest { Root = CadDocumentLookup.ByNumber("ASM-1"), WorkspaceRoot = _root }));

        var after = await File.ReadAllTextAsync(Path.Combine(_root, WorkspaceManifest.RelativeManifestPath));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Resolve_404_surfaces_as_a_NotFound_ArchApiException_no_workspace_change()
    {
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/desktop/cad-documents/resolve"
            ? FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"error":"No CAD document matched that lookup in your organization."}""")
            : FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));

        var ex = await Assert.ThrowsAsync<ArchApiException>(
            () => Orchestrator(new HttpClient(handler)).RunAsync(WorkspaceTestSession.Make(),
                new GetLatestRequest { Root = CadDocumentLookup.ByNumber("NOPE"), WorkspaceRoot = _root }));

        Assert.Equal(ArchApiFailureKind.NotFound, ex.Kind);
        Assert.False(Directory.Exists(Path.Combine(_root, ".arch")));
    }

    [Fact]
    public async Task Re_running_a_completed_get_latest_reports_every_entry_already_current()
    {
        var request = new GetLatestRequest { Root = CadDocumentLookup.ByNumber("ASM-1"), WorkspaceRoot = _root };
        await Orchestrator(new HttpClient(ServerHandler())).RunAsync(WorkspaceTestSession.Make(), request);

        // Second run: content endpoints now refuse to serve; nothing should be
        // requested because both files already match the pinned checksum+size.
        var noContent = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/desktop/cad-documents/resolve" => FakeHttpHandler.Json(HttpStatusCode.OK,
                """{"contract":"arch-plm.desktop-cad-document.v1","cadDocumentId":"cad_root","documentNumber":"ASM-1","fileName":"asm.iam","cadType":"IAM"}"""),
            "/api/cad-documents/cad_root/workspace-plan" => FakeHttpHandler.Json(HttpStatusCode.OK, PlanJson()),
            _ => FakeHttpHandler.Json(HttpStatusCode.InternalServerError, """{"error":"should not be called"}"""),
        });

        var report = await Orchestrator(new HttpClient(noContent)).RunAsync(WorkspaceTestSession.Make(), request);

        Assert.True(report.ReachedValidState);
        Assert.Equal(2, report.Summary.AlreadyCurrent);
        Assert.Equal(0, report.Summary.Downloaded);
        Assert.DoesNotContain(noContent.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/api/file-versions/"));
    }

    [Fact]
    public async Task A_401_on_the_workspace_plan_aborts_cleanly_with_nothing_downloaded()
    {
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/desktop/cad-documents/resolve" => FakeHttpHandler.Json(HttpStatusCode.OK,
                """{"contract":"arch-plm.desktop-cad-document.v1","cadDocumentId":"cad_root","documentNumber":"ASM-1","fileName":"asm.iam","cadType":"IAM"}"""),
            "/api/cad-documents/cad_root/workspace-plan" => FakeHttpHandler.Json(HttpStatusCode.Unauthorized,
                """{"error":"Your session is not valid."}"""),
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"),
        });

        var ex = await Assert.ThrowsAsync<ArchApiException>(
            () => Orchestrator(new HttpClient(handler)).RunAsync(WorkspaceTestSession.Make(),
                new GetLatestRequest { Root = CadDocumentLookup.ByNumber("ASM-1"), WorkspaceRoot = _root }));

        Assert.Equal(ArchApiFailureKind.Unauthorized, ex.Kind);
        Assert.False(Directory.Exists(Path.Combine(_root, ".arch")));
        Assert.Empty(Directory.GetFiles(_root));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/api/file-versions/"));
    }

    [Fact]
    public async Task A_local_conflict_on_re_run_downgrades_that_binding_without_overwriting()
    {
        var http = new HttpClient(ServerHandler());
        var request = new GetLatestRequest { Root = CadDocumentLookup.ByNumber("ASM-1"), WorkspaceRoot = _root };
        await Orchestrator(http).RunAsync(WorkspaceTestSession.Make(), request);

        // user checks out (clears the P4C read-only guard) and edits part.ipt
        var partPath = Path.Combine(_root, "part.ipt");
        Assert.True((File.GetAttributes(partPath) & FileAttributes.ReadOnly) != 0); // Get Latest left it controlled
        File.SetAttributes(partPath, FileAttributes.Normal);
        var edited = Encoding.UTF8.GetBytes("user edits");
        await File.WriteAllBytesAsync(partPath, edited);

        var report = await Orchestrator(new HttpClient(ServerHandler())).RunAsync(WorkspaceTestSession.Make(), request);

        var partResult = report.Results.Single(r => r.RelativePath == "part.ipt");
        Assert.Equal(MaterializationStatus.Blocked, partResult.Status);
        Assert.Equal(edited, await File.ReadAllBytesAsync(Path.Combine(_root, "part.ipt")));

        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        var partEntry = mf.FindByAbsolutePath(Path.Combine(_root, "part.ipt"));
        Assert.NotNull(partEntry);
        Assert.Equal("cad_part", partEntry!.CadDocumentId);
        Assert.Equal(WorkspaceManifestEntryState.Unverified, partEntry.State);
    }

    // ---- resolve-response contract validation ---------------------------

    private FakeHttpHandler ResolveThenFail(string resolveBody) => new(req =>
        req.RequestUri!.AbsolutePath == "/api/desktop/cad-documents/resolve"
            ? FakeHttpHandler.Json(HttpStatusCode.OK, resolveBody)
            : FakeHttpHandler.Json(HttpStatusCode.InternalServerError, """{"error":"must not be reached"}"""));

    private async Task AssertResolveRejectedNoDownstream(string resolveBody)
    {
        var handler = ResolveThenFail(resolveBody);

        var ex = await Assert.ThrowsAsync<ArchApiException>(
            () => Orchestrator(new HttpClient(handler)).RunAsync(WorkspaceTestSession.Make(),
                new GetLatestRequest { Root = CadDocumentLookup.ByNumber("ASM-1"), WorkspaceRoot = _root }));

        Assert.Equal(ArchApiFailureKind.Server, ex.Kind);
        // nothing downstream: no plan fetch, no content, no workspace mutation
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("/workspace-plan"));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/api/file-versions/"));
        Assert.False(Directory.Exists(Path.Combine(_root, ".arch")));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Resolve_response_with_the_expected_contract_is_accepted()
    {
        // the happy-path ServerHandler already stamps the contract; this makes
        // the positive assertion explicit next to the negatives.
        var report = await Orchestrator(new HttpClient(ServerHandler())).RunAsync(
            WorkspaceTestSession.Make(),
            new GetLatestRequest { Root = CadDocumentLookup.ByNumber("ASM-1"), WorkspaceRoot = _root });
        Assert.True(report.ReachedValidState);
    }

    [Fact]
    public Task Resolve_response_with_the_WRONG_contract_is_rejected_before_any_downstream_call() =>
        AssertResolveRejectedNoDownstream(
            """{"contract":"arch-plm.something-else.v9","cadDocumentId":"cad_root","documentNumber":"ASM-1","fileName":"asm.iam","cadType":"IAM"}""");

    [Fact]
    public Task Resolve_response_with_NO_contract_is_rejected_before_any_downstream_call() =>
        AssertResolveRejectedNoDownstream(
            """{"cadDocumentId":"cad_root","documentNumber":"ASM-1","fileName":"asm.iam","cadType":"IAM"}""");

    [Fact]
    public Task Resolve_response_with_an_empty_cadDocumentId_is_rejected() =>
        AssertResolveRejectedNoDownstream(
            """{"contract":"arch-plm.desktop-cad-document.v1","cadDocumentId":"","documentNumber":"ASM-1","fileName":"asm.iam","cadType":"IAM"}""");

    [Fact]
    public Task Resolve_response_missing_the_identity_display_fields_is_rejected() =>
        AssertResolveRejectedNoDownstream(
            """{"contract":"arch-plm.desktop-cad-document.v1","cadDocumentId":"cad_root"}""");

    [Fact]
    public Task Resolve_response_that_is_malformed_json_is_rejected() =>
        AssertResolveRejectedNoDownstream("""{"contract":"arch-plm.desktop-cad-document.v1", not json]""");
}
