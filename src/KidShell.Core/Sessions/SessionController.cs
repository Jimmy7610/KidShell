using KidShell.Core.Diagnostics;

namespace KidShell.Core.Sessions;

/// <summary>What a parent asked the machine to do.</summary>
public enum SessionAction
{
    /// <summary>Close KidShell and leave the desktop as it is.</summary>
    CloseKidShell = 0,

    /// <summary>Sign the child's Windows account out.</summary>
    SignOut = 1,

    /// <summary>Lock the workstation.</summary>
    Lock = 2
}

/// <summary>What actually happened.</summary>
public sealed record SessionActionResult(bool Performed, string Description)
{
    public static SessionActionResult Simulated(SessionAction action) =>
        new(false, $"Simulerat: {Describe(action)} utfördes inte.");

    public static string Describe(SessionAction action) => action switch
    {
        SessionAction.CloseKidShell => "stäng KidShell",
        SessionAction.SignOut => "logga ut Windows-kontot",
        SessionAction.Lock => "lås datorn",
        _ => "okänd åtgärd"
    };
}

/// <summary>
/// Ends a session.
///
/// The abstraction exists so "Avsluta till Windows" can mean different things
/// in different builds without the view model knowing which. On a development
/// machine it must never sign anybody out - a developer who loses their
/// session to a button they were testing has lost their work.
/// </summary>
public interface ISessionController
{
    /// <summary>Whether this controller would really do it.</summary>
    bool IsSimulated { get; }

    Task<SessionActionResult> PerformAsync(SessionAction action, CancellationToken cancellationToken = default);
}

/// <summary>
/// Records what would have happened and does none of it.
///
/// Wired in every 0.x build. There is deliberately no real implementation in
/// the solution: signing a Windows account out is a machine action and belongs
/// behind the same boundary as everything else, and a test asserts this is the
/// only implementation that exists.
/// </summary>
public sealed class DevelopmentSessionController : ISessionController
{
    private readonly IKidShellLogger _logger;
    private readonly List<SessionAction> _requested = [];

    public DevelopmentSessionController(IKidShellLogger logger) => _logger = logger;

    public bool IsSimulated => true;

    /// <summary>Everything that was asked for, in order. Used by tests and diagnostics.</summary>
    public IReadOnlyList<SessionAction> Requested => _requested;

    public Task<SessionActionResult> PerformAsync(
        SessionAction action,
        CancellationToken cancellationToken = default)
    {
        _requested.Add(action);

        _logger.Info("Session",
            $"Session action requested but NOT performed (development build): {SessionActionResult.Describe(action)}.");

        return Task.FromResult(SessionActionResult.Simulated(action));
    }
}
