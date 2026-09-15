using Arch.CadConnect.Core.Workspace;
using System.Security.Cryptography;

namespace Arch.CadConnect.Core.References;

/// <summary>
/// The default <see cref="IRepairTargetLocator"/>: it consults the SAME
/// verified workspace manifests the P5A scan / P4B Get Latest already use. For a
/// target <c>(cadDocumentId, authoritative latest fileVersionId)</c> it accepts
/// a path ONLY when an exact manifest entry
/// (<c>root + relativePath</c>) has that exact <c>cadDocumentId</c> AND that
/// exact <c>fileVersionId</c>, is <see cref="WorkspaceManifestEntryState.Verified"/>,
/// and the file is present on disk.
///
/// It NEVER:
///  - matches by filename, display name, path similarity, timestamp or version
///    number;
///  - downloads anything or runs Get Latest;
///  - picks "the newest looking" file when more than one path claims the exact
///    identity (that is <see cref="RepairTargetOutcome.AmbiguousTargets"/>).
///
/// The only I/O is reading <c>.arch\workspace.json</c> under the supplied roots
/// (via <see cref="WorkspaceManifest.LoadOrEmpty"/>, which never throws for a
/// bad file) plus a <see cref="File.Exists(string)"/> probe.
/// </summary>
public sealed class WorkspaceManifestRepairTargetLocator : IRepairTargetLocator
{
    private readonly IReadOnlyList<string> _workspaceRoots;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, long, string, bool> _binaryMatches;

