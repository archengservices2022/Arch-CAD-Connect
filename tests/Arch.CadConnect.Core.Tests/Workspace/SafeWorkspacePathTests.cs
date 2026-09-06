using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.Workspace;

public class SafeWorkspacePathTests
{
    private static readonly string Root =
        OperatingSystem.IsWindows() ? @"C:\Work\ws" : "/work/ws";

    [Theory]
    [InlineData("assembly.iam")]
    [InlineData("Part_1.ipt")]
    [InlineData("10073-MFD-AS210.iam")]
    [InlineData("a.b.c.dwg")]
    public void ValidateShape_accepts_a_plain_flat_filename(string value)
    {
        SafeWorkspacePath.ValidateShape(value);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("../escape.ipt", "dot segment")]
    [InlineData("..\\escape.ipt", "backslash")]
    [InlineData("/etc/passwd", "absolute / home / UNC")]
    [InlineData("~/thing.ipt", "absolute / home / UNC")]
    [InlineData("C:\\abs.ipt", "backslash")]
    [InlineData("bad:name.ipt", "Windows-invalid character")]
    [InlineData("name?.ipt", "Windows-invalid character")]
    [InlineData("trailing .ipt", "bad segment")]
    [InlineData("trailingdot.", "trailing dot/space")]
    [InlineData("CON", "reserved Windows device name")]
    [InlineData("con.txt", "reserved Windows device name")]
    [InlineData("LPT1.iam", "reserved Windows device name")]
    [InlineData(".hidden", "bad segment")]
    [InlineData("_leading.ipt", "bad segment")]
    [InlineData("a b.ipt", "bad segment")]
    public void ValidateShape_rejects_unsafe_shapes(string value, string reasonFragment)
    {
        var ex = Assert.Throws<UnsafeWorkspacePathException>(() => SafeWorkspacePath.ValidateShape(value));
        Assert.Contains(reasonFragment, ex.Message);
    }

    [Fact]
    public void ValidateShape_rejects_control_characters()
    {
        var ex = Assert.Throws<UnsafeWorkspacePathException>(
            () => SafeWorkspacePath.ValidateShape("a\tb.ipt"));
        Assert.Contains("control character", ex.Message);
    }

    [Fact]
    public void ResolveWithinRoot_returns_the_path_under_the_root_for_a_flat_name()
    {
        var resolved = SafeWorkspacePath.ResolveWithinRoot(Root, "part.ipt");
        Assert.Equal(Path.Combine(Path.GetFullPath(Root), "part.ipt"), resolved);
    }

    [Fact]
    public void ResolveWithinRoot_refuses_a_nested_path_in_this_milestone()
    {
        var ex = Assert.Throws<UnsafeWorkspacePathException>(
            () => SafeWorkspacePath.ResolveWithinRoot(Root, "sub/part.ipt"));
        Assert.Contains("nested paths are not supported", ex.Message);
    }

    [Theory]
    [InlineData("../outside.ipt")]
    [InlineData("..\\outside.ipt")]
    public void ResolveWithinRoot_refuses_traversal(string relative)
    {
        Assert.Throws<UnsafeWorkspacePathException>(
            () => SafeWorkspacePath.ResolveWithinRoot(Root, relative));
    }

    [Fact]
    public void ResolveWithinRoot_requires_an_absolute_root()
    {
        Assert.Throws<WorkspaceRootException>(
            () => SafeWorkspacePath.ResolveWithinRoot("relative/root", "part.ipt"));
    }

    [Fact]
    public void RequireAbsoluteRoot_rejects_relative_and_empty()
    {
        Assert.Throws<WorkspaceRootException>(() => SafeWorkspacePath.RequireAbsoluteRoot(null));
        Assert.Throws<WorkspaceRootException>(() => SafeWorkspacePath.RequireAbsoluteRoot(""));
        Assert.Throws<WorkspaceRootException>(() => SafeWorkspacePath.RequireAbsoluteRoot("ws"));
        SafeWorkspacePath.RequireAbsoluteRoot(Root); // ok
    }
}
