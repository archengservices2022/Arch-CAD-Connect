using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

/// <summary>
/// P6D PRODUCTION RECOVERY: pure tests for
/// <see cref="CopyDesignResumeAttemptReconstructor"/> - proving it rebuilds
/// the EXACT original attempt from server-authoritative status +
/// cross-validated workspace-manifest bindings, and fails closed on every
/// proven unsafe/ambiguous shape. Modeled directly on the LIVE operation
/// this round exists to recover: cmuecs9bo0003yymo3roh70a9 - ROOT.iam,
/// PART-A.ipt, PART-B.ipt MATERIALIZED; ROOT.idw PENDING.
/// </summary>
public class CopyDesignResumeAttemptReconstructorTests : IDisposable
{
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), "arch-cc-resume-src-" + Guid.NewGuid().ToString("N"));
    private readonly string _destinationFolder = Path.Combine(Path.GetTempPath(), "arch-cc-resume-dst-" + Guid.NewGuid().ToString("N"));

    public CopyDesignResumeAttemptReconstructorTests()
    {
        Directory.CreateDirectory(Path.Combine(_sourceRoot, ".arch"));
        Directory.CreateDirectory(_destinationFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sourceRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_destinationFolder, recursive: true); } catch { /* best effort */ }
    }

    // ---- fixture builders -------------------------------------------------

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
              "rootCadDocumentId": "cad-root-idw",
              "rootDocumentNumber": "P6D-REAL-ROOT",
              "updatedAtUtc": "2026-09-23T00:00:00Z",
              "entries": [
                {{entryJson}}
              ]
            }
            """);
        return WorkspaceManifest.LoadOrEmpty(_sourceRoot);
    }

    /// <summary>The EXACT live operation's shape: 3 COPY entries
    ///  MATERIALIZED (ROOT.iam, PART-A.ipt, PART-B.ipt), 1 COPY entry
    ///  PENDING (ROOT.idw).</summary>
    private static CopyDesignDurableResumeStatusResult LiveShapeStatus(
        string idwState = "PENDING") => new(
        CopyDesignDurableResumeStatusOutcome.Found,
        "cmuecs9bo0003yymo3roh70a9",
        "cadconnect-p6c-73a1860f0c684ff5bc2de84782db6fe4",
        new[]
        {
            new CopyDesignDurableResumeEntry(
                0, "COPY", "MATERIALIZED", "cad-root-iam", "fv-root-iam-src", "cad-root-iam-new",
                "P6DNEW-REAL-ROOT", "P6DNEW-REAL-ROOT.iam", "IAM", null,
                "fv-root-iam-1", 1, new string('1', 64), 74752L),
            new CopyDesignDurableResumeEntry(
                1, "COPY", "MATERIALIZED", "cad-part-a", "fv-part-a-src", "cad-part-a-new",
                "P6DNEW-REAL-PART-A", "P6DNEW-REAL-PART-A.ipt", "IPT", null,
                "fv-part-a-1", 1, new string('2', 64), 80896L),
            new CopyDesignDurableResumeEntry(
                2, "COPY", "MATERIALIZED", "cad-part-b", "fv-part-b-src", "cad-part-b-new",
                "P6DNEW-REAL-PART-B", "P6DNEW-REAL-PART-B.ipt", "IPT", null,
                "fv-part-b-1", 1, new string('3', 64), 80896L),
            new CopyDesignDurableResumeEntry(
                3, "COPY", idwState, "cad-root-idw", "fv-root-idw-src", "cad-root-idw-new",
                "P6DNEW-REAL-ROOT", "P6DNEW-REAL-ROOT.idw", "IDW", null),
        });

    private WorkspaceManifest LiveShapeManifest() => SourceManifest(
        ("cad-root-iam", "fv-root-iam-src", "P6D-REAL-ROOT.iam"),
        ("cad-part-a", "fv-part-a-src", "P6D-REAL-PART-A.ipt"),
        ("cad-part-b", "fv-part-b-src", "P6D-REAL-PART-B.ipt"),
        ("cad-root-idw", "fv-root-idw-src", "P6D-REAL-ROOT.idw"));

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> LiveShapeDependencies() =>
        new Dictionary<string, IReadOnlySet<string>>
        {
            ["cad-root-idw"] = new HashSet<string> { "cad-root-iam" },
        };

    // ---- happy path ---------------------------------------------------

    [Fact]
    public void FIX_the_exact_live_shape_reconstructs_successfully()
    {
        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            LiveShapeStatus(), LiveShapeManifest(), _destinationFolder, LiveShapeDependencies());

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("cmuecs9bo0003yymo3roh70a9", result.CopyDesignOperationId);
        Assert.Equal("cadconnect-p6c-73a1860f0c684ff5bc2de84782db6fe4", result.IdempotencyKey);
        Assert.Equal(4, result.Plan!.Nodes.Count);
        Assert.True(result.Plan.IsExecutable);
        Assert.False(result.Plan.ModelFilesOnlyAcknowledged);
    }

    [Fact]
    public void The_reconstructed_plan_proposes_COPY_for_every_entry_with_the_correct_destination()
    {
        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            LiveShapeStatus(), LiveShapeManifest(), _destinationFolder, LiveShapeDependencies());

        var idw = Assert.Single(result.Plan!.Nodes, n => n.CadDocumentId == "cad-root-idw");
        Assert.Equal(CopyDesignAction.Copy, idw.ProposedAction);
        Assert.Equal(Path.Combine(_destinationFolder, "P6DNEW-REAL-ROOT.idw"), idw.ProposedDestinationAbsolutePath);
        Assert.Equal(CadDocumentType.Idw, idw.DocumentType);
        Assert.Equal("fv-root-idw-src", idw.CurrentFileVersionId);
    }

    [Fact]
    public void The_drawing_model_edge_is_reconstructed_from_the_authoritative_dependency_map()
    {
        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            LiveShapeStatus(), LiveShapeManifest(), _destinationFolder, LiveShapeDependencies());

        var idw = result.Plan!.Nodes.Single(n => n.CadDocumentId == "cad-root-idw");
        var iam = result.Plan.Nodes.Single(n => n.CadDocumentId == "cad-root-iam");
        var edge = Assert.Single(result.Plan.Edges);
        Assert.Equal(idw.SourceAbsolutePath, edge.ParentAbsolutePath);
        Assert.Equal(iam.SourceAbsolutePath, edge.ChildAbsolutePath);
        Assert.Equal(Arch.CadConnect.Core.References.CadRelationshipKind.DrawingModel, edge.RelationshipKind);
        Assert.Equal(CopyDesignEdgeDisposition.PointsToNewCopy, edge.Disposition);
    }

    [Fact]
    public void An_all_materialized_operation_still_reconstructs_with_zero_edges_needed()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(
                    0, "COPY", "MATERIALIZED", "cad-part-a", "fv-part-a-src", "cad-part-a-new",
                    "P6DNEW-REAL-PART-A", "P6DNEW-REAL-PART-A.ipt", "IPT", null, "fv-1", 1, new string('2', 64), 80896L),
            });
        var manifest = SourceManifest(("cad-part-a", "fv-part-a-src", "P6D-REAL-PART-A.ipt"));

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, manifest, _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());

        Assert.True(result.Success, result.FailureReason);
        Assert.Single(result.Plan!.Nodes);
        Assert.Empty(result.Plan.Edges);
    }

    // ---- fail closed: status-level -----------------------------------

    [Theory]
    [InlineData(CopyDesignDurableResumeStatusOutcome.NotFound)]
    [InlineData(CopyDesignDurableResumeStatusOutcome.ServerUnavailable)]
    [InlineData(CopyDesignDurableResumeStatusOutcome.AuthenticationFailed)]
    [InlineData(CopyDesignDurableResumeStatusOutcome.MalformedResponse)]
    public void A_non_Found_status_outcome_fails_closed(CopyDesignDurableResumeStatusOutcome outcome)
    {
        var status = new CopyDesignDurableResumeStatusResult(outcome);

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, LiveShapeManifest(), _destinationFolder, LiveShapeDependencies());

        Assert.False(result.Success);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void A_wrong_or_unknown_operation_id_status_NotFound_fails_closed()
    {
        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.NotFound),
            LiveShapeManifest(), _destinationFolder, LiveShapeDependencies());

        Assert.False(result.Success);
    }

    [Fact]
    public void A_foreign_organizations_operation_reported_NotFound_by_the_server_fails_closed_identically()
    {
        // The server's own 404 semantics make "genuinely unknown" and
        // "exists in a different org" indistinguishable - both already
        // arrive here as NotFound, and both must fail closed the same way.
        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            new CopyDesignDurableResumeStatusResult(CopyDesignDurableResumeStatusOutcome.NotFound),
            LiveShapeManifest(), _destinationFolder, LiveShapeDependencies());

        Assert.False(result.Success);
        Assert.Contains("Could not obtain a trustworthy authoritative status", result.FailureReason);
    }

    [Fact]
    public void Missing_operation_id_or_idempotency_key_fails_closed()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, OperationId: null, IdempotencyKey: "key-1",
            Entries: Array.Empty<CopyDesignDurableResumeEntry>());

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, LiveShapeManifest(), _destinationFolder, LiveShapeDependencies());

        Assert.False(result.Success);
    }

    [Fact]
    public void Zero_entries_fails_closed()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1", Array.Empty<CopyDesignDurableResumeEntry>());

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, LiveShapeManifest(), _destinationFolder, LiveShapeDependencies());

        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\path")]
    public void An_invalid_destination_folder_fails_closed(string badFolder)
    {
        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            LiveShapeStatus(), LiveShapeManifest(), badFolder, LiveShapeDependencies());

        Assert.False(result.Success);
    }

    [Fact]
    public void A_malformed_operation_duplicate_ordinal_fails_closed()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(0, "COPY", "PENDING", "cad-a", "fv-a", "cad-a-new", "A", "A.ipt", "IPT", null),
                new CopyDesignDurableResumeEntry(0, "COPY", "PENDING", "cad-b", "fv-b", "cad-b-new", "B", "B.ipt", "IPT", null),
            });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"), ("cad-b", "fv-b", "B.ipt"));

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, manifest, _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());

        Assert.False(result.Success);
        Assert.Contains("more than one entry for ordinal", result.FailureReason);
    }

    [Fact]
    public void A_malformed_operation_gapped_ordinal_fails_closed()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(0, "COPY", "PENDING", "cad-a", "fv-a", "cad-a-new", "A", "A.ipt", "IPT", null),
                new CopyDesignDurableResumeEntry(2, "COPY", "PENDING", "cad-b", "fv-b", "cad-b-new", "B", "B.ipt", "IPT", null),
            });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"), ("cad-b", "fv-b", "B.ipt"));

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, manifest, _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());

        Assert.False(result.Success);
        Assert.Contains("missing ordinal", result.FailureReason);
    }

    // ---- fail closed: entry-level ---------------------------------------

    [Fact]
    public void An_INVALID_COPY_entry_fails_closed_operation_already_invalid()
    {
        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            LiveShapeStatus(idwState: "INVALID"), LiveShapeManifest(), _destinationFolder, LiveShapeDependencies());

        Assert.False(result.Success);
        Assert.Contains("INVALID", result.FailureReason);
    }

    [Fact]
    public void A_non_blank_original_description_fails_closed()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(0, "COPY", "PENDING", "cad-a", "fv-a", "cad-a-new", "A", "A.ipt", "IPT", "has a description"),
            });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, manifest, _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());

        Assert.False(result.Success);
        Assert.Contains("description", result.FailureReason);
    }

    [Fact]
    public void A_document_number_that_does_not_match_the_file_name_stem_fails_closed()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(0, "COPY", "PENDING", "cad-a", "fv-a", "cad-a-new", "MISMATCHED-NUMBER", "A.ipt", "IPT", null),
            });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, manifest, _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());

        Assert.False(result.Success);
        Assert.Contains("document number", result.FailureReason);
    }

    [Fact]
    public void An_unrecognized_documentType_fails_closed()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(0, "COPY", "PENDING", "cad-a", "fv-a", "cad-a-new", "A", "A.step", "STEP", null),
            });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.step"));

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, manifest, _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());

        Assert.False(result.Success);
    }

    // required: missing/wrong source workspace manifest --------------------

    [Fact]
    public void A_source_not_bound_in_the_workspace_manifest_fails_closed_missing_source_workspace()
    {
        var emptyManifest = SourceManifest(); // no entries at all - "wrong workspace"

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            LiveShapeStatus(), emptyManifest, _destinationFolder, LiveShapeDependencies());

        Assert.False(result.Success);
        Assert.Contains("does not have a bound entry", result.FailureReason);
    }

    [Fact]
    public void A_source_bound_to_a_DIFFERENT_FileVersion_than_the_operation_reserved_fails_closed()
    {
        // The local workspace has since moved on (e.g. a Get Latest pulled a
        // newer version) - the manifest binds cad-root-idw to a DIFFERENT
        // FileVersion than the operation was reserved against.
        var driftedManifest = SourceManifest(
            ("cad-root-iam", "fv-root-iam-src", "P6D-REAL-ROOT.iam"),
            ("cad-part-a", "fv-part-a-src", "P6D-REAL-PART-A.ipt"),
            ("cad-part-b", "fv-part-b-src", "P6D-REAL-PART-B.ipt"),
            ("cad-root-idw", "fv-root-idw-DRIFTED", "P6D-REAL-ROOT.idw"));

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            LiveShapeStatus(), driftedManifest, _destinationFolder, LiveShapeDependencies());

        Assert.False(result.Success);
        Assert.Contains("DIFFERENT FileVersion", result.FailureReason);
    }

    // required: missing reference target / unconfirmed drawing dependency --

    [Fact]
    public void A_drawing_with_no_confirmed_model_dependency_fails_closed_refuses_to_guess()
    {
        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            LiveShapeStatus(), LiveShapeManifest(), _destinationFolder,
            new Dictionary<string, IReadOnlySet<string>>()); // no dependency supplied at all

        Assert.False(result.Success);
        Assert.Contains("No authoritative model dependency", result.FailureReason);
    }

    [Fact]
    public void A_drawing_dependency_naming_a_model_outside_this_operation_fails_closed()
    {
        var deps = new Dictionary<string, IReadOnlySet<string>>
        {
            ["cad-root-idw"] = new HashSet<string> { "cad-some-other-model-not-in-this-operation" },
        };

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            LiveShapeStatus(), LiveShapeManifest(), _destinationFolder, deps);

        Assert.False(result.Success);
        Assert.Contains("not part of this operation", result.FailureReason);
    }

    // ---- REUSE shape (structural, even though the live operation has none) --

    [Fact]
    public void A_REUSE_entry_reconstructs_using_its_resultingCadDocumentId_as_identity()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(0, "REUSE", "REUSED", null, null, "cad-existing", "STD-1", "std.ipt", "IPT", null),
            });

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, LiveShapeManifest(), _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());

        Assert.True(result.Success, result.FailureReason);
        var node = Assert.Single(result.Plan!.Nodes);
        Assert.Equal(CopyDesignAction.Reuse, node.ProposedAction);
        Assert.Equal("cad-existing", node.CadDocumentId);
    }

    [Fact]
    public void A_REUSE_entry_with_an_inconsistent_state_fails_closed()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(0, "REUSE", "PENDING", null, null, "cad-existing", "STD-1", "std.ipt", "IPT", null),
            });

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, LiveShapeManifest(), _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());

        Assert.False(result.Success);
    }

    // ---- FindMissingMaterializedDestinations (P6D DURABLE RESUME LIVE
    //      BLOCKER fix) -----------------------------------------------------

    [Fact]
    public void FindMissingMaterializedDestinations_reports_nothing_when_every_materialized_file_exists()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(_destinationFolder, "P6DNEW-REAL-ROOT.iam"),
            Path.Combine(_destinationFolder, "P6DNEW-REAL-PART-A.ipt"),
            Path.Combine(_destinationFolder, "P6DNEW-REAL-PART-B.ipt"),
        };

        var missing = CopyDesignResumeAttemptReconstructor.FindMissingMaterializedDestinations(
            LiveShapeStatus(), _destinationFolder, present.Contains);

        Assert.Empty(missing);
    }

    [Fact]
    public void FindMissingMaterializedDestinations_reports_every_missing_materialized_file_not_just_the_first()
    {
        // Reproduces the exact live shape (cmuecs9bo0003yymo3roh70a9: ordinals
        // 0-2 MATERIALIZED, ordinal 3 PENDING) with a destination folder that
        // has NONE of the three materialized files - the wrong-folder case
        // that produced UnexpectedMaterializationState live.
        var missing = CopyDesignResumeAttemptReconstructor.FindMissingMaterializedDestinations(
            LiveShapeStatus(), _destinationFolder, _ => false);

        Assert.Equal(3, missing.Count);
        Assert.Contains(missing, m => m.OriginalFileName == "P6DNEW-REAL-ROOT.iam"
            && m.ExpectedPath == Path.Combine(_destinationFolder, "P6DNEW-REAL-ROOT.iam"));
        Assert.Contains(missing, m => m.OriginalFileName == "P6DNEW-REAL-PART-A.ipt");
        Assert.Contains(missing, m => m.OriginalFileName == "P6DNEW-REAL-PART-B.ipt");
        // The PENDING drawing entry is never "missing" - it isn't
        // materialized yet, so it is expected to not exist locally.
        Assert.DoesNotContain(missing, m => m.OriginalFileName == "P6DNEW-REAL-ROOT.idw");
    }

    [Fact]
    public void FindMissingMaterializedDestinations_reports_only_the_one_entry_that_is_actually_missing()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(_destinationFolder, "P6DNEW-REAL-ROOT.iam"),
            Path.Combine(_destinationFolder, "P6DNEW-REAL-PART-B.ipt"),
        };

        var missing = CopyDesignResumeAttemptReconstructor.FindMissingMaterializedDestinations(
            LiveShapeStatus(), _destinationFolder, present.Contains);

        var only = Assert.Single(missing);
        Assert.Equal("P6DNEW-REAL-PART-A.ipt", only.OriginalFileName);
        Assert.Equal(Path.Combine(_destinationFolder, "P6DNEW-REAL-PART-A.ipt"), only.ExpectedPath);
    }

    [Fact]
    public void FindMissingMaterializedDestinations_ignores_PENDING_and_REUSED_entries()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(0, "COPY", "PENDING", "cad-a", "fv-a", "cad-a-new", "A", "A.ipt", "IPT", null),
                new CopyDesignDurableResumeEntry(1, "REUSE", "REUSED", null, null, "cad-existing", "STD-1", "std.ipt", "IPT", null),
            });

        var missing = CopyDesignResumeAttemptReconstructor.FindMissingMaterializedDestinations(
            status, _destinationFolder, _ => false);

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\path")]
    public void FindMissingMaterializedDestinations_returns_empty_for_an_unusable_destination_folder_rather_than_throwing(string badFolder)
    {
        var missing = CopyDesignResumeAttemptReconstructor.FindMissingMaterializedDestinations(
            LiveShapeStatus(), badFolder, _ => false);

        Assert.Empty(missing);
    }

    // ---- P6D FINAL BLOCKER FIX: destination-escape hardening -------------
    // (malicious server-reported originalFileName) --------------------------

    // required test: malicious operation-status originalFileName fails
    // reconstruction / rooted path fails / traversal fails / separator-
    // containing filename fails / reserved name fails.
    [Theory]
    [InlineData("..\\P6D-EVIL.ipt")]
    [InlineData("../P6D-EVIL.ipt")]
    [InlineData("..\\..\\outside.iam")]
    [InlineData("C:\\Temp\\evil.ipt")]
    [InlineData("\\\\server\\share\\evil.idw")]
    [InlineData("subdir\\evil.ipt")]
    [InlineData("subdir/evil.ipt")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("CON.ipt")]
    [InlineData("con.ipt")]
    [InlineData("COM1.iam")]
    [InlineData("LPT9.idw")]
    public void A_malicious_originalFileName_fails_reconstruction_before_any_path_is_produced(string maliciousFileName)
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(
                    0, "COPY", "PENDING", "cad-a", "fv-a", "cad-a-new",
                    "IGNORED-STEM", maliciousFileName, "IPT", null),
            });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, manifest, _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());

        Assert.False(result.Success, $"expected originalFileName \"{maliciousFileName}\" to fail reconstruction");
        Assert.Null(result.Plan);
    }

    [Fact]
    public void An_ordinary_valid_originalFileName_still_reconstructs_successfully()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(
                    0, "COPY", "PENDING", "cad-a", "fv-a", "cad-a-new",
                    "A B-C_01", "A B-C_01.ipt", "IPT", null),
            });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, manifest, _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());

        Assert.True(result.Success, result.FailureReason);
        var node = Assert.Single(result.Plan!.Nodes);
        Assert.Equal(Path.Combine(_destinationFolder, "A B-C_01.ipt"), node.ProposedDestinationAbsolutePath);
    }

    // required test: resolved destination parent must equal selected root /
    // "C:\Approved" does not accept a path under "C:\Approved2" - proven at
    // the Reconstruct level (not just the underlying CopyDesignSafeBaseName
    // unit) by using a REAL, unambiguous absolute destination folder and
    // confirming the reconstructed node's path is contained exactly within
    // it, never in any sibling-looking folder.
    [Fact]
    public void The_reconstructed_destination_always_resolves_directly_inside_the_selected_root()
    {
        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(
                    0, "COPY", "PENDING", "cad-a", "fv-a", "cad-a-new",
                    "A", "A.ipt", "IPT", null),
            });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));

        var result = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, manifest, _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());

        Assert.True(result.Success, result.FailureReason);
        var node = Assert.Single(result.Plan!.Nodes);
        Assert.Equal(
            Path.GetFullPath(_destinationFolder),
            Path.GetDirectoryName(Path.GetFullPath(node.ProposedDestinationAbsolutePath!)));
    }
}
