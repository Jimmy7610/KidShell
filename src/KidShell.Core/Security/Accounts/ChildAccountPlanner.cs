using KidShell.Core.Security.Readiness;

namespace KidShell.Core.Security.Accounts;

/// <summary>What KidShell would do about the child's Windows account.</summary>
public enum ChildAccountAction
{
    /// <summary>Nothing can be decided yet.</summary>
    Unknown = 0,

    /// <summary>A suitable standard account exists and can be used as is.</summary>
    UseExisting = 1,

    /// <summary>No suitable account exists; one would be created.</summary>
    CreateNew = 2,

    /// <summary>
    /// An account was chosen but is an administrator. Demoting it is possible
    /// but is a separate, riskier decision than picking a different one.
    /// </summary>
    DemoteExisting = 3,

    /// <summary>Nothing can be done safely - see the blockers.</summary>
    Blocked = 4
}

/// <summary>Why a plan is blocked. Parent-facing wording lives alongside.</summary>
public enum ChildAccountBlocker
{
    /// <summary>No enabled administrator would remain to recover with.</summary>
    NoRecoveryAdministrator = 0,

    /// <summary>The chosen account is the only administrator.</summary>
    ChosenAccountIsOnlyAdministrator = 1,

    /// <summary>The chosen account is the one the parent is signed in as.</summary>
    ChosenAccountIsCurrentUser = 2,

    /// <summary>Accounts could not be read at all.</summary>
    AccountsUnreadable = 3,

    /// <summary>The chosen account is disabled.</summary>
    ChosenAccountDisabled = 4,

    /// <summary>The chosen account is a Windows built-in.</summary>
    ChosenAccountIsBuiltIn = 5
}

/// <summary>
/// The plan for the child's Windows account.
///
/// A plan, not an action. Nothing in KidShell creates, modifies, enables,
/// disables or deletes a Windows account, and there is deliberately no
/// interface that could - account management would need a new, visibly named
/// type to exist at all.
/// </summary>
public sealed record ChildAccountPlan
{
    public required ChildAccountAction Action { get; init; }

    /// <summary>The account this plan is about, when one was chosen or found.</summary>
    public WindowsAccount? SelectedAccount { get; init; }

    /// <summary>The administrator that must still work afterwards.</summary>
    public WindowsAccount? RecoveryAdministrator { get; init; }

    /// <summary>Suggested user name when creating a new account.</summary>
    public string ProposedUserName { get; init; } = string.Empty;

    public IReadOnlyList<ChildAccountBlocker> Blockers { get; init; } = [];

    /// <summary>Accounts that could serve as the child's, for a picker.</summary>
    public IReadOnlyList<WindowsAccount> Candidates { get; init; } = [];

    /// <summary>Ordered, parent-facing steps this plan would perform.</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    public bool IsActionable => Action is ChildAccountAction.UseExisting or ChildAccountAction.CreateNew
                                && Blockers.Count == 0;
}

