using Arch.CadConnect.Core.Files;

namespace Arch.CadConnect.Core.Tests.Files;

/// <summary>
/// FINDING 3: P5C must authorize a repair only on an AFFIRMATIVE
/// <see cref="LocalWritability.Writable"/>. Every other outcome - read-only,
/// missing, access/probe failure, unusable path - must fail closed.
/// </summary>
public class LocalWritabilityProbeTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "arch-cc-writ-" + Guid.NewGuid().ToString("N"));

    public LocalWritabilityProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(_dir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private string NewFile(string name = "part.ipt")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "bytes");
        return p;
    }

    [Fact]
    public void An_ordinary_existing_file_is_affirmatively_writable()
    {
        var p = NewFile();

        var result = LocalWritabilityProbe.Probe(p);

        Assert.Equal(LocalWritability.Writable, result);
        Assert.True(LocalWritabilityProbe.IsAffirmativelyWritable(result));
        // the probe must not have touched the bytes
        Assert.Equal("bytes", File.ReadAllText(p));
    }

    [Fact]
    public void A_read_only_file_is_ReadOnly_and_never_authorizes()
    {
        var p = NewFile();
        File.SetAttributes(p, File.GetAttributes(p) | FileAttributes.ReadOnly);

        var result = LocalWritabilityProbe.Probe(p);

        Assert.Equal(LocalWritability.ReadOnly, result);
        Assert.False(LocalWritabilityProbe.IsAffirmativelyWritable(result));
        // the read-only attribute must be left exactly as found
        Assert.True((File.GetAttributes(p) & FileAttributes.ReadOnly) != 0);
    }

    [Fact]
    public void A_missing_file_is_Missing_and_never_authorizes()
    {
        var result = LocalWritabilityProbe.Probe(Path.Combine(_dir, "does-not-exist.ipt"));

        Assert.Equal(LocalWritability.Missing, result);
        Assert.False(LocalWritabilityProbe.IsAffirmativelyWritable(result));
    }

    [Fact]
    public void An_exclusively_held_file_cannot_establish_write_access_and_is_Indeterminate()
    {
        var p = NewFile();
        using var held = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = LocalWritabilityProbe.Probe(p);

        Assert.Equal(LocalWritability.Indeterminate, result);
        Assert.False(LocalWritabilityProbe.IsAffirmativelyWritable(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\path.ipt")]
    public void An_unusable_path_is_Indeterminate_and_never_authorizes(string? path)
    {
        var result = LocalWritabilityProbe.Probe(path);

        Assert.Equal(LocalWritability.Indeterminate, result);
        Assert.False(LocalWritabilityProbe.IsAffirmativelyWritable(result));
    }

    [Fact]
    public void Only_the_affirmative_Writable_outcome_authorizes_a_repair()
    {
        Assert.True(LocalWritabilityProbe.IsAffirmativelyWritable(LocalWritability.Writable));
        Assert.False(LocalWritabilityProbe.IsAffirmativelyWritable(LocalWritability.ReadOnly));
        Assert.False(LocalWritabilityProbe.IsAffirmativelyWritable(LocalWritability.Missing));
        Assert.False(LocalWritabilityProbe.IsAffirmativelyWritable(LocalWritability.Indeterminate));
    }
}
