namespace KidShell.Core.Security.Readiness;

/// <summary>
/// Describes what secure setup would do on this machine.
///
/// Pure: it reads capabilities and returns descriptions. It holds no reference
/// to anything that could execute a step, which is what makes "Visa
/// säkerhetsplan" safe to press.
/// </summary>
public static class SecurityPlanBuilder
{
    /// <summary>
    /// Builds the plan for the mode being recommended. The Secure plan adds
    /// the Assigned Access steps on top of the Standard ones rather than being
    /// a separate list, so the two stay honest about their difference.
    /// </summary>
    public static IReadOnlyList<PlannedAction> Build(WindowsSecurityCapabilities capabilities, SecurityMode targetMode)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (targetMode == SecurityMode.Development)
        {
            return [];
        }

        var actions = new List<PlannedAction>();
        var order = 1;

        void Add(
            string id,
            string description,
            string detail,
            bool requiresAdmin,
            RequiredCapability capability,
            ChangeRiskLevel risk,
            bool canRollback)
        {
            actions.Add(new PlannedAction
            {
                Order = order++,
                Id = id,
                Description = description,
                Detail = detail,
                RequiresAdmin = requiresAdmin,
                CapabilityRequired = capability,
                RiskLevel = risk,
                CanRollback = canRollback
            });
        }

        Add("child-account",
            "Skapa eller välj separat barnkonto",
            "Ett eget Windows-konto för barnet, skilt från ditt.",
            requiresAdmin: true,
            RequiredCapability.LocalAccountManagement,
            ChangeRiskLevel.High,
            canRollback: true);

        Add("verify-standard-user",
            "Verifiera att kontot är standardanvändare",
            "Kontrollerar att barnets konto inte är administratör.",
            requiresAdmin: true,
            RequiredCapability.LocalAccountManagement,
            ChangeRiskLevel.Medium,
            canRollback: true);

        Add("verify-recovery-account",
            "Verifiera administratörens återställningskonto",
            "Säkerställer att du har ett eget konto kvar att logga in med.",
            requiresAdmin: true,
            RequiredCapability.LocalAccountManagement,
            ChangeRiskLevel.Low,
            canRollback: true);

        Add("configure-apps",
            "Konfigurera tillåtna appar",
            "Skriver KidShells egen applista för barnets konto.",
            requiresAdmin: false,
            RequiredCapability.KidShellAppAllowlist,
            ChangeRiskLevel.Low,
            canRollback: true);

        if (capabilities.SupportsAppLocker && targetMode == SecurityMode.Secure)
        {
            Add("configure-applocker",
                "Konfigurera appkontroll i Windows",
                "Lägger till regler som hindrar andra program från att starta.",
                requiresAdmin: true,
                RequiredCapability.AppLocker,
                ChangeRiskLevel.High,
                canRollback: true);
        }

        if (targetMode == SecurityMode.Secure)
        {
            Add("configure-assigned-access",
                "Konfigurera Assigned Access",
                "Låser barnets inloggning till KidShell.",
                requiresAdmin: true,
                RequiredCapability.AssignedAccess,
                ChangeRiskLevel.High,
                canRollback: true);
        }

        Add("configure-autostart",
            "Konfigurera KidShell autostart",
            "Startar KidShell automatiskt när barnet loggar in.",
            requiresAdmin: false,
            RequiredCapability.None,
            ChangeRiskLevel.Medium,
            canRollback: true);

        Add("configure-browser-policy",
            "Konfigurera browser-policy",
            "Begränsar webbläsaren enligt ditt val på Webb-sidan.",
            requiresAdmin: true,
            RequiredCapability.None,
            ChangeRiskLevel.Medium,
            canRollback: true);

        Add("install-watchdog",
            "Installera återställningsövervakning",
            "Startar om KidShell om det stängs av på barnets konto.",
            requiresAdmin: true,
            RequiredCapability.None,
            ChangeRiskLevel.High,
            canRollback: true);

        return actions;
    }
}
