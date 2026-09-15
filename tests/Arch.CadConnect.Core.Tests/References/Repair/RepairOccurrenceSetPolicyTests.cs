using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References.Repair;

/// <summary>
/// Codex round-3 HIGH finding: ComponentOccurrence.Replace(..., ReplaceAll:
/// true) replaces EVERY occurrence Inventor considers bound to the document,
/// not merely the ones this process previously recorded. These tests cover
/// the COM-free equality policy that must prove the live occurrence set is
/// EXACTLY the prepared set immediately before that call.
/// </summary>
public class RepairOccurrenceSetPolicyTests
{
    private static RepairOccurrenceIdentity Id(string name, string path = @"C:\ws\PartA.ipt") => new(name, path);

    [Fact]
    public void Identical_single_occurrence_sets_are_equal()
    {
        var prepared = new[] { Id("Occ1:1") };
        var live = new[] { Id("Occ1:1") };

        var result = RepairOccurrenceSetPolicy.VerifyEqual(prepared, live);

        Assert.Equal(RepairOccurrenceSetVerdict.Equal, result.Verdict);
        Assert.True(result.IsEqual);
    }

    [Fact]
    public void Identical_multi_occurrence_sets_are_equal_regardless_of_order()
    {
        var prepared = new[] { Id("Occ1:1"), Id("Occ2:1"), Id("Occ3:1") };
        var live = new[] { Id("Occ3:1"), Id("Occ1:1"), Id("Occ2:1") };

        var result = RepairOccurrenceSetPolicy.VerifyEqual(prepared, live);

        Assert.True(result.IsEqual);
    }

    [Fact]
    public void Live_set_with_an_extra_matching_occurrence_is_rejected()
    {
        var prepared = new[] { Id("Occ1:1") };
        var live = new[] { Id("Occ1:1"), Id("Occ2:1") }; // a new occurrence appeared after Prepare

        var result = RepairOccurrenceSetPolicy.VerifyEqual(prepared, live);

        Assert.Equal(RepairOccurrenceSetVerdict.Mismatch, result.Verdict);
        Assert.Contains("NOT in the prepared set", result.Detail);
    }

    [Fact]
    public void Live_set_missing_a_prepared_occurrence_is_rejected()
    {
        var prepared = new[] { Id("Occ1:1"), Id("Occ2:1") };
        var live = new[] { Id("Occ1:1") }; // Occ2 vanished or drifted off the old path

        var result = RepairOccurrenceSetPolicy.VerifyEqual(prepared, live);

        Assert.Equal(RepairOccurrenceSetVerdict.Mismatch, result.Verdict);
        Assert.Contains("no longer present", result.Detail);
    }

    [Fact]
    public void A_name_mismatch_at_the_same_path_is_rejected()
    {
        var prepared = new[] { Id("Occ1:1") };
        var live = new[] { Id("Occ1:2") }; // different occurrence name reporting the same path

        var result = RepairOccurrenceSetPolicy.VerifyEqual(prepared, live);

        Assert.Equal(RepairOccurrenceSetVerdict.Mismatch, result.Verdict);
    }

    [Fact]
    public void A_path_drift_for_the_same_occurrence_name_is_rejected()
    {
        var prepared = new[] { Id("Occ1:1", @"C:\ws\PartA.ipt") };
        var live = new[] { Id("Occ1:1", @"C:\ws\PartA-renamed.ipt") };

        var result = RepairOccurrenceSetPolicy.VerifyEqual(prepared, live);

        Assert.Equal(RepairOccurrenceSetVerdict.Mismatch, result.Verdict);
    }

    [Fact]
    public void A_duplicate_identity_in_the_prepared_set_is_rejected()
    {
        var prepared = new[] { Id("Occ1:1"), Id("Occ1:1") };
        var live = new[] { Id("Occ1:1") };

        var result = RepairOccurrenceSetPolicy.VerifyEqual(prepared, live);

        Assert.Equal(RepairOccurrenceSetVerdict.Mismatch, result.Verdict);
        Assert.Contains("duplicate identity", result.Detail);
    }

    [Fact]
    public void A_duplicate_identity_in_the_live_set_is_rejected()
    {
        var prepared = new[] { Id("Occ1:1") };
        var live = new[] { Id("Occ1:1"), Id("Occ1:1") };

        var result = RepairOccurrenceSetPolicy.VerifyEqual(prepared, live);

        Assert.Equal(RepairOccurrenceSetVerdict.Mismatch, result.Verdict);
        Assert.Contains("duplicate identity", result.Detail);
    }

    [Fact]
    public void An_empty_prepared_set_is_NoMatches_not_Mismatch()
    {
        var result = RepairOccurrenceSetPolicy.VerifyEqual(Array.Empty<RepairOccurrenceIdentity>(), Array.Empty<RepairOccurrenceIdentity>());

        Assert.Equal(RepairOccurrenceSetVerdict.NoMatches, result.Verdict);
        Assert.False(result.IsEqual);
    }

    [Fact]
    public void A_null_prepared_set_is_NoMatches()
    {
        var result = RepairOccurrenceSetPolicy.VerifyEqual(null, Array.Empty<RepairOccurrenceIdentity>());

        Assert.Equal(RepairOccurrenceSetVerdict.NoMatches, result.Verdict);
    }

    [Fact]
    public void An_indeterminate_unreadable_live_set_null_is_rejected_not_treated_as_equal()
    {
        var prepared = new[] { Id("Occ1:1") };

        var result = RepairOccurrenceSetPolicy.VerifyEqual(prepared, null);

        Assert.Equal(RepairOccurrenceSetVerdict.Mismatch, result.Verdict);
        Assert.Contains("could not be established", result.Detail);
    }
}
