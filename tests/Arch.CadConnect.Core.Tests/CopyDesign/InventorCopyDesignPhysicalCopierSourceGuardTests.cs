using System.Text.RegularExpressions;

namespace Arch.CadConnect.Core.Tests.CopyDesign;

/// <summary>
/// CODEX ROUND 3, MEDIUM: <c>InventorCopyDesignPhysicalCopier</c> is COM-only
/// (<c>Arch.CadConnect.Inventor</c> is <c>net8.0-windows</c> and has no
/// dedicated test project - see <c>CopyDesignPreviewZeroMutationSourceTests</c>'s
/// own doc comment for the established precedent), so the temp-cleanup
/// retry-ordering fix is locked in with the same lightweight SOURCE-LEVEL
/// guard pattern: read the actual source and assert the fix's structural
/// invariants directly against its text.
/// </summary>
public class InventorCopyDesignPhysicalCopierSourceGuardTests
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
                + "above the test assembly - InventorCopyDesignPhysicalCopierSourceGuardTests cannot run.");
    }

    private static string Source() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "Arch.CadConnect.Inventor", "CopyDesign", "InventorCopyDesignPhysicalCopier.cs"));

    /// <summary>Finds the actual `finally` KEYWORD (a code block opener),
    ///  never a prose mention of the word inside a comment - this class's
    ///  own doc comments legitimately use the word "finally" in English, so
    ///  a bare substring search is not reliable.</summary>
    private static int FindFinallyKeyword(string source, int startAt) =>
        Regex.Match(source[startAt..], @"finally\s*\r?\n\s*\{").Index is var idx && idx >= 0
            ? startAt + idx
            : -1;

    [Fact]
    public void A_SaveAs_failure_attempts_cleanup_INSIDE_the_catch_block_BEFORE_the_finally_closes_the_document()
    {
        var source = Source();

        var catchIndex = source.IndexOf("catch (COMException ex)", StringComparison.Ordinal);
        var firstCleanupIndex = source.IndexOf("tempConfirmedGone = CopyDesignAtomicPromotion.CleanUpTempOnly(tempPath)", StringComparison.Ordinal);
        var finallyIndex = FindFinallyKeyword(source, catchIndex);

        Assert.True(catchIndex >= 0, "COMException catch block not found - source layout changed.");
        Assert.True(firstCleanupIndex >= 0, "The while-open cleanup attempt was not found.");
        Assert.True(finallyIndex >= 0, "finally block not found after the catch blocks.");
        Assert.True(firstCleanupIndex > catchIndex && firstCleanupIndex < finallyIndex,
            "The first cleanup attempt must happen inside the catch block, before the finally block closes the document.");
    }

    [Fact]
    public void A_retry_cleanup_attempt_happens_AFTER_the_finally_block_that_closes_the_document()
    {
        var source = Source();

        var finallyIndex = FindFinallyKeyword(source, 0);
        var retryIndex = source.IndexOf("CleanUpTempOnly(tempPath)", finallyIndex, StringComparison.Ordinal);

        Assert.True(finallyIndex >= 0, "finally block not found.");
        Assert.True(retryIndex > finallyIndex, "A retry cleanup attempt must appear after the finally block.");
        // Only retried when the earlier attempt did not confirm removal.
        Assert.Contains("if (tempPath is not null && !tempConfirmedGone)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unremoved_temp_path_is_surfaced_on_every_failure_path_that_could_leave_one_behind()
    {
        var source = Source();

        // SaveAs failure path.
        Assert.Contains("new CopyDesignPhysicalCopyResult(false, saveFailureMessage, unremovedTempPath)", source, StringComparison.Ordinal);
        // Lost-the-race path.
        Assert.Contains("raceCleanedUp ? null : tempPath", source, StringComparison.Ordinal);
        // Promotion-failure path.
        Assert.Contains("new CopyDesignPhysicalCopyResult(false, promotion.FailureReason, promotion.UnremovedTempPath)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Temp_cleanup_never_targets_the_source_path()
    {
        var source = Source();

        Assert.DoesNotContain("CleanUpTempOnly(node.SourceAbsolutePath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete(node.SourceAbsolutePath", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pre_existing_racing_final_is_never_deleted()
    {
        var source = Source();

        Assert.DoesNotContain("File.Delete(destination", source, StringComparison.Ordinal);
    }
}
