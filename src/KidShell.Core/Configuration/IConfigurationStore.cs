namespace KidShell.Core.Configuration;

/// <summary>Outcome of loading the configuration document.</summary>
public enum ConfigurationLoadStatus
{
    /// <summary>A valid document was read from disk.</summary>
    Loaded = 0,

    /// <summary>No document existed yet; defaults were created.</summary>
    CreatedDefaults = 1,

    /// <summary>The document was unreadable; defaults were used and a copy kept.</summary>
    RecoveredFromCorruption = 2,

    /// <summary>
    /// The document was unreadable but the previous good one was, so the
    /// parent's settings survived.
    ///
    /// Distinct from <see cref="RecoveredFromCorruption"/> on purpose: one of
    /// these means "we kept your configuration" and the other means "we lost
    /// it", and a parent deserves to be told which.
    /// </summary>
    RecoveredFromBackup = 3
}

public sealed record ConfigurationLoadResult(
    KidShellConfiguration Configuration,
    ConfigurationLoadStatus Status,
    string? Detail = null,
    bool WasMigrated = false);

/// <summary>Reads and writes the persisted KidShell configuration.</summary>
public interface IConfigurationStore
{
    string ConfigurationFilePath { get; }

    ConfigurationLoadResult Load();

    /// <summary>Writes the document. Returns false when the write failed.</summary>
    bool Save(KidShellConfiguration configuration);
}
