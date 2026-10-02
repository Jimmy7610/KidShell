using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidShell.Core.Deployment;

/// <summary>
/// What a build is allowed to claim about itself.
///
/// THREE CHANNELS, AND NOTHING BETWEEN THEM
/// ----------------------------------------
/// The point of naming them is that a lab build must be impossible to mistake
/// for a production one. Not hard to mistake - impossible: a different package
/// identity, a different label in every artifact, a receipt that records it,
/// and a production install path that refuses an unsigned bundle outright.
/// </summary>
public enum ReleaseChannel
{
    /// <summary>A developer's own machine. Never leaves it.</summary>
    Development = 0,

    /// <summary>
    /// A bundle for a dedicated validation device, built before a signing
    /// certificate exists.
    ///
    /// Allowed to be unsigned, because the alternative is never validating on
    /// real hardware. Loudly labelled everywhere, with automatic updates and
    /// every production claim disabled.
    /// </summary>
    DedicatedLabUnsigned = 1,

    /// <summary>
    /// A build for other people. Must be signed; an unsigned one cannot be
    /// installed, not merely discouraged.
    /// </summary>
    Production = 2
}

/// <summary>
/// One file in a release bundle.
/// </summary>
/// <param name="Path">
/// Where it goes, relative to the component's directory. Untrusted: it came
/// out of a manifest, and <see cref="InstallationLayout.IsSafeRelativePath"/>
/// is what decides whether it may be written.
/// </param>
/// <param name="Sha256">Lowercase hexadecimal. Verified before the file is copied.</param>
/// <param name="Bytes">The expected length, checked alongside the digest.</param>
public sealed record ReleaseFile(string Path, string Sha256, long Bytes);

/// <summary>One component's payload in a bundle.</summary>
public sealed record ReleaseComponent
{
    public required KidShellComponent Component { get; init; }

    /// <summary>Where this component's files are inside the bundle.</summary>
    public required string BundlePath { get; init; }

    public IReadOnlyList<ReleaseFile> Files { get; init; } = [];
}

