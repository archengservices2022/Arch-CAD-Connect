using System.Security;
using System.Security.Cryptography;

namespace Arch.CadConnect.Core.Files;

/// <summary>
/// A short-lived PROTECTED READ handle on one exact local file, held across the
/// P5C Inventor <c>ReplaceReference</c> boundary so the target binary cannot be
/// changed, replaced, deleted, or renamed by another process between the
/// integrity check and the COM mutation.
///
/// Semantics (Windows share model):
///  * opened <see cref="FileAccess.Read"/> with <see cref="FileShare.Read"/> -
///    another reader (Inventor itself) may still open the file for READ;
///  * <see cref="FileShare"/> does NOT include Write or Delete, so any other
///    process attempting to open the file for WRITE, or to DELETE / REPLACE /
///    RENAME it, fails with a sharing violation while this lease is held;
///  * the lease NEVER writes to the file, never changes its attributes, never
///    clears read-only protection, and never bypasses Vault - it only holds an
///    open read handle;
///  * <see cref="HashProtected"/> hashes the bytes reachable through THIS held
///    handle (no re-open), so the value returned is provably the bytes the
///    lease is protecting.
///
/// Pure .NET (no COM) so the Inventor adapter can create one and the Core tests
/// can exercise the real share semantics with temp files.
/// </summary>
public sealed class ProtectedFileLease : IDisposable
{
    private FileStream? _stream;

    private ProtectedFileLease(FileStream? stream, string detail)
    {
        _stream = stream;
        Detail = detail;
    }

    /// <summary>True while the protected read handle is open.</summary>
    public bool IsHeld => _stream is not null;

    /// <summary>The exact full path the handle is open on, or null.</summary>
    public string? Path { get; private init; }

    public string Detail { get; }

    /// <summary>
    /// Acquire a protected read handle on <paramref name="absolutePath"/>. A
    /// blank / non-qualified path, a missing file, an access-denied error, a
    /// sharing violation, or any I/O fault yields a NOT-<see cref="IsHeld"/>
    /// lease - the caller MUST fail closed and make zero mutation.
    /// </summary>
    public static ProtectedFileLease Acquire(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)
            || !System.IO.Path.IsPathFullyQualified(absolutePath))
        {
            return new ProtectedFileLease(null, "The target path is not a usable absolute path.");
        }

        var full = absolutePath.Trim();
        try
        {
            var stream = new FileStream(
                full,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read, // read may be shared; write / delete / rename may NOT
                bufferSize: 1,
                FileOptions.None);
            return new ProtectedFileLease(stream, "Protected read handle held.") { Path = full };
        }
        catch (Exception ex) when (IsFileSystemFault(ex))
        {
            return new ProtectedFileLease(null,
                $"A protected read handle on the target could not be acquired ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Size + lowercase-hex SHA-256 of the bytes reachable through the HELD
    /// handle. <c>Ok == false</c> (with size <c>-1</c>) when the lease is not
    /// held or the read faulted - never a silent "match".
    /// </summary>
    public (bool Ok, long Size, string Sha256) HashProtected()
    {
        if (_stream is null)
        {
            return (false, -1, "");
        }
        try
        {
            _stream.Position = 0;
            var digest = SHA256.HashData(_stream);
            return (true, _stream.Length, Convert.ToHexString(digest).ToLowerInvariant());
        }
        catch (Exception ex) when (IsFileSystemFault(ex))
        {
            return (false, -1, "");
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }

    private static bool IsFileSystemFault(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException
            or ObjectDisposedException;
}
