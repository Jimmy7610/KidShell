using System.Text.Json;
using System.Text.Json.Serialization;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security;

namespace KidShell.Core.Configuration;

/// <summary>
/// JSON-on-disk configuration store with defensive save/load behaviour:
///
///  * serialize to a string first, so a serialization failure never truncates
///    a good file,
///  * write a temporary file, then replace the live file,
///  * keep the previous good file as a .bak,
///  * on unreadable input, quarantine the bad file and fall back to defaults.
/// </summary>
public sealed class JsonConfigurationStore : IConfigurationStore
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IKidShellLogger _logger;
    private readonly object _gate = new();

    public JsonConfigurationStore(string configurationFilePath, IKidShellLogger logger)
    {
        ConfigurationFilePath = configurationFilePath;
        _logger = logger;
    }

    public string ConfigurationFilePath { get; }

    internal string TempPath => ConfigurationFilePath + ".tmp";

    internal string BackupPath => ConfigurationFilePath + ".bak";

    public ConfigurationLoadResult Load()
    {
        lock (_gate)
        {
            if (!File.Exists(ConfigurationFilePath))
            {
                _logger.Info("Config", "No configuration file yet; creating defaults.");
                return new ConfigurationLoadResult(
                    KidShellConfiguration.CreateDefault(),
                    ConfigurationLoadStatus.CreatedDefaults);
            }

            try
            {
                var json = File.ReadAllText(ConfigurationFilePath);
                var config = Deserialize(json, out var wasMigrated);
                _logger.Info("Config", $"Configuration loaded (schemaVersion {config.SchemaVersion}).");
                return new ConfigurationLoadResult(
                    config,
                    ConfigurationLoadStatus.Loaded,
                    Detail: null,
                    WasMigrated: wasMigrated);
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                _logger.Error("Config", "Configuration file could not be read.", ex);
                QuarantineCorruptFile();

                // The backup exists precisely for this moment, and until now it
                // was written and never read: a corrupt primary threw away the
                // parent's entire configuration while a good copy sat beside
                // it. Losing a child's profile, app list and PIN to one bad
                // write is a far worse outcome than the write itself.
                if (TryLoadBackup(out var restored, out var migrated))
                {
                    _logger.Warning("Config",
                        "Configuration was restored from the previous good file.");

                    return new ConfigurationLoadResult(
                        restored!,
                        ConfigurationLoadStatus.RecoveredFromBackup,
                        ex.Message,
                        WasMigrated: migrated);
                }

                _logger.Error("Config", "No usable backup either; falling back to defaults.");

                return new ConfigurationLoadResult(
                    KidShellConfiguration.CreateDefault(),
                    ConfigurationLoadStatus.RecoveredFromCorruption,
                    ex.Message);
            }
        }
    }

    /// <summary>
    /// Reads the previous good document, if there is one and it parses.
    ///
    /// Deliberately quiet about its own failures: this runs while already
    /// handling a corrupt primary, and a throw here would turn a recoverable
    /// situation into an unhandled one.
    /// </summary>
    private bool TryLoadBackup(out KidShellConfiguration? configuration, out bool wasMigrated)
    {
        configuration = null;
        wasMigrated = false;

        if (!File.Exists(BackupPath))
        {
            return false;
        }

        try
        {
            configuration = Deserialize(File.ReadAllText(BackupPath), out wasMigrated);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning("Config", "The backup configuration could not be read either.", ex);
            return false;
        }
    }

    public bool Save(KidShellConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        lock (_gate)
        {
            string json;
            try
            {
                // Serialize up front: if this throws, the good file is untouched.
                json = JsonSerializer.Serialize(configuration, SerializerOptions);

                // Cheap round-trip validation before anything is replaced.
                _ = Deserialize(json, out _);
            }
            catch (Exception ex)
            {
                _logger.Error("Config", "Configuration failed to serialize; nothing was written.", ex);
                return false;
            }

            try
            {
                var directory = Path.GetDirectoryName(ConfigurationFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(TempPath, json);

                if (File.Exists(ConfigurationFilePath))
                {
                    // File.Replace gives an atomic-ish swap plus a backup copy.
                    File.Replace(TempPath, ConfigurationFilePath, BackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(TempPath, ConfigurationFilePath);
                }

                _logger.Info("Config", "Configuration saved.");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error("Config", "Configuration could not be written.", ex);
                TryDelete(TempPath);
                return false;
            }
        }
    }

    private KidShellConfiguration Deserialize(string json, out bool wasMigrated)
    {
        var config = JsonSerializer.Deserialize<KidShellConfiguration>(json, SerializerOptions)
                     ?? throw new InvalidDataException("Configuration document deserialized to null.");

        wasMigrated = ConfigurationMigrator.Migrate(config, _logger);

        return Normalize(config);
    }

    /// <summary>
    /// Repairs structurally-valid-but-incomplete documents so the rest of the
    /// app can assume non-null collections and sane values.
    /// </summary>
    internal static KidShellConfiguration Normalize(KidShellConfiguration config)
    {
        config.Child ??= new ChildProfile();
        config.Apps ??= [];
        config.ScreenTime ??= new ScreenTimeSettings();
        config.Web ??= new WebSettings();
        config.Web.AllowedDomains ??= [];
        config.ParentPin ??= new ParentPinSettings();

        if (config.SchemaVersion <= 0)
        {
            config.SchemaVersion = KidShellConfiguration.CurrentSchemaVersion;
        }

        // A blank name and a zero age are valid: they mean first-run setup has
        // not been completed. Normalize tidies, it never invents a child.
        config.Child.Name = (config.Child.Name ?? string.Empty).Trim();

        if (config.Child.Name.Length > ChildProfile.MaxNameLength)
        {
            config.Child.Name = config.Child.Name[..ChildProfile.MaxNameLength];
        }

        config.Child.Age = config.Child.Age <= 0
            ? 0
            : Math.Clamp(config.Child.Age, ChildProfile.MinAge, ChildProfile.MaxAge);

        config.Child.AvatarId = (config.Child.AvatarId ?? string.Empty).Trim();
        config.Child.ThemeId = ThemeIds.Migrate(config.Child.ThemeId);

        config.ScreenTime.WeekdayMinutes = Math.Clamp(config.ScreenTime.WeekdayMinutes, 0, 24 * 60);
        config.ScreenTime.WeekendMinutes = Math.Clamp(config.ScreenTime.WeekendMinutes, 0, 24 * 60);

        foreach (var app in config.Apps.Where(app => string.IsNullOrWhiteSpace(app.Id)))
        {
            app.Id = Guid.NewGuid().ToString("n");
        }

        return config;
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            var quarantine = $"{ConfigurationFilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(ConfigurationFilePath, quarantine, overwrite: true);
            _logger.Warning("Config", "Unreadable configuration copied aside for inspection.");
        }
        catch (Exception ex)
        {
            _logger.Warning("Config", "Could not quarantine the unreadable configuration file.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort only.
        }
    }
}
