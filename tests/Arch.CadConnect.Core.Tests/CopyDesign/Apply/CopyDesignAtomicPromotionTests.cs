using Arch.CadConnect.Core.CopyDesign.Apply;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

/// <summary>CODEX FINAL AUDIT ROUND 1, HIGH 4: the pure filesystem boundary
///  of the atomic temp-then-promote pattern, exercised against REAL
///  temporary files/directories - no COM, no fakes needed, since this is
///  genuine .NET/NTFS behavior the fix depends on.</summary>
public class CopyDesignAtomicPromotionTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "p6c-atomic-promotion-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void NewTempPath_is_in_the_SAME_directory_as_the_final_destination()
    {
        var final = @"C:\dest\folder\FOO.ipt";
        var temp = CopyDesignAtomicPromotion.NewTempPath(final);

        Assert.Equal(Path.GetDirectoryName(final), Path.GetDirectoryName(temp));
        Assert.NotEqual(final, temp);
    }

    [Fact]
    public void NewTempPath_is_unique_across_calls()
    {
        var final = @"C:\dest\FOO.ipt";
        Assert.NotEqual(CopyDesignAtomicPromotion.NewTempPath(final), CopyDesignAtomicPromotion.NewTempPath(final));
    }

    [Fact]
    public void NewTempPath_rejects_a_non_fully_qualified_path()
    {
        Assert.Throws<ArgumentException>(() => CopyDesignAtomicPromotion.NewTempPath("relative\\path.ipt"));
    }

    // ---- item: final absent -> success ------------------------------------

    [Fact]
    public void PromoteToFinal_succeeds_when_the_final_destination_is_absent()
    {
        var dir = NewTempDir();
        try
        {
            var final = Path.Combine(dir, "FOO.ipt");
            var temp = CopyDesignAtomicPromotion.NewTempPath(final);
            File.WriteAllText(temp, "copied bytes");

            var result = CopyDesignAtomicPromotion.PromoteToFinal(temp, final);

            Assert.True(result.Success);
            Assert.True(File.Exists(final));
            Assert.False(File.Exists(temp));
            Assert.Equal("copied bytes", File.ReadAllText(final));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- item: final appears before promotion -> fail, preserve racing file

    [Fact]
    public void PromoteToFinal_fails_and_preserves_the_racing_file_when_the_final_destination_already_exists()
    {
        var dir = NewTempDir();
        try
        {
            var final = Path.Combine(dir, "FOO.ipt");
            File.WriteAllText(final, "RACING file - created by someone else"); // simulates a race winner
            var temp = CopyDesignAtomicPromotion.NewTempPath(final);
            File.WriteAllText(temp, "our copy");

            var result = CopyDesignAtomicPromotion.PromoteToFinal(temp, final);

            Assert.False(result.Success);
            Assert.True(File.Exists(final));
            Assert.Equal("RACING file - created by someone else", File.ReadAllText(final)); // never overwritten
            Assert.False(File.Exists(temp)); // our own temp is cleaned up
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- item: promotion failure -> temp cleaned (covered above too; this
    //      proves it independently of WHY promotion failed) ----------------

    [Fact]
    public void PromoteToFinal_cleans_up_ONLY_the_temp_file_on_failure_never_the_final_path()
    {
        var dir = NewTempDir();
        try
        {
            var final = Path.Combine(dir, "BAR.iam");
            File.WriteAllText(final, "pre-existing, unrelated final content");
            var temp = CopyDesignAtomicPromotion.NewTempPath(final);
            File.WriteAllText(temp, "attempted copy");

            var result = CopyDesignAtomicPromotion.PromoteToFinal(temp, final);

            Assert.False(result.Success);
            Assert.False(File.Exists(temp));
            Assert.True(File.Exists(final));
            Assert.Equal("pre-existing, unrelated final content", File.ReadAllText(final));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PromoteToFinal_fails_cleanly_when_the_temp_file_itself_does_not_exist()
    {
        var dir = NewTempDir();
        try
        {
            var final = Path.Combine(dir, "FOO.ipt");
            var temp = CopyDesignAtomicPromotion.NewTempPath(final);
            // temp was never actually created.

            var result = CopyDesignAtomicPromotion.PromoteToFinal(temp, final);

            Assert.False(result.Success);
            Assert.False(File.Exists(final));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CleanUpTempOnly_never_throws_when_the_temp_file_is_already_gone()
    {
        var dir = NewTempDir();
        try
        {
            var final = Path.Combine(dir, "FOO.ipt");
            var temp = CopyDesignAtomicPromotion.NewTempPath(final);

            var exception = Record.Exception(() => CopyDesignAtomicPromotion.CleanUpTempOnly(temp));

            Assert.Null(exception);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CleanUpTempOnly_deletes_only_the_named_temp_file_and_nothing_else_in_the_directory()
    {
        var dir = NewTempDir();
        try
        {
            var final = Path.Combine(dir, "FOO.ipt");
            var untouched = Path.Combine(dir, "UNTOUCHED.ipt");
            File.WriteAllText(untouched, "must survive");
            var temp = CopyDesignAtomicPromotion.NewTempPath(final);
            File.WriteAllText(temp, "to be removed");

            CopyDesignAtomicPromotion.CleanUpTempOnly(temp);

            Assert.False(File.Exists(temp));
            Assert.True(File.Exists(untouched));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ======================================================================
    // CODEX ROUND 3, MEDIUM: temp cleanup is OBSERVABLE, never silently
    // assumed to have succeeded.
    // ======================================================================

    [Fact]
    public void CleanUpTempOnly_returns_true_when_the_file_is_already_absent()
    {
        var dir = NewTempDir();
        try
        {
            var temp = Path.Combine(dir, ".p6c-tmp-never-created.ipt");

            Assert.True(CopyDesignAtomicPromotion.CleanUpTempOnly(temp));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CleanUpTempOnly_returns_true_after_successfully_deleting_an_unlocked_file()
    {
        var dir = NewTempDir();
        try
        {
            var temp = Path.Combine(dir, ".p6c-tmp-unlocked.ipt");
            File.WriteAllText(temp, "x");

            var result = CopyDesignAtomicPromotion.CleanUpTempOnly(temp);

            Assert.True(result);
            Assert.False(File.Exists(temp));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // item 2/3 (MEDIUM): "delete fails while [a lock is held]" then "retry
    // after [the lock is released] succeeds" / "still fails" - exercised
    // directly against the REAL primitive the physical copier calls twice
    // (once while the document would still be open, once after it closes).
    [Fact]
    public void CleanUpTempOnly_returns_false_while_the_file_is_locked_open_elsewhere()
    {
        var dir = NewTempDir();
        try
        {
            var temp = Path.Combine(dir, ".p6c-tmp-locked.ipt");
            File.WriteAllText(temp, "x");
            using var lockHandle = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None);

            var result = CopyDesignAtomicPromotion.CleanUpTempOnly(temp);

            Assert.False(result);
            Assert.True(File.Exists(temp)); // still there - never silently claimed removed
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CleanUpTempOnly_retried_after_the_lock_is_released_succeeds()
    {
        var dir = NewTempDir();
        try
        {
            var temp = Path.Combine(dir, ".p6c-tmp-locked-then-released.ipt");
            File.WriteAllText(temp, "x");
            var lockHandle = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None);

            var firstAttempt = CopyDesignAtomicPromotion.CleanUpTempOnly(temp); // "while open" - fails
            Assert.False(firstAttempt);

            lockHandle.Dispose(); // simulates "document closed"

            var retryAfterClose = CopyDesignAtomicPromotion.CleanUpTempOnly(temp); // "after close" - succeeds

            Assert.True(retryAfterClose);
            Assert.False(File.Exists(temp));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CleanUpTempOnly_retried_after_still_locked_STILL_fails_manual_cleanup_required()
    {
        var dir = NewTempDir();
        try
        {
            var temp = Path.Combine(dir, ".p6c-tmp-still-locked.ipt");
            File.WriteAllText(temp, "x");
            using var lockHandle = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None);

            var firstAttempt = CopyDesignAtomicPromotion.CleanUpTempOnly(temp);
            var retryStillLocked = CopyDesignAtomicPromotion.CleanUpTempOnly(temp); // lock never released

            Assert.False(firstAttempt);
            Assert.False(retryStillLocked); // manual cleanup required - the exact path is still known to the caller
            Assert.True(File.Exists(temp));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- PromoteToFinal surfaces UnremovedTempPath truthfully ------------

    [Fact]
    public void PromoteToFinal_reports_no_UnremovedTempPath_when_cleanup_after_a_lost_race_succeeds()
    {
        var dir = NewTempDir();
        try
        {
            var final = Path.Combine(dir, "FOO.ipt");
            File.WriteAllText(final, "racing winner");
            var temp = CopyDesignAtomicPromotion.NewTempPath(final);
            File.WriteAllText(temp, "our copy");

            var result = CopyDesignAtomicPromotion.PromoteToFinal(temp, final);

            Assert.False(result.Success);
            Assert.Null(result.UnremovedTempPath); // cleanup succeeded - nothing to report
            Assert.False(File.Exists(temp));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PromoteToFinal_surfaces_UnremovedTempPath_when_cleanup_cannot_confirm_removal()
    {
        var dir = NewTempDir();
        try
        {
            var final = Path.Combine(dir, "FOO.ipt");
            File.WriteAllText(final, "racing winner"); // forces PromoteToFinal to fail
            var temp = CopyDesignAtomicPromotion.NewTempPath(final);
            File.WriteAllText(temp, "our copy");
            using var lockHandle = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None);

            var result = CopyDesignAtomicPromotion.PromoteToFinal(temp, final);

            Assert.False(result.Success);
            Assert.Equal(temp, result.UnremovedTempPath); // surfaced, never silently dropped
            Assert.True(File.Exists(temp)); // failure path never silently claims complete cleanup
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
