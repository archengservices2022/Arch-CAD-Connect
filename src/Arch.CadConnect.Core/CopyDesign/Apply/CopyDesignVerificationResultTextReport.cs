using System.Text;

namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6E-D: renders a <see cref="CopyDesignOperationVerificationResult"/> as
/// deterministic, human-readable, WORD-WRAP-FRIENDLY text for the Ribbon
/// result dialog. Pure text, no secrets, no COM.
///
/// Deliberately a SEPARATE renderer from <see cref="CopyDesignApplyResultTextReport"/>:
/// that one is read in a fixed-width, no-wrap, horizontally-scrolling text
/// box (<c>ScanResultDialog</c>'s <c>WordWrap = false</c>) - fine for its
/// compact one-line-per-entry table, but exactly the "P6D horizontal-
/// truncation problem" a verification report (many long per-check reasons)
/// must avoid. This renderer never truncates a reason and never packs more
/// than one fact onto a line, so it stays fully readable in a WORD-WRAPPING
/// text box (<c>CopyDesignVerificationResultDialog</c>).
///
/// Every reason/detail is shown, never collapsed - see <see cref="Render"/>'s
/// per-check loop.
/// </summary>
public static class CopyDesignVerificationResultTextReport
{
    private const string Rule = "============================================================";
    private const string Divider = "------------------------------------------------------------";

    public static string Render(CopyDesignOperationVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        AppendHeader(sb, result.CopyDesignOperationId);

        sb.AppendLine("Outcome: " + OutcomeLabel(result.Outcome));
        sb.AppendLine();

        switch (result.Outcome)
        {
            case CopyDesignVerificationOutcome.Verified:
                sb.AppendLine("The Copy Design package passed all required verifiable integrity checks.");
                break;
            case CopyDesignVerificationOutcome.Failed:
                sb.AppendLine("One or more integrity checks found POSITIVE evidence of a problem. See the");
                sb.AppendLine("FAILED checks below for the exact defect(s) observed.");
                break;
            case CopyDesignVerificationOutcome.Incomplete when result.Entries.Count == 0:
                // P6E-B/D FINAL SEMANTIC ALIGNMENT: a whole-operation
                // STRUCTURAL failure (CopyDesignOperationVerificationResult.StructuralFailure)
                // - the expected topology itself could not be safely built,
                // so there is nothing to attribute to a specific entry.
                // Never "the copied package was proven wrong".
                sb.AppendLine("A legitimate pass/fail conclusion could not be reached: the expected");
                sb.AppendLine("verification topology could not be safely built from the server's durable");
                sb.AppendLine("data and the local source workspace, so NO Inventor document was opened and");
                sb.AppendLine("NO per-entry check ran. See the reason below.");
                break;
            case CopyDesignVerificationOutcome.Incomplete:
                sb.AppendLine("Nothing was proven wrong, but at least one required check could not reach a");
                sb.AppendLine("verdict - missing/unavailable authoritative evidence, or unfinished operation");
                sb.AppendLine("state. See the NOT PROVABLE / unfinished checks below.");
                break;
        }
        sb.AppendLine();

        if (result.FailureReason is not null)
        {
            sb.AppendLine("Reason: " + result.FailureReason);
            sb.AppendLine();
        }

        if (result.Entries.Count > 0 && result.Entries.Any(e => e.Action == "REUSE"))
        {
            sb.AppendLine("Historical byte immutability for REUSE entries was not recorded by P6D and is");
            sb.AppendLine("reported as informational NOT PROVABLE. This limitation is never counted");
            sb.AppendLine("against VERIFIED, and it is never itself reported as verified.");
            sb.AppendLine();
        }

        var verified = result.Entries.Count(e => e.Outcome == CopyDesignVerificationOutcome.Verified);
        var failed = result.Entries.Count(e => e.Outcome == CopyDesignVerificationOutcome.Failed);
        var incomplete = result.Entries.Count(e => e.Outcome == CopyDesignVerificationOutcome.Incomplete);
        sb.AppendLine("Entries verified: " + verified);
        sb.AppendLine("Entries failed: " + failed);
        sb.AppendLine("Entries incomplete: " + incomplete);
        sb.AppendLine();

        foreach (var entry in result.Entries.OrderBy(e => e.Ordinal))
        {
            sb.AppendLine(Divider);
            sb.AppendLine($"Entry {entry.Ordinal} - {entry.OriginalFileName} ({entry.Action}) - {OutcomeLabel(entry.Outcome)}");
            sb.AppendLine(Divider);
            sb.AppendLine("  Document type: " + entry.OriginalDocumentType);
            sb.AppendLine("  CadDocumentId: " + entry.ResultingCadDocumentId);
            sb.AppendLine();

            foreach (var check in entry.Checks)
            {
                sb.AppendLine($"  {check.Name}: {CheckStateLabel(check.State)} [{check.Requirement}]");
                if (check.Detail is not null)
                {
                    sb.AppendLine("    " + check.Detail);
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, string? operationId)
    {
        sb.AppendLine(Rule);
        sb.AppendLine("COPY DESIGN VERIFICATION");
        sb.AppendLine(Rule);
        sb.AppendLine();
        sb.AppendLine("Operation: " + (operationId ?? "(unknown)"));
        sb.AppendLine();
    }

    private static string OutcomeLabel(CopyDesignVerificationOutcome outcome) => outcome switch
    {
        CopyDesignVerificationOutcome.Verified => "VERIFIED",
        CopyDesignVerificationOutcome.Failed => "FAILED",
        CopyDesignVerificationOutcome.Incomplete => "INCOMPLETE",
        _ => outcome.ToString(),
    };

    private static string CheckStateLabel(CopyDesignVerificationCheckState state) => state switch
    {
        CopyDesignVerificationCheckState.Passed => "PASSED",
        CopyDesignVerificationCheckState.Failed => "FAILED",
        CopyDesignVerificationCheckState.NotProvable => "NOT PROVABLE",
        _ => state.ToString(),
    };
}
