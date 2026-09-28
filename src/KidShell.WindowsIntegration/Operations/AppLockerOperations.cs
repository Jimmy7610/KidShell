using System.Xml.Linq;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using KidShell.WindowsIntegration.Platform;

namespace KidShell.WindowsIntegration.Operations;

/// <summary>
/// Deploys a generated AppLocker policy through the AppLocker PowerShell
/// module, which is the documented local deployment route.
///
/// WHAT MICROSOFT ACTUALLY SAYS
/// ----------------------------
/// Since KB 5024351, Windows 10 2004+ and all Windows 11 editions can ENFORCE
/// AppLocker policies. That is not the same as being able to DEPLOY one, and
/// conflating the two is the single most common false claim about AppLocker.
/// Group Policy deployment needs GPMC/RSAT; MDM deployment needs a management
/// authority. A stock Windows Home machine can enforce a policy it has no
/// supported way to receive.
///
/// KidShell's answer to that is to block activation and say so, never to write
/// SrpV2 registry keys by hand. That would work, and it would be undocumented,
/// unsupported, and exactly the kind of trick that makes a security product
/// untrustworthy.
///
/// Reference:
/// https://learn.microsoft.com/windows/security/application-security/application-control/app-control-for-business/applocker/requirements-to-use-applocker
/// </summary>
public sealed class AppLockerDeploymentOperation : SecurityOperationBase
{
    private static readonly TimeSpan PowerShellTimeout = TimeSpan.FromMinutes(2);

    private readonly IToolRunner _tools;
    private readonly IFileSystem _files;
    private readonly string _policyXml;
    private readonly string _workingDirectory;

    private string? _capturedPolicyPath;

    public AppLockerDeploymentOperation(
        IToolRunner tools,
        IFileSystem files,
        string policyXml,
        string workingDirectory,
        IKidShellLogger logger) : base(logger)
    {
        _tools = tools;
        _files = files;
        _policyXml = policyXml;
        _workingDirectory = workingDirectory;
    }

    public override string Id => "applocker-deploy";

    public override string Description => "Installera AppLocker-regler";

    public override ChangeRiskLevel RiskLevel => ChangeRiskLevel.High;

    public override RequiredCapability CapabilityRequired => RequiredCapability.AppLockerDeployment;

    private static string PowerShellPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

    private string CapturePath => Path.Combine(_workingDirectory, "applocker-previous.xml");

    private string ApplyPath => Path.Combine(_workingDirectory, "applocker-new.xml");

    // ------------------------------------------------------------ preflight

    protected override async Task<OperationOutcome> DoPreflightAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var validation = ValidatePolicy(_policyXml);

        if (validation is not null)
        {
            return OperationOutcome.Fail(validation);
        }

