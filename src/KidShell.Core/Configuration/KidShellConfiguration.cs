using System.Text.Json.Serialization;
using KidShell.Core.Security;

namespace KidShell.Core.Configuration;

/// <summary>
/// The single persisted configuration document for KidShell. One central model
/// rather than scattered per-page state; the UI binds to view models that are
/// projections of this.
/// </summary>
public sealed class KidShellConfiguration
{
    /// <summary>
    /// Schema version of the persisted document.
    ///
    /// 1 — MVP 0.1. Shipped a placeholder child profile.
    /// 2 — adds ChildProfile.IsOnboardingComplete and the five named themes.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public ChildProfile Child { get; set; } = new();

    public List<KidAppDefinition> Apps { get; set; } = [];

    public ScreenTimeSettings ScreenTime { get; set; } = new();

    public WebSettings Web { get; set; } = new();

    public ParentPinSettings ParentPin { get; set; } = new();

    /// <summary>Apps the child should actually see, in display order.</summary>
    [JsonIgnore]
    public IEnumerable<KidAppDefinition> EnabledApps =>
        Apps.Where(a => a.IsEnabled).OrderBy(a => a.SortOrder);

    /// <summary>
    /// Whether KidShell must run first-run setup before showing Child Mode.
    ///
    /// Both halves matter: the flag says a parent finished the flow, and the
    /// profile check means a document that was written half-way can never
    /// produce a partially configured Child Mode.
    /// </summary>
    [JsonIgnore]
    public bool RequiresOnboarding => !Child.IsOnboardingComplete || !Child.HasRequiredDetails;

    public KidAppDefinition? FindApp(string id) =>
        Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    public KidShellConfiguration Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Child = Child.Clone(),
        Apps = [.. Apps.Select(a => a.Clone())],
        ScreenTime = ScreenTime.Clone(),
        Web = Web.Clone(),
        ParentPin = ParentPin.Clone()
    };

    /// <summary>
    /// The out-of-the-box KidShell setup: an empty child profile awaiting
    /// onboarding, plus the demo app catalogue from the approved design.
    /// </summary>
    public static KidShellConfiguration CreateDefault() => new()
    {
        SchemaVersion = CurrentSchemaVersion,

        // Deliberately blank. A parent names the child during first-run setup.
        Child = new ChildProfile(),

        ScreenTime = new ScreenTimeSettings { IsEnabled = false, WeekdayMinutes = 60, WeekendMinutes = 120 },
        Web = new WebSettings { Mode = WebMode.NoBrowser, AllowedDomains = ["svt.se", "nasa.gov", "wikipedia.org"] },
        Apps = CreateDefaultApps()
    };

    /// <summary>The eight child cards from the approved design, plus two parent-only rows.</summary>
    public static List<KidAppDefinition> CreateDefaultApps() =>
    [
        new KidAppDefinition
        {
            Id = "paint", DisplayName = "Rita", ProgramName = "Paint", Description = "Skapa och rita", Category = "Skapa",
            Icon = IconKeys.Paint, AccentStyle = AccentStyle.Rose, IsEnabled = true,
            ExecutablePath = "mspaint.exe", SortOrder = 0
        },
        new KidAppDefinition
        {
            Id = "games", DisplayName = "Spel", ProgramName = "Spelsamling", Description = "Lek och pussel", Category = "Spel",
            Icon = IconKeys.Games, AccentStyle = AccentStyle.Grass, IsEnabled = true,
            ExecutablePath = string.Empty, SortOrder = 1
        },
        new KidAppDefinition
        {
            Id = "learn", DisplayName = "Lära", ProgramName = "Lärportal", Description = "Upptäck nya saker", Category = "Lärande",
            Icon = IconKeys.Learn, AccentStyle = AccentStyle.Sun, IsEnabled = true,
            ExecutablePath = string.Empty, SortOrder = 2
        },
        new KidAppDefinition
        {
            Id = "calculator", DisplayName = "Miniräknare", Description = "Matematik", Category = "Lärande",
            Icon = IconKeys.Calculator, AccentStyle = AccentStyle.Sky, IsEnabled = true,
            ExecutablePath = "calc.exe", SortOrder = 3
        },
        new KidAppDefinition
        {
            Id = "music", DisplayName = "Musik", ProgramName = "Musikspelare", Description = "Lyssna och sjung", Category = "Musik",
            Icon = IconKeys.Music, AccentStyle = AccentStyle.Grape, IsEnabled = true,
            ExecutablePath = string.Empty, SortOrder = 4
        },
        new KidAppDefinition
        {
            Id = "reading", DisplayName = "Läsa", ProgramName = "Läsprogram", Description = "Böcker och lärande", Category = "Lärande",
            Icon = IconKeys.Reading, AccentStyle = AccentStyle.Coral, IsEnabled = true,
            ExecutablePath = string.Empty, SortOrder = 5
        },
        new KidAppDefinition
        {
            Id = "video", DisplayName = "Film", ProgramName = "Filmspelare", Description = "Video", Category = "Film",
            Icon = IconKeys.Video, AccentStyle = AccentStyle.Teal, IsEnabled = true,
            ExecutablePath = string.Empty, SortOrder = 6
        },
        new KidAppDefinition
        {
            Id = "minecraft", DisplayName = "Minecraft", Description = "Spel", Category = "Spel",
            Icon = IconKeys.Blocks, AccentStyle = AccentStyle.Leaf, IsEnabled = true,
            ExecutablePath = string.Empty, SortOrder = 7
        },
        new KidAppDefinition
        {
            Id = "vlc", DisplayName = "VLC", Description = "Video", Category = "Film",
            Icon = IconKeys.Video, AccentStyle = AccentStyle.Sun, IsEnabled = false,
            ExecutablePath = string.Empty, SortOrder = 8
        },
        new KidAppDefinition
        {
            Id = "browser", DisplayName = "Webbläsare", Description = "Internet", Category = "Webb",
            Icon = IconKeys.Browser, AccentStyle = AccentStyle.Sky, IsEnabled = false,
            ExecutablePath = string.Empty, SortOrder = 9
        }
    ];
}
