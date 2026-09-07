using System.Text;

namespace Arch.CadConnect.Core.References;

/// <summary>
/// Renders a <see cref="ReferenceHealthReport"/> as a deterministic,
/// human-readable text block for the P5B-A "Reference Health" dialog. Groups
/// edges by parent (root first, then other parents in discovery order) so
/// recursive topology stays visible. Pure and stable - the same report always
/// renders identical text. The underlying facts (paths, scope, identity) are
/// always shown; the severity never hides them.
/// </summary>
public static class ReferenceHealthTextReport
{
    public static string Render(ReferenceHealthReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine("REFERENCE HEALTH - " + NameOf(report.Root.AbsolutePath));
        sb.AppendLine();
        sb.AppendLine("Overall Status: " + report.OverallStatusLabel);
        sb.AppendLine("Scan Status: " + (report.ScanComplete ? "COMPLETE" : "PARTIAL"));
        if (!report.ScanComplete)
        {
            sb.AppendLine("  Some documents could not be inspected - their child references are");
            sb.AppendLine("  UNKNOWN. This report cannot be considered complete. (See below.)");
        }
        sb.AppendLine();

        sb.AppendLine("Document: " + NameOf(report.Root.AbsolutePath));
        sb.AppendLine("Path: " + report.Root.AbsolutePath);
        sb.AppendLine("Type: " + TypeLabel(report.Root.DocumentType));
        if (report.Root.Identity is { } rid)
        {
            sb.AppendLine("Managed root identity: cadDocumentId=" + rid.CadDocumentId
                + (string.IsNullOrEmpty(rid.FileVersionId) ? "" : ", fileVersionId=" + rid.FileVersionId));
        }
        sb.AppendLine();

        if (report.Entries.Count == 0)
        {
            sb.AppendLine(report.ScanComplete
                ? "References: none - Inventor inspected the document and reported zero direct references."
                : "References: none reported (the document could not be inspected - see below).");
        }
        else
        {
            sb.AppendLine("References (by parent):");
            foreach (var parentPath in ParentOrder(report))
            {
                sb.AppendLine();
                sb.AppendLine(NameOf(parentPath));
                foreach (var entry in report.Entries.Where(x =>
                             string.Equals(x.Reference.ParentAbsolutePath, parentPath, StringComparison.OrdinalIgnoreCase)))
                {
                    AppendEntry(sb, entry);
                }
            }
        }

        AppendUnenumerated(sb, report);

        sb.AppendLine();
        AppendSummary(sb, report);
        return sb.ToString().TrimEnd();
    }

    private static void AppendEntry(StringBuilder sb, ReferenceHealthEntry entry)
    {
        sb.AppendLine("  -> " + NameOf(entry.Reference.InventorReportedName)
            + $"  ({entry.Reference.RelationshipKind})");
        sb.AppendLine("     " + entry.Health.ToString().ToUpperInvariant());
        sb.AppendLine("     " + Classification(entry));
        foreach (var reason in entry.Reasons)
        {
            sb.AppendLine("       - " + reason);
        }
    }

    private static string Classification(ReferenceHealthEntry entry)
    {
        var parts = new List<string>
        {
            entry.IsMissing ? "Missing" : "Resolved",
        };

        if (entry.IsResolved)
        {
            parts.Add(entry.Scope switch
            {
                ReferenceWorkspaceScope.InsideWorkspace => "Inside Workspace",
                ReferenceWorkspaceScope.OutsideWorkspace => "Outside Workspace",
                _ => "Workspace Unknown",
            });
            parts.Add(entry.Management switch
            {
                ReferenceManagement.Managed => "Managed",
                ReferenceManagement.Unmanaged => "Unmanaged",
                _ => "Management Unknown",
            });
        }

        return string.Join(" | ", parts);
    }

    private static void AppendUnenumerated(StringBuilder sb, ReferenceHealthReport report)
    {
        if (report.UnenumeratedDocuments.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("Documents NOT enumerated (their child references are unknown):");
        foreach (var node in report.UnenumeratedDocuments)
        {
            sb.AppendLine("  " + node.AbsolutePath);
        }
    }

    private static void AppendSummary(StringBuilder sb, ReferenceHealthReport report)
    {
        var s = report.Summary;
        sb.AppendLine("Summary:");
        sb.AppendLine($"  References: {s.Total}");
        sb.AppendLine($"  Healthy: {s.Healthy}");
        sb.AppendLine($"  Warnings: {s.Warnings}");
        sb.AppendLine($"  Errors: {s.Errors}");
        sb.AppendLine($"  Unknown: {s.Unknown}");
        sb.AppendLine($"  Managed: {s.Managed}");
        sb.AppendLine($"  Unmanaged: {s.Unmanaged}");
        if (s.ManagementUnknown > 0)
        {
            sb.AppendLine($"  Management unknown: {s.ManagementUnknown}");
        }
        sb.AppendLine($"  Inside workspace: {s.InsideWorkspace}");
        sb.AppendLine($"  Outside workspace: {s.OutsideWorkspace}");
        if (s.ScopeUnknown > 0)
        {
            sb.AppendLine($"  Workspace unknown: {s.ScopeUnknown}");
        }
        sb.AppendLine($"  Missing: {s.Missing}");
        sb.AppendLine($"  Scan completeness: {(s.ScanComplete ? "COMPLETE" : "PARTIAL")}");
        sb.AppendLine($"  Documents not enumerated: {s.DocumentsNotEnumerated}");
    }

    private static IEnumerable<string> ParentOrder(ReferenceHealthReport report)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootPath = report.Root.AbsolutePath;
        if (report.Entries.Any(x => x.Reference.IsDirectChildOf(rootPath)))
        {
            seen.Add(rootPath);
            yield return rootPath;
        }
        foreach (var entry in report.Entries)
        {
            if (seen.Add(entry.Reference.ParentAbsolutePath))
            {
                yield return entry.Reference.ParentAbsolutePath;
            }
        }
    }

    private static string NameOf(string path)
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

    private static string TypeLabel(CadDocumentType type) => type switch
    {
        CadDocumentType.Ipt => "part (.ipt)",
        CadDocumentType.Iam => "assembly (.iam)",
        CadDocumentType.Idw => "drawing (.idw)",
        CadDocumentType.Dwg => "drawing (.dwg)",
        _ => "unknown",
    };
}
