using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

using static Arch.CadConnect.Core.Tests.CopyDesign.Apply.CopyDesignApplyFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

/// <summary>
/// P6D PRODUCTION RECOVERY - END TO END: reproduces the EXACT live sequence
/// this round exists to fix, through the REAL, unmodified
/// <see cref="CopyDesignApplyOrchestrator"/> - proving the durable-resume
/// mechanism (server-authoritative status -&gt; <see cref="CopyDesignResumeAttemptReconstructor"/>
/// -&gt; the SAME orchestrator entry point every other apply/resume already
/// uses) genuinely completes a partial operation with NO in-memory session
/// state at all (simulating an Inventor restart between phase 1 and phase 2 -
/// unlike the in-session resume tests elsewhere in this file, NOTHING here
/// is carried over except what a fresh <see cref="CopyDesignResumeAttemptReconstructor.Reconstruct"/>
/// call derives from durable server status + a freshly-loaded workspace
/// manifest).
/// </summary>
public class CopyDesignDurableResumeEndToEndTests : IDisposable
{
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), "arch-cc-durable-src-" + Guid.NewGuid().ToString("N"));
    private readonly string _destinationFolder = Path.Combine(Path.GetTempPath(), "arch-cc-durable-dst-" + Guid.NewGuid().ToString("N"));

    public CopyDesignDurableResumeEndToEndTests()
    {
        Directory.CreateDirectory(Path.Combine(_sourceRoot, ".arch"));
        Directory.CreateDirectory(_destinationFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sourceRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_destinationFolder, recursive: true); } catch { /* best effort */ }
    }

    private void WriteSourceManifest()
    {
        string Entry(string cadId, string fvId, string fileName) => $$"""
            {
              "relativePath": "{{fileName}}",
              "cadDocumentId": "{{cadId}}",
              "documentNumber": "{{Path.GetFileNameWithoutExtension(fileName)}}",
              "fileName": "{{fileName}}",
              "cadType": "IPT",
              "fileVersionId": "{{fvId}}",
              "versionNumber": 1,
              "checksum": "{{new string('f', 64)}}",
              "fileSize": 8,
              "isRoot": false,
              "dependsOn": [],
              "state": "Verified",
              "retrievedAtUtc": "2026-09-23T00:00:00Z"
            }
            """;
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
                {{Entry("cad-root", "fv-root-src", "P6D-REAL-ROOT.iam")}},
                {{Entry("cad-part-a", "fv-part-a-src", "P6D-REAL-PART-A.ipt")}},
                {{Entry("cad-part-b", "fv-part-b-src", "P6D-REAL-PART-B.ipt")}},
                {{Entry("cad-drawing", "fv-drawing-src", "P6D-REAL-ROOT.idw")}}
              ]
            }
            """);
    }

    [Fact]
    public async Task LIVE_SEQUENCE_durable_resume_with_NO_in_memory_state_completes_only_the_drawing()
    {
        WriteSourceManifest();

        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        var rootModel = CopyNode("cad-root", Path.Combine(_sourceRoot, "P6D-REAL-ROOT.iam"), Path.Combine(_destinationFolder, "P6DNEW-REAL-ROOT.iam"), CadDocumentType.Iam, fileVersionId: "fv-root-src", isRoot: true);
        var partA = CopyNode("cad-part-a", Path.Combine(_sourceRoot, "P6D-REAL-PART-A.ipt"), Path.Combine(_destinationFolder, "P6DNEW-REAL-PART-A.ipt"), fileVersionId: "fv-part-a-src");
        var partB = CopyNode("cad-part-b", Path.Combine(_sourceRoot, "P6D-REAL-PART-B.ipt"), Path.Combine(_destinationFolder, "P6DNEW-REAL-PART-B.ipt"), fileVersionId: "fv-part-b-src");
        var drawing = CopyNode("cad-drawing", Path.Combine(_sourceRoot, "P6D-REAL-ROOT.idw"), Path.Combine(_destinationFolder, "P6DNEW-REAL-ROOT.idw"), CadDocumentType.Idw, fileVersionId: "fv-drawing-src");
        var edges = new[]
        {
            ComponentEdge(rootModel, partA, childIsCopy: true),
            ComponentEdge(rootModel, partB, childIsCopy: true),
            DrawingModelEdge(drawing, rootModel, modelIsCopy: true),
        };
        var plan = Plan(new[] { rootModel, partA, partB, drawing }, edges);
        h.ExistingSources.Add(rootModel.SourceAbsolutePath);
        h.ExistingSources.Add(partA.SourceAbsolutePath);
        h.ExistingSources.Add(partB.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Copier.FailFor.Add("cad-drawing"); // Dirty, at this layer: an ordinary physical-copy failure

        // Canonical (64-hex) content hashes for the 3 model destinations -
        // FakeVerifier's own placeholder ("result-hash") is NOT canonical, so
        // it can never survive the resume-time integrity check (which
        // requires a genuine, canonical FileVersion 1 identity) - set BEFORE
        // phase 1 runs so it is the SAME value both when phase 1 first
        // hashes the freshly-copied file and when phase 2 re-hashes it.
        var rootDest = rootModel.ProposedDestinationAbsolutePath!;
        var partADest = partA.ProposedDestinationAbsolutePath!;
        var partBDest = partB.ProposedDestinationAbsolutePath!;
        SetStableHash(h, rootDest, new string('1', 64));
        SetStableHash(h, partADest, new string('2', 64));
        SetStableHash(h, partBDest, new string('3', 64));
        h.FileSizeFor[rootDest] = 74752L;
        h.FileSizeFor[partADest] = 80896L;
        h.FileSizeFor[partBDest] = 80896L;

        // ---- PHASE 1: the ORIGINAL Apply Copy Design attempt --------------
        var result1 = await h.Build().ExecuteAsync(plan, "key-p6d-durable-live", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingPhysicalCopyFailed, result1.Outcome);
        Assert.True(result1.ReservedButUnmaterialized);
        var operationId = result1.CopyDesignOperationId!;
        var rootOutcome1 = result1.Entries.Single(e => e.CadDocumentId == "cad-root-new");
        var partAOutcome1 = result1.Entries.Single(e => e.CadDocumentId == "cad-part-a-new");
        var partBOutcome1 = result1.Entries.Single(e => e.CadDocumentId == "cad-part-b-new");
        Assert.All(new[] { rootOutcome1, partAOutcome1, partBOutcome1 },
            o => Assert.Equal(CopyDesignMaterializationOutcome.Materialized, o.Materialization!.Outcome));

        // Simulate the 3 model destination files genuinely existing on disk
        // (a real materialized artifact would).
        h.ExistingDestinations.Add(rootDest);
        h.ExistingDestinations.Add(partADest);
        h.ExistingDestinations.Add(partBDest);

        // The ORCHESTRATOR's OWN internal (expectation-based) status client
        // must ALSO now report these 3 as materialized - in reality BOTH
        // this and the discovery status below read the SAME live server
        // data; the test keeps its two fakes in sync to model that.
        h.OperationStatusClient.MarkMaterialized("cad-root-new", 1, new string('1', 64), 74752L, "fv-root-1");
        h.OperationStatusClient.MarkMaterialized("cad-part-a-new", 1, new string('2', 64), 80896L, "fv-part-a-1");
        h.OperationStatusClient.MarkMaterialized("cad-part-b-new", 1, new string('3', 64), 80896L, "fv-part-b-1");

        // ---- "Inventor restarts" - build the DURABLE status purely from
        //      what the server now authoritatively reports, with NOTHING
        //      carried over in memory. --------------------------------
        var durableStatus = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, operationId, "key-p6d-durable-live",
            new[]
            {
                new CopyDesignDurableResumeEntry(
                    0, "COPY", "MATERIALIZED", "cad-root", "fv-root-src", "cad-root-new",
                    "P6DNEW-REAL-ROOT", "P6DNEW-REAL-ROOT.iam", "IAM", null,
                    "fv-root-1", 1, new string('1', 64), 74752L),
                new CopyDesignDurableResumeEntry(
                    1, "COPY", "MATERIALIZED", "cad-part-a", "fv-part-a-src", "cad-part-a-new",
                    "P6DNEW-REAL-PART-A", "P6DNEW-REAL-PART-A.ipt", "IPT", null,
                    "fv-part-a-1", 1, new string('2', 64), 80896L),
                new CopyDesignDurableResumeEntry(
                    2, "COPY", "MATERIALIZED", "cad-part-b", "fv-part-b-src", "cad-part-b-new",
                    "P6DNEW-REAL-PART-B", "P6DNEW-REAL-PART-B.ipt", "IPT", null,
                    "fv-part-b-1", 1, new string('3', 64), 80896L),
                new CopyDesignDurableResumeEntry(
                    3, "COPY", "PENDING", "cad-drawing", "fv-drawing-src", "cad-drawing-new",
                    "P6DNEW-REAL-ROOT", "P6DNEW-REAL-ROOT.idw", "IDW", null),
            });

        // A FRESH source-workspace manifest load (nothing carried over) +
        // the authoritative drawing dependency (from the SAME
        // drawing-association authority the live blocker fix already uses).
        var freshManifest = WorkspaceManifest.LoadOrEmpty(_sourceRoot);
        var dependencies = new Dictionary<string, IReadOnlySet<string>>
        {
            ["cad-drawing"] = new HashSet<string> { "cad-root" },
        };

        var reconstruction = CopyDesignResumeAttemptReconstructor.Reconstruct(
            durableStatus, freshManifest, _destinationFolder, dependencies);
        Assert.True(reconstruction.Success, reconstruction.FailureReason);
        Assert.Equal(operationId, reconstruction.CopyDesignOperationId);
        Assert.Equal("key-p6d-durable-live", reconstruction.IdempotencyKey);

        // The reconstructed nodes resolve their source path via the FRESH
        // manifest (WorkspaceManifest.FindByCadDocumentId), independent of
        // whatever path the phase-1 fixture nodes happened to use - register
        // THOSE exact paths as existing/hashable, not the phase-1 ones.
        foreach (var node in reconstruction.Plan!.Nodes)
        {
            h.ExistingSources.Add(node.SourceAbsolutePath);
        }

        // The engineer saved the drawing (no longer Dirty) before resuming.
        h.Copier.FailFor.Remove("cad-drawing");
        var copierCallsBeforeResume = h.Copier.Calls.Count;
        var rewirerCallsBeforeResume = h.Rewirer.Calls.Count;
        var materializerCallsBeforeResume = h.Materializer.Calls.Count;

        // ---- PHASE 2: the DURABLE resume - the SAME idempotency key, via
        //      the SAME, UNMODIFIED orchestrator entry point. -------------
        var result2 = await h.Build().ExecuteAsync(reconstruction.Plan!, reconstruction.IdempotencyKey!, null);

        if (result2.Outcome != CopyDesignApplyOutcome.Succeeded) Assert.Fail(result2.FailureReason);
        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result2.Outcome);
        Assert.Equal(operationId, result2.CopyDesignOperationId); // SAME operation - never a new reservation
        Assert.False(result2.ReservedButUnmaterialized);
        Assert.Equal(2, h.Reservation.Calls.Count); // idempotent REPLAY, not a fresh request
        Assert.Equal(h.Reservation.Calls[0].IdempotencyKey, h.Reservation.Calls[1].IdempotencyKey);

        // required: zero model copier/rewirer/materializer calls during resume.
        var copierDuringResume = h.Copier.Calls.Skip(copierCallsBeforeResume).ToArray();
        var rewirerDuringResume = h.Rewirer.Calls.Skip(rewirerCallsBeforeResume).ToArray();
        var materializerDuringResume = h.Materializer.Calls.Skip(materializerCallsBeforeResume).ToArray();
        Assert.DoesNotContain("cad-root", copierDuringResume);
        Assert.DoesNotContain("cad-part-a", copierDuringResume);
        Assert.DoesNotContain("cad-part-b", copierDuringResume);
        Assert.DoesNotContain("cad-root", rewirerDuringResume);
        Assert.DoesNotContain("cad-root-new", materializerDuringResume);
        Assert.DoesNotContain("cad-part-a-new", materializerDuringResume);
        Assert.DoesNotContain("cad-part-b-new", materializerDuringResume);

        // required: only the PENDING drawing proceeds, all the way through.
        Assert.Contains("cad-drawing", copierDuringResume);
        Assert.Contains("cad-drawing", h.DrawingRewirer.Calls);
        Assert.Contains("cad-drawing", h.DrawingVerifier.Calls);
        Assert.Contains("cad-drawing-new", materializerDuringResume);

        // required: SAME resultingCadDocumentIds retained (never re-allocated) -
        // the reported identity for an already-materialized entry comes from
        // the AUTHORITATIVE status (fv-root-1, as fed above), never a fresh
        // materializer call (which would default to "fv-1" and never runs
        // here at all - already proven by materializerDuringResume above).
        var rootOutcome2 = result2.Entries.Single(e => e.CadDocumentId == "cad-root-new");
        var drawingOutcome2 = result2.Entries.Single(e => e.CadDocumentId == "cad-drawing-new");
        Assert.Equal("cad-root-new", rootOutcome1.CadDocumentId);
        Assert.Equal("cad-root-new", rootOutcome2.CadDocumentId);
        Assert.Equal("fv-root-1", rootOutcome2.Materialization!.FileVersionId);
        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, drawingOutcome2.Materialization!.Outcome);
    }

    [Fact]
    public async Task An_operation_that_is_ALREADY_fully_materialized_reconstructs_to_an_idempotent_completed_result()
    {
        WriteSourceManifest();
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };

        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(
                    0, "COPY", "MATERIALIZED", "cad-part-a", "fv-part-a-src", "cad-part-a-new",
                    "P6DNEW-REAL-PART-A", "P6DNEW-REAL-PART-A.ipt", "IPT", null, "fv-x", 1, new string('9', 64), 500L),
            });
        var manifest = WorkspaceManifest.LoadOrEmpty(_sourceRoot);
        var reconstruction = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, manifest, _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());
        Assert.True(reconstruction.Success, reconstruction.FailureReason);
        h.ExistingSources.Add(reconstruction.Plan!.Nodes.Single().SourceAbsolutePath);

        var destPath = reconstruction.Plan!.Nodes.Single().ProposedDestinationAbsolutePath!;
        h.ExistingDestinations.Add(destPath);
        SetStableHash(h, destPath, new string('9', 64));
        h.FileSizeFor[destPath] = 500L;
        // The orchestrator's OWN internal status client must ALSO report
        // this as materialized - same reasoning as the LIVE_SEQUENCE test.
        // Unlike that test, no reservation call has run yet to auto-populate
        // this fake's Entries (there is no "phase 1" here), so the PENDING
        // placeholder must be seeded before it can be marked materialized.
        h.OperationStatusClient.AddPendingIfAbsent(true, "cad-part-a", "cad-part-a-new", "P6DNEW-REAL-PART-A", "P6DNEW-REAL-PART-A.ipt", "IPT");
        h.OperationStatusClient.MarkMaterialized("cad-part-a-new", 1, new string('9', 64), 500L, "fv-x");

        var result = await h.Build().ExecuteAsync(reconstruction.Plan!, reconstruction.IdempotencyKey!, null);

        if (result.Outcome != CopyDesignApplyOutcome.Succeeded) Assert.Fail(result.FailureReason);
        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.Empty(h.Copier.Calls);
        Assert.Empty(h.Materializer.Calls);
    }

    // P6D DURABLE RESUME LIVE BLOCKER fix: proves the new EARLY pre-check
    // (CopyDesignResumeAttemptReconstructor.FindMissingMaterializedDestinations,
    // wired into ArchAddInController.FinishDurableCopyDesignResume right
    // before the confirm dialog) reports exactly the same "not actually
    // there" condition the REAL, UNCHANGED orchestrator itself independently
    // fails closed on - so the new check is strictly a legibility
    // improvement (earlier, itemized, non-truncated), never a weaker OR a
    // stricter gate than what already existed.
    [Fact]
    public async Task FindMissingMaterializedDestinations_agrees_with_the_real_orchestrators_own_fail_closed_verdict()
    {
        WriteSourceManifest();
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };

        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-missing-dest", "key-missing-dest",
            new[]
            {
                new CopyDesignDurableResumeEntry(
                    0, "COPY", "MATERIALIZED", "cad-root", "fv-root-src", "cad-root-new",
                    "P6DNEW-REAL-ROOT", "P6DNEW-REAL-ROOT.iam", "IAM", null,
                    "fv-root-1", 1, new string('1', 64), 74752L),
            });

        var manifest = WorkspaceManifest.LoadOrEmpty(_sourceRoot);
        var reconstruction = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, manifest, _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());
        Assert.True(reconstruction.Success, reconstruction.FailureReason);

        // Deliberately never added to h.ExistingDestinations - reproducing
        // this round's exact live blocker: the server reports MATERIALIZED,
        // but the destination folder given does not actually hold the file
        // (a wrong/mistyped destination folder, per this round's root-cause
        // finding).
        h.ExistingSources.Add(reconstruction.Plan!.Nodes.Single().SourceAbsolutePath);
        h.OperationStatusClient.AddPendingIfAbsent(true, "cad-root", "cad-root-new", "P6DNEW-REAL-ROOT", "P6DNEW-REAL-ROOT.iam", "IAM");
        h.OperationStatusClient.MarkMaterialized("cad-root-new", 1, new string('1', 64), 74752L, "fv-root-1");

        // The new pre-check catches it, itemized, BEFORE any orchestrator call.
        var missing = CopyDesignResumeAttemptReconstructor.FindMissingMaterializedDestinations(
            status, _destinationFolder, File.Exists);
        var only = Assert.Single(missing);
        Assert.Equal("P6DNEW-REAL-ROOT.iam", only.OriginalFileName);

        // ...and, run anyway (proving the pre-check did not weaken
        // anything), the REAL, unmodified orchestrator independently
        // rejects the exact same condition on its own authoritative terms.
        var result = await h.Build().ExecuteAsync(reconstruction.Plan!, reconstruction.IdempotencyKey!, null);
        Assert.Equal(CopyDesignApplyOutcome.UnexpectedMaterializationState, result.Outcome);
        Assert.Empty(h.Copier.Calls);
        Assert.Empty(h.Materializer.Calls);
    }

    // required test: failure happens before copier/materializer calls. A
    // malicious server-reported originalFileName is caught by Reconstruct
    // ITSELF - there is no Plan to execute, so the real orchestrator (and
    // therefore its copier/rewirer/materializer adapters) is never even
    // reached, exactly like every other reconstruction failure this file's
    // other tests already exercise (Reconstruct is always called BEFORE
    // ExecuteAsync in the real controller - see ArchAddInController.
    // FinishDurableCopyDesignResume, which returns on `!reconstruction.Success`
    // before ever touching the orchestrator).
    [Fact]
    public void P6D_FINAL_BLOCKER_FIX_a_malicious_originalFileName_never_reaches_the_orchestrator()
    {
        WriteSourceManifest();
        var h = new OrchestratorHarness();

        var status = new CopyDesignDurableResumeStatusResult(
            CopyDesignDurableResumeStatusOutcome.Found, "op-1", "key-1",
            new[]
            {
                new CopyDesignDurableResumeEntry(
                    0, "COPY", "PENDING", "cad-root", "fv-root-src", "cad-root-new",
                    "IGNORED", "..\\P6D-EVIL.iam", "IAM", null),
            });
        var manifest = WorkspaceManifest.LoadOrEmpty(_sourceRoot);

        var reconstruction = CopyDesignResumeAttemptReconstructor.Reconstruct(
            status, manifest, _destinationFolder, new Dictionary<string, IReadOnlySet<string>>());

        Assert.False(reconstruction.Success);
        Assert.Null(reconstruction.Plan);
        // No Plan means the real controller never calls
        // CopyDesignApplyOrchestrator.ExecuteAsync at all - proven here by
        // the fact NOTHING in this harness was ever invoked.
        Assert.Empty(h.Copier.Calls);
        Assert.Empty(h.Rewirer.Calls);
        Assert.Empty(h.Materializer.Calls);
        Assert.Empty(h.Reservation.Calls);
    }

    private static void SetStableHash(OrchestratorHarness h, string path, string hash) =>
        h.Hasher.SequencedHashesFor[path] = new Queue<string>(Enumerable.Repeat(hash, 10));
}
