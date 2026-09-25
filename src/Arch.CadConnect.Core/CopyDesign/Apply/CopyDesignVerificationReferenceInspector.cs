namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6E-B EVALUATOR ADAPTATION: a NARROW, EXPLICITLY-BOUNDARIED adapter over
/// the EXISTING, COMPLETELY UNMODIFIED P6D <see cref="CopyDesignVerificationEvaluator.Evaluate"/> -
/// reused for EXACTLY the parts of its verdict that remain semantically
/// valid for a STANDALONE, LATER, read-only P6E verification pass:
/// destination existence, document type match, Inventor-openability, and -
/// the real value being reused here - the COMPLETE reference-topology
/// comparison (stale/unplanned/missing-reference detection), which is
/// subtle, already battle-tested, and deliberately NOT reimplemented a
/// second time.
///
/// THE COMPATIBILITY BOUNDARY, AND WHY IT EXISTS:
/// <see cref="CopyDesignVerificationEvaluator.Evaluate"/> ALSO judges
/// whether <c>SourceSha256BeforeOperation</c> still equals
/// <c>SourceSha256AfterOperation</c> - a check that is completely valid at
/// APPLY TIME (where "before" genuinely means "immediately before THIS
/// attempt's own physical copy", captured once by the orchestrator BEFORE
/// any mutation) but has NO valid meaning for P6E, which runs long after
/// the operation - there is no "before this attempt" moment left to compare
/// against. P6E's REAL source-immutability verdict, against the DURABLE,
/// checked-in authoritative baseline, is computed EXCLUSIVELY by
/// <see cref="CopyDesignVerificationOrchestrator"/>'s own, entirely
/// SEPARATE <c>SourceIntegrity</c> check, from genuine evidence
/// (<see cref="CopyDesignSourceIntegrityEvidence"/>) that is NEVER
/// fabricated - this class has nothing to do with that check and never
/// influences it.
///
/// To still reuse <see cref="CopyDesignVerificationEvaluator.Evaluate"/>'s
/// reference-topology logic rather than re-implementing it, this adapter
/// supplies <paramref name="currentSourceSha256"/> (the SAME value the
/// caller's OWN SourceIntegrity check already computed - never a second,
/// separate hash computation, and never itself treated as a historical
/// baseline by either caller) for what <see cref="ICopyDesignVerifier.GatherFactsAsync"/>
/// treats as "before", and lets it independently re-hash the CURRENT source
/// as "after". This is, AT MOST, two reads of the CURRENT file moments
/// apart - it can detect that the source changed WHILE this ONE scan was
/// running (a narrow, genuine fact), but it is structurally INCAPABLE of
/// producing a false claim about long-term immutability in either
/// direction, because it never compares against anything OTHER than
/// "itself, a moment ago". <see cref="Result"/> deliberately carries NO
/// before/after/hash field of any kind - only <c>Passed</c>/<c>Reasons</c> -
/// so nothing downstream of this adapter could ever mistake this internal
/// artifact for a genuine historical measurement. The ONE reason
/// <see cref="CopyDesignVerificationEvaluator.Evaluate"/> could emit from
/// that now-meaningless comparison is explicitly stripped (see
/// <see cref="InspectAsync"/>'s own comment for why an exact-literal match
/// is safe here) - every OTHER reason (destination/type/openable/reference-
/// topology) is genuine and passed through completely unchanged.
///
/// A SECOND, SEPARATE reason is also filtered here, for a different
/// reason: <see cref="CopyDesignVerificationEvaluator.Evaluate"/> also
/// flags <c>SourceAbsolutePath == DestinationAbsolutePath</c> as a defect
/// (a genuine, valuable check at apply time - a copy must never target its
/// own source). For a REUSE entry, <see cref="CopyDesignVerificationOrchestrator"/>
/// has NO separate destination to inspect at all (nothing was ever copied
/// for REUSE) - it deliberately passes a "probe" node whose
/// <c>ProposedDestinationAbsolutePath</c> is redirected to the REUSE'd
/// document's OWN <c>SourceAbsolutePath</c>, purely so
/// <see cref="ICopyDesignVerifier.GatherFactsAsync"/> (which always opens
/// <c>ProposedDestinationAbsolutePath</c>) has a real, existing file to
/// open - the UNCHANGED source itself. Source-equals-destination is then
/// not a defect but the WHOLE POINT of that probe, so this adapter strips
/// that ONE reason too. For a genuine COPY entry this reason can never
/// legitimately fire in the first place (source and destination are, by
/// construction, always different paths), so stripping it is a no-op there
/// - this filter is safe for both callers.
///
/// KNOWN LIMITATION (accepted, documented, not silently hidden): both
/// stripped-reason matches are exact string literals, not shared constants
/// (P6D's evaluator does not expose them, and this class does not modify
/// P6D to add any). If either message ever changes wording in a future P6D
/// round, this filter simply stops matching that one - which FAILS SAFE: an
/// extra, harmless reason (vacuously true for the before/after case; a
/// deliberate, expected fact for the REUSE-probe case) would surface in
/// P6E's output, never a silently dropped REAL one.
/// </summary>
public static class CopyDesignVerificationReferenceInspector
{
    public sealed record Result(bool Passed, IReadOnlyList<string> Reasons);

    /// <summary>The EXACT literal <see cref="CopyDesignVerificationEvaluator.Evaluate"/>
    ///  emits for its source-hash-before/after check - see this class's own
    ///  doc comment for why an exact-literal match here is safe and why a
    ///  future mismatch fails safe rather than unsafe.</summary>
    private const string SourceHashReasonOutOfScopeForThisAdapter =
        "The source file's SHA-256 could not be re-verified as unchanged - the source may have been mutated.";

    /// <summary>The EXACT literal <see cref="CopyDesignVerificationEvaluator.Evaluate"/>
    ///  emits when source and destination paths are identical - see this
    ///  class's own doc comment for why that is EXPECTED (not a defect) for
    ///  P6E's REUSE-probe technique, and a structural impossibility (so a
    ///  harmless no-op filter) for a genuine COPY entry.</summary>
    private const string SourceEqualsDestinationReasonExpectedForReuseProbe =
        "The source and destination paths are identical.";

    public static async Task<Result> InspectAsync(
        ICopyDesignVerifier verifier,
        CopyDesignNode node,
        IReadOnlyList<CopyDesignReferenceTarget> referenceTargets,
        /// <summary>The SAME current-source-hash value the caller's OWN
        ///  durable-baseline SourceIntegrity check already computed (or
        ///  <c>null</c> when that could not be computed either, e.g. the
        ///  local source file is unreadable) - see this class's own doc
        ///  comment for why this is safe: it is used ONLY as an intra-scan
        ///  consistency probe, NEVER as, and never described as, a
        ///  historical baseline.</summary>
        string? currentSourceSha256,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(referenceTargets);

        var probe = currentSourceSha256 ?? string.Empty;
        var facts = await verifier.GatherFactsAsync(node, referenceTargets, probe, ct).ConfigureAwait(false);
        var evaluation = CopyDesignVerificationEvaluator.Evaluate(facts);

        var reasons = evaluation.Reasons
            .Where(r => r != SourceHashReasonOutOfScopeForThisAdapter && r != SourceEqualsDestinationReasonExpectedForReuseProbe)
            .ToArray();
        return new Result(reasons.Length == 0, reasons);
    }
}
