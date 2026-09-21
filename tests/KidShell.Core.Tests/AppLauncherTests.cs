using KidShell.Core.Launching;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// Launcher behaviour, exercised entirely with fakes - no program is ever
/// started by the test suite.
/// </summary>
public class AppLauncherTests
{
    private static AppLauncher Create(
        ExecutableResolution resolution,
        out FakeProcessRunner runner,
        out RecordingLogger logger,
        Exception? throwOnStart = null)
    {
        runner = new FakeProcessRunner(throwOnStart);
        logger = new RecordingLogger();
        return new AppLauncher(new StubResolver(resolution), runner, logger);
    }

    [Fact]
    public void A_resolvable_file_reports_success_and_starts_it()
    {
        var launcher = Create(
            new ExecutableResolution(ExecutableResolutionKind.File, @"C:\Windows\System32\calc.exe"),
            out var runner,
            out _);

        var result = launcher.Launch(TestFactory.App("calculator", path: "calc.exe", arguments: "/x"));

        Assert.Equal(LaunchStatus.Success, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Single(runner.Started);
        Assert.Equal(@"C:\Windows\System32\calc.exe", runner.Started[0].FileName);
        Assert.Equal("/x", runner.Started[0].Arguments);
    }

    [Fact]
    public void An_empty_path_reports_not_configured_and_starts_nothing()
    {
        var launcher = Create(
            new ExecutableResolution(ExecutableResolutionKind.Empty),
            out var runner,
            out _);

        var result = launcher.Launch(TestFactory.App("minecraft", path: string.Empty));

        Assert.Equal(LaunchStatus.NotConfigured, result.Status);
        Assert.Empty(runner.Started);
        Assert.Contains("inte konfigurerat", result.ChildMessage);
    }

    [Fact]
    public void A_missing_program_reports_not_found()
    {
        var launcher = Create(
            new ExecutableResolution(ExecutableResolutionKind.NotFound, null, "Not found on PATH: nope.exe"),
            out var runner,
            out var logger);

        var result = launcher.Launch(TestFactory.App("ghost", path: "nope.exe"));

        Assert.Equal(LaunchStatus.NotFound, result.Status);
        Assert.Empty(runner.Started);
        Assert.Contains(logger.Entries, e => e.Level == Core.Diagnostics.LogLevel.Warning);
    }

    [Fact]
    public void A_failing_start_reports_failed_rather_than_throwing()
    {
        var launcher = Create(
            new ExecutableResolution(ExecutableResolutionKind.File, @"C:\locked.exe"),
            out _,
            out _,
            throwOnStart: new UnauthorizedAccessException("denied"));

        var result = launcher.Launch(TestFactory.App("locked", path: @"C:\locked.exe"));

        Assert.Equal(LaunchStatus.Failed, result.Status);
        Assert.Contains("UnauthorizedAccessException", result.TechnicalDetail);
    }

    [Fact]
    public void A_throwing_resolver_reports_failed_rather_than_propagating()
    {
        var launcher = new AppLauncher(
            new StubResolver(_ => throw new InvalidOperationException("boom")),
            new FakeProcessRunner(),
            new RecordingLogger());

        var result = launcher.Launch(TestFactory.App());

        Assert.Equal(LaunchStatus.Failed, result.Status);
        Assert.Contains("boom", result.TechnicalDetail);
    }

    [Fact]
    public void A_disabled_app_is_blocked_before_anything_is_resolved()
    {
        var launcher = Create(
            new ExecutableResolution(ExecutableResolutionKind.File, @"C:\calc.exe"),
            out var runner,
            out _);

        var result = launcher.Launch(TestFactory.App("off", enabled: false));

        Assert.Equal(LaunchStatus.Blocked, result.Status);
        Assert.Empty(runner.Started);
    }

    [Fact]
    public void A_protocol_target_is_started_through_the_shell()
    {
        var launcher = Create(
            new ExecutableResolution(ExecutableResolutionKind.ShellTarget, "calculator:"),
            out var runner,
            out _);

        var result = launcher.Launch(TestFactory.App("calculator", path: "calculator:"));

        Assert.Equal(LaunchStatus.Success, result.Status);
        Assert.True(runner.Started[0].UseShellExecute);
    }

    [Fact]
    public void Technical_detail_never_reaches_the_child_message()
    {
        var launcher = Create(
            new ExecutableResolution(ExecutableResolutionKind.NotFound, null, @"Not found: C:\Users\Someone\secret.exe"),
            out _,
            out _);

        var result = launcher.Launch(TestFactory.App("ghost", path: @"C:\Users\Someone\secret.exe"));

        Assert.DoesNotContain("secret.exe", result.ChildMessage);
        Assert.Contains("secret.exe", result.TechnicalDetail);
    }

    [Fact]
    public void Every_outcome_carries_the_app_id_for_the_log()
    {
        var launcher = Create(new ExecutableResolution(ExecutableResolutionKind.Empty), out _, out _);

        var result = launcher.Launch(TestFactory.App("minecraft", path: string.Empty));

        Assert.Equal("minecraft", result.AppId);
        Assert.Equal("minecraft", result.ChildTitle);
    }
}

public class WindowsExecutableResolverTests
{
    private static WindowsExecutableResolver Create(params string[] existingFiles) =>
        new(
            path => existingFiles.Contains(path, StringComparer.OrdinalIgnoreCase),
            [@"C:\Windows\System32", @"C:\Tools"]);