/// <summary>
/// Works out what to do about the child's Windows account.
///
/// Pure: it takes the discovered accounts and a choice, and returns a plan. It
/// has no dependency on anything that could act, which is what makes the
/// Windows-account screen safe to open.
///
/// The invariant it protects, above every other consideration: **an enabled
/// administrator account that is not the child's must survive**. Everything
/// else is a preference; that one is the difference between a locked-down
/// machine and a brick.
/// </summary>
public static class ChildAccountPlanner
{
    /// <summary>Default name offered when creating an account for a child.</summary>
    public static string ProposeUserName(string childName)
    {
        var trimmed = (childName ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return "Barn";
        }

        // Windows user names disallow a specific set of characters and cannot
        // end in a period. Strip rather than reject: the parent named their
        // child, not a Windows account.
        var invalid = new HashSet<char>(@"""/\[]:;|=,+*?<>@".ToCharArray());
        var cleaned = new string([.. trimmed.Where(c => !invalid.Contains(c) && !char.IsControl(c))]).Trim().TrimEnd('.');

        if (cleaned.Length == 0)
        {
            return "Barn";
        }

        // 20 characters is the Windows limit for a local account name.
        return cleaned.Length > 20 ? cleaned[..20].TrimEnd('.') : cleaned;
    }

    /// <summary>
    /// Builds a plan from the accounts on the machine.
    ///
    /// <paramref name="chosenSid"/> is the account a parent picked, or null to
    /// let the planner recommend one.
    /// </summary>
    public static ChildAccountPlan Plan(
        IReadOnlyList<WindowsAccount> accounts,
        string childName,
        string? currentUserSid = null,
        string? chosenSid = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var proposedName = ProposeUserName(childName);
        var blockers = new List<ChildAccountBlocker>();

        if (accounts.Count == 0)
        {
            return new ChildAccountPlan
            {
                Action = ChildAccountAction.Blocked,
                ProposedUserName = proposedName,
                Blockers = [ChildAccountBlocker.AccountsUnreadable],
                Steps = []
            };
        }

        var candidates = accounts.Where(a => a.IsCandidateChildAccount).ToList();
        var administrators = accounts.Where(a => a.IsRecoveryCandidate).ToList();

        var chosen = chosenSid is null
            ? null
            : accounts.FirstOrDefault(a => string.Equals(a.Sid, chosenSid, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------ recovery invariant
        // Whatever else happens, an enabled administrator that is not the
        // child's account has to remain. Without it a failed setup has no way
        // back in.
        var recovery = administrators
            .FirstOrDefault(a => chosen is null || !string.Equals(a.Sid, chosen.Sid, StringComparison.OrdinalIgnoreCase));

        if (recovery is null)
        {
            blockers.Add(administrators.Count == 0
                ? ChildAccountBlocker.NoRecoveryAdministrator
                : ChildAccountBlocker.ChosenAccountIsOnlyAdministrator);
        }

        // ------------------------------------------------ chosen account
        if (chosen is not null)
        {
            if (!chosen.IsEnabled)
            {
                blockers.Add(ChildAccountBlocker.ChosenAccountDisabled);
            }

            if (chosen.IsBuiltIn)
            {
                blockers.Add(ChildAccountBlocker.ChosenAccountIsBuiltIn);
            }

            if (currentUserSid is not null &&
                string.Equals(chosen.Sid, currentUserSid, StringComparison.OrdinalIgnoreCase))
            {
                // Turning the signed-in parent's own account into the child's
                // is the most direct route to locking them out.
                blockers.Add(ChildAccountBlocker.ChosenAccountIsCurrentUser);
            }

            var action = blockers.Count > 0
                ? ChildAccountAction.Blocked
                : chosen.IsAdministrator
                    ? ChildAccountAction.DemoteExisting
                    : ChildAccountAction.UseExisting;

            return new ChildAccountPlan
            {
                Action = action,
                SelectedAccount = chosen,
                RecoveryAdministrator = recovery,
                ProposedUserName = proposedName,
                Blockers = blockers,
                Candidates = candidates,
                Steps = BuildSteps(action, chosen, proposedName, recovery)
            };
        }

        // ------------------------------------------------ recommend
        var recommended = candidates.FirstOrDefault();

        var recommendedAction = blockers.Count > 0
            ? ChildAccountAction.Blocked
            : recommended is not null
                ? ChildAccountAction.UseExisting
                : ChildAccountAction.CreateNew;

        return new ChildAccountPlan
        {
            Action = recommendedAction,
            SelectedAccount = recommended,
            RecoveryAdministrator = recovery,
            ProposedUserName = proposedName,
            Blockers = blockers,
            Candidates = candidates,
            Steps = BuildSteps(recommendedAction, recommended, proposedName, recovery)
        };
    }

    private static IReadOnlyList<string> BuildSteps(
        ChildAccountAction action,
        WindowsAccount? account,
        string proposedName,
        WindowsAccount? recovery)
    {
        var steps = new List<string>();

        switch (action)
        {
            case ChildAccountAction.CreateNew:
                steps.Add($"Skapa ett nytt standardkonto med namnet \"{proposedName}\".");
                steps.Add("Kontrollera att kontot inte är administratör.");
                break;

            case ChildAccountAction.UseExisting when account is not null:
                steps.Add($"Använd det befintliga kontot \"{account.Username}\".");
                steps.Add("Kontrollera att kontot fortfarande är standardanvändare.");
                break;

            case ChildAccountAction.DemoteExisting when account is not null:
                steps.Add($"Ta bort administratörsrollen från \"{account.Username}\".");
                steps.Add("Kontrollera att kontot blivit standardanvändare.");
                break;

            case ChildAccountAction.Blocked:
                return [];
        }

        if (recovery is not null)
        {
            steps.Add($"Bekräfta att du fortfarande kan logga in som \"{recovery.Username}\".");
        }

        steps.Add("Konfigurera KidShell så att det startar på barnets konto.");
        return steps;
    }

    /// <summary>Parent-facing wording for a blocker.</summary>
    public static string Describe(ChildAccountBlocker blocker) => blocker switch
    {
        ChildAccountBlocker.NoRecoveryAdministrator =>
            "Det finns inget aktivt administratörskonto att logga in med om något går fel.",
        ChildAccountBlocker.ChosenAccountIsOnlyAdministrator =>
            "Det valda kontot är det enda administratörskontot. Välj ett annat konto till barnet.",
        ChildAccountBlocker.ChosenAccountIsCurrentUser =>
            "Det valda kontot är ditt eget. Barnet behöver ett eget konto.",
        ChildAccountBlocker.AccountsUnreadable =>
            "Windows-kontona kunde inte läsas.",
        ChildAccountBlocker.ChosenAccountDisabled =>
            "Det valda kontot är avstängt.",
        ChildAccountBlocker.ChosenAccountIsBuiltIn =>
            "Det valda kontot är ett av Windows egna konton och bör inte användas.",
        _ => "Okänt hinder."
    };
}
