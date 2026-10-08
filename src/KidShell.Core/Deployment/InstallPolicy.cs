using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidShell.Core.Deployment;

/// <summary>Why an install is not allowed to proceed.</summary>
public enum InstallRefusal
{
    None = 0,

    /// <summary>No manifest, or one that does not parse.</summary>
    ManifestMissing = 1,

    /// <summary>The manifest is structurally wrong or unsafe.</summary>
    ManifestInvalid = 2,

    /// <summary>The bundle does not contain everything a device install needs.</summary>
    ComponentsMissing = 3,

    /// <summary>A file's digest or length did not match the manifest.</summary>
    IntegrityFailed = 4,

    /// <summary>Built from a working tree with uncommitted changes.</summary>
    DirtyBuild = 5,

    /// <summary>Unsigned, and this is a production install path.</summary>
    UnsignedInProduction = 6,

    /// <summary>The bundle is for another architecture.</summary>
    ArchitectureMismatch = 7,

    /// <summary>The same version is already installed.</summary>
    SameVersionInstalled = 8,

    /// <summary>A newer version is already installed.</summary>
    DowngradeRefused = 9,

    /// <summary>The same version from a different commit is installed.</summary>
    SameVersionDifferentCommit = 10,

    /// <summary>The bundle's channel is not the one this install path serves.</summary>
    ChannelMismatch = 11
}

/// <summary>What the installer is about to do.</summary>
public enum InstallAction
{
    /// <summary>Nothing is installed yet.</summary>
    FreshInstall = 0,

    /// <summary>An older version is installed and is being replaced.</summary>
    Upgrade = 1,

    /// <summary>The same bundle is being laid down again on purpose.</summary>
    Repair = 2,

    /// <summary>Nothing will happen.</summary>
    Refused = 3
}

/// <summary>Whether an install may proceed, what it would be, and why not.</summary>
public sealed record InstallDecision(
    InstallAction Action,
    IReadOnlyList<InstallRefusal> Refusals,
    IReadOnlyList<string> Explanations)
{
    public bool Allowed => Action != InstallAction.Refused;

    public static InstallDecision Refuse(InstallRefusal refusal, string explanation) =>
        new(InstallAction.Refused, [refusal], [explanation]);
}

/// <summary>
/// The record of what is installed, written on a real Apply run.
///
/// The uninstaller reads it rather than guessing, which is the difference
/// between removing what this product put there and removing a directory.
/// </summary>
public sealed record InstallReceipt
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Version { get; init; } = string.Empty;

    public string Prerelease { get; init; } = string.Empty;

    public string GitSha { get; init; } = string.Empty;

    public string Architecture { get; init; } = string.Empty;

    public string InstalledUtc { get; init; } = string.Empty;

    public bool Signed { get; init; }

    public ReleaseChannel Channel { get; init; } = ReleaseChannel.Development;

    public string PackageIdentity { get; init; } = string.Empty;

    public string InstallRoot { get; init; } = string.Empty;

    /// <summary>Which components were laid down.</summary>
    public IReadOnlyList<string> Components { get; init; } = [];

    /// <summary>
    /// Every installed file and its digest, relative to the install root.
    ///
    /// The uninstaller removes exactly these. A file under the install root
    /// that is not in this list is REPORTED and left alone - something else
    /// put it there, and deleting it would be this tool guessing.
    /// </summary>
    public IReadOnlyDictionary<string, string> Files { get; init; }
        = new Dictionary<string, string>();

    /// <summary>What was installed before, if anything. For a rollback or a report.</summary>
    public string PreviousVersion { get; init; } = string.Empty;

    public string PreviousGitSha { get; init; } = string.Empty;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static InstallReceipt? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var receipt = JsonSerializer.Deserialize<InstallReceipt>(json, Options);

            return receipt?.SchemaVersion > CurrentSchemaVersion ? null : receipt;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [JsonIgnore]
    public string DisplayVersion =>
        string.IsNullOrWhiteSpace(Prerelease) ? Version : $"{Version}-{Prerelease}";

    /// <summary>
    /// A receipt for what a manifest is about to install.
    ///
    /// Deliberately takes the file list from the caller rather than the
    /// manifest: the caller is the step that actually wrote them, so the
    /// receipt records what happened rather than what was intended.
    /// </summary>
    public static InstallReceipt For(
        ReleaseManifest manifest,
        string installRoot,
        IReadOnlyDictionary<string, string> writtenFiles,
        DateTimeOffset whenUtc,
        InstallReceipt? previous = null) => new()
        {
            Version = manifest.Version,
            Prerelease = manifest.Prerelease,
            GitSha = manifest.GitSha,
            Architecture = manifest.Architecture,
            InstalledUtc = whenUtc.ToUniversalTime().ToString("u"),
            Signed = manifest.Signed,
            Channel = manifest.Channel,
            PackageIdentity = manifest.PackageIdentity,
            InstallRoot = installRoot,
            Components = [.. manifest.Components.Select(c => c.Component.ToString())],
            Files = writtenFiles,
            PreviousVersion = previous?.DisplayVersion ?? string.Empty,
            PreviousGitSha = previous?.GitSha ?? string.Empty
        };
}

