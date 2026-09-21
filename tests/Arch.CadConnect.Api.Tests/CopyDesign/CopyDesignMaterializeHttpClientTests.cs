using System.Net;
using System.Security.Cryptography;
using System.Text;

using Arch.CadConnect.Api.CopyDesign;
using Arch.CadConnect.Api.Tests.Workspace;
using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign.Apply;

namespace Arch.CadConnect.Api.Tests.CopyDesign;

/// <summary>
/// P6C: the dedicated first-FileVersion materialization client. Mirrors
/// <c>CheckInClientTests</c>'s conventions (same <see cref="FakeHttpHandler"/>,
/// same <see cref="WorkspaceTestSession"/>) - this client replaces the
/// earlier CheckIn-based workaround, so there is deliberately no dependency
/// on <c>CheckInClient</c> anywhere in this file or in the class under test.
/// </summary>
public class CopyDesignMaterializeHttpClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arch-cc-materialize-" + Guid.NewGuid().ToString("N"));

    public CopyDesignMaterializeHttpClientTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteFile(byte[] bytes, string name = "10137-P001.ipt")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static CopyDesignMaterializeHttpClient Client(FakeHttpHandler handler) =>
        new(WorkspaceTestSession.Server, WorkspaceTestSession.Make(), new HttpClient(handler), TimeSpan.FromSeconds(5));

    private static CopyDesignMaterializeRequest RequestFor(string localFilePath, byte[] bytes) => new(
        CopyDesignOperationId: "op-1",
        ResultingCadDocumentId: "cad-new-1",
        SourceCadDocumentId: "cad-src-1",
        DocumentType: CadDocumentType.Ipt,
        LocalFilePath: localFilePath,
        FileName: "10137-P001.ipt",
        ExpectedSha256: Sha(bytes),
        ExpectedFileSize: bytes.Length);

    private static string HappyJson(CopyDesignMaterializeRequest request, int versionNumber = 1) =>
        $$"""
        {"cadDocumentId":"{{request.ResultingCadDocumentId}}","fileVersionId":"fv-1","versionNumber":{{versionNumber}},
         "fileName":"{{request.FileName}}","fileSize":{{request.ExpectedFileSize}},"sha256":"{{request.ExpectedSha256}}",
         "copyDesignOperationId":"{{request.CopyDesignOperationId}}"}
        """;

    // ---- 1-7: exact request shape ---------------------------------------

    [Fact]
    public async Task Sends_the_exact_materialize_URL_with_the_resulting_cadDocumentId()
    {
        var bytes = Encoding.UTF8.GetBytes("verified destination binary");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(request)));

        await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(
            "https://plm.example.com/api/desktop/copy-design/materialize/cad-new-1",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Sends_the_correct_operation_header()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(request)));

        await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal("op-1", handler.Requests[0].Headers.GetValues("X-Copy-Design-Operation-Id").Single());
    }

    [Fact]
    public async Task Sends_the_correct_source_cad_document_header()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(request)));

        await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal("cad-src-1", handler.Requests[0].Headers.GetValues("X-Source-Cad-Document-Id").Single());
    }

    [Fact]
    public async Task Sends_the_correct_document_type_header()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes) with { DocumentType = CadDocumentType.Iam };
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(request)));

        await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal("IAM", handler.Requests[0].Headers.GetValues("X-Document-Type").Single());
    }

    [Fact]
    public async Task Sends_the_final_local_SHA_header()
    {
        var bytes = Encoding.UTF8.GetBytes("content for hashing");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(request)));

        await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(Sha(bytes), handler.Requests[0].Headers.GetValues("X-Expected-Sha256").Single());
    }

    [Fact]
    public async Task Sends_the_final_local_size_header()
    {
        var bytes = Encoding.UTF8.GetBytes("content of a specific length");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(request)));

        await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(bytes.Length.ToString(), handler.Requests[0].Headers.GetValues("X-Expected-File-Size").Single());
    }

    [Fact]
    public async Task Sends_the_actual_final_binary_as_the_request_body()
    {
        var bytes = Encoding.UTF8.GetBytes("the EXACT verified destination bytes");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(request)));

        await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(Encoding.UTF8.GetString(bytes), handler.RequestBodies[0]);
    }

    // ---- 8-9: status mapping ---------------------------------------------

    [Fact]
    public async Task A_201_response_is_accepted_as_Materialized()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(request)));

        var result = await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, result.Outcome);
        Assert.Equal("fv-1", result.FileVersionId);
    }

    [Fact]
    public async Task A_200_idempotent_replay_response_is_ALSO_accepted_as_Materialized()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, HappyJson(request)));

        var result = await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, result.Outcome);
    }

    // ---- 10-15: mandatory post-response validation, fail closed ----------

    [Fact]
    public async Task A_response_naming_a_DIFFERENT_cadDocumentId_fails_closed()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var wrong = request with { ResultingCadDocumentId = "cad-DIFFERENT" };
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(wrong)));

        var result = await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(CopyDesignMaterializationOutcome.Failed, result.Outcome);
        Assert.Null(result.FileVersionId);
    }

    [Fact]
    public async Task A_response_naming_a_DIFFERENT_operationId_fails_closed()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var wrong = request with { CopyDesignOperationId = "op-DIFFERENT" };
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(wrong)));

        var result = await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(CopyDesignMaterializationOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_versionNumber_other_than_1_fails_closed()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(request, versionNumber: 2)));

        var result = await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(CopyDesignMaterializationOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_SHA256_mismatch_between_response_and_locally_verified_value_fails_closed()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var wrong = request with { ExpectedSha256 = new string('f', 64) };
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(wrong)));

        var result = await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(CopyDesignMaterializationOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_file_size_mismatch_between_response_and_locally_verified_value_fails_closed()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var wrong = request with { ExpectedFileSize = request.ExpectedFileSize + 999 };
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(wrong)));

        var result = await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(CopyDesignMaterializationOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_blank_fileVersionId_fails_closed()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created,
            $$"""{"cadDocumentId":"{{request.ResultingCadDocumentId}}","fileVersionId":"","versionNumber":1,"fileName":"x","fileSize":{{request.ExpectedFileSize}},"sha256":"{{request.ExpectedSha256}}","copyDesignOperationId":"{{request.CopyDesignOperationId}}"}"""));

        var result = await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(CopyDesignMaterializationOutcome.Failed, result.Outcome);
    }

    // ---- 19: no CheckInClient involvement ---------------------------------

    [Fact]
    public async Task Never_calls_the_checkin_endpoint()
    {
        // CopyDesignMaterializeHttpClient has no CheckInClient field/dependency
        // at all (see this file's class doc comment) - the only request it
        // ever sends is to the dedicated materialize endpoint, never
        // "/checkin".
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, HappyJson(request)));

        await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.DoesNotContain("checkin", handler.Requests[0].RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copy-design/materialize", handler.Requests[0].RequestUri!.ToString());
    }

    // ---- transport hardening (mirrors CheckInClientTests) -----------------

    [Fact]
    public async Task A_missing_local_file_is_rejected_before_any_request()
    {
        var request = RequestFor(Path.Combine(_dir, "nope.ipt"), Encoding.UTF8.GetBytes("x"));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));

        var result = await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(CopyDesignMaterializationOutcome.Failed, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_file_that_changed_size_since_verification_is_rejected_before_any_request()
    {
        var bytes = Encoding.UTF8.GetBytes("original verified content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("a totally different, longer replacement body"));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));

        var result = await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(CopyDesignMaterializationOutcome.Failed, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_409_conflict_is_a_controlled_Failed_outcome_not_an_unhandled_exception()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Conflict, """{"error":"already materialized"}"""));

        var result = await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(CopyDesignMaterializationOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_redirect_is_refused()
    {
        var bytes = Encoding.UTF8.GetBytes("content");
        var path = WriteFile(bytes);
        var request = RequestFor(path, bytes);
        var handler = new FakeHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://evil.example.com/x");
            return r;
        });

        var result = await Client(handler).MaterializeFirstFileVersionAsync(request, CancellationToken.None);

        Assert.Equal(CopyDesignMaterializationOutcome.Failed, result.Outcome);
    }
}
