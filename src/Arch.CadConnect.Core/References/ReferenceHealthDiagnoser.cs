namespace Arch.CadConnect.Core.References;

/// <summary>
/// Turns a P5A <see cref="CadReferenceScan"/> into local reference-health
/// intelligence. PURE: no COM, no HTTP, no I/O, no mutation. It only
/// re-classifies facts P5A already established (resolution, workspace
/// containment, exact-manifest identity) into a per-edge health and an
/// aggregate report.
///
/// SCOPE: P5B-A is LOCAL only. It never says a file is "current" or "stale" -
/// that needs authoritative PLM version knowledge (P5B-B). A managed edge is
/// "healthy locally", nothing more.
/// </summary>
public static class ReferenceHealthDiagnoser
{
    public static ReferenceHealthReport Diagnose(CadReferenceScan scan, DateTimeOffset? nowUtc = null)
    {
        var entries = scan.References
            .Select(reference => Evaluate(reference, childEnumerated: !scan.TargetIsUnenumerated(reference)))
            .ToArray();

        var worstEdge = entries.Length == 0
            ? ReferenceHealth.Healthy
            : entries.Max(x => x.Health);

        // The overall report is never better than Unknown while the underlying
        // scan is PARTIAL - some sub-graph was not inspected.
        var overall = scan.IsComplete
            ? worstEdge
            : Max(worstEdge, ReferenceHealth.Unknown);

        return new ReferenceHealthReport(
            Root: scan.Root,
            Entries: entries,
            UnenumeratedDocuments: scan.UnenumeratedNodes,
            ScanComplete: scan.IsComplete,
            OverallHealth: overall,
            DiagnosedAtUtc: nowUtc ?? scan.ScannedAtUtc);
    }

    /// <summary>
    /// Classify one edge. Deterministic total function - exactly one health per
    /// edge, computed from the edge's own facts:
    ///
    ///   unresolved / missing                  -> ERROR   (management UNKNOWN)
    ///   resolved, scope UNKNOWN                -> UNKNOWN (management UNKNOWN)
    ///   resolved, OUTSIDE the workspace        -> WARNING (management UNMANAGED)
    ///   resolved, INSIDE, exact manifest match -> HEALTHY (management MANAGED)
    ///   resolved, INSIDE, no manifest match    -> WARNING (management UNMANAGED)
    ///
    /// A target whose own children were not enumerated does NOT change this
    /// edge's health (its own facts are known); it is recorded on the entry and
    /// forces the report to PARTIAL.
    /// </summary>
    public static ReferenceHealthEntry Evaluate(CadReference reference, bool childEnumerated)
    {
        var reasons = new List<string>();

        if (reference.Resolution == CadReferenceResolution.Unresolved)
        {
            reasons.Add("Missing - Inventor could not resolve this reference to a file.");
            reasons.Add($"Inventor reported: {reference.InventorReportedName}");
            return Entry(reference, ReferenceHealth.Error, ReferenceManagement.Unknown, childEnumerated, reasons);
        }

        reasons.Add($"Resolved -> {reference.ResolvedAbsolutePath}");

        switch (reference.Scope)
        {
            case ReferenceWorkspaceScope.Unknown:
                reasons.Add("Workspace containment could not be determined safely - management is unknown.");
                AppendChildNote(reasons, childEnumerated);
                return Entry(reference, ReferenceHealth.Unknown, ReferenceManagement.Unknown, childEnumerated, reasons);

            case ReferenceWorkspaceScope.OutsideWorkspace:
                reasons.Add("Outside the managed workspace - cannot be a managed file here.");
                AppendChildNote(reasons, childEnumerated);
                return Entry(reference, ReferenceHealth.Warning, ReferenceManagement.Unmanaged, childEnumerated, reasons);

            case ReferenceWorkspaceScope.InsideWorkspace:
            default:
                reasons.Add("Inside the managed workspace.");
                if (reference.ManifestIdentity is { } identity)
                {
                    reasons.Add("Managed - exact workspace-manifest match.");
                    reasons.Add($"cadDocumentId: {identity.CadDocumentId}");
                    if (!string.IsNullOrEmpty(identity.FileVersionId))
                    {
                        reasons.Add($"fileVersionId: {identity.FileVersionId}");
                    }
                    reasons.Add($"relativePath: {identity.RelativePath}");
                    AppendChildNote(reasons, childEnumerated);
                    return Entry(reference, ReferenceHealth.Healthy, ReferenceManagement.Managed, childEnumerated, reasons);
                }

                reasons.Add("Unmanaged - the resolved path has no exact workspace-manifest entry.");
                AppendChildNote(reasons, childEnumerated);
                return Entry(reference, ReferenceHealth.Warning, ReferenceManagement.Unmanaged, childEnumerated, reasons);
        }
    }

    private static void AppendChildNote(List<string> reasons, bool childEnumerated)
    {
        if (!childEnumerated)
        {
            reasons.Add("This document's own child references were NOT enumerated - its sub-graph is unknown.");
        }
    }

    private static ReferenceHealthEntry Entry(
        CadReference reference,
        ReferenceHealth health,
        ReferenceManagement management,
        bool childEnumerated,
        List<string> reasons)
        => new(reference, health, management, childEnumerated, reasons);

    private static ReferenceHealth Max(ReferenceHealth a, ReferenceHealth b) => a > b ? a : b;
}
