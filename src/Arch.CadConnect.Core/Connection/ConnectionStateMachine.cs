namespace Arch.CadConnect.Core.Connection;

/// <summary>
/// The legal transitions between <see cref="ConnectionState"/> values, plus a
/// <see cref="Changed"/> event the UI subscribes to. Pure and synchronous:
/// callers (the Inventor controller) are responsible for marshalling
/// <see cref="Changed"/> onto the UI thread.
///
/// Illegal transitions throw <see cref="InvalidOperationException"/> rather
/// than silently no-op, so a wiring bug surfaces in tests instead of leaving
/// the ribbon in a wrong state.
/// </summary>
public sealed class ConnectionStateMachine
{
    private ConnectionState _state;

    public ConnectionStateMachine(ConnectionState initial = ConnectionState.SignedOut)
    {
        _state = initial;
    }

    public ConnectionState State => _state;

    /// <summary>Raised AFTER the state changes. Not raised for a no-op.</summary>
    public event Action<ConnectionStateChange>? Changed;

    /// <summary>User started a sign-in (opened the dialog / submitted creds).</summary>
    public void BeginSignIn() => Transition(
        ConnectionState.Connecting,
        from: static s => s is ConnectionState.SignedOut
            or ConnectionState.Unauthorized
            or ConnectionState.ServerUnavailable,
        trigger: nameof(BeginSignIn));

    /// <summary>Server accepted the credentials and issued a live session.</summary>
    public void SignInSucceeded() => Transition(
        ConnectionState.Connected,
        from: static s => s is ConnectionState.Connecting,
        trigger: nameof(SignInSucceeded));

    /// <summary>A background session check confirmed the session is still live.</summary>
    public void SessionConfirmed() => Transition(
        ConnectionState.Connected,
        from: static s => s is ConnectionState.Connected
            or ConnectionState.Connecting
            or ConnectionState.ServerUnavailable,
        trigger: nameof(SessionConfirmed));

    /// <summary>
    /// Server was reached but rejected the credentials / session. Legal from
    /// <see cref="ConnectionState.Connecting"/> and
    /// <see cref="ConnectionState.Connected"/>, and ALSO from
    /// <see cref="ConnectionState.ServerUnavailable"/>: a locally valid session
    /// can legitimately be retained while the server was briefly unreachable,
    /// and a later authenticated request that finally reaches the server can
    /// come back 401 / 403 - that must land in
    /// <see cref="ConnectionState.Unauthorized"/>, not stay "server unavailable".
    /// </summary>
    public void Rejected() => Transition(
        ConnectionState.Unauthorized,
        from: static s => s is ConnectionState.Connecting
            or ConnectionState.Connected
            or ConnectionState.ServerUnavailable,
        trigger: nameof(Rejected));

    /// <summary>Server could not be reached (network / TLS / timeout / 5xx).</summary>
    public void ServerUnreachable() => Transition(
        ConnectionState.ServerUnavailable,
        from: static s => s is ConnectionState.Connecting or ConnectionState.Connected,
        trigger: nameof(ServerUnreachable));

    /// <summary>User signed out, or the local session was discarded.</summary>
    public void SignOut() => Transition(
        ConnectionState.SignedOut,
        from: static _ => true,
        trigger: nameof(SignOut));

    private void Transition(ConnectionState to, Func<ConnectionState, bool> from, string trigger)
    {
        if (!from(_state))
        {
            throw new InvalidOperationException(
                $"Connection transition '{trigger}' is not valid from state '{_state}'.");
        }

        if (_state == to)
        {
            return;
        }

        var previous = _state;
        _state = to;
        Changed?.Invoke(new ConnectionStateChange(previous, to, trigger));
    }
}

public sealed record ConnectionStateChange(
    ConnectionState Previous,
    ConnectionState Current,
    string Trigger);
