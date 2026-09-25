using System.Globalization;
using System.Text;
using System.Text.Json;
using KidShell.App.ViewModels;
using KidShell.App.ViewModels.Parent;
using KidShell.Core.Configuration;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace KidShell.App.Services;

/// <summary>
/// Renders every screen at every supported window size and reports what does
/// not fit.
///
/// WHY THIS EXISTS
/// ---------------
/// The supported display matrix is nine resolutions by five scale factors.
/// After removing the combinations Windows will not offer, that is twenty-two
/// distinct effective sizes, and KidShell has around fifteen screens. Checking
/// that by hand is three hundred screenshots, which is not a thing anybody
/// does twice - so in practice it gets done once, on one monitor, and the next
/// change quietly breaks the other twenty-one.
///
/// A person still has to look at the result: this cannot tell whether a screen
/// is pleasant, balanced or legible. What it can do is answer the one question
/// that is objective and that reviewers reliably miss - is any content clipped,
/// truncated or pushed somewhere the user cannot reach it. That frees the
/// manual pass to spend its attention on the things only a person can judge.
///
/// WHAT IT LOOKS FOR
/// -----------------
/// <list type="bullet">
/// <item>Content laid out beyond the window with nothing to scroll it there.
/// This is the "NO CLIPPING" rule: a button below the fold is not a cosmetic
/// problem, it is a parent who cannot finish setup.</item>
/// <item>Text shortened to an ellipsis. Sometimes deliberate, so each one is
/// reported with its content rather than simply failed.</item>
/// <item>An element that asked for more room than it was given, with no
/// scrollable ancestor. This is what clipping looks like from the inside,
/// and it catches the case where the missing content is drawn nowhere at
/// all rather than merely off-screen.</item>
/// </list>
///
/// It deliberately does not judge spacing, alignment or density.
/// </summary>
internal sealed class LayoutAudit
{
    /// <summary>
    /// Dropping this file in the app's data directory runs the audit on the
    /// next start.
    ///
    /// A file rather than an environment variable because KidShell is packaged,
    /// and a packaged app started through the shell does not inherit the
    /// environment of whatever asked for it. The request is consumed as soon as
    /// it is seen, so an audit never runs twice by accident.
    /// </summary>
    private const string RequestFile = "layout-audit.request";

    private const string ResultFile = "layout-audit.json";

    /// <summary>
    /// Holds one screen at one size so a person can look at it.
    ///
    /// The audit answers "is anything unreachable", which is mechanical. It
    /// cannot answer "does this look right", and the setup steps and the
    /// awkward content states are exactly where that question matters and
    /// where getting to them by hand is slowest - restoring a configuration,
    /// clicking through six steps, typing twenty websites. The file contains
    /// a screen name and a size, for example "Onboarding/Rules 819x614".
    /// </summary>
    private const string PoseFile = "layout-pose.request";

    internal static string? TakePose()
    {
        var pose = Path.Combine(AppPaths.DataDirectory, PoseFile);

        if (!File.Exists(pose))
        {
            return null;
        }

        var request = File.ReadAllText(pose).Trim();
        File.Delete(pose);

        return request;
    }

    /// <summary>
    /// Sizes the window and shows the named screen, then leaves it alone.
    /// </summary>
    internal async Task PoseAsync(string request)
    {
        var parts = request.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = parts.ElementAtOrDefault(0) ?? "Child";
        var size = (parts.ElementAtOrDefault(1) ?? "1366x768").Split('x');

        if (int.TryParse(size.ElementAtOrDefault(0), out var width) &&
            int.TryParse(size.ElementAtOrDefault(1), out var height))
        {
            await ResizeAsync(new Viewport(width, height, request));
        }

        var screen = Screens().Concat(StressStates())
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        screen.Show?.Invoke();
        await SettleAsync();
    }

    /// <summary>
    /// Sub-pixel differences are rounding, not clipping. A third of an
    /// effective pixel is below what any display can show.
    /// </summary>
    private const double Tolerance = 0.34;

    /// <summary>
    /// How much smaller than its wish an element has to be before that counts
    /// as content being lost.
    ///
    /// Much looser than <see cref="Tolerance"/>, because measuring and
    /// arranging text disagree by a fraction of a pixel as a matter of course:
    /// at a third of a pixel this rule produced 7,861 findings, every one of
    /// them a label that wanted 216.4 and got 216. Two effective pixels is
    /// still far below the height of a line of text or the width of a
    /// character, so nothing that actually hides content gets through.
    /// </summary>
    private const double SqueezeTolerance = 2.0;

