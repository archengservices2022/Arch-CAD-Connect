using Arch.CadConnect.Core.CopyDesign.Apply;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

public class CopyDesignVerificationEvaluatorTests
{
    private static CopyDesignNodeVerificationFacts GoodFacts(
        IReadOnlyList<CopyDesignReferenceResolutionFact>? refs = null,
        IReadOnlyList<CopyDesignActualComponentOccurrence>? actual = null,
        bool occurrenceEnumerationSucceeded = true) => new(
        "cad-1", @"C:\src\a.ipt", @"C:\dst\a-new.ipt",
        DestinationExists: true, DocumentTypeMatches: true, OpenableThroughInventor: true,
        ResultingSha256: "resulthash", ResultingFileSize: 12345L,
        SourceSha256BeforeOperation: "samehash", SourceSha256AfterOperation: "samehash",
        ReferenceResolutions: refs ?? Array.Empty<CopyDesignReferenceResolutionFact>(),
        // Default: every expected target has exactly one occurrence
        // resolving to it - "everything expected is present and correct".
        ActualComponentOccurrences: actual
            ?? (refs ?? Array.Empty<CopyDesignReferenceResolutionFact>())
                .Select(r => new CopyDesignActualComponentOccurrence(r.ExpectedTargetAbsolutePath)).ToArray(),
        OccurrenceEnumerationSucceeded: occurrenceEnumerationSucceeded);

