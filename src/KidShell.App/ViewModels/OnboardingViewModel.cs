using System.Collections.ObjectModel;
using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.App.Themes;
using KidShell.Core.Configuration;
using KidShell.Core.Security;
using KidShell.Core.Runtime;
using KidShell.Core.Mvvm;
using KidShell.Core.Onboarding;
using Microsoft.UI.Xaml.Media;

namespace KidShell.App.ViewModels;

public enum OnboardingStep
{
    Welcome = 0,

    /// <summary>
    /// The parent PIN.
    ///
    /// Second, not last. It is the one thing a Release build cannot finish
    /// without, and asking for it at the end would mean a parent discovering
    /// that after choosing a name, an avatar, an age and a theme.
    /// </summary>
    ParentPin = 1,

    Name = 2,
    Avatar = 3,
    Age = 4,
    Theme = 5,

    /// <summary>Screen time and web, grouped: both are one decision each.</summary>
    Rules = 6,

    Done = 7
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
    private readonly IRuntimeEnvironment _environment;

    private OnboardingDraft _draft = new();
    private OnboardingStep _step = OnboardingStep.Welcome;
    private string _nameText = string.Empty;
    private string _pinText = string.Empty;
    private string _pinConfirmText = string.Empty;
    private string? _validationMessage;

