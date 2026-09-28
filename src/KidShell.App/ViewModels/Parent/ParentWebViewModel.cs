using System.Collections.ObjectModel;
using KidShell.App.Localization;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;
using KidShell.Core.Web;

namespace KidShell.App.ViewModels.Parent;

/// <summary>
/// Föräldraläge → Webb.
///
/// WHAT THIS CONFIGURES, AND WHAT IT DOES NOT
/// ------------------------------------------
/// The mode and the allowlist are stored, and a preview shows exactly which
/// Microsoft Edge policy values would be written. Nothing is written here: a
/// browser policy is a machine change and goes through the security
/// transaction at secure setup, like everything else that touches Windows.
///
/// It also configures ONE browser. A different browser, a game's built-in
/// store page, an in-app web view and a help link that opens a URL are all
/// outside it - which is why the honest protection is application control
/// deciding what may run at all, with browser policy as a second layer. The
/// page says so rather than implying the web is now safe.
/// </summary>
public sealed class ParentWebViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private KidShellConfiguration _draft = KidShellConfiguration.CreateDefault();
    private string _newDomain = string.Empty;
    private string? _validationMessage;

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
        set
        {
            if (SetProperty(ref _newDomain, value))
            {
                // Clear the previous complaint as soon as they start fixing it.
                ValidationMessage = null;
            }
        }
    }

    public bool HasDomains => AllowedDomains.Count > 0;

    /// <summary>Why the last entry was refused, in Swedish. Null when fine.</summary>
    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage => !string.IsNullOrEmpty(_validationMessage);

    // ----------------------------------------------------------- the policy

    /// <summary>
    /// The exact Edge policy values this configuration would produce.
    ///
    /// Shown before anything is applied, because "review the exact machine
    /// changes" is not a slogan if the parent cannot see them.
    /// </summary>
    public string PolicyPreview
    {
        get
        {
            var policy = BrowserPolicyGenerator.Generate(_draft.Web, BuildEntries());
            return BrowserPolicyGenerator.ToRegistryPreview(policy);
        }
    }

    /// <summary>Warnings the generator produced about this configuration.</summary>
    public IReadOnlyList<string> PolicyWarnings =>
        BrowserPolicyGenerator.Generate(_draft.Web, BuildEntries()).Warnings;

    public bool HasPolicyWarnings => PolicyWarnings.Count > 0;

    private IReadOnlyList<AllowlistEntry> BuildEntries()
    {
        var entries = new List<AllowlistEntry>();

        foreach (var domain in _draft.Web.AllowedDomains)
        {
            var (result, entry) = WebAllowlist.TryCreate(domain, entries);

            if (result == UrlValidation.Ok && entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    // ------------------------------------------------------------ the list

    public void Load(KidShellConfiguration draft)
    {
        _draft = draft;
        ValidationMessage = null;

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
        // Through the tested validator, not a local string trim. The previous
        // version stripped a scheme prefix and accepted whatever was left,
        // which meant "javascript:alert(1)" became "alert(1)" and was added as
        // a host - a dangerous scheme laundered into the allowlist.
        var (result, entry) = WebAllowlist.TryCreate(NewDomain, BuildEntries());

        if (result != UrlValidation.Ok || entry is null)
        {
            ValidationMessage = WebAllowlist.Describe(result);
            return;
        }

        AllowedDomains.Add(entry.Host);
        _draft.Web.AllowedDomains.Add(entry.Host);

        NewDomain = string.Empty;
        ValidationMessage = null;

        OnPropertyChanged(nameof(HasDomains));
        NotifyPolicyChanged();
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
        NotifyPolicyChanged();
        _onChanged();
    }

    /// <summary>
    /// Replaces one entry with a corrected one, so fixing a typo does not mean
    /// removing and re-adding.
    /// </summary>
    public bool EditDomain(string? original, string? replacement)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return false;
        }

        var others = BuildEntries()
            .Where(e => !string.Equals(e.Host, original, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var (result, entry) = WebAllowlist.TryCreate(replacement, others);

        if (result != UrlValidation.Ok || entry is null)
        {
            ValidationMessage = WebAllowlist.Describe(result);
            return false;
        }

        var index = AllowedDomains.IndexOf(original);

        if (index < 0)
        {
            return false;
        }

        AllowedDomains[index] = entry.Host;

        var draftIndex = _draft.Web.AllowedDomains
            .FindIndex(d => string.Equals(d, original, StringComparison.OrdinalIgnoreCase));

        if (draftIndex >= 0)
        {
            _draft.Web.AllowedDomains[draftIndex] = entry.Host;
        }

        ValidationMessage = null;
        NotifyPolicyChanged();
        _onChanged();
        return true;
    }

    private void SetMode(bool isSelected, WebMode mode)
    {
        if (!isSelected || _draft.Web.Mode == mode)
        {
            return;
        }

        _draft.Web.Mode = mode;
        NotifyModeChanged();
        NotifyPolicyChanged();
        _onChanged();
    }

    private void NotifyModeChanged()
    {
        OnPropertyChanged(nameof(IsNoBrowser));
        OnPropertyChanged(nameof(IsAllowlist));
        OnPropertyChanged(nameof(IsOpenWeb));
    }

    private void NotifyPolicyChanged()
    {
        OnPropertyChanged(nameof(PolicyPreview));
        OnPropertyChanged(nameof(PolicyWarnings));
        OnPropertyChanged(nameof(HasPolicyWarnings));
    }
}
