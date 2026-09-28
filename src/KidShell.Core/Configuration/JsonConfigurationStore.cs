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

        // Sections before migration, not after.
        //
        // The migrator's job is to rewrite real fields, and it reads the child
        // profile to decide whether a schema 1 document was ever set up. So it
        // must not be the code that discovers the profile is missing - given
        // {"schemaVersion": 1, "child": null} it walked straight into a null,
        // which is the audit's own payload failing one stage earlier than they
        // saw it.
        EnsureSections(config);

        wasMigrated = ConfigurationMigrator.Migrate(config, _logger);

        return Normalize(config);
    }

    /// <summary>
    /// Repairs structurally-valid-but-incomplete documents so the rest of the
    /// app can assume non-null collections and sane values.
    ///
    /// The contract, relied on everywhere downstream: after this runs, no
    /// object is null, no collection is null, no collection CONTAINS a null,
    /// and every number is inside the range its own type implies.
    ///
    /// That middle clause is the one that had been missed. Null objects were
    /// already handled, because a missing section is the obvious case. A list
    /// that is present and holds a hole is not obvious at all - `"apps":
    /// [null]` deserializes into a perfectly good List with one null in it,
    /// and the tidy-up loop below used to walk straight into it. `"screenTime":
    /// { "warningMinutes": null }` was worse: it loaded without complaint and
    /// failed much later, in a clone or an evaluation, nowhere near the file
    /// that caused it.
    /// </summary>
    /// <summary>
    /// Gives the document every section it is supposed to have.
    ///
    /// Separate from the rest of normalization because it has to run earlier -
    /// before migration, which reads these fields. Cheap and idempotent, so
    /// <see cref="Normalize"/> calls it again rather than assuming.
    /// </summary>
    internal static void EnsureSections(KidShellConfiguration config)
    {
        config.Child ??= new ChildProfile();
        config.Apps ??= [];
        config.ScreenTime ??= new ScreenTimeSettings();
        config.Web ??= new WebSettings();
        config.Web.AllowedDomains ??= [];
        config.ParentPin ??= new ParentPinSettings();
        config.ScreenTime.WarningMinutes ??= [.. ScreenTimeSettings.DefaultWarningMinutes];
    }

    internal static KidShellConfiguration Normalize(KidShellConfiguration config)
    {
        EnsureSections(config);

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

        NormalizeScreenTime(config.ScreenTime);
        NormalizeWeb(config.Web);
        NormalizeApps(config);

        return config;
    }

    private static void NormalizeScreenTime(ScreenTimeSettings screenTime)
    {
        screenTime.WeekdayMinutes = Math.Clamp(screenTime.WeekdayMinutes, 0, 24 * 60);
        screenTime.WeekendMinutes = Math.Clamp(screenTime.WeekendMinutes, 0, 24 * 60);

        // An hour outside a clock face is not a restriction anybody chose, and
        // silently comparing against hour 99 would quietly allow everything.
        screenTime.AllowedFromHour = Math.Clamp(screenTime.AllowedFromHour, 0, 23);
        screenTime.AllowedUntilHour = Math.Clamp(screenTime.AllowedUntilHour, 0, 24);

        // A warning at zero minutes is not a warning, a negative one cannot
        // happen, and two identical ones would tell a child the same thing
        // twice. Largest first, which is the order they fire in.
        screenTime.WarningMinutes = screenTime.WarningMinutes is { } warnings
            ? [.. warnings.Where(m => m is > 0 and <= 24 * 60).Distinct().OrderByDescending(m => m)]
            : [.. ScreenTimeSettings.DefaultWarningMinutes];

        if (screenTime.WarningMinutes.Count == 0)
        {
            screenTime.WarningMinutes = [.. ScreenTimeSettings.DefaultWarningMinutes];
        }
    }

    private static void NormalizeWeb(WebSettings web)
    {
        if (web.AllowedDomains is not { } domains)
        {
            web.AllowedDomains = [];
            return;
        }

        // Case and surrounding space are not part of a hostname, so two
        // entries differing only by those are one entry. Comparing them as
        // written would let "SVT.se" and "svt.se" both sit in a list a parent
        // reads as a single decision.
        web.AllowedDomains =
        [
            .. domains
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
        ];
    }

    private static void NormalizeApps(KidShellConfiguration config)
    {
        // Holes in the list first. Everything after this may assume an app.
        config.Apps = config.Apps is { } apps
            ? [.. apps.Where(app => app is not null)]
            : [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in config.Apps)
        {
            app.DisplayName = (app.DisplayName ?? string.Empty).Trim();
            app.ProgramName = (app.ProgramName ?? string.Empty).Trim();
            app.Description = (app.Description ?? string.Empty).Trim();
            app.Category = (app.Category ?? string.Empty).Trim();
            app.ExecutablePath = (app.ExecutablePath ?? string.Empty).Trim();
            app.Arguments = app.Arguments ?? string.Empty;

            if (string.IsNullOrWhiteSpace(app.Icon))
            {
                app.Icon = IconKeys.Generic;
            }

            // An id must exist and must be unique, because it is what a launch
            // and a removal both resolve against. A duplicate would make
            // "remove this app" ambiguous.
            if (string.IsNullOrWhiteSpace(app.Id) || !seen.Add(app.Id))
            {
                app.Id = Guid.NewGuid().ToString("n");
                seen.Add(app.Id);
            }
        }
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
