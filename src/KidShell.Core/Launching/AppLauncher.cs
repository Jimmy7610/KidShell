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

        // A packaged application is activated through the apps folder by its
        // AUMID. Sending it to the executable resolver would ask the file
        // system about an identity that is not a path - which is how a Store
        // app that Windows can start perfectly well became "not found".
        if (app.LaunchKind == ApplicationLaunchKind.PackagedApp)
        {
            return LaunchPackaged(app);
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

    /// <summary>
    /// Starts a Store application by its AUMID.
    ///
    /// shell:AppsFolder&#92;&lt;AUMID&gt; is the documented way to activate a
    /// packaged application from a shell, and it needs the shell rather than
    /// CreateProcess - there is no executable to start.
    /// </summary>
    private LaunchResult LaunchPackaged(KidAppDefinition app)
    {
        var check = LaunchTargetPolicy.Check(ApplicationLaunchKind.PackagedApp, app.ExecutablePath);

        if (!check.IsAllowed)
        {
            return Report(check.Verdict == ManualProgramVerdict.Empty
                ? LaunchResult.NotConfigured(app)
                : LaunchResult.Failed(app, $"Packaged identity refused: {check.Verdict}."));
        }

        var target = $@"shell:AppsFolder\{app.ExecutablePath.Trim()}";

        try
        {
            _runner.Start(target, app.Arguments, useShellExecute: true);
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
