using Arch.CadConnect.Core.Connection;
using Arch.CadConnect.Core.Session;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api;

/// <summary>
/// COM-free orchestration of "the connection to Arch PLM": sign in, sign out,
/// restore a persisted session on start-up, and re-check the server on demand.
/// Owns the <see cref="ConnectionStateMachine"/> and the <see cref="ISessionStore"/>.
///
/// The Inventor add-in's controller drives this and reacts to
/// <see cref="StateChanged"/> / <see cref="SessionChanged"/> to enable/disable
/// ribbon commands. Everything here is unit-testable with a fake
/// <see cref="IArchApi"/> and an <see cref="InMemorySessionStore"/>.
/// </summary>
public sealed class ArchConnectionManager
{
    private readonly Func<ArchServerUri, IArchApi> _apiFactory;
    private readonly ISessionStore _store;
    private readonly ConnectionStateMachine _machine = new();
    private readonly Func<DateTimeOffset> _clock;

    public ArchConnectionManager(
        Func<ArchServerUri, IArchApi> apiFactory,
        ISessionStore store,
        Func<DateTimeOffset>? clock = null)
    {
        _apiFactory = apiFactory ?? throw new ArgumentNullException(nameof(apiFactory));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _machine.Changed += change => StateChanged?.Invoke(change);
    }

    public ConnectionState State => _machine.State;

    public IArchSession? CurrentSession { get; private set; }

    public event Action<ConnectionStateChange>? StateChanged;

    public event Action<IArchSession?>? SessionChanged;

    /// <summary>
    /// Sign in with credentials against <paramref name="serverAddress"/>. On
    /// success the session is persisted (encrypted) and the state becomes
    /// <see cref="ConnectionState.Connected"/>. On failure the state reflects
    /// why (Unauthorized vs ServerUnavailable) and the exception is rethrown
    /// for the UI to show its <see cref="ArchApiException.Message"/>.
    /// </summary>
    public async Task SignInAsync(
        string serverAddress,
        string email,
        string password,
        string? clientLabel = null,
        CancellationToken ct = default)
    {
        // Transport policy is enforced here, BEFORE any credential leaves the
        // process. An insecure address throws ArchServerUriException while the
        // state is still SignedOut - nothing is sent, nothing is stored.
        var server = ArchServerUri.Parse(serverAddress);
        var api = _apiFactory(server);

        _machine.BeginSignIn();
        try
        {
            var session = await api.SignInAsync(email, password, clientLabel, ct).ConfigureAwait(false);
            AdoptSession(session);
            _machine.SignInSucceeded();
        }
        catch (ArchApiException ex)
        {
            // Timeout at any stage -> ServerUnavailable; auth failure ->
            // Unauthorized. Either way we leave Connecting; no session is
            // persisted (AdoptSession never ran).
            ApplyFailure(ex);
            throw;
        }
        catch
        {
            // Any other failure (incl. a raw cancellation) must still get us
            // out of Connecting. Re-thrown, never swallowed.
            _machine.ServerUnreachable();
            throw;
        }
    }

    /// <summary>
    /// Revoke the session server-side (best effort), then ALWAYS clear the
    /// local session - even if the revoke request times out, fails, or is
    /// cancelled. The local clear is in a `finally` so no failure path can
    /// skip it.
    /// </summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var session = CurrentSession;
        try
        {
            if (session is not null)
            {
                await _apiFactory(session.Server).SignOutAsync(session, ct).ConfigureAwait(false);
            }
        }
        catch (ArchApiException)
        {
            // best effort - the `finally` still clears local state
        }
        catch (OperationCanceledException)
        {
            // ditto - a cancelled revoke must not leave a stale local session
        }
        finally
        {
            _store.Clear();
            SetSession(null);
            _machine.SignOut();
        }
    }

    /// <summary>
    /// Called on add-in start-up. Returns true if a session was restored (even
    /// if the server is momentarily unreachable), false if there is nothing
    /// usable and the user must sign in.
    /// </summary>
    public async Task<bool> TryRestoreAsync(CancellationToken ct = default)
    {
        var persisted = _store.TryLoad();
        if (persisted is null)
        {
            return false;
        }

        DesktopSession session;
        try
        {
            // Re-applies the transport policy: a persisted session that names a
            // remote http:// origin is rejected HERE, before its bearer token
            // is ever attached to a request.
            session = DesktopSession.FromPersisted(persisted);
        }
        catch (ArchServerUriException)
        {
            _store.Clear();
            return false;
        }

        if (session.IsExpired(_clock()))
        {
            _store.Clear();
            return false;
        }

        SetSession(session);
        _machine.BeginSignIn();

        var api = _apiFactory(session.Server);
        try
        {
            await api.GetSessionAsync(session, ct).ConfigureAwait(false);
            _machine.SessionConfirmed();
            return true;
        }
        catch (ArchApiException ex) when (ex.IsAuthFailure)
        {
            _store.Clear();
            SetSession(null);
            _machine.Rejected();
            return false;
        }
        catch (ArchApiException)
        {
            // Server down right now - keep the (locally valid) session so the
            // user does not have to re-enter credentials once it is back.
            _machine.ServerUnreachable();
            return true;
        }
    }

    /// <summary>
    /// Run a real Get Latest for the current session against the P3B
    /// workspace-plan + content endpoints, bearer-authenticated. Requires a
    /// session; if the server rejects the bearer mid-operation the connection
    /// transitions to <see cref="ConnectionState.Unauthorized"/> and the
    /// exception is rethrown for the UI. Per-entry outcomes (blocked / failed)
    /// are in the returned <see cref="MaterializationReport"/>, never thrown.
    /// </summary>
    public async Task<MaterializationReport> GetLatestAsync(
        GetLatestRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = CurrentSession
            ?? throw new ArchApiException(ArchApiFailureKind.Unauthorized, "Sign in before running Get Latest.");

        try
        {
            return await _apiFactory(session.Server).GetLatestAsync(session, request, ct).ConfigureAwait(false);
        }
        catch (ArchApiException ex)
        {
            if (ex.IsAuthFailure)
            {
                ApplyFailure(ex);
            }
            throw;
        }
    }

    /// <summary>The "Server Status" ribbon command: re-probe the server now.</summary>
    public async Task RefreshServerStatusAsync(CancellationToken ct = default)
    {
        var session = CurrentSession;
        if (session is null)
        {
            return;
        }

        var api = _apiFactory(session.Server);
        try
        {
            await api.GetSessionAsync(session, ct).ConfigureAwait(false);
            _machine.SessionConfirmed();
        }
        catch (ArchApiException ex)
        {
            ApplyFailure(ex);
        }
    }

    private void AdoptSession(IArchSession session)
    {
        if (session is DesktopSession desktop)
        {
            _store.Save(desktop.ToPersisted());
        }
        SetSession(session);
    }

    private void SetSession(IArchSession? session)
    {
        CurrentSession = session;
        SessionChanged?.Invoke(session);
    }

    private void ApplyFailure(ArchApiException ex)
    {
        if (ex.IsAuthFailure)
        {
            _store.Clear();
            SetSession(null);
            _machine.Rejected();
            return;
        }

        // Everything else (network, timeout, 5xx, a rejected request) leaves
        // the user in "server unavailable" - retryable without re-auth.
        _machine.ServerUnreachable();
    }
}
