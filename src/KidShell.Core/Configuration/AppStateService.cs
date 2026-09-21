using KidShell.Core.Diagnostics;

namespace KidShell.Core.Configuration;

public sealed class ConfigurationChangedEventArgs(KidShellConfiguration configuration) : EventArgs
{
    public KidShellConfiguration Configuration { get; } = configuration;
}

/// <summary>
/// The single source of truth for live KidShell state.
///
/// Child Mode reads <see cref="Current"/>. Parent Mode edits a detached draft
/// (<see cref="CreateDraft"/>) so that half-finished edits never leak onto the
/// child's screen, and only <see cref="Commit"/> makes them real.
/// </summary>
public interface IAppStateService
{
    KidShellConfiguration Current { get; }

    ConfigurationLoadStatus LoadStatus { get; }

    /// <summary>Extra detail about the last load, e.g. why a document was rejected.</summary>
    string? LoadDetail { get; }

    string ConfigurationFilePath { get; }

    event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

    /// <summary>Loads configuration from the store. Safe to call once at startup.</summary>
    ConfigurationLoadStatus Initialize();

    /// <summary>A detached copy for Parent Mode to edit.</summary>
    KidShellConfiguration CreateDraft();

    /// <summary>Persists a draft and promotes it to <see cref="Current"/>.</summary>
    bool Commit(KidShellConfiguration draft);

    /// <summary>Persists in-place changes to <see cref="Current"/> (e.g. a new PIN).</summary>
    bool SaveCurrent();
}

public sealed class AppStateService : IAppStateService
{
    private readonly IConfigurationStore _store;
    private readonly IKidShellLogger _logger;

    public AppStateService(IConfigurationStore store, IKidShellLogger logger)
    {
        _store = store;
        _logger = logger;
        Current = KidShellConfiguration.CreateDefault();
    }

    public KidShellConfiguration Current { get; private set; }

    public ConfigurationLoadStatus LoadStatus { get; private set; } = ConfigurationLoadStatus.CreatedDefaults;

    public string? LoadDetail { get; private set; }

    public string ConfigurationFilePath => _store.ConfigurationFilePath;

    public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

    public ConfigurationLoadStatus Initialize()
    {
        var result = _store.Load();
        Current = result.Configuration;
        LoadStatus = result.Status;
        LoadDetail = result.Detail;

        if (result.Status != ConfigurationLoadStatus.Loaded)
        {
            // Materialise the defaults so the next start is an ordinary load.
            _store.Save(Current);
        }

        RaiseChanged();
        return result.Status;
    }

    public KidShellConfiguration CreateDraft() => Current.Clone();

    public bool Commit(KidShellConfiguration draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        draft.SchemaVersion = KidShellConfiguration.CurrentSchemaVersion;
        JsonConfigurationStore.Normalize(draft);

        if (!_store.Save(draft))
        {
            _logger.Error("State", "Draft was rejected by the configuration store; live state unchanged.");
            return false;
        }

        Current = draft;
        RaiseChanged();
        return true;
    }

    public bool SaveCurrent() => _store.Save(Current);

    private void RaiseChanged() => ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(Current));
}
