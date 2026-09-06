using System.Text.RegularExpressions;

namespace Arch.CadConnect.Core.Workspace;

/// <summary>
/// Client-side re-validation and root-relative resolution of a workspace
/// placement path, BEFORE any filesystem write. A faithful C# port of
/// <c>web/app/lib/get-latest-core.ts</c> <c>assertSafeWorkspacePath</c> plus
/// the P3B-2B materializer's extra constraints
/// (<c>resolveWithinWorkspaceRoot</c>):
///
///   - the shape rules (no backslash, no control chars, no absolute / drive /
///     home prefix, no <c>.</c> / <c>..</c> segment, no Windows-invalid char,
///     no trailing dot/space, no reserved device name, segment charset);
///   - FLAT filename only - a nested path is refused in this milestone (Arch
///     does not yet carry real Inventor relative-reference paths, and a
///     nested directory would open a junction/reparse escape surface);
///   - the resolved absolute path is independently confirmed to sit under the
///     workspace root, with a CASE-INSENSITIVE containment check (NTFS).
///
/// Defense in depth: a <c>safe</c> plan already guarantees these, but a plan
/// is untrusted input the moment it leaves the server process.
/// </summary>
public static class SafeWorkspacePath
{
    private static readonly Regex SafeSegment =
        new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    private static readonly Regex WindowsInvalidChars =
        new("[<>:\"|?*]", RegexOptions.CultureInvariant);

    private static readonly Regex DrivePrefix =
        new("^[A-Za-z]:", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Validate the shape of a relative path. Throws
    ///  <see cref="UnsafeWorkspacePathException"/> - the message never leaks a
    ///  server path, only the offending value.</summary>
    public static void ValidateShape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new UnsafeWorkspacePathException(value ?? "", "empty");
        }
        if (value.Length > 512)
        {
            throw new UnsafeWorkspacePathException(value, "too long");
        }
        if (value.Contains('\\'))
        {
            throw new UnsafeWorkspacePathException(value, "backslash");
        }
        foreach (var ch in value)
        {
            if (ch <= 0x1F || ch == 0x7F)
            {
                throw new UnsafeWorkspacePathException(value, "control character");
            }
        }
        if (value.StartsWith('/') || value.StartsWith('~'))
        {
            throw new UnsafeWorkspacePathException(value, "absolute / home / UNC");
        }
        if (DrivePrefix.IsMatch(value))
        {
            throw new UnsafeWorkspacePathException(value, "drive prefix");
        }

        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0)
            {
                throw new UnsafeWorkspacePathException(value, "empty segment");
            }
            if (segment is "." or "..")
            {
                throw new UnsafeWorkspacePathException(value, "dot segment");
            }
            if (WindowsInvalidChars.IsMatch(segment))
            {
                throw new UnsafeWorkspacePathException(value, "Windows-invalid character");
            }
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                throw new UnsafeWorkspacePathException(value, "trailing dot/space");
            }
            var deviceBase = segment.Split('.')[0];
            if (ReservedDeviceNames.Contains(deviceBase))
            {
                throw new UnsafeWorkspacePathException(value, "reserved Windows device name");
            }
            if (!SafeSegment.IsMatch(segment))
            {
                throw new UnsafeWorkspacePathException(value, "bad segment");
            }
        }
    }

    /// <summary>
    /// Resolve <paramref name="relativePath"/> strictly under
    /// <paramref name="workspaceRoot"/> (an absolute directory), Windows
    /// semantics. Enforces shape + FLAT-only + case-insensitive containment.
    /// </summary>
    /// <exception cref="WorkspaceRootException">root is not an absolute path.</exception>
    /// <exception cref="UnsafeWorkspacePathException">path fails re-validation,
    ///   is nested, or would escape the root.</exception>
    public static string ResolveWithinRoot(string workspaceRoot, string relativePath)
    {
        RequireAbsoluteRoot(workspaceRoot);

        try
        {
            ValidateShape(relativePath);
        }
        catch (UnsafeWorkspacePathException ex)
        {
            throw new UnsafeWorkspacePathException(relativePath, $"failed safety re-validation ({ex.Reason})");
        }

        if (relativePath.Contains('/'))
        {
            throw new UnsafeWorkspacePathException(
                relativePath,
                "nested paths are not supported in this milestone - only a single flat filename");
        }

        var resolvedRoot = Path.GetFullPath(workspaceRoot);
        var resolvedTarget = Path.GetFullPath(Path.Combine(resolvedRoot, relativePath));

        var rootWithSep = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;

        var targetFolded = resolvedTarget.ToLowerInvariant();
        if (targetFolded != resolvedRoot.ToLowerInvariant()
            && !targetFolded.StartsWith(rootWithSep.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new UnsafeWorkspacePathException(relativePath, "escapes the workspace root");
        }

        return resolvedTarget;
    }

    public static void RequireAbsoluteRoot(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Path.IsPathFullyQualified(workspaceRoot))
        {
            throw new WorkspaceRootException(
                $"Workspace root must be an absolute path: {Quote(workspaceRoot)}");
        }
    }

    internal static string Quote(string? value) => value is null ? "null" : $"\"{value}\"";
}

public sealed class UnsafeWorkspacePathException : Exception
{
    public string Value { get; }
    public string Reason { get; }

    public UnsafeWorkspacePathException(string value, string reason)
        : base($"Unsafe workspace placement path ({reason}): {SafeWorkspacePath.Quote(value)}")
    {
        Value = value;
        Reason = reason;
    }
}

public sealed class WorkspaceRootException(string message) : Exception(message);
