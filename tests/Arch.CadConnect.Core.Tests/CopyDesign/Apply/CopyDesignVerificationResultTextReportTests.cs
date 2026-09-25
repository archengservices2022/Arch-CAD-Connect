using Arch.CadConnect.Core.CopyDesign.Apply;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

/// <summary>
/// P6E-D: <see cref="CopyDesignVerificationResultTextReport"/> - the
/// word-wrap-friendly renderer for the Verify Copy Design result dialog.
/// Deliberately checks it never truncates a reason (the "P6D horizontal-
/// truncation problem" this renderer exists to avoid) and never collapses
/// multiple failures onto one line.
/// </summary>
public class CopyDesignVerificationResultTextReportTests
{
    private static CopyDesignVerificationEntryResult CopyEntry(
        int ordinal, string outcomeName, IReadOnlyList<CopyDesignVerificationCheck> checks) =>
        new(ordinal, $"cad-{ordinal}", "COPY", $"part{ordinal}.ipt", "IPT",
            outcomeName switch
            {
                "Verified" => CopyDesignVerificationOutcome.Verified,
                "Failed" => CopyDesignVerificationOutcome.Failed,
                _ => CopyDesignVerificationOutcome.Incomplete,
            },
            checks);

    [Fact]
    public void VERIFIED_result_states_the_exact_required_sentence()
    {
        var result = new CopyDesignOperationVerificationResult(
            CopyDesignVerificationOutcome.Verified, "op-1",
            new[]
            {
                CopyEntry(0, "Verified", new[]
                {
                    CopyDesignVerificationCheck.Pass("MaterializationState"),
                    CopyDesignVerificationCheck.Pass("SourceIntegrity"),
                }),
            });

        var text = CopyDesignVerificationResultTextReport.Render(result);

        Assert.Contains("COPY DESIGN VERIFICATION", text);
        Assert.Contains("Operation: op-1", text);
        Assert.Contains("Outcome: VERIFIED", text);
        Assert.Contains("The Copy Design package passed all required verifiable integrity checks.", text);
        Assert.Contains("Entries verified: 1", text);
        Assert.Contains("Entries failed: 0", text);
        Assert.Contains("Entries incomplete: 0", text);
    }

    [Fact]
    public void VERIFIED_result_with_a_REUSE_entry_adds_the_informational_disclaimer_without_implying_it_was_verified()
    {
        var result = new CopyDesignOperationVerificationResult(
            CopyDesignVerificationOutcome.Verified, "op-1",
            new[]
            {
                new CopyDesignVerificationEntryResult(0, "cad-reused", "REUSE", "bolt.ipt", "IPT",
                    CopyDesignVerificationOutcome.Verified,
                    new[]
                    {
                        CopyDesignVerificationCheck.Pass("ReferenceTopology"),
                        CopyDesignVerificationCheck.NotProvable(
                            "SourceByteImmutability",
                            "P6D never freezes a source FileVersion for REUSE.",
                            CopyDesignVerificationCheckRequirement.Informational),
                    }),
            });

        var text = CopyDesignVerificationResultTextReport.Render(result);

        Assert.Contains(
            "Historical byte immutability for REUSE entries was not recorded by P6D and is",
            text);
        Assert.Contains("reported as informational NOT PROVABLE", text);
        Assert.Contains("never itself reported as verified", text);
        Assert.Contains("NOT PROVABLE [Informational]", text);
    }

    [Fact]
    public void FAILED_result_shows_every_reason_never_collapsed_onto_one_line()
    {
        var result = new CopyDesignOperationVerificationResult(
            CopyDesignVerificationOutcome.Failed, "op-1",
            new[]
            {
                CopyEntry(0, "Failed", new[]
                {
                    CopyDesignVerificationCheck.Fail("SourceIntegrity", "The source SHA-256 no longer matches the recorded canonical hash."),
                    CopyDesignVerificationCheck.Fail("DestinationIntegrity", "The destination file's SHA-256 does not match the materialized FileVersion."),
                }),
            });

        var text = CopyDesignVerificationResultTextReport.Render(result);
        var lines = text.Split(Environment.NewLine);

        Assert.Contains("Outcome: FAILED", text);
        Assert.Contains("Entries failed: 1", text);
        Assert.Contains(lines, l => l.Trim() == "The source SHA-256 no longer matches the recorded canonical hash.");
        Assert.Contains(lines, l => l.Trim() == "The destination file's SHA-256 does not match the materialized FileVersion.");
        // Never both reasons on the same line.
        Assert.DoesNotContain(lines, l => l.Contains("SHA-256 no longer matches") && l.Contains("does not match the materialized"));
    }

