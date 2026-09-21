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
                var config = Deserialize(json);
                _logger.Info("Config", $"Configuration loaded (schemaVersion {config.SchemaVersion}).");
                return new ConfigurationLoadResult(config, ConfigurationLoadStatus.Loaded);
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                _logger.Error("Config", "Configuration file could not be read; falling back to defaults.", ex);
                QuarantineCorruptFile();
                return new ConfigurationLoadResult(
                    KidShellConfiguration.CreateDefault(),
                    ConfigurationLoadStatus.RecoveredFromCorruption,
                    ex.Message);
            }
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
                _ = Deserialize(json);
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

    private static KidShellConfiguration Deserialize(string json)
    {
        var config = JsonSerializer.Deserialize<KidShellConfiguration>(json, SerializerOptions)
                     ?? throw new InvalidDataException("Configuration document deserialized to null.");

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

        if (config.Child.Age is < 0 or > 18)
        {
            config.Child.Age = 6;
        }

        if (string.IsNullOrWhiteSpace(config.Child.Name))
        {
            config.Child.Name = "Barnet";
        }

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
