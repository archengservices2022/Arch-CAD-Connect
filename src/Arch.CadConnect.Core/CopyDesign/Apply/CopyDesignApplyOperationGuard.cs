namespace Arch.CadConnect.Core.CopyDesign.Apply;

/// <summary>
/// P6C: the smallest appropriate IN-PROCESS guard against two Copy Design
/// apply operations mutating a destination set concurrently in THIS add-in
/// process - e.g. a double-click on "Apply", or a second confirmation while
/// one is already running.
///
/// This is deliberately NOT a distributed lock: it says nothing about a
/// second Inventor session, a second machine, or a second user. Cross-
/// process / cross-session safety is P6B's job (the server-side idempotency
/// key + the destination documentNumber unique constraint), never this
/// class's. One global guard is sufficient here because there is exactly one
/// Inventor Application per add-in process, so at most one apply operation
/// could ever be "in this process" at a time regardless of which plan it is
/// for - a per-plan lock would add complexity without adding any real
/// protection this milestone needs.
/// </summary>
public sealed class CopyDesignApplyOperationGuard
{
    private int _busy;

    /// <summary>
    /// Attempt to become the sole holder. Returns a disposable lease if
    /// successful (release it when the operation completes, success or
    /// failure) or <c>null</c> if another operation already holds it - the
    /// caller must refuse to start, never queue, never silently proceed.
    /// </summary>
    public IDisposable? TryEnter()
    {
        return Interlocked.CompareExchange(ref _busy, 1, 0) == 0 ? new Lease(this) : null;
    }

    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    private sealed class Lease(CopyDesignApplyOperationGuard owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                Volatile.Write(ref owner._busy, 0);
            }
        }
    }
}
