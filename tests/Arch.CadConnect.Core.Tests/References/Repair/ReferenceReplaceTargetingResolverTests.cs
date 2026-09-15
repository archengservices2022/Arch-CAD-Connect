using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.References.Repair.RepairFixtures;

namespace Arch.CadConnect.Core.Tests.References.Repair;

/// <summary>
/// The COM-free selection of the ONE descriptor a replacement acts on: matched
/// strictly by currently-resolved absolute path, never by name, never a guess.
/// </summary>
public class ReferenceReplaceTargetingResolverTests
{
    private static ReferenceDescriptorSnapshot D(
        int i, string? resolved, string name = "ref", string cad = "cad_a", string fv = "fv_v1",
        bool verified = true) => new(i, resolved, name, cad, fv, verified);

    private static ReferenceReplaceTargeting Resolve(IReadOnlyList<ReferenceDescriptorSnapshot> descriptors, string path) =>
        ReferenceReplaceTargetingResolver.Resolve(descriptors, path, "cad_a", "fv_v1");

    [Fact]
    public void Exactly_one_descriptor_resolving_to_the_current_path_is_Matched()
    {
        var descriptors = new[]
        {
            D(0, P("Arch", "Job1", "OTHER.ipt")),
            D(1, P("Arch", "Job1", "PART-A.ipt")),
            D(2, null, "missing"),
        };

        var result = Resolve(descriptors, P("Arch", "Job1", "PART-A.ipt"));

        Assert.Equal(ReferenceReplaceTargetingOutcome.Matched, result.Outcome);
        Assert.Equal(1, result.Index);
    }

    [Fact]
    public void No_descriptor_resolving_to_the_current_path_is_NoMatch()
    {
        var descriptors = new[] { D(0, P("Arch", "Job1", "OTHER.ipt")) };
        var result = Resolve(descriptors, P("Arch", "Job1", "PART-A.ipt"));
        Assert.Equal(ReferenceReplaceTargetingOutcome.NoMatch, result.Outcome);
        Assert.Equal(-1, result.Index);
    }

    [Fact]
    public void More_than_one_descriptor_resolving_to_the_same_path_is_Ambiguous_never_a_guess()
    {
        var descriptors = new[]
        {
            D(0, P("Arch", "Job1", "PART-A.ipt")),
            D(1, P("Arch", "Job1", "PART-A.ipt")),
        };
        var result = Resolve(descriptors, P("Arch", "Job1", "PART-A.ipt"));
        Assert.Equal(ReferenceReplaceTargetingOutcome.Ambiguous, result.Outcome);
        Assert.Equal(-1, result.Index);
    }

    [Fact]
    public void Matching_is_case_insensitive_and_path_normalised_but_never_by_name()
    {
        var descriptors = new[]
        {
            D(0, P("Arch", "Job1", "sub", "..", "PART-A.ipt"), name: "WOULD-MATCH-BY-NAME"),
            D(1, P("Arch", "Job1", "PART-A.IPT")),
        };
        // request by a differently-cased path; the name-only decoy at index 0
        // also normalises to the same path, so this is Ambiguous (never "pick
        // the one whose name matches").
        var result = Resolve(descriptors, P("Arch", "Job1", "part-a.ipt"));
        Assert.Equal(ReferenceReplaceTargetingOutcome.Ambiguous, result.Outcome);
    }

    [Theory]
    [InlineData("cad_other", "fv_v1", true)]
    [InlineData("cad_a", "fv_other", true)]
    [InlineData("cad_a", "fv_v1", false)]
    public void Path_match_without_exact_verified_identity_is_not_authorized(string cad, string fv, bool verified)
    {
        var descriptors = new[] { D(0, P("Arch", "Job1", "PART-A.ipt"), cad: cad, fv: fv, verified: verified) };

        Assert.Equal(ReferenceReplaceTargetingOutcome.NoMatch,
            Resolve(descriptors, P("Arch", "Job1", "PART-A.ipt")).Outcome);
    }
}
