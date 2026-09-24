using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;
using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.CopyDesign.Apply.CopyDesignApplyFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

public class CopyDesignApplyOrchestratorTests
{
    // ---- happy path -----------------------------------------------------

    [Fact]
    public async Task HappyPath_MixedCopyAndReuse_SucceedsAndMaterializesEveryCopy()
    {
        var h = new OrchestratorHarness();
        var reuseChild = ReuseNode("cad-std", @"C:\src\std.ipt");
        var copyChild = CopyNode("cad-child", @"C:\src\child.ipt", @"C:\dst\child-new.ipt");
        var parent = CopyNode("cad-parent", @"C:\src\parent.iam", @"C:\dst\parent-new.iam", CadDocumentType.Iam, isRoot: true);
        var edges = new[]
        {
            ComponentEdge(parent, copyChild, childIsCopy: true),
            ComponentEdge(parent, reuseChild, childIsCopy: false),
        };
        var plan = Plan(new[] { parent, copyChild, reuseChild }, edges);
        h.ExistingSources.Add(parent.SourceAbsolutePath);
        h.ExistingSources.Add(copyChild.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal("op-1", result.CopyDesignOperationId);
        Assert.False(result.ReservedButUnmaterialized);
        Assert.Equal(new[] { "cad-child", "cad-parent" }, h.Copier.Calls.OrderBy(x => x));
        Assert.Equal(new[] { "cad-parent" }, h.Rewirer.Calls); // only the copied IAM is rewired
        Assert.Equal(2, h.Materializer.Calls.Count); // one per COPY entry, never for REUSE
    }

    // 2. explicit confirmation required / caller must supply an idempotency key
    [Fact]
    public async Task NoIdempotencyKeyMeansNoApplyIsPossible()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node });

        var result = await h.Build().ExecuteAsync(plan, "", null);

        Assert.Equal(CopyDesignApplyOutcome.MappingFailed, result.Outcome);
        Assert.Empty(h.Reservation.Calls); // never even reaches the server
    }

    [Fact]
    public async Task NonExecutablePlanNeverReachesReservationOrPhysicalCopy()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node }, isExecutable: false);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.MappingFailed, result.Outcome);
        Assert.Empty(h.Reservation.Calls);
        Assert.Empty(h.Copier.Calls);
    }

    // 5. same logical retry retains idempotency key
    [Fact]
    public async Task TheSameIdempotencyKeyIsSentOnEveryCallForOneAttempt()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        var orchestrator = h.Build();

        await orchestrator.ExecuteAsync(plan, "same-key-123", null);
        await orchestrator.ExecuteAsync(plan, "same-key-123", null);

        Assert.Equal(2, h.Reservation.Calls.Count);
        Assert.All(h.Reservation.Calls, r => Assert.Equal("same-key-123", r.IdempotencyKey));
    }

    // ---- reservation response validation stops mutation ------------------

    [Fact]
    public async Task ReservationResponseMismatchStopsBeforeAnyPhysicalMutation()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        h.Reservation.Responder = _ => new CopyDesignReservationResponse("c", "op-99", "now", Array.Empty<CopyDesignReservationResponseEntry>());

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.ReservationResponseInvalid, result.Outcome);
        Assert.Equal("op-99", result.CopyDesignOperationId); // reservation evidence still reported, never hidden
        Assert.Empty(h.Copier.Calls); // NEVER physically mutates on a mismatch
    }

    [Fact]
    public async Task ReservationCallFailureIsReportedWithoutAnOperationId()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        h.Reservation.ThrowOnCall = new HttpRequestExceptionStub();

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.ReservationCallFailed, result.Outcome);
        Assert.Null(result.CopyDesignOperationId);
        Assert.Empty(h.Copier.Calls);
    }

    // ---- reservation succeeded, then a local failure = reserved but unmaterialized ----

    // 25. reservation-success/local-failure reports reserved-unmaterialized
    [Fact]
    public async Task PhysicalCopyFailureAfterReservationReportsReservedButUnmaterializedAndCleansUp()
    {
        var h = new OrchestratorHarness();
        var first = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var second = CopyNode("cad-2", @"C:\src\b.ipt", @"C:\dst\b.ipt");
        var plan = Plan(new[] { first, second });
        h.ExistingSources.Add(first.SourceAbsolutePath);
        h.ExistingSources.Add(second.SourceAbsolutePath);
        h.Copier.FailFor.Add("cad-2"); // first succeeds, second fails

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.PhysicalCopyFailed, result.Outcome);
        Assert.Equal("op-1", result.CopyDesignOperationId);
        Assert.True(result.ReservedButUnmaterialized);
        Assert.Empty(h.Materializer.Calls); // no FileVersion for a failed attempt

        // 26. cleanup deletes only operation-created destination files
        Assert.NotNull(result.Cleanup);
        Assert.Single(h.Deleted); // only cad-1's destination was ever created
        Assert.Equal(first.ProposedDestinationAbsolutePath, h.Deleted[0]);
    }

    // P6D ROUND 3, item G ("complete result reporting"): a MODEL-phase
    // failure must still report EVERY reserved entry - the REUSE entries
    // (never physically touched, previously omitted entirely because the
    // REUSE-reporting loop only ran near the very end, AFTER the model
    // phases) and every drawing entry never reached (as "not physically
    // created", never silently dropped from the list).
    [Fact]
    public async Task A_model_physical_copy_failure_still_reports_every_REUSE_entry_and_every_unattempted_drawing_entry()
    {
        var h = new OrchestratorHarness();
        var modelFail = CopyNode("cad-model-fail", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var modelReuse = ReuseNode("cad-model-reuse", @"C:\src\std-bolt.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, modelFail, modelIsCopy: true) };
        var plan = Plan(new[] { modelFail, modelReuse, drawing }, edges);
        h.ExistingSources.Add(modelFail.SourceAbsolutePath);
        h.ExistingSources.Add(modelReuse.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Copier.FailFor.Add("cad-model-fail");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.PhysicalCopyFailed, result.Outcome);
        Assert.Equal(3, result.Entries.Count);

        var modelEntry = result.Entries.Single(e => e.CadDocumentId == "cad-model-fail-new");
        Assert.False(modelEntry.PhysicallyCreated); // failed - never claims success

        var reuseEntry = result.Entries.Single(e => e.CadDocumentId == "cad-model-reuse");
        Assert.Equal(CopyDesignApplyEntryAction.Reuse, reuseEntry.Action);
        Assert.True(reuseEntry.VerificationPassed);

        var drawingEntry = result.Entries.Single(e => e.CadDocumentId == "cad-drawing-new");
        Assert.Equal(CopyDesignApplyEntryAction.Copy, drawingEntry.Action);
        Assert.False(drawingEntry.PhysicallyCreated); // never even attempted
        Assert.Null(drawingEntry.Materialization);
        Assert.Empty(h.DrawingRewirer.Calls); // the drawing phase never ran at all
    }

    // P6D ROUND 3, item G: a resume-status/revalidation failure that occurs
    // AFTER a successful reservation must still report every entry the
    // reservation confirmed - never an empty list, even though nothing has
    // been physically attempted yet.
    [Fact]
    public async Task An_operation_status_failure_still_reports_every_reserved_entry_never_an_empty_list()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        h.OperationStatusClient.ThrowOnCall = new InvalidOperationException("simulated transport failure");
        var modelCopy = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var modelReuse = ReuseNode("cad-model-reuse", @"C:\src\std-bolt.ipt");
        var plan = Plan(new[] { modelCopy, modelReuse });
        h.ExistingSources.Add(modelCopy.SourceAbsolutePath);
        h.ExistingSources.Add(modelReuse.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.UnexpectedMaterializationState, result.Outcome);
        Assert.Equal(2, result.Entries.Count);
        Assert.Contains(result.Entries, e => e.CadDocumentId == "cad-model-new" && !e.PhysicallyCreated);
        Assert.Contains(result.Entries, e => e.CadDocumentId == "cad-model-reuse" && e.Action == CopyDesignApplyEntryAction.Reuse);
    }

    // 23. failed rewiring creates no materialization request
    [Fact]
    public async Task ReferenceRewiringFailureNeverMaterializesAnything()
    {
        var h = new OrchestratorHarness();
        var child = CopyNode("cad-child", @"C:\src\child.ipt", @"C:\dst\child-new.ipt");
        var parent = CopyNode("cad-parent", @"C:\src\parent.iam", @"C:\dst\parent-new.iam", CadDocumentType.Iam, isRoot: true);
        var plan = Plan(new[] { parent, child }, new[] { ComponentEdge(parent, child, true) });
        h.ExistingSources.Add(parent.SourceAbsolutePath);
        h.ExistingSources.Add(child.SourceAbsolutePath);
        h.Rewirer.FailFor.Add("cad-parent");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.ReferenceRewiringFailed, result.Outcome);
        Assert.Empty(h.Materializer.Calls);
        Assert.True(result.ReservedButUnmaterialized);
        Assert.Equal(2, h.Deleted.Count); // both physically-copied destinations cleaned up
    }

    // 24. failed verification creates no materialization request
    [Fact]
    public async Task VerificationFailureNeverMaterializesAnything()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        h.Verifier.FailFor.Add("cad-1");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.VerificationFailed, result.Outcome);
        Assert.Empty(h.Materializer.Calls);
        Assert.True(result.ReservedButUnmaterialized);
        Assert.Single(h.Deleted);
    }

    // ---- a materialization failure is honest, not a physical-cleanup failure ----------

    [Fact]
    public async Task MaterializationFailureSucceedsPhysicallyButFlagsTheGap()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        h.Materializer.Responder = id => new CopyDesignMaterializationResult(
            id, CopyDesignMaterializationOutcome.Failed, null, null, null, null, "materialization failed");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SucceededWithMaterializationGaps, result.Outcome);
        Assert.True(result.ReservedButUnmaterialized);
        // 29 (partial): the physical file itself is NOT deleted for a
        // materialization failure - only a physical/rewire/verification
        // FAILURE (earlier phases) triggers cleanup.
        Assert.Empty(h.Deleted);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(CopyDesignMaterializationOutcome.Failed, entry.Materialization!.Outcome);
    }

    // 28 / 29: materialization uses P6B resulting CadDocumentId + the actual binary
    [Fact]
    public async Task MaterializationIsRequestedForTheResultingCadDocumentIdAndTheActualDestinationFile()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        await h.Build().ExecuteAsync(plan, "key-1", null);

        var materializedId = Assert.Single(h.Materializer.Calls);
        Assert.Equal("cad-1-new", materializedId); // the P6B RESULTING id, never the source id
    }

    // P6D superseded this: a drawing with a proven model owner now DOES
    // reach the physical copier (P6D owns drawing execution). See
    // "CopyDesignNodesNeverReachThePhysicalCopier..." below for the ONE
    // remaining case where a drawing still never reaches it (model-files-
    // only acknowledgement).
    [Fact]
    public async Task A_drawing_COPY_node_with_a_proven_model_owner_now_reaches_the_physical_copier_and_is_materialized()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw, isRoot: true);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { drawing, model }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.Contains("cad-drawing", h.Copier.Calls);
        Assert.Contains("cad-drawing", h.DrawingRewirer.Calls);
        Assert.Contains("cad-drawing", h.DrawingVerifier.Calls);
        Assert.DoesNotContain("cad-drawing", h.Rewirer.Calls); // never the MODEL rewirer
        Assert.DoesNotContain("cad-drawing", h.Verifier.Calls); // never the MODEL verifier
        Assert.Equal(2, h.Materializer.Calls.Count); // one per COPY entry (model + drawing)
    }

    // P6D ordering: models complete/verify BEFORE any drawing copy begins.
    [Fact]
    public async Task Drawings_are_never_physically_copied_until_every_model_has_been_copied_rewired_and_verified()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.iam", @"C:\dst\a-new.iam", CadDocumentType.Iam);
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Verifier.FailFor.Add("cad-model"); // model verification fails

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.VerificationFailed, result.Outcome);
        Assert.DoesNotContain("cad-drawing", h.Copier.Calls); // drawing phase never started
        Assert.Empty(h.DrawingRewirer.Calls);
        Assert.Empty(h.DrawingVerifier.Calls);
    }

    // 32 / 33: double-execution guard + cannot silently overwrite prior output
    [Fact]
    public async Task ConcurrentExecuteCallsRefuseTheSecondOne()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        using var lease = h.Guard.TryEnter(); // simulate an apply already in progress
        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.AlreadyRunning, result.Outcome);
        Assert.Empty(h.Reservation.Calls);
    }

    [Fact]
    public async Task ARepeatedApplyAfterTheDestinationNowExistsRefusesToOverwrite()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        var first = await h.Build().ExecuteAsync(plan, "key-1", null);
        Assert.Equal(CopyDesignApplyOutcome.Succeeded, first.Outcome);

        // Simulate the destination now existing (e.g. the prior run's output,
        // or something else) for a SECOND, independent apply attempt.
        h.ExistingDestinations.Add(node.ProposedDestinationAbsolutePath!);
        var second = await h.Build().ExecuteAsync(plan, "key-2", null);

        Assert.Equal(CopyDesignApplyOutcome.RevalidationFailed, second.Outcome);
        Assert.Single(h.Copier.Calls); // the second attempt never physically copied again
    }

    private sealed class HttpRequestExceptionStub : Exception
    {
        public HttpRequestExceptionStub() : base("simulated network failure") { }
    }

    // ======================================================================
    // CODEX FINAL AUDIT ROUND 1, HIGH 1 (RETIRED BY P6D): a represented
    // drawing is NEVER silently omitted from Apply. P6C's fix was to REFUSE
    // to apply; P6D's fix is to EXECUTE it instead - see
    // CopyDesignApplyOutcome.DrawingsPresentWithoutAcknowledgement's own doc
    // comment.
    // ======================================================================

    // test 26 (required test list item 26): an unacknowledged executable
    // drawing node is processed, never silently omitted.
    [Fact]
    public async Task An_unacknowledged_drawing_COPY_node_is_now_executed_not_rejected()
    {
        var h = new OrchestratorHarness();
        var iptNode = CopyNode("cad-ipt", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawingNode = CopyNode("cad-dwg", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw, isRoot: true);
        var plan = Plan(new[] { iptNode, drawingNode }); // ModelFilesOnlyAcknowledged defaults to false, no DrawingModel edge (no owner)
        h.ExistingSources.Add(iptNode.SourceAbsolutePath);
        h.ExistingSources.Add(drawingNode.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.Contains("cad-dwg", h.Copier.Calls);
        Assert.NotEmpty(h.Reservation.Calls);
        var sentEntries = h.Reservation.Calls[0].WireEntries;
        Assert.Contains(sentEntries, e => e.Action == CopyDesignApplyEntryAction.Copy && e.Copy!.DocumentType == CadDocumentType.Idw);
    }

    [Fact]
    public async Task An_unacknowledged_drawing_REUSE_node_is_ALSO_now_executed_not_rejected()
    {
        var h = new OrchestratorHarness();
        var iptNode = CopyNode("cad-ipt", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawingReuse = ReuseNode("cad-dwg", @"C:\src\a.idw", CadDocumentType.Idw);
        var plan = Plan(new[] { iptNode, drawingReuse });
        h.ExistingSources.Add(iptNode.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        // A REUSE drawing is never physically touched, but IS still sent to
        // the reservation and reported as materialized-as-reused.
        var sentEntries = h.Reservation.Calls[0].WireEntries;
        Assert.Contains(sentEntries, e => e.Action == CopyDesignApplyEntryAction.Reuse && e.Reuse!.CadDocumentId == "cad-dwg");
        var reuseOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-dwg");
        Assert.Equal(CopyDesignApplyEntryAction.Reuse, reuseOutcome.Action);
        Assert.False(reuseOutcome.PhysicallyCreated);
    }

    // required test list item 25: a plan the engineer explicitly
    // acknowledged as model-files-only never executes drawings, even a
    // drawing that WOULD have had a proven owner / would otherwise be safe.
    [Fact]
    public async Task Model_files_only_acknowledged_plan_never_executes_a_drawing_even_with_a_proven_owner()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges) with { ModelFilesOnlyAcknowledged = true };
        h.ExistingSources.Add(model.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain("cad-drawing", h.Copier.Calls);
        Assert.Empty(h.DrawingRewirer.Calls);
        Assert.Empty(h.DrawingVerifier.Calls);
        Assert.DoesNotContain(result.Entries, e => e.CadDocumentId == "cad-drawing");
    }

    // config guard: an orchestrator wired WITHOUT drawing adapters must fail
    // closed rather than NullReferenceException or silently skip a drawing
    // COPY node the plan actually represents.
    [Fact]
    public async Task A_drawing_COPY_node_with_no_drawing_adapters_configured_fails_closed_before_any_mutation()
    {
        var h = new OrchestratorHarness { IncludeDrawingAdapters = false };
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw, isRoot: true);
        var plan = Plan(new[] { drawing });
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.MappingFailed, result.Outcome);
        Assert.Empty(h.Reservation.Calls);
        Assert.Empty(h.Copier.Calls);
    }

    // a REUSE-only drawing plan never needs drawing adapters at all (it is
    // never physically touched), so the config guard must not fire for it.
    [Fact]
    public async Task A_REUSE_only_drawing_never_requires_drawing_adapters_to_be_configured()
    {
        var h = new OrchestratorHarness { IncludeDrawingAdapters = false };
        var drawingReuse = ReuseNode("cad-drawing", @"C:\src\a.idw", CadDocumentType.Idw);
        var plan = Plan(new[] { drawingReuse });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task An_acknowledged_model_files_only_plan_proceeds_and_the_drawing_is_never_sent_or_copied()
    {
        var h = new OrchestratorHarness();
        var iptNode = CopyNode("cad-ipt", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawingNode = CopyNode("cad-dwg", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var plan = Plan(new[] { iptNode, drawingNode }) with { ModelFilesOnlyAcknowledged = true };
        h.ExistingSources.Add(iptNode.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain("cad-dwg", h.Copier.Calls);
        var sentEntries = Assert.Single(h.Reservation.Calls).WireEntries;
        Assert.DoesNotContain(sentEntries, e => e.Action == CopyDesignApplyEntryAction.Copy && e.Copy!.DocumentType == CadDocumentType.Idw);
    }

    // NOTE: P6C's "acknowledgement gate" this test originally proved a
    // drawing could bypass no longer exists (see the orchestrator's own
    // class doc comment - P6D executes drawings instead of gating them).
    // What this test proves NOW: CopyDesignApplyRequestMapper treats ANY
    // in-scope node (model OR drawing alike) whose ProposedAction is
    // Exclude/NeedsDecision as a hard mapping failure for the WHOLE
    // request - this is PRE-EXISTING P6C mapper behavior (see
    // NeedsDecisionNodeFailsClosedEvenOnANominallyExecutablePlan /
    // ExcludeNodeFailsClosed in CopyDesignApplyRequestMapperTests, both
    // unchanged), now equally reachable via a drawing since drawings are
    // in-scope by default. This is a discovered pre-existing characteristic
    // (never a P6D regression - a model-only plan with a genuinely
    // dependency-free EXCLUDE node already failed to map identically before
    // P6D) - see the P6D report's "remaining limitations".
    [Fact]
    public async Task An_EXCLUDED_in_scope_drawing_node_fails_mapping_closed_exactly_like_an_excluded_model_would()
    {
        var h = new OrchestratorHarness();
        var iptNode = CopyNode("cad-ipt", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var excludedDrawing = CopyNode("cad-dwg", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw) with { ProposedAction = CopyDesignAction.Exclude };
        var plan = Plan(new[] { iptNode, excludedDrawing });
        h.ExistingSources.Add(iptNode.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.MappingFailed, result.Outcome);
        Assert.Empty(h.Reservation.Calls);
    }

    // ======================================================================
    // CODEX FINAL AUDIT ROUND 1, HIGH 2: bounded, same-key, same-request
    // automatic retry for an UNCERTAIN reservation result only.
    // ======================================================================

    [Fact]
    public async Task An_uncertain_first_reservation_result_is_automatically_retried_with_the_SAME_key_and_request_and_succeeds()
    {
        var h = new OrchestratorHarness { IsTransientReservationFailure = ex => ex is IOException };
        h.Reservation.ThrowForFirstNCalls = 1;
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "same-key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, h.Reservation.Calls.Count);
        Assert.All(h.Reservation.Calls, r => Assert.Equal("same-key-1", r.IdempotencyKey));
        Assert.Same(h.Reservation.Calls[0], h.Reservation.Calls[1]); // the EXACT SAME immutable request object
        Assert.Equal("same-key-1", result.IdempotencyKey);
    }

    [Fact]
    public async Task A_deterministic_conflict_exception_is_never_retried()
    {
        var h = new OrchestratorHarness { IsTransientReservationFailure = ex => ex is IOException };
        h.Reservation.ThrowOnCall = new InvalidOperationException("simulated deterministic 409 conflict");
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.ReservationCallFailed, result.Outcome);
        Assert.Single(h.Reservation.Calls); // no retry at all - not classified as transient
        Assert.Empty(h.Copier.Calls);
    }

    [Fact]
    public async Task Retry_exhaustion_after_every_bounded_attempt_performs_ZERO_physical_mutation()
    {
        var h = new OrchestratorHarness { IsTransientReservationFailure = ex => ex is IOException, MaxReservationAttempts = 3 };
        h.Reservation.ThrowForFirstNCalls = int.MaxValue; // always transient-fails
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.ReservationCallFailed, result.Outcome);
        Assert.Equal(3, h.Reservation.Calls.Count); // bounded, never unbounded
        Assert.Empty(h.Copier.Calls);
        Assert.Equal("key-1", result.IdempotencyKey); // preserved for diagnosis/manual retry
    }

    [Fact]
    public async Task No_new_idempotency_key_is_ever_generated_during_bounded_reservation_retries()
    {
        var h = new OrchestratorHarness { IsTransientReservationFailure = ex => ex is IOException };
        h.Reservation.ThrowForFirstNCalls = 2;
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        var attempt = Core.CopyDesign.Apply.CopyDesignApplyAttempt.StartNewAttempt();

        var result = await h.Build().ExecuteAsync(plan, attempt.IdempotencyKey, null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        var distinctKeys = h.Reservation.Calls.Select(r => r.IdempotencyKey).Distinct().ToArray();
        Assert.Single(distinctKeys);
        Assert.Equal(attempt.IdempotencyKey, distinctKeys[0]);
    }

    // ======================================================================
    // CODEX FINAL AUDIT ROUND 1, HIGH 5: verification exception/cancellation
    // after physical creation must never escape uncontrolled.
    // ======================================================================

    [Fact]
    public async Task A_verification_exception_after_physical_copy_runs_cleanup_and_reports_a_controlled_VerificationFailed()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        h.Verifier.ThrowFor.Add("cad-1");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.VerificationFailed, result.Outcome);
        Assert.Equal("op-1", result.CopyDesignOperationId); // reservation evidence preserved
        Assert.NotNull(result.Cleanup);
        Assert.Contains(node.ProposedDestinationAbsolutePath!, h.Deleted); // cleanup actually ran
        Assert.Contains("was NOT rolled back", result.FailureReason); // never claims the server reservation was rolled back
        Assert.Empty(h.Materializer.Calls); // zero materialization after failed verification
    }

    [Fact]
    public async Task A_cancellation_during_verification_after_physical_copy_is_caught_and_reported_not_left_unhandled()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        h.Verifier.ThrowFor.Add("cad-1");
        h.Verifier.ExceptionToThrow = new OperationCanceledException("verification cancelled");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.VerificationFailed, result.Outcome);
        Assert.NotNull(result.Cleanup);
        Assert.Empty(h.Materializer.Calls);
    }

    // ======================================================================
    // CODEX ROUND 3, HIGH: an occurrence-enumeration failure must fail
    // verification end-to-end and block materialization, with the
    // reservation operationId preserved and journal cleanup run.
    // ======================================================================

    // item 5: no materialization after enumeration failure
    [Fact]
    public async Task An_occurrence_enumeration_failure_blocks_materialization_preserves_operationId_and_runs_cleanup()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.iam", @"C:\dst\a-new.iam", CadDocumentType.Iam);
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        h.Verifier.EnumerationFailureFor.Add("cad-1");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.VerificationFailed, result.Outcome);
        Assert.Equal("op-1", result.CopyDesignOperationId); // reservation evidence preserved
        Assert.NotNull(result.Cleanup);
        Assert.Contains(node.ProposedDestinationAbsolutePath!, h.Deleted); // journal cleanup actually ran
        Assert.Empty(h.Materializer.Calls); // zero materialization
        Assert.Contains("occurrence enumeration", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // ======================================================================
    // CODEX ROUND 3, MEDIUM: an unremoved operation-owned temp artifact is
    // surfaced through the EXISTING FailureReason reporting surface, never
    // silently dropped and never a new UI section.
    // ======================================================================

    [Fact]
    public async Task An_unremoved_temp_path_from_a_failed_physical_copy_is_folded_into_the_existing_failure_reason()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        h.Copier.FailFor.Add("cad-1");
        h.Copier.UnremovedTempPathFor["cad-1"] = @"C:\dst\.p6c-tmp-abc-a-new.ipt";

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.PhysicalCopyFailed, result.Outcome);
        Assert.Contains(@"C:\dst\.p6c-tmp-abc-a-new.ipt", result.FailureReason);
        Assert.Contains("manual cleanup", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_physical_copy_with_NO_unremoved_temp_path_never_mentions_manual_cleanup()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        h.Copier.FailFor.Add("cad-1"); // no UnremovedTempPathFor entry - cleanup succeeded

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.PhysicalCopyFailed, result.Outcome);
        Assert.DoesNotContain("manual cleanup", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_occurrence_enumeration_failure_on_an_IAM_with_zero_expected_references_still_fails()
    {
        // The exact scenario the bug allowed through: a copied IAM with NO
        // component children at all, where enumeration itself still failed.
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.iam", @"C:\dst\a-new.iam", CadDocumentType.Iam);
        var plan = Plan(new[] { node }); // no edges - zero expected references
        h.ExistingSources.Add(node.SourceAbsolutePath);
        h.Verifier.EnumerationFailureFor.Add("cad-1");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.VerificationFailed, result.Outcome);
        Assert.Empty(h.Materializer.Calls);
    }

    // ======================================================================
    // P6D: DRAWING PHASE - physical copy / rewire / verify failures each
    // stop further drawing mutation, clean up ONLY the drawing-owned
    // destination artifacts THIS attempt created, preserve the P6B
    // reservation, and NEVER touch a model artifact - but a model that
    // already independently passed ITS OWN verification is still
    // materialized (see the orchestrator's own class doc comment for why).
    // ======================================================================

    [Fact]
    public async Task Drawing_physical_copy_failure_after_models_succeed_cleans_up_ONLY_the_drawing_and_still_materializes_the_model()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Copier.FailFor.Add("cad-drawing"); // the MODEL copy still succeeds - only the drawing fails

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingPhysicalCopyFailed, result.Outcome);
        Assert.Equal("op-1", result.CopyDesignOperationId); // reservation preserved
        Assert.True(result.ReservedButUnmaterialized);

        // the model's destination was never touched by drawing-phase cleanup
        Assert.DoesNotContain(model.ProposedDestinationAbsolutePath!, h.Deleted);
        Assert.Empty(h.DrawingRewirer.Calls); // rewiring never even started
        Assert.Empty(h.DrawingVerifier.Calls);

        // the model still genuinely materialized - it passed its own
        // verification before the drawing phase ever began.
        var modelOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-model-new");
        Assert.NotNull(modelOutcome.Materialization);
        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, modelOutcome.Materialization!.Outcome);

        // the drawing itself carries no materialization at all.
        var drawingOutcome = result.Entries.SingleOrDefault(e => e.CadDocumentId == "cad-drawing-new");
        Assert.Null(drawingOutcome?.Materialization);
    }

    [Fact]
    public async Task Drawing_reference_rewiring_failure_cleans_up_ONLY_the_drawing_and_still_materializes_the_model()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.DrawingRewirer.FailFor.Add("cad-drawing");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingReferenceRewiringFailed, result.Outcome);
        Assert.Contains("cad-drawing", h.Copier.Calls); // physically copied before rewiring was attempted
        Assert.Single(h.Deleted); // ONLY the drawing's destination was cleaned up
        Assert.Equal(drawing.ProposedDestinationAbsolutePath, h.Deleted[0]);
        Assert.Empty(h.DrawingVerifier.Calls); // never reached verification

        var modelOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-model-new");
        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, modelOutcome.Materialization!.Outcome);
    }

    [Fact]
    public async Task Drawing_verification_failure_never_materializes_the_drawing_but_still_materializes_the_model()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.DrawingVerifier.FailFor.Add("cad-drawing");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingVerificationFailed, result.Outcome);
        Assert.Single(h.Deleted);
        Assert.Equal(drawing.ProposedDestinationAbsolutePath, h.Deleted[0]);

        var modelOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-model-new");
        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, modelOutcome.Materialization!.Outcome);

        // never materialize a drawing whose verification failed.
        var drawingOutcome = result.Entries.SingleOrDefault(e => e.CadDocumentId == "cad-drawing-new");
        Assert.Null(drawingOutcome?.Materialization);
    }

    [Fact]
    public async Task A_drawing_verification_exception_after_physical_copy_runs_drawing_only_cleanup_and_reports_a_controlled_outcome()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.DrawingVerifier.ThrowFor.Add("cad-drawing");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingVerificationFailed, result.Outcome);
        Assert.Equal("op-1", result.CopyDesignOperationId);
        Assert.Contains("was NOT rolled back", result.FailureReason);
        Assert.Single(h.Deleted);
        Assert.Equal(drawing.ProposedDestinationAbsolutePath, h.Deleted[0]);
    }

    // required test list item 7: multiple model owners for one drawing are
    // ALL retained/verified (this exercises the FULL pipeline end-to-end,
    // not just CopyDesignReferenceTargetResolver in isolation).
    [Fact]
    public async Task A_drawing_referencing_multiple_models_rewires_and_verifies_every_owner()
    {
        var h = new OrchestratorHarness();
        var modelA = CopyNode("cad-model-a", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var modelB = ReuseNode("cad-model-b", @"C:\src\std-b.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\root.idw", @"C:\dst\root-new.idw", CadDocumentType.Idw);
        var edges = new[]
        {
            DrawingModelEdge(drawing, modelA, modelIsCopy: true),
            DrawingModelEdge(drawing, modelB, modelIsCopy: false),
        };
        var plan = Plan(new[] { modelA, modelB, drawing }, edges);
        h.ExistingSources.Add(modelA.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        var drawingEntry = result.Entries.Single(e => e.CadDocumentId == "cad-drawing-new");
        Assert.True(drawingEntry.VerificationPassed);
    }

    // required test list item 24: materialization uses the RESERVED
    // resultingCadDocumentId for a drawing too, never the source id.
    [Fact]
    public async Task Drawing_materialization_uses_the_reserved_resultingCadDocumentId_and_the_verified_final_binary()
    {
        var h = new OrchestratorHarness();
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var plan = Plan(new[] { drawing });
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        await h.Build().ExecuteAsync(plan, "key-1", null);

        var materializedId = Assert.Single(h.Materializer.Calls);
        Assert.Equal("cad-drawing-new", materializedId);
        var request = Assert.Single(h.Materializer.Requests);
        Assert.Equal("cad-drawing", request.SourceCadDocumentId); // source id preserved as lineage input, never the resulting id
        Assert.Equal(CadDocumentType.Idw, request.DocumentType);
    }

    // required test list item 29 / drawing-specific 30: no PDF/DXF/STEP/
    // Pack&Go/Mirror surface exists anywhere in the orchestrator or its
    // adapter interfaces - a structural guarantee, not a behavioral one:
    // the orchestrator only ever calls the SEVEN injected interfaces, none
    // of which can express those operations at all.
    [Fact]
    public async Task No_drawing_COPY_action_ever_invokes_the_MODEL_rewirer_or_verifier_and_vice_versa()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.iam", @"C:\dst\a-new.iam", CadDocumentType.Iam);
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(new[] { "cad-model" }, h.Rewirer.Calls);
        Assert.Equal(new[] { "cad-model" }, h.Verifier.Calls);
        Assert.Equal(new[] { "cad-drawing" }, h.DrawingRewirer.Calls);
        Assert.Equal(new[] { "cad-drawing" }, h.DrawingVerifier.Calls);
    }

    // ======================================================================
    // P6D ROUND 2, CRITICAL item 6: referenced model document integrity is
    // re-checked (hash before vs. after the drawing phase) independent of
    // each drawing's own verification - "confirm source model hashes remain
    // unchanged in ALL drawing paths".
    // ======================================================================

    [Fact]
    public async Task A_COPY_model_destination_whose_hash_changes_during_the_drawing_phase_fails_drawing_verification()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Hasher.SequencedHashesFor[model.ProposedDestinationAbsolutePath!] =
            new Queue<string>(new[] { "hash-before-drawing-phase", "hash-after-drawing-phase-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        // P6D ROUND 4, HIGH fix (item C): a violation detected here (Phase
        // 6b, the final integrity re-check) is a SOURCE INTEGRITY VIOLATION,
        // not an ordinary DrawingVerificationFailed - and per item C, that
        // means ZERO further materialization for ANY not-yet-materialized
        // entry, the already-verified model INCLUDED (the model's own
        // destination is exactly the file about to become FileVersion 1 -
        // materializing it now would upload content that no longer matches
        // what verification actually checked).
        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("integrity", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a-new.ipt", result.FailureReason);
        var modelOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-model-new");
        Assert.Null(modelOutcome.Materialization);
    }

    [Fact]
    public async Task A_REUSE_model_source_whose_hash_changes_during_the_drawing_phase_fails_drawing_verification()
    {
        var h = new OrchestratorHarness();
        var modelReuse = ReuseNode("cad-model", @"C:\src\std-bolt.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, modelReuse, modelIsCopy: false) };
        var plan = Plan(new[] { modelReuse, drawing }, edges);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Hasher.SequencedHashesFor[modelReuse.SourceAbsolutePath] =
            new Queue<string>(new[] { "hash-before", "hash-after-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        // P6D ROUND 3, CRITICAL fix (items A+B): a REUSE model IS a SOURCE
        // reference (source == final target for REUSE), so its mutation is
        // now caught by the per-drawing SOURCE-set recheck immediately after
        // physical copy - BEFORE rewiring is even attempted - rather than
        // only at the final post-verification check. P6D ROUND 4 (item C):
        // this is now the dedicated SourceIntegrityViolation outcome, not a
        // generic DrawingPhysicalCopyFailed.
        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("std-bolt.ipt", result.FailureReason);
    }

    [Fact]
    public async Task A_referenced_model_that_is_unreadable_fails_closed_before_any_drawing_copy_is_ever_attempted()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        // Unreadable from the very first call - a missing/unreadable "before"
        // hash must never be treated as "nothing to compare, so this
        // passes". P6D ROUND 4 (item A): the pre-flight baseline capture now
        // catches this BEFORE any drawing copy call, not merely at a later
        // recheck.
        h.Hasher.ThrowForPath.Add(model.ProposedDestinationAbsolutePath!);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingPhysicalCopyFailed, result.Outcome);
        Assert.Contains("pre-mutation integrity baseline", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        // the MODEL is still physically copied (models are unconditionally
        // processed before drawings) - only the DRAWING's own physical copy
        // must never be attempted.
        Assert.DoesNotContain("cad-drawing", h.Copier.Calls);
    }

    [Fact]
    public async Task Referenced_model_integrity_is_unaffected_when_no_drawing_is_copied()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { model });
        h.ExistingSources.Add(model.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
    }

    // ======================================================================
    // P6D ROUND 3, CRITICAL items A+B: two EXPLICIT, never-substituted
    // integrity sets - the SOURCE reference set (a drawing's ORIGINAL model
    // path(s), at risk during physical SaveAs, BEFORE rewiring) and the
    // FINAL target set (the planned post-rewiring path(s)) - and fail-closed
    // pre-flight resolution before ANY drawing physical mutation.
    // ======================================================================

    [Fact]
    public async Task A_COPY_childs_ORIGINAL_source_is_protected_even_though_its_destination_never_changes()
    {
        // Regression test for the exact ROUND-2 bug: the integrity baseline
        // used to be built from ExpectedTargetAbsolutePath (the COPY
        // destination) only - a mutation of the drawing's ACTUAL reference
        // (the model's ORIGINAL source path, still what the drawing points
        // at during SaveAs, before any rewiring) would have gone completely
        // undetected. Here the destination is left untouched throughout.
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Hasher.SequencedHashesFor[model.SourceAbsolutePath] =
            new Queue<string>(new[] { "hash-before", "hash-after-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        // P6D ROUND 4 (item C): once a SOURCE INTEGRITY VIOLATION is
        // detected, ZERO further materialization happens for ANY not-yet-
        // materialized entry - the model INCLUDED, even though the model's
        // OWN file was never itself corrupted by this operation. Detected
        // here by the per-drawing PRE-SaveAs recheck (item B step 1).
        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("a.ipt", result.FailureReason);
        Assert.Contains("source", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        var modelOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-model-new");
        Assert.Null(modelOutcome.Materialization);
    }

    [Fact]
    public async Task The_FINAL_target_set_is_never_a_substitute_for_the_SOURCE_set_a_target_only_mutation_is_still_caught_late()
    {
        // The inverse of the test above: only the COPY destination mutates,
        // the ORIGINAL source is untouched throughout - proves the FINAL
        // target set is tracked as its own, independent, non-discarded set
        // (caught by the final PHASE 6b check, since the destination isn't
        // written until AFTER the physical copy step).
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Hasher.SequencedHashesFor[model.ProposedDestinationAbsolutePath!] =
            new Queue<string>(new[] { "hash-before", "hash-after-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("a-new.ipt", result.FailureReason);
        Assert.Contains("final target", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_REUSE_childs_single_path_is_protected_as_both_its_own_source_and_final_target()
    {
        var h = new OrchestratorHarness();
        var modelReuse = ReuseNode("cad-model", @"C:\src\std-bolt.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, modelReuse, modelIsCopy: false) };
        var plan = Plan(new[] { modelReuse, drawing }, edges);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Hasher.SequencedHashesFor[modelReuse.SourceAbsolutePath] =
            new Queue<string>(new[] { "hash-before", "hash-after-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("std-bolt.ipt", result.FailureReason);
    }

    [Fact]
    public async Task A_drawing_referencing_a_mix_of_COPY_and_REUSE_models_protects_both_independently()
    {
        var h = new OrchestratorHarness();
        var modelCopy = CopyNode("cad-model-copy", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var modelReuse = ReuseNode("cad-model-reuse", @"C:\src\std-bolt.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\root.idw", @"C:\dst\root-new.idw", CadDocumentType.Idw);
        var edges = new[]
        {
            DrawingModelEdge(drawing, modelCopy, modelIsCopy: true),
            DrawingModelEdge(drawing, modelReuse, modelIsCopy: false),
        };
        var plan = Plan(new[] { modelCopy, modelReuse, drawing }, edges);
        h.ExistingSources.Add(modelCopy.SourceAbsolutePath);
        h.ExistingSources.Add(modelReuse.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        // Only the REUSE model's source mutates - the COPY model is entirely
        // healthy throughout - confirms REUSE protection is never skipped or
        // masked just because a COPY child is ALSO present on the same edge set.
        h.Hasher.SequencedHashesFor[modelReuse.SourceAbsolutePath] =
            new Queue<string>(new[] { "hash-before", "hash-after-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("std-bolt.ipt", result.FailureReason);
    }

    [Fact]
    public async Task Every_model_a_drawing_references_is_protected_not_merely_the_first_one_resolved()
    {
        var h = new OrchestratorHarness();
        var modelA = CopyNode("cad-model-a", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var modelB = CopyNode("cad-model-b", @"C:\src\b.ipt", @"C:\dst\b-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\root.idw", @"C:\dst\root-new.idw", CadDocumentType.Idw);
        var edges = new[]
        {
            DrawingModelEdge(drawing, modelA, modelIsCopy: true),
            DrawingModelEdge(drawing, modelB, modelIsCopy: true),
        };
        var plan = Plan(new[] { modelA, modelB, drawing }, edges);
        h.ExistingSources.Add(modelA.SourceAbsolutePath);
        h.ExistingSources.Add(modelB.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        // Only the SECOND model's source mutates - proves the loop protects
        // every referenced model, not just the first one it happens to check.
        h.Hasher.SequencedHashesFor[modelB.SourceAbsolutePath] =
            new Queue<string>(new[] { "hash-before", "hash-after-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("b.ipt", result.FailureReason);
    }

    [Fact]
    public async Task A_blank_model_reference_path_fails_closed_before_any_drawing_mutation()
    {
        var h = new OrchestratorHarness();
        // A REUSE model with a blank/whitespace source path - structurally
        // possible if a scan ever produced an unresolvable canonical path -
        // must never be silently treated as "no reference to protect".
        var modelReuseBlank = ReuseNode("cad-model-blank", "   ");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, modelReuseBlank, modelIsCopy: false) };
        var plan = Plan(new[] { modelReuseBlank, drawing }, edges);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingReferenceRewiringFailed, result.Outcome);
        Assert.Contains("blank", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        // no drawing physical copy was ever attempted (there is no model to
        // copy in this plan, so the physical copier is never invoked at all).
        Assert.Empty(h.Copier.Calls);
    }

    [Fact]
    public async Task An_unresolved_or_unsafe_drawing_model_edge_fails_closed_before_any_drawing_mutation()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var unresolvedEdge = new CopyDesignEdge(
            drawing.SourceAbsolutePath, model.SourceAbsolutePath, CadRelationshipKind.DrawingModel,
            CopyDesignEdgeDisposition.UnresolvedOrUnsafe);
        var plan = Plan(new[] { model, drawing }, new[] { unresolvedEdge });
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingReferenceRewiringFailed, result.Outcome);
        // the MODEL is still physically copied (models are unconditionally
        // processed before drawings) - only the DRAWING's own physical copy
        // must never be attempted.
        Assert.DoesNotContain("cad-drawing", h.Copier.Calls);
    }

    [Fact]
    public async Task A_reference_resolution_failure_can_never_silently_become_an_empty_target_set_and_a_success()
    {
        // Regression test for the exact ROUND-2 bug: "is { Success: true } r
        // ? r.Targets : Array.Empty<...>()" silently converted ANY resolver
        // failure into "zero references to protect" and let the drawing
        // proceed to a normal (successful) physical copy/rewire/verify/
        // materialize. This must now be an outright operation failure - the
        // drawing must NEVER be materialized when its references could not
        // be safely established.
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var unresolvedEdge = new CopyDesignEdge(
            drawing.SourceAbsolutePath, model.SourceAbsolutePath, CadRelationshipKind.DrawingModel,
            CopyDesignEdgeDisposition.UnresolvedOrUnsafe);
        var plan = Plan(new[] { model, drawing }, new[] { unresolvedEdge });
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.NotEqual(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.NotEqual(CopyDesignApplyOutcome.SucceededWithMaterializationGaps, result.Outcome);
        // the drawing was never physically copied, never mind materialized.
        Assert.DoesNotContain("cad-drawing", h.Copier.Calls);
        var drawingEntry = result.Entries.SingleOrDefault(e => e.CadDocumentId == "cad-drawing-new");
        Assert.True(drawingEntry is null || drawingEntry.Materialization is null);
    }

    [Fact]
    public async Task A_source_reference_that_becomes_unreadable_immediately_after_physical_copy_fails_closed()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        // Readable for the model's own pre-mutation source-hash capture
        // (call 1), the integrity baseline capture (call 2), and the
        // per-drawing PRE-SaveAs recheck (call 3, item B step 1) - then
        // becomes unreadable/missing starting with the POST-SaveAs recheck
        // (call 4, item B step 3, immediately after the drawing's physical
        // copy, before rewiring).
        h.Hasher.ThrowStartingFromCall[model.SourceAbsolutePath] = 4;

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        // P6D ROUND 4 (item C): a violation detected here is a SOURCE
        // INTEGRITY VIOLATION - the model, though already verified, must
        // NOT be materialized.
        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("a.ipt", result.FailureReason);
        var modelOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-model-new");
        Assert.Null(modelOutcome.Materialization);
    }

    [Fact]
    public async Task Rewiring_reuses_the_exact_targets_the_source_protection_check_already_validated_never_re_resolving()
    {
        // Proves PHASE 5 consumes the SAME pre-resolved/pre-validated
        // drawingModelTargetsByNode set (never re-resolves independently) -
        // observable via the rewirer's own captured call arguments.
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        var capturedTargets = Assert.Single(h.DrawingRewirer.CapturedTargets["cad-drawing"]);
        Assert.Equal(model.SourceAbsolutePath, capturedTargets.OriginalChildAbsolutePath);
        Assert.Equal(model.ProposedDestinationAbsolutePath, capturedTargets.ExpectedTargetAbsolutePath);
    }

    // ======================================================================
    // P6D ROUND 4, CRITICAL item A: baseline capture itself must fail
    // closed BEFORE any drawing physical-copy call - never a deferred
    // discovery via a later comparison.
    // ======================================================================

    [Fact]
    public async Task An_unreadable_protected_path_at_baseline_time_means_the_drawing_copier_is_never_called()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        // NOT the model's own SourceAbsolutePath - that path is ALSO hashed
        // unconditionally by the pre-reservation "capture every COPY
        // source's hash before mutation" step (unrelated to this test), and
        // would fail there first (RevalidationFailed) rather than at the
        // item-A baseline capture this test targets. The model's own
        // DESTINATION (the FINAL TARGET for this drawing's reference) is
        // never touched by that earlier step.
        h.Hasher.ThrowForPath.Add(model.ProposedDestinationAbsolutePath!);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingPhysicalCopyFailed, result.Outcome);
        Assert.Contains("pre-mutation integrity baseline", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cad-drawing", h.Copier.Calls);
    }

    [Fact]
    public async Task A_missing_protected_path_at_baseline_time_means_the_drawing_copier_is_never_called()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        // The final target (the model's own destination) is what "goes
        // missing" here - simulated the same way as "unreadable" (the fake
        // hasher/size reader cannot distinguish "missing" from "unreadable"
        // any more than the real filesystem APIs the orchestrator actually
        // catches - IOException/FileNotFoundException/UnauthorizedAccessException
        // are all treated identically, by design).
        h.Hasher.ThrowForPath.Add(model.ProposedDestinationAbsolutePath!);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingPhysicalCopyFailed, result.Outcome);
        Assert.Contains("pre-mutation integrity baseline", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cad-drawing", h.Copier.Calls);
    }

    [Fact]
    public async Task One_bad_path_among_several_protected_paths_still_blocks_every_drawing_copy()
    {
        var h = new OrchestratorHarness();
        var modelA = CopyNode("cad-model-a", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var modelB = CopyNode("cad-model-b", @"C:\src\b.ipt", @"C:\dst\b-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\root.idw", @"C:\dst\root-new.idw", CadDocumentType.Idw);
        var edges = new[]
        {
            DrawingModelEdge(drawing, modelA, modelIsCopy: true),
            DrawingModelEdge(drawing, modelB, modelIsCopy: true),
        };
        var plan = Plan(new[] { modelA, modelB, drawing }, edges);
        h.ExistingSources.Add(modelA.SourceAbsolutePath);
        h.ExistingSources.Add(modelB.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        // Only modelB's DESTINATION (its final target) is unreadable -
        // everything else, including modelA entirely, is perfectly healthy.
        // (Not modelB's own SourceAbsolutePath - that is ALSO hashed by the
        // unrelated pre-reservation "capture every COPY source's hash"
        // step, which would fail first with a different outcome.)
        h.Hasher.ThrowForPath.Add(modelB.ProposedDestinationAbsolutePath!);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingPhysicalCopyFailed, result.Outcome);
        Assert.DoesNotContain("cad-drawing", h.Copier.Calls);
    }

    [Fact]
    public async Task All_valid_baselines_let_drawing_copying_begin_normally()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.Contains("cad-drawing", h.Copier.Calls);
    }

    // ======================================================================
    // P6D ROUND 4, MEDIUM/HIGH item B: per-drawing pre/post-SaveAs integrity
    // recheck - a violation on ONE drawing must stop the loop immediately,
    // never letting a subsequent drawing be copied.
    // ======================================================================

    [Fact]
    public async Task First_drawing_mutating_the_shared_source_after_its_own_SaveAs_means_the_second_drawing_is_never_copied()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing1 = CopyNode("cad-drawing-1", @"C:\src\d1.idw", @"C:\dst\d1-new.idw", CadDocumentType.Idw);
        var drawing2 = CopyNode("cad-drawing-2", @"C:\src\d2.idw", @"C:\dst\d2-new.idw", CadDocumentType.Idw);
        var edges = new[]
        {
            DrawingModelEdge(drawing1, model, modelIsCopy: true),
            DrawingModelEdge(drawing2, model, modelIsCopy: true),
        };
        var plan = Plan(new[] { model, drawing1, drawing2 }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing1.SourceAbsolutePath);
        h.ExistingSources.Add(drawing2.SourceAbsolutePath);
        // Calls against model.SourceAbsolutePath, in order: (1) the
        // unconditional pre-reservation "capture every COPY source's hash"
        // step, (2) the item-A baseline capture, (3) drawing-1's PRE-SaveAs
        // recheck (still unchanged), (4) drawing-1's POST-SaveAs recheck
        // ("hash-after-CHANGED" - violation, detected before drawing-2 is
        // ever touched).
        h.Hasher.SequencedHashesFor[model.SourceAbsolutePath] =
            new Queue<string>(new[] { "hash-before", "hash-before", "hash-before", "hash-after-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("cad-drawing-1", h.Copier.Calls);
        Assert.DoesNotContain("cad-drawing-2", h.Copier.Calls);
    }

    [Fact]
    public async Task A_clean_first_drawing_lets_the_second_drawing_proceed_normally()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing1 = CopyNode("cad-drawing-1", @"C:\src\d1.idw", @"C:\dst\d1-new.idw", CadDocumentType.Idw);
        var drawing2 = CopyNode("cad-drawing-2", @"C:\src\d2.idw", @"C:\dst\d2-new.idw", CadDocumentType.Idw);
        var edges = new[]
        {
            DrawingModelEdge(drawing1, model, modelIsCopy: true),
            DrawingModelEdge(drawing2, model, modelIsCopy: true),
        };
        var plan = Plan(new[] { model, drawing1, drawing2 }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing1.SourceAbsolutePath);
        h.ExistingSources.Add(drawing2.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.Contains("cad-drawing-1", h.Copier.Calls);
        Assert.Contains("cad-drawing-2", h.Copier.Calls);
    }

    [Fact]
    public async Task Second_drawing_mutating_the_shared_source_means_the_third_drawing_is_never_copied()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing1 = CopyNode("cad-drawing-1", @"C:\src\d1.idw", @"C:\dst\d1-new.idw", CadDocumentType.Idw);
        var drawing2 = CopyNode("cad-drawing-2", @"C:\src\d2.idw", @"C:\dst\d2-new.idw", CadDocumentType.Idw);
        var drawing3 = CopyNode("cad-drawing-3", @"C:\src\d3.idw", @"C:\dst\d3-new.idw", CadDocumentType.Idw);
        var edges = new[]
        {
            DrawingModelEdge(drawing1, model, modelIsCopy: true),
            DrawingModelEdge(drawing2, model, modelIsCopy: true),
            DrawingModelEdge(drawing3, model, modelIsCopy: true),
        };
        var plan = Plan(new[] { model, drawing1, drawing2, drawing3 }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing1.SourceAbsolutePath);
        h.ExistingSources.Add(drawing2.SourceAbsolutePath);
        h.ExistingSources.Add(drawing3.SourceAbsolutePath);
        // Calls against model.SourceAbsolutePath, in order: (1) the
        // unconditional pre-reservation source-hash capture, (2) the item-A
        // baseline capture, (3)/(4) drawing-1's PRE/POST-SaveAs rechecks
        // (unchanged), (5) drawing-2's PRE-SaveAs recheck (still unchanged),
        // (6) drawing-2's POST-SaveAs recheck ("hash-after-CHANGED" -
        // violation, detected before drawing-3 is ever touched).
        h.Hasher.SequencedHashesFor[model.SourceAbsolutePath] = new Queue<string>(
            new[] { "hash-before", "hash-before", "hash-before", "hash-before", "hash-before", "hash-after-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("cad-drawing-1", h.Copier.Calls);
        Assert.Contains("cad-drawing-2", h.Copier.Calls);
        Assert.DoesNotContain("cad-drawing-3", h.Copier.Calls);
    }

    [Fact]
    public async Task Multiple_drawings_sharing_the_same_source_model_remain_deterministic_when_nothing_mutates()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing1 = CopyNode("cad-drawing-1", @"C:\src\d1.idw", @"C:\dst\d1-new.idw", CadDocumentType.Idw);
        var drawing2 = CopyNode("cad-drawing-2", @"C:\src\d2.idw", @"C:\dst\d2-new.idw", CadDocumentType.Idw);
        var edges = new[]
        {
            DrawingModelEdge(drawing1, model, modelIsCopy: true),
            DrawingModelEdge(drawing2, model, modelIsCopy: true),
        };
        var plan = Plan(new[] { model, drawing1, drawing2 }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing1.SourceAbsolutePath);
        h.ExistingSources.Add(drawing2.SourceAbsolutePath);

        var result1 = await h.Build().ExecuteAsync(plan, "key-1", null);

        var h2 = new OrchestratorHarness();
        h2.ExistingSources.Add(model.SourceAbsolutePath);
        h2.ExistingSources.Add(drawing1.SourceAbsolutePath);
        h2.ExistingSources.Add(drawing2.SourceAbsolutePath);
        var result2 = await h2.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result1.Outcome);
        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result2.Outcome);
        Assert.Equal(result1.Entries.Select(e => e.CadDocumentId).OrderBy(x => x),
            result2.Entries.Select(e => e.CadDocumentId).OrderBy(x => x));
    }

    // ======================================================================
    // P6D ROUND 4, HIGH item C: a detected SOURCE INTEGRITY VIOLATION must
    // stop ALL later materialization - stronger than an ordinary drawing
    // failure, which still lets already-verified models materialize.
    // ======================================================================

    [Fact]
    public async Task Source_mutation_detected_immediately_after_drawing_copy_means_zero_materializer_calls()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Hasher.ThrowStartingFromCall[model.SourceAbsolutePath] = 4; // survives baseline + pre-check, fails at post-check

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Empty(h.Materializer.Calls);
    }

    [Fact]
    public async Task Source_mutation_detected_only_after_rewiring_final_target_check_means_zero_materializer_calls()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        // Only the FINAL TARGET (the model's own destination) mutates - not
        // caught until PHASE 6b, AFTER rewiring has already happened.
        h.Hasher.SequencedHashesFor[model.ProposedDestinationAbsolutePath!] =
            new Queue<string>(new[] { "hash-before", "hash-after-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Empty(h.Materializer.Calls);
    }

    [Fact]
    public async Task An_ordinary_drawing_rewire_failure_with_no_source_mutation_still_materializes_the_model_unlike_a_real_violation()
    {
        // Direct contrast with the two tests above: an ORDINARY drawing
        // failure (no integrity violation involved at all) preserves the
        // existing, approved "models already verified may still be
        // materialized" policy - only a genuine SOURCE INTEGRITY VIOLATION
        // suppresses it.
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.DrawingRewirer.FailFor.Add("cad-drawing");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingReferenceRewiringFailed, result.Outcome);
        var modelOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-model-new");
        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, modelOutcome.Materialization!.Outcome);
        Assert.Contains("cad-model-new", h.Materializer.Calls);
    }

    [Fact]
    public async Task Already_materialized_resume_entries_remain_untouched_when_a_later_source_integrity_violation_is_detected()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing1 = CopyNode("cad-drawing-1", @"C:\src\d1.idw", @"C:\dst\d1-new.idw", CadDocumentType.Idw);
        var drawing2 = CopyNode("cad-drawing-2", @"C:\src\d2.idw", @"C:\dst\d2-new.idw", CadDocumentType.Idw);
        var edges = new[]
        {
            DrawingModelEdge(drawing1, model, modelIsCopy: true),
            DrawingModelEdge(drawing2, model, modelIsCopy: true),
        };
        var plan = Plan(new[] { model, drawing1, drawing2 }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing1.SourceAbsolutePath);
        h.ExistingSources.Add(drawing2.SourceAbsolutePath);

        var modelDest = model.ProposedDestinationAbsolutePath!;
        h.ExistingDestinations.Add(modelDest);
        h.FileSizeFor[modelDest] = 111L;
        h.Hasher.SequencedHashesFor[modelDest] = new Queue<string>(new[] { new string('a', 64) });
        h.Reservation.OnResponded = (request, response) =>
        {
            foreach (var entry in response.Entries)
            {
                h.OperationStatusClient.AddPendingIfAbsent(
                    entry.Action == CopyDesignApplyEntryAction.Copy, entry.SourceCadDocumentId,
                    entry.ResultingCadDocumentId, entry.DocumentNumber, entry.FileName, entry.DocumentType);
            }
            h.OperationStatusClient.OperationId = response.CopyDesignOperationId;
            h.OperationStatusClient.IdempotencyKey = request.IdempotencyKey;
            // model is already materialized from a prior attempt.
            h.OperationStatusClient.MarkMaterialized("cad-model-new", 1, new string('a', 64), 111L, "fv-resumed-1");
        };
        // drawing-1's source recheck mutates right after its own physical
        // copy - a genuine, freshly-detected violation for THIS attempt.
        // Calls against model.SourceAbsolutePath, in order: (1) step 4's
        // unconditional pre-reservation source-hash capture, (2) the item-A
        // baseline capture, (3) drawing-1's PRE-SaveAs recheck (unchanged),
        // (4) drawing-1's POST-SaveAs recheck (mutated).
        h.Hasher.SequencedHashesFor[model.SourceAbsolutePath] =
            new Queue<string>(new[] { "hash-before", "hash-before", "hash-before", "hash-after-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Empty(h.Materializer.Calls); // never a SECOND FileVersion 1, and no NEW materialization either
        var modelOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-model-new");
        Assert.Contains("Already materialized", modelOutcome.Materialization!.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("fv-resumed-1", modelOutcome.Materialization!.FileVersionId);
    }

    // P6D ROUND 4, item F: the orchestrator must consume ONLY a status
    // response that has passed COMPLETE client-side validation - a
    // malformed response (here: standing in for "the client detected an
    // extra/unexpected entry and reported MalformedResponse", exactly what
    // HttpCopyDesignOperationStatusProbeTests proves the REAL client does)
    // must result in ZERO physical mutation across every adapter, not merely
    // the physical copier.
    [Fact]
    public async Task A_malformed_operation_status_response_causes_zero_physical_work_across_every_adapter()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        // Standing in for the client having already detected an internally
        // inconsistent (e.g. extra-entry) response and refusing to report
        // Found - see HttpCopyDesignOperationStatusProbeTests for the exact
        // wire-level scenarios that produce this.
        h.OperationStatusClient.ForcedResult = () =>
            CopyDesignOperationStatusResult.Failure(CopyDesignOperationStatusOutcome.MalformedResponse);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.UnexpectedMaterializationState, result.Outcome);
        Assert.Empty(h.Copier.Calls);
        Assert.Empty(h.Rewirer.Calls);
        Assert.Empty(h.Verifier.Calls);
        Assert.Empty(h.DrawingRewirer.Calls);
        Assert.Empty(h.DrawingVerifier.Calls);
        Assert.Empty(h.Materializer.Calls);
        // the reservation itself is still durable and reported, never hidden.
        Assert.NotNull(result.CopyDesignOperationId);
    }

    // ======================================================================
    // P6D ROUND 5, item C: the post-attempt source-integrity recheck runs
    // UNCONDITIONALLY immediately after the drawing physical-copy attempt
    // returns OR throws - never skipped just because the attempt itself
    // failed. A violation found here ALWAYS takes precedence over an
    // ordinary physical-copy failure outcome.
    //
    // Every test below configures model.SourceAbsolutePath's hash queue with
    // FOUR entries, matching the four real reads of that path in this
    // scenario, in order: (1) the unconditional pre-reservation source-hash
    // capture (step 4), (2) the item-A baseline capture, (3) the per-drawing
    // PRE-SaveAs recheck (item B step 1), (4) the per-drawing POST-attempt
    // recheck (item B/C step 3/C, now unconditional). "Unchanged" queues all
    // four identically; "changed" queues a different 4th value.
    // ======================================================================

    private static (CopyDesignNode Model, CopyDesignNode Drawing, CopyDesignEdge[] Edges, CopyDesignPlan Plan) OneModelOneDrawingScenario()
    {
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        return (model, drawing, edges, plan);
    }

    [Fact]
    public async Task Case1_Copy_succeeds_and_integrity_unchanged_proceeds_normally_to_success()
    {
        var (model, drawing, _, plan) = OneModelOneDrawingScenario();
        var h = new OrchestratorHarness();
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Hasher.SequencedHashesFor[model.SourceAbsolutePath] =
            new Queue<string>(new[] { "h", "h", "h", "h", "h" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.Contains("cad-drawing", h.Copier.Calls);
    }

    [Fact]
    public async Task Case2_Copy_succeeds_but_integrity_changed_reports_SourceIntegrityViolation()
    {
        var (model, drawing, _, plan) = OneModelOneDrawingScenario();
        var h = new OrchestratorHarness();
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Hasher.SequencedHashesFor[model.SourceAbsolutePath] =
            new Queue<string>(new[] { "h", "h", "h", "h-CHANGED", "h-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        // the physical copy DID succeed and was journalled - cleanup must
        // know about it (proved indirectly: the drawing's own destination
        // was recorded as created, so a real implementation's cleanup would
        // remove it; here we confirm the copier was actually invoked).
        Assert.Contains("cad-drawing", h.Copier.Calls);
        Assert.Empty(h.Materializer.Calls);
    }

    [Fact]
    public async Task Case3_Copy_returns_failure_and_integrity_unchanged_reports_the_ordinary_physical_copy_failure()
    {
        var (model, drawing, _, plan) = OneModelOneDrawingScenario();
        var h = new OrchestratorHarness();
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Hasher.SequencedHashesFor[model.SourceAbsolutePath] =
            new Queue<string>(new[] { "h", "h", "h", "h", "h" });
        h.Copier.FailFor.Add("cad-drawing");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingPhysicalCopyFailed, result.Outcome);
        Assert.Contains("simulated copy failure", result.FailureReason);
    }

    [Fact]
    public async Task Case4_Copy_returns_failure_but_integrity_changed_SourceIntegrityViolation_takes_precedence()
    {
        var (model, drawing, _, plan) = OneModelOneDrawingScenario();
        var h = new OrchestratorHarness();
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Hasher.SequencedHashesFor[model.SourceAbsolutePath] =
            new Queue<string>(new[] { "h", "h", "h", "h-CHANGED", "h-CHANGED" });
        h.Copier.FailFor.Add("cad-drawing");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("simulated copy failure", result.FailureReason); // the underlying failure is still mentioned
        Assert.Empty(h.Materializer.Calls);
    }

    [Fact]
    public async Task Case5_Copy_throws_and_integrity_unchanged_reports_a_controlled_physical_copy_failure()
    {
        var (model, drawing, _, plan) = OneModelOneDrawingScenario();
        var h = new OrchestratorHarness();
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Hasher.SequencedHashesFor[model.SourceAbsolutePath] =
            new Queue<string>(new[] { "h", "h", "h", "h", "h" });
        h.Copier.ThrowFor.Add("cad-drawing");
        h.Copier.ExceptionToThrow = new InvalidOperationException("simulated SaveAs COM exception");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingPhysicalCopyFailed, result.Outcome);
        Assert.Contains("simulated SaveAs COM exception", result.FailureReason);
    }

    [Fact]
    public async Task Case6_Copy_throws_but_integrity_changed_SourceIntegrityViolation_takes_precedence()
    {
        var (model, drawing, _, plan) = OneModelOneDrawingScenario();
        var h = new OrchestratorHarness();
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.Hasher.SequencedHashesFor[model.SourceAbsolutePath] =
            new Queue<string>(new[] { "h", "h", "h", "h-CHANGED", "h-CHANGED" });
        h.Copier.ThrowFor.Add("cad-drawing");
        h.Copier.ExceptionToThrow = new InvalidOperationException("simulated SaveAs COM exception");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("simulated SaveAs COM exception", result.FailureReason); // the underlying failure is still mentioned
        Assert.Empty(h.Materializer.Calls);
    }

    [Fact]
    public async Task No_later_drawing_is_ever_copied_before_the_post_attempt_check_of_an_earlier_one_completes()
    {
        // Two drawings sharing one source model - the FIRST drawing's own
        // post-attempt check (case 2 above) must stop the loop before the
        // SECOND drawing's copier is ever invoked, exactly like round 4's
        // per-drawing checks already proved for the pre-SaveAs case.
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing1 = CopyNode("cad-drawing-1", @"C:\src\d1.idw", @"C:\dst\d1-new.idw", CadDocumentType.Idw);
        var drawing2 = CopyNode("cad-drawing-2", @"C:\src\d2.idw", @"C:\dst\d2-new.idw", CadDocumentType.Idw);
        var edges = new[]
        {
            DrawingModelEdge(drawing1, model, modelIsCopy: true),
            DrawingModelEdge(drawing2, model, modelIsCopy: true),
        };
        var plan = Plan(new[] { model, drawing1, drawing2 }, edges);
        var h = new OrchestratorHarness();
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing1.SourceAbsolutePath);
        h.ExistingSources.Add(drawing2.SourceAbsolutePath);
        // step4, baseline, drawing-1 pre-check, drawing-1 post-check(CHANGED).
        h.Hasher.SequencedHashesFor[model.SourceAbsolutePath] =
            new Queue<string>(new[] { "h", "h", "h", "h-CHANGED", "h-CHANGED" });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.SourceIntegrityViolation, result.Outcome);
        Assert.Contains("cad-drawing-1", h.Copier.Calls);
        Assert.DoesNotContain("cad-drawing-2", h.Copier.Calls);
    }

    // ======================================================================
    // P6D ROUND 5, item D: a dedicated test where the ORIGINAL SOURCE
    // reference specifically becomes unreadable between the earlier general
    // pre-reservation source hash (step 4) and the item-A drawing baseline
    // capture - a small, previously-uncovered gap between two existing,
    // already-tested mechanisms.
    // ======================================================================

    [Fact]
    public async Task A_source_that_becomes_unreadable_between_the_general_source_hash_and_the_drawing_baseline_capture_fails_closed()
    {
        var (model, drawing, _, plan) = OneModelOneDrawingScenario();
        var h = new OrchestratorHarness();
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        // Call 1 (step 4's unconditional pre-reservation source-hash
        // capture) succeeds; call 2 (the item-A drawing baseline capture)
        // throws - the file became unreadable in between.
        h.Hasher.ThrowStartingFromCall[model.SourceAbsolutePath] = 2;

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingPhysicalCopyFailed, result.Outcome);
        Assert.Contains("pre-mutation integrity baseline", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cad-drawing", h.Copier.Calls);
    }

    // ======================================================================
    // P6D ROUND 2, MEDIUM item 4: REUSE outcomes must always be reported,
    // even when a later drawing COPY fails.
    // ======================================================================

    [Fact]
    public async Task COPY_plus_REUSE_plus_drawing_failure_still_produces_a_complete_result_list_including_REUSE()
    {
        var h = new OrchestratorHarness();
        var modelCopy = CopyNode("cad-model-copy", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var modelReuse = ReuseNode("cad-model-reuse", @"C:\src\std-bolt.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\root.idw", @"C:\dst\root-new.idw", CadDocumentType.Idw);
        var edges = new[]
        {
            DrawingModelEdge(drawing, modelCopy, modelIsCopy: true),
            DrawingModelEdge(drawing, modelReuse, modelIsCopy: false),
        };
        var plan = Plan(new[] { modelCopy, modelReuse, drawing }, edges);
        h.ExistingSources.Add(modelCopy.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);
        h.DrawingVerifier.FailFor.Add("cad-drawing");

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingVerificationFailed, result.Outcome);

        // no phantom success for the failed drawing.
        var drawingOutcome = result.Entries.SingleOrDefault(e => e.CadDocumentId == "cad-drawing-new");
        Assert.Null(drawingOutcome?.Materialization);

        // the materialized model COPY remains visible.
        var modelOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-model-copy-new");
        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, modelOutcome.Materialization!.Outcome);

        // the REUSE entry is present with the correct identity - THIS is the
        // fix: previously omitted entirely on a drawing failure.
        var reuseOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-model-reuse");
        Assert.Equal(CopyDesignApplyEntryAction.Reuse, reuseOutcome.Action);
        Assert.False(reuseOutcome.PhysicallyCreated);
        Assert.True(reuseOutcome.VerificationPassed);
        Assert.Null(reuseOutcome.Materialization);

        Assert.Equal(3, result.Entries.Count);
    }

    [Fact]
    public async Task REUSE_entries_are_reported_for_every_drawing_failure_kind_physical_copy_rewire_and_verify()
    {
        foreach (var failureMode in new[] { "copy", "rewire", "verify" })
        {
            var h = new OrchestratorHarness();
            var modelReuse = ReuseNode("cad-model-reuse", @"C:\src\std-bolt.ipt");
            var drawing = CopyNode("cad-drawing", @"C:\src\root.idw", @"C:\dst\root-new.idw", CadDocumentType.Idw);
            var edges = new[] { DrawingModelEdge(drawing, modelReuse, modelIsCopy: false) };
            var plan = Plan(new[] { modelReuse, drawing }, edges);
            h.ExistingSources.Add(drawing.SourceAbsolutePath);

            switch (failureMode)
            {
                case "copy": h.Copier.FailFor.Add("cad-drawing"); break;
                case "rewire": h.DrawingRewirer.FailFor.Add("cad-drawing"); break;
                case "verify": h.DrawingVerifier.FailFor.Add("cad-drawing"); break;
            }

            var result = await h.Build().ExecuteAsync(plan, "key-1", null);

            Assert.True(result.Outcome is CopyDesignApplyOutcome.DrawingPhysicalCopyFailed
                or CopyDesignApplyOutcome.DrawingReferenceRewiringFailed
                or CopyDesignApplyOutcome.DrawingVerificationFailed, $"unexpected outcome for {failureMode}: {result.Outcome}");
            var reuseOutcome = result.Entries.SingleOrDefault(e => e.CadDocumentId == "cad-model-reuse");
            Assert.NotNull(reuseOutcome);
            Assert.Equal(CopyDesignApplyEntryAction.Reuse, reuseOutcome!.Action);
        }
    }

    // ======================================================================
    // P6D ROUND 2, HIGH: RESUME - calling ExecuteAsync again with the SAME
    // plan and the SAME idempotencyKey safely continues a partially-failed
    // attempt, skipping whatever a prior attempt already materialized.
    // ======================================================================

    // A canonical (64 lowercase hex) fake hash - AuthoritativeLatestVersion.HasCanonicalIntegrity
    // requires this exact shape; FakeHasher's own default ("hash:" + path)
    // is deliberately NOT canonical, so every resume test that expects a
    // MATCH must explicitly make the LOCAL read return one of these via
    // SequencedHashesFor.
    private static readonly string CanonicalHashA = new('a', 64);
    private static readonly string CanonicalHashB = new('b', 64);

    /// <summary>Configures the harness so hashing <paramref name="path"/>
    ///  locally returns <paramref name="canonicalHash"/> - use together with
    ///  <see cref="FakeOperationStatusClient.MarkMaterialized"/> to simulate a
    ///  genuinely matching already-materialized target.</summary>
    private static void SetLocalHash(OrchestratorHarness h, string path, string canonicalHash) =>
        h.Hasher.SequencedHashesFor[path] = new Queue<string>(new[] { canonicalHash });

    [Fact]
    public async Task Zero_entries_materialized_resume_behaves_exactly_like_a_normal_fresh_apply()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.Contains("cad-1", h.Copier.Calls);
        Assert.Contains("cad-1-new", h.Materializer.Calls);
    }

    // required test: model materialized + drawing pending => resume drawing only
    [Fact]
    public async Task Model_already_materialized_and_drawing_pending_resumes_the_drawing_only()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        var modelDest = model.ProposedDestinationAbsolutePath!;
        h.ExistingDestinations.Add(modelDest);
        h.FileSizeFor[modelDest] = 123L;
        SetLocalHash(h, modelDest, CanonicalHashA);
        h.Reservation.OnResponded = (request, response) =>
        {
            foreach (var entry in response.Entries)
            {
                h.OperationStatusClient.AddPendingIfAbsent(
                    entry.Action == CopyDesignApplyEntryAction.Copy, entry.SourceCadDocumentId,
                    entry.ResultingCadDocumentId, entry.DocumentNumber, entry.FileName, entry.DocumentType);
            }
            h.OperationStatusClient.OperationId = response.CopyDesignOperationId;
            h.OperationStatusClient.IdempotencyKey = request.IdempotencyKey;
            h.OperationStatusClient.MarkMaterialized("cad-model-new", 1, CanonicalHashA, 123L, "fv-resumed-1");
        };

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain("cad-model", h.Copier.Calls);
        Assert.Contains("cad-drawing", h.Copier.Calls);
        Assert.DoesNotContain("cad-model-new", h.Materializer.Calls); // required: no second FileVersion 1
        Assert.Contains("cad-drawing-new", h.Materializer.Calls);

        var modelOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-model-new");
        Assert.Contains("Already materialized", modelOutcome.Materialization!.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("fv-resumed-1", modelOutcome.Materialization!.FileVersionId);
        var drawingOutcome = result.Entries.Single(e => e.CadDocumentId == "cad-drawing-new");
        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, drawingOutcome.Materialization!.Outcome);
    }

    // ======================================================================
    // P6D LIVE ACCEPTANCE REGRESSION: the EXACT live sequence from the
    // "PARTIAL OPERATION RESUME COMMAND DISABLED" round - 4 COPY entries (3
    // models + 1 drawing), the 3 models genuinely materialize via a REAL
    // (first) ExecuteAsync call, the drawing's physical copy fails (source
    // Dirty), and a SECOND ExecuteAsync call with the SAME idempotency key
    // (a real Resume) must reuse the SAME operation, skip all 3 already-
    // materialized models entirely (no copier/rewirer/materializer calls for
    // them), and carry ONLY the drawing through copy -> rewire -> verify ->
    // materialize to a final Succeeded outcome. UNLIKE the other resume
    // tests in this file (which simulate resume-status in a SINGLE call via
    // manual MarkMaterialized seeding), this test runs TWO real orchestrator
    // calls on the SAME harness and feeds phase 2's status from phase 1's
    // OWN genuine materialization output - proving the actual end-to-end
    // mechanics the local `_resumableCopyDesignAttempt` handle + Resume
    // command are meant to drive (the controller-level enablement bug that
    // masked this working mechanism is covered separately by
    // RibbonCommandPolicyTests).
    // ======================================================================
    [Fact]
    public async Task LIVE_SEQUENCE_three_models_materialize_drawing_fails_dirty_then_resume_completes_only_the_drawing()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        var rootModel = CopyNode("cad-root", @"C:\src\P6D-REAL-ROOT.iam", @"C:\dst\P6DNEW-REAL-ROOT.iam", CadDocumentType.Iam, isRoot: true);
        var partA = CopyNode("cad-part-a", @"C:\src\P6D-REAL-PART-A.ipt", @"C:\dst\P6DNEW-REAL-PART-A.ipt");
        var partB = CopyNode("cad-part-b", @"C:\src\P6D-REAL-PART-B.ipt", @"C:\dst\P6DNEW-REAL-PART-B.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\P6D-REAL-ROOT.idw", @"C:\dst\P6DNEW-REAL-ROOT.idw", CadDocumentType.Idw);
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
        // Canonical (64-char lowercase hex) content hashes for the 3 model
        // destinations - the default fake hasher's "hash:<path>" shape is
        // NOT canonical, so the resume-time integrity check (which requires
        // a genuine FileVersion 1 identity, not just any string) would
        // reject it. Queued many times over (unlike SetLocalHash's single
        // value) because BOTH phase 1 (post-copy verification) AND phase 2
        // (the resume-time re-hash) read the SAME path - it must stay
        // canonical across every read, not just the first.
        static void SetStableCanonicalHash(OrchestratorHarness harness, string path, string hash) =>
            harness.Hasher.SequencedHashesFor[path] = new Queue<string>(Enumerable.Repeat(hash, 10));
        SetStableCanonicalHash(h, rootModel.ProposedDestinationAbsolutePath!, new string('1', 64));
        SetStableCanonicalHash(h, partA.ProposedDestinationAbsolutePath!, new string('2', 64));
        SetStableCanonicalHash(h, partB.ProposedDestinationAbsolutePath!, new string('3', 64));
        h.FileSizeFor[rootModel.ProposedDestinationAbsolutePath!] = 74752L;
        h.FileSizeFor[partA.ProposedDestinationAbsolutePath!] = 80896L;
        h.FileSizeFor[partB.ProposedDestinationAbsolutePath!] = 80896L;
        // Simulates the physical copy failing because the source .idw was
        // Dirty in Inventor - at THIS layer (adapter-agnostic) that is
        // reported exactly like any other physical-copy failure; the
        // Dirty-specific check itself lives in the real Inventor adapter,
        // already covered elsewhere and out of scope for this Core-layer test.
        h.Copier.FailFor.Add("cad-drawing");

        // ---- PHASE 1: the original, genuine Apply -------------------------
        var result1 = await h.Build().ExecuteAsync(plan, "key-p6d-live", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingPhysicalCopyFailed, result1.Outcome);
        Assert.Equal("op-1", result1.CopyDesignOperationId);
        Assert.True(result1.ReservedButUnmaterialized); // this is what makes _resumableCopyDesignAttempt get retained
        Assert.Single(h.Reservation.Calls);

        var rootOutcome1 = result1.Entries.Single(e => e.CadDocumentId == "cad-root-new");
        var partAOutcome1 = result1.Entries.Single(e => e.CadDocumentId == "cad-part-a-new");
        var partBOutcome1 = result1.Entries.Single(e => e.CadDocumentId == "cad-part-b-new");
        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, rootOutcome1.Materialization!.Outcome);
        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, partAOutcome1.Materialization!.Outcome);
        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, partBOutcome1.Materialization!.Outcome);
        var drawingOutcome1 = result1.Entries.SingleOrDefault(e => e.CadDocumentId == "cad-drawing-new");
        Assert.Null(drawingOutcome1?.Materialization); // drawing never reached materialization

        // ---- bridge phase 1 -> phase 2: the operation-status authority now
        //      genuinely reports what phase 1 actually did (mirrors what a
        //      real server GET .../status call would report from the real
        //      FileVersion rows phase 1's materializer just created). The
        //      FileVersionId/VersionNumber are taken verbatim from phase 1's
        //      OWN result; the sha256/fileSize use the SAME canonical values
        //      configured on h.Hasher/h.FileSizeFor above (FakeVerifier's
        //      internal "result-hash" placeholder is not itself a canonical
        //      64-hex digest, so it is never propagated as if it were one -
        //      the canonical values ARE what a real server-computed
        //      FileVersion hash would look like, and phase 2's local re-hash
        //      below reads the SAME configured values, exactly modeling "the
        //      file on disk still matches the server-authoritative digest"). -
        void MarkFromPhase1(string resultingId, CopyDesignApplyEntryOutcome outcome, string canonicalSha256, long canonicalFileSize) =>
            h.OperationStatusClient.MarkMaterialized(
                resultingId, outcome.Materialization!.VersionNumber!.Value, canonicalSha256,
                canonicalFileSize, outcome.Materialization!.FileVersionId!);
        MarkFromPhase1("cad-root-new", rootOutcome1, new string('1', 64), 74752L);
        MarkFromPhase1("cad-part-a-new", partAOutcome1, new string('2', 64), 80896L);
        MarkFromPhase1("cad-part-b-new", partBOutcome1, new string('3', 64), 80896L);

        // Confirm the resume status now sees EXACTLY the expected conceptual
        // state: 3 MATERIALIZED, 1 PENDING (the drawing was auto-populated
        // PENDING by the harness when the reservation first responded, and
        // nothing has marked it otherwise).
        Assert.Equal(3, h.OperationStatusClient.Entries.Count(e => e.State == CopyDesignOperationEntryState.Materialized));
        Assert.Equal(1, h.OperationStatusClient.Entries.Count(e => e.State == CopyDesignOperationEntryState.Pending));

        // The 3 models' destination files genuinely exist on disk from
        // phase 1's real (fake) copy - the harness tracks "exists on disk"
        // as its own manually-controlled set, independent of the copier fake.
        h.ExistingDestinations.Add(rootModel.ProposedDestinationAbsolutePath!);
        h.ExistingDestinations.Add(partA.ProposedDestinationAbsolutePath!);
        h.ExistingDestinations.Add(partB.ProposedDestinationAbsolutePath!);

        // The engineer saved the drawing (no longer Dirty) before resuming.
        h.Copier.FailFor.Remove("cad-drawing");
        var copierCallsBeforeResume = h.Copier.Calls.Count;
        var rewirerCallsBeforeResume = h.Rewirer.Calls.Count;
        var materializerCallsBeforeResume = h.Materializer.Calls.Count;

        // ---- PHASE 2: Resume - the SAME idempotency key, the SAME plan ----
        var result2 = await h.Build().ExecuteAsync(plan, "key-p6d-live", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result2.Outcome);
        Assert.Equal("op-1", result2.CopyDesignOperationId); // SAME operation - never a new reservation
        Assert.False(result2.ReservedButUnmaterialized);
        Assert.Equal(2, h.Reservation.Calls.Count); // idempotent REPLAY of the SAME request, not a fresh one
        Assert.Equal("key-p6d-live", h.Reservation.Calls[1].IdempotencyKey);
        Assert.Equal(h.Reservation.Calls[0].IdempotencyKey, h.Reservation.Calls[1].IdempotencyKey);

        // required: no model copier/materializer/rewirer calls during resume.
        var copierCallsDuringResume = h.Copier.Calls.Skip(copierCallsBeforeResume).ToArray();
        var rewirerCallsDuringResume = h.Rewirer.Calls.Skip(rewirerCallsBeforeResume).ToArray();
        var materializerCallsDuringResume = h.Materializer.Calls.Skip(materializerCallsBeforeResume).ToArray();
        Assert.DoesNotContain("cad-root", copierCallsDuringResume);
        Assert.DoesNotContain("cad-part-a", copierCallsDuringResume);
        Assert.DoesNotContain("cad-part-b", copierCallsDuringResume);
        Assert.DoesNotContain("cad-root", rewirerCallsDuringResume);
        Assert.DoesNotContain("cad-root-new", materializerCallsDuringResume);
        Assert.DoesNotContain("cad-part-a-new", materializerCallsDuringResume);
        Assert.DoesNotContain("cad-part-b-new", materializerCallsDuringResume);

        // required: the PENDING drawing proceeds through copy -> rewire ->
        // verify -> materialize, and ONLY the drawing.
        Assert.Contains("cad-drawing", copierCallsDuringResume);
        Assert.Contains("cad-drawing", h.DrawingRewirer.Calls);
        Assert.Contains("cad-drawing", h.DrawingVerifier.Calls);
        Assert.Contains("cad-drawing-new", materializerCallsDuringResume);

        // the 3 already-done entries are still reported, sourced from the
        // AUTHORITATIVE status, not from any new materializer call.
        var rootOutcome2 = result2.Entries.Single(e => e.CadDocumentId == "cad-root-new");
        Assert.Contains("Already materialized", rootOutcome2.Materialization!.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(rootOutcome1.Materialization!.FileVersionId, rootOutcome2.Materialization!.FileVersionId);
        var drawingOutcome2 = result2.Entries.Single(e => e.CadDocumentId == "cad-drawing-new");
        Assert.Equal(CopyDesignMaterializationOutcome.Materialized, drawingOutcome2.Materialization!.Outcome);
    }

    // required test: all entries materialized => idempotent completed result
    [Fact]
    public async Task All_entries_already_materialized_is_an_idempotent_completed_result_with_no_new_work()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        var model = CopyNode("cad-model", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawing = CopyNode("cad-drawing", @"C:\src\a.idw", @"C:\dst\a-drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        var modelDest = model.ProposedDestinationAbsolutePath!;
        var drawingDest = drawing.ProposedDestinationAbsolutePath!;
        h.ExistingDestinations.Add(modelDest);
        h.ExistingDestinations.Add(drawingDest);
        h.FileSizeFor[modelDest] = 111L;
        h.FileSizeFor[drawingDest] = 222L;
        SetLocalHash(h, modelDest, CanonicalHashA);
        SetLocalHash(h, drawingDest, CanonicalHashB);
        h.Reservation.OnResponded = (request, response) =>
        {
            foreach (var entry in response.Entries)
            {
                h.OperationStatusClient.AddPendingIfAbsent(
                    entry.Action == CopyDesignApplyEntryAction.Copy, entry.SourceCadDocumentId,
                    entry.ResultingCadDocumentId, entry.DocumentNumber, entry.FileName, entry.DocumentType);
            }
            h.OperationStatusClient.OperationId = response.CopyDesignOperationId;
            h.OperationStatusClient.IdempotencyKey = request.IdempotencyKey;
            h.OperationStatusClient.MarkMaterialized("cad-model-new", 1, CanonicalHashA, 111L, "fv-model-1");
            h.OperationStatusClient.MarkMaterialized("cad-drawing-new", 1, CanonicalHashB, 222L, "fv-drawing-1");
        };

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.Empty(h.Copier.Calls); // required: no model destination collision / re-copy on resume
        Assert.Empty(h.Rewirer.Calls);
        Assert.Empty(h.DrawingRewirer.Calls);
        Assert.Empty(h.Materializer.Calls); // required: no second FileVersion 1, for either entry
        Assert.Equal(2, result.Entries.Count);
    }

    // required test: malformed partial state => fail (version number != 1)
    [Fact]
    public async Task A_reserved_target_reporting_a_version_number_other_than_1_fails_closed()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        var dest = node.ProposedDestinationAbsolutePath!;
        h.ExistingDestinations.Add(dest);
        h.FileSizeFor[dest] = 50L;
        SetLocalHash(h, dest, CanonicalHashA);
        // VersionNumber 2 should be structurally impossible for a fresh Copy
        // Design target, but the orchestrator never trusts that blindly - it
        // is now caught by the ADAPTER-level shape check (never even a
        // well-formed MATERIALIZED entry), so the fake simulates it via
        // ForcedResult instead of MarkMaterialized (which enforces
        // VersionNumber 1 shape at construction).
        h.Reservation.OnResponded = (request, response) =>
        {
            h.OperationStatusClient.OperationId = response.CopyDesignOperationId;
            h.OperationStatusClient.IdempotencyKey = request.IdempotencyKey;
            var resultingId = response.Entries.Single().ResultingCadDocumentId;
            h.OperationStatusClient.ForcedResult = () => new CopyDesignOperationStatusResult(
                CopyDesignOperationStatusOutcome.Found, response.CopyDesignOperationId, request.IdempotencyKey,
                new[]
                {
                    new CopyDesignOperationStatusEntry(
                        Ordinal: 0, IsCopy: true, State: CopyDesignOperationEntryState.Materialized,
                        SourceCadDocumentId: "cad-1", ResultingCadDocumentId: resultingId,
                        OriginalDocumentNumber: "1001", OriginalFileName: "a-new.ipt", OriginalDocumentType: "IPT",
                        FileVersionId: "fv-weird", VersionNumber: 2, Sha256: CanonicalHashA, FileSize: 50L),
                });
        };

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.UnexpectedMaterializationState, result.Outcome);
        Assert.Empty(h.Copier.Calls);
        Assert.Empty(h.Materializer.Calls);
    }

    // required test: changed/corrupt materialized model binary => fail
    [Fact]
    public async Task A_changed_or_corrupt_materialized_binary_fails_closed_never_resumed()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        var dest = node.ProposedDestinationAbsolutePath!;
        h.ExistingDestinations.Add(dest);
        h.FileSizeFor[dest] = 50L;
        // The server-reported hash (canonical) does NOT match what the local
        // file actually hashes to (also canonical, but a DIFFERENT value) -
        // the local binary has changed or is corrupt.
        SetLocalHash(h, dest, CanonicalHashA);
        h.Reservation.OnResponded = (request, response) =>
        {
            foreach (var entry in response.Entries)
            {
                h.OperationStatusClient.AddPendingIfAbsent(
                    entry.Action == CopyDesignApplyEntryAction.Copy, entry.SourceCadDocumentId,
                    entry.ResultingCadDocumentId, entry.DocumentNumber, entry.FileName, entry.DocumentType);
            }
            h.OperationStatusClient.OperationId = response.CopyDesignOperationId;
            h.OperationStatusClient.IdempotencyKey = request.IdempotencyKey;
            h.OperationStatusClient.MarkMaterialized("cad-1-new", 1, CanonicalHashB, 50L, "fv-1");
        };

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.UnexpectedMaterializationState, result.Outcome);
        Assert.Empty(h.Copier.Calls);
        Assert.Empty(h.Materializer.Calls);
    }

    [Fact]
    public async Task Materialized_server_side_but_local_destination_file_missing_fails_closed()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        var dest = node.ProposedDestinationAbsolutePath!;
        // Deliberately NOT added to h.ExistingDestinations - the server
        // thinks this is materialized, but the local file is missing.
        h.Reservation.OnResponded = (request, response) =>
        {
            foreach (var entry in response.Entries)
            {
                h.OperationStatusClient.AddPendingIfAbsent(
                    entry.Action == CopyDesignApplyEntryAction.Copy, entry.SourceCadDocumentId,
                    entry.ResultingCadDocumentId, entry.DocumentNumber, entry.FileName, entry.DocumentType);
            }
            h.OperationStatusClient.OperationId = response.CopyDesignOperationId;
            h.OperationStatusClient.IdempotencyKey = request.IdempotencyKey;
            h.OperationStatusClient.MarkMaterialized("cad-1-new", 1, CanonicalHashA, 50L, "fv-1");
        };

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.UnexpectedMaterializationState, result.Outcome);
        Assert.Empty(h.Copier.Calls);
    }

    // P6D ROUND 3: UNLIKE round 2's probe (which silently proceeded as a
    // fresh apply on a transport failure), an operation-status failure is now
    // FATAL - the whole apply fails closed, never silently reverting to
    // "proceed as fresh". This is a deliberate, required behavior change from
    // round 2 - see the orchestrator's own step 6b comment.
    [Fact]
    public async Task An_operation_status_lookup_failure_fails_the_whole_apply_closed_never_proceeds_as_fresh()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        h.OperationStatusClient.ThrowOnCall = new InvalidOperationException("simulated transport failure");
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.UnexpectedMaterializationState, result.Outcome);
        Assert.Empty(h.Copier.Calls);
        // the reservation itself already succeeded and is durable - its id is
        // still reported, never hidden, so the attempt remains recoverable.
        Assert.NotNull(result.CopyDesignOperationId);
    }

    [Fact]
    public async Task An_operation_status_response_missing_an_entry_for_a_reserved_target_fails_closed()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        // Deliberately never populated (ForcedResult below returns an empty
        // entry list) - simulates a response that omits a reserved COPY
        // target entirely.
        h.Reservation.OnResponded = (request, response) =>
        {
            h.OperationStatusClient.ForcedResult = () => new CopyDesignOperationStatusResult(
                CopyDesignOperationStatusOutcome.Found, response.CopyDesignOperationId, request.IdempotencyKey,
                Array.Empty<CopyDesignOperationStatusEntry>());
        };
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.UnexpectedMaterializationState, result.Outcome);
        Assert.Empty(h.Copier.Calls);
    }

    [Fact]
    public async Task An_operation_status_response_naming_a_different_operationId_fails_closed()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        h.Reservation.OnResponded = (request, response) =>
        {
            h.OperationStatusClient.ForcedResult = () => new CopyDesignOperationStatusResult(
                CopyDesignOperationStatusOutcome.Found, "op-DIFFERENT-FROM-REQUESTED", request.IdempotencyKey,
                Array.Empty<CopyDesignOperationStatusEntry>());
        };
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.UnexpectedMaterializationState, result.Outcome);
        Assert.Empty(h.Copier.Calls);
    }

    [Fact]
    public async Task An_operation_status_response_with_a_duplicate_entry_fails_closed()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        h.Reservation.OnResponded = (request, response) =>
        {
            var resultingId = response.Entries.Single().ResultingCadDocumentId;
            h.OperationStatusClient.ForcedResult = () => new CopyDesignOperationStatusResult(
                CopyDesignOperationStatusOutcome.Found, response.CopyDesignOperationId, request.IdempotencyKey,
                new[]
                {
                    new CopyDesignOperationStatusEntry(
                        0, IsCopy: true, CopyDesignOperationEntryState.Pending, "cad-1", resultingId, "1001", "a-new.ipt", "IPT"),
                    new CopyDesignOperationStatusEntry(
                        1, IsCopy: true, CopyDesignOperationEntryState.Pending, "cad-1", resultingId, "1001", "a-new.ipt", "IPT"),
                });
        };
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.UnexpectedMaterializationState, result.Outcome);
        Assert.Empty(h.Copier.Calls);
    }

    [Fact]
    public async Task An_operation_status_response_naming_a_different_source_identity_fails_closed()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        h.Reservation.OnResponded = (request, response) =>
        {
            var resultingId = response.Entries.Single().ResultingCadDocumentId;
            h.OperationStatusClient.ForcedResult = () => new CopyDesignOperationStatusResult(
                CopyDesignOperationStatusOutcome.Found, response.CopyDesignOperationId, request.IdempotencyKey,
                new[]
                {
                    new CopyDesignOperationStatusEntry(
                        0, IsCopy: true, CopyDesignOperationEntryState.Pending, "cad-SOME-OTHER-SOURCE", resultingId,
                        "1001", "a-new.ipt", "IPT"),
                });
        };
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.UnexpectedMaterializationState, result.Outcome);
        Assert.Empty(h.Copier.Calls);
    }

    [Fact]
    public async Task An_operation_status_response_reporting_an_INVALID_entry_fails_closed()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        h.Reservation.OnResponded = (request, response) =>
        {
            foreach (var entry in response.Entries)
            {
                h.OperationStatusClient.AddPendingIfAbsent(
                    entry.Action == CopyDesignApplyEntryAction.Copy, entry.SourceCadDocumentId,
                    entry.ResultingCadDocumentId, entry.DocumentNumber, entry.FileName, entry.DocumentType);
            }
            h.OperationStatusClient.OperationId = response.CopyDesignOperationId;
            h.OperationStatusClient.IdempotencyKey = request.IdempotencyKey;
            h.OperationStatusClient.MarkInvalid("cad-1-new");
        };
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.UnexpectedMaterializationState, result.Outcome);
        Assert.Empty(h.Copier.Calls);
    }

    // required test: same reserved resultingCadDocumentIds retained on resume.
    [Fact]
    public async Task Resume_retains_the_SAME_reserved_resultingCadDocumentId_never_a_different_one()
    {
        var h = new OrchestratorHarness { IncludeOperationStatusClient = true };
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);
        var dest = node.ProposedDestinationAbsolutePath!;
        h.ExistingDestinations.Add(dest);
        h.FileSizeFor[dest] = 50L;
        SetLocalHash(h, dest, CanonicalHashA);
        h.Reservation.OnResponded = (request, response) =>
        {
            foreach (var entry in response.Entries)
            {
                h.OperationStatusClient.AddPendingIfAbsent(
                    entry.Action == CopyDesignApplyEntryAction.Copy, entry.SourceCadDocumentId,
                    entry.ResultingCadDocumentId, entry.DocumentNumber, entry.FileName, entry.DocumentType);
            }
            h.OperationStatusClient.OperationId = response.CopyDesignOperationId;
            h.OperationStatusClient.IdempotencyKey = request.IdempotencyKey;
            h.OperationStatusClient.MarkMaterialized("cad-1-new", 1, CanonicalHashA, 50L, "fv-1");
        };

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("cad-1-new", entry.CadDocumentId); // the SAME id the (idempotent) reservation always returns
    }

    // ---- P6D LIVE BLOCKER fix: the physical copier receives the SAME
    //      step-4 pre-mutation source hash baseline the verifier already
    //      receives - required so InventorCopyDesignPhysicalCopier can
    //      distinguish a document it opened itself coming back Dirty as
    //      Inventor's own load-time side effect from a genuine pre-existing
    //      unsaved edit (see CopyDesignSourceDirtyProvenanceGuardTests for
    //      the PURE decision logic itself). -----------------------------

    [Fact]
    public async Task The_physical_copier_receives_the_orchestrators_own_pre_mutation_source_hash_for_a_model()
    {
        var h = new OrchestratorHarness();
        var node = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var plan = Plan(new[] { node });
        h.ExistingSources.Add(node.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        var expectedBaseline = h.Hasher.ComputeSha256(node.SourceAbsolutePath); // same deterministic fake, re-read is safe
        Assert.Equal(expectedBaseline, h.Copier.CapturedSourceSha256BeforeOperation["cad-1"]);
    }

    [Fact]
    public async Task The_physical_copier_receives_the_orchestrators_own_pre_mutation_source_hash_for_a_drawing()
    {
        var h = new OrchestratorHarness();
        var model = CopyNode("cad-model", @"C:\src\model.iam", @"C:\dst\model-new.iam", CadDocumentType.Iam, isRoot: true);
        var drawing = CopyNode("cad-drawing", @"C:\src\drawing.idw", @"C:\dst\drawing-new.idw", CadDocumentType.Idw);
        var edges = new[] { DrawingModelEdge(drawing, model, modelIsCopy: true) };
        var plan = Plan(new[] { model, drawing }, edges);
        h.ExistingSources.Add(model.SourceAbsolutePath);
        h.ExistingSources.Add(drawing.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        var expectedBaseline = h.Hasher.ComputeSha256(drawing.SourceAbsolutePath);
        Assert.Equal(expectedBaseline, h.Copier.CapturedSourceSha256BeforeOperation["cad-drawing"]);
    }
}
