using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

/// <summary>P6E-B: PURE tests for <see cref="CopyDesignVerificationTopologyBuilder"/> -
///  building the EXPECTED graph from durable verification-support data +
///  the local source workspace manifest. No COM, no HTTP, no I/O beyond
///  reading the in-memory manifest fixture files this test class writes to
///  a temp directory.</summary>
public class CopyDesignVerificationTopologyBuilderTests : IDisposable
{
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), "arch-cc-verify-topo-src-" + Guid.NewGuid().ToString("N"));
    private readonly string _destinationFolder = Path.Combine(Path.GetTempPath(), "arch-cc-verify-topo-dst-" + Guid.NewGuid().ToString("N"));

    public CopyDesignVerificationTopologyBuilderTests()
    {
        Directory.CreateDirectory(Path.Combine(_sourceRoot, ".arch"));
        Directory.CreateDirectory(_destinationFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sourceRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_destinationFolder, recursive: true); } catch { /* best effort */ }
    }

    private WorkspaceManifest SourceManifest(params (string CadDocumentId, string FileVersionId, string FileName)[] entries)
    {
        var entryJson = string.Join(",\n", entries.Select(e => $$"""
            {
              "relativePath": "{{e.FileName}}",
              "cadDocumentId": "{{e.CadDocumentId}}",
              "documentNumber": "{{Path.GetFileNameWithoutExtension(e.FileName)}}",
              "fileName": "{{e.FileName}}",
              "cadType": "IPT",
              "fileVersionId": "{{e.FileVersionId}}",
              "versionNumber": 1,
              "checksum": "{{new string('a', 64)}}",
              "fileSize": 8,
              "isRoot": false,
              "dependsOn": [],
              "state": "Verified",
              "retrievedAtUtc": "2026-09-23T00:00:00Z"
            }
            """));
        File.WriteAllText(Path.Combine(_sourceRoot, WorkspaceManifest.RelativeManifestPath), $$"""
            {
              "schema": "arch-plm.workspace-manifest.v1",
              "serverOrigin": "https://plm.example.com",
              "organizationId": "org1",
              "organizationCode": "ORGA",
              "rootCadDocumentId": "cad-root",
              "rootDocumentNumber": "ROOT",
              "updatedAtUtc": "2026-09-23T00:00:00Z",
              "entries": [
                {{entryJson}}
              ]
            }
            """);
        return WorkspaceManifest.LoadOrEmpty(_sourceRoot);
    }

    private static CopyDesignDurableResumeEntry CopyEntry(
        int ordinal, string sourceId, string sourceFvId, string resultingId,
        string fileName, string docType, string state = "PENDING",
        string? fvId = null, int? versionNumber = null, string? sha256 = null, long? fileSize = null) => new(
        ordinal, "COPY", state, sourceId, sourceFvId, resultingId,
        Path.GetFileNameWithoutExtension(fileName), fileName, docType, null,
        fvId, versionNumber, sha256, fileSize);

    private static CopyDesignDurableResumeEntry ReuseEntry(int ordinal, string resultingId, string fileName, string docType) => new(
        ordinal, "REUSE", "REUSED", null, null, resultingId,
        Path.GetFileNameWithoutExtension(fileName), fileName, docType, null);

    private static CopyDesignVerificationSupportResult Support(
        IReadOnlyList<CopyDesignDurableResumeEntry> entries,
        IReadOnlyList<CopyDesignComponentEdge>? componentEdges = null,
        IReadOnlyList<CopyDesignSourceIntegrityEvidence>? sourceIntegrity = null) => new(
        CopyDesignVerificationSupportOutcome.Found, "op-1", "key-1", entries,
        sourceIntegrity ?? Array.Empty<CopyDesignSourceIntegrityEvidence>(),
        componentEdges ?? Array.Empty<CopyDesignComponentEdge>());

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> NoDrawings =
        new Dictionary<string, IReadOnlySet<string>>();

    // ---- happy path ------------------------------------------------------

    [Fact]
    public void A_single_COPY_entry_builds_one_node_with_a_safe_resolved_destination()
    {
        var manifest = SourceManifest(("cad-src", "fv-src", "A.ipt"));
        var support = Support(new[] { CopyEntry(0, "cad-src", "fv-src", "cad-new", "A-NEW.ipt", "IPT") });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.True(result.Success, result.FailureReason);
        var node = Assert.Single(result.Plan!.Nodes);
        Assert.Equal(CopyDesignAction.Copy, node.ProposedAction);
        Assert.Equal(Path.Combine(_destinationFolder, "A-NEW.ipt"), node.ProposedDestinationAbsolutePath);
        Assert.Empty(result.DestinationProblems!);
    }

    [Fact]
    public void A_REUSE_entry_resolves_its_OWN_path_via_resultingCadDocumentId_not_an_empty_string()
    {
        // Regression-motivated: a REUSE node needs a REAL, unique
        // SourceAbsolutePath so multiple REUSE entries never collide on the
        // same key when building the edge graph.
        var manifest = SourceManifest(("cad-reused", "fv-reused", "STD.ipt"));
        var support = Support(new[] { ReuseEntry(0, "cad-reused", "STD.ipt", "IPT") });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.True(result.Success, result.FailureReason);
        var node = Assert.Single(result.Plan!.Nodes);
        Assert.Equal(CopyDesignAction.Reuse, node.ProposedAction);
        Assert.Null(node.ProposedDestinationAbsolutePath);
        Assert.EndsWith("STD.ipt", node.SourceAbsolutePath);
    }

    [Fact]
    public void Two_different_REUSE_entries_get_two_DISTINCT_resolved_paths()
    {
        var manifest = SourceManifest(("cad-r1", "fv-r1", "R1.ipt"), ("cad-r2", "fv-r2", "R2.ipt"));
        var support = Support(new[] { ReuseEntry(0, "cad-r1", "R1.ipt", "IPT"), ReuseEntry(1, "cad-r2", "R2.ipt", "IPT") });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(2, result.Plan!.Nodes.Select(n => n.SourceAbsolutePath).Distinct().Count());
    }

    // ---- component topology (nested) --------------------------------------

    [Fact]
    public void Nested_COMPONENT_edges_MAIN_to_SUBA_to_A1_are_built_correctly()
    {
        var manifest = SourceManifest(
            ("cad-main", "fv-main", "MAIN.iam"),
            ("cad-suba", "fv-suba", "SUBA.iam"),
            ("cad-a1", "fv-a1", "A1.ipt"));
        var support = Support(
            new[]
            {
                CopyEntry(0, "cad-main", "fv-main", "cad-main-new", "MAIN-NEW.iam", "IAM"),
                CopyEntry(1, "cad-suba", "fv-suba", "cad-suba-new", "SUBA-NEW.iam", "IAM"),
                CopyEntry(2, "cad-a1", "fv-a1", "cad-a1-new", "A1-NEW.ipt", "IPT"),
            },
            componentEdges: new[]
            {
                new CopyDesignComponentEdge("cad-main", "cad-suba"),
                new CopyDesignComponentEdge("cad-suba", "cad-a1"),
            });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(2, result.Plan!.Edges.Count);
        Assert.All(result.Plan.Edges, e => Assert.Equal(CadRelationshipKind.Component, e.RelationshipKind));
    }

    [Fact]
    public void A_mixed_COPY_REUSE_topology_still_builds_the_edge_into_the_REUSE_identity()
    {
        var manifest = SourceManifest(("cad-main", "fv-main", "MAIN.iam"), ("cad-suba", "fv-suba", "SUBA.iam"));
        var support = Support(
            new[]
            {
                CopyEntry(0, "cad-main", "fv-main", "cad-main-new", "MAIN-NEW.iam", "IAM"),
                ReuseEntry(1, "cad-suba", "SUBA.iam", "IAM"),
            },
            componentEdges: new[] { new CopyDesignComponentEdge("cad-main", "cad-suba") });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.True(result.Success, result.FailureReason);
        var edge = Assert.Single(result.Plan!.Edges);
        Assert.Equal(CopyDesignEdgeDisposition.RemainsOnReusedSource, edge.Disposition);
    }

    [Fact]
    public void A_COMPONENT_edge_naming_a_document_OUTSIDE_this_operations_entries_is_silently_not_represented()
    {
        var manifest = SourceManifest(("cad-main", "fv-main", "MAIN.iam"));
        var support = Support(
            new[] { CopyEntry(0, "cad-main", "fv-main", "cad-main-new", "MAIN-NEW.iam", "IAM") },
            componentEdges: new[] { new CopyDesignComponentEdge("cad-main", "cad-OUTSIDE") });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.True(result.Success, result.FailureReason);
        Assert.Empty(result.Plan!.Edges);
    }

    // ---- drawing topology --------------------------------------------------

    [Fact]
    public void DrawingModel_edges_are_built_from_the_supplied_authority()
    {
        var manifest = SourceManifest(("cad-main", "fv-main", "MAIN.iam"), ("cad-idw", "fv-idw", "MAIN.idw"));
        var support = Support(new[]
        {
            CopyEntry(0, "cad-main", "fv-main", "cad-main-new", "MAIN-NEW.iam", "IAM"),
            CopyEntry(1, "cad-idw", "fv-idw", "cad-idw-new", "MAIN-NEW.idw", "IDW"),
        });
        var drawings = new Dictionary<string, IReadOnlySet<string>> { ["cad-idw"] = new HashSet<string> { "cad-main" } };

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, drawings);

        Assert.True(result.Success, result.FailureReason);
        var edge = Assert.Single(result.Plan!.Edges);
        Assert.Equal(CadRelationshipKind.DrawingModel, edge.RelationshipKind);
        Assert.Empty(result.DrawingAuthorityProblems!);
    }

    [Fact]
    public void A_drawing_with_NO_key_at_all_in_the_authority_map_is_flagged_as_a_problem_not_silently_zero()
    {
        var manifest = SourceManifest(("cad-idw", "fv-idw", "MAIN.idw"));
        var support = Support(new[] { CopyEntry(0, "cad-idw", "fv-idw", "cad-idw-new", "MAIN-NEW.idw", "IDW") });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.True(result.Success, result.FailureReason);
        Assert.True(result.DrawingAuthorityProblems!.ContainsKey("cad-idw-new"));
    }

    [Fact]
    public void A_drawing_with_a_PRESENT_but_EMPTY_authority_set_is_trusted_as_authoritatively_zero_not_a_problem()
    {
        var manifest = SourceManifest(("cad-idw", "fv-idw", "MAIN.idw"));
        var support = Support(new[] { CopyEntry(0, "cad-idw", "fv-idw", "cad-idw-new", "MAIN-NEW.idw", "IDW") });
        var drawings = new Dictionary<string, IReadOnlySet<string>> { ["cad-idw"] = new HashSet<string>() };

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, drawings);

        Assert.True(result.Success, result.FailureReason);
        Assert.False(result.DrawingAuthorityProblems!.ContainsKey("cad-idw-new"));
        Assert.Empty(result.Plan!.Edges);
    }

    // ---- per-entry destination problems (LENIENT - never abort the build) --

    [Theory]
    [InlineData("..\\P6E-EVIL.ipt")]
    [InlineData("C:\\Temp\\evil.ipt")]
    [InlineData("subdir\\evil.ipt")]
    public void An_unsafe_destination_file_name_is_a_PER_ENTRY_problem_never_a_whole_build_abort(string maliciousFileName)
    {
        var manifest = SourceManifest(("cad-src", "fv-src", "A.ipt"));
        var support = Support(new[] { CopyEntry(0, "cad-src", "fv-src", "cad-new", maliciousFileName, "IPT") });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.True(result.Success, result.FailureReason);
        var node = Assert.Single(result.Plan!.Nodes);
        Assert.Null(node.ProposedDestinationAbsolutePath);
        Assert.True(result.DestinationProblems!.ContainsKey("cad-new"));
    }

    // ---- structural (whole-build) failures --------------------------------

    [Fact]
    public void A_gapped_ordinal_sequence_fails_the_whole_build()
    {
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"), ("cad-b", "fv-b", "B.ipt"));
        var support = Support(new[]
        {
            CopyEntry(0, "cad-a", "fv-a", "cad-a-new", "A-NEW.ipt", "IPT"),
            CopyEntry(2, "cad-b", "fv-b", "cad-b-new", "B-NEW.ipt", "IPT"),
        });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.False(result.Success);
        Assert.Contains("missing ordinal", result.FailureReason);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void A_duplicate_ordinal_fails_the_whole_build()
    {
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"), ("cad-b", "fv-b", "B.ipt"));
        var support = Support(new[]
        {
            CopyEntry(0, "cad-a", "fv-a", "cad-a-new", "A-NEW.ipt", "IPT"),
            CopyEntry(0, "cad-b", "fv-b", "cad-b-new", "B-NEW.ipt", "IPT"),
        });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.False(result.Success);
        Assert.Contains("More than one entry", result.FailureReason);
    }

    [Fact]
    public void A_source_not_bound_in_the_local_workspace_manifest_fails_the_whole_build()
    {
        var manifest = SourceManifest(("cad-other", "fv-other", "OTHER.ipt"));
        var support = Support(new[] { CopyEntry(0, "cad-src", "fv-src", "cad-new", "A-NEW.ipt", "IPT") });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.False(result.Success);
        Assert.Contains("does not have a bound entry", result.FailureReason);
    }

    [Fact]
    public void A_local_workspace_bound_to_a_DIFFERENT_FileVersion_than_the_operation_fails_the_whole_build()
    {
        var manifest = SourceManifest(("cad-src", "fv-DIFFERENT", "A.ipt"));
        var support = Support(new[] { CopyEntry(0, "cad-src", "fv-src", "cad-new", "A-NEW.ipt", "IPT") });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.False(result.Success);
        Assert.Contains("DIFFERENT FileVersion", result.FailureReason);
    }

    [Fact]
    public void An_unrecognized_action_fails_the_whole_build()
    {
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var entry = new CopyDesignDurableResumeEntry(0, "DELETE", "PENDING", "cad-a", "fv-a", "cad-a-new", "A-NEW", "A-NEW.ipt", "IPT", null);
        var support = Support(new[] { entry });

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.False(result.Success);
        Assert.Contains("unrecognized action", result.FailureReason);
    }

    [Fact]
    public void An_unsuccessful_support_result_is_rejected_up_front()
    {
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = new CopyDesignVerificationSupportResult(CopyDesignVerificationSupportOutcome.NotFound);

        var result = CopyDesignVerificationTopologyBuilder.Build(support, manifest, _destinationFolder, NoDrawings);

        Assert.False(result.Success);
    }
}
