namespace Arch.CadConnect.Core.Tests.CopyDesign;

/// <summary>
/// BLOCKER 4 (P6A Round 3): the Copy Design preview flow is PLAN + PREVIEW
/// ONLY - it must never create a directory, write/copy/move a file, or invoke
/// any Inventor Save/SaveAs. There is no WinForms UI test harness anywhere in
/// this repository (the Inventor project is <c>net8.0-windows</c>, COM-only,
/// and has no dedicated test project), so - per the Round 3 instruction to
/// avoid inventing brittle WinForms automation - this is a lightweight
/// SOURCE-LEVEL guard: it reads the actual preview-flow source files and
/// asserts the zero-mutation invariants directly against their text, so a
/// regression (e.g. someone flipping <c>ShowNewFolderButton</c> back to
/// <c>true</c>, or adding a stray <c>Directory.CreateDirectory</c>) fails a
/// fast, deterministic, non-UI test instead of only being caught by hand.
/// </summary>
public class CopyDesignPreviewZeroMutationSourceTests
{
    private static readonly string[] ForbiddenFilesystemMutationCalls =
    {
        "Directory.CreateDirectory",
        "Directory.Move",
        "Directory.Delete",
        "File.Create",
        "File.WriteAllText",
        "File.WriteAllBytes",
        "File.WriteAllLines",
        "File.AppendAllText",
        "File.Copy",
        "File.Move",
        "File.Delete",
        "GetTempFileName",
        "SaveAs",
        "SaveCopyAs",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arch.CadConnect.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (Arch.CadConnect.sln) "
                + "above the test assembly - CopyDesignPreviewZeroMutationSourceTests cannot run.");
    }

    private static string ReadSource(params string[] relativeSegments) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relativeSegments).ToArray()));

    [Fact]
    public void The_destination_folder_picker_does_NOT_offer_New_Folder()
    {
        var source = ReadSource("src", "Arch.CadConnect.Inventor", "Ui", "CopyDesignPreviewDialog.cs");

        Assert.Contains("ShowNewFolderButton = false", source);
        Assert.DoesNotContain("ShowNewFolderButton = true", source);
    }

    [Theory]
    [MemberData(nameof(PreviewFlowSourceFiles))]
    public void The_Copy_Design_preview_flow_contains_no_filesystem_mutation_call(string relativePath)
    {
        var source = ReadSource(relativePath.Split('/'));

        foreach (var forbidden in ForbiddenFilesystemMutationCalls)
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    public static IEnumerable<object[]> PreviewFlowSourceFiles()
    {
        yield return new object[] { "src/Arch.CadConnect.Inventor/Ui/CopyDesignPreviewDialog.cs" };
        yield return new object[] { "src/Arch.CadConnect.Core/CopyDesign/CopyDesignPlanner.cs" };
        yield return new object[] { "src/Arch.CadConnect.Core/CopyDesign/CopyDesignPlanTextReport.cs" };
    }

    [Fact]
    public void The_ONLY_filesystem_touch_in_the_controller_preview_method_is_a_read_only_existence_check()
    {
        var source = ReadSource("src", "Arch.CadConnect.Inventor", "ArchAddInController.cs");

        // The controller as a whole legitimately reads other things (e.g.
        // workspace settings) elsewhere, so this asserts specifically within
        // the Copy Design preview method's body, not the whole file.
        var start = source.IndexOf("private void RunCopyDesignPreview()", StringComparison.Ordinal);
        Assert.True(start >= 0, "RunCopyDesignPreview() method declaration not found - source layout changed.");
        var end = source.IndexOf("\n    private", start, StringComparison.Ordinal);
        var body = end > start ? source[start..end] : source[start..];

        foreach (var forbidden in ForbiddenFilesystemMutationCalls)
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
        }
        // The one permitted filesystem touch is the read-only existence
        // check wired in as `destinationExists`.
        Assert.Contains("destinationExists: SafeFileExists", body, StringComparison.Ordinal);
    }
}
