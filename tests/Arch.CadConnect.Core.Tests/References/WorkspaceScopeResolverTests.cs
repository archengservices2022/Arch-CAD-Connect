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

    // ---- nested workspaces: the DEEPEST containing root wins ----------

    private static readonly string Outer = FullPath("Engineering", "WorkspaceA");
    private static readonly string Inner = FullPath("Engineering", "WorkspaceA", "Nested");

    [Fact]
    public void Nested_workspaces_outer_first_inner_wins()
    {
        var target = Path.Combine(Inner, "PART.ipt");

        var location = WorkspaceScopeResolver.Locate(target, new[] { Outer, Inner });

        Assert.Equal(ReferenceWorkspaceScope.InsideWorkspace, location.Scope);
        Assert.Equal(Inner, location.ContainingRoot);
    }

    [Fact]
    public void Nested_workspaces_inner_first_inner_still_wins()
    {
        var target = Path.Combine(Inner, "PART.ipt");

        var location = WorkspaceScopeResolver.Locate(target, new[] { Inner, Outer });

        Assert.Equal(ReferenceWorkspaceScope.InsideWorkspace, location.Scope);
        Assert.Equal(Inner, location.ContainingRoot);
    }

    [Fact]
    public void A_file_directly_in_the_outer_workspace_still_resolves_to_the_outer_root()
    {
        var target = Path.Combine(Outer, "ROOT.iam");

        var location = WorkspaceScopeResolver.Locate(target, new[] { Inner, Outer });

        Assert.Equal(Outer, location.ContainingRoot);
    }

    [Fact]
    public void Unrelated_roots_resolve_to_the_correct_containing_root()
    {
        var wsA = FullPath("Eng", "WorkspaceA");
        var wsB = FullPath("Eng", "WorkspaceB");
        var target = Path.Combine(wsA, "PART.ipt");

        var location = WorkspaceScopeResolver.Locate(target, new[] { wsB, wsA });

        Assert.Equal(ReferenceWorkspaceScope.InsideWorkspace, location.Scope);
        Assert.Equal(wsA, location.ContainingRoot);
    }

    [Fact]
    public void Same_filename_in_unrelated_roots_is_disambiguated_by_the_absolute_path_only()
    {
        var wsA = FullPath("Eng", "WorkspaceA");
        var wsB = FullPath("Eng", "WorkspaceB");

        Assert.Equal(wsA, WorkspaceScopeResolver.Locate(Path.Combine(wsA, "PART.ipt"), new[] { wsA, wsB }).ContainingRoot);
        Assert.Equal(wsB, WorkspaceScopeResolver.Locate(Path.Combine(wsB, "PART.ipt"), new[] { wsA, wsB }).ContainingRoot);
    }

    [Fact]
    public void Duplicate_roots_produce_a_deterministic_result()
    {
        var target = Path.Combine(Inner, "PART.ipt");

        var a = WorkspaceScopeResolver.Locate(target, new[] { Outer, Inner, Outer, Inner });
        var b = WorkspaceScopeResolver.Locate(target, new[] { Inner, Outer, Inner, Outer });

        Assert.Equal(Inner, a.ContainingRoot);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Trailing_separator_variants_of_a_root_give_the_same_result()
    {
        var target = Path.Combine(Inner, "PART.ipt");
        var sep = Path.DirectorySeparatorChar;

        var withSep = WorkspaceScopeResolver.Locate(target, new[] { Outer + sep, Inner + sep });
        var withoutSep = WorkspaceScopeResolver.Locate(target, new[] { Outer, Inner });

        Assert.Equal(withoutSep, withSep);
        Assert.Equal(Inner, withSep.ContainingRoot);
    }

    [Fact]
    public void Nesting_never_weakens_the_Job1_vs_Job10_boundary()
    {
        // Job10 has its own (nested-looking but sibling) root; a file in Job1
        // must still not be captured by Job10 and vice-versa.
        var target = Path.Combine(Job1, "PartA.ipt");

        var location = WorkspaceScopeResolver.Locate(target, new[] { Job10, Job1 });

        Assert.Equal(Job1, location.ContainingRoot);
        Assert.Equal(
            ReferenceWorkspaceScope.OutsideWorkspace,
            WorkspaceScopeResolver.Locate(Path.Combine(Job10, "PartA.ipt"), new[] { Job1 }).Scope);
    }

    [Fact]
    public void Nesting_never_weakens_the_archive_sibling_boundary()
    {
        var live = FullPath("Eng", "Job1");
        var archive = FullPath("Eng", "Job1-archive");
        var target = Path.Combine(archive, "OLD.ipt");

        Assert.Equal(
            ReferenceWorkspaceScope.OutsideWorkspace,
            WorkspaceScopeResolver.Locate(target, new[] { live }).Scope);
    }
}
