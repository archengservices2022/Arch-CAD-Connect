using System.Security.Cryptography;

using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Workspace;

/// <summary>
/// Materializes an already-fetched, validated <see cref="WorkspacePlan"/>
/// onto the local filesystem under a caller-supplied absolute workspace root.
/// A faithful port of <c>web/app/lib/workspace-materialize-core.ts</c>.
///
/// INVARIANTS
///   - The server / object storage stays authoritative. Nothing here mutates
///     server state or creates an authoritative local database.
///   - Only the EXACT pinned <c>version.contentPath</c> is downloaded - never
///     a re-resolved "latest".
///   - Per entry: validate contentPath -> resolve a safe root-relative flat
///     path -> (existing file? same checksum = already-current, different =
///     blocked, never overwritten) -> stream to a temp file -> verify EXACT
///     byte size AND SHA-256 -> atomic CREATE-NEW promote (never overwrite).
///   - A problem with one entry never aborts the others. Every entry gets a
///     result.
///   - The whole operation is refused (throws) when the plan's contract is
///     wrong or <c>plan.Safe</c> is not true.
/// </summary>
public sealed class WorkspaceMaterializer
{
    private const string StagingDirName = @".arch\.staging";

    public async Task<MaterializationReport> MaterializeAsync(
        WorkspacePlan plan,
        string workspaceRoot,
        IContentDownloader downloader,
        CancellationToken ct = default,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(downloader);
        SafeWorkspacePath.RequireAbsoluteRoot(workspaceRoot);
        var now = clock ?? (() => DateTimeOffset.UtcNow);

        if (plan.Contract.Id != WorkspacePlanContract.Id
            || !WorkspacePlanContract.IsSupportedVersion(plan.Contract.Version))
        {
            throw new UnsupportedWorkspaceContractException(
                $"Unsupported workspace contract: \"{plan.Contract.Id}\" / \"{plan.Contract.Version}\".");
        }
        if (!plan.Safe)
        {
            throw new UnsafeWorkspacePlanException(plan.Problems);
        }

        var root = Path.GetFullPath(workspaceRoot);
        var startedAt = now();

        // Case-insensitive cross-entry collision pre-pass (NTFS). A `safe` plan
        // should never contain one - defense in depth.
        var colliding = FindCollidingCadDocumentIds(plan, root);

        var results = new List<MaterializationEntryResult>(plan.Entries.Count);
        foreach (var entry in plan.Entries)
        {
            if (colliding.Contains(entry.CadDocumentId))
            {
                results.Add(Base(entry) with
                {
                    RelativePath = entry.Placement.GeneratedRelativePath,
                    Status = MaterializationStatus.Blocked,
                    Reason = "local-path-collision: another entry in this plan resolves to the same local path (case-insensitive)",
                });
                continue;
            }
            results.Add(await MaterializeEntryAsync(entry, root, downloader, ct).ConfigureAwait(false));
        }

        return new MaterializationReport
        {
            WorkspaceRoot = root,
            RootCadDocumentId = plan.Root.CadDocumentId,
            StartedAtUtc = startedAt,
            FinishedAtUtc = now(),
            RequestedEntries = plan.Entries.Count,
            Results = results,
        };
    }

