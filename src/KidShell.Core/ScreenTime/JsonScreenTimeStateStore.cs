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
/// OPSV FINDING 04 — A FAILED WRITE USED TO REFUND TIME
/// ----------------------------------------------------
/// Sixty seconds were used, the save failed, and after a restart the counter
/// read zero. Two separate mistakes made that happen.
///
/// The first was that an unreadable file produced a FRESH counter. Losing
/// today's count was described as a minor annoyance, and for a corrupt file
/// written by a crash that is true - but the same path answers "no time has
/// been used" to a child who deleted the file, and to a disk that failed
/// halfway through a write. A counter is security state: the safe direction
/// for it is up, never down.
///
/// The second was that the write was temp-then-move with no durable previous
/// copy. When the move failed there was nothing to fall back to.
///
/// So: every successful write leaves the superseded state behind as a backup,
/// a load falls back to that backup, and a load that finds a counter it cannot
/// read says so rather than inventing a zero.
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

    /// <summary>The previous known-good counter. Written by a successful save.</summary>
    public string BackupPath => _path + ".bak";

    private string TempPath => _path + ".tmp";

    public ScreenTimeStateLoad Load()
    {
        var primary = TryRead(_path, out var primaryState);
        var hadPrimary = File.Exists(_path);

        if (primary && primaryState is not null)
        {
            return new ScreenTimeStateLoad(primaryState, ScreenTimeLoadOutcome.Primary);
        }

        var backup = TryRead(BackupPath, out var backupState);
        var hadBackup = File.Exists(BackupPath);

        if (backup && backupState is not null)
        {
            // Deliberately preferred over a fresh counter even when the
            // primary is merely missing. The backup is the state as of the
            // last successful write, so at worst it over-counts - which is the
            // direction a failure is allowed to go.
            _logger.Warning("ScreenTime",
                "The screen-time counter was unreadable; the previous known-good copy was used. " +
                "Some time may be counted twice, which is the safe direction.");

            return new ScreenTimeStateLoad(
                backupState,
                ScreenTimeLoadOutcome.RecoveredFromBackup,
                "primary unreadable");
        }

        if (!hadPrimary && !hadBackup)
        {
            // Nothing has ever been written. No time has been used because the
            // product has not been used, and that really is zero.
            return ScreenTimeStateLoad.FirstRun();
        }

        // A counter existed and neither copy parsed. How much was used is
        // unknown, and zero is the one answer that is certainly wrong.
        _logger.Error("ScreenTime",
            "Both the screen-time counter and its backup are unreadable. " +
            "Today is treated as spent rather than refunded; a parent can reset it.");

        return new ScreenTimeStateLoad(
            new ScreenTimeState(),
            ScreenTimeLoadOutcome.Unreadable,
            "primary and backup both unreadable");
    }

    private bool TryRead(string path, out ScreenTimeState? state)
    {
        state = null;

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var parsed = JsonSerializer.Deserialize<ScreenTimeState>(File.ReadAllText(path), Options);

            if (parsed is null)
            {
                return false;
            }

            // A counter from a future schema is not understood. It is NOT
            // treated as zero: a number whose meaning is unknown is exactly
            // the case where guessing downwards hands out free time.
            if (parsed.SchemaVersion > ScreenTimeState.CurrentSchemaVersion)
            {
                _logger.Warning("ScreenTime",
                    $"Screen-time state is schema {parsed.SchemaVersion}, which this build does not understand.");
                return false;
            }

            parsed.UsedSeconds = Math.Max(0, parsed.UsedSeconds);
            parsed.BonusMinutes = Math.Max(0, parsed.BonusMinutes);

            state = parsed;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning("ScreenTime", $"Screen-time state at '{path}' could not be read.", ex);
            return false;
        }
    }

    /// <summary>
    /// Writes the counter durably, keeping the superseded one as a backup.
    ///
    /// The authoritative file is never truncated first. It is replaced, in one
    /// step, by a temporary file that has already been written and flushed to
    /// disk - so an interruption leaves either the old counter or the new one,
    /// and never half of either.
    /// </summary>
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

            WriteAndFlush(TempPath, JsonSerializer.Serialize(state, Options));

            if (File.Exists(_path))
            {
                // Replace keeps the superseded file as the backup, atomically.
                // This is the whole recovery story: after any successful save
                // there are two readable copies of the counter.
                File.Replace(TempPath, _path, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(TempPath, _path, overwrite: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            // The previous counter is still on disk and still readable. That
            // is the point of not truncating it first.
            _logger.Error("ScreenTime",
                "Screen-time state could not be saved; the previous counter is unchanged.", ex);

            TryDeleteTemp();
            return false;
        }
    }

    /// <summary>
    /// Writes and forces the bytes to the device.
    ///
    /// Without the flush the replace can be ordered before the data reaches
    /// the disk, so a power loss leaves an empty file where the counter was -
    /// which is the refund this whole class exists to prevent, arriving by a
    /// different route.
    /// </summary>
    private static void WriteAndFlush(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);

        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private void TryDeleteTemp()
    {
        try
        {
            if (File.Exists(TempPath))
            {
                File.Delete(TempPath);
            }
        }
        catch (Exception)
        {
            // Best effort. A stale temp file is replaced by the next save.
        }
    }
}

/// <summary>In-memory store. Used by tests and by a session that must not persist.</summary>
public sealed class InMemoryScreenTimeStateStore : IScreenTimeStateStore
{
    private ScreenTimeState _state = new();
    private bool _written;

    public int SaveCount { get; private set; }

    /// <summary>Makes every subsequent save fail, the way a full disk does.</summary>
    public bool FailWrites { get; set; }

    public ScreenTimeStateLoad Load() => _written
        ? new ScreenTimeStateLoad(_state.Clone(), ScreenTimeLoadOutcome.Primary)
        : ScreenTimeStateLoad.FirstRun();

    public bool Save(ScreenTimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        SaveCount++;

        if (FailWrites)
        {
            return false;
        }

        _state = state.Clone();
        _written = true;
        return true;
    }
}
