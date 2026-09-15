using System.Security.Cryptography;
using System.Text;

using Arch.CadConnect.Core.Files;

namespace Arch.CadConnect.Core.Tests.Files;

/// <summary>
/// HIGH 2: the P5C protected-target lease must deny another process WRITE,
/// DELETE, REPLACE, and RENAME of the exact target while it is held, still let
/// a reader (Inventor) open it, hash the bytes it protects, and fail closed
/// when it cannot be acquired.
/// </summary>
public sealed class ProtectedFileLeaseTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "arch-cc-lease-" + Guid.NewGuid().ToString("N"));

    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("protected target bytes");
    private static readonly long Size = Bytes.LongLength;
    private static readonly string Sha = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();

    public ProtectedFileLeaseTests() => Directory.CreateDirectory(_dir);

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

    private string NewTarget(string name = "TARGET.ipt")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, Bytes);
        return p;
    }

    [Fact]
    public void Acquires_a_held_lease_on_an_existing_file_and_hashes_the_protected_bytes()
    {
        var target = NewTarget();

        using var lease = ProtectedFileLease.Acquire(target);

        Assert.True(lease.IsHeld);
        Assert.Equal(Path.GetFullPath(target), lease.Path);
        var (ok, size, sha) = lease.HashProtected();
        Assert.True(ok);
        Assert.Equal(Size, size);
        Assert.Equal(Sha, sha);
    }

    [Fact]
    public void While_held_another_process_cannot_open_the_target_for_write()
    {
        var target = NewTarget();
        using var lease = ProtectedFileLease.Acquire(target);

        Assert.Throws<IOException>(() =>
            new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
    }

    [Fact]
    public void While_held_another_process_cannot_delete_the_target()
    {
        var target = NewTarget();
        using var lease = ProtectedFileLease.Acquire(target);

        Assert.Throws<IOException>(() => File.Delete(target));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void While_held_another_process_cannot_rename_or_replace_the_target()
    {
        var target = NewTarget();
        var other = Path.Combine(_dir, "REPLACEMENT.ipt");
        File.WriteAllText(other, "evil");

        using var lease = ProtectedFileLease.Acquire(target);

        Assert.Throws<IOException>(() => File.Move(target, Path.Combine(_dir, "MOVED.ipt")));
        try { File.Replace(other, target, null); } catch { /* expected: target is protected */ }
        Assert.True(File.Exists(target));
        Assert.Equal(Bytes, File.ReadAllBytes(target));
    }

    [Fact]
    public void While_held_a_reader_can_still_open_the_target()
    {
        var target = NewTarget();
        using var lease = ProtectedFileLease.Acquire(target);

        using var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(Size, reader.Length);
    }

    [Fact]
    public void After_dispose_the_target_can_be_written_and_deleted_again()
    {
        var target = NewTarget();
        var lease = ProtectedFileLease.Acquire(target);
        Assert.True(lease.IsHeld);

        lease.Dispose();

        Assert.False(lease.IsHeld);
        File.WriteAllText(target, "now writable");
        File.Delete(target);
    }

    [Fact]
    public void A_missing_file_yields_a_not_held_lease_never_throws()
    {
        var lease = ProtectedFileLease.Acquire(Path.Combine(_dir, "does-not-exist.ipt"));

        Assert.False(lease.IsHeld);
        var (ok, size, _) = lease.HashProtected();
        Assert.False(ok);
        Assert.Equal(-1, size);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\path.ipt")]
    public void An_unusable_path_yields_a_not_held_lease(string? path)
    {
        var lease = ProtectedFileLease.Acquire(path);
        Assert.False(lease.IsHeld);
    }

    [Fact]
    public void A_file_another_process_holds_exclusively_cannot_be_leased()
    {
        var target = NewTarget();
        using var exclusive = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var lease = ProtectedFileLease.Acquire(target);

        Assert.False(lease.IsHeld);
        lease.Dispose();
    }
}
