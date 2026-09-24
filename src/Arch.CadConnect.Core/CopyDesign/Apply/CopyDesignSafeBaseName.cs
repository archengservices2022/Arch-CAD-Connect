namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6D FINAL BLOCKER FIX (durable-resume destination-escape hardening):
/// PURE, no-I/O validator that a server-authoritative file name is exactly
/// ONE safe file name component before it is EVER combined into a local
/// filesystem path - and, separately, that the combined path genuinely
/// resolves to a file DIRECTLY inside the caller's chosen destination
/// folder (never a subdirectory, never outside it).
///
/// WHY THIS EXISTS: <see cref="CopyDesignResumeAttemptReconstructor"/>
/// rebuilds a durable resume's destination paths from
/// <c>CopyDesignDurableResumeEntry.OriginalFileName</c> - a value the
/// server's operation-status endpoint reports back verbatim from what the
/// original reservation request supplied. The server independently
/// validates this shape at the reservation boundary too (this repo's own
/// web counterpart is <c>isSafeCadBaseName</c> in
/// <c>app/lib/cad-basename-core.ts</c>, applying the SAME rules), but a
/// client NEVER treats "the server already checked it" as a substitute for
/// its own defensive re-check - see this class's callers for why (a
/// compromised/buggy server response, a future relaxed server validation,
/// or simply an untrusted transport must never be able to walk a local
/// path outside the folder the user explicitly chose).
///
/// DELIBERATELY A BLOCKLIST, NOT A CHARACTER ALLOWLIST: a real CAD
/// engineering file name routinely contains spaces (e.g. "A B-C_01.ipt") -
/// an allowlist tight enough to be "obviously safe" would reject those
/// too. This instead rejects the SPECIFIC unsafe constructs a Windows path
/// component can never legitimately contain.
///
/// NEVER used to construct a path itself and never mutates its input -
/// <see cref="Validate"/> is a pure yes/no (plus a reason) predicate, and
/// <see cref="TryResolveContained"/> only ever RETURNS a path for the
/// caller to use, after proving it is safe.
/// </summary>
public static class CopyDesignSafeBaseName
{
    public sealed record Result(bool Safe, string? Reason)
    {
        public static Result Ok() => new(true, null);
        public static Result Fail(string reason) => new(false, reason);
    }

    public sealed record ContainmentResult(bool Safe, string? ResolvedPath, string? FailureReason)
    {
        public static ContainmentResult Ok(string resolvedPath) => new(true, resolvedPath, null);
        public static ContainmentResult Fail(string reason) => new(false, null, reason);
    }

    private static readonly char[] WindowsInvalidChars = { '<', '>', ':', '"', '|', '?', '*' };

    /// <summary>Windows reserved device names (case-insensitive), matched
    ///  against the basename BEFORE its first dot - so "CON", "con.ipt",
    ///  "COM1.iam" and "Nul.idw" are all reserved, but "MyCOM1.ipt" is
    ///  not.</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Generous but bounded - rejects an absurd/adversarial length
    ///  before running anything else against it, never meant as a tight
    ///  limit.</summary>
    private const int MaxLength = 255;

    /// <summary>
    /// True iff <paramref name="value"/> is exactly ONE safe file name
    /// component - never a path, in any form. Rejects: non-null-but-blank
    /// or whitespace-only; longer than 255 characters; containing '/' or
    /// '\' (rules out "subdir\x", "subdir/x", a UNC path "\\server\share\x",
    /// and a rooted "C:\x" path alike); exactly "." or ".."; a drive prefix
    /// ("C:", "D:", ...) at the start; any Windows-invalid filename
    /// character (&lt; &gt; : " | ? *); any ASCII control character
    /// (0x00-0x1F, 0x7F); a trailing '.' or trailing ' '; a Windows
    /// reserved device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9),
    /// case-insensitive, with or without an extension.
    /// </summary>
    public static Result Validate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Result.Fail("blank");
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Fail("whitespace-only");
        }
        if (value.Length > MaxLength)
        {
            return Result.Fail($"longer than {MaxLength} characters");
        }
        if (value.Contains('/') || value.Contains('\\'))
        {
            return Result.Fail("contains a path separator ('/' or '\\')");
        }
        if (value is "." or "..")
        {
            return Result.Fail("\".\" or \"..\" is not a file name");
        }
        if (value.Length >= 2 && IsAsciiLetter(value[0]) && value[1] == ':')
        {
            return Result.Fail("drive prefix");
        }
        foreach (var ch in value)
        {
            if (ch <= 0x1F || ch == 0x7F)
            {
                return Result.Fail("control character");
            }
        }
        if (value.IndexOfAny(WindowsInvalidChars) >= 0)
        {
            return Result.Fail("Windows-invalid character (one of < > : \" | ? *)");
        }
        if (value.EndsWith('.') || value.EndsWith(' '))
        {
            return Result.Fail("trailing dot or trailing space");
        }
        var dotIndex = value.IndexOf('.');
        var deviceBaseName = dotIndex >= 0 ? value[..dotIndex] : value;
        if (ReservedDeviceNames.Contains(deviceBaseName))
        {
            return Result.Fail("reserved Windows device name");
        }
        return Result.Ok();
    }

    /// <summary>
    /// Validates <paramref name="fileName"/> (see <see cref="Validate"/>),
    /// then combines it with <paramref name="destinationFolder"/> and
    /// proves the result via NORMALIZED, path-aware comparison - never a
    /// naive string-prefix check (which "C:\Approved2" would wrongly pass
    /// against a root of "C:\Approved"). For P6D durable resume,
    /// subdirectories are NOT allowed: the resolved file's parent directory
    /// must be EXACTLY <paramref name="destinationFolder"/>'s own resolved,
    /// separator-normalized form - not merely "under" it.
    /// </summary>
    public static ContainmentResult TryResolveContained(string destinationFolder, string? fileName)
    {
        var validity = Validate(fileName);
        if (!validity.Safe)
        {
            return ContainmentResult.Fail($"unsafe file name ({validity.Reason}): {fileName}");
        }
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Path.IsPathFullyQualified(destinationFolder))
        {
            return ContainmentResult.Fail("the destination folder must be a full, absolute path.");
        }

        string resolvedRoot;
        string resolvedTarget;
        try
        {
            resolvedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationFolder));
            resolvedTarget = Path.GetFullPath(Path.Combine(destinationFolder, fileName!));
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or NotSupportedException or PathTooLongException)
        {
            return ContainmentResult.Fail($"malformed destination path: {ex.Message}");
        }

        var resolvedParent = Path.GetDirectoryName(resolvedTarget);
        if (!string.Equals(resolvedParent, resolvedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return ContainmentResult.Fail(
                $"resolved path \"{resolvedTarget}\" does not resolve directly inside the destination folder " +
                $"\"{resolvedRoot}\" - refusing (no subdirectories, no escape).");
        }

        return ContainmentResult.Ok(resolvedTarget);
    }

    private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}
