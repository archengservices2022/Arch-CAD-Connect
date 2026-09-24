using Arch.CadConnect.Core.CopyDesign.Apply;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

/// <summary>P6D LIVE BLOCKER fix ("drawing remains Dirty after source IDW
///  is closed"): the PURE decision logic distinguishing a genuine
///  pre-existing unsaved user edit (MUST block) from Inventor's own
///  load-time side effect on a document THIS operation itself opened from
///  provably-untouched, authoritative bytes (may proceed) - fully
///  unit-testable without any COM/live Inventor session, since
///  <c>InventorCopyDesignPhysicalCopier</c> only ever gathers facts
///  (Dirty, was-already-open, a fresh on-disk hash) and hands them to this
///  class.</summary>
public class CopyDesignSourceDirtyProvenanceGuardTests
{
    private const string HashA = "1111111111111111111111111111111111111111111111111111111111111a";
    private const string HashB = "2222222222222222222222222222222222222222222222222222222222222b";

    [Fact]
    public void A_clean_document_always_proceeds_regardless_of_open_provenance()
    {
        var result = CopyDesignSourceDirtyProvenanceGuard.Evaluate(
            "P6D-REAL-ROOT.idw", wasAlreadyOpen: false, dirty: false, postOpenSha256: null, sourceSha256BeforeOperation: HashA);

        Assert.True(result.Safe);
        Assert.Null(result.FailureReason);
    }

    // required test: source opened by user + Dirty blocks (pre-opened dirty drawing blocks)
    [Fact]
    public void A_document_already_open_before_this_operation_and_Dirty_blocks_unconditionally()
    {
        var result = CopyDesignSourceDirtyProvenanceGuard.Evaluate(
            "P6D-REAL-ROOT.idw", wasAlreadyOpen: true, dirty: true, postOpenSha256: HashA, sourceSha256BeforeOperation: HashA);

        Assert.False(result.Safe);
        Assert.Contains("P6D-REAL-ROOT.idw", result.FailureReason);
        Assert.Contains("Dirty", result.FailureReason);
    }

    [Fact]
    public void A_document_already_open_and_Dirty_blocks_EVEN_when_the_on_disk_bytes_still_match_the_baseline()
    {
        // Proves the relaxation NEVER applies to an already-open document,
        // even if the hash happens to match - open-provenance alone is
        // decisive, exactly as before this fix.
        var result = CopyDesignSourceDirtyProvenanceGuard.Evaluate(
            "P6D-REAL-ROOT.idw", wasAlreadyOpen: true, dirty: true, postOpenSha256: HashA, sourceSha256BeforeOperation: HashA);

        Assert.False(result.Safe);
    }

    // required test: P6D-opened clean source that Inventor reports Dirty
    // solely after opening is handled safely ONLY if provenance/integrity
    // conditions prove it.
    [Fact]
    public void A_document_this_operation_opened_itself_that_is_Dirty_proceeds_when_on_disk_bytes_match_the_operations_own_baseline()
    {
        var result = CopyDesignSourceDirtyProvenanceGuard.Evaluate(
            "P6D-REAL-ROOT.idw", wasAlreadyOpen: false, dirty: true, postOpenSha256: HashA, sourceSha256BeforeOperation: HashA);

        Assert.True(result.Safe, result.FailureReason);
    }

    // required test: source bytes changing still blocks
    [Fact]
    public void A_document_this_operation_opened_itself_that_is_Dirty_still_blocks_when_the_on_disk_bytes_no_longer_match()
    {
        var result = CopyDesignSourceDirtyProvenanceGuard.Evaluate(
            "P6D-REAL-ROOT.idw", wasAlreadyOpen: false, dirty: true, postOpenSha256: HashB, sourceSha256BeforeOperation: HashA);

        Assert.False(result.Safe);
        Assert.Contains("no longer match", result.FailureReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_document_this_operation_opened_itself_that_is_Dirty_blocks_when_the_post_open_hash_could_not_be_computed(string? unavailable)
    {
        // An unprovable on-disk state is NEVER silently treated as a pass -
        // fails closed exactly like a proven mismatch.
        var result = CopyDesignSourceDirtyProvenanceGuard.Evaluate(
            "P6D-REAL-ROOT.idw", wasAlreadyOpen: false, dirty: true, postOpenSha256: unavailable, sourceSha256BeforeOperation: HashA);

        Assert.False(result.Safe);
        Assert.Contains("re-verify", result.FailureReason);
    }

    [Fact]
    public void The_hash_comparison_is_case_insensitive()
    {
        var result = CopyDesignSourceDirtyProvenanceGuard.Evaluate(
            "P6D-REAL-ROOT.idw", wasAlreadyOpen: false, dirty: true,
            postOpenSha256: HashA.ToUpperInvariant(), sourceSha256BeforeOperation: HashA);

        Assert.True(result.Safe, result.FailureReason);
    }
}