/// <summary>
/// Whether a bundle may be installed over what is already there.
///
/// WHY EVERY ANSWER HERE IS "NO" BY DEFAULT
/// ----------------------------------------
/// This is lab tooling for a device whose whole purpose is to produce a
/// trustworthy answer about a security design. An install that quietly put the
/// wrong binaries down would not break the device - it would produce a
/// validation report about code nobody shipped, which is worse, because
/// somebody would believe it.
///
/// So the rules refuse anything ambiguous. The same version from a different
/// commit is the sharpest case: semantically it is "the same build", and on a
/// release-candidate branch it is routinely a different one.
/// </summary>
public static class InstallPolicy
{
    /// <summary>
    /// Decides.
    ///
    /// <paramref name="production"/> is the install path's own nature, not
    /// something the bundle gets to claim: a lab installer passes false and a
    /// production installer passes true, and an unsigned bundle is refused
    /// outright on the latter.
    /// </summary>
    public static InstallDecision Decide(
        ReleaseManifest? manifest,
        InstallReceipt? installed,
        string architecture,
        bool production,
        bool repairRequested = false)
    {
        if (manifest is null)
        {
            return InstallDecision.Refuse(InstallRefusal.ManifestMissing,
                "There is no release manifest, so nothing is known about this bundle.");
        }

        var refusals = new List<InstallRefusal>();
        var explanations = new List<string>();

        void Refuse(InstallRefusal refusal, string explanation)
        {
            refusals.Add(refusal);
            explanations.Add(explanation);
        }

        var problems = manifest.Problems();

        if (problems.Count > 0)
        {
            Refuse(InstallRefusal.ManifestInvalid,
                $"The manifest is not usable: {string.Join(" ", problems)}");
        }

        var missing = manifest.MissingComponents();

        if (missing.Count > 0)
        {
            // The defect this catches for real: a bundle that shipped with no
            // SecurityHost in it at all, because the bundler copied from a
            // folder that did not exist and did not mind.
            Refuse(InstallRefusal.ComponentsMissing,
                $"The bundle is missing: {string.Join(", ", missing)}.");
        }

        if (manifest.Dirty)
        {
            Refuse(InstallRefusal.DirtyBuild,
                "The bundle was built from a working tree with uncommitted changes, " +
                "so which code it contains has no answer.");
        }

        if (production && !manifest.Signed)
        {
            Refuse(InstallRefusal.UnsignedInProduction,
                "An unsigned bundle cannot be installed by a production install path.");
        }

        if (production && manifest.Channel != ReleaseChannel.Production)
        {
            Refuse(InstallRefusal.ChannelMismatch,
                $"The bundle is channel {manifest.Channel} and this is a production install.");
        }

        if (!production && manifest.Channel == ReleaseChannel.Production)
        {
            // Not a safety problem, and still a refusal: a production bundle
            // put on a lab device by the lab installer would be installed
            // under lab rules and recorded as a lab install.
            Refuse(InstallRefusal.ChannelMismatch,
                "The bundle is a production release. Install it with the production path, not the lab one.");
        }

        if (!string.IsNullOrWhiteSpace(architecture) &&
            !string.Equals(manifest.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
        {
            Refuse(InstallRefusal.ArchitectureMismatch,
                $"The bundle is {manifest.Architecture} and this machine is {architecture}.");
        }

        // ------------------------------------------------- what is installed

        var action = InstallAction.FreshInstall;

        if (installed is not null)
        {
            var comparison = CompareVersions(manifest.Version, installed.Version);

            if (comparison < 0)
            {
                Refuse(InstallRefusal.DowngradeRefused,
                    $"Version {installed.DisplayVersion} is installed and this bundle is {manifest.DisplayVersion}.");
            }
            else if (comparison > 0)
            {
                action = InstallAction.Upgrade;
            }
            else
            {
                // Same version. Whether that is a repair or a mistake depends
                // on the commit.
                var sameCommit = string.Equals(
                    manifest.GitSha, installed.GitSha, StringComparison.OrdinalIgnoreCase);

                if (!repairRequested)
                {
                    Refuse(
                        sameCommit
                            ? InstallRefusal.SameVersionInstalled
                            : InstallRefusal.SameVersionDifferentCommit,
                        sameCommit
                            ? $"Version {installed.DisplayVersion} from the same commit is already installed. Pass -Repair to lay it down again."
                            : $"Version {installed.DisplayVersion} is installed from commit {Short(installed.GitSha)}, and this bundle is the same version from {Short(manifest.GitSha)}. Pass -Repair if that is deliberate.");
                }
                else
                {
                    action = InstallAction.Repair;
                }
            }
        }

        if (refusals.Count > 0)
        {
            return new InstallDecision(InstallAction.Refused, refusals, explanations);
        }

        explanations.Add(action switch
        {
            InstallAction.FreshInstall => $"Nothing is installed. {manifest.DisplayVersion} will be installed fresh.",
            InstallAction.Upgrade => $"{installed!.DisplayVersion} will be upgraded to {manifest.DisplayVersion}.",
            _ => $"{manifest.DisplayVersion} will be laid down again as a repair."
        });

        if (!manifest.Signed)
        {
            explanations.Add(
                "THIS BUNDLE IS UNSIGNED. It is a dedicated-lab build: automatic updates stay off, " +
                "and nothing about it may be presented as a release.");
        }
        else if (manifest.Channel == ReleaseChannel.DedicatedLabSigned)
        {
            explanations.Add(
                "THIS BUNDLE IS LAB-SIGNED. The signature exists only so Windows can deploy the real package " +
                "on a dedicated test device. It is not production signing and nothing about it may be presented as a release.");
        }

        return new InstallDecision(action, [], explanations);
    }

    private static string Short(string sha) =>
        sha.Length >= 12 ? sha[..12] : sha;

    /// <summary>
    /// Compares two dotted versions numerically.
    ///
    /// Numerically and not as strings, because "1.0.10" sorts before "1.0.9"
    /// as text and that would turn an upgrade into a refused downgrade exactly
    /// once, on the tenth patch, long after anybody was still watching.
    /// </summary>
    public static int CompareVersions(string? left, string? right)
    {
        var a = Parts(left);
        var b = Parts(right);

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;

            if (x != y)
            {
                return x < y ? -1 : 1;
            }
        }

        return 0;
    }

    private static int[] Parts(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? []
            : [.. version.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0)];
}
