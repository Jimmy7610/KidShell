using KidShell.Core.Runtime;

namespace KidShell.App.Services;

/// <summary>
/// The app's runtime mode, decided by the compiler.
///
/// HOW DEVELOPER MODE IS ENABLED
/// -----------------------------
/// Build KidShell in the <c>Debug</c> configuration. That is the whole
/// mechanism, and it is the whole mechanism on purpose.
///
///     dotnet build KidShell.sln -p:Platform=x64 -c Debug      developer build
///     dotnet build KidShell.sln -p:Platform=x64 -c Release    production build
///
/// There is deliberately no environment variable, no configuration key, no
/// command-line switch and no marker file that turns developer mode on. Every
/// one of those would be reachable by a child who can open Notepad, and would
/// hand them a Parent Mode whose PIN is published in the README.
///
/// A Release build therefore:
///   * refuses the development fallback PIN outright,
///   * has no debug shortcut into Parent Mode,
///   * cannot finish first-run setup without a real parent PIN,
///   * draws no development watermark, because there is nothing to warn about.
/// </summary>
public sealed class BuildRuntimeEnvironment : IRuntimeEnvironment
{
    public static IRuntimeEnvironment Current { get; } = new BuildRuntimeEnvironment();

    public KidShellRuntimeMode Mode =>
#if DEBUG
        KidShellRuntimeMode.Development;
#else
        KidShellRuntimeMode.Production;
#endif

    public string BuildConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif
}
