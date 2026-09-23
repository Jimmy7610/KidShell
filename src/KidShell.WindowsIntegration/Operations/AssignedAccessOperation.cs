using System.Security;
using System.Xml.Linq;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using KidShell.WindowsIntegration.Platform;

namespace KidShell.WindowsIntegration.Operations;

/// <summary>
/// Configures Assigned Access as a RESTRICTED USER EXPERIENCE for the child.
///
/// WHY NOT SINGLE-APP KIOSK
/// ------------------------
/// Single-app kiosk runs one UWP app full screen above the lock screen. That
/// would be a smaller attack surface and a worse product: the child could open
/// KidShell and nothing else, so every app the parent approved - Calculator,
/// Paint, a game - would be unreachable. Microsoft's restricted user experience
/// is the documented multi-app shape: a defined list of allowed applications, a
/// tailored Start menu and taskbar, and AppLocker rules applied underneath.
/// That is exactly KidShell's model, so that is what this configures.
///
/// EDITIONS
/// --------
/// Pro, Enterprise, Education and the IoT Enterprise families. Home is not on
/// that list and is never told otherwise - the Säkerhet page reports Secure
/// Mode as unavailable rather than offering a button that would fail.
///
/// UAC must be enabled, and the configuration applies to a STANDARD account.
/// Both are checked in preflight rather than discovered at Apply.
///
/// Reference: https://learn.microsoft.com/windows/configuration/assigned-access/
/// </summary>
public sealed class AssignedAccessOperation : SecurityOperationBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly IToolRunner _tools;
    private readonly ILocalAccountService _accounts;
    private readonly WindowsSecurityCapabilities _capabilities;
    private readonly string _childSid;
    private readonly string _configurationXml;

    public AssignedAccessOperation(
        IToolRunner tools,
        ILocalAccountService accounts,
        WindowsSecurityCapabilities capabilities,
        string childSid,
        string configurationXml,
        IKidShellLogger logger) : base(logger)
    {
        _tools = tools;
        _accounts = accounts;
        _capabilities = capabilities;
        _childSid = childSid;
        _configurationXml = configurationXml;
    }

    public override string Id => "assigned-access";

    public override string Description => "Ställ in Windows begränsade läge för barnet";

    public override ChangeRiskLevel RiskLevel => ChangeRiskLevel.High;

    public override RequiredCapability CapabilityRequired => RequiredCapability.AssignedAccess;

    private static string PowerShellPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

    protected override async Task<OperationOutcome> DoPreflightAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        if (!_capabilities.SupportsAssignedAccess)
        {
            return OperationOutcome.Fail(
                $"{_capabilities.EditionDisplayName} stöder inte Windows begränsade läge. " +
                "Det kräver Windows Pro, Enterprise, Education eller IoT Enterprise.");
        }

        // Microsoft states UAC must be enabled. Without it the separate child
        // account provides no real separation, so a "secure" mode built on top
        // would be a claim KidShell could not back up.
        if (_capabilities.IsUacEnabled == false)
        {
            return OperationOutcome.Fail(
                "Användarkontokontroll (UAC) är avstängd. Windows begränsade läge kräver att den är på.");
        }

        var account = await _accounts.FindBySidAsync(_childSid, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            return OperationOutcome.Fail("Barnkontot kunde inte hittas.");
        }

        if (account.IsAdministrator)
        {
            return OperationOutcome.Fail(
                $"Kontot \"{account.Username}\" är administratör. Det begränsade läget gäller standardkonton.");
        }

        if (!account.IsEnabled)
        {
            return OperationOutcome.Fail($"Kontot \"{account.Username}\" är avstängt.");
        }

        var validation = ValidateConfiguration(_configurationXml, account.Username);

        if (validation is not null)
        {
            return OperationOutcome.Fail(validation);
        }

        return OperationOutcome.Ok("Det begränsade läget kan ställas in.");
    }

    /// <summary>
    /// Checks the generated configuration before it becomes the child's entire
    /// Windows experience.
    ///
    /// The failure that matters: a configuration naming an account that is not
    /// the child's would restrict the wrong person, and a configuration with no
    /// allowed apps would give the child a working login and nothing to do.
    /// </summary>
    internal static string? ValidateConfiguration(string xml, string expectedAccount)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return "Konfigurationen för begränsat läge är tom.";
        }

        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            return $"Konfigurationen är inte giltig XML: {ex.Message}";
        }

        if (document.Root?.Name.LocalName != "AssignedAccessConfiguration")
        {
            return "Konfigurationen är inte en AssignedAccessConfiguration.";
        }

        var accounts = document.Descendants()
            .Where(e => e.Name.LocalName == "Account")
            .Select(e => e.Value.Trim())
            .ToList();

        if (accounts.Count == 0)
        {
            return "Konfigurationen anger inget konto.";
        }

        // Compared on the account name, allowing the MACHINE\user form the CSP
        // uses.
        if (!accounts.Any(a =>
                a.EndsWith($"\\{expectedAccount}", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, expectedAccount, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Konfigurationen gäller inte kontot \"{expectedAccount}\".";
        }

        var allowedApps = document.Descendants().Count(e => e.Name.LocalName == "App");

        if (allowedApps == 0)
        {
            return "Konfigurationen tillåter inga program alls.";
        }

        return null;
    }

    protected override async Task<OperationSnapshot> DoCaptureStateAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var existing = await ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);

        return new OperationSnapshot
        {
            OperationId = Id,
            Description = string.IsNullOrWhiteSpace(existing)
                ? "Datorn hade inget begränsat läge inställt."
                : "Datorn hade redan ett begränsat läge inställt. Det är sparat.",
            PreviousValue = existing,
            ExistedBefore = !string.IsNullOrWhiteSpace(existing)
        };
    }

    /// <summary>
    /// Reads the current configuration through the MDM WMI bridge, which is the
    /// documented way to reach the AssignedAccess CSP on a device that is not
    /// enrolled with an MDM authority.
    /// </summary>
    private async Task<string?> ReadConfigurationAsync(CancellationToken cancellationToken)
    {
        var result = await _tools.RunAsync(PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command",
             "(Get-CimInstance -Namespace root\\cimv2\\mdm\\dmmap " +
             "-ClassName MDM_AssignedAccess -ErrorAction SilentlyContinue).Configuration"],
            Timeout, cancellationToken).ConfigureAwait(false);

        return result.Succeeded && result.StandardOutput.Trim().Length > 0
            ? result.StandardOutput.Trim()
            : null;
    }

    protected override async Task<OperationOutcome> DoApplyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var result = await WriteConfigurationAsync(_configurationXml, cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            ? OperationOutcome.Ok("Det begränsade läget ställdes in.")
            : OperationOutcome.Fail(
                "Det begränsade läget kunde inte ställas in.",
                $"exit {result.ExitCode}: {result.StandardError.Trim()}");
    }

    private Task<ToolResult> WriteConfigurationAsync(string xml, CancellationToken cancellationToken)
    {
        // The XML is passed through a here-string with its own quotes escaped.
        // It is generated by KidShell from validated settings, never taken from
        // a file a child could edit.
        var escaped = SecurityElement.Escape(xml) ?? string.Empty;

        var script =
            "$xml = [System.Web.HttpUtility]::HtmlDecode(@'\n" + escaped + "\n'@);" +
            "$obj = Get-CimInstance -Namespace root\\cimv2\\mdm\\dmmap -ClassName MDM_AssignedAccess;" +
            "$obj.Configuration = $xml;" +
            "Set-CimInstance -CimInstance $obj -ErrorAction Stop";

        return _tools.RunAsync(PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command",
             "Add-Type -AssemblyName System.Web; " + script],
            Timeout, cancellationToken);
    }

    protected override async Task<OperationOutcome> DoVerifyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var current = await ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(current))
        {
            return OperationOutcome.Fail("Det begränsade läget kunde inte läsas tillbaka.");
        }

        // Compared structurally rather than as text: the CSP normalises
        // whitespace and attribute order, so a string comparison would report a
        // false failure on a configuration that is in fact correct.
        if (!SameConfiguration(current, _configurationXml))
        {
            return OperationOutcome.Fail(
                "Det begränsade läget innehåller inte det KidShell skrev.");
        }

        return OperationOutcome.Ok("Det begränsade läget är bekräftat.");
    }

    internal static bool SameConfiguration(string a, string b)
    {
        try
        {
            var left = XDocument.Parse(a);
            var right = XDocument.Parse(b);

            var leftAccounts = Names(left, "Account");
            var rightAccounts = Names(right, "Account");
            var leftApps = Names(left, "App");
            var rightApps = Names(right, "App");

            return leftAccounts.SetEquals(rightAccounts) && leftApps.SetEquals(rightApps);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static HashSet<string> Names(XDocument document, string element) =>
        [.. document.Descendants()
            .Where(e => e.Name.LocalName == element)
            .Select(e => (string?)e.Attribute("AppUserModelId")
                         ?? (string?)e.Attribute("DesktopAppPath")
                         ?? e.Value.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())];

    protected override async Task<OperationOutcome> DoRollbackAsync(
        OperationSnapshot snapshot, SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        if (snapshot.ExistedBefore && !string.IsNullOrWhiteSpace(snapshot.PreviousValue))
        {
            var restore = await WriteConfigurationAsync(snapshot.PreviousValue, cancellationToken).ConfigureAwait(false);

            return restore.Succeeded
                ? OperationOutcome.Ok("Det tidigare begränsade läget återställdes.")
                : OperationOutcome.Fail(
                    "Det tidigare begränsade läget kunde inte återställas.", restore.StandardError.Trim());
        }

        // Nothing was configured before, so removing the configuration is the
        // correct undo. Clearing the CSP property is the documented way.
        var clear = await _tools.RunAsync(PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command",
             "$obj = Get-CimInstance -Namespace root\\cimv2\\mdm\\dmmap -ClassName MDM_AssignedAccess;" +
             "$obj.Configuration = $null;" +
             "Set-CimInstance -CimInstance $obj -ErrorAction Stop"],
            Timeout, cancellationToken).ConfigureAwait(false);

        if (!clear.Succeeded)
        {
            return OperationOutcome.Fail(
                "Det begränsade läget kunde inte tas bort.", clear.StandardError.Trim());
        }

        var remaining = await ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(remaining)
            ? OperationOutcome.Ok("Det begränsade läget togs bort.")
            : OperationOutcome.Fail("Det begränsade läget finns kvar.");
    }
}