    /// <summary>
    /// The path to write results to, or null when no audit was asked for.
    /// </summary>
    internal static string? TakeRequest()
    {
        var request = Path.Combine(AppPaths.DataDirectory, RequestFile);

        if (!File.Exists(request))
        {
            return null;
        }

        // Consumed rather than left behind: a request that survived would turn
        // every subsequent start into an audit.
        File.Delete(request);

        return Path.Combine(AppPaths.DataDirectory, ResultFile);
    }

    /// <summary>
    /// A window size worth rendering, and the display configurations that
    /// produce it.
    /// </summary>
    internal readonly record struct Viewport(int Width, int Height, string Origin);

    /// <summary>
    /// The supported matrix, reduced to the sizes that actually differ.
    ///
    /// Windows scaling is expressed in effective pixels, so 1920x1080 at 150%
    /// gives an application exactly the same 1280x720 it gets from a 1280x720
    /// display at 100%. Testing both would be testing the same arithmetic
    /// twice, so each distinct effective size appears once and names every
    /// configuration that lands on it.
    ///
    /// Combinations Windows will not offer are left out: it keeps roughly
    /// 800x600 effective pixels available, which rules out 1366x768 at 150%
    /// and the other seventeen like it.
    /// </summary>
    internal static readonly Viewport[] Matrix =
    [
        // Below the window's own minimum, and reachable anyway.
        //
        // OverlappedPresenter.PreferredMinimumWidth does not say what unit it
        // is in, and microsoft-ui-xaml issue 10452 reports that it mishandles
        // a window moved between displays of different scale - open, with no
        // maintainer reply. A floor that the framework may or may not apply in
        // the unit we assumed is not a floor, so rather than depend on it,
        // these two sizes check that the layout survives underneath it.
        new(640, 480, "below the minimum; reachable at high DPI"),
        new(700, 500, "below the minimum; reachable at high DPI"),

        new(780, 560, "minimum supported window"),
        new(819, 614, "1024x768 @125%"),
        new(960, 600, "1440x900 @150%"),
        new(1024, 640, "1280x800 @125%"),
        new(1024, 768, "1024x768 @100%"),
        new(1067, 600, "1600x900 @150%"),
        new(1093, 614, "1366x768 @125%"),
        new(1097, 617, "1920x1080 @175%"),
        new(1152, 720, "1440x900 @125%"),
        new(1280, 720, "1280x720 @100%, 1600x900 @125%, 1920x1080 @150%, 2560x1440 @200%"),
        new(1280, 800, "1280x800 @100%"),
        new(1366, 768, "1366x768 @100%"),
        new(1440, 900, "1440x900 @100%"),
        new(1463, 823, "2560x1440 @175%"),
        new(1536, 864, "1920x1080 @125%"),
        new(1600, 900, "1600x900 @100%"),
        new(1707, 960, "2560x1440 @150%"),
        new(1920, 1080, "1920x1080 @100%, 3840x2160 @200%"),
        new(2048, 1152, "2560x1440 @125%"),
        new(2194, 1234, "3840x2160 @175%"),
        new(2560, 1440, "2560x1440 @100%, 3840x2160 @150%"),
        new(3072, 1728, "3840x2160 @125%"),
        new(3840, 2160, "3840x2160 @100%")
    ];

    private readonly MainWindow _window;
    private readonly ShellViewModel _shell;
    private readonly List<Finding> _findings = [];

    internal LayoutAudit(MainWindow window, ShellViewModel shell)
    {
        _window = window;
        _shell = shell;
    }

    internal sealed record Finding(
        string Viewport,
        string Origin,
        string Screen,
        string Kind,
        string Element,
        string Detail);

