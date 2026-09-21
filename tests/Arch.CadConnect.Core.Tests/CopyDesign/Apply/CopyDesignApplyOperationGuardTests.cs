using Arch.CadConnect.Core.CopyDesign.Apply;

namespace Arch.CadConnect.Core.Tests.CopyDesign.Apply;

public class CopyDesignApplyOperationGuardTests
{
    // 32. operation double-execution guard works
    [Fact]
    public void SecondTryEnterFailsWhileTheFirstLeaseIsHeld()
    {
        var guard = new CopyDesignApplyOperationGuard();

        using var first = guard.TryEnter();
        Assert.NotNull(first);

        var second = guard.TryEnter();
        Assert.Null(second);
    }

    [Fact]
    public void TryEnterSucceedsAgainAfterTheLeaseIsReleased()
    {
        var guard = new CopyDesignApplyOperationGuard();

        var first = guard.TryEnter();
        Assert.NotNull(first);
        first!.Dispose();

        var second = guard.TryEnter();
        Assert.NotNull(second);
        second!.Dispose();
    }

    [Fact]
    public void IsBusyReflectsHeldState()
    {
        var guard = new CopyDesignApplyOperationGuard();
        Assert.False(guard.IsBusy);

        var lease = guard.TryEnter();
        Assert.True(guard.IsBusy);

        lease!.Dispose();
        Assert.False(guard.IsBusy);
    }

    [Fact]
    public void DisposingTheLeaseTwiceIsSafe()
    {
        var guard = new CopyDesignApplyOperationGuard();
        var lease = guard.TryEnter();
        lease!.Dispose();
        lease.Dispose(); // must not throw, must not double-release someone else's lease
        Assert.False(guard.IsBusy);
    }
}
