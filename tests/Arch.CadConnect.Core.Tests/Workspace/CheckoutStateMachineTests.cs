using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.Workspace;

public class CheckoutStateMachineTests
{
    private static WorkspaceManifestEntry Entry(
        WorkspaceManifestEntryState state = WorkspaceManifestEntryState.Verified,
        bool checkedOut = false) => new()
    {
        RelativePath = "part.ipt",
        CadDocumentId = "cad_1",
        FileVersionId = "fv_1",
        VersionNumber = 3,
        Checksum = "abc",
        FileSize = 100,
        State = state,
        Checkout = checkedOut
            ? new WorkspaceCheckoutBinding { CheckoutId = "co", BaseFileVersionId = "fv_1", BaseVersionNumber = 3, BaseChecksum = "abc", BaseFileSize = 100 }
            : null,
    };

    [Fact]
    public void No_entry_is_Unmanaged()
        => Assert.Equal(LocalCheckoutState.Unmanaged, CheckoutStateMachine.Evaluate(null, fileExists: true));

    [Fact]
    public void Verified_entry_no_marker_present_file_is_Controlled()
        => Assert.Equal(LocalCheckoutState.Controlled, CheckoutStateMachine.Evaluate(Entry(), fileExists: true));

    [Fact]
    public void Verified_entry_but_missing_file_is_Unverified()
        => Assert.Equal(LocalCheckoutState.Unverified, CheckoutStateMachine.Evaluate(Entry(), fileExists: false));

    [Fact]
    public void Local_checkout_marker_is_CheckedOutByMe()
        => Assert.Equal(LocalCheckoutState.CheckedOutByMe,
            CheckoutStateMachine.Evaluate(Entry(checkedOut: true), fileExists: true));

    [Fact]
    public void Unverified_entry_is_Unverified()
        => Assert.Equal(LocalCheckoutState.Unverified,
            CheckoutStateMachine.Evaluate(Entry(WorkspaceManifestEntryState.Unverified), fileExists: true));

    [Fact]
    public void Server_locked_wins_over_a_stale_local_marker()
        => Assert.Equal(LocalCheckoutState.CheckedOutByOther,
            CheckoutStateMachine.Evaluate(Entry(checkedOut: true), fileExists: true,
                new ServerCheckoutStatus(ServerCheckoutState.Locked, "Someone Else", "s@x.com")));

    [Fact]
    public void Server_available_wins_over_a_stale_local_marker_and_reports_Controlled()
        => Assert.Equal(LocalCheckoutState.Controlled,
            CheckoutStateMachine.Evaluate(Entry(checkedOut: true), fileExists: true,
                new ServerCheckoutStatus(ServerCheckoutState.Available)));

    [Fact]
    public void Server_mine_confirms_CheckedOutByMe_even_without_a_local_marker()
        => Assert.Equal(LocalCheckoutState.CheckedOutByMe,
            CheckoutStateMachine.Evaluate(Entry(), fileExists: true, new ServerCheckoutStatus(ServerCheckoutState.Mine)));

    // ---- command guards ---------------------------------------------

    [Theory]
    [InlineData("ENGINEER", true)]
    [InlineData("ADMIN", true)]
    [InlineData("MANAGER", true)]
    [InlineData("VIEWER", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Write_role_excludes_only_VIEWER(string? role, bool expected)
        => Assert.Equal(expected, CheckoutStateMachine.IsWriteRole(role));

    [Fact]
    public void CanCheckout_only_from_Controlled_with_a_write_role()
    {
        Assert.True(CheckoutStateMachine.CanCheckout(LocalCheckoutState.Controlled, "ENGINEER"));
        Assert.False(CheckoutStateMachine.CanCheckout(LocalCheckoutState.Controlled, "VIEWER"));
        Assert.False(CheckoutStateMachine.CanCheckout(LocalCheckoutState.CheckedOutByMe, "ENGINEER"));
        Assert.False(CheckoutStateMachine.CanCheckout(LocalCheckoutState.Unverified, "ENGINEER"));
        Assert.False(CheckoutStateMachine.CanCheckout(LocalCheckoutState.Unmanaged, "ENGINEER"));
    }

    [Fact]
    public void CanCheckIn_needs_CheckedOutByMe_write_role_and_a_saved_document()
    {
        Assert.True(CheckoutStateMachine.CanCheckIn(LocalCheckoutState.CheckedOutByMe, "ENGINEER", documentSaved: true));
        Assert.False(CheckoutStateMachine.CanCheckIn(LocalCheckoutState.CheckedOutByMe, "ENGINEER", documentSaved: false));
        Assert.False(CheckoutStateMachine.CanCheckIn(LocalCheckoutState.Controlled, "ENGINEER", documentSaved: true));
        Assert.False(CheckoutStateMachine.CanCheckIn(LocalCheckoutState.CheckedOutByMe, "VIEWER", documentSaved: true));
    }

    [Fact]
    public void CanUndo_needs_CheckedOutByMe_and_a_write_role_regardless_of_saved()
    {
        Assert.True(CheckoutStateMachine.CanUndo(LocalCheckoutState.CheckedOutByMe, "ENGINEER"));
        Assert.False(CheckoutStateMachine.CanUndo(LocalCheckoutState.Controlled, "ENGINEER"));
        Assert.False(CheckoutStateMachine.CanUndo(LocalCheckoutState.CheckedOutByMe, "VIEWER"));
    }
}
