using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

// ------------------------------------------------------------
// Fakes (self-contained to this test file - deliberately NOT shared with
// CopyDesignApplyOrchestratorFakes.cs's FakeVerifier/FakeHasher, which are
// shaped for APPLY-time scenarios; these need finer per-test control over
// the ACTUAL occurrence set, to exercise stale/unplanned-reference cases).
// ------------------------------------------------------------

internal sealed class FakeVerificationInventorAdapter : ICopyDesignVerifier
{
    public List<string> Calls { get; } = new();
    public HashSet<string> ThrowFor { get; } = new(StringComparer.Ordinal);
    /// <summary>Keyed by CadDocumentId - full control over the returned
    ///  facts for a specific node, used to engineer stale/unplanned/missing
    ///  reference scenarios. Falls back to a clean "everything resolves
    ///  exactly as expected" default when absent.</summary>
    public Dictionary<string, Func<CopyDesignNode, IReadOnlyList<CopyDesignReferenceTarget>, string, CopyDesignNodeVerificationFacts>> FactsOverride { get; } =
        new(StringComparer.Ordinal);

    public Task<CopyDesignNodeVerificationFacts> GatherFactsAsync(
        CopyDesignNode node, IReadOnlyList<CopyDesignReferenceTarget> referenceTargets, string sourceSha256BeforeOperation, CancellationToken ct)
    {
        Calls.Add(node.CadDocumentId!);
        if (ThrowFor.Contains(node.CadDocumentId!))
        {
            throw new InvalidOperationException("simulated Inventor failure");
        }
        if (FactsOverride.TryGetValue(node.CadDocumentId!, out var factory))
        {
            return Task.FromResult(factory(node, referenceTargets, sourceSha256BeforeOperation));
        }
        var refs = referenceTargets.Select(t => new CopyDesignReferenceResolutionFact(
            t.ChildCadDocumentId, t.ExpectedTargetAbsolutePath, t.OriginalChildAbsolutePath, t.ExpectedTargetAbsolutePath, t.ChildIsCopy)).ToArray();
        var actual = referenceTargets.Select(t => new CopyDesignActualComponentOccurrence(t.ExpectedTargetAbsolutePath)).ToArray();
        return Task.FromResult(new CopyDesignNodeVerificationFacts(
            node.CadDocumentId!, node.SourceAbsolutePath, node.ProposedDestinationAbsolutePath ?? "",
            DestinationExists: true, DocumentTypeMatches: true, OpenableThroughInventor: true,
            ResultingSha256: "irrelevant-not-checked-by-evaluator-authoritatively", ResultingFileSize: 1L,
            SourceSha256BeforeOperation: sourceSha256BeforeOperation, SourceSha256AfterOperation: sourceSha256BeforeOperation,
            ReferenceResolutions: refs, ActualComponentOccurrences: actual, OccurrenceEnumerationSucceeded: true));
    }
}

internal sealed class FakeVerificationHasher : ICopyDesignFileHasher
{
    public Dictionary<string, string> HashFor { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ThrowFor { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = new();

    public string ComputeSha256(string absolutePath)
    {
        Calls.Add(absolutePath);
        if (ThrowFor.Contains(absolutePath))
        {
            throw new IOException("simulated unreadable file");
        }
        return HashFor.TryGetValue(absolutePath, out var hash) ? hash : "default:" + absolutePath.ToLowerInvariant();
    }
}

/// <summary>P6E-B: comprehensive tests for
///  <see cref="CopyDesignVerificationOrchestrator"/> - the full engine,
///  including the nested 7-entry and mixed COPY/REUSE/drawing scenarios.
///  All fakes; no COM, no HTTP, no mutation of any kind.</summary>
public class CopyDesignVerificationOrchestratorTests : IDisposable
{
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), "arch-cc-verify-orch-src-" + Guid.NewGuid().ToString("N"));
    private readonly string _destinationFolder = Path.Combine(Path.GetTempPath(), "arch-cc-verify-orch-dst-" + Guid.NewGuid().ToString("N"));
    private static readonly string CanonicalA = "a1" + new string('1', 62);
    private static readonly string CanonicalB = "b2" + new string('2', 62);
    private static readonly string CanonicalC = "c3" + new string('3', 62);

    public CopyDesignVerificationOrchestratorTests()
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

    private static CopyDesignDurableResumeEntry MaterializedCopyEntry(
        int ordinal, string sourceId, string sourceFvId, string resultingId,
        string fileName, string docType, string fvId, string sha256, long fileSize) =>
        CopyEntry(ordinal, sourceId, sourceFvId, resultingId, fileName, docType, "MATERIALIZED", fvId, 1, sha256, fileSize);

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

    private void WriteFile(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private CopyDesignVerificationOrchestrator BuildOrchestrator(
        FakeVerificationInventorAdapter? verifier = null,
        FakeVerificationInventorAdapter? drawingVerifier = null,
        FakeVerificationHasher? hasher = null) =>
        new(verifier ?? new FakeVerificationInventorAdapter(), hasher ?? new FakeVerificationHasher(),
            localFileExists: File.Exists, drawingVerifier: drawingVerifier, getFileSize: p => new FileInfo(p).Length);

    // ---- structural failures propagate cleanly (P6E-B/D FINAL SEMANTIC
    //      ALIGNMENT: ALWAYS INCOMPLETE, never FAILED - "verification cannot
    //      legitimately conclude", never "the copied package was proven
    //      wrong") --------------------------------------------------------

    [Fact]
    public async Task A_structurally_broken_topology_produces_a_top_level_INCOMPLETE_result_with_empty_entries()
    {
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(new[]
        {
            CopyEntry(0, "cad-a", "fv-a", "cad-a-new", "A-NEW.ipt", "IPT"),
            CopyEntry(0, "cad-a", "fv-a", "cad-a-new2", "A-NEW2.ipt", "IPT"), // duplicate ordinal
        });
        var verifier = new FakeVerificationInventorAdapter();
        var orchestrator = BuildOrchestrator(verifier: verifier);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, result.Outcome);
        Assert.Empty(result.Entries);
        Assert.NotNull(result.FailureReason);
        // required test 9: a structural (whole-topology) failure performs
        // ZERO COM verification calls - GatherFactsAsync is never reached.
        Assert.Empty(verifier.Calls);
    }

