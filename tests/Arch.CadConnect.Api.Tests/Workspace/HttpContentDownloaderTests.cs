using System.Net;
using System.Text;

using Arch.CadConnect.Api.Workspace;

namespace Arch.CadConnect.Api.Tests.Workspace;

public class HttpContentDownloaderTests
{
    private static HttpContentDownloader Downloader(FakeHttpHandler handler, TimeSpan? timeout = null) =>
        new(WorkspaceTestSession.Server, WorkspaceTestSession.Make(), new HttpClient(handler),
            new DownloadOptions { TransferTimeout = timeout ?? TimeSpan.FromSeconds(30) });

    [Fact]
    public async Task Streams_the_body_and_attaches_the_bearer_token_to_the_exact_origin()
    {
        var bytes = Encoding.UTF8.GetBytes("cad-binary-payload");
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        var dl = Downloader(handler);

        await using var stream = await dl.OpenContentStreamAsync("/api/file-versions/fv_1/content");
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        Assert.Equal(bytes, ms.ToArray());
        Assert.Equal("Bearer arch_dt_TESTTOKEN123456", handler.LastAuthorizationHeader);
        Assert.Equal("https://plm.example.com/api/file-versions/fv_1/content", handler.Requests.Single().RequestUri!.ToString());
    }

    [Theory]
    [InlineData("https://plm.example.com/api/file-versions/fv_1/content")]
    [InlineData("/api/file-versions/fv_1/other")]
    [InlineData("/api/other/fv_1/content")]
    [InlineData("//evil.example.com/api/file-versions/fv_1/content")]
    [InlineData("")]
    public async Task Refuses_an_unexpected_contentPath_before_any_network_call(string contentPath)
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, "x");
        await Assert.ThrowsAsync<UnsafeContentPathException>(
            () => Downloader(handler).OpenContentStreamAsync(contentPath));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Refuses_ANY_redirect()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://plm.example.com/api/file-versions/fv_1/content?token=leak");
            return r;
        });
        await Assert.ThrowsAsync<ContentDownloadRedirectException>(
            () => Downloader(handler).OpenContentStreamAsync("/api/file-versions/fv_1/content"));
    }

    [Fact]
    public async Task Non_2xx_is_a_ContentDownloadHttpException()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.Unauthorized, """{"error":"nope"}""");
        var ex = await Assert.ThrowsAsync<ContentDownloadHttpException>(
            () => Downloader(handler).OpenContentStreamAsync("/api/file-versions/fv_1/content"));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task A_body_that_stalls_mid_transfer_times_out_the_stream()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new HangingStream()),
        });
        var dl = Downloader(handler, TimeSpan.FromMilliseconds(150));

        await using var stream = await dl.OpenContentStreamAsync("/api/file-versions/fv_1/content");
        var buffer = new byte[16];
        await Assert.ThrowsAsync<ContentDownloadTimeoutException>(
            async () => await stream.ReadAsync(buffer));
    }

    private sealed class HangingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
