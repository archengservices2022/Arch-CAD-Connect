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
/// The authoritative latest FileVersion the Arch PLM server reported for one
/// stable <see cref="CadDocumentId"/>. Identity is <c>cadDocumentId</c> +
/// <c>fileVersionId</c> only - nothing else in a response is ever treated as
/// identity.
///
/// <see cref="FileSize"/> and <see cref="Sha256"/> are the server-authoritative
/// canonical binary integrity metadata for that exact FileVersion (P5C-B). They
/// are the ONLY trusted authority for whether a local repair-target binary is
/// exactly this FileVersion - the mutable local <c>.arch\workspace.json</c> is
/// NOT. <see cref="FileSize"/> is <c>-1</c> and <see cref="Sha256"/> is empty
/// when the server did not supply usable integrity metadata; a P5C repair then
/// fails closed. <see cref="HasCanonicalIntegrity"/> is the single predicate.
/// </summary>
public sealed record AuthoritativeLatestVersion(
    string CadDocumentId,
    string LatestFileVersionId,
    long FileSize = -1,
    string Sha256 = "")
{
    /// <summary>True only when both the server byte size (&gt;= 0, safe range)
    ///  and the server checksum (exactly 64 lowercase hex, no prefix, no
    ///  whitespace) are present and canonical. A malformed value is never
    ///  trimmed or lower-cased into validity.</summary>
    public bool HasCanonicalIntegrity =>
        FileVersionIntegrity.IsRepresentableFileSize(FileSize)
        && FileVersionIntegrity.IsCanonicalSha256(Sha256);
}

/// <summary>
/// Shared, pure validation of Arch FileVersion binary integrity metadata - the
/// SAME semantics the web repository enforces (`isCanonicalSha256` /
/// `toSafeFileSize`) and the download endpoint verifies.
/// </summary>
public static class FileVersionIntegrity
{
    /// <summary>A canonical Arch content checksum: exactly 64 characters, all
    ///  lowercase hexadecimal, no <c>sha256:</c> prefix, no whitespace. Null,
    ///  blank, padded, wrong-length, prefixed, non-hex, or upper-cased =&gt;
    ///  false. Never normalised.</summary>
    public static bool IsCanonicalSha256(string? value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }
        foreach (var c in value)
        {
            var isLowerHex = c is (>= '0' and <= '9') or (>= 'a' and <= 'f');
            if (!isLowerHex)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A usable byte size: non-negative and within the range the server
    ///  guarantees (a JavaScript-safe integer), so it round-trips exactly.</summary>
    public static bool IsRepresentableFileSize(long value) =>
        value >= 0 && value <= MaxSafeInteger;

    /// <summary>2^53 - 1: the largest integer the server's JSON number can carry
    ///  without loss. A byte size above it is rejected, never trusted.</summary>
    public const long MaxSafeInteger = 9007199254740991L;
}

/// <summary>One authoritative lookup result for one cadDocumentId.</summary>
public sealed record LatestVersionResult(
    string CadDocumentId,
    LatestVersionOutcome Outcome,
    AuthoritativeLatestVersion? Version = null)
{
    /// <summary>
    /// An authoritative Found result. <paramref name="fileSize"/> /
    /// <paramref name="sha256"/> are the server-authoritative canonical integrity
    /// metadata; the defaults (<c>-1</c> / empty) are deliberately non-canonical
    /// so a P5C repair built on a result that omitted them fails closed rather
    /// than trusting the mutable local manifest.
    /// </summary>
    public static LatestVersionResult Found(
        string cadDocumentId, string latestFileVersionId, long fileSize = -1, string sha256 = "") =>
        new(cadDocumentId, LatestVersionOutcome.Found,
            new AuthoritativeLatestVersion(cadDocumentId, latestFileVersionId, fileSize, sha256));

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