        // A machine that cannot run the module cannot receive a policy through
        // this channel, and there is no fallback that KidShell is willing to
        // use.
        var probe = await _tools.RunAsync(PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command",
             "if (Get-Module -ListAvailable -Name AppLocker) { exit 0 } else { exit 1 }"],
            PowerShellTimeout, cancellationToken).ConfigureAwait(false);

        if (!probe.Succeeded)
        {
            return OperationOutcome.Fail(
                "AppLockers PowerShell-modul finns inte på den här datorn, så reglerna kan inte installeras. " +
                "KidShell använder inte odokumenterade genvägar.");
        }

        return OperationOutcome.Ok("AppLocker-regler kan installeras.");
    }

    /// <summary>
    /// Checks the generated XML before it can lock anybody out.
    ///
    /// The rules that matter are not "is this well-formed" but "does this still
    /// let an administrator in". A policy that denies the Administrators group
    /// is a policy that can end a family's use of their computer, and it must
    /// never reach Apply.
    /// </summary>
    internal static string? ValidatePolicy(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return "Regelfilen är tom.";
        }

        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            return $"Regelfilen är inte giltig XML: {ex.Message}";
        }

        if (document.Root?.Name.LocalName != "AppLockerPolicy")
        {
            return "Regelfilen är inte en AppLocker-policy.";
        }

        var collections = document.Root.Elements("RuleCollection").ToList();

        if (collections.Count == 0)
        {
            return "Regelfilen innehåller inga regelsamlingar.";
        }

        // Every enforced collection must allow the local Administrators group
        // everything. Without it, a failed rollback locks the parent out of the
        // tool they would use to fix it.
        const string administratorsSid = "S-1-5-32-544";

        foreach (var collection in collections.Where(c =>
                     (string?)c.Attribute("EnforcementMode") == "Enabled"))
        {
            var type = (string?)collection.Attribute("Type") ?? "okänd";

            var adminEscape = collection.Elements()
                .Where(r => (string?)r.Attribute("Action") == "Allow")
                .Any(r => (string?)r.Attribute("UserOrGroupSid") == administratorsSid);

            if (!adminEscape)
            {
                return $"Regelsamlingen \"{type}\" saknar en regel som släpper igenom administratörer. " +
                       "KidShell installerar inte regler som kan låsa ute en vuxen.";
            }
        }

        return null;
    }

    // -------------------------------------------------------------- capture

    protected override async Task<OperationSnapshot> DoCaptureStateAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        await _files.CreateDirectoryAsync(_workingDirectory, cancellationToken).ConfigureAwait(false);

        // Get-AppLockerPolicy -Local -Xml is the documented way to read the
        // machine's own policy back. Exported to a file rather than parsed:
        // rollback re-applies exactly the bytes that were there.
        var export = await _tools.RunAsync(PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command",
             $"(Get-AppLockerPolicy -Local -Xml) | Out-File -FilePath '{CapturePath}' -Encoding utf8"],
            PowerShellTimeout, cancellationToken).ConfigureAwait(false);

        var captured = export.Succeeded &&
                       await _files.FileExistsAsync(CapturePath, cancellationToken).ConfigureAwait(false);

        if (captured)
        {
            _capturedPolicyPath = CapturePath;
        }

        var previous = captured
            ? await _files.ReadAllTextAsync(CapturePath, cancellationToken).ConfigureAwait(false)
            : null;

        var hadRules = previous is not null && previous.Contains("<RuleCollection", StringComparison.Ordinal) &&
                       !IsEmptyPolicy(previous);

        return new OperationSnapshot
        {
            OperationId = Id,
            Description = hadRules
                ? "Datorn hade redan en lokal AppLocker-policy. Den är sparad."
                : "Datorn hade ingen lokal AppLocker-policy.",

            // The path, not the XML: a policy can be large, and the recovery
            // manifest is a file a worried parent opens in Notepad.
            PreviousValue = captured ? CapturePath : null,
            ExistedBefore = hadRules
        };
    }

    /// <summary>An AppLocker export with no rules is "no policy", not "a policy that allows nothing".</summary>
    internal static bool IsEmptyPolicy(string xml)
    {
        try
        {
            var document = XDocument.Parse(xml);
            return document.Root?.Elements("RuleCollection").All(c => !c.Elements().Any()) ?? true;
        }
        catch (System.Xml.XmlException)
        {
            return true;
        }
    }

    // ---------------------------------------------------------------- apply

    protected override async Task<OperationOutcome> DoApplyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        await _files.WriteAllTextAsync(ApplyPath, _policyXml, cancellationToken).ConfigureAwait(false);

        // No -Merge: KidShell sets the policy it validated, so what is on the
        // machine afterwards is what the parent reviewed. Merging would produce
        // a combined policy nobody had seen.
        var result = await _tools.RunAsync(PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command",
             $"Set-AppLockerPolicy -XmlPolicy '{ApplyPath}' -ErrorAction Stop"],
            PowerShellTimeout, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return OperationOutcome.Fail(
                "AppLocker-reglerna kunde inte installeras.",
                $"exit {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return OperationOutcome.Ok("AppLocker-reglerna installerades.");
    }

    // --------------------------------------------------------------- verify

    protected override async Task<OperationOutcome> DoVerifyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        // Read the EFFECTIVE policy, not the local one. Local says what was
        // written; effective says what Windows will actually enforce after
        // combining every applicable policy - and that difference is the whole
        // reason verification exists.
        var result = await _tools.RunAsync(PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command", "(Get-AppLockerPolicy -Effective -Xml)"],
            PowerShellTimeout, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return OperationOutcome.Fail(
                "AppLocker-reglerna kunde inte läsas tillbaka, så de kan inte bekräftas.",
                result.StandardError.Trim());
        }

        var effective = result.StandardOutput;

        if (!effective.Contains("<AppLockerPolicy", StringComparison.Ordinal))
        {
            return OperationOutcome.Fail("AppLocker svarade inte med en policy.");
        }

        var expectedRuleIds = RuleIds(_policyXml);
        var actualRuleIds = RuleIds(effective);
        var missing = expectedRuleIds.Except(actualRuleIds).ToList();

        if (missing.Count > 0)
        {
            return OperationOutcome.Fail(
                "Alla regler kom inte med i den gällande policyn.",
                $"missing rule ids: {string.Join(", ", missing.Take(5))}");
        }

        // Written AND enforced. A policy present in audit mode protects nobody,
        // so reporting success on that would be the exact overclaim this
        // codebase exists to avoid.
        if (!HasEnforcedCollection(effective))
        {
            return OperationOutcome.Fail(
                "Reglerna finns men tillämpas inte. KidShell rapporterar inte skydd som inte gäller.");
        }

        return OperationOutcome.Ok("AppLocker-reglerna gäller och är bekräftade.");
    }

    internal static HashSet<string> RuleIds(string xml)
    {
        try
        {
            return [.. XDocument.Parse(xml)
                .Descendants()
                .Select(e => (string?)e.Attribute("Id"))
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)];
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    internal static bool HasEnforcedCollection(string xml)
    {
        try
        {
            return XDocument.Parse(xml)
                .Descendants("RuleCollection")
                .Any(c => (string?)c.Attribute("EnforcementMode") == "Enabled");
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------- rollback

    protected override async Task<OperationOutcome> DoRollbackAsync(
        OperationSnapshot snapshot, SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var path = snapshot.PreviousValue ?? _capturedPolicyPath;

        if (path is null || !await _files.FileExistsAsync(path, cancellationToken).ConfigureAwait(false))
        {
            return OperationOutcome.Fail(
                "Den tidigare AppLocker-policyn kunde inte hittas, så reglerna kunde inte återställas. " +
                "Följ återställningsfilen manuellt.");
        }

        // Re-applying the captured export restores exactly what was there,
        // including "no rules at all", which is the usual previous state.
        var result = await _tools.RunAsync(PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-Command",
             $"Set-AppLockerPolicy -XmlPolicy '{path}' -ErrorAction Stop"],
            PowerShellTimeout, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return OperationOutcome.Fail(
                "AppLocker-reglerna kunde inte återställas.",
                $"exit {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return OperationOutcome.Ok("Den tidigare AppLocker-policyn återställdes.");
    }
}

/// <summary>
/// Sets the Application Identity service to start automatically.
///
/// AppLocker enforces nothing while AppIDSvc is stopped, so this is part of
/// making a policy real rather than decorative.
///
/// THE ASYMMETRY IS MICROSOFT'S, NOT KIDSHELL'S
/// --------------------------------------------
/// Since Windows 10 the service is a protected process. Microsoft documents
/// <c>sc.exe config appidsvc start=auto</c> as the way to set it automatic, and
/// states plainly that the startup type "cannot be set to Manual using sc.exe".
/// So Apply and Rollback deliberately use different documented mechanisms:
/// sc.exe to turn it on, and a secedit security template to put it back. Using
/// sc.exe for both would produce a rollback that silently does nothing - the
/// worst possible outcome for a recovery path.
///
/// Reference:
/// https://learn.microsoft.com/windows/security/application-security/application-control/app-control-for-business/applocker/configure-the-application-identity-service
/// </summary>
public sealed class ApplicationIdentityServiceOperation : SecurityOperationBase
{
    private const string ServiceName = "appidsvc";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Security-template start values: 2 automatic, 3 manual, 4 disabled.</summary>
    private const int TemplateAutomatic = 2;
    private const int TemplateManual = 3;

    private readonly IServiceControl _services;
    private readonly IToolRunner _tools;
    private readonly IFileSystem _files;
    private readonly string _workingDirectory;

    public ApplicationIdentityServiceOperation(
        IServiceControl services,
        IToolRunner tools,
        IFileSystem files,
        string workingDirectory,
        IKidShellLogger logger) : base(logger)
    {
        _services = services;
        _tools = tools;
        _files = files;
        _workingDirectory = workingDirectory;
    }

    public override string Id => "appid-service";

    public override string Description => "Starta tjänsten som får AppLocker att gälla";

    public override ChangeRiskLevel RiskLevel => ChangeRiskLevel.Medium;

    public override RequiredCapability CapabilityRequired => RequiredCapability.AppLockerEnforcement;

    protected override async Task<OperationOutcome> DoPreflightAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var status = await _services.QueryAsync(ServiceName, cancellationToken).ConfigureAwait(false);

        if (!status.IsInstalled)
        {
            return OperationOutcome.Fail(
                "Tjänsten Programidentitet finns inte på den här datorn, så AppLocker kan inte tillämpas.");
        }

        return OperationOutcome.Ok("Tjänsten Programidentitet kan ställas in att starta automatiskt.");
    }

    protected override async Task<OperationSnapshot> DoCaptureStateAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var status = await _services.QueryAsync(ServiceName, cancellationToken).ConfigureAwait(false);

        return new OperationSnapshot
        {
            OperationId = Id,
            Description = $"Tjänsten Programidentitet startade som \"{(status.StartType.Length == 0 ? "okänt" : status.StartType)}\".",
            PreviousValue = status.StartType,
            ExistedBefore = status.IsInstalled
        };
    }

    protected override async Task<OperationOutcome> DoApplyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        await _services.SetStartTypeAsync(ServiceName, "auto", cancellationToken).ConfigureAwait(false);
        await _services.StartAsync(ServiceName, cancellationToken).ConfigureAwait(false);

        return OperationOutcome.Ok("Tjänsten Programidentitet startar nu automatiskt.");
    }

    protected override async Task<OperationOutcome> DoVerifyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var status = await _services.QueryAsync(ServiceName, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(status.StartType, "Automatic", StringComparison.OrdinalIgnoreCase))
        {
            return OperationOutcome.Fail(
                "Tjänsten Programidentitet är inte inställd på automatisk start.",
                $"start type is '{status.StartType}'");
        }

        if (!status.IsRunning)
        {
            // Configured but stopped means AppLocker enforces nothing right now.
            return OperationOutcome.Fail("Tjänsten Programidentitet körs inte.");
        }

        return OperationOutcome.Ok("Tjänsten Programidentitet körs och startar automatiskt.");
    }

    protected override async Task<OperationOutcome> DoRollbackAsync(
        OperationSnapshot snapshot, SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var previous = snapshot.PreviousValue ?? string.Empty;

        if (string.Equals(previous, "Automatic", StringComparison.OrdinalIgnoreCase))
        {
            return OperationOutcome.Ok("Tjänsten startade automatiskt redan innan, så inget behövde ändras.");
        }

        var target = string.Equals(previous, "Disabled", StringComparison.OrdinalIgnoreCase)
            ? 4
            : TemplateManual;

        return await ApplyTemplateAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Restores the start type with secedit, the documented alternative for a
    /// protected service that sc.exe cannot move back to Manual.
    /// </summary>
    private async Task<OperationOutcome> ApplyTemplateAsync(int startValue, CancellationToken cancellationToken)
    {
        await _files.CreateDirectoryAsync(_workingDirectory, cancellationToken).ConfigureAwait(false);

        var infPath = Path.Combine(_workingDirectory, "appidsvc-restore.inf");
        var dbPath = Path.Combine(_workingDirectory, "appidsvc-restore.sdb");

        // An empty ACL string means "leave the service's permissions alone" -
        // KidShell changes the start type and nothing else.
        var template = string.Join("\r\n",
            "[Unicode]",
            "Unicode=yes",
            "[Version]",
            "signature=\"$CHICAGO$\"",
            "Revision=1",
            "[Service General Setting]",
            $"\"{ServiceName}\",{startValue},\"\"",
            string.Empty);

        await _files.WriteAllTextAsync(infPath, template, cancellationToken).ConfigureAwait(false);

        var seceditPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "secedit.exe");

        var result = await _tools.RunAsync(seceditPath,
            ["/configure", "/db", dbPath, "/cfg", infPath, "/areas", "SERVICES", "/quiet"],
            Timeout, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return OperationOutcome.Fail(
                "Tjänsten Programidentitet kunde inte återställas automatiskt. Se återställningsfilen.",
                $"secedit exit {result.ExitCode}: {result.StandardOutput.Trim()}");
        }

        var status = await _services.QueryAsync(ServiceName, cancellationToken).ConfigureAwait(false);

        var expected = startValue == TemplateManual ? "Manual" : "Disabled";

        return string.Equals(status.StartType, expected, StringComparison.OrdinalIgnoreCase)
            ? OperationOutcome.Ok($"Tjänsten Programidentitet återställdes till \"{expected}\".")
            : OperationOutcome.Fail(
                "Tjänsten Programidentitet kunde inte bekräftas som återställd.",
                $"expected '{expected}', found '{status.StartType}'");
    }

    /// <summary>Exposed for tests: the template a given start value produces.</summary>
    internal static int AutomaticTemplateValue => TemplateAutomatic;
}
