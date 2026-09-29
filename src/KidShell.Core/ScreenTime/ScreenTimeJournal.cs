namespace KidShell.Core.ScreenTime;

/// <summary>
/// Whether the last session ended tidily.
///
/// This is what makes a restart safe. A counter alone cannot tell you whether
/// the number in it is the whole story, and the difference between "the child
/// stopped at 60" and "the last thing we managed to write was 60" is the whole
/// of OPSV retest 2 finding 03.
/// </summary>
public enum ScreenTimeSessionState
{
    /// <summary>
    /// Closed properly. The committed figure is the complete figure.
    /// </summary>
    Clean = 0,

    /// <summary>
    /// A session was running and has not been closed.
    ///
    /// The child may have used any amount of time since the last checkpoint,
    /// and nothing durable records how much. Written BEFORE any usage is
    /// credited, so that a crash or a failed write cannot look like a clean
    /// stop.
    /// </summary>
    Open = 1,

    /// <summary>
    /// Recovered from an Open session, and not yet resolved by a parent.
    ///
    /// Distinct from Open so that repeated restarts do not keep re-reporting
    /// the same crash as a fresh one.
    /// </summary>
    Dirty = 2
}

/// <summary>
/// The rules that make used time survive a failure.
///
/// THE INVARIANT
/// -------------
/// A persistence failure may make KidShell stricter. It must never make
/// KidShell more permissive. Across any restart, for the same day, used time
/// must not decrease because of a write failure, a crash, a corrupt file, a
/// stale backup, a deleted file, or an unavailable writer.
///
/// WHY A BACKUP WAS NOT ENOUGH
/// ---------------------------
/// The previous design fell back to the previous known-good copy, which is
/// older by definition. OPSV made the point exactly: primary says 1200,
/// backup says 600, primary is corrupted - and reading 600 hands the child ten
/// minutes back. A backup is fine for AVAILABILITY and must never lower the
/// security value.
///
/// So recovery is allowed to raise the figure and never to lower it, and where
/// the true figure cannot be established the day is treated as spent until a
/// parent says otherwise.
/// </summary>
public static class ScreenTimeJournalRules
{
    /// <summary>
    /// The larger of two counters for the same day.
    ///
    /// Same day only. Yesterday's 3600 must not become today's floor, or a
    /// heavy Saturday would block Sunday morning.
    /// </summary>
    public static int HighWaterFor(string day, ScreenTimeState? a, ScreenTimeState? b)
    {
        var first = UsedOn(day, a);
        var second = UsedOn(day, b);

        return Math.Max(first, second);
    }

    private static int UsedOn(string day, ScreenTimeState? state) =>
        state is not null && string.Equals(state.LocalDate, day, StringComparison.Ordinal)
            ? Math.Max(0, state.UsedSeconds)
            : 0;

    /// <summary>
    /// Whether a recovered state can be trusted to be the whole story.
    ///
    /// Only a Clean session that was readable from the authoritative copy can.
    /// Everything else means an unknown amount of time was used after the last
    /// figure that reached the disk.
    /// </summary>
    public static bool IsComplete(ScreenTimeLoadOutcome outcome, ScreenTimeSessionState session) =>
        outcome == ScreenTimeLoadOutcome.Primary && session == ScreenTimeSessionState.Clean;

    /// <summary>
    /// What a restart should conclude.
    ///
    /// The three answers, in order of preference:
    ///
    ///   * a clean primary          use it
    ///   * anything else, same day  keep the highest figure seen and fail
    ///                              closed, because the true figure is higher
    ///                              by an unknown amount
    ///   * a genuine first run      zero, which is true
    /// </summary>
    public static ScreenTimeRecovery Recover(
        string today,
        ScreenTimeLoadOutcome outcome,
        ScreenTimeState? primary,
        ScreenTimeState? backup)
    {
        if (outcome == ScreenTimeLoadOutcome.FirstRun)
        {
            return new ScreenTimeRecovery(0, false, "nothing has ever been written");
        }

        var highWater = HighWaterFor(today, primary, backup);
        var session = primary?.SessionState ?? backup?.SessionState ?? ScreenTimeSessionState.Open;

        if (IsComplete(outcome, session))
        {
            return new ScreenTimeRecovery(highWater, false, "the previous session closed cleanly");
        }

        // The figure is a floor rather than a total. Carrying on from a floor
        // would credit the child with the difference, which is the refund.
        return new ScreenTimeRecovery(
            highWater,
            true,
            outcome == ScreenTimeLoadOutcome.Primary
                ? "the previous session did not close"
                : $"the counter was recovered ({outcome})");
    }
}

/// <summary>What a restart concluded about today.</summary>
/// <param name="UsedSeconds">The highest figure that can be proven for today.</param>
/// <param name="MustFailClosed">
/// Whether more time may have been used than is recorded. When true the day is
/// treated as spent until a parent resets it.
/// </param>
/// <param name="Reason">Why, in one phrase, for the log and the parent surface.</param>
public sealed record ScreenTimeRecovery(int UsedSeconds, bool MustFailClosed, string Reason);
