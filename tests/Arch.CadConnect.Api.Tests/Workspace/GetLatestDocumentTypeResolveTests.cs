using System.Net;
using System.Text;

using Arch.CadConnect.Api;
using Arch.CadConnect.Api.Workspace;
using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Tests.Workspace;

/// <summary>
/// P6D GET LATEST DOCUMENT TYPE FIX: proves the actual HTTP query string the
/// typed resolve sends, not just the DTO/request objects - a documentNumber
/// is no longer unique by itself (a model and its drawing may share one
/// engineering number, e.g. this suite's own "P6D-REAL-ROOT" IAM+IDW pair),
/// so Get Latest must send an explicit documentType and must never let it
/// collide with documentNumber under the wrong query parameter.
/// </summary>
public class GetLatestDocumentTypeResolveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arch-cc-doctype-" + Guid.NewGuid().ToString("N"));

    public GetLatestDocumentTypeResolveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static readonly byte[] RootIdwBytes = Encoding.UTF8.GetBytes("root-idw-bytes");
    private static readonly byte[] RootIamBytes = Encoding.UTF8.GetBytes("root-iam-bytes");
    private static readonly byte[] PartABytes = Encoding.UTF8.GetBytes("part-a-bytes");
    private static readonly byte[] PartBBytes = Encoding.UTF8.GetBytes("part-b-bytes-longer");

    private static string Sha(byte[] b) => FakeContentDownloader.Sha256Hex(b);

    /// <summary>The P6D fixture's real closure: ROOT.idw -DRAWING_REFERENCE-&gt;
    ///  ROOT.iam -COMPONENT-&gt; PART-A.ipt, PART-B.ipt.</summary>
    private const string FourDocPlanTemplate =
        """
        {"contract":{"id":"arch-plm.cad-workspace-plan","version":"1.0.0"},
         "root":{"cadDocumentId":"cad_root_idw"},"generatedAt":"2026-09-23T00:00:00Z","documentCount":4,"safe":true,
         "entries":[
           {"cadDocumentId":"cad_root_idw","documentNumber":"P6D-REAL-ROOT","fileName":"P6D-REAL-ROOT.idw","cadType":"IDW","isRoot":true,
            "placement":{"generatedRelativePath":"P6D-REAL-ROOT.idw"},"dependsOn":[{"cadDocumentId":"cad_root_iam","dependencyType":"DRAWING_REFERENCE"}],
            "canMaterialize":true,"blockedReasons":[],
            "version":{"fileVersionId":"fv_root_idw","versionNumber":1,"checksum":"__IDW_SHA__","fileSize":__IDW_LEN__,
                       "originalFileName":"P6D-REAL-ROOT.idw","contentPath":"/api/file-versions/fv_root_idw/content"}},
           {"cadDocumentId":"cad_root_iam","documentNumber":"P6D-REAL-ROOT","fileName":"P6D-REAL-ROOT.iam","cadType":"IAM","isRoot":false,
            "placement":{"generatedRelativePath":"P6D-REAL-ROOT.iam"},"dependsOn":[
              {"cadDocumentId":"cad_part_a","dependencyType":"COMPONENT"},
              {"cadDocumentId":"cad_part_b","dependencyType":"COMPONENT"}],
            "canMaterialize":true,"blockedReasons":[],
            "version":{"fileVersionId":"fv_root_iam","versionNumber":1,"checksum":"__IAM_SHA__","fileSize":__IAM_LEN__,
                       "originalFileName":"P6D-REAL-ROOT.iam","contentPath":"/api/file-versions/fv_root_iam/content"}},
           {"cadDocumentId":"cad_part_a","documentNumber":"P6D-REAL-PART-A","fileName":"P6D-REAL-PART-A.ipt","cadType":"IPT","isRoot":false,
            "placement":{"generatedRelativePath":"P6D-REAL-PART-A.ipt"},"dependsOn":[],
            "canMaterialize":true,"blockedReasons":[],
            "version":{"fileVersionId":"fv_part_a","versionNumber":1,"checksum":"__PARTA_SHA__","fileSize":__PARTA_LEN__,
                       "originalFileName":"P6D-REAL-PART-A.ipt","contentPath":"/api/file-versions/fv_part_a/content"}},
           {"cadDocumentId":"cad_part_b","documentNumber":"P6D-REAL-PART-B","fileName":"P6D-REAL-PART-B.ipt","cadType":"IPT","isRoot":false,
            "placement":{"generatedRelativePath":"P6D-REAL-PART-B.ipt"},"dependsOn":[],
            "canMaterialize":true,"blockedReasons":[],
            "version":{"fileVersionId":"fv_part_b","versionNumber":1,"checksum":"__PARTB_SHA__","fileSize":__PARTB_LEN__,
                       "originalFileName":"P6D-REAL-PART-B.ipt","contentPath":"/api/file-versions/fv_part_b/content"}}],
         "problems":[],"traversal":{"visitedCount":4,"edgeCount":3,"maxNodes":5000}}
        """;

    private static string FourDocPlanJson() => FourDocPlanTemplate
        .Replace("__IDW_SHA__", Sha(RootIdwBytes)).Replace("__IDW_LEN__", RootIdwBytes.Length.ToString())
        .Replace("__IAM_SHA__", Sha(RootIamBytes)).Replace("__IAM_LEN__", RootIamBytes.Length.ToString())
        .Replace("__PARTA_SHA__", Sha(PartABytes)).Replace("__PARTA_LEN__", PartABytes.Length.ToString())
        .Replace("__PARTB_SHA__", Sha(PartBBytes)).Replace("__PARTB_LEN__", PartBBytes.Length.ToString());

    private FakeHttpHandler IdwRootHandler() => new(req => req.RequestUri!.AbsolutePath switch
    {
        "/api/desktop/cad-documents/resolve" => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"contract":"arch-plm.desktop-cad-document.v1","cadDocumentId":"cad_root_idw","documentNumber":"P6D-REAL-ROOT","fileName":"P6D-REAL-ROOT.idw","cadType":"IDW"}"""),
        "/api/cad-documents/cad_root_idw/workspace-plan" => FakeHttpHandler.Json(HttpStatusCode.OK, FourDocPlanJson()),
        "/api/file-versions/fv_root_idw/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(RootIdwBytes) },
        "/api/file-versions/fv_root_iam/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(RootIamBytes) },
        "/api/file-versions/fv_part_a/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PartABytes) },
        "/api/file-versions/fv_part_b/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PartBBytes) },
        _ => FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"error":"no route"}"""),
    });

    private GetLatestOrchestrator Orchestrator(HttpClient http) => new(
        WorkspaceTestSession.Server,
        resolve: (s, lu, c) => GetLatestOrchestrator.ResolveViaHttpAsync(WorkspaceTestSession.Server, s, http, lu, TimeSpan.FromSeconds(5), c),
        planClientFactory: s => new WorkspacePlanClient(WorkspaceTestSession.Server, s, http, TimeSpan.FromSeconds(5)),
        downloaderFactory: s => new HttpContentDownloader(WorkspaceTestSession.Server, s, http));

    // ---- exact HTTP query string, inspected on the wire ------------------

    [Fact]
    public async Task Typed_IDW_resolve_sends_documentNumber_and_documentType_IDW_on_the_actual_request()
    {
        var handler = IdwRootHandler();
        var lookup = CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: "IDW");

        await GetLatestOrchestrator.ResolveViaHttpAsync(
            WorkspaceTestSession.Server, WorkspaceTestSession.Make(), new HttpClient(handler), lookup, TimeSpan.FromSeconds(5), default);

        var sent = handler.Requests.Single(r => r.RequestUri!.AbsolutePath == "/api/desktop/cad-documents/resolve");
        Assert.Equal("?documentNumber=P6D-REAL-ROOT&documentType=IDW", sent.RequestUri!.Query);
    }

    [Fact]
    public async Task Typed_IAM_resolve_independently_sends_documentNumber_and_documentType_IAM_on_the_actual_request()
    {
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/desktop/cad-documents/resolve"
            ? FakeHttpHandler.Json(HttpStatusCode.OK,
                """{"contract":"arch-plm.desktop-cad-document.v1","cadDocumentId":"cad_root_iam","documentNumber":"P6D-REAL-ROOT","fileName":"P6D-REAL-ROOT.iam","cadType":"IAM"}""")
            : FakeHttpHandler.Json(HttpStatusCode.NotFound, "{}"));
        var lookup = CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: "IAM");

        await GetLatestOrchestrator.ResolveViaHttpAsync(
            WorkspaceTestSession.Server, WorkspaceTestSession.Make(), new HttpClient(handler), lookup, TimeSpan.FromSeconds(5), default);

        var sent = handler.Requests.Single(r => r.RequestUri!.AbsolutePath == "/api/desktop/cad-documents/resolve");
        Assert.Equal("?documentNumber=P6D-REAL-ROOT&documentType=IAM", sent.RequestUri!.Query);
    }

    [Fact]
    public async Task An_untyped_lookup_still_sends_only_documentNumber_existing_behavior_unchanged()
    {
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/desktop/cad-documents/resolve"
            ? FakeHttpHandler.Json(HttpStatusCode.OK,
                """{"contract":"arch-plm.desktop-cad-document.v1","cadDocumentId":"cad_part_a","documentNumber":"P6D-REAL-PART-A","fileName":"P6D-REAL-PART-A.ipt","cadType":"IPT"}""")
            : FakeHttpHandler.Json(HttpStatusCode.NotFound, "{}"));
        var lookup = CadDocumentLookup.ByNumber("P6D-REAL-PART-A");

        var resolved = await GetLatestOrchestrator.ResolveViaHttpAsync(
            WorkspaceTestSession.Server, WorkspaceTestSession.Make(), new HttpClient(handler), lookup, TimeSpan.FromSeconds(5), default);

        var sent = handler.Requests.Single(r => r.RequestUri!.AbsolutePath == "/api/desktop/cad-documents/resolve");
        Assert.Equal("?documentNumber=P6D-REAL-PART-A", sent.RequestUri!.Query);
        Assert.DoesNotContain("documentType", sent.RequestUri.Query);
        Assert.Equal("cad_part_a", resolved.CadDocumentId);
    }

    // ---- end-to-end: typed IDW Get Latest through the full pipeline ------

    [Fact]
    public async Task Typed_IDW_Get_Latest_materializes_the_full_four_document_closure()
    {
        var request = new GetLatestRequest
        {
            Root = CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: "IDW"),
            WorkspaceRoot = _root,
        };

        var report = await Orchestrator(new HttpClient(IdwRootHandler())).RunAsync(WorkspaceTestSession.Make(), request);

        Assert.True(report.ReachedValidState);
        Assert.Equal(4, report.Summary.Downloaded);
        Assert.Equal(0, report.Summary.AlreadyCurrent);
        Assert.Equal(0, report.Summary.Blocked);
        Assert.Equal(0, report.Summary.Failed);
        Assert.Equal(RootIdwBytes, await File.ReadAllBytesAsync(Path.Combine(_root, "P6D-REAL-ROOT.idw")));
        Assert.Equal(RootIamBytes, await File.ReadAllBytesAsync(Path.Combine(_root, "P6D-REAL-ROOT.iam")));
        Assert.Equal(PartABytes, await File.ReadAllBytesAsync(Path.Combine(_root, "P6D-REAL-PART-A.ipt")));
        Assert.Equal(PartBBytes, await File.ReadAllBytesAsync(Path.Combine(_root, "P6D-REAL-PART-B.ipt")));
    }

    [Fact]
    public async Task Typed_IDW_Get_Latest_reports_an_already_current_file_as_current_not_re_downloaded()
    {
        // Seed the workspace exactly as the live P6D-Accept-Workspace state:
        // PART-A already materialized from a prior typed-IPT Get Latest.
        var partA = Orchestrator(new HttpClient(new FakeHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/desktop/cad-documents/resolve" => FakeHttpHandler.Json(HttpStatusCode.OK,
                """{"contract":"arch-plm.desktop-cad-document.v1","cadDocumentId":"cad_part_a","documentNumber":"P6D-REAL-PART-A","fileName":"P6D-REAL-PART-A.ipt","cadType":"IPT"}"""),
            "/api/cad-documents/cad_part_a/workspace-plan" => FakeHttpHandler.Json(HttpStatusCode.OK,
                """
                {"contract":{"id":"arch-plm.cad-workspace-plan","version":"1.0.0"},
                 "root":{"cadDocumentId":"cad_part_a"},"generatedAt":"2026-09-23T00:00:00Z","documentCount":1,"safe":true,
                 "entries":[{"cadDocumentId":"cad_part_a","documentNumber":"P6D-REAL-PART-A","fileName":"P6D-REAL-PART-A.ipt","cadType":"IPT","isRoot":true,
                  "placement":{"generatedRelativePath":"P6D-REAL-PART-A.ipt"},"dependsOn":[],"canMaterialize":true,"blockedReasons":[],
                  "version":{"fileVersionId":"fv_part_a","versionNumber":1,"checksum":"__SHA__","fileSize":__LEN__,
                             "originalFileName":"P6D-REAL-PART-A.ipt","contentPath":"/api/file-versions/fv_part_a/content"}}],
                 "problems":[],"traversal":{"visitedCount":1,"edgeCount":0,"maxNodes":5000}}
                """.Replace("__SHA__", Sha(PartABytes)).Replace("__LEN__", PartABytes.Length.ToString())),
            "/api/file-versions/fv_part_a/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PartABytes) },
            _ => FakeHttpHandler.Json(HttpStatusCode.NotFound, "{}"),
        })));
        await partA.RunAsync(WorkspaceTestSession.Make(),
            new GetLatestRequest { Root = CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-PART-A", documentType: "IPT"), WorkspaceRoot = _root });
        Assert.True(File.Exists(Path.Combine(_root, "P6D-REAL-PART-A.ipt")));

        // Now the typed IDW Get Latest pulls the full closure; PART-A is
        // already current and must not be re-downloaded.
        var request = new GetLatestRequest
        {
            Root = CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: "IDW"),
            WorkspaceRoot = _root,
        };
        var report = await Orchestrator(new HttpClient(IdwRootHandler())).RunAsync(WorkspaceTestSession.Make(), request);

        Assert.True(report.ReachedValidState);
        Assert.Equal(3, report.Summary.Downloaded);
        Assert.Equal(1, report.Summary.AlreadyCurrent);
        Assert.Equal(0, report.Summary.Blocked);
        Assert.Equal(0, report.Summary.Failed);
    }

    // ---- fail closed ------------------------------------------------------

    [Fact]
    public void Blank_document_type_fails_before_any_HTTP_resolution_is_attempted()
    {
        Assert.Throws<ArgumentException>(() =>
            CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: " "));
    }

    [Fact]
    public void Unsupported_document_type_fails_safely_before_any_HTTP_resolution_is_attempted()
    {
        Assert.Throws<ArgumentException>(() =>
            CadDocumentLookup.ByNumber(documentNumber: "P6D-REAL-ROOT", documentType: "STEP"));
    }

    [Fact]
    public async Task Server_side_ambiguity_still_fails_closed_when_the_client_omits_the_type()
    {
        // Mirrors the live blocker: server correctly 409s an ambiguous
        // number-only lookup. The client must surface it, never guess.
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath == "/api/desktop/cad-documents/resolve"
            ? FakeHttpHandler.Json(HttpStatusCode.Conflict, """{"error":"Multiple documents share this number; specify documentType."}""")
            : FakeHttpHandler.Json(HttpStatusCode.NotFound, "{}"));

        var ex = await Assert.ThrowsAsync<ArchApiException>(() => GetLatestOrchestrator.ResolveViaHttpAsync(
            WorkspaceTestSession.Server, WorkspaceTestSession.Make(), new HttpClient(handler),
            CadDocumentLookup.ByNumber("P6D-REAL-ROOT"), TimeSpan.FromSeconds(5), default));

        Assert.Equal(ArchApiFailureKind.Server, ex.Kind);
        Assert.Equal(409, ex.HttpStatus);
    }
}