/// <summary>
/// The machine-readable description of a release bundle.
///
/// EVERY INSTALL STEP READS THIS RATHER THAN LOOKING AROUND
/// -------------------------------------------------------
/// The installer never enumerates a directory and copies what it finds. It
/// reads the manifest, checks each file's digest, and copies exactly the files
/// the manifest names. A file in the bundle that the manifest does not mention
/// is not installed - it is reported, because something put it there.
///
/// The manifest is itself hash-covered by hashes.sha256, so the document that
/// decides what gets installed cannot be the one thing nobody checked.
/// </summary>
public sealed record ReleaseManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Product { get; init; } = "KidShell";

    /// <summary>The product version, e.g. 1.0.0.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>The four-part MSIX version, e.g. 1.0.0.0.</summary>
    public string PackageVersion { get; init; } = string.Empty;

    /// <summary>rc.1, or empty for a stable release.</summary>
    public string Prerelease { get; init; } = string.Empty;

    /// <summary>The exact commit. A bundle without one cannot be installed.</summary>
    public string GitSha { get; init; } = string.Empty;

    /// <summary>
    /// Whether the working tree had uncommitted changes.
    ///
    /// A dirty bundle is refused by every install path including the lab one.
    /// "Which code is on that device" has to have an answer, and a dirty tree
    /// means it does not.
    /// </summary>
    public bool Dirty { get; init; }

    public string Architecture { get; init; } = string.Empty;

    public string BuildConfiguration { get; init; } = string.Empty;

    public string BuildUtc { get; init; } = string.Empty;

    public bool Signed { get; init; }

    public ReleaseChannel Channel { get; init; } = ReleaseChannel.Development;

    /// <summary>The MSIX package identity this bundle installs under.</summary>
    public string PackageIdentity { get; init; } = string.Empty;

    public IReadOnlyList<ReleaseComponent> Components { get; init; } = [];

    /// <summary>
    /// Where each component is expected to land, as the bundle understood it.
    ///
    /// Recorded so a mismatch between the bundle's expectation and
    /// <see cref="InstallationLayout"/> is a refusal rather than a surprise.
    /// The installer uses the LAYOUT, never this - this is what it checks the
    /// layout against.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExpectedInstallPaths { get; init; }
        = new Dictionary<string, string>();

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ReleaseManifest? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReleaseManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The full version as a human reads it.</summary>
    [JsonIgnore]
    public string DisplayVersion =>
        string.IsNullOrWhiteSpace(Prerelease) ? Version : $"{Version}-{Prerelease}";

    /// <summary>
    /// Everything structurally wrong with this manifest.
    ///
    /// Shape and safety only. Whether the FILES match comes later, and whether
    /// this bundle may replace what is installed is
    /// <see cref="InstallPolicy"/>'s decision.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (SchemaVersion > CurrentSchemaVersion)
        {
            problems.Add($"The manifest is schema {SchemaVersion} and this build understands {CurrentSchemaVersion}.");
        }

        if (!string.Equals(Product, "KidShell", StringComparison.Ordinal))
        {
            problems.Add($"The manifest is for '{Product}', not KidShell.");
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            problems.Add("The manifest has no version.");
        }

        if (string.IsNullOrWhiteSpace(GitSha))
        {
            // "Which code is on that device" must have an answer.
            problems.Add("The manifest has no Git SHA, so what is in this bundle cannot be established.");
        }
        else if (GitSha.Length != 40 || !GitSha.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')))
        {
            problems.Add($"The Git SHA '{GitSha}' is not a full commit hash.");
        }

        if (string.IsNullOrWhiteSpace(Architecture))
        {
            problems.Add("The manifest does not say which architecture it is for.");
        }

        if (Components.Count == 0)
        {
            problems.Add("The manifest lists no components.");
        }

        foreach (var component in Components)
        {
            if (!Enum.IsDefined(component.Component))
            {
                // A component this build has never heard of is not something to
                // install into a guessed folder.
                problems.Add($"Unknown component '{component.Component}'.");
                continue;
            }

            if (!InstallationLayout.IsSafeRelativePath(component.BundlePath))
            {
                problems.Add($"Component {component.Component} has an unsafe bundle path '{component.BundlePath}'.");
            }

            if (component.Files.Count == 0)
            {
                problems.Add($"Component {component.Component} lists no files.");
            }

            foreach (var file in component.Files)
            {
                if (!InstallationLayout.IsSafeRelativePath(file.Path))
                {
                    problems.Add($"{component.Component}: unsafe file path '{file.Path}'.");
                }

                if (file.Sha256 is not { Length: 64 } ||
                    !file.Sha256.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')))
                {
                    problems.Add($"{component.Component}: '{file.Path}' has no usable SHA-256.");
                }

                if (file.Bytes < 0)
                {
                    problems.Add($"{component.Component}: '{file.Path}' has a negative length.");
                }
            }
        }

        var duplicates = Components
            .GroupBy(c => c.Component)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.ToString())
            .ToList();

        if (duplicates.Count > 0)
        {
            problems.Add($"Components listed more than once: {string.Join(", ", duplicates)}.");
        }

        // The bundle's own expectation about where things go must agree with
        // the layout. A disagreement is how a validation script ends up
        // checking a folder the installer never populated.
        foreach (var (name, expected) in ExpectedInstallPaths)
        {
            if (!Enum.TryParse<KidShellComponent>(name, ignoreCase: true, out var component))
            {
                problems.Add($"ExpectedInstallPaths names an unknown component '{name}'.");
                continue;
            }

            var layout = InstallationLayout.FolderOf(component);

            if (!expected.Replace('/', '\\').TrimEnd('\\').EndsWith(layout, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"The bundle expects {component} at '{expected}', and the layout says '{layout}'.");
            }
        }

        return problems;
    }

    [JsonIgnore]
    public bool IsWellFormed => Problems().Count == 0;

    /// <summary>Which required components this bundle is missing.</summary>
    public IReadOnlyList<KidShellComponent> MissingComponents() =>
    [
        .. InstallationLayout.Required.Where(
            required => !Components.Any(c => c.Component == required))
    ];
}
