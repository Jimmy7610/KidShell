using KidShell.Core.Runtime;

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

    /// <summary>
    /// Whether the app should draw the development watermark. Always true in a
    /// developer build: a build with relaxed security must never look like a
    /// shipped one.
    /// </summary>
    bool ShowDevelopmentWatermark { get; }
}

/// <summary>
/// Developer mode, derived from the build rather than from configuration.
///
/// Until 0.2 this was a hard-coded <c>true</c>. It is now a function of
/// <see cref="IRuntimeEnvironment"/>, which is decided at compile time, so a
/// production build has no path back to developer behaviour. There is
/// deliberately no constructor that lets a caller pass <c>true</c>
/// independently of the environment — that would be the backdoor this type
/// exists to remove.
/// </summary>
public sealed class DeveloperOptions : IDeveloperOptions
{
    private readonly IRuntimeEnvironment _environment;

    public DeveloperOptions(IRuntimeEnvironment environment) => _environment = environment;

    public bool DeveloperMode => _environment.IsDevelopment;

    public bool ShowDevelopmentWatermark => _environment.IsDevelopment;

    /// <summary>A production options instance. The safe default.</summary>
    public static IDeveloperOptions Production { get; } = new DeveloperOptions(RuntimeEnvironment.Production);
}
