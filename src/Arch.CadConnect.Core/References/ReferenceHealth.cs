namespace Arch.CadConnect.Core.References;

/// <summary>
/// The LOCAL health of a single observed reference edge. P5B-A only - it says
/// nothing about whether a managed file is the current PLM version (that needs
/// authoritative server knowledge and lands in P5B-B).
///
/// Ordered by severity: <see cref="Error"/> &gt; <see cref="Warning"/> &gt;
/// <see cref="Unknown"/> &gt; <see cref="Healthy"/>. The overall report health
/// is the highest severity of any edge (and never better than
/// <see cref="Unknown"/> when the underlying P5A scan was PARTIAL).
/// </summary>
public enum ReferenceHealth
{
    Healthy = 0,
    Unknown = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Whether a resolved reference target is a managed Arch document. MANAGED is
/// established ONLY by an exact canonical-path match against the genuine
/// workspace manifest (carried through from P5A as
/// <see cref="CadReference.ManifestIdentity"/>). It is never inferred from a
/// filename, display name, document number, or a filesystem search.
/// </summary>
public enum ReferenceManagement
{
    /// <summary>Diagnosis could not be safely established (containment unknown,
    ///  or the reference is unresolved).</summary>
    Unknown,

    /// <summary>Resolved path has an exact workspace-manifest entry.</summary>
    Managed,

    /// <summary>Resolved path has NO exact workspace-manifest entry.</summary>
    Unmanaged,
}

/// <summary>
/// The read-only local reference-health report for one root document. Built
/// purely from a <see cref="CadReferenceScan"/> - no COM, no HTTP, no mutation.
/// </summary>
public sealed record ReferenceHealthReport(
    CadReferenceRoot Root,
    IReadOnlyList<ReferenceHealthEntry> Entries,
    IReadOnlyList<CadReferenceNode> UnenumeratedDocuments,
    bool ScanComplete,
    ReferenceHealth OverallHealth,
    DateTimeOffset DiagnosedAtUtc)
{
    public ReferenceHealthSummary Summary => ReferenceHealthSummary.From(this);

    /// <summary>A short label for the overall state, for a report header.</summary>
    public string OverallStatusLabel => OverallHealth switch
    {
        ReferenceHealth.Error => "ERRORS FOUND",
        ReferenceHealth.Warning => "ATTENTION REQUIRED",
        ReferenceHealth.Unknown => "INCOMPLETE - REVIEW NEEDED",
        _ => "HEALTHY",
    };
}

/// <summary>
/// The diagnosis of one <see cref="CadReference"/> edge. Carries the original
/// edge verbatim so the underlying facts (paths, scope, identity) are never
/// buried behind the severity.
/// </summary>
public sealed record ReferenceHealthEntry(
    CadReference Reference,
    ReferenceHealth Health,
    ReferenceManagement Management,
    /// <summary>False when the resolved target is a document the P5A scan could
    ///  not enumerate - its own sub-graph is UNKNOWN. Does not by itself change
    ///  this edge's <see cref="Health"/> (the edge's own facts are known), but
    ///  it forces the overall report to PARTIAL / not-better-than-Unknown.</summary>
    bool ChildReferencesEnumerated,
    /// <summary>Human-readable facts behind the classification, in order. Never
    ///  a summary that hides a fact.</summary>
    IReadOnlyList<string> Reasons)
{
    public bool IsResolved => Reference.Resolution == CadReferenceResolution.Resolved;
    public bool IsMissing => Reference.Resolution == CadReferenceResolution.Unresolved;
    public ReferenceWorkspaceScope Scope => Reference.Scope;

    /// <summary>The exact manifest identity, ONLY for a managed edge. Never
    ///  synthesised for unmanaged / missing references.</summary>
    public CadManifestIdentity? ManagedIdentity =>
        Management == ReferenceManagement.Managed ? Reference.ManifestIdentity : null;
}

/// <summary>Deterministic counts over a <see cref="ReferenceHealthReport"/>.</summary>
public sealed record ReferenceHealthSummary(
    int Total,
    int Healthy,
    int Warnings,
    int Errors,
    int Unknown,
    int Managed,
    int Unmanaged,
    int ManagementUnknown,
    int InsideWorkspace,
    int OutsideWorkspace,
    int ScopeUnknown,
    int Missing,
    bool ScanComplete,
    int DocumentsNotEnumerated)
{
    public static ReferenceHealthSummary From(ReferenceHealthReport report)
    {
        var e = report.Entries;
        return new ReferenceHealthSummary(
            Total: e.Count,
            Healthy: e.Count(x => x.Health == ReferenceHealth.Healthy),
            Warnings: e.Count(x => x.Health == ReferenceHealth.Warning),
            Errors: e.Count(x => x.Health == ReferenceHealth.Error),
            Unknown: e.Count(x => x.Health == ReferenceHealth.Unknown),
            Managed: e.Count(x => x.Management == ReferenceManagement.Managed),
            Unmanaged: e.Count(x => x.Management == ReferenceManagement.Unmanaged),
            ManagementUnknown: e.Count(x => x.Management == ReferenceManagement.Unknown),
            InsideWorkspace: e.Count(x => x.Scope == ReferenceWorkspaceScope.InsideWorkspace),
            OutsideWorkspace: e.Count(x => x.Scope == ReferenceWorkspaceScope.OutsideWorkspace),
            ScopeUnknown: e.Count(x => x.Scope == ReferenceWorkspaceScope.Unknown),
            Missing: e.Count(x => x.IsMissing),
            ScanComplete: report.ScanComplete,
            DocumentsNotEnumerated: report.UnenumeratedDocuments.Count);
    }
}
