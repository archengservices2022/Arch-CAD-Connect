namespace Arch.CadConnect.Core.CopyDesign.Apply;

public enum CopyDesignApplyOutcome
{
    /// <summary>Another apply operation is already running in this process
    ///  (the in-process guard refused entry) - nothing was attempted.</summary>
    AlreadyRunning,

    /// <summary>The plan is not executable, or an in-scope node could not be
    ///  mapped to a P6B request at all - nothing was attempted.</summary>
    MappingFailed,

    /// <summary>HIGH 1 fix: the confirmed plan contains at least one IDW/DWG
    ///  node that would participate in Copy Design (a COPY or REUSE action),
    ///  but the plan was never explicitly acknowledged as "model files only"
    ///  (<see cref="CopyDesignPlan.ModelFilesOnlyAcknowledged"/>). P6C never
    ///  silently omits a represented drawing - nothing was attempted. Either
    ///  re-preview with explicit model-files-only acknowledgement, or resolve
    ///  the drawing(s) so P6D can own them.</summary>
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

    /// <summary>Verification failed for at least one entry after physical
    ///  copy and rewiring succeeded - RESERVED BUT UNMATERIALIZED, cleaned up
    ///  where safe. No FileVersion is ever created for a failed
    ///  verification.</summary>
    VerificationFailed,

    /// <summary>Every entry was physically created, rewired (if an IAM),
    ///  verified, AND materialized with its first FileVersion.</summary>
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
