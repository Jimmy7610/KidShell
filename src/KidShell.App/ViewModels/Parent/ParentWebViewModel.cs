using System.Collections.ObjectModel;
using KidShell.App.Localization;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;

namespace KidShell.App.ViewModels.Parent;

/// <summary>
/// Föräldraläge → Webb.
///
/// The chosen mode and the allowlist are stored locally. MVP 0.1 changes no
/// browser settings and sets no policies.
/// </summary>
public sealed class ParentWebViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private KidShellConfiguration _draft = KidShellConfiguration.CreateDefault();
    private string _newDomain = string.Empty;

    public ParentWebViewModel(Action onChanged) => _onChanged = onChanged;

    public ObservableCollection<string> AllowedDomains { get; } = [];

    public string Subtitle => Strings.Format("Web.Subtitle", _draft.Child.Name);

    public bool IsNoBrowser
    {
        get => _draft.Web.Mode == WebMode.NoBrowser;
        set => SetMode(value, WebMode.NoBrowser);
    }

    public bool IsAllowlist
    {
        get => _draft.Web.Mode == WebMode.Allowlist;
        set => SetMode(value, WebMode.Allowlist);
    }

    public bool IsOpenWeb
    {
        get => _draft.Web.Mode == WebMode.Open;
        set => SetMode(value, WebMode.Open);
    }

    public string NewDomain
    {
        get => _newDomain;
        set => SetProperty(ref _newDomain, value);
    }

    public bool HasDomains => AllowedDomains.Count > 0;

    public void Load(KidShellConfiguration draft)
    {
        _draft = draft;

        AllowedDomains.Clear();
        foreach (var domain in draft.Web.AllowedDomains)
        {
            AllowedDomains.Add(domain);
        }

        NotifyModeChanged();
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HasDomains));
    }

    public void AddDomain()
    {
        var domain = Normalize(NewDomain);

        if (domain.Length == 0 || AllowedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            NewDomain = string.Empty;
            return;
        }

        AllowedDomains.Add(domain);
        _draft.Web.AllowedDomains.Add(domain);
        NewDomain = string.Empty;
        OnPropertyChanged(nameof(HasDomains));
        _onChanged();
    }

    public void RemoveDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return;
        }

        AllowedDomains.Remove(domain);
        _draft.Web.AllowedDomains.RemoveAll(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasDomains));
        _onChanged();
    }

    /// <summary>Strips scheme, path and stray whitespace from typed input.</summary>
    internal static string Normalize(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant() ?? string.Empty;

        if (value.Length == 0)
        {
            return string.Empty;
        }

        foreach (var prefix in new[] { "https://", "http://", "www." })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                value = value[prefix.Length..];
            }
        }

        var slash = value.IndexOf('/');
        if (slash >= 0)
        {
            value = value[..slash];
        }

        return value.Trim();
    }

    private void SetMode(bool isSelected, WebMode mode)
    {
        if (!isSelected || _draft.Web.Mode == mode)
        {
            return;
        }

        _draft.Web.Mode = mode;
        NotifyModeChanged();
        _onChanged();
    }

    private void NotifyModeChanged()
    {
        OnPropertyChanged(nameof(IsNoBrowser));
        OnPropertyChanged(nameof(IsAllowlist));
        OnPropertyChanged(nameof(IsOpenWeb));
    }
}
