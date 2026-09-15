using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References.Repair;

/// <summary>
/// Codex round-3 finding: the confirmation dialog's "Only this one reference
/// will change" wording is misleading when the file is used by more than one
/// occurrence. These tests cover the pure wording-selection logic (the live
/// occurrence count itself requires COM and is gathered separately in the
/// Inventor project, for disclosure only - never as an authorization input).
/// </summary>
public class RepairOccurrenceDisclosureTextTests
{
    [Fact]
    public void Unknown_count_never_claims_a_specific_number()
    {
        var text = RepairOccurrenceDisclosureText.ScopeSentence(null);

        Assert.DoesNotContain("1 occurrence", text);
        Assert.Contains("more than one occurrence", text);
    }

    [Fact]
    public void Exactly_one_occurrence_uses_singular_wording()
    {
        var text = RepairOccurrenceDisclosureText.ScopeSentence(1);

        Assert.Contains("1 occurrence", text);
        Assert.DoesNotContain("occurrences", text);
    }

    [Fact]
    public void Multiple_occurrences_discloses_the_exact_count_and_that_all_will_be_replaced()
    {
        var text = RepairOccurrenceDisclosureText.ScopeSentence(3);

        Assert.Contains("3 occurrences", text);
        Assert.Contains("all 3 will be replaced", text);
    }

    [Fact]
    public void Never_implies_only_one_occurrence_changes_when_more_than_one_will()
    {
        var text = RepairOccurrenceDisclosureText.ScopeSentence(5);

        Assert.DoesNotContain("Only this one reference", text);
        Assert.DoesNotContain("1 occurrence", text);
    }
}
