using KidShell.App.Localization;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;

namespace KidShell.App.ViewModels.Parent;

/// <summary>
/// Föräldraläge → Översikt.
///
/// A live summary of the draft configuration, plus a banner that makes it
/// unmistakable that nothing is actually locked down in MVP 0.1.
/// </summary>
public sealed class ParentOverviewViewModel : ObservableObject
{
    private KidShellConfiguration _draft = KidShellConfiguration.CreateDefault();

    public string Subtitle => Strings.Format("Overview.Subtitle", Strings.Genitive(_draft.Child.Name));

    public string AppsValue => Strings.Format("Overview.AppsValue", _draft.Apps.Count(a => a.IsEnabled));

    public string AppsHint => Strings.Format("Overview.AppsHint", _draft.Apps.Count);

    public string ScreenTimeValue => _draft.ScreenTime.IsEnabled
        ? Strings.Format(
            "Overview.ScreenTimeConfigured",
            _draft.ScreenTime.WeekdayMinutes,
            _draft.ScreenTime.WeekendMinutes)
        : Strings.Get("Overview.ScreenTimeNoLimit");

    public string WebValue => _draft.Web.Mode switch
    {
        WebMode.Allowlist => Strings.Get("Web.ModeAllowlist"),
        WebMode.Open => Strings.Get("Web.ModeOpen"),
        WebMode.NoBrowser => Strings.Get("Web.ModeNone"),
        _ => Strings.Get("Overview.WebNotConfigured")
    };

    /// <summary>
    /// Mirrors the Säkerhet page's readiness verdict rather than a fixed
    /// string, so the two screens can never disagree about how protected the
    /// machine is.
    /// </summary>
    public string SecurityValue { get; private set; } = Strings.Get("Overview.SecurityValue");

    public void Load(KidShellConfiguration draft, string? securitySummary = null)
    {
        if (!string.IsNullOrWhiteSpace(securitySummary))
        {
            SecurityValue = securitySummary;
        }

        _draft = draft;
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(AppsValue));
        OnPropertyChanged(nameof(AppsHint));
        OnPropertyChanged(nameof(ScreenTimeValue));
        OnPropertyChanged(nameof(WebValue));
        OnPropertyChanged(nameof(SecurityValue));
    }
}
