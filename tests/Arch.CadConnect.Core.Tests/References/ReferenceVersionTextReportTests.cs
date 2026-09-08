using Arch.CadConnect.Core;
using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References;

public class ReferenceVersionTextReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Base = OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static string P(params string[] segments) => Path.GetFullPath(Path.Combine(Base, Path.Combine(segments)));

    private static CadReference Managed(string name, string cad, string fv) => new()
    {
        ParentAbsolutePath = P("Arch", "Job1", "ROOT.iam"),
        InventorReportedName = name,
        ResolvedAbsolutePath = P("Arch", "Job1", name),
        ReferenceType = CadDocumentType.Ipt,
        RelationshipKind = CadRelationshipKind.Component,
        Resolution = CadReferenceResolution.Resolved,
        Scope = ReferenceWorkspaceScope.InsideWorkspace,
        ManifestIdentity = new CadManifestIdentity(cad, fv, "DOC-" + cad, name),
    };

    private static ReferenceHealthReport Local(params CadReference[] edges)
    {
        var root = P("Arch", "Job1", "ROOT.iam");
        var scan = new CadReferenceScan(
            new CadReferenceRoot(root, CadDocumentType.Iam, null),
            edges,
            new[] { new CadReferenceNode(root, CadDocumentType.Iam, true) },
            Now);
        return ReferenceHealthDiagnoser.Diagnose(scan);
    }

    [Fact]
    public void It_renders_the_P5BA_block_then_an_additive_version_section()
    {
        var local = Local(Managed("PART-A.ipt", "cad_a", "fv_v1"), Managed("PART-B.ipt", "cad_b", "fv_b"));
        var oracle = LatestVersionLookup.FromResults(new[]
        {
            LatestVersionResult.Found("cad_a", "fv_v2"),   // STALE
            LatestVersionResult.Found("cad_b", "fv_b"),    // CURRENT
        });

        var text = ReferenceVersionTextReport.Render(ReferenceVersionReport.Build(local, oracle));

        // P5B-A block still present verbatim
        Assert.Contains("REFERENCE HEALTH - ROOT.iam", text);
        Assert.Contains("Resolved | Inside Workspace | Managed", text);

        // additive version section
        Assert.Contains("VERSION INTELLIGENCE - authoritative Arch PLM", text);
        Assert.Contains("Version Status: STALE REFERENCES FOUND", text);
        Assert.Contains("Combined Health (local + version): ATTENTION REQUIRED", text);
        Assert.Contains("STALE", text);
        Assert.Contains("CURRENT", text);
        Assert.Contains("authoritative latest fileVersionId: fv_v2", text);
        Assert.Contains("Managed references checked: 2", text);
        Assert.Contains("Current: 1", text);
        Assert.Contains("Stale: 1", text);
        Assert.Contains("Unknown version: 0", text);
    }

    [Fact]
    public void It_states_plainly_when_there_is_nothing_to_version_check()
    {
        var text = ReferenceVersionTextReport.Render(ReferenceVersionReport.Build(
            Local(), LatestVersionLookup.WholeFailure(LatestVersionOutcome.NotAttempted)));

        Assert.Contains("No managed references with a pinned local identity", text);
        Assert.Contains("Managed references checked: 0", text);
    }

    [Fact]
    public void Rendering_is_deterministic()
    {
        var local = Local(Managed("PART-A.ipt", "cad_a", "fv_v1"));
        var oracle = LatestVersionLookup.WholeFailure(LatestVersionOutcome.ServerUnavailable);
        var report = ReferenceVersionReport.Build(local, oracle);

        Assert.Equal(ReferenceVersionTextReport.Render(report), ReferenceVersionTextReport.Render(report));
    }
}
