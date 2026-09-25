using KidShell.Core.Runtime;
using KidShell.App.ViewModels.Parent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace KidShell.App.Views.Parent;

/// <summary>Föräldraläge → Webb.</summary>
public sealed partial class ParentWebPage : UserControl
{
    private ParentWebViewModel? _viewModel;
    private bool _loading;

    public ParentWebPage() => InitializeComponent();

    public void Initialize(ParentWebViewModel viewModel)
    {
        // The policy preview follows the window; see ApplySize.
        SizeChanged += (_, e) => ApplySize(e.NewSize.Height);

        _viewModel = viewModel;
        DomainList.ItemsSource = viewModel.AllowedDomains;
        viewModel.AllowedDomains.CollectionChanged += (_, _) => RenderAllowlist();
        viewModel.PropertyChanged += (_, _) => Render();
        Render();
    }

    private void Render()
    {
        if (_viewModel is null)
        {
            return;
        }

        _loading = true;

        SubtitleText.Text = _viewModel.Subtitle;
        ModeNone.IsChecked = _viewModel.IsNoBrowser;
        ModeAllowlist.IsChecked = _viewModel.IsAllowlist;
        ModeOpen.IsChecked = _viewModel.IsOpenWeb;
        DomainBox.Text = _viewModel.NewDomain;

        // The allowlist editor is only meaningful in allowlist mode.
        AllowlistPanel.Opacity = _viewModel.IsAllowlist ? 1 : 0.55;
        AllowlistPanel.IsHitTestVisible = _viewModel.IsAllowlist;

        RenderAllowlist();
        RenderPolicy();

        ValidationText.Text = _viewModel.ValidationMessage ?? string.Empty;
        ValidationText.Visibility = _viewModel.HasValidationMessage
            ? Visibility.Visible
            : Visibility.Collapsed;

        _loading = false;
    }

    private void RenderAllowlist() =>
        EmptyAllowlistText.Visibility = _viewModel?.AllowedDomains.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <summary>
    /// Shows the exact Edge policy values this configuration would produce.
    /// Nothing here writes them; the preview exists so the parent can read what
    /// secure setup would do before agreeing to it.
    /// </summary>
    private void RenderPolicy()
    {
        if (_viewModel is null)
        {
            return;
        }

        PolicyPreviewText.Text = _viewModel.PolicyPreview;
        PolicyWarnings.ItemsSource = _viewModel.PolicyWarnings;
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || _viewModel is null || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        switch (tag)
        {
            case "None":
                _viewModel.IsNoBrowser = true;
                break;
            case "Allowlist":
                _viewModel.IsAllowlist = true;
                break;
            case "Open":
                _viewModel.IsOpenWeb = true;
                break;
        }
    }

    private void OnAddDomainClick(object sender, RoutedEventArgs e) => AddDomain();

    private void OnDomainKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            AddDomain();
            e.Handled = true;
        }
    }

    private void AddDomain()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.NewDomain = DomainBox.Text;
        _viewModel.AddDomain();
        DomainBox.Text = string.Empty;
    }

    private void OnRemoveDomainClick(object sender, RoutedEventArgs e)
    {
        var domain = (sender as FrameworkElement)?.Tag as string
                     ?? (sender as FrameworkElement)?.DataContext as string;

        if (!string.IsNullOrEmpty(domain))
        {
            _viewModel?.RemoveDomain(domain);
        }
    }

    /// <summary>
    /// Gives the policy preview a share of the window rather than a constant.
    ///
    /// 220 was comfortable on the monitor it was written on and most of a
    /// 620-tall window at 150% scaling, where it pushed the rest of the page
    /// out of reach.
    /// </summary>
    private void ApplySize(double height)
    {
        if (height <= 0 || double.IsNaN(height))
        {
            return;
        }

        PolicyScroller.MaxHeight = ResponsiveLayout.ScrollableHeight(
            height, reservedForChrome: 420, minimum: 120, maximum: 320);
    }
}
