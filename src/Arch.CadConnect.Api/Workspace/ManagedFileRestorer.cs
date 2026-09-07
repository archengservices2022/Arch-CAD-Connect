using System.Security.Cryptography;

using Arch.CadConnect.Core.Files;
using Arch.CadConnect.Core.Workspace;

namespace Arch.CadConnect.Api.Workspace;

/// <summary>
/// Downloads ONE exact FileVersion and puts it back on disk as the
/// authoritative managed copy. Used by Undo Checkout, which - unlike Get Latest
/// - is allowed to OVERWRITE the working file (that is the point of undo).
///
/// The two steps are deliberately separate so the orchestrator can honour the
/// filesystem-vs-PostgreSQL ordering rule (see <see cref="CheckoutOrchestrator"/>):
///
///   1. <see cref="StageBaseVersionAsync"/> - download + fully verify into
///      <c>.arch\.staging\*.part</c> WHILE the server checkout is still held.
///      Any download / size / SHA-256 / header failure throws here and the
///      server checkout is therefore NEVER released.
///   2. <see cref="Promote"/> - atomically replace the working file with the
///      already-verified staged bytes and mark it controlled. Called only
///      AFTER the server has confirmed the checkout release.
/// </summary>
public sealed class ManagedFileRestorer
{
    private const string StagingDirName = @".arch\.staging";

    private readonly HttpContentDownloader _downloader;

    public ManagedFileRestorer(HttpContentDownloader downloader)
        => _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));

    /// <summary>
    /// Download <paramref name="fileVersionId"/> to a staged temp file under
    /// <paramref name="workspaceRoot"/> and verify it against the expected
    /// size + SHA-256 (and the server's own <c>X-Content-SHA256</c> header when
    /// present). Throws on ANY failure, leaving no partial file.
    /// </summary>
    public async Task<StagedRestore> StageBaseVersionAsync(
        string workspaceRoot,
        string fileVersionId,
        string expectedSha256,
        long expectedSize,
        CancellationToken ct = default)
    {
        SafeWorkspacePath.RequireAbsoluteRoot(workspaceRoot);
        var stagingDir = Path.Combine(Path.GetFullPath(workspaceRoot), StagingDirName);
        Directory.CreateDirectory(stagingDir);
        var stagedPath = Path.Combine(stagingDir, Guid.NewGuid().ToString("N") + ".part");

        var contentPath = $"/api/file-versions/{fileVersionId}/content";
        var download = await _downloader.OpenContentWithMetadataAsync(contentPath, ct).ConfigureAwait(false);

        try
        {
            using var sha = SHA256.Create();
            long size = 0;

            await using (var file = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await download.Body.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    if (size > expectedSize)
                    {
                        throw new RestoreVerificationException(
                            $"The base version download exceeded its expected size of {expectedSize} bytes.");
                    }
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                sha.TransformFinalBlock([], 0, 0);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }
            await download.Body.DisposeAsync().ConfigureAwait(false);

            var actualSha = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

            if (size != expectedSize)
            {
                throw new RestoreVerificationException(
                    $"The base version download was {size} bytes; expected {expectedSize}.");
            }
            if (!string.Equals(actualSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new RestoreVerificationException("The base version download failed its SHA-256 check.");
            }
            if (download.ServerSha256 is { Length: > 0 }
                && !string.Equals(download.ServerSha256, actualSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new RestoreVerificationException(
                    "The base version download did not match the server's own X-Content-SHA256.");
            }

            return new StagedRestore(stagedPath, actualSha, size);
        }
        catch
        {
            try { await download.Body.DisposeAsync().ConfigureAwait(false); } catch { /* already failing */ }
            TryDelete(stagedPath);
            throw;
        }
    }

    /// <summary>
    /// Atomically replace <paramref name="absoluteTargetPath"/> with the
    /// verified staged bytes, then mark it controlled (read-only). On success
    /// the staged file is consumed. On failure the staged file is KEPT (the
    /// caller decides whether to surface it for manual recovery) and
    /// <see cref="RestorePromoteException"/> is thrown.
    /// </summary>
    public static void Promote(StagedRestore staged, string absoluteTargetPath)
    {
        if (!File.Exists(staged.StagedPath))
        {
            throw new RestorePromoteException("The staged recovery file is missing.");
        }

        try
        {
            var dir = Path.GetDirectoryName(absoluteTargetPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            // The working file may be read-only (controlled) - clear it so the
            // replace can proceed, then re-assert controlled after.
            ManagedFileGuard.SetWritable(absoluteTargetPath);
            File.Move(staged.StagedPath, absoluteTargetPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RestorePromoteException(
                "Could not replace the local file with the restored version.", ex);
        }

        ManagedFileGuard.SetControlled(absoluteTargetPath);
    }

    public static void Discard(StagedRestore staged) => TryDelete(staged.StagedPath);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}

/// <summary>A downloaded + fully verified base FileVersion, staged and ready to
///  promote once the server has confirmed the checkout release.</summary>
public sealed record StagedRestore(string StagedPath, string Sha256, long Size);

/// <summary>The base-version download could not be verified. Thrown from
///  <see cref="ManagedFileRestorer.StageBaseVersionAsync"/> - i.e. BEFORE any
///  server call - so the server checkout is never released on a bad download.</summary>
public sealed class RestoreVerificationException(string message) : Exception(message);

/// <summary>The verified staged bytes could not be moved onto the working
///  file (e.g. it is open in Inventor). Thrown from
///  <see cref="ManagedFileRestorer.Promote"/> - i.e. AFTER the server released
///  the checkout - so the caller must mark the manifest Unverified.</summary>
public sealed class RestorePromoteException(string message, Exception? inner = null) : Exception(message, inner);
