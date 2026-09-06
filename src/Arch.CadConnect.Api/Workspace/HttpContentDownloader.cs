using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.Workspace;

/// <summary>
/// The ONLY place the CAD Connect client issues an HTTP request for CAD
/// binary bytes. A faithful port of
/// <c>web/cli/http-content-downloader.ts</c>:
///
///   - binds the request to the session's EXACT origin before the bearer
///     token is attached;
///   - only ever fetches a path matching
///     <c>/api/file-versions/&lt;id&gt;/content</c>, resolved against the
///     server origin - an absolute URL / protocol-relative / other shape is
///     refused before any network call;
///   - refuses ANY redirect (same- or cross-origin) unconditionally;
///   - rejects any non-2xx response;
///   - streams the body (never buffers a whole CAD file);
///   - bounds the WHOLE transfer (connect + headers + the streamed body) with
///     a single timeout - a stall mid-transfer errors the returned stream
///     with <see cref="ContentDownloadTimeoutException"/> rather than hanging.
///     (The reference CLI uses the shared 30s request timeout; a CAD binary
///     transfer gets a larger default here - see <see cref="DownloadOptions"/>.)
/// </summary>
public sealed class HttpContentDownloader : IContentDownloader
{
    private static readonly Regex ContentPathShape =
        new(@"^/api/file-versions/[^/?#]+/content$", RegexOptions.CultureInvariant);

    private readonly ArchServerUri _server;
    private readonly IArchSession _session;
    private readonly HttpClient _http;
    private readonly DownloadOptions _options;

    public HttpContentDownloader(
        ArchServerUri server,
        IArchSession session,
        HttpClient http,
        DownloadOptions? options = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? new DownloadOptions();
    }

    /// <summary>Build a downloader with a hardened default handler (no
    ///  redirects, no cookies).</summary>
    public static HttpContentDownloader Create(
        ArchServerUri server,
        IArchSession session,
        DownloadOptions? options = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        return new HttpContentDownloader(server, session, new HttpClient(handler, disposeHandler: true), options);
    }

    public async Task<Stream> OpenContentStreamAsync(string contentPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(contentPath) || !ContentPathShape.IsMatch(contentPath))
        {
            throw new UnsafeContentPathException(
                "Refusing to download an unexpected contentPath (expected /api/file-versions/<id>/content).");
        }

        Uri url;
        try
        {
            url = _server.ResolvePath(contentPath);
        }
        catch (Exception)
        {
            throw new UnsafeContentPathException("contentPath did not resolve to a valid URL.");
        }
        if (!_server.MatchesOrigin(url))
        {
            throw new UnsafeContentPathException("contentPath resolved outside the Arch server origin.");
        }

        // One CTS spans connect + headers + the caller's later streamed read.
        // It is only disposed when the returned stream is disposed.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.TransferTimeout);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(_session.AuthorizationHeaderValue());
            request.Headers.Accept.ParseAdd("application/octet-stream");

            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            cts.Dispose();
            throw new ContentDownloadTimeoutException(_options.TransferTimeout);
        }
        catch
        {
            cts.Dispose();
            throw;
        }

        try
        {
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new ContentDownloadRedirectException();
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ContentDownloadHttpException((int)response.StatusCode);
            }

            var inner = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            return new TimeoutOwningStream(inner, response, cts, _options.TransferTimeout);
        }
        catch
        {
            response.Dispose();
            cts.Dispose();
            throw;
        }
    }

    /// <summary>Wraps the response body stream and OWNS the response +
    ///  timeout CTS, disposing them when the stream is disposed. Translates a
    ///  timeout during the streamed read into
    ///  <see cref="ContentDownloadTimeoutException"/>.</summary>
    private sealed class TimeoutOwningStream(
        Stream inner,
        HttpResponseMessage response,
        CancellationTokenSource cts,
        TimeSpan timeout) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
                return await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ContentDownloadTimeoutException(timeout);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                cts.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            cts.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class DownloadOptions
{
    /// <summary>Overall bound on connect + headers + the streamed body of one
    ///  file. Larger than the CLI's 30s request timeout because CAD binaries
    ///  can be large; still bounded.</summary>
    public TimeSpan TransferTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class UnsafeContentPathException(string message) : Exception(message);

public sealed class ContentDownloadRedirectException()
    : Exception("Content download was redirected; refusing to follow.");

public sealed class ContentDownloadHttpException(int status)
    : Exception($"Content download failed: HTTP {status}.")
{
    public int Status { get; } = status;
}

public sealed class ContentDownloadTimeoutException(TimeSpan timeout)
    : Exception($"Content download did not complete within {timeout.TotalSeconds:0}s.");
