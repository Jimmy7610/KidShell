using System.Text.Json;

namespace KidShell.Core.Security.Validation;

/// <summary>
/// Where a validation run had got to before the machine restarted.
///
/// WHY A FILE AND NOT A MEMORY
/// ---------------------------
/// Several things this validation proves can only be proven across a reboot:
/// that the service starts before anyone signs in, that screen time is not a
/// fresh day, that a PIN cooldown survives. A reboot ends the process doing
/// the proving, so what it had established has to be written down first, and
/// written down in a form that cannot be mistaken for a fresh start.
///
/// The hashes are what make it evidence rather than a note. After the restart
/// the same documents are hashed again and compared, so "the counter survived"
/// is a comparison rather than a recollection.
///
/// NOTHING HERE REBOOTS ANYTHING. A human decides that, and this records the
/// state they will want afterwards.
/// </summary>
public sealed record RebootCheckpoint
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The run this belongs to. A checkpoint from another run is not resumed.</summary>
    public string RunId { get; init; } = string.Empty;

    public string MachineName { get; init; } = string.Empty;

    /// <summary>The stage the operator should continue from.</summary>
    public string Stage { get; init; } = string.Empty;

    public DateTimeOffset WrittenUtc { get; init; }

    /// <summary>Whether the service was installed and running before the restart.</summary>
    public bool ServiceRunning { get; init; }

    public string ChildSid { get; init; } = string.Empty;

    /// <summary>
    /// Digests of the protected documents, keyed by document name.
    ///
    /// Digests rather than contents, for the reason in
    /// <see cref="EvidenceRedaction"/>: the question is whether they changed.
    /// </summary>
    public IReadOnlyDictionary<string, string> StateDigests { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Semantic values worth comparing in words as well as in digests.</summary>
    public IReadOnlyDictionary<string, string> Observations { get; init; }
        = new Dictionary<string, string>();

    /// <summary>The machine-state audit hash, so a reboot can be shown to have changed nothing else.</summary>
    public string MachineStateHash { get; init; } = string.Empty;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// Reads a checkpoint, or explains why it cannot be resumed.
    ///
    /// A checkpoint that does not parse, is for another machine, is for
    /// another run or is from a schema this build does not know is NOT
    /// resumed. Resuming the wrong one would silently compare the state of one
    /// run against the expectations of another, and report a pass.
    /// </summary>
    public static CheckpointLoad Load(string? json, string expectedMachine, string expectedRunId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CheckpointLoad(null, "there is no checkpoint");
        }

        RebootCheckpoint? checkpoint;

        try
        {
            checkpoint = JsonSerializer.Deserialize<RebootCheckpoint>(json, Options);
        }
        catch (JsonException)
        {
            return new CheckpointLoad(null, "the checkpoint is not valid JSON");
        }

        if (checkpoint is null)
        {
            return new CheckpointLoad(null, "the checkpoint is empty");
        }

        if (checkpoint.SchemaVersion > CurrentSchemaVersion)
        {
            return new CheckpointLoad(null,
                $"the checkpoint is schema {checkpoint.SchemaVersion} and this build understands {CurrentSchemaVersion}");
        }

        if (!string.IsNullOrWhiteSpace(expectedMachine) &&
            !string.Equals(checkpoint.MachineName.Trim(), expectedMachine.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return new CheckpointLoad(null,
                $"the checkpoint was written on {checkpoint.MachineName}, not {expectedMachine}");
        }

        if (!string.IsNullOrWhiteSpace(expectedRunId) &&
            !string.Equals(checkpoint.RunId.Trim(), expectedRunId.Trim(), StringComparison.Ordinal))
        {
            return new CheckpointLoad(null,
                $"the checkpoint belongs to run {checkpoint.RunId}, not {expectedRunId}");
        }

        if (string.IsNullOrWhiteSpace(checkpoint.Stage))
        {
            return new CheckpointLoad(null, "the checkpoint does not say which stage to resume");
        }

        return new CheckpointLoad(checkpoint, null);
    }

    /// <summary>
    /// Which protected documents changed since the checkpoint.
    ///
    /// Missing on either side counts as a change, because a document that has
    /// appeared or disappeared across a reboot is exactly the kind of thing
    /// this is looking for.
    /// </summary>
    public IReadOnlyList<string> Changed(IReadOnlyDictionary<string, string> now)
    {
        ArgumentNullException.ThrowIfNull(now);

        var names = StateDigests.Keys.Union(now.Keys, StringComparer.OrdinalIgnoreCase);

        return
        [
            .. names.Where(name =>
                !string.Equals(
                    StateDigests.GetValueOrDefault(name, "(absent)"),
                    now.GetValueOrDefault(name, "(absent)"),
                    StringComparison.OrdinalIgnoreCase))
        ];
    }
}

/// <summary>A checkpoint, or the reason it is not being resumed.</summary>
public sealed record CheckpointLoad(RebootCheckpoint? Checkpoint, string? Refusal)
{
    public bool CanResume => Checkpoint is not null && Refusal is null;
}
