namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// CODEX FINAL AUDIT ROUND 1, HIGH 4 fix: the atomic "write to an operation-
/// owned temporary file next to the final destination, then promote temp -&gt;
/// final with NO overwrite" pattern that closes the
/// <c>File.Exists</c>-then-write race a physical-copy adapter (Inventor
/// SaveAs, or any other) would otherwise have. Pure filesystem I/O only - no
/// COM - so this is directly unit-testable with REAL temporary files, unlike
/// the Inventor adapter that calls it.
///
/// The promotion step (<see cref="PromoteToFinal"/>) is the ONLY point that
/// can lose a race to a concurrently-created final destination, and losing
/// it is reported as a clean failure - never a silent overwrite, never an
/// auto-rename, and the racing file at the final path is NEVER touched or
/// deleted. On any failure, ONLY the operation-owned temp file is ever
/// removed.
///
/// CODEX ROUND 3, MEDIUM fix: temp cleanup is no longer a silent best-effort
/// - <see cref="CleanUpTempOnly"/> now CONFIRMS (via a follow-up
/// <c>File.Exists</c>) and reports whether the temp file is actually gone,
/// and <see cref="PromotionResult.UnremovedTempPath"/> surfaces the exact
/// path when a promotion failure leaves one behind, so a failed automatic
/// cleanup is never silently claimed as complete.
/// </summary>
public static class CopyDesignAtomicPromotion
{
    /// <summary>A unique, operation-owned temp file name in the SAME
    ///  directory as <paramref name="finalDestinationAbsolutePath"/> - same
    ///  directory so <see cref="PromoteToFinal"/> is a same-volume rename
    ///  (atomic on NTFS), never a cross-volume copy. The final destination's
    ///  own name/extension stays embedded for diagnosability, but the
    ///  leading dot and GUID make it unambiguously operation-owned - nothing
    ///  else should ever create a file matching this pattern.</summary>
    public static string NewTempPath(string finalDestinationAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(finalDestinationAbsolutePath) || !Path.IsPathFullyQualified(finalDestinationAbsolutePath))
        {
            throw new ArgumentException("The final destination must be a fully-qualified path.", nameof(finalDestinationAbsolutePath));
        }
        var dir = Path.GetDirectoryName(finalDestinationAbsolutePath);
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new ArgumentException("The final destination has no usable directory.", nameof(finalDestinationAbsolutePath));
        }
        var name = Path.GetFileName(finalDestinationAbsolutePath);
        return Path.Combine(dir, $".p6c-tmp-{Guid.NewGuid():N}-{name}");
    }

    public sealed record PromotionResult(
        bool Success,
        string? FailureReason,
        /// <summary>CODEX ROUND 3, MEDIUM fix: present ONLY when this
        ///  failure left an operation-owned temp artifact that cleanup could
        ///  NOT confirm was removed - the exact path, for manual-cleanup
        ///  reporting. Always <c>null</c> on success, and always <c>null</c>
        ///  on a failure where cleanup DID confirm removal (or there was
        ///  never a temp file to begin with).</summary>
        string? UnremovedTempPath = null)
    {
        public static readonly PromotionResult Ok = new(true, null);
        public static PromotionResult Fail(string reason, string? unremovedTempPath = null) => new(false, reason, unremovedTempPath);
    }

    /// <summary>Promotes <paramref name="tempAbsolutePath"/> to
    ///  <paramref name="finalDestinationAbsolutePath"/> via a move that FAILS
    ///  (never overwrites) if the final path already exists. On ANY failure
    ///  - including having lost the race - cleanup of the temp file (and
    ///  ONLY the temp file) is attempted and its outcome is reported via
    ///  <see cref="PromotionResult.UnremovedTempPath"/> - never silently
    ///  assumed to have succeeded; a pre-existing/racing file at the final
    ///  path is NEVER deleted, NEVER touched.</summary>
    public static PromotionResult PromoteToFinal(string tempAbsolutePath, string finalDestinationAbsolutePath)
    {
        ArgumentNullException.ThrowIfNull(tempAbsolutePath);
        ArgumentNullException.ThrowIfNull(finalDestinationAbsolutePath);

        if (!File.Exists(tempAbsolutePath))
        {
            return PromotionResult.Fail("The operation-owned temporary file does not exist - nothing to promote.");
        }
        try
        {
            File.Move(tempAbsolutePath, finalDestinationAbsolutePath, overwrite: false);
            return PromotionResult.Ok;
        }
        catch (IOException ex)
        {
            // Most commonly: the final destination now exists (lost the
            // race to a concurrent writer) - it is NOT this operation's
            // file and is never deleted.
            var cleanedUp = CleanUpTempOnly(tempAbsolutePath);
            return PromotionResult.Fail(
                $"Could not promote the copy to its final destination - it may already exist: {ex.Message}",
                unremovedTempPath: cleanedUp ? null : tempAbsolutePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            var cleanedUp = CleanUpTempOnly(tempAbsolutePath);
            return PromotionResult.Fail(
                $"Could not promote the copy to its final destination: {ex.Message}",
                unremovedTempPath: cleanedUp ? null : tempAbsolutePath);
        }
    }

    /// <summary>Attempts to delete an operation-owned TEMP file ONLY - never
    ///  call this with a final destination path. Never throws. CODEX ROUND 3,
    ///  MEDIUM fix: the outcome is now OBSERVABLE - returns <c>true</c> only
    ///  when the temp path is CONFIRMED gone afterward (already absent, or
    ///  successfully deleted), <c>false</c> when it could not be confirmed
    ///  removed (e.g. still locked) - a caller must never assume cleanup
    ///  completed just because this didn't throw.</summary>
    public static bool CleanUpTempOnly(string tempAbsolutePath)
    {
        try
        {
            if (File.Exists(tempAbsolutePath))
            {
                File.Delete(tempAbsolutePath);
            }
        }
        catch
        {
            // Fall through to the confirmation check below rather than
            // trusting the exception alone - either way, the caller gets a
            // truthful answer from the filesystem itself.
        }
        return !File.Exists(tempAbsolutePath);
    }
}
