using Arch.CadConnect.Core.Connection;
using Arch.CadConnect.Core.References;
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

    /// <summary>P4C: take an exclusive checkout of a managed file. Requires a
    ///  session; an auth failure transitions the connection to
    ///  <see cref="ConnectionState.Unauthorized"/> and the exception is
    ///  rethrown for the UI.</summary>
    public Task<Workspace.CheckoutOperationResult> CheckoutAsync(ManagedFileRef file, CancellationToken ct = default)
        => WithSessionAsync((api, s) => api.CheckoutAsync(s, file, ct));

    /// <summary>P4C: check in a managed file's saved local copy as a new
    ///  FileVersion and release the checkout.</summary>
    public Task<Workspace.CheckInOperationResult> CheckInAsync(ManagedFileRef file, CancellationToken ct = default)
        => WithSessionAsync((api, s) => api.CheckInAsync(s, file, ct));

    /// <summary>P4C: release a checkout and restore the base FileVersion
    ///  locally (staged + verified before the server release).</summary>
    public Task<Workspace.UndoOperationResult> UndoCheckoutAsync(ManagedFileRef file, string? reason, CancellationToken ct = default)
        => WithSessionAsync((api, s) => api.UndoCheckoutAsync(s, file, reason, ct));

    /// <summary>Read the authenticated server-authoritative checkout state for
    /// one stable CAD document id. This is read-only and never checks a file
    /// out.</summary>
    public Task<ServerCheckoutStatus> GetCheckoutStatusAsync(
        string cadDocumentId, CancellationToken ct = default)
        => WithSessionAsync((api, s) => api.GetCheckoutStatusAsync(s, cadDocumentId, ct));

    /// <summary>P6C: reserve server-side identities + lineage for a confirmed
    ///  Copy Design apply request via the existing P6B contract.</summary>
    public Task<Core.CopyDesign.Apply.CopyDesignReservationResponse> ApplyCopyDesignAsync(
        Core.CopyDesign.Apply.CopyDesignApplyRequest request, CancellationToken ct = default)
        => WithSessionAsync((api, s) => api.ApplyCopyDesignAsync(s, request, ct));

    /// <summary>P6C: attempt to materialize the first FileVersion for a
    ///  P6B-reserved CadDocument from a verified local file.</summary>
    public Task<Core.CopyDesign.Apply.CopyDesignMaterializationResult> MaterializeFirstFileVersionAsync(
        Core.CopyDesign.Apply.CopyDesignMaterializeRequest request, CancellationToken ct = default)
        => WithSessionAsync((api, s) => api.MaterializeFirstFileVersionAsync(s, request, ct));

    /// <summary>
    /// P6D ROUND 3, HIGH fix (items D/E): fetch the AUTHORITATIVE status of
    /// ONE Copy Design operation for the current session. READ-ONLY. Never
    /// throws for transport / auth / contract failures - they are outcomes in
    /// the returned <see cref="Core.CopyDesign.Apply.CopyDesignOperationStatusResult"/>
    /// so the orchestrator's RESUME classification fails closed, exactly
    /// like <see cref="GetLatestVersionsAsync"/>. With no session, resolves
    /// to <see cref="Core.CopyDesign.Apply.CopyDesignOperationStatusOutcome.AuthenticationFailed"/>.
    /// </summary>
    public async Task<Core.CopyDesign.Apply.CopyDesignOperationStatusResult> GetCopyDesignOperationStatusAsync(
        Core.CopyDesign.Apply.CopyDesignOperationStatusExpectation expectation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectation);

        var session = CurrentSession;
        if (session is null)
        {
            return Core.CopyDesign.Apply.CopyDesignOperationStatusResult.Failure(
                Core.CopyDesign.Apply.CopyDesignOperationStatusOutcome.AuthenticationFailed);
        }

        Core.CopyDesign.Apply.CopyDesignOperationStatusResult result;
        try
        {
            result = await _apiFactory(session.Server)
                .GetCopyDesignOperationStatusAsync(session, expectation, ct)
                .ConfigureAwait(false);
        }
        catch (ArchApiException ex)
        {
            // The shipped probe fails closed and never throws; a future / fake
            // IArchApi might. Honour the same session-failure semantics as
            // every other authenticated call, but still return a fail-closed
            // result rather than propagate.
            if (ex.IsAuthFailure)
            {
                InvalidateRejectedSession();
                return Core.CopyDesign.Apply.CopyDesignOperationStatusResult.Failure(
                    Core.CopyDesign.Apply.CopyDesignOperationStatusOutcome.AuthenticationFailed);
            }
            return Core.CopyDesign.Apply.CopyDesignOperationStatusResult.Failure(
                Core.CopyDesign.Apply.CopyDesignOperationStatusOutcome.ServerUnavailable);
        }

        // An AUTHORITATIVE 401 / 403 rejects this desktop session: clear the
        // stored token and move the connection to Unauthorized, exactly like
        // GetLatest / Checkout / Check-In / Undo / GetLatestVersionsAsync.
        if (result.Outcome == Core.CopyDesign.Apply.CopyDesignOperationStatusOutcome.AuthenticationFailed)
        {
            InvalidateRejectedSession();
        }

        return result;
    }

    /// <summary>
    /// P6D PRODUCTION RECOVERY: a RAW, structurally-validated authoritative
    /// status lookup for ONE Copy Design operation, by id alone - the
    /// DURABLE RESUME DISCOVERY entry point (see
    /// <see cref="Core.CopyDesign.Apply.IDurableCopyDesignOperationStatusClient"/>'s
    /// own doc comment). READ-ONLY. Never throws - a missing session or any
    /// transport/auth/contract failure resolves to a non-Found outcome.
    /// </summary>
    public async Task<Core.CopyDesign.Apply.CopyDesignDurableResumeStatusResult> GetDurableCopyDesignOperationStatusAsync(
        string operationId, CancellationToken ct = default)
    {
        var session = CurrentSession;
        if (session is null)
        {
            return new Core.CopyDesign.Apply.CopyDesignDurableResumeStatusResult(
                Core.CopyDesign.Apply.CopyDesignDurableResumeStatusOutcome.AuthenticationFailed);
        }

        Core.CopyDesign.Apply.CopyDesignDurableResumeStatusResult result;
        try
        {
            result = await _apiFactory(session.Server)
                .GetDurableCopyDesignOperationStatusAsync(session, operationId, ct)
                .ConfigureAwait(false);
        }
        catch (ArchApiException ex)
        {
            if (ex.IsAuthFailure)
            {
                InvalidateRejectedSession();
                return new Core.CopyDesign.Apply.CopyDesignDurableResumeStatusResult(
                    Core.CopyDesign.Apply.CopyDesignDurableResumeStatusOutcome.AuthenticationFailed);
            }
            return new Core.CopyDesign.Apply.CopyDesignDurableResumeStatusResult(
                Core.CopyDesign.Apply.CopyDesignDurableResumeStatusOutcome.ServerUnavailable);
        }

        if (result.Outcome == Core.CopyDesign.Apply.CopyDesignDurableResumeStatusOutcome.AuthenticationFailed)
        {
            InvalidateRejectedSession();
        }

        return result;
    }

    /// <summary>
    /// P6E-C: the RAW, structurally-validated verification-support payload
    /// for ONE Copy Design operation, by id alone, for the current session -
    /// see <see cref="Core.CopyDesign.Apply.ICopyDesignVerificationSupportClient"/>'s
    /// own doc comment. READ-ONLY. Never throws - a missing session or any
    /// transport/auth/contract failure resolves to a non-Found outcome.
    /// </summary>
    public async Task<Core.CopyDesign.Apply.CopyDesignVerificationSupportResult> GetCopyDesignVerificationSupportAsync(
        string operationId, CancellationToken ct = default)
    {
        var session = CurrentSession;
        if (session is null)
        {
            return new Core.CopyDesign.Apply.CopyDesignVerificationSupportResult(
                Core.CopyDesign.Apply.CopyDesignVerificationSupportOutcome.AuthenticationFailed);
        }

        Core.CopyDesign.Apply.CopyDesignVerificationSupportResult result;
        try
        {
            result = await _apiFactory(session.Server)
                .GetCopyDesignVerificationSupportAsync(session, operationId, ct)
                .ConfigureAwait(false);
        }
        catch (ArchApiException ex)
        {
            if (ex.IsAuthFailure)
            {
                InvalidateRejectedSession();
                return new Core.CopyDesign.Apply.CopyDesignVerificationSupportResult(
                    Core.CopyDesign.Apply.CopyDesignVerificationSupportOutcome.AuthenticationFailed);
            }
            return new Core.CopyDesign.Apply.CopyDesignVerificationSupportResult(
                Core.CopyDesign.Apply.CopyDesignVerificationSupportOutcome.ServerUnavailable);
        }

        if (result.Outcome == Core.CopyDesign.Apply.CopyDesignVerificationSupportOutcome.AuthenticationFailed)
        {
            InvalidateRejectedSession();
        }

        return result;
    }

    /// <summary>
    /// P5B-B: fetch the AUTHORITATIVE latest FileVersion identity for each of
    /// <paramref name="cadDocumentIds"/> for the current session. READ-ONLY.
    /// Never throws for transport / auth / unknown-id failures - they are
    /// outcomes in the returned <see cref="LatestVersionLookup"/> so version
    /// classification fails closed. With no session every id resolves to
    /// <see cref="LatestVersionOutcome.AuthenticationFailed"/>.
    /// </summary>
    public async Task<LatestVersionLookup> GetLatestVersionsAsync(
        IReadOnlyCollection<string> cadDocumentIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cadDocumentIds);

        var session = CurrentSession;
        if (session is null)
        {
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.AuthenticationFailed);
        }

        LatestVersionLookup lookup;
        try
        {
            lookup = await _apiFactory(session.Server)
                .GetLatestVersionsAsync(session, cadDocumentIds, ct)
                .ConfigureAwait(false);
        }
        catch (ArchApiException ex)
        {
            // The shipped probe fails closed and never throws; a future / fake
            // IArchApi might. Honour the same session-failure semantics as
            // every other authenticated call, but still return a fail-closed
            // result rather than propagate.
            if (ex.IsAuthFailure)
            {
                InvalidateRejectedSession();
                return LatestVersionLookup.WholeFailure(LatestVersionOutcome.AuthenticationFailed);
            }
            return LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable);
        }

        // An AUTHORITATIVE 401 / 403 rejects this desktop session: clear the
        // stored token and move the connection to Unauthorized, exactly like
        // GetLatest / Checkout / Check-In / Undo. The fail-closed version
        // result is still returned (this read never throws for auth).
        if (lookup.WholeFailureOutcome == LatestVersionOutcome.AuthenticationFailed)
        {
            InvalidateRejectedSession();
        }

        return lookup;
    }

    /// <summary>
    /// P6D DRAWING ASSOCIATION AUTHORITY: fetch, in ONE batched request, the
    /// AUTHORITATIVE drawing set for each of <paramref name="modelCadDocumentIds"/>
    /// for the current session. READ-ONLY. Never throws - a missing session,
    /// or any transport/auth/contract failure, resolves every requested id to
    /// <see cref="Core.CopyDesign.DrawingAssociationOutcome.NotAvailable"/>,
    /// the exact same "authority unavailable" state Copy Design Preview
    /// already handles for the P6A <c>NoDrawingAssociationSource</c> stub.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, Core.CopyDesign.DrawingAssociationResult>> GetDrawingAssociationsAsync(
        IReadOnlyCollection<string> modelCadDocumentIds,
        Core.Workspace.WorkspaceManifest? manifest,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(modelCadDocumentIds);

        var session = CurrentSession;
        if (session is null)
        {
            return AllNotAvailable(modelCadDocumentIds);
        }

        try
        {
            return await _apiFactory(session.Server)
                .GetDrawingAssociationsAsync(session, modelCadDocumentIds, manifest, ct)
                .ConfigureAwait(false);
        }
        catch (ArchApiException)
        {
            // The shipped client fails closed and never throws; a future /
            // fake IArchApi might. Never propagate - this lookup is
            // read-only/best-effort and must degrade exactly like an
            // unavailable authority, never crash Copy Design Preview.
            return AllNotAvailable(modelCadDocumentIds);
        }
    }

    private static Dictionary<string, Core.CopyDesign.DrawingAssociationResult> AllNotAvailable(
        IEnumerable<string> ids)
    {
        var result = new Dictionary<string, Core.CopyDesign.DrawingAssociationResult>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            result[id] = Core.CopyDesign.DrawingAssociationResult.NotAvailable;
        }
        return result;
    }

    private async Task<T> WithSessionAsync<T>(Func<IArchApi, IArchSession, Task<T>> op)
    {
        var session = CurrentSession
            ?? throw new ArchApiException(ArchApiFailureKind.Unauthorized, "Sign in first.");
        try
        {
            return await op(_apiFactory(session.Server), session).ConfigureAwait(false);
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
            InvalidateRejectedSession();
            return;
        }

        // Everything else (network, timeout, 5xx, a rejected request) leaves
        // the user in "server unavailable" - retryable without re-auth.
        _machine.ServerUnreachable();
    }

    /// <summary>
    /// The server rejected this session's bearer: discard the persisted token,
    /// forget the in-memory session, and move the connection to
    /// <see cref="ConnectionState.Unauthorized"/>. <see cref="ConnectionStateMachine.Rejected"/>
    /// is legal from Connecting / Connected / ServerUnavailable - all the states
    /// in which a live session can exist - so an auth rejection is always
    /// reflected consistently, even when a valid session was retained while the
    /// server was briefly unreachable. From SignedOut / Unauthorized there is no
    /// session to reject (the guard just keeps the no-throw contract intact).
    /// </summary>
    private void InvalidateRejectedSession()
    {
        _store.Clear();
        SetSession(null);
        if (_machine.State is ConnectionState.Connecting
            or ConnectionState.Connected
            or ConnectionState.ServerUnavailable)
        {
            _machine.Rejected();
        }
    }
}
