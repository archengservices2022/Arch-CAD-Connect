using System.Security;

namespace Arch.CadConnect.Core.Files;

/// <summary>The result of a fail-closed local-file writability probe.</summary>
public enum LocalWritability
{
    /// <summary>Affirmatively writable: the file exists, has no read-only
    ///  attribute, and could be opened for write access right now.</summary>
    Writable,

    /// <summary>The file exists but carries the Windows read-only attribute.</summary>
    ReadOnly,

    /// <summary>There is no file at the path.</summary>
    Missing,

    /// <summary>Write access could not be established: the path is unusable, an
    ///  attribute read failed, access was denied, or the file could not be
    ///  opened for write. NEVER treat this as writable.</summary>
    Indeterminate,
}

/// <summary>
/// A FAIL-CLOSED writability check for a local managed file. It answers the
/// question P5C needs before it repoints a reference (which dirties the owning
/// document in memory): "may this document already be modified and saved?"
///
/// Design rules:
///  * only <see cref="LocalWritability.Writable"/> is an AFFIRMATIVE result -
///    callers authorize on <c>== Writable</c>, never on negating a helper whose
///    failure also returns "not read-only";
///  * a missing file, an unusable path, an attribute-inspection failure, an
///    access-denied error, or any inability to open for write is
///    <see cref="LocalWritability.Indeterminate"/> (rejected);
///  * NO file content or last-write timestamp is ever changed - the probe only
///    reads attributes and opens an existing file for write WITHOUT writing
///    (shared, so a co-open by Inventor does not matter);
///  * NO read-only attribute is cleared and NO Vault / file protection is
///    disabled or bypassed;
///  * NO checkout is ever taken.
/// </summary>
public static class LocalWritabilityProbe
{
    /// <summary>True only for the single affirmative outcome.</summary>
    public static bool IsAffirmativelyWritable(LocalWritability writability) =>
        writability == LocalWritability.Writable;

    public static LocalWritability Probe(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath))
        {
            return LocalWritability.Indeterminate;
        }

        var path = absolutePath.Trim();

        bool exists;
        try
        {
            exists = File.Exists(path);
        }
        catch (Exception ex) when (IsFileSystemFault(ex))
        {
            return LocalWritability.Indeterminate;
        }

        if (!exists)
        {
            return LocalWritability.Missing;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (IsFileSystemFault(ex))
        {
            // Access denied / attribute probe failure -> cannot establish write
            // access -> reject.
            return LocalWritability.Indeterminate;
        }

        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
        {
            return LocalWritability.ReadOnly;
        }

        // Affirmative check: can the file actually be opened for write right now?
        // FileShare.ReadWrite so a concurrent read handle (e.g. Inventor) does
        // not block the probe; nothing is written, so no content or timestamp
        // changes.
        try
        {
            using var probe = new FileStream(
                path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return LocalWritability.Writable;
        }
        catch (Exception ex) when (IsFileSystemFault(ex))
        {
            return LocalWritability.Indeterminate;
        }
    }

    private static bool IsFileSystemFault(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException;
}
