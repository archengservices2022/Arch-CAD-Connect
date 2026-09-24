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

    // ======================================================================
    // P6D: IDW/DWG physical copy extension - same SOURCE-LEVEL guard
    // pattern (no live Inventor session available for this test project).
    // ======================================================================

    [Fact]
    public void An_Inventor_DWG_source_is_saved_through_SaveAsInventorDWG_never_the_plain_SaveAs()
    {
        var source = Source();

        Assert.Contains("SaveAsInventorDWG(tempPath, SaveCopyAs: true)", source, StringComparison.Ordinal);
        // The branch guarding it must key off CadDocumentType.Dwg specifically.
        Assert.Contains("node.DocumentType == CadDocumentType.Dwg", source, StringComparison.Ordinal);
    }

    [Fact]
    public void An_IDW_source_still_uses_the_ordinary_SaveAs_not_SaveAsInventorDWG()
    {
        var source = Source();
        var elseIndex = source.IndexOf("document.SaveAs(tempPath, SaveCopyAs: true);", StringComparison.Ordinal);
        Assert.True(elseIndex >= 0, "The ordinary SaveAs(tempPath, SaveCopyAs: true) call was not found.");
    }

    [Fact]
    public void IDW_and_DWG_are_both_verified_as_kDrawingDocumentObject_before_copy_is_attempted()
    {
        var source = Source();

        Assert.Contains("kDrawingDocumentObject", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drawing_format_mismatch_between_expected_IDW_and_actual_DWG_or_vice_versa_is_rejected_before_SaveAs()
    {
        var source = Source();

        Assert.Contains("IsInventorDWG != (node.DocumentType == CadDocumentType.Dwg)", source, StringComparison.Ordinal);
        var formatCheckIndex = source.IndexOf("IsInventorDWG != (node.DocumentType == CadDocumentType.Dwg)", StringComparison.Ordinal);
        var saveAsIndex = source.IndexOf("tempPath = CopyDesignAtomicPromotion.NewTempPath(destination);", StringComparison.Ordinal);
        Assert.True(formatCheckIndex >= 0 && saveAsIndex >= 0 && formatCheckIndex < saveAsIndex,
            "The IDW/DWG format check must happen BEFORE any SaveAs is attempted.");
    }

    [Fact]
    public void The_dirty_source_check_applies_uniformly_to_every_document_type_including_drawings()
    {
        var source = Source();
        var dirtyCheckIndex = source.IndexOf("if (document.Dirty)", StringComparison.Ordinal);
        var formatCheckIndex = source.IndexOf("IsInventorDWG != (node.DocumentType == CadDocumentType.Dwg)", StringComparison.Ordinal);

        Assert.True(dirtyCheckIndex >= 0, "The Dirty-source check was not found.");
        // The SAME single Dirty check must run AFTER the type/format checks
        // for every node - there is only ONE such check in the whole method
        // (never a second, drawing-specific copy of it), so it necessarily
        // covers IDW/DWG too.
        Assert.True(dirtyCheckIndex > formatCheckIndex, "The Dirty check must run after the drawing format check.");
        Assert.Single(Regex.Matches(source, @"if \(document\.Dirty\)"));
    }

    // ======================================================================
    // P6D ROUND 2, CRITICAL fix: never let Inventor SaveAs/SaveAsInventorDWG
    // a drawing while a referenced model dependent is Dirty.
    // ======================================================================

    [Fact]
    public void The_dependent_safety_guard_is_evaluated_for_a_drawing_BEFORE_any_SaveAs_or_SaveAsInventorDWG_call()
    {
        var source = Source();

        var guardIndex = source.IndexOf("InventorDrawingDependentSafety.Evaluate(document)", StringComparison.Ordinal);
        var tempPathIndex = source.IndexOf("tempPath = CopyDesignAtomicPromotion.NewTempPath(destination);", StringComparison.Ordinal);
        var saveAsIndex = source.IndexOf("SaveAsInventorDWG(tempPath, SaveCopyAs: true)", StringComparison.Ordinal);
        var plainSaveAsIndex = source.IndexOf("document.SaveAs(tempPath, SaveCopyAs: true);", StringComparison.Ordinal);

        Assert.True(guardIndex >= 0, "The dependent safety guard call was not found.");
        Assert.True(tempPathIndex >= 0 && saveAsIndex >= 0 && plainSaveAsIndex >= 0);
        Assert.True(guardIndex < tempPathIndex, "The dependent safety guard must run BEFORE the temp path / SaveAs call.");
        Assert.True(guardIndex < saveAsIndex && guardIndex < plainSaveAsIndex);
    }

    [Fact]
    public void The_dependent_safety_guard_is_scoped_to_drawing_document_types_only()
    {
        var source = Source();

        var guardIndex = source.IndexOf("InventorDrawingDependentSafety.Evaluate(document)", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0);
        // Immediately preceded by a check on node.DocumentType being Idw/Dwg
        // - never applied unconditionally to every node type.
        var precedingText = source[..guardIndex];
        var lastTypeCheckIndex = precedingText.LastIndexOf("node.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg", StringComparison.Ordinal);
        Assert.True(lastTypeCheckIndex >= 0, "The dependent safety guard must be gated on the node being a drawing.");
    }

    [Fact]
    public void A_failed_dependent_safety_check_returns_its_failure_reason_and_never_proceeds_to_SaveAs()
    {
        var source = Source();

        Assert.Contains("if (!dependentSafety.Safe)", source, StringComparison.Ordinal);
        Assert.Contains("new CopyDesignPhysicalCopyResult(false, dependentSafety.FailureReason)", source, StringComparison.Ordinal);
    }
}
