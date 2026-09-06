namespace Arch.CadConnect.Core.Connection;

/// <summary>
/// Client-side view of the link to an Arch PLM server. This drives ribbon
/// button enablement ONLY - it is never a security boundary. Every request is
/// still authorized server-side against the bearer session.
/// </summary>
public enum ConnectionState
{
    /// <summary>No local session. Sign In is the only connection action.</summary>
    SignedOut = 0,

    /// <summary>A sign-in or session check is in flight.</summary>
    Connecting,

    /// <summary>A valid session is established with a reachable server.</summary>
    Connected,

    /// <summary>
    /// The server was reached but rejected the session (expired / revoked /
    /// bad credentials). The local session should be discarded; the user must
    /// sign in again.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// The server could not be reached (network error, TLS failure, timeout,
    /// 5xx). The local session is kept - the user can retry without re-entering
    /// credentials once the server is back.
    /// </summary>
    ServerUnavailable,
}

public static class ConnectionStates
{
    /// <summary>True when document-aware (PDM) commands may be offered at all.</summary>
    public static bool AllowsPdmCommands(this ConnectionState state) =>
        state == ConnectionState.Connected;

    /// <summary>True when the Sign In command should be enabled.</summary>
    public static bool AllowsSignIn(this ConnectionState state) => state switch
    {
        ConnectionState.SignedOut => true,
        ConnectionState.Unauthorized => true,
        ConnectionState.ServerUnavailable => true,
        _ => false,
    };

    /// <summary>True when the Sign Out command should be enabled.</summary>
    public static bool AllowsSignOut(this ConnectionState state) => state switch
    {
        ConnectionState.Connected => true,
        ConnectionState.ServerUnavailable => true,
        ConnectionState.Unauthorized => true,
        _ => false,
    };
}
