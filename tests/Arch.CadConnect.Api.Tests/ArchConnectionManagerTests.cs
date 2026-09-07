using Arch.CadConnect.Api;
using Arch.CadConnect.Api.Dtos;
using Arch.CadConnect.Core.Connection;
using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Tests;

public class ArchConnectionManagerTests
{
    private sealed class FakeApi : IArchApi
    {
        public FakeApi(ArchServerUri server) => Server = server;
        public ArchServerUri Server { get; }

        public Func<IArchSession>? OnSignIn { get; set; }
        public Func<Task>? OnGetSession { get; set; }
        public Action? OnSignOut { get; set; }
        public int SignOutCalls { get; private set; }

        public Task<IArchSession> SignInAsync(string email, string password, string? label, CancellationToken ct = default)
            => OnSignIn is null
                ? throw new ArchApiException(ArchApiFailureKind.Unauthorized, "bad creds")
                : Task.FromResult(OnSignIn());

        public Task SignOutAsync(IArchSession session, CancellationToken ct = default)
        {
            SignOutCalls++;
            // `OnSignOut` lets a test force a failure here, verifying the
            // manager still clears local state even if the revoke call throws.
            OnSignOut?.Invoke();
            return Task.CompletedTask;
        }

        public Task<SessionResponseDto> GetSessionAsync(IArchSession session, CancellationToken ct = default)
        {
            OnGetSession?.Invoke().GetAwaiter().GetResult();
            var id = new IdentityDto("u1", "T", "t@o.com", "ENGINEER", new OrganizationDto("org1", "ORGA", "Org A"));
            return Task.FromResult(new SessionResponseDto("arch-plm.desktop-auth.v1", id,
                new SessionTimingDto(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(12), DateTimeOffset.UtcNow),
                new ServerDto("Arch PLM", DateTimeOffset.UtcNow)));
        }

        public Task<ResolvedCadDocument> ResolveCadDocumentAsync(IArchSession s, CadDocumentLookup lu, CancellationToken ct = default) => throw ArchApiException.NotImplemented("Resolve");
        public Task<MaterializationReport> GetLatestAsync(IArchSession s, GetLatestRequest r, CancellationToken ct = default) => throw ArchApiException.NotImplemented("Get Latest");
        public Task<PlmDocumentStatusDto> GetDocumentStatusAsync(IArchSession s, string id, CancellationToken ct = default) => throw ArchApiException.NotImplemented("Status");
        public Task<Arch.CadConnect.Api.Workspace.CheckoutOperationResult> CheckoutAsync(IArchSession s, ManagedFileRef f, CancellationToken ct = default) => throw ArchApiException.NotImplemented("Checkout");
        public Task<Arch.CadConnect.Api.Workspace.CheckInOperationResult> CheckInAsync(IArchSession s, ManagedFileRef f, CancellationToken ct = default) => throw ArchApiException.NotImplemented("Check In");
        public Task<Arch.CadConnect.Api.Workspace.UndoOperationResult> UndoCheckoutAsync(IArchSession s, ManagedFileRef f, string? reason, CancellationToken ct = default) => throw ArchApiException.NotImplemented("Undo Checkout");
        public Task<WhereUsedDto> GetWhereUsedAsync(IArchSession s, string id, CancellationToken ct = default) => throw ArchApiException.NotImplemented("Where Used");
        public Task<ReleaseInfoDto> GetReleaseInfoAsync(IArchSession s, string r, CancellationToken ct = default) => throw ArchApiException.NotImplemented("Release information");
    }

