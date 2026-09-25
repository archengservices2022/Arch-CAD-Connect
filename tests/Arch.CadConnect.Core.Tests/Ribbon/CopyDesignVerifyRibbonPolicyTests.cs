using Arch.CadConnect.Core;
using Arch.CadConnect.Core.Connection;
using Arch.CadConnect.Core.Ribbon;

namespace Arch.CadConnect.Core.Tests.Ribbon;

/// <summary>
/// P6E-D "Verify Copy Design" ribbon enablement: like GetLatest, it needs
/// only a live connection - the operation id is typed into its own dialog,
/// never inferred from the active document, so NO active-document gate and
/// NO remembered-attempt flag (unlike Resume/Recover).
/// </summary>
public class CopyDesignVerifyRibbonPolicyTests
{
    [Fact]
    public void CopyDesignVerify_is_implemented_and_lives_in_the_INFORMATION_panel()
    {
        Assert.True(ArchCommand.CopyDesignVerify.IsImplemented());
        Assert.Equal("INFORMATION", ArchCommand.CopyDesignVerify.Panel());
        Assert.Contains(ArchCommand.CopyDesignVerify, ArchCommands.All);
    }

    [Fact]
    public void CopyDesignVerify_display_name_is_Verify_Copy_Design()
    {
        Assert.Equal("Verify Copy Design", ArchCommand.CopyDesignVerify.DisplayName());
    }

    [Fact]
    public void CopyDesignVerify_does_not_require_an_active_document()
    {
        Assert.False(ArchCommand.CopyDesignVerify.RequiresActiveDocument());
    }

    [Theory]
    [InlineData(ConnectionState.SignedOut, false)]
    [InlineData(ConnectionState.Connecting, false)]
    [InlineData(ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Unauthorized, false)]
    [InlineData(ConnectionState.ServerUnavailable, false)]
    public void CopyDesignVerify_is_enabled_exactly_when_connected(ConnectionState state, bool expected)
    {
        Assert.Equal(expected,
            RibbonCommandPolicy.IsEnabled(ArchCommand.CopyDesignVerify, state, CadDocumentContext.None, userRole: "ENGINEER"));
    }

    [Fact]
    public void CopyDesignVerify_is_enabled_with_no_active_document_at_all()
    {
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.CopyDesignVerify, ConnectionState.Connected, CadDocumentContext.None, userRole: "ENGINEER"));
    }

    [Fact]
    public void CopyDesignVerify_is_available_to_a_VIEWER_since_it_is_read_only()
    {
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.CopyDesignVerify, ConnectionState.Connected, CadDocumentContext.None, "VIEWER"));
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.CopyDesignVerify, ConnectionState.Connected, CadDocumentContext.None, userRole: null));
    }

    [Fact]
    public void CopyDesignVerify_is_NOT_gated_by_remembered_Resume_or_Recover_attempts()
    {
        // Neither having, nor lacking, a remembered Resume/Recover attempt
        // should change Verify's own enablement - it is a categorically
        // different, always-explicit-by-operationId flow.
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.CopyDesignVerify, ConnectionState.Connected, CadDocumentContext.None, "ENGINEER",
            hasRememberedUndoTarget: false, hasResumableCopyDesignAttempt: false, hasUncertainCopyDesignAttempt: false));
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.CopyDesignVerify, ConnectionState.Connected, CadDocumentContext.None, "ENGINEER",
            hasRememberedUndoTarget: true, hasResumableCopyDesignAttempt: true, hasUncertainCopyDesignAttempt: true));
    }

    [Fact]
    public void Evaluate_still_covers_every_command_including_CopyDesignVerify()
    {
        var map = RibbonCommandPolicy.Evaluate(ConnectionState.Connected, CadDocumentContext.None, "ENGINEER");
        Assert.Equal(ArchCommands.All.Count, map.Count);
        Assert.True(map[ArchCommand.CopyDesignVerify]);
    }

    [Fact]
    public void Adding_CopyDesignVerify_leaves_every_other_commands_enablement_unchanged()
    {
        // A narrow regression guard: Resume/Recover/Preview keep their exact
        // pre-P6E-D behavior after CopyDesignVerify was added to the switch.
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.CopyDesignResume, ConnectionState.Connected, CadDocumentContext.None, "ENGINEER"));
        Assert.False(RibbonCommandPolicy.IsEnabled(
            ArchCommand.CopyDesignRecover, ConnectionState.Connected, CadDocumentContext.None, "ENGINEER",
            hasUncertainCopyDesignAttempt: false));
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.CopyDesignRecover, ConnectionState.Connected, CadDocumentContext.None, "ENGINEER",
            hasUncertainCopyDesignAttempt: true));
    }
}
