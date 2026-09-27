using KidShell.Core.Runtime;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// EXTERNAL AUDIT: nine Core tests failed on a non-Windows host because
/// Windows paths were being interpreted by the host's own path rules.
///
/// System.IO.Path answers about the machine it is running on.
/// <c>Path.IsPathRooted(@"C:\Program Files\App\app.exe")</c> is true on
/// Windows and FALSE on Linux, where a backslash is an ordinary character in a
/// file name - so the same configuration produced a different application
/// control policy depending on where the build ran. A security policy that
/// depends on the build agent's operating system is not a policy.
///
/// The policy is always about a Windows machine, whatever is generating it.
/// <see cref="WindowsPath"/> encodes those rules directly, so these assertions
/// hold identically on Windows, Linux and macOS - which is the whole point,
/// and why they are written without a single reference to the host.
/// </summary>
public class WindowsPathPortabilityTests
{
    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe")]
    [InlineData(@"c:\app.exe")]
    [InlineData(@"Z:\deep\path\program.exe")]
    [InlineData(@"\\server\share\app.exe")]
    [InlineData(@"%WINDIR%\explorer.exe")]
    [InlineData(@"%SYSTEM32%\userinit.exe")]
    [InlineData(@"%PROGRAMFILES%\App\app.exe")]
    [InlineData(@"  ""C:\Program Files\App\app.exe""  ")]
    public void A_full_Windows_path_is_recognised_anywhere(string path) =>
        Assert.True(WindowsPath.IsFullyQualified(path));

    [Theory]
    [InlineData("calc.exe")]
    [InlineData("app")]
    [InlineData(@"relative\path\app.exe")]
    [InlineData(@"..\app.exe")]
    [InlineData("C:app.exe")]          // drive-relative: resolves against that drive's cwd
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_short_of_a_full_path_is_not(string? path) =>
        Assert.False(WindowsPath.IsFullyQualified(path));

    /// <summary>
    /// Path.GetFileName returns the WHOLE string for this on Linux, because it
    /// sees one long file name with backslashes in it.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", "app.exe")]
    [InlineData(@"C:/Program Files/App/app.exe", "app.exe")]
    [InlineData(@"%SYSTEM32%\cmd.exe", "cmd.exe")]
    [InlineData("app.exe", "app.exe")]
    [InlineData(@"C:\", "")]
    public void The_file_name_is_found_with_Windows_separators(string path, string expected) =>
        Assert.Equal(expected, WindowsPath.FileName(path));

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:\Program Files\App")]
    [InlineData(@"app.exe", "")]
    public void The_directory_is_found_with_Windows_separators(string path, string expected) =>
        Assert.Equal(expected, WindowsPath.DirectoryName(path));

    /// <summary>
    /// Windows compares paths case-insensitively and treats both separators
    /// alike; Linux does neither.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", @"c:\program files\app\APP.EXE")]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:/Program Files/App/app.exe")]
    [InlineData(@"  C:\App\a.exe  ", @"""C:\App\a.exe""")]
    public void Two_spellings_of_one_path_are_equivalent(string left, string right) =>
        Assert.True(WindowsPath.AreEquivalent(left, right));

    [Theory]
    [InlineData(@"C:\App\a.exe", @"C:\App\b.exe")]
    [InlineData(@"C:\App\a.exe", @"D:\App\a.exe")]
    public void Different_paths_are_not_equivalent(string left, string right) =>
        Assert.False(WindowsPath.AreEquivalent(left, right));

    /// <summary>
    /// Upper-casing with the CURRENT culture would make this fail in Turkish,
    /// where "i" does not upper-case to "I". A security comparison that
    /// depends on the user's language fails somewhere, quietly.
    /// </summary>
    [Fact]
    public void Casing_does_not_depend_on_the_machines_language()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("tr-TR");

            Assert.True(WindowsPath.AreEquivalent(
                @"C:\Windows\System32\notepad.exe",
                @"c:\windows\system32\NOTEPAD.EXE"));

            Assert.Equal(@"C:\WINDOWS\SYSTEM32\NOTEPAD.EXE",
                WindowsPath.Canonical(@"C:\Windows\System32\notepad.exe"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// The assertion the audit's nine failures were really about: the same
    /// configuration has to produce the same policy wherever it is built.
    /// </summary>
    [Fact]
    public void A_rooted_program_path_produces_a_rule_on_any_host()
    {
        var config = KidShell.Core.Configuration.KidShellConfiguration.CreateDefault();
        config.Apps =
        [
            new KidShell.Core.Configuration.KidAppDefinition
            {
                Id = "paint",
                DisplayName = "Paint",
                ProgramName = "Paint",
                ExecutablePath = @"C:\Program Files\Paint\mspaint.exe",
                IsEnabled = true
            }
        ];

        var policy = KidShell.Core.Security.AppControl.AppControlPolicyBuilder.Build(
            config,
            KidShell.Core.Apps.ApplicationProfileLibrary.Default,
            @"C:\Program Files\KidShell\KidShell.exe");

        Assert.Contains(policy.ApplicationRules, r =>
            WindowsPath.FileName(r.Value).Equals("mspaint.exe", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(policy.Warnings, w => w.Code == "unresolved-path");
    }

    [Fact]
    public void A_bare_command_name_produces_a_warning_on_any_host()
    {
        var config = KidShell.Core.Configuration.KidShellConfiguration.CreateDefault();
        config.Apps =
        [
            new KidShell.Core.Configuration.KidAppDefinition
            {
                Id = "calc",
                DisplayName = "Kalkylator",
                ProgramName = "Kalkylator",
                ExecutablePath = "calc.exe",
                IsEnabled = true
            }
        ];

        var policy = KidShell.Core.Security.AppControl.AppControlPolicyBuilder.Build(
            config,
            KidShell.Core.Apps.ApplicationProfileLibrary.Default,
            @"C:\Program Files\KidShell\KidShell.exe");

        Assert.Contains(policy.Warnings, w => w.Code == "unresolved-path");
    }

    /// <summary>
    /// The test project targets plain net10.0 rather than a Windows-specific
    /// framework, which is what makes running it elsewhere possible at all.
    /// Stated as a test so that adding a Windows-only dependency to Core is a
    /// decision somebody has to take deliberately.
    /// </summary>
    [Fact]
    public void Core_does_not_depend_on_running_under_Windows()
    {
        var core = typeof(WindowsPath).Assembly;

        Assert.DoesNotContain(
            core.GetReferencedAssemblies(),
            a => a.Name?.Contains("Windows.SDK", StringComparison.OrdinalIgnoreCase) == true
                 || a.Name?.Contains("WinRT", StringComparison.OrdinalIgnoreCase) == true
                 || a.Name?.StartsWith("Microsoft.UI", StringComparison.OrdinalIgnoreCase) == true);
    }
}
