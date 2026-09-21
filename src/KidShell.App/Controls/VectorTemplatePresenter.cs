using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Controls;

/// <summary>
/// Resolves a vector <see cref="DataTemplate"/> from the application resources
/// by key and presents it. Icons and avatars are stored as XAML shapes
/// (Themes/AppIcons.xaml, Themes/Avatars.xaml) rather than bitmaps, so they
/// stay sharp at every DPI and the repository needs no image licences.
///
/// The canvas inside each template is 100x100; wrap the presenter in a
/// Viewbox to size it.
/// </summary>
public abstract partial class VectorTemplatePresenter : ContentControl
{
    protected VectorTemplatePresenter()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>Prefix used to build the resource key, e.g. "Icon_".</summary>
    protected abstract string ResourcePrefix { get; }

    /// <summary>Key used when the requested one is unknown.</summary>
    protected abstract string FallbackKey { get; }

    protected void ApplyKey(string? key)
    {
        var template = Resolve(key) ?? Resolve(FallbackKey);

        // A ContentPresenter needs non-null content before it will realise a
        // template, so the key doubles as the content value.
        Content = key ?? FallbackKey;
        ContentTemplate = template;
    }

    private DataTemplate? Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var resourceKey = ResourcePrefix + key;

        if (Application.Current?.Resources is { } resources &&
            resources.TryGetValue(resourceKey, out var value) &&
            value is DataTemplate template)
        {
            return template;
        }

        return null;
    }
}

/// <summary>Draws one of the KidShell app icons.</summary>
public sealed partial class AppIconPresenter : VectorTemplatePresenter
{
    public static readonly DependencyProperty IconKeyProperty = DependencyProperty.Register(
        nameof(IconKey),
        typeof(string),
        typeof(AppIconPresenter),
        new PropertyMetadata(null, OnIconKeyChanged));

    public string? IconKey
    {
        get => (string?)GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    protected override string ResourcePrefix => "Icon_";

    protected override string FallbackKey => "generic";

    private static void OnIconKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AppIconPresenter)d).ApplyKey(e.NewValue as string);
}

/// <summary>Draws one of the KidShell avatars.</summary>
public sealed partial class AvatarPresenter : VectorTemplatePresenter
{
    public static readonly DependencyProperty AvatarIdProperty = DependencyProperty.Register(
        nameof(AvatarId),
        typeof(string),
        typeof(AvatarPresenter),
        new PropertyMetadata(null, OnAvatarIdChanged));

    public string? AvatarId
    {
        get => (string?)GetValue(AvatarIdProperty);
        set => SetValue(AvatarIdProperty, value);
    }

    protected override string ResourcePrefix => "Avatar_";

    protected override string FallbackKey => "fox";

    private static void OnAvatarIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AvatarPresenter)d).ApplyKey(e.NewValue as string);
}
