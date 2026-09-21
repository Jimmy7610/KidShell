using System.Text.Json.Serialization;

namespace KidShell.Core.Security;

/// <summary>
/// Persisted parent PIN material. Only a PBKDF2 hash and its salt are stored -
/// the PIN itself is never written to disk in any form.
/// </summary>
public sealed class ParentPinSettings
{
    /// <summary>Base64 PBKDF2-SHA256 hash, or null when no PIN has been set yet.</summary>
    public string? Hash { get; set; }

    /// <summary>Base64 salt, or null when no PIN has been set yet.</summary>
    public string? Salt { get; set; }

    public int Iterations { get; set; } = PinHasher.DefaultIterations;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Hash) && !string.IsNullOrWhiteSpace(Salt);

    public ParentPinSettings Clone() => new()
    {
        Hash = Hash,
        Salt = Salt,
        Iterations = Iterations
    };
}