    [Fact]
    public void FullyGoodFactsPass()
    {
        var result = CopyDesignVerificationEvaluator.Evaluate(GoodFacts());
        Assert.True(result.Passed);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void MissingDestinationFails()
    {
        var facts = GoodFacts() with { DestinationExists = false };
        Assert.False(CopyDesignVerificationEvaluator.Evaluate(facts).Passed);
    }

    // 20. source hashes unchanged verification
    [Fact]
    public void ChangedSourceHashFails()
    {
        var facts = GoodFacts() with { SourceSha256AfterOperation = "different-hash" };
        var result = CopyDesignVerificationEvaluator.Evaluate(facts);
        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, r => r.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NullSourceHashAfterOperationFails()
    {
        var facts = GoodFacts() with { SourceSha256AfterOperation = null };
        Assert.False(CopyDesignVerificationEvaluator.Evaluate(facts).Passed);
    }

    [Fact]
    public void SourceEqualsDestinationFails()
    {
        var facts = GoodFacts() with { DestinationAbsolutePath = @"C:\src\a.ipt" };
        Assert.False(CopyDesignVerificationEvaluator.Evaluate(facts).Passed);
    }

    [Fact]
    public void DocumentTypeMismatchFails()
    {
        var facts = GoodFacts() with { DocumentTypeMatches = false };
        Assert.False(CopyDesignVerificationEvaluator.Evaluate(facts).Passed);
    }

    [Fact]
    public void NotOpenableThroughInventorFails()
    {
        var facts = GoodFacts() with { OpenableThroughInventor = false };
        Assert.False(CopyDesignVerificationEvaluator.Evaluate(facts).Passed);
    }

    [Fact]
    public void MissingResultingHashFails()
    {
        var facts = GoodFacts() with { ResultingSha256 = null };
        Assert.False(CopyDesignVerificationEvaluator.Evaluate(facts).Passed);
    }

    [Fact]
    public void MissingResultingFileSizeFails()
    {
        var facts = GoodFacts() with { ResultingFileSize = null };
        Assert.False(CopyDesignVerificationEvaluator.Evaluate(facts).Passed);
    }

    [Fact]
    public void ZeroResultingFileSizeFails()
    {
        var facts = GoodFacts() with { ResultingFileSize = 0L };
        Assert.False(CopyDesignVerificationEvaluator.Evaluate(facts).Passed);
    }

    // 22. unresolved managed reference fails
    [Fact]
    public void UnresolvedReferenceFails()
    {
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-child", @"C:\dst\child-new.ipt", @"C:\src\child.ipt", null, true) };
        var actual = new[] { new CopyDesignActualComponentOccurrence(null) };
        var result = CopyDesignVerificationEvaluator.Evaluate(GoodFacts(refs, actual));
        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, r => r.Contains("did not resolve", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CopyChildResolvingToStaleSourceInsteadOfNewCopyFails()
    {
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-child", @"C:\dst\child-new.ipt", @"C:\src\child.ipt", null, true) };
        var actual = new[] { new CopyDesignActualComponentOccurrence(@"C:\src\child.ipt") };
        var result = CopyDesignVerificationEvaluator.Evaluate(GoodFacts(refs, actual));
        Assert.False(result.Passed);
    }

    [Fact]
    public void CopyChildResolvingToItsExpectedNewCopyPasses()
    {
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-child", @"C:\dst\child-new.ipt", @"C:\src\child.ipt", null, true) };
        var actual = new[] { new CopyDesignActualComponentOccurrence(@"C:\dst\child-new.ipt") };
        Assert.True(CopyDesignVerificationEvaluator.Evaluate(GoodFacts(refs, actual)).Passed);
    }

    // 21. unexpected source reference fails unless REUSE
    [Fact]
    public void ReuseChildResolvingToAnUnexpectedDifferentPathFails()
    {
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-child", @"C:\src\std.ipt", @"C:\src\std.ipt", null, false) };
        var actual = new[] { new CopyDesignActualComponentOccurrence(@"C:\some\other\file.ipt") };
        var result = CopyDesignVerificationEvaluator.Evaluate(GoodFacts(refs, actual));
        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, r => r.Contains("source-project reference", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReuseChildResolvingToItsOriginalUnchangedPathPasses()
    {
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-child", @"C:\src\std.ipt", @"C:\src\std.ipt", null, false) };
        var actual = new[] { new CopyDesignActualComponentOccurrence(@"C:\src\std.ipt") };
        Assert.True(CopyDesignVerificationEvaluator.Evaluate(GoodFacts(refs, actual)).Passed);
    }

    [Fact]
    public void EvaluateAllFailsIfAnySingleNodeFails()
    {
        var good = GoodFacts();
        var bad = GoodFacts() with { CadDocumentId = "cad-2", DestinationExists = false };

        var result = CopyDesignVerificationEvaluator.EvaluateAll(new[] { good, bad });

        Assert.False(result.Passed);
        Assert.Equal(2, result.NodeResults.Count);
    }

    // ==================================================================
    // CODEX FINAL AUDIT ROUND 1, HIGH 3: verification must evaluate the
    // COMPLETE actual occurrence set, not merely "does the expected target
    // exist somewhere" - see CopyDesignApplyDocumentNumberMappingTests-style
    // regression coverage below.
    // ==================================================================

    [Fact]
    public void ExpectedDestination_present_TOGETHER_with_a_stale_original_occurrence_FAILS()
    {
        // The classic "existential, not complete" bug: the expected
        // destination IS present, but a SECOND, stale occurrence still
        // points at the pre-copy source path too.
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-child", @"C:\dst\child-new.ipt", @"C:\src\child.ipt", null, true) };
        var actual = new[]
        {
            new CopyDesignActualComponentOccurrence(@"C:\dst\child-new.ipt"), // correct
            new CopyDesignActualComponentOccurrence(@"C:\src\child.ipt"),     // stale leftover
        };

        var result = CopyDesignVerificationEvaluator.Evaluate(GoodFacts(refs, actual));

        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, r => r.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unexpected_extra_resolved_reference_not_named_by_ANY_target_FAILS()
    {
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-child", @"C:\dst\child-new.ipt", @"C:\src\child.ipt", null, true) };
        var actual = new[]
        {
            new CopyDesignActualComponentOccurrence(@"C:\dst\child-new.ipt"),   // expected, fine
            new CopyDesignActualComponentOccurrence(@"C:\dst\unplanned.ipt"),   // never named by the plan at all
        };

        var result = CopyDesignVerificationEvaluator.Evaluate(GoodFacts(refs, actual));

        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, r => r.Contains("unexpected/unplanned", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unresolved_occurrence_among_otherwise_fine_occurrences_FAILS()
    {
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-child", @"C:\dst\child-new.ipt", @"C:\src\child.ipt", null, true) };
        var actual = new[]
        {
            new CopyDesignActualComponentOccurrence(@"C:\dst\child-new.ipt"),
            new CopyDesignActualComponentOccurrence(null), // unresolved
        };

        var result = CopyDesignVerificationEvaluator.Evaluate(GoodFacts(refs, actual));

        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, r => r.Contains("did not resolve", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Repeated_legitimate_occurrences_ALL_resolving_to_the_expected_target_PASS()
    {
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-child", @"C:\dst\child-new.ipt", @"C:\src\child.ipt", null, true) };
        var actual = new[]
        {
            new CopyDesignActualComponentOccurrence(@"C:\dst\child-new.ipt"),
            new CopyDesignActualComponentOccurrence(@"C:\dst\child-new.ipt"),
            new CopyDesignActualComponentOccurrence(@"C:\dst\child-new.ipt"),
        };

        Assert.True(CopyDesignVerificationEvaluator.Evaluate(GoodFacts(refs, actual)).Passed);
    }

    [Fact]
    public void An_expected_reference_with_ZERO_matching_actual_occurrences_FAILS_as_missing()
    {
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-child", @"C:\dst\child-new.ipt", @"C:\src\child.ipt", null, true) };
        var actual = Array.Empty<CopyDesignActualComponentOccurrence>();

        var result = CopyDesignVerificationEvaluator.Evaluate(GoodFacts(refs, actual));

        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, r => r.Contains("does not resolve to its copied destination", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Normal_COPY_with_exactly_the_expected_occurrence_and_nothing_else_PASSES()
    {
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-a", @"C:\dst\a-new.ipt", @"C:\src\a.ipt", null, true) };
        var actual = new[] { new CopyDesignActualComponentOccurrence(@"C:\dst\a-new.ipt") };

        Assert.True(CopyDesignVerificationEvaluator.Evaluate(GoodFacts(refs, actual)).Passed);
    }

    [Fact]
    public void Normal_REUSE_with_exactly_the_expected_unchanged_occurrence_PASSES()
    {
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-b", @"C:\src\std.ipt", @"C:\src\std.ipt", null, false) };
        var actual = new[] { new CopyDesignActualComponentOccurrence(@"C:\src\std.ipt") };

        Assert.True(CopyDesignVerificationEvaluator.Evaluate(GoodFacts(refs, actual)).Passed);
    }

    // ======================================================================
    // CODEX ROUND 3, HIGH: occurrence ENUMERATION success is its own
    // required fact - independent of expected/actual reference counts, so a
    // zero-expected-reference IAM can never pass on a silently-lost
    // enumeration failure.
    // ======================================================================

    // item 1: IAM zero expected refs + enumeration failure => fail
    [Fact]
    public void Zero_expected_references_plus_a_FAILED_occurrence_enumeration_FAILS_verification()
    {
        var facts = GoodFacts(
            refs: Array.Empty<CopyDesignReferenceResolutionFact>(),
            actual: Array.Empty<CopyDesignActualComponentOccurrence>(),
            occurrenceEnumerationSucceeded: false);

        var result = CopyDesignVerificationEvaluator.Evaluate(facts);

        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, r => r.Contains("occurrence enumeration", StringComparison.OrdinalIgnoreCase));
    }

    // item 2: IAM with expected refs (otherwise satisfied) + enumeration
    //         failure => still fails
    [Fact]
    public void Expected_references_that_would_otherwise_PASS_still_FAIL_when_enumeration_itself_failed()
    {
        var refs = new[] { new CopyDesignReferenceResolutionFact("cad-child", @"C:\dst\child-new.ipt", @"C:\src\child.ipt", null, true) };
        var actual = new[] { new CopyDesignActualComponentOccurrence(@"C:\dst\child-new.ipt") }; // would otherwise pass
        var facts = GoodFacts(refs, actual, occurrenceEnumerationSucceeded: false);

        var result = CopyDesignVerificationEvaluator.Evaluate(facts);

        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, r => r.Contains("occurrence enumeration", StringComparison.OrdinalIgnoreCase));
    }

    // item 3: enumeration success + zero refs => passes if otherwise valid
    [Fact]
    public void Zero_expected_references_plus_a_SUCCESSFUL_occurrence_enumeration_PASSES_if_otherwise_valid()
    {
        var facts = GoodFacts(
            refs: Array.Empty<CopyDesignReferenceResolutionFact>(),
            actual: Array.Empty<CopyDesignActualComponentOccurrence>(),
            occurrenceEnumerationSucceeded: true);

        var result = CopyDesignVerificationEvaluator.Evaluate(facts);

        Assert.True(result.Passed);
        Assert.Empty(result.Reasons);
    }

    // item 4 (IPT unaffected) is proven at the gatherer level (COM-only, no
    // unit-test harness - see the source-level guard in
    // CopyDesignPreviewZeroMutationSourceTests) since this evaluator has no
    // document-type concept at all: OccurrenceEnumerationSucceeded defaults
    // to true, which is exactly what an IPT's gatherer always reports.
}
