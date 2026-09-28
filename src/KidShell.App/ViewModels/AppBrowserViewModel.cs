using System.Collections.ObjectModel;
using KidShell.App.Localization;
using KidShell.Core.Apps;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;

namespace KidShell.App.ViewModels;

/// <summary>
/// The installed-applications browser.
///
/// WHY THIS EXISTS
/// ---------------
/// Three scanners have existed for a while, along with a catalogue that merges
/// their results - all registered in dependency injection and consumed by
/// nothing. Adding an app meant typing a path into a text box, which is a
/// reasonable expert affordance and a hopeless default for a parent who just
/// wants Paint.
///
/// WHAT IT DOES NOT DO
/// -------------------
/// Discovery is read-only and grants nothing. Listing an application says it
/// exists; adding it puts a card in the child's grid; permitting it under
/// Windows application control is a third, separate decision made during secure
/// setup. The UI keeps all three apart, because a parent who believes "I
/// removed the card" means "the child cannot run it" has been misled by the
/// product.
/// </summary>
public sealed class AppBrowserViewModel : ObservableObject
{
    private readonly IApplicationCatalog _catalog;

    private string _query = string.Empty;
    private bool _isLoading;
    private bool _hasLoaded;
    private string? _errorMessage;
    private IReadOnlyList<DiscoveredApplication> _all = [];
    private IReadOnlyCollection<KidAppDefinition> _existing = [];

    public AppBrowserViewModel(IApplicationCatalog catalog)
    {
        _catalog = catalog;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(refresh: true));
    }

    public ObservableCollection<DiscoveredAppViewModel> Results { get; } = [];

    public RelayCommand RefreshCommand { get; }

    /// <summary>Raised when the parent picks an application to add.</summary>
    public event EventHandler<DiscoveredApplication>? ApplicationChosen;

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    /// <summary>True when a finished scan produced nothing to show.</summary>
    public bool ShowEmptyState => _hasLoaded && !IsLoading && Results.Count == 0;

    public string EmptyStateText => _query.Length > 0
        ? Strings.Format("Discover.NoMatches", _query)
        : Strings.Get("Discover.NothingFound");

    public string ResultSummary => Strings.Format("Discover.Count", Results.Count, _all.Count);

    /// <summary>
    /// The apps already in the child's grid, so the list can mark them rather
    /// than letting a parent add the same program twice.
    /// </summary>
    public void SetExisting(IReadOnlyCollection<KidAppDefinition> existing)
    {
        _existing = existing;
        MarkExisting();
    }

    public async Task LoadAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            _all = await _catalog.GetApplicationsAsync(refresh, cancellationToken).ConfigureAwait(true);
            _hasLoaded = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A machine with an unhealthy package store or a locked registry
            // key must not break the dialog - the manual path still works.
            _all = [];
            _hasLoaded = true;
            ErrorMessage = Strings.Get("Discover.ScanFailed");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            IsLoading = false;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var matches = ApplicationCatalog.Filter(_all, _query);

        Results.Clear();

        foreach (var application in matches)
        {
            Results.Add(new DiscoveredAppViewModel(application));
        }

        MarkExisting();

        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void MarkExisting()
    {
        foreach (var row in Results)
        {
            row.IsAlreadyAdded = ApplicationCatalog.IsAlreadyAdded(row.Application, _existing);
        }
    }

    public void Choose(DiscoveredAppViewModel? row)
    {
        if (row is { CanAdd: true })
        {
            ApplicationChosen?.Invoke(this, row.Application);
        }
    }
}
