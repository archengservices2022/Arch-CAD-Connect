namespace Arch.CadConnect.Core.CopyDesign.Apply;

public enum CopyDesignApplyOutcome
{
    /// <summary>Another apply operation is already running in this process
    ///  (the in-process guard refused entry) - nothing was attempted.</summary>
    AlreadyRunning,

    /// <summary>The plan is not executable, or an in-scope node could not be
    ///  mapped to a P6B request at all - nothing was attempted.</summary>
    MappingFailed,

    /// <summary>HIGH 1 fix (P6C): the confirmed plan contained at least one
    ///  IDW/DWG node that would participate in Copy Design (a COPY or REUSE
    ///  action), but the plan was never explicitly acknowledged as "model
    ///  files only" (<see cref="CopyDesignPlan.ModelFilesOnlyAcknowledged"/>).
    ///  P6C's ORIGINAL fix for "never silently omit a represented drawing"
    ///  was to REFUSE to apply until acknowledged. RETIRED by P6D:
    ///  <see cref="CopyDesignApplyOrchestrator"/> no longer produces this
    ///  outcome - an unacknowledged drawing is now EXECUTED (P6D owns
    ///  drawings), which satisfies the SAME "never silently omitted"
    ///  requirement more usefully. Kept defined (never removed) only for
    ///  source/binary compatibility with any existing exhaustive switch;
    ///  see <see cref="DrawingPhysicalCopyFailed"/>/
    ///  <see cref="DrawingReferenceRewiringFailed"/>/
    ///  <see cref="DrawingVerificationFailed"/> for P6D's actual drawing
    ///  failure outcomes.</summary>
    DrawingsPresentWithoutAcknowledgement,

    /// <summary>Pre-mutation revalidation found the world has changed since
    ///  preview (missing source, existing destination, duplicate
    ///  destination, or an existence check itself failed) - nothing was
    ///  attempted. No reservation call was made.</summary>
    RevalidationFailed,

    /// <summary>The P6B reservation HTTP call itself failed (network/
    ///  transport/server error) - whether the server actually committed is
    ///  UNKNOWN; retry with the SAME idempotency key is safe.</summary>
    ReservationCallFailed,

    /// <summary>The server's reservation response did not match the request
    ///  (count/order/action/identity mismatch). STOPPED BEFORE PHYSICAL
    ///  MUTATION as required - but the reservation itself may still be
    ///  durable server-side; see <see cref="CopyDesignApplyOperationResult.CopyDesignOperationId"/>.</summary>
    ReservationResponseInvalid,

    /// <summary>A physical copy failed after reservation succeeded - RESERVED
    ///  BUT UNMATERIALIZED. Local destination artifacts created by THIS
    ///  attempt were cleaned up where safe (see <see cref="CopyDesignApplyOperationResult.Cleanup"/>).</summary>
    PhysicalCopyFailed,

    /// <summary>Reference rewiring failed after every physical copy
    ///  succeeded - RESERVED BUT UNMATERIALIZED, cleaned up where safe.</summary>
    ReferenceRewiringFailed,

    /// <summary>Verification failed for at least one MODEL (IAM/IPT) entry
    ///  after physical copy and rewiring succeeded - RESERVED BUT
    ///  UNMATERIALIZED, cleaned up where safe. No FileVersion is ever created
    ///  for a failed verification. Per the required safe ordering (P6D:
    ///  models complete and verify BEFORE any drawing copy begins), this
    ///  outcome can only ever occur before drawing execution starts - no
    ///  drawing artifact could exist yet when this is returned.</summary>
    VerificationFailed,

    /// <summary>P6D: a physical drawing (IDW/DWG) copy failed AFTER every
    ///  in-scope MODEL entry already succeeded, was rewired, and verified.
    ///  Every model entry that already verified is still MATERIALIZED (see
    ///  each entry's own <see cref="CopyDesignApplyEntryOutcome.Materialization"/>)
    ///  - only the drawing phase is reserved-but-unmaterialized. Cleanup
    ///  removed ONLY the drawing destination artifacts this attempt itself
    ///  created; no model file is ever touched by a drawing-phase
    ///  failure.</summary>
    DrawingPhysicalCopyFailed,

    /// <summary>P6D: rewiring a copied drawing's model reference(s) failed
    ///  after every in-scope drawing was physically copied. Same
    ///  "models already materialized, drawing phase cleaned up and
    ///  unmaterialized" semantics as <see cref="DrawingPhysicalCopyFailed"/>.</summary>
    DrawingReferenceRewiringFailed,

    /// <summary>P6D: verification failed for at least one copied drawing
    ///  (a stale, unexpected, unresolved, or missing model reference; a
    ///  changed source hash; a type/openability mismatch) after every
    ///  in-scope drawing was physically copied and rewired. Same
    ///  "models already materialized, drawing phase cleaned up and
    ///  unmaterialized" semantics as <see cref="DrawingPhysicalCopyFailed"/> -
    ///  no drawing is EVER materialized when this is returned (see the P6D
    ///  ordering requirement: never materialize a drawing whose model
    ///  reference verification failed).</summary>
    DrawingVerificationFailed,

