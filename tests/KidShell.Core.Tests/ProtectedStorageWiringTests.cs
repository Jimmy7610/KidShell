using System.Text.Json;
using KidShell.Core.Configuration;
using KidShell.Core.Runtime;
using KidShell.Core.ScreenTime;
using KidShell.Core.Security.Storage;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// OPSV FINDING 01 — the protected store existed and the product did not use it.
///
/// ProtectedStorePlan, ProtectedStoreGate and PolicyDataClassification were
/// all written, all tested, and registered nowhere. The application went on
/// reading the PIN, the approved apps and the screen-time policy out of one
/// file in the signed-in user's own LocalState, which in the shipped
/// architecture is the child's profile.
///
/// The previous tests described ACLs. These describe what the product does.
/// </summary>
public class ProtectedStorageWiringTests
{
    private static readonly IRuntimeEnvironment Production =
        new RuntimeEnvironment(KidShellRuntimeMode.Production, "Release");

    private static readonly IRuntimeEnvironment Development =
        new RuntimeEnvironment(KidShellRuntimeMode.Development, "Debug");

    private static ProtectedConfigurationStore Store(
        TempDirectory dir, IProtectedPolicyStore protectedStore, IRuntimeEnvironment environment)
    {
        var logger = new RecordingLogger();
        var json = new JsonConfigurationStore(Path.Combine(dir.Path, "kidshell.config.json"), logger);

        return new ProtectedConfigurationStore(json, protectedStore, environment, logger);
    }

    // ------------------------------------ production requires a real store

    [Theory]
    [InlineData(ProtectedStoreStatus.NotProvisioned)]
    [InlineData(ProtectedStoreStatus.PermissionsWrong)]
    [InlineData(ProtectedStoreStatus.Unavailable)]
    [InlineData(ProtectedStoreStatus.DevelopmentOnly)]
    public void A_production_build_refuses_to_load_policy_without_a_protected_store(
        ProtectedStoreStatus status)
    {
        using var dir = new TempDirectory();
        var store = Store(dir, new InMemoryProtectedPolicyStore(status), Production);

        var result = store.Load();

        // Not "loaded with defaults", which would read as working. Failed.
        Assert.Equal(ConfigurationLoadStatus.Failed, result.Status);
    }

    [Fact]
    public void A_production_build_refuses_to_save_policy_without_a_protected_store()
    {
        using var dir = new TempDirectory();
        var unprotected = Path.Combine(dir.Path, "kidshell.config.json");
        var logger = new RecordingLogger();

        var store = new ProtectedConfigurationStore(
            new JsonConfigurationStore(unprotected, logger),
            new InMemoryProtectedPolicyStore(ProtectedStoreStatus.NotProvisioned),
            Production,
            logger);

        Assert.False(store.Save(KidShellConfiguration.CreateDefault()));

        // And nothing was written to the child-writable file on the way out.
        // A refusal that still wrote the policy would be the original defect
        // with an error message attached.
        Assert.False(File.Exists(unprotected));
    }

    [Fact]
    public void A_child_writable_store_is_never_good_enough_in_production()
    {
        // PermissionsWrong is the dangerous one: the store is THERE, so
        // everything looks provisioned, and the child can write it.
        var state = new ProtectedStoreState(ProtectedStoreStatus.PermissionsWrong, "child can write");

        Assert.False(ProtectedStoreGate.MayUseUnprotectedStorage(state, isDevelopmentBuild: false));
        Assert.False(state.IsTrustworthy);
    }

    // ----------------------------------------- development stays workable

    [Fact]
    public void A_development_build_carries_on_without_one()
    {
        using var dir = new TempDirectory();
        var store = Store(dir, new InMemoryProtectedPolicyStore(ProtectedStoreStatus.DevelopmentOnly), Development);

        var result = store.Load();

        Assert.NotEqual(ConfigurationLoadStatus.Failed, result.Status);
        Assert.True(store.Save(KidShellConfiguration.CreateDefault()));
    }

