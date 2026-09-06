namespace Arch.CadConnect.Core.Session;

/// <summary>
/// The client's authenticated session with an Arch PLM server. This is the C#
/// counterpart of the reference CLI's <c>ArchSession</c> interface: "give me
/// what I need to authenticate a request to THIS origin", with the mechanism
/// (here: a bearer token) hidden behind it so the API client, and later the
/// Get Latest / Checkout code, never touch auth details directly.
///
/// SECURITY: implementations must never log, print or persist the raw token in
/// clear text. <see cref="AuthorizationHeaderValue"/> returns the secret and
/// callers must treat the return value as sensitive.
/// </summary>
public interface IArchSession
{
    /// <summary>The origin (scheme://host[:port]) this session is valid for.</summary>
    ArchServerUri Server { get; }

    /// <summary>The server-derived identity for this session.</summary>
    ArchIdentity Identity { get; }

    /// <summary>UTC instant after which the session is no longer valid.</summary>
    DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>True when <see cref="ExpiresAtUtc"/> is in the past (with a small skew).</summary>
    bool IsExpired(DateTimeOffset nowUtc);

    /// <summary>
    /// The value for an <c>Authorization</c> header on a request to
    /// <see cref="Server"/> (e.g. <c>"Bearer arch_dt_..."</c>). Sensitive -
    /// never log the return value.
    /// </summary>
    string AuthorizationHeaderValue();
}
