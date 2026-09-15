using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References.Repair;

/// <summary>
/// ROUND 4 (Codex HIGH - final authoritative latest-version drift): proves the
/// pure gate that must pass WHILE the protected target lease is held,
/// immediately before Inventor mutation - a fresh authoritative latest-version
/// lookup for the target's cadDocumentId must be EXACTLY the confirmed/prepared
/// target (fileVersionId + size + SHA-256), or the repair aborts before COM.
/// </summary>
public class FinalTargetFreshnessGateTests
{
    private const string ExpectedFv = "fv_v4";
    private const long ExpectedSize = 97280;
    private static readonly string ExpectedSha = new('a', 64);

    private static LatestVersionResult Found(string fv, long size, string sha) =>
        LatestVersionResult.Found("cad_a", fv, size, sha);

    private static bool StillMatches(LatestVersionResult? fresh) =>
        FinalTargetFreshnessGate.StillMatches(fresh, ExpectedFv, ExpectedSize, ExpectedSha);

    [Fact]
    public void The_same_target_remaining_the_authoritative_latest_is_allowed()
    {
        Assert.True(StillMatches(Found(ExpectedFv, ExpectedSize, ExpectedSha)));
    }

    [Fact]
    public void A_changed_authoritative_latest_FileVersionId_is_rejected()
    {
        // v5 became authoritative after v4 was confirmed/revalidated.
        Assert.False(StillMatches(Found("fv_v5", ExpectedSize, ExpectedSha)));
    }

    [Fact]
    public void The_same_FileVersionId_but_a_different_size_is_rejected()
    {
        Assert.False(StillMatches(Found(ExpectedFv, ExpectedSize + 1, ExpectedSha)));
    }

    [Fact]
    public void The_same_FileVersionId_and_size_but_a_different_SHA_is_rejected()
    {
        Assert.False(StillMatches(Found(ExpectedFv, ExpectedSize, new string('b', 64))));
    }

    [Theory]
    [InlineData(LatestVersionOutcome.ServerUnavailable)]
    [InlineData(LatestVersionOutcome.AuthenticationFailed)]
    [InlineData(LatestVersionOutcome.LookupUnavailable)]
    [InlineData(LatestVersionOutcome.DocumentNotRecognized)]
    [InlineData(LatestVersionOutcome.MalformedResponse)]
    [InlineData(LatestVersionOutcome.NotAttempted)]
    public void Any_non_Found_authoritative_lookup_outcome_is_rejected(LatestVersionOutcome outcome)
    {
        Assert.False(StillMatches(LatestVersionResult.Failure("cad_a", outcome)));
    }

    [Fact]
    public void A_null_lookup_result_is_rejected()
    {
        Assert.False(StillMatches(null));
    }

    [Fact]
    public void A_Found_result_without_canonical_integrity_metadata_is_rejected()
    {
        // Found, but the server omitted usable size/SHA-256 (defaults -1 / "").
        Assert.False(StillMatches(LatestVersionResult.Found("cad_a", ExpectedFv)));
    }

    [Fact]
    public void An_incanonical_expected_SHA_never_matches_even_a_byte_identical_fresh_value()
    {
        // Defensive: a caller passing a non-canonical "expected" value (e.g. an
        // empty string) must never be treated as trivially satisfied.
        Assert.False(FinalTargetFreshnessGate.StillMatches(
            Found(ExpectedFv, ExpectedSize, ExpectedSha), ExpectedFv, ExpectedSize, expectedSha256: ""));
    }

    [Fact]
    public void A_blank_expected_FileVersionId_never_matches()
    {
        Assert.False(FinalTargetFreshnessGate.StillMatches(
            Found(ExpectedFv, ExpectedSize, ExpectedSha), "", ExpectedSize, ExpectedSha));
    }
}
