namespace KidShell.Core.Configuration;

/// <summary>
/// Named colour role for a child app card. The concrete colours live in the
/// App layer design system (Themes/Tokens.xaml) so that Core stays free of
/// any presentation dependency and a future theme can remap them.
/// </summary>
public enum AccentStyle
{
    Rose = 0,
    Grass = 1,
    Sun = 2,
    Sky = 3,
    Grape = 4,
    Coral = 5,
    Teal = 6,
    Leaf = 7
}
