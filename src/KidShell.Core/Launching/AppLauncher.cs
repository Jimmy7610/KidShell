using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;

namespace KidShell.Core.Launching;

/// <summary>
/// Maps a <see cref="KidAppDefinition"/> onto a launch attempt and turns every
/// possible outcome into a friendly <see cref="LaunchResult"/>. It never
/// throws: a missing or broken program is a UI state, not a crash.
/// </summary>
public sealed class AppLauncher : IAppLauncher
{
    private readonly IExecutableResolver _resolver;
    private readonly IProcessRunner _runner;
    private readonly IKidShellLogger _logger;

    public AppLauncher(IExecutableResolver resolver, IProcessRunner runner, IKidShellLogger logger)
    {
        _resolver = resolver;
        _runner = runner;
        _logger = logger;
    }

    public LaunchResult Launch(KidAppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.IsEnabled)
        {
            return Report(LaunchResult.Blocked(app));
        }

        ExecutableResolution resolution;
        try
        {
            resolution = _resolver.Resolve(app.ExecutablePath);
        }
        catch (Exception ex)
        {
            return Report(LaunchResult.Failed(app, $"Resolver threw: {ex.GetType().Name}: {ex.Message}"));
        }

        switch (resolution.Kind)
        {
            case ExecutableResolutionKind.Empty:
                return Report(LaunchResult.NotConfigured(app));

            case ExecutableResolutionKind.NotFound:
                return Report(LaunchResult.NotFound(app, resolution.Detail ?? app.ExecutablePath));

            case ExecutableResolutionKind.File:
            case ExecutableResolutionKind.ShellTarget:
                break;

            default:
                return Report(LaunchResult.Failed(app, $"Unknown resolution kind '{resolution.Kind}'."));
        }

        var target = resolution.ResolvedPath ?? app.ExecutablePath;
        var useShell = resolution.Kind == ExecutableResolutionKind.ShellTarget;

        try
        {
            _runner.Start(target, app.Arguments, useShell);
            return Report(LaunchResult.Success(app, target));
        }
        catch (Exception ex)
        {
            return Report(LaunchResult.Failed(app, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private LaunchResult Report(LaunchResult result)
    {
        var message = $"Launch '{result.AppId}' -> {result.Status}. {result.TechnicalDetail}".TrimEnd();

        if (result.Status is LaunchStatus.Failed or LaunchStatus.NotFound)
        {
            _logger.Warning("Launcher", message);
        }
        else
        {
            _logger.Info("Launcher", message);
        }

        return result;
    }
}
