namespace Arch.CadConnect.Core.References;

/// <summary>
/// One reference edge's authoritative version assessment (P5B-B). Carries the
/// underlying P5B-A <see cref="ReferenceHealthEntry"/> verbatim so no local
/// fact is hidden behind the version status.
/// </summary>
public sealed record ReferenceVersionAssessment(
    ReferenceHealthEntry Entry,
    /// <summary>True when this edge is an exactly-managed reference for which
    ///  an authoritative version check is meaningful (whether or not it
    ///  succeeded). False for unresolved / unmanaged / management-unknown
    ///  edges - their version status is simply not applicable.</summary>
    bool Applicable,
    PlmVersionStatus Status,
    string? CadDocumentId,
    string? AuthoritativeLatestFileVersionId,
    IReadOnlyList<string> Reasons)
{
    public string? PinnedLocalFileVersionId => Entry.ManagedIdentity?.FileVersionId is { Length: > 0 } fv ? fv : null;

    public string StatusLabel => Status switch
    {
        PlmVersionStatus.Current => "CURRENT",
        PlmVersionStatus.Stale => "STALE",
        _ => "UNKNOWN VERSION",
    };

    /// <summary>
    /// The severity this version status contributes to the overall report. It
    /// can only ever RAISE severity (it is Max()'d onto the P5B-A health) -
    /// it never improves health.
    ///   CURRENT                         -> Healthy (contributes nothing)
    ///   STALE                           -> Warning (at least WARNING)
    ///   UNKNOWN VERSION on a managed edge -> Unknown (fail closed)
    ///   not applicable                  -> Healthy (the base health already
    ///                                      reflects the unresolved / unmanaged
    ///                                      condition)
    /// </summary>
    public ReferenceHealth HealthContribution => Status switch
    {
        PlmVersionStatus.Stale => ReferenceHealth.Warning,
        PlmVersionStatus.UnknownVersion when Applicable => ReferenceHealth.Unknown,
        _ => ReferenceHealth.Healthy,
    };
}

/// <summary>
/// The read-only P5B-B report: the pristine P5B-A
/// <see cref="ReferenceHealthReport"/> plus an authoritative version
/// assessment per edge and a combined overall health.
///
/// PRESERVATION: <see cref="Local"/> is the unmodified P5B-A report - every
/// P5A / P5B-A fact (resolved/missing, inside/outside/unknown workspace,
/// managed/unmanaged/unknown, stable manifest identity, per-edge health, scan
/// completeness) is carried through untouched. Version intelligence is an
/// additional dimension.
///
/// HEALTH INTEGRATION: <see cref="OverallHealth"/> is never better than
/// <see cref="ReferenceHealthReport.OverallHealth"/>. STALE forces at least
/// WARNING. An applicable-but-UNKNOWN version forces at least UNKNOWN. A
/// PARTIAL underlying scan is already floored at UNKNOWN by P5B-A and that
/// floor is preserved.
/// </summary>
public sealed record ReferenceVersionReport(
    ReferenceHealthReport Local,
    IReadOnlyList<ReferenceVersionAssessment> Assessments,
    ReferenceHealth OverallHealth,
    DateTimeOffset AssessedAtUtc)
{
    public ReferenceVersionSummary VersionSummary => ReferenceVersionSummary.From(this);

    /// <summary>Combined (local + version) overall-status label.</summary>
    public string OverallStatusLabel => OverallHealth switch
    {
        ReferenceHealth.Error => "ERRORS FOUND",
        ReferenceHealth.Warning => "ATTENTION REQUIRED",
        ReferenceHealth.Unknown => "INCOMPLETE - REVIEW NEEDED",
        _ => "HEALTHY",
    };

    /// <summary>Short label for the authoritative-version dimension alone.</summary>
    public string VersionStatusLabel
    {
        get
        {
            var s = VersionSummary;
            if (s.ApplicableReferences == 0) return "NO MANAGED REFERENCES TO CHECK";
            if (s.Stale > 0) return "STALE REFERENCES FOUND";
            if (s.UnknownVersion > 0) return "VERSION STATUS INCOMPLETE";
            return "ALL MANAGED REFERENCES CURRENT";
        }
    }

    public static ReferenceVersionReport Build(
        ReferenceHealthReport local, ILatestVersionOracle oracle, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(oracle);

        var assessments = local.Entries
            .Select(e => ReferenceVersionClassifier.Assess(e, oracle))
            .ToArray();

        // Fold the version dimension onto the P5B-A overall health WITHOUT
        // mutating any P5B-A entry. Max() guarantees the composed result is
        // never better than the P5B-A overall (which already carries the
        // partial-scan UNKNOWN floor).
        var versionFloor = assessments.Aggregate(
            ReferenceHealth.Healthy, (acc, a) => Max(acc, a.HealthContribution));
        var overall = Max(local.OverallHealth, versionFloor);

        return new ReferenceVersionReport(local, assessments, overall, nowUtc ?? local.DiagnosedAtUtc);
    }

    private static ReferenceHealth Max(ReferenceHealth a, ReferenceHealth b) => a > b ? a : b;
}

/// <summary>Deterministic counts over a <see cref="ReferenceVersionReport"/>.
///  Only APPLICABLE (exactly-managed) references contribute to the
///  current / stale / unknown tallies.</summary>
public sealed record ReferenceVersionSummary(
    int ApplicableReferences,
    int Current,
    int Stale,
    int UnknownVersion,
    int NotApplicable)
{
    public static ReferenceVersionSummary From(ReferenceVersionReport report)
    {
        var a = report.Assessments;
        return new ReferenceVersionSummary(
            ApplicableReferences: a.Count(x => x.Applicable),
            Current: a.Count(x => x.Applicable && x.Status == PlmVersionStatus.Current),
            Stale: a.Count(x => x.Applicable && x.Status == PlmVersionStatus.Stale),
            UnknownVersion: a.Count(x => x.Applicable && x.Status == PlmVersionStatus.UnknownVersion),
            NotApplicable: a.Count(x => !x.Applicable));
    }
}
