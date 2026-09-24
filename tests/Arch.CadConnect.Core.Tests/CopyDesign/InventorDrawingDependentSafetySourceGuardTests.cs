namespace Arch.CadConnect.Core.Tests.CopyDesign;

/// <summary>P6D ROUND 3, CRITICAL fix (item C): <c>InventorDrawingDependentSafety</c>
///  is COM-only - see <see cref="InventorCopyDesignPhysicalCopierSourceGuardTests"/>'s
///  own doc comment for the established SOURCE-LEVEL guard pattern this
///  follows.</summary>
public class InventorDrawingDependentSafetySourceGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Arch.CadConnect.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (Arch.CadConnect.sln) "
                + "above the test assembly - InventorDrawingDependentSafetySourceGuardTests cannot run.");
    }

    private static string Source() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "Arch.CadConnect.Inventor", "CopyDesign", "InventorDrawingDependentSafety.cs"));

    [Fact]
    public void A_blank_FullFileName_fails_closed_it_is_never_silently_skipped()
    {
        var source = Source();

        var blankCheckIndex = source.IndexOf("if (string.IsNullOrWhiteSpace(path))", StringComparison.Ordinal);
        var failResultIndex = source.IndexOf("no canonical physical path (blank FullFileName)", StringComparison.Ordinal);
        var addIndex = source.IndexOf("states.Add(new CopyDesignDependentDocumentState(path, dirty));", StringComparison.Ordinal);

        Assert.True(blankCheckIndex >= 0, "The blank-path check was not found.");
        Assert.True(failResultIndex >= 0, "The blank-path fail-closed message was not found.");
        Assert.True(addIndex >= 0, "The states.Add call was not found.");
        Assert.True(blankCheckIndex < failResultIndex, "The blank check must precede its fail-closed result.");
        Assert.True(failResultIndex < addIndex, "The fail-closed return for a blank path must happen BEFORE the Add call - it must never reach Add for that path.");
    }

    [Fact]
    public void Every_path_added_to_the_state_list_is_proven_non_blank_first()
    {
        var source = Source();

        // There is exactly ONE Add call, and it is unconditionally preceded
        // by the blank-path guard clause (not inside an `if (!blank)` any
        // more - a positive return-on-blank pattern instead).
        var addCount = System.Text.RegularExpressions.Regex.Matches(source, "states\\.Add\\(").Count;
        Assert.Equal(1, addCount);
        Assert.DoesNotContain("if (!string.IsNullOrWhiteSpace(path))", source, StringComparison.Ordinal);
    }
}
