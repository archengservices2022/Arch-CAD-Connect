using System.Text;

namespace Arch.CadConnect.Core.References;

/// <summary>
/// Renders a <see cref="CadReferenceScan"/> as a deterministic, human-readable
/// text block for the P5A results dialog. Groups edges by parent (root first,
/// then other parents in discovery order) so recursive topology is visible.
/// Pure and stable: the same scan always renders identical text.
/// </summary>
public static class CadReferenceScanTextReport
{
    public static string Render(CadReferenceScan scan)
    {
        var sb = new StringBuilder();
        var root = scan.Root;

        sb.AppendLine("Scan Status: " + (scan.IsComplete ? "COMPLETE" : "PARTIAL"));
        if (!scan.IsComplete)
        {
            sb.AppendLine("  Some documents could not be inspected - their child references");
            sb.AppendLine("  are UNKNOWN. Do not treat them as leaf nodes. (See below.)");
        }
        sb.AppendLine();

        sb.AppendLine("Document: " + NameOf(root.AbsolutePath));
        sb.AppendLine("Path: " + root.AbsolutePath);
        sb.AppendLine("Type: " + TypeLabel(root.DocumentType));
        if (root.Identity is { } rid)
        {
            sb.AppendLine("Identity: " + IdentityLabel(rid.DocumentNumber, rid.CadDocumentId, rid.FileVersionId));
        }
        sb.AppendLine();

        if (scan.References.Count == 0)
        {
            sb.AppendLine(scan.IsComplete
                ? "References: none - Inventor inspected the document and reported zero direct references."
                : "References: none reported (the document could not be inspected - see below).");
        }
        else
        {
            sb.AppendLine("References (by parent):");

            foreach (var parentPath in ParentOrder(scan))
            {
                sb.AppendLine();
                sb.AppendLine(NameOf(parentPath));
                foreach (var reference in scan.References.Where(r =>
                             string.Equals(r.ParentAbsolutePath, parentPath, StringComparison.OrdinalIgnoreCase)))
                {
                    AppendReference(sb, reference, scan.TargetIsUnenumerated(reference));
                }
            }
        }

        AppendUnenumerated(sb, scan);

        sb.AppendLine();
        AppendSummary(sb, scan);
        return sb.ToString().TrimEnd();
    }

    private static void AppendReference(StringBuilder sb, CadReference reference, bool targetNotEnumerated)
    {
        sb.AppendLine($"  [{ScopeTag(reference)}] {NameOf(reference.InventorReportedName)}"
            + $"  ({reference.RelationshipKind}, {TypeLabel(reference.ReferenceType)})");

        if (reference.IsResolved)
        {
            sb.AppendLine("    resolved -> " + reference.ResolvedAbsolutePath);
            if (reference.ManifestIdentity is { } mid)
            {
                sb.AppendLine("    identity -> " + IdentityLabel(mid.DocumentNumber, mid.CadDocumentId, mid.FileVersionId)
                    + $"  (relativePath={mid.RelativePath})");
            }
            if (targetNotEnumerated)
            {
                sb.AppendLine("    [CHILD REFERENCES NOT ENUMERATED - this document was not inspected]");
            }
        }
        else
        {
            sb.AppendLine("    UNRESOLVED - Inventor reported: " + reference.InventorReportedName);
        }
    }

    private static void AppendUnenumerated(StringBuilder sb, CadReferenceScan scan)
    {
        var unenumerated = scan.UnenumeratedNodes;
        if (unenumerated.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("Documents NOT enumerated (their child references are unknown):");
        foreach (var node in unenumerated)
        {
            sb.AppendLine("  " + node.AbsolutePath);
        }
    }

    private static void AppendSummary(StringBuilder sb, CadReferenceScan scan)
    {
        var s = scan.Summary;
        sb.AppendLine("Summary:");
        sb.AppendLine($"  Direct references: {s.DirectReferences}");
        if (s.TotalReferences != s.DirectReferences)
        {
            sb.AppendLine($"  Total reference edges: {s.TotalReferences}");
        }
        sb.AppendLine($"  Resolved: {s.Resolved}");
        sb.AppendLine($"  Unresolved: {s.Unresolved}");
        sb.AppendLine($"  Inside workspace: {s.InsideWorkspace}");
        sb.AppendLine($"  Outside workspace: {s.OutsideWorkspace}");
        sb.AppendLine($"  With exact manifest identity: {s.WithManifestIdentity}");
        sb.AppendLine($"  Scan completeness: {(s.IsComplete ? "COMPLETE" : "PARTIAL")}");
        sb.AppendLine($"  Documents not enumerated: {s.DocumentsNotEnumerated}");
    }

    /// <summary>Parents in a stable order: the root first, then every other
    ///  parent in the order its first edge appears.</summary>
    private static IEnumerable<string> ParentOrder(CadReferenceScan scan)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scan.References.Any(r => r.IsDirectChildOf(scan.Root.AbsolutePath)))
        {
            seen.Add(scan.Root.AbsolutePath);
            yield return scan.Root.AbsolutePath;
        }
        foreach (var reference in scan.References)
        {
            if (seen.Add(reference.ParentAbsolutePath))
            {
                yield return reference.ParentAbsolutePath;
            }
        }
    }

    private static string ScopeTag(CadReference reference) => reference.Resolution == CadReferenceResolution.Unresolved
        ? "UNRESOLVED"
        : reference.Scope switch
        {
            ReferenceWorkspaceScope.InsideWorkspace => "INSIDE WORKSPACE",
            ReferenceWorkspaceScope.OutsideWorkspace => "OUTSIDE WORKSPACE",
            _ => "SCOPE UNKNOWN",
        };

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

    private static string IdentityLabel(string? documentNumber, string cadDocumentId, string? fileVersionId)
    {
        var label = string.IsNullOrWhiteSpace(documentNumber) ? cadDocumentId : documentNumber!;
        var parts = new List<string> { "cadDocumentId=" + cadDocumentId };
        if (!string.IsNullOrWhiteSpace(fileVersionId))
        {
            parts.Add("fileVersionId=" + fileVersionId);
        }
        return $"{label}  ({string.Join(", ", parts)})";
    }
}
