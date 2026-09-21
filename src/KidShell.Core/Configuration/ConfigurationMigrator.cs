using KidShell.Core.Diagnostics;

namespace KidShell.Core.Configuration;

/// <summary>
/// Brings older configuration documents up to the current schema.
///
/// The guiding rule is: never throw away something a parent actually chose.
/// The only value treated as disposable is the MVP 0.1 placeholder profile,
/// which was a mockup artefact rather than anybody's real child.
/// </summary>
public static class ConfigurationMigrator
{
    /// <summary>
    /// The child name MVP 0.1 shipped as a default. It came from the design
    /// mockups and was never a real configuration choice, so a schema 1
    /// document carrying it is treated as "never set up".
    /// </summary>
    internal const string LegacyPlaceholderName = "Alice";

    /// <summary>
    /// Migrates in place. Returns true if the document was changed, so the
    /// caller can decide whether to write it back.
    /// </summary>
    public static bool Migrate(KidShellConfiguration config, IKidShellLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.SchemaVersion >= KidShellConfiguration.CurrentSchemaVersion)
        {
            // Already current (or newer, which we leave alone rather than
            // downgrade). Theme ids are still validated by Normalize.
            return false;
        }

        var from = config.SchemaVersion;

        if (config.SchemaVersion < 2)
        {
            MigrateToVersion2(config, logger);
        }

        config.SchemaVersion = KidShellConfiguration.CurrentSchemaVersion;
        logger?.Info("Config", $"Configuration migrated from schema {from} to {config.SchemaVersion}.");
        return true;
    }

    /// <summary>
    /// Schema 1 → 2.
    ///
    ///  * theme ids gain real names (meadow/sunset become forest/bright),
    ///  * the profile gains IsOnboardingComplete.
    ///
    /// Schema 1 had no concept of onboarding, so the flag has to be inferred:
    /// a document still carrying the shipped placeholder profile has plainly
    /// never been set up, and anything else is treated as a real profile that
    /// has already been personalised.
    /// </summary>
    private static void MigrateToVersion2(KidShellConfiguration config, IKidShellLogger? logger)
    {
        config.Child.ThemeId = ThemeIds.Migrate(config.Child.ThemeId);

        var name = config.Child.Name?.Trim() ?? string.Empty;
        var isPlaceholder = string.Equals(name, LegacyPlaceholderName, StringComparison.OrdinalIgnoreCase);

        if (isPlaceholder || name.Length == 0)
        {
            logger?.Info("Config", "Schema 1 profile was never personalised; first-run setup will run.");
            config.Child.ResetForOnboarding();
            return;
        }

        // A real name was already chosen. Keep it, and treat the profile as
        // set up so that upgrading never pushes a family back through setup.
        config.Child.IsOnboardingComplete = true;

        if (config.Child.Age <= 0)
        {
            config.Child.Age = 0;
        }

        logger?.Info("Config", "Schema 1 profile carried over as an already-configured child.");
    }
}
