using KidShell.Core.Configuration;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace KidShell.App.Views;

/// <summary>
/// Decorative background for Child Mode, Parent Mode and first-run setup.
///
/// One scene, five palettes. <see cref="Theme"/> recolours the shared brushes
/// and toggles the themed extras, so a theme the parent picks during setup is
/// immediately and obviously different rather than a relabelled shade.
/// </summary>
public sealed partial class SceneBackground : UserControl
{
    /// <summary>The colours and switches that make up one scene theme.</summary>
    private sealed record ScenePalette(
        string SkyTop,
        string SkyMid,
        string SkyBottom,
        string HillFar,
        string HillMid,
        string HillNear,
        string Water,
        string Mountain,
        string MountainCap,
        string TreeDark,
        string TreeLight,
        string Trunk,
        string Foam,
        string SunCore,
        string SunGlow,
        string SunRay,
        string Cloud,
        string Bloom,
        bool ShowSun = true,
        bool ShowMoon = false,
        bool ShowStars = false,
        bool ShowClouds = true,
        bool ShowMountains = true,
        bool ShowVolcano = false,
        bool ShowFlora = true,
        bool ShowLake = true,
        bool ShowSea = false);

    private static readonly Dictionary<string, ScenePalette> Palettes = new(StringComparer.Ordinal)
    {
        // Skogen - the original KidShell meadow.
        [ThemeIds.Forest] = new ScenePalette(
            SkyTop: "#FFA9D8F4", SkyMid: "#FFCFE9FA", SkyBottom: "#FFEDF7FD",
            HillFar: "#FF9FD7A0", HillMid: "#FF76C77A", HillNear: "#FF59B463",
            Water: "#FF78C0E8", Mountain: "#FF8FB2D1", MountainCap: "#FFE8F1F8",
            TreeDark: "#FF3F9A54", TreeLight: "#FF63B96C", Trunk: "#FF8A6046",
            Foam: "#E6FFFFFF", SunCore: "#FFFCD65B", SunGlow: "#3DFBCB3A", SunRay: "#FFF6C63A",
            Cloud: "#D9FFFFFF", Bloom: "#FFFFFFFF"),

        // Rymden - night sky, craters, no clouds.
        [ThemeIds.Space] = new ScenePalette(
            SkyTop: "#FF141A3C", SkyMid: "#FF2C2A6B", SkyBottom: "#FF4A3E8C",
            HillFar: "#FF4A4478", HillMid: "#FF3A3563", HillNear: "#FF2C2950",
            Water: "#FF5B54A8", Mountain: "#FF5A5390", MountainCap: "#FFCFCBEA",
            TreeDark: "#FF3A3563", TreeLight: "#FF4A4478", Trunk: "#FF2C2950",
            Foam: "#E6EDEBFA", SunCore: "#FFEFF2F8", SunGlow: "#33FFFFFF", SunRay: "#66FFFFFF",
            Cloud: "#26FFFFFF", Bloom: "#FFCFCBEA",
            ShowSun: false, ShowMoon: true, ShowStars: true, ShowClouds: false,
            ShowMountains: true, ShowFlora: false, ShowLake: true),

        // Havet - open water all the way to the horizon.
        [ThemeIds.Ocean] = new ScenePalette(
            SkyTop: "#FF63C1E8", SkyMid: "#FFA8E0F2", SkyBottom: "#FFE0F6FC",
            HillFar: "#FF7FD3C6", HillMid: "#FF4FBFC0", HillNear: "#FF2E9FB5",
            Water: "#FF2FA3C9", Mountain: "#FF8FC9D8", MountainCap: "#FFEAF7FC",
            TreeDark: "#FF3F9A7F", TreeLight: "#FF63B99C", Trunk: "#FF8A6046",
            Foam: "#E6F2FBFF", SunCore: "#FFFCE07E", SunGlow: "#3DFFE9A3", SunRay: "#FFF8CE58",
            Cloud: "#E6FFFFFF", Bloom: "#FFFFFFFF",
            ShowMountains: false, ShowFlora: false, ShowLake: false, ShowSea: true),

        // Dinosaurier - warm prehistoric light and a volcano.
        [ThemeIds.Dino] = new ScenePalette(
            SkyTop: "#FFF7B27A", SkyMid: "#FFFBD3A6", SkyBottom: "#FFFDEBD6",
            HillFar: "#FF9CB86A", HillMid: "#FF7DA254", HillNear: "#FF5F8742",
            Water: "#FF64AE9C", Mountain: "#FFA78A7C", MountainCap: "#FFE4D5CC",
            TreeDark: "#FF3F7A3C", TreeLight: "#FF64A24C", Trunk: "#FF7A5236",
            Foam: "#E6FFF6EC", SunCore: "#FFFBA95B", SunGlow: "#3DF08E45", SunRay: "#FFEF9B45",
            Cloud: "#B3FFF0E2", Bloom: "#FFFBE6B8",
            ShowMountains: false, ShowVolcano: true),

        // Färgglatt - the loud one.
        [ThemeIds.Bright] = new ScenePalette(
            SkyTop: "#FFFFA8D2", SkyMid: "#FFFFD79B", SkyBottom: "#FFFFF3C9",
            HillFar: "#FF8ED9E8", HillMid: "#FF7FC8F0", HillNear: "#FFA88BEA",
            Water: "#FF56C5E8", Mountain: "#FFBFA4F0", MountainCap: "#FFFFF2FA",
            TreeDark: "#FF3FB08A", TreeLight: "#FF62D0A2", Trunk: "#FF9A6A4E",
            Foam: "#E6FFFFFF", SunCore: "#FFFFE45B", SunGlow: "#4DFF8AC4", SunRay: "#FFFF9AD0",
            Cloud: "#E6FFFFFF", Bloom: "#FFFFFFFF")
    };

