using KidShell.Core.Configuration;
using KidShell.Core.Updates;
using KidShell.Core.Watchdog;
using KidShell.Core.Web;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>Web allowlist normalization and browser policy generation.</summary>
public class WebPolicyTests
{
    // ------------------------------------------------ normalization

    [Theory]
    [InlineData("svt.se", "svt.se")]
    [InlineData("www.svt.se", "svt.se")]
    [InlineData("https://svt.se", "svt.se")]
    [InlineData("https://www.svt.se/", "svt.se")]
    [InlineData("HTTPS://SVT.SE", "svt.se")]
    [InlineData("http://svt.se/barn/spel?x=1#top", "svt.se")]
    [InlineData("https://svt.se:8443/", "svt.se")]
    [InlineData("  svt.se.  ", "svt.se")]
    [InlineData("barn.svt.se", "barn.svt.se")]
    public void Everything_a_parent_might_type_reduces_to_one_host(string input, string expected)
    {
        var (result, host) = WebAllowlist.Normalize(input);

        Assert.Equal(UrlValidation.Ok, result);
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("file:///C:/Users")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("ms-settings:privacy")]
    [InlineData("shell:Downloads")]
    [InlineData("search-ms:query=x")]
    public void Schemes_that_are_ways_out_of_the_browser_are_refused(string input)
    {
        // "file:///C:/" in an allowlist would be a file browser.
        var (result, _) = WebAllowlist.Normalize(input);

        Assert.Equal(UrlValidation.UnsupportedScheme, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_entry_is_refused(string input) =>
        Assert.Equal(UrlValidation.Empty, WebAllowlist.Normalize(input).Result);

    [Theory]
    [InlineData("notahost")]
    [InlineData("has space.se")]
    [InlineData("-leading.se")]
    [InlineData("trailing-.se")]
    public void Things_that_are_not_hosts_are_refused(string input) =>
        Assert.Equal(UrlValidation.Malformed, WebAllowlist.Normalize(input).Result);

    [Fact]
    public void An_IP_address_is_accepted_but_distinguished()
    {
        var (result, host) = WebAllowlist.Normalize("192.168.1.10");

        // Valid, but it has no subdomains and will not survive the site moving.
        Assert.Equal(UrlValidation.IpAddress, result);
        Assert.Equal("192.168.1.10", host);
    }

    // ------------------------------------------------ list behaviour

    [Fact]
    public void The_same_site_typed_twice_is_a_duplicate()
    {
        var existing = new List<AllowlistEntry>();
        var (_, first) = WebAllowlist.TryCreate("svt.se", existing);
        existing.Add(first!);

        var (result, _) = WebAllowlist.TryCreate("https://www.svt.se/barn", existing);

        Assert.Equal(UrlValidation.Duplicate, result);
    }

    [Fact]
    public void Subdomains_are_covered_but_lookalikes_are_not()
    {
        var list = new List<AllowlistEntry>
        {
            new() { Host = "svt.se", IncludeSubdomains = true }
        };

        Assert.True(WebAllowlist.Permits(list, "https://barn.svt.se/spel"));
        Assert.True(WebAllowlist.Permits(list, "svt.se"));

        // The dot is what makes it a subdomain; "notsvt.se" is a different site.
        Assert.False(WebAllowlist.Permits(list, "https://notsvt.se"));
        Assert.False(WebAllowlist.Permits(list, "https://svt.se.evil.com"));
    }

    [Fact]
    public void Subdomains_can_be_excluded()
    {
        var list = new List<AllowlistEntry>
        {
            new() { Host = "svt.se", IncludeSubdomains = false }
        };

        Assert.True(WebAllowlist.Permits(list, "svt.se"));
        Assert.False(WebAllowlist.Permits(list, "barn.svt.se"));
    }

    [Fact]
    public void A_dangerous_url_is_never_permitted()
    {
        var list = new List<AllowlistEntry> { new() { Host = "svt.se" } };

        Assert.False(WebAllowlist.Permits(list, "file:///C:/Windows"));
    }

    [Theory]
    [InlineData(UrlValidation.Empty)]
    [InlineData(UrlValidation.UnsupportedScheme)]
    [InlineData(UrlValidation.Malformed)]
    [InlineData(UrlValidation.Duplicate)]
    public void Every_rejection_has_parent_facing_wording(UrlValidation result) =>
        Assert.False(string.IsNullOrWhiteSpace(WebAllowlist.Describe(result)));

    // ------------------------------------------------ policy generation

    [Fact]
    public void Allowlist_mode_blocks_everything_then_permits_the_list()
    {
        var entries = new List<AllowlistEntry>
        {
            new() { Host = "svt.se", IncludeSubdomains = true }
        };

        var policy = BrowserPolicyGenerator.Generate(
            new WebSettings { Mode = WebMode.Allowlist }, entries);

        Assert.Contains(policy.Settings, s => s.Name == "URLBlocklist" && s.Value.Contains('*'));
        Assert.Contains(policy.Settings, s => s.Name == "URLAllowlist" && s.Value.Contains("svt.se"));
    }

    [Fact]
    public void Allowlist_mode_also_closes_the_side_doors()
    {
        var policy = BrowserPolicyGenerator.Generate(
            new WebSettings { Mode = WebMode.Allowlist },
            [new AllowlistEntry { Host = "svt.se" }]);

        // Downloads, popups and InPrivate are all ways around an allowlist.
        Assert.Contains(policy.Settings, s => s.Name == "DownloadRestrictions");
        Assert.Contains(policy.Settings, s => s.Name == "DefaultPopupsSetting");
        Assert.Contains(policy.Settings, s => s.Name == "InPrivateModeAvailability");
    }

    [Fact]
    public void An_empty_allowlist_is_flagged()
    {
        var policy = BrowserPolicyGenerator.Generate(new WebSettings { Mode = WebMode.Allowlist }, []);

        // Valid, but a browser that opens nothing is never what a parent meant.
        Assert.Contains(policy.Warnings, w => w.Contains("inte kunna öppna", StringComparison.Ordinal));
    }

    [Fact]
    public void No_browser_mode_generates_no_policy_and_says_why()
    {
        var policy = BrowserPolicyGenerator.Generate(new WebSettings { Mode = WebMode.NoBrowser });

        Assert.Empty(policy.Settings);
        Assert.Contains(policy.Warnings, w => w.Contains("applistan", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_mode_admits_it_restricts_nothing()
    {
        var policy = BrowserPolicyGenerator.Generate(new WebSettings { Mode = WebMode.Open });

        Assert.Empty(policy.Settings);
        Assert.Contains(policy.Warnings, w => w.Contains("hela internet", StringComparison.Ordinal));
    }

    [Fact]
    public void A_generated_policy_is_never_marked_applied()
    {
        var policy = BrowserPolicyGenerator.Generate(
            new WebSettings { Mode = WebMode.Allowlist },
            [new AllowlistEntry { Host = "svt.se" }]);

        Assert.False(policy.WasApplied);
    }

    [Fact]
    public void The_registry_preview_says_it_was_not_applied()
    {
        var policy = BrowserPolicyGenerator.Generate(
            new WebSettings { Mode = WebMode.Allowlist },
            [new AllowlistEntry { Host = "svt.se" }]);

        var preview = BrowserPolicyGenerator.ToRegistryPreview(policy);

        Assert.Contains("FÖRHANDSGRANSKNING", preview, StringComparison.Ordinal);
        Assert.Contains("Ingenting av detta har tillämpats", preview, StringComparison.Ordinal);
        Assert.Contains("svt.se", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generator_cannot_apply_anything()
    {
        // Applying browser policy is a machine change and belongs behind the
        // transaction boundary. No method here may do it.
        var methods = typeof(BrowserPolicyGenerator).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        foreach (var forbidden in new[] { "Apply", "Write", "Install", "Set", "Deploy" })
        {
            Assert.DoesNotContain(methods, m => m.Name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }
}

/// <summary>Watchdog crash-loop logic.</summary>
public class WatchdogTests
{
    private static (ShellHealthMonitor Monitor, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 11, 10, 0, 0, TimeSpan.Zero));
        return (new ShellHealthMonitor(new RecordingLogger(), time), time);
    }

    [Fact]
    public void A_beating_shell_is_healthy()
    {
        var (monitor, time) = Create();

        time.Advance(TimeSpan.FromSeconds(10));
        monitor.Heartbeat();

        Assert.Equal(ShellHealth.Healthy, monitor.Evaluate().Health);
    }

    [Fact]
    public void A_quiet_shell_becomes_unresponsive_before_it_is_declared_dead()
    {
        var (monitor, time) = Create();

        time.Advance(ShellHealthMonitor.UnresponsiveAfter + TimeSpan.FromSeconds(1));

        var decision = monitor.Evaluate();

        // Noticed, but not restarted: a busy shell deserves a moment.
        Assert.Equal(ShellHealth.Unresponsive, decision.Health);
        Assert.Equal(WatchdogAction.None, decision.Action);
    }

    [Fact]
    public void A_silent_shell_is_restarted()
    {
        var (monitor, time) = Create();

        time.Advance(ShellHealthMonitor.StoppedAfter + TimeSpan.FromSeconds(1));

        var decision = monitor.Evaluate();

        Assert.Equal(ShellHealth.Stopped, decision.Health);
        Assert.Equal(WatchdogAction.Restart, decision.Action);
    }

    [Fact]
    public void Rapid_restarts_stop_the_watchdog_rather_than_flickering()
    {
        var (monitor, time) = Create();

        for (var i = 0; i < ShellHealthMonitor.CrashLoopThreshold; i++)
        {
            monitor.RecordRestart();
            time.Advance(TimeSpan.FromSeconds(5));
        }

        var decision = monitor.Evaluate();

        // A stuck child who can ask for help is better off than one watching
        // a window strobe.
        Assert.Equal(ShellHealth.CrashLooping, decision.Health);
        Assert.Equal(WatchdogAction.ShowFailureScreen, decision.Action);
    }

    [Fact]
    public void Restarts_spread_over_time_are_not_a_crash_loop()
    {
        var (monitor, time) = Create();

        for (var i = 0; i < 5; i++)
        {
            monitor.RecordRestart();
            time.Advance(ShellHealthMonitor.CrashLoopWindow + TimeSpan.FromMinutes(1));
            monitor.Heartbeat();
        }

        Assert.NotEqual(ShellHealth.CrashLooping, monitor.Evaluate().Health);

        // Each restart aged out of the window before the next one, so none of
        // them count towards a loop.
        Assert.True(monitor.RecentRestarts < ShellHealthMonitor.CrashLoopThreshold);
    }

    [Fact]
    public void A_parent_can_clear_the_crash_history()
    {
        var (monitor, _) = Create();

        for (var i = 0; i < ShellHealthMonitor.CrashLoopThreshold; i++)
        {
            monitor.RecordRestart();
        }

        Assert.Equal(ShellHealth.CrashLooping, monitor.Evaluate().Health);

        monitor.Reset();

        Assert.Equal(ShellHealth.Healthy, monitor.Evaluate().Health);
    }

    [Fact]
    public void The_failure_screen_never_offers_the_desktop()
    {
        // Dropping a child onto the desktop as an error path would make
        // crashing the shell the easiest way out of it.
        Assert.Equal("Något gick fel.", ShellHealthMonitor.FailureHeadline);
        Assert.Equal("Be en vuxen om hjälp.", ShellHealthMonitor.FailureBody);

        foreach (var word in new[] { "skrivbord", "desktop", "Utforskaren", "Explorer", "avsluta" })
        {
            Assert.DoesNotContain(word, ShellHealthMonitor.FailureBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(word, ShellHealthMonitor.FailureHeadline, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Update verification rules.</summary>
public class UpdatePolicyTests
{
    private static UpdateManifest Manifest(
        string version = "0.9.0",
        string url = "https://example.invalid/kidshell.msix",
        bool signed = true,
        string? sha = null,
        string? minimumFrom = null) => new()
    {
        Version = version,
        PackageUrl = url,
        Sha256 = sha ?? new string('a', 64),
        IsSigned = signed,
        MinimumFromVersion = minimumFrom
    };

    // ------------------------------------------------ disabled by default

    [Fact]
    public void Updates_are_disabled_in_this_build()
    {
        // No signing infrastructure yet, and an updater that installs
        // unsigned packages is remote code execution with a friendly name.
        Assert.False(UpdatePolicy.UpdatesEnabled);

        var decision = UpdatePolicy.Evaluate(Manifest(), new KidShellVersion(0, 2, 0));

        Assert.False(decision.ShouldUpdate);
        Assert.Equal(UpdateRejection.UpdatesDisabled, decision.Rejection);
    }

    // ------------------------------------------------ the rules themselves

    [Fact]
    public void A_newer_signed_https_package_would_be_accepted()
    {
        var decision = UpdatePolicy.Evaluate(
            Manifest("0.9.0"), new KidShellVersion(0, 2, 0), allowWhenDisabled: true);

        Assert.True(decision.ShouldUpdate);
    }

    [Fact]
    public void An_unsigned_package_is_refused()
    {
        var decision = UpdatePolicy.Evaluate(
            Manifest(signed: false), new KidShellVersion(0, 2, 0), allowWhenDisabled: true);

        Assert.Equal(UpdateRejection.Unsigned, decision.Rejection);
    }

    [Fact]
    public void A_package_offered_over_plain_http_is_refused()
    {
        // Plain http lets anyone on the network choose what gets installed.
        var decision = UpdatePolicy.Evaluate(
            Manifest(url: "http://example.invalid/kidshell.msix"),
            new KidShellVersion(0, 2, 0),
            allowWhenDisabled: true);

        Assert.Equal(UpdateRejection.InsecureUrl, decision.Rejection);
    }

    [Fact]
    public void An_older_or_equal_version_is_refused()
    {
        Assert.Equal(UpdateRejection.NotNewer, UpdatePolicy.Evaluate(
            Manifest("0.1.0"), new KidShellVersion(0, 2, 0), allowWhenDisabled: true).Rejection);

        Assert.Equal(UpdateRejection.NotNewer, UpdatePolicy.Evaluate(
            Manifest("0.2.0"), new KidShellVersion(0, 2, 0), allowWhenDisabled: true).Rejection);
    }

    [Fact]
    public void A_malformed_hash_is_refused()
    {
        var decision = UpdatePolicy.Evaluate(
            Manifest(sha: "nope"), new KidShellVersion(0, 2, 0), allowWhenDisabled: true);

        Assert.Equal(UpdateRejection.MalformedManifest, decision.Rejection);
    }

    [Fact]
    public void An_unsupported_upgrade_path_is_refused()
    {
        var decision = UpdatePolicy.Evaluate(
            Manifest("1.0.0", minimumFrom: "0.8.0"),
            new KidShellVersion(0, 2, 0),
            allowWhenDisabled: true);

        Assert.Equal(UpdateRejection.UpgradePathUnsupported, decision.Rejection);
    }

    [Fact]
    public void A_malformed_manifest_is_refused_rather_than_throwing()
    {
        Assert.Null(UpdatePolicy.ParseManifest("{ not json"));

        var decision = UpdatePolicy.Evaluate(null, new KidShellVersion(0, 2, 0), allowWhenDisabled: true);
        Assert.Equal(UpdateRejection.MalformedManifest, decision.Rejection);
    }

    // ------------------------------------------------ hash verification

    [Fact]
    public void A_package_matching_its_hash_verifies()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("kidshell package bytes");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));

        Assert.True(UpdatePolicy.VerifyPackage(payload, hash));
        Assert.True(UpdatePolicy.VerifyPackage(payload, hash.ToUpperInvariant()));
    }

    [Fact]
    public void A_tampered_package_fails_verification()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("kidshell package bytes");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));

        var tampered = System.Text.Encoding.UTF8.GetBytes("kidshell package bytez");

        Assert.False(UpdatePolicy.VerifyPackage(tampered, hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzz")]
    public void An_invalid_hash_never_verifies(string hash) =>
        Assert.False(UpdatePolicy.VerifyPackage([1, 2, 3], hash));

    // ------------------------------------------------ version comparison

    [Theory]
    [InlineData("1.0.0", "0.9.9", 1)]
    [InlineData("0.2.0", "0.2.0", 0)]
    [InlineData("0.2.0", "0.10.0", -1)]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("v1.0.0", "1.0.0", 0)]
    public void Versions_compare_numerically_not_alphabetically(string a, string b, int expected)
    {
        Assert.True(KidShellVersion.TryParse(a, out var left));
        Assert.True(KidShellVersion.TryParse(b, out var right));

        Assert.Equal(expected, Math.Sign(left.CompareTo(right)));
    }

    [Fact]
    public void A_release_outranks_its_own_pre_release()
    {
        // The opposite of a plain string comparison, where "1.0.0-rc1" sorts
        // after "1.0.0".
        Assert.True(KidShellVersion.TryParse("1.0.0", out var release));
        Assert.True(KidShellVersion.TryParse("1.0.0-rc1", out var candidate));

        Assert.True(release > candidate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    public void Malformed_versions_do_not_parse(string text) =>
        Assert.False(KidShellVersion.TryParse(text, out _));

    [Fact]
    public void A_version_round_trips_through_its_string_form()
    {
        Assert.True(KidShellVersion.TryParse("0.8.0-rc2", out var version));
        Assert.Equal("0.8.0-rc2", version.ToString());
    }
}