    [Fact]
    public void INCOMPLETE_whole_operation_result_identifies_missing_evidence()
    {
        var result = new CopyDesignOperationVerificationResult(
            CopyDesignVerificationOutcome.Incomplete, "op-1",
            new[]
            {
                CopyEntry(0, "Incomplete", new[]
                {
                    CopyDesignVerificationCheck.NotProvable("SourceIntegrity", "No source-integrity evidence was available for this entry."),
                }),
            });

        var text = CopyDesignVerificationResultTextReport.Render(result);

        Assert.Contains("Outcome: INCOMPLETE", text);
        Assert.Contains("missing/unavailable authoritative evidence", text);
        Assert.Contains("No source-integrity evidence was available for this entry.", text);
        Assert.Contains("Entries incomplete: 1", text);
    }

    [Fact]
    public void A_shared_StructuralFailure_result_renders_as_INCOMPLETE_with_the_reason_and_zero_entries()
    {
        // P6E-B/D FINAL SEMANTIC ALIGNMENT: there is no separate UI-only
        // "topology incomplete" renderer any more - both the orchestrator's
        // own internal fallback AND P6E-D's controller pre-check construct
        // their result through the SAME CopyDesignOperationVerificationResult.StructuralFailure
        // factory, and Render() alone decides how it is displayed.
        var result = CopyDesignOperationVerificationResult.StructuralFailure(
            "The source workspace does not have a bound entry for \"1001.ipt\".", "op-1");

        var text = CopyDesignVerificationResultTextReport.Render(result);

        Assert.Contains("Outcome: INCOMPLETE", text);
        Assert.Contains("Operation: op-1", text);
        Assert.Contains("NO per-entry check ran", text);
        Assert.Contains("The source workspace does not have a bound entry for \"1001.ipt\".", text);
        Assert.Contains("Entries verified: 0", text);
        Assert.Contains("Entries failed: 0", text);
        Assert.Contains("Entries incomplete: 0", text);
    }

    [Fact]
    public void A_shared_StructuralFailure_result_with_a_null_operationId_renders_unknown()
    {
        var result = CopyDesignOperationVerificationResult.StructuralFailure("some reason");
        var text = CopyDesignVerificationResultTextReport.Render(result);
        Assert.Contains("Operation: (unknown)", text);
    }

    [Fact]
    public void Entries_and_checks_render_in_deterministic_ordinal_and_list_order_regardless_of_input_order()
    {
        var entryB = CopyEntry(1, "Verified", new[] { CopyDesignVerificationCheck.Pass("First"), CopyDesignVerificationCheck.Pass("Second") });
        var entryA = CopyEntry(0, "Verified", new[] { CopyDesignVerificationCheck.Pass("Alpha") });

        // Deliberately supplied OUT of ordinal order.
        var result = new CopyDesignOperationVerificationResult(
            CopyDesignVerificationOutcome.Verified, "op-1", new[] { entryB, entryA });

        var text = CopyDesignVerificationResultTextReport.Render(result);

        var indexEntry0 = text.IndexOf("Entry 0", StringComparison.Ordinal);
        var indexEntry1 = text.IndexOf("Entry 1", StringComparison.Ordinal);
        Assert.True(indexEntry0 >= 0 && indexEntry1 >= 0 && indexEntry0 < indexEntry1);

        var indexFirst = text.IndexOf("First:", StringComparison.Ordinal);
        var indexSecond = text.IndexOf("Second:", StringComparison.Ordinal);
        Assert.True(indexFirst >= 0 && indexSecond >= 0 && indexFirst < indexSecond);
    }

    [Fact]
    public void Never_truncates_a_long_check_detail_no_matter_how_long()
    {
        var longDetail = "The reference topology mismatch involves " + new string('x', 500) + " end-of-detail-marker";
        var result = new CopyDesignOperationVerificationResult(
            CopyDesignVerificationOutcome.Failed, "op-1",
            new[]
            {
                CopyEntry(0, "Failed", new[] { CopyDesignVerificationCheck.Fail("ReferenceTopology", longDetail) }),
            });

        var text = CopyDesignVerificationResultTextReport.Render(result);

        Assert.Contains(longDetail, text);
        Assert.DoesNotContain("…", text); // no ellipsis truncation marker anywhere
    }

    [Fact]
    public void StructuralFailure_style_result_with_zero_entries_still_renders_the_reason_as_INCOMPLETE()
    {
        var result = CopyDesignOperationVerificationResult.StructuralFailure("More than one entry reports ordinal 0.");

        var text = CopyDesignVerificationResultTextReport.Render(result);

        Assert.Contains("Outcome: INCOMPLETE", text);
        Assert.Contains("Reason: More than one entry reports ordinal 0.", text);
        Assert.Contains("Entries verified: 0", text);
    }
}
