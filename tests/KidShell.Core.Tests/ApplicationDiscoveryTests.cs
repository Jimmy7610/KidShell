using KidShell.Core.Apps;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// Catalogue merging and profile matching.
///
/// All of it runs from fixture data. The scanners that read the real machine
/// live in the App layer; what is tested here is the part that decides what a
/// parent finally sees, which is where the bugs would be.
/// </summary>
public class ApplicationDiscoveryTests
{
    private static DiscoveredApplication App(
        string name,
        string? exe = null,
        string? aumid = null,
        ApplicationKind kind = ApplicationKind.Win32,
        DiscoverySource source = DiscoverySource.StartMenu,
        string publisher = "",
        string icon = "",
        string version = "",
        bool exists = true) => new()
    {
        Key = string.Empty,
        DisplayName = name,
        Kind = aumid is null ? kind : ApplicationKind.Packaged,
        Source = source,
        ExecutablePath = exe ?? string.Empty,
        Aumid = aumid ?? string.Empty,
        Publisher = publisher,
        IconPath = icon,
        Version = version,
        TargetExists = exists
    };

    // ------------------------------------------------ de-duplication

    [Fact]
    public void The_same_program_from_three_scanners_appears_once()
    {
        // The realistic case: a Start Menu shortcut, an uninstall entry and an
        // App Paths entry all describing Notepad++.
        var merged = ApplicationCatalog.Merge(
        [
            App("Notepad++", @"C:\Program Files\Notepad++\notepad++.exe"),
            App("Notepad++ (64-bit x64)", @"C:\Program Files\Notepad++\notepad++.exe",
                source: DiscoverySource.RegistryUninstall, publisher: "Notepad++ Team", version: "8.6"),
            App("notepad++", @"C:\Program Files\Notepad++\notepad++.exe",
                source: DiscoverySource.AppPaths)
        ]);

        Assert.Single(merged);
    }

