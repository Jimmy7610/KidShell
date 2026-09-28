using System.Collections.ObjectModel;
using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;

namespace KidShell.App.ViewModels.Parent;

/// <summary>Föräldraläge → Appar.</summary>
public sealed class ParentAppsViewModel : ObservableObject
{
    private readonly IAddAppFlow _addAppFlow;
    private readonly Action _onChanged;

    private KidShellConfiguration _draft = KidShellConfiguration.CreateDefault();

    public ParentAppsViewModel(IAddAppFlow addAppFlow, Action onChanged)
    {
        _addAppFlow = addAppFlow;
        _onChanged = onChanged;

        AddAppCommand = new RelayCommand(() => _ = AddAppAsync());
        RemoveCommand = new RelayCommand(parameter => Remove(parameter as ParentAppRowViewModel));
    }

    public ObservableCollection<ParentAppRowViewModel> Rows { get; } = [];

    public RelayCommand AddAppCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public string Title => Strings.Get("Apps.Title");

    public string Subtitle => Strings.Format("Apps.Subtitle", _draft.Child.Name);

    public string CountSummary => Strings.Format(
        "Apps.Count",
        Rows.Count(r => r.IsEnabled),
        Rows.Count);

    public void Load(KidShellConfiguration draft)
    {
        _draft = draft;

        Rows.Clear();
        foreach (var app in draft.Apps.OrderBy(a => a.SortOrder))
        {
            Rows.Add(new ParentAppRowViewModel(app, OnRowChanged));
        }

        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(CountSummary));
    }

    private void OnRowChanged()
    {
        OnPropertyChanged(nameof(CountSummary));
        _onChanged();
    }

    private async Task AddAppAsync()
    {
        var definition = await _addAppFlow.RequestNewAppAsync(_draft.Apps);
        if (definition is null)
        {
            return;
        }

        definition.SortOrder = _draft.Apps.Count == 0 ? 0 : _draft.Apps.Max(a => a.SortOrder) + 1;
        _draft.Apps.Add(definition);
        Rows.Add(new ParentAppRowViewModel(definition, OnRowChanged));

        OnPropertyChanged(nameof(CountSummary));
        _onChanged();
    }

    private void Remove(ParentAppRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _draft.Apps.Remove(row.Definition);
        Rows.Remove(row);

        OnPropertyChanged(nameof(CountSummary));
        _onChanged();
    }
}
