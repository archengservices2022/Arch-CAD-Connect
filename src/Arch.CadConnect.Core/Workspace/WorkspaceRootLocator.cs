namespace Arch.CadConnect.Core.Workspace;

/// <summary>
/// Finds the Arch managed-workspace root that contains a local file, by walking
/// UP from the file's own directory until it reaches a directory that holds a
/// <c>.arch/workspace.json</c> manifest. The manifest file is the ONLY
/// workspace marker - never a directory name, a filename, or a document number.
///
/// Deterministic: the NEAREST ancestor with a manifest wins. Bounded: it stops
/// at the filesystem root. Safe: it only ever looks at ancestor directories of
/// the file itself - it never searches siblings or arbitrary directories, so a
/// sibling-prefixed folder (<c>Job1</c> vs <c>Job1-archive</c>) can never
/// capture a file it does not actually contain.
/// </summary>
public static class WorkspaceRootLocator
{
    // Defensive only - the parent-equals-self check already terminates at the
    // real filesystem root; no real workspace is anywhere near this deep.
    private const int MaxAncestorHops = 128;

    /// <summary>
    /// The absolute directory of the managed workspace that contains
    /// <paramref name="absoluteFilePath"/>, or null when the file is not inside
    /// any managed workspace (no <c>.arch/workspace.json</c> on any ancestor),
    /// or the path is not usable.
    /// </summary>
    public static string? FindRootForFile(string? absoluteFilePath)
    {
        if (string.IsNullOrWhiteSpace(absoluteFilePath) || !Path.IsPathFullyQualified(absoluteFilePath))
        {
            return null;
        }

        string? current;
        try
        {
            current = Path.GetDirectoryName(Path.GetFullPath(absoluteFilePath.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        for (var hop = 0; hop < MaxAncestorHops && !string.IsNullOrEmpty(current); hop++)
        {
            if (HasManifest(current))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break; // reached the filesystem root
            }
            current = parent;
        }

        return null;
    }

    private static bool HasManifest(string directory)
    {
        try
        {
            return File.Exists(Path.Combine(directory, WorkspaceManifest.RelativeManifestPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
