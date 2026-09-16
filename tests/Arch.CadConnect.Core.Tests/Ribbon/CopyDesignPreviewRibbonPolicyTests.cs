using Arch.CadConnect.Core;
using Arch.CadConnect.Core.Connection;
using Arch.CadConnect.Core.Ribbon;

namespace Arch.CadConnect.Core.Tests.Ribbon;

/// <summary>
/// P6A "Copy Design Preview" ribbon enablement: a read-only command, so it
/// uses the SAME gate as Scan References / Reference Health (connected + a
/// saved, supported document) - NOT write-role gated, since nothing is
/// mutated. Adding it leaves every other command's enablement untouched.
/// </summary>
public class CopyDesignPreviewRibbonPolicyTests
{
    private static CadDocumentContext ScannableDoc(CadDocumentType type = CadDocumentType.Iam) =>
        CadDocumentContext.Create(@"C:\work\proj\housing" + (type == CadDocumentType.Iam ? ".iam" : ".ipt"),
            displayNameFallback: null, isDirty: false);

    [Fact]
    public void CopyDesignPreview_is_implemented_and_lives_in_the_INFORMATION_panel()
    {
        Assert.True(ArchCommand.CopyDesignPreview.IsImplemented());
        Assert.Equal("INFORMATION", ArchCommand.CopyDesignPreview.Panel());
        Assert.Contains(ArchCommand.CopyDesignPreview, ArchCommands.All);
    }

    [Theory]
    [InlineData(ConnectionState.SignedOut, false)]
    [InlineData(ConnectionState.Connecting, false)]
    [InlineData(ConnectionState.Connected, true)]
    [InlineData(ConnectionState.Unauthorized, false)]
    [InlineData(ConnectionState.ServerUnavailable, false)]
    public void CopyDesignPreview_needs_a_connection_and_a_saved_supported_doc(ConnectionState state, bool expected)
    {
        Assert.Equal(expected,
            RibbonCommandPolicy.IsEnabled(ArchCommand.CopyDesignPreview, state, ScannableDoc(), userRole: "ENGINEER"));
    }

    [Fact]
    public void CopyDesignPreview_is_available_to_a_VIEWER_since_it_is_read_only()
    {
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.CopyDesignPreview, ConnectionState.Connected, ScannableDoc(), "VIEWER"));
        Assert.True(RibbonCommandPolicy.IsEnabled(
            ArchCommand.CopyDesignPreview, ConnectionState.Connected, ScannableDoc(), userRole: null));
    }

    [Fact]
    public void CopyDesignPreview_needs_a_saved_document()
    {
        Assert.False(RibbonCommandPolicy.IsEnabled(
            ArchCommand.CopyDesignPreview, ConnectionState.Connected, CadDocumentContext.None, "ENGINEER"));
    }

    [Fact]
    public void There_is_no_Execute_Copy_Design_command_in_P6A()
    {
        Assert.DoesNotContain(ArchCommands.All, c => c.DisplayName().Contains("Execute Copy Design", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_still_covers_every_command_including_CopyDesignPreview()
    {
        var map = RibbonCommandPolicy.Evaluate(ConnectionState.Connected, ScannableDoc(), "ENGINEER");
        Assert.Equal(ArchCommands.All.Count, map.Count);
        Assert.True(map[ArchCommand.CopyDesignPreview]);
    }
}
