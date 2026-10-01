namespace KidShell.Core.Security.Broker;

/// <summary>
/// How a broker transport proposes to reach the privileged side.
///
/// WHY THIS TYPE EXISTS
/// --------------------
/// A transport that cannot work is not a bug you can see in a unit test,
/// because the unit works. <c>ProcessElevatedBrokerClient</c> built a correct
/// <c>ProcessStartInfo</c>, started a real process, wrote a well-formed
/// request and read the answer - and the answer was always "this helper must
/// run as an administrator", because nothing in that path ever elevated
/// anything.
///
/// So the launch contract is made into a value. A test can then ask the
/// production client what it intends to do, compare that against what the
/// helper requires, and fail on the contradiction rather than on a timeout.
/// </summary>
public sealed record BrokerLaunchPlan
{
    /// <summary>The executable the transport starts, if it starts one.</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// The ShellExecute verb. "runas" is the only one that raises UAC.
    /// </summary>
    public string Verb { get; init; } = string.Empty;

    /// <summary>
    /// Whether the launch goes through ShellExecute.
    ///
    /// This is the field the whole defect turns on. A verb is only honoured
    /// when it is true, and the standard streams can only be redirected when
    /// it is false.
    /// </summary>
    public bool UseShellExecute { get; init; }

    public bool RedirectStandardInput { get; init; }

    public bool RedirectStandardOutput { get; init; }

    /// <summary>What the started process requires in order to serve requests.</summary>
    public required BrokerElevationRequirement Requires { get; init; }

    /// <summary>
    /// What the manifest of the started process asks Windows for.
    /// </summary>
    public required BrokerManifestElevation Manifest { get; init; }

    /// <summary>
    /// Whether this plan can ever succeed from a standard user account.
    ///
    /// Three ways to fail, and the old production path managed two of them:
    ///
    ///   * the started process needs elevation, and nothing in the plan
    ///     provides it;
    ///   * the plan asks for a verb while ShellExecute is off, so Windows
    ///     ignores the verb entirely;
    ///   * the plan asks for both redirection and ShellExecute, which .NET
    ///     refuses outright.
    /// </summary>
    public BrokerLaunchViability Evaluate()
    {
        if (RedirectsStreams && UseShellExecute)
        {
            return new BrokerLaunchViability(false,
                "Standard streams cannot be redirected through ShellExecute.");
        }

        if (Verb.Length > 0 && !UseShellExecute)
        {
            return new BrokerLaunchViability(false,
                "A verb is ignored unless the launch goes through ShellExecute.");
        }

        if (Requires == BrokerElevationRequirement.None)
        {
            return new BrokerLaunchViability(true, "The started process needs no elevation.");
        }

        if (Manifest == BrokerManifestElevation.RequireAdministrator)
        {
            return new BrokerLaunchViability(true, "The manifest raises the prompt.");
        }

        if (Verb.Equals("runas", StringComparison.OrdinalIgnoreCase) && UseShellExecute)
        {
            return new BrokerLaunchViability(true, "The runas verb raises the prompt.");
        }

        return new BrokerLaunchViability(false,
            "The started process requires elevation and nothing in the launch elevates it.");
    }

    /// <summary>Whether the plan depends on the started process's streams.</summary>
    public bool RedirectsStreams => RedirectStandardInput || RedirectStandardOutput;

    /// <summary>
    /// Whether this transport would raise a UAC prompt per request.
    ///
    /// Relevant even when a plan is viable. The screen-time counter and the
    /// PIN throttle are written repeatedly during ordinary use, and a product
    /// a child operates cannot raise a consent prompt on a timer.
    /// </summary>
    public bool PromptsPerRequest =>
        Verb.Equals("runas", StringComparison.OrdinalIgnoreCase) && UseShellExecute;
}

/// <summary>Whether a launched process can do its job without elevation.</summary>
public enum BrokerElevationRequirement
{
    None = 0,

    /// <summary>It refuses to serve requests unless the token is elevated.</summary>
    Administrator = 1
}

/// <summary>What an executable's application manifest asks Windows for.</summary>
public enum BrokerManifestElevation
{
    AsInvoker = 0,
    HighestAvailable = 1,
    RequireAdministrator = 2
}

/// <summary>Whether a launch plan can work, and why not when it cannot.</summary>
public sealed record BrokerLaunchViability(bool CanSucceed, string Reason);
