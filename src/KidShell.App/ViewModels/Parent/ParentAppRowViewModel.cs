using KidShell.App.Localization;
using KidShell.App.Themes;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;
using Microsoft.UI.Xaml.Media;

namespace KidShell.App.ViewModels.Parent;

/// <summary>
/// One row in "Tillåtna appar". Writes straight through to the draft
/// configuration, so toggling is real state rather than a decorative switch -
/// it reaches Child Mode as soon as the parent saves.
/// </summary>
public sealed class ParentAppRowViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public ParentAppRowViewModel(KidAppDefinition definition, Action onChanged)
    {
        Definition = definition;
        _onChanged = onChanged;
    }

    public KidAppDefinition Definition { get; }

    public string Id => Definition.Id;

    /// <summary>Parent Mode shows the program name, Child Mode the activity name.</summary>
    public string ProgramName => Definition.EffectiveProgramName;

    public string Description => Definition.Description;

    public string IconKey => Definition.Icon;

    public Brush AccentBrush => ThemeLookup.AccentBrush(Definition.AccentStyle);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Definition.ExecutablePath);

    public string StatusText => IsConfigured
        ? Definition.ExecutablePath
        : Strings.Get("Apps.NotConfiguredTag");

    public string ToggleAutomationName => Strings.Format("Apps.ToggleAutomation", ProgramName);

    public string RemoveAutomationName => Strings.Format("Apps.RemoveAutomation", ProgramName);

    public bool IsEnabled
    {
        get => Definition.IsEnabled;
        set
        {
            if (Definition.IsEnabled == value)
            {
                return;
            }

            Definition.IsEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EnabledText));
            _onChanged();
        }
    }

    /// <summary>
    /// A text label beside the switch: state is never communicated by colour
    /// alone.
    /// </summary>
    public string EnabledText => IsEnabled
        ? Strings.Get("Apps.EnabledTag")
        : Strings.Get("Apps.DisabledTag");
}
