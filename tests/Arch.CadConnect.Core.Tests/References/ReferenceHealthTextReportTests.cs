using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References;

public class ReferenceHealthTextReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Base = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string P(params string[] segments) => Path.GetFullPath(Path.Combine(Base, Path.Combine(segments)));

    private static CadReference Edge(string parent, string name, string? resolved,
        CadReferenceResolution resolution, ReferenceWorkspaceScope scope, CadManifestIdentity? identity = null) => new()
    {
        ParentAbsolutePath = parent,
        InventorReportedName = name,
        ResolvedAbsolutePath = resolved,
        ReferenceType = CadDocumentType.Ipt,
        RelationshipKind = CadRelationshipKind.Component,
        Resolution = resolution,
        Scope = scope,
        ManifestIdentity = identity,
    };

    private static ReferenceHealthReport Diagnose(string root, CadReference[] edges, CadReferenceNode[]? nodes = null)
    {
        var n = nodes ?? new[] { new CadReferenceNode(root, CadDocumentType.Iam, true) };
        var scan = new CadReferenceScan(new CadReferenceRoot(root, CadDocumentType.Iam, null), edges, n, Now);
        return ReferenceHealthDiagnoser.Diagnose(scan);
    }

    [Fact]
    public void The_report_leads_with_overall_status_then_shows_every_edge_and_a_summary()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var sub = P("Arch", "Job1", "SUB.iam");
        var report = Diagnose(root, new[]
        {
            Edge(root, "SUB.iam", sub, CadReferenceResolution.Resolved, ReferenceWorkspaceScope.InsideWorkspace,
                new CadManifestIdentity("cad_sub", "fv_sub", "ASM-SUB", "SUB.iam")),
            Edge(root, "PART-B.ipt", P("Vendor", "PART-B.ipt"), CadReferenceResolution.Resolved, ReferenceWorkspaceScope.OutsideWorkspace),
            Edge(root, "MOTOR.ipt", null, CadReferenceResolution.Unresolved, ReferenceWorkspaceScope.Unknown),
        }, nodes: new[]
        {
            new CadReferenceNode(root, CadDocumentType.Iam, true),
            new CadReferenceNode(sub, CadDocumentType.Iam, true),
        });

        var text = ReferenceHealthTextReport.Render(report);

        Assert.StartsWith("REFERENCE HEALTH - ROOT.iam", text);
        Assert.Contains("Overall Status: ERRORS FOUND", text);
        Assert.Contains("Scan Status: COMPLETE", text);

        Assert.Contains("-> SUB.iam", text);
        Assert.Contains("HEALTHY", text);
        Assert.Contains("Resolved | Inside Workspace | Managed", text);
        Assert.Contains("cadDocumentId: cad_sub", text);
        Assert.Contains("fileVersionId: fv_sub", text);
        Assert.Contains("relativePath: SUB.iam", text);

        Assert.Contains("-> PART-B.ipt", text);
        Assert.Contains("Resolved | Outside Workspace | Unmanaged", text);

        Assert.Contains("-> MOTOR.ipt", text);
        Assert.Contains("ERROR", text);
        Assert.Contains("Missing", text);

        Assert.Contains("References: 3", text);
        Assert.Contains("Healthy: 1", text);
        Assert.Contains("Warnings: 1", text);
        Assert.Contains("Errors: 1", text);
        Assert.Contains("Managed: 1", text);
        Assert.Contains("Unmanaged: 1", text);
        Assert.Contains("Missing: 1", text);
        Assert.Contains("Scan completeness: COMPLETE", text);
    }

    [Fact]
    public void A_partial_scan_says_so_prominently_and_lists_the_unenumerated_documents()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var sub = P("Arch", "Job1", "SUB.iam");
        var report = Diagnose(root, new[]
        {
            Edge(root, "SUB.iam", sub, CadReferenceResolution.Resolved, ReferenceWorkspaceScope.InsideWorkspace,
                new CadManifestIdentity("cad_sub", "fv_sub", "ASM-SUB", "SUB.iam")),
        }, nodes: new[]
        {
            new CadReferenceNode(root, CadDocumentType.Iam, true),
            new CadReferenceNode(sub, CadDocumentType.Iam, false),
        });

        var text = ReferenceHealthTextReport.Render(report);

        Assert.Contains("Overall Status: INCOMPLETE - REVIEW NEEDED", text);
        Assert.Contains("Scan Status: PARTIAL", text);
        Assert.Contains("cannot be considered complete", text);
        Assert.Contains("Documents NOT enumerated", text);
        Assert.Contains(sub, text);
        Assert.Contains("child references were NOT enumerated", text);
        Assert.Contains("Scan completeness: PARTIAL", text);
        Assert.Contains("Documents not enumerated: 1", text);
    }

    [Fact]
    public void A_clean_assembly_reads_as_healthy()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var report = Diagnose(root, new[]
        {
            Edge(root, "A.ipt", P("Arch", "Job1", "A.ipt"), CadReferenceResolution.Resolved,
                ReferenceWorkspaceScope.InsideWorkspace, new CadManifestIdentity("cad_a", "fv_a", "PRT-A", "A.ipt")),
        });

        var text = ReferenceHealthTextReport.Render(report);
        Assert.Contains("Overall Status: HEALTHY", text);
        Assert.Contains("Scan Status: COMPLETE", text);
        Assert.DoesNotContain("Documents NOT enumerated", text);
    }

    [Fact]
    public void Rendering_is_deterministic()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var report = Diagnose(root, new[]
        {
            Edge(root, "A.ipt", P("Arch", "Job1", "A.ipt"), CadReferenceResolution.Resolved, ReferenceWorkspaceScope.InsideWorkspace),
            Edge(root, "B.ipt", null, CadReferenceResolution.Unresolved, ReferenceWorkspaceScope.Unknown),
        });

        Assert.Equal(ReferenceHealthTextReport.Render(report), ReferenceHealthTextReport.Render(report));
    }

    [Fact]
    public void The_underlying_facts_are_never_hidden_behind_severity()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var outside = P("Users", "me", "Source", "PART-A.ipt");
        var report = Diagnose(root, new[]
        {
            Edge(root, "PART-A.ipt", outside, CadReferenceResolution.Resolved, ReferenceWorkspaceScope.OutsideWorkspace),
        });

        var text = ReferenceHealthTextReport.Render(report);
        // the actual resolved path is shown even though the edge is only a WARNING
        Assert.Contains(outside, text);
        Assert.Contains("WARNING", text);
    }
}
