using System.Text.Json;

using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.References;

/// <summary>
/// End-to-end: the real P5A <see cref="CadReferenceScanner"/> feeding the
/// P5B-A <see cref="ReferenceHealthDiagnoser"/>, over a genuine (test-written)
/// workspace manifest and a fake Inventor reference source. Proves the wiring
/// and the "no managed identity for a same-named file outside the workspace"
/// invariant through the whole pipeline.
/// </summary>
public class ReferenceHealthPipelineTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "arch-cc-rh-" + Guid.NewGuid().ToString("N"));
    private readonly string _source =
        Path.Combine(Path.GetTempPath(), "arch-cc-rh-src-" + Guid.NewGuid().ToString("N"));

    public ReferenceHealthPipelineTests()
    {
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_source);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _workspace, _source })
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private string InWs(string name) => Path.Combine(_workspace, name);
    private string Outside(string name) => Path.Combine(_source, name);

    private ReferenceHealthReport Run(string root, FakeDocumentReferenceSource source) =>
        ReferenceHealthDiagnoser.Diagnose(
            new CadReferenceScanner(source, new[] { _workspace }, () => Now).Scan(root));

    /// <summary>
    /// Mimics exactly what <c>ArchAddInController.WorkspaceRootsForDocument</c>
    /// does: the authoritative root is discovered from the active document's
    /// own <c>.arch/workspace.json</c>, plus an optional stale "last Get Latest"
    /// folder that must NOT be able to override the real one.
    /// </summary>
    private ReferenceHealthReport RunAsCommandWould(string root, FakeDocumentReferenceSource source, string? staleLastRoot = null)
    {
        var roots = new List<string?> { WorkspaceRootLocator.FindRootForFile(root) };
        if (staleLastRoot is not null)
        {
            roots.Add(staleLastRoot);
        }
        return ReferenceHealthDiagnoser.Diagnose(
            new CadReferenceScanner(source, roots, () => Now).Scan(root));
    }

    private ReferenceHealthReport RunWithRoots(string root, FakeDocumentReferenceSource source, params string?[] roots) =>
        ReferenceHealthDiagnoser.Diagnose(new CadReferenceScanner(source, roots, () => Now).Scan(root));

    // ---- regression: the real Inventor 2025 manual-test defect --------

    [Fact]
    public void Manual_defect_repro_children_of_the_active_documents_own_workspace_are_managed_and_healthy()
    {
        // Genuine managed workspace: ROOT + PART-A + PART-B all exact manifest
        // entries. The document is opened directly - no preceding Get Latest -
        // and the "last Get Latest" folder still points somewhere unrelated.
        WriteManifest(
            ("ROOT.iam", "cad_root", "fv_root"),
            ("PART-A.ipt", "cad_a", "fv_a"),
            ("PART-B.ipt", "cad_b", "fv_b"));

        var root = InWs("ROOT.iam");
        var source = new FakeDocumentReferenceSource().Add(root,
            FakeDocumentReferenceSource.Ref(InWs("PART-A.ipt")),
            FakeDocumentReferenceSource.Ref(InWs("PART-B.ipt")));

        var report = RunAsCommandWould(root, source, staleLastRoot: _source); // stale/wrong last root

        Assert.Equal(ReferenceHealth.Healthy, report.OverallHealth);
        Assert.True(report.ScanComplete);
        Assert.Equal("cad_root", report.Root.Identity?.CadDocumentId);

        foreach (var (name, cad, fv) in new[] { ("PART-A.ipt", "cad_a", "fv_a"), ("PART-B.ipt", "cad_b", "fv_b") })
        {
            var entry = Assert.Single(report.Entries,
                e => string.Equals(Path.GetFileName(e.Reference.ResolvedAbsolutePath), name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(ReferenceWorkspaceScope.InsideWorkspace, entry.Scope);
            Assert.Equal(ReferenceManagement.Managed, entry.Management);
            Assert.Equal(ReferenceHealth.Healthy, entry.Health);
            Assert.NotNull(entry.ManagedIdentity);
            Assert.Equal(cad, entry.ManagedIdentity!.CadDocumentId);
            Assert.Equal(fv, entry.ManagedIdentity.FileVersionId);
            Assert.Equal(name, entry.ManagedIdentity.RelativePath);
        }

        var s = report.Summary;
        Assert.Equal(2, s.Healthy);
        Assert.Equal(0, s.Warnings);
        Assert.Equal(2, s.Managed);
        Assert.Equal(0, s.Unmanaged);
        Assert.Equal(2, s.InsideWorkspace);
        Assert.Equal(0, s.OutsideWorkspace);
    }

    [Fact]
    public void A_reference_into_a_nested_workspace_is_managed_via_the_nested_manifest_regardless_of_root_order()
    {
        // Outer workspace: ROOT.iam bound in the OUTER manifest.
        WriteManifest(("ROOT.iam", "cad_root", "fv_root"));

        // Nested workspace under it, with its OWN genuine manifest binding PART.ipt.
        var nested = Path.Combine(_workspace, "Nested");
        Directory.CreateDirectory(nested);
        WriteManifestAt(nested, ("PART.ipt", "cad_nested_part", "fv_nested_part"));

        var root = InWs("ROOT.iam");
        var nestedPart = Path.Combine(nested, "PART.ipt");
        var source = new FakeDocumentReferenceSource().Add(root, FakeDocumentReferenceSource.Ref(nestedPart));

        foreach (var roots in new[] { new[] { _workspace, nested }, new[] { nested, _workspace } })
        {
            var entry = Assert.Single(RunWithRoots(root, source, roots).Entries);

            Assert.Equal(ReferenceWorkspaceScope.InsideWorkspace, entry.Scope);
            Assert.Equal(ReferenceManagement.Managed, entry.Management);
            Assert.Equal(ReferenceHealth.Healthy, entry.Health);
            Assert.NotNull(entry.ManagedIdentity);
            // identity comes from the NESTED manifest, not the outer one
            Assert.Equal("cad_nested_part", entry.ManagedIdentity!.CadDocumentId);
            Assert.Equal("fv_nested_part", entry.ManagedIdentity.FileVersionId);
            Assert.Equal("PART.ipt", entry.ManagedIdentity.RelativePath);
        }
    }

    [Fact]
    public void A_file_directly_in_the_outer_workspace_still_resolves_against_the_outer_manifest()
    {
        WriteManifest(("ROOT.iam", "cad_root", "fv_root"), ("PART-A.ipt", "cad_a", "fv_a"));
        var nested = Path.Combine(_workspace, "Nested");
        Directory.CreateDirectory(nested);
        WriteManifestAt(nested, ("OTHER.ipt", "cad_other", "fv_other"));

        var root = InWs("ROOT.iam");
        var source = new FakeDocumentReferenceSource().Add(root, FakeDocumentReferenceSource.Ref(InWs("PART-A.ipt")));

        var entry = Assert.Single(RunWithRoots(root, source, nested, _workspace).Entries);
        Assert.Equal(ReferenceManagement.Managed, entry.Management);
        Assert.Equal("cad_a", entry.ManagedIdentity!.CadDocumentId); // OUTER manifest
    }

    [Fact]
    public void A_stale_last_get_latest_root_cannot_flip_a_managed_child_to_unmanaged()
    {
        WriteManifest(("ROOT.iam", "cad_root", "fv_root"), ("PART-A.ipt", "cad_a", "fv_a"));
        var root = InWs("ROOT.iam");
        var source = new FakeDocumentReferenceSource().Add(root, FakeDocumentReferenceSource.Ref(InWs("PART-A.ipt")));

        // Whether or not a stale root is supplied, the discovered workspace wins.
        Assert.Equal(ReferenceManagement.Managed, Assert.Single(RunAsCommandWould(root, source).Entries).Management);
        Assert.Equal(ReferenceManagement.Managed, Assert.Single(RunAsCommandWould(root, source, staleLastRoot: _source).Entries).Management);
    }

    [Fact]
    public void The_source_fixture_outside_the_workspace_still_reads_as_unmanaged_through_discovery()
    {
        WriteManifest(("ROOT.iam", "cad_root", "fv_root"), ("PART-A.ipt", "cad_a", "fv_a"));
        var root = InWs("ROOT.iam");
        // ROOT references a same-named part that Inventor resolves into the
        // SOURCE folder (which has NO .arch/workspace.json).
        var source = new FakeDocumentReferenceSource().Add(root,
            FakeDocumentReferenceSource.Ref(Outside("PART-A.ipt")));

        var entry = Assert.Single(RunAsCommandWould(root, source).Entries);
        Assert.Equal(ReferenceWorkspaceScope.OutsideWorkspace, entry.Scope);
        Assert.Equal(ReferenceManagement.Unmanaged, entry.Management);
        Assert.Equal(ReferenceHealth.Warning, entry.Health);
        Assert.Null(entry.ManagedIdentity);
    }

    [Fact]
    public void An_active_document_not_inside_any_managed_workspace_yields_no_managed_identities()
    {
        // No manifest written anywhere. The scan still runs; nothing is managed.
        var root = InWs("ROOT.iam");
        var source = new FakeDocumentReferenceSource().Add(root,
            FakeDocumentReferenceSource.Ref(InWs("PART-A.ipt")));

        var report = RunAsCommandWould(root, source);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(ReferenceManagement.Unknown, entry.Management);
        Assert.Equal(ReferenceHealth.Unknown, entry.Health); // scope Unknown - no root discovered
        Assert.Null(report.Root.Identity);
    }

    [Fact]
    public void Managed_child_is_healthy_unmanaged_sibling_is_a_warning_missing_is_an_error()
    {
        WriteManifest(
            ("ROOT.iam", "cad_root", "fv_root"),
            ("PART-A.ipt", "cad_a", "fv_a"));

        var root = InWs("ROOT.iam");
        var source = new FakeDocumentReferenceSource().Add(root,
            FakeDocumentReferenceSource.Ref(InWs("PART-A.ipt")),        // managed
            FakeDocumentReferenceSource.Ref(InWs("SCRATCH.ipt")),       // inside, no manifest entry
            FakeDocumentReferenceSource.Missing(@"X:\gone\MOTOR.ipt")); // missing

        var report = Run(root, source);

        Assert.Equal(ReferenceHealth.Error, report.OverallHealth);
        var byName = report.Entries.ToDictionary(e => Path.GetFileName(e.Reference.ResolvedAbsolutePath ?? e.Reference.InventorReportedName));

        Assert.Equal(ReferenceHealth.Healthy, byName["PART-A.ipt"].Health);
        Assert.Equal(ReferenceManagement.Managed, byName["PART-A.ipt"].Management);
        Assert.Equal("cad_a", byName["PART-A.ipt"].ManagedIdentity!.CadDocumentId);

        Assert.Equal(ReferenceHealth.Warning, byName["SCRATCH.ipt"].Health);
        Assert.Equal(ReferenceManagement.Unmanaged, byName["SCRATCH.ipt"].Management);
        Assert.Null(byName["SCRATCH.ipt"].ManagedIdentity);

        Assert.Equal(ReferenceHealth.Error, byName["MOTOR.ipt"].Health);
        Assert.True(byName["MOTOR.ipt"].IsMissing);
    }

    [Fact]
    public void A_same_named_file_resolved_outside_the_workspace_is_unmanaged_through_the_whole_pipeline()
    {
        // The manifest binds the IN-WORKSPACE PART-A.ipt...
        WriteManifest(
            ("ROOT.iam", "cad_root", "fv_root"),
            ("PART-A.ipt", "cad_a", "fv_a"));

        // ...but Inventor resolves ROOT's reference to a same-named file in the
        // SOURCE directory outside the workspace (the exact P4C scenario).
        var root = InWs("ROOT.iam");
        var source = new FakeDocumentReferenceSource().Add(root,
            FakeDocumentReferenceSource.Ref(Outside("PART-A.ipt")));

        var entry = Assert.Single(Run(root, source).Entries);

        Assert.Equal(ReferenceWorkspaceScope.OutsideWorkspace, entry.Scope);
        Assert.Equal(ReferenceManagement.Unmanaged, entry.Management);
        Assert.Equal(ReferenceHealth.Warning, entry.Health);
        Assert.Null(entry.ManagedIdentity);
        Assert.Equal(Path.GetFullPath(Outside("PART-A.ipt")), entry.Reference.ResolvedAbsolutePath);
    }

    [Fact]
    public void A_sibling_prefix_directory_is_outside_the_workspace_not_managed()
    {
        // workspace = ...\job1 ; reference resolves under ...\job1-archive
        var archive = _workspace + "-archive";
        Directory.CreateDirectory(archive);
        try
        {
            WriteManifest(("ROOT.iam", "cad_root", "fv_root"));
            var root = InWs("ROOT.iam");
            var source = new FakeDocumentReferenceSource().Add(root,
                FakeDocumentReferenceSource.Ref(Path.Combine(archive, "PART-A.ipt")));

            var entry = Assert.Single(Run(root, source).Entries);
            Assert.Equal(ReferenceWorkspaceScope.OutsideWorkspace, entry.Scope);
            Assert.Equal(ReferenceManagement.Unmanaged, entry.Management);
        }
        finally
        {
            try { Directory.Delete(archive, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void An_unavailable_subassembly_makes_the_report_partial_without_fabricating_children()
    {
        WriteManifest(
            ("ROOT.iam", "cad_root", "fv_root"),
            ("SUB.iam", "cad_sub", "fv_sub"));

        var root = InWs("ROOT.iam");
        var sub = InWs("SUB.iam");
        var partA = InWs("PART-A.ipt");
        var source = new FakeDocumentReferenceSource()
            .Add(root, FakeDocumentReferenceSource.Ref(sub))
            .Add(sub, FakeDocumentReferenceSource.Ref(partA)) // registered but...
            .Unavailable(sub);                                 // ...SUB cannot be inspected

        var report = Run(root, source);

        Assert.False(report.ScanComplete);
        Assert.Equal(ReferenceHealth.Unknown, report.OverallHealth); // healthy edge + partial => Unknown
        var edge = Assert.Single(report.Entries);
        Assert.Equal(ReferenceHealth.Healthy, edge.Health);       // the ROOT->SUB edge's own facts are known
        Assert.False(edge.ChildReferencesEnumerated);
        Assert.DoesNotContain(report.Entries, e => Path.GetFileName(e.Reference.ResolvedAbsolutePath ?? "") == "PART-A.ipt");
        Assert.Single(report.UnenumeratedDocuments);
    }

    private void WriteManifest(params (string rel, string cad, string fv)[] entries)
        => WriteManifestAt(_workspace, entries);

    private void WriteManifestAt(string directory, params (string rel, string cad, string fv)[] entries)
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
                documentNumber = "DOC-" + e.cad,
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

        var manifestPath = Path.Combine(directory, WorkspaceManifest.RelativeManifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
