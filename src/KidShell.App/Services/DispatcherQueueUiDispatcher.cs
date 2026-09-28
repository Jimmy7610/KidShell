using KidShell.Core.Runtime;
using Microsoft.UI.Dispatching;

namespace KidShell.App.Services;

/// <summary>
/// The production <see cref="IUiDispatcher"/>: WinUI's own DispatcherQueue.
///
/// Captured from the UI thread at composition time. It cannot be obtained from
/// a background thread later - <c>GetForCurrentThread</c> returns null there,
/// which is precisely the situation this type exists to rescue, so asking for
/// it at the wrong moment would fail exactly when it was needed.
/// </summary>
public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public DispatcherQueueUiDispatcher(DispatcherQueue queue) =>
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    /// <summary>
    /// Captures the queue of the thread this is called on.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The calling thread has no dispatcher queue, which means this was not
    /// called from the UI thread.
    /// </exception>
    public static DispatcherQueueUiDispatcher ForCurrentThread()
    {
        var queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "No DispatcherQueue on this thread. The UI dispatcher must be captured on the UI thread, " +
                "because a background thread cannot obtain one - and a background thread is exactly what " +
                "will be asking it to marshal.");

        return new DispatcherQueueUiDispatcher(queue);
    }

    public bool IsOnUiThread => _queue.HasThreadAccess;

    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Enqueued even when already on the UI thread, so that ordering is the
        // same either way. Running inline here and queueing there would let a
        // later event overtake an earlier one.
        return _queue.TryEnqueue(() => action());
    }
}
