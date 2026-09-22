using System.Text.Json;
using System.Text.Json.Serialization;
using KidShell.Core.Diagnostics;

namespace KidShell.Core.ScreenTime;

/// <summary>
/// Persists the screen-time counter to its own small JSON file.
///
/// Separate from the configuration document because it is written every tick
/// while the configuration is written when a parent saves. Sharing a file
/// would mean a crash during a routine counter update could corrupt the
/// parent's settings, which is a far worse loss.
///
/// Writes are temp-then-replace, and a corrupt file is replaced by a fresh
/// counter rather than throwing: losing today's count is a minor annoyance,
/// while failing to start is not.
/// </summary>
public sealed class JsonScreenTimeStateStore : IScreenTimeStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly IKidShellLogger _logger;

    public JsonScreenTimeStateStore(string path, IKidShellLogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public string Path => _path;

    public ScreenTimeState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new ScreenTimeState();
            }

            var state = JsonSerializer.Deserialize<ScreenTimeState>(File.ReadAllText(_path), Options);

            if (state is null)
            {
                return new ScreenTimeState();
            }

            // A counter from a future schema is not understood, so the safe
            // reading is "no time used" rather than a number that might mean
            // something else.
            if (state.SchemaVersion > ScreenTimeState.CurrentSchemaVersion)
            {
                _logger.Warning("ScreenTime",
                    $"Screen-time state is schema {state.SchemaVersion}; starting a fresh counter.");
                return new ScreenTimeState();
            }

            state.UsedSeconds = Math.Max(0, state.UsedSeconds);
            state.BonusMinutes = Math.Max(0, state.BonusMinutes);

            return state;
        }
        catch (Exception ex)
        {
            // Losing today's count is an annoyance; failing to start is not.
            _logger.Warning("ScreenTime", "Screen-time state was unreadable; starting a fresh counter.", ex);
            return new ScreenTimeState();
        }
    }

    public bool Save(ScreenTimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
            File.Move(temp, _path, overwrite: true);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("ScreenTime", "Screen-time state could not be saved.", ex);
            return false;
        }
    }
}

/// <summary>In-memory store. Used by tests and by a session that must not persist.</summary>
public sealed class InMemoryScreenTimeStateStore : IScreenTimeStateStore
{
    private ScreenTimeState _state = new();

    public int SaveCount { get; private set; }

    public ScreenTimeState Load() => _state.Clone();

    public bool Save(ScreenTimeState state)
    {
        _state = state.Clone();
        SaveCount++;
        return true;
    }
}
