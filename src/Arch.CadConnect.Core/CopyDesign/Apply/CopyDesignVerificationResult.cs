namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6E-B: the three-way outcome of an independent, read-only Copy Design
/// verification pass.
///
///   VERIFIED   - every REQUIRED check on every entry PASSED. An
///                INFORMATIONAL check that is NOT_PROVABLE (see
///                <see cref="CopyDesignVerificationCheckRequirement"/>'s own
///                doc comment - e.g. a REUSE entry's historical byte
///                immutability, which P6D never freezes evidence for) does
///                NOT by itself prevent VERIFIED - an otherwise-correct
///                mixed COPY+REUSE operation reaches VERIFIED while still
///                truthfully reporting that one informational limitation in
///                its per-entry Checks. VERIFIED is never reachable while
///                ANY entry is still PENDING/INVALID at the operation-status
///                level (that IS a required check).
///   FAILED     - at least one check found POSITIVE evidence of a problem,
///                observed AFTER a trustworthy per-entry graph was already
///                built (a proven hash/size mismatch, a missing materialized
///                destination, a stale or unplanned reference, a
///                wrong/missing reused identity). Takes priority over
///                INCOMPLETE: if anything is PROVEN wrong, the whole result
///                is FAILED even if other entries are merely unfinished.
///   INCOMPLETE - a legitimate pass/fail conclusion could not be reached,
///                because required authoritative evidence/reconstruction/
///                facts are unavailable or malformed - this is NEVER "the
///                copied package was proven wrong". Two distinct sources:
///                (a) an entry-level REQUIRED check that could not reach a
///                verdict (an entry not yet finished (PENDING), a
///                server-side state that is itself inconsistent (INVALID),
///                or REQUIRED evidence that is NOT_PROVABLE - missing/
///                invalid source-integrity evidence, a drawing whose model-
///                dependency authority was never confirmed); (b) a
///                STRUCTURAL failure - the expected verification topology
///                itself could not be safely constructed at all (see
///                <see cref="CopyDesignOperationVerificationResult.StructuralFailure"/>'s
///                own doc comment) - P6E-B/D FINAL SEMANTIC ALIGNMENT:
///                REPLACES this class's PRIOR behavior of reporting a
///                structural/reconstruction failure as FAILED, since
///                "the topology could not be built" is categorically
///                "verification cannot legitimately conclude", never a
///                proven defect in the copied package itself. Verification
///                MUST NEVER promote an unfinished/unprovable-on-a-REQUIRED-
///                check or structurally-unbuildable operation to VERIFIED.
/// </summary>
public enum CopyDesignVerificationOutcome
{
    Verified,
    Failed,
    Incomplete,
}

/// <summary>Per-check result state - see
///  <see cref="CopyDesignVerificationOutcome"/>'s own doc comment for the
///  FAILED-vs-NOT_PROVABLE distinction this mirrors at the individual-check
///  level: <see cref="Failed"/> means positive proof of a mismatch;
///  <see cref="NotProvable"/> means the evidence needed to decide either way
///  does not exist - two different facts, never collapsed into one.</summary>
public enum CopyDesignVerificationCheckState
{
    Passed,
    Failed,
    NotProvable,
}

/// <summary>
/// P6E-B SEMANTIC CORRECTION: whether a NOT_PROVABLE check result is allowed
/// to roll an entry (and therefore the whole operation) up to INCOMPLETE.
///
///   Required     - this fact is necessary to call the entry VERIFIED.
///                  NOT_PROVABLE here means "we genuinely cannot vouch for
///                  this entry yet" and forces INCOMPLETE.
///   Informational - this fact is reported honestly, in full, alongside
///                  every other check - but its own NOT_PROVABLE state is a
///                  KNOWN, DOCUMENTED, PERMANENT limitation of what P6D's
///                  durable data can prove (not a sign this PARTICULAR
///                  entry is unfinished or suspicious), so it must NEVER by
///                  itself prevent VERIFIED. The one case this round
///                  introduces it for: a REUSE entry's historical byte
///                  immutability (P6D never freezes a source FileVersion
///                  for REUSE - see <see cref="CopyDesignVerificationOrchestrator"/>'s
///                  own REUSE doc comment) - this is architecture-locked:
///                  that limitation must not force INCOMPLETE.
///
/// A FAILED check's requirement is irrelevant to rollup (see
/// <see cref="CopyDesignVerificationEntryResult.Rollup"/>) - POSITIVE proof
/// of a problem always makes the result FAILED regardless of which bucket
/// the check belongs to. Only the NOT_PROVABLE escalation is gated by this.
/// </summary>
public enum CopyDesignVerificationCheckRequirement
{
    Required,
    Informational,
}

/// <summary>ONE named, explicit check result - always present for every
///  check that was applicable to an entry (never a silent omission), so a
///  consumer can enumerate exactly what was and was not evaluated.</summary>
public sealed record CopyDesignVerificationCheck(
    string Name,
    CopyDesignVerificationCheckState State,
    CopyDesignVerificationCheckRequirement Requirement,
    /// <summary>Human-readable explanation - required (non-null) for
    ///  anything other than <see cref="CopyDesignVerificationCheckState.Passed"/>.</summary>
    string? Detail = null)
{
    public static CopyDesignVerificationCheck Pass(string name, CopyDesignVerificationCheckRequirement requirement = CopyDesignVerificationCheckRequirement.Required) =>
        new(name, CopyDesignVerificationCheckState.Passed, requirement);
    public static CopyDesignVerificationCheck Fail(string name, string detail, CopyDesignVerificationCheckRequirement requirement = CopyDesignVerificationCheckRequirement.Required) =>
        new(name, CopyDesignVerificationCheckState.Failed, requirement, detail);
    public static CopyDesignVerificationCheck NotProvable(string name, string detail, CopyDesignVerificationCheckRequirement requirement = CopyDesignVerificationCheckRequirement.Required) =>
        new(name, CopyDesignVerificationCheckState.NotProvable, requirement, detail);
}

