using Arch.CadConnect.Core;
using Arch.CadConnect.Core.Connection;
using Arch.CadConnect.Core.Ribbon;

namespace Arch.CadConnect.Core.Tests.Ribbon;

public class RibbonCommandPolicyTests
{
    private static CadDocumentContext NoDoc => CadDocumentContext.None;

    private static CadDocumentContext SavedPart => CadDocumentContext.Create(
        @"C:\work\proj\housing.ipt", displayNameFallback: null, isDirty: false);

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

    [Fact]
    public void GetLatest_does_not_require_an_open_document()
    {
        // It resolves the root by an explicit document number, so "no document"
        // is fine as long as the connection is live.
        Assert.True(RibbonCommandPolicy.IsEnabled(ArchCommand.GetLatest, ConnectionState.Connected, NoDoc));
        Assert.True(RibbonCommandPolicy.IsEnabled(ArchCommand.GetLatest, ConnectionState.Connected, SavedPart));
    }

    [Theory]
    [InlineData(ArchCommand.Checkout)]
    [InlineData(ArchCommand.CheckIn)]
    [InlineData(ArchCommand.UndoCheckout)]
    [InlineData(ArchCommand.Status)]
    [InlineData(ArchCommand.Version)]
    [InlineData(ArchCommand.Revision)]
    [InlineData(ArchCommand.WhereUsed)]
    public void Not_yet_implemented_commands_are_always_disabled_even_when_connected(ArchCommand command)
    {
        Assert.False(command.IsImplemented());
        Assert.False(RibbonCommandPolicy.IsEnabled(command, ConnectionState.Connected, SavedPart));
    }

    [Fact]
    public void Evaluate_covers_every_command_and_matches_IsEnabled()
    {
        var map = RibbonCommandPolicy.Evaluate(ConnectionState.Connected, NoDoc);
        Assert.Equal(ArchCommands.All.Count, map.Count);
        foreach (var command in ArchCommands.All)
        {
            Assert.Equal(RibbonCommandPolicy.IsEnabled(command, ConnectionState.Connected, NoDoc), map[command]);
        }
    }
}