    internal async Task<IReadOnlyList<Finding>> RunAsync()
    {
        foreach (var viewport in Matrix)
        {
            await ResizeAsync(viewport);

            var (width, height) = Achieved();

            var actual = viewport;

            if (Math.Abs(width - viewport.Width) > 1 || Math.Abs(height - viewport.Height) > 1)
            {
                // A desktop cannot always make a window as large as the matrix
                // asks for. Rather than skip the size - which would quietly
                // drop it from the report while the summary still said
                // twenty-three - the screens are rendered at the size that was
                // actually achieved, and the entry says so. Both numbers
                // matter: the one asked for and the one measured.
                _findings.Add(new Finding(
                    $"{viewport.Width}x{viewport.Height}",
                    viewport.Origin,
                    "-",
                    "reduced-size",
                    "window",
                    $"the desktop only allowed {width:F0}x{height:F0}; screens were audited at that size"));

                actual = viewport with { Width = (int)Math.Round(width), Height = (int)Math.Round(height) };
            }

            foreach (var (name, show) in Screens())
            {
                show();
                await SettleAsync();
                await InspectThroughlyAsync(actual, name);
            }

            // The default configuration is a pleasant one: ten apps with short
            // names and no websites. Real ones are not, and a layout that only
            // holds for the demo data is not responsive. The awkward states go
            // on the sizes where they would break first, rather than on all
            // twenty-three, because they are about content rather than width.
            if (!StressSizes.Contains(viewport.Width))
            {
                continue;
            }

            foreach (var (name, show) in StressStates())
            {
                show();
                await SettleAsync();
                await InspectThroughlyAsync(actual, name);
            }

            // Put the real configuration back, or the next size's "Child"
            // screen is quietly still showing forty apps with invented names
            // and the report says otherwise.
            _shell.Parent.Reset();
            _shell.Child.Refresh();
        }

        return _findings;
    }

    /// <summary>
    /// Every screen a parent or a child can be looking at.
    ///
    /// The setup steps are set directly rather than walked through, because a
    /// step that cannot be reached without valid input is still a step that has
    /// to render at 819 effective pixels.
    /// </summary>
    private IEnumerable<(string Name, Action Show)> Screens()
    {
        foreach (var step in Enum.GetValues<OnboardingStep>())
        {
            yield return ($"Onboarding/{step}", () =>
            {
                _shell.Mode = ShellMode.Onboarding;
                _shell.Onboarding.CurrentStep = step;
            }
            );
        }

        yield return ("Child", () =>
        {
            _shell.ClosePin();
            _shell.Mode = ShellMode.Child;
        }
        );

        yield return ("PinOverlay", () =>
        {
            _shell.Mode = ShellMode.Child;
            _shell.OpenPin();
        }
        );

        foreach (var page in Enum.GetValues<ParentPage>())
        {
            yield return ($"Parent/{page}", () =>
            {
                _shell.ClosePin();
                _shell.Mode = ShellMode.Parent;
                _shell.Parent.SelectedPage = page;
            }
            );
        }
    }

    /// <summary>
    /// Where the awkward content states are worth rendering: the narrowest
    /// and shortest sizes, the most common laptop, and one roomy one as a
    /// control.
    /// </summary>
    private static readonly int[] StressSizes = [780, 819, 960, 1024, 1067, 1093, 1366, 1920];

    /// <summary>
    /// Content a family will produce and the sample configuration will not.
    /// </summary>
    private IEnumerable<(string Name, Action Show)> StressStates()
    {
        // A single DNS label can be 63 characters, and parents paste in
        // whatever the address bar showed them.
        const string LongHost = "en-mycket-lang-webbadress-som-ett-barn-kanske-vill-besoka.example.com";

        yield return ("Web/no-sites", () => ShowWeb(0));
        yield return ("Web/one-site", () => ShowWeb(1));
        yield return ("Web/twenty-sites", () => ShowWeb(20));
        yield return ("Web/long-hostname", () => ShowWeb(3, LongHost));

        yield return ("Apps/none", () => ShowApps(0));
        yield return ("Apps/one", () => ShowApps(1));
        yield return ("Apps/forty-long-names", () => ShowApps(40, longNames: true));

        foreach (var count in new[] { 0, 1, 2, 4, 8, 16 })
        {
            yield return ($"Child/{count}-apps", () => ShowChild(count));
        }
    }

    private void ShowWeb(int domains, string? longHost = null)
    {
        _shell.ClosePin();
        _shell.Mode = ShellMode.Parent;
        _shell.Parent.SelectedPage = ParentPage.Web;

        var web = _shell.Parent.Web;
        web.IsAllowlist = true;
        web.AllowedDomains.Clear();

        for (var i = 0; i < domains; i++)
        {
            web.AllowedDomains.Add(
                longHost is not null && i == 0 ? longHost : $"sajt-nummer-{i + 1}.example.com");
        }
    }

