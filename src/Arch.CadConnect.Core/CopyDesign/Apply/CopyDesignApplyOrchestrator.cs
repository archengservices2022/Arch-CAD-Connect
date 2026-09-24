using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6C/P6D: the CENTRAL orchestration of one "Apply Copy Design" attempt. This
/// is the "pure core / testability" seam the P6C spec requires - it depends
/// only on the small adapter interfaces in <c>CopyDesignApplyAdapters.cs</c>
/// (reservation HTTP, physical copy, reference rewiring, verification-fact
/// gathering, materialization, hashing, plus two simple existence probes), so
/// EVERY orchestration decision (guard, revalidation, mapping, response
/// validation, execution order, journal/cleanup, verification gating,
/// materialization gating) is unit-testable with fakes - NO Inventor COM, NO
/// real HTTP, anywhere in this file.
///
/// MODEL SEQUENCE (each step fails closed and stops before the next):
///  1. acquire the in-process operation guard (refuse a concurrent apply);
///  2. map the confirmed plan to a P6B request (fails if not executable, or
///     any in-scope node isn't cleanly Copy/Reuse with a complete identity) -
///     P6D: the SAME single request/reservation now also carries every
///     drawing (IDW/DWG) Copy/Reuse entry, UNLESS the plan itself carries an
///     explicit <see cref="CopyDesignPlan.ModelFilesOnlyAcknowledged"/>
///     acknowledgement, in which case drawings are omitted from the request
///     exactly as P6C originally did;
///  3. revalidate safety-sensitive facts against the CURRENT world (source
///     still exists, destination still free, no duplicate destinations) -
///     fail-closed on any throw;
///  4. capture every COPY source's SHA-256 BEFORE any mutation (models AND
///     drawings alike);
///  5. call the P6B reservation endpoint ONCE with the request's OWN
///     idempotency key (never minted fresh here - see
///     <see cref="CopyDesignApplyAttempt"/>) - covering every model AND
///     drawing entry in one atomic reservation;
///  6. validate the response against the exact request - STOP BEFORE
///     PHYSICAL MUTATION on any mismatch (the reservation may still be
///     durable server-side; its id is always reported, never hidden);
///  7. compute a deterministic, dependency-aware (children-before-parents)
///     execution order for the MODEL (IAM/IPT) nodes only - a drawing never
///     participates in this ordering (see
///     <see cref="CopyDesignExecutionOrderPlanner"/>, which already ignores
///     any non-Component edge);
///  8. PHASE 1: physically copy every COPY model node, journalling each
///     success;
///  9. PHASE 2: rewire every copied IAM's COMPONENT references (COPY child ->
///     its copied destination; REUSE child -> unchanged, confirmed only);
///  10. PHASE 3: verify every COPY model node (destination, type, openability,
///      hashes, every reference resolution) - ANY failure aborts the WHOLE
///      operation (cleanup, <see cref="CopyDesignApplyOutcome.VerificationFailed"/>)
///      before a single drawing is ever touched - this is the P6D ordering
///      requirement "models complete and verify before drawing execution
///      begins".
///
/// DRAWING SEQUENCE (P6D) - runs ONLY after every model above has succeeded,
/// using a SEPARATE execution journal (<c>drawingJournal</c>) so a drawing-
/// phase failure can NEVER cause a model artifact to be cleaned up, and a
/// drawing is NEVER even attempted if model verification failed:
///  11. PHASE 4: physically copy every COPY drawing node (deterministic order
///      by CadDocumentId - a drawing never depends on another drawing);
///  12. PHASE 5: rewire every copied drawing's DRAWING-&gt;MODEL references
///      (COPY model target -> its copied destination; REUSE model target ->
///      unchanged, confirmed only) via the SEPARATE
///      <c>drawingRewirer</c>/<c>drawingVerifier</c> adapters (a drawing has
///      no ComponentOccurrence to mutate - see
///      <c>InventorCopyDesignDrawingReferenceRewirer</c>'s own doc comment
///      for the exact API and why);
///  13. PHASE 6: verify every copied drawing's COMPLETE model reference set
///      (stale/unexpected/unresolved/missing = failure, exactly like a
///      model's component set) via <see cref="CopyDesignVerificationEvaluator"/>
///      - the SAME pure evaluator P6C already uses, since its fact shape is
///      document-type-agnostic.
///
/// On ANY drawing-phase failure (11-13): drawing mutation stops immediately,
/// ONLY the drawing-owned destination artifacts this attempt itself created
/// (<c>drawingJournal</c>) are cleaned up - never a model artifact, never a
/// pre-existing or source file - and the operation falls through to
/// materialization below rather than returning immediately: every model that
/// already passed ITS OWN verification is still genuinely correct and gets
/// materialized normally; only the drawing(s) stay reserved-but-
/// unmaterialized. This is the P6D-required honest reporting - see each
/// <see cref="CopyDesignApplyOutcome.DrawingPhysicalCopyFailed"/>/
/// <see cref="CopyDesignApplyOutcome.DrawingReferenceRewiringFailed"/>/
/// <see cref="CopyDesignApplyOutcome.DrawingVerificationFailed"/>'s own doc
/// comment. No DB rollback is ever fabricated for either side.
///
///  14. PHASE 7: materialize every model COPY node (always attempted - it
///      already passed verification in phase 10) and, ONLY when the drawing
///      phase did NOT fail, every drawing COPY node too - a server contract
///      gap or failure here NEVER triggers cleanup of an already-verified
///      physical file (the file is correct; only the server-side step did
///      not complete) and NEVER fabricates a FileVersion.
///
/// On any failure at step 8, 9, or 10 (the MODEL phases), EVERY destination
/// this attempt itself created (models only - no drawing has been touched
/// yet) is journalled and cleanup is attempted via the injected delete
/// capability - never a pre-existing file, never a source file (see
/// <see cref="CopyDesignCleanupPlanner"/>). The P6B reservation/lineage is
/// NEVER touched by cleanup - "reserved but unmaterialized" is reported, not
/// hidden.
///
/// CODEX FINAL AUDIT ROUND 1 fixes (carried forward unmodified for the model
/// path; the SAME discipline is applied to the drawing path):
///  HIGH 1 (superseded by P6D) - a confirmed plan can never silently drop a
///   represented IDW/DWG node past this orchestrator; P6C achieved this by
///   REFUSING to apply until the engineer explicitly acknowledged model-
///   files-only mode. P6D achieves the SAME guarantee more usefully: an
///   unacknowledged drawing is no longer refused, it is EXECUTED (see the
///   mapping step above) - "never silently omitted" now means "never
///   silently left out of the request", not "always rejected".
///  HIGH 2 - step 5's reservation call is now a small, BOUNDED, same-key,
///   same-request retry for a caller-classified TRANSIENT failure only;
///   physical mutation still never begins until a reservation is
///   conclusively validated.
///  HIGH 5 - step 10 (and, for P6D, step 13) is wrapped so ANY exception or
///   cancellation after physical creation runs cleanup and returns a
///   truthful failed outcome, never an uncontrolled/unhandled failure.
///  Every returned <see cref="CopyDesignApplyOperationResult"/> now also
///   carries the attempt's <see cref="CopyDesignApplyOperationResult.IdempotencyKey"/>,
///   on every outcome, for diagnosis and safe manual retry.
///
/// P6D ROUND 2, HIGH fix - RESUME: calling <see cref="ExecuteAsync"/> again
///  with the SAME confirmed plan and the SAME idempotencyKey (never a fresh
///  one - see <see cref="CopyDesignApplyAttempt.Resume"/>) safely continues a
///  partially-failed attempt. Step 5's reservation call is already fully
///  idempotent server-side (the SAME operation/entries/resultingCadDocumentIds
///  come back, never a duplicate) - step 6b then classifies each reserved
///  COPY entry as already-materialized (via the OPTIONAL <c>materializationStatusProbe</c>
///  - skip its physical copy/rewire/verify/materialize entirely, reusing the
///  server-authoritative FileVersion 1 facts) or still-pending (proceed
///  exactly as a fresh attempt would). No new CadDocument or FileVersion is
///  ever created for an already-reserved/already-materialized entry. P6D
///  ROUND 3: classification is driven by the DEDICATED, authoritative
///  per-operation status endpoint - see
///  <see cref="ICopyDesignOperationStatusClient"/>'s own doc comment for why
///  round 2's reuse of the generic `latest-versions` lookup was replaced.
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
    int maxReservationAttempts = 3,
    /// <summary>P6D: physically copies/rewires a copied drawing's model
    ///  reference(s) - a SEPARATE adapter from <paramref name="rewirer"/>
    ///  because a drawing has no ComponentOccurrence to mutate (see
    ///  <c>InventorCopyDesignDrawingReferenceRewirer</c>). Added as a
    ///  trailing optional parameter so every existing (model-only) caller
    ///  and test keeps working unmodified. Required (non-null) ONLY when the
    ///  confirmed plan actually carries an in-scope drawing Copy/Reuse node -
    ///  see the config-guard immediately after mapping below.</summary>
    ICopyDesignReferenceRewirer? drawingRewirer = null,
    /// <summary>P6D: gathers a copied drawing's verification facts (its
    ///  COMPLETE model reference set) - a SEPARATE adapter from
    ///  <paramref name="verifier"/> because a drawing's actual reference set
    ///  is enumerated via <c>Document.ReferencedDocumentDescriptors</c>, not
    ///  <c>AssemblyDocument.ComponentDefinition.Occurrences</c> (see
    ///  <c>InventorCopyDesignDrawingVerifier</c>).</summary>
    ICopyDesignVerifier? drawingVerifier = null,
    /// <summary>P6D ROUND 3, HIGH fix (RESUME - replaces round 2's
    ///  <c>ICopyDesignMaterializationStatusProbe</c>): when supplied, the
    ///  operation's AUTHORITATIVE status is fetched ONCE (by operationId,
    ///  covering every entry in one call) BEFORE any physical mutation -
    ///  an entry reported MATERIALIZED (with a local destination file whose
    ///  hash/size match the server-authoritative FileVersion 1) is treated as
    ///  ALREADY DONE: never re-copied, never re-rewired, never re-
    ///  materialized (never a second FileVersion 1). A response the
    ///  orchestrator cannot fully trust - a transport/auth failure, a missing
    ///  or duplicate entry, an unexpected operationId/resultingCadDocumentId/
    ///  source identity/action, or an entry reported INVALID - FAILS CLOSED
    ///  (never silently treated as "proceed as a fresh apply", unlike round
    ///  2's probe). <c>null</c> (the default) disables this check entirely -
    ///  every existing (non-resume) caller and test keeps working
    ///  unmodified.</summary>
    ICopyDesignOperationStatusClient? operationStatusClient = null,
    /// <summary>P6D ROUND 2 (RESUME): reads a local file's byte length -
    ///  injected (rather than an inline <c>new FileInfo(path).Length</c>) so
    ///  the RESUME integrity check stays testable with fakes, matching every
    ///  other file-touching seam in this class. Defaults to the real
    ///  filesystem when omitted (every existing caller).</summary>
    Func<string, long>? getFileSize = null)
{
    private readonly Func<string, long> _getFileSize = getFileSize ?? (path => new FileInfo(path).Length);
    private static readonly HashSet<CadDocumentType> DrawingTypes = new() { CadDocumentType.Idw, CadDocumentType.Dwg };

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

        // ---- P6D: drawings execute normally UNLESS the plan itself was
        //           explicitly acknowledged as model-files-only - that
        //           acknowledgement is the ONLY thing that excludes a
        //           drawing from this request; it is never inferred and
        //           never defaulted. -------------------------------------
        var includeDrawings = !plan.ModelFilesOnlyAcknowledged;
        // Only a COPY drawing ever needs physical copy + rewire + verify
        // (and therefore drawingRewirer/drawingVerifier); a REUSE drawing is
        // never touched at all (see the generic REUSE handling at the end of
        // this method) so its presence alone must never demand these
        // adapters be configured.
        var drawingCopyNodesRequiringExecution = includeDrawings
            ? plan.Nodes.Where(n => DrawingTypes.Contains(n.DocumentType) && n.ProposedAction == CopyDesignAction.Copy).ToArray()
            : Array.Empty<CopyDesignNode>();
        if (drawingCopyNodesRequiringExecution.Length > 0 && (drawingRewirer is null || drawingVerifier is null))
        {
            // Config guard - never a silent NullReferenceException, and
            // never physical mutation attempted with half the drawing
            // adapters missing. This should only ever happen if a caller
            // wires the orchestrator incorrectly; a correctly wired caller
            // (see ArchAddInController) always supplies both.
            var names = string.Join(", ", drawingCopyNodesRequiringExecution.Select(n => n.SourceFileName).OrderBy(n => n, StringComparer.Ordinal));
            return CopyDesignApplyOperationResult.Simple(
                CopyDesignApplyOutcome.MappingFailed,
                $"The confirmed plan includes drawing(s) that require execution ({names}), but this orchestrator "
                + "was not configured with drawing reference-rewiring/verification adapters - refusing to apply.",
                idempotencyKey);
        }

        // ---- 2. map -----------------------------------------------------
        var mapping = CopyDesignApplyRequestMapper.Map(plan, idempotencyKey, label, includeDrawings);
        if (!mapping.Success)
        {
            return CopyDesignApplyOperationResult.Simple(CopyDesignApplyOutcome.MappingFailed, mapping.FailureReason!, idempotencyKey);
        }
        var request = mapping.Request!;

        // ---- 3. revalidate (RESUME-agnostic half only - duplicate-within-
        //         request, self-overwrite, source-still-exists) - safe here,
        //         BEFORE reservation, exactly as the ORIGINAL combined check
        //         always ran. The destination-must-not-exist half is
        //         RESUME-sensitive (an already-materialized entry is
        //         EXPECTED to have an existing destination) and is deferred
        //         to step 6c, after RESUME classification knows which
        //         entries to exempt - see CopyDesignApplyPlanRevalidator's
        //         own class doc comment for the full split rationale. -----
        var structuralRevalidation = CopyDesignApplyPlanRevalidator.RevalidateSourceAndStructure(request, sourceExists);
        if (!structuralRevalidation.Success)
        {
            return CopyDesignApplyOperationResult.Simple(CopyDesignApplyOutcome.RevalidationFailed, structuralRevalidation.FailureReason!, idempotencyKey);
        }

        // ---- 4. capture source hashes BEFORE any mutation (models AND
        //         drawings alike - the request's own entries already
        //         include both) -----------------------------------------
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

        // P6D ROUND 3, item G: SEED entryOutcomes for EVERY entry the NOW-
        // CONFIRMED reservation covers, BEFORE any phase runs - a REUSE entry
        // gets its real (never-changing) outcome immediately; a COPY entry
        // gets a "not yet physically created" placeholder every later phase
        // OVERWRITES as it actually processes that entry. This is what makes
        // EVERY subsequent early-return (resume-status failure, revalidation
        // failure, or a later phase failure) able to report a COMPLETE
        // accounting of every operation entry - never merely the entries a
        // phase happened to reach before failing, and NEVER an empty list
        // once the reservation itself is confirmed (see FinalizeOutcomes
        // call sites below, which replace the OLD Array.Empty<>() on every
        // path from here on).
        var entryOutcomes = new Dictionary<string, CopyDesignApplyEntryOutcome>(StringComparer.Ordinal);
        SeedEntryOutcomes(entryOutcomes, request, resultingIdByCadDocumentId);

        // ---- 6b. RESUME (P6D ROUND 3, HIGH fix - replaces round 2's
        //          latest-versions-based classification): classify every
        //          COPY entry as already-materialized (skip all physical
        //          work) or still pending (proceed normally), using ONE
        //          call to the AUTHORITATIVE per-operation status endpoint -
        //          see ICopyDesignOperationStatusClient's own doc comment.
        //          Keyed by the SOURCE cadDocumentId (matching
        //          entryOutcomes/journal elsewhere), carrying the
        //          synthesized "already materialized" result each skipped
        //          phase reuses directly. `operationStatusClient` being null
        //          (every existing non-resume caller/test) makes this a
        //          no-op - identical behavior to before this fix.
        //
        //          UNLIKE round 2: ANY failure to obtain a fully-trusted
        //          status (transport/auth/malformed, a missing or duplicate
        //          entry, an unexpected operationId/resultingCadDocumentId/
        //          source identity/action, or an entry reported INVALID)
        //          FAILS CLOSED here - it is never silently treated as
        //          "proceed as a fresh apply". Round-2 review judged that
        //          safe because the physical copier's own no-overwrite check
        //          was a sufficient backstop; round-3 review required an
        //          explicit, immediate failure instead of relying on that
        //          backstop to surface the problem deep in the pipeline. ----
        var alreadyMaterialized = new Dictionary<string, CopyDesignApplyEntryOutcome>(StringComparer.Ordinal);
        if (operationStatusClient is not null && copyEntries.Length > 0)
        {
            // P6D ROUND 4, item D: build the COMPLETE expected shape of the
            // status response from what THIS reservation already confirmed -
            // every entry (COPY *and* REUSE), in stable ordinal order
            // (array index, matching CopyDesignOperationEntry.ordinal's own
            // definition). `response` (the RAW, already-validated P6B
            // reservation response) is the source of OriginalDocumentNumber/
            // FileName/DocumentType for EVERY entry, COPY and REUSE alike -
            // both captured from the SAME reservation transaction the status
            // endpoint's own immutable snapshot must agree with. COPY's
            // OriginalDescription is the CLIENT's own submitted value (the
            // one thing about a COPY entry's snapshot the client genuinely
            // knows in advance); REUSE has no such independently-knowable
            // value at all (the request only ever carried its
            // cadDocumentId), so it is left null here and the client-level
            // validator does not require an exact match for it.
            var expectedEntries = new List<CopyDesignOperationStatusExpectedEntry>(request.Entries.Count);
            for (var i = 0; i < request.Entries.Count; i++)
            {
                var requestEntry = request.Entries[i];
                var responseEntry = response!.Entries[i];
                var isCopy = requestEntry.Entry.Action == CopyDesignApplyEntryAction.Copy;
                expectedEntries.Add(new CopyDesignOperationStatusExpectedEntry(
                    i, isCopy,
                    isCopy ? requestEntry.Node.CadDocumentId! : null,
                    responseEntry.ResultingCadDocumentId,
                    responseEntry.DocumentNumber, responseEntry.FileName, responseEntry.DocumentType,
                    isCopy ? requestEntry.Entry.Copy!.Description : null));
            }
            var expectation = new CopyDesignOperationStatusExpectation(operationId, idempotencyKey, expectedEntries);

            CopyDesignOperationStatusResult status;
            try
            {
                status = await operationStatusClient.GetStatusAsync(expectation, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new CopyDesignApplyOperationResult(
                    CopyDesignApplyOutcome.UnexpectedMaterializationState,
                    $"Could not obtain the authoritative status of Copy Design operation {operationId} to determine "
                        + $"resume state: {ex.Message}. Retry with the SAME idempotency key ({idempotencyKey}).",
                    operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
            }
            if (!status.Success)
            {
                return new CopyDesignApplyOperationResult(
                    CopyDesignApplyOutcome.UnexpectedMaterializationState,
                    $"Could not obtain the authoritative status of Copy Design operation {operationId} to determine "
                        + $"resume state ({status.Outcome}). Retry with the SAME idempotency key ({idempotencyKey}).",
                    operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
            }
            if (!string.Equals(status.CopyDesignOperationId, operationId, StringComparison.Ordinal))
            {
                // Defense in depth (a well-behaved client already refuses to
                // report Success for this case) - never trust a `Success`
                // result blindly, even for the operation id itself.
                return new CopyDesignApplyOperationResult(
                    CopyDesignApplyOutcome.UnexpectedMaterializationState,
                    "The operation status response named a different operationId than the one requested - refusing to "
                        + "trust it (fail closed).",
                    operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
            }

            // Every check below this point (missing entry, duplicate entry,
            // wrong action/source identity, wrong immutable snapshot, wrong
            // operationId/idempotencyKey/entry count) has ALREADY been
            // proven by a well-behaved client against `expectation` above -
            // `status` being `Success` means the response is a COMPLETE,
            // exact, internally-consistent match for what this reservation
            // confirmed. This orchestrator still never trusts that blindly,
            // though: `byResultingId` is built defensively (a duplicate
            // resultingCadDocumentId fails closed here too, never "last
            // wins" or a crash) and each lookup below is a TryGetValue, not
            // an indexer - so even a non-conforming/hostile
            // ICopyDesignOperationStatusClient implementation that reports
            // `Success` without actually honoring the expectation can never
            // crash this method or silently proceed; it fails closed exactly
            // like every other untrusted input in this class.
            var byResultingId = new Dictionary<string, CopyDesignOperationStatusEntry>(StringComparer.Ordinal);
            foreach (var statusEntry in status.Entries!)
            {
                if (!byResultingId.TryAdd(statusEntry.ResultingCadDocumentId, statusEntry))
                {
                    return new CopyDesignApplyOperationResult(
                        CopyDesignApplyOutcome.UnexpectedMaterializationState,
                        $"The operation status response contained more than one entry for resultingCadDocumentId "
                            + $"{statusEntry.ResultingCadDocumentId} - refusing to trust an internally inconsistent response "
                            + "(fail closed).",
                        operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
                }
            }

            foreach (var entry in copyEntries)
            {
                var sourceCadDocumentId = entry.Node.CadDocumentId!;
                var resultingId = resultingIdByCadDocumentId[sourceCadDocumentId];
                if (!byResultingId.TryGetValue(resultingId, out var statusEntry))
                {
                    return new CopyDesignApplyOperationResult(
                        CopyDesignApplyOutcome.UnexpectedMaterializationState,
                        $"The operation status response did not include an entry for reserved target "
                            + $"\"{entry.Node.SourceFileName}\" (resultingCadDocumentId {resultingId}) - refusing to guess its "
                            + "state (fail closed).",
                        operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
                }
                if (!statusEntry.IsCopy || !string.Equals(statusEntry.SourceCadDocumentId, sourceCadDocumentId, StringComparison.Ordinal))
                {
                    return new CopyDesignApplyOperationResult(
                        CopyDesignApplyOutcome.UnexpectedMaterializationState,
                        $"The operation status response names an inconsistent action/source identity for resultingCadDocumentId "
                            + $"{resultingId} - refusing to trust an internally inconsistent response (fail closed).",
                        operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
                }

                if (statusEntry.State == CopyDesignOperationEntryState.Pending)
                {
                    continue; // still pending - proceed normally
                }
                if (statusEntry.State == CopyDesignOperationEntryState.Invalid)
                {
                    return new CopyDesignApplyOperationResult(
                        CopyDesignApplyOutcome.UnexpectedMaterializationState,
                        $"The server reports an INVALID state for reserved target \"{entry.Node.SourceFileName}\" "
                            + $"(resultingCadDocumentId {resultingId}) - refusing to resume an unexpected state.",
                        operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
                }
                if (statusEntry.State == CopyDesignOperationEntryState.Reused)
                {
                    // Structurally impossible given the IsCopy check above,
                    // but never assumed - fail closed rather than guess.
                    return new CopyDesignApplyOperationResult(
                        CopyDesignApplyOutcome.UnexpectedMaterializationState,
                        $"The server reports a REUSED state for a COPY target \"{entry.Node.SourceFileName}\" "
                            + $"(resultingCadDocumentId {resultingId}) - refusing to trust an inconsistent response (fail closed).",
                        operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
                }

                // Materialized.
                if (statusEntry.VersionNumber != 1
                    || statusEntry.FileVersionId is null
                    || !FileVersionIntegrity.IsCanonicalSha256(statusEntry.Sha256)
                    || statusEntry.FileSize is null
                    || !FileVersionIntegrity.IsRepresentableFileSize(statusEntry.FileSize.Value))
                {
                    return new CopyDesignApplyOperationResult(
                        CopyDesignApplyOutcome.UnexpectedMaterializationState,
                        $"The server reports MATERIALIZED for reserved target \"{entry.Node.SourceFileName}\" "
                            + $"(resultingCadDocumentId {resultingId}) but did not supply a complete, canonical FileVersion 1 "
                            + "identity - refusing to resume without proof the existing materialization is genuinely correct.",
                        operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
                }

                var destination = entry.Node.ProposedDestinationAbsolutePath!;
                if (!destinationExists(destination))
                {
                    return new CopyDesignApplyOperationResult(
                        CopyDesignApplyOutcome.UnexpectedMaterializationState,
                        $"Reserved target \"{entry.Node.SourceFileName}\" (resultingCadDocumentId {resultingId}) is already "
                            + $"materialized server-side, but its expected local destination file is missing: \"{destination}\" - "
                            + "refusing to resume rather than silently re-creating it.",
                        operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
                }

                string localHash;
                long localSize;
                try
                {
                    localHash = hasher.ComputeSha256(destination);
                    localSize = _getFileSize(destination);
                }
                catch (Exception ex)
                {
                    return new CopyDesignApplyOperationResult(
                        CopyDesignApplyOutcome.UnexpectedMaterializationState,
                        $"Could not verify the already-materialized local file for \"{entry.Node.SourceFileName}\": {ex.Message}",
                        operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
                }
                if (!string.Equals(localHash, statusEntry.Sha256, StringComparison.OrdinalIgnoreCase) || localSize != statusEntry.FileSize)
                {
                    return new CopyDesignApplyOperationResult(
                        CopyDesignApplyOutcome.UnexpectedMaterializationState,
                        $"The local file for already-materialized target \"{entry.Node.SourceFileName}\" no longer matches the "
                            + "server-authoritative FileVersion 1 hash/size - refusing to resume over a changed or corrupt binary.",
                        operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
                }

                alreadyMaterialized[sourceCadDocumentId] = new CopyDesignApplyEntryOutcome(
                    resultingId, CopyDesignApplyEntryAction.Copy, destination, PhysicallyCreated: true, VerificationPassed: true,
                    Materialization: new CopyDesignMaterializationResult(
                        resultingId, CopyDesignMaterializationOutcome.Materialized, statusEntry.FileVersionId,
                        statusEntry.VersionNumber.Value, statusEntry.Sha256, statusEntry.FileSize.Value,
                        "Already materialized (resumed from a prior attempt) - not re-copied, not re-materialized."));
            }
        }

        // ---- 6c. revalidate destinations are free - the RESUME-sensitive
        //          half deferred from step 3 (see its own comment), now
        //          exempting exactly the entries RESUME just proved are
        //          genuinely already-materialized. Every OTHER COPY entry
        //          (including every entry when no resume probe is
        //          configured at all) is still checked exactly as before. --
        var destinationRevalidation = CopyDesignApplyPlanRevalidator.RevalidateDestinationsFree(
            request, destinationExists, alreadyMaterialized.Count > 0 ? alreadyMaterialized.Keys.ToHashSet(StringComparer.Ordinal) : null);
        if (!destinationRevalidation.Success)
        {
            // Unlike step 3's (pre-reservation) structural check, the
            // reservation has ALREADY succeeded by this point - the
            // operationId is real and durable, and must be reported, never
            // hidden behind Simple()'s null.
            return new CopyDesignApplyOperationResult(
                CopyDesignApplyOutcome.RevalidationFailed, destinationRevalidation.FailureReason,
                operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
        }

        // ---- 7. execution order (MODEL nodes only - a drawing never
        //         participates in this ordering; see CopyDesignExecutionOrderPlanner,
        //         which already ignores any non-Component edge) -----------
        var inScopeModelNodes = plan.Nodes.Where(n => n.DocumentType is CadDocumentType.Ipt or CadDocumentType.Iam).ToArray();
        var orderResult = CopyDesignExecutionOrderPlanner.ComputeOrder(inScopeModelNodes, plan.Edges);
        if (!orderResult.Success)
        {
            return new CopyDesignApplyOperationResult(
                CopyDesignApplyOutcome.MappingFailed, orderResult.FailureReason, operationId,
                FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
        }

        var modelCopyNodeIds = new HashSet<string>(
            copyEntries.Where(e => e.Node.DocumentType is CadDocumentType.Ipt or CadDocumentType.Iam).Select(e => e.Node.CadDocumentId!),
            StringComparer.Ordinal);
        var modelCopyNodesInOrder = orderResult.Order.Where(n => modelCopyNodeIds.Contains(n.CadDocumentId!)).ToArray();

        // Drawings never depend on each other (no Component edge between two
        // drawings is possible) - a simple, deterministic ordinal sort by
        // stable identity is sufficient and fully reproducible.
        var drawingCopyNodeIds = new HashSet<string>(
            copyEntries.Where(e => DrawingTypes.Contains(e.Node.DocumentType)).Select(e => e.Node.CadDocumentId!),
            StringComparer.Ordinal);
        var drawingCopyNodesInOrder = plan.Nodes
            .Where(n => drawingCopyNodeIds.Contains(n.CadDocumentId ?? ""))
            .OrderBy(n => n.CadDocumentId, StringComparer.Ordinal)
            .ToArray();

        var journal = new CopyDesignExecutionJournal();

        // ==================================================================
        // MODEL PHASES (1-3) - unchanged P6C behavior. ANY failure here
        // aborts the ENTIRE operation before a single drawing is touched.
        // ==================================================================

        // ---- 8. PHASE 1: physical copy (models) -----------------------------
        foreach (var node in modelCopyNodesInOrder)
        {
            // RESUME: already materialized in a prior attempt - never
            // recopy. entryOutcomes is fully populated already; deliberately
            // NEVER added to `journal` (we did not create this destination
            // in THIS attempt, so cleanup must never consider deleting it).
            if (alreadyMaterialized.TryGetValue(node.CadDocumentId!, out var resumedModelOutcome))
            {
                entryOutcomes[node.CadDocumentId!] = resumedModelOutcome;
                continue;
            }

            CopyDesignPhysicalCopyResult copyResult;
            try
            {
                copyResult = await physicalCopier.CopyAsync(node, sourceHashBefore[node.CadDocumentId!], ct).ConfigureAwait(false);
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
        foreach (var node in modelCopyNodesInOrder.Where(n => n.DocumentType == CadDocumentType.Iam && !alreadyMaterialized.ContainsKey(n.CadDocumentId!)))
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

        // ---- 10. PHASE 3: verification (models) (HIGH 5 fix: the ENTIRE
        //          phase is wrapped so ANY exception - I/O failure,
        //          Inventor/COM failure, timeout, or cancellation -
        //          occurring AFTER physical creation/rewiring NEVER escapes
        //          as an uncontrolled generic failure. It always runs
        //          journal cleanup, returns the truthful VerificationFailed
        //          outcome, preserves the CopyDesignOperationId, and reports
        //          cleanup honestly - and it NEVER claims the server
        //          reservation itself was rolled back, since it was not.) --
        var allFacts = new List<CopyDesignNodeVerificationFacts>();
        foreach (var node in modelCopyNodesInOrder)
        {
            // RESUME: already verified (and materialized) in a prior attempt
            // - entryOutcomes already carries VerificationPassed: true.
            if (alreadyMaterialized.ContainsKey(node.CadDocumentId!))
            {
                continue;
            }

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

        // ==================================================================
        // DRAWING PHASES (4-6, P6D) - runs ONLY now that every model has
        // been copied, rewired, AND verified. A SEPARATE journal means a
        // drawing-phase failure can NEVER cause cleanup to touch a model
        // artifact. On failure, mutation stops immediately, drawing-owned
        // artifacts are cleaned up, and the method falls through to
        // materialization (models still get materialized - see the class
        // doc comment) rather than returning early.
        // ==================================================================

        var drawingJournal = new CopyDesignExecutionJournal();
        // Hoisted to method scope (rather than local to PHASE 6 below) so
        // PHASE 7's drawing materialization loop can reuse the SAME gathered
        // facts (ResultingSha256/ResultingFileSize of the FINAL destination
        // binary) without re-gathering them - mirroring exactly how the
        // model phase's own `allFacts` is used below.
        var allDrawingFacts = new List<CopyDesignNodeVerificationFacts>();
        CopyDesignApplyOutcome? drawingFailureOutcome = null;
        string? drawingFailureReason = null;
        CopyDesignCleanupReport? drawingCleanup = null;

        // ==================================================================
        // P6D ROUND 3, CRITICAL fix (items A+B): resolve EVERY non-resumed
        // drawing's model reference targets EXACTLY ONCE, here, BEFORE any
        // physical mutation - fail closed IMMEDIATELY on a resolver failure
        // or a blank source/target path (never silently swallowed into an
        // empty set the way "is { Success: true } r ? r.Targets : []" did
        // before this fix). The SAME resolved set is reused (never re-
        // resolved) by PHASE 5 below, so what gets REWIRED is provably the
        // SAME set that was safety-checked - RESUME: a drawing already
        // materialized in a prior attempt is never touched by this attempt
        // at all, so it is excluded here too (never at risk from THIS
        // attempt).
        // ==================================================================
        var nonResumedDrawings = drawingCopyNodesInOrder.Where(n => !alreadyMaterialized.ContainsKey(n.CadDocumentId!)).ToArray();
        var drawingModelTargetsByNode = new Dictionary<string, IReadOnlyList<CopyDesignReferenceTarget>>(StringComparer.Ordinal);
        // NOTE: a resolution failure here sets drawingFailureOutcome and
        // BREAKS (never `return`s directly) - a `return` would skip PHASE 7
        // below and the model materialization it performs UNCONDITIONALLY
        // for every already-verified model (see that PHASE's own doc
        // comment: "models still get materialized" on ANY drawing-phase
        // failure, resolution failures included), and would also skip the
        // REUSE-entries-always-reported loop. This mirrors exactly how
        // every other drawing-phase failure in this method is handled.
        foreach (var node in nonResumedDrawings)
        {
            var resolveResult = CopyDesignReferenceTargetResolver.Resolve(node, plan.Edges, plan.Nodes, CadRelationshipKind.DrawingModel);
            if (!resolveResult.Success)
            {
                drawingFailureOutcome = CopyDesignApplyOutcome.DrawingReferenceRewiringFailed;
                drawingFailureReason = $"Could not safely resolve model reference targets for \"{node.SourceFileName}\" before any "
                    + $"drawing mutation: {resolveResult.FailureReason}";
                break;
            }

            var blankTarget = resolveResult.Targets.FirstOrDefault(t =>
                string.IsNullOrWhiteSpace(t.OriginalChildAbsolutePath) || string.IsNullOrWhiteSpace(t.ExpectedTargetAbsolutePath));
            if (blankTarget is not null)
            {
                drawingFailureOutcome = CopyDesignApplyOutcome.DrawingReferenceRewiringFailed;
                drawingFailureReason = $"Drawing \"{node.SourceFileName}\" has a blank model reference path (source or planned "
                    + $"target) for child \"{blankTarget.ChildCadDocumentId}\" - refusing to proceed (fail closed).";
                break;
            }
            drawingModelTargetsByNode[node.CadDocumentId!] = resolveResult.Targets;
        }

        // ---- SOURCE REFERENCE SET (item A): the drawing's ORIGINAL model
        //      path(s) - what the drawing ACTUALLY references BEFORE any
        //      rewiring, and therefore what is genuinely at risk during
        //      physical SaveAs (SaveAs happens BEFORE rewiring - the
        //      drawing still points at these exact paths at that moment,
        //      never at a COPY destination it has not been rewired to yet).
        //      NEVER derived from ExpectedTargetAbsolutePath - see
        //      OriginalChildAbsolutePath's own doc comment on
        //      CopyDesignReferenceTarget. For a REUSE child this is the SAME
        //      path as its final target - protecting it here already covers
        //      that case; for a COPY child it is DIFFERENT from the final
        //      target and BOTH remain explicit (see finalTargetSet below). --
        var sourceReferenceSet = drawingModelTargetsByNode.Values.SelectMany(t => t)
            .Select(t => t.OriginalChildAbsolutePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // ---- FINAL TARGET SET: the planned model path(s) AFTER rewiring -
        //      COPY => its copied destination; REUSE => the (same) intended
        //      reused source. A SEPARATE set from the source set above -
        //      never a substitute for it; both are protected independently. -
        var finalTargetSet = drawingModelTargetsByNode.Values.SelectMany(t => t)
            .Select(t => t.ExpectedTargetAbsolutePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // A single MERGED baseline, keyed by path, captured once per UNIQUE
        // path across both sets - a REUSE child's source and final target
        // are the literal same path, and must be hashed only ONCE for that
        // shared point in time, not once per set (double-reading an
        // unmodified file is harmless in principle, but keeping the capture
        // to a single pass per unique path keeps the "before" baseline
        // strictly a single snapshot in time rather than two nominally-
        // simultaneous-but-actually-sequential reads).
        var protectedPaths = sourceReferenceSet.Concat(finalTargetSet).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // ==================================================================
        // P6D ROUND 4, CRITICAL fix (item A): the baseline capture itself
        // must FAIL CLOSED, immediately, BEFORE the drawing physical-copy
        // loop below is ever entered - never encoded as an incomplete/null
        // snapshot that is only discovered by a LATER comparison (round 3's
        // design), by which point the physical copier may already have been
        // called. `integrityBaseline` is populated ONLY on success; every
        // downstream use is unreachable unless capture fully succeeded,
        // because `drawingFailureOutcome` gates PHASE 4 below exactly like
        // every other pre-flight failure in this method.
        // ==================================================================
        IReadOnlyDictionary<string, FileIntegritySnapshot> integrityBaseline =
            new Dictionary<string, FileIntegritySnapshot>(StringComparer.OrdinalIgnoreCase);
        // Tracks whether `integrityBaseline` is a REAL, trustworthy snapshot
        // (capture succeeded, or there was nothing to protect) as opposed to
        // the empty placeholder above (capture FAILED, or was skipped
        // because an earlier pre-flight step already failed). PHASE 6b below
        // MUST NOT run its own integrity comparison against a placeholder -
        // every path would spuriously look "changed" (absent from an empty
        // dictionary), which would misreport an unrelated earlier failure as
        // a fresh SOURCE INTEGRITY VIOLATION and overwrite its true reason.
        var baselineCaptured = false;
        if (drawingFailureOutcome is null)
        {
            if (protectedPaths.Length == 0)
            {
                baselineCaptured = true;
            }
            else
            {
                var captureResult = CaptureIntegrityBaseline(protectedPaths);
                if (!captureResult.Success)
                {
                    drawingFailureOutcome = CopyDesignApplyOutcome.DrawingPhysicalCopyFailed;
                    drawingFailureReason = "Could not establish a complete pre-mutation integrity baseline for the referenced "
                        + $"model(s) before any drawing physical copy: {captureResult.FailureReason}";
                }
                else
                {
                    integrityBaseline = captureResult.Baseline!;
                    baselineCaptured = true;
                }
            }
        }

        // P6D ROUND 4, HIGH fix (item C): a violation of a PROTECTED source/
        // reference file detected AFTER drawing mutation began is a
        // STRONGER safety failure than an ordinary drawing failure - once
        // true, ZERO further materialization is attempted for ANY not-yet-
        // materialized entry (model entries included - see PHASE 7 below).
        // Deliberately a SEPARATE flag from `drawingFailureOutcome` (which
        // ordinary drawing failures - rewire/verify/physical-copy-for-a-
        // non-integrity-reason, and the pre-flight resolve/baseline failures
        // above - also set, but WITHOUT blocking model materialization,
        // exactly per the existing/approved "models already verified may
        // still be materialized" design).
        var sourceIntegrityViolationDetected = false;

        if (drawingCopyNodesInOrder.Length > 0)
        {
            // ---- 11. PHASE 4: physical copy (drawings) --------------------
            //      Gated on the pre-flight resolve/baseline-capture loop
            //      above having succeeded for EVERY drawing - a resolution/
            //      blank-path/baseline-capture failure there must block ALL
            //      drawing physical mutation, never merely the one drawing
            //      it was found on (P6D ROUND 3/4: "before any drawing
            //      mutation").
            //
            //      P6D ROUND 4, MEDIUM/HIGH fix (item B): restructured into
            //      a SINGLE per-drawing loop that, for EACH drawing COPY
            //      entry: (1) immediately BEFORE its SaveAs, rechecks the
            //      baseline for exactly the source references THAT drawing
            //      depends on (catches damage a PRIOR drawing's SaveAs may
            //      have caused to a source this drawing also depends on);
            //      (2) executes that drawing's physical copy; (3)
            //      immediately AFTER that SaveAs returns, rechecks the SAME
            //      relevant references again. Any violation at either point
            //      STOPS the loop immediately - the NEXT drawing is never
            //      copied, and rewiring never begins. Rechecking may repeat
            //      the SAME path across drawings that share a source model -
            //      correctness matters more than avoiding an extra hash. ---
            if (drawingFailureOutcome is null)
            {
                foreach (var node in drawingCopyNodesInOrder)
                {
                    // RESUME: already materialized in a prior attempt - never
                    // recopy, never added to drawingJournal (never created by
                    // THIS attempt, so cleanup must never touch it), and never
                    // subject to the integrity recheck below (nothing of
                    // THIS attempt's touches it).
                    if (alreadyMaterialized.TryGetValue(node.CadDocumentId!, out var resumedDrawingOutcome))
                    {
                        entryOutcomes[node.CadDocumentId!] = resumedDrawingOutcome;
                        continue;
                    }

                    var relevantSourcePaths = drawingModelTargetsByNode[node.CadDocumentId!]
                        .Select(t => t.OriginalChildAbsolutePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                    // (1) immediately BEFORE SaveAs.
                    var preViolations = RecheckIntegrityBaseline(relevantSourcePaths, integrityBaseline);
                    if (preViolations.Count > 0)
                    {
                        sourceIntegrityViolationDetected = true;
                        drawingFailureOutcome = CopyDesignApplyOutcome.SourceIntegrityViolation;
                        drawingFailureReason = "SOURCE INTEGRITY VIOLATION: the following ORIGINAL source model file(s) no "
                            + $"longer match their pre-mutation SHA-256/size, detected immediately BEFORE copying "
                            + $"\"{node.SourceFileName}\" (an earlier drawing's SaveAs may have persisted a dirty "
                            + "dependent this drawing also references): "
                            + string.Join(", ", preViolations.OrderBy(x => x, StringComparer.Ordinal))
                            + ". No further drawing copy or materialization will occur for this operation.";
                        break;
                    }

                    // (2) execute this drawing's physical copy. (3) - P6D
                    //     ROUND 5, item C - the post-attempt integrity
                    //     recheck below is now UNCONDITIONAL: it runs
                    //     immediately after CopyAsync returns OR throws,
                    //     regardless of whether the attempt itself
                    //     succeeded or failed. Round 4's version only ran it
                    //     on the SUCCESS path, so a dirty-dependent-save
                    //     that occurred DURING a FAILED or THROWN SaveAs
                    //     attempt was silently skipped at this point (Phase
                    //     6b would still catch it eventually, but only
                    //     after possibly reporting the wrong outcome and
                    //     with a wider blast radius). A violation found here
                    //     ALWAYS takes precedence over an ordinary physical-
                    //     copy failure, even when the copy attempt itself
                    //     also failed/threw. ---------------------------
                    CopyDesignPhysicalCopyResult copyResult;
                    try
                    {
                        copyResult = await physicalCopier.CopyAsync(node, sourceHashBefore[node.CadDocumentId!], ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        copyResult = new CopyDesignPhysicalCopyResult(false, ex.Message);
                    }

                    // A genuinely created destination file is tracked for
                    // cleanup IMMEDIATELY - before the integrity recheck
                    // below decides anything else - so cleanup always knows
                    // about anything this attempt actually created,
                    // regardless of what the recheck (or the copy's own
                    // reported outcome) says next.
                    if (copyResult.Success)
                    {
                        drawingJournal.RecordCreated(resultingIdByCadDocumentId[node.CadDocumentId!], node.ProposedDestinationAbsolutePath!);
                        entryOutcomes[node.CadDocumentId!] = new CopyDesignApplyEntryOutcome(
                            resultingIdByCadDocumentId[node.CadDocumentId!], CopyDesignApplyEntryAction.Copy,
                            node.ProposedDestinationAbsolutePath, PhysicallyCreated: true, VerificationPassed: null, Materialization: null);
                    }

                    var postAttemptViolations = RecheckIntegrityBaseline(relevantSourcePaths, integrityBaseline);
                    if (postAttemptViolations.Count > 0)
                    {
                        sourceIntegrityViolationDetected = true;
                        drawingFailureOutcome = CopyDesignApplyOutcome.SourceIntegrityViolation;
                        drawingFailureReason = "SOURCE INTEGRITY VIOLATION: the following ORIGINAL source model file(s) no "
                            + $"longer match their pre-copy SHA-256/size, detected immediately after the physical-copy "
                            + $"attempt for \"{node.SourceFileName}\" returned or threw (a dirty dependent may have been "
                            + "persisted regardless of whether the copy attempt itself succeeded): "
                            + string.Join(", ", postAttemptViolations.OrderBy(x => x, StringComparer.Ordinal))
                            + (copyResult.Success ? "" : $" (the physical copy attempt itself ALSO failed: {copyResult.FailureReason})")
                            + ". No further drawing copy or materialization will occur for this operation.";
                        break;
                    }

                    if (!copyResult.Success)
                    {
                        var reason = $"Drawing physical copy failed for \"{node.SourceFileName}\": {copyResult.FailureReason}";
                        if (copyResult.UnremovedTempPath is not null)
                        {
                            reason += $" An operation-owned temporary file could not be automatically removed and " +
                                $"requires manual cleanup: {copyResult.UnremovedTempPath}";
                        }
                        drawingFailureOutcome = CopyDesignApplyOutcome.DrawingPhysicalCopyFailed;
                        drawingFailureReason = reason;
                        break;
                    }
                    // success, integrity unchanged - proceed to the next drawing.
                }
            }

            // ---- 12. PHASE 5: drawing -> model reference rewiring ----------
            //      Reuses the ALREADY-RESOLVED, ALREADY-VALIDATED targets
            //      from drawingModelTargetsByNode - never re-resolves (which
            //      could otherwise observe a DIFFERENT result than what was
            //      safety-checked above, e.g. if the plan's edges were
            //      somehow mutable). ------------------------------------
            if (drawingFailureOutcome is null)
            {
                foreach (var node in nonResumedDrawings)
                {
                    var targets = drawingModelTargetsByNode[node.CadDocumentId!];

                    CopyDesignRewireResult rewireResult;
                    try
                    {
                        rewireResult = await drawingRewirer!.RewireAsync(node, targets, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        rewireResult = new CopyDesignRewireResult(false, ex.Message);
                    }
                    if (!rewireResult.Success)
                    {
                        drawingFailureOutcome = CopyDesignApplyOutcome.DrawingReferenceRewiringFailed;
                        drawingFailureReason = $"Drawing reference rewiring failed for \"{node.SourceFileName}\": {rewireResult.FailureReason}";
                        break;
                    }
                }
            }

            // ---- 13. PHASE 6: drawing verification (HIGH 5 pattern) --------
            if (drawingFailureOutcome is null)
            {
                foreach (var node in nonResumedDrawings)
                {
                    var targets = drawingModelTargetsByNode.TryGetValue(node.CadDocumentId!, out var t) ? t : Array.Empty<CopyDesignReferenceTarget>();
                    CopyDesignNodeVerificationFacts facts;
                    try
                    {
                        facts = await drawingVerifier!.GatherFactsAsync(node, targets, sourceHashBefore[node.CadDocumentId!], ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        drawingFailureOutcome = CopyDesignApplyOutcome.DrawingVerificationFailed;
                        drawingFailureReason = $"Drawing verification could not complete for \"{node.SourceFileName}\": {ex.Message}. "
                            + "The Copy Design reservation itself was NOT rolled back by this failure.";
                        break;
                    }
                    allDrawingFacts.Add(facts);
                }

                if (drawingFailureOutcome is null)
                {
                    var drawingVerification = CopyDesignVerificationEvaluator.EvaluateAll(allDrawingFacts);
                    foreach (var nodeResult in drawingVerification.NodeResults)
                    {
                        var existing = entryOutcomes[nodeResult.CadDocumentId];
                        entryOutcomes[nodeResult.CadDocumentId] = existing with { VerificationPassed = nodeResult.Passed };
                    }
                    if (!drawingVerification.Passed)
                    {
                        drawingFailureOutcome = CopyDesignApplyOutcome.DrawingVerificationFailed;
                        drawingFailureReason = "Drawing verification failed: " + string.Join(" | ", drawingVerification.AllReasons);
                    }
                }
            }

            // ---- PHASE 6b: FINAL integrity re-check of BOTH the SOURCE
            //      reference set AND the FINAL target set - runs
            //      UNCONDITIONALLY whenever at least one drawing copy was
            //      attempted, regardless of whether an earlier drawing phase
            //      already failed - "confirm source model hashes remain
            //      unchanged in ALL drawing paths", not merely the success
            //      path, and never substituting one set for the other (item
            //      A). Any violation found HERE is likewise a SOURCE
            //      INTEGRITY VIOLATION (item C) - mutation discovered after
            //      rewiring/Save2 is exactly as serious as mutation caught
            //      by one of PHASE 4's per-drawing checks; it is never
            //      silently downgraded to an ordinary DrawingVerificationFailed
            //      just because it was caught at the very end. This NEVER
            //      silently passes just because the drawing's OWN
            //      verification already ran; it is an independent check over
            //      the REFERENCED model files, not the drawing itself.
            //
            //      P6D ROUND 4 bugfix: gated on `baselineCaptured` - when the
            //      pre-flight baseline capture itself FAILED (item A),
            //      `integrityBaseline` is an empty placeholder and EVERY path
            //      would spuriously look "changed" (absent from an empty
            //      dictionary). Running this check against that placeholder
            //      would overwrite item A's precise, already-correct
            //      DrawingPhysicalCopyFailed reason with a misleading
            //      "SOURCE INTEGRITY VIOLATION" that names files that were
            //      simply never baselined, not files that are provably
            //      shown to have changed. Skipping this check when there is
            //      no real baseline to compare against loses nothing - item
            //      A's failure already fully explains why nothing proceeded. -
            if (baselineCaptured)
            {
                var integrityViolations = new List<string>();
                integrityViolations.AddRange(RecheckIntegrityBaseline(sourceReferenceSet, integrityBaseline)
                    .Select(name => $"{name} (ORIGINAL source)"));
                integrityViolations.AddRange(RecheckIntegrityBaseline(finalTargetSet, integrityBaseline)
                    .Select(name => $"{name} (final target)"));
                if (integrityViolations.Count > 0)
                {
                    var integrityReason = "SOURCE INTEGRITY VIOLATION: referenced model document integrity check failed "
                        + "after the drawing phase - the following referenced model file(s) no longer match their "
                        + "pre-drawing-phase SHA-256/size (a drawing Save/SaveAs/Save2 may have mutated a dependent): "
                        + string.Join(", ", integrityViolations.OrderBy(x => x, StringComparer.Ordinal))
                        + ". No further materialization will occur for this operation.";
                    sourceIntegrityViolationDetected = true;
                    drawingFailureOutcome = CopyDesignApplyOutcome.SourceIntegrityViolation;
                    drawingFailureReason = drawingFailureReason is null ? integrityReason : drawingFailureReason + " " + integrityReason;
                }
            }

            if (drawingFailureOutcome is not null)
            {
                drawingCleanup = CopyDesignCleanupPlanner.CleanUp(drawingJournal, deleteFile);
            }
        }

        // ==================================================================
        // PHASE 7: materialization. Every MODEL copy is always attempted
        // (it already passed verification above); a DRAWING copy is
        // attempted ONLY when the drawing phase did NOT fail - never
        // materialize a drawing whose copy/rewire/verification failed.
        //
        // P6D ROUND 4, HIGH fix (item C): the ONE exception to "every MODEL
        // copy is always attempted" - if a SOURCE INTEGRITY VIOLATION was
        // detected (see `sourceIntegrityViolationDetected` above), ZERO
        // FURTHER materialization is attempted for ANY not-yet-materialized
        // entry, model entries included. This is deliberately NOT the same
        // gate as "drawingFailureOutcome is not null" - an ORDINARY drawing
        // failure (rewire/verify/physical-copy-for-a-non-integrity-reason)
        // still lets already-verified models materialize normally, exactly
        // as before this round; only a genuine integrity violation suppresses
        // it. Entries already materialized from an earlier RESUME attempt
        // are untouched either way (the `alreadyMaterialized` check below
        // already skips them, and this outer gate never un-does that).
        // ==================================================================
        var factsBySourceId = allFacts.ToDictionary(f => f.CadDocumentId, StringComparer.Ordinal);
        var anyGap = false;
        if (!sourceIntegrityViolationDetected)
        {
            foreach (var node in modelCopyNodesInOrder)
            {
                // RESUME: already materialized - entryOutcomes already carries
                // the synthesized Materialization result. NEVER call the
                // materializer again for this id (never a second FileVersion 1).
                if (alreadyMaterialized.ContainsKey(node.CadDocumentId!))
                {
                    continue;
                }

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
        }

        // ROUND 2, MEDIUM fix (item 4): every reservation entry gets an
        // outcome, REUSE included, BEFORE any drawing-failure early return
        // below - a REUSE entry (model or drawing) never carries a physical
        // action and never depends on whether the drawing phase succeeded,
        // so it must never be silently omitted from the result just because
        // a LATER drawing COPY failed. Moved here (was previously AFTER the
        // drawing-failure return, meaning it was skipped entirely for any
        // failed-drawing attempt).
        foreach (var reuseEntry in request.Entries.Where(e => e.Entry.Action == CopyDesignApplyEntryAction.Reuse))
        {
            var resultingId = resultingIdByCadDocumentId[reuseEntry.Node.CadDocumentId!];
            entryOutcomes[reuseEntry.Node.CadDocumentId!] = new CopyDesignApplyEntryOutcome(
                resultingId, CopyDesignApplyEntryAction.Reuse, null, PhysicallyCreated: false, VerificationPassed: true, Materialization: null);
        }

        if (drawingFailureOutcome is not null)
        {
            return new CopyDesignApplyOperationResult(
                drawingFailureOutcome.Value, drawingFailureReason, operationId,
                FinalizeOutcomes(entryOutcomes), drawingCleanup, idempotencyKey);
        }

        var drawingFactsBySourceId = allDrawingFacts.ToDictionary(f => f.CadDocumentId, StringComparer.Ordinal);
        foreach (var node in drawingCopyNodesInOrder)
        {
            // RESUME: already materialized - never a second FileVersion 1.
            if (alreadyMaterialized.ContainsKey(node.CadDocumentId!))
            {
                continue;
            }

            var resultingCadDocumentId = resultingIdByCadDocumentId[node.CadDocumentId!];
            var facts = drawingFactsBySourceId[node.CadDocumentId!];
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

        // REUSE entries (models AND drawings alike) were already reported
        // above, BEFORE the drawing-failure return - see the ROUND 2,
        // MEDIUM fix comment there.
        var finalOutcome = anyGap ? CopyDesignApplyOutcome.SucceededWithMaterializationGaps : CopyDesignApplyOutcome.Succeeded;
        return new CopyDesignApplyOperationResult(finalOutcome, null, operationId, FinalizeOutcomes(entryOutcomes), null, idempotencyKey);
    }

    private static IReadOnlyList<CopyDesignApplyEntryOutcome> FinalizeOutcomes(
        Dictionary<string, CopyDesignApplyEntryOutcome> outcomes) =>
        outcomes.Values.OrderBy(o => o.CadDocumentId, StringComparer.Ordinal).ToArray();

    /// <summary>P6D ROUND 3, item G ("complete result reporting"): seeds ONE
    ///  outcome for EVERY entry a NOW-CONFIRMED reservation covers - called
    ///  exactly once, immediately after the response is validated (before any
    ///  phase runs). A REUSE entry gets its real, final outcome immediately
    ///  (REUSE requires no further physical work - nothing later ever
    ///  changes it). A COPY entry gets a placeholder that is truthful right
    ///  now (nothing has been physically created for it YET) and that every
    ///  later phase OVERWRITES the moment it actually processes that entry -
    ///  never a claim of success for work that has not happened.
    ///
    ///  This is what lets EVERY early-return AFTER this point - a resume-
    ///  status failure, a destination-revalidation failure, or a phase
    ///  failure partway through a batch - report a COMPLETE accounting of
    ///  every operation entry via <see cref="FinalizeOutcomes"/>, instead of
    ///  either an empty list or only the entries a phase happened to reach
    ///  before failing. The failing entry itself, and any entry after it in
    ///  the same phase never reached, both end up with
    ///  <see cref="CopyDesignApplyEntryOutcome.PhysicallyCreated"/> false -
    ///  the per-entry list is deliberately never the place that distinguishes
    ///  "attempted and failed" from "never attempted"; that distinction is
    ///  always the TOP-LEVEL <see cref="CopyDesignApplyOperationResult.Outcome"/>
    ///  + <see cref="CopyDesignApplyOperationResult.FailureReason"/> (which
    ///  already names the exact entry/file that failed).</summary>
    private static void SeedEntryOutcomes(
        Dictionary<string, CopyDesignApplyEntryOutcome> entryOutcomes,
        CopyDesignApplyRequest request,
        Dictionary<string, string> resultingIdByCadDocumentId)
    {
        foreach (var entry in request.Entries)
        {
            var sourceId = entry.Node.CadDocumentId!;
            var resultingId = resultingIdByCadDocumentId[sourceId];
            entryOutcomes[sourceId] = entry.Entry.Action == CopyDesignApplyEntryAction.Reuse
                ? new CopyDesignApplyEntryOutcome(
                    resultingId, CopyDesignApplyEntryAction.Reuse, null, PhysicallyCreated: false, VerificationPassed: true, Materialization: null)
                : new CopyDesignApplyEntryOutcome(
                    resultingId, CopyDesignApplyEntryAction.Copy, null, PhysicallyCreated: false, VerificationPassed: null, Materialization: null);
        }
    }

    /// <summary>An immutable, fully-resolved SHA-256 + file-size pair for one
    ///  protected path at one point in time - deliberately NOT nullable-field
    ///  based (see <see cref="CaptureIntegrityBaselineResult"/>'s own doc
    ///  comment for why the ROUND 3 design of encoding a capture failure as a
    ///  null-field snapshot was replaced this round).</summary>
    private readonly record struct FileIntegritySnapshot(string Sha256, long Size);

    /// <summary>P6D ROUND 4, CRITICAL fix (item A): the result of capturing a
    ///  pre-mutation integrity baseline for a SET of protected paths -
    ///  Success + a COMPLETE dictionary (every requested path present and
    ///  valid), or Failure + the exact reason, with NO dictionary at all.
    ///
    ///  REPLACES round 3's design, where a capture failure for one path was
    ///  encoded as a snapshot with null fields and only discovered later (at
    ///  the FIRST recheck point) by a null-vs-anything comparison - which
    ///  meant a drawing physical-copy call could already have happened
    ///  before that discovery. This type makes "the baseline itself could
    ///  not be safely established" a distinct, checkable FAILURE the caller
    ///  must act on BEFORE calling the physical copier even once, rather than
    ///  a deferred comparison outcome.</summary>
    private sealed record CaptureIntegrityBaselineResult(
        bool Success, string? FailureReason, IReadOnlyDictionary<string, FileIntegritySnapshot>? Baseline)
    {
        public static CaptureIntegrityBaselineResult Ok(IReadOnlyDictionary<string, FileIntegritySnapshot> baseline) =>
            new(true, null, baseline);

        public static CaptureIntegrityBaselineResult Fail(string reason) => new(false, reason, null);
    }

    /// <summary>P6D ROUND 4, CRITICAL fix (item A): captures a SHA-256 +
    ///  file-size snapshot for EVERY path in <paramref name="paths"/> -
    ///  fails the WHOLE capture closed, with the exact reason, on the FIRST
    ///  blank path, unreadable file, hash failure, or size failure
    ///  encountered - never returns a partial/incomplete baseline. The
    ///  caller must check <see cref="CaptureIntegrityBaselineResult.Success"/>
    ///  BEFORE calling the physical copier for any drawing.</summary>
    private CaptureIntegrityBaselineResult CaptureIntegrityBaseline(IReadOnlyList<string> paths)
    {
        var baseline = new Dictionary<string, FileIntegritySnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return CaptureIntegrityBaselineResult.Fail(
                    "A protected model reference path is blank - refusing to establish an integrity baseline (fail closed).");
            }

            string sha256;
            try
            {
                sha256 = hasher.ComputeSha256(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                return CaptureIntegrityBaselineResult.Fail(
                    $"Could not read \"{Path.GetFileName(path)}\" to establish its pre-mutation integrity baseline "
                        + $"(fail closed before any drawing physical copy): {ex.Message}");
            }

            long size;
            try
            {
                size = _getFileSize(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                return CaptureIntegrityBaselineResult.Fail(
                    $"Could not read the size of \"{Path.GetFileName(path)}\" to establish its pre-mutation integrity "
                        + $"baseline (fail closed before any drawing physical copy): {ex.Message}");
            }

            baseline[path] = new FileIntegritySnapshot(sha256, size);
        }
        return CaptureIntegrityBaselineResult.Ok(baseline);
    }

    /// <summary>Re-reads every path in <paramref name="paths"/> and compares
    ///  it against its already-established <paramref name="baseline"/>
    ///  snapshot - returns the file name (not full path, to avoid leaking
    ///  local disk layout into failure messages) of every path whose hash or
    ///  size no longer matches, is now unreadable, or was never captured
    ///  (never trusted blindly even though the baseline is complete by
    ///  construction). Never throws - a comparison failure IS a violation,
    ///  not an exception to propagate.</summary>
    private List<string> RecheckIntegrityBaseline(
        IReadOnlyList<string> paths, IReadOnlyDictionary<string, FileIntegritySnapshot> baseline)
    {
        var violations = new List<string>();
        foreach (var path in paths)
        {
            if (!baseline.TryGetValue(path, out var before))
            {
                violations.Add(Path.GetFileName(path));
                continue;
            }
            var after = ReadIntegritySnapshot(path);
            var unchanged = after is { } afterValue
                && string.Equals(before.Sha256, afterValue.Sha256, StringComparison.OrdinalIgnoreCase)
                && before.Size == afterValue.Size;
            if (!unchanged)
            {
                violations.Add(Path.GetFileName(path));
            }
        }
        return violations;
    }

    private FileIntegritySnapshot? ReadIntegritySnapshot(string path)
    {
        string sha256;
        try { sha256 = hasher.ComputeSha256(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { return null; }
        long size;
        try { size = _getFileSize(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { return null; }
        return new FileIntegritySnapshot(sha256, size);
    }
}
