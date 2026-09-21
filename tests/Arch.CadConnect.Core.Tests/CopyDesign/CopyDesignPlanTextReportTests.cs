using Arch.CadConnect.Core.CopyDesign;

namespace Arch.CadConnect.Core.Tests.CopyDesign;

/// <summary>P6C Round 8: the rendered PREVIEW text must visibly state the
///  acknowledged model-files-only mode - not only inside the warnings list -
///  so both the preview dialog AND (indirectly, since the Apply confirmation
///  restates it independently) the mandatory confirmation step communicate
///  the same fact.</summary>
public class CopyDesignPlanTextReportTests
{
    private static CopyDesignNode RootNode(bool acknowledged) => new(
        "cad_root", "fv_root1", CadDocumentType.Iam, @"C:\Design\ROOT.iam", "ROOT.iam",
        RelationshipToParent: null, IsManaged: true, IsVerified: true, IsResolved: true,
        CopyDesignAction.Copy, "ROOT2.iam", @"C:\Dest\ROOT2.iam", Array.Empty<string>(), IsRoot: true);

    private static CopyDesignPlan BuildPlan(bool acknowledged) => new(
        RootAbsolutePath: @"C:\Design\ROOT.iam",
        Nodes: new[] { RootNode(acknowledged) },
        Edges: Array.Empty<CopyDesignEdge>(),
        Warnings: acknowledged
            ? new[] { "MODE: MODEL FILES ONLY - the engineer explicitly acknowledged that drawing associations "
                + "cannot be proven for one or more models and chose to continue without them. "
                + "DRAWINGS: NOT INCLUDED - no IDW/DWG file will be copied or reference-rewired by this operation." }
            : Array.Empty<string>(),
        IsExecutable: true,
        ScanWasComplete: true,
        DrawingAssociationAvailable: false,
        ModelFilesOnlyAcknowledged: acknowledged);

    [Fact]
    public void Render_visibly_states_MODEL_FILES_ONLY_and_DRAWINGS_NOT_INCLUDED_when_acknowledged()
    {
        var text = CopyDesignPlanTextReport.Render(BuildPlan(acknowledged: true));

        Assert.Contains("MODE: MODEL FILES ONLY", text, StringComparison.Ordinal);
        Assert.Contains("DRAWINGS: NOT INCLUDED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_never_mentions_MODEL_FILES_ONLY_when_not_acknowledged()
    {
        var text = CopyDesignPlanTextReport.Render(BuildPlan(acknowledged: false));

        Assert.DoesNotContain("MODEL FILES ONLY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DRAWINGS: NOT INCLUDED", text, StringComparison.Ordinal);
    }
}
