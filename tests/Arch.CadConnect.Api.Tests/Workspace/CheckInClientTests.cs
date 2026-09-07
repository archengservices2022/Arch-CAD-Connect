using System.Net;
using System.Security.Cryptography;
using System.Text;

using Arch.CadConnect.Api;
using Arch.CadConnect.Api.Workspace;

namespace Arch.CadConnect.Api.Tests.Workspace;

public class CheckInClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arch-cc-ci-" + Guid.NewGuid().ToString("N"));

    public CheckInClientTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteFile(byte[] bytes, string name = "part.ipt")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static CheckInClient Client(FakeHttpHandler handler) =>
        new(WorkspaceTestSession.Server, WorkspaceTestSession.Make(), new HttpClient(handler), TimeSpan.FromSeconds(5));

    [Fact]
    public async Task CheckIn_streams_the_exact_file_with_original_filename_and_verifies_the_result()
    {
        var bytes = Encoding.UTF8.GetBytes("the saved local file contents v2");
        var path = WriteFile(bytes);

        var handler = new FakeHttpHandler(req =>
        {
            Assert.Equal("part.ipt", req.Headers.GetValues("X-Original-Filename").Single());
            return FakeHttpHandler.Json(HttpStatusCode.Created,
                $$"""{"fileVersionId":"fv_2","cadDocumentId":"cad_1","versionNumber":2,"originalFileName":"part.ipt","storageKey":"k","fileSize":{{bytes.Length}},"checksum":"{{Sha(bytes)}}","createdById":"u1"}""");
        });

        var outcome = await Client(handler).CheckInAsync("cad_1", path, "part.ipt");

        Assert.Equal("fv_2", outcome.FileVersionId);
        Assert.Equal(2, outcome.VersionNumber);
        Assert.Equal(Sha(bytes), outcome.Checksum);
        Assert.Equal(bytes.Length, outcome.FileSize);
        Assert.Equal(Encoding.UTF8.GetString(bytes), handler.RequestBodies[0]);
        Assert.Equal("https://plm.example.com/api/cad-documents/cad_1/checkin", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task A_409_is_a_pre_201_failure_ArchApiException()
    {
        var path = WriteFile(Encoding.UTF8.GetBytes("x"));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Conflict, """{"error":"stale base"}"""));
        var ex = await Assert.ThrowsAsync<ArchApiException>(() => Client(handler).CheckInAsync("cad_1", path, null));
        Assert.Equal(ArchApiFailureKind.Conflict, ex.Kind);
    }

    [Fact]
    public async Task A_201_whose_checksum_does_not_match_the_upload_throws_CheckInVerificationException()
    {
        var bytes = Encoding.UTF8.GetBytes("uploaded content");
        var path = WriteFile(bytes);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created,
            $$"""{"fileVersionId":"fv_2","cadDocumentId":"cad_1","versionNumber":2,"originalFileName":"part.ipt","storageKey":"k","fileSize":{{bytes.Length}},"checksum":"0000000000000000000000000000000000000000000000000000000000000000","createdById":"u1"}"""));

        var ex = await Assert.ThrowsAsync<CheckInVerificationException>(() => Client(handler).CheckInAsync("cad_1", path, null));
        Assert.Equal("fv_2", ex.FileVersionId);
        Assert.Equal(2, ex.VersionNumber);
    }

    [Fact]
    public async Task A_201_with_an_unreadable_body_is_also_a_CheckInVerificationException()
    {
        var path = WriteFile(Encoding.UTF8.GetBytes("x"));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.Created, "not json"));
        var ex = await Assert.ThrowsAsync<CheckInVerificationException>(() => Client(handler).CheckInAsync("cad_1", path, null));
        Assert.Null(ex.FileVersionId);
    }

    [Fact]
    public async Task A_missing_local_file_is_rejected_before_any_request()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));
        await Assert.ThrowsAsync<ArchApiException>(() => Client(handler).CheckInAsync("cad_1", Path.Combine(_dir, "nope.ipt"), null));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_redirect_is_refused()
    {
        var path = WriteFile(Encoding.UTF8.GetBytes("x"));
        var handler = new FakeHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://evil.example.com/x");
            return r;
        });
        var ex = await Assert.ThrowsAsync<ArchApiException>(() => Client(handler).CheckInAsync("cad_1", path, null));
        Assert.Equal(ArchApiFailureKind.Server, ex.Kind);
    }
}
