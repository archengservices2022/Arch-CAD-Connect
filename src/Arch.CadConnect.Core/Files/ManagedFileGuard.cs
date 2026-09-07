namespace Arch.CadConnect.Core.Files;

/// <summary>
/// Toggles the Windows read-only attribute on a managed local file. This is a
/// USABILITY guard only - it makes a "controlled" managed file resist an
/// accidental save in Inventor. It is NEVER the lock: the server
/// <c>CadCheckout</c> row is the sole authority for who may create the next
/// FileVersion. A user (or another program) can always clear the attribute
/// manually; that does not mean they hold the checkout.
///
/// Every method is best-effort and takes an ABSOLUTE path that a caller has
/// already resolved from a verified workspace-manifest binding
/// (<c>root + relativePath</c>) - never a filename guess.
/// </summary>
public static class ManagedFileGuard
{
    /// <summary>
    /// Mark the file "controlled": add <see cref="FileAttributes.ReadOnly"/>.
    /// Returns true if the file now exists and is read-only. A missing file,
    /// a relative path, or an OS failure returns false without throwing.
    /// </summary>
    public static bool SetControlled(string absolutePath) => TrySetReadOnly(absolutePath, readOnly: true);

    /// <summary>
    /// Mark the file "writable" (checked out by this user): remove
    /// <see cref="FileAttributes.ReadOnly"/>. Returns true if the file now
    /// exists and is writable. Best-effort, never throws.
    /// </summary>
    public static bool SetWritable(string absolutePath) => TrySetReadOnly(absolutePath, readOnly: false);

    /// <summary>True when the file exists and carries the read-only attribute.
    ///  A missing file or any error returns false.</summary>
    public static bool IsControlled(string absolutePath)
    {
        try
        {
            if (!IsUsablePath(absolutePath) || !File.Exists(absolutePath))
            {
                return false;
            }
            return (File.GetAttributes(absolutePath) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
        }
        catch (Exception ex) when (IsFileSystemFault(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the file at <paramref name="absolutePath"/> can be opened for
    /// an exclusive write right now (no other process - e.g. Inventor - holds
    /// it open). Used as an Undo pre-flight: the working file must be
    /// replaceable BEFORE the server checkout is released. A missing file is
    /// considered replaceable (there is nothing locking it).
    /// </summary>
    public static bool CanReplaceInPlace(string absolutePath)
    {
        try
        {
            if (!IsUsablePath(absolutePath))
            {
                return false;
            }
            if (!File.Exists(absolutePath))
            {
                return true;
            }

            var wasReadOnly = IsControlled(absolutePath);
            if (wasReadOnly)
            {
                // A read-only file cannot be opened FileAccess.Write; probe
                // with the attribute momentarily cleared, then restore it so
                // this check has no visible side effect.
                TrySetReadOnly(absolutePath, readOnly: false);
            }
            try
            {
                using var probe = new FileStream(
                    absolutePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            finally
            {
                if (wasReadOnly)
                {
                    TrySetReadOnly(absolutePath, readOnly: true);
                }
            }
        }
        catch (IOException)
        {
            return false; // another handle holds it
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TrySetReadOnly(string absolutePath, bool readOnly)
    {
        try
        {
            if (!IsUsablePath(absolutePath) || !File.Exists(absolutePath))
            {
                return false;
            }
            var attrs = File.GetAttributes(absolutePath);
            var next = readOnly ? attrs | FileAttributes.ReadOnly : attrs & ~FileAttributes.ReadOnly;
            if (next != attrs)
            {
                File.SetAttributes(absolutePath, next);
            }
            return readOnly
                ? (File.GetAttributes(absolutePath) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly
                : (File.GetAttributes(absolutePath) & FileAttributes.ReadOnly) == 0;
        }
        catch (Exception ex) when (IsFileSystemFault(ex))
        {
            return false;
        }
    }

    private static bool IsUsablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool IsFileSystemFault(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
