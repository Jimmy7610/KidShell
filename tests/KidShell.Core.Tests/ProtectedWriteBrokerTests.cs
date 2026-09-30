using KidShell.Core.Configuration;
using KidShell.Core.Runtime;
using KidShell.Core.Security.Storage;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// OPSV RETEST 2, FINDING 01 — the protected store had no working write path,
/// and FINDING 02 — a Ready store with no policy handed authority back to the
/// child.
///
/// The first was an architectural contradiction rather than a bug: the store
/// was considered Ready exactly when the process that had to write to it
/// could not, and if that process COULD write there the gate rejected the
/// store as wrongly permissioned. Both branches were unreachable-by-design.
///
/// The answer is that reading and writing are different responsibilities with
/// different privileges. The child process reads; a privileged broker writes;
/// the request names a DOCUMENT and never a path.
/// </summary>
public class ProtectedWriteBrokerTests
{
    private static readonly IRuntimeEnvironment Production =
        new RuntimeEnvironment(KidShellRuntimeMode.Production, "Release");

    private static readonly IRuntimeEnvironment Development =
        new RuntimeEnvironment(KidShellRuntimeMode.Development, "Debug");

    private static ProtectedConfigurationStore Store(
        TempDirectory dir,
        InMemoryProtectedStateStore vault,
        IRuntimeEnvironment environment,
        IProtectedStateWriter? writer = null)
    {
        var logger = new RecordingLogger();

        return new ProtectedConfigurationStore(
            new JsonConfigurationStore(Path.Combine(dir.Path, "kidshell.config.json"), logger),
            vault,
            writer ?? vault,
            environment,
            logger);
    }

    // ---------------------------------------- finding 01: the write path

    [Fact]
    public void A_ready_store_can_still_be_written_through_the_broker()
    {
        // The contradiction, resolved. Ready means "the child cannot write
        // here", and a save still succeeds because the child does not write.
        using var dir = new TempDirectory();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);

        var configuration = KidShellConfiguration.CreateDefault();
        configuration.ScreenTime.WeekdayMinutes = 45;

