using KidShell.App.Localization;
using KidShell.Core.Security;
using KidShell.App.ViewModels;
using KidShell.App.Views.Dialogs;
using KidShell.Core.Apps;
using KidShell.Core.Configuration;
using KidShell.Core.Launching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Services;

/// <summary>Shows the "Lägg till app" dialog and returns what the parent configured.</summary>
public interface IAddAppFlow
{
    /// <summary>
    /// Asks the parent for a new app.
    ///
    /// <paramref name="existing"/> is what is already in the child's grid, so
    /// the browser can mark those rather than letting the same program be
    /// added twice.
    /// </summary>
    Task<KidAppDefinition?> RequestNewAppAsync(IReadOnlyCollection<KidAppDefinition> existing);
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
    private readonly IApplicationCatalog _catalog;

    public AddAppFlow(
        IDialogService dialogs,
        IFilePickerService picker,
        IExecutableResolver resolver,
        IApplicationCatalog catalog)
    {
        _dialogs = dialogs;
        _picker = picker;
        _resolver = resolver;
        _catalog = catalog;
    }

    /// <summary>
    /// Two steps: pick an application, then decide how its card looks.
    ///
    /// Browsing comes FIRST because typing a path is an expert affordance and a
    /// hopeless default for a parent who just wants Paint. The manual form is
    /// still there - a program the scanners missed, an unusual install - but it
    /// is the second button rather than the only one.
    /// </summary>
    public async Task<KidAppDefinition?> RequestNewAppAsync(IReadOnlyCollection<KidAppDefinition> existing)
    {
        var browser = new AppBrowserViewModel(_catalog);
        browser.SetExisting(existing);

        var browseDialog = new BrowseAppsDialog(browser);
        var browseResult = await _dialogs.ShowDialogAsync(browseDialog);

        // Closed without choosing and without asking for the manual form.
        if (browseResult == ContentDialogResult.None && browseDialog.Chosen is null)
        {
            return null;
        }

        var viewModel = new AddAppViewModel(_picker, _resolver);

        if (browseDialog.Chosen is { } chosen)
        {
            viewModel.PrefillFrom(chosen);
        }

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

            // One shared policy decides, so the reason shown is the reason the
            // PIN was actually refused. Telling a parent "must be six digits"
            // when they typed six digits is worse than saying nothing.
            var validation = ParentPinPolicy.ValidatePair(pin, confirm);

            if (validation != PinValidation.Ok)
            {
                error.Text = KidShell.App.Localization.PinMessages.Describe(validation);
                error.Visibility = Visibility.Visible;
                continue;
            }

            return pin;
        }
    }

}
