using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Views;

/// <summary>
/// Decorative background for both Child Mode and Parent Mode. The
/// <see cref="Theme"/> property swaps the sky so that the profile theme a
/// parent picks is actually visible to the child.
/// </summary>
public sealed partial class SceneBackground : UserControl
{
    public SceneBackground()
    {
        InitializeComponent();
        ApplyTheme(Theme);
    }

    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme),
        typeof(string),
        typeof(SceneBackground),
        new PropertyMetadata("meadow", OnThemeChanged));

    public string Theme
    {
        get => (string)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    private static void OnThemeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SceneBackground)d).ApplyTheme(e.NewValue as string);

    private void ApplyTheme(string? theme)
    {
        if (SkyMeadow is null)
        {
            return;
        }

        SkyMeadow.Opacity = theme is "sunset" or "ocean" ? 0 : 1;
        SkySunset.Opacity = theme == "sunset" ? 1 : 0;
        SkyOcean.Opacity = theme == "ocean" ? 1 : 0;
    }
}
