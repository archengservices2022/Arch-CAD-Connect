using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;
using Arch.CadConnect.Core.References;

using static Arch.CadConnect.Core.Tests.CopyDesign.CopyDesignFixtures;

namespace Arch.CadConnect.Core.Tests.CopyDesign;

/// <summary>P6C Round 6: <see cref="CopyDesignExplicitDecisionSet"/> validation
///  and <see cref="CopyDesignExplicitDecisionEligibility"/> - the shared
///  vocabulary a caller (typically the Ribbon UI) uses to know which nodes it
///  may show a COPY/REUSE/EXCLUDE control for, and to build a validated set of
///  decisions before handing it to <see cref="CopyDesignPlanner.Plan"/>. See
///  <c>CopyDesignPlannerTests</c> for how the planner actually CONSUMES a
///  decision set.</summary>
public class CopyDesignExplicitDecisionsTests
{
    // ---- CopyDesignExplicitDecisionSet.Build validation -----------------

    [Fact]
    public void Build_accepts_Copy_Reuse_and_Exclude_keyed_by_cadDocumentId()
    {
        var set = CopyDesignExplicitDecisionSet.Build(new[]
        {
            ("cad_a", CopyDesignAction.Copy),
            ("cad_b", CopyDesignAction.Reuse),
            ("cad_c", CopyDesignAction.Exclude),
        });

        Assert.Equal(3, set.Count);
        Assert.Equal(CopyDesignAction.Copy, set.AsDictionary()["cad_a"]);
        Assert.Equal(CopyDesignAction.Reuse, set.AsDictionary()["cad_b"]);
        Assert.Equal(CopyDesignAction.Exclude, set.AsDictionary()["cad_c"]);
    }

    [Fact]
    public void Empty_set_has_zero_entries_and_resolves_nothing()
    {
        Assert.Equal(0, CopyDesignExplicitDecisionSet.Empty.Count);
        Assert.Empty(CopyDesignExplicitDecisionSet.Empty.AsDictionary());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_rejects_a_blank_cadDocumentId(string? blankId)
    {
        Assert.Throws<ArgumentException>(() =>
            CopyDesignExplicitDecisionSet.Build(new[] { (blankId!, CopyDesignAction.Copy) }));
    }

    [Fact]
    public void Build_rejects_NeedsDecision_as_an_explicit_decision()
    {
        // There is no such thing as an explicit decision to "still not decide".
        var ex = Assert.Throws<ArgumentException>(() =>
            CopyDesignExplicitDecisionSet.Build(new[] { ("cad_a", CopyDesignAction.NeedsDecision) }));
        Assert.Contains("cad_a", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_rejects_a_duplicate_cadDocumentId()
    {
        Assert.Throws<ArgumentException>(() => CopyDesignExplicitDecisionSet.Build(new[]
        {
            ("cad_a", CopyDesignAction.Copy),
            ("cad_a", CopyDesignAction.Reuse),
        }));
    }

    // ---- CopyDesignExplicitDecisionEligibility ---------------------------

    private static CopyDesignNode ClassificationUnknownNode(CadDocumentType type = CadDocumentType.Ipt) => new(
        "cad_p1", "fv_p1", type, P("Design", "10073-P001.ipt"), "10073-P001.ipt",
        CadRelationshipKind.Component, IsManaged: true, IsVerified: true, IsResolved: true,
        CopyDesignAction.NeedsDecision, null, null,
        new[] { CopyDesignExplicitDecisionEligibility.ClassificationUnknownReasonPrefix + " - refusing to guess. Suggested: COPY." });

    [Fact]
    public void An_IPT_or_IAM_NeedsDecision_node_with_the_classification_unknown_reason_is_eligible()
    {
        Assert.True(CopyDesignExplicitDecisionEligibility.IsEligibleForExplicitDecision(ClassificationUnknownNode(CadDocumentType.Ipt)));
        Assert.True(CopyDesignExplicitDecisionEligibility.IsEligibleForExplicitDecision(ClassificationUnknownNode(CadDocumentType.Iam)));
    }

    [Fact]
    public void A_drawing_NeedsDecision_node_is_never_eligible_even_with_the_classification_unknown_reason()
    {
        // P6C's strict scope never surfaces a decision control for a drawing -
        // P6D owns drawings.
        Assert.False(CopyDesignExplicitDecisionEligibility.IsEligibleForExplicitDecision(ClassificationUnknownNode(CadDocumentType.Idw)));
    }

    [Fact]
    public void A_NeedsDecision_node_for_ANY_other_reason_is_never_eligible()
    {
        var conflicted = new CopyDesignNode(
            "cad_p1", "fv_p1", CadDocumentType.Ipt, P("Design", "10073-P001.ipt"), "10073-P001.ipt",
            CadRelationshipKind.Component, IsManaged: true, IsVerified: true, IsResolved: true,
            CopyDesignAction.NeedsDecision, null, null,
            new[] { "Conflicting FileVersionId observations for the same cadDocumentId." });

        Assert.False(CopyDesignExplicitDecisionEligibility.IsEligibleForExplicitDecision(conflicted));
    }

    [Fact]
    public void A_node_that_is_not_NeedsDecision_is_never_eligible_even_if_it_somehow_carried_the_marker_text()
    {
        var decided = new CopyDesignNode(
            "cad_p1", "fv_p1", CadDocumentType.Ipt, P("Design", "10073-P001.ipt"), "10073-P001.ipt",
            CadRelationshipKind.Component, IsManaged: true, IsVerified: true, IsResolved: true,
            CopyDesignAction.Copy, "10137-P001.ipt", P("Dest", "10137-P001.ipt"),
            new[] { CopyDesignExplicitDecisionEligibility.ClassificationUnknownReasonPrefix + " - refusing to guess. Suggested: COPY." });

        Assert.False(CopyDesignExplicitDecisionEligibility.IsEligibleForExplicitDecision(decided));
    }
}
