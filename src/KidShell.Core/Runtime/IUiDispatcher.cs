namespace KidShell.Core.Runtime;

/// <summary>
/// Moves work onto the thread that owns the user interface.
///
/// WHY THIS EXISTS
/// ---------------
/// The screen-time coordinator ticks on a <see cref="System.Threading.Timer"/>,
/// whose callback runs on a thread-pool thread. Its Changed event was handled
/// by a view model that set bound properties directly, so the child's warning
/// line and the time-is-up overlay were being raised from the wrong thread.
///
/// WinUI does not always throw for that. It sometimes updates, sometimes drops
/// the change, and sometimes fails - which is worse than a reliable crash,
/// because it made "the time-is-up screen did not appear" an intermittent
/// report nobody could reproduce.
///
/// Catching the cross-thread exception would not have helped. The fix is an
/// ownership boundary: background services raise events wherever they like,
/// and anything that touches UI state marshals first.
///
/// WHY IT IS IN CORE
/// -----------------
/// So that view models can depend on it without depending on WinUI, and so a
/// unit test can supply one that runs inline. A view model that needs a real
/// DispatcherQueue to be constructed is a view model with no tests.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// Whether the caller is already on the UI thread.
    ///
    /// Not for deciding whether to marshal - <see cref="Post"/> handles that.
    /// It is here so that a diagnostic can say which thread it was on.
    /// </summary>
    bool IsOnUiThread { get; }

    /// <summary>
    /// Runs the action on the UI thread.
    ///
    /// Ordering is guaranteed: two posts from the same thread run in the order
    /// they were made. The child gets "15 minutes left" before "5 minutes
    /// left", never the other way round.
    ///
    /// Returns false when the UI is gone - the window has closed, or the
    /// queue has shut down. A caller that cannot update anything is not an
    /// error worth throwing over; the subscription is simply outliving its
    /// view, which is what <see cref="Post"/> returning false means.
    /// </summary>
    bool Post(Action action);
}

/// <summary>
/// Runs everything immediately, on the calling thread.
///
/// For unit tests, and for the composition-time code that runs before there is
/// a dispatcher at all. It preserves ordering trivially, which is the property
/// the tests care about.
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;

    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        action();
        return true;
    }
}
