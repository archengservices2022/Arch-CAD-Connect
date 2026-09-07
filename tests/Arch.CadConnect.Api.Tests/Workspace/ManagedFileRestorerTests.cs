using System.Net;
using System.Security.Cryptography;
using System.Text;

using Arch.CadConnect.Api.Workspace;
using Arch.CadConnect.Core.Files;

namespace Arch.CadConnect.Api.Tests.Workspace;

public class ManagedFileRestorerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arch-cc-restore-" + Guid.NewGuid().ToString("N"));

    public ManagedFileRestorerTests() => Directory.CreateDirectory(_root);

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

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static (ManagedFileRestorer Restorer, FakeHttpHandler Handler) Make(
        byte[] served, string fileVersionId = "fv_1", bool withHeader = true, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler(req =>
        {
            Assert.Equal($"https://plm.example.com/api/file-versions/{fileVersionId}/content", req.RequestUri!.ToString());
            var r = new HttpResponseMessage(status) { Content = new ByteArrayContent(served) };
            if (withHeader) r.Headers.TryAddWithoutValidation("X-Content-SHA256", Sha(served));
            return r;
        });
        var downloader = new HttpContentDownloader(WorkspaceTestSession.Server, WorkspaceTestSession.Make(), new HttpClient(handler));
        return (new ManagedFileRestorer(downloader), handler);
    }

    [Fact]
    public async Task Stage_then_promote_replaces_the_working_file_and_marks_it_controlled()
    {
        var baseBytes = Encoding.UTF8.GetBytes("the authoritative base version bytes");
        var target = Path.Combine(_root, "part.ipt");
        await File.WriteAllBytesAsync(target, Encoding.UTF8.GetBytes("the user's local edits"));

        var (restorer, _) = Make(baseBytes);
        var staged = await restorer.StageBaseVersionAsync(_root, "fv_1", Sha(baseBytes), baseBytes.Length);
        Assert.True(File.Exists(staged.StagedPath));

        ManagedFileRestorer.Promote(staged, target);

        Assert.Equal(baseBytes, await File.ReadAllBytesAsync(target));
        Assert.True(ManagedFileGuard.IsControlled(target));
        Assert.False(File.Exists(staged.StagedPath));
    }

    [Fact]
    public async Task A_size_mismatch_throws_before_any_promotion_and_leaves_no_partial()
    {
        var expected = Encoding.UTF8.GetBytes("expected nineteen!!");
        var served = Encoding.UTF8.GetBytes("this is much longer than the plan says");
        var (restorer, _) = Make(served);

        await Assert.ThrowsAsync<RestoreVerificationException>(
            () => restorer.StageBaseVersionAsync(_root, "fv_1", Sha(expected), expected.Length));

        var staging = Path.Combine(_root, ".arch", ".staging");
        Assert.True(!Directory.Exists(staging) || Directory.GetFiles(staging).Length == 0);
    }

    [Fact]
    public async Task A_sha_mismatch_throws_before_any_promotion()
    {
        var served = Encoding.UTF8.GetBytes("sixteen bytes!!!"); // 16
        var wrongExpected = Encoding.UTF8.GetBytes("DIFFERENT16BYTES"); // 16, different sha
        var (restorer, _) = Make(served);
        await Assert.ThrowsAsync<RestoreVerificationException>(
            () => restorer.StageBaseVersionAsync(_root, "fv_1", Sha(wrongExpected), wrongExpected.Length));
    }

    [Fact]
    public async Task A_server_header_that_disagrees_with_the_bytes_is_rejected()
    {
        var served = Encoding.UTF8.GetBytes("some bytes");
        var handler = new FakeHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) };
            r.Headers.TryAddWithoutValidation("X-Content-SHA256", "deadbeef" + new string('0', 56));
            return r;
        });
        var restorer = new ManagedFileRestorer(new HttpContentDownloader(WorkspaceTestSession.Server, WorkspaceTestSession.Make(), new HttpClient(handler)));
        await Assert.ThrowsAsync<RestoreVerificationException>(
            () => restorer.StageBaseVersionAsync(_root, "fv_1", Sha(served), served.Length));
    }

    [Fact]
    public async Task Promote_throws_RestorePromoteException_when_the_working_file_is_held_open()
    {
        var baseBytes = Encoding.UTF8.GetBytes("base");
        var target = Path.Combine(_root, "part.ipt");
        await File.WriteAllBytesAsync(target, Encoding.UTF8.GetBytes("local"));

        var (restorer, _) = Make(baseBytes);
        var staged = await restorer.StageBaseVersionAsync(_root, "fv_1", Sha(baseBytes), baseBytes.Length);

        using var held = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<RestorePromoteException>(() => ManagedFileRestorer.Promote(staged, target));
        // staged bytes are kept for recovery
        Assert.True(File.Exists(staged.StagedPath));
    }

    [Fact]
    public async Task A_non_2xx_content_response_throws_and_never_releases()
    {
        var (restorer, _) = Make(Encoding.UTF8.GetBytes("x"), status: HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<ContentDownloadHttpException>(
            () => restorer.StageBaseVersionAsync(_root, "fv_1", Sha(Encoding.UTF8.GetBytes("x")), 1));
    }
}
