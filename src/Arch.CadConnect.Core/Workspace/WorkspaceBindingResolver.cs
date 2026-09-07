namespace Arch.CadConnect.Core.Workspace;

/// <summary>
/// Given a local file's absolute path and the workspace roots the client
/// knows about, returns the STABLE Arch identity the verified workspace
/// manifest bound to that exact <c>root + relativePath</c> - or null.
///
/// This is the ONLY sanctioned way <c>CadDocumentContext.PlmIdentity</c> gets
/// populated. It NEVER infers identity from the filename, the path text, or a
/// document number that happens to appear in a filename. If no manifest entry
/// matches the exact location, the file is UNMANAGED (null), truthfully.
/// </summary>
public static class WorkspaceBindingResolver
{
    public sealed record Binding(
        PlmIdentity Identity,
        WorkspaceManifestEntryState State,
        string WorkspaceRoot,
        WorkspaceManifestEntry Entry);

    /// <summary>
    /// Look up <paramref name="absoluteFilePath"/> across every root in
    /// <paramref name="workspaceRoots"/>. The first exact match wins. A
    /// missing / corrupt manifest under a root contributes nothing.
    /// </summary>
    public static Binding? Resolve(string? absoluteFilePath, IEnumerable<string?> workspaceRoots)
    {
        if (string.IsNullOrWhiteSpace(absoluteFilePath))
        {
            return null;
        }

        foreach (var root in workspaceRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            {
                continue;
            }

            WorkspaceManifest manifest;
            try
            {
                manifest = WorkspaceManifest.LoadOrEmpty(root);
            }
            catch (Exception ex) when (ex is IOException or WorkspaceRootException or UnauthorizedAccessException)
            {
                continue;
            }

            var entry = manifest.FindByAbsolutePath(absoluteFilePath);
            if (entry is not null)
            {
                return new Binding(entry.ToPlmIdentity(), entry.State, Path.GetFullPath(root), entry);
            }
        }

        return null;
    }
}
