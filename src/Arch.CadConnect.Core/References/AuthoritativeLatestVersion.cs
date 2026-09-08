namespace Arch.CadConnect.Core.References;

/// <summary>
/// Why an authoritative latest-version fact is (or is not) available for one
/// stable <c>cadDocumentId</c>. Every value except <see cref="Found"/>
/// classifies the reference as <see cref="PlmVersionStatus.UnknownVersion"/>
/// (fail closed).
/// </summary>
public enum LatestVersionOutcome
{
    /// <summary>No authoritative lookup was attempted (not signed in, or there
    ///  was no stable identity to look up).</summary>
    NotAttempted = 0,

    /// <summary>The authenticated server returned an authoritative latest
    ///  FileVersion identity for this cadDocumentId.</summary>
    Found,

    /// <summary>The Arch PLM server could not be reached, timed out, or
    ///  returned a 5xx / unexpected redirect.</summary>
    ServerUnavailable,

    /// <summary>Authentication was unavailable or rejected (no session, 401,
    ///  or 403).</summary>
    AuthenticationFailed,

    /// <summary>The server does not offer authoritative version lookup at all -
    ///  the endpoint is absent (404 / missing route) or unsupported on this
    ///  (older) server. A malformed or contract-mismatched response is
    ///  <see cref="MalformedResponse"/>, not this.</summary>
    LookupUnavailable,

    /// <summary>The authenticated server did not recognize this
    ///  cadDocumentId.</summary>
    DocumentNotRecognized,

    /// <summary>The response was malformed, or its identity could not be
    ///  safely established.</summary>
    MalformedResponse,
}

/// <summary>
/// The authoritative latest FileVersion identity the Arch PLM server reported
/// for one stable <see cref="CadDocumentId"/>. Identity is
/// <c>cadDocumentId</c> + <c>fileVersionId</c> only - nothing else in a
/// response is ever treated as identity.
/// </summary>
public sealed record AuthoritativeLatestVersion(string CadDocumentId, string LatestFileVersionId);

/// <summary>One authoritative lookup result for one cadDocumentId.</summary>
public sealed record LatestVersionResult(
    string CadDocumentId,
    LatestVersionOutcome Outcome,
    AuthoritativeLatestVersion? Version = null)
{
    public static LatestVersionResult Found(string cadDocumentId, string latestFileVersionId) =>
        new(cadDocumentId, LatestVersionOutcome.Found,
            new AuthoritativeLatestVersion(cadDocumentId, latestFileVersionId));

    public static LatestVersionResult Failure(string cadDocumentId, LatestVersionOutcome outcome) =>
        new(cadDocumentId, outcome == LatestVersionOutcome.Found ? LatestVersionOutcome.MalformedResponse : outcome, null);
}

/// <summary>
/// Read-only authoritative-version oracle the pure
/// <see cref="ReferenceVersionClassifier"/> consults. An implementation is
/// backed by an authenticated Arch PLM server call; tests supply a
/// deterministic fake (or use <see cref="LatestVersionLookup"/> directly).
/// </summary>
public interface ILatestVersionOracle
{
    /// <summary>The authoritative result for <paramref name="cadDocumentId"/>.
    ///  Never null - an id that was not resolved returns a fail-closed
    ///  outcome, never a currency guess.</summary>
    LatestVersionResult Get(string cadDocumentId);
}

/// <summary>
/// An immutable snapshot of authoritative latest-version facts for a set of
/// cadDocumentIds, plus a fallback outcome for any id that was not resolved
/// (so an unqueried or partially-returned id still fails closed).
/// </summary>
public sealed class LatestVersionLookup : ILatestVersionOracle
{
    private readonly IReadOnlyDictionary<string, LatestVersionResult> _byId;
    private readonly LatestVersionOutcome _fallback;

    private LatestVersionLookup(
        IReadOnlyDictionary<string, LatestVersionResult> byId,
        LatestVersionOutcome fallback,
        bool wholeFailure)
    {
        _byId = byId;
        _fallback = fallback == LatestVersionOutcome.Found ? LatestVersionOutcome.MalformedResponse : fallback;
        WholeFailureOutcome = wholeFailure ? _fallback : null;
    }

    /// <summary>
    /// When the ENTIRE authoritative call could not be completed, the single
    /// reason (server unavailable, auth rejected, lookup unsupported, not
    /// attempted, malformed). Null when per-id results were produced. The
    /// connection manager uses <see cref="LatestVersionOutcome.AuthenticationFailed"/>
    /// here to invalidate the desktop session, exactly like every other
    /// authenticated operation.
    /// </summary>
    public LatestVersionOutcome? WholeFailureOutcome { get; }

    /// <summary>Every requested id resolves to <paramref name="fallback"/> -
    ///  the whole authoritative call could not be completed (server
    ///  unavailable, auth rejected, lookup unsupported, not attempted).</summary>
    public static LatestVersionLookup WholeFailure(LatestVersionOutcome fallback) =>
        new(new Dictionary<string, LatestVersionResult>(StringComparer.Ordinal), fallback, wholeFailure: true);

    /// <summary>Build from per-id results. Any id not present in
    ///  <paramref name="results"/> resolves to <paramref name="fallback"/>.</summary>
    public static LatestVersionLookup FromResults(
        IEnumerable<LatestVersionResult>? results,
        LatestVersionOutcome fallback = LatestVersionOutcome.DocumentNotRecognized)
    {
        var map = new Dictionary<string, LatestVersionResult>(StringComparer.Ordinal);
        foreach (var r in results ?? [])
        {
            if (r is not null && !string.IsNullOrWhiteSpace(r.CadDocumentId))
            {
                map[r.CadDocumentId] = r;
            }
        }
        return new LatestVersionLookup(map, fallback, wholeFailure: false);
    }

    public LatestVersionResult Get(string cadDocumentId)
    {
        if (string.IsNullOrWhiteSpace(cadDocumentId))
        {
            return LatestVersionResult.Failure(cadDocumentId ?? "", LatestVersionOutcome.MalformedResponse);
        }
        return _byId.TryGetValue(cadDocumentId, out var r)
            ? r
            : LatestVersionResult.Failure(cadDocumentId, _fallback);
    }
}
