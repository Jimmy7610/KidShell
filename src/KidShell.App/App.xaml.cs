using KidShell.App.Services;
using KidShell.App.ViewModels;
using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Launching;
using KidShell.Core.Onboarding;
using KidShell.Core.Security;
using KidShell.Core.Runtime;
using KidShell.Core.Security.Storage;
using KidShell.Core.Security.Readiness;
using KidShell.App.Services.Apps;
using KidShell.App.Services.Security;
using KidShell.Core.Apps;
using KidShell.Core.ScreenTime;
using KidShell.Core.Sessions;
using KidShell.Core.Security.Transactions;
using KidShell.Core.Watchdog;
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

        // OPSV FINDING 01 - the protected store existed and nothing used it.
        //
        // The parent's decisions no longer come from the file in the child's
        // own LocalState. ProtectedConfigurationStore routes each part of the
        // configuration to the store its trust class requires, and is the one
        // place that decides what to do when the protected store is not
        // usable: a development build falls back loudly, a production build
        // refuses.
        //
        // The two implementations are told apart by the BUILD, not by
        // configuration, for the same reason the runtime environment is.
        services.AddSingleton<IProtectedPolicyStore>(sp =>
        {
            var logger = sp.GetRequiredService<IKidShellLogger>();

            return sp.GetRequiredService<IRuntimeEnvironment>().IsDevelopment
                ? new DevelopmentProtectedPolicyStore(AppPaths.DevelopmentPolicyDirectory, logger)
                : new FileSystemProtectedPolicyStore(AppPaths.ProtectedPolicyDirectory, logger);
        });

        // Registered as the concrete type, so nothing can resolve the
        // unprotected store by asking for IConfigurationStore - which is
        // exactly how the protected one came to be bypassed.
        services.AddSingleton(
            sp => new JsonConfigurationStore(AppPaths.ConfigurationFilePath, sp.GetRequiredService<IKidShellLogger>()));

        services.AddSingleton<IConfigurationStore>(sp => new ProtectedConfigurationStore(
            sp.GetRequiredService<JsonConfigurationStore>(),
            sp.GetRequiredService<IProtectedPolicyStore>(),
            sp.GetRequiredService<IRuntimeEnvironment>(),
            sp.GetRequiredService<IKidShellLogger>()));

        services.AddSingleton<IAppStateService, AppStateService>();

        services.AddSingleton<IExecutableResolver>(new WindowsExecutableResolver());
        services.AddSingleton<IProcessRunner, ProcessRunner>();

        // The launcher everything resolves is the GUARDED one.
        //
        // AppLauncher itself is registered only as a concrete type, so asking
        // for IAppLauncher cannot get you the unguarded one by accident. The
        // screen-time check therefore sits on the single path every launch
        // takes, rather than in each caller that remembered to write it.
        services.AddSingleton<AppLauncher>();
        services.AddSingleton<IAppLauncher>(sp => new ScreenTimeGuardedLauncher(
            sp.GetRequiredService<AppLauncher>(),
            sp.GetRequiredService<IScreenTimeCoordinator>(),
            sp.GetRequiredService<IKidShellLogger>()));

        // Installed-application discovery. Every scanner is read-only; the
        // catalogue merges and de-duplicates what they find.
        services.AddSingleton<IApplicationProfileLibrary>(ApplicationProfileLibrary.Default);
        services.AddSingleton<IApplicationScanner, StartMenuScanner>();
        services.AddSingleton<IApplicationScanner, RegistryApplicationScanner>();
        services.AddSingleton<IApplicationScanner, PackagedApplicationScanner>();
        services.AddSingleton<IApplicationCatalog, ApplicationCatalog>();

        services.AddSingleton<IParentPinService, ParentPinService>();

        // Parent Mode re-locks. Without this it stayed open until somebody
        // closed it, which on a machine the child also uses means it stayed
        // open. See ParentSession for the semantics.
        services.AddSingleton<IParentSession>(
            sp => new ParentSession(sp.GetRequiredService<IKidShellLogger>()));
        services.AddSingleton<IOnboardingService, OnboardingService>();

        // Security readiness. Detection and account discovery are read-only
        // implementations; there is deliberately no ISecurityMutator
        // registration, because no implementation of it exists anywhere.
        services.AddSingleton<ISystemFactsProvider, WindowsSystemFactsProvider>();
        services.AddSingleton<IWindowsAccountDiscovery, WindowsLocalAccountDiscovery>();
        services.AddSingleton<ISecurityReadinessService, SecurityReadinessService>();

        // Recovery manifests. Written before any future transaction applies
        // anything; nothing writes one yet because nothing applies anything.
        services.AddSingleton<IRecoveryManifestStore>(
            sp => new RecoveryManifestStore(AppPaths.RecoveryDirectory, sp.GetRequiredService<IKidShellLogger>()));

        // Screen time. The counter lives in its own file for the reason given
        // on AppPaths.ScreenTimeStatePath.
        // The counter is security state: a child who can edit it gets an
        // unlimited day, which PolicyDataClassification records as
        // EnforcementState rather than personalisation. It therefore goes to
        // the protected store too, with the same fall-back rules.
        services.AddSingleton<IScreenTimeStateStore>(sp => new ProtectedScreenTimeStateStore(
            new JsonScreenTimeStateStore(AppPaths.ScreenTimeStatePath, sp.GetRequiredService<IKidShellLogger>()),
            sp.GetRequiredService<IProtectedPolicyStore>(),
            sp.GetRequiredService<IRuntimeEnvironment>(),
            sp.GetRequiredService<IKidShellLogger>()));
        services.AddSingleton<ScreenTimeEngine>();

        // Something has to tick the engine and notice when a warning threshold
        // is crossed. Without this the engine is a tested calculator nobody
        // calls - which is exactly what it was until now.
        services.AddSingleton<IScreenTimeCoordinator, ScreenTimeCoordinator>();

        services.AddSingleton<ChildSessionManager>();
        services.AddSingleton<IWatchdog, ShellHealthMonitor>();

        // Simulated on purpose: the only ISessionController that exists does
        // not sign anybody out. See DevelopmentSessionController.
        services.AddSingleton<ISessionController, DevelopmentSessionController>();

        // The UI thread's own dispatcher, captured HERE because this runs on
        // it. A background thread cannot obtain one - GetForCurrentThread
        // returns null there - so asking later would fail exactly when the
        // marshalling was needed. See IUiDispatcher.
        services.AddSingleton<IUiDispatcher>(DispatcherQueueUiDispatcher.ForCurrentThread());

        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<ISystemStatusService, SystemStatusService>();

        // Presentation, not security: Child Mode is borderless full screen in a
        // shipped build and windowed in a developer one, so nobody gets trapped
        // on the machine the code is written on.
        services.AddSingleton<IChildPresentation, ChildPresentation>();
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