    [Fact]
    public void Merging_keeps_the_richest_information_from_every_source()
    {
        var merged = ApplicationCatalog.Merge(
        [
            App("VLC", @"C:\Program Files\VideoLAN\VLC\vlc.exe", icon: @"C:\vlc.ico"),
            App("VLC media player", @"C:\Program Files\VideoLAN\VLC\vlc.exe",
                source: DiscoverySource.RegistryUninstall, publisher: "VideoLAN", version: "3.0.20")
        ]);

        var vlc = Assert.Single(merged);

        // Fields are filled from whichever record had them, rather than the
        // loser being thrown away.
        Assert.Equal("VideoLAN", vlc.Publisher);
        Assert.Equal("3.0.20", vlc.Version);
        Assert.Equal(@"C:\vlc.ico", vlc.IconPath);
    }

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", @"c:\program files\app\app.exe")]
    [InlineData(@"""C:\Program Files\App\app.exe""", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:/Program Files/App/app.exe")]
    public void Paths_that_differ_only_cosmetically_are_the_same_program(string a, string b)
    {
        var merged = ApplicationCatalog.Merge([App("App", a), App("App", b)]);

        Assert.Single(merged);
    }

    [Fact]
    public void Packaged_apps_are_keyed_by_aumid_not_by_path()
    {
        // Packaged apps have no useful executable path, so the AUMID has to be
        // the identity - otherwise every one of them collides on "no path".
        var merged = ApplicationCatalog.Merge(
        [
            App("Kalkylatorn", aumid: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
            App("Paint", aumid: "Microsoft.Paint_8wekyb3d8bbwe!App")
        ]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Paint_and_Paint_3D_are_not_merged()
    {
        // Different packages with confusingly similar names. Substring
        // matching on "Paint" gets this wrong; AUMID does not.
        var merged = ApplicationCatalog.Merge(
        [
            App("Paint", aumid: "Microsoft.Paint_8wekyb3d8bbwe!App"),
            App("Paint 3D", aumid: "Microsoft.MSPaint_8wekyb3d8bbwe!Microsoft.MSPaint")
        ]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void An_entry_with_neither_path_nor_aumid_is_kept_under_its_name()
    {
        var merged = ApplicationCatalog.Merge([App("Mystery", exe: null)]);

        Assert.Single(merged);
        Assert.StartsWith("name:", merged[0].Key, StringComparison.Ordinal);
    }

    [Fact]
    public void Nameless_entries_are_dropped()
    {
        var merged = ApplicationCatalog.Merge(
        [
            App("", @"C:\x\y.exe"),
            App("   ", @"C:\x\z.exe")
        ]);

        Assert.Empty(merged);
    }

    [Fact]
    public void Results_are_sorted_for_a_human_reader()
    {
        var merged = ApplicationCatalog.Merge(
        [
            App("Zebra", @"C:\z.exe"),
            App("apple", @"C:\a.exe"),
            App("Mango", @"C:\m.exe")
        ]);

        Assert.Equal(["apple", "Mango", "Zebra"], merged.Select(a => a.DisplayName));
    }

    // ------------------------------------------------ launchability

    [Fact]
    public void A_shortcut_to_a_deleted_program_is_not_launchable()
    {
        var stale = App("Gone", @"C:\nope\gone.exe", exists: false);

        Assert.False(stale.IsLaunchable);
    }

    [Fact]
    public void A_packaged_app_is_launchable_from_its_aumid_alone()
    {
        var packaged = App("Kalkylatorn", aumid: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

        Assert.True(packaged.IsLaunchable);
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", packaged.LaunchTarget);
    }

    [Fact]
    public void A_win32_app_launches_from_its_path()
    {
        var win32 = App("VLC", @"C:\Program Files\VideoLAN\VLC\vlc.exe");

        Assert.Equal(@"C:\Program Files\VideoLAN\VLC\vlc.exe", win32.LaunchTarget);
    }

    // ------------------------------------------------ search

    [Fact]
    public void Search_matches_name_publisher_and_file_name()
    {
        var all = ApplicationCatalog.Merge(
        [
            App("VLC media player", @"C:\Program Files\VideoLAN\VLC\vlc.exe", publisher: "VideoLAN"),
            App("Notepad++", @"C:\Program Files\Notepad++\notepad++.exe", publisher: "Notepad++ Team"),
            App("Kalkylatorn", aumid: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", publisher: "Microsoft")
        ]);

        Assert.Single(ApplicationCatalog.Filter(all, "videolan"));     // publisher
        Assert.Single(ApplicationCatalog.Filter(all, "notepad++.exe")); // file name
        Assert.Single(ApplicationCatalog.Filter(all, "kalkyl"));        // name
        Assert.Equal(3, ApplicationCatalog.Filter(all, "   ").Count);   // blank = everything
    }

    [Fact]
    public void Search_is_case_insensitive()
    {
        var all = ApplicationCatalog.Merge([App("VLC media player", @"C:\vlc.exe")]);

        Assert.Single(ApplicationCatalog.Filter(all, "VLC"));
        Assert.Single(ApplicationCatalog.Filter(all, "vlc"));
    }

    // ------------------------------------------------ profiles

    [Fact]
    public void A_discovered_app_is_matched_to_its_profile_by_aumid()
    {
        var merged = ApplicationCatalog.Merge(
            [App("Kalkylatorn med graffunktion", aumid: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")],
            ApplicationProfileLibrary.Default);

        Assert.Equal("windows-calculator", Assert.Single(merged).ProfileId);
    }

    [Fact]
    public void A_discovered_app_is_matched_by_executable_name()
    {
        // The path varies by machine; the file name does not, which is why
        // profiles carry names rather than invented absolute paths.
        var merged = ApplicationCatalog.Merge(
            [App("VLC", @"D:\Portable\VideoLAN\vlc.exe")],
            ApplicationProfileLibrary.Default);

        Assert.Equal("vlc", Assert.Single(merged).ProfileId);
    }

    [Fact]
    public void An_unknown_program_gets_no_profile()
    {
        var merged = ApplicationCatalog.Merge(
            [App("Some Vendor Tool", @"C:\Vendor\tool.exe")],
            ApplicationProfileLibrary.Default);

        Assert.Null(Assert.Single(merged).ProfileId);
    }

    [Fact]
    public void Calculator_is_the_only_profile_with_no_way_out()
    {
        var calculator = ApplicationProfileLibrary.Default.FindById("windows-calculator")!;

        Assert.Equal(ProfileReviewStatus.Reviewed, calculator.ReviewStatus);
        Assert.Empty(calculator.EscapeSurfaces);
        Assert.False(calculator.HasUnrestrictedFileAccess);
    }

    [Fact]
    public void Paint_is_flagged_because_its_open_dialog_is_a_file_browser()
    {
        var paint = ApplicationProfileLibrary.Default.FindById("windows-paint")!;

        Assert.Equal(ProfileReviewStatus.RequiresReview, paint.ReviewStatus);
        Assert.True(paint.HasUnrestrictedFileAccess);
        Assert.Contains(EscapeSurface.FileOpenDialog, paint.EscapeSurfaces);
    }

    [Fact]
    public void A_launcher_declares_the_process_the_child_actually_uses()
    {
        // Allowing only the launcher would let it start and then fail when it
        // tries to run the game.
        var minecraft = ApplicationProfileLibrary.Default.FindById("minecraft-launcher")!;

        Assert.Equal(ApplicationKind.Launcher, minecraft.Kind);
        Assert.Equal("javaw.exe", minecraft.LaunchedProcess);
        Assert.Contains("javaw.exe", minecraft.AllRequiredProcesses);
        Assert.Contains("MinecraftLauncher.exe", minecraft.AllRequiredProcesses);
    }

    [Fact]
    public void A_browser_profile_does_not_pretend_to_be_contained()
    {
        var edge = ApplicationProfileLibrary.Default.FindById("microsoft-edge")!;

        Assert.Equal(ProfileReviewStatus.RequiresReview, edge.ReviewStatus);
        Assert.Contains(EscapeSurface.Downloads, edge.EscapeSurfaces);
        Assert.Contains(EscapeSurface.EmbeddedBrowser, edge.EscapeSurfaces);
        Assert.NotEmpty(edge.SecurityNote);
    }

    [Fact]
    public void No_profile_carries_an_invented_absolute_path()
    {
        // Profiles must describe file names only. An absolute path here would
        // be a guess about a machine nobody has seen.
        foreach (var profile in ApplicationProfileLibrary.Default.Profiles)
        {
            Assert.All(profile.ExecutableNames, n =>
            {
                Assert.DoesNotContain(@"\", n, StringComparison.Ordinal);
                Assert.DoesNotContain(":", n, StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public void Every_profile_that_can_escape_says_how()
    {
        // A "requires review" badge with no explanation is not useful to a
        // parent, so the note is mandatory whenever the status is raised.
        foreach (var profile in ApplicationProfileLibrary.Default.Profiles
                     .Where(p => p.ReviewStatus == ProfileReviewStatus.RequiresReview))
        {
            Assert.NotEmpty(profile.EscapeSurfaces);
            Assert.False(string.IsNullOrWhiteSpace(profile.SecurityNote));
        }
    }

    [Fact]
    public void Profile_ids_are_unique()
    {
        var ids = ApplicationProfileLibrary.Default.Profiles.Select(p => p.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
