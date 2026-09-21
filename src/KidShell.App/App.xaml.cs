using KidShell.App.Services;
using KidShell.App.ViewModels;
using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Launching;
using KidShell.Core.Onboarding;
using KidShell.Core.Security;
using KidShell.Core.Runtime;
using KidShell.Core.Security.Readiness;
using KidShell.App.Services.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace KidShell.App;

/// <summary>
/// Composition root.
///
/// Dependency injection is used here because KidShell genuinely has a service
/// graph worth wiring once - configuration, logging, launching, PIN - rather
/// than for its own sake.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = BuildServices();

        // Load configuration before the first frame so Child Mode never
        // flashes defaults it is about to replace.
        var state = Services.GetRequiredService<IAppStateService>();
        var status = state.Initialize();

        var logger = Services.GetRequiredService<IKidShellLogger>();
        logger.Info("App", $"KidShell 0.1 starting. Configuration status: {status}.");

        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        IKidShellLogger logger = new FileLogger(AppPaths.LogFilePath);
        services.AddSingleton(logger);

        // Decided by the compiler, not by configuration. See
        // BuildRuntimeEnvironment for why there is no runtime switch.
        services.AddSingleton<IRuntimeEnvironment>(BuildRuntimeEnvironment.Current);
        services.AddSingleton<IDeveloperOptions, DeveloperOptions>();

        services.AddSingleton<IConfigurationStore>(
            sp => new JsonConfigurationStore(AppPaths.ConfigurationFilePath, sp.GetRequiredService<IKidShellLogger>()));
        services.AddSingleton<IAppStateService, AppStateService>();

        services.AddSingleton<IExecutableResolver>(new WindowsExecutableResolver());
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IAppLauncher, AppLauncher>();

        services.AddSingleton<IParentPinService, ParentPinService>();
        services.AddSingleton<IOnboardingService, OnboardingService>();

        // Security readiness. Detection and account discovery are read-only
        // implementations; there is deliberately no ISecurityMutator
        // registration, because no implementation of it exists anywhere.
        services.AddSingleton<ISystemFactsProvider, WindowsSystemFactsProvider>();
        services.AddSingleton<IWindowsAccountDiscovery, WindowsLocalAccountDiscovery>();
        services.AddSingleton<ISecurityReadinessService, SecurityReadinessService>();

        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<ISystemStatusService, SystemStatusService>();
        services.AddSingleton<IAddAppFlow, AddAppFlow>();
        services.AddSingleton<IPinChangeFlow, PinChangeFlow>();
        services.AddSingleton<ISecurityDialogs, SecurityDialogs>();

        services.AddSingleton<OnboardingViewModel>();
        services.AddSingleton<ChildHomeViewModel>();
        services.AddSingleton<PinOverlayViewModel>();
        services.AddSingleton<ParentShellViewModel>();
        services.AddSingleton<ShellViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Last line of defence. A crash in front of a six-year-old is the worst
    /// possible outcome, so anything that escapes is logged and swallowed.
    /// </summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            Services?.GetService<IKidShellLogger>()?
                .Error("App", "Unhandled exception reached the application.", e.Exception);
        }
        catch
        {
            // Nothing sensible left to do.
        }

        e.Handled = true;
    }
}
