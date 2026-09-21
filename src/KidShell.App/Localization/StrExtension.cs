using Microsoft.UI.Xaml.Markup;

namespace KidShell.App.Localization;

/// <summary>
/// XAML access to <see cref="Strings"/>:
/// <c>Text="{loc:Str Key=Child.Tagline}"</c>.
/// Keeps literal UI text out of the views.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class StrExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue() => Strings.Get(Key);
}
