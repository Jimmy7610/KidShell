using KidShell.Core.Runtime;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// A dispatcher that runs nothing until it is told to.
///
/// The point of the test double: if a handler runs before <see cref="Pump"/>,
/// it ran on the caller's thread rather than on the UI thread, which is the
/// defect. "Did it marshal" is otherwise very hard to assert, because a
/// dispatcher that runs inline looks identical to no dispatcher at all.
/// </summary>
internal sealed class DeferredUiDispatcher : IUiDispatcher
{
    private readonly List<Action> _pending = [];

    public bool IsOnUiThread => false;

    public bool IsShutDown { get; set; }

    public int PostCount { get; private set; }

    public int Pending => _pending.Count;

    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        PostCount++;

        if (IsShutDown)
        {
            return false;
        }

        _pending.Add(action);
        return true;
    }

    /// <summary>Runs what was posted, in the order it was posted.</summary>
    public void Pump()
    {
        var work = _pending.ToList();
        _pending.Clear();

        foreach (var action in work)
        {
            action();
        }
    }
}

/// <summary>
/// OPSV FINDING 02 — screen-time events were raised on a background thread and
/// handled by code that set bound WinUI properties directly.
///
/// These tests are about the ownership boundary rather than about WinUI. A
/// test cannot create a DispatcherQueue, and a view model that needed one in
/// order to be constructed would be a view model with no tests - which is half
/// of why the defect survived.
/// </summary>
public class UiDispatcherTests
{
    [Fact]
    public void A_posted_callback_does_not_run_until_the_dispatcher_runs_it()
    {
        var ui = new DeferredUiDispatcher();
        var ran = false;

        ui.Post(() => ran = true);

        Assert.False(ran);      // this is the whole defect, in one assertion
        Assert.Equal(1, ui.PostCount);

        ui.Pump();

        Assert.True(ran);
    }

    [Fact]
    public void Warnings_stay_in_the_order_they_were_raised()
    {
        // "5 minutes left" arriving before "15 minutes left" would be worse
        // than either arriving late.
        var ui = new DeferredUiDispatcher();
        var seen = new List<int>();

        foreach (var minutes in new[] { 15, 10, 5 })
        {
            var captured = minutes;
            ui.Post(() => seen.Add(captured));
        }

        ui.Pump();

        Assert.Equal([15, 10, 5], seen);
    }

    [Fact]
    public void A_shut_down_dispatcher_reports_failure_rather_than_throwing()
    {
        // The window has closed while a background tick was in flight. That is
        // a subscription outliving its view, not an error: nothing can be
        // updated and nothing should crash.
        var ui = new DeferredUiDispatcher { IsShutDown = true };
        var ran = false;

        var posted = ui.Post(() => ran = true);

        Assert.False(posted);
        ui.Pump();
        Assert.False(ran);
    }

    [Fact]
    public void The_immediate_dispatcher_runs_inline_for_tests()
    {
        var ui = new ImmediateUiDispatcher();
        var ran = false;

        Assert.True(ui.Post(() => ran = true));
        Assert.True(ran);
        Assert.True(ui.IsOnUiThread);
    }

    [Fact]
    public void Posting_nothing_is_a_programming_error_not_a_silent_no_op()
    {
        Assert.Throws<ArgumentNullException>(() => new ImmediateUiDispatcher().Post(null!));
        Assert.Throws<ArgumentNullException>(() => new DeferredUiDispatcher().Post(null!));
    }
}
