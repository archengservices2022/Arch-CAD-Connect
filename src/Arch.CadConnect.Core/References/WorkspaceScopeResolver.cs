namespace Arch.CadConnect.Core.References;

/// <summary>
/// Decides whether a resolved absolute path sits inside a known managed
/// workspace root. Boundary-safe: both sides are normalised with
/// <see cref="Path.GetFullPath(string)"/> and the root is compared as a
/// separator-terminated prefix, so <c>C:\Arch\Job1</c> never "contains"
/// <c>C:\Arch\Job10\...</c>. Case-insensitive to match NTFS and the rest of
/// the workspace-manifest matching in this codebase.
/// </summary>
public static class WorkspaceScopeResolver
{
    public readonly record struct Location(ReferenceWorkspaceScope Scope, string? ContainingRoot);

    public static Location Locate(string? resolvedAbsolutePath, IEnumerable<string?> workspaceRoots)
    {
        if (string.IsNullOrWhiteSpace(resolvedAbsolutePath)
            || !Path.IsPathFullyQualified(resolvedAbsolutePath))
        {
            return new Location(ReferenceWorkspaceScope.Unknown, null);
        }

        string target;
        try
        {
            target = Path.GetFullPath(resolvedAbsolutePath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new Location(ReferenceWorkspaceScope.Unknown, null);
        }

        var sawAnyRoot = false;

        foreach (var rawRoot in workspaceRoots)
        {
            if (string.IsNullOrWhiteSpace(rawRoot) || !Path.IsPathFullyQualified(rawRoot))
            {
                continue;
            }

            string root;
            try
            {
                root = Path.GetFullPath(rawRoot.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            sawAnyRoot = true;

            if (string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
            {
                return new Location(ReferenceWorkspaceScope.InsideWorkspace, root);
            }

            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            if (target.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                return new Location(ReferenceWorkspaceScope.InsideWorkspace, root);
            }
        }

        // A resolved path with at least one known root that it did not match is
        // genuinely OUTSIDE the managed workspace. With no roots at all we
        // simply cannot say.
        return sawAnyRoot
            ? new Location(ReferenceWorkspaceScope.OutsideWorkspace, null)
            : new Location(ReferenceWorkspaceScope.Unknown, null);
    }
}
