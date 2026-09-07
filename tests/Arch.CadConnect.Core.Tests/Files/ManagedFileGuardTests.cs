using Arch.CadConnect.Core.Files;

namespace Arch.CadConnect.Core.Tests.Files;

public class ManagedFileGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arch-cc-guard-" + Guid.NewGuid().ToString("N"));

    public ManagedFileGuardTests() => Directory.CreateDirectory(_dir);

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
    public void SetControlled_then_SetWritable_round_trips_the_read_only_attribute()
    {
        var p = NewFile();
        Assert.False(ManagedFileGuard.IsControlled(p));

        Assert.True(ManagedFileGuard.SetControlled(p));
        Assert.True(ManagedFileGuard.IsControlled(p));
        Assert.True((File.GetAttributes(p) & FileAttributes.ReadOnly) != 0);

        Assert.True(ManagedFileGuard.SetWritable(p));
        Assert.False(ManagedFileGuard.IsControlled(p));
    }

    [Fact]
    public void All_methods_are_best_effort_on_a_missing_or_relative_path()
    {
        Assert.False(ManagedFileGuard.SetControlled(Path.Combine(_dir, "nope.ipt")));
        Assert.False(ManagedFileGuard.SetWritable(Path.Combine(_dir, "nope.ipt")));
        Assert.False(ManagedFileGuard.IsControlled(Path.Combine(_dir, "nope.ipt")));
        Assert.False(ManagedFileGuard.SetControlled("relative\\path.ipt"));
        Assert.False(ManagedFileGuard.SetControlled(""));
    }

    [Fact]
    public void CanReplaceInPlace_true_for_a_free_file_including_a_controlled_one()
    {
        var p = NewFile();
        Assert.True(ManagedFileGuard.CanReplaceInPlace(p));

        ManagedFileGuard.SetControlled(p);
        Assert.True(ManagedFileGuard.CanReplaceInPlace(p));
        // the probe must leave the attribute exactly as it found it
        Assert.True(ManagedFileGuard.IsControlled(p));
    }

    [Fact]
    public void CanReplaceInPlace_true_for_a_missing_file()
    {
        Assert.True(ManagedFileGuard.CanReplaceInPlace(Path.Combine(_dir, "missing.ipt")));
    }

    [Fact]
    public void CanReplaceInPlace_false_when_another_handle_holds_the_file_exclusively()
    {
        var p = NewFile();
        using var held = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(ManagedFileGuard.CanReplaceInPlace(p));
    }
}
