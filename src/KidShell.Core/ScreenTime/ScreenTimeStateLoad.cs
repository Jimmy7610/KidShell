namespace KidShell.Core.ScreenTime;

/// <summary>
/// Where a loaded counter came from, and therefore how much to trust it.
///
/// The distinction that matters is between "there has never been a counter"
/// and "there was one and it cannot be read". Both used to produce a fresh
/// state with zero seconds used, which is correct for the first and is a
/// refund for the second.
/// </summary>
public enum ScreenTimeLoadOutcome
{
    /// <summary>
    /// Nothing has ever been written. A genuine first run: no time has been
    /// used because the product has not been used.
    /// </summary>
    FirstRun = 0,

    /// <summary>The authoritative file was read.</summary>
    Primary = 1,

    /// <summary>
    /// The authoritative file was missing or unreadable and the previous
    /// known-good copy was used instead.
    ///
    /// Conservative by construction: the backup is the state as of the last
    /// successful write, so at worst it over-counts the seconds since.
    /// </summary>
    RecoveredFromBackup = 2,

    /// <summary>
    /// A counter existed and neither copy could be read.
    ///
    /// This is the case that must not become zero. How much was used is
    /// unknown, and the only answer that cannot hand the child free time is to
    /// treat the day as spent until a parent says otherwise.
    /// </summary>
    Unreadable = 3
}

/// <summary>What a load produced, and where it came from.</summary>
/// <param name="State">The counter to use.</param>
/// <param name="Outcome">Which source it came from.</param>
/// <param name="Detail">Why, when it was not the primary file.</param>
public sealed record ScreenTimeStateLoad(
    ScreenTimeState State,
    ScreenTimeLoadOutcome Outcome,
    string Detail = "")
{
    /// <summary>
    /// Whether the counter is a measurement rather than a guess.
    ///
    /// A recovered backup counts: it is a real measurement, just an older one.
    /// </summary>
    public bool IsTrustworthy =>
        Outcome is ScreenTimeLoadOutcome.FirstRun
                or ScreenTimeLoadOutcome.Primary
                or ScreenTimeLoadOutcome.RecoveredFromBackup;

    public static ScreenTimeStateLoad FirstRun() =>
        new(new ScreenTimeState(), ScreenTimeLoadOutcome.FirstRun);
}
