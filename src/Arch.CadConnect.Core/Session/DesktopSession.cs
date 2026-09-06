using System.Text.Json.Serialization;

namespace Arch.CadConnect.Core.Session;

/// <summary>
/// A concrete <see cref="IArchSession"/> backed by a server-issued opaque
/// bearer token (<c>arch_dt_...</c>). Immutable. The token is held only in
/// memory here; persistence is the Api layer's job and must be encrypted at
/// rest (DPAPI).
///
/// The token string itself is deliberately NOT exposed as a public property -
/// only <see cref="IArchSession.AuthorizationHeaderValue"/> hands it out, so
/// there is exactly one, greppable, place a caller can obtain the secret.
/// </summary>
public sealed class DesktopSession : IArchSession
{
    /// <summary>Treat a session as expired this long before its real expiry.</summary>
    public static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(1);

    private readonly string _token;

    public DesktopSession(
        ArchServerUri server,
        ArchIdentity identity,
        string token,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset issuedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Session token must not be empty.", nameof(token));
        }

        Server = server ?? throw new ArgumentNullException(nameof(server));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _token = token;
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
        IssuedAtUtc = issuedAtUtc.ToUniversalTime();
    }

    public ArchServerUri Server { get; }

    public ArchIdentity Identity { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    public bool IsExpired(DateTimeOffset nowUtc) => nowUtc.ToUniversalTime() >= ExpiresAtUtc - ExpirySkew;

    public string AuthorizationHeaderValue() => "Bearer " + _token;

    /// <summary>
    /// The last 6 characters of the token, for a "signed in (…abc123)" hint.
    /// Never the whole token.
    /// </summary>
    public string TokenHint => _token.Length <= 6 ? "******" : "…" + _token[^6..];

    /// <summary>
    /// Project to a serializable record for encrypted-at-rest persistence.
    /// This is the ONLY place the raw token leaves the object, and the caller
    /// (Api layer) must immediately DPAPI-protect the result.
    /// </summary>
    public PersistedDesktopSession ToPersisted() => new(
        Server.OriginString,
        _token,
        ExpiresAtUtc,
        IssuedAtUtc,
        Identity);

    /// <summary>
    /// Rehydrate a persisted session. The stored origin is re-validated
    /// against the current transport policy - a session file that names a
    /// remote http:// origin throws here, BEFORE the bearer token is ever
    /// attached to a request.
    /// </summary>
    public static DesktopSession FromPersisted(PersistedDesktopSession p) => new(
        ArchServerUri.Parse(p.ServerOrigin),
        p.Identity,
        p.Token,
        p.ExpiresAtUtc,
        p.IssuedAtUtc);
}

/// <summary>
/// Serialization shape for an at-rest (DPAPI-encrypted) session file. Contains
/// the raw token, so the file MUST be encrypted for the current user only and
/// MUST never be logged.
/// </summary>
public sealed record PersistedDesktopSession(
    [property: JsonPropertyName("serverOrigin")] string ServerOrigin,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyName("issuedAtUtc")] DateTimeOffset IssuedAtUtc,
    [property: JsonPropertyName("identity")] ArchIdentity Identity);
