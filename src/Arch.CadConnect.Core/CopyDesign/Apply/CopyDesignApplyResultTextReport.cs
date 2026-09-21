using System.Text;

namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// Renders a <see cref="CopyDesignApplyOperationResult"/> as deterministic,
/// human-readable text for the Ribbon result dialog. Pure text, no secrets.
/// </summary>
public static class CopyDesignApplyResultTextReport
{
    private const string Rule = "============================================================";

    public static string Render(CopyDesignApplyOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.AppendLine(Rule);
        sb.AppendLine("COPY DESIGN - APPLY RESULT");
        sb.AppendLine(Rule);
        sb.AppendLine();
        sb.AppendLine("Outcome: " + result.Outcome);
        if (result.CopyDesignOperationId is not null)
        {
            sb.AppendLine("CopyDesignOperationId: " + result.CopyDesignOperationId);
        }
        if (result.FailureReason is not null)
        {
            sb.AppendLine("Reason: " + result.FailureReason);
        }
        sb.AppendLine();

        if (result.Entries.Count > 0)
        {
            sb.AppendLine($"{"CadDocumentId",-24} {"Action",-8} {"Created",-8} {"Verified",-9} Materialization");
            sb.AppendLine(new string('-', 100));
            foreach (var entry in result.Entries)
            {
                var materialization = entry.Materialization is null
                    ? "-"
                    : $"{entry.Materialization.Outcome} - {entry.Materialization.Detail}";
                sb.AppendLine(
                    $"{Truncate(entry.CadDocumentId, 24),-24} {entry.Action,-8} {(entry.PhysicallyCreated ? "yes" : "-"),-8} " +
                    $"{(entry.VerificationPassed is true ? "yes" : entry.VerificationPassed is false ? "NO" : "-"),-9} {materialization}");
            }
            sb.AppendLine();
        }

        if (result.Cleanup is not null)
        {
            sb.AppendLine("Cleanup of destination artifacts created by this attempt:");
            foreach (var outcome in result.Cleanup.Outcomes)
            {
                sb.AppendLine($"  {(outcome.Deleted ? "deleted" : "FAILED - MANUAL CLEANUP REQUIRED")}: {outcome.AbsolutePath}"
                    + (outcome.FailureReason is null ? "" : $" ({outcome.FailureReason})"));
            }
            sb.AppendLine();
        }

        if (result.ReservedButUnmaterialized)
        {
            sb.AppendLine(Rule);
            sb.AppendLine("RESERVED BUT UNMATERIALIZED");
            sb.AppendLine("The P6B server reservation above is durable and has NOT been rolled back.");
            sb.AppendLine("No FileVersion was fabricated for any incomplete entry.");
            sb.AppendLine(Rule);
        }
        else if (result.Outcome == CopyDesignApplyOutcome.Succeeded)
        {
            sb.AppendLine(Rule);
            sb.AppendLine("READY FOR MANUAL INVENTOR TEST");
            sb.AppendLine(Rule);
        }

        return sb.ToString();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
