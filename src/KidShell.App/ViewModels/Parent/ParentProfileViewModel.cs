using System.Collections.ObjectModel;
using KidShell.App.Localization;
using KidShell.App.Themes;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;

namespace KidShell.App.ViewModels.Parent;

/// <summary>One selectable avatar in the profile page.</summary>
public sealed class AvatarChoiceViewModel : ObservableObject
{
    private bool _isSelected;

    public AvatarChoiceViewModel(string id) => Id = id;

    public string Id { get; }

    public string AutomationName => Strings.Format("Profile.AvatarAutomation", Id);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>Föräldraläge → Profil. Changes reach Child Mode on save.</summary>
public sealed class ParentProfileViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private KidShellConfiguration _draft = KidShellConfiguration.CreateDefault();
    private bool _suppress;

    public ParentProfileViewModel(Action onChanged)
    {
        _onChanged = onChanged;

        foreach (var id in ThemeLookup.AvatarIds)
        {
            Avatars.Add(new AvatarChoiceViewModel(id));
        }

        ThemeChoices.Add(new ThemeChoice("meadow", Strings.Get("Profile.ThemeMeadow")));
        ThemeChoices.Add(new ThemeChoice("sunset", Strings.Get("Profile.ThemeSunset")));
        ThemeChoices.Add(new ThemeChoice("ocean", Strings.Get("Profile.ThemeOcean")));

        SelectAvatarCommand = new RelayCommand(parameter => SelectAvatar(parameter as AvatarChoiceViewModel));
    }

    public sealed record ThemeChoice(string Id, string Label);

    public ObservableCollection<AvatarChoiceViewModel> Avatars { get; } = [];

    public ObservableCollection<ThemeChoice> ThemeChoices { get; } = [];

    public RelayCommand SelectAvatarCommand { get; }

    public string Name
    {
        get => _draft.Child.Name;
        set
        {
            var name = value ?? string.Empty;
            if (_draft.Child.Name == name)
            {
                return;
            }

            _draft.Child.Name = name;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewGreeting));
            NotifyChanged();
        }
    }

    public double Age
    {
        get => _draft.Child.Age;
        set
        {
            var age = (int)Math.Round(value);
            if (_draft.Child.Age == age)
            {
                return;
            }

            _draft.Child.Age = age;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PreviewSummary));
            NotifyChanged();
        }
    }

    public string AvatarId => _draft.Child.AvatarId;

    public int SelectedThemeIndex
    {
        get
        {
            var index = ThemeChoices.ToList().FindIndex(t => t.Id == _draft.Child.ThemeId);
            return index < 0 ? 0 : index;
        }
        set
        {
            if (value < 0 || value >= ThemeChoices.Count)
            {
                return;
            }

            var id = ThemeChoices[value].Id;
            if (_draft.Child.ThemeId == id)
            {
                return;
            }

            _draft.Child.ThemeId = id;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public string PreviewGreeting => Strings.Format("Child.Greeting", _draft.Child.Name);

    public string PreviewSummary => Strings.Format("Parent.ChildSummary", _draft.Child.Name, _draft.Child.Age);

    public void Load(KidShellConfiguration draft)
    {
        _suppress = true;
        _draft = draft;

        foreach (var avatar in Avatars)
        {
            avatar.IsSelected = avatar.Id == draft.Child.AvatarId;
        }

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Age));
        OnPropertyChanged(nameof(AvatarId));
        OnPropertyChanged(nameof(SelectedThemeIndex));
        OnPropertyChanged(nameof(PreviewGreeting));
        OnPropertyChanged(nameof(PreviewSummary));
        _suppress = false;
    }

    private void SelectAvatar(AvatarChoiceViewModel? choice)
    {
        if (choice is null || _draft.Child.AvatarId == choice.Id)
        {
            return;
        }

        _draft.Child.AvatarId = choice.Id;

        foreach (var avatar in Avatars)
        {
            avatar.IsSelected = avatar.Id == choice.Id;
        }

        OnPropertyChanged(nameof(AvatarId));
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        if (!_suppress)
        {
            _onChanged();
        }
    }
}