    /// <summary>P6D ROUND 4, HIGH fix (item C): a PROTECTED original source/
    ///  reference file's SHA-256 or size changed, disappeared, or became
    ///  unreadable AFTER drawing physical mutation began (detected either
    ///  immediately before/after one specific drawing's SaveAs, or by the
    ///  final post-drawing-phase integrity re-check). This is a STRONGER
    ///  safety failure than an ordinary drawing failure
    ///  (<see cref="DrawingPhysicalCopyFailed"/> / <see cref="DrawingReferenceRewiringFailed"/> /
    ///  <see cref="DrawingVerificationFailed"/>): it means a file this
    ///  operation is contractually required to leave untouched was NOT left
    ///  untouched, so - UNLIKE those ordinary drawing failures - ZERO further
    ///  materialization is ever attempted for ANY not-yet-materialized entry,
    ///  model entries included. Entries already materialized from an earlier
    ///  RESUME attempt are left exactly as they were (never rolled back,
    ///  never fabricated). Drawing-owned artifacts created by THIS attempt
    ///  are still cleaned up where safe; the P6B reservation itself is never
    ///  touched.</summary>
    SourceIntegrityViolation,

    /// <summary>P6D ROUND 2, HIGH fix (RESUME): a reserved
    ///  <c>resultingCadDocumentId</c> was found to be in a state RESUME
    ///  cannot safely reconcile - a FileVersion exists but its version
    ///  number is not exactly 1 (should be structurally impossible for a
    ///  fresh Copy Design target, but never trusted blindly), the server
    ///  reports "materialized" while the expected local destination file is
    ///  missing, or the local file's hash/size no longer matches the
    ///  server-authoritative integrity metadata (a changed/corrupt
    ///  materialized binary). Nothing is attempted or cleaned up - see
    ///  <see cref="CopyDesignApplyOperationResult.FailureReason"/> for the
    ///  exact entry and condition; the P6B reservation is untouched.</summary>
    UnexpectedMaterializationState,

    /// <summary>Every entry was physically created, rewired (if an IAM or a
    ///  drawing with model references), verified, AND materialized with its
    ///  first FileVersion.</summary>
    Succeeded,

    /// <summary>Every entry was physically created, rewired (if an IAM), and
    ///  verified - but at least one entry's materialization did not complete
    ///  (see each entry's own <see cref="CopyDesignApplyEntryOutcome.Materialization"/>,
    ///  <see cref="CopyDesignMaterializationOutcome.Failed"/>, for the exact
    ///  reason). RESERVED BUT UNMATERIALIZED for those entries - this is an
    ///  HONEST terminal state, not a failure requiring cleanup: the physical
    ///  file is correct and verified; only the server-side FileVersion step
    ///  did not complete.</summary>
    SucceededWithMaterializationGaps,
}

public sealed record CopyDesignApplyEntryOutcome(
    string CadDocumentId,
    CopyDesignApplyEntryAction Action,
    string? DestinationAbsolutePath,
    bool PhysicallyCreated,
    bool? VerificationPassed,
    CopyDesignMaterializationResult? Materialization);

public sealed record CopyDesignApplyOperationResult(
    CopyDesignApplyOutcome Outcome,
    string? FailureReason,
    /// <summary>Present whenever the P6B server confirmed a reservation, even
    ///  if a later step failed - NEVER cleared, NEVER re-derived, so a
    ///  failure report always names the durable reservation to investigate.</summary>
    string? CopyDesignOperationId,
    IReadOnlyList<CopyDesignApplyEntryOutcome> Entries,
    CopyDesignCleanupReport? Cleanup,
    /// <summary>HIGH 2 fix: the idempotency key THIS attempt used, on every
    ///  outcome (success or failure) - so an uncertain result (see
    ///  <see cref="CopyDesignApplyOutcome.ReservationCallFailed"/>) always
    ///  carries the exact key needed to safely retry the SAME logical
    ///  attempt (<see cref="CopyDesignApplyAttempt.Resume"/>), never a
    ///  freshly-minted one.</summary>
    string? IdempotencyKey = null)
{
    public static CopyDesignApplyOperationResult Simple(CopyDesignApplyOutcome outcome, string reason, string? idempotencyKey = null) =>
        new(outcome, reason, null, Array.Empty<CopyDesignApplyEntryOutcome>(), null, idempotencyKey);

    /// <summary>True whenever a P6B reservation exists but the operation did
    ///  not fully succeed with materialization - the exact state P6C must
    ///  never hide, delete, or paper over.</summary>
    public bool ReservedButUnmaterialized =>
        CopyDesignOperationId is not null && Outcome != CopyDesignApplyOutcome.Succeeded;
}
