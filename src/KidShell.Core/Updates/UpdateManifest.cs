using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidShell.Core.Updates;

/// <summary>A KidShell version, comparable.</summary>
public readonly record struct KidShellVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<KidShellVersion>
{
    public static bool TryParse(string? text, out KidShellVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().TrimStart('v', 'V');
        var dash = trimmed.IndexOf('-');

        string? preRelease = null;

        if (dash > 0)
        {
            preRelease = trimmed[(dash + 1)..];
            trimmed = trimmed[..dash];
        }

        var parts = trimmed.Split('.');

        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            return false;
        }

        var patch = 0;

        if (parts.Length >= 3 && !int.TryParse(parts[2], out patch))
        {
            return false;
        }

        version = new KidShellVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(KidShellVersion other)
    {
        var byMajor = Major.CompareTo(other.Major);
        if (byMajor != 0) return byMajor;

        var byMinor = Minor.CompareTo(other.Minor);
        if (byMinor != 0) return byMinor;

        var byPatch = Patch.CompareTo(other.Patch);
        if (byPatch != 0) return byPatch;

        // A release outranks a pre-release of the same numbers: 1.0.0 is newer
        // than 1.0.0-rc1, which is the opposite of a plain string comparison.
        return (PreRelease, other.PreRelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            var (a, b) => string.CompareOrdinal(a, b)
        };
    }

    public static bool operator >(KidShellVersion a, KidShellVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(KidShellVersion a, KidShellVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(KidShellVersion a, KidShellVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(KidShellVersion a, KidShellVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() =>
        PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

/// <summary>What a release feed says about one version.</summary>
public sealed record UpdateManifest
{
    public required string Version { get; init; }

    /// <summary>Where the package is. Must be https.</summary>
    public required string PackageUrl { get; init; }

    /// <summary>Lower-case hex SHA-256 of the package.</summary>
    public required string Sha256 { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Parent-facing release notes, Swedish.</summary>
    public string ReleaseNotes { get; init; } = string.Empty;

    /// <summary>Minimum version that may upgrade directly to this one.</summary>
    public string? MinimumFromVersion { get; init; }

    /// <summary>
    /// Whether the package is signed. An unsigned package is never installed,
    /// whatever else is true of it.
    /// </summary>
    public bool IsSigned { get; init; }

    [JsonIgnore]
    public KidShellVersion ParsedVersion =>
        KidShellVersion.TryParse(Version, out var v) ? v : default;
}

/// <summary>Why an update was refused.</summary>
public enum UpdateRejection
{
    None = 0,
    MalformedManifest = 1,
    NotNewer = 2,

    /// <summary>The package is not served over https.</summary>
    InsecureUrl = 3,

    /// <summary>The downloaded bytes do not match the manifest hash.</summary>
    HashMismatch = 4,

    /// <summary>The package is unsigned.</summary>
    Unsigned = 5,

    /// <summary>The installed version is too old to upgrade directly.</summary>
    UpgradePathUnsupported = 6,

    /// <summary>Updating is switched off in this build.</summary>
    UpdatesDisabled = 7
}

public sealed record UpdateDecision(bool ShouldUpdate, UpdateRejection Rejection, string Message);

/// <summary>
/// Decides whether an update may be installed.
///
/// DISABLED IN THIS BUILD. <see cref="UpdatesEnabled"/> is a hard false
/// constant, because there is no signing infrastructure yet and an updater
/// that installs unsigned packages is a remote code execution feature rather
/// than a convenience.
///
/// The logic is written and tested anyway so the rules exist before the
/// machinery does: https only, hash verified against the manifest, signature
/// required, and a version that is genuinely newer.
/// </summary>
public static class UpdatePolicy
{
    /// <summary>
    /// Whether this build may install updates at all.
    ///
    /// False until package signing exists. A constant so that enabling it is a
    /// code change a reviewer sees.
    /// </summary>
    public const bool UpdatesEnabled = false;

    public static UpdateDecision Evaluate(
        UpdateManifest? manifest,
        KidShellVersion installed,
        bool allowWhenDisabled = false)
    {
        if (!UpdatesEnabled && !allowWhenDisabled)
        {
            return new UpdateDecision(false, UpdateRejection.UpdatesDisabled,
                "Automatiska uppdateringar är avstängda i den här versionen.");
        }

        if (manifest is null || !KidShellVersion.TryParse(manifest.Version, out var candidate))
        {
            return new UpdateDecision(false, UpdateRejection.MalformedManifest,
                "Uppdateringsinformationen kunde inte läsas.");
        }

        if (candidate <= installed)
        {
            return new UpdateDecision(false, UpdateRejection.NotNewer,
                "KidShell är redan uppdaterat.");
        }

        if (!manifest.PackageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Plain http would let anyone on the network choose what gets
            // installed.
            return new UpdateDecision(false, UpdateRejection.InsecureUrl,
                "Uppdateringen erbjuds inte över en säker anslutning.");
        }

        if (!manifest.IsSigned)
        {
            return new UpdateDecision(false, UpdateRejection.Unsigned,
                "Uppdateringen är inte signerad.");
        }

        if (!IsValidSha256(manifest.Sha256))
        {
            return new UpdateDecision(false, UpdateRejection.MalformedManifest,
                "Uppdateringens kontrollsumma saknas eller är ogiltig.");
        }

        if (manifest.MinimumFromVersion is { } minimum &&
            KidShellVersion.TryParse(minimum, out var minimumVersion) &&
            installed < minimumVersion)
        {
            return new UpdateDecision(false, UpdateRejection.UpgradePathUnsupported,
                "Den här versionen av KidShell är för gammal för att uppdateras direkt.");
        }

        return new UpdateDecision(true, UpdateRejection.None,
            $"Version {manifest.Version} är tillgänglig.");
    }

    /// <summary>
    /// Verifies downloaded bytes against the manifest hash.
    ///
    /// Constant-time comparison: a timing side channel on a hash check is
    /// unlikely to matter here, but getting it right costs nothing.
    /// </summary>
    public static bool VerifyPackage(byte[] payload, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!IsValidSha256(expectedSha256))
        {
            return false;
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(payload));

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual),
            System.Text.Encoding.ASCII.GetBytes(expectedSha256.Trim().ToLowerInvariant()));
    }

    public static bool IsValidSha256(string? hash) =>
        hash is not null &&
        hash.Trim().Length == 64 &&
        hash.Trim().All(Uri.IsHexDigit);

    /// <summary>Parses a release feed. Returns null rather than throwing.</summary>
    public static UpdateManifest? ParseManifest(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }
}