    public OnboardingViewModel(
        IOnboardingService onboarding,
        IDialogService dialogs,
        IRuntimeEnvironment environment)
    {
        _onboarding = onboarding;
        _dialogs = dialogs;
        _environment = environment;

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

    public static readonly int TotalSteps = Enum.GetValues<OnboardingStep>().Length;

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

    public bool IsParentPin => CurrentStep == OnboardingStep.ParentPin;

    public bool IsRules => CurrentStep == OnboardingStep.Rules;

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

    // --------------------------------------------------------- parent PIN

    public string PinText
    {
        get => _pinText;
        set
        {
            if (SetProperty(ref _pinText, value ?? string.Empty))
            {
                ValidationMessage = null;
            }
        }
    }

    public string PinConfirmText
    {
        get => _pinConfirmText;
        set
        {
            if (SetProperty(ref _pinConfirmText, value ?? string.Empty))
            {
                ValidationMessage = null;
            }
        }
    }

    /// <summary>
    /// Whether the PIN step may be skipped.
    ///
    /// Only in a developer build, and the screen says so. A Release build
    /// cannot finish setup without a real PIN, because finishing without one
    /// would leave Parent Mode either unreachable or - if the published
    /// fallback were ever reinstated - open to anybody who read the
    /// documentation.
    /// </summary>
    public bool CanSkipPin => _environment.IsDevelopment;

    public string PinBody => CanSkipPin
        ? Strings.Get("Setup.PinBodyDeveloper")
        : Strings.Get("Setup.PinBody");

    // --------------------------------------------------------------- rules

    public bool ScreenTimeEnabled
    {
        get => _draft.ScreenTimeEnabled;
        set
        {
            if (_draft.ScreenTimeEnabled == value)
            {
                return;
            }

            _draft.ScreenTimeEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RulesSummary));
        }
    }

    public double WeekdayMinutes
    {
        get => _draft.WeekdayMinutes;
        set
        {
            var minutes = (int)Math.Round(value);

            if (_draft.WeekdayMinutes == minutes)
            {
                return;
            }

            _draft.WeekdayMinutes = minutes;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WeekdayText));
            OnPropertyChanged(nameof(RulesSummary));
        }
    }

    public double WeekendMinutes
    {
        get => _draft.WeekendMinutes;
        set
        {
            var minutes = (int)Math.Round(value);

            if (_draft.WeekendMinutes == minutes)
            {
                return;
            }

            _draft.WeekendMinutes = minutes;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WeekendText));
            OnPropertyChanged(nameof(RulesSummary));
        }
    }

    public string WeekdayText => FormatMinutes(_draft.WeekdayMinutes);

    public string WeekendText => FormatMinutes(_draft.WeekendMinutes);

    public bool WebNone
    {
        get => _draft.WebMode == WebMode.NoBrowser;
        set => SetWebMode(value, WebMode.NoBrowser);
    }

    public bool WebAllowlist
    {
        get => _draft.WebMode == WebMode.Allowlist;
        set => SetWebMode(value, WebMode.Allowlist);
    }

    public bool WebOpen
    {
        get => _draft.WebMode == WebMode.Open;
        set => SetWebMode(value, WebMode.Open);
    }

    private void SetWebMode(bool isSelected, WebMode mode)
    {
        if (!isSelected || _draft.WebMode == mode)
        {
            return;
        }

        _draft.WebMode = mode;

        OnPropertyChanged(nameof(WebNone));
        OnPropertyChanged(nameof(WebAllowlist));
        OnPropertyChanged(nameof(WebOpen));
        OnPropertyChanged(nameof(RulesSummary));
    }

    /// <summary>One line summarising the rules, shown on the final screen.</summary>
    public string RulesSummary
    {
        get
        {
            var time = _draft.ScreenTimeEnabled
                ? Strings.Format("Setup.RulesTime", WeekdayText, WeekendText)
                : Strings.Get("Setup.RulesNoTime");

            var web = _draft.WebMode switch
            {
                WebMode.NoBrowser => Strings.Get("Web.ModeNone"),
                WebMode.Allowlist => Strings.Get("Web.ModeAllowlist"),
                _ => Strings.Get("Web.ModeOpen")
            };

            return $"{time} · {web}";
        }
    }

    internal static string FormatMinutes(int minutes) => minutes switch
    {
        60 => Strings.Get("ScreenTime.OneHour"),
        < 60 => Strings.Format("ScreenTime.Minutes", minutes),
        _ when minutes % 60 == 0 => Strings.Format("ScreenTime.Hours", minutes / 60),
        _ => Strings.Format("ScreenTime.HoursAndMinutes", minutes / 60, minutes % 60)
    };

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

        PinText = string.Empty;
        PinConfirmText = string.Empty;

        CurrentStep = OnboardingStep.Welcome;
        NotifyDraftState();
    }

    private void GoForward()
    {
        switch (CurrentStep)
        {
            case OnboardingStep.Welcome:
                CurrentStep = OnboardingStep.ParentPin;
                break;

            case OnboardingStep.ParentPin:
                if (!TryCommitPin())
                {
                    return;
                }

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

                CurrentStep = OnboardingStep.Rules;
                break;

            case OnboardingStep.Rules:
                // Every value has a workable default, so there is nothing to
                // validate - a parent who accepts the suggestion gets something
                // sensible rather than nothing.
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

        // The PIN is deliberately NOT restored into the boxes when stepping
        // back: a PIN sitting in a visible field is a PIN somebody can read
        // over a shoulder. The parent retypes it, which also re-confirms it.
        if (CurrentStep == OnboardingStep.Name)
        {
            PinText = string.Empty;
            PinConfirmText = string.Empty;
        }

        CurrentStep = CurrentStep - 1;
    }

    /// <summary>
    /// Validates the PIN pair and puts it on the draft.
    ///
    /// Through <see cref="ParentPinPolicy.ValidatePair"/> so the reason is the
    /// real one. Telling a parent "must be six digits" when they typed a
    /// six-digit sequence is how a form teaches somebody it is broken.
    /// </summary>
    private bool TryCommitPin()
    {
        // A developer skipping the step leaves the draft without a PIN, which
        // the fallback covers in Debug and which Release refuses outright.
        if (CanSkipPin && PinText.Length == 0 && PinConfirmText.Length == 0)
        {
            _draft.ParentPin = null;
            return true;
        }

        var validation = ParentPinPolicy.ValidatePair(PinText, PinConfirmText);

        if (validation != PinValidation.Ok)
        {
            ValidationMessage = PinMessages.Describe(validation);
            return false;
        }

        _draft.ParentPin = PinText;
        return true;
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

        // A production build without a PIN is a specific, fixable problem, and
        // it used to surface as the generic "could not save" - which left the
        // parent stuck on the last screen with no idea what to do. Send them
        // back to the step that fixes it.
        if (result == OnboardingCompletion.ParentPinRequired)
        {
            CurrentStep = OnboardingStep.ParentPin;
            ValidationMessage = Strings.Get("Setup.PinRequired");
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
        OnPropertyChanged(nameof(IsParentPin));
        OnPropertyChanged(nameof(IsRules));
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
        OnPropertyChanged(nameof(RulesSummary));
    }
}
