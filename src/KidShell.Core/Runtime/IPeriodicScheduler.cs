namespace KidShell.Core.Runtime;

/// <summary>
/// Something that calls back on a fixed interval, whatever else is happening.
///
/// OPSV RETEST 2, FINDING 04. Parent Mode's idle timeout was evaluated from
/// the screen-time coordinator's Changed event, which was convenient and
/// wrong: that event fires when the remaining minutes change, so it does not
/// fire at all when screen time is disabled, when the allowance is unlimited,
/// or when it has already run out. In exactly those states Parent Mode stayed
/// unlocked for as long as anybody left it open.
///
/// A session lifetime cannot depend on a signal that is allowed to be silent.
/// It gets its own heartbeat, injected so a test can advance it without
/// waiting.
/// </summary>
public interface IPeriodicScheduler : IDisposable
{
    /// <summary>
    /// Starts calling <paramref name="callback"/> every
    /// <paramref name="interval"/>, until disposed.
    ///
    /// Called once. A second start replaces the first rather than adding a
    /// second timer, so a restarted shell cannot end up with two.
    /// </summary>
    void Start(TimeSpan interval, Action callback);

    void Stop();
}

/// <summary>
/// The production heartbeat: a timer.
///
/// Deliberately not the screen-time timer. Sharing one would re-create the
/// coupling this exists to remove, and the screen-time coordinator is allowed
/// to be stopped.
/// </summary>
public sealed class TimerPeriodicScheduler : IPeriodicScheduler
{
    private readonly Lock _gate = new();

    private Timer? _timer;
    private bool _disposed;

    public void Start(TimeSpan interval, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval must be positive.");
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _timer?.Dispose();
            _timer = new Timer(_ => Fire(callback), null, interval, interval);
        }
    }

    private static void Fire(Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception)
        {
            // A heartbeat that throws must not take the process down, and the
            // next beat is a fraction of a minute away. What the callback
            // wanted to report is the callback's own business to log.
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}

/// <summary>
/// A scheduler a test drives by hand.
///
/// <see cref="Advance"/> fires the callback as many times as the elapsed time
/// covers, so a test can say "twenty minutes passed" without twenty calls and
/// without waiting twenty minutes.
/// </summary>
public sealed class ManualPeriodicScheduler : IPeriodicScheduler
{
    private TimeSpan _interval;
    private Action? _callback;

    public bool IsRunning => _callback is not null;

    public int FireCount { get; private set; }

    public void Start(TimeSpan interval, Action callback)
    {
        _interval = interval;
        _callback = callback;
    }

    /// <summary>Fires the callback once for each whole interval elapsed.</summary>
    public void Advance(TimeSpan elapsed)
    {
        if (_callback is null || _interval <= TimeSpan.Zero)
        {
            return;
        }

        var beats = (int)(elapsed.Ticks / _interval.Ticks);

        for (var i = 0; i < beats; i++)
        {
            FireCount++;
            _callback();
        }
    }

    public void Stop() => _callback = null;

    public void Dispose() => Stop();
}
