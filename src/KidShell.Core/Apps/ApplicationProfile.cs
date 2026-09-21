namespace KidShell.Core.Apps;

/// <summary>
/// How much KidShell knows about an application's escape surfaces.
///
/// Deliberately not a safety score. An unreviewed application is not
/// "dangerous" — it is unreviewed, and the UI says exactly that.
/// </summary>
public enum ProfileReviewStatus
{
    /// <summary>Nobody has looked at this program's behaviour yet.</summary>
    NotReviewed = 0,

    /// <summary>Reviewed, and its escape surfaces are understood and listed.</summary>
    Reviewed = 1,

    /// <summary>
    /// Reviewed, and it has an unresolved way out — an unrestricted file
    /// picker, a shell verb, an in-app browser. Parent Mode shows this.
    /// </summary>
    RequiresReview = 2
}

/// <summary>A way a program can reach beyond itself.</summary>
public enum EscapeSurface
{
    /// <summary>A standard Open dialog, which is also a file browser.</summary>
    FileOpenDialog = 0,

    /// <summary>A Save As dialog, likewise.</summary>
    FileSaveDialog = 1,

    /// <summary>"Open containing folder" and similar, which start Explorer.</summary>
    OpenContainingFolder = 2,

    /// <summary>Can hand a path or URL to the shell.</summary>
    ShellExecute = 3,

    /// <summary>Opens links in a browser.</summary>
    ExternalLinks = 4,

    /// <summary>Has an embedded browser or web view.</summary>
    EmbeddedBrowser = 5,

    /// <summary>Downloads files.</summary>
    Downloads = 6,

    /// <summary>Starts another program, e.g. a launcher or an updater.</summary>
    ChildProcess = 7,

    /// <summary>Registers or invokes a custom URI scheme.</summary>
    CustomProtocol = 8
}

/// <summary>
/// What KidShell knows about one program.
///
/// Profiles exist because "allow Paint" is not a complete statement. Paint has
/// an Open dialog, which is a file browser; a game launcher starts a second
/// process that is the thing the child actually uses. Application control that
/// ignores this either blocks the program or lets the child out through it.
///
/// Nothing here is invented. A profile records paths and behaviour that were
/// observed or documented; where a value is unknown it is absent, and
/// discovery fills it from the machine.
/// </summary>
public sealed record ApplicationProfile
{
    public required string Id { get; init; }

    /// <summary>Parent-facing name.</summary>
    public required string DisplayName { get; init; }

    public required ApplicationKind Kind { get; init; }

    /// <summary>
    /// Executable file names this program is known by, without directories.
    /// Used to match a discovered application to this profile; paths vary by
    /// machine and are never assumed.
    /// </summary>
    public IReadOnlyList<string> ExecutableNames { get; init; } = [];

    /// <summary>AUMIDs for packaged apps, where stable and documented.</summary>
    public IReadOnlyList<string> Aumids { get; init; } = [];

    /// <summary>
    /// Processes this program legitimately starts and that application control
    /// would also have to allow. Empty means "none known".
    /// </summary>
    public IReadOnlyList<string> ChildProcesses { get; init; } = [];

    /// <summary>
    /// Updater processes. Listed separately because a parent may reasonably
    /// want the program allowed and its updater not.
    /// </summary>
    public IReadOnlyList<string> UpdaterProcesses { get; init; } = [];

    /// <summary>
    /// For a launcher, the process the child ends up using. Allowing the
    /// launcher without this would let it start and then fail.
    /// </summary>
    public string? LaunchedProcess { get; init; }

    /// <summary>Protocols the program registers or relies on.</summary>
    public IReadOnlyList<string> Protocols { get; init; } = [];

    /// <summary>Ways this program can reach beyond itself.</summary>
    public IReadOnlyList<EscapeSurface> EscapeSurfaces { get; init; } = [];

    public ProfileReviewStatus ReviewStatus { get; init; } = ProfileReviewStatus.NotReviewed;

    /// <summary>Plain-language note for the parent, in Swedish.</summary>
    public string SecurityNote { get; init; } = string.Empty;

    /// <summary>Suggested KidShell icon key.</summary>
    public string SuggestedIcon { get; init; } = string.Empty;

    /// <summary>Suggested child-facing category.</summary>
    public string SuggestedCategory { get; init; } = string.Empty;

    /// <summary>
    /// Whether this program can reach the file system freely. Drives the
    /// "Requires review" badge in Parent Mode.
    /// </summary>
    public bool HasUnrestrictedFileAccess =>
        EscapeSurfaces.Contains(EscapeSurface.FileOpenDialog) ||
        EscapeSurfaces.Contains(EscapeSurface.FileSaveDialog) ||
        EscapeSurfaces.Contains(EscapeSurface.OpenContainingFolder);

    /// <summary>Every process name application control would need to allow.</summary>
    public IReadOnlyList<string> AllRequiredProcesses =>
    [
        .. ExecutableNames
            .Concat(ChildProcesses)
            .Concat(LaunchedProcess is null ? [] : new[] { LaunchedProcess })
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];
}