    [Fact]
    public async Task Malformed_authoritative_reconstruction_data_an_entry_whose_source_id_the_workspace_never_bound_is_INCOMPLETE()
    {
        // required test 2: "malformed authoritative evidence" - a COPY
        // entry's claimed sourceCadDocumentId has NO binding at all in the
        // local source workspace manifest, so the topology cannot be
        // reconstructed - this is a DIFFERENT whole-build gate than the
        // duplicate-ordinal case above, proving the alignment is not
        // specific to just one trigger.
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt")); // binds only cad-a
        var support = Support(new[] { CopyEntry(0, "cad-ghost", "fv-ghost", "cad-new", "GHOST-NEW.ipt", "IPT") });
        var verifier = new FakeVerificationInventorAdapter();
        var orchestrator = BuildOrchestrator(verifier: verifier);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, result.Outcome);
        Assert.Empty(result.Entries);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(verifier.Calls);
    }

    [Fact]
    public async Task A_drawing_present_without_a_configured_drawingVerifier_is_a_structural_INCOMPLETE()
    {
        var manifest = SourceManifest(("cad-idw", "fv-idw", "M.idw"));
        var support = Support(new[] { CopyEntry(0, "cad-idw", "fv-idw", "cad-idw-new", "M-NEW.idw", "IDW") });
        var orchestrator = BuildOrchestrator(drawingVerifier: null);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, result.Outcome);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void StructuralFailure_never_produces_VERIFIED_and_always_INCOMPLETE()
    {
        // required test 10: no VERIFIED can ever result from a structural
        // reconstruction failure - asserted directly against the shared
        // Core factory both the orchestrator and P6E-D's controller
        // pre-check construct their result through.
        var result = CopyDesignOperationVerificationResult.StructuralFailure("any reason", "op-9");

        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, result.Outcome);
        Assert.NotEqual(CopyDesignVerificationOutcome.Verified, result.Outcome);
        Assert.NotEqual(CopyDesignVerificationOutcome.Failed, result.Outcome);
        Assert.Equal("op-9", result.CopyDesignOperationId);
        Assert.Equal("any reason", result.FailureReason);
        Assert.Empty(result.Entries);
    }

    // ---- MaterializationState -----------------------------------------------

    [Fact]
    public async Task PENDING_state_is_NotProvable_and_rolls_up_to_INCOMPLETE()
    {
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(new[] { CopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT", "PENDING") });
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, result.Outcome);
        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "MaterializationState");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, check.State);
    }

    [Fact]
    public async Task INVALID_state_is_NotProvable_and_rolls_up_to_INCOMPLETE()
    {
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(new[] { CopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT", "INVALID") });
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, result.Outcome);
        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "MaterializationState");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, check.State);
    }

    [Fact]
    public async Task MATERIALIZED_with_non_canonical_evidence_on_the_entry_itself_is_FAILED()
    {
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(new[] { CopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT", "MATERIALIZED", "fv-new", 1, "NOT-CANONICAL", 10) });
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Failed, result.Outcome);
        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "MaterializationState");
        Assert.Equal(CopyDesignVerificationCheckState.Failed, check.State);
    }

    // ---- SourceIntegrity ------------------------------------------------

    [Fact]
    public async Task SourceIntegrity_Available_and_matching_is_PASSED()
    {
        var sourcePath = Path.Combine(_sourceRoot, "A.ipt");
        WriteFile(sourcePath, new byte[] { 1 });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(
            new[] { CopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT") },
            sourceIntegrity: new[] { new CopyDesignSourceIntegrityEvidence(0, "cad-a", "fv-a", CopyDesignSourceIntegrityState.Available, CanonicalA, 1) });
        var hasher = new FakeVerificationHasher();
        hasher.HashFor[sourcePath] = CanonicalA;
        var orchestrator = BuildOrchestrator(hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "SourceIntegrity");
        Assert.Equal(CopyDesignVerificationCheckState.Passed, check.State);
    }

    [Fact]
    public async Task SourceIntegrity_Available_but_local_bytes_differ_is_FAILED()
    {
        var sourcePath = Path.Combine(_sourceRoot, "A.ipt");
        WriteFile(sourcePath, new byte[] { 1 });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(
            new[] { CopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT") },
            sourceIntegrity: new[] { new CopyDesignSourceIntegrityEvidence(0, "cad-a", "fv-a", CopyDesignSourceIntegrityState.Available, CanonicalA, 1) });
        var hasher = new FakeVerificationHasher();
        hasher.HashFor[sourcePath] = CanonicalB; // mismatched
        var orchestrator = BuildOrchestrator(hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Failed, result.Outcome);
        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "SourceIntegrity");
        Assert.Equal(CopyDesignVerificationCheckState.Failed, check.State);
    }

    [Fact]
    public async Task SourceIntegrity_Unavailable_is_NotProvable()
    {
        var sourcePath = Path.Combine(_sourceRoot, "A.ipt");
        WriteFile(sourcePath, new byte[] { 1 });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(
            new[] { CopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT") },
            sourceIntegrity: new[] { new CopyDesignSourceIntegrityEvidence(0, "cad-a", "fv-a", CopyDesignSourceIntegrityState.Unavailable) });
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "SourceIntegrity");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, check.State);
    }

    [Fact]
    public async Task SourceIntegrity_Invalid_is_NotProvable()
    {
        var sourcePath = Path.Combine(_sourceRoot, "A.ipt");
        WriteFile(sourcePath, new byte[] { 1 });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(
            new[] { CopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT") },
            sourceIntegrity: new[] { new CopyDesignSourceIntegrityEvidence(0, "cad-a", "fv-a", CopyDesignSourceIntegrityState.Invalid, Reason: "mismatched ownership") });
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "SourceIntegrity");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, check.State);
        Assert.Contains("mismatched ownership", check.Detail);
    }

    [Fact]
    public async Task SourceIntegrity_with_no_evidence_record_at_all_is_NotProvable()
    {
        var sourcePath = Path.Combine(_sourceRoot, "A.ipt");
        WriteFile(sourcePath, new byte[] { 1 });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(new[] { CopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT") }); // no integrity at all
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "SourceIntegrity");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, check.State);
    }

    [Fact]
    public async Task SourceIntegrity_Available_but_local_source_file_missing_is_NotProvable_not_Failed()
    {
        // Deliberately never written to disk.
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(
            new[] { CopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT") },
            sourceIntegrity: new[] { new CopyDesignSourceIntegrityEvidence(0, "cad-a", "fv-a", CopyDesignSourceIntegrityState.Available, CanonicalA, 1) });
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "SourceIntegrity");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, check.State);
    }

    // ---- DestinationIntegrity ------------------------------------------

    [Fact]
    public async Task DestinationIntegrity_matching_is_PASSED()
    {
        var sourcePath = Path.Combine(_sourceRoot, "A.ipt");
        WriteFile(sourcePath, new byte[] { 1 });
        var destPath = Path.Combine(_destinationFolder, "A-NEW.ipt");
        WriteFile(destPath, new byte[] { 2 });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(new[] { MaterializedCopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT", "fv-new", CanonicalB, 1) });
        var hasher = new FakeVerificationHasher();
        hasher.HashFor[destPath] = CanonicalB;
        var orchestrator = BuildOrchestrator(hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "DestinationIntegrity");
        Assert.Equal(CopyDesignVerificationCheckState.Passed, check.State);
    }

    [Fact]
    public async Task DestinationIntegrity_missing_file_is_FAILED()
    {
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(new[] { MaterializedCopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT", "fv-new", CanonicalB, 1) });
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Failed, result.Outcome);
        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "DestinationIntegrity");
        Assert.Equal(CopyDesignVerificationCheckState.Failed, check.State);
    }

    [Fact]
    public async Task DestinationIntegrity_hash_mismatch_is_FAILED()
    {
        var destPath = Path.Combine(_destinationFolder, "A-NEW.ipt");
        WriteFile(destPath, new byte[] { 2 });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(new[] { MaterializedCopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT", "fv-new", CanonicalB, 1) });
        var hasher = new FakeVerificationHasher();
        hasher.HashFor[destPath] = CanonicalA; // does not match entry's authoritative CanonicalB
        var orchestrator = BuildOrchestrator(hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Failed, result.Outcome);
        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "DestinationIntegrity");
        Assert.Equal(CopyDesignVerificationCheckState.Failed, check.State);
    }

    [Fact]
    public async Task DestinationIntegrity_size_mismatch_is_FAILED()
    {
        // required test 6: a safely-resolved destination whose HASH matches
        // but whose SIZE does not is still a concrete, proven FAILED defect
        // - distinct from the DestinationResolution/INCOMPLETE cases above.
        var destPath = Path.Combine(_destinationFolder, "A-NEW.ipt");
        WriteFile(destPath, new byte[] { 2, 2 }); // actual 2 bytes
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(new[] { MaterializedCopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT", "fv-new", CanonicalB, 1) }); // entry claims fileSize 1
        var hasher = new FakeVerificationHasher();
        hasher.HashFor[destPath] = CanonicalB; // hash matches; size does not
        var orchestrator = BuildOrchestrator(hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Failed, result.Outcome);
        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "DestinationIntegrity");
        Assert.Equal(CopyDesignVerificationCheckState.Failed, check.State);
    }

    // ---- P6E FINAL SEMANTIC CLEANUP: unsafe/uncontained destination name -
    //      REQUIRED DestinationResolution NotProvable (INCOMPLETE), NEVER a
    //      FAILED DestinationIntegrity - "cannot safely establish the
    //      expected verification target" is categorically different from
    //      "proven wrong", and no filesystem/COM check is ever attempted. ---

    [Theory]
    [InlineData("..\\EVIL.ipt")] // required test 2: traversal (path separator)
    [InlineData("CON.ipt")] // required test 1: unsafe basename (reserved device name)
    [InlineData("C:evil.ipt")] // required test 2: rooted (drive prefix)
    public async Task An_unsafe_or_uncontained_destination_name_produces_DestinationResolution_NotProvable_INCOMPLETE_not_Failed(string unsafeFileName)
    {
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(new[] { MaterializedCopyEntry(0, "cad-a", "fv-a", "cad-new", unsafeFileName, "IPT", "fv-new", CanonicalB, 1) });
        var verifier = new FakeVerificationInventorAdapter();
        var orchestrator = BuildOrchestrator(verifier: verifier);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, result.Outcome);
        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "DestinationResolution");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, check.State);
        Assert.Equal(CopyDesignVerificationCheckRequirement.Required, check.Requirement);
        Assert.DoesNotContain(result.Entries[0].Checks, c => c.Name == "DestinationIntegrity");
    }

    [Fact]
    public async Task An_unsafe_destination_name_for_an_IAM_performs_ZERO_filesystem_or_COM_destination_verification()
    {
        // required test 3: an IAM would normally ALWAYS attempt
        // ReferenceTopology (a COM call through the verifier) - proving
        // verifier.Calls stays empty here shows the unsafe-name short-circuit
        // genuinely skips it, not merely "an IPT has nothing to check".
        var manifest = SourceManifest(("cad-main", "fv-main", "MAIN.iam"));
        var support = Support(new[] { MaterializedCopyEntry(0, "cad-main", "fv-main", "cad-main-new", "..\\EVIL.iam", "IAM", "fvm", CanonicalA, 1) });
        var verifier = new FakeVerificationInventorAdapter();
        var orchestrator = BuildOrchestrator(verifier: verifier);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, result.Outcome);
        var resolutionCheck = Assert.Single(result.Entries[0].Checks, c => c.Name == "DestinationResolution");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, resolutionCheck.State);
        var topologyCheck = Assert.Single(result.Entries[0].Checks, c => c.Name == "ReferenceTopology");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, topologyCheck.State);
        Assert.Empty(verifier.Calls);
    }

    [Fact]
    public async Task One_entry_unsafe_INCOMPLETE_plus_another_entry_with_a_concrete_defect_FAILED_makes_the_whole_operation_FAILED()
    {
        // required test 7: FAILED still dominates globally even when a
        // SIBLING entry is only INCOMPLETE due to an unresolvable destination.
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"), ("cad-b", "fv-b", "B.ipt"));
        var support = Support(new[]
        {
            MaterializedCopyEntry(0, "cad-a", "fv-a", "cad-a-new", "..\\EVIL.ipt", "IPT", "fva", CanonicalA, 1), // unsafe -> Incomplete
            MaterializedCopyEntry(1, "cad-b", "fv-b", "cad-b-new", "B-NEW.ipt", "IPT", "fvb", CanonicalB, 1), // destination missing -> Failed
        });
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Failed, result.Outcome);
        var entryA = result.Entries.Single(e => e.ResultingCadDocumentId == "cad-a-new");
        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, entryA.Outcome);
        var entryB = result.Entries.Single(e => e.ResultingCadDocumentId == "cad-b-new");
        Assert.Equal(CopyDesignVerificationOutcome.Failed, entryB.Outcome);
    }

    [Fact]
    public async Task An_unsafe_destination_name_can_never_roll_up_VERIFIED()
    {
        // required test 8.
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(new[] { MaterializedCopyEntry(0, "cad-a", "fv-a", "cad-new", "..\\EVIL.ipt", "IPT", "fv-new", CanonicalB, 1) });
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.NotEqual(CopyDesignVerificationOutcome.Verified, result.Outcome);
        Assert.NotEqual(CopyDesignVerificationOutcome.Verified, result.Entries[0].Outcome);
    }

    // ---- ReferenceTopology ------------------------------------------------

    [Fact]
    public async Task ReferenceTopology_is_skipped_entirely_for_a_leaf_IPT()
    {
        var destPath = Path.Combine(_destinationFolder, "A-NEW.ipt");
        WriteFile(destPath, new byte[] { 2 });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(new[] { MaterializedCopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT", "fv-new", CanonicalB, 1) });
        var hasher = new FakeVerificationHasher();
        hasher.HashFor[destPath] = CanonicalB;
        var verifier = new FakeVerificationInventorAdapter();
        var orchestrator = BuildOrchestrator(verifier: verifier, hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.DoesNotContain(result.Entries[0].Checks, c => c.Name == "ReferenceTopology");
        Assert.Empty(verifier.Calls); // Inventor never even consulted for an IPT
    }

    [Fact]
    public async Task ReferenceTopology_clean_IAM_with_a_COPY_child_is_PASSED()
    {
        var mainDest = Path.Combine(_destinationFolder, "MAIN-NEW.iam");
        var aDest = Path.Combine(_destinationFolder, "A-NEW.ipt");
        WriteFile(Path.Combine(_sourceRoot, "MAIN.iam"), new byte[] { 9 });
        WriteFile(Path.Combine(_sourceRoot, "A.ipt"), new byte[] { 8 });
        WriteFile(mainDest, new byte[] { 1 });
        WriteFile(aDest, new byte[] { 2 });
        var manifest = SourceManifest(("cad-main", "fv-main", "MAIN.iam"), ("cad-a", "fv-a", "A.ipt"));
        var support = Support(
            new[]
            {
                MaterializedCopyEntry(0, "cad-main", "fv-main", "cad-main-new", "MAIN-NEW.iam", "IAM", "fvm", CanonicalA, 1),
                MaterializedCopyEntry(1, "cad-a", "fv-a", "cad-a-new", "A-NEW.ipt", "IPT", "fva", CanonicalB, 1),
            },
            componentEdges: new[] { new CopyDesignComponentEdge("cad-main", "cad-a") });
        var hasher = new FakeVerificationHasher();
        hasher.HashFor[mainDest] = CanonicalA;
        hasher.HashFor[aDest] = CanonicalB;
        var orchestrator = BuildOrchestrator(hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var mainResult = result.Entries.Single(e => e.ResultingCadDocumentId == "cad-main-new");
        var check = Assert.Single(mainResult.Checks, c => c.Name == "ReferenceTopology");
        Assert.Equal(CopyDesignVerificationCheckState.Passed, check.State);
    }

    [Fact]
    public async Task ReferenceTopology_with_a_stale_reference_reported_by_the_evaluator_is_FAILED()
    {
        var mainDest = Path.Combine(_destinationFolder, "MAIN-NEW.iam");
        var aDest = Path.Combine(_destinationFolder, "A-NEW.ipt");
        WriteFile(Path.Combine(_sourceRoot, "MAIN.iam"), new byte[] { 9 });
        WriteFile(Path.Combine(_sourceRoot, "A.ipt"), new byte[] { 8 });
        WriteFile(mainDest, new byte[] { 1 });
        WriteFile(aDest, new byte[] { 2 });
        var manifest = SourceManifest(("cad-main", "fv-main", "MAIN.iam"), ("cad-a", "fv-a", "A.ipt"));
        var support = Support(
            new[]
            {
                MaterializedCopyEntry(0, "cad-main", "fv-main", "cad-main-new", "MAIN-NEW.iam", "IAM", "fvm", CanonicalA, 1),
                MaterializedCopyEntry(1, "cad-a", "fv-a", "cad-a-new", "A-NEW.ipt", "IPT", "fva", CanonicalB, 2),
            },
            componentEdges: new[] { new CopyDesignComponentEdge("cad-main", "cad-a") });
        var hasher = new FakeVerificationHasher();
        hasher.HashFor[mainDest] = CanonicalA;
        hasher.HashFor[aDest] = CanonicalB;

        var verifier = new FakeVerificationInventorAdapter();
        verifier.FactsOverride["cad-main-new"] = (node, targets, before) =>
        {
            var target = targets.Single();
            // ACTUAL occurrence still points at the child's ORIGINAL
            // (pre-copy) path - a classic stale reference.
            var actual = new[] { new CopyDesignActualComponentOccurrence(target.OriginalChildAbsolutePath) };
            var refs = new[] { new CopyDesignReferenceResolutionFact(target.ChildCadDocumentId, target.ExpectedTargetAbsolutePath, target.OriginalChildAbsolutePath, target.OriginalChildAbsolutePath, target.ChildIsCopy) };
            return new CopyDesignNodeVerificationFacts(
                node.CadDocumentId!, node.SourceAbsolutePath, node.ProposedDestinationAbsolutePath!,
                true, true, true, "irrelevant", 1L, before, before, refs, actual, true);
        };
        var orchestrator = BuildOrchestrator(verifier: verifier, hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var mainResult = result.Entries.Single(e => e.ResultingCadDocumentId == "cad-main-new");
        var check = Assert.Single(mainResult.Checks, c => c.Name == "ReferenceTopology");
        Assert.Equal(CopyDesignVerificationCheckState.Failed, check.State);
        Assert.Contains("stale", check.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CopyDesignVerificationOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ReferenceTopology_for_a_drawing_with_no_confirmed_authority_is_NotProvable()
    {
        var idwDest = Path.Combine(_destinationFolder, "M-NEW.idw");
        WriteFile(idwDest, new byte[] { 1 });
        var manifest = SourceManifest(("cad-idw", "fv-idw", "M.idw"));
        var support = Support(new[] { MaterializedCopyEntry(0, "cad-idw", "fv-idw", "cad-idw-new", "M-NEW.idw", "IDW", "fvi", CanonicalA, 1) });
        var hasher = new FakeVerificationHasher();
        hasher.HashFor[idwDest] = CanonicalA;
        var orchestrator = BuildOrchestrator(drawingVerifier: new FakeVerificationInventorAdapter(), hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings); // NoDrawings = no key for cad-idw

        // required test 3: missing drawing authority -> INCOMPLETE (this is
        // an ENTRY-level NotProvable Required check, not a whole-topology
        // structural failure - the topology itself builds fine; only this
        // one drawing's reference-topology verdict is unprovable).
        var check = Assert.Single(result.Entries[0].Checks, c => c.Name == "ReferenceTopology");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, check.State);
        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, result.Outcome);
        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, result.Outcome);
    }

    // ---- REUSE --------------------------------------------------------------

    // required test 5: REUSE result still visibly reports historical byte
    // immutability NOT_PROVABLE, and required test 2/4: an INFORMATIONAL
    // NotProvable check alone does NOT force INCOMPLETE - a clean,
    // standalone REUSE entry (identity confirmed, no children to verify)
    // now reaches VERIFIED, with the informational note still present.
    [Fact]
    public async Task A_clean_REUSE_entry_reaches_VERIFIED_while_still_truthfully_reporting_SourceByteImmutability_as_an_INFORMATIONAL_NotProvable_check()
    {
        WriteFile(Path.Combine(_sourceRoot, "STD.ipt"), new byte[] { 1 });
        var manifest = SourceManifest(("cad-reused", "fv-reused", "STD.ipt"));
        var support = Support(new[] { ReuseEntry(0, "cad-reused", "STD.ipt", "IPT") });
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var entry = Assert.Single(result.Entries);
        var identityCheck = Assert.Single(entry.Checks, c => c.Name == "IntendedReuseIdentity");
        Assert.Equal(CopyDesignVerificationCheckState.Passed, identityCheck.State);
        Assert.Equal(CopyDesignVerificationCheckRequirement.Required, identityCheck.Requirement);

        var immutabilityCheck = Assert.Single(entry.Checks, c => c.Name == "SourceByteImmutability");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, immutabilityCheck.State);
        Assert.Equal(CopyDesignVerificationCheckRequirement.Informational, immutabilityCheck.Requirement);
        Assert.Contains("P6D does not freeze a historical FileVersion baseline for REUSE", immutabilityCheck.Detail);

        // required test 4: correct COPY + (here, alone) REUSE -> VERIFIED,
        // even though the informational limitation is still visibly present.
        Assert.Equal(CopyDesignVerificationOutcome.Verified, entry.Outcome);
        Assert.Equal(CopyDesignVerificationOutcome.Verified, result.Outcome);
    }

    // required test 1: a REQUIRED NotProvable check still forces INCOMPLETE
    // (here: IntendedReuseIdentity is Required and fails closed on a
    // missing local file - NOT the informational immutability check).
    [Fact]
    public async Task A_REUSE_entry_whose_local_file_is_missing_is_FAILED_via_the_REQUIRED_IntendedReuseIdentity_check()
    {
        // Deliberately never written to disk.
        var manifest = SourceManifest(("cad-reused", "fv-reused", "STD.ipt"));
        var support = Support(new[] { ReuseEntry(0, "cad-reused", "STD.ipt", "IPT") });
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var entry = Assert.Single(result.Entries);
        var identityCheck = Assert.Single(entry.Checks, c => c.Name == "IntendedReuseIdentity");
        Assert.Equal(CopyDesignVerificationCheckState.Failed, identityCheck.State);
        Assert.Equal(CopyDesignVerificationOutcome.Failed, entry.Outcome);
        Assert.Equal(CopyDesignVerificationOutcome.Failed, result.Outcome);
    }

    // required test 1: a REQUIRED NotProvable check (here, a REUSE'd
    // DRAWING's own ReferenceTopology - a model-dependency authority that
    // was never confirmed, the ONLY way ReferenceTopology itself can be
    // NotProvable rather than Failed - see CopyDesignVerificationTopologyBuilder's
    // "missing key vs. present-but-empty" contract) still forces INCOMPLETE,
    // exactly like a COPY entry's.
    [Fact]
    public async Task A_REUSE_drawing_with_an_unconfirmed_model_authority_is_REQUIRED_NotProvable_and_forces_INCOMPLETE()
    {
        WriteFile(Path.Combine(_sourceRoot, "REUSED.idw"), new byte[] { 1 });
        WriteFile(Path.Combine(_sourceRoot, "MODEL.iam"), new byte[] { 1 });
        var manifest = SourceManifest(("cad-reused-idw", "fv-idw", "REUSED.idw"), ("cad-model", "fv-model", "MODEL.iam"));
        var support = Support(new[]
        {
            ReuseEntry(0, "cad-reused-idw", "REUSED.idw", "IDW"),
            ReuseEntry(1, "cad-model", "MODEL.iam", "IAM"),
        });
        // NoDrawings: "cad-reused-idw" has NO key at all in the authority
        // map - unconfirmed, never treated as "authoritatively zero".
        var orchestrator = BuildOrchestrator(drawingVerifier: new FakeVerificationInventorAdapter());

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var idwEntry = result.Entries.Single(e => e.ResultingCadDocumentId == "cad-reused-idw");
        var topologyCheck = Assert.Single(idwEntry.Checks, c => c.Name == "ReferenceTopology");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, topologyCheck.State);
        Assert.Equal(CopyDesignVerificationCheckRequirement.Required, topologyCheck.Requirement);
        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, idwEntry.Outcome);
        Assert.Equal(CopyDesignVerificationOutcome.Incomplete, result.Outcome);
    }

    // required test 6: a wrong REUSE reference identity (the PARENT
    // actually resolves somewhere else entirely) -> FAILED.
    [Fact]
    public async Task A_wrong_REUSE_reference_identity_observed_by_Inventor_is_FAILED()
    {
        WriteFile(Path.Combine(_sourceRoot, "MAIN.iam"), new byte[] { 1 });
        WriteFile(Path.Combine(_sourceRoot, "SUBB.iam"), new byte[] { 1 });
        var mainDest = Path.Combine(_destinationFolder, "MAIN-NEW.iam");
        WriteFile(mainDest, new byte[] { 1 });
        var manifest = SourceManifest(("cad-main", "fv-main", "MAIN.iam"), ("cad-subb", "fv-subb", "SUBB.iam"));
        var support = Support(
            new[]
            {
                MaterializedCopyEntry(0, "cad-main", "fv-main", "cad-main-new", "MAIN-NEW.iam", "IAM", "fvm", CanonicalA, 1),
                ReuseEntry(1, "cad-subb", "SUBB.iam", "IAM"),
            },
            componentEdges: new[] { new CopyDesignComponentEdge("cad-main", "cad-subb") });
        var hasher = new FakeVerificationHasher();
        hasher.HashFor[mainDest] = CanonicalA;

        var verifier = new FakeVerificationInventorAdapter();
        verifier.FactsOverride["cad-main-new"] = (node, targets, before) =>
        {
            // MAIN actually resolves its SUBB reference somewhere totally
            // unplanned - not SUBB's original path at all.
            var actual = new[] { new CopyDesignActualComponentOccurrence(@"C:\somewhere\else\WRONG.iam") };
            var refs = targets.Select(t => new CopyDesignReferenceResolutionFact(t.ChildCadDocumentId, t.ExpectedTargetAbsolutePath, t.OriginalChildAbsolutePath, null, t.ChildIsCopy)).ToArray();
            return new CopyDesignNodeVerificationFacts(
                node.CadDocumentId!, node.SourceAbsolutePath, node.ProposedDestinationAbsolutePath!,
                true, true, true, "irrelevant", 1L, before, before, refs, actual, true);
        };
        var orchestrator = BuildOrchestrator(verifier: verifier, hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var mainResult = result.Entries.Single(e => e.ResultingCadDocumentId == "cad-main-new");
        var check = Assert.Single(mainResult.Checks, c => c.Name == "ReferenceTopology");
        Assert.Equal(CopyDesignVerificationCheckState.Failed, check.State);
        Assert.Equal(CopyDesignVerificationOutcome.Failed, result.Outcome);
    }

    // required test 7: a stale reference involving a REUSE child (the
    // PARENT's occurrence still points at the REUSE child's path, but the
    // evaluator's own "stale" classification is keyed by the ORIGINAL path
    // matching the EXPECTED path for a REUSE child - so this proves an
    // actively WRONG resolution for a REUSE child is still caught).
    [Fact]
    public async Task A_REUSE_child_that_fails_to_resolve_at_all_is_FAILED_missing_expected_reference()
    {
        WriteFile(Path.Combine(_sourceRoot, "MAIN.iam"), new byte[] { 1 });
        WriteFile(Path.Combine(_sourceRoot, "SUBB.iam"), new byte[] { 1 });
        var mainDest = Path.Combine(_destinationFolder, "MAIN-NEW.iam");
        WriteFile(mainDest, new byte[] { 1 });
        var manifest = SourceManifest(("cad-main", "fv-main", "MAIN.iam"), ("cad-subb", "fv-subb", "SUBB.iam"));
        var support = Support(
            new[]
            {
                MaterializedCopyEntry(0, "cad-main", "fv-main", "cad-main-new", "MAIN-NEW.iam", "IAM", "fvm", CanonicalA, 1),
                ReuseEntry(1, "cad-subb", "SUBB.iam", "IAM"),
            },
            componentEdges: new[] { new CopyDesignComponentEdge("cad-main", "cad-subb") });
        var hasher = new FakeVerificationHasher();
        hasher.HashFor[mainDest] = CanonicalA;

        var verifier = new FakeVerificationInventorAdapter();
        verifier.FactsOverride["cad-main-new"] = (node, targets, before) =>
            // MAIN has NO occurrences at all - the expected REUSE reference
            // to SUBB is simply missing.
            new CopyDesignNodeVerificationFacts(
                node.CadDocumentId!, node.SourceAbsolutePath, node.ProposedDestinationAbsolutePath!,
                true, true, true, "irrelevant", 1L, before, before,
                targets.Select(t => new CopyDesignReferenceResolutionFact(t.ChildCadDocumentId, t.ExpectedTargetAbsolutePath, t.OriginalChildAbsolutePath, null, t.ChildIsCopy)).ToArray(),
                Array.Empty<CopyDesignActualComponentOccurrence>(), true);
        var orchestrator = BuildOrchestrator(verifier: verifier, hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        var mainResult = result.Entries.Single(e => e.ResultingCadDocumentId == "cad-main-new");
        var check = Assert.Single(mainResult.Checks, c => c.Name == "ReferenceTopology");
        Assert.Equal(CopyDesignVerificationCheckState.Failed, check.State);
        Assert.Contains("REUSE child", check.Detail);
        Assert.Equal(CopyDesignVerificationOutcome.Failed, result.Outcome);
    }

    // ---- rollup priority: FAILED beats INCOMPLETE beats VERIFIED -----------

    [Fact]
    public async Task Operation_rollup_FAILED_takes_priority_over_an_otherwise_INCOMPLETE_entry()
    {
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"), ("cad-b", "fv-b", "B.ipt"));
        var support = Support(new[]
        {
            CopyEntry(0, "cad-a", "fv-a", "cad-a-new", "A-NEW.ipt", "IPT", "PENDING"), // -> Incomplete
            MaterializedCopyEntry(1, "cad-b", "fv-b", "cad-b-new", "B-NEW.ipt", "IPT", "fvb", CanonicalB, 1), // destination missing -> Failed
        });
        var orchestrator = BuildOrchestrator();

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_fully_clean_all_COPY_operation_with_no_REUSE_entries_reaches_VERIFIED()
    {
        var srcPath = Path.Combine(_sourceRoot, "A.ipt");
        var destPath = Path.Combine(_destinationFolder, "A-NEW.ipt");
        WriteFile(srcPath, new byte[] { 9 });
        WriteFile(destPath, new byte[] { 8 });
        var manifest = SourceManifest(("cad-a", "fv-a", "A.ipt"));
        var support = Support(
            new[] { MaterializedCopyEntry(0, "cad-a", "fv-a", "cad-new", "A-NEW.ipt", "IPT", "fv-new", CanonicalB, 1) },
            sourceIntegrity: new[] { new CopyDesignSourceIntegrityEvidence(0, "cad-a", "fv-a", CopyDesignSourceIntegrityState.Available, CanonicalA, 1) });
        var hasher = new FakeVerificationHasher();
        hasher.HashFor[srcPath] = CanonicalA;
        hasher.HashFor[destPath] = CanonicalB;
        var orchestrator = BuildOrchestrator(hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, NoDrawings);

        Assert.Equal(CopyDesignVerificationOutcome.Verified, result.Outcome);
        Assert.Equal(CopyDesignVerificationOutcome.Verified, result.Entries[0].Outcome);
        Assert.All(result.Entries[0].Checks, c => Assert.Equal(CopyDesignVerificationCheckState.Passed, c.State));
    }

    // ---- structural read-only proof -----------------------------------------

    [Fact]
    public void The_orchestrators_constructor_has_NO_seam_for_copy_rewire_materialize_or_reservation()
    {
        var ctor = typeof(CopyDesignVerificationOrchestrator).GetConstructors().Single();
        var paramTypeNames = ctor.GetParameters().Select(p => p.ParameterType.Name).ToArray();
        foreach (var forbidden in new[] { "ICopyDesignPhysicalCopier", "ICopyDesignReferenceRewirer", "ICopyDesignMaterializer", "ICopyDesignReservationClient" })
        {
            Assert.DoesNotContain(forbidden, paramTypeNames);
        }
    }

    // ---- THE flagship comprehensive scenarios -------------------------------

    [Fact]
    public async Task Nested_7_entry_operation_MAIN_SUBA_SUBB_A1_A2_B1_IDW_all_clean_reaches_VERIFIED()
    {
        // Mirrors the real nested P6D acceptance fixture's exact shape.
        var names = new[] { "MAIN.iam", "SUBA.iam", "SUBB.iam", "A1.ipt", "A2.ipt", "B1.ipt", "MAIN.idw" };
        var ids = names.Select((_, i) => $"cad-{i}").ToArray();
        var newIds = names.Select((_, i) => $"cad-{i}-new").ToArray();
        var types = new[] { "IAM", "IAM", "IAM", "IPT", "IPT", "IPT", "IDW" };
        var hashes = names.Select((_, i) => (i + 1).ToString("x1") + new string('0', 63)).ToArray();

        var manifest = SourceManifest(names.Select((n, i) => (ids[i], $"fv-{i}", n)).ToArray());

        var entries = names.Select((n, i) =>
            MaterializedCopyEntry(i, ids[i], $"fv-{i}", newIds[i], $"NEW-{n}", types[i], $"fvnew-{i}", hashes[i], i + 1)).ToArray();

        var componentEdges = new[]
        {
            new CopyDesignComponentEdge(ids[0], ids[1]), // MAIN -> SUBA
            new CopyDesignComponentEdge(ids[0], ids[2]), // MAIN -> SUBB
            new CopyDesignComponentEdge(ids[1], ids[3]), // SUBA -> A1
            new CopyDesignComponentEdge(ids[1], ids[4]), // SUBA -> A2
            new CopyDesignComponentEdge(ids[2], ids[5]), // SUBB -> B1
        };
        var drawings = new Dictionary<string, IReadOnlySet<string>> { [ids[6]] = new HashSet<string> { ids[0] } }; // MAIN.idw -> MAIN

        var sourceIntegrity = names.Select((_, i) => new CopyDesignSourceIntegrityEvidence(
            i, ids[i], $"fv-{i}", CopyDesignSourceIntegrityState.Available, hashes[i], 100 + i)).ToArray();

        var support = Support(entries, componentEdges, sourceIntegrity);

        var hasher = new FakeVerificationHasher();
        for (var i = 0; i < names.Length; i++)
        {
            var srcPath = Path.Combine(_sourceRoot, names[i]);
            var destPath = Path.Combine(_destinationFolder, $"NEW-{names[i]}");
            WriteFile(srcPath, new byte[100 + i]); // length matches sourceIntegrity's declared FileSize (100+i)
            WriteFile(destPath, new byte[i + 1]);  // length matches the entry's declared FileSize (i+1)
            hasher.HashFor[srcPath] = hashes[i];
            hasher.HashFor[destPath] = hashes[i];
        }

        var orchestrator = BuildOrchestrator(drawingVerifier: new FakeVerificationInventorAdapter(), hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, drawings);

        Assert.Equal(CopyDesignVerificationOutcome.Verified, result.Outcome);
        Assert.Equal(7, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Equal(CopyDesignVerificationOutcome.Verified, e.Outcome));
        // MAIN and SUBA each get a ReferenceTopology check (they have
        // children); the IDW gets one too (references MAIN); leaf parts do not.
        Assert.Contains(result.Entries[0].Checks, c => c.Name == "ReferenceTopology"); // MAIN
        Assert.Contains(result.Entries[1].Checks, c => c.Name == "ReferenceTopology"); // SUBA
        Assert.Contains(result.Entries[6].Checks, c => c.Name == "ReferenceTopology"); // IDW
        Assert.DoesNotContain(result.Entries[3].Checks, c => c.Name == "ReferenceTopology"); // A1 (leaf)
    }

    // required test 4: an otherwise-correct mixed COPY + REUSE operation
    // reaches VERIFIED - the REUSE entry's informational immutability
    // limitation must NOT hold the whole operation back.
    [Fact]
    public async Task Mixed_COPY_REUSE_drawing_operation_reaches_VERIFIED_the_REUSE_entrys_informational_limitation_does_not_hold_it_back()
    {
        // MAIN (COPY, IAM) -> SUBA (COPY, IAM) + SUBB (REUSE, IAM); MAIN.idw
        // (COPY) references MAIN. Everything is clean and correctly
        // identified - the operation reaches VERIFIED overall, even though
        // SUBB's own per-entry Checks still truthfully report its
        // historical byte immutability as NOT_PROVABLE (informational).
        var manifest = SourceManifest(
            ("cad-main", "fv-main", "MAIN.iam"), ("cad-suba", "fv-suba", "SUBA.iam"),
            ("cad-subb", "fv-subb", "SUBB.iam"), ("cad-idw", "fv-idw", "MAIN.idw"));

        var mainDest = Path.Combine(_destinationFolder, "MAIN-NEW.iam");
        var subaDest = Path.Combine(_destinationFolder, "SUBA-NEW.iam");
        var idwDest = Path.Combine(_destinationFolder, "MAIN-NEW.idw");
        WriteFile(mainDest, new byte[] { 1 });
        WriteFile(subaDest, new byte[] { 1 });
        WriteFile(idwDest, new byte[] { 1 });
        WriteFile(Path.Combine(_sourceRoot, "MAIN.iam"), new byte[] { 1 });
        WriteFile(Path.Combine(_sourceRoot, "SUBA.iam"), new byte[] { 1 });
        WriteFile(Path.Combine(_sourceRoot, "SUBB.iam"), new byte[] { 1 });
        WriteFile(Path.Combine(_sourceRoot, "MAIN.idw"), new byte[] { 1 });

        var entries = new[]
        {
            MaterializedCopyEntry(0, "cad-main", "fv-main", "cad-main-new", "MAIN-NEW.iam", "IAM", "fvm", CanonicalA, 1),
            MaterializedCopyEntry(1, "cad-suba", "fv-suba", "cad-suba-new", "SUBA-NEW.iam", "IAM", "fvs", CanonicalB, 1),
            ReuseEntry(2, "cad-subb", "SUBB.iam", "IAM"),
            MaterializedCopyEntry(3, "cad-idw", "fv-idw", "cad-idw-new", "MAIN-NEW.idw", "IDW", "fvi", CanonicalC, 1),
        };
        var componentEdges = new[]
        {
            new CopyDesignComponentEdge("cad-main", "cad-suba"),
            new CopyDesignComponentEdge("cad-main", "cad-subb"),
        };
        var drawings = new Dictionary<string, IReadOnlySet<string>> { ["cad-idw"] = new HashSet<string> { "cad-main" } };
        var sourceIntegrity = new[]
        {
            new CopyDesignSourceIntegrityEvidence(0, "cad-main", "fv-main", CopyDesignSourceIntegrityState.Available, CanonicalA, 1),
            new CopyDesignSourceIntegrityEvidence(1, "cad-suba", "fv-suba", CopyDesignSourceIntegrityState.Available, CanonicalB, 1),
            new CopyDesignSourceIntegrityEvidence(3, "cad-idw", "fv-idw", CopyDesignSourceIntegrityState.Available, CanonicalC, 1),
        };
        var support = Support(entries, componentEdges, sourceIntegrity);

        var hasher = new FakeVerificationHasher();
        hasher.HashFor[Path.Combine(_sourceRoot, "MAIN.iam")] = CanonicalA;
        hasher.HashFor[Path.Combine(_sourceRoot, "SUBA.iam")] = CanonicalB;
        hasher.HashFor[Path.Combine(_sourceRoot, "MAIN.idw")] = CanonicalC;
        hasher.HashFor[mainDest] = CanonicalA;
        hasher.HashFor[subaDest] = CanonicalB;
        hasher.HashFor[idwDest] = CanonicalC;

        var orchestrator = BuildOrchestrator(drawingVerifier: new FakeVerificationInventorAdapter(), hasher: hasher);

        var result = await orchestrator.VerifyAsync(support, manifest, _destinationFolder, drawings);

        // The whole operation reaches VERIFIED - the REUSE entry's
        // informational limitation is visible but non-blocking.
        Assert.Equal(CopyDesignVerificationOutcome.Verified, result.Outcome);
        var mainResult = result.Entries.Single(e => e.ResultingCadDocumentId == "cad-main-new");
        var subaResult = result.Entries.Single(e => e.ResultingCadDocumentId == "cad-suba-new");
        var subbResult = result.Entries.Single(e => e.ResultingCadDocumentId == "cad-subb");
        var idwResult = result.Entries.Single(e => e.ResultingCadDocumentId == "cad-idw-new");
        Assert.Equal(CopyDesignVerificationOutcome.Verified, mainResult.Outcome);
        Assert.Equal(CopyDesignVerificationOutcome.Verified, subaResult.Outcome);
        Assert.Equal(CopyDesignVerificationOutcome.Verified, subbResult.Outcome); // the REUSE entry - now VERIFIED
        Assert.Equal(CopyDesignVerificationOutcome.Verified, idwResult.Outcome);

        // required test 5: the informational limitation is STILL visibly,
        // truthfully present in the REUSE entry's own Checks.
        var immutabilityCheck = Assert.Single(subbResult.Checks, c => c.Name == "SourceByteImmutability");
        Assert.Equal(CopyDesignVerificationCheckState.NotProvable, immutabilityCheck.State);
        Assert.Equal(CopyDesignVerificationCheckRequirement.Informational, immutabilityCheck.Requirement);
    }
}
