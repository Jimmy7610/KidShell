using System.Collections.ObjectModel;
using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.App.Themes;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;
using KidShell.Core.Onboarding;
using Microsoft.UI.Xaml.Media;

namespace KidShell.App.ViewModels;

public enum OnboardingStep
{
    Welcome = 0,
    Name = 1,
    Avatar = 2,
    Age = 3,
    Theme = 4,
    Done = 5
}

/// <summary>One avatar tile on the setup avatar screen.</summary>
public sealed class OnboardingAvatarViewModel : ObservableObject
{
    private bool _isSelected;

    public OnboardingAvatarViewModel(string id) => Id = id;

    public string Id { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>Selection is announced, not only drawn.</summary>
    public string AutomationName => Strings.Format(
        IsSelected ? "Setup.AvatarSelectedAutomation" : "Setup.AvatarAutomation",
        Id);
}

/// <summary>One age choice.</summary>
public sealed class OnboardingAgeViewModel : ObservableObject
{
    private bool _isSelected;

    public OnboardingAgeViewModel(int age, bool isOpenEnded)
    {
        Age = age;
        IsOpenEnded = isOpenEnded;
    }

    public int Age { get; }

    public bool IsOpenEnded { get; }

    public string Label => IsOpenEnded ? Strings.Get("Setup.AgeOpenEnded") : Age.ToString();

    public string AutomationName => IsOpenEnded
        ? Strings.Get("Setup.AgeOpenEndedAutomation")
        : Strings.Format("Setup.AgeYears", Age);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One theme card.</summary>
public sealed class OnboardingThemeViewModel : ObservableObject
{
    private bool _isSelected;

    public OnboardingThemeViewModel(string id) => Id = id;

    public string Id { get; }

    public string Label => Strings.Get($"Theme.{Id}");

    public string Hint => Strings.Get($"Theme.{Id}.Hint");

    /// <summary>A miniature of the scene this theme produces.</summary>
    public Brush Swatch => ThemeLookup.Brush($"ThemeSwatch{char.ToUpperInvariant(Id[0])}{Id[1..]}");

    public string AutomationName => $"{Label}. {Hint}";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// First-run setup.
///
/// Everything the parent picks lives on an <see cref="OnboardingDraft"/> held
/// here for the length of the session, which is what makes Back lossless. None
/// of it reaches the configuration file until the final screen is confirmed,
/// so closing the window half-way leaves nothing behind and setup simply runs
/// again next time.
/// </summary>
public sealed class OnboardingViewModel : ObservableObject
{
    private readonly IOnboardingService _onboarding;
    private readonly IDialogService _dialogs;

    private OnboardingDraft _draft = new();
    private OnboardingStep _step = OnboardingStep.Welcome;
    private string _nameText = string.Empty;
    private string? _validationMessage;

    public OnboardingViewModel(IOnboardingService onboarding, IDialogService dialogs)
    {
        _onboarding = onboarding;
        _dialogs = dialogs;

        foreach (var id in AvatarIds.All)
        {
            Avatars.Add(new OnboardingAvatarViewModel(id));
        }

        foreach (var age in new[] { 5, 6, 7, 8, 9 })
        {
            Ages.Add(new OnboardingAgeViewModel(age, isOpenEnded: false));
        }

        Ages.Add(new OnboardingAgeViewModel(ChildProfile.OpenEndedAge, isOpenEnded: true));

        foreach (var id in ThemeIds.All)
        {
            Themes.Add(new OnboardingThemeViewModel(id));
        }

        ContinueCommand = new RelayCommand(GoForward);
        BackCommand = new RelayCommand(GoBack);
        SelectAvatarCommand = new RelayCommand(p => SelectAvatar(p as OnboardingAvatarViewModel));
        SelectAgeCommand = new RelayCommand(p => SelectAge(p as OnboardingAgeViewModel));
        SelectThemeCommand = new RelayCommand(p => SelectTheme(p as OnboardingThemeViewModel));
    }

    public ObservableCollection<OnboardingAvatarViewModel> Avatars { get; } = [];