    private static async Task<MaterializationEntryResult> MaterializeEntryAsync(
        WorkspacePlanEntry entry,
        string root,
        IContentDownloader downloader,
        CancellationToken ct)
    {
        var baseResult = Base(entry);

        if (!entry.CanMaterialize || entry.Version is null)
        {
            return baseResult with
            {
                RelativePath = null,
                Status = MaterializationStatus.Blocked,
                Reason = entry.BlockedReasons.Count > 0
                    ? $"Entry cannot be materialized: {string.Join(", ", entry.BlockedReasons)}"
                    : "Entry has no materializable version",
            };
        }

        var version = entry.Version;

        if (version.ContentPath != $"/api/file-versions/{version.FileVersionId}/content")
        {
            return baseResult with
            {
                RelativePath = null,
                Status = MaterializationStatus.Blocked,
                Reason = "Refusing an untrusted contentPath: expected the authenticated file-version content endpoint",
            };
        }

        string absolutePath;
        try
        {
            absolutePath = SafeWorkspacePath.ResolveWithinRoot(root, entry.Placement.GeneratedRelativePath);
        }
        catch (Exception ex) when (ex is UnsafeWorkspacePathException or WorkspaceRootException)
        {
            return baseResult with
            {
                RelativePath = entry.Placement.GeneratedRelativePath,
                Status = MaterializationStatus.Blocked,
                Reason = ex.Message,
            };
        }

        var relativePath = entry.Placement.GeneratedRelativePath;

        if (File.Exists(absolutePath))
        {
            (string Sha256, long Size) existing;
            try
            {
                existing = await HashFileAsync(absolutePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return baseResult with
                {
                    RelativePath = relativePath,
                    Status = MaterializationStatus.Failed,
                    Reason = $"Could not inspect the existing local file: {ex.GetType().Name}",
                };
            }

            if (existing.Sha256 == version.Checksum && existing.Size == version.FileSize)
            {
                // P4C: re-assert "controlled" on an already-current file (a
                // prior run may have left it writable). A file the user
                // currently holds a checkout on is a local-conflict below, not
                // here, so this only ever touches genuinely-current copies.
                Core.Files.ManagedFileGuard.SetControlled(absolutePath);
                return baseResult with
                {
                    RelativePath = relativePath,
                    Status = MaterializationStatus.AlreadyCurrent,
                    Checksum = existing.Sha256,
                    BytesWritten = 0,
                };
            }

            return baseResult with
            {
                RelativePath = relativePath,
                Status = MaterializationStatus.Blocked,
                Reason = "local-conflict: an existing file at this path differs from the pinned FileVersion and was not overwritten",
            };
        }

        string stagedPath;
        string stagedSha256;
        long stagedSize;
        try
        {
            (stagedPath, stagedSha256, stagedSize) = await StageDownloadAsync(
                root, downloader, version.ContentPath, version.FileSize, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return baseResult with
            {
                RelativePath = relativePath,
                Status = MaterializationStatus.Failed,
                Reason = $"Download failed: {DescribeDownloadError(ex)}",
            };
        }

        try
        {
            if (stagedSize != version.FileSize || stagedSha256 != version.Checksum)
            {
                return baseResult with
                {
                    RelativePath = relativePath,
                    Status = MaterializationStatus.Failed,
                    Reason = $"Integrity check failed: expected {version.FileSize} bytes / {version.Checksum}, "
                           + $"got {stagedSize} bytes / {stagedSha256}",
                };
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
                // overwrite:false -> throws IOException if the target exists.
                File.Move(stagedPath, absolutePath, overwrite: false);
            }
            catch (IOException)
            {
                return baseResult with
                {
                    RelativePath = relativePath,
                    Status = MaterializationStatus.Blocked,
                    Reason = "local-conflict: a file appeared at this path during materialization and was not overwritten",
                };
            }

            // P4C: a freshly materialized, verified managed file is
            // "controlled" (read-only) until the user checks it out.
            Core.Files.ManagedFileGuard.SetControlled(absolutePath);

            return baseResult with
            {
                RelativePath = relativePath,
                Status = MaterializationStatus.Downloaded,
                Checksum = stagedSha256,
                BytesWritten = stagedSize,
            };
        }
        finally
        {
            TryDelete(stagedPath);
        }
    }

    /// <summary>
    /// Stream <paramref name="contentPath"/> into a fresh
    /// <c>.arch\.staging\&lt;guid&gt;.part</c> file, hashing and size-capping as
    /// it goes. On SUCCESS the staged path is returned for the caller's
    /// verify + atomic-promote step (which then consumes / deletes it). On ANY
    /// unsuccessful path - an oversized response, a source read fault, a
    /// destination write/flush fault, a stream-disposal fault, or cancellation
    /// - the staged <c>.part</c> file is removed before the exception
    /// propagates. Cleanup is best-effort and never masks the original error;
    /// a partial file is never returned and never promoted.
    /// </summary>
    private static async Task<(string Path, string Sha256, long Size)> StageDownloadAsync(
        string root,
        IContentDownloader downloader,
        string contentPath,
        long maxBytes,
        CancellationToken ct)
    {
        var stagingDir = Path.Combine(root, StagingDirName);
        Directory.CreateDirectory(stagingDir);
        var stagedPath = Path.Combine(stagingDir, Guid.NewGuid().ToString("N") + ".part");

        var source = await downloader.OpenContentStreamAsync(contentPath, ct).ConfigureAwait(false);
        try
        {
            using var sha = SHA256.Create();
            long size = 0;

            await using (var file = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    if (size > maxBytes)
                    {
                        throw new DownloadTooLargeException(maxBytes);
                    }
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                sha.TransformFinalBlock([], 0, 0);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }

            // Dispose the source INSIDE the guarded region so a disposal fault
            // is handled exactly like a read fault (staged file removed).
            await source.DisposeAsync().ConfigureAwait(false);

            return (stagedPath, Convert.ToHexString(sha.Hash!).ToLowerInvariant(), size);
        }
        catch
        {
            // ANY unsuccessful path - oversized response, a source read fault,
            // a destination write/flush fault, a stream-disposal fault, or
            // cancellation - removes the staged .part file before the
            // exception propagates. The secondary dispose + the delete are
            // best-effort and never mask the original error; a partial file is
            // never returned and never promoted.
            try { await source.DisposeAsync().ConfigureAwait(false); }
            catch { /* the real error is already in flight */ }
            TryDelete(stagedPath);
            throw;
        }
    }

    private static async Task<(string Sha256, long Size)> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return (Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
    }

    private static HashSet<string> FindCollidingCadDocumentIds(WorkspacePlan plan, string root)
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan.Entries)
        {
            if (!entry.CanMaterialize) continue;
            try
            {
                var abs = SafeWorkspacePath.ResolveWithinRoot(root, entry.Placement.GeneratedRelativePath);
                var key = abs.ToLowerInvariant();
                if (!owners.TryGetValue(key, out var list)) { list = []; owners[key] = list; }
                list.Add(entry.CadDocumentId);
            }
            catch
            {
                // reported again inside MaterializeEntryAsync
            }
        }
        var colliding = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in owners.Values)
        {
            if (list.Count > 1)
            {
                foreach (var id in list) colliding.Add(id);
            }
        }
        return colliding;
    }

    private static MaterializationEntryResult Base(WorkspacePlanEntry entry) => new()
    {
        CadDocumentId = entry.CadDocumentId,
        FileVersionId = entry.Version?.FileVersionId,
        RelativePath = null,
        Status = MaterializationStatus.Blocked,
        IsRoot = entry.IsRoot,
        DocumentNumber = entry.DocumentNumber,
    };

    private static string DescribeDownloadError(Exception ex) => ex switch
    {
        ContentDownloadTimeoutException => "the transfer timed out",
        ContentDownloadRedirectException => "the download was redirected (refused)",
        ContentDownloadHttpException h => $"HTTP {h.Status}",
        UnsafeContentPathException => "an unexpected content path (refused)",
        DownloadTooLargeException => "the response exceeded the expected size",
        _ => ex.GetType().Name,
    };

    /// <summary>Best-effort removal of a staged temp file. Swallows EVERY
    ///  failure - it runs in `finally` blocks and must never mask the original
    ///  download / integrity error, nor throw from a cleanup path.</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort - never let a cleanup failure surface */ }
    }
}

public sealed class DownloadTooLargeException(long maxBytes)
    : Exception($"Downloaded content exceeds the expected size of {maxBytes} bytes.");
