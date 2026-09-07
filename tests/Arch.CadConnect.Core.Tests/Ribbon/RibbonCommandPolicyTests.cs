using Arch.CadConnect.Core;
using Arch.CadConnect.Core.Connection;
using Arch.CadConnect.Core.Ribbon;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.Ribbon;

public class RibbonCommandPolicyTests
{
    private static CadDocumentContext NoDoc => CadDocumentContext.None;

    private static CadDocumentContext Doc(LocalCheckoutState state, bool saved = true) =>
        CadDocumentContext.Create(@"C:\work\proj\housing.ipt", displayNameFallback: null, isDirty: !saved)
            with { PlmIdentity = new PlmIdentity("cad_1"), CheckoutState = state, WorkspaceRoot = @"C:\work\proj" };

    // ---- Get Latest (P4B, unchanged) ----------------------------------

    [Theory]
    [InlineData(ConnectionState.SignedOut, false)]
    [InlineData(ConnectionState.Connecting, false)]
    [InlineData(ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Unauthorized, false)]
    [InlineData(ConnectionState.ServerUnavailable, false)]
    public void GetLatest_is_enabled_only_when_connected(ConnectionState state, bool expected)
    {
        Assert.Equal(expected, RibbonCommandPolicy.IsEnabled(ArchCommand.GetLatest, state, NoDoc));
    }

    // ---- P4C: checkout / check-in / undo -----------------------------

    [Fact]
    public void Checkout_enabled_only_when_connected_write_role_and_Controlled()
    {
        Assert.True(RibbonCommandPolicy.IsEnabled(ArchCommand.Checkout, ConnectionState.Connected, Doc(LocalCheckoutState.Controlled), "ENGINEER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(ArchCommand.Checkout, ConnectionState.ServerUnavailable, Doc(LocalCheckoutState.Controlled), "ENGINEER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(ArchCommand.Checkout, ConnectionState.Connected, Doc(LocalCheckoutState.Controlled), "VIEWER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(ArchCommand.Checkout, ConnectionState.Connected, Doc(LocalCheckoutState.CheckedOutByMe), "ENGINEER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(ArchCommand.Checkout, ConnectionState.Connected, Doc(LocalCheckoutState.CheckedOutByOther), "ENGINEER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(ArchCommand.Checkout, ConnectionState.Connected, Doc(LocalCheckoutState.Unverified), "ENGINEER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(ArchCommand.Checkout, ConnectionState.Connected, NoDoc, "ENGINEER"));
    }

    [Fact]
    public void CheckIn_enabled_only_when_CheckedOutByMe_write_role_and_saved()
    {
        Assert.True(RibbonCommandPolicy.IsEnabled(ArchCommand.CheckIn, ConnectionState.Connected, Doc(LocalCheckoutState.CheckedOutByMe, saved: true), "ENGINEER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(ArchCommand.CheckIn, ConnectionState.Connected, Doc(LocalCheckoutState.CheckedOutByMe, saved: false), "ENGINEER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(ArchCommand.CheckIn, ConnectionState.Connected, Doc(LocalCheckoutState.Controlled, saved: true), "ENGINEER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(ArchCommand.CheckIn, ConnectionState.Connected, Doc(LocalCheckoutState.CheckedOutByMe, saved: true), "VIEWER"));
    }

    [Fact]
    public void Undo_enabled_only_when_CheckedOutByMe_and_write_role_regardless_of_saved()
    {
        Assert.True(RibbonCommandPolicy.IsEnabled(ArchCommand.UndoCheckout, ConnectionState.Connected, Doc(LocalCheckoutState.CheckedOutByMe, saved: false), "ENGINEER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(ArchCommand.UndoCheckout, ConnectionState.Connected, Doc(LocalCheckoutState.Controlled), "ENGINEER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(ArchCommand.UndoCheckout, ConnectionState.Connected, Doc(LocalCheckoutState.CheckedOutByMe), "VIEWER"));
    }

    [Fact]
    public void Undo_stays_enabled_for_a_remembered_target_after_the_document_is_closed()
    {
        // no active managed doc (document closed) but a remembered exact target exists
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.UndoCheckout, ConnectionState.Connected, NoDoc, "ENGINEER", hasRememberedUndoTarget: true));

        // still gated: VIEWER, no connection, not-implemented -> disabled
        Assert.False(RibbonCommandPolicy.IsEnabled(
            ArchCommand.UndoCheckout, ConnectionState.Connected, NoDoc, "VIEWER", hasRememberedUndoTarget: true));
        Assert.False(RibbonCommandPolicy.IsEnabled(
            ArchCommand.UndoCheckout, ConnectionState.ServerUnavailable, NoDoc, "ENGINEER", hasRememberedUndoTarget: true));

        // a remembered target does NOT enable Checkout or Check In
        Assert.False(RibbonCommandPolicy.IsEnabled(
            ArchCommand.Checkout, ConnectionState.Connected, NoDoc, "ENGINEER", hasRememberedUndoTarget: true));
        Assert.False(RibbonCommandPolicy.IsEnabled(
            ArchCommand.CheckIn, ConnectionState.Connected, NoDoc, "ENGINEER", hasRememberedUndoTarget: true));
    }

    [Fact]
    public void VIEWER_has_every_mutation_command_disabled()
    {
        foreach (var cmd in new[] { ArchCommand.Checkout, ArchCommand.CheckIn, ArchCommand.UndoCheckout })
        {
            Assert.False(RibbonCommandPolicy.IsEnabled(cmd, ConnectionState.Connected, Doc(LocalCheckoutState.CheckedOutByMe), "VIEWER"));
        }
    }

    [Theory]
    [InlineData(ArchCommand.Status)]
    [InlineData(ArchCommand.Version)]
    [InlineData(ArchCommand.Revision)]
    [InlineData(ArchCommand.WhereUsed)]
    public void Future_scope_commands_are_always_disabled(ArchCommand command)
    {
        Assert.False(command.IsImplemented());
        Assert.False(RibbonCommandPolicy.IsEnabled(command, ConnectionState.Connected, Doc(LocalCheckoutState.Controlled), "ENGINEER"));
    }

    [Fact]
    public void Evaluate_covers_every_command_and_matches_IsEnabled()
    {
        var doc = Doc(LocalCheckoutState.CheckedOutByMe);
        var map = RibbonCommandPolicy.Evaluate(ConnectionState.Connected, doc, "ENGINEER");
        Assert.Equal(ArchCommands.All.Count, map.Count);
        foreach (var command in ArchCommands.All)
        {
            Assert.Equal(RibbonCommandPolicy.IsEnabled(command, ConnectionState.Connected, doc, "ENGINEER"), map[command]);
        }
    }
}
