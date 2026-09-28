using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// OPSV FINDING 03 — every Store application was refused by the add-app flow.
///
/// The browse list found them, offered them, and carried their packaged
/// identity into the field a hand-typed path goes in. That field was then
/// checked by the rule for hand-typed paths, which requires .exe - so an AUMID
/// was rejected as "not a supported program", and so was a protocol
/// identifier. A parent could see Calculator in the list and had no way to add
/// it.
///
/// These tests are about the input KIND being represented rather than guessed.
/// </summary>
public class LaunchKindTests
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    // ------------------------------------------------------------ packaged

    [Fact]
    public void A_store_app_is_accepted_by_its_packaged_identity()
    {
        var check = LaunchTargetPolicy.Check(ApplicationLaunchKind.PackagedApp, CalculatorAumid);

        Assert.True(check.IsAllowed);
    }

    [Fact]
    public void A_store_app_is_not_required_to_end_in_exe()
    {
        // The regression, stated directly. The same string through the manual
        // rule is refused, and that is correct for a hand-typed path and wrong
        // for a discovered application.
        Assert.False(ManualProgramPolicy.Check(CalculatorAumid).IsAllowed);
        Assert.True(LaunchTargetPolicy.Check(ApplicationLaunchKind.PackagedApp, CalculatorAumid).IsAllowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NoBangSeparator")]
    [InlineData("!LeadingBang")]
    [InlineData("TrailingBang!")]
    public void A_packaged_identity_that_is_not_one_is_refused(string aumid) =>
        Assert.False(LaunchTargetPolicy.Check(ApplicationLaunchKind.PackagedApp, aumid).IsAllowed);

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\server\share\thing.exe")]
    [InlineData("folder/app!App")]
    [InlineData(@"folder\app!App")]
    public void A_path_cannot_be_smuggled_in_as_a_packaged_app(string value)
    {
        // Otherwise the packaged kind would be a way round the script,
        // shortcut and escape-surface rules, which it does not apply.
        Assert.False(LaunchTargetPolicy.Check(ApplicationLaunchKind.PackagedApp, value).IsAllowed);
    }

    // ------------------------------------------------------------- win32

    [Fact]
    public void A_normal_executable_is_still_accepted() =>
        Assert.True(LaunchTargetPolicy.Check(
            ApplicationLaunchKind.Win32Executable, @"C:\Program Files\Paint\mspaint.exe").IsAllowed);

    [Theory]
    [InlineData(@"C:\tools\run.bat")]
    [InlineData(@"C:\tools\run.cmd")]
    [InlineData(@"C:\tools\run.ps1")]
    [InlineData(@"C:\tools\run.vbs")]
    public void A_script_is_still_refused(string path) =>
        Assert.False(LaunchTargetPolicy.Check(ApplicationLaunchKind.Win32Executable, path).IsAllowed);

    [Fact]
    public void A_shortcut_is_still_refused() =>
        Assert.False(LaunchTargetPolicy.Check(
            ApplicationLaunchKind.Win32Executable, @"C:\Users\Lucas\Desktop\Game.lnk").IsAllowed);

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\powershell.exe")]
    [InlineData(@"C:\Windows\regedit.exe")]
    public void A_dangerous_system_tool_is_still_refused(string path) =>
        Assert.False(LaunchTargetPolicy.Check(ApplicationLaunchKind.Win32Executable, path).IsAllowed);

    // ----------------------------------------------------------- protocol

    [Theory]
    [InlineData("calculator:")]
    [InlineData("ms-settings:")]
    [InlineData("http://example.com")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    public void An_arbitrary_protocol_is_refused(string target)
    {
        // A scheme names whichever application is registered for it today,
        // which can change after the parent approved it. Nothing KidShell
        // ships needs one, so the list is empty and this is the whole rule.
        Assert.False(LaunchTargetPolicy.Check(ApplicationLaunchKind.UriProtocol, target).IsAllowed);
    }

    [Fact]
    public void The_supported_protocol_list_is_empty_on_purpose()
    {
        // If this ever stops being true, the entry needs a test of its own
        // saying which application it names and why that is safe.
        Assert.Empty(LaunchTargetPolicy.SupportedProtocols);
    }

    // ------------------------------------------------------- persistence

    [Fact]
    public void The_launch_kind_survives_a_save_and_a_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kidshell-launchkind-{Guid.NewGuid():n}.json");

        try
        {
            var store = new JsonConfigurationStore(path, new RecordingLogger());
            var configuration = KidShellConfiguration.CreateDefault();

            configuration.Apps.Clear();
            configuration.Apps.Add(new KidAppDefinition
            {
                Id = "calc",
                DisplayName = "Miniräknare",
                LaunchKind = ApplicationLaunchKind.PackagedApp,
                ExecutablePath = CalculatorAumid
            });

            Assert.True(store.Save(configuration));

            var reloaded = store.Load().Configuration;
            var app = Assert.Single(reloaded.Apps);

            Assert.Equal(ApplicationLaunchKind.PackagedApp, app.LaunchKind);
            Assert.Equal(CalculatorAumid, app.ExecutablePath);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void A_configuration_written_before_this_field_existed_reads_as_win32()
    {
        // The default must be the kind every existing entry actually is, or an
        // upgrade would reclassify every app a family has already approved.
        Assert.Equal(ApplicationLaunchKind.Win32Executable, new KidAppDefinition().LaunchKind);
    }

    [Fact]
    public void Cloning_carries_the_launch_kind()
    {
        // The draft/commit cycle clones. A kind lost there would silently
        // reclassify a Store app as an executable the first time a parent
        // saved anything at all.
        var original = new KidAppDefinition
        {
            Id = "calc",
            LaunchKind = ApplicationLaunchKind.PackagedApp,
            ExecutablePath = CalculatorAumid
        };

        Assert.Equal(ApplicationLaunchKind.PackagedApp, original.Clone().LaunchKind);
    }

    // ---------------------------------------------------------- launching

    [Fact]
    public void A_store_app_reaches_the_launcher_through_the_apps_folder()
    {
        var runner = new RecordingProcessRunner();
        var launcher = new AppLauncher(new WindowsExecutableResolver(), runner, new RecordingLogger());

        var result = launcher.Launch(new KidAppDefinition
        {
            Id = "calc",
            DisplayName = "Miniräknare",
            IsEnabled = true,
            LaunchKind = ApplicationLaunchKind.PackagedApp,
            ExecutablePath = CalculatorAumid
        });

        Assert.Equal(LaunchStatus.Success, result.Status);
        Assert.Equal($@"shell:AppsFolder\{CalculatorAumid}", runner.LastFileName);
        Assert.True(runner.LastUsedShell);
    }

    [Fact]
    public void A_store_app_is_never_looked_for_on_disk()
    {
        // An AUMID is an identity Windows holds, not a path. Asking the file
        // system about one always answers "not found", which is how a Store
        // app that Windows can start perfectly well became unlaunchable.
        var resolver = new ThrowingResolver();
        var runner = new RecordingProcessRunner();
        var launcher = new AppLauncher(resolver, runner, new RecordingLogger());

        var result = launcher.Launch(new KidAppDefinition
        {
            Id = "calc",
            IsEnabled = true,
            LaunchKind = ApplicationLaunchKind.PackagedApp,
            ExecutablePath = CalculatorAumid
        });

        Assert.Equal(LaunchStatus.Success, result.Status);
        Assert.False(resolver.WasAsked);
    }

    [Fact]
    public void A_packaged_entry_with_a_broken_identity_does_not_start_anything()
    {
        var runner = new RecordingProcessRunner();
        var launcher = new AppLauncher(new WindowsExecutableResolver(), runner, new RecordingLogger());

        var result = launcher.Launch(new KidAppDefinition
        {
            Id = "bad",
            IsEnabled = true,
            LaunchKind = ApplicationLaunchKind.PackagedApp,
            ExecutablePath = @"C:\Windows\System32\cmd.exe"
        });

        Assert.NotEqual(LaunchStatus.Success, result.Status);
        Assert.Null(runner.LastFileName);
    }

    private sealed class ThrowingResolver : IExecutableResolver
    {
        public bool WasAsked { get; private set; }

        public ExecutableResolution Resolve(string? command)
        {
            WasAsked = true;
            throw new InvalidOperationException("A packaged app must never reach the file resolver.");
        }
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public string? LastFileName { get; private set; }

        public bool LastUsedShell { get; private set; }

        public void Start(string fileName, string arguments, bool useShellExecute)
        {
            LastFileName = fileName;
            LastUsedShell = useShellExecute;
        }
    }
}
