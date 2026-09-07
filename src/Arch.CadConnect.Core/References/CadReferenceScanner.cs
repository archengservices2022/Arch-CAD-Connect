using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Core.References;

/// <summary>
/// Runs a P5A reference scan: walk the DIRECT references Inventor reports for
/// the root document and, recursively, for each resolved child - recording
/// every direct parent -> child edge exactly once, guarding against cycles and
/// repeated sub-assemblies. Each resolved edge is enriched with workspace
/// containment and (only for an exact managed path) the workspace-manifest
/// identity.
///
/// Pure: no COM, no HTTP. The only I/O is reading <c>.arch\workspace.json</c>
/// under the known roots (via <see cref="WorkspaceManifest.LoadOrEmpty"/>,
/// which never throws for a bad file).
/// </summary>
public interface ICadReferenceScanner
{
    CadReferenceScan Scan(string rootAbsolutePath);
}

public sealed class CadReferenceScanner : ICadReferenceScanner
{
    private readonly IDocumentReferenceSource _source;
    private readonly IReadOnlyList<string> _workspaceRoots;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, WorkspaceManifest?> _manifestCache =
        new(StringComparer.OrdinalIgnoreCase);

    public CadReferenceScanner(
        IDocumentReferenceSource source,
        IEnumerable<string?> workspaceRoots,
        Func<DateTimeOffset>? clock = null)
    {
        _source = source;
        _workspaceRoots = (workspaceRoots ?? Array.Empty<string?>())
            .Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r))
            .Select(r => SafeFullPath(r!) ?? r!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public CadReferenceScan Scan(string rootAbsolutePath)
    {
        var rootKey = SafeFullPath(rootAbsolutePath)
            ?? throw new ArgumentException("The scan root must be an absolute path.", nameof(rootAbsolutePath));

        var rootType = CadDocumentTypes.FromPath(rootKey);
        var rootIdentity = ManifestIdentityFor(rootKey)?.ToPlmIdentity();

        var references = new List<CadReference>();
        var nodes = new List<CadReferenceNode>();
        var typeAndIdentity = new Dictionary<string, NodeInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [rootKey] = new NodeInfo(rootType, rootIdentity),
        };
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edgeSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(rootKey);

        while (queue.Count > 0)
        {
            var parentKey = queue.Dequeue();
            if (!expanded.Add(parentKey))
            {
                continue; // already expanded - cycle / repeated sub-assembly guard
            }

            var parent = typeAndIdentity.TryGetValue(parentKey, out var info)
                ? info
                : new NodeInfo(CadDocumentTypes.FromPath(parentKey), null);

            var enumeration = _source.GetDirectReferences(parentKey)
                ?? DocumentReferenceEnumeration.Unavailable;

            // Record the visited document + whether Inventor could actually
            // describe it. "Unavailable" is NOT "a leaf with zero children" -
            // the sub-graph below it is simply unknown, so we neither add edges
            // for it nor recurse into it.
            nodes.Add(new CadReferenceNode(
                parentKey,
                parent.Type,
                enumeration.Status == DocumentEnumerationStatus.Enumerated));

            if (enumeration.Status != DocumentEnumerationStatus.Enumerated)
            {
                continue;
            }

            foreach (var raw in enumeration.References ?? Array.Empty<RawDocumentReference>())
            {
                var resolvedKey = raw.IsResolved ? SafeFullPath(raw.ResolvedFullPath) : null;
                var isResolved = resolvedKey is not null;

                var dedupeKey = parentKey + " -> "
                    + (resolvedKey ?? raw.ReportedName ?? "")
                    + (isResolved ? " [resolved]" : " [unresolved]");
                if (!edgeSeen.Add(dedupeKey))
                {
                    continue; // exact duplicate edge under the same parent
                }

                var childType = isResolved
                    ? PreferKnown(CadDocumentTypes.FromPath(resolvedKey!), raw.ReportedType)
                    : PreferKnown(CadDocumentTypes.FromPath(raw.ReportedName), raw.ReportedType);

                var scope = isResolved
                    ? WorkspaceScopeResolver.Locate(resolvedKey, _workspaceRoots)
                    : new WorkspaceScopeResolver.Location(ReferenceWorkspaceScope.Unknown, null);

                CadManifestIdentity? manifestIdentity = null;
                if (isResolved
                    && scope.Scope == ReferenceWorkspaceScope.InsideWorkspace
                    && scope.ContainingRoot is not null)
                {
                    manifestIdentity = ManifestIdentityFor(resolvedKey!, scope.ContainingRoot);
                }

                references.Add(new CadReference
                {
                    ParentAbsolutePath = parentKey,
                    ParentIdentity = parent.Identity,
                    InventorReportedName = string.IsNullOrWhiteSpace(raw.ReportedName)
                        ? "(unnamed reference)"
                        : raw.ReportedName.Trim(),
                    InventorReportedFullPath = string.IsNullOrWhiteSpace(raw.ReportedFullPath)
                        ? null
                        : raw.ReportedFullPath.Trim(),
                    ResolvedAbsolutePath = resolvedKey,
                    ReferenceType = childType,
                    RelationshipKind = CadRelationshipClassifier.Classify(parent.Type, childType),
                    Resolution = isResolved ? CadReferenceResolution.Resolved : CadReferenceResolution.Unresolved,
                    Scope = scope.Scope,
                    ManifestIdentity = manifestIdentity,
                });

                if (isResolved)
                {
                    if (!typeAndIdentity.ContainsKey(resolvedKey!))
                    {
                        typeAndIdentity[resolvedKey!] =
                            new NodeInfo(childType, manifestIdentity?.ToPlmIdentity());
                    }
                    if (!expanded.Contains(resolvedKey!))
                    {
                        queue.Enqueue(resolvedKey!);
                    }
                }
            }
        }

        return new CadReferenceScan(
            new CadReferenceRoot(rootKey, rootType, rootIdentity),
            references,
            nodes,
            _clock());
    }

    private readonly record struct NodeInfo(CadDocumentType Type, PlmIdentity? Identity);

    private static CadDocumentType PreferKnown(CadDocumentType primary, CadDocumentType fallback) =>
        primary != CadDocumentType.Unknown ? primary : fallback;

    private CadManifestIdentity? ManifestIdentityFor(string absolutePath, string? containingRoot = null)
    {
        var root = containingRoot
            ?? WorkspaceScopeResolver.Locate(absolutePath, _workspaceRoots).ContainingRoot;
        if (root is null)
        {
            return null;
        }

        var manifest = ManifestFor(root);
        var entry = manifest?.FindByAbsolutePath(absolutePath);
        return entry is null
            ? null
            : new CadManifestIdentity(entry.CadDocumentId, entry.FileVersionId, entry.DocumentNumber, entry.RelativePath);
    }

    private WorkspaceManifest? ManifestFor(string root)
    {
        if (_manifestCache.TryGetValue(root, out var cached))
        {
            return cached;
        }

        WorkspaceManifest? manifest;
        try
        {
            manifest = WorkspaceManifest.LoadOrEmpty(root);
        }
        catch (Exception ex) when (ex is IOException or WorkspaceRootException or UnauthorizedAccessException)
        {
            manifest = null;
        }

        _manifestCache[root] = manifest;
        return manifest;
    }

    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

file static class ManifestIdentityExtensions
{
    public static PlmIdentity ToPlmIdentity(this CadManifestIdentity identity) => new(
        identity.CadDocumentId,
        string.IsNullOrEmpty(identity.DocumentNumber) ? null : identity.DocumentNumber,
        string.IsNullOrEmpty(identity.FileVersionId) ? null : identity.FileVersionId,
        null);
}
