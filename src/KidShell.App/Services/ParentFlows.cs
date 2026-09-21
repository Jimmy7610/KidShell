using KidShell.App.Localization;
using KidShell.App.ViewModels;
using KidShell.App.Views.Dialogs;
using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Services;

/// <summary>Shows the "Lägg till app" dialog and returns what the parent configured.</summary>
public interface IAddAppFlow
{
    Task<KidAppDefinition?> RequestNewAppAsync();
}

/// <summary>Collects and validates a new parent PIN.</summary>
public interface IPinChangeFlow
{
    /// <summary>Returns the new PIN, or null if the parent backed out.</summary>
    Task<string?> RequestNewPinAsync();
}

public sealed class AddAppFlow : IAddAppFlow
{
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService _picker;
    private readonly IExecutableResolver _resolver;

    public AddAppFlow(IDialogService dialogs, IFilePickerService picker, IExecutableResolver resolver)
    {
        _dialogs = dialogs;
        _picker = picker;
        _resolver = resolver;
    }

    public async Task<KidAppDefinition?> RequestNewAppAsync()
    {
        var viewModel = new AddAppViewModel(_picker, _resolver);
        var dialog = new AddAppDialog(viewModel);

        // Keep the dialog open when validation fails rather than silently
        // discarding what the parent typed.
        while (true)
        {
            var result = await _dialogs.ShowDialogAsync(dialog);

            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            if (viewModel.TryBuild(out var definition))
            {
                return definition;
            }
        }
    }
}

public sealed class PinChangeFlow : IPinChangeFlow
{
    private readonly IDialogService _dialogs;

    public PinChangeFlow(IDialogService dialogs) => _dialogs = dialogs;

    public async Task<string?> RequestNewPinAsync()
    {
        var first = new PasswordBox
        {
            PlaceholderText = "••••••",
            MaxLength = 6,
            Margin = new Thickness(0, 4, 0, 12)
        };
        AutomationProperties.SetName(first, Strings.Get("Dialog.ChangePinTitle"));

        var second = new PasswordBox
        {
            PlaceholderText = "••••••",
            MaxLength = 6,
            Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetName(second, Strings.Get("Dialog.ChangePinConfirm"));

        var error = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["StatusAlertBrush"],
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var panel = new StackPanel { Width = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("Dialog.ChangePinBody"),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(first);
        panel.Children.Add(new TextBlock { Text = Strings.Get("Dialog.ChangePinConfirm") });
        panel.Children.Add(second);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = Strings.Get("Dialog.ChangePinTitle"),
            Content = panel,
            PrimaryButtonText = Strings.Get("Parent.Save"),
            CloseButtonText = Strings.Get("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        while (true)
        {
            var result = await _dialogs.ShowDialogAsync(dialog);

            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            var pin = first.Password ?? string.Empty;
            var confirm = second.Password ?? string.Empty;

            if (pin.Length != 6 || !pin.All(char.IsAsciiDigit))
            {
                error.Text = Strings.Get("Dialog.ChangePinInvalid");
                error.Visibility = Visibility.Visible;
                continue;
            }

            if (!string.Equals(pin, confirm, StringComparison.Ordinal))
            {
                error.Text = Strings.Get("Dialog.ChangePinMismatch");
                error.Visibility = Visibility.Visible;
                continue;
            }

            return pin;
        }
    }
}
