using System.Net;

using Arch.CadConnect.Api;
using Arch.CadConnect.Api.Workspace;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Tests.Workspace;

public class CheckoutHttpClientsTests
{
    private static CheckoutHttpClient Client(FakeHttpHandler handler) =>
        new(WorkspaceTestSession.Server, WorkspaceTestSession.Make(), new HttpClient(handler), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task RequestCheckout_201_created_returns_the_base_binding_over_bearer()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created,
            """{"status":"created","checkout":{"id":"co_1","cadDocumentId":"cad_1","baseFileVersionId":"fv_9","baseVersionNumber":4,"checkedOutBy":{"name":"Me","email":"me@x.com"}}}"""));

        var result = await Client(handler).RequestCheckoutAsync("cad_1");

        Assert.Equal(CheckoutOutcome.Created, result.Kind);
        Assert.Equal("co_1", result.CheckoutId);
        Assert.Equal("fv_9", result.BaseFileVersionId);
        Assert.Equal(4, result.BaseVersionNumber);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://plm.example.com/api/cad-documents/cad_1/checkout", req.RequestUri!.ToString());
        Assert.Equal("Bearer arch_dt_TESTTOKEN123456", handler.LastAuthorizationHeader);
    }

    [Fact]
    public async Task RequestCheckout_200_already_mine()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"status":"already-mine","checkout":{"id":"co_1","cadDocumentId":"cad_1","baseFileVersionId":"fv_9","baseVersionNumber":4}}"""));
        var result = await Client(handler).RequestCheckoutAsync("cad_1");
        Assert.Equal(CheckoutOutcome.AlreadyMine, result.Kind);
    }

    [Fact]
    public async Task RequestCheckout_409_throws_CheckoutConflictException_with_holder_only()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Conflict,
            """{"error":"checked out by another user","checkout":{"id":"co_x","cadDocumentId":"cad_1","baseFileVersionId":"fv_1","baseVersionNumber":1,"checkedOutBy":{"name":"Dana Lee","email":"dana@x.com"}}}"""));

        var ex = await Assert.ThrowsAsync<CheckoutConflictException>(() => Client(handler).RequestCheckoutAsync("cad_1"));
        Assert.Equal("Dana Lee", ex.HolderName);
        Assert.Equal("dana@x.com", ex.HolderEmail);
        Assert.Contains("Dana Lee", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, ArchApiFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, ArchApiFailureKind.NotFound)]
    [InlineData((HttpStatusCode)422, ArchApiFailureKind.Conflict)]
    public async Task RequestCheckout_maps_error_status_codes(HttpStatusCode code, ArchApiFailureKind kind)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(code, """{"error":"nope"}"""));
        var ex = await Assert.ThrowsAsync<ArchApiException>(() => Client(handler).RequestCheckoutAsync("cad_1"));
        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task RequestCheckout_refuses_a_redirect()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://evil.example.com/x");
            return r;
        });
        var ex = await Assert.ThrowsAsync<ArchApiException>(() => Client(handler).RequestCheckoutAsync("cad_1"));
        Assert.Equal(ArchApiFailureKind.Server, ex.Kind);
    }

    [Fact]
    public async Task GetStatus_maps_state_holder_and_base_identity()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"state":"mine","checkout":{"id":"co_42","cadDocumentId":"cad_1","baseFileVersionId":"fv_7","baseVersionNumber":3,"checkedOutBy":{"name":"Sam","email":"sam@x.com"}}}"""));
        var status = await Client(handler).GetStatusAsync("cad_1");
        Assert.Equal(ServerCheckoutState.Mine, status.State);
        Assert.Equal("co_42", status.CheckoutId);
        Assert.Equal("fv_7", status.BaseFileVersionId);
        Assert.Equal(3, status.BaseVersionNumber);
    }

    [Fact]
    public async Task GetStatus_available_has_no_checkout_detail()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"state":"available"}"""));
        var status = await Client(handler).GetStatusAsync("cad_1");
        Assert.Equal(ServerCheckoutState.Available, status.State);
        Assert.Null(status.CheckoutId);
        Assert.Null(status.BaseFileVersionId);
    }

    [Fact]
    public async Task GetStatus_locked_by_another_surfaces_the_holder()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"state":"locked","checkout":{"id":"co","cadDocumentId":"cad_1","baseFileVersionId":"fv","baseVersionNumber":1,"checkedOutBy":{"name":"Sam","email":"sam@x.com"}}}"""));
        var status = await Client(handler).GetStatusAsync("cad_1");
        Assert.Equal(ServerCheckoutState.Locked, status.State);
        Assert.Equal("Sam", status.HolderName);
    }

    [Fact]
    public async Task Undo_200_returns_the_base_file_version()
    {
        var handler = new FakeHttpHandler(req =>
        {
            Assert.Equal("https://plm.example.com/api/cad-documents/cad_1/checkout/undo", req.RequestUri!.ToString());
            return FakeHttpHandler.Json(HttpStatusCode.OK,
                """{"cadDocumentId":"cad_1","checkoutId":"co_1","baseFileVersionId":"fv_1","previousOwnerId":"u1"}""");
        });
        var result = await Client(handler).UndoAsync("cad_1", reason: "done");
        Assert.Equal("fv_1", result.BaseFileVersionId);
        Assert.Contains("\"reason\":\"done\"", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Undo_409_maps_to_Conflict()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Conflict, """{"error":"not checked out"}"""));
        var ex = await Assert.ThrowsAsync<ArchApiException>(() => Client(handler).UndoAsync("cad_1", null));
        Assert.Equal(ArchApiFailureKind.Conflict, ex.Kind);
    }
}
