using System.Text;

namespace Arch.CadConnect.Core.References;

/// <summary>
/// Renders a <see cref="ReferenceRepairPlan"/> as deterministic, human-readable
/// preview text for the "Repair Reference" dialog. It shows every fact the
/// engineer needs to understand the operation and the exact eligibility /
/// blocking reason. It never prints a secret or a bearer token (the plan
/// carries none).
/// </summary>
public static class ReferenceRepairTextReport
{
    private const string Rule = "============================================================";

    public static string Render(ReferenceRepairPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder();
        sb.AppendLine(Rule);
        sb.AppendLine("REPAIR REFERENCE - PREVIEW");
        sb.AppendLine(Rule);
        sb.AppendLine();
        sb.AppendLine("Eligibility: " + plan.EligibilityLabel);
        foreach (var line in Wrap(plan.EligibilityDetail))
        {
            sb.AppendLine("  " + line);
        }
        sb.AppendLine();

        sb.AppendLine("Referencing document : " + Show(plan.ReferencingDocumentPath));
        sb.AppendLine("Observed reference    : " + Show(plan.ObservedReferenceName));
        sb.AppendLine("cadDocumentId         : " + Show(plan.CadDocumentId));
        sb.AppendLine("Current local version : " + Show(plan.CurrentPinnedFileVersionId));
        sb.AppendLine("Authoritative target  : " + Show(plan.AuthoritativeTargetFileVersionId));
        sb.AppendLine("Current path          : " + Show(plan.CurrentReferencePath));
        sb.AppendLine("Proposed target path  : " + Show(plan.ProposedTargetPath));
        sb.AppendLine();

        sb.AppendLine("Reason for repair:");
        foreach (var line in Wrap(plan.RepairReason))
        {
            sb.AppendLine("  " + line);
        }

        if (plan.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Notes:");
            foreach (var note in plan.Notes)
            {
                foreach (var line in Wrap(note))
                {
                    sb.AppendLine("  - " + line);
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine(Rule);
        if (plan.CanProceed)
        {
            sb.AppendLine("Confirming will replace ONLY this managed file reference through the Inventor API.");
            sb.AppendLine("If this file is used by more than one occurrence in an assembly, ALL occurrences");
            sb.AppendLine("using it will be replaced together - no OTHER file reference is touched. The");
            sb.AppendLine("document becomes MODIFIED in memory - P5C never saves, checks in, checks out, or");
            sb.AppendLine("runs Get Latest for you.");
        }
        else
        {
            sb.AppendLine("This reference cannot be repaired by P5C right now. No change will be made.");
        }
        sb.AppendLine(Rule);

        return sb.ToString().TrimEnd();
    }

    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "(not available)" : value.Trim();

    private static IEnumerable<string> Wrap(string text, int width = 72)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield return "(none)";
            yield break;
        }

        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0)
            {
                line.Append(' ');
            }
            line.Append(word);
        }
        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }
}
