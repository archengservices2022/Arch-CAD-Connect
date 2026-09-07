using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References;

public class CadReferenceScanTextReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly string Base = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string P(params string[] segments) => Path.GetFullPath(Path.Combine(Base, Path.Combine(segments)));

    private static CadReferenceNode Node(string path, CadDocumentType type, bool enumerated) =>
        new(path, type, enumerated);

    private static CadReference Edge(
        string parent,
        string name,
        string? resolved,
        CadRelationshipKind kind,
        ReferenceWorkspaceScope scope,
        CadManifestIdentity? identity = null) => new()
    {
        ParentAbsolutePath = parent,
        InventorReportedName = name,
        ResolvedAbsolutePath = resolved,
        ReferenceType = CadDocumentType.Ipt,
        RelationshipKind = kind,
        Resolution = resolved is null ? CadReferenceResolution.Unresolved : CadReferenceResolution.Resolved,
        Scope = scope,
        ManifestIdentity = identity,
    };

    [Fact]
    public void The_report_shows_parent_child_structure_scopes_and_a_summary()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var sub = P("Arch", "Job1", "SUB.iam");
        var partB = P("Vendor", "src", "PART-B.ipt");
        var scan = new CadReferenceScan(
            new CadReferenceRoot(root, CadDocumentType.Iam, new PlmIdentity("cad_root", "ASM-1")),
            new[]
            {
                Edge(root, "SUB.iam", sub, CadRelationshipKind.Component, ReferenceWorkspaceScope.InsideWorkspace,
                    new CadManifestIdentity("cad_sub", "fv_sub", "ASM-SUB", "SUB.iam")),
                Edge(root, partB, partB, CadRelationshipKind.Component, ReferenceWorkspaceScope.OutsideWorkspace),
                Edge(sub, "LEGACY.ipt", null, CadRelationshipKind.Other, ReferenceWorkspaceScope.Unknown),
            },
            new[]
            {
                Node(root, CadDocumentType.Iam, true),
                Node(sub, CadDocumentType.Iam, true),
            },
            Now);

        var text = CadReferenceScanTextReport.Render(scan);

        Assert.StartsWith("Scan Status: COMPLETE", text);
        Assert.Contains("Document: ROOT.iam", text);
        Assert.Contains("Identity: ASM-1", text);
        Assert.Contains("[INSIDE WORKSPACE] SUB.iam", text);
        Assert.Contains("identity -> ASM-SUB", text);
        Assert.Contains("[OUTSIDE WORKSPACE] PART-B.ipt", text);
        Assert.Contains("[UNRESOLVED] LEGACY.ipt", text);

        Assert.Contains("Direct references: 2", text);
        Assert.Contains("Total reference edges: 3", text);
        Assert.Contains("Resolved: 2", text);
        Assert.Contains("Unresolved: 1", text);
        Assert.Contains("Inside workspace: 1", text);
        Assert.Contains("Outside workspace: 1", text);
        Assert.Contains("With exact manifest identity: 1", text);
        Assert.Contains("Scan completeness: COMPLETE", text);
        Assert.Contains("Documents not enumerated: 0", text);
        Assert.DoesNotContain("[CHILD REFERENCES NOT ENUMERATED", text);
        Assert.DoesNotContain("Documents NOT enumerated", text);
    }

    [Fact]
    public void A_partial_scan_is_called_out_prominently_and_names_the_unenumerated_documents()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var sub = P("Arch", "Job1", "SUB.iam");
        var partB = P("Arch", "Job1", "PART-B.ipt");
        var scan = new CadReferenceScan(
            new CadReferenceRoot(root, CadDocumentType.Iam, null),
            new[]
            {
                Edge(root, "SUB.iam", sub, CadRelationshipKind.Component, ReferenceWorkspaceScope.InsideWorkspace),
                Edge(root, "PART-B.ipt", partB, CadRelationshipKind.Component, ReferenceWorkspaceScope.InsideWorkspace),
            },
            new[]
            {
                Node(root, CadDocumentType.Iam, true),
                Node(sub, CadDocumentType.Iam, false),   // SUB could not be inspected
                Node(partB, CadDocumentType.Ipt, true),
            },
            Now);

        var text = CadReferenceScanTextReport.Render(scan);

        Assert.StartsWith("Scan Status: PARTIAL", text);
        Assert.Contains("child references", text); // the explanatory banner
        Assert.Contains("[CHILD REFERENCES NOT ENUMERATED", text);
        Assert.Contains("Documents NOT enumerated", text);
        Assert.Contains(sub, text);
        Assert.Contains("Scan completeness: PARTIAL", text);
        Assert.Contains("Documents not enumerated: 1", text);

        // exactly one edge carries the annotation (the SUB edge - PART-B is a
        // proven leaf), and the block for it sits directly under the SUB edge.
        Assert.Equal(1, CountOccurrences(text, "[CHILD REFERENCES NOT ENUMERATED"));
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var annotationLine = Array.FindIndex(lines, l => l.Contains("[CHILD REFERENCES NOT ENUMERATED"));
        var precedingEdge = Array.FindLastIndex(
            lines, annotationLine - 1, l => l.TrimStart().StartsWith("[INSIDE")
                || l.TrimStart().StartsWith("[OUTSIDE") || l.TrimStart().StartsWith("[SCOPE"));
        Assert.Contains("SUB.iam", lines[precedingEdge]);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    [Fact]
    public void A_complete_zero_reference_document_is_not_labelled_unavailable()
    {
        var scan = new CadReferenceScan(
            new CadReferenceRoot(P("Arch", "Job1", "LONE.ipt"), CadDocumentType.Ipt, null),
            Array.Empty<CadReference>(),
            new[] { Node(P("Arch", "Job1", "LONE.ipt"), CadDocumentType.Ipt, true) },
            Now);

        var text = CadReferenceScanTextReport.Render(scan);
        Assert.StartsWith("Scan Status: COMPLETE", text);
        Assert.Contains("zero direct references", text);
        Assert.Contains("Direct references: 0", text);
        Assert.Contains("Scan completeness: COMPLETE", text);
        Assert.DoesNotContain("Documents NOT enumerated", text);
    }

    [Fact]
    public void Rendering_is_deterministic()
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var scan = new CadReferenceScan(
            new CadReferenceRoot(root, CadDocumentType.Iam, null),
            new[]
            {
                Edge(root, "A.ipt", P("Arch", "Job1", "A.ipt"), CadRelationshipKind.Component, ReferenceWorkspaceScope.InsideWorkspace),
                Edge(root, "B.ipt", P("Arch", "Job1", "B.ipt"), CadRelationshipKind.Component, ReferenceWorkspaceScope.InsideWorkspace),
            },
            new[]
            {
                Node(root, CadDocumentType.Iam, true),
                Node(P("Arch", "Job1", "A.ipt"), CadDocumentType.Ipt, false),
                Node(P("Arch", "Job1", "B.ipt"), CadDocumentType.Ipt, true),
            },
            Now);

        Assert.Equal(CadReferenceScanTextReport.Render(scan), CadReferenceScanTextReport.Render(scan));
    }
}
