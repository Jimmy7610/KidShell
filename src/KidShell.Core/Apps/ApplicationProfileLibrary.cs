namespace KidShell.Core.Apps;

public interface IApplicationProfileLibrary
{
    IReadOnlyList<ApplicationProfile> Profiles { get; }

    ApplicationProfile? FindById(string id);

    /// <summary>Matches a discovered application to a known profile, or null.</summary>
    ApplicationProfile? Match(DiscoveredApplication application);
}

/// <summary>
/// The profiles KidShell ships with.
///
/// Two rules govern what goes in here:
///
///  * **No invented paths.** Profiles carry executable *file names*, never
///    absolute paths, because those vary by machine, architecture and Store
///    version. Discovery supplies the real path.
///  * **No unearned assurances.** A profile is marked Reviewed only for a
///    program whose behaviour was actually established. Everything else is
///    NotReviewed, and a program with a known way out is RequiresReview even
///    when it is a Microsoft one — Paint's Open dialog is a file browser
///    whoever wrote it.
/// </summary>
public sealed class ApplicationProfileLibrary : IApplicationProfileLibrary
{
    public static ApplicationProfileLibrary Default { get; } = new();

    public IReadOnlyList<ApplicationProfile> Profiles { get; } = BuildProfiles();

    public ApplicationProfile? FindById(string id) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public ApplicationProfile? Match(DiscoveredApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        // AUMID is exact where present, so try it first.
        if (!string.IsNullOrWhiteSpace(application.Aumid))
        {
            var byAumid = Profiles.FirstOrDefault(p =>
                p.Aumids.Any(a => string.Equals(a, application.Aumid, StringComparison.OrdinalIgnoreCase)));

            if (byAumid is not null)
            {
                return byAumid;
            }
        }

        var fileName = SafeFileName(application.ExecutablePath);

        if (fileName.Length == 0)
        {
            return null;
        }

        return Profiles.FirstOrDefault(p =>
            p.ExecutableNames.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)));
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path.Trim().Trim('"'));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<ApplicationProfile> BuildProfiles() =>
    [
        // ---------------------------------------------------- Calculator
        new ApplicationProfile
        {
            Id = "windows-calculator",
            DisplayName = "Miniräknare",
            Kind = ApplicationKind.Packaged,
            ExecutableNames = ["calc.exe", "CalculatorApp.exe", "Calculator.exe"],
            Aumids = ["Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"],
            ReviewStatus = ProfileReviewStatus.Reviewed,

            // Genuinely contained: no file dialogs, no links, no child
            // processes. The safest thing KidShell can offer.
            EscapeSurfaces = [],
            SecurityNote = "Inga kända vägar ut. Miniräknaren kan varken öppna filer eller länkar.",
            SuggestedIcon = "calculator",
            SuggestedCategory = "Lärande"
        },

        // ---------------------------------------------------- Paint
        new ApplicationProfile
        {
            Id = "windows-paint",
            DisplayName = "Paint",
            Kind = ApplicationKind.Packaged,
            ExecutableNames = ["mspaint.exe", "PaintStudio.View.exe", "PaintApp.exe"],

            // Verified against a real machine. Note that
            // Microsoft.MSPaint_8wekyb3d8bbwe is Paint 3D, a different
            // application - matching on a "Paint" substring gets it wrong.
            Aumids = ["Microsoft.Paint_8wekyb3d8bbwe!App"],
            ReviewStatus = ProfileReviewStatus.RequiresReview,

            // Open and Save As are full file browsers. That is not a flaw in
            // Paint, but it is a way out of the child's world, and a parent
            // deserves to know before allowing it.
            EscapeSurfaces =
            [
                EscapeSurface.FileOpenDialog,
                EscapeSurface.FileSaveDialog
            ],
            SecurityNote =
                "Paints Öppna- och Spara som-rutor är fullständiga filbläddrare. " +
                "Barnet kan nå andra filer den vägen tills appkontroll är på plats.",
            SuggestedIcon = "paint",
            SuggestedCategory = "Skapa"
        },

        // ---------------------------------------------------- Minecraft
        new ApplicationProfile
        {
            Id = "minecraft-launcher",
            DisplayName = "Minecraft Launcher",
            Kind = ApplicationKind.Launcher,
            ExecutableNames = ["MinecraftLauncher.exe", "Minecraft.exe"],

            // The launcher starts the game, which is what the child uses.
            // Allowing only the launcher would let it start and then fail.
            LaunchedProcess = "javaw.exe",
            ChildProcesses = ["javaw.exe", "java.exe"],
            UpdaterProcesses = ["MinecraftLauncher.exe"],
            ReviewStatus = ProfileReviewStatus.RequiresReview,
            EscapeSurfaces =
            [
                EscapeSurface.ChildProcess,
                EscapeSurface.ExternalLinks,
                EscapeSurface.Downloads,
                EscapeSurface.EmbeddedBrowser
            ],
            SecurityNote =
                "Startar spelet som en egen process och kan hämta uppdateringar. " +
                "Inloggningen sker i en inbyggd webbvy.",
            SuggestedIcon = "blocks",
            SuggestedCategory = "Spel"
        },

        // ---------------------------------------------------- VLC
        new ApplicationProfile
        {
            Id = "vlc",
            DisplayName = "VLC",
            Kind = ApplicationKind.Win32,
            ExecutableNames = ["vlc.exe"],
            Protocols = ["vlc"],
            ReviewStatus = ProfileReviewStatus.RequiresReview,
            EscapeSurfaces =
            [
                EscapeSurface.FileOpenDialog,
                EscapeSurface.ExternalLinks,
                EscapeSurface.CustomProtocol
            ],
            SecurityNote =
                "Kan öppna filer och nätverksströmmar, och registrerar ett eget protokoll.",
            SuggestedIcon = "video",
            SuggestedCategory = "Film"
        },

        // ---------------------------------------------------- Scratch
        new ApplicationProfile
        {
            Id = "scratch-desktop",
            DisplayName = "Scratch",
            Kind = ApplicationKind.Win32,
            ExecutableNames = ["Scratch 3.exe", "Scratch Desktop.exe", "Scratch.exe"],
            ReviewStatus = ProfileReviewStatus.RequiresReview,
            EscapeSurfaces =
            [
                EscapeSurface.FileOpenDialog,
                EscapeSurface.FileSaveDialog,
                EscapeSurface.EmbeddedBrowser
            ],
            SecurityNote =
                "Sparar och öppnar projekt via vanliga fildialoger och bygger på en webbvy.",
            SuggestedIcon = "learn",
            SuggestedCategory = "Skapa"
        },

        // ---------------------------------------------------- Browsers
        new ApplicationProfile
        {
            Id = "microsoft-edge",
            DisplayName = "Microsoft Edge",
            Kind = ApplicationKind.Win32,
            ExecutableNames = ["msedge.exe"],
            ChildProcesses = ["msedge.exe", "msedgewebview2.exe"],
            UpdaterProcesses = ["MicrosoftEdgeUpdate.exe"],
            Protocols = ["http", "https", "microsoft-edge"],
            ReviewStatus = ProfileReviewStatus.RequiresReview,
            EscapeSurfaces =
            [
                EscapeSurface.EmbeddedBrowser,
                EscapeSurface.Downloads,
                EscapeSurface.FileOpenDialog,
                EscapeSurface.ExternalLinks,
                EscapeSurface.CustomProtocol,
                EscapeSurface.ChildProcess
            ],

            // A browser is the whole internet. Nothing about allowing one is
            // contained, and the Webb page exists precisely to manage that.
            SecurityNote =
                "En webbläsare ger tillgång till hela internet, filhämtning och andra program " +
                "via länkar. Använd Webb-sidan för att begränsa vad som får öppnas.",
            SuggestedIcon = "browser",
            SuggestedCategory = "Webb"
        }
    ];
}
