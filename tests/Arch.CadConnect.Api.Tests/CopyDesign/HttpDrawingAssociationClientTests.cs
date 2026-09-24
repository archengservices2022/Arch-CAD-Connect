using System.Net;

using Arch.CadConnect.Api.CopyDesign;
using Arch.CadConnect.Api.Tests.Workspace;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Tests.CopyDesign;

/// <summary>
/// P6D LIVE ACCEPTANCE BLOCKER FIX: <see cref="HttpDrawingAssociationClient"/>
/// is the actual code that previously did not exist - Copy Design Preview had
/// no way to ask the server "which drawing(s) belong to this model?" at all.
/// These tests inspect the ACTUAL HTTP request (query string, ids sent
/// verbatim) and prove the response mapping is fail-closed at every seam.
/// </summary>
public sealed class HttpDrawingAssociationClientTests
{
    private static readonly ArchServerUri Server = ArchServerUri.Parse("https://plm.example.com");

    private static IArchSession Session() => WorkspaceTestSession.Make();

    private HttpDrawingAssociationClient Client(FakeHttpHandler handler) =>
        new(Server, new HttpClient(handler), TimeSpan.FromSeconds(5));

    // ---- request construction --------------------------------------------

    [Fact]
    public async Task Sends_every_requested_model_id_verbatim_on_the_actual_query_string()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"contract":"arch-plm.desktop-drawing-associations.v1","results":[{"cadDocumentId":"cad_root_iam","recognized":true,"drawings":[]},{"cadDocumentId":"cad_part_a","recognized":true,"drawings":[]}]}"""));

        await Client(handler).FetchAsync(Session(), new[] { "cad_root_iam", "cad_part_a" }, manifest: null);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("/api/desktop/cad-documents/drawing-associations", sent.RequestUri!.AbsolutePath);
        Assert.Equal("?cadDocumentId=cad_root_iam&cadDocumentId=cad_part_a", sent.RequestUri.Query);
    }

    [Fact]
    public async Task Empty_id_collection_makes_no_HTTP_request_at_all()
    {
        var handler = new FakeHttpHandler(_ =>
            FakeHttpHandler.Json(HttpStatusCode.InternalServerError, """{"error":"must not be reached"}"""));

        var result = await Client(handler).FetchAsync(Session(), Array.Empty<string>(), manifest: null);

        Assert.Empty(result);
        Assert.Empty(handler.Requests);
    }

    // ---- P6D fixture: model retrieves its authoritative drawing ----------

    [Fact]
    public async Task Model_stable_id_retrieves_its_authoritative_drawing()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """
            {"contract":"arch-plm.desktop-drawing-associations.v1","results":[
              {"cadDocumentId":"cad_root_iam","recognized":true,"drawings":[
                {"cadDocumentId":"cad_root_idw","documentNumber":"P6D-REAL-ROOT","fileName":"P6D-REAL-ROOT.idw",
                 "cadType":"IDW","currentFileVersionId":"fv_idw_1"}]}]}
            """));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_root_iam" }, manifest: null);

        var association = result["cad_root_iam"];
        Assert.Equal(DrawingAssociationOutcome.Found, association.Outcome);
        var drawing = Assert.Single(association.Drawings);
        Assert.Equal("cad_root_idw", drawing.CadDocumentId);
        Assert.Equal("fv_idw_1", drawing.CurrentFileVersionId);
        Assert.Equal(Arch.CadConnect.Core.CadDocumentType.Idw, drawing.DocumentType);
        Assert.True(drawing.IsVerified);
    }

    [Fact]
    public async Task Same_documentNumber_IAM_plus_IDW_independent_results_never_cross_contaminate()
    {
        // The wire DTO carries no documentNumber for the QUERIED model (only
        // for a returned drawing), so this proves the client keys results by
        // the exact requested cadDocumentId, never anything derived from a
        // shared documentNumber.
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """
            {"contract":"arch-plm.desktop-drawing-associations.v1","results":[
              {"cadDocumentId":"cad_root_iam","recognized":true,"drawings":[
                {"cadDocumentId":"cad_root_idw","documentNumber":"P6D-REAL-ROOT","fileName":"P6D-REAL-ROOT.idw",
                 "cadType":"IDW","currentFileVersionId":"fv_idw_1"}]},
              {"cadDocumentId":"cad_root_idw","recognized":true,"drawings":[]}]}
            """));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_root_iam", "cad_root_idw" }, manifest: null);

        Assert.Single(result["cad_root_iam"].Drawings);
        Assert.Empty(result["cad_root_idw"].Drawings);
        Assert.Equal(DrawingAssociationOutcome.Found, result["cad_root_idw"].Outcome);
    }

    // ---- local absolute-path enrichment (never authority) ----------------

    [Fact]
    public async Task A_drawing_already_in_the_local_workspace_manifest_is_enriched_with_its_absolute_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "arch-cc-daclient-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".arch"));
        try
        {
            // Write the manifest JSON directly (WorkspaceManifest's own
            // schema - see WorkspaceManifest.cs) rather than driving a full
            // Get Latest run, so this test only depends on the ONE thing it
            // is actually about: FindByCadDocumentId's path resolution.
            File.WriteAllText(Path.Combine(root, WorkspaceManifest.RelativeManifestPath), $$"""
                {
                  "schema": "arch-plm.workspace-manifest.v1",
                  "serverOrigin": "https://plm.example.com",
                  "organizationId": "org1",
                  "organizationCode": "ORGA",
                  "rootCadDocumentId": "cad_root_idw",
                  "rootDocumentNumber": "P6D-REAL-ROOT",
                  "updatedAtUtc": "2026-09-23T00:00:00Z",
                  "entries": [
                    {
                      "relativePath": "P6D-REAL-ROOT.idw",
                      "cadDocumentId": "cad_root_idw",
                      "documentNumber": "P6D-REAL-ROOT",
                      "fileName": "P6D-REAL-ROOT.idw",
                      "cadType": "IDW",
                      "fileVersionId": "fv_idw_1",
                      "versionNumber": 1,
                      "checksum": "{{new string('a', 64)}}",
                      "fileSize": 8,
                      "isRoot": true,
                      "dependsOn": [],
                      "state": "Verified",
                      "retrievedAtUtc": "2026-09-23T00:00:00Z"
                    }
                  ]
                }
                """);
            var manifest = WorkspaceManifest.LoadOrEmpty(root);

            var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
                """
                {"contract":"arch-plm.desktop-drawing-associations.v1","results":[
                  {"cadDocumentId":"cad_root_iam","recognized":true,"drawings":[
                    {"cadDocumentId":"cad_root_idw","documentNumber":"P6D-REAL-ROOT","fileName":"P6D-REAL-ROOT.idw",
                     "cadType":"IDW","currentFileVersionId":"fv_idw_1"}]}]}
                """));

            var result = await Client(handler).FetchAsync(Session(), new[] { "cad_root_iam" }, manifest);

            var drawing = Assert.Single(result["cad_root_iam"].Drawings);
            Assert.Equal(Path.Combine(root, "P6D-REAL-ROOT.idw"), drawing.AbsolutePath);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task A_drawing_NOT_in_the_local_manifest_is_claimed_but_unresolved_null_path_never_a_guess()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """
            {"contract":"arch-plm.desktop-drawing-associations.v1","results":[
              {"cadDocumentId":"cad_root_iam","recognized":true,"drawings":[
                {"cadDocumentId":"cad_root_idw","documentNumber":"P6D-REAL-ROOT","fileName":"P6D-REAL-ROOT.idw",
                 "cadType":"IDW","currentFileVersionId":"fv_idw_1"}]}]}
            """));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_root_iam" }, manifest: null);

        Assert.Null(Assert.Single(result["cad_root_iam"].Drawings).AbsolutePath);
    }

    // ---- empty association vs unavailable authority: distinct ------------

    [Fact]
    public async Task Recognized_with_zero_drawings_is_Found_never_NotAvailable()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"contract":"arch-plm.desktop-drawing-associations.v1","results":[{"cadDocumentId":"cad_part_a","recognized":true,"drawings":[]}]}"""));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_part_a" }, manifest: null);

        Assert.Equal(DrawingAssociationOutcome.Found, result["cad_part_a"].Outcome);
        Assert.Empty(result["cad_part_a"].Drawings);
    }

    [Fact]
    public async Task Recognized_false_is_NotAvailable()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"contract":"arch-plm.desktop-drawing-associations.v1","results":[{"cadDocumentId":"cad_unknown","recognized":false}]}"""));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_unknown" }, manifest: null);

        Assert.Equal(DrawingAssociationOutcome.NotAvailable, result["cad_unknown"].Outcome);
    }

    [Fact]
    public async Task An_id_the_server_omits_from_results_is_NotAvailable_never_a_currency_guess()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"contract":"arch-plm.desktop-drawing-associations.v1","results":[]}"""));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_asked_about" }, manifest: null);

        Assert.Equal(DrawingAssociationOutcome.NotAvailable, result["cad_asked_about"].Outcome);
    }

    // ---- fail closed: transport / auth / contract / malformed ------------

    [Fact]
    public async Task Network_failure_fails_every_requested_id_closed()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("boom"));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_a", "cad_b" }, manifest: null);

        Assert.Equal(DrawingAssociationOutcome.NotAvailable, result["cad_a"].Outcome);
        Assert.Equal(DrawingAssociationOutcome.NotAvailable, result["cad_b"].Outcome);
    }

    [Fact]
    public async Task A_401_fails_every_requested_id_closed()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Unauthorized, """{"error":"no"}"""));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_a" }, manifest: null);

        Assert.Equal(DrawingAssociationOutcome.NotAvailable, result["cad_a"].Outcome);
    }

    [Fact]
    public async Task A_404_older_server_without_this_route_fails_closed_exactly_like_the_old_stub()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"error":"no route"}"""));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_a" }, manifest: null);

        Assert.Equal(DrawingAssociationOutcome.NotAvailable, result["cad_a"].Outcome);
    }

    [Fact]
    public async Task Wrong_contract_fails_every_requested_id_closed()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"contract":"arch-plm.something-else.v9","results":[{"cadDocumentId":"cad_a","recognized":true,"drawings":[]}]}"""));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_a" }, manifest: null);

        Assert.Equal(DrawingAssociationOutcome.NotAvailable, result["cad_a"].Outcome);
    }

    [Fact]
    public async Task Malformed_JSON_fails_every_requested_id_closed()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, "{not json]"));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_a" }, manifest: null);

        Assert.Equal(DrawingAssociationOutcome.NotAvailable, result["cad_a"].Outcome);
    }

    [Fact]
    public async Task Recognized_true_with_no_drawings_array_is_malformed_never_treated_as_empty()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"contract":"arch-plm.desktop-drawing-associations.v1","results":[{"cadDocumentId":"cad_a","recognized":true}]}"""));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_a" }, manifest: null);

        Assert.Equal(DrawingAssociationOutcome.NotAvailable, result["cad_a"].Outcome);
    }

    [Fact]
    public async Task A_malformed_drawing_entry_blank_cadDocumentId_fails_that_model_id_closed()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """
            {"contract":"arch-plm.desktop-drawing-associations.v1","results":[
              {"cadDocumentId":"cad_a","recognized":true,"drawings":[
                {"cadDocumentId":"","documentNumber":"X","fileName":"x.idw","cadType":"IDW","currentFileVersionId":null}]}]}
            """));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_a" }, manifest: null);

        Assert.Equal(DrawingAssociationOutcome.NotAvailable, result["cad_a"].Outcome);
    }

    [Fact]
    public async Task An_unrecognized_cadType_becomes_Unknown_not_dropped_so_planner_validation_still_sees_it()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """
            {"contract":"arch-plm.desktop-drawing-associations.v1","results":[
              {"cadDocumentId":"cad_a","recognized":true,"drawings":[
                {"cadDocumentId":"cad_weird","documentNumber":"X","fileName":"x.step","cadType":"STEP","currentFileVersionId":null}]}]}
            """));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_a" }, manifest: null);

        var drawing = Assert.Single(result["cad_a"].Drawings);
        Assert.Equal(Arch.CadConnect.Core.CadDocumentType.Unknown, drawing.DocumentType);
    }

    [Fact]
    public async Task Duplicate_cadDocumentId_in_results_is_ambiguous_fails_closed_never_last_wins()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK,
            """
            {"contract":"arch-plm.desktop-drawing-associations.v1","results":[
              {"cadDocumentId":"cad_a","recognized":true,"drawings":[]},
              {"cadDocumentId":"cad_a","recognized":false}]}
            """));

        var result = await Client(handler).FetchAsync(Session(), new[] { "cad_a" }, manifest: null);

        Assert.Equal(DrawingAssociationOutcome.NotAvailable, result["cad_a"].Outcome);
    }
}
