namespace KidShell.Core.Configuration;

/// <summary>
/// Switches that only matter while KidShell is under development.
/// </summary>
public interface IDeveloperOptions
{
    /// <summary>
    /// When true the app keeps a normal resizable window, exposes the debug
    /// shortcut into Parent Mode, accepts the development fallback PIN, and
    /// treats "Avsluta till Windows" as "close KidShell" rather than
    /// "sign the child out".
    /// </summary>
    bool DeveloperMode { get; }
}

/// <summary>
/// MVP 0.1 ships with developer mode hard-on. There is no lockdown to escape
/// from yet, and the milestone is explicitly a visual/architectural shell.
/// The later Windows integration milestone turns this into a real, parent
/// controlled switch.
/// </summary>
public sealed class DeveloperOptions : IDeveloperOptions
{
    public const bool DeveloperModeDefault = true;

    public bool DeveloperMode { get; init; } = DeveloperModeDefault;
}