    public SceneBackground()
    {
        InitializeComponent();
        ApplyTheme(Theme);
    }

    public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
        nameof(Theme),
        typeof(string),
        typeof(SceneBackground),
        new PropertyMetadata(ThemeIds.Default, OnThemeChanged));

    public string Theme
    {
        get => (string)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    private static void OnThemeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SceneBackground)d).ApplyTheme(e.NewValue as string);

    private void ApplyTheme(string? theme)
    {
        if (SceneRoot is null)
        {
            return;
        }

        var key = ThemeIds.Migrate(theme);
        if (!Palettes.TryGetValue(key, out var palette))
        {
            palette = Palettes[ThemeIds.Default];
        }

        SkyStopTop.Color = Parse(palette.SkyTop);
        SkyStopMid.Color = Parse(palette.SkyMid);
        SkyStopBottom.Color = Parse(palette.SkyBottom);

        SetBrush("SceneHillFar", palette.HillFar);
        SetBrush("SceneHillMid", palette.HillMid);
        SetBrush("SceneHillNear", palette.HillNear);
        SetBrush("SceneWater", palette.Water);
        SetBrush("SceneMountain", palette.Mountain);
        SetBrush("SceneMountainCap", palette.MountainCap);
        SetBrush("SceneTreeDark", palette.TreeDark);
        SetBrush("SceneTreeLight", palette.TreeLight);
        SetBrush("SceneTrunk", palette.Trunk);
        SetBrush("SceneFoam", palette.Foam);
        SetBrush("SceneSunCore", palette.SunCore);
        SetBrush("SceneSunGlow", palette.SunGlow);
        SetBrush("SceneSunRay", palette.SunRay);
        SetBrush("SceneCloud", palette.Cloud);
        SetBrush("SceneBloom", palette.Bloom);

        SunLayer.Visibility = Show(palette.ShowSun);
        MoonLayer.Visibility = Show(palette.ShowMoon);
        StarLayer.Visibility = Show(palette.ShowStars);
        CloudLayer.Visibility = Show(palette.ShowClouds);
        VolcanoLayer.Visibility = Show(palette.ShowVolcano);
        FloraLayer.Visibility = Show(palette.ShowFlora);
        BloomLayer.Visibility = Show(palette.ShowFlora);
        LakeLayer.Visibility = Show(palette.ShowLake);
        SeaLayer.Visibility = Show(palette.ShowSea);

        var mountains = Show(palette.ShowMountains);
        MountainLayer.Visibility = mountains;
        MountainCapA.Visibility = mountains;
        MountainCapB.Visibility = mountains;

        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBrush(string key, string argb)
    {
        if (Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
        {
            brush.Color = Parse(argb);
        }
    }

    /// <summary>Parses "#AARRGGBB". Falls back to transparent rather than throwing.</summary>
    private static Color Parse(string argb)
    {
        if (argb.Length == 9 &&
            argb[0] == '#' &&
            byte.TryParse(argb.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var a) &&
            byte.TryParse(argb.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(argb.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(argb.AsSpan(7, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return Color.FromArgb(a, r, g, b);
        }

        return Colors.Transparent;
    }
}
