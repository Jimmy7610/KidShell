using KidShell.App.Localization;
using KidShell.Core.Mvvm;
using KidShell.Core.Runtime;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Updates;

namespace KidShell.App.ViewModels.Parent;

/// <summary>One labelled fact on the About panel.</summary>
public sealed record AboutFact(string Label, string Value, string? Detail = null);

/// <summary>
/// Om KidShell — version, build and what this machine can actually do.
///
/// WHY THIS PAGE EXISTS
/// --------------------
/// Two questions come up whenever something is wrong: "which version is this?"
/// and "is my computer actually protected?" A product that cannot answer the
/// second one honestly, on screen, is a product whose security claims live only
/// in its marketing.
///
/// So every line is derived rather than declared. The version comes from the
/// assembly, the edition from the readiness report, and the security state from
/// what KidShell can verify - never from a constant somebody remembered to
/// update.
/// </summary>
public sealed class AboutViewModel : ObservableObject
{
    private readonly IRuntimeEnvironment _environment;
    private WindowsSecurityCapabilities? _capabilities;

    public AboutViewModel(IRuntimeEnvironment environment)
    {
        _environment = environment;
    }

    /// <summary>Called once the readiness report has been produced.</summary>
    public void Load(WindowsSecurityCapabilities? capabilities)
    {
        _capabilities = capabilities;
        OnPropertyChanged(string.Empty);
    }

    public string Version => BuildInfo.Version;

    /// <summary>
    /// Whether this is a developer build. Shown prominently rather than in
    /// small print: a Debug build accepts a PIN printed in the README.
    /// </summary>
    public bool IsDevelopmentBuild => _environment.IsDevelopment;

    public string BuildLabel => _environment.IsDevelopment
        ? Strings.Get("About.BuildDevelopment")
        : Strings.Get("About.BuildRelease");

    public string BuildWarning => Strings.Get("About.DevelopmentWarning");

    /// <summary>Every fact, in the order a person would want to read them.</summary>
    public IReadOnlyList<AboutFact> Facts => BuildFacts();

    private IReadOnlyList<AboutFact> BuildFacts()
    {
        var facts = new List<AboutFact>
        {
            new(Strings.Get("About.Version"), BuildInfo.Version),
            new(Strings.Get("About.Build"), BuildLabel,
                BuildInfo.Commit is { } commit ? Strings.Format("About.Commit", commit) : null)
        };

        if (_capabilities is { } capabilities)
        {
            facts.Add(new AboutFact(
                Strings.Get("About.Windows"),
                capabilities.EditionDisplayName,
                Strings.Format("About.Build.Number", capabilities.BuildNumber,
                    capabilities.Version.Length > 0 ? capabilities.Version : "-")));

            // Standard and Secure are reported separately because they are
            // different questions with different answers on the same machine.
            facts.Add(new AboutFact(
                Strings.Get("About.StandardMode"),
                Strings.Get("About.Available"),
                Strings.Get("About.StandardHint")));

            facts.Add(new AboutFact(
                Strings.Get("About.SecureMode"),
                capabilities.SupportsAssignedAccess
                    ? Strings.Get("About.Available")
                    : Strings.Get("About.NotSupported"),
                capabilities.SupportsAssignedAccess
                    ? Strings.Get("About.SecureHint")
                    : Strings.Format("About.SecureUnsupportedHint", capabilities.EditionDisplayName)));

            // The honest distinction the whole app-control model turns on.
            facts.Add(new AboutFact(
                Strings.Get("About.AppControl"),
                DescribeAppControl(capabilities),
                Strings.Get("About.AppControlHint")));
        }

        facts.Add(new AboutFact(
            Strings.Get("About.SecurityStatus"),
            Strings.Get("About.SecurityNotApplied"),
            Strings.Get("About.SecurityNotAppliedHint")));

        facts.Add(new AboutFact(
            Strings.Get("About.Updates"),
            UpdatePolicy.UpdatesEnabled
                ? Strings.Get("About.UpdatesOn")
                : Strings.Get("About.UpdatesOff"),
            Strings.Get("About.UpdatesHint")));

        facts.Add(new AboutFact(Strings.Get("About.Licence"), Strings.Get("About.LicenceValue")));

        return facts;
    }

    private static string DescribeAppControl(WindowsSecurityCapabilities capabilities)
    {
        // Three genuinely different states, and the middle one is the awkward
        // truth about a stock Home machine: it would enforce a policy it has no
        // supported way to receive.
        if (!capabilities.SupportsAppLockerEnforcement)
        {
            return Strings.Get("About.NotSupported");
        }

        return capabilities.SupportsAppLockerDeployment
            ? Strings.Get("About.Available")
            : Strings.Get("About.PartlyAvailable");
    }
}