    private void ShowApps(int count, bool longNames = false)
    {
        _shell.ClosePin();
        _shell.Mode = ShellMode.Parent;
        _shell.Parent.SelectedPage = ParentPage.Apps;

        _shell.Parent.Apps.Load(BuildConfiguration(count, longNames));
    }

    private void ShowChild(int count)
    {
        _shell.ClosePin();
        _shell.Mode = ShellMode.Child;

        _shell.Child.Tiles.Clear();

        foreach (var app in BuildConfiguration(count, longNames: count > 8).Apps)
        {
            _shell.Child.Tiles.Add(new ChildAppTileViewModel(app));
        }
    }

    /// <summary>
    /// A draft configuration with a given number of apps.
    ///
    /// Never saved: Parent Mode edits a detached draft and nothing reaches the
    /// real configuration without an explicit save, which the audit does not
    /// perform.
    /// </summary>
    private static KidShellConfiguration BuildConfiguration(int apps, bool longNames)
    {
        var configuration = KidShellConfiguration.CreateDefault();
        configuration.Apps = [];

        for (var i = 0; i < apps; i++)
        {
            var name = longNames
                ? $"Ritprogram för förskolebarn med mycket långt namn {i + 1}"
                : $"App {i + 1}";

            configuration.Apps.Add(new KidAppDefinition
            {
                Id = $"stress-{i}",
                DisplayName = name,
                ProgramName = name,
                Description = longNames
                    ? "En ovanligt utförlig beskrivning av precis allting appen kan göra"
                    : "Beskrivning",
                IsEnabled = i % 5 != 0,
                SortOrder = i
            });
        }

        return configuration;
    }

    /// <summary>
    /// Sizes the window so that its <em>content</em> is the requested size.
    ///
    /// AppWindow.Resize sets the outer window, which includes the resize
    /// border, so asking for 1366 gives the layout slightly less than 1366 to
    /// work with. The matrix is about what the layout gets, so the difference
    /// is measured once and corrected rather than assumed - it varies with the
    /// theme and the Windows version, and guessing it wrong would shift every
    /// size in the report by a few pixels.
    /// </summary>
    private async Task ResizeAsync(Viewport viewport)
    {
        // The window refuses to be resized below its own preferred minimum,
        // which would quietly turn the two sub-minimum entries into another
        // audit of 780x560. Lifted here rather than once at the start so that
        // posing a single screen gets it too. Nothing puts it back, and
        // nothing needs to: the audit closes the window when it finishes, and
        // a posed window is a developer looking at one screen.
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1;
            presenter.PreferredMinimumHeight = 1;
        }

        // Out of the way of the screen edges first: a window is easier to make
        // large when it is not already up against them.
        _window.AppWindow.Move(new Windows.Graphics.PointInt32(0, 0));

