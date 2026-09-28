using KidShell.App.Localization;
using KidShell.Core.Mvvm;
using KidShell.Core.Security.Transactions;

namespace KidShell.App.ViewModels.Parent;

/// <summary>One recorded security change, as a parent reads it.</summary>
public sealed class RecoveryEntryViewModel
{
    internal RecoveryEntryViewModel(RecoveryManifest manifest)
    {
        When = manifest.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        TransactionId = manifest.TransactionId;
        StepCount = manifest.Steps.Count;
        NeedsAttention = manifest.RequiresAttention;

        State = manifest.FinalState switch
        {
            null => Strings.Get("Recovery.StateUnfinished"),
            TransactionState.Committed => Strings.Get("Recovery.StateCommitted"),
            TransactionState.RolledBack => Strings.Get("Recovery.StateRolledBack"),
            TransactionState.RollbackFailed => Strings.Get("Recovery.StateRollbackFailed"),
            TransactionState.Refused => Strings.Get("Recovery.StateRefused"),
            TransactionState.Cancelled => Strings.Get("Recovery.StateCancelled"),
            _ => manifest.FinalState.ToString()!
        };
    }

    public string When { get; }

    public string TransactionId { get; }

    public string State { get; }

    public int StepCount { get; }

    public bool NeedsAttention { get; }

    public string Summary => Strings.Format("Recovery.EntrySummary", StepCount);
}

/// <summary>
/// Föräldraläge → Säkerhet → Återställning.
///
/// WHAT A PARENT NEEDS TO SEE, AND WHEN
/// ------------------------------------
/// Almost always: nothing has been changed, so there is nothing to recover
/// from, and saying that plainly is the whole content.
///
/// Occasionally: a change was applied, and the parent should be able to see
/// what and when without opening a JSON file.
///
/// Rarely, and this is what the surface exists for: a change failed AND its
/// rollback failed, so the machine is in a state nobody chose. That case must
/// be impossible to miss, and it must say exactly where to go next.
/// </summary>
public sealed class RecoveryStatusViewModel : ObservableObject
{
    private readonly IRecoveryManifestStore _store;

    private bool _isLoading;
    private bool _hasLoaded;

    public RecoveryStatusViewModel(IRecoveryManifestStore store)
    {
        _store = store;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
    }

    public RelayCommand RefreshCommand { get; }

    public List<RecoveryEntryViewModel> Entries { get; private set; } = [];

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>True when nothing has ever been applied. The usual case.</summary>
    public bool HasNothingToRecover => _hasLoaded && Entries.Count == 0;

    /// <summary>The one state that needs a human. Shown prominently.</summary>
    public bool NeedsAttention => Entries.Any(e => e.NeedsAttention);

    public int AttentionCount => Entries.Count(e => e.NeedsAttention);

    public string Headline
    {
        get
        {
            if (!_hasLoaded)
            {
                return Strings.Get("Recovery.Loading");
            }

            if (NeedsAttention)
            {
                return Strings.Format("Recovery.NeedsAttention", AttentionCount);
            }

            return Entries.Count == 0
                ? Strings.Get("Recovery.NothingApplied")
                : Strings.Format("Recovery.Count", Entries.Count);
        }
    }

    public string Explanation => NeedsAttention
        ? Strings.Get("Recovery.AttentionBody")
        : Strings.Get("Recovery.NormalBody");

    /// <summary>Where the manifests are, so a parent can find them without KidShell.</summary>
    public string Location => _store is RecoveryManifestStore concrete
        ? concrete.Directory
        : Strings.Get("Recovery.UnknownLocation");

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;

        try
        {
            var manifests = await _store.ListAsync(cancellationToken).ConfigureAwait(true);
            Entries = [.. manifests.Select(m => new RecoveryEntryViewModel(m))];
            _hasLoaded = true;
        }
        catch
        {
            // An unreadable recovery directory must not break the Säkerhet
            // page. The tool reads the same files without KidShell.
            Entries = [];
            _hasLoaded = true;
        }
        finally
        {
            IsLoading = false;
        }

        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(HasNothingToRecover));
        OnPropertyChanged(nameof(NeedsAttention));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(Explanation));
        OnPropertyChanged(nameof(Location));
    }
}
