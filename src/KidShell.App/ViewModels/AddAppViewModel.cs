using System.Collections.ObjectModel;
using KidShell.App.Localization;
using KidShell.App.Services;
using KidShell.App.Themes;
using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using KidShell.Core.Mvvm;
using Microsoft.UI.Xaml.Media;

namespace KidShell.App.ViewModels;

public sealed class IconChoice(string key, string label)
{
    public string Key { get; } = key;

    public string Label { get; } = label;
}

public sealed class AccentChoice(AccentStyle style, string label)
{
    public AccentStyle Style { get; } = style;

    public string Label { get; } = label;

    public Brush Brush => ThemeLookup.AccentBrush(Style);
}

/// <summary>
/// Backs the "Lägg till app" dialog: a parent points KidShell at a program on
/// this machine and decides how the card looks to the child.
/// </summary>
public sealed class AddAppViewModel : ObservableObject
{
    private readonly IFilePickerService _picker;
    private readonly IExecutableResolver _resolver;

    private string _displayName = string.Empty;
    private string _programName = string.Empty;
    private string _executablePath = string.Empty;
    private ApplicationLaunchKind _launchKind = ApplicationLaunchKind.Win32Executable;
    private string _arguments = string.Empty;
    private string _category = string.Empty;
    private string? _validationMessage;
    private int _selectedIconIndex;
    private int _selectedAccentIndex;

    public AddAppViewModel(IFilePickerService picker, IExecutableResolver resolver)
    {
        _picker = picker;
        _resolver = resolver;

        Icons.Add(new IconChoice(IconKeys.Paint, "Rita"));
        Icons.Add(new IconChoice(IconKeys.Games, "Spel"));
        Icons.Add(new IconChoice(IconKeys.Learn, "Lära"));
        Icons.Add(new IconChoice(IconKeys.Calculator, "Räkna"));
        Icons.Add(new IconChoice(IconKeys.Music, "Musik"));
        Icons.Add(new IconChoice(IconKeys.Reading, "Läsa"));
        Icons.Add(new IconChoice(IconKeys.Video, "Film"));
        Icons.Add(new IconChoice(IconKeys.Blocks, "Bygga"));
        Icons.Add(new IconChoice(IconKeys.Browser, "Webb"));
        Icons.Add(new IconChoice(IconKeys.Generic, "Annat"));

        Accents.Add(new AccentChoice(AccentStyle.Rose, "Rosa"));
        Accents.Add(new AccentChoice(AccentStyle.Grass, "Grön"));
        Accents.Add(new AccentChoice(AccentStyle.Sun, "Gul"));
        Accents.Add(new AccentChoice(AccentStyle.Sky, "Blå"));
        Accents.Add(new AccentChoice(AccentStyle.Grape, "Lila"));
        Accents.Add(new AccentChoice(AccentStyle.Coral, "Korall"));
        Accents.Add(new AccentChoice(AccentStyle.Teal, "Turkos"));
        Accents.Add(new AccentChoice(AccentStyle.Leaf, "Limegrön"));

        BrowseCommand = new RelayCommand(() => _ = BrowseAsync());
    }

    /// <summary>
    /// Fills the form from something the parent picked out of the installed-
    /// applications list, so the common path is "choose Paint, press Add"
    /// rather than "type a path".
    ///
    /// A packaged app has no executable a parent could point at - the AUMID is
    /// its identity, both for launching and for future application control -
    /// so it goes into the same field the launcher reads.
    /// </summary>
    public void PrefillFrom(KidShell.Core.Apps.DiscoveredApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        DisplayName = application.DisplayName;
        ProgramName = application.DisplayName;
        ExecutablePath = application.LaunchTarget;
        Arguments = application.Arguments;

        // What the catalogue found it to BE, carried across rather than
        // guessed back out of the string afterwards. Without this a Store app
        // arrived here as a bare AUMID and was then judged by the rule for
        // hand-typed paths, which requires .exe - so every packaged
        // application in the browse list was refused on the next screen.
        _launchKind = application.Kind switch
        {
            KidShell.Core.Apps.ApplicationKind.Packaged => ApplicationLaunchKind.PackagedApp,
            KidShell.Core.Apps.ApplicationKind.Protocol => ApplicationLaunchKind.UriProtocol,
            _ => ApplicationLaunchKind.Win32Executable
        };

        // A guessed icon is better than the generic one, and the parent can
        // still change it before adding.
        SelectedIconIndex = GuessIconIndex(application.DisplayName);
    }

