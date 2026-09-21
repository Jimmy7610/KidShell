using KidShell.App.Localization;
using KidShell.Core.Security.Readiness;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KidShell.App.Services;

public interface ISecurityDialogs
{
    /// <summary>Standard versus Secure, as a plain comparison table.</summary>
    Task ShowModeComparisonAsync();

    /// <summary>The dry-run plan: what secure setup would do, and nothing else.</summary>
    Task ShowSecurityPlanAsync(SecurityReadinessReport report);
}

/// <summary>
/// The two informational dialogs on the Säkerhet page.
///
/// Both are read-only views over data that already exists. Neither has a
/// confirm button, an apply path, or any reference to something that could
/// change the machine — pressing "Visa säkerhetsplan" prints a list.
/// </summary>
public sealed class SecurityDialogs : ISecurityDialogs
{
    private readonly IDialogService _dialogs;

    public SecurityDialogs(IDialogService dialogs) => _dialogs = dialogs;

    public async Task ShowModeComparisonAsync()
    {
        var content = new StackPanel { Spacing = 16, MaxWidth = 520 };

        content.Children.Add(new TextBlock
        {
            Text = Strings.Get("Security.CompareIntro"),
            TextWrapping = TextWrapping.Wrap,
            Style = Resource<Style>("SecondaryTextStyle")
        });

        content.Children.Add(BuildComparisonTable());

        content.Children.Add(new TextBlock
        {
            Text = Strings.Get("Security.CompareFooter"),
            TextWrapping = TextWrapping.Wrap,
            Style = Resource<Style>("CaptionTextStyle")
        });

        await ShowAsync(
            Strings.Get("Security.CompareTitle"),
            content,
            Strings.Get("Security.CompareClose"));
    }

