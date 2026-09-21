namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6C: the CENTRAL orchestration of one "Apply Copy Design" attempt. This is
/// the "pure core / testability" seam the P6C spec requires - it depends only
/// on the small adapter interfaces in <c>CopyDesignApplyAdapters.cs</c>
/// (reservation HTTP, physical copy, reference rewiring, verification-fact
/// gathering, materialization, hashing, plus two simple existence probes), so
/// EVERY orchestration decision (guard, revalidation, mapping, response
/// validation, execution order, journal/cleanup, verification gating,
/// materialization gating) is unit-testable with fakes - NO Inventor COM, NO
/// real HTTP, anywhere in this file.
///
/// SEQUENCE (each step fails closed and stops before the next):
///  1. acquire the in-process operation guard (refuse a concurrent apply);
///  2. map the confirmed plan to a P6B request (fails if not executable, or
///     any in-scope node isn't cleanly Copy/Reuse with a complete identity);
///  3. revalidate safety-sensitive facts against the CURRENT world (source
///     still exists, destination still free, no duplicate destinations) -
///     fail-closed on any throw;
///  4. capture every COPY source's SHA-256 BEFORE any mutation;
///  5. call the P6B reservation endpoint with the request's OWN idempotency
///     key (never minted fresh here - see <see cref="CopyDesignApplyAttempt"/>);
///  6. validate the response against the exact request - STOP BEFORE
///     PHYSICAL MUTATION on any mismatch (the reservation may still be
///     durable server-side; its id is always reported, never hidden);
///  7. compute a deterministic, dependency-aware (children-before-parents)
///     execution order;
///  8. PHASE 1: physically copy every COPY node, journalling each success;
///  9. PHASE 2: rewire every copied IAM's references (COPY child -&gt; its
///     copied destination; REUSE child -&gt; unchanged, confirmed only);
///  10. PHASE 3: verify every COPY node (destination, type, openability,
///      hashes, every reference resolution) - ANY failure aborts
///      materialization for the WHOLE operation (never a partial
///      materialize-what-passed shortcut, which the spec does not define);
///  11. PHASE 4: materialize each COPY node's first FileVersion via the
///      injected materializer - a server contract gap or failure here NEVER
///      triggers cleanup of an already-verified physical file (the file is
///      correct; only the server-side step did not complete) and NEVER
///      fabricates a FileVersion.
///
/// On any failure at step 8, 9, or 10, EVERY destination this attempt itself
/// created is journalled and cleanup is attempted via the injected delete
/// capability - never a pre-existing file, never a source file (see
/// <see cref="CopyDesignCleanupPlanner"/>). The P6B reservation/lineage is
/// NEVER touched by cleanup - "reserved but unmaterialized" is reported, not
/// hidden.
///
/// CODEX FINAL AUDIT ROUND 1 fixes:
///  HIGH 1 - a confirmed plan can never silently drop a represented IDW/DWG
///   node past this orchestrator; see the gate immediately after the guard.
///  HIGH 2 - step 5's reservation call is now a small, BOUNDED, same-key,
///   same-request retry for a caller-classified TRANSIENT failure only;
///   physical mutation still never begins until a reservation is
///   conclusively validated.
///  HIGH 5 - step 10 (verification) is wrapped so ANY exception or
///   cancellation after physical creation runs cleanup and returns a
///   truthful <see cref="CopyDesignApplyOutcome.VerificationFailed"/>,
///   never an uncontrolled/unhandled failure.
///  Every returned <see cref="CopyDesignApplyOperationResult"/> now also
///   carries the attempt's <see cref="CopyDesignApplyOperationResult.IdempotencyKey"/>,
///   on every outcome, for diagnosis and safe manual retry.
/// </summary>
public sealed class CopyDesignApplyOrchestrator(
    CopyDesignApplyOperationGuard guard,
    ICopyDesignReservationClient reservationClient,
    ICopyDesignPhysicalCopier physicalCopier,
    ICopyDesignReferenceRewirer rewirer,
    ICopyDesignVerifier verifier,
    ICopyDesignMaterializer materializer,
    ICopyDesignFileHasher hasher,
    Func<string, bool> sourceExists,
    Func<string, bool> destinationExists,
    Action<string> deleteFile,
    /// <summary>HIGH 2 fix: classifies a reservation-call exception as
    ///  TRANSIENT/uncertain (worth a bounded, SAME-key, SAME-request retry -
    ///  e.g. network/timeout/transport) versus a DETERMINISTIC rejection
    ///  (a 4xx business/validation conflict - never retried, since retrying
    ///  it would never succeed and would only delay an honest failure).
    ///  Core has no HTTP dependency, so the real classification (e.g.
    ///  <c>ArchApiException.IsServerUnavailable</c>) is injected by the
    ///  caller that actually knows the transport; omitting this disables
    ///  automatic retry entirely (every failure is reported immediately,
    ///  exactly like before this fix) rather than guessing.</summary>
    Func<Exception, bool>? isTransientReservationFailure = null,
    /// <summary>Bounded: the TOTAL number of reservation attempts (the first
    ///  try plus up to <c>maxReservationAttempts - 1</c> retries), all using
    ///  the EXACT SAME immutable <see cref="CopyDesignApplyRequest"/> and its
    ///  one idempotency key - never a fresh key, never a modified request.</summary>
    int maxReservationAttempts = 3)
{
    public async Task<CopyDesignApplyOperationResult> ExecuteAsync(
        CopyDesignPlan plan, string idempotencyKey, string? label, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        using var lease = guard.TryEnter();
        if (lease is null)
        {
            return CopyDesignApplyOperationResult.Simple(
                CopyDesignApplyOutcome.AlreadyRunning,
                "A Copy Design apply operation is already running - refusing to start a second one.",
                idempotencyKey);
        }

        // ---- HIGH 1 fix: never silently omit a represented drawing --------
        // CopyDesignApplyRequestMapper only ever maps IAM/IPT nodes - that
        // remains P6C's fixed scope - but a drawing the CONFIRMED plan itself
        // proposes to Copy/Reuse must never simply vanish from what the user
        // was told would happen. The ONLY way past this gate is the SAME
        // explicit, visible acknowledgement Round 8 already requires for
        // "drawings cannot be proven" - never inferred, never defaulted.
        var unacknowledgedDrawingNodes = plan.Nodes
            .Where(n => n.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg
                && n.ProposedAction is CopyDesignAction.Copy or CopyDesignAction.Reuse)
            .ToArray();
        if (unacknowledgedDrawingNodes.Length > 0 && !plan.ModelFilesOnlyAcknowledged)
        {
            var names = string.Join(", ", unacknowledgedDrawingNodes.Select(n => n.SourceFileName).OrderBy(n => n, StringComparer.Ordinal));
            return CopyDesignApplyOperationResult.Simple(
                CopyDesignApplyOutcome.DrawingsPresentWithoutAcknowledgement,
                $"The confirmed plan includes drawing(s) that would participate in Copy Design ({names}), but this "
                + "operation was never explicitly acknowledged as model-files-only. P6C only copies/rewires IAM/IPT "
                + "files - re-preview and either resolve the drawing(s) (P6D) or explicitly acknowledge model-files-"
                + "only mode before applying.",
                idempotencyKey);
        }

        // ---- 2. map -----------------------------------------------------
        var mapping = CopyDesignApplyRequestMapper.Map(plan, idempotencyKey, label);
        if (!mapping.Success)
        {
            return CopyDesignApplyOperationResult.Simple(CopyDesignApplyOutcome.MappingFailed, mapping.FailureReason!, idempotencyKey);
        }
        var request = mapping.Request!;

        // ---- 3. revalidate ------------------------------------------------
        var revalidation = CopyDesignApplyPlanRevalidator.Revalidate(request, sourceExists, destinationExists);
        if (!revalidation.Success)
        {
            return CopyDesignApplyOperationResult.Simple(CopyDesignApplyOutcome.RevalidationFailed, revalidation.FailureReason!, idempotencyKey);
        }

        // ---- 4. capture source hashes BEFORE any mutation -----------------
        var copyEntries = request.Entries.Where(e => e.Entry.Action == CopyDesignApplyEntryAction.Copy).ToArray();
        var sourceHashBefore = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in copyEntries)
        {
            string hash;
            try
            {
                hash = hasher.ComputeSha256(entry.Node.SourceAbsolutePath);
            }
            catch (Exception ex)
            {
                return CopyDesignApplyOperationResult.Simple(
                    CopyDesignApplyOutcome.RevalidationFailed,
                    $"Could not compute the source hash for \"{entry.Node.SourceFileName}\" before mutation: {ex.Message}",
                    idempotencyKey);
            }
            sourceHashBefore[entry.Node.CadDocumentId!] = hash;
        }

        // ---- 5. reservation (HIGH 2 fix: bounded, same-key, same-request
        //         automatic retry for a TRANSIENT/uncertain failure only) ----
        CopyDesignReservationResponse? response = null;
        Exception? lastReservationException = null;
        var attempts = Math.Max(1, maxReservationAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                response = await reservationClient.ApplyAsync(request, ct).ConfigureAwait(false);
                lastReservationException = null;
                break;
            }
            catch (Exception ex)
            {
                lastReservationException = ex;
                var isTransient = isTransientReservationFailure?.Invoke(ex) ?? false;
                if (!isTransient || attempt >= attempts)
                {
                    break;
                }
                try
                {
                    // Small bounded backoff before retrying the EXACT SAME
                    // request/key - never a new attempt, never a new key.
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        if (response is null)
        {
            // Still uncertain after every bounded retry (or the failure was
            // deterministic and never retried) - an HONEST controlled
            // failure that preserves the idempotency key: a caller MAY
            // safely retry this SAME logical attempt later via
            // CopyDesignApplyAttempt.Resume(idempotencyKey), never a fresh
            // key.
            return CopyDesignApplyOperationResult.Simple(
                CopyDesignApplyOutcome.ReservationCallFailed,
                $"The Copy Design reservation request failed after {attempts} attempt(s): "
                    + $"{lastReservationException?.Message}. Retry with the SAME idempotency key ({idempotencyKey}).",
                idempotencyKey);
        }

        // ---- 6. validate response - STOP BEFORE PHYSICAL MUTATION --------
        var validated = CopyDesignReservationResponseValidator.Validate(request, response);
        if (!validated.Success)
        {
            return new CopyDesignApplyOperationResult(
                CopyDesignApplyOutcome.ReservationResponseInvalid,
                validated.FailureReason,
                validated.CopyDesignOperationId ?? response.CopyDesignOperationId,
                Array.Empty<CopyDesignApplyEntryOutcome>(),
                null,
                idempotencyKey);
        }
        var operationId = validated.CopyDesignOperationId!;
        var resultingIdByCadDocumentId = validated.Entries.ToDictionary(e => e.Node.CadDocumentId!, e => e.ResultingCadDocumentId, StringComparer.Ordinal);

        // ---- 7. execution order --------------------------------------------
        var inScopeNodes = plan.Nodes.Where(n => n.DocumentType is CadDocumentType.Ipt or CadDocumentType.Iam).ToArray();
        var orderResult = CopyDesignExecutionOrderPlanner.ComputeOrder(inScopeNodes, plan.Edges);
        if (!orderResult.Success)
        {
            return new CopyDesignApplyOperationResult(
                CopyDesignApplyOutcome.MappingFailed, orderResult.FailureReason, operationId,
                Array.Empty<CopyDesignApplyEntryOutcome>(), null, idempotencyKey);
        }

        var copyNodeIds = new HashSet<string>(copyEntries.Select(e => e.Node.CadDocumentId!), StringComparer.Ordinal);
        var copyNodesInOrder = orderResult.Order.Where(n => copyNodeIds.Contains(n.CadDocumentId!)).ToArray();

        var journal = new CopyDesignExecutionJournal();
        var entryOutcomes = new Dictionary<string, CopyDesignApplyEntryOutcome>(StringComparer.Ordinal);

        // ---- 8. PHASE 1: physical copy --------------------------------------
        foreach (var node in copyNodesInOrder)
        {
            CopyDesignPhysicalCopyResult copyResult;
            try
            {
                copyResult = await physicalCopier.CopyAsync(node, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                copyResult = new CopyDesignPhysicalCopyResult(false, ex.Message);
            }

            if (!copyResult.Success)
            {
                var cleanup = CopyDesignCleanupPlanner.CleanUp(journal, deleteFile);
                // CODEX ROUND 3, MEDIUM fix: reuse the EXISTING FailureReason/
                // "Reason:" reporting surface for an unremoved operation-owned
                // temp artifact - never a new UI section, and never silently
                // dropped either.
                var reason = $"Physical copy failed for \"{node.SourceFileName}\": {copyResult.FailureReason}";
                if (copyResult.UnremovedTempPath is not null)
                {
                    reason += $" An operation-owned temporary file could not be automatically removed and " +
                        $"requires manual cleanup: {copyResult.UnremovedTempPath}";
                }
                return new CopyDesignApplyOperationResult(
                    CopyDesignApplyOutcome.PhysicalCopyFailed,
                    reason, operationId, FinalizeOutcomes(entryOutcomes), cleanup, idempotencyKey);
            }

            journal.RecordCreated(resultingIdByCadDocumentId[node.CadDocumentId!], node.ProposedDestinationAbsolutePath!);
            entryOutcomes[node.CadDocumentId!] = new CopyDesignApplyEntryOutcome(
                resultingIdByCadDocumentId[node.CadDocumentId!], CopyDesignApplyEntryAction.Copy,
                node.ProposedDestinationAbsolutePath, PhysicallyCreated: true, VerificationPassed: null, Materialization: null);
        }

        // ---- 9. PHASE 2: reference rewiring (copied IAMs only) --------------
        var referenceTargetsByNode = new Dictionary<string, IReadOnlyList<CopyDesignReferenceTarget>>(StringComparer.Ordinal);
        foreach (var node in copyNodesInOrder.Where(n => n.DocumentType == CadDocumentType.Iam))
        {
            var mappingResult = CopyDesignReferenceTargetResolver.Resolve(node, plan.Edges, plan.Nodes);
            if (!mappingResult.Success)
            {
                var cleanup = CopyDesignCleanupPlanner.CleanUp(journal, deleteFile);
                return new CopyDesignApplyOperationResult(
                    CopyDesignApplyOutcome.ReferenceRewiringFailed,
                    $"Reference mapping failed for \"{node.SourceFileName}\": {mappingResult.FailureReason}",
                    operationId, FinalizeOutcomes(entryOutcomes), cleanup, idempotencyKey);
            }
            referenceTargetsByNode[node.CadDocumentId!] = mappingResult.Targets;

            CopyDesignRewireResult rewireResult;
            try
            {
                rewireResult = await rewirer.RewireAsync(node, mappingResult.Targets, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                rewireResult = new CopyDesignRewireResult(false, ex.Message);
            }
            if (!rewireResult.Success)
            {
                var cleanup = CopyDesignCleanupPlanner.CleanUp(journal, deleteFile);
                return new CopyDesignApplyOperationResult(
                    CopyDesignApplyOutcome.ReferenceRewiringFailed,
                    $"Reference rewiring failed for \"{node.SourceFileName}\": {rewireResult.FailureReason}",
                    operationId, FinalizeOutcomes(entryOutcomes), cleanup, idempotencyKey);
            }
        }

        // ---- 10. PHASE 3: verification (HIGH 5 fix: the ENTIRE phase is
        //          wrapped so ANY exception - I/O failure, Inventor/COM
        //          failure, timeout, or cancellation - occurring AFTER
        //          physical creation/rewiring NEVER escapes as an
        //          uncontrolled generic failure. It always runs journal
        //          cleanup, returns the truthful VerificationFailed outcome,
        //          preserves the CopyDesignOperationId, and reports cleanup
        //          honestly - and it NEVER claims the server reservation
        //          itself was rolled back, since it was not.) ---------------
        var allFacts = new List<CopyDesignNodeVerificationFacts>();
        foreach (var node in copyNodesInOrder)
        {
            var targets = referenceTargetsByNode.TryGetValue(node.CadDocumentId!, out var t) ? t : Array.Empty<CopyDesignReferenceTarget>();
            CopyDesignNodeVerificationFacts facts;
            try
            {
                facts = await verifier.GatherFactsAsync(node, targets, sourceHashBefore[node.CadDocumentId!], ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Includes OperationCanceledException: cancellation AFTER
                // physical creation is an INCOMPLETE, unverified state, never
                // a clean no-op abort - it must be cleaned up exactly like
                // any other verification failure, never left dangling.
                var cleanupOnException = CopyDesignCleanupPlanner.CleanUp(journal, deleteFile);
                return new CopyDesignApplyOperationResult(
                    CopyDesignApplyOutcome.VerificationFailed,
                    $"Verification could not complete for \"{node.SourceFileName}\": {ex.Message}. "
                        + "The Copy Design reservation itself was NOT rolled back by this failure.",
                    operationId, FinalizeOutcomes(entryOutcomes), cleanupOnException, idempotencyKey);
            }
            allFacts.Add(facts);
        }
        var verification = CopyDesignVerificationEvaluator.EvaluateAll(allFacts);
        foreach (var nodeResult in verification.NodeResults)
        {
            var existing = entryOutcomes[nodeResult.CadDocumentId];
            entryOutcomes[nodeResult.CadDocumentId] = existing with { VerificationPassed = nodeResult.Passed };
        }
        if (!verification.Passed)
        {
            var cleanup = CopyDesignCleanupPlanner.CleanUp(journal, deleteFile);
            return new CopyDesignApplyOperationResult(
                CopyDesignApplyOutcome.VerificationFailed,
                "Verification failed: " + string.Join(" | ", verification.AllReasons),
                operationId, FinalizeOutcomes(entryOutcomes), cleanup, idempotencyKey);
        }

        // ---- 11. PHASE 4: materialization ------------------------------------
        // Materialization requests are built ENTIRELY from authoritative P6C
        // state gathered during THIS attempt - the validated P6B reservation
        // (operationId, resultingCadDocumentId), the confirmed plan node
        // (sourceCadDocumentId, documentType), and the JUST-COMPLETED local
        // verification facts (ResultingSha256 / ResultingFileSize of the
        // FINAL destination binary) - never a pre-copy or stale value. Every
        // fact used here is guaranteed present because Phase 3 already
        // required verification.Passed before reaching this phase.
        var factsBySourceId = allFacts.ToDictionary(f => f.CadDocumentId, StringComparer.Ordinal);
        var anyGap = false;
        foreach (var node in copyNodesInOrder)
        {
            var resultingCadDocumentId = resultingIdByCadDocumentId[node.CadDocumentId!];
            var facts = factsBySourceId[node.CadDocumentId!];
            var materializeRequest = new CopyDesignMaterializeRequest(
                operationId,
                resultingCadDocumentId,
                node.CadDocumentId!,
                node.DocumentType,
                node.ProposedDestinationAbsolutePath!,
                node.ProposedDestinationFileName!,
                facts.ResultingSha256!,
                facts.ResultingFileSize!.Value);
            var materialization = await materializer.MaterializeFirstFileVersionAsync(materializeRequest, ct).ConfigureAwait(false);
            anyGap |= materialization.Outcome != CopyDesignMaterializationOutcome.Materialized;
            var existing = entryOutcomes[node.CadDocumentId!];
            entryOutcomes[node.CadDocumentId!] = existing with { Materialization = materialization };
        }

        // REUSE entries carry no physical action - report them as-is, already
        // materialized (they always were).
        foreach (var reuseEntry in request.Entries.Where(e => e.Entry.Action == CopyDesignApplyEntryAction.Reuse))
        {
            var resultingId = resultingIdByCadDocumentId[reuseEntry.Node.CadDocumentId!];
            entryOutcomes[reuseEntry.Node.CadDocumentId!] = new CopyDesignApplyEntryOutcome(
                resultingId, CopyDesignApplyEntryAction.Reuse, null, PhysicallyCreated: false, VerificationPassed: true, Materialization: null);
        }

        var finalOutcome = anyGap ? CopyDesignApplyOutcome.SucceededWithMaterializationGaps : CopyDesignApplyOutcome.Succeeded;
        return new CopyDesignApplyOperationResult(finalOutcome, null, operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
    }

    private static IReadOnlyList<CopyDesignApplyEntryOutcome> FinalizeOutcomes(
        Dictionary<string, CopyDesignApplyEntryOutcome> outcomes) =>
        outcomes.Values.OrderBy(o => o.CadDocumentId, StringComparer.Ordinal).ToArray();
}