    [Fact]
    public void The_development_store_never_claims_to_be_protected()
    {
        using var dir = new TempDirectory();
        var store = new DevelopmentProtectedPolicyStore(dir.Path, new RecordingLogger());

        var state = store.Probe();

        Assert.Equal(ProtectedStoreStatus.DevelopmentOnly, state.Status);
        Assert.False(state.IsTrustworthy);
    }

    // ------------------------------------------------ the boundary itself

    [Fact]
    public void Parent_policy_goes_to_the_protected_store_and_the_profile_does_not()
    {
        using var dir = new TempDirectory();
        var unprotected = Path.Combine(dir.Path, "kidshell.config.json");
        var logger = new RecordingLogger();
        var vault = new InMemoryProtectedPolicyStore();

        var store = new ProtectedConfigurationStore(
            new JsonConfigurationStore(unprotected, logger), vault, Production, logger);

        var configuration = KidShellConfiguration.CreateDefault();
        configuration.Child.Name = "Lucas";
        configuration.Child.AvatarId = "whale";
        configuration.ScreenTime.WeekdayMinutes = 45;

        Assert.True(store.Save(configuration));

        var policy = vault.Read(ProtectedConfigurationStore.PolicyDocumentName);
        Assert.NotNull(policy);

        // The parent's decisions are in the protected document.
        Assert.Contains("45", policy);

        // The child's own choices are in the ordinary file, deliberately.
        // Protecting an avatar would mean an elevation prompt to change a
        // picture, which is how a product teaches people to click through
        // elevation prompts.
        var onDisk = File.ReadAllText(unprotected);
        Assert.Contains("Lucas", onDisk);
        Assert.Contains("whale", onDisk);
    }

    [Fact]
    public void Policy_is_read_back_from_the_protected_store_not_from_the_file()
    {
        using var dir = new TempDirectory();
        var unprotected = Path.Combine(dir.Path, "kidshell.config.json");
        var logger = new RecordingLogger();
        var vault = new InMemoryProtectedPolicyStore();

        var store = new ProtectedConfigurationStore(
            new JsonConfigurationStore(unprotected, logger), vault, Production, logger);

        var configuration = KidShellConfiguration.CreateDefault();
        configuration.ScreenTime.WeekdayMinutes = 45;
        store.Save(configuration);

        // The child edits the file they own, the way they can today.
        var tampered = File.ReadAllText(unprotected).Replace("\"weekdayMinutes\": 45", "\"weekdayMinutes\": 999");
        File.WriteAllText(unprotected, tampered);

        var reloaded = store.Load();

        Assert.Equal(45, reloaded.Configuration.ScreenTime.WeekdayMinutes);
    }

    [Fact]
    public void Malformed_protected_data_does_not_silently_reset_the_protection()
    {
        using var dir = new TempDirectory();
        var vault = new InMemoryProtectedPolicyStore();
        var store = Store(dir, vault, Production);

        store.Save(KidShellConfiguration.CreateDefault());
        vault.Corrupt(ProtectedConfigurationStore.PolicyDocumentName);

        var result = store.Load();

        // NOT a quiet fall back to the unprotected copy. Damaging one file
        // must not be enough to remove the protection from all of it.
        Assert.Equal(ConfigurationLoadStatus.Failed, result.Status);
    }

