using KidShell.App.Services;
using KidShell.App.ViewModels;
using KidShell.Core.Configuration;
using KidShell.Core.Diagnostics;
using KidShell.Core.Launching;
using KidShell.Core.Onboarding;
using KidShell.Core.Security;
using KidShell.Core.Runtime;
using KidShell.Core.Security.Storage;
using KidShell.Core.Security.Broker;
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
        // Prefer the real protected store whenever this machine has actually
        // been provisioned, even in a Debug build. Installed pilot/debug
        // packages run under the child account too, so choosing LocalState
        // purely from #if DEBUG would silently make child-writable data
        // authoritative on a machine whose ProgramData boundary is healthy.
        //
        // Development LocalState remains available only when the real store
        // is not trustworthy and this is a development build. Release never
        // falls back.
        services.AddSingleton<IProtectedStateReader>(sp =>
        {
            var logger = sp.GetRequiredService<IKidShellLogger>();
            var protectedReader = new FileSystemProtectedStateReader(
                AppPaths.ProtectedPolicyDirectory, logger);

            if (protectedReader.Probe().IsTrustworthy)
            {
                logger.Info("Storage",
                    "Using the provisioned machine-wide protected policy store.");
                return protectedReader;
            }

            if (sp.GetRequiredService<IRuntimeEnvironment>().IsDevelopment)
            {
                logger.Warning("Storage",
                    "The machine-wide protected store is not trustworthy; " +
                    "this development build is using the child-writable development store.");

                return new DevelopmentProtectedStateReader(
                    AppPaths.DevelopmentPolicyDirectory, logger);
            }

            return protectedReader;
        });

        // OPSV RETEST 2, FINDING 01. Reading and writing are different
        // responsibilities with different privileges, so they are different
        // registrations.
        //
        // The protected store is trustworthy only when the account KidShell
        // runs as CANNOT write to it, which made a child-process writer a
        // contradiction: Ready exactly when it could not be used. Production
        // therefore asks the elevated helper through a typed request that
        // names a DOCUMENT and never a path.
        // PRIVILEGED BROKER HARDENING. The transport is a named pipe to a
        // service that is already running as LocalSystem, not a process
        // started per request.
        //
        // The old one started KidShell.SecurityHost with the streams
        // redirected and nothing elevating it, so the helper refused every
        // request on every real child account - and adding a UAC prompt
        // would not have repaired it, because screen time is written on a
        // timer and a shell a child operates cannot prompt on a timer.
        services.AddSingleton<IElevatedBrokerClient>(sp => new NamedPipeElevatedBrokerClient(
            BrokerEndpoint.PipeName, sp.GetRequiredService<IKidShellLogger>()));

        // Kept as one object so the development approval channel can commit
        // what the development writer staged.
        services.AddSingleton(sp => new DirectProtectedStateWriter(
            AppPaths.DevelopmentPolicyDirectory, sp.GetRequiredService<IKidShellLogger>()));

        services.AddSingleton<IProtectedStateWriter>(sp =>
        {
            var logger = sp.GetRequiredService<IKidShellLogger>();
            var reader = sp.GetRequiredService<IProtectedStateReader>();

            // The writer must follow the selected storage boundary, not the
            // build configuration. A Debug package on a provisioned child PC
            // therefore uses the LocalSystem broker just like Release.
            return reader is DevelopmentProtectedStateReader
                ? sp.GetRequiredService<DirectProtectedStateWriter>()
                : new BrokeredProtectedStateWriter(
                    sp.GetRequiredService<IElevatedBrokerClient>(), logger);
        });

        // Who may turn a proposed policy into the live one.
        //
        // Not this process. KidShell runs as the child, and a modified copy
        // of it running as the child is indistinguishable from the real one
        // without code signing that does not exist yet - so the authority
        // for a policy change is a Windows one, and a parent saving settings
        // answers a consent prompt. Exactly once, for an action they took
        // deliberately; never for the writes that happen during ordinary
        // use. See ParentPolicyApproval.cs.
        services.AddSingleton<IParentPolicyApprovalChannel>(sp =>
        {
            var logger = sp.GetRequiredService<IKidShellLogger>();
            var reader = sp.GetRequiredService<IProtectedStateReader>();

            if (reader is DevelopmentProtectedStateReader)
            {
                return new LocalParentPolicyApprovalChannel(
                    sp.GetRequiredService<DirectProtectedStateWriter>(), logger);
            }

            return File.Exists(AppPaths.SecurityHostPath)
                ? new ElevatedCommitApprovalChannel(AppPaths.SecurityHostPath, logger)
                : new UnavailableParentPolicyApprovalChannel();
        });

        // The capability the security service issues when IT has verified a
        // parent's PIN. In memory, for the length of a parent session.
        services.AddSingleton<ParentCapabilityHolder>();

        // Registered as the concrete type, so nothing can resolve the
        // unprotected store by asking for IConfigurationStore - which is
        // exactly how the protected one came to be bypassed.
        services.AddSingleton(
            sp => new JsonConfigurationStore(AppPaths.ConfigurationFilePath, sp.GetRequiredService<IKidShellLogger>()));

        services.AddSingleton<IConfigurationStore>(sp => new ProtectedConfigurationStore(
            sp.GetRequiredService<JsonConfigurationStore>(),
            sp.GetRequiredService<IProtectedStateReader>(),
            sp.GetRequiredService<IProtectedStateWriter>(),
            sp.GetRequiredService<IRuntimeEnvironment>(),
            sp.GetRequiredService<IKidShellLogger>(),
            time: null,
            sp.GetRequiredService<IParentPolicyApprovalChannel>()));

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

        // The PIN throttle is security state and goes through the same
        // privileged write path as everything else. It used to live only in
        // memory, so restarting the shell returned the attempts a child had
        // already spent.
        services.AddSingleton(sp => new ProtectedPinThrottleStore(
            sp.GetRequiredService<IProtectedStateReader>(),
            sp.GetRequiredService<IProtectedStateWriter>(),
            sp.GetRequiredService<IKidShellLogger>()));

        // The comparison happens on the privileged side when there is one.
        //
        // A throttle enforced by the process being throttled is a
        // suggestion, and a hash comparison performed by a program running
        // as the child is one a modified copy of that program can return
        // true from. Neither could be fixed by writing this class more
        // carefully: the problem was where the code ran.
        services.AddSingleton<IParentAuthenticator>(sp => new BrokeredParentAuthenticator(
            sp.GetRequiredService<IElevatedBrokerClient>(),
            sp.GetRequiredService<IKidShellLogger>()));

        services.AddSingleton<IParentPinService>(sp => new ParentPinService(
            sp.GetRequiredService<IAppStateService>(),
            sp.GetRequiredService<IRuntimeEnvironment>(),
            sp.GetRequiredService<IKidShellLogger>(),
            time: null,
            sp.GetRequiredService<ProtectedPinThrottleStore>(),
            sp.GetRequiredService<IParentAuthenticator>(),
            sp.GetRequiredService<ParentCapabilityHolder>()));

        // Parent Mode re-locks. Without this it stayed open until somebody
        // closed it, which on a machine the child also uses means it stayed
        // open. See ParentSession for the semantics.
        // The parent session's own heartbeat. Deliberately not the screen-time
        // timer: that one is allowed to be silent, and a session lifetime
        // cannot depend on a signal that is allowed to be silent.
        services.AddSingleton<IPeriodicScheduler, TimerPeriodicScheduler>();

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
            sp.GetRequiredService<IProtectedStateReader>(),
            sp.GetRequiredService<IProtectedStateWriter>(),
            sp.GetRequiredService<IRuntimeEnvironment>(),
            sp.GetRequiredService<IKidShellLogger>()));

        // The three screen-time changes that make the child's situation
        // LOOSER - bonus minutes, an unlimited day, a reset - do not travel
        // as a state this process composed. The privileged side applies them
        // to the counter it holds, because "here is the new state" from a
        // process running as the child is "used seconds: 0" waiting to
        // happen.
        services.AddSingleton<IScreenTimeParentAuthority>(sp => new BrokeredScreenTimeParentAuthority(
            sp.GetRequiredService<IElevatedBrokerClient>(),
            sp.GetRequiredService<ParentCapabilityHolder>(),
            sp.GetRequiredService<IKidShellLogger>()));

        services.AddSingleton(sp => new ScreenTimeEngine(
            sp.GetRequiredService<IAppStateService>(),
            sp.GetRequiredService<IScreenTimeStateStore>(),
            sp.GetRequiredService<IKidShellLogger>(),
            time: null,
            sp.GetRequiredService<IScreenTimeParentAuthority>()));

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
