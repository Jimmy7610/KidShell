using KidShell.App.Themes;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;
using Microsoft.UI.Xaml.Media;

namespace KidShell.App.ViewModels;

/// <summary>One card in the child app grid. A projection of a
/// <see cref="KidAppDefinition"/> - the grid is never authored in XAML.</summary>
public sealed class ChildAppTileViewModel : ObservableObject
{
    public ChildAppTileViewModel(KidAppDefinition definition)
    {
        Definition = definition;
    }

    public KidAppDefinition Definition { get; }

    public string Id => Definition.Id;

    public string DisplayName => Definition.DisplayName;

    public string IconKey => Definition.Icon;

    public Brush AccentBrush => ThemeLookup.AccentBrush(Definition.AccentStyle);

    /// <summary>What a screen reader announces for the card.</summary>
    public string AutomationName => string.IsNullOrWhiteSpace(Definition.Description)
        ? Definition.DisplayName
        : $"{Definition.DisplayName}. {Definition.Description}";
}