    public ObservableCollection<OnboardingAgeViewModel> Ages { get; } = [];

    public ObservableCollection<OnboardingThemeViewModel> Themes { get; } = [];

    public RelayCommand ContinueCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand SelectAvatarCommand { get; }

    public RelayCommand SelectAgeCommand { get; }

    public RelayCommand SelectThemeCommand { get; }

    /// <summary>Raised once the profile has been saved and Child Mode should open.</summary>
    public event EventHandler? Completed;

    /// <summary>Raised on every step change so the view can run its transition.</summary>
    public event EventHandler<OnboardingStep>? StepChanged;

    public const int TotalSteps = 6;

    public OnboardingStep CurrentStep
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value))
            {
                return;
            }

            ValidationMessage = null;
            NotifyStepState();
            StepChanged?.Invoke(this, value);
        }
    }

    public bool IsWelcome => CurrentStep == OnboardingStep.Welcome;

    public bool IsName => CurrentStep == OnboardingStep.Name;

    public bool IsAvatar => CurrentStep == OnboardingStep.Avatar;

    public bool IsAge => CurrentStep == OnboardingStep.Age;

    public bool IsTheme => CurrentStep == OnboardingStep.Theme;

    public bool IsDone => CurrentStep == OnboardingStep.Done;

    /// <summary>"Steg 2 av 6". Hidden on the welcome screen.</summary>
    public string StepIndicator => Strings.Format("Setup.StepOf", (int)CurrentStep + 1, TotalSteps);

    public bool ShowsStepIndicator => CurrentStep != OnboardingStep.Welcome;

    public bool CanGoBack => CurrentStep != OnboardingStep.Welcome;

    /// <summary>The primary button reads differently at the two ends of the flow.</summary>
    public string PrimaryButtonText => CurrentStep switch
    {
        OnboardingStep.Welcome => Strings.Get("Setup.WelcomeStart"),
        OnboardingStep.Done => Strings.Get("Setup.DoneStart"),
        _ => Strings.Get("Setup.Continue")
    };

    public string ChildName => _draft.Name;

    public string SelectedAvatarId => _draft.AvatarId;

    public string SelectedThemeId => _draft.ThemeId;

    /// <summary>Theme applied to the scene behind setup, so the choice previews live.</summary>
    public string PreviewThemeId => _draft.HasTheme ? _draft.ThemeId : ThemeIds.Default;

    public string AvatarTitle => Strings.Format("Setup.AvatarTitle", ChildName);

    public string AvatarBody => Strings.Format("Setup.AvatarBody", ChildName);

    public string AgeTitle => Strings.Format("Setup.AgeTitle", ChildName);

    public string ThemeBody => Strings.Format("Setup.ThemeBody", ChildName);

    public string DoneTitle => Strings.Format("Setup.DoneTitle", ChildName);

    public string DoneSummary => Strings.Format("Setup.DoneSummary", ChildName, _draft.Age);

    public string NameText
    {
        get => _nameText;
        set
        {
            if (SetProperty(ref _nameText, value ?? string.Empty))
            {
                ValidationMessage = null;
            }
        }
    }

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    /// <summary>Starts a clean session. Never pre-filled from an existing profile.</summary>
    public void Reset()
    {
        _draft = _onboarding.CreateDraft();
        NameText = string.Empty;
        ValidationMessage = null;

        foreach (var avatar in Avatars)
        {
            avatar.IsSelected = false;
        }

        foreach (var age in Ages)
        {
            age.IsSelected = false;
        }

        foreach (var theme in Themes)
        {
            theme.IsSelected = false;
        }

        CurrentStep = OnboardingStep.Welcome;
        NotifyDraftState();
    }

    private void GoForward()
    {
        switch (CurrentStep)
        {
            case OnboardingStep.Welcome:
                CurrentStep = OnboardingStep.Name;
                break;

            case OnboardingStep.Name:
                if (!TryCommitName())
                {
                    return;
                }

                CurrentStep = OnboardingStep.Avatar;
                break;

            case OnboardingStep.Avatar:
                if (!_draft.HasAvatar)
                {
                    ValidationMessage = Strings.Get("Setup.AvatarRequired");
                    return;
                }

                CurrentStep = OnboardingStep.Age;
                break;

            case OnboardingStep.Age:
                if (!_draft.HasAge)
                {
                    ValidationMessage = Strings.Get("Setup.AgeRequired");
                    return;
                }

                CurrentStep = OnboardingStep.Theme;
                break;

            case OnboardingStep.Theme:
                if (!_draft.HasTheme)
                {
                    ValidationMessage = Strings.Get("Setup.ThemeRequired");
                    return;
                }

                CurrentStep = OnboardingStep.Done;
                break;

            case OnboardingStep.Done:
                _ = FinishAsync();
                break;
        }
    }

    private void GoBack()
    {
        if (CurrentStep == OnboardingStep.Welcome)
        {
            return;
        }

        // Values already entered stay on the draft, so stepping back and
        // forward again shows what the parent chose.
        if (CurrentStep == OnboardingStep.Avatar)
        {
            NameText = _draft.Name;
        }

        CurrentStep = CurrentStep - 1;
    }

    private bool TryCommitName()
    {
        var validation = OnboardingDraft.ValidateName(NameText);

        if (validation != NameValidation.Ok)
        {
            ValidationMessage = Strings.Get(
                validation == NameValidation.Empty ? "Setup.NameEmpty" : "Setup.NameTooLong");
            return false;
        }

        _draft.Name = NameText;
        NotifyDraftState();
        return true;
    }

    private void SelectAvatar(OnboardingAvatarViewModel? choice)
    {
        if (choice is null)
        {
            return;
        }

        _draft.AvatarId = choice.Id;
        ValidationMessage = null;

        foreach (var avatar in Avatars)
        {
            avatar.IsSelected = avatar.Id == choice.Id;
        }

        OnPropertyChanged(nameof(SelectedAvatarId));
    }

    private void SelectAge(OnboardingAgeViewModel? choice)
    {
        if (choice is null)
        {
            return;
        }

        _draft.Age = choice.Age;
        ValidationMessage = null;

        foreach (var age in Ages)
        {
            age.IsSelected = ReferenceEquals(age, choice);
        }

        OnPropertyChanged(nameof(DoneSummary));
    }

    private void SelectTheme(OnboardingThemeViewModel? choice)
    {
        if (choice is null)
        {
            return;
        }

        _draft.ThemeId = choice.Id;
        ValidationMessage = null;

        foreach (var theme in Themes)
        {
            theme.IsSelected = theme.Id == choice.Id;
        }

        OnPropertyChanged(nameof(SelectedThemeId));
        OnPropertyChanged(nameof(PreviewThemeId));
    }

    private async Task FinishAsync()
    {
        var result = _onboarding.Complete(_draft);

        if (result == OnboardingCompletion.Completed)
        {
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Nothing was written, so the parent stays on the final screen and can
        // simply try again.
        ValidationMessage = Strings.Get("Setup.SaveFailed");
        await _dialogs.ShowMessageAsync(Strings.Get("Setup.DoneStart"), Strings.Get("Setup.SaveFailed"));
    }

    private void NotifyStepState()
    {
        OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(IsName));
        OnPropertyChanged(nameof(IsAvatar));
        OnPropertyChanged(nameof(IsAge));
        OnPropertyChanged(nameof(IsTheme));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(StepIndicator));
        OnPropertyChanged(nameof(ShowsStepIndicator));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(PrimaryButtonText));
    }

    private void NotifyDraftState()
    {
        OnPropertyChanged(nameof(ChildName));
        OnPropertyChanged(nameof(SelectedAvatarId));
        OnPropertyChanged(nameof(SelectedThemeId));
        OnPropertyChanged(nameof(PreviewThemeId));
        OnPropertyChanged(nameof(AvatarTitle));
        OnPropertyChanged(nameof(AvatarBody));
        OnPropertyChanged(nameof(AgeTitle));
        OnPropertyChanged(nameof(ThemeBody));
        OnPropertyChanged(nameof(DoneTitle));
        OnPropertyChanged(nameof(DoneSummary));
    }
}