    /// <summary>
    /// Picks a plausible icon from the name. Deliberately crude: it is a
    /// starting point for the parent, not a classification.
    /// </summary>
    internal int GuessIconIndex(string displayName)
    {
        var name = displayName.ToLowerInvariant();

        var key = name switch
        {
            _ when name.Contains("paint") || name.Contains("rita") || name.Contains("draw") => IconKeys.Paint,
            _ when name.Contains("calc") || name.Contains("räkna") || name.Contains("miniräkn") => IconKeys.Calculator,
            _ when name.Contains("music") || name.Contains("musik") || name.Contains("spotify") => IconKeys.Music,
            _ when name.Contains("video") || name.Contains("film") || name.Contains("vlc") || name.Contains("media") => IconKeys.Video,
            _ when name.Contains("book") || name.Contains("läs") || name.Contains("read") => IconKeys.Reading,
            _ when name.Contains("edge") || name.Contains("chrome") || name.Contains("firefox") || name.Contains("webb") => IconKeys.Browser,
            _ when name.Contains("minecraft") || name.Contains("lego") || name.Contains("bygg") => IconKeys.Blocks,
            _ when name.Contains("game") || name.Contains("spel") || name.Contains("scratch") => IconKeys.Games,
            _ when name.Contains("learn") || name.Contains("lär") || name.Contains("skol") => IconKeys.Learn,
            _ => IconKeys.Generic
        };

        var index = Icons.ToList().FindIndex(i => i.Key == key);
        return index >= 0 ? index : Icons.Count - 1;
    }

    public ObservableCollection<IconChoice> Icons { get; } = [];

    public ObservableCollection<AccentChoice> Accents { get; } = [];

    public RelayCommand BrowseCommand { get; }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
            {
                ValidationMessage = null;
            }
        }
    }

    public string ProgramName
    {
        get => _programName;
        set => SetProperty(ref _programName, value);
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set
        {
            if (SetProperty(ref _executablePath, value))
            {
                ValidationMessage = null;

                // Editing the field makes this a hand-typed path again,
                // whatever it was prefilled from. A parent who prefills from a
                // Store app and then types over the identity has typed a path,
                // and it must be judged as one - otherwise the packaged rules,
                // which do not check for scripts or escape surfaces, would be
                // applied to something arbitrary.
                _launchKind = ApplicationLaunchKind.Win32Executable;
            }
        }
    }

    public string Arguments
    {
        get => _arguments;
        set => SetProperty(ref _arguments, value);
    }

    public string Category
    {
        get => _category;
        set => SetProperty(ref _category, value);
    }

    public int SelectedIconIndex
    {
        get => _selectedIconIndex;
        set => SetProperty(ref _selectedIconIndex, value);
    }

    public int SelectedAccentIndex
    {
        get => _selectedAccentIndex;
        set => SetProperty(ref _selectedAccentIndex, value);
    }

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    /// <summary>
    /// Validates the form. A missing program is reported but never throws, and
    /// an app with no path is still allowed - it simply shows the friendly
    /// "inte konfigurerat ännu" card in Child Mode.
    /// </summary>
    public bool TryBuild(out KidAppDefinition? definition)
    {
        definition = null;

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ValidationMessage = Strings.Get("AddApp.NameRequired");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ExecutablePath))
        {
            // Judged by the rule that belongs to the KIND of thing this is.
            // A batch file that exists is still a batch file, and running one
            // runs an interpreter KidShell's own policy refuses - but a Store
            // app has no extension at all, and demanding one refused every
            // packaged application on the machine.
            var check = LaunchTargetPolicy.Check(_launchKind, ExecutablePath);

            if (!check.IsAllowed)
            {
                ValidationMessage = Strings.Get(check.ResourceKey);
                return false;
            }

            // Only an executable is a file the resolver can look for. An AUMID
            // is an identity Windows holds, not a path on disk, so asking the
            // file system about one always answers "not found".
            if (_launchKind == ApplicationLaunchKind.Win32Executable)
            {
                var resolution = _resolver.Resolve(ExecutablePath);
                if (resolution.Kind == ExecutableResolutionKind.NotFound)
                {
                    ValidationMessage = Strings.Get("AddApp.PathMissing");
                    return false;
                }
            }
        }

        var icon = Icons.ElementAtOrDefault(SelectedIconIndex) ?? Icons[^1];
        var accent = Accents.ElementAtOrDefault(SelectedAccentIndex) ?? Accents[0];

        definition = new KidAppDefinition
        {
            Id = Guid.NewGuid().ToString("n"),
            DisplayName = DisplayName.Trim(),
            ProgramName = ProgramName.Trim(),
            Description = Category.Trim(),
            Category = Category.Trim(),
            Icon = icon.Key,
            AccentStyle = accent.Style,
            IsEnabled = true,
            LaunchKind = _launchKind,
            ExecutablePath = ExecutablePath.Trim(),
            Arguments = Arguments.Trim()
        };

        return true;
    }

    private async Task BrowseAsync()
    {
        var path = await _picker.PickProgramAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ExecutablePath = path;

        // Picking a file replaces whatever kind was prefilled: a path from the
        // file picker is an executable, and must be judged as one.
        _launchKind = ApplicationLaunchKind.Win32Executable;

        if (string.IsNullOrWhiteSpace(ProgramName))
        {
            ProgramName = Path.GetFileNameWithoutExtension(path);
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            DisplayName = Path.GetFileNameWithoutExtension(path);
        }
    }
}
