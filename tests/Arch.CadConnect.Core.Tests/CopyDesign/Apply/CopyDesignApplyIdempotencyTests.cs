using Arch.CadConnect.Core.CopyDesign.Apply;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

public class CopyDesignApplyIdempotencyTests
{
    [Fact]
    public void StartNewAttemptMintsAFreshKeyEachTime()
    {
        var first = CopyDesignApplyAttempt.StartNewAttempt();
        var second = CopyDesignApplyAttempt.StartNewAttempt();

        Assert.NotEqual(first.IdempotencyKey, second.IdempotencyKey);
    }

    // 5. same logical retry retains idempotency key
    [Fact]
    public void ResumePreservesTheExactOriginalKeyForARetry()
    {
        var original = CopyDesignApplyAttempt.StartNewAttempt();

        var retry = CopyDesignApplyAttempt.Resume(original.IdempotencyKey);

        Assert.Equal(original.IdempotencyKey, retry.IdempotencyKey);
    }

    [Fact]
    public void ResumeRejectsABlankKey()
    {
        Assert.Throws<ArgumentException>(() => CopyDesignApplyAttempt.Resume(""));
        Assert.Throws<ArgumentException>(() => CopyDesignApplyAttempt.Resume("   "));
    }

    [Fact]
    public void KeysAreNeverDerivedFromAnythingGuessableAndCarryAStableRecognizablePrefix()
    {
        var attempt = CopyDesignApplyAttempt.StartNewAttempt();
        Assert.StartsWith("cadconnect-p6c-", attempt.IdempotencyKey);
    }
}
