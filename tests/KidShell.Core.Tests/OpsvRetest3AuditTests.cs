using System.Text.Json;
using KidShell.Core.Apps;
using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using KidShell.Core.Runtime;
using KidShell.Core.ScreenTime;
using KidShell.Core.Security;
using KidShell.Core.Security.AppControl;
using KidShell.Core.Security.Broker;
using KidShell.Core.Security.Storage;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// THE SIX PREVIOUS FINDINGS, ATTACKED AGAIN THROUGH THE NEW TRANSPORT.
///
/// The transport under all six changed in this pass. A fix that was correct
/// against a process-per-request helper is not automatically correct against
/// a LocalSystem service with an authorization matrix, and the whole history
/// of this project is of correct components reached through a path that
/// undid them.
///
/// So none of them is assumed. Each one is attacked with the thing the new
/// architecture makes possible - a child's session sending whatever it likes
/// to something that will act on it as SYSTEM.
/// </summary>
public class OpsvRetest3AuditTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 19, 0, 0, TimeSpan.Zero);

    private static readonly IRuntimeEnvironment Production =
        new RuntimeEnvironment(KidShellRuntimeMode.Production, "Release");

    private static readonly JsonSerializerOptions Camel =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static BrokerCaller Child => new()
    {
        Class = BrokerCallerClass.ChildSession,
        Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001",
        AccountName = "barn",
        SessionId = 1
    };

    private static BrokerCaller Administrator => new()
    {
        Class = BrokerCallerClass.Administrator,
        Sid = "S-1-5-21-1111111111-2222222222-3333333333-1000",
        AccountName = "foralder",
        IsElevated = true,
        SessionId = 1
    };

    private static ElevatedRequest Request(ElevatedOperationKind kind) => new()
    {
        Kind = kind, RequestId = Guid.NewGuid().ToString("n"), DryRun = false
    };

    private static string Line(ElevatedRequest request) => ElevatedProtocol.Serialize(request);

    // --------------------------------------------------------- finding 1
    // The protected write path.

    [Fact]
    public void Finding_1_the_reader_still_cannot_write_and_the_writer_still_names_no_path()
    {
        // The original property, unchanged by the new transport.
        Assert.DoesNotContain(
            typeof(IProtectedStateReader).GetMethods(),
            m => m.Name.Contains("Write", StringComparison.OrdinalIgnoreCase)
                 || m.Name.Contains("Save", StringComparison.OrdinalIgnoreCase));

        foreach (var parameter in typeof(IProtectedStateWriter).GetMethods().SelectMany(m => m.GetParameters()))
        {
            Assert.DoesNotContain("path", parameter.Name!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("file", parameter.Name!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("directory", parameter.Name!, StringComparison.OrdinalIgnoreCase);
        }

        // And the new one it needed: the request contract still carries no
        // destination either, so a caller cannot name one over the pipe.
        foreach (var property in typeof(ElevatedRequest).GetProperties().Select(p => p.Name.ToLowerInvariant()))
        {
            Assert.False(property is "path" or "directory" or "destination" or "filename",
                $"ElevatedRequest grew a destination: {property}");
        }
    }

    [Fact]
    public void Finding_1_the_transport_can_now_actually_reach_the_privileged_side()
    {
        // The regression of the retest-2 fix itself. The boundary was right
        // and the transport could not work, so every protected write failed
        // on every real child account.
        var old = new ProcessElevatedBrokerClient(
            @"C:\Program Files\KidShell\KidShell.SecurityHost.exe", new RecordingLogger());

        Assert.False(old.LaunchPlan.Evaluate().CanSucceed);

        var replacement = new NamedPipeElevatedBrokerClient(
            BrokerEndpoint.PipeName, new RecordingLogger());

        Assert.True(replacement.LaunchPlan.Evaluate().CanSucceed);
        Assert.False(replacement.LaunchPlan.PromptsPerRequest);
    }

    // --------------------------------------------------------- finding 2
    // The missing protected policy fallback.

    [Fact]
    public void Finding_2_a_hostile_user_file_still_cannot_replace_a_missing_policy()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "kidshell.config.json");
        var logger = new RecordingLogger();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);

        ProtectedConfigurationStore Store() => new(
            new JsonConfigurationStore(path, logger), vault, vault, Production, logger);

        var configured = KidShellConfiguration.CreateDefault();
        configured.ScreenTime.IsEnabled = true;
        configured.ScreenTime.WeekdayMinutes = 30;

        Assert.True(Store().Save(configured));

        vault.Delete(ProtectedDocument.ParentPolicy);

        File.WriteAllText(path, """
        {
          "schemaVersion": 2,
          "parentPin": { "hash": "hostile", "salt": "hostile", "iterations": 210000 },
          "screenTime": { "isEnabled": false, "weekdayMinutes": 1440 }
        }
        """);

        var state = new AppStateService(Store(), logger);

        Assert.Equal(ConfigurationLoadStatus.Failed, state.Initialize());
        Assert.NotEqual("hostile", state.Current.ParentPin.Hash);
    }

    [Fact]
    public void Finding_2_a_child_cannot_write_the_marker_that_decides_first_run()
    {
        // The new attack the service makes possible: ask SYSTEM to unwrite
        // the provisioning marker, so the next absent policy looks like a
        // new machine again.
        var store = new InMemoryPrivilegedStore();
        store.Seed(ProtectedDocument.ProvisioningMarker,
            ProtectedPolicyTrustEvaluator.MarkerDocument(Start));

        var time = new FakeTimeProvider(Start);
        var server = new ElevatedBrokerServer(
            store, new ParentCapabilityRegistry(time), new RecordingLogger(), time);

        var response = server.Handle(
            Line(Request(ElevatedOperationKind.MarkProvisioned) with
            {
                ProtectedPayload = """{"schemaVersion":1,"provisioned":false}"""
            }), Child);

        Assert.False(response.Success);
        Assert.Equal(BrokerFailureReason.NotAuthorized, response.Reason);
        Assert.Contains("true", store.Read(ProtectedDocument.ProvisioningMarker)!);
    }

    [Fact]
    public void Finding_2_an_approved_first_policy_marks_the_machine_provisioned()
    {
        var store = new InMemoryPrivilegedStore();
        var time = new FakeTimeProvider(Start);
        var server = new ElevatedBrokerServer(
            store, new ParentCapabilityRegistry(time), new RecordingLogger(), time);

        var document = JsonSerializer.Serialize(new ParentPolicyDocument(), Camel);

        var staged = server.Handle(
            Line(Request(ElevatedOperationKind.StageParentPolicy) with
            {
                ProtectedPayload = document
            }), Child);

        Assert.True(staged.Success);

        var committed = server.Handle(
            Line(Request(ElevatedOperationKind.CommitStagedParentPolicy) with
            {
                ExpectedDigest = staged.StagedDigest
            }), Administrator);

        Assert.True(committed.Success);
        Assert.Contains("\"provisioned\":true",
            store.Read(ProtectedDocument.ProvisioningMarker)!);
    }

    [Fact]
    public void Finding_2_a_refused_approval_leaves_the_user_file_byte_for_byte()
    {
        // The transaction property, re-checked now that the authoritative
        // half goes through a stage and an approval rather than one write.
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "kidshell.config.json");
        var logger = new RecordingLogger();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);

        var store = new ProtectedConfigurationStore(
            new JsonConfigurationStore(path, logger), vault, vault, Production, logger,
            time: null, approval: new RefusingApproval());

        var first = KidShellConfiguration.CreateDefault();
        first.Child.Name = "Nils";

        // Nothing durable yet, so write the user file through a store with
        // no approval channel to get a baseline on disk.
        new ProtectedConfigurationStore(
            new JsonConfigurationStore(path, logger), vault, vault, Production, logger)
            .Save(first);

        var before = File.ReadAllBytes(path);

        var second = first.Clone();
        second.Child.Name = "Nagon annan";
        second.ScreenTime.WeekdayMinutes = 999;

        Assert.False(store.Save(second));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private sealed class RefusingApproval : IParentPolicyApprovalChannel
    {
        public bool IsAvailable => true;

        public ProtectedWriteResult Approve(string digest) =>
            ProtectedWriteResult.Reject("(test) the parent said no");
    }

    // --------------------------------------------------------- finding 3
    // Screen-time monotonic persistence.

    [Fact]
    public void Finding_3_a_restart_still_never_refunds_time()
    {
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);
        var logger = new RecordingLogger();

        var configuration = KidShellConfiguration.CreateDefault();
        configuration.ScreenTime.IsEnabled = true;
        configuration.ScreenTime.WeekdayMinutes = 60;

        var state = new StubState(configuration);
        var time = new FakeTimeProvider(Start);

        IScreenTimeStateStore Store() => new ProtectedScreenTimeStateStore(
            new InMemoryScreenTimeStateStore(), vault, vault, Production, logger);

        var first = new ScreenTimeEngine(state, Store(), logger, time);

        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            first.Tick();
        }

        var used = first.State.UsedSeconds;
        vault.RefuseWrites = true;

        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            first.Tick();
        }

        vault.RefuseWrites = false;

        var restarted = new ScreenTimeEngine(state, Store(), logger, time);

        Assert.True(restarted.State.UsedSeconds >= used);
        Assert.True(restarted.Evaluate().IsBlocked);
    }

    [Fact]
    public void Finding_3_the_service_refuses_the_write_the_old_design_would_have_accepted()
    {
        // The attack the privileged service makes newly available, and the
        // reason the transition rules exist: ask SYSTEM for a zero.
        var store = new InMemoryPrivilegedStore();
        store.Seed(ProtectedDocument.ScreenTimeState, JsonSerializer.Serialize(
            new ScreenTimeState { LocalDate = "2026-09-30", UsedSeconds = 3600, Sequence = 20 }, Camel));

        var time = new FakeTimeProvider(Start);
        var server = new ElevatedBrokerServer(
            store, new ParentCapabilityRegistry(time), new RecordingLogger(), time);

        foreach (var attempt in new[]
                 {
                     new ScreenTimeState { LocalDate = "2026-09-30", UsedSeconds = 0, Sequence = 21 },
                     new ScreenTimeState { LocalDate = "2026-09-30", UsedSeconds = 3600, Sequence = 21, BonusMinutes = 600 },
                     new ScreenTimeState { LocalDate = "2026-09-30", UsedSeconds = 3600, Sequence = 21, UnlimitedForToday = true },
                     new ScreenTimeState { LocalDate = "2026-09-29", UsedSeconds = 0, Sequence = 21 }
                 })
        {
            var response = server.Handle(
                Line(Request(ElevatedOperationKind.SaveScreenTimeState) with
                {
                    ProtectedPayload = JsonSerializer.Serialize(attempt, Camel)
                }), Child);

            Assert.False(response.Success, $"the service accepted {JsonSerializer.Serialize(attempt, Camel)}");
        }

        Assert.Contains("3600", store.Read(ProtectedDocument.ScreenTimeState)!);
    }

    // --------------------------------------------------------- finding 4
    // Parent auto-relock.

    [Fact]
    public void Finding_4_the_session_still_relocks_with_no_screen_time_activity()
    {
        var time = new FakeTimeProvider(Start);
        var session = new ParentSession(new RecordingLogger(), time);

        using var scheduler = new ManualPeriodicScheduler();
        scheduler.Start(ParentSession.HeartbeatInterval, session.Evaluate);

        session.Begin();

        for (var i = 0; i < 40; i++)
        {
            time.Advance(ParentSession.HeartbeatInterval);
            scheduler.Advance(ParentSession.HeartbeatInterval);
        }

        Assert.False(session.IsUnlocked);
        Assert.Equal(ParentSessionEndReason.Inactivity, session.LastEndReason);
    }

    [Fact]
    public void Finding_4_the_capability_outlives_nothing_it_should_not()
    {
        // The new question the pass raises: a session that relocks in the UI
        // while the service still believes a parent is present would be a
        // relock in appearance only. The capability is bounded, and it is
        // bounded shorter than the shell could plausibly be left open.
        Assert.True(BrokerEndpoint.ParentCapabilityLifetime < TimeSpan.FromHours(1));

        var holder = new ParentCapabilityHolder();
        holder.Hold("something");

        Assert.True(holder.IsHeld);

        holder.Release();

        Assert.False(holder.IsHeld);
    }

    // --------------------------------------------------------- finding 5
    // Store identity through to AppLocker.

    [Fact]
    public void Finding_5_a_packaged_app_still_reaches_a_publisher_rule()
    {
        using var dir = new TempDirectory();

        var approved = new KidAppDefinition
        {
            Id = "calc",
            DisplayName = "Miniräknare",
            ProgramName = "Miniräknare",
            IsEnabled = true,
            LaunchKind = ApplicationLaunchKind.PackagedApp,
            ExecutablePath = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            Publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
            PackageFamilyName = "Microsoft.WindowsCalculator_8wekyb3d8bbwe"
        };

        var store = new JsonConfigurationStore(
            Path.Combine(dir.Path, "kidshell.config.json"), new RecordingLogger());

        var configuration = KidShellConfiguration.CreateDefault();
        configuration.Apps.Clear();
        configuration.Apps.Add(approved);
        store.Save(configuration);

        var policy = AppControlPolicyBuilder.Build(
            store.Load().Configuration,
            ApplicationProfileLibrary.Default,
            @"C:\Program Files\KidShell\KidShell.exe",
            "S-1-5-21-0-0-0-1001");

        var rule = Assert.Single(policy.ApplicationRules, r => r.Collection == RuleCollection.Appx);

        Assert.Equal(RuleStrategy.Publisher, rule.Strategy);
        Assert.DoesNotContain(policy.Warnings, w => w.Code == "packaged-app-without-identity");
    }

    [Fact]
    public void Finding_5_identity_survives_the_staging_round_trip()
    {
        // New in this pass: the policy now goes to the privileged side as a
        // staged document and comes back through a commit. An identity lost
        // in that round trip would disarm Secure Mode exactly as a lost
        // Clone did, and nothing downstream would notice.
        var store = new InMemoryPrivilegedStore();
        var time = new FakeTimeProvider(Start);
        var server = new ElevatedBrokerServer(
            store, new ParentCapabilityRegistry(time), new RecordingLogger(), time);

        var policy = new ParentPolicyDocument
        {
            Apps =
            [
                new KidAppDefinition
                {
                    Id = "calc",
                    DisplayName = "Miniräknare",
                    ProgramName = "Miniräknare",
                    LaunchKind = ApplicationLaunchKind.PackagedApp,
                    Publisher = "CN=Microsoft Corporation",
                    PackageFamilyName = "Microsoft.WindowsCalculator_8wekyb3d8bbwe"
                }
            ]
        };

        var document = JsonSerializer.Serialize(policy, Camel);

        var staged = server.Handle(
            Line(Request(ElevatedOperationKind.StageParentPolicy) with { ProtectedPayload = document }),
            Child);

        Assert.True(server.Handle(
            Line(Request(ElevatedOperationKind.CommitStagedParentPolicy) with
            {
                ExpectedDigest = staged.StagedDigest
            }), Administrator).Success);

        var committed = JsonSerializer.Deserialize<ParentPolicyDocument>(
            store.Read(ProtectedDocument.ParentPolicy)!,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var app = Assert.Single(committed!.Apps);

        Assert.Equal("CN=Microsoft Corporation", app.Publisher);
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe", app.PackageFamilyName);
        Assert.True(app.IsSecureModeReady);
    }

    // ----------------------------------------------- the pin throttle

    [Fact]
    public void The_pin_throttle_still_survives_a_restart_of_the_shell()
    {
        var time = new FakeTimeProvider(Start);
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);
        var configuration = KidShellConfiguration.CreateDefault();
        var logger = new RecordingLogger();

        ParentPinService Service() => new(
            new StubState(configuration), Production, logger, time,
            new ProtectedPinThrottleStore(vault, vault, logger, time));

        var first = Service();

        for (var i = 0; i <= PinAttemptPolicy.FreeAttempts; i++)
        {
            first.Verify("000000");
        }

        Assert.Equal(PinVerificationResult.Throttled, Service().Verify("000000"));
    }

    [Fact]
    public void The_pin_throttle_now_also_survives_a_child_that_lies_about_it()
    {
        // What the retest-2 fix could not do. The throttle was persisted by
        // the process being throttled, so a modified shell simply wrote a
        // clean one. The service refuses that write, and the service is also
        // now the thing that does the comparing - so there is no longer a
        // path where the child's own answer about the PIN is the one that
        // counts.
        var store = new InMemoryPrivilegedStore();

        store.Seed(ProtectedDocument.PinThrottleState, JsonSerializer.Serialize(
            new PinThrottleState
            {
                FailedAttemptCount = 9,
                CooldownUntilUtc = Start.AddSeconds(90)
            }, Camel));

        var time = new FakeTimeProvider(Start);
        var server = new ElevatedBrokerServer(
            store, new ParentCapabilityRegistry(time), new RecordingLogger(), time);

        var response = server.Handle(
            Line(Request(ElevatedOperationKind.SavePinThrottleState) with
            {
                ProtectedPayload = JsonSerializer.Serialize(
                    new PinThrottleState { FailedAttemptCount = 0, CooldownUntilUtc = null }, Camel)
            }), Child);

        Assert.False(response.Success);

        var after = ProtectedStateTransitionRules.Parse<PinThrottleState>(
            store.Read(ProtectedDocument.PinThrottleState))!;

        Assert.Equal(9, after.FailedAttemptCount);
    }

    private sealed class StubState : IAppStateService
    {
        public StubState(KidShellConfiguration configuration) => Current = configuration;

        public KidShellConfiguration Current { get; private set; }

        public ConfigurationLoadStatus LoadStatus => ConfigurationLoadStatus.Loaded;

        public string? LoadDetail => null;

        public string ConfigurationFilePath => "(in-memory)";

        public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

        public ConfigurationLoadStatus Initialize() => ConfigurationLoadStatus.Loaded;

        public KidShellConfiguration CreateDraft() => Current.Clone();

        public bool Commit(KidShellConfiguration draft)
        {
            Current = draft;
            ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(Current));
            return true;
        }

        public bool SaveCurrent() => true;
    }
}
