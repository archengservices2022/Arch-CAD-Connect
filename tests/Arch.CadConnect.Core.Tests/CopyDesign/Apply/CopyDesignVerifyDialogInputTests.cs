using Arch.CadConnect.Core.CopyDesign.Apply;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

/// <summary>
/// P6E-D: <see cref="CopyDesignVerifyDialogInput.Validate"/> - the pure,
/// fail-before-HTTP/COM gate for the Verify Copy Design dialog's three
/// inputs. Uses an injected directory-existence probe so no real filesystem
/// access is needed.
/// </summary>
public class CopyDesignVerifyDialogInputTests
{
    private static readonly Func<string, bool> AllExist = _ => true;
    private static readonly Func<string, bool> NoneExist = _ => false;

    [Fact]
    public void Blank_operation_id_is_rejected()
    {
        var result = CopyDesignVerifyDialogInput.Validate("", @"C:\ws\source", @"C:\ws\dest", AllExist);
        Assert.False(result.IsValid);
        Assert.Contains("operation id", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Whitespace_only_operation_id_is_rejected()
    {
        var result = CopyDesignVerifyDialogInput.Validate("   ", @"C:\ws\source", @"C:\ws\dest", AllExist);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Null_operation_id_is_rejected()
    {
        var result = CopyDesignVerifyDialogInput.Validate(null, @"C:\ws\source", @"C:\ws\dest", AllExist);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Missing_source_workspace_folder_text_is_rejected()
    {
        var result = CopyDesignVerifyDialogInput.Validate("op-1", "", @"C:\ws\dest", AllExist);
        Assert.False(result.IsValid);
        Assert.Contains("source workspace", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_fully_qualified_source_workspace_path_is_rejected()
    {
        var result = CopyDesignVerifyDialogInput.Validate("op-1", "relative\\path", @"C:\ws\dest", AllExist);
        Assert.False(result.IsValid);
        Assert.Contains("full path", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_source_workspace_folder_that_does_not_exist_is_rejected()
    {
        var result = CopyDesignVerifyDialogInput.Validate("op-1", @"C:\ws\source", @"C:\ws\dest", NoneExist);
        Assert.False(result.IsValid);
        Assert.Contains("does not exist", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_destination_folder_text_is_rejected()
    {
        var result = CopyDesignVerifyDialogInput.Validate("op-1", @"C:\ws\source", "", AllExist);
        Assert.False(result.IsValid);
        Assert.Contains("destination", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_fully_qualified_destination_path_is_rejected()
    {
        var result = CopyDesignVerifyDialogInput.Validate("op-1", @"C:\ws\source", "relative\\dest", AllExist);
        Assert.False(result.IsValid);
        Assert.Contains("full path", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_destination_folder_that_does_not_exist_is_rejected()
    {
        var result = CopyDesignVerifyDialogInput.Validate(
            "op-1", @"C:\ws\source", @"C:\ws\dest", path => path == @"C:\ws\source");
        Assert.False(result.IsValid);
        Assert.Contains("destination", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not exist", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Valid_trimmed_paths_are_accepted_and_returned_trimmed()
    {
        var result = CopyDesignVerifyDialogInput.Validate(
            "  op-1  ", @"  C:\ws\source  ", @"  C:\ws\dest  ", AllExist);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("op-1", result.OperationId);
        Assert.Equal(@"C:\ws\source", result.SourceWorkspaceRoot);
        Assert.Equal(@"C:\ws\dest", result.DestinationFolder);
    }

    [Fact]
    public void Never_infers_an_operation_id_when_the_field_is_blank_even_with_valid_folders()
    {
        // Explicit guard for "never guess/find operation ids from the
        // filesystem" - a blank id is rejected regardless of how valid the
        // folders are; nothing here ever substitutes a discovered id.
        var result = CopyDesignVerifyDialogInput.Validate("", @"C:\ws\source", @"C:\ws\dest", AllExist);
        Assert.False(result.IsValid);
        Assert.Equal(string.Empty, result.OperationId);
    }
}
