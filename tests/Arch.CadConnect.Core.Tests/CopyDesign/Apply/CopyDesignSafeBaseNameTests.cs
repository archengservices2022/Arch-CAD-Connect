using Arch.CadConnect.Core.CopyDesign.Apply;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

/// <summary>P6D FINAL BLOCKER FIX (durable-resume destination-escape
///  hardening): PURE tests for <see cref="CopyDesignSafeBaseName"/> - the
///  exact attack list from the report, plus the ordinary CAD names it must
///  keep accepting. Fully unit-testable, no I/O, no COM.</summary>
public class CopyDesignSafeBaseNameTests
{
    // ---- Validate: adversarial ------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Validate_rejects_blank_or_whitespace_only(string? value)
    {
        var result = CopyDesignSafeBaseName.Validate(value);
        Assert.False(result.Safe);
    }

    [Theory]
    [InlineData(@"..\P6D-EVIL.ipt")]
    [InlineData("../P6D-EVIL.ipt")]
    [InlineData(@"..\..\outside.iam")]
    [InlineData(@"C:\Temp\evil.ipt")]
    [InlineData(@"\\server\share\evil.idw")]
    [InlineData(@"subdir\evil.ipt")]
    [InlineData("subdir/evil.ipt")]
    public void Validate_rejects_the_report_attack_list(string attack)
    {
        var result = CopyDesignSafeBaseName.Validate(attack);
        Assert.False(result.Safe, $"expected \"{attack}\" to be rejected");
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void Validate_rejects_dot_and_dotdot_as_the_whole_name(string value)
    {
        Assert.False(CopyDesignSafeBaseName.Validate(value).Safe);
    }

    [Fact]
    public void Validate_rejects_a_bare_drive_relative_prefix_with_no_following_separator()
    {
        Assert.False(CopyDesignSafeBaseName.Validate("C:evil.ipt").Safe);
    }

    [Theory]
    [InlineData("evil.")]
    [InlineData("evil ")]
    [InlineData("P6DNEW-ROOT.iam.")]
    [InlineData("P6DNEW-ROOT.iam ")]
    public void Validate_rejects_trailing_dot_or_trailing_space(string value)
    {
        Assert.False(CopyDesignSafeBaseName.Validate(value).Safe);
    }

    [Theory]
    [InlineData("evil<1>.ipt")]
    [InlineData("evil\".ipt")]
    [InlineData("evil|1.ipt")]
    [InlineData("evil?.ipt")]
    [InlineData("evil*.ipt")]
    [InlineData("evil:stream.ipt")]
    public void Validate_rejects_windows_invalid_characters(string value)
    {
        Assert.False(CopyDesignSafeBaseName.Validate(value).Safe);
    }

    [Fact]
    public void Validate_rejects_control_characters()
    {
        Assert.False(CopyDesignSafeBaseName.Validate("evil\u0000.ipt").Safe);
        Assert.False(CopyDesignSafeBaseName.Validate("evil\u0007.ipt").Safe);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("CON.ipt")]
    [InlineData("con.ipt")]
    [InlineData("Con.IAM")]
    [InlineData("PRN.idw")]
    [InlineData("AUX.ipt")]
    [InlineData("NUL.ipt")]
    [InlineData("COM1.iam")]
    [InlineData("com1.iam")]
    [InlineData("COM9.ipt")]
    [InlineData("LPT1.idw")]
    [InlineData("LPT9.idw")]
    public void Validate_rejects_reserved_windows_device_names_case_insensitively_with_or_without_extension(string value)
    {
        Assert.False(CopyDesignSafeBaseName.Validate(value).Safe);
    }

    [Theory]
    [InlineData("MyCOM1.ipt")]
    [InlineData("CONFIG.ipt")]
    [InlineData("NULLABLE.ipt")]
    public void Validate_does_not_reject_a_device_name_as_a_mere_substring(string value)
    {
        Assert.True(CopyDesignSafeBaseName.Validate(value).Safe);
    }

    // ---- Validate: must keep accepting ordinary CAD names ----------------

    [Theory]
    [InlineData("P6DNEW-ROOT.iam")]
    [InlineData("1001.idw")]
    [InlineData("A B-C_01.ipt")]
    [InlineData("P6DNEW-REAL-PART-A.ipt")]
    [InlineData("P6D-NESTED-SUB-A.iam")]
    [InlineData("Bracket (Rev2).ipt")]
    [InlineData("weld_assembly-01.dwg")]
    public void Validate_accepts_ordinary_realistic_cad_names(string value)
    {
        var result = CopyDesignSafeBaseName.Validate(value);
        Assert.True(result.Safe, result.Reason);
    }

    // ---- TryResolveContained ----------------------------------------------

    [Fact]
    public void TryResolveContained_accepts_a_safe_name_directly_inside_the_destination_folder()
    {
        var result = CopyDesignSafeBaseName.TryResolveContained(@"C:\Approved", "P6DNEW-ROOT.iam");

        Assert.True(result.Safe, result.FailureReason);
        Assert.Equal(@"C:\Approved\P6DNEW-ROOT.iam", result.ResolvedPath);
    }

    [Theory]
    [InlineData(@"..\P6D-EVIL.ipt")]
    [InlineData(@"C:\Temp\evil.ipt")]
    [InlineData(@"\\server\share\evil.ipt")]
    [InlineData(@"subdir\evil.ipt")]
    [InlineData("subdir/evil.ipt")]
    public void TryResolveContained_rejects_every_attack_before_ever_producing_a_path(string attack)
    {
        var result = CopyDesignSafeBaseName.TryResolveContained(@"C:\Approved", attack);

        Assert.False(result.Safe);
        Assert.Null(result.ResolvedPath);
    }

    // required test: destination "C:\Approved" does not accept a path under "C:\Approved2"
    [Fact]
    public void TryResolveContained_uses_exact_parent_directory_equality_not_prefix_matching()
    {
        // Regression test for the naive "StartsWith" containment bug:
        // "C:\Approved2\ok.ipt".StartsWith("C:\Approved") is true, but its
        // parent ("C:\Approved2") is NOT EQUAL to "C:\Approved" - the real
        // containment check compares the resolved PARENT DIRECTORY by exact
        // equality, so a sibling folder whose name happens to start with
        // the same characters as the real root can never be confused with it.
        var resolvedForRealRoot = CopyDesignSafeBaseName.TryResolveContained(@"C:\Approved", "ok.ipt");
        Assert.True(resolvedForRealRoot.Safe, resolvedForRealRoot.FailureReason);
        Assert.Equal(@"C:\Approved", Path.GetDirectoryName(resolvedForRealRoot.ResolvedPath));

        var resolvedForSiblingRoot = CopyDesignSafeBaseName.TryResolveContained(@"C:\Approved2", "ok.ipt");
        Assert.True(resolvedForSiblingRoot.Safe, resolvedForSiblingRoot.FailureReason);
        Assert.Equal(@"C:\Approved2", Path.GetDirectoryName(resolvedForSiblingRoot.ResolvedPath));

        // Two DIFFERENT roots must never resolve to the same file, even
        // though one root string starts with the other.
        Assert.NotEqual(resolvedForRealRoot.ResolvedPath, resolvedForSiblingRoot.ResolvedPath);
    }

    [Fact]
    public void TryResolveContained_rejects_a_relative_destination_folder()
    {
        var result = CopyDesignSafeBaseName.TryResolveContained("relative\\folder", "ok.ipt");
        Assert.False(result.Safe);
    }

    [Fact]
    public void TryResolveContained_is_case_insensitive_for_the_root_comparison()
    {
        // NTFS is case-insensitive - a destination folder typed with
        // different casing than its canonical form must not spuriously fail.
        var result = CopyDesignSafeBaseName.TryResolveContained(@"c:\approved", "ok.ipt");
        Assert.True(result.Safe, result.FailureReason);
    }
}