    [Fact]
    public void No_plaintext_pin_reaches_either_store()
    {
        using var dir = new TempDirectory();
        var unprotected = Path.Combine(dir.Path, "kidshell.config.json");
        var logger = new RecordingLogger();
        var vault = new InMemoryProtectedPolicyStore();

        var store = new ProtectedConfigurationStore(
            new JsonConfigurationStore(unprotected, logger), vault, Production, logger);

        var configuration = KidShellConfiguration.CreateDefault();
        var (hash, salt) = KidShell.Core.Security.PinHasher.Hash("135790");
        configuration.ParentPin.Hash = hash;
        configuration.ParentPin.Salt = salt;
        configuration.ParentPin.Iterations = KidShell.Core.Security.PinHasher.DefaultIterations;

        store.Save(configuration);

        var policy = vault.Read(ProtectedConfigurationStore.PolicyDocumentName) ?? string.Empty;

        Assert.DoesNotContain("135790", policy);
        Assert.DoesNotContain("135790", File.ReadAllText(unprotected));

        // The hash IS there - what must never be written is the PIN. Compared
        // after deserialising rather than as a substring, because base64 can
        // contain characters the JSON encoder escapes.
        var stored = JsonSerializer.Deserialize<ParentPolicyDocument>(
            policy, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Equal(hash, stored!.ParentPin.Hash);
    }

    // ----------------------------------------- the counter is protected too

    [Fact]
    public void Screen_time_usage_is_classified_as_something_the_child_must_not_write()
    {
        var usage = PolicyDataClassification.Items
            .Where(i => i.Name.StartsWith("screenTimeState.", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(usage);
        Assert.All(usage, item => Assert.NotEqual(PolicyDataClass.ChildPersonalisation, item.Class));
        Assert.All(usage, item => Assert.Contains(item, PolicyDataClassification.Protected));
    }

    [Fact]
    public void The_counter_is_written_to_the_protected_store()
    {
        var vault = new InMemoryProtectedPolicyStore();
        var fallback = new InMemoryScreenTimeStateStore();

        var store = new ProtectedScreenTimeStateStore(
            fallback, vault, Production, new RecordingLogger());

        Assert.True(store.Save(new ScreenTimeState { LocalDate = "2026-09-28", UsedSeconds = 1234 }));

        Assert.NotNull(vault.Read(ProtectedScreenTimeStateStore.DocumentName));
        Assert.Equal(0, fallback.SaveCount);

        var load = store.Load();
        Assert.Equal(1234, load.State.UsedSeconds);
    }

    [Fact]
    public void A_production_counter_without_a_protected_store_is_unknown_rather_than_zero()
    {
        var fallback = new InMemoryScreenTimeStateStore();
        fallback.Save(new ScreenTimeState { UsedSeconds = 9999 });

        var store = new ProtectedScreenTimeStateStore(
            fallback,
            new InMemoryProtectedPolicyStore(ProtectedStoreStatus.NotProvisioned),
            Production,
            new RecordingLogger());

        var load = store.Load();

        // It does not read the child-writable copy, and it does not answer
        // zero either. Both would be wrong in the same direction.
        Assert.Equal(ScreenTimeLoadOutcome.Unreadable, load.Outcome);
    }

    [Fact]
    public void A_development_counter_falls_back_to_the_ordinary_file()
    {
        var fallback = new InMemoryScreenTimeStateStore();

        var store = new ProtectedScreenTimeStateStore(
            fallback,
            new InMemoryProtectedPolicyStore(ProtectedStoreStatus.DevelopmentOnly),
            Development,
            new RecordingLogger());

        Assert.True(store.Save(new ScreenTimeState { UsedSeconds = 60 }));
        Assert.Equal(1, fallback.SaveCount);
        Assert.Equal(60, store.Load().State.UsedSeconds);
    }

    // -------------------------------------------- the real store, probed

    [Fact]
    public void An_absent_protected_directory_is_not_provisioned_and_is_not_created()
    {
        using var dir = new TempDirectory();
        var missing = Path.Combine(dir.Path, "never-created");

        var store = new FileSystemProtectedPolicyStore(missing, new RecordingLogger());
        var state = store.Probe();

        Assert.Equal(ProtectedStoreStatus.NotProvisioned, state.Status);

        // Creating it here would produce a directory owned by whoever ran
        // KidShell - on a locked-down machine, the child. A store that looks
        // like protection and is not is worse than no store.
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void A_directory_this_process_can_write_is_reported_as_wrongly_permissioned()
    {
        using var dir = new TempDirectory();
        var store = new FileSystemProtectedPolicyStore(dir.Path, new RecordingLogger());

        var state = store.Probe();

        Assert.Equal(ProtectedStoreStatus.PermissionsWrong, state.Status);
        Assert.False(state.IsTrustworthy);

        // The probe cleans up after itself.
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void The_plan_still_describes_a_boundary_that_would_work()
    {
        var plan = ProtectedStorePlan.For(@"C:\ProgramData");

        Assert.True(plan.IsChildWriteProtected);
        Assert.True(plan.IsChildReadable);
        Assert.True(plan.IsAdministratorRecoverable);
        Assert.True(plan.RemoveInheritance);
    }
}
