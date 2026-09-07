using System.Text.Json;

using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.References;

public class CadReferenceScannerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "arch-cc-scan-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside =
        Path.Combine(Path.GetTempPath(), "arch-cc-src-" + Guid.NewGuid().ToString("N"));

    public CadReferenceScannerTests()
    {
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _workspace, _outside })
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private string InWorkspace(string name) => Path.Combine(_workspace, name);
    private string Outside(string name) => Path.Combine(_outside, name);

    private CadReferenceScanner Scanner(IDocumentReferenceSource source, bool withWorkspace = false) =>
        new(source, withWorkspace ? new[] { _workspace } : Array.Empty<string?>(), () => Now);

    // ---- topology ---------------------------------------------------------

    [Fact]
    public void Direct_topology_is_preserved()
    {
        var root = InWorkspace("ROOT.iam");
        var source = new FakeDocumentReferenceSource()
            .Component(root, InWorkspace("PART-A.ipt"))
            .Add(root,
                FakeDocumentReferenceSource.Ref(InWorkspace("PART-A.ipt")),
                FakeDocumentReferenceSource.Ref(InWorkspace("PART-B.ipt")));

        var scan = Scanner(source).Scan(root);

        Assert.Equal(2, scan.References.Count);
        Assert.All(scan.References, r => Assert.Equal(Path.GetFullPath(root), r.ParentAbsolutePath));
        Assert.All(scan.References, r => Assert.Equal(CadRelationshipKind.Component, r.RelationshipKind));
        Assert.Equal(2, scan.Summary.DirectReferences);
        Assert.Equal(2, scan.Summary.TotalReferences);
    }

    [Fact]
    public void Recursive_topology_is_preserved_and_not_flattened()
    {
        var root = InWorkspace("ROOT.iam");
        var sub = InWorkspace("SUB.iam");
        var partA = InWorkspace("PART-A.ipt");
        var partB = InWorkspace("PART-B.ipt");

        var source = new FakeDocumentReferenceSource()
            .Add(root, FakeDocumentReferenceSource.Ref(sub), FakeDocumentReferenceSource.Ref(partB))
            .Add(sub, FakeDocumentReferenceSource.Ref(partA));

        var scan = Scanner(source).Scan(root);

        Assert.Equal(3, scan.References.Count);
        AssertEdge(scan, root, sub, CadRelationshipKind.Component);
        AssertEdge(scan, root, partB, CadRelationshipKind.Component);
        AssertEdge(scan, sub, partA, CadRelationshipKind.Component);

        // NOT flattened: ROOT never gains a direct edge to PART-A.
        Assert.DoesNotContain(scan.References, r =>
            Same(r.ParentAbsolutePath, root) && Same(r.ResolvedAbsolutePath, partA));

        Assert.Equal(2, scan.Summary.DirectReferences); // SUB + PART-B
        Assert.Equal(3, scan.Summary.TotalReferences);
    }

    // ---- cycle / dedup / repeated sub-assembly ---------------------------

    [Fact]
    public void A_cycle_terminates_and_records_both_edges()
    {
        var a = InWorkspace("A.iam");
        var b = InWorkspace("B.iam");
        var source = new FakeDocumentReferenceSource()
            .Add(a, FakeDocumentReferenceSource.Ref(b))
            .Add(b, FakeDocumentReferenceSource.Ref(a));

        var scan = Scanner(source).Scan(a);

        Assert.Equal(2, scan.References.Count);
        AssertEdge(scan, a, b, CadRelationshipKind.Component);
        AssertEdge(scan, b, a, CadRelationshipKind.Component);
        Assert.Equal(2, source.Calls); // each node expanded exactly once
    }

    [Fact]
    public void A_repeated_subassembly_is_expanded_once_but_every_edge_is_kept()
    {
        var root = InWorkspace("ROOT.iam");
        var sub = InWorkspace("SUB.iam");
        var sub2 = InWorkspace("SUB2.iam");
        var partA = InWorkspace("PART-A.ipt");

        var source = new FakeDocumentReferenceSource()
            .Add(root, FakeDocumentReferenceSource.Ref(sub), FakeDocumentReferenceSource.Ref(sub2))
            .Add(sub2, FakeDocumentReferenceSource.Ref(sub))
            .Add(sub, FakeDocumentReferenceSource.Ref(partA));

        var scan = Scanner(source).Scan(root);

        Assert.Equal(4, scan.References.Count);
        AssertEdge(scan, root, sub, CadRelationshipKind.Component);
        AssertEdge(scan, root, sub2, CadRelationshipKind.Component);
        AssertEdge(scan, sub2, sub, CadRelationshipKind.Component);
        AssertEdge(scan, sub, partA, CadRelationshipKind.Component);

        // SUB -> PART-A recorded exactly once even though SUB is reached twice.
        Assert.Single(scan.References, r => Same(r.ParentAbsolutePath, sub) && Same(r.ResolvedAbsolutePath, partA));
        Assert.Equal(4, source.Calls); // root, sub, sub2, partA - once each
    }

    [Fact]
    public void A_duplicate_direct_reference_is_deduplicated_without_a_second_edge()
    {
        var root = InWorkspace("ROOT.iam");
        var partA = InWorkspace("PART-A.ipt");
        var source = new FakeDocumentReferenceSource().Add(root,
            FakeDocumentReferenceSource.Ref(partA),
            FakeDocumentReferenceSource.Ref(partA)); // same target twice

        var scan = Scanner(source).Scan(root);

        Assert.Single(scan.References);
    }

    // ---- unresolved -----------------------------------------------------

    [Fact]
    public void An_unresolved_reference_is_a_first_class_result_and_never_recursed()
    {
        var root = InWorkspace("ROOT.iam");
        var source = new FakeDocumentReferenceSource().Add(root,
            FakeDocumentReferenceSource.Missing(@"X:\gone\LEGACY-PART.ipt"));

        var scan = Scanner(source, withWorkspace: true).Scan(root);

        var reference = Assert.Single(scan.References);
        Assert.Equal(CadReferenceResolution.Unresolved, reference.Resolution);
        Assert.Null(reference.ResolvedAbsolutePath);
        Assert.Equal(ReferenceWorkspaceScope.Unknown, reference.Scope);
        Assert.Null(reference.ManifestIdentity);
        Assert.Equal(@"X:\gone\LEGACY-PART.ipt", reference.InventorReportedName);
        Assert.Equal(1, scan.Summary.Unresolved);
        Assert.Equal(1, source.Calls); // the missing target is never expanded
    }

    // ---- workspace / manifest identity ---------------------------------

    [Fact]
    public void A_resolved_reference_to_an_exact_managed_path_gets_its_manifest_identity()
    {
        WriteManifest(
            ("ROOT.iam", "cad_root", "fv_root", "ASM-1"),
            ("PART-A.ipt", "cad_a", "fv_a", "PRT-A"));

        var root = InWorkspace("ROOT.iam");
        var source = new FakeDocumentReferenceSource().Component(root, InWorkspace("PART-A.ipt"));

        var scan = Scanner(source, withWorkspace: true).Scan(root);

        var reference = Assert.Single(scan.References);
        Assert.Equal(ReferenceWorkspaceScope.InsideWorkspace, reference.Scope);
        Assert.NotNull(reference.ManifestIdentity);
        Assert.Equal("cad_a", reference.ManifestIdentity!.CadDocumentId);
        Assert.Equal("fv_a", reference.ManifestIdentity.FileVersionId);
        Assert.Equal("PRT-A", reference.ManifestIdentity.DocumentNumber);
        Assert.Equal("PART-A.ipt", reference.ManifestIdentity.RelativePath);

        // the root itself is recognised as managed
        Assert.Equal("cad_root", scan.Root.Identity!.CadDocumentId);
    }

    [Fact]
    public void A_same_named_file_outside_the_workspace_gets_no_manifest_identity()
    {
        // The manifest binds the IN-WORKSPACE PART-A.ipt...
        WriteManifest(
            ("ROOT.iam", "cad_root", "fv_root", "ASM-1"),
            ("PART-A.ipt", "cad_a", "fv_a", "PRT-A"));

        // ...but Inventor resolves ROOT's reference to a same-named file in a
        // SOURCE directory outside the managed workspace (the exact P4C case).
        var root = InWorkspace("ROOT.iam");
        var source = new FakeDocumentReferenceSource().Component(root, Outside("PART-A.ipt"));

        var scan = Scanner(source, withWorkspace: true).Scan(root);

        var reference = Assert.Single(scan.References);
        Assert.Equal(ReferenceWorkspaceScope.OutsideWorkspace, reference.Scope);
        Assert.Null(reference.ManifestIdentity);
        Assert.Equal(Path.GetFullPath(Outside("PART-A.ipt")), reference.ResolvedAbsolutePath);
    }

    [Fact]
    public void An_in_workspace_path_with_no_manifest_entry_is_inside_but_unidentified()
    {
        WriteManifest(("ROOT.iam", "cad_root", "fv_root", "ASM-1"));

        var root = InWorkspace("ROOT.iam");
        var source = new FakeDocumentReferenceSource().Component(root, InWorkspace("UNTRACKED.ipt"));

        var reference = Assert.Single(Scanner(source, withWorkspace: true).Scan(root).References);
        Assert.Equal(ReferenceWorkspaceScope.InsideWorkspace, reference.Scope);
        Assert.Null(reference.ManifestIdentity);
    }

    [Fact]
    public void With_no_known_workspace_a_resolved_reference_scope_is_unknown()
    {
        var root = Outside("ROOT.iam");
        var source = new FakeDocumentReferenceSource().Component(root, Outside("PART-A.ipt"));

        var scan = Scanner(source).Scan(root);

        var reference = Assert.Single(scan.References);
        Assert.Equal(ReferenceWorkspaceScope.Unknown, reference.Scope);
        Assert.Null(reference.ManifestIdentity);
        Assert.Null(scan.Root.Identity);
    }

    // ---- partial-graph honesty -----------------------------------------

    [Fact]
    public void A_document_enumerated_with_zero_references_is_a_COMPLETE_scan()
    {
        var root = InWorkspace("LONE.ipt");
        var source = new FakeDocumentReferenceSource().Add(root); // Enumerated, no refs

        var scan = Scanner(source).Scan(root);

        Assert.Empty(scan.References);
        Assert.True(scan.IsComplete);
        Assert.True(scan.Summary.IsComplete);
        Assert.Equal(0, scan.Summary.DocumentsNotEnumerated);
        var node = Assert.Single(scan.Nodes);
        Assert.True(node.Enumerated);
    }

    [Fact]
    public void A_root_enumerated_but_a_child_unavailable_is_a_PARTIAL_scan_with_no_fabricated_edges()
    {
        var root = InWorkspace("ROOT.iam");
        var sub = InWorkspace("SUB.iam");
        var partA = InWorkspace("PART-A.ipt");   // "child of SUB" - must NEVER appear as a ROOT edge
        var partB = InWorkspace("PART-B.ipt");

        var source = new FakeDocumentReferenceSource()
            .Add(root, FakeDocumentReferenceSource.Ref(sub), FakeDocumentReferenceSource.Ref(partB))
            .Add(sub, FakeDocumentReferenceSource.Ref(partA)) // registered, but...
            .Unavailable(sub);                                // ...SUB cannot be inspected

        var scan = Scanner(source).Scan(root);

        Assert.False(scan.IsComplete);
        Assert.False(scan.Summary.IsComplete);
        Assert.Equal(1, scan.Summary.DocumentsNotEnumerated);

        // the two ROOT edges are kept...
        AssertEdge(scan, root, sub, CadRelationshipKind.Component);
        AssertEdge(scan, root, partB, CadRelationshipKind.Component);
        // ...and NO fake edge to PART-A (under ROOT or under SUB) is created
        Assert.DoesNotContain(scan.References, r => Same(r.ResolvedAbsolutePath, partA));
        Assert.DoesNotContain(scan.References, r => Same(r.ParentAbsolutePath, sub));

        // SUB is identified as the un-enumerated document
        var unenumerated = Assert.Single(scan.UnenumeratedNodes);
        Assert.True(Same(unenumerated.AbsolutePath, sub));
        Assert.True(scan.TargetIsUnenumerated(
            scan.References.Single(r => Same(r.ResolvedAbsolutePath, sub))));
    }

    [Fact]
    public void Multiple_unavailable_children_are_all_counted()
    {
        var root = InWorkspace("ROOT.iam");
        var subX = InWorkspace("SUB-X.iam");
        var subY = InWorkspace("SUB-Y.iam");
        var source = new FakeDocumentReferenceSource()
            .Add(root, FakeDocumentReferenceSource.Ref(subX), FakeDocumentReferenceSource.Ref(subY))
            .Unavailable(subX)
            .Unavailable(subY);

        var scan = Scanner(source).Scan(root);

        Assert.False(scan.IsComplete);
        Assert.Equal(2, scan.Summary.DocumentsNotEnumerated);
        Assert.Equal(2, scan.UnenumeratedNodes.Count);
        Assert.Equal(2, scan.References.Count); // both edges still recorded
    }

    [Fact]
    public void A_repeated_unavailable_child_yields_exactly_one_deterministic_diagnostic()
    {
        var root = InWorkspace("ROOT.iam");
        var sub2 = InWorkspace("SUB2.iam");
        var sub = InWorkspace("SUB.iam");
        var source = new FakeDocumentReferenceSource()
            .Add(root, FakeDocumentReferenceSource.Ref(sub), FakeDocumentReferenceSource.Ref(sub2))
            .Add(sub2, FakeDocumentReferenceSource.Ref(sub)) // SUB reached twice
            .Unavailable(sub);

        var first = Scanner(source).Scan(root);
        var second = Scanner(source).Scan(root);

        Assert.Single(first.UnenumeratedNodes.Where(n => Same(n.AbsolutePath, sub)));
        Assert.Equal(
            first.Nodes.Select(n => $"{n.AbsolutePath}|{n.Enumerated}"),
            second.Nodes.Select(n => $"{n.AbsolutePath}|{n.Enumerated}"));
    }

    [Fact]
    public void An_unavailable_node_inside_a_cycle_still_terminates()
    {
        var a = InWorkspace("A.iam");
        var b = InWorkspace("B.iam");
        var source = new FakeDocumentReferenceSource()
            .Add(a, FakeDocumentReferenceSource.Ref(b))
            .Add(b, FakeDocumentReferenceSource.Ref(a))
            .Unavailable(b);

        var scan = Scanner(source).Scan(a);

        Assert.Single(scan.References, r => Same(r.ParentAbsolutePath, a) && Same(r.ResolvedAbsolutePath, b));
        Assert.DoesNotContain(scan.References, r => Same(r.ParentAbsolutePath, b)); // B not enumerated -> no B->A
        Assert.False(scan.IsComplete);
        Assert.Equal(2, source.Calls); // A, B - once each
    }

    [Fact]
    public void The_default_reference_source_reports_unavailable_not_a_fake_leaf()
    {
        var scan = new CadReferenceScanner(NoDocumentReferenceSource.Instance, Array.Empty<string?>(), () => Now)
            .Scan(InWorkspace("ROOT.iam"));

        Assert.Empty(scan.References);
        Assert.False(scan.IsComplete);
        Assert.Single(scan.UnenumeratedNodes);
    }

    // ---- determinism --------------------------------------------------

    [Fact]
    public void Repeating_the_scan_produces_an_equivalent_result()
    {
        WriteManifest(("PART-A.ipt", "cad_a", "fv_a", "PRT-A"));
        var root = InWorkspace("ROOT.iam");
        var sub = InWorkspace("SUB.iam");
        var source = new FakeDocumentReferenceSource()
            .Add(root, FakeDocumentReferenceSource.Ref(sub), FakeDocumentReferenceSource.Ref(InWorkspace("PART-B.ipt")))
            .Add(sub, FakeDocumentReferenceSource.Ref(InWorkspace("PART-A.ipt")));

        var first = Scanner(source, withWorkspace: true).Scan(root);
        var second = Scanner(source, withWorkspace: true).Scan(root);

        Assert.Equal(
            first.References.Select(Fingerprint),
            second.References.Select(Fingerprint));
        Assert.Equal(
            first.Nodes.Select(n => $"{n.AbsolutePath}|{n.DocumentType}|{n.Enumerated}"),
            second.Nodes.Select(n => $"{n.AbsolutePath}|{n.DocumentType}|{n.Enumerated}"));
        Assert.Equal(first.Summary, second.Summary);
    }

    // ---- helpers ----------------------------------------------------------

    private static string Fingerprint(CadReference r) =>
        $"{r.ParentAbsolutePath}|{r.ResolvedAbsolutePath}|{r.InventorReportedName}|{r.RelationshipKind}|{r.Resolution}|{r.Scope}|{r.ManifestIdentity?.CadDocumentId}";

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void AssertEdge(CadReferenceScan scan, string parent, string child, CadRelationshipKind kind)
    {
        var edge = Assert.Single(scan.References, r =>
            Same(r.ParentAbsolutePath, parent) && Same(r.ResolvedAbsolutePath, child));
        Assert.Equal(kind, edge.RelationshipKind);
    }

    private void WriteManifest(params (string rel, string cad, string fv, string docNum)[] entries)
    {
        var doc = new
        {
            schema = WorkspaceManifest.SchemaId,
            serverOrigin = "https://plm.example.com",
            organizationId = "org1",
            organizationCode = "ACME",
            rootCadDocumentId = "cad_root",
            rootDocumentNumber = "ASM-1",
            updatedAtUtc = Now,
            entries = entries.Select(e => new
            {
                relativePath = e.rel,
                cadDocumentId = e.cad,
                documentNumber = e.docNum,
                fileName = e.rel,
                cadType = e.rel.EndsWith(".iam") ? "IAM" : "IPT",
                fileVersionId = e.fv,
                versionNumber = 1,
                checksum = "sha-" + e.fv,
                fileSize = 10,
                isRoot = e.rel == "ROOT.iam",
                dependsOn = Array.Empty<string>(),
                state = "Verified",
                retrievedAtUtc = Now,
            }).ToArray(),
        };

        Directory.CreateDirectory(Path.Combine(_workspace, ".arch"));
        File.WriteAllText(
            Path.Combine(_workspace, WorkspaceManifest.RelativeManifestPath),
            JsonSerializer.Serialize(doc, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
