using Arch.CadConnect.Core.References;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.References.Repair;

/// <summary>
/// FINDING 1: the mutation boundary compares the fresh manifest AND the fresh
/// authoritative server checkout to an IMMUTABLE snapshot of the identity the
/// user's confirmed authorization was verified against. A manifest reloaded at
/// the boundary can never redefine the expected checkout id / base
/// fileVersionId; any drift => no mutation.
/// </summary>
public class RepairAuthorizationSnapshotTests
{
    private static readonly RepairAuthorizationSnapshot Snap =
        new("cad_parent", "co_1", "fv_base_1");

    /// <summary>ROUND 4 (Codex HIGH - exact checkout/base/local-parent binding):
    ///  the entry's OWN currently-pinned local FileVersion (<paramref name="localFv"/>)
    ///  defaults to the checkout's base - a fresh local FileVersion that has
    ///  drifted away from the checkout base must be passed explicitly.</summary>
    private static WorkspaceManifestEntry Entry(
        string cad = "cad_parent", string checkoutId = "co_1", string baseFv = "fv_base_1",
        string? localFv = null,
        WorkspaceManifestEntryState state = WorkspaceManifestEntryState.Verified) => new()
    {
        CadDocumentId = cad,
        FileVersionId = localFv ?? baseFv,
        State = state,
        Checkout = new WorkspaceCheckoutBinding { CheckoutId = checkoutId, BaseFileVersionId = baseFv },
    };

    private static ServerCheckoutStatus Server(
        string checkoutId = "co_1", string baseFv = "fv_base_1",
        ServerCheckoutState state = ServerCheckoutState.Mine) =>
        new(state, CheckoutId: checkoutId, BaseFileVersionId: baseFv);

    [Fact]
    public void IsComplete_requires_every_field_to_be_a_clean_stable_identifier()
    {
        Assert.True(Snap.IsComplete);
        Assert.False(new RepairAuthorizationSnapshot("", "co_1", "fv_base_1").IsComplete);
        Assert.False(new RepairAuthorizationSnapshot("cad_parent", "  ", "fv_base_1").IsComplete);
        Assert.False(new RepairAuthorizationSnapshot("cad_parent", "co_1", " fv_base_1 ").IsComplete);
    }

    [Fact]
    public void An_unchanged_exact_manifest_and_server_state_matches_the_snapshot()
    {
        Assert.True(Snap.MatchesFinalState(Entry(), Server()));
    }

    [Fact]
    public void A_manifest_cadDocumentId_change_after_revalidation_does_not_match()
    {
        Assert.False(Snap.MatchesFinalState(Entry(cad: "cad_OTHER"), Server()));
    }

    [Fact]
    public void A_manifest_checkoutId_change_does_not_match()
    {
        Assert.False(Snap.MatchesFinalState(Entry(checkoutId: "co_2"), Server()));
    }

    [Fact]
    public void A_manifest_baseFileVersionId_change_does_not_match()
    {
        Assert.False(Snap.MatchesFinalState(Entry(baseFv: "fv_base_2"), Server()));
    }

    [Fact]
    public void A_manifest_AND_server_changing_TOGETHER_to_another_otherwise_valid_checkout_does_not_match()
    {
        // Both drift consistently to a different, internally-consistent checkout
        // identity. The immutable snapshot still rejects it.
        Assert.False(Snap.MatchesFinalState(
            Entry(checkoutId: "co_99", baseFv: "fv_base_99"),
            Server(checkoutId: "co_99", baseFv: "fv_base_99")));
    }

    [Fact]
    public void A_server_that_is_no_longer_Mine_does_not_match()
    {
        Assert.False(Snap.MatchesFinalState(Entry(), Server(state: ServerCheckoutState.Available)));
        Assert.False(Snap.MatchesFinalState(Entry(), Server(state: ServerCheckoutState.Locked)));
    }

    [Fact]
    public void A_server_checkoutId_or_base_change_does_not_match()
    {
        Assert.False(Snap.MatchesFinalState(Entry(), Server(checkoutId: "co_2")));
        Assert.False(Snap.MatchesFinalState(Entry(), Server(baseFv: "fv_base_2")));
    }

    [Fact]
    public void A_missing_manifest_entry_or_missing_server_status_does_not_match()
    {
        Assert.False(Snap.MatchesFinalState(null, Server()));
        Assert.False(Snap.MatchesFinalState(Entry(), null));
    }

    [Fact]
    public void An_unverified_manifest_entry_does_not_match()
    {
        Assert.False(Snap.MatchesFinalState(Entry(state: WorkspaceManifestEntryState.Unverified), Server()));
    }

    [Fact]
    public void A_manifest_entry_with_no_checkout_binding_does_not_match()
    {
        var noBinding = Entry() with { Checkout = null };
        Assert.False(Snap.MatchesFinalState(noBinding, Server()));
    }

    [Fact]
    public void An_incomplete_snapshot_never_matches_anything()
    {
        var bad = new RepairAuthorizationSnapshot("cad_parent", "", "fv_base_1");
        Assert.False(bad.MatchesFinalState(Entry(), Server()));
    }

    // ---- ROUND 4 (Codex HIGH): exact checkout/base/local-parent binding ----

    [Fact]
    public void A_fresh_local_FileVersion_that_drifted_from_the_checkout_base_does_not_match_even_though_the_checkout_marker_still_agrees()
    {
        // The checkout marker (checkoutId + its OWN recorded base) still
        // agrees exactly with the snapshot, but the manifest entry's OWN
        // currently-pinned FileVersionId has moved on (e.g. a concurrent
        // Check-In elsewhere rebound the local copy) - this must NOT match.
        Assert.False(Snap.MatchesFinalState(Entry(localFv: "fv_OTHER"), Server()));
    }

    [Fact]
    public void A_fresh_local_FileVersion_equal_to_a_DIFFERENT_checkout_base_does_not_match()
    {
        // Both the checkout's base AND the local FileVersion drift together to
        // another value - still must not match the immutable snapshot.
        Assert.False(Snap.MatchesFinalState(Entry(baseFv: "fv_base_2", localFv: "fv_base_2"), Server()));
    }

    [Fact]
    public void An_unchanged_local_FileVersion_exactly_equal_to_the_expected_base_matches()
    {
        // Explicit positive control for the new invariant, independent of the
        // Entry() default.
        Assert.True(Snap.MatchesFinalState(Entry(localFv: "fv_base_1"), Server()));
    }
}
