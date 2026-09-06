using System.Net;

using Arch.CadConnect.Api;
using Arch.CadConnect.Core.Session;

namespace Arch.CadConnect.Api.Tests;

public class ArchApiClientTests
{
    private static readonly ArchServerUri Server = ArchServerUri.Parse("https://plm.example.com");

    private const string LoginOk = """
        {"contract":"arch-plm.desktop-auth.v1","tokenType":"Bearer","token":"arch_dt_TESTTOKEN123456",
         "expiresAt":"2026-05-01T12:00:00Z",
         "identity":{"userId":"u1","name":"Test User","email":"t@orga.com","role":"ENGINEER",
                     "organization":{"id":"org1","code":"ORGA","name":"Org A"}}}
        """;

    private const string SessionOk = """
        {"contract":"arch-plm.desktop-auth.v1",
         "identity":{"userId":"u1","name":"Test User","email":"t@orga.com","role":"ENGINEER",
                     "organization":{"id":"org1","code":"ORGA","name":"Org A"}},
         "session":{"createdAt":"2026-05-01T00:00:00Z","expiresAt":"2026-05-01T12:00:00Z","lastUsedAt":"2026-05-01T00:00:00Z"},
         "server":{"product":"Arch PLM","time":"2026-05-01T01:00:00Z"}}
        """;

    private static ArchApiClient Client(FakeHttpHandler handler, TimeSpan? timeout = null) =>
        new(Server, new HttpClient(handler),
            new ArchApiClientOptions { Timeout = timeout ?? TimeSpan.FromSeconds(30) });

