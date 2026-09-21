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
            var resolution = _resolver.Resolve(ExecutablePath);
            if (resolution.Kind == ExecutableResolutionKind.NotFound)
            {
                ValidationMessage = Strings.Get("AddApp.PathMissing");
                return false;
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