    private static IArchSession Session(DateTimeOffset? expires = null) => new DesktopSession(
        ArchServerUri.Parse("https://plm.example.com"),
        new ArchIdentity("u1", "T", "t@o.com", "ENGINEER", "org1", "ORGA", "Org A"),
        "arch_dt_abc123def456",
        expires ?? DateTimeOffset.UtcNow.AddHours(12),
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task SignIn_success_persists_the_session_and_moves_to_Connected()
    {
        var store = new InMemorySessionStore();
        FakeApi api = null!;
        var mgr = new ArchConnectionManager(server => api = new FakeApi(server) { OnSignIn = () => Session() }, store);

        var states = new List<ConnectionState>();
        mgr.StateChanged += c => states.Add(c.Current);

        await mgr.SignInAsync("https://plm.example.com", "t@o.com", "pw");

        Assert.Equal(ConnectionState.Connected, mgr.State);
        Assert.NotNull(mgr.CurrentSession);
        Assert.NotNull(store.TryLoad());
        Assert.Equal(new[] { ConnectionState.Connecting, ConnectionState.Connected }, states);
    }

    [Fact]
    public async Task SignIn_bad_credentials_moves_to_Unauthorized_and_stores_nothing()
    {
        var store = new InMemorySessionStore();
        var mgr = new ArchConnectionManager(server => new FakeApi(server) { OnSignIn = null }, store);

        var ex = await Assert.ThrowsAsync<ArchApiException>(
            () => mgr.SignInAsync("https://plm.example.com", "t@o.com", "wrong"));

        Assert.Equal(ArchApiFailureKind.Unauthorized, ex.Kind);
        Assert.Equal(ConnectionState.Unauthorized, mgr.State);
        Assert.Null(store.TryLoad());
        Assert.Null(mgr.CurrentSession);
    }

    [Theory]
    [InlineData("http://plm.example.com")]
    [InlineData("http://192.168.1.50:3000")]
    public async Task SignIn_rejects_an_insecure_server_address_before_any_request(string address)
    {
        var store = new InMemorySessionStore();
        var mgr = new ArchConnectionManager(server => new FakeApi(server), store);
        await Assert.ThrowsAsync<ArchServerUriException>(() => mgr.SignInAsync(address, "t@o.com", "pw"));
        Assert.Equal(ConnectionState.SignedOut, mgr.State);
        Assert.Null(store.TryLoad());
    }

    // ---- timeout / state handling ------------------------------------

    [Fact]
    public async Task SignIn_timeout_moves_to_ServerUnavailable_and_persists_nothing()
    {
        var store = new InMemorySessionStore();
        var mgr = new ArchConnectionManager(
            server => new FakeApi(server)
            {
                OnSignIn = () => throw new ArchApiException(ArchApiFailureKind.Timeout, "no response in time"),
            },
            store);

        var ex = await Assert.ThrowsAsync<ArchApiException>(
            () => mgr.SignInAsync("https://plm.example.com", "t@o.com", "pw"));

        Assert.Equal(ArchApiFailureKind.Timeout, ex.Kind);
        Assert.Equal(ConnectionState.ServerUnavailable, mgr.State); // NOT stuck in Connecting
        Assert.Null(store.TryLoad());
        Assert.Null(mgr.CurrentSession);
    }

    [Fact]
    public async Task SignIn_unexpected_exception_still_leaves_Connecting_state_and_rethrows()
    {
        var mgr = new ArchConnectionManager(
            server => new FakeApi(server) { OnSignIn = () => throw new InvalidOperationException("boom") },
            new InMemorySessionStore());

        // Not swallowed - the original exception propagates...
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mgr.SignInAsync("https://plm.example.com", "t@o.com", "pw"));
        // ...but the machine still leaves Connecting (never stuck).
        Assert.NotEqual(ConnectionState.Connecting, mgr.State);
        Assert.Equal(ConnectionState.ServerUnavailable, mgr.State);
    }

    [Fact]
    public async Task Status_refresh_timeout_keeps_the_session_and_reports_ServerUnavailable()
    {
        var store = new InMemorySessionStore();
        var api = new FakeApi(ArchServerUri.Parse("https://plm.example.com")) { OnSignIn = () => Session() };
        var mgr = new ArchConnectionManager(_ => api, store);
        await mgr.SignInAsync("https://plm.example.com", "t@o.com", "pw");
        Assert.Equal(ConnectionState.Connected, mgr.State);

        api.OnGetSession = () => throw new ArchApiException(ArchApiFailureKind.Timeout, "no response in time");
        await mgr.RefreshServerStatusAsync();

        Assert.Equal(ConnectionState.ServerUnavailable, mgr.State); // not falsely Connected
        Assert.NotNull(store.TryLoad());
        Assert.NotNull(mgr.CurrentSession);
    }

    [Fact]
    public async Task Restore_timeout_keeps_the_local_session_for_retry()
    {
        var store = new InMemorySessionStore();
        store.Save(((DesktopSession)Session()).ToPersisted());
        var mgr = new ArchConnectionManager(
            s => new FakeApi(s) { OnGetSession = () => throw new ArchApiException(ArchApiFailureKind.Timeout, "slow") },
            store);

        Assert.True(await mgr.TryRestoreAsync());
        Assert.Equal(ConnectionState.ServerUnavailable, mgr.State);
        Assert.NotNull(store.TryLoad());
        Assert.NotNull(mgr.CurrentSession);
    }