    [Fact]
    public async Task SignIn_returns_a_bound_session_and_sends_credentials_in_the_body()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, LoginOk);
        var client = Client(handler);

        var session = await client.SignInAsync("t@orga.com", "hunter2", "Inventor 2025 on WS-01");

        Assert.Equal("Bearer arch_dt_TESTTOKEN123456", session.AuthorizationHeaderValue());
        Assert.Equal("ORGA", session.Identity.OrganizationCode);
        Assert.Equal(Server.OriginString, session.Server.OriginString);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://plm.example.com/api/desktop/auth/login", req.RequestUri!.ToString());
        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"hunter2\"", body);
        Assert.Contains("Inventor 2025 on WS-01", body);
    }

    [Fact]
    public async Task SignIn_maps_401_to_a_safe_unauthorized_exception()
    {
        var client = Client(FakeHttpHandler.Always(HttpStatusCode.Unauthorized,
            """{"error":"Sign-in failed. Check the email and password and try again."}"""));

        var ex = await Assert.ThrowsAsync<ArchApiException>(() => client.SignInAsync("t@orga.com", "bad", null));
        Assert.Equal(ArchApiFailureKind.Unauthorized, ex.Kind);
        Assert.Equal(401, ex.HttpStatus);
        Assert.DoesNotContain("bad", ex.Message);
    }

    [Fact]
    public async Task Server_5xx_stays_generic_and_does_not_leak_the_body()
    {
        var client = Client(FakeHttpHandler.Always(HttpStatusCode.InternalServerError,
            """{"error":"NullReferenceException at ProductionModule.cs:812 stacktrace..."}"""));

        var ex = await Assert.ThrowsAsync<ArchApiException>(() => client.GetSessionAsync(FakeSession()));
        Assert.Equal(ArchApiFailureKind.Server, ex.Kind);
        Assert.DoesNotContain("stacktrace", ex.Message);
        Assert.DoesNotContain("ProductionModule", ex.Message);
    }

    [Fact]
    public async Task Network_failure_maps_to_a_retryable_network_error()
    {
        var client = Client(FakeHttpHandler.Throw(new HttpRequestException("name resolution failed")));
        var ex = await Assert.ThrowsAsync<ArchApiException>(() => client.SignInAsync("t@orga.com", "x", null));
        Assert.Equal(ArchApiFailureKind.Network, ex.Kind);
        Assert.True(ex.IsServerUnavailable);
        Assert.DoesNotContain("resolution", ex.Message);
    }

    [Fact]
    public async Task A_redirect_response_is_refused_never_followed()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Redirect);
            r.Headers.Location = new Uri("https://evil.example.com/steal");
            return r;
        });
        var ex = await Assert.ThrowsAsync<ArchApiException>(() => Client(handler).GetSessionAsync(FakeSession()));
        Assert.Equal(ArchApiFailureKind.Server, ex.Kind);
    }

    [Fact]
    public async Task GetSession_attaches_the_bearer_header()
    {
        var handler = FakeHttpHandler.Always(HttpStatusCode.OK, SessionOk);
        var client = Client(handler);

        await client.GetSessionAsync(FakeSession());

        var req = Assert.Single(handler.Requests);
        Assert.Equal("Bearer arch_dt_TESTTOKEN123456", handler.LastAuthorizationHeader);
        Assert.Equal("https://plm.example.com/api/desktop/session", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task SignOut_never_throws_even_when_the_server_errors()
    {
        var client = Client(FakeHttpHandler.Always(HttpStatusCode.InternalServerError, "{}"));
        await client.SignOutAsync(FakeSession()); // must not throw
    }

    // ---- response-body timeout (headers arrive, body stalls) ----------

    [Fact]
    public async Task SignIn_body_that_stalls_after_headers_is_a_Timeout_not_a_hang()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StallingContent(),
        });
        var client = Client(handler, timeout: TimeSpan.FromMilliseconds(150));

        var ex = await Assert.ThrowsAsync<ArchApiException>(() => client.SignInAsync("t@orga.com", "x", null));
        Assert.Equal(ArchApiFailureKind.Timeout, ex.Kind);
        Assert.True(ex.IsServerUnavailable);
    }

    [Fact]
    public async Task GetSession_body_that_stalls_after_headers_is_a_Timeout()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StallingContent(),
        });
        var client = Client(handler, timeout: TimeSpan.FromMilliseconds(150));

        var ex = await Assert.ThrowsAsync<ArchApiException>(() => client.GetSessionAsync(FakeSession()));
        Assert.Equal(ArchApiFailureKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task SignOut_swallows_a_body_timeout_and_still_returns()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StallingContent(),
        });
        var client = Client(handler, timeout: TimeSpan.FromMilliseconds(150));

        await client.SignOutAsync(FakeSession()); // must not throw, must not hang
    }

    [Fact]
    public async Task Connection_dropped_mid_body_is_a_Network_error()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new FaultingContent(),
        });
        var client = Client(handler);

        var ex = await Assert.ThrowsAsync<ArchApiException>(() => client.SignInAsync("t@orga.com", "x", null));
        Assert.Equal(ArchApiFailureKind.Network, ex.Kind);
        Assert.DoesNotContain("stream", ex.Message.ToLowerInvariant());
    }

    /// <summary>Headers arrive immediately; the body read blocks until cancelled.</summary>
    private sealed class StallingContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => await Task.Delay(Timeout.Infinite).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
            => await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new HangingStream());

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new HangingStream());

        protected override bool TryComputeLength(out long length) { length = 0; return false; }

        private sealed class HangingStream : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    /// <summary>Headers arrive; reading the body throws an IOException.</summary>
    private sealed class FaultingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => throw new IOException("connection reset");
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new FaultingStream());
        protected override bool TryComputeLength(out long length) { length = 0; return false; }

        private sealed class FaultingStream : Stream
        {
            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
                => throw new IOException("connection reset");
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

    [Fact]
    public async Task Malformed_login_body_is_a_server_contract_failure()
    {
        var client = Client(FakeHttpHandler.Always(HttpStatusCode.OK, """{"contract":"something-else"}"""));
        var ex = await Assert.ThrowsAsync<ArchApiException>(() => client.SignInAsync("t@orga.com", "x", null));
        Assert.Equal(ArchApiFailureKind.Server, ex.Kind);
    }

    [Fact]
    public async Task Pdm_WRITE_operations_are_declared_but_throw_NotImplemented_not_a_fake_success()
    {
        var client = Client(FakeHttpHandler.Always(HttpStatusCode.OK, "{}"));
        var s = FakeSession();

        // P4C write operations - still declared-not-implemented, never faked.
        await Assert.ThrowsAsync<ArchApiException>(() => client.CheckoutAsync(s, "doc1"));
        await Assert.ThrowsAsync<ArchApiException>(() => client.CheckInAsync(s, "doc1", @"C:\ws\a.ipt"));
        await Assert.ThrowsAsync<ArchApiException>(() => client.UndoCheckoutAsync(s, "doc1"));
        await Assert.ThrowsAsync<ArchApiException>(() => client.GetDocumentStatusAsync(s, "doc1"));
        await Assert.ThrowsAsync<ArchApiException>(() => client.GetWhereUsedAsync(s, "doc1"));

        var ex = await Assert.ThrowsAsync<ArchApiException>(() => client.CheckoutAsync(s, "doc1"));
        Assert.Equal(ArchApiFailureKind.NotImplemented, ex.Kind);
    }

    private static IArchSession FakeSession() => new DesktopSession(
        Server,
        new ArchIdentity("u1", "Test User", "t@orga.com", "ENGINEER", "org1", "ORGA", "Org A"),
        "arch_dt_TESTTOKEN123456",
        DateTimeOffset.UtcNow.AddHours(12),
        DateTimeOffset.UtcNow);
}
