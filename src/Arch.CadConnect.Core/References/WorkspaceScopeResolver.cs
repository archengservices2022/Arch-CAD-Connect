namespace Arch.CadConnect.Core.References;

/// <summary>
/// Decides whether a resolved absolute path sits inside a known managed
/// workspace root, and which one. Boundary-safe: both sides are normalised
/// with <see cref="Path.GetFullPath(string)"/> and the root is compared as a
/// separator-terminated prefix, so <c>C:\Arch\Job1</c> never "contains"
/// <c>C:\Arch\Job10\...</c>. Case-insensitive to match NTFS and the rest of
/// the workspace-manifest matching in this codebase.
///
/// NESTED WORKSPACES: when more than one candidate root safely contains the
/// path, the DEEPEST (longest canonical) containing root wins - the file
/// belongs to its nearest workspace, not an ancestor one. The result is
/// independent of the order the roots were supplied in.
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
        string? deepestContainingRoot = null;

        foreach (var rawRoot in workspaceRoots)
        {
            if (string.IsNullOrWhiteSpace(rawRoot) || !Path.IsPathFullyQualified(rawRoot))
            {
                continue;
            }

            string root;
            try
            {
                root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawRoot.Trim()));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            sawAnyRoot = true;

            if (!Contains(root, target))
            {
                continue;
            }

            // Deepest containing root wins. A length tie can only be the same
            // canonical directory (two disjoint directories cannot both contain
            // the same path) - possibly differing only by case; the ordinal
            // tie-break keeps the outcome independent of input order.
            if (deepestContainingRoot is null
                || root.Length > deepestContainingRoot.Length
                || (root.Length == deepestContainingRoot.Length
                    && string.CompareOrdinal(root, deepestContainingRoot) < 0))
            {
                deepestContainingRoot = root;
            }
        }

        if (deepestContainingRoot is not null)
        {
            return new Location(ReferenceWorkspaceScope.InsideWorkspace, deepestContainingRoot);
        }

        // A resolved path with at least one known root that it did not match is
        // genuinely OUTSIDE the managed workspace. With no roots at all we
        // simply cannot say.
        return sawAnyRoot
            ? new Location(ReferenceWorkspaceScope.OutsideWorkspace, null)
            : new Location(ReferenceWorkspaceScope.Unknown, null);
    }

    /// <summary>Separator-safe, case-insensitive containment: <paramref name="target"/>
    ///  is <paramref name="root"/> itself, or sits strictly under it.</summary>
    private static bool Contains(string root, string target)
    {
        if (string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return target.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }
}
