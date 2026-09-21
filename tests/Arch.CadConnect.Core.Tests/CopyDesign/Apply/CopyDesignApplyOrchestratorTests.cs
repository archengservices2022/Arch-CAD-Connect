using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.CopyDesign.Apply;

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

    // 30. no drawing type can execute in P6C
    [Fact]
    public async Task DrawingNodesNeverReachThePhysicalCopier()
    {
        var h = new OrchestratorHarness();
        var copyIpt = CopyNode("cad-1", @"C:\src\a.ipt", @"C:\dst\a.ipt");
        var drawing = new CopyDesignNode(
            "cad-drawing", "fv-1", CadDocumentType.Idw, @"C:\src\a.idw", "a.idw",
            null, true, true, true, CopyDesignAction.Copy, "b.idw", @"C:\dst\b.idw", Array.Empty<string>());
        var plan = Plan(new[] { copyIpt, drawing });
        h.ExistingSources.Add(copyIpt.SourceAbsolutePath);

        await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.DoesNotContain("cad-drawing", h.Copier.Calls);
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
    // CODEX FINAL AUDIT ROUND 1, HIGH 1: a represented drawing is NEVER
    // silently omitted from Apply.
    // ======================================================================

    [Fact]
    public async Task An_unacknowledged_drawing_COPY_node_is_rejected_before_ANY_reservation_or_physical_copy()
    {
        var h = new OrchestratorHarness();
        var iptNode = CopyNode("cad-ipt", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawingNode = CopyNode("cad-dwg", @"C:\src\a.idw", @"C:\dst\a-new.idw", CadDocumentType.Idw);
        var plan = Plan(new[] { iptNode, drawingNode }); // ModelFilesOnlyAcknowledged defaults to false

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingsPresentWithoutAcknowledgement, result.Outcome);
        Assert.Empty(h.Reservation.Calls);
        Assert.Empty(h.Copier.Calls);
    }

    [Fact]
    public async Task An_unacknowledged_drawing_REUSE_node_is_ALSO_rejected()
    {
        var h = new OrchestratorHarness();
        var iptNode = CopyNode("cad-ipt", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawingReuse = ReuseNode("cad-dwg", @"C:\src\a.idw", CadDocumentType.Idw);
        var plan = Plan(new[] { iptNode, drawingReuse });

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.DrawingsPresentWithoutAcknowledgement, result.Outcome);
        Assert.Empty(h.Reservation.Calls);
    }

    [Fact]
    public async Task An_acknowledged_model_files_only_plan_proceeds_and_the_drawing_is_never_sent_or_copied()
    {
        var h = new OrchestratorHarness();
        var iptNode = CopyNode("cad-ipt", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var drawingNode = CopyNode("cad-dwg", @"C:\src\a.idw", @"C:\dst\a-new.idw", CadDocumentType.Idw);
        var plan = Plan(new[] { iptNode, drawingNode }) with { ModelFilesOnlyAcknowledged = true };
        h.ExistingSources.Add(iptNode.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
        Assert.DoesNotContain("cad-dwg", h.Copier.Calls);
        var sentEntries = Assert.Single(h.Reservation.Calls).WireEntries;
        Assert.DoesNotContain(sentEntries, e => e.Action == CopyDesignApplyEntryAction.Copy && e.Copy!.DocumentType == CadDocumentType.Idw);
    }

    [Fact]
    public async Task An_EXCLUDED_drawing_node_never_triggers_the_acknowledgement_gate()
    {
        var h = new OrchestratorHarness();
        var iptNode = CopyNode("cad-ipt", @"C:\src\a.ipt", @"C:\dst\a-new.ipt");
        var excludedDrawing = CopyNode("cad-dwg", @"C:\src\a.idw", @"C:\dst\a-new.idw", CadDocumentType.Idw) with { ProposedAction = CopyDesignAction.Exclude };
        var plan = Plan(new[] { iptNode, excludedDrawing });
        h.ExistingSources.Add(iptNode.SourceAbsolutePath);

        var result = await h.Build().ExecuteAsync(plan, "key-1", null);

        Assert.Equal(CopyDesignApplyOutcome.Succeeded, result.Outcome);
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
}
