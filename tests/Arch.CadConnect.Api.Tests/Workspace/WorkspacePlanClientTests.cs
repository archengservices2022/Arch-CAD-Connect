using System.Net;

using Arch.CadConnect.Api;
using Arch.CadConnect.Api.Workspace;

namespace Arch.CadConnect.Api.Tests.Workspace;

public class WorkspacePlanClientTests
{
    private static WorkspacePlanClient Client(FakeHttpHandler handler) =>
        new(WorkspaceTestSession.Server, WorkspaceTestSession.Make(), new HttpClient(handler), TimeSpan.FromSeconds(5));

    private const string SafePlan = """
        {"contract":{"id":"arch-plm.cad-workspace-plan","version":"1.0.0"},
         "root":{"cadDocumentId":"cad_root"},"generatedAt":"2026-09-06T00:00:00Z","documentCount":1,
         "safe":true,
         "entries":[{"cadDocumentId":"cad_root","documentNumber":"ASM-1","fileName":"a.iam","cadType":"IAM",
                     "isRoot":true,"placement":{"generatedRelativePath":"a.iam"},"dependsOn":[],
                     "canMaterialize":true,"blockedReasons":[],
                     "version":{"fileVersionId":"fv_1","versionNumber":3,"checksum":"abc","fileSize":10,
                                "originalFileName":"a.iam","contentPath":"/api/file-versions/fv_1/content"}}],
         "problems":[],"traversal":{"visitedCount":1,"edgeCount":0,"maxNodes":5000}}
        """;

    [Fact]
    public async Task Fetches_and_returns_a_safe_plan_over_the_bearer_endpoint()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, SafePlan);
        var plan = await Client(handler).FetchAsync("cad_root");

        Assert.True(plan.Safe);
        Assert.Single(plan.Entries);
        Assert.Equal("fv_1", plan.Entries[0].Version!.FileVersionId);
        Assert.Equal("Bearer arch_dt_TESTTOKEN123456", handler.LastAuthorizationHeader);
        Assert.Equal("https://plm.example.com/api/cad-documents/cad_root/workspace-plan",
            handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task An_unsafe_plan_is_refused_wholesale_with_the_problems_list()
    {
        var unsafePlan = SafePlan
            .Replace("\"safe\":true", "\"safe\":false")
            .Replace("\"problems\":[]",
                "\"problems\":[{\"kind\":\"missing-binary\",\"message\":\"no stored binary for ASM-1\"}]");
        var ex = await Assert.ThrowsAsync<UnsafeWorkspacePlanException>(
            () => Client(FakeHttpHandler.Always(HttpStatusCode.OK, unsafePlan)).FetchAsync("cad_root"));
        Assert.Single(ex.Problems);
        Assert.Contains("missing-binary", ex.Problems[0].Kind);
    }

    [Fact]
    public async Task An_unknown_contract_id_or_version_is_refused()
    {
        var wrongId = SafePlan.Replace("arch-plm.cad-workspace-plan", "arch-plm.something-else");
        await Assert.ThrowsAsync<UnsupportedWorkspaceContractException>(
            () => Client(FakeHttpHandler.Always(HttpStatusCode.OK, wrongId)).FetchAsync("cad_root"));

        var wrongVersion = SafePlan.Replace("\"version\":\"1.0.0\"", "\"version\":\"2.0.0\"");
        await Assert.ThrowsAsync<UnsupportedWorkspaceContractException>(
            () => Client(FakeHttpHandler.Always(HttpStatusCode.OK, wrongVersion)).FetchAsync("cad_root"));
    }

    [Fact]
    public async Task Redirect_is_refused_and_never_followed()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://evil.example.com/x");
            return r;
        });
        var ex = await Assert.ThrowsAsync<ArchApiException>(() => Client(handler).FetchAsync("cad_root"));
        Assert.Equal(ArchApiFailureKind.Server, ex.Kind);
    }

    [Fact]
    public async Task Http_401_maps_to_a_safe_Unauthorized_ArchApiException()
    {
        var ex = await Assert.ThrowsAsync<ArchApiException>(
            () => Client(FakeHttpHandler.Always(HttpStatusCode.Unauthorized, """{"error":"Authentication required"}""")).FetchAsync("cad_root"));
        Assert.Equal(ArchApiFailureKind.Unauthorized, ex.Kind);
    }

    [Fact]
    public async Task Http_404_maps_to_NotFound()
    {
        var ex = await Assert.ThrowsAsync<ArchApiException>(
            () => Client(FakeHttpHandler.Always(HttpStatusCode.NotFound, """{"error":"not found"}""")).FetchAsync("cad_missing"));
        Assert.Equal(ArchApiFailureKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Malformed_json_is_a_generic_server_failure()
    {
        var ex = await Assert.ThrowsAsync<ArchApiException>(
            () => Client(FakeHttpHandler.Always(HttpStatusCode.OK, "{ not json")).FetchAsync("cad_root"));
        Assert.Equal(ArchApiFailureKind.Server, ex.Kind);
    }
}
