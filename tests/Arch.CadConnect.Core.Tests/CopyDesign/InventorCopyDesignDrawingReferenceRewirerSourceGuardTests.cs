namespace Arch.CadConnect.Core.Tests.CopyDesign;

/// <summary>
/// P6D: <c>InventorCopyDesignDrawingReferenceRewirer</c> is COM-only
/// (<c>Arch.CadConnect.Inventor</c> is <c>net8.0-windows</c> and has no
/// dedicated test project - see <see cref="InventorCopyDesignPhysicalCopierSourceGuardTests"/>'s
/// own doc comment for the established precedent), so its API choice and
/// safety invariants are locked in with the same lightweight SOURCE-LEVEL
/// guard pattern: read the actual source and assert the fix's structural
/// invariants directly against its text.
/// </summary>
public class InventorCopyDesignDrawingReferenceRewirerSourceGuardTests
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
                + "above the test assembly - InventorCopyDesignDrawingReferenceRewirerSourceGuardTests cannot run.");
    }

    private static string Source() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "Arch.CadConnect.Inventor", "CopyDesign", "InventorCopyDesignDrawingReferenceRewirer.cs"));

    /// <summary>Strips <c>///</c> XML-doc-comment lines, which legitimately
    ///  discuss (in prose) the OTHER APIs this class deliberately does NOT
    ///  use (ComponentOccurrence.Replace, the plural ReferencedFileDescriptors
    ///  collection) to explain WHY - so a blanket text search for those
    ///  names must only ever look at the actual executable code, never the
    ///  explanatory documentation.</summary>
    private static string CodeOnly(string source) => string.Join('\n',
        source.Split('\n').Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));

    [Fact]
    public void Mutation_uses_FileDescriptor_ReplaceReference_never_ComponentOccurrence_Replace()
    {
        var source = Source();
        var code = CodeOnly(source);

        Assert.Contains(".ReplaceReference(target.ExpectedTargetAbsolutePath)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ComponentOccurrence", code, StringComparison.Ordinal);
        // The assembly rewirer's exact two-argument call shape
        // (Replace(FileName, ReplaceAll)) must never appear in the actual
        // code of this class.
        Assert.DoesNotContain(".Replace(target.ExpectedTargetAbsolutePath, true)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Enumeration_uses_the_document_level_ReferencedDocumentDescriptors_collection()
    {
        var source = Source();

        Assert.Contains("drawing.ReferencedDocumentDescriptors", source, StringComparison.Ordinal);
        Assert.Contains("DocumentDescriptor descriptor in descriptors", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Never_uses_the_plural_ReferencedFileDescriptors_collection_which_has_no_ReplaceReference_method()
    {
        var code = CodeOnly(Source());

        // The plural collection's item type (singular "ReferencedFileDescriptor")
        // has no ReplaceReference at all - deliberately not the type this
        // class mutates. Only the DocumentDescriptor-sourced FileDescriptor
        // (via .ReferencedFileDescriptor) is ever used. Only the CODE (doc
        // comments legitimately explain this choice in prose) is checked.
        Assert.DoesNotContain("ReferencedFileDescriptors", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_freshly_opened_COPIED_destination_is_ever_opened_the_source_is_never_touched()
    {
        var source = Source();

        Assert.Contains("application.Documents.Open(destination, OpenVisible: false)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Open(copiedDrawingNode.SourceAbsolutePath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("node.SourceAbsolutePath", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_COPY_targets_are_mutated_REUSE_targets_are_never_touched()
    {
        var source = Source();

        Assert.Contains("targets.Where(t => t.ChildIsCopy)", source, StringComparison.Ordinal);
    }

    // P6D ROUND 2, CRITICAL fix: plain Save() is REPLACED by
    // Save2(SaveDependents: false, ...) - see the dedicated guard tests
    // below for the exact call and the dirty-dependent gate in front of it.
    [Fact]
    public void The_document_is_saved_after_mutation_via_Save2_never_plain_Save_and_always_closed_with_SkipSave()
    {
        var code = CodeOnly(Source());

        Assert.Contains("drawing.Save2(SaveDependents: false, DocumentsToSave: Type.Missing);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("document.Save();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("drawing.Save();", code, StringComparison.Ordinal);
        Assert.Contains("document.Close(SkipSave: true)", code, StringComparison.Ordinal);
    }

    // ======================================================================
    // P6D ROUND 2, CRITICAL fix: never let Inventor Save2 a drawing while a
    // referenced model dependent is Dirty.
    // ======================================================================

    [Fact]
    public void The_dependent_safety_guard_is_evaluated_AFTER_ReplaceReference_and_BEFORE_Save2()
    {
        var code = CodeOnly(Source());

        var lastReplaceReferenceIndex = code.LastIndexOf(".ReplaceReference(target.ExpectedTargetAbsolutePath)", StringComparison.Ordinal);
        var guardIndex = code.IndexOf("InventorDrawingDependentSafety.Evaluate((InventorApi.Document)drawing)", StringComparison.Ordinal);
        var save2Index = code.IndexOf("drawing.Save2(SaveDependents: false", StringComparison.Ordinal);

        Assert.True(lastReplaceReferenceIndex >= 0, "ReplaceReference call not found.");
        Assert.True(guardIndex >= 0, "The dependent safety guard call was not found.");
        Assert.True(save2Index >= 0, "The Save2 call was not found.");
        Assert.True(lastReplaceReferenceIndex < guardIndex, "The guard must run AFTER ReplaceReference (the referenced set has just changed).");
        Assert.True(guardIndex < save2Index, "The guard must run BEFORE Save2.");
    }

    [Fact]
    public void A_failed_dependent_safety_check_returns_its_failure_reason_and_never_reaches_Save2()
    {
        var code = CodeOnly(Source());

        Assert.Contains("if (!dependentSafety.Safe)", code, StringComparison.Ordinal);
        Assert.Contains("new CopyDesignRewireResult(false, dependentSafety.FailureReason)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ambiguous_match_more_than_one_descriptor_resolving_to_the_same_original_path_is_rejected_never_guessed()
    {
        var source = Source();

        Assert.Contains("matched.Count > 1", source, StringComparison.Ordinal);
        Assert.Contains("refusing to guess which to mutate", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_expected_reference_is_rejected_never_silently_skipped()
    {
        var source = Source();

        Assert.Contains("matched.Count == 0", source, StringComparison.Ordinal);
        Assert.Contains("refusing to guess", source, StringComparison.Ordinal);
    }
}