        Assert.True(Store(dir, vault, Production).Save(configuration));
        Assert.NotNull(vault.Read(ProtectedDocument.ParentPolicy));
    }

    [Fact]
    public void The_reader_has_no_way_to_write_at_all()
    {
        // Stated as a type check, because it is a design property rather than
        // a behaviour: if IProtectedStateReader ever grows a write method the
        // contradiction is back.
        var members = typeof(IProtectedStateReader).GetMethods().Select(m => m.Name).ToList();

        Assert.DoesNotContain(members, name => name.Contains("Write", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, name => name.Contains("Save", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_writer_has_no_way_to_name_a_destination()
    {
        // The security property of the whole design: the unprivileged caller
        // contributes contents and nothing else. A Write(path, bytes) member
        // would make the elevated helper a general-purpose administrator
        // service and everything else decorative.
        foreach (var method in typeof(IProtectedStateWriter).GetMethods())
        {
            foreach (var parameter in method.GetParameters())
            {
                Assert.DoesNotContain("path", parameter.Name!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("file", parameter.Name!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("directory", parameter.Name!, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Each_document_has_one_fixed_destination()
    {
        var names = Enum.GetValues<ProtectedDocument>()
            .Select(ProtectedDocumentNames.FileNameOf)
            .ToList();

        // Total, distinct, and none of them reachable from a caller's string.
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, name => Assert.DoesNotContain("..", name));
        Assert.All(names, name => Assert.DoesNotContain("/", name));
        Assert.All(names, name => Assert.DoesNotContain("\\", name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    public void A_malformed_payload_is_rejected(string payload) =>
        Assert.NotNull(ProtectedPayloadPolicy.Validate(payload));

    [Fact]
    public void An_oversized_payload_is_rejected()
    {
        var huge = "{\"a\":\"" + new string('x', ProtectedPayloadPolicy.MaxPayloadBytes) + "\"}";

        Assert.NotNull(ProtectedPayloadPolicy.Validate(huge));
    }

    [Fact]
    public void A_broker_failure_propagates_rather_than_being_swallowed()
    {
        using var dir = new TempDirectory();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready) { RefuseWrites = true };

        Assert.False(Store(dir, vault, Production).Save(KidShellConfiguration.CreateDefault()));
    }

    [Fact]
    public void An_unavailable_writer_stops_the_save()
    {
        using var dir = new TempDirectory();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready) { IsAvailable = false };

        Assert.False(Store(dir, vault, Production).Save(KidShellConfiguration.CreateDefault()));
    }

    [Fact]
    public void A_development_build_writes_without_any_elevation()
    {
        using var dir = new TempDirectory();
        var writer = new DirectProtectedStateWriter(Path.Combine(dir.Path, "policy"), new RecordingLogger());

        Assert.True(writer.SaveParentPolicy("""{"schemaVersion":1}""").Success);
        Assert.True(writer.IsAvailable);
    }

    // ------------------------------- finding 02: the fallback that was left

    [Fact]
    public void A_ready_store_with_a_missing_policy_does_not_import_a_hostile_file()
    {
        // The OPSV reproduction, exactly. The store is Ready, the machine has
        // been provisioned, the protected policy is gone, and a hostile file
        // sits in the child's own profile carrying a PIN and a rule set.
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "kidshell.config.json");

        File.WriteAllText(path, """
        {
          "schemaVersion": 2,
          "parentPin": { "hash": "hostile", "salt": "hostile", "iterations": 210000 },
          "screenTime": { "isEnabled": false, "weekdayMinutes": 1440 },
          "apps": [ { "id": "cmd", "displayName": "Allt", "isEnabled": true,
                      "executablePath": "C:\\Windows\\System32\\cmd.exe" } ]
        }
        """);

        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);
        vault.Seed(ProtectedDocument.ProvisioningMarker,
            ProtectedPolicyTrustEvaluator.MarkerDocument(DateTimeOffset.UtcNow));

        var logger = new RecordingLogger();
        var store = new ProtectedConfigurationStore(
            new JsonConfigurationStore(path, logger), vault, vault, Production, logger);

        var result = store.Load();

        Assert.Equal(ConfigurationLoadStatus.Failed, result.Status);
        Assert.NotEqual("hostile", result.Configuration.ParentPin.Hash);
        Assert.DoesNotContain(result.Configuration.Apps, a => a.Id == "cmd");
    }

    [Theory]
    [InlineData(ProtectedPolicyTrust.MissingButExpected)]
    [InlineData(ProtectedPolicyTrust.ReadFailed)]
    [InlineData(ProtectedPolicyTrust.Corrupt)]
    [InlineData(ProtectedPolicyTrust.AccessDenied)]
    [InlineData(ProtectedPolicyTrust.VersionUnsupported)]
    public void Only_a_first_run_or_an_existing_policy_may_proceed(ProtectedPolicyTrust trust) =>
        Assert.False(new ProtectedPolicyLoad(trust).MayProceed);

    [Theory]
    [InlineData(ProtectedPolicyTrust.GenuineFirstRun)]
    [InlineData(ProtectedPolicyTrust.ExistingProtectedPolicy)]
    public void The_two_workable_states_proceed(ProtectedPolicyTrust trust) =>
        Assert.True(new ProtectedPolicyLoad(trust).MayProceed);

    [Fact]
    public void A_genuine_first_run_is_told_apart_from_a_missing_policy()
    {
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);

        // No marker, no policy: a device nobody has set up.
        Assert.Equal(ProtectedPolicyTrust.GenuineFirstRun,
            ProtectedPolicyTrustEvaluator.Evaluate(vault).Trust);

        // Marker, no policy: the policy is gone.
        vault.Seed(ProtectedDocument.ProvisioningMarker,
            ProtectedPolicyTrustEvaluator.MarkerDocument(DateTimeOffset.UtcNow));

        Assert.Equal(ProtectedPolicyTrust.MissingButExpected,
            ProtectedPolicyTrustEvaluator.Evaluate(vault).Trust);
    }

    [Fact]
    public void A_damaged_marker_counts_as_provisioned()
    {
        // Otherwise damaging one small file would be a route back to first-run
        // initialisation, which is the shape of the bug being closed.
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);
        vault.Corrupt(ProtectedDocument.ProvisioningMarker);

        Assert.Equal(ProtectedPolicyTrust.MissingButExpected,
            ProtectedPolicyTrustEvaluator.Evaluate(vault).Trust);
    }

    [Fact]
    public void A_first_save_records_that_the_machine_is_provisioned()
    {
        using var dir = new TempDirectory();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);

        Assert.True(Store(dir, vault, Production).Save(KidShellConfiguration.CreateDefault()));
        Assert.NotNull(vault.Read(ProtectedDocument.ProvisioningMarker));

        // And the policy going missing afterwards is now a missing policy.
        vault.Delete(ProtectedDocument.ParentPolicy);

        Assert.Equal(ConfigurationLoadStatus.Failed, Store(dir, vault, Production).Load().Status);
    }

    [Fact]
    public void A_corrupt_protected_policy_fails_closed()
    {
        using var dir = new TempDirectory();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);

        Store(dir, vault, Production).Save(KidShellConfiguration.CreateDefault());
        vault.Corrupt(ProtectedDocument.ParentPolicy);

        Assert.Equal(ConfigurationLoadStatus.Failed, Store(dir, vault, Production).Load().Status);
    }

    [Fact]
    public void A_future_schema_fails_closed()
    {
        using var dir = new TempDirectory();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);
        vault.Seed(ProtectedDocument.ParentPolicy, """{"schemaVersion":99}""");

        Assert.Equal(ConfigurationLoadStatus.Failed, Store(dir, vault, Production).Load().Status);
    }

    [Fact]
    public void A_true_first_run_uses_kidshells_own_defaults_not_the_child_file()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "kidshell.config.json");

        File.WriteAllText(path, """
        { "schemaVersion": 2,
          "parentPin": { "hash": "hostile", "salt": "hostile", "iterations": 210000 } }
        """);

        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);
        var logger = new RecordingLogger();

        var result = new ProtectedConfigurationStore(
            new JsonConfigurationStore(path, logger), vault, vault, Production, logger).Load();

        // A first run is allowed to proceed, and still must not adopt a PIN
        // out of a file the child can write.
        Assert.NotEqual(ConfigurationLoadStatus.Failed, result.Status);
        Assert.NotEqual("hostile", result.Configuration.ParentPin.Hash);
    }

    // --------------------------------------------- transaction semantics

    [Fact]
    public void A_failed_protected_write_leaves_the_user_file_untouched()
    {
        // OPSV found the reverse order: the user file was written first, the
        // protected write then failed, Save returned false, and the
        // personalisation had already changed.
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "kidshell.config.json");
        var logger = new RecordingLogger();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.Ready);

        var store = new ProtectedConfigurationStore(
            new JsonConfigurationStore(path, logger), vault, vault, Production, logger);

        var first = KidShellConfiguration.CreateDefault();
        first.Child.Name = "Lucas";
        Assert.True(store.Save(first));

        var before = File.ReadAllText(path);

        vault.RefuseWrites = true;

        var second = KidShellConfiguration.CreateDefault();
        second.Child.Name = "Someone else";
        second.ScreenTime.WeekdayMinutes = 999;

        Assert.False(store.Save(second));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void A_development_build_still_works_with_no_protected_store()
    {
        using var dir = new TempDirectory();
        var vault = new InMemoryProtectedStateStore(ProtectedStoreStatus.DevelopmentOnly);
        var store = Store(dir, vault, Development);

        Assert.NotEqual(ConfigurationLoadStatus.Failed, store.Load().Status);
        Assert.True(store.Save(KidShellConfiguration.CreateDefault()));
    }
}
