using System.Collections.ObjectModel;
using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;
using KidShell.App.Themes;
using KidShell.Core.Security;
using Microsoft.UI.Xaml.Media;

namespace KidShell.App.ViewModels.Parent;

public enum SecurityState
{
    Inactive,
    Active,
    Warning
}

/// <summary>A single honest line on the security page.</summary>
public sealed class SecurityStatusViewModel(string label, string value, SecurityState state, string? hint = null)
{
    public string Label { get; } = label;

    public string Value { get; } = value;

    public SecurityState State { get; } = state;

    public string? Hint { get; } = hint;

    /// <summary>Glyph carries the state too: never colour alone.</summary>
    public string Glyph => State switch
    {
        SecurityState.Active => "",   // check mark
        SecurityState.Warning => "",  // warning
        _ => ""                        // cancel
    };

    public Brush StatusBrush => ThemeLookup.Brush(State switch
    {
        SecurityState.Active => "StatusOkBrush",
        SecurityState.Warning => "StatusWarningBrush",
        _ => "StatusInactiveBrush"
    });

    public string AutomationName => $"{Label}: {Value}";
}

/// <summary>
/// Föräldraläge → Säkerhet.
///
/// This page exists to tell the truth. MVP 0.1 has applied no Windows
/// lockdown of any kind, and every row here says so.
/// </summary>
public sealed class ParentSecurityViewModel : ObservableObject
{
    private readonly IParentPinService _pinService;
    private readonly IDeveloperOptions _developerOptions;

    public ParentSecurityViewModel(IParentPinService pinService, IDeveloperOptions developerOptions)
    {
        _pinService = pinService;
        _developerOptions = developerOptions;
        Refresh();
    }

    public ObservableCollection<SecurityStatusViewModel> Statuses { get; } = [];

    public string ConfigurationPath => AppPaths.ConfigurationFilePath;

    public string LogPath => AppPaths.LogFilePath;

    public void Refresh()
    {
        Statuses.Clear();

        Statuses.Add(new SecurityStatusViewModel(
            Strings.Get("Security.WindowsLock"),
            Strings.Get("Security.NotEnabled"),
            SecurityState.Inactive));

        Statuses.Add(new SecurityStatusViewModel(
            Strings.Get("Security.ChildAccount"),
            Strings.Get("Security.NotConfigured"),
            SecurityState.Inactive));

        Statuses.Add(new SecurityStatusViewModel(
            Strings.Get("Security.Allowlist"),
            Strings.Get("Security.NotEnabled"),
            SecurityState.Inactive));

        Statuses.Add(new SecurityStatusViewModel(
            Strings.Get("Security.Watchdog"),
            Strings.Get("Security.NotInstalled"),
            SecurityState.Inactive));

        var pinConfigured = _pinService.IsCustomPinConfigured;
        Statuses.Add(new SecurityStatusViewModel(
            Strings.Get("Security.ParentPin"),
            pinConfigured
                ? Strings.Get("Security.Active")
                : Strings.Get("Security.DevelopmentPinActive"),
            pinConfigured ? SecurityState.Active : SecurityState.Warning,
            pinConfigured || !_developerOptions.DeveloperMode
                ? null
                : Strings.Get("Pin.DeveloperHint")));
    }
}
