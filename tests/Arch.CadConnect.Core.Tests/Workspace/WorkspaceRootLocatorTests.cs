using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.Tests.Workspace;

public class WorkspaceRootLocatorTests : IDisposable
{
    private readonly string _tmp =
        Path.Combine(Path.GetTempPath(), "arch-cc-wrl-" + Guid.NewGuid().ToString("N"));

    public WorkspaceRootLocatorTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _tmp }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void PlaceManifest(string directory)
    {
        var full = Path.Combine(directory, WorkspaceManifest.RelativeManifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "{\"schema\":\"" + WorkspaceManifest.SchemaId + "\",\"entries\":[]}");
    }

    [Fact]
    public void Finds_the_workspace_root_for_a_file_directly_in_it()
    {
        var ws = Dir("job1");
        PlaceManifest(ws);

        Assert.Equal(ws, WorkspaceRootLocator.FindRootForFile(Path.Combine(ws, "ROOT.iam")));
    }

    [Fact]
    public void Finds_the_workspace_root_for_a_file_in_a_nested_subdirectory()
    {
        var ws = Dir("job1");
        PlaceManifest(ws);
        var deep = Dir("job1", "parts", "brackets");

        Assert.Equal(ws, WorkspaceRootLocator.FindRootForFile(Path.Combine(deep, "BRACKET.ipt")));
    }

    [Fact]
    public void Returns_the_nearest_ancestor_workspace_when_manifests_are_nested()
    {
        var outer = Dir("outer");
        var inner = Dir("outer", "inner");
        PlaceManifest(outer);
        PlaceManifest(inner);

        Assert.Equal(inner, WorkspaceRootLocator.FindRootForFile(Path.Combine(inner, "ROOT.iam")));
    }

    [Fact]
    public void Returns_null_when_no_ancestor_has_a_manifest()
    {
        var plain = Dir("not-managed", "sub");

        Assert.Null(WorkspaceRootLocator.FindRootForFile(Path.Combine(plain, "ROOT.iam")));
    }

    [Fact]
    public void A_sibling_prefixed_directory_never_captures_a_file_it_does_not_contain()
    {
        var job1 = Dir("job1");
        PlaceManifest(job1);
        var job10 = Dir("job10"); // shares the "job1" prefix, has NO manifest

        // A file in job10 must NOT resolve to job1's workspace.
        Assert.Null(WorkspaceRootLocator.FindRootForFile(Path.Combine(job10, "ROOT.iam")));
    }

    [Fact]
    public void A_file_in_an_archive_sibling_does_not_inherit_the_live_workspace()
    {
        var job1 = Dir("job1");
        PlaceManifest(job1);
        var archive = Dir("job1-archive"); // NOT under job1, no manifest

        Assert.Null(WorkspaceRootLocator.FindRootForFile(Path.Combine(archive, "OLD-ROOT.iam")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path/ROOT.iam")]
    [InlineData(@"relative\ROOT.iam")]
    public void Rejects_an_unusable_path(string? path)
    {
        Assert.Null(WorkspaceRootLocator.FindRootForFile(path));
    }

    [Fact]
    public void Normalises_dot_segments_before_walking()
    {
        var ws = Dir("job1");
        PlaceManifest(ws);
        var deep = Dir("job1", "parts");

        var messy = Path.Combine(deep, "..", "sub", "..", "ROOT.iam");
        Assert.Equal(ws, WorkspaceRootLocator.FindRootForFile(messy));
    }
}
