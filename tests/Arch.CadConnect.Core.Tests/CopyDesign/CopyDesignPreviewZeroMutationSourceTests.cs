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
        // Round 6 / Round 8: the NeedsDecision-resolution and model-files-only
        // dialogs are part of the SAME zero-mutation preview flow - they must
        // never touch the filesystem either.
        yield return new object[] { "src/Arch.CadConnect.Inventor/Ui/CopyDesignDecisionsDialog.cs" };
        yield return new object[] { "src/Arch.CadConnect.Inventor/Ui/CopyDesignModelFilesOnlyDialog.cs" };
        // P6D: the drawing-association authority prefetch is now part of the
        // SAME zero-mutation preview flow - a read-only HTTP lookup and a
        // pure candidate-id derivation, neither ever touching the filesystem.
        yield return new object[] { "src/Arch.CadConnect.Api/CopyDesign/HttpDrawingAssociationClient.cs" };
        yield return new object[] { "src/Arch.CadConnect.Core/CopyDesign/CopyDesignDrawingAssociationCandidates.cs" };
    }

    [Fact]
    public void The_model_files_only_dialog_never_pre_selects_an_AcceptButton()
    {
        // Round 8: "No pre-selected/default Yes" - pressing Enter must never
        // be mistaken for an affirmative acknowledgement.
        var source = ReadSource("src", "Arch.CadConnect.Inventor", "Ui", "CopyDesignModelFilesOnlyDialog.cs");

        Assert.DoesNotContain("AcceptButton =", source, StringComparison.Ordinal);
    }

    // ---- CODEX FINAL AUDIT ROUND 1, MEDIUM 1: a Dirty source is rejected
    //      regardless of who opened it - there is no dedicated WinForms/COM
    //      test harness for this repo (see this class's own doc comment), so
    //      this is the same established SOURCE-LEVEL guard pattern. --------

    [Fact]
    public void The_physical_copier_checks_Dirty_UNCONDITIONALLY_not_only_when_the_source_was_already_open()
    {
        var source = ReadSource("src", "Arch.CadConnect.Inventor", "CopyDesign", "InventorCopyDesignPhysicalCopier.cs");

        // The fix: the Dirty check must be its own, unconditional guard -
        // never re-gated behind "wasAlreadyOpen &&" (a regression would
        // silently let a self-opened-but-Dirty document through).
        Assert.Contains("if (document.Dirty)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if (wasAlreadyOpen && document.Dirty)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_physical_copier_never_saves_the_source_document()
    {
        var source = ReadSource("src", "Arch.CadConnect.Inventor", "CopyDesign", "InventorCopyDesignPhysicalCopier.cs");

        Assert.DoesNotContain("document.Save(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("document.Save2(", source, StringComparison.Ordinal);
        // The only close of a document THIS class itself opened must be
        // SkipSave - never persisting whatever state the source was in.
        Assert.Contains("document.Close(SkipSave: true)", source, StringComparison.Ordinal);
    }

    // ---- CODEX FINAL AUDIT ROUND 1, HIGH 4: never SaveAs directly onto the
    //      unclaimed final destination - source-level guard for the same
    //      untestable-COM reason. -------------------------------------------

    [Fact]
    public void The_physical_copier_never_calls_SaveAs_with_the_final_destination_directly()
    {
        var source = ReadSource("src", "Arch.CadConnect.Inventor", "CopyDesign", "InventorCopyDesignPhysicalCopier.cs");

        Assert.DoesNotContain("SaveAs(destination,", source, StringComparison.Ordinal);
        Assert.Contains("SaveAs(tempPath,", source, StringComparison.Ordinal);
        Assert.Contains("CopyDesignAtomicPromotion.PromoteToFinal(tempPath, destination)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ONLY_filesystem_touch_in_the_controller_preview_method_is_a_read_only_existence_check()
    {
        var source = ReadSource("src", "Arch.CadConnect.Inventor", "ArchAddInController.cs");

        // The controller as a whole legitimately reads other things (e.g.
        // workspace settings) elsewhere, so this asserts specifically within
        // the Copy Design preview flow's body, not the whole file.
        //
        // P6D: the flow is now split across TWO private methods -
        // RunCopyDesignPreview() (scan + dialog + the one drawing-association
        // network prefetch, dispatched via RunBackground so the synchronous
        // COM scan step is never blocked on it) and ContinueCopyDesignPreview
        // (everything downstream: planning, the decision/model-files-only
        // dialogs, the report, and the apply hand-off) - both are captured
        // here so the zero-mutation guarantee still covers the WHOLE flow,
        // not just the first half.
        var start = source.IndexOf("private void RunCopyDesignPreview()", StringComparison.Ordinal);
        Assert.True(start >= 0, "RunCopyDesignPreview() method declaration not found - source layout changed.");
        var continueStart = source.IndexOf("private void ContinueCopyDesignPreview(", start, StringComparison.Ordinal);
        Assert.True(continueStart > start, "ContinueCopyDesignPreview(...) method declaration not found after RunCopyDesignPreview() - source layout changed.");
        var end = source.IndexOf("\n    private", continueStart, StringComparison.Ordinal);
        var body = end > continueStart ? source[start..end] : source[start..];

        foreach (var forbidden in ForbiddenFilesystemMutationCalls)
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
        }
        // The one permitted filesystem touch is the read-only existence
        // check wired in as `destinationExists`.
        Assert.Contains("destinationExists: SafeFileExists", body, StringComparison.Ordinal);
    }

    // ---- CODEX ROUND 3, HIGH, item 4: IPT is unaffected by the occurrence-
    //      enumeration-success requirement - the gatherer must compute it as
    //      "not an IAM -> always true", never gated behind enumeration that
    //      an IPT never performs. COM-only, no dedicated test harness (see
    //      this class's own doc comment), hence the same source-level guard
    //      pattern. -------------------------------------------------------

    [Fact]
    public void The_verifier_reports_occurrence_enumeration_as_trivially_succeeded_for_a_non_IAM_node()
    {
        var source = ReadSource("src", "Arch.CadConnect.Inventor", "CopyDesign", "InventorCopyDesignVerifier.cs");

        Assert.Contains("node.DocumentType != CadDocumentType.Iam || occurrencesGathered", source, StringComparison.Ordinal);
    }
}
