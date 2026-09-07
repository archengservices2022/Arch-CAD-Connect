using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Core.Tests.References;

public class WorkspaceScopeResolverTests
{
    private static readonly string Job1 = FullPath("Arch", "Job1");
    private static readonly string Job10 = FullPath("Arch", "Job10");

    private static string FullPath(params string[] segments) =>
        Path.GetFullPath(Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(segments)));

    [Fact]
    public void An_exact_child_of_the_root_is_inside()
    {
        var target = Path.Combine(Job1, "PartA.ipt");

        var location = WorkspaceScopeResolver.Locate(target, new[] { Job1 });

        Assert.Equal(ReferenceWorkspaceScope.InsideWorkspace, location.Scope);
        Assert.Equal(Job1, location.ContainingRoot);
    }

    [Fact]
    public void A_nested_child_of_the_root_is_inside()
    {
        var target = Path.Combine(Job1, "sub", "deep", "PartA.ipt");

        Assert.Equal(
            ReferenceWorkspaceScope.InsideWorkspace,
            WorkspaceScopeResolver.Locate(target, new[] { Job1 }).Scope);
    }

    [Fact]
    public void A_sibling_directory_with_a_shared_name_prefix_is_outside()
    {
        // Job1 vs Job10 - the classic naive-StartsWith bug.
        var target = Path.Combine(Job10, "PartA.ipt");

        var location = WorkspaceScopeResolver.Locate(target, new[] { Job1 });

        Assert.Equal(ReferenceWorkspaceScope.OutsideWorkspace, location.Scope);
        Assert.Null(location.ContainingRoot);
    }

    [Fact]
    public void A_completely_unrelated_path_with_a_known_root_is_outside()
    {
        var target = FullPath("Vendor", "Source", "PartA.ipt");

        Assert.Equal(
            ReferenceWorkspaceScope.OutsideWorkspace,
            WorkspaceScopeResolver.Locate(target, new[] { Job1 }).Scope);
    }

    [Fact]
    public void With_no_known_roots_the_scope_is_unknown_not_outside()
    {
        var target = Path.Combine(Job1, "PartA.ipt");

        Assert.Equal(
            ReferenceWorkspaceScope.Unknown,
            WorkspaceScopeResolver.Locate(target, Array.Empty<string?>()).Scope);
    }

    [Fact]
    public void A_null_or_relative_target_is_unknown()
    {
        Assert.Equal(ReferenceWorkspaceScope.Unknown, WorkspaceScopeResolver.Locate(null, new[] { Job1 }).Scope);
        Assert.Equal(ReferenceWorkspaceScope.Unknown, WorkspaceScopeResolver.Locate("  ", new[] { Job1 }).Scope);
        Assert.Equal(ReferenceWorkspaceScope.Unknown, WorkspaceScopeResolver.Locate(Path.Combine("rel", "a.ipt"), new[] { Job1 }).Scope);
    }

    [Fact]
    public void Null_and_relative_roots_are_ignored_but_a_valid_one_still_counts()
    {
        var target = Path.Combine(Job1, "PartA.ipt");

        var location = WorkspaceScopeResolver.Locate(
            target, new string?[] { null, "", "   ", Path.Combine("rel", "root"), Job1 });

        Assert.Equal(ReferenceWorkspaceScope.InsideWorkspace, location.Scope);
    }

    [Fact]
    public void Dot_segments_in_the_target_are_normalised_before_comparison()
    {
        var target = Path.Combine(Job1, "sub", "..", "PartA.ipt");

        Assert.Equal(
            ReferenceWorkspaceScope.InsideWorkspace,
            WorkspaceScopeResolver.Locate(target, new[] { Job1 }).Scope);
    }
}
