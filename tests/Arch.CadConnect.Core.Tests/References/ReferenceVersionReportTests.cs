using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References;

/// <summary>
/// P5B-B health integration: the authoritative version dimension folded onto
/// the P5B-A report. STALE raises severity, UNKNOWN VERSION never improves it,
/// existing ERROR / WARNING / PARTIAL outcomes are preserved, and the pristine
/// P5B-A report is carried through untouched.
/// </summary>
public class ReferenceVersionReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Base = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string P(params string[] segments) => Path.GetFullPath(Path.Combine(Base, Path.Combine(segments)));

    private static CadReference Edge(
        string name,
        string? resolved,
        CadReferenceResolution resolution,
        ReferenceWorkspaceScope scope,
        CadManifestIdentity? identity = null) => new()
    {
        ParentAbsolutePath = P("Arch", "Job1", "ROOT.iam"),
        InventorReportedName = name,
        ResolvedAbsolutePath = resolved,
        ReferenceType = CadDocumentType.Ipt,
        RelationshipKind = CadRelationshipKind.Component,
        Resolution = resolution,
        Scope = scope,
        ManifestIdentity = identity,
    };

    private static CadReference ManagedEdge(string name, string cad, string fv)
        => Edge(name, P("Arch", "Job1", name), CadReferenceResolution.Resolved,
            ReferenceWorkspaceScope.InsideWorkspace, new CadManifestIdentity(cad, fv, "DOC-" + cad, name));

    private static CadReference UnmanagedEdge(string name)
        => Edge(name, P("Arch", "Job1", name), CadReferenceResolution.Resolved, ReferenceWorkspaceScope.InsideWorkspace);

    private static CadReference MissingEdge(string name)
        => Edge(name, null, CadReferenceResolution.Unresolved, ReferenceWorkspaceScope.Unknown);

    private static CadReferenceScan Scan(IEnumerable<CadReference> edges, bool complete = true)
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var nodes = new List<CadReferenceNode> { new(root, CadDocumentType.Iam, true) };
        if (!complete)
        {
            nodes.Add(new CadReferenceNode(P("Arch", "Job1", "SUB.iam"), CadDocumentType.Iam, false));
        }
        return new CadReferenceScan(new CadReferenceRoot(root, CadDocumentType.Iam, null), edges.ToArray(), nodes, Now);
    }

    private static ReferenceHealthReport Local(CadReferenceScan scan) => ReferenceHealthDiagnoser.Diagnose(scan);

    // ---- STALE raises health --------------------------------------

    [Fact]
    public void STALE_produces_at_least_WARNING_overall()
    {
        var scan = Scan(new[] { ManagedEdge("PART-A.ipt", "cad_a", "fv_v1") });
        var local = Local(scan);
        Assert.Equal(ReferenceHealth.Healthy, local.OverallHealth);

        var oracle = LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_v2") });
        var report = ReferenceVersionReport.Build(local, oracle);

        Assert.Equal(PlmVersionStatus.Stale, report.Assessments.Single().Status);
        Assert.Equal(ReferenceHealth.Warning, report.OverallHealth);
        Assert.Equal("ATTENTION REQUIRED", report.OverallStatusLabel);
    }

    [Fact]
    public void CURRENT_keeps_a_healthy_report_healthy()
    {
        var scan = Scan(new[] { ManagedEdge("PART-A.ipt", "cad_a", "fv_v2") });
        var oracle = LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_v2") });

        var report = ReferenceVersionReport.Build(Local(scan), oracle);

        Assert.Equal(PlmVersionStatus.Current, report.Assessments.Single().Status);
        Assert.Equal(ReferenceHealth.Healthy, report.OverallHealth);
        Assert.Equal("ALL MANAGED REFERENCES CURRENT", report.VersionStatusLabel);
    }

    // ---- UNKNOWN VERSION must not falsely improve health ------------

    [Fact]
    public void UNKNOWN_VERSION_on_a_managed_edge_forces_overall_to_at_least_UNKNOWN()
    {
        var scan = Scan(new[] { ManagedEdge("PART-A.ipt", "cad_a", "fv_v1") });
        var report = ReferenceVersionReport.Build(
            Local(scan), LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable));

        Assert.Equal(PlmVersionStatus.UnknownVersion, report.Assessments.Single().Status);
        Assert.Equal(ReferenceHealth.Unknown, report.OverallHealth);
        Assert.Equal("VERSION STATUS INCOMPLETE", report.VersionStatusLabel);
    }

    [Fact]
    public void UNKNOWN_VERSION_never_lowers_an_existing_ERROR()
    {
        var scan = Scan(new[]
        {
            ManagedEdge("PART-A.ipt", "cad_a", "fv_v1"),
            MissingEdge("MOTOR.ipt"),
        });
        var local = Local(scan);
        Assert.Equal(ReferenceHealth.Error, local.OverallHealth);

        var report = ReferenceVersionReport.Build(
            local, LatestVersionLookup.WholeFailure(LatestVersionOutcome.AuthenticationFailed));

        Assert.Equal(ReferenceHealth.Error, report.OverallHealth);
    }

    [Fact]
    public void CURRENT_never_improves_an_existing_WARNING()
    {
        var scan = Scan(new[]
        {
            ManagedEdge("PART-A.ipt", "cad_a", "fv_v2"),
            UnmanagedEdge("SCRATCH.ipt"),
        });
        var local = Local(scan);
        Assert.Equal(ReferenceHealth.Warning, local.OverallHealth);

        var oracle = LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_v2") });
        var report = ReferenceVersionReport.Build(local, oracle);

        Assert.Equal(ReferenceHealth.Warning, report.OverallHealth);
    }

    [Fact]
    public void A_partial_scan_can_never_be_better_than_UNKNOWN_even_when_all_versions_are_CURRENT()
    {
        var scan = Scan(new[] { ManagedEdge("SUB.iam", "cad_sub", "fv_sub") }, complete: false);
        var local = Local(scan);
        Assert.False(local.ScanComplete);
        Assert.Equal(ReferenceHealth.Unknown, local.OverallHealth);

        var oracle = LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_sub", "fv_sub") });
        var report = ReferenceVersionReport.Build(local, oracle);

        Assert.Equal(PlmVersionStatus.Current, report.Assessments.Single().Status);
        Assert.Equal(ReferenceHealth.Unknown, report.OverallHealth);
    }

    // ---- P5B-A preservation --------------------------------------

    [Fact]
    public void The_underlying_P5BA_report_is_carried_through_untouched()
    {
        var scan = Scan(new[]
        {
            ManagedEdge("PART-A.ipt", "cad_a", "fv_v1"),
            UnmanagedEdge("SCRATCH.ipt"),
            MissingEdge("MOTOR.ipt"),
        });
        var local = Local(scan);

        var report = ReferenceVersionReport.Build(
            local, LatestVersionLookup.FromResults(new[] { LatestVersionResult.Found("cad_a", "fv_v9") }));

        Assert.Same(local, report.Local);
        Assert.Equal(local.OverallHealth, report.Local.OverallHealth);
        Assert.Equal(local.Summary, report.Local.Summary);
        // per-edge P5B-A health is unchanged by the version dimension
        Assert.Equal(ReferenceHealth.Healthy, report.Local.Entries[0].Health);
        Assert.Equal(ReferenceManagement.Managed, report.Local.Entries[0].Management);
        Assert.Equal("cad_a", report.Local.Entries[0].ManagedIdentity!.CadDocumentId);
    }

    [Fact]
    public void Version_summary_counts_only_applicable_managed_references()
    {
        var scan = Scan(new[]
        {
            ManagedEdge("A.ipt", "cad_a", "fv_a"),   // CURRENT
            ManagedEdge("B.ipt", "cad_b", "fv_b1"),  // STALE
            ManagedEdge("C.ipt", "cad_c", "fv_c"),   // UNKNOWN (not returned)
            UnmanagedEdge("D.ipt"),                   // not applicable
            MissingEdge("E.ipt"),                     // not applicable
        });
        var oracle = LatestVersionLookup.FromResults(new[]
        {
            LatestVersionResult.Found("cad_a", "fv_a"),
            LatestVersionResult.Found("cad_b", "fv_b2"),
        });

        var s = ReferenceVersionReport.Build(Local(scan), oracle).VersionSummary;

        Assert.Equal(3, s.ApplicableReferences);
        Assert.Equal(1, s.Current);
        Assert.Equal(1, s.Stale);
        Assert.Equal(1, s.UnknownVersion);
        Assert.Equal(2, s.NotApplicable);
    }

    [Fact]
    public void No_managed_references_yields_a_clear_not_checked_label_and_no_health_change()
    {
        var scan = Scan(new[] { UnmanagedEdge("SCRATCH.ipt") });
        var local = Local(scan);

        var report = ReferenceVersionReport.Build(
            local, LatestVersionLookup.WholeFailure(LatestVersionOutcome.NotAttempted));

        Assert.Equal("NO MANAGED REFERENCES TO CHECK", report.VersionStatusLabel);
        Assert.Equal(local.OverallHealth, report.OverallHealth);
    }
}