    [Fact]
    public async Task SignOut_clears_local_state_even_if_the_revoke_call_times_out_or_throws()
    {
        var store = new InMemorySessionStore();
        var api = new FakeApi(ArchServerUri.Parse("https://plm.example.com")) { OnSignIn = () => Session() };
        var mgr = new ArchConnectionManager(_ => api, store);
        await mgr.SignInAsync("https://plm.example.com", "t@o.com", "pw");

        api.OnSignOut = () => throw new ArchApiException(ArchApiFailureKind.Timeout, "revoke timed out");
        await mgr.SignOutAsync(); // must not throw

        Assert.Equal(ConnectionState.SignedOut, mgr.State);
        Assert.Null(store.TryLoad());
        Assert.Null(mgr.CurrentSession);
    }

    [Fact]
    public async Task SignOut_clears_local_state_even_if_the_revoke_call_is_cancelled()
    {
        var store = new InMemorySessionStore();
        var api = new FakeApi(ArchServerUri.Parse("https://plm.example.com")) { OnSignIn = () => Session() };
        var mgr = new ArchConnectionManager(_ => api, store);
        await mgr.SignInAsync("https://plm.example.com", "t@o.com", "pw");

        api.OnSignOut = () => throw new OperationCanceledException();
        await mgr.SignOutAsync();

        Assert.Equal(ConnectionState.SignedOut, mgr.State);
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public async Task SignOut_revokes_server_side_and_clears_local_state()
    {
        var store = new InMemorySessionStore();
        FakeApi api = null!;
        var mgr = new ArchConnectionManager(server => api = new FakeApi(server) { OnSignIn = () => Session() }, store);
        await mgr.SignInAsync("https://plm.example.com", "t@o.com", "pw");

        await mgr.SignOutAsync();

        Assert.Equal(1, api.SignOutCalls);
        Assert.Equal(ConnectionState.SignedOut, mgr.State);
        Assert.Null(store.TryLoad());
        Assert.Null(mgr.CurrentSession);
    }

    [Fact]
    public async Task TryRestore_with_no_saved_session_returns_false()
    {
        var mgr = new ArchConnectionManager(s => new FakeApi(s), new InMemorySessionStore());
        Assert.False(await mgr.TryRestoreAsync());
        Assert.Equal(ConnectionState.SignedOut, mgr.State);
    }

    [Fact]
    public async Task TryRestore_discards_a_locally_expired_session()
    {
        var store = new InMemorySessionStore();
        store.Save(((DesktopSession)Session(DateTimeOffset.UtcNow.AddMinutes(-5))).ToPersisted());
        var mgr = new ArchConnectionManager(s => new FakeApi(s), store);

        Assert.False(await mgr.TryRestoreAsync());
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public async Task TryRestore_validates_a_live_session_and_connects()
    {
        var store = new InMemorySessionStore();
        store.Save(((DesktopSession)Session()).ToPersisted());
        var mgr = new ArchConnectionManager(s => new FakeApi(s), store);

        Assert.True(await mgr.TryRestoreAsync());
        Assert.Equal(ConnectionState.Connected, mgr.State);
    }

    [Fact]
    public async Task TryRestore_keeps_the_session_when_the_server_is_only_temporarily_down()
    {
        var store = new InMemorySessionStore();
        store.Save(((DesktopSession)Session()).ToPersisted());
        var mgr = new ArchConnectionManager(
            s => new FakeApi(s) { OnGetSession = () => throw new ArchApiException(ArchApiFailureKind.Network, "down") },
            store);

        Assert.True(await mgr.TryRestoreAsync());
        Assert.Equal(ConnectionState.ServerUnavailable, mgr.State);
        Assert.NotNull(store.TryLoad());
        Assert.NotNull(mgr.CurrentSession);
    }

    [Fact]
    public async Task TryRestore_discards_the_session_when_the_server_rejects_it()
    {
        var store = new InMemorySessionStore();
        store.Save(((DesktopSession)Session()).ToPersisted());
        var mgr = new ArchConnectionManager(
            s => new FakeApi(s) { OnGetSession = () => throw new ArchApiException(ArchApiFailureKind.Unauthorized, "revoked") },
            store);

        Assert.False(await mgr.TryRestoreAsync());
        Assert.Equal(ConnectionState.Unauthorized, mgr.State);
        Assert.Null(store.TryLoad());
    }
}
