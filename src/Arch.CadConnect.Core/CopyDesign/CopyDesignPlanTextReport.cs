using System.Text;

namespace Arch.CadConnect.Core.CopyDesign;

/// <summary>
/// Renders a <see cref="CopyDesignPlan"/> as deterministic, human-readable
/// PREVIEW text - Source / Type / Action / Destination / Status per node,
/// plus plan-level warnings and the executable/not-executable verdict. Pure
/// text, no secrets, no absolute storage paths beyond what the plan itself
/// already carries (local file paths only).
/// </summary>
public static class CopyDesignPlanTextReport
{
    private const string Rule = "============================================================";

    public static string Render(CopyDesignPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder();
        sb.AppendLine(Rule);
        sb.AppendLine("COPY DESIGN - PREVIEW (read-only, no mutation)");
        sb.AppendLine(Rule);
        sb.AppendLine();
        sb.AppendLine("Root: " + plan.RootAbsolutePath);
        sb.AppendLine("Scan: " + (plan.ScanWasComplete ? "COMPLETE" : "PARTIAL"));
        sb.AppendLine("Drawing association data: " + (plan.DrawingAssociationAvailable ? "AVAILABLE" : "NOT AVAILABLE"));
        if (plan.ModelFilesOnlyAcknowledged)
        {
            // Round 8: always shown, at the top of the report, whenever the
            // engineer explicitly acknowledged this mode - never buried only
            // in the warnings list.
            sb.AppendLine("MODE: MODEL FILES ONLY");
            sb.AppendLine("DRAWINGS: NOT INCLUDED");
        }
        sb.AppendLine("Plan status: " + (plan.IsExecutable ? "READY (all nodes resolved safely)" : "NOT EXECUTABLE"));
        sb.AppendLine();

        sb.AppendLine($"{"Source",-32} {"Type",-6} {"Action",-14} {"Destination",-32} Status");
        sb.AppendLine(new string('-', 100));
        foreach (var node in plan.Nodes)
        {
            var source = node.SourceFileName;
            var type = node.DocumentType.ToString().ToUpperInvariant();
            var action = node.IsRoot ? $"{node.ProposedAction} (root)" : node.ProposedAction.ToString();
            var destination = node.ProposedDestinationFileName ?? (node.ProposedAction == CopyDesignAction.Reuse ? "(same identity)" : "-");
            var status = node.Reasons.Count > 0 ? node.Reasons[0] : "";
            sb.AppendLine($"{Truncate(source, 32),-32} {Truncate(type, 6),-6} {Truncate(action, 14),-14} {Truncate(destination, 32),-32} {status}");
        }

        if (plan.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            foreach (var warning in plan.Warnings)
            {
                foreach (var line in Wrap(warning))
                {
                    sb.AppendLine("  - " + line);
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine(Rule);
        sb.AppendLine(plan.IsExecutable
            ? "This is a PREVIEW only. No file has been copied, renamed, saved, or replaced. Execution is a "
              + "LATER phase - P6A never performs it."
            : "This plan is NOT EXECUTABLE - resolve the warning(s) above before any later phase may act on it. "
              + "No file has been copied, renamed, saved, or replaced.");
        sb.AppendLine(Rule);

        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private static IEnumerable<string> Wrap(string text, int width = 92)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
