using KidShell.Core.Deployment;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// A filesystem that is a dictionary.
///
/// Every install test runs against this, because the thing worth testing is
/// what happens when an install fails HALFWAY, and no test in this repository
/// writes to Program Files to find out. <see cref="FailOnCopyNumber"/> is how
/// that halfway point is chosen.
/// </summary>
internal sealed class FakeInstallFileSystem : IInstallFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public int Copies { get; private set; }

    /// <summary>Which copy throws. Zero means none do.</summary>
    public int FailOnCopyNumber { get; set; }

    /// <summary>Paths whose deletion fails, so a rollback can be made to fail.</summary>
    public HashSet<string> UndeletablePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Seed(string path, string content)
    {
        _files[path] = System.Text.Encoding.UTF8.GetBytes(content);
        _directories.Add(DirectoryOf(path));
    }

    public IReadOnlyDictionary<string, byte[]> All => _files;

    public bool FileExists(string path) => _files.ContainsKey(path);

    public bool DirectoryExists(string path) =>
        _directories.Contains(path) ||
        _files.Keys.Any(f => f.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    public void CreateDirectory(string path) => _directories.Add(path);

    public string Sha256(string path) => InstallTransaction.Digest(_files[path]);

    public long Length(string path) => _files[path].Length;

    public void Copy(string source, string destination, bool overwrite)
    {
        Copies++;

        if (FailOnCopyNumber > 0 && Copies == FailOnCopyNumber)
        {
            throw new IOException($"(fake) copy {Copies} failed");
        }

        _files[destination] = _files[source];
        _directories.Add(DirectoryOf(destination));
    }

    public void DeleteFile(string path)
    {
        if (UndeletablePaths.Contains(path))
        {
            throw new UnauthorizedAccessException($"(fake) '{path}' cannot be deleted");
        }

        _files.Remove(path);
    }

    public IReadOnlyList<string> EnumerateFiles(string directory) =>
    [
        .. _files.Keys.Where(f => f.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
    ];

    public void DeleteDirectoryIfEmpty(string path)
    {
        if (!EnumerateFiles(path).Any())
        {
            _directories.Remove(path);
        }
    }

    /// <summary>Replaces a file's contents without going through Copy, to simulate tampering.</summary>
    public void Tamper(string path, string content) =>
        _files[path] = System.Text.Encoding.UTF8.GetBytes(content);

    private static string DirectoryOf(string path)
    {
        var index = path.LastIndexOf('\\');
        return index > 0 ? path[..index] : path;
    }
}

/// <summary>
/// THE LAYOUT, AND THE DEFECT IT EXISTS TO END.
///
/// build-release.ps1 built with -p:Platform=x64 and copied from
/// src\&lt;component&gt;\bin\Release\net10.0-windows. MSBuild had written to
/// bin\x64\Release. On a developer's machine the unplatformed folder existed
/// from an older build, so the copy succeeded and shipped binaries a week older
/// than the commit being released - including a SecurityHost from before the
/// broker was rewritten. On a clean checkout the folder was absent, the copy
/// was skipped, and the bundle had no helper in it at all.
///
/// Neither case produced an error, which is why the path now has exactly one
/// definition and it is tested.
/// </summary>
public class InstallationLayoutTests
{
    [Theory]
    [InlineData(KidShellComponent.SecurityHost, "x64", @"src\KidShell.SecurityHost\bin\x64\Release\net10.0-windows")]
    [InlineData(KidShellComponent.Watchdog, "x64", @"src\KidShell.Watchdog\bin\x64\Release\net10.0-windows")]
    [InlineData(KidShellComponent.Recovery, "ARM64", @"src\KidShell.Recovery\bin\ARM64\Release\net10.0-windows")]
    [InlineData(KidShellComponent.DeviceValidation, "x64", @"src\KidShell.DeviceValidation\bin\x64\Release\net10.0-windows")]
    public void The_build_output_path_includes_the_platform(
        KidShellComponent component, string platform, string expected) =>
        Assert.Equal(expected, InstallationLayout.BuildOutputOf(component, "Release", platform));

    [Fact]
    public void Anycpu_is_the_one_case_with_no_platform_folder()
    {
        // MSBuild's own spelling for "no platform subfolder". Worth a test
        // because getting it wrong in the other direction would make the
        // bundler look in a folder that never exists.
        Assert.Equal(
            @"src\KidShell.Recovery\bin\Release\net10.0-windows",
            InstallationLayout.BuildOutputOf(KidShellComponent.Recovery, "Release", "AnyCPU"));
    }

    [Fact]
    public void The_app_has_its_own_target_framework()
    {
        // The packaged app targets a Windows SDK version the others do not, so
        // one shared constant would have sent the bundler to a folder that
        // does not exist for four components or for one.
        Assert.Contains("net10.0-windows10.0.26100.0",
            InstallationLayout.BuildOutputOf(KidShellComponent.App, "Release", "x64"));

        Assert.EndsWith("net10.0-windows",
            InstallationLayout.BuildOutputOf(KidShellComponent.SecurityHost, "Release", "x64"));
    }

    [Fact]
    public void Every_component_has_a_folder_a_project_and_a_build_output()
    {
        foreach (var component in Enum.GetValues<KidShellComponent>())
        {
            Assert.False(string.IsNullOrWhiteSpace(InstallationLayout.FolderOf(component)));
            Assert.False(string.IsNullOrWhiteSpace(InstallationLayout.ProjectOf(component)));
            Assert.False(string.IsNullOrWhiteSpace(
                InstallationLayout.BuildOutputOf(component, "Release", "x64")));
        }
    }

    [Fact]
    public void The_app_has_no_executable_because_asking_is_a_category_error()
    {
        // It is an MSIX: registered, not copied. Returning something plausible
        // would let a caller build a path that can never exist.
        Assert.Null(InstallationLayout.ExecutableOf(KidShellComponent.App));

        foreach (var component in InstallationLayout.FileCopied)
        {
            Assert.NotNull(InstallationLayout.ExecutableOf(component));
        }
    }

    [Fact]
    public void The_app_is_not_file_copied()
    {
        Assert.DoesNotContain(KidShellComponent.App, InstallationLayout.FileCopied);
        Assert.Contains(KidShellComponent.App, InstallationLayout.Required);
        Assert.Equal(Enum.GetValues<KidShellComponent>().Length - 1, InstallationLayout.FileCopied.Count);
    }

    [Fact]
    public void The_paths_agree_with_what_the_validation_scripts_look_for()
    {
        // The other half of the same defect: a validation probe checking a
        // location the installer was never asked to populate.
        Assert.Equal(@"C:\Program Files\KidShell", InstallationLayout.InstallRoot(@"C:\Program Files"));

        Assert.Equal(@"C:\Program Files\KidShell\KidShell.SecurityHost",
            InstallationLayout.ComponentDirectory(@"C:\Program Files", KidShellComponent.SecurityHost));

        Assert.Equal(@"C:\ProgramData\KidShell\policy",
            InstallationLayout.PolicyDirectory(@"C:\ProgramData"));

        Assert.Equal(@"C:\ProgramData\KidShell\security\recovery",
            InstallationLayout.RecoveryDirectory(@"C:\ProgramData"));

        Assert.Equal(@"C:\ProgramData\KidShell\installation\install-receipt.json",
            InstallationLayout.ReceiptFile(@"C:\ProgramData"));
    }

    [Fact]
    public void A_trailing_separator_does_not_double_up()
    {
        Assert.Equal(@"C:\Program Files\KidShell", InstallationLayout.InstallRoot(@"C:\Program Files\"));
    }

    [Fact]
    public void The_recovery_record_and_the_policy_are_preserved_on_uninstall()
    {
        // A recovery manifest is the record of how to undo a security change.
        // An uninstall that removed it would take away the one thing a parent
        // needs if the uninstall itself goes wrong.
        var preserved = InstallationLayout.PreservedOnUninstall(@"C:\ProgramData");

        Assert.Contains(@"C:\ProgramData\KidShell\security\recovery", preserved);
        Assert.Contains(@"C:\ProgramData\KidShell\policy", preserved);
    }

    // ------------------------------------------------------- path safety

    [Theory]
    [InlineData(@"KidShell.SecurityHost.exe")]
    [InlineData(@"runtimes\win-x64\native\thing.dll")]
    [InlineData(@"a\b\c\d.json")]
    public void An_ordinary_relative_path_is_safe(string path) =>
        Assert.True(InstallationLayout.IsSafeRelativePath(path), path);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"..\outside.exe")]
    [InlineData(@"a\..\..\outside.exe")]
    [InlineData(@"C:\Windows\System32\evil.dll")]
    [InlineData(@"\\server\share\evil.dll")]
    [InlineData(@"\Windows\System32\evil.dll")]
    [InlineData(@"/etc/passwd")]
    [InlineData(@"a\\b.dll")]
    [InlineData(@"a\.\b.dll")]
    [InlineData(@"file.exe:stream")]
    [InlineData(@"trailing.")]
    [InlineData(@"trailing ")]
    public void Anything_that_could_escape_the_root_is_refused(string? path) =>
        Assert.False(InstallationLayout.IsSafeRelativePath(path), path ?? "(null)");

    [Fact]
    public void A_single_traversal_check_would_not_have_been_enough()
    {
        // Each of these is a different way out, and a blacklist of "..\" would
        // have caught exactly one.
        foreach (var path in new[]
                 {
                     @"..\x.dll", @"C:\x.dll", @"\\host\share\x.dll",
                     @"\x.dll", @"x.dll:alt", @"x.dll."
                 })
        {
            Assert.False(InstallationLayout.IsSafeRelativePath(path), path);
        }
    }

    [Theory]
    [InlineData(@"C:\Program Files\KidShell", @"C:\Program Files\KidShell\a\b.dll", true)]
    [InlineData(@"C:\Program Files\KidShell", @"C:\Program Files\KidShellEvil\b.dll", false)]
    [InlineData(@"C:\Program Files\KidShell", @"C:\Windows\b.dll", false)]
    [InlineData(@"C:\Program Files\KidShell\", @"C:\Program Files\KidShell\b.dll", true)]
    public void A_destination_must_resolve_inside_the_root(string root, string candidate, bool expected) =>
        Assert.Equal(expected, InstallationLayout.IsInsideRoot(root, candidate));

    [Fact]
    public void A_sibling_directory_with_the_same_prefix_is_outside()
    {
        // The classic off-by-one in a prefix check: "KidShell" and
        // "KidShellEvil" share a prefix and are different directories.
        Assert.False(InstallationLayout.IsInsideRoot(
            @"C:\Program Files\KidShell", @"C:\Program Files\KidShell.evil\x.dll"));
    }
}

/// <summary>The manifest, treated as the untrusted document it is.</summary>
public class ReleaseManifestTests
{
    internal static ReleaseManifest Valid(Action<Dictionary<string, object>>? _ = null) => new()
    {
        Version = "1.0.0",
        PackageVersion = "1.0.0.0",
        Prerelease = "rc.1",
        GitSha = new string('a', 40),
        Architecture = "x64",
        BuildConfiguration = "Release",
        BuildUtc = "2026-10-02 10:00:00Z",
        Signed = false,
        Channel = ReleaseChannel.DedicatedLabUnsigned,
        PackageIdentity = "KidShell.Barnlage.Lab",
        Components = [.. Enum.GetValues<KidShellComponent>().Select(c => new ReleaseComponent
        {
            Component = c,
            BundlePath = $@"components\{InstallationLayout.FolderOf(c)}",
            Files = [new ReleaseFile("thing.dll", new string('b', 64), 10)]
        })],
        ExpectedInstallPaths = Enum.GetValues<KidShellComponent>()
            .ToDictionary(c => c.ToString(), c => $@"C:\Program Files\KidShell\{InstallationLayout.FolderOf(c)}")
    };

    [Fact]
    public void A_complete_manifest_is_well_formed()
    {
        var manifest = Valid();

        Assert.True(manifest.IsWellFormed, string.Join("; ", manifest.Problems()));
        Assert.Empty(manifest.MissingComponents());
        Assert.Equal("1.0.0-rc.1", manifest.DisplayVersion);
    }

    [Fact]
    public void It_round_trips_through_json()
    {
        var parsed = ReleaseManifest.Parse(Valid().ToJson());

        Assert.NotNull(parsed);
        Assert.Equal("1.0.0", parsed.Version);
        Assert.Equal(ReleaseChannel.DedicatedLabUnsigned, parsed.Channel);
        Assert.Equal(Enum.GetValues<KidShellComponent>().Length, parsed.Components.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void Anything_unreadable_parses_to_nothing(string? json) =>
        Assert.Null(ReleaseManifest.Parse(json));

    [Fact]
    public void A_manifest_with_no_commit_is_refused()
    {
        // "Which code is on that device" has to have an answer.
        var problems = (Valid() with { GitSha = "" }).Problems();

        Assert.Contains(problems, p => p.Contains("Git SHA", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("not-a-sha-not-a-sha-not-a-sha-not-a-sha-")]
    public void A_commit_that_is_not_a_commit_is_refused(string sha) =>
        Assert.Contains((Valid() with { GitSha = sha }).Problems(),
            p => p.Contains("commit hash", StringComparison.Ordinal));

    [Fact]
    public void A_future_schema_is_refused()
    {
        Assert.Contains(
            (Valid() with { SchemaVersion = ReleaseManifest.CurrentSchemaVersion + 1 }).Problems(),
            p => p.Contains("schema", StringComparison.Ordinal));
    }

    [Fact]
    public void A_manifest_for_another_product_is_refused()
    {
        Assert.Contains((Valid() with { Product = "SomethingElse" }).Problems(),
            p => p.Contains("not KidShell", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unsafe_file_path_in_the_manifest_is_refused()
    {
        var manifest = Valid() with
        {
            Components =
            [
                new ReleaseComponent
                {
                    Component = KidShellComponent.SecurityHost,
                    BundlePath = @"components\KidShell.SecurityHost",
                    Files = [new ReleaseFile(@"..\..\Windows\System32\evil.dll", new string('b', 64), 10)]
                }
            ]
        };

        Assert.Contains(manifest.Problems(), p => p.Contains("unsafe file path", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unsafe_bundle_path_is_refused()
    {
        var manifest = Valid() with
        {
            Components =
            [
                new ReleaseComponent
                {
                    Component = KidShellComponent.SecurityHost,
                    BundlePath = @"..\..\elsewhere",
                    Files = [new ReleaseFile("thing.dll", new string('b', 64), 10)]
                }
            ]
        };

        Assert.Contains(manifest.Problems(), p => p.Contains("unsafe bundle path", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_with_no_digest_is_refused()
    {
        var manifest = Valid() with
        {
            Components =
            [
                new ReleaseComponent
                {
                    Component = KidShellComponent.SecurityHost,
                    BundlePath = @"components\KidShell.SecurityHost",
                    Files = [new ReleaseFile("thing.dll", "", 10)]
                }
            ]
        };

        Assert.Contains(manifest.Problems(), p => p.Contains("SHA-256", StringComparison.Ordinal));
    }

    [Fact]
    public void A_component_listed_twice_is_refused()
    {
        var one = new ReleaseComponent
        {
            Component = KidShellComponent.Watchdog,
            BundlePath = @"components\KidShell.Watchdog",
            Files = [new ReleaseFile("thing.dll", new string('b', 64), 10)]
        };

        Assert.Contains((Valid() with { Components = [one, one] }).Problems(),
            p => p.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void A_bundle_expecting_a_path_the_layout_does_not_use_is_refused()
    {
        // The disagreement that would otherwise produce a validation script
        // checking a folder the installer never populated.
        var manifest = Valid() with
        {
            ExpectedInstallPaths = new Dictionary<string, string>
            {
                ["SecurityHost"] = @"C:\Program Files\KidShell\helper"
            }
        };

        Assert.Contains(manifest.Problems(), p => p.Contains("the layout says", StringComparison.Ordinal));
    }

    [Fact]
    public void A_bundle_missing_a_component_is_named()
    {
        // The real defect: a bundle with no SecurityHost in it, because the
        // bundler copied from a folder that did not exist and did not mind.
        var manifest = Valid() with
        {
            Components = [.. Valid().Components.Where(c => c.Component != KidShellComponent.SecurityHost)]
        };

        Assert.Equal([KidShellComponent.SecurityHost], manifest.MissingComponents());
    }
}

/// <summary>Which bundles may replace what is already installed.</summary>
public class InstallPolicyTests
{
    private static ReleaseManifest Bundle() => ReleaseManifestTests.Valid();

    private static InstallReceipt Installed(string version = "1.0.0", string? sha = null) => new()
    {
        Version = version,
        Prerelease = "rc.1",
        GitSha = sha ?? new string('a', 40),
        Architecture = "x64",
        InstalledUtc = "2026-10-01 10:00:00Z",
        Channel = ReleaseChannel.DedicatedLabUnsigned
    };

    [Fact]
    public void A_fresh_lab_install_is_allowed()
    {
        var decision = InstallPolicy.Decide(Bundle(), null, "x64", production: false);

        Assert.True(decision.Allowed, string.Join("; ", decision.Explanations));
        Assert.Equal(InstallAction.FreshInstall, decision.Action);
    }

    [Fact]
    public void An_unsigned_lab_install_says_so_loudly()
    {
        var decision = InstallPolicy.Decide(Bundle(), null, "x64", production: false);

        Assert.Contains(decision.Explanations,
            e => e.Contains("UNSIGNED", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unsigned_production_install_is_impossible()
    {
        // Not discouraged. Refused.
        var decision = InstallPolicy.Decide(
            Bundle() with { Channel = ReleaseChannel.Production, Signed = false },
            null, "x64", production: true);

        Assert.False(decision.Allowed);
        Assert.Contains(InstallRefusal.UnsignedInProduction, decision.Refusals);
    }

    [Fact]
    public void A_signed_production_bundle_installs_in_production()
    {
        var decision = InstallPolicy.Decide(
            Bundle() with { Channel = ReleaseChannel.Production, Signed = true },
            null, "x64", production: true);

        Assert.True(decision.Allowed, string.Join("; ", decision.Explanations));
    }

    [Fact]
    public void A_production_bundle_is_not_installed_by_the_lab_path()
    {
        // Not a safety problem and still refused: it would be installed under
        // lab rules and recorded as a lab install.
        var decision = InstallPolicy.Decide(
            Bundle() with { Channel = ReleaseChannel.Production, Signed = true },
            null, "x64", production: false);

        Assert.False(decision.Allowed);
        Assert.Contains(InstallRefusal.ChannelMismatch, decision.Refusals);
    }

    [Fact]
    public void A_dirty_build_is_refused_everywhere()
    {
        foreach (var production in new[] { true, false })
        {
            var decision = InstallPolicy.Decide(
                Bundle() with { Dirty = true, Signed = production, Channel = production ? ReleaseChannel.Production : ReleaseChannel.DedicatedLabUnsigned },
                null, "x64", production);

            Assert.False(decision.Allowed);
            Assert.Contains(InstallRefusal.DirtyBuild, decision.Refusals);
        }
    }

    [Fact]
    public void A_bundle_for_another_architecture_is_refused()
    {
        var decision = InstallPolicy.Decide(Bundle(), null, "ARM64", production: false);

        Assert.False(decision.Allowed);
        Assert.Contains(InstallRefusal.ArchitectureMismatch, decision.Refusals);
    }

    [Fact]
    public void A_bundle_missing_a_component_is_refused()
    {
        var manifest = Bundle() with
        {
            Components = [.. Bundle().Components.Where(c => c.Component != KidShellComponent.SecurityHost)]
        };

        var decision = InstallPolicy.Decide(manifest, null, "x64", production: false);

        Assert.False(decision.Allowed);
        Assert.Contains(InstallRefusal.ComponentsMissing, decision.Refusals);
    }

    [Fact]
    public void No_manifest_at_all_is_refused()
    {
        var decision = InstallPolicy.Decide(null, null, "x64", production: false);

        Assert.False(decision.Allowed);
        Assert.Contains(InstallRefusal.ManifestMissing, decision.Refusals);
    }

    // ------------------------------------------------------ upgrade rules

    [Fact]
    public void An_older_installation_is_upgraded()
    {
        var decision = InstallPolicy.Decide(Bundle(), Installed("0.9.0"), "x64", production: false);

        Assert.True(decision.Allowed);
        Assert.Equal(InstallAction.Upgrade, decision.Action);
    }

    [Fact]
    public void A_newer_installation_is_not_downgraded()
    {
        var decision = InstallPolicy.Decide(Bundle(), Installed("2.0.0"), "x64", production: false);

        Assert.False(decision.Allowed);
        Assert.Contains(InstallRefusal.DowngradeRefused, decision.Refusals);
    }

    [Fact]
    public void The_same_version_from_the_same_commit_needs_an_explicit_repair()
    {
        var same = Installed("1.0.0", new string('a', 40));

        Assert.False(InstallPolicy.Decide(Bundle(), same, "x64", false).Allowed);
        Assert.Contains(InstallRefusal.SameVersionInstalled,
            InstallPolicy.Decide(Bundle(), same, "x64", false).Refusals);

        var repair = InstallPolicy.Decide(Bundle(), same, "x64", false, repairRequested: true);

        Assert.True(repair.Allowed);
        Assert.Equal(InstallAction.Repair, repair.Action);
    }

    [Fact]
    public void The_same_version_from_a_different_commit_is_its_own_refusal()
    {
        // The sharpest case, and routine on a release-candidate branch:
        // semantically "the same build", actually different code. Installing
        // it silently would produce a validation report about code nobody
        // shipped.
        var other = Installed("1.0.0", new string('c', 40));

        var decision = InstallPolicy.Decide(Bundle(), other, "x64", production: false);

        Assert.False(decision.Allowed);
        Assert.Contains(InstallRefusal.SameVersionDifferentCommit, decision.Refusals);
        Assert.Contains(decision.Explanations, e => e.Contains("ccccccccccc", StringComparison.Ordinal));
    }

    [Fact]
    public void A_different_commit_at_the_same_version_can_be_a_deliberate_repair()
    {
        var other = Installed("1.0.0", new string('c', 40));

        var decision = InstallPolicy.Decide(Bundle(), other, "x64", false, repairRequested: true);

        Assert.True(decision.Allowed);
        Assert.Equal(InstallAction.Repair, decision.Action);
    }

    [Theory]
    [InlineData("1.0.9", "1.0.10", -1)]
    [InlineData("1.0.10", "1.0.9", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("2.0.0", "1.9.9", 1)]
    public void Versions_compare_numerically_and_not_as_text(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(InstallPolicy.CompareVersions(left, right)));

    [Fact]
    public void The_tenth_patch_is_an_upgrade_and_not_a_downgrade()
    {
        // As strings "1.0.10" sorts before "1.0.9", which would have turned
        // one upgrade into a refused downgrade exactly once, long after
        // anybody was still watching for it.
        var decision = InstallPolicy.Decide(
            ReleaseManifestTests.Valid() with { Version = "1.0.10" },
            Installed("1.0.9"), "x64", production: false);

        Assert.Equal(InstallAction.Upgrade, decision.Action);
    }

    // ---------------------------------------------------------- receipt

    [Fact]
    public void A_receipt_records_what_was_written_and_what_was_there_before()
    {
        var previous = Installed("0.9.0", new string('d', 40));

        var receipt = InstallReceipt.For(
            Bundle(), @"C:\Program Files\KidShell",
            new Dictionary<string, string> { [@"KidShell.Watchdog\thing.dll"] = new string('b', 64) },
            DateTimeOffset.UnixEpoch, previous);

        Assert.Equal("1.0.0", receipt.Version);
        Assert.Equal("0.9.0-rc.1", receipt.PreviousVersion);
        Assert.Equal(new string('d', 40), receipt.PreviousGitSha);
        Assert.Single(receipt.Files);
        Assert.False(receipt.Signed);
        Assert.Equal(ReleaseChannel.DedicatedLabUnsigned, receipt.Channel);
    }

    [Fact]
    public void A_receipt_round_trips()
    {
        var receipt = InstallReceipt.For(
            Bundle(), @"C:\Program Files\KidShell",
            new Dictionary<string, string> { ["a.dll"] = new string('b', 64) },
            DateTimeOffset.UnixEpoch);

        var parsed = InstallReceipt.Parse(receipt.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(receipt.GitSha, parsed.GitSha);
        Assert.Equal(receipt.Files.Count, parsed.Files.Count);
    }

    [Fact]
    public void A_receipt_from_a_future_schema_is_not_read()
    {
        var json = (new InstallReceipt { SchemaVersion = 99, Version = "9.9.9" }).ToJson();

        Assert.Null(InstallReceipt.Parse(json));
    }

    [Fact]
    public void A_receipt_holds_no_secret()
    {
        var fields = typeof(InstallReceipt).GetProperties().Select(p => p.Name.ToLowerInvariant());

        foreach (var forbidden in new[] { "password", "pin", "token", "secret", "privatekey" })
        {
            Assert.DoesNotContain(fields, f => f.Contains(forbidden, StringComparison.Ordinal));
        }
    }
}

/// <summary>Laying files down, and undoing it when something goes wrong halfway.</summary>
public class InstallTransactionTests
{
    private const string BundleRoot = @"D:\bundle";
    private const string InstallRoot = @"C:\Program Files\KidShell";
    private const string StagingRoot = @"C:\Program Files\KidShell.staging";

    private static (ReleaseManifest Manifest, FakeInstallFileSystem Files) Prepare(
        int filesPerComponent = 2)
    {
        var files = new FakeInstallFileSystem();
        var components = new List<ReleaseComponent>();

        foreach (var component in InstallationLayout.FileCopied)
        {
            var folder = InstallationLayout.FolderOf(component);
            var bundlePath = $@"components\{folder}";
            var entries = new List<ReleaseFile>();

            for (var i = 0; i < filesPerComponent; i++)
            {
                var name = $"file{i}.dll";
                var content = $"{folder}/{name}";
                var source = $@"{BundleRoot}\{bundlePath}\{name}";

                files.Seed(source, content);

                entries.Add(new ReleaseFile(
                    name, InstallTransaction.Digest(content), content.Length));
            }

            components.Add(new ReleaseComponent
            {
                Component = component,
                BundlePath = bundlePath,
                Files = entries
            });
        }

        // The App is in the manifest and is skipped by the transaction, which
        // is the behaviour under test rather than an omission.
        components.Add(new ReleaseComponent
        {
            Component = KidShellComponent.App,
            BundlePath = @"package",
            Files = [new ReleaseFile("KidShell.msix", InstallTransaction.Digest("msix"), 4)]
        });

        var manifest = ReleaseManifestTests.Valid() with { Components = components };

        return (manifest, files);
    }

    [Fact]
    public void A_clean_install_lands_every_file_and_verifies_it()
    {
        var (manifest, files) = Prepare();

        var outcome = new InstallTransaction(files)
            .Execute(manifest, BundleRoot, InstallRoot, StagingRoot);

        Assert.True(outcome.Committed, string.Join("; ", outcome.Log));
        Assert.False(outcome.RolledBack);
        Assert.False(outcome.NeedsManualRecovery);

        // Four file-copied components, two files each.
        Assert.Equal(InstallationLayout.FileCopied.Count * 2, outcome.WrittenFiles.Count);

        Assert.True(files.FileExists($@"{InstallRoot}\KidShell.SecurityHost\file0.dll"));
        Assert.True(files.FileExists($@"{InstallRoot}\KidShell.DeviceValidation\file1.dll"));
    }

    [Fact]
    public void The_packaged_app_is_not_file_copied()
    {
        var (manifest, files) = Prepare();

        var outcome = new InstallTransaction(files)
            .Execute(manifest, BundleRoot, InstallRoot, StagingRoot);

        Assert.True(outcome.Committed);
        Assert.False(files.FileExists($@"{InstallRoot}\KidShell.App\KidShell.msix"));
        Assert.Contains(outcome.Log, l => l.Contains("package, not files", StringComparison.Ordinal));
    }

    [Fact]
    public void A_manifest_naming_a_path_outside_the_root_installs_nothing()
    {
        var files = new FakeInstallFileSystem();
        files.Seed($@"{BundleRoot}\components\x\evil.dll", "evil");

        var manifest = ReleaseManifestTests.Valid() with
        {
            Components =
            [
                new ReleaseComponent
                {
                    Component = KidShellComponent.Watchdog,
                    BundlePath = @"components\x",
                    Files = [new ReleaseFile(@"..\..\..\Windows\System32\evil.dll", InstallTransaction.Digest("evil"), 4)]
                }
            ]
        };

        var outcome = new InstallTransaction(files)
            .Execute(manifest, BundleRoot, InstallRoot, StagingRoot);

        Assert.False(outcome.Committed);
        Assert.Contains(outcome.Log, l => l.Contains("unsafe path", StringComparison.Ordinal));

        // Refused during preflight, so nothing was copied at all.
        Assert.Equal(0, files.Copies);
    }

    [Fact]
    public void A_file_missing_from_the_bundle_stops_before_anything_is_copied()
    {
        var (manifest, files) = Prepare();

        files.DeleteFile($@"{BundleRoot}\components\KidShell.Recovery\file0.dll");

        var outcome = new InstallTransaction(files)
            .Execute(manifest, BundleRoot, InstallRoot, StagingRoot);

        Assert.False(outcome.Committed);
        Assert.Equal(0, files.Copies);
        Assert.Contains(outcome.Log, l => l.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hash_mismatch_in_the_bundle_stops_before_installation()
    {
        var (manifest, files) = Prepare();

        // Substituted after the manifest was written, which is the whole point
        // of carrying digests.
        files.Tamper($@"{BundleRoot}\components\KidShell.SecurityHost\file0.dll", "something else entirely");

        var outcome = new InstallTransaction(files)
            .Execute(manifest, BundleRoot, InstallRoot, StagingRoot);

        Assert.False(outcome.Committed);
        Assert.Contains(outcome.Log, l => l.Contains("does not match the manifest", StringComparison.Ordinal));

        // Staged, caught, and never installed.
        Assert.False(files.FileExists($@"{InstallRoot}\KidShell.SecurityHost\file0.dll"));
    }

    [Fact]
    public void A_failure_halfway_through_installation_rolls_back()
    {
        var (manifest, files) = Prepare();

        // Eight staging copies, then the install copies. Failing on the
        // eleventh puts it in the middle of laying files down.
        files.FailOnCopyNumber = 11;

        var outcome = new InstallTransaction(files)
            .Execute(manifest, BundleRoot, InstallRoot, StagingRoot);

        Assert.False(outcome.Committed);
        Assert.True(outcome.RolledBack, string.Join("; ", outcome.Log));
        Assert.False(outcome.NeedsManualRecovery);

        // Nothing is left under the install root.
        Assert.Empty(files.EnumerateFiles(InstallRoot));
    }

    [Fact]
    public void A_rollback_that_cannot_finish_says_so_rather_than_claiming_success()
    {
        var (manifest, files) = Prepare();

        files.FailOnCopyNumber = 11;
        files.UndeletablePaths.Add($@"{InstallRoot}\KidShell.SecurityHost\file0.dll");

        var outcome = new InstallTransaction(files)
            .Execute(manifest, BundleRoot, InstallRoot, StagingRoot);

        Assert.False(outcome.Committed);
        Assert.False(outcome.RolledBack);
        Assert.True(outcome.NeedsManualRecovery);
        Assert.Contains(outcome.Log, l => l.Contains("ROLLBACK INCOMPLETE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rollback_removes_only_what_this_attempt_wrote()
    {
        var (manifest, files) = Prepare();

        // A previous installation that this attempt is replacing. A rollback
        // that cleared the install root would turn a failed upgrade into a
        // machine with nothing on it.
        files.Seed($@"{InstallRoot}\KidShell.Recovery\previous.dll", "from the last install");

        files.FailOnCopyNumber = 11;

        var outcome = new InstallTransaction(files)
            .Execute(manifest, BundleRoot, InstallRoot, StagingRoot);

        Assert.False(outcome.Committed);
        Assert.True(outcome.RolledBack);
        Assert.True(files.FileExists($@"{InstallRoot}\KidShell.Recovery\previous.dll"));
    }

    [Fact]
    public void A_file_substituted_between_staging_and_installation_is_caught()
    {
        // The only window a substitution fits into, and the reason the
        // verification runs twice.
        var (manifest, files) = Prepare(filesPerComponent: 1);

        var transaction = new InstallTransaction(files);

        // Stand in for the race by tampering with the installed copy through a
        // path that does not go through Copy.
        var outcome = transaction.Execute(manifest, BundleRoot, InstallRoot, StagingRoot);
        Assert.True(outcome.Committed);

        files.Tamper($@"{InstallRoot}\KidShell.Watchdog\file0.dll", "swapped");

        var receipt = InstallReceipt.For(
            manifest, InstallRoot, outcome.WrittenFiles, DateTimeOffset.UnixEpoch);

        var checks = InstallVerification.Check(receipt, files, InstallRoot, signatureChecked: false);

        Assert.Equal(InstallVerificationStatus.Fail, InstallVerification.Overall(checks));
        Assert.Contains(checks, c => c.Detail.Contains("has changed since it was installed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_manifest_with_no_installable_files_is_refused()
    {
        var files = new FakeInstallFileSystem();

        var manifest = ReleaseManifestTests.Valid() with
        {
            Components =
            [
                new ReleaseComponent
                {
                    Component = KidShellComponent.App,
                    BundlePath = "package",
                    Files = [new ReleaseFile("KidShell.msix", InstallTransaction.Digest("x"), 1)]
                }
            ]
        };

        var outcome = new InstallTransaction(files)
            .Execute(manifest, BundleRoot, InstallRoot, StagingRoot);

        Assert.False(outcome.Committed);
        Assert.Contains(outcome.Log, l => l.Contains("no files to install", StringComparison.Ordinal));
    }

    [Fact]
    public void Installing_files_enables_no_lockdown()
    {
        // The property the whole design rests on: installing the binaries and
        // enabling the security are different operations. Asserted as an
        // absence, because an absence is what it is - the transaction has no
        // service, no account, no access list and no registry in it at all.
        var (manifest, files) = Prepare();

        var outcome = new InstallTransaction(files)
            .Execute(manifest, BundleRoot, InstallRoot, StagingRoot);

        Assert.True(outcome.Committed);

        // Everything written is under the install root. Nothing reached
        // ProgramData, no service was registered, nothing touched a policy.
        foreach (var path in files.All.Keys.Where(k => !k.StartsWith(BundleRoot, StringComparison.OrdinalIgnoreCase)))
        {
            Assert.True(
                path.StartsWith(InstallRoot, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(StagingRoot, StringComparison.OrdinalIgnoreCase),
                $"the install wrote outside the install and staging roots: {path}");
        }

        Assert.DoesNotContain(files.All.Keys, p => p.Contains("ProgramData", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The read-only verifier, and its refusal to call a skipped check a pass.</summary>
public class InstallVerificationTests
{
    private const string InstallRoot = @"C:\Program Files\KidShell";

    private static (InstallReceipt Receipt, FakeInstallFileSystem Files) Installed(bool signed = false)
    {
        var files = new FakeInstallFileSystem();
        files.Seed($@"{InstallRoot}\KidShell.Watchdog\a.dll", "alpha");
        files.CreateDirectory(InstallRoot);

        var receipt = new InstallReceipt
        {
            Version = "1.0.0",
            Prerelease = "rc.1",
            GitSha = new string('a', 40),
            Architecture = "x64",
            InstalledUtc = "2026-10-02 10:00:00Z",
            Signed = signed,
            Channel = signed ? ReleaseChannel.Production : ReleaseChannel.DedicatedLabUnsigned,
            InstallRoot = InstallRoot,
            Files = new Dictionary<string, string>
            {
                [@"KidShell.Watchdog\a.dll"] = InstallTransaction.Digest("alpha")
            }
        };

        return (receipt, files);
    }

    [Fact]
    public void A_matching_unsigned_installation_passes()
    {
        var (receipt, files) = Installed();

        var checks = InstallVerification.Check(receipt, files, InstallRoot, signatureChecked: false);

        Assert.Equal(InstallVerificationStatus.Pass, InstallVerification.Overall(checks));
        Assert.Contains(checks, c => c.Detail.Contains("UNSIGNED", StringComparison.Ordinal));
    }

    [Fact]
    public void A_signed_installation_with_no_signature_check_is_incomplete_not_pass()
    {
        // The rule that matters. The point of signing is that somebody later
        // relies on the check having happened.
        var (receipt, files) = Installed(signed: true);

        var checks = InstallVerification.Check(receipt, files, InstallRoot, signatureChecked: false);

        Assert.Equal(InstallVerificationStatus.Incomplete, InstallVerification.Overall(checks));
        Assert.Contains(checks, c => c.Detail.Contains("not a pass", StringComparison.Ordinal));
    }

    [Fact]
    public void A_signed_installation_with_the_signature_checked_passes()
    {
        var (receipt, files) = Installed(signed: true);

        var checks = InstallVerification.Check(receipt, files, InstallRoot, signatureChecked: true);

        Assert.Equal(InstallVerificationStatus.Pass, InstallVerification.Overall(checks));
    }

    [Fact]
    public void No_receipt_is_incomplete_rather_than_a_failure()
    {
        var checks = InstallVerification.Check(null, new FakeInstallFileSystem(), InstallRoot, false);

        Assert.Equal(InstallVerificationStatus.Incomplete, InstallVerification.Overall(checks));
    }

    [Fact]
    public void A_receipt_with_no_install_root_on_disk_fails()
    {
        var (receipt, _) = Installed();

        var checks = InstallVerification.Check(receipt, new FakeInstallFileSystem(), InstallRoot, false);

        Assert.Equal(InstallVerificationStatus.Fail, InstallVerification.Overall(checks));
    }

    [Fact]
    public void A_missing_file_fails()
    {
        var (receipt, files) = Installed();
        files.DeleteFile($@"{InstallRoot}\KidShell.Watchdog\a.dll");
        files.CreateDirectory(InstallRoot);

        var checks = InstallVerification.Check(receipt, files, InstallRoot, false);

        Assert.Equal(InstallVerificationStatus.Fail, InstallVerification.Overall(checks));
        Assert.Contains(checks, c => c.Detail.Contains("is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_changed_file_fails()
    {
        var (receipt, files) = Installed();
        files.Tamper($@"{InstallRoot}\KidShell.Watchdog\a.dll", "tampered");

        var checks = InstallVerification.Check(receipt, files, InstallRoot, false);

        Assert.Equal(InstallVerificationStatus.Fail, InstallVerification.Overall(checks));
    }

    [Fact]
    public void An_unexpected_file_is_reported_and_not_deleted_or_failed()
    {
        // On a lab device the something that put it there is usually the
        // operator, so the answer is "tell them", not "fail" and certainly not
        // "delete".
        var (receipt, files) = Installed();
        files.Seed($@"{InstallRoot}\KidShell.Watchdog\stray.txt", "not mine");

        var checks = InstallVerification.Check(receipt, files, InstallRoot, false);

        Assert.Equal(InstallVerificationStatus.Incomplete, InstallVerification.Overall(checks));
        Assert.Contains(checks, c => c.Detail.Contains("not in the receipt", StringComparison.Ordinal));
        Assert.True(files.FileExists($@"{InstallRoot}\KidShell.Watchdog\stray.txt"));
    }

    [Fact]
    public void A_receipt_with_no_commit_fails()
    {
        var (receipt, files) = Installed();

        var checks = InstallVerification.Check(
            receipt with { GitSha = "" }, files, InstallRoot, false);

        Assert.Equal(InstallVerificationStatus.Fail, InstallVerification.Overall(checks));
    }

    [Fact]
    public void No_checks_at_all_is_incomplete()
    {
        Assert.Equal(InstallVerificationStatus.Incomplete, InstallVerification.Overall([]));
    }

    [Fact]
    public void One_failure_outranks_any_number_of_passes()
    {
        var checks = new List<InstallCheck>
        {
            new(InstallVerificationStatus.Pass, "a"),
            new(InstallVerificationStatus.Pass, "b"),
            new(InstallVerificationStatus.Fail, "c"),
            new(InstallVerificationStatus.Incomplete, "d")
        };

        Assert.Equal(InstallVerificationStatus.Fail, InstallVerification.Overall(checks));
    }
}