    public async Task ShowSecurityPlanAsync(SecurityReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var content = new StackPanel { Spacing = 14, MaxWidth = 560 };

        content.Children.Add(new TextBlock
        {
            Text = Strings.Get("Security.PlanIntro"),
            TextWrapping = TextWrapping.Wrap,
            Style = Resource<Style>("SecondaryTextStyle")
        });

        if (report.PlannedActions.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = Strings.Get("Security.PlanEmpty"),
                TextWrapping = TextWrapping.Wrap,
                Style = Resource<Style>("BodyTextStyle")
            });
        }
        else
        {
            foreach (var action in report.PlannedActions)
            {
                content.Children.Add(BuildPlanRow(action));
            }
        }

        content.Children.Add(new TextBlock
        {
            Text = Strings.Get("Security.PlanRiskNote"),
            TextWrapping = TextWrapping.Wrap,
            Style = Resource<Style>("CaptionTextStyle")
        });

        // The closing statement of the whole dry run: nothing ran.
        content.Children.Add(new Border
        {
            Background = Resource<Microsoft.UI.Xaml.Media.Brush>("TintAmberBrush"),
            CornerRadius = Resource<CornerRadius>("RadiusMd"),
            Padding = new Thickness(14, 10, 14, 10),
            Child = new TextBlock
            {
                Text = Strings.Get("Security.PlanNothingExecuted"),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Style = Resource<Style>("BodyTextStyle")
            }
        });

        await ShowAsync(
            Strings.Get("Security.PlanTitle"),
            content,
            Strings.Get("Security.PlanClose"),
            scrollHeight: 440);
    }

    /// <summary>
    /// Both dialogs are close-only: one button, no primary action, nothing to
    /// confirm. Goes through IDialogService so the app-wide "only one dialog
    /// at a time" guard applies.
    /// </summary>
    private async Task ShowAsync(string title, FrameworkElement content, string closeText, double? scrollHeight = null)
    {
        var scroller = new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        // ContentDialog measures its content against infinite height, so a
        // MaxHeight alone makes a tall ScrollViewer size to content and then
        // get clipped rather than scroll. Callers with long content give an
        // explicit height; short content is left to size itself.
        if (scrollHeight is { } height)
        {
            scroller.Height = height;
        }

        await _dialogs.ShowDialogAsync(new ContentDialog
        {
            Title = title,
            Content = scroller,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close
        });
    }

    private static Grid BuildComparisonTable()
    {
        var grid = new Grid { RowSpacing = 0, ColumnSpacing = 0 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var yes = Strings.Get("Security.CompareYes");
        var no = Strings.Get("Security.CompareNo");
        var limited = Strings.Get("Security.CompareLimited");

        (string Feature, string Standard, string Secure)[] rows =
        [
            (Strings.Get("Security.CompareRowInterface"), yes, yes),
            (Strings.Get("Security.CompareRowChildAccount"), yes, yes),
            (Strings.Get("Security.CompareRowAppLimits"), yes, yes),
            (Strings.Get("Security.CompareRowPin"), yes, yes),
            (Strings.Get("Security.CompareRowAssignedAccess"), no, yes),
            (Strings.Get("Security.CompareRowLockedEnvironment"), limited, yes)
        ];

        AddRow(grid, 0,
            Strings.Get("Security.CompareFeature"),
            Strings.Get("Security.CompareStandard"),
            Strings.Get("Security.CompareSecure"),
            isHeader: true);

        for (var i = 0; i < rows.Length; i++)
        {
            AddRow(grid, i + 1, rows[i].Feature, rows[i].Standard, rows[i].Secure, isHeader: false);
        }

        return grid;
    }

    /// <summary>
    /// Words rather than tick marks, so the table reads correctly to a screen
    /// reader and does not depend on a glyph font.
    /// </summary>
    private static void AddRow(Grid grid, int row, string feature, string standard, string secure, bool isHeader)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var weight = isHeader ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        var padding = new Thickness(0, 9, 16, 9);

        var featureBlock = new TextBlock
        {
            Text = feature,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            Margin = padding
        };
        Grid.SetRow(featureBlock, row);
        Grid.SetColumn(featureBlock, 0);
        grid.Children.Add(featureBlock);

        AddCell(grid, row, 1, standard, weight);
        AddCell(grid, row, 2, secure, weight);

        if (!isHeader)
        {
            var rule = new Border
            {
                Height = 1,
                Background = Resource<Microsoft.UI.Xaml.Media.Brush>("HairlineBrush"),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetRow(rule, row);
            Grid.SetColumnSpan(rule, 3);
            grid.Children.Add(rule);
        }
    }

    private static void AddCell(Grid grid, int row, int column, string text, Windows.UI.Text.FontWeight weight)
    {
        var block = new TextBlock
        {
            Text = text,
            FontWeight = weight,
            MinWidth = 76,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(6, 9, 6, 9)
        };

        Grid.SetRow(block, row);
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private static Border BuildPlanRow(PlannedAction action)
    {
        var panel = new StackPanel { Spacing = 5 };

        panel.Children.Add(new TextBlock
        {
            Text = $"{action.Order}. {action.Description}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = action.Detail,
            TextWrapping = TextWrapping.Wrap,
            Style = Resource<Style>("CaptionTextStyle")
        });

        var facts = new List<string>
        {
            action.RiskLevel switch
            {
                ChangeRiskLevel.High => Strings.Get("Security.PlanRiskHigh"),
                ChangeRiskLevel.Medium => Strings.Get("Security.PlanRiskMedium"),
                _ => Strings.Get("Security.PlanRiskLow")
            },
            action.CanRollback ? Strings.Get("Security.PlanCanRollback") : Strings.Get("Security.PlanCannotRollback")
        };

        if (action.RequiresAdmin)
        {
            facts.Add(Strings.Get("Security.PlanRequiresAdmin"));
        }

        panel.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", facts),
            TextWrapping = TextWrapping.Wrap,
            Style = Resource<Style>("CaptionTextStyle")
        });

        return new Border
        {
            Background = Resource<Microsoft.UI.Xaml.Media.Brush>("TintNeutralBrush"),
            CornerRadius = Resource<CornerRadius>("RadiusMd"),
            Padding = new Thickness(14, 12, 14, 12),
            Child = panel
        };
    }

    private static T Resource<T>(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is T typed
            ? typed
            : default!;
}
