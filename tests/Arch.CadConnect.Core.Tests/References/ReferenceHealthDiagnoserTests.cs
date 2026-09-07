using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References;

public class ReferenceHealthDiagnoserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Base = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string P(params string[] segments) => Path.GetFullPath(Path.Combine(Base, Path.Combine(segments)));

    private static CadReference Edge(
        string parent,
        string name,
        string? resolved,
        CadReferenceResolution resolution,
        ReferenceWorkspaceScope scope,
        CadManifestIdentity? identity = null,
        CadRelationshipKind kind = CadRelationshipKind.Component) => new()
    {
        ParentAbsolutePath = parent,
        InventorReportedName = name,
        ResolvedAbsolutePath = resolved,
        ReferenceType = CadDocumentType.Ipt,
        RelationshipKind = kind,
        Resolution = resolution,
        Scope = scope,
        ManifestIdentity = identity,
    };

    private static CadReference Resolved(string parent, string resolvedPath, ReferenceWorkspaceScope scope,
        CadManifestIdentity? identity = null)
        => Edge(parent, Path.GetFileName(resolvedPath), resolvedPath, CadReferenceResolution.Resolved, scope, identity);

    private static CadReference Missing(string parent, string reportedName)
        => Edge(parent, reportedName, null, CadReferenceResolution.Unresolved, ReferenceWorkspaceScope.Unknown);

    private static CadReferenceScan Scan(
        string root,
        IEnumerable<CadReference> edges,
        IEnumerable<CadReferenceNode>? nodes = null,
        PlmIdentity? rootIdentity = null)
    {
        var edgeList = edges.ToArray();
        var nodeList = (nodes ?? new[] { new CadReferenceNode(root, CadDocumentType.Iam, true) }).ToArray();
        return new CadReferenceScan(
            new CadReferenceRoot(root, CadDocumentType.Iam, rootIdentity), edgeList, nodeList, Now);
    }

    private static CadManifestIdentity Identity(string cad = "cad_x", string fv = "fv_x", string rel = "PART.ipt")
        => new(cad, fv, "DOC-" + cad, rel);

    // ---- per-edge classification --------------------------------------

    [Fact]
    public void Resolved_managed_inside_workspace_is_healthy()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var scan = Scan(root, new[]
        {
            Resolved(root, P("Arch", "Job1", "PART-A.ipt"), ReferenceWorkspaceScope.InsideWorkspace,
                Identity("cad_a", "fv_a", "PART-A.ipt")),
        });

        var entry = Assert.Single(ReferenceHealthDiagnoser.Diagnose(scan).Entries);
        Assert.Equal(ReferenceHealth.Healthy, entry.Health);
        Assert.Equal(ReferenceManagement.Managed, entry.Management);
        Assert.NotNull(entry.ManagedIdentity);
        Assert.Equal("cad_a", entry.ManagedIdentity!.CadDocumentId);
        Assert.Equal("fv_a", entry.ManagedIdentity.FileVersionId);
        Assert.Equal("PART-A.ipt", entry.ManagedIdentity.RelativePath);
    }

    [Fact]
    public void Resolved_unmanaged_inside_workspace_is_a_warning()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var scan = Scan(root, new[]
        {
            Resolved(root, P("Arch", "Job1", "UNTRACKED.ipt"), ReferenceWorkspaceScope.InsideWorkspace),
        });

        var entry = Assert.Single(ReferenceHealthDiagnoser.Diagnose(scan).Entries);
        Assert.Equal(ReferenceHealth.Warning, entry.Health);
        Assert.Equal(ReferenceManagement.Unmanaged, entry.Management);
        Assert.Null(entry.ManagedIdentity);
    }

    [Fact]
    public void Resolved_outside_workspace_is_a_warning_and_unmanaged()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var scan = Scan(root, new[]
        {
            Resolved(root, P("Vendor", "src", "PART-B.ipt"), ReferenceWorkspaceScope.OutsideWorkspace),
        });

        var entry = Assert.Single(ReferenceHealthDiagnoser.Diagnose(scan).Entries);
        Assert.Equal(ReferenceHealth.Warning, entry.Health);
        Assert.Equal(ReferenceManagement.Unmanaged, entry.Management);
        Assert.Null(entry.ManagedIdentity);
    }

    [Fact]
    public void A_same_named_file_outside_the_workspace_never_inherits_managed_identity()
    {
        // P5A never sets ManifestIdentity for an outside-workspace path; even if
        // a (bogus) identity were attached, scope wins - the diagnoser keys off
        // OUTSIDE first and reports Unmanaged with NO identity exposed.
        var root = P("Arch", "Job1", "ROOT.iam");
        var scan = Scan(root, new[]
        {
            Resolved(root, P("Users", "me", "Source", "PART-A.ipt"), ReferenceWorkspaceScope.OutsideWorkspace,
                identity: Identity("cad_a", "fv_a", "PART-A.ipt")),
        });

        var entry = Assert.Single(ReferenceHealthDiagnoser.Diagnose(scan).Entries);
        Assert.Equal(ReferenceManagement.Unmanaged, entry.Management);
        Assert.Equal(ReferenceHealth.Warning, entry.Health);
        Assert.Null(entry.ManagedIdentity);
    }

    [Fact]
    public void A_missing_reference_is_an_error_with_unknown_management_and_no_identity()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var scan = Scan(root, new[] { Missing(root, "MOTOR.ipt") });

        var entry = Assert.Single(ReferenceHealthDiagnoser.Diagnose(scan).Entries);
        Assert.Equal(ReferenceHealth.Error, entry.Health);
        Assert.Equal(ReferenceManagement.Unknown, entry.Management);
        Assert.Null(entry.ManagedIdentity);
        Assert.True(entry.IsMissing);
        Assert.Contains(entry.Reasons, r => r.Contains("Missing"));
    }

    [Fact]
    public void A_resolved_reference_with_unknown_scope_is_unknown_health_and_management()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var scan = Scan(root, new[]
        {
            Resolved(root, P("somewhere", "PART.ipt"), ReferenceWorkspaceScope.Unknown),
        });

        var entry = Assert.Single(ReferenceHealthDiagnoser.Diagnose(scan).Entries);
        Assert.Equal(ReferenceHealth.Unknown, entry.Health);
        Assert.Equal(ReferenceManagement.Unknown, entry.Management);
    }

    [Fact]
    public void Managed_identity_is_exposed_only_for_managed_edges()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var report = ReferenceHealthDiagnoser.Diagnose(Scan(root, new[]
        {
            Resolved(root, P("Arch", "Job1", "M.ipt"), ReferenceWorkspaceScope.InsideWorkspace, Identity("cad_m", "fv_m", "M.ipt")),
            Resolved(root, P("Arch", "Job1", "U.ipt"), ReferenceWorkspaceScope.InsideWorkspace),
            Resolved(root, P("Vendor", "O.ipt"), ReferenceWorkspaceScope.OutsideWorkspace),
            Missing(root, "X.ipt"),
        }));

        Assert.Equal("cad_m", report.Entries[0].ManagedIdentity!.CadDocumentId);
        Assert.Null(report.Entries[1].ManagedIdentity);
        Assert.Null(report.Entries[2].ManagedIdentity);
        Assert.Null(report.Entries[3].ManagedIdentity);
    }

    // ---- severity precedence -----------------------------------------

    [Fact]
    public void Overall_health_takes_the_highest_edge_severity()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var healthy = Resolved(root, P("Arch", "Job1", "H.ipt"), ReferenceWorkspaceScope.InsideWorkspace, Identity());
        var unknown = Resolved(root, P("x", "U.ipt"), ReferenceWorkspaceScope.Unknown);
        var warning = Resolved(root, P("Vendor", "W.ipt"), ReferenceWorkspaceScope.OutsideWorkspace);
        var error = Missing(root, "E.ipt");

        Assert.Equal(ReferenceHealth.Healthy, ReferenceHealthDiagnoser.Diagnose(Scan(root, new[] { healthy })).OverallHealth);
        Assert.Equal(ReferenceHealth.Unknown, ReferenceHealthDiagnoser.Diagnose(Scan(root, new[] { healthy, unknown })).OverallHealth);
        Assert.Equal(ReferenceHealth.Warning, ReferenceHealthDiagnoser.Diagnose(Scan(root, new[] { healthy, unknown, warning })).OverallHealth);
        Assert.Equal(ReferenceHealth.Error, ReferenceHealthDiagnoser.Diagnose(Scan(root, new[] { healthy, unknown, warning, error })).OverallHealth);
    }

    [Fact]
    public void An_empty_complete_scan_is_healthy_overall()
    {
        var root = P("Arch", "Job1", "LONE.ipt");
        var report = ReferenceHealthDiagnoser.Diagnose(Scan(root, Array.Empty<CadReference>()));

        Assert.Empty(report.Entries);
        Assert.Equal(ReferenceHealth.Healthy, report.OverallHealth);
        Assert.Equal("HEALTHY", report.OverallStatusLabel);
    }

    // ---- partial-graph honesty --------------------------------------

    [Fact]
    public void A_partial_scan_is_never_better_than_unknown_overall()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var sub = P("Arch", "Job1", "SUB.iam");
        var scan = Scan(root,
            new[] { Resolved(root, sub, ReferenceWorkspaceScope.InsideWorkspace, Identity("cad_sub", "fv_sub", "SUB.iam")) },
            nodes: new[]
            {
                new CadReferenceNode(root, CadDocumentType.Iam, true),
                new CadReferenceNode(sub, CadDocumentType.Iam, false), // SUB not enumerated
            });

        var report = ReferenceHealthDiagnoser.Diagnose(scan);

        // the edge's own facts are still known -> HEALTHY edge...
        Assert.Equal(ReferenceHealth.Healthy, report.Entries[0].Health);
        Assert.False(report.Entries[0].ChildReferencesEnumerated);
        Assert.Contains(report.Entries[0].Reasons, r => r.Contains("child references were NOT enumerated"));
        // ...but the overall report cannot be better than Unknown
        Assert.False(report.ScanComplete);
        Assert.Equal(ReferenceHealth.Unknown, report.OverallHealth);
        Assert.Single(report.UnenumeratedDocuments);
    }

    [Fact]
    public void A_partial_scan_with_a_missing_reference_stays_error_overall()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var sub = P("Arch", "Job1", "SUB.iam");
        var scan = Scan(root,
            new[]
            {
                Resolved(root, sub, ReferenceWorkspaceScope.InsideWorkspace),
                Missing(root, "MOTOR.ipt"),
            },
            nodes: new[]
            {
                new CadReferenceNode(root, CadDocumentType.Iam, true),
                new CadReferenceNode(sub, CadDocumentType.Iam, false),
            });

        Assert.Equal(ReferenceHealth.Error, ReferenceHealthDiagnoser.Diagnose(scan).OverallHealth);
    }

    // ---- graph shapes ----------------------------------------------

    [Fact]
    public void Every_scan_edge_gets_exactly_one_diagnosis_entry_in_order()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var sub = P("Arch", "Job1", "SUB.iam");
        var edges = new[]
        {
            Resolved(root, sub, ReferenceWorkspaceScope.InsideWorkspace, Identity("cad_sub", "fv_sub", "SUB.iam")),
            Resolved(root, P("Arch", "Job1", "PART-B.ipt"), ReferenceWorkspaceScope.InsideWorkspace),
            Resolved(sub, P("Arch", "Job1", "PART-A.ipt"), ReferenceWorkspaceScope.OutsideWorkspace),
        };
        var scan = Scan(root, edges, nodes: new[]
        {
            new CadReferenceNode(root, CadDocumentType.Iam, true),
            new CadReferenceNode(sub, CadDocumentType.Iam, true),
        });

        var report = ReferenceHealthDiagnoser.Diagnose(scan);
        Assert.Equal(3, report.Entries.Count);
        Assert.Same(edges[0], report.Entries[0].Reference);
        Assert.Same(edges[1], report.Entries[1].Reference);
        Assert.Same(edges[2], report.Entries[2].Reference);
    }

    [Fact]
    public void A_cycle_scan_diagnoses_every_edge()
    {
        var a = P("Arch", "Job1", "A.iam");
        var b = P("Arch", "Job1", "B.iam");
        var scan = Scan(a, new[]
        {
            Resolved(a, b, ReferenceWorkspaceScope.InsideWorkspace, Identity("cad_b", "fv_b", "B.iam")),
            Resolved(b, a, ReferenceWorkspaceScope.InsideWorkspace, Identity("cad_a", "fv_a", "A.iam")),
        }, nodes: new[]
        {
            new CadReferenceNode(a, CadDocumentType.Iam, true),
            new CadReferenceNode(b, CadDocumentType.Iam, true),
        });

        var report = ReferenceHealthDiagnoser.Diagnose(scan);
        Assert.Equal(2, report.Entries.Count);
        Assert.All(report.Entries, e => Assert.Equal(ReferenceHealth.Healthy, e.Health));
    }

    [Fact]
    public void Diagnosis_is_deterministic()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var scan = Scan(root, new[]
        {
            Resolved(root, P("Arch", "Job1", "A.ipt"), ReferenceWorkspaceScope.InsideWorkspace, Identity("cad_a", "fv_a", "A.ipt")),
            Resolved(root, P("Vendor", "B.ipt"), ReferenceWorkspaceScope.OutsideWorkspace),
            Missing(root, "C.ipt"),
        });

        var first = ReferenceHealthDiagnoser.Diagnose(scan);
        var second = ReferenceHealthDiagnoser.Diagnose(scan);

        Assert.Equal(
            first.Entries.Select(e => $"{e.Reference.ResolvedAbsolutePath}|{e.Health}|{e.Management}|{string.Join(";", e.Reasons)}"),
            second.Entries.Select(e => $"{e.Reference.ResolvedAbsolutePath}|{e.Health}|{e.Management}|{string.Join(";", e.Reasons)}"));
        Assert.Equal(first.Summary, second.Summary);
    }

    [Fact]
    public void The_summary_counts_every_dimension()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var report = ReferenceHealthDiagnoser.Diagnose(Scan(root, new[]
        {
            Resolved(root, P("Arch", "Job1", "M.ipt"), ReferenceWorkspaceScope.InsideWorkspace, Identity()),   // healthy/managed/inside
            Resolved(root, P("Arch", "Job1", "U.ipt"), ReferenceWorkspaceScope.InsideWorkspace),                 // warning/unmanaged/inside
            Resolved(root, P("Vendor", "O.ipt"), ReferenceWorkspaceScope.OutsideWorkspace),                      // warning/unmanaged/outside
            Resolved(root, P("x", "S.ipt"), ReferenceWorkspaceScope.Unknown),                                    // unknown/unknown
            Missing(root, "X.ipt"),                                                                              // error/missing
        }));

        var s = report.Summary;
        Assert.Equal(5, s.Total);
        Assert.Equal(1, s.Healthy);
        Assert.Equal(2, s.Warnings);
        Assert.Equal(1, s.Errors);
        Assert.Equal(1, s.Unknown);
        Assert.Equal(1, s.Managed);
        Assert.Equal(2, s.Unmanaged);
        Assert.Equal(2, s.ManagementUnknown);   // the scope-unknown edge + the missing edge
        Assert.Equal(2, s.InsideWorkspace);
        Assert.Equal(1, s.OutsideWorkspace);
        Assert.Equal(2, s.ScopeUnknown);        // the scope-unknown edge + the missing edge (scope Unknown)
        Assert.Equal(1, s.Missing);
        Assert.True(s.ScanComplete);
        Assert.Equal(0, s.DocumentsNotEnumerated);
    }

    [Fact]
    public void Diagnosis_never_labels_a_managed_file_current_or_stale()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var report = ReferenceHealthDiagnoser.Diagnose(Scan(root, new[]
        {
            Resolved(root, P("Arch", "Job1", "M.ipt"), ReferenceWorkspaceScope.InsideWorkspace, Identity()),
        }));

        var text = string.Join("\n", report.Entries.SelectMany(e => e.Reasons));
        Assert.DoesNotContain("current", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stale", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("out of date", text, StringComparison.OrdinalIgnoreCase);
    }
}
