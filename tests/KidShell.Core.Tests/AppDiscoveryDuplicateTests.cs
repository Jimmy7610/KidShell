using KidShell.Core.Apps;
using KidShell.Core.Configuration;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// Whether an application the parent is browsing is already in the child's
/// grid.
///
/// The failure mode this prevents is two cards that start the same program,
/// which looks like a bug and is one. The rule is that identity is the
/// executable or the AUMID, never the display name - a parent who renamed
/// Paint to "Rita" has still added Paint.
/// </summary>
public class AppDiscoveryDuplicateTests
{
    private static DiscoveredApplication Win32(string path, string name = "Paint") => new()
    {
        Key = path,
        DisplayName = name,
        Kind = ApplicationKind.Win32,
        ExecutablePath = path,
        TargetExists = true
    };

    private static DiscoveredApplication Packaged(string aumid, string name = "Paint") => new()
    {
        Key = aumid,
        DisplayName = name,
        Kind = ApplicationKind.Packaged,
        Aumid = aumid,
        TargetExists = true
    };

    private static KidAppDefinition Added(string target, string display = "Rita") => new()
    {
        Id = "a",
        DisplayName = display,
        ExecutablePath = target
    };

    [Fact]
    public void The_same_executable_is_recognised_however_the_parent_renamed_it()
    {
        var existing = new[] { Added(@"C:\Windows\System32\mspaint.exe", display: "Rita") };

        Assert.True(ApplicationCatalog.IsAlreadyAdded(
            Win32(@"C:\Windows\System32\mspaint.exe"), existing));
    }

    [Theory]
    [InlineData(@"c:\windows\system32\mspaint.exe")]
    [InlineData(@"C:\WINDOWS\SYSTEM32\MSPAINT.EXE")]
    [InlineData(@"C:\Windows\System32\mspaint.exe  ")]
    public void Path_comparison_ignores_case_and_surrounding_space(string variant)
    {
        var existing = new[] { Added(@"C:\Windows\System32\mspaint.exe") };

        Assert.True(ApplicationCatalog.IsAlreadyAdded(Win32(variant), existing));
    }

    [Fact]
    public void A_different_program_is_not_a_duplicate()
    {
        var existing = new[] { Added(@"C:\Windows\System32\mspaint.exe") };

        Assert.False(ApplicationCatalog.IsAlreadyAdded(
            Win32(@"C:\Windows\System32\calc.exe", "Kalkylator"), existing));
    }

    [Fact]
    public void A_packaged_app_is_matched_on_its_AUMID()
    {
        var existing = new[] { Added("Microsoft.Paint_8wekyb3d8bbwe!App") };

        Assert.True(ApplicationCatalog.IsAlreadyAdded(
            Packaged("Microsoft.Paint_8wekyb3d8bbwe!App"), existing));
    }

    [Fact]
    public void A_packaged_app_with_a_different_AUMID_is_not_a_duplicate()
    {
        var existing = new[] { Added("Microsoft.Paint_8wekyb3d8bbwe!App") };

        Assert.False(ApplicationCatalog.IsAlreadyAdded(
            Packaged("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"), existing));
    }

    [Fact]
    public void A_packaged_app_is_never_matched_by_path_normalisation()
    {
        // An AUMID contains a "!" and no directory separators. Running it
        // through path normalisation would be meaningless, and could produce a
        // false match between two unrelated packages.
        var existing = new[] { Added(@"C:\Windows\System32\mspaint.exe") };

        Assert.False(ApplicationCatalog.IsAlreadyAdded(
            Packaged("Microsoft.Paint_8wekyb3d8bbwe!App"), existing));
    }

    [Fact]
    public void An_app_with_no_program_configured_is_never_a_duplicate()
    {
        // A card with no executable is the "not configured yet" placeholder.
        // Treating it as a match would stop the parent finishing the job.
        var existing = new[] { Added(string.Empty, display: "Rita") };

        Assert.False(ApplicationCatalog.IsAlreadyAdded(
            Win32(@"C:\Windows\System32\mspaint.exe"), existing));
    }

    [Fact]
    public void An_empty_grid_matches_nothing()
    {
        Assert.False(ApplicationCatalog.IsAlreadyAdded(
            Win32(@"C:\Windows\System32\mspaint.exe"), []));
    }

    [Fact]
    public void Matching_ignores_the_display_name_entirely()
    {
        // Two cards with the same name and different programs are legitimate;
        // the same program twice is not.
        var existing = new[] { Added(@"C:\Games\game.exe", display: "Paint") };

        Assert.False(ApplicationCatalog.IsAlreadyAdded(
            Win32(@"C:\Windows\System32\mspaint.exe", "Paint"), existing));
    }
}
