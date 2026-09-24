namespace Arch.CadConnect.Core.Tests.CopyDesign;

/// <summary>
/// P6D: <c>InventorCopyDesignDrawingVerifier</c> is COM-only - see
/// <see cref="InventorCopyDesignPhysicalCopierSourceGuardTests"/>'s own doc
/// comment for the established SOURCE-LEVEL guard pattern this follows.
/// </summary>
public class InventorCopyDesignDrawingVerifierSourceGuardTests
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
                + "above the test assembly - InventorCopyDesignDrawingVerifierSourceGuardTests cannot run.");
    }

    private static string Source() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "Arch.CadConnect.Inventor", "CopyDesign", "InventorCopyDesignDrawingVerifier.cs"));

    [Fact]
    public void The_destination_is_opened_FRESH_independent_of_whatever_the_rewirer_had_open()
    {
        var source = Source();

        Assert.Contains("application.Documents.Open(destination, OpenVisible: false)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_actual_reference_set_is_gathered_from_ReferencedDocumentDescriptors_never_ComponentDefinition_Occurrences()
    {
        var source = Source();

        Assert.Contains("drawing.ReferencedDocumentDescriptors", source, StringComparison.Ordinal);
        // The word "Occurrence(s)" legitimately appears elsewhere (the doc
        // comment contrasting this with the assembly verifier, and the
        // shared CopyDesignActualComponentOccurrence type this class reuses)
        // - what must NEVER appear is the actual live COM call chain an
        // assembly verifier uses to enumerate component occurrences.
        Assert.DoesNotContain(".ComponentDefinition.Occurrences", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AssemblyDocument", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IDW_vs_DWG_document_type_matching_checks_both_kDrawingDocumentObject_AND_IsInventorDWG()
    {
        var source = Source();

        Assert.Contains("kDrawingDocumentObject", source, StringComparison.Ordinal);
        Assert.Contains("IsInventorDWG == (node.DocumentType == CadDocumentType.Dwg)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Enumeration_failure_is_reported_as_its_own_fact_never_inferred_from_an_empty_list()
    {
        var source = Source();

        Assert.Contains("descriptorsGathered", source, StringComparison.Ordinal);
        Assert.Contains("occurrenceEnumerationSucceeded = descriptorsGathered", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gathered_facts_reuse_the_SAME_pure_evaluator_types_as_the_model_verifier()
    {
        var source = Source();

        // Confirms this class produces CopyDesignNodeVerificationFacts /
        // CopyDesignActualComponentOccurrence / CopyDesignReferenceResolutionFact
        // - the SAME document-type-agnostic shapes CopyDesignVerificationEvaluator
        // already judges, rather than inventing a parallel drawing-only fact
        // shape.
        Assert.Contains("new CopyDesignNodeVerificationFacts(", source, StringComparison.Ordinal);
        Assert.Contains("new CopyDesignActualComponentOccurrence(resolved)", source, StringComparison.Ordinal);
        Assert.Contains("new CopyDesignReferenceResolutionFact(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_document_is_always_closed_with_SkipSave_never_mutates_anything()
    {
        var source = Source();

        Assert.Contains("document.Close(SkipSave: true)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("document.Save()", source, StringComparison.Ordinal);
        // "ReplaceReference" is legitimately mentioned once in the class doc
        // comment (contrasting this READ-ONLY verifier with the rewirer that
        // actually calls it) - what must never appear is an actual CALL.
        Assert.DoesNotContain(".ReplaceReference(", source, StringComparison.Ordinal);
    }
}