/// <summary>The complete verification verdict for ONE operation entry
///  (COPY or REUSE), carrying EVERY check that was applicable - never
///  fewer, never a summary that drops detail.</summary>
public sealed record CopyDesignVerificationEntryResult(
    int Ordinal,
    string ResultingCadDocumentId,
    /// <summary>"COPY" or "REUSE" - the raw wire string, matching
    ///  <see cref="CopyDesignDurableResumeEntry.Action"/>.</summary>
    string Action,
    string OriginalFileName,
    string OriginalDocumentType,
    CopyDesignVerificationOutcome Outcome,
    IReadOnlyList<CopyDesignVerificationCheck> Checks)
{
    /// <summary>FAILED (any check, any requirement) beats INCOMPLETE
    ///  (a REQUIRED check that is NOT_PROVABLE) beats all-else-PASSED. An
    ///  INFORMATIONAL check's NOT_PROVABLE state is deliberately EXCLUDED
    ///  from the INCOMPLETE test - see <see cref="CopyDesignVerificationCheckRequirement"/>'s
    ///  own doc comment.</summary>
    public static CopyDesignVerificationOutcome Rollup(IReadOnlyList<CopyDesignVerificationCheck> checks)
    {
        if (checks.Any(c => c.State == CopyDesignVerificationCheckState.Failed))
        {
            return CopyDesignVerificationOutcome.Failed;
        }
        if (checks.Any(c => c.State == CopyDesignVerificationCheckState.NotProvable && c.Requirement == CopyDesignVerificationCheckRequirement.Required))
        {
            return CopyDesignVerificationOutcome.Incomplete;
        }
        return CopyDesignVerificationOutcome.Verified;
    }
}

/// <summary>The complete verification verdict for ONE Copy Design
///  operation. <see cref="FailureReason"/> is populated ONLY for a
///  STRUCTURAL failure (malformed/inconsistent durable data so severe that
///  no per-entry graph could be safely built at all - e.g. a gapped ordinal
///  sequence, or the local source workspace not matching this operation's
///  own source identities) - in that case <see cref="Entries"/> is empty,
///  since attributing problems to specific entries would not be honest when
///  the WHOLE structure could not be trusted.
///
///  P6E-B/D FINAL SEMANTIC ALIGNMENT: a STRUCTURAL failure is ALWAYS
///  <see cref="CopyDesignVerificationOutcome.Incomplete"/>, never
///  <see cref="CopyDesignVerificationOutcome.Failed"/> - see
///  <see cref="StructuralFailure"/>'s own doc comment for why. An ordinary
///  per-entry problem (a missing file, a hash mismatch, unprovable evidence)
///  is NEVER a structural failure - it always shows up as a normal per-entry
///  <see cref="CopyDesignVerificationOutcome.Failed"/>/<see cref="CopyDesignVerificationOutcome.Incomplete"/>
///  result instead, so the run stays maximally diagnostic rather than
///  aborting at the first problem.</summary>
public sealed record CopyDesignOperationVerificationResult(
    CopyDesignVerificationOutcome Outcome,
    string? CopyDesignOperationId,
    IReadOnlyList<CopyDesignVerificationEntryResult> Entries,
    string? FailureReason = null)
{
    /// <summary>
    /// P6E-B/D FINAL SEMANTIC ALIGNMENT: the SINGLE, AUTHORITATIVE
    /// construction for "the expected verification topology/reconstruction
    /// could not be safely built at all" - ALWAYS
    /// <see cref="CopyDesignVerificationOutcome.Incomplete"/>. This is a
    /// deliberate correction from this class's prior behavior (FAILED):
    /// "the topology could not be constructed" means "verification cannot
    /// legitimately conclude" (missing/malformed authoritative evidence,
    /// data, or associations) - it does NOT mean "the copied package was
    /// proven wrong". A concrete, PROVEN defect discovered AFTER a
    /// trustworthy topology was already built (a hash mismatch, a missing
    /// materialized destination, a stale reference) is NEVER represented
    /// this way - those remain ordinary per-entry FAILED results.
    ///
    /// Both <see cref="CopyDesignVerificationOrchestrator"/>'s own internal
    /// topology-build fallback AND any caller that needs to guarantee zero
    /// COM construction before even attempting verification (see
    /// P6E-D's controller pre-check) MUST construct their "cannot safely
    /// build" result through this ONE shared factory - never a separately
    /// invented outcome/interpretation.
    /// </summary>
    public static CopyDesignOperationVerificationResult StructuralFailure(string reason, string? operationId = null) =>
        new(CopyDesignVerificationOutcome.Incomplete, operationId, Array.Empty<CopyDesignVerificationEntryResult>(), reason);

    /// <summary>FAILED beats INCOMPLETE beats all-VERIFIED - mirrors
    ///  <see cref="CopyDesignVerificationEntryResult.Rollup"/> one level up.</summary>
    public static CopyDesignVerificationOutcome Rollup(IReadOnlyList<CopyDesignVerificationEntryResult> entries)
    {
        if (entries.Any(e => e.Outcome == CopyDesignVerificationOutcome.Failed))
        {
            return CopyDesignVerificationOutcome.Failed;
        }
        if (entries.Any(e => e.Outcome == CopyDesignVerificationOutcome.Incomplete))
        {
            return CopyDesignVerificationOutcome.Incomplete;
        }
        return CopyDesignVerificationOutcome.Verified;
    }
}
