using System.Net;
using System.Security.Cryptography;
using System.Text;

using Arch.CadConnect.Api;
using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Tests;

/// <summary>
/// End-to-end through the public <see cref="ArchApiClient.GetLatestAsync"/>
/// seam (resolve -> workspace-plan -> content -> materialize -> manifest),
/// driven by a scripted <see cref="FakeHttpHandler"/> into a disposable temp
/// directory. No network, no Inventor.
/// </summary>
public sealed class ArchApiClientGetLatestTests : IDisposable
{
    private static readonly ArchServerUri Server = ArchServerUri.Parse("https://plm.example.com");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arch-cc-client-gl-" + Guid.NewGuid().ToString("N"));

    public ArchApiClientGetLatestTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static readonly byte[] AsmBytes = Encoding.UTF8.GetBytes("root-assembly-bytes");
    private static readonly byte[] PartBytes = Encoding.UTF8.GetBytes("child-part-bytes-xyz");

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private const string PlanTemplate =
        """
        {"contract":{"id":"arch-plm.cad-workspace-plan","version":"1.0.0"},
         "root":{"cadDocumentId":"cad_root"},"generatedAt":"2026-09-06T00:00:00Z","documentCount":2,"safe":true,
         "entries":[
           {"cadDocumentId":"cad_root","documentNumber":"ASM-9","fileName":"top.iam","cadType":"IAM","isRoot":true,
            "placement":{"generatedRelativePath":"top.iam"},"dependsOn":[{"cadDocumentId":"cad_part","dependencyType":"COMPONENT"}],
            "canMaterialize":true,"blockedReasons":[],
            "version":{"fileVersionId":"fv_asm","versionNumber":7,"checksum":"__ASM_SHA__","fileSize":__ASM_LEN__,
                       "originalFileName":"top.iam","contentPath":"/api/file-versions/fv_asm/content"}},
           {"cadDocumentId":"cad_part","documentNumber":"PRT-9","fileName":"child.ipt","cadType":"IPT","isRoot":false,
            "placement":{"generatedRelativePath":"child.ipt"},"dependsOn":[],
            "canMaterialize":true,"blockedReasons":[],
            "version":{"fileVersionId":"fv_part","versionNumber":3,"checksum":"__PART_SHA__","fileSize":__PART_LEN__,
                       "originalFileName":"child.ipt","contentPath":"/api/file-versions/fv_part/content"}}],
         "problems":[],"traversal":{"visitedCount":2,"edgeCount":1,"maxNodes":5000}}
        """;

    private static string PlanJson() => PlanTemplate
        .Replace("__ASM_SHA__", Sha(AsmBytes))
        .Replace("__ASM_LEN__", AsmBytes.Length.ToString())
        .Replace("__PART_SHA__", Sha(PartBytes))
        .Replace("__PART_LEN__", PartBytes.Length.ToString());

    private FakeHttpHandler Handler() => new(req =>
    {
        Assert.Equal("Bearer arch_dt_TESTTOKEN123456", req.Headers.Authorization?.ToString());
        return req.RequestUri!.AbsolutePath switch
        {
            "/api/desktop/cad-documents/resolve" => FakeHttpHandler.Json(HttpStatusCode.OK,
                """{"contract":"arch-plm.desktop-cad-document.v1","cadDocumentId":"cad_root","documentNumber":"ASM-9","fileName":"top.iam","cadType":"IAM"}"""),
            "/api/cad-documents/cad_root/workspace-plan" => FakeHttpHandler.Json(HttpStatusCode.OK, PlanJson()),
            "/api/file-versions/fv_asm/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(AsmBytes) },
            "/api/file-versions/fv_part/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PartBytes) },
            _ => FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"error":"no route"}"""),
        };
    });

    private ArchApiClient Client(FakeHttpHandler handler) =>
        new(Server, new HttpClient(handler), new ArchApiClientOptions { Timeout = TimeSpan.FromSeconds(10) });

    private static IArchSession Session() => new DesktopSession(
        Server,
        new ArchIdentity("u1", "Test User", "t@orga.com", "VIEWER", "org1", "ORGA", "Org A"),
        "arch_dt_TESTTOKEN123456",
        DateTimeOffset.UtcNow.AddHours(12),
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task GetLatestAsync_resolves_downloads_verifies_and_writes_the_manifest()
    {
        var request = new GetLatestRequest { Root = CadDocumentLookup.ByNumber("ASM-9"), WorkspaceRoot = _root };

        var report = await Client(Handler()).GetLatestAsync(Session(), request);

        Assert.True(report.ReachedValidState);
        Assert.Equal(2, report.Summary.Downloaded);
        Assert.Equal(AsmBytes, await File.ReadAllBytesAsync(Path.Combine(_root, "top.iam")));
        Assert.Equal(PartBytes, await File.ReadAllBytesAsync(Path.Combine(_root, "child.ipt")));

        var mf = WorkspaceManifest.LoadOrEmpty(_root);
        var rootEntry = mf.FindByAbsolutePath(Path.Combine(_root, "top.iam"));
        Assert.NotNull(rootEntry);
        Assert.Equal("cad_root", rootEntry!.CadDocumentId);
        Assert.Equal("fv_asm", rootEntry.FileVersionId);
        Assert.Equal(WorkspaceManifestEntryState.Verified, rootEntry.State);
    }

    [Fact]
    public async Task GetLatestAsync_refuses_an_unsafe_plan_wholesale_and_downloads_nothing()
    {
        var handler = new FakeHttpHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/desktop/cad-documents/resolve" => FakeHttpHandler.Json(HttpStatusCode.OK,
                """{"contract":"arch-plm.desktop-cad-document.v1","cadDocumentId":"cad_root","documentNumber":"ASM-9","fileName":"top.iam","cadType":"IAM"}"""),
            "/api/cad-documents/cad_root/workspace-plan" => FakeHttpHandler.Json(HttpStatusCode.OK,
                PlanJson().Replace("\"safe\":true", "\"safe\":false")),
            _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"),
        });

        await Assert.ThrowsAsync<Arch.CadConnect.Api.Workspace.UnsafeWorkspacePlanException>(
            () => Client(handler).GetLatestAsync(Session(),
                new GetLatestRequest { Root = CadDocumentLookup.ByNumber("ASM-9"), WorkspaceRoot = _root }));

        Assert.False(Directory.Exists(Path.Combine(_root, ".arch")));
        Assert.Empty(Directory.GetFiles(_root));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/api/file-versions/"));
    }

    [Fact]
    public async Task GetLatestAsync_maps_resolve_404_to_a_NotFound_exception()
    {
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"error":"No CAD document matched that lookup in your organization."}"""));

        var ex = await Assert.ThrowsAsync<ArchApiException>(
            () => Client(handler).GetLatestAsync(Session(),
                new GetLatestRequest { Root = CadDocumentLookup.ByNumber("MISSING"), WorkspaceRoot = _root }));

        Assert.Equal(ArchApiFailureKind.NotFound, ex.Kind);
    }
}
