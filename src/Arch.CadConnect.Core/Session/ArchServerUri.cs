namespace Arch.CadConnect.Core.Session;

/// <summary>
/// A validated Arch PLM server base address. Construction enforces the
/// transport policy so no other layer has to:
///
///   - scheme must be http or https;
///   - plain http is allowed ONLY to a loopback host - exactly
///     <c>localhost</c>, <c>127.0.0.1</c>, or <c>::1</c> (the URI parser
///     normalizes the IPv6 form to <c>[::1]</c>). There is NO override:
///     no constructor argument, no environment variable, no setting can
///     permit plain http to a private LAN IP or a remote host name, in
///     development or production;
///   - the address is reduced to its ORIGIN (scheme + host + port). A session
///     is later bound to this exact origin and its bearer token is never sent
///     anywhere else (mirrors the reference CLI's
///     <c>assertSessionOriginMatches</c>).
///
/// This mirrors the server's <c>assertTransportAllowed</c> exactly, and is
/// stricter than the reference CLI's <c>assertHttpOrigin</c> (which allowed
/// plain http to any host).
/// </summary>
public sealed class ArchServerUri
{
    private ArchServerUri(Uri origin)
    {
        Origin = origin;
        OriginString = origin.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>The normalized origin, e.g. <c>https://plm.example.com</c>.</summary>
    public Uri Origin { get; }

    public string OriginString { get; }

    /// <summary>True only when the host is exactly one of the three loopback
    ///  forms - NOT the whole 127.0.0.0/8 range, and never based on
    ///  <see cref="Uri.IsLoopback"/> (which is broader).</summary>
    public bool IsLoopback => IsLoopbackHost(Origin.Host);

    public static readonly IReadOnlySet<string> LoopbackHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1", "[::1]" };

    private static bool IsLoopbackHost(string host) => LoopbackHosts.Contains(host.Trim());

    /// <summary>
    /// Parse and validate. Throws <see cref="ArchServerUriException"/> - whose
    /// message never echoes credentials, host or query - on any violation.
    /// </summary>
    public static ArchServerUri Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArchServerUriException("A server address is required.");
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArchServerUriException("The server address is not a valid URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArchServerUriException("The server address must start with http:// or https://.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArchServerUriException("The server address must not contain embedded credentials.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(uri.Host))
        {
            throw new ArchServerUriException(
                "HTTPS is required. Plain http:// is only allowed for a loopback address (localhost, 127.0.0.1, ::1).");
        }

        return new ArchServerUri(uri);
    }

    public static bool TryParse(string? value, out ArchServerUri? result)
    {
        try
        {
            result = Parse(value);
            return true;
        }
        catch (ArchServerUriException)
        {
            result = null;
            return false;
        }
    }

    /// <summary>Combine this origin with a server-relative path.</summary>
    public Uri ResolvePath(string relativePath)
    {
        var trimmed = relativePath.StartsWith('/') ? relativePath[1..] : relativePath;
        return new Uri(new Uri(OriginString + "/"), trimmed);
    }

    /// <summary>
    /// True when <paramref name="targetOrigin"/> is the SAME origin as this one.
    /// The Api layer calls this before attaching the bearer token to any
    /// request, so a token can never leak to a different origin.
    /// </summary>
    public bool MatchesOrigin(Uri targetOrigin) =>
        string.Equals(
            targetOrigin.GetLeftPart(UriPartial.Authority),
            OriginString,
            StringComparison.OrdinalIgnoreCase);

    public override string ToString() => OriginString;
}

public sealed class ArchServerUriException(string message) : Exception(message);
