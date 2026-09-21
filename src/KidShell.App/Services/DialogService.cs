using KidShell.App.Localization;
using KidShell.Core.Diagnostics;
using KidShell.Core.Launching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Services;

public enum ConfirmChoice
{
    Primary,
    Secondary,
    Cancel
}

/// <summary>
/// All modal messaging goes through here so that wording, styling and the
/// "child sees friendly text, log sees technical detail" rule are applied in
/// one place.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Element the dialogs are hosted from. The XamlRoot is read from it at
    /// show time rather than cached, because it is null until the window has
    /// been activated.
    /// </summary>
    UIElement? Host { get; set; }

    Task ShowMessageAsync(string title, string body, string? closeText = null);

    Task ShowLaunchProblemAsync(LaunchResult result);

    Task<ConfirmChoice> ShowConfirmAsync(
        string title,
        string body,
        string primaryText,
        string? secondaryText = null,
        string? cancelText = null);

    Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog);
}

public sealed class DialogService : IDialogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IKidShellLogger _logger;

    public DialogService(IKidShellLogger logger) => _logger = logger;

    public UIElement? Host { get; set; }

    public async Task ShowMessageAsync(string title, string body, string? closeText = null)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = BuildBody(body),
            CloseButtonText = closeText ?? Strings.Get("Dialog.Ok"),
            DefaultButton = ContentDialogButton.Close
        };

        await ShowDialogAsync(dialog);
    }

    public Task ShowLaunchProblemAsync(LaunchResult result)
    {
        var title = result.Status switch
        {
            LaunchStatus.NotConfigured => Strings.Get("Launch.NotConfiguredTitle"),
            LaunchStatus.NotFound => Strings.Get("Launch.MissingTitle"),
            _ => Strings.Get("Launch.FailedTitle")
        };

        var body = result.Status == LaunchStatus.NotConfigured
            ? $"{result.ChildMessage}\n\n{Strings.Get("Launch.NotConfiguredHint")}"
            : result.ChildMessage;

        // result.TechnicalDetail is deliberately not shown: it has already
        // been written to the log by the launcher.
        return ShowMessageAsync(title, body, Strings.Get("Launch.Back"));
    }

    public async Task<ConfirmChoice> ShowConfirmAsync(
        string title,
        string body,
        string primaryText,
        string? secondaryText = null,
        string? cancelText = null)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = BuildBody(body),
            PrimaryButtonText = primaryText,
            CloseButtonText = cancelText ?? Strings.Get("Dialog.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            dialog.SecondaryButtonText = secondaryText;
        }

        var result = await ShowDialogAsync(dialog);

        return result switch
        {
            ContentDialogResult.Primary => ConfirmChoice.Primary,
            ContentDialogResult.Secondary => ConfirmChoice.Secondary,
            _ => ConfirmChoice.Cancel
        };
    }

    public async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        var xamlRoot = Host?.XamlRoot;

        if (xamlRoot is null)
        {
            // No window to host the dialog: never throw, just say so in the log.
            _logger.Warning("Dialog", "No XamlRoot available; dialog was not shown.");
            return ContentDialogResult.None;
        }

        // ContentDialog throws if two are open at once, which is easy to hit
        // when a child taps several cards quickly.
        await _gate.WaitAsync();
        try
        {
            dialog.XamlRoot = xamlRoot;
            dialog.RequestedTheme = ElementTheme.Light;
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Dialog", "A dialog could not be shown.", ex);
            return ContentDialogResult.None;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TextBlock BuildBody(string body) => new()
    {
        Text = body,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 420
    };
}
