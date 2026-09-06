namespace Arch.CadConnect.Core.Documents;

/// <summary>
/// Holds the current <see cref="CadDocumentContext"/> and raises an event when
/// it changes. The Inventor layer's COM event handlers call
/// <see cref="Set"/> with a freshly-mapped, COM-free context; the ribbon
/// controller subscribes to <see cref="Changed"/> to refresh button state and
/// the Information panel.
///
/// Equality is by value (<see cref="CadDocumentContext"/> is a record), so
/// redundant Inventor events (which fire liberally) do not produce redundant
/// UI churn.
///
/// Not thread-safe by design: Inventor delivers its events on the UI thread
/// and the ribbon controller runs there too. The Inventor layer must marshal
/// any off-thread callback before calling <see cref="Set"/>.
/// </summary>
public sealed class ActiveDocumentTracker
{
    public CadDocumentContext Current { get; private set; } = CadDocumentContext.None;

    /// <summary>Raised after <see cref="Current"/> changes to a different value.</summary>
    public event Action<CadDocumentContext>? Changed;

    /// <summary>
    /// Replace the current context. A null argument is treated as "no active
    /// document" (<see cref="CadDocumentContext.None"/>). No event is raised
    /// when the new value equals the old.
    /// </summary>
    public void Set(CadDocumentContext? context)
    {
        var next = context ?? CadDocumentContext.None;
        if (next == Current)
        {
            return;
        }

        Current = next;
        Changed?.Invoke(next);
    }

    /// <summary>Reset to "no document" (e.g. on add-in shutdown).</summary>
    public void Clear() => Set(CadDocumentContext.None);
}