    [Fact]
    public void An_empty_command_resolves_to_empty()
    {
        Assert.Equal(ExecutableResolutionKind.Empty, Create().Resolve("").Kind);
        Assert.Equal(ExecutableResolutionKind.Empty, Create().Resolve("   ").Kind);
    }

    [Fact]
    public void A_bare_name_is_probed_against_the_search_directories()
    {
        var resolution = Create(@"C:\Windows\System32\calc.exe").Resolve("calc.exe");

        Assert.Equal(ExecutableResolutionKind.File, resolution.Kind);
        Assert.Equal(@"C:\Windows\System32\calc.exe", resolution.ResolvedPath);
    }

    [Fact]
    public void A_name_without_an_extension_gets_one_tried_for_it()
    {
        var resolution = Create(@"C:\Tools\thing.exe").Resolve("thing");

        Assert.Equal(ExecutableResolutionKind.File, resolution.Kind);
        Assert.Equal(@"C:\Tools\thing.exe", resolution.ResolvedPath);
    }

    [Fact]
    public void An_unknown_name_resolves_to_not_found()
    {
        var resolution = Create().Resolve("nope.exe");

        Assert.Equal(ExecutableResolutionKind.NotFound, resolution.Kind);
    }

    [Fact]
    public void An_absolute_path_is_checked_directly()
    {
        Assert.Equal(
            ExecutableResolutionKind.File,
            Create(@"C:\Games\game.exe").Resolve(@"C:\Games\game.exe").Kind);

        Assert.Equal(
            ExecutableResolutionKind.NotFound,
            Create().Resolve(@"C:\Games\game.exe").Kind);
    }

    [Fact]
    public void Surrounding_quotes_are_tolerated()
    {
        var resolution = Create(@"C:\Games\game.exe").Resolve("\"C:\\Games\\game.exe\"");

        Assert.Equal(ExecutableResolutionKind.File, resolution.Kind);
    }

    [Theory]
    [InlineData("calculator:")]
    [InlineData("ms-paint:")]
    [InlineData("https://example.com")]
    public void Protocol_activations_are_recognised(string command)
    {
        Assert.Equal(ExecutableResolutionKind.ShellTarget, Create().Resolve(command).Kind);
    }

    [Fact]
    public void A_drive_letter_is_not_mistaken_for_a_protocol()
    {
        Assert.False(WindowsExecutableResolver.LooksLikeShellTarget(@"C:\Windows\notepad.exe"));
        Assert.True(WindowsExecutableResolver.LooksLikeShellTarget("calculator:"));
    }
}
