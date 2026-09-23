using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;

namespace KidShell.Core.ScreenTime;

/// <summary>What the child shell should be showing right now.</summary>
public sealed record ScreenTimeStatusView
{
    public required ScreenTimeSnapshot Snapshot { get; init; }

    /// <summary>Whether launching anything should be refused.</summary>
    public bool IsBlocked => Snapshot.IsBlocked;

    /// <summary>A warning threshold was crossed on this tick, and has not been shown.</summary>
    public int? NewWarningMinutes { get; init; }
}

/// <summary>
/// Drives the screen-time engine and tells the child shell what to do.
///
/// WHY THIS EXISTS AS A SERVICE
/// ----------------------------
/// The engine is pure: it decides, given a clock and a stored counter, how much
/// time is left. Something has to tick it, persist the result, and notice when
/// a warning threshold is crossed. Putting that in a view model would make it
/// untestable and would tie the child's remaining time to whichever screen
/// happened to be open.
///
/// WHAT IT DELIBERATELY DOES NOT DO
/// --------------------------------
/// It does not count time while the computer is asleep. The engine caps each
/// credited tick, so a laptop closed for three hours resumes with the same
/// allowance it had - sleeping neither consumes the day nor earns extra.
///
/// It does not fight clock tampering. Usage is credited from a monotonic
/// timestamp, so winding the clock back refunds nothing; a backward jump is
/// recorded and shown to the parent, and that is where it ends. Turning this
/// into an anti-tamper arms race against a child with a settings app is a fight
/// worth losing on purpose.
///
/// AND IT IS NOT A LOCK
/// --------------------
/// Blocking here stops KidShell launching things and shows a time-is-up screen.
/// Until a verified secure configuration is active the child can still leave
/// KidShell entirely, and every surface that reports this says so.
/// </summary>
public interface IScreenTimeCoordinator
{
    /// <summary>The current state, evaluated without advancing the counter.</summary>
    ScreenTimeStatusView Current { get; }

    /// <summary>Raised when the status changes in a way the UI should react to.</summary>
    event EventHandler<ScreenTimeStatusView>? Changed;

    /// <summary>Starts ticking. Called once, when the child shell appears.</summary>
    void Start();

    void Stop();

    /// <summary>Whether the child may launch something right now.</summary>
    bool CanLaunch();

    /// <summary>Marks a warning as shown, so it is not repeated every tick.</summary>
    void AcknowledgeWarning(int minutes);

    /// <summary>Re-evaluates immediately, e.g. after a parent granted more time.</summary>
    void Refresh();
}

/// <summary>
/// The production coordinator. A timer, the engine, and a small amount of
/// "have we already said this" state.
/// </summary>
public sealed class ScreenTimeCoordinator : IScreenTimeCoordinator, IDisposable
{
    /// <summary>
    /// How often the engine is ticked.
    ///
    /// Thirty seconds, not one: the engine credits elapsed time rather than
    /// counting ticks, so a slower timer costs nothing in accuracy and wakes a
    /// laptop's CPU less. The warning thresholds are minutes apart, so this is
    /// comfortably fine enough to catch them.
    /// </summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly ScreenTimeEngine _engine;
    private readonly IAppStateService _state;
    private readonly IKidShellLogger _logger;
    private readonly HashSet<int> _acknowledgedWarnings = [];

    private Timer? _timer;
    private ScreenTimeStatusView _current;
    private string _warningDay = string.Empty;
    private bool _disposed;

    public ScreenTimeCoordinator(ScreenTimeEngine engine, IAppStateService state, IKidShellLogger logger)
    {
        _engine = engine;
        _state = state;
        _logger = logger;

        _current = new ScreenTimeStatusView { Snapshot = _engine.Evaluate() };
    }

    public ScreenTimeStatusView Current => _current;

    public event EventHandler<ScreenTimeStatusView>? Changed;

    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _logger.Info("ScreenTime", $"Screen time coordinator started; ticking every {TickInterval.TotalSeconds:F0}s.");
        _timer = new Timer(_ => Tick(), null, TickInterval, TickInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Tick()
    {
        try
        {
            Publish(_engine.Tick());
        }
        catch (Exception ex)
        {
            // A failed tick must not take the child's shell down. The next one
            // will credit the elapsed time anyway.
            _logger.Error("ScreenTime", "A screen-time tick failed.", ex);
        }
    }

    public void Refresh() => Publish(_engine.Evaluate());

    private void Publish(ScreenTimeSnapshot snapshot)
    {
        // Warnings are per-day. Crossing "15 minutes left" on Monday must not
        // suppress Tuesday's.
        var today = _engine.Today;

        if (!string.Equals(today, _warningDay, StringComparison.Ordinal))
        {
            _warningDay = today;
            _acknowledgedWarnings.Clear();
        }

        int? newWarning = null;

        if (snapshot.WarningMinutes is { } minutes && !_acknowledgedWarnings.Contains(minutes))
        {
            newWarning = minutes;
        }

        var previous = _current;

        _current = new ScreenTimeStatusView
        {
            Snapshot = snapshot,
            NewWarningMinutes = newWarning
        };

        // Raised only on a change that matters, so the UI is not rebuilt every
        // thirty seconds for a counter nobody is watching.
        if (previous.Snapshot.Status != snapshot.Status ||
            previous.NewWarningMinutes != newWarning ||
            previous.Snapshot.Remaining.Minutes != snapshot.Remaining.Minutes)
        {
            Changed?.Invoke(this, _current);
        }
    }

    public bool CanLaunch()
    {
        var settings = _state.Current.ScreenTime;

        if (!settings.IsEnabled)
        {
            return true;
        }

        return !_engine.Evaluate().IsBlocked;
    }

    public void AcknowledgeWarning(int minutes) => _acknowledgedWarnings.Add(minutes);

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
