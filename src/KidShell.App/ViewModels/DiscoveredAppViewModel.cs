using KidShell.App.Localization;
using KidShell.Core.Apps;
using KidShell.Core.Mvvm;

namespace KidShell.App.ViewModels;

/// <summary>
/// One row in the installed-applications browser.
///
/// THE DISTINCTION THIS ROW HAS TO KEEP
/// ------------------------------------
/// "Found on this machine", "shown in KidShell" and "permitted by Windows
/// application control" are three different things, and a UI that blurs them
/// teaches a parent the wrong model of what they have configured.
///
/// This row is about the first two only: it says what was discovered, and
/// whether the parent has already added it to the child's grid. It says
/// nothing about OS-level permission, because adding a card grants none.
/// </summary>
public sealed class DiscoveredAppViewModel : ObservableObject
{
    private bool _isAlreadyAdded;

    internal DiscoveredAppViewModel(DiscoveredApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// The discovered record.
    ///
    /// Internal on purpose: exposing it publicly makes the XAML compiler
    /// generate an activator for DiscoveredApplication, which has required
    /// members and cannot be default-constructed. The template binds to the
    /// projected strings above instead, which is the better shape anyway - the
    /// view has no business reading a raw discovery record.
    /// </summary>
    internal DiscoveredApplication Application { get; }

    public string DisplayName => Application.DisplayName;

    public string Publisher => Application.Publisher;

    public bool HasPublisher => !string.IsNullOrWhiteSpace(Application.Publisher);

    /// <summary>
    /// How this application is started. Shown because it changes what a parent
    /// can expect: a packaged app has no path to point at, and a launcher
    /// starts something other than itself.
    /// </summary>
    public string KindLabel => Application.Kind switch
    {
        ApplicationKind.Win32 => Strings.Get("Discover.KindWin32"),
        ApplicationKind.Packaged => Strings.Get("Discover.KindPackaged"),
        ApplicationKind.Launcher => Strings.Get("Discover.KindLauncher"),
        ApplicationKind.Protocol => Strings.Get("Discover.KindProtocol"),
        _ => Strings.Get("Discover.KindUnknown")
    };

    /// <summary>Path or AUMID, whichever identifies this application.</summary>
    public string TargetDescription => Application.Kind == ApplicationKind.Packaged
        ? Application.Aumid
        : Application.ExecutablePath;

    /// <summary>
    /// Whether this application needs a closer look before a child gets it.
    ///
    /// A launcher is the clearest case: allowing it says nothing about what it
    /// goes on to start. A browser is the other: it can reach anything.
    /// </summary>
    public bool NeedsSecurityReview =>
        Application.Kind == ApplicationKind.Launcher ||
        Application.ProfileId is "browser" or "minecraft-launcher";

    public string SecurityReviewNote => Application.Kind == ApplicationKind.Launcher
        ? Strings.Get("Discover.ReviewLauncher")
        : Strings.Get("Discover.ReviewBrowser");

    /// <summary>Whether the target is actually present. A dead shortcut is worth saying.</summary>
    public bool IsMissing => !Application.TargetExists;

    /// <summary>Whether the parent has already put this in the child's grid.</summary>
    public bool IsAlreadyAdded
    {
        get => _isAlreadyAdded;
        set
        {
            if (SetProperty(ref _isAlreadyAdded, value))
            {
                OnPropertyChanged(nameof(AddButtonLabel));
                OnPropertyChanged(nameof(CanAdd));
            }
        }
    }

    public bool CanAdd => !IsAlreadyAdded && Application.IsLaunchable && Application.TargetExists;

    public string AddButtonLabel => IsAlreadyAdded
        ? Strings.Get("Discover.AlreadyAdded")
        : Strings.Get("Discover.Add");

    public string AutomationName => IsAlreadyAdded
        ? Strings.Format("Discover.AutomationAdded", DisplayName)
        : Strings.Format("Discover.AutomationAdd", DisplayName);
}
