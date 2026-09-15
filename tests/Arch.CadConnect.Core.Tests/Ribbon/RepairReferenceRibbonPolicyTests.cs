using Arch.CadConnect.Core;
using Arch.CadConnect.Core.Connection;
using Arch.CadConnect.Core.Ribbon;

namespace Arch.CadConnect.Core.Tests.Ribbon;

/// <summary>
/// P5C "Repair Reference" ribbon enablement: same document gate as Reference
/// Health, but - because it mutates the in-memory reference graph - it is
/// write-role gated. Adding it leaves P5A / P5B enablement untouched.
/// </summary>
public class RepairReferenceRibbonPolicyTests
{
    private static CadDocumentContext ScannableDoc(CadDocumentType type = CadDocumentType.Iam) =>
        CadDocumentContext.Create(@"C:\work\proj\housing" + (type == CadDocumentType.Iam ? ".iam" : ".ipt"),
            displayNameFallback: null, isDirty: false);

    [Fact]
    public void RepairReference_is_implemented_and_lives_in_the_INFORMATION_panel()
    {
        Assert.True(ArchCommand.RepairReference.IsImplemented());
        Assert.Equal("INFORMATION", ArchCommand.RepairReference.Panel());
        Assert.Contains(ArchCommand.RepairReference, ArchCommands.All);
    }

    [Theory]
    [InlineData(ConnectionState.SignedOut, false)]
    [InlineData(ConnectionState.Connecting, false)]
    [InlineData(ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Unauthorized, false)]
    [InlineData(ConnectionState.ServerUnavailable, false)]
    public void RepairReference_needs_a_connection_and_a_saved_supported_doc(ConnectionState state, bool expected)
    {
        Assert.Equal(expected,
            RibbonCommandPolicy.IsEnabled(ArchCommand.RepairReference, state, ScannableDoc(), userRole: "ENGINEER"));
    }

    [Fact]
    public void RepairReference_is_write_role_gated_unlike_the_read_only_P5_commands()
    {
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.RepairReference, ConnectionState.Connected, ScannableDoc(), "ENGINEER"));

        Assert.False(RibbonCommandPolicy.IsEnabled(
            ArchCommand.RepairReference, ConnectionState.Connected, ScannableDoc(), "VIEWER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(
            ArchCommand.RepairReference, ConnectionState.Connected, ScannableDoc(), userRole: null));

        // the read-only P5 commands stay available to a VIEWER
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.ScanReferences, ConnectionState.Connected, ScannableDoc(), "VIEWER"));
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.ReferenceHealth, ConnectionState.Connected, ScannableDoc(), "VIEWER"));
    }

    [Fact]
    public void RepairReference_needs_a_saved_document()
    {
        Assert.False(RibbonCommandPolicy.IsEnabled(
            ArchCommand.RepairReference, ConnectionState.Connected, CadDocumentContext.None, "ENGINEER"));
    }

    [Fact]
    public void Evaluate_still_covers_every_command_including_RepairReference()
    {
        var map = RibbonCommandPolicy.Evaluate(ConnectionState.Connected, ScannableDoc(), "ENGINEER");
        Assert.Equal(ArchCommands.All.Count, map.Count);
        Assert.True(map[ArchCommand.RepairReference]);
    }
}