    public WorkspaceManifestRepairTargetLocator(
        IEnumerable<string?> workspaceRoots,
        Func<string, bool>? fileExists = null,
        Func<string, long, string, bool>? binaryMatches = null)
    {
        _workspaceRoots = (workspaceRoots ?? Array.Empty<string?>())
            .Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r))
            .Select(r => SafeFullPath(r!) ?? r!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _fileExists = fileExists ?? SafeFileExists;
        _binaryMatches = binaryMatches ?? BinaryMatches;
    }

    public RepairTargetResolution Locate(
        string cadDocumentId,
        string authoritativeLatestFileVersionId,
        long serverFileSize,
        string serverSha256)
    {
        // Opaque stable identifiers - used verbatim. A blank / whitespace-padded
        // id is malformed; there is nothing safe to resolve.
        if (IsMalformed(cadDocumentId) || IsMalformed(authoritativeLatestFileVersionId))
        {
            return RepairTargetResolution.Failure(
                RepairTargetOutcome.NotAttempted,
                "The target identity (cadDocumentId / fileVersionId) is missing or malformed.");
        }

        // The SERVER-authoritative integrity metadata is the ONLY trusted
        // authority for the target binary. Without a canonical pair there is
        // nothing safe to verify the local bytes against.
        if (!FileVersionIntegrity.IsRepresentableFileSize(serverFileSize)
            || !FileVersionIntegrity.IsCanonicalSha256(serverSha256))
        {
            return RepairTargetResolution.Failure(
                RepairTargetOutcome.NoServerIntegrityMetadata,
                "No canonical server-authoritative FileVersion integrity metadata (size + SHA-256) is available.");
        }

        var sawCadDocument = false;
        var verifiedMatches = new List<RepairTargetCandidate>();
        var unverifiedMatch = false;
        var manifestServerMismatch = false;

        foreach (var root in _workspaceRoots)
        {
            WorkspaceManifest manifest;
            try
            {
                manifest = WorkspaceManifest.LoadOrEmpty(root);
            }
            catch (Exception ex) when (ex is IOException or WorkspaceRootException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in manifest.Entries)
            {
                if (!string.Equals(entry.CadDocumentId, cadDocumentId, StringComparison.Ordinal))
                {
                    continue;
                }
                sawCadDocument = true;

                if (!string.Equals(entry.FileVersionId, authoritativeLatestFileVersionId, StringComparison.Ordinal))
                {
                    continue; // a managed copy, but not the authoritative latest FileVersion
                }

                string absolutePath;
                try
                {
                    absolutePath = SafeWorkspacePath.ResolveWithinRoot(root, entry.RelativePath);
                }
                catch (Exception ex) when (ex is UnsafeWorkspacePathException or WorkspaceRootException)
                {
                    continue;
                }

                if (entry.State != WorkspaceManifestEntryState.Verified)
                {
                    unverifiedMatch = true;
                    continue;
                }

                // The mutable manifest is used ONLY for the stable local path
                // mapping above. Its own recorded size/checksum are a
                // cross-check: if they disagree with the server-authoritative
                // values we fail closed - we never repair the manifest and never
                // trust it over the server.
                var manifestAgrees =
                    entry.FileSize == serverFileSize
                    && string.Equals(entry.Checksum, serverSha256, StringComparison.OrdinalIgnoreCase);
                if (!manifestAgrees)
                {
                    manifestServerMismatch = true;
                }

                var exists = _fileExists(absolutePath);
                var integrityVerified = false;
                if (exists && manifestAgrees)
                {
                    try
                    {
                        // Verify the file's CURRENT bytes against the
                        // SERVER-authoritative size + SHA-256 - not the manifest.
                        integrityVerified = _binaryMatches(absolutePath, serverFileSize, serverSha256);
                    }
                    catch
                    {
                        integrityVerified = false;
                    }
                }

                verifiedMatches.Add(new RepairTargetCandidate(
                    CadDocumentId: entry.CadDocumentId,
                    FileVersionId: entry.FileVersionId,
                    AbsolutePath: absolutePath,
                    IdentitySource: RepairTargetIdentitySource.WorkspaceManifestVerified,
                    ExistsOnDisk: exists,
                    WorkspaceRoot: root,
                    BinaryIntegrityVerified: integrityVerified,
                    ManifestAgreesWithServer: manifestAgrees,
                    ServerFileSize: serverFileSize,
                    ServerSha256: serverSha256));
            }
        }

        var distinctPaths = verifiedMatches
            .Select(c => c.AbsolutePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinctPaths.Length > 1)
        {
            return RepairTargetResolution.Failure(
                RepairTargetOutcome.AmbiguousTargets,
                "More than one managed workspace binds the exact target FileVersion to a different path: "
                + string.Join(", ", distinctPaths));
        }

        if (distinctPaths.Length == 1)
        {
            var candidate = verifiedMatches.First(c =>
                string.Equals(c.AbsolutePath, distinctPaths[0], StringComparison.OrdinalIgnoreCase));
            if (!candidate.ManifestAgreesWithServer)
            {
                return RepairTargetResolution.Failure(
                    RepairTargetOutcome.TargetManifestServerMismatch,
                    "The local workspace manifest's recorded size / checksum for the target FileVersion disagree "
                    + $"with the server-authoritative values: {candidate.AbsolutePath}. "
                    + "Run Get Latest to re-establish a trusted local copy - P5C never reconciles the manifest.");
            }
            if (!candidate.ExistsOnDisk)
            {
                return RepairTargetResolution.Failure(
                    RepairTargetOutcome.TargetFileMissing,
                    $"A verified manifest entry matches the target FileVersion but the file is missing: {candidate.AbsolutePath}");
            }
            return candidate.BinaryIntegrityVerified
                ? RepairTargetResolution.Found(candidate,
                    "Exact workspace-manifest path mapping and current size + SHA-256 verification against the "
                    + "server-authoritative FileVersion metadata passed.")
                : RepairTargetResolution.Failure(
                    RepairTargetOutcome.TargetBinaryVerificationFailed,
                    $"The target file's current bytes do not match the server-authoritative size / SHA-256: {candidate.AbsolutePath}");
        }

        if (unverifiedMatch)
        {
            return RepairTargetResolution.Failure(
                RepairTargetOutcome.TargetCopyUnverified,
                "A local copy matches the target FileVersion but was never verified. Run Get Latest, then retry.");
        }

        if (manifestServerMismatch)
        {
            return RepairTargetResolution.Failure(
                RepairTargetOutcome.TargetManifestServerMismatch,
                "The local workspace manifest's recorded size / checksum for the target FileVersion disagree with "
                + "the server-authoritative values. Run Get Latest - P5C never reconciles the manifest.");
        }

        return sawCadDocument
            ? RepairTargetResolution.Failure(
                RepairTargetOutcome.TargetVersionNotLocal,
                "A managed copy of the target document exists locally, but not at the authoritative latest FileVersion. "
                + "Run Get Latest for that document, then retry.")
            : RepairTargetResolution.Failure(
                RepairTargetOutcome.NoManagedCopyFound,
                "No managed workspace on this machine binds the target document. Run Get Latest, then retry.");
    }

    private static bool IsMalformed(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1]);

    private static bool SafeFileExists(string path)
    {
        try { return !string.IsNullOrEmpty(path) && File.Exists(path); }
        catch { return false; }
    }

    private static bool BinaryMatches(string path, long expectedSize, string expectedChecksum)
    {
        if (expectedSize < 0 || string.IsNullOrWhiteSpace(expectedChecksum))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != expectedSize)
        {
            return false;
        }
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedChecksum, StringComparison.OrdinalIgnoreCase);
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
