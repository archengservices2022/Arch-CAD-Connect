using System.Text;

namespace Arch.CadConnect.Core.References;

/// <summary>
/// Renders a <see cref="ReferenceVersionReport"/> as deterministic,
/// human-readable text for the "Reference Health" dialog. It emits the full
/// P5B-A local report verbatim (via <see cref="ReferenceHealthTextReport"/>)
/// and then appends a P5B-B authoritative-version section, so no P5A / P5B-A
/// fact is lost and the version dimension is clearly additive.
/// </summary>
public static class ReferenceVersionTextReport
{
    private const string Rule = "============================================================";

    public static string Render(ReferenceVersionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine(ReferenceHealthTextReport.Render(report.Local));
        sb.AppendLine();
        sb.AppendLine(Rule);
        sb.AppendLine("VERSION INTELLIGENCE - authoritative Arch PLM");
        sb.AppendLine(Rule);
        sb.AppendLine("Version Status: " + report.VersionStatusLabel);
        sb.AppendLine("Combined Health (local + version): " + report.OverallStatusLabel);
        sb.AppendLine();
        sb.AppendLine("CURRENT / STALE are asserted ONLY from an authenticated authoritative");
        sb.AppendLine("Arch PLM comparison of the exact cadDocumentId + the pinned local");
        sb.AppendLine("fileVersionId. Anything unsafe or unavailable is reported as UNKNOWN");
        sb.AppendLine("VERSION - it never improves health and CURRENT is never guessed.");
        sb.AppendLine();

        var applicable = report.Assessments.Where(a => a.Applicable).ToArray();
        if (applicable.Length == 0)
        {
            sb.AppendLine("No managed references with a pinned local identity were found to");
            sb.AppendLine("version-check.");
        }
        else
        {
            sb.AppendLine("Per managed reference:");
            foreach (var a in applicable)
            {
                sb.AppendLine();
                sb.AppendLine("  -> " + NameOf(a.Entry.Reference.ResolvedAbsolutePath
                    ?? a.Entry.Reference.InventorReportedName));
                sb.AppendLine("     " + a.StatusLabel);
                foreach (var reason in a.Reasons)
                {
                    sb.AppendLine("       - " + reason);
                }
            }
        }

        sb.AppendLine();
        AppendSummary(sb, report.VersionSummary);
        return sb.ToString().TrimEnd();
    }

    private static void AppendSummary(StringBuilder sb, ReferenceVersionSummary s)
    {
        sb.AppendLine("Version Summary:");
        sb.AppendLine($"  Managed references checked: {s.ApplicableReferences}");
        sb.AppendLine($"  Current: {s.Current}");
        sb.AppendLine($"  Stale: {s.Stale}");
        sb.AppendLine($"  Unknown version: {s.UnknownVersion}");
        if (s.NotApplicable > 0)
        {
            sb.AppendLine($"  Not applicable (unresolved / unmanaged): {s.NotApplicable}");
        }
    }

    private static string NameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "(unknown)";
        }
        try
        {
            var name = Path.GetFileName(path.Trim());
            return string.IsNullOrEmpty(name) ? path.Trim() : name;
        }
        catch (ArgumentException)
        {
            return path.Trim();
        }
    }
}
