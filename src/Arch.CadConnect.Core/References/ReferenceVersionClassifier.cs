namespace Arch.CadConnect.Core.References;

/// <summary>
/// P5B-B authoritative version intelligence. PURE: no COM, no HTTP, no I/O, no
/// mutation. Given a P5B-A <see cref="ReferenceHealthEntry"/> and an
/// authoritative <see cref="ILatestVersionOracle"/>, decides whether the
/// reference is <see cref="PlmVersionStatus.Current"/>,
/// <see cref="PlmVersionStatus.Stale"/>, or
/// <see cref="PlmVersionStatus.UnknownVersion"/>.
///
/// AUTHORITATIVE IDENTITY RULE: CURRENT / STALE come ONLY from
///   exact stable cadDocumentId
///   + pinned local fileVersionId (from the verified workspace manifest)
///   + the authenticated authoritative server's latest fileVersionId for that
///     same cadDocumentId.
/// Never from a name, path, timestamp, local file mtime, version number alone,
/// or the local manifest alone.
///
/// FAIL CLOSED: any doubt -> <see cref="PlmVersionStatus.UnknownVersion"/>.
/// CURRENT is never guessed.
/// </summary>
public static class ReferenceVersionClassifier
{
    public static ReferenceVersionAssessment Assess(ReferenceHealthEntry entry, ILatestVersionOracle oracle)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(oracle);

        var reasons = new List<string>();

        // Only an exactly-managed edge carrying a stable workspace-manifest
        // identity can be version-checked. Unresolved / unmanaged /
        // management-unknown edges are NOT APPLICABLE and stay UNKNOWN VERSION.
        if (entry.Management != ReferenceManagement.Managed || entry.ManagedIdentity is not { } identity)
        {
            reasons.Add(entry.IsMissing
                ? "Unresolved reference - there is no managed identity to version-check."
                : "Not a locally managed reference with an exact stable identity - authoritative version status is not applicable.");
            return NotApplicable(entry, reasons);
        }

        // cadDocumentId / fileVersionId are OPAQUE stable identifiers - used
        // verbatim, never trimmed. A blank, whitespace-only, or
        // whitespace-padded LOCAL identifier is malformed and fails closed to
        // UNKNOWN VERSION (identity is never inferred from anything else).
        var cadDocumentId = identity.CadDocumentId ?? "";
        var pinnedFileVersionId = identity.FileVersionId ?? "";

        if (string.IsNullOrWhiteSpace(cadDocumentId) || HasSurroundingWhitespace(cadDocumentId))
        {
            reasons.Add("The local cadDocumentId is missing or malformed - an authoritative version cannot be established.");
            return Unknown(entry, cadDocumentId: null, latest: null, reasons);
        }

        reasons.Add($"cadDocumentId: {cadDocumentId}");

        if (string.IsNullOrWhiteSpace(pinnedFileVersionId) || HasSurroundingWhitespace(pinnedFileVersionId))
        {
            reasons.Add("The pinned local fileVersionId is missing or malformed - "
                + "there is nothing safe to compare against the authoritative latest.");
            return Unknown(entry, cadDocumentId, latest: null, reasons);
        }

        reasons.Add($"pinned local fileVersionId: {pinnedFileVersionId}");

        var result = oracle.Get(cadDocumentId);
        switch (result.Outcome)
        {
            case LatestVersionOutcome.Found:
            {
                var latest = result.Version?.LatestFileVersionId ?? "";
                if (result.Version is null
                    || string.IsNullOrWhiteSpace(latest)
                    || HasSurroundingWhitespace(latest)
                    || !string.Equals(result.Version.CadDocumentId, cadDocumentId, StringComparison.Ordinal))
                {
                    reasons.Add("The authoritative response could not be safely matched to this CAD document identity.");
                    return Unknown(entry, cadDocumentId, latest: null, reasons);
                }

                reasons.Add($"authoritative latest fileVersionId: {latest}");

                if (string.Equals(latest, pinnedFileVersionId, StringComparison.Ordinal))
                {
                    reasons.Add("The pinned local version IS the authoritative latest version.");
                    return new ReferenceVersionAssessment(
                        entry, Applicable: true, PlmVersionStatus.Current, cadDocumentId, latest, reasons);
                }

                reasons.Add("The authoritative latest version DIFFERS from the pinned local version - this reference is out of date.");
                return new ReferenceVersionAssessment(
                    entry, Applicable: true, PlmVersionStatus.Stale, cadDocumentId, latest, reasons);
            }

            case LatestVersionOutcome.ServerUnavailable:
                reasons.Add("The Arch PLM server could not be reached for an authoritative version check.");
                return Unknown(entry, cadDocumentId, latest: null, reasons);

            case LatestVersionOutcome.AuthenticationFailed:
                reasons.Add("Authentication with Arch PLM was unavailable or rejected - an authoritative version check could not be performed.");
                return Unknown(entry, cadDocumentId, latest: null, reasons);

            case LatestVersionOutcome.LookupUnavailable:
                reasons.Add("This Arch PLM server does not provide authoritative version lookup yet.");
                return Unknown(entry, cadDocumentId, latest: null, reasons);

            case LatestVersionOutcome.DocumentNotRecognized:
                reasons.Add("The Arch PLM server did not recognize this CAD document.");
                return Unknown(entry, cadDocumentId, latest: null, reasons);

            case LatestVersionOutcome.MalformedResponse:
                reasons.Add("The authoritative version response was malformed or unsafe.");
                return Unknown(entry, cadDocumentId, latest: null, reasons);

            case LatestVersionOutcome.NotAttempted:
            default:
                reasons.Add("No authoritative version lookup was performed for this reference.");
                return Unknown(entry, cadDocumentId, latest: null, reasons);
        }
    }

    /// <summary>A stable identifier is opaque; surrounding whitespace is a
    ///  malformed identity, never something to normalise away.</summary>
    private static bool HasSurroundingWhitespace(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    private static ReferenceVersionAssessment NotApplicable(ReferenceHealthEntry entry, List<string> reasons) =>
        new(entry, Applicable: false, PlmVersionStatus.UnknownVersion, null, null, reasons);

    private static ReferenceVersionAssessment Unknown(
        ReferenceHealthEntry entry, string? cadDocumentId, string? latest, List<string> reasons) =>
        new(entry, Applicable: true, PlmVersionStatus.UnknownVersion, cadDocumentId, latest, reasons);
}