        var scale = _window.Content.XamlRoot?.RasterizationScale ?? 1.0;
        double targetWidth = viewport.Width;
        double targetHeight = viewport.Height;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Round(targetWidth * scale),
                (int)Math.Round(targetHeight * scale)));

            await SettleAsync();

            var (width, height) = Achieved();

            if (Math.Abs(width - viewport.Width) <= 1 && Math.Abs(height - viewport.Height) <= 1)
            {
                return;
            }

            targetWidth += viewport.Width - width;
            targetHeight += viewport.Height - height;
        }
    }

    /// <summary>
    /// The size the window actually reached, which is not always the size it
    /// was asked for.
    ///
    /// A window cannot always be made bigger than the desktop it is on, so on
    /// a 1920x1200 machine the four-figure entries in the matrix may quietly
    /// come back as 1920. Reporting the requested size in that case would be a
    /// lie in the worst possible direction: it would claim 3840x2160 was
    /// rendered and found clean, when what was rendered was 1920 for the sixth
    /// time. The audit records what it measured and says so.
    /// </summary>
    private (double Width, double Height) Achieved() =>
        _window.Content is FrameworkElement root
            ? (root.ActualWidth, root.ActualHeight)
            : (0, 0);

    /// <summary>
    /// Waits for the layout to actually happen.
    ///
    /// A resize is not synchronous, and neither is the measure pass that
    /// follows a page becoming visible. Reading ActualWidth too early reports
    /// the previous size, which produces an audit full of findings that are
    /// really just the harness racing the layout engine.
    /// </summary>
    private async Task SettleAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            if (_window.Content is FrameworkElement root)
            {
                root.UpdateLayout();
            }

            var frame = new TaskCompletionSource();
            void OnRendering(object? sender, object e)
            {
                CompositionTarget.Rendering -= OnRendering;
                frame.SetResult();
            }

            CompositionTarget.Rendering += OnRendering;
            await frame.Task;
        }
    }

    /// <summary>
    /// Looks at the screen as it arrives, and again with everything scrolled
    /// to the bottom.
    ///
    /// Content below the fold is clipped, and clipped content is deliberately
    /// ignored - it cannot visibly collide with anything the user can see. The
    /// consequence is that a page which scrolls is only ever examined down to
    /// the fold, which is how the Webb page kept a clean report while its list
    /// of websites ran its long hostnames underneath the remove buttons. The
    /// rest of the page has to be brought into view before it can be judged.
    /// </summary>
    private async Task InspectThroughlyAsync(Viewport viewport, string screen)
    {
        Inspect(viewport, screen);

        if (_window.Content is not FrameworkElement root)
        {
            return;
        }

        var scrollers = new List<ScrollViewer>();
        CollectScrollers(root, scrollers);

        var scrolled = scrollers.Where(s => s.ScrollableHeight > Tolerance).ToList();

        if (scrolled.Count == 0)
        {
            return;
        }

        // Stepped, not jumped to the end. A long page's problem is as likely to
        // be in the middle as at the bottom - the list of websites sits between
        // the mode buttons and the policy preview, so scrolling straight to the
        // end sailed past the very rows that were broken.
        var deepest = scrolled.Max(s => s.ScrollableHeight);
        var steps = (int)Math.Min(6, Math.Ceiling(deepest / Math.Max(1, viewport.Height * 0.8)));

        for (var step = 1; step <= steps; step++)
        {
            var fraction = (double)step / steps;

            foreach (var scroller in scrolled)
            {
                scroller.ChangeView(null, scroller.ScrollableHeight * fraction, null, disableAnimation: true);
            }

            await SettleAsync();
            Inspect(viewport, $"{screen} (scrolled {fraction:P0})");
        }

        foreach (var scroller in scrolled)
        {
            scroller.ChangeView(null, 0, null, disableAnimation: true);
        }

        await SettleAsync();
    }

    private static void CollectScrollers(DependencyObject node, List<ScrollViewer> into)
    {
        if (node is ScrollViewer scroller)
        {
            into.Add(scroller);
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            CollectScrollers(VisualTreeHelper.GetChild(node, i), into);
        }
    }

    private void Inspect(Viewport viewport, string screen)
    {
        if (_window.Content is not FrameworkElement root)
        {
            return;
        }

        var window = new Rect(0, 0, root.ActualWidth, root.ActualHeight);

        Walk(root, root, window, viewport, screen, scrollable: false, clip: window);
    }

    /// <summary>
    /// Two children of the same container drawn on top of each other, where
    /// one of them is a control and the other is text.
    ///
    /// This is what a single-cell Grid does when one child is right-aligned
    /// and the other is not constrained. Everything stays inside the window,
    /// nothing is trimmed and nothing reports being squeezed - the label
    /// simply runs underneath the button and the glyph draws on top of the
    /// words. A 63-character hostname on the Webb page did exactly that, and
    /// only a screenshot showed it.
    ///
    /// Restricted to siblings on purpose. Comparing everything against
    /// everything sounds stricter but is not usable: a modal scrim covers the
    /// screen behind it, and the cards on Parent Mode are deliberately layered,
    /// so bounds that overlap are normal and only the pair sharing a cell is
    /// evidence of anything. Overlays are excluded by the same reasoning - a
    /// sibling that covers another almost entirely is a layer, not a collision.
    /// </summary>
    private void ReportSiblingOverlaps(
        Panel panel, FrameworkElement root, Rect clip, Viewport viewport, string screen)
    {
        var children = panel.Children
            .OfType<FrameworkElement>()
            .Where(c => c.Visibility == Visibility.Visible)
            .Select(c => (Element: c, Bounds: Intersected(clip, BoundsOf(c, root))))
            .Where(c => !c.Bounds.IsEmpty && c.Bounds.Width > 0 && c.Bounds.Height > 0)
            .ToList();

        for (var i = 0; i < children.Count; i++)
        {
            for (var j = i + 1; j < children.Count; j++)
            {
                var (a, boundsA) = children[i];
                var (b, boundsB) = children[j];

                if (Intersected(boundsA, boundsB).IsEmpty)
                {
                    continue;
                }

                var overlapArea = Intersected(boundsA, boundsB);
                var larger = Math.Max(boundsA.Width * boundsA.Height, boundsB.Width * boundsB.Height);

                // A sibling that covers another almost entirely is a layer,
                // not a collision.
                if (larger <= 0 || (overlapArea.Width * overlapArea.Height) / larger > 0.9)
                {
                    continue;
                }

                // One side has to be something the user operates and the other
                // something they read, or the overlap is just decoration.
                var (control, text) =
                    Operable(a) is { } ca && Find<TextBlock>(b) is { } tb ? (ca, tb) :
                    Operable(b) is { } cb && Find<TextBlock>(a) is { } ta ? (cb, ta) :
                    (null, null);

                if (control is null || text is null)
                {
                    continue;
                }

                // The words and the control themselves, not the panels holding
                // them. A title in a stretched container has bounds the full
                // width of the row, so comparing containers said the Appar
                // heading ran 159 epx under the Add button when on screen they
                // were nowhere near each other.
                var overlap = Intersected(
                    Intersected(clip, BoundsOf(text, root)),
                    Intersected(clip, BoundsOf(control, root)));

                if (overlap.IsEmpty || overlap.Width <= SqueezeTolerance || overlap.Height <= SqueezeTolerance)
                {
                    continue;
                }

                _findings.Add(new Finding(
                    $"{viewport.Width}x{viewport.Height}",
                    viewport.Origin,
                    screen,
                    "overlapping",
                    Describe(text),
                    $"runs {overlap.Width:F0} epx under {Describe(control)} in the same cell"));
            }
        }
    }

    /// <summary>
    /// The first control the user actually operates at or under this element.
    ///
    /// Scrollbar parts and the reveal button inside a PasswordBox are controls
    /// too, but they live in somebody else's template and sit on top of the
    /// content by design - a scrollbar overlaying the text it scrolls is how
    /// Windows draws scrollbars, not a mistake worth reporting.
    /// </summary>
    private static FrameworkElement? Operable(DependencyObject node)
    {
        if (node is RepeatButton or Thumb or ScrollBar)
        {
            return null;
        }

        if (node is Button or RadioButton or CheckBox or HyperlinkButton)
        {
            return (FrameworkElement)node;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            if (Operable(VisualTreeHelper.GetChild(node, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether this text is really an icon.
    ///
    /// A FontIcon draws its glyph through an ordinary TextBlock holding one
    /// character from the private use area. It is a picture, and pictures are
    /// allowed to sit close to a button - only words being covered up is worth
    /// reporting.
    /// </summary>
    private static bool IsGlyph(string text) =>
        text.Trim().All(c => c is >= '' and <= '');

    /// <summary>The first element of a kind at or under this one.</summary>
    private static T? Find<T>(DependencyObject node) where T : FrameworkElement
    {
        if (node is T match &&
            (match is not TextBlock t || (!string.IsNullOrWhiteSpace(t.Text) && !IsGlyph(t.Text))))
        {
            return match;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            if (Find<T>(VisualTreeHelper.GetChild(node, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void Walk(
        DependencyObject node,
        FrameworkElement root,
        Rect window,
        Viewport viewport,
        string screen,
        bool scrollable,
        Rect clip)
    {
        if (node is FrameworkElement element)
        {
            if (element.Visibility == Visibility.Collapsed)
            {
                return;
            }

            // A scrollable ancestor means content beyond the window is still
            // reachable, which is the documented answer to not fitting.
            if (node is ScrollViewer scroller)
            {
                scrollable = scrollable ||
                             scroller.ScrollableHeight > Tolerance ||
                             scroller.ScrollableWidth > Tolerance;

                // A ScrollViewer clips to its viewport, so everything below it
                // is only visible inside these bounds. Without this, content
                // that has scrolled out of sight still reports the position it
                // would have had, and appears to sit on top of whatever is
                // drawn underneath the scroller - which made the setup screens
                // look as though every heading was lying across the Tillbaka
                // button.
                clip = Intersected(clip, BoundsOf(scroller, root));
            }

            // A rounded Border clips whatever it contains, which is how the
            // navigation rail hid its own last item on a short window: the
            // buttons were laid out past the bottom of the panel, inside the
            // window, unclipped by anything the audit was looking at, and
            // simply not drawn. Clipping is not only something the window edge
            // does.
            if (node is Border { CornerRadius.TopLeft: > 0 } rounded)
            {
                clip = Intersected(clip, BoundsOf(rounded, root));
            }

            if (IsParked(element, root, window))
            {
                return;
            }

            if (node is Panel panel && panel.Children.Count > 1)
            {
                ReportSiblingOverlaps(panel, root, clip, viewport, screen);
            }

            Check(element, root, window, clip, viewport, screen, scrollable);
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), root, window, viewport, screen, scrollable, clip);
        }
    }

    private void Check(
        FrameworkElement element,
        FrameworkElement root,
        Rect window,
        Rect clip,
        Viewport viewport,
        string screen,
        bool scrollable)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return;
        }

        Finding Record(string kind, string detail) =>
            new($"{viewport.Width}x{viewport.Height}", viewport.Origin, screen, kind, Describe(element), detail);

        // Text cut short with an ellipsis. Reported with the text so that a
        // deliberate one - a child's name on a narrow card - is obvious.
        if (element is TextBlock { IsTextTrimmed: true } text)
        {
            _findings.Add(Record("trimmed-text", Shorten(text.Text)));
        }

        // Asked for more room than it got. This is clipping seen from the
        // inside: whatever did not fit is not drawn anywhere.
        //
        // Containers only. A TextBlock reports the extent of its text rather
        // than the size of the slot it was arranged in, so a right-aligned
        // label with a MinWidth looks permanently squeezed - "Tillåten" wants
        // 74 and reports 41 while rendering perfectly, which is what the whole
        // Appar page looked like on the first run of this audit. For text,
        // "was anything lost" is answered exactly by IsTextTrimmed above.
        if (element is not TextBlock)
        {
            var wanted = element.DesiredSize;

            if (!scrollable && wanted.Height - element.ActualHeight > SqueezeTolerance + element.Margin.Top + element.Margin.Bottom)
            {
                _findings.Add(Record(
                    "squeezed",
                    $"wanted {wanted.Height:F1} high, given {element.ActualHeight:F1}"));
            }

            if (!scrollable && wanted.Width - element.ActualWidth > SqueezeTolerance + element.Margin.Left + element.Margin.Right)
            {
                _findings.Add(Record(
                    "squeezed",
                    $"wanted {wanted.Width:F1} wide, given {element.ActualWidth:F1}"));
            }
        }

        // Laid out beyond the window. Only a defect when nothing scrolls,
        // because otherwise the user can simply reach it.
        if (scrollable)
        {
            return;
        }

        Rect bounds;
        try
        {
            bounds = element
                .TransformToVisual(root)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (Exception)
        {
            // An element mid-transition has no path to the root yet. It will
            // be measured on the next screen.
            return;
        }

        // A panel is a container for real content, so content of its own that
        // sits outside the box it is drawn in has been lost. Everything else
        // is checked against the window instead: a control's own background
        // may legitimately be larger than what encloses it - a default
        // RadioButton has a MinWidth of 120, and eight of them inside a
        // 72-epx rail is how a strictly-drawn version of this rule produced
        // seven thousand complaints about a navigation bar that looks right.
        if (element is Panel && !scrollable)
        {
            var hidden = bounds.Bottom - clip.Bottom;

            if (hidden > SqueezeTolerance)
            {
                _findings.Add(Record("clipped", $"{hidden:F0} epx of it is below the panel it is drawn in"));
            }
        }

        var overflowRight = bounds.Right - window.Right;
        var overflowBottom = bounds.Bottom - window.Bottom;

        // An interactive control off the edge is worse than a decoration off
        // the edge, because the user cannot do the thing it is for.
        var kind = element is ButtonBase or TextBox or PasswordBox or ComboBox or ListViewBase
            ? "unreachable-control"
            : "off-window";

        if (overflowRight > Tolerance)
        {
            _findings.Add(Record(kind, $"extends {overflowRight:F0} past the right edge"));
        }

        if (overflowBottom > Tolerance)
        {
            _findings.Add(Record(kind, $"extends {overflowBottom:F0} below the bottom edge"));
        }

        if (bounds.Left < -Tolerance)
        {
            _findings.Add(Record(kind, $"starts {-bounds.Left:F0} left of the window"));
        }
    }

    /// <summary>
    /// Records the pieces the overlap check compares afterwards.
    /// </summary>
    private static Rect BoundsOf(FrameworkElement element, FrameworkElement root)
    {
        try
        {
            return element
                .TransformToVisual(root)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (Exception)
        {
            return Rect.Empty;
        }
    }

    private static Rect Intersected(Rect a, Rect b)
    {
        a.Intersect(b);
        return a;
    }

    /// <summary>
    /// Whether this element is a recycled list row rather than part of the
    /// layout.
    ///
    /// ItemsRepeater does not remove the rows it is not showing; it parks them
    /// far off to one side and reuses them. On the Appar page that means
    /// roughly ten thousand effective pixels to the left, which on the first
    /// run of the stress pass produced nine thousand reports about a Paint
    /// icon that no longer existed.
    ///
    /// The test is "further outside than the window is big", which no real
    /// layout produces: content that genuinely fails to fit misses by tens of
    /// pixels, not by more than a screen. That keeps it from being a magic
    /// number - it scales with the window being audited.
    /// </summary>
    private static bool IsParked(FrameworkElement element, FrameworkElement root, Rect window)
    {
        try
        {
            var bounds = element
                .TransformToVisual(root)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

            return bounds.Right < -window.Width ||
                   bounds.Bottom < -window.Height ||
                   bounds.Left > window.Width * 2 ||
                   bounds.Top > window.Height * 2;
        }
        catch (Exception)
        {
            // No path to the root: not laid out, so not a layout finding.
            return true;
        }
    }

    private static string Describe(FrameworkElement element)
    {
        var type = element.GetType().Name;

        if (!string.IsNullOrEmpty(element.Name))
        {
            return $"{type}#{element.Name}";
        }

        if (element is TextBlock { Text.Length: > 0 } text)
        {
            return $"{type}(\"{Shorten(text.Text)}\")";
        }

        return type;
    }

    private static string Shorten(string value)
    {
        var flat = value.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 48 ? flat : flat[..45] + "...";
    }

    /// <summary>
    /// Writes the findings as JSON next to a summary a person can read
    /// without a parser.
    /// </summary>
    internal static async Task WriteAsync(string path, IReadOnlyList<Finding> findings)
    {
        var json = JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);

        // Two lists, because they mean different things. A defect is content
        // the user cannot get to, and one is too many. Trimming is a decision
        // - a long app name ending in an ellipsis on a small tile is correct,
        // and shrinking the type until it fits would be worse - so those are
        // listed to be looked at rather than counted against the gate.
        // Partitioned by kind rather than with Except: Finding is a record, so
        // Except compares by value and would quietly drop the repeats - which
        // turned forty-five shortened labels into "3" the first time.
        static bool IsDefect(Finding f) =>
            f.Kind is "clipped" or "off-window" or "unreachable-control" or "squeezed" or "overlapping";

        var defects = findings.Where(IsDefect).ToList();
        var notes = findings.Where(f => !IsDefect(f)).ToList();

        var summary = new StringBuilder();
        summary.AppendLine(CultureInfo.InvariantCulture,
            $"{defects.Count} defect(s), {notes.Count} note(s) across {Matrix.Length} window size(s).");

        summary.AppendLine("DEFECTS");
        foreach (var group in defects.GroupBy(f => f.Kind).OrderByDescending(g => g.Count()))
        {
            summary.AppendLine(CultureInfo.InvariantCulture, $"  {group.Count(),5}  {group.Key}");
        }

        summary.AppendLine("NOTES");
        foreach (var group in notes.GroupBy(f => f.Kind).OrderByDescending(g => g.Count()))
        {
            summary.AppendLine(CultureInfo.InvariantCulture, $"  {group.Count(),5}  {group.Key}");
        }

        summary.AppendLine("SCREENS WHERE TEXT WAS SHORTENED");
        foreach (var group in notes.Where(n => n.Kind == "trimmed-text")
                                   .GroupBy(f => f.Screen)
                                   .OrderByDescending(g => g.Count()))
        {
            summary.AppendLine(CultureInfo.InvariantCulture, $"  {group.Count(),5}  {group.Key}");
        }

        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".txt"), summary.ToString());
    }
}
