namespace KidShell.Core.Security.Validation;

/// <summary>
/// What is actually true about the child account right now, as observed by a
/// script that asked Windows.
///
/// Every field is an observation, not a conclusion. The nullable ones are the
/// important part: <see cref="InStandardUsersGroup"/> is null when membership
/// could not be read, and that is not the same as false.
/// </summary>
public sealed record ChildAccountFacts
{
    /// <summary>The name the config asks for.</summary>
    public required string ConfiguredName { get; init; }

    /// <summary>Whether a local account with that name exists.</summary>
    public bool Exists { get; init; }

    /// <summary>Whether it is enabled. Meaningless when it does not exist.</summary>
    public bool Enabled { get; init; }

    /// <summary>Its SID, as read from Windows.</summary>
    public string Sid { get; init; } = string.Empty;

    /// <summary>
    /// The SID the config expects, when it records one.
    ///
    /// Empty is ordinary on a first run: the SID is not known until the account
    /// exists. A MISMATCH is not ordinary - it means the account was deleted and
    /// recreated, and every permission scoped to the old SID now refers to
    /// nobody.
    /// </summary>
    public string ExpectedSid { get; init; } = string.Empty;

    /// <summary>
    /// Whether it is in the built-in Users group (S-1-5-32-545), or null when
    /// that could not be determined.
    ///
    /// Null must never be treated as false. False means "add it"; null means
    /// "we do not know, so do not report this stage as finished".
    /// </summary>
    public bool? InStandardUsersGroup { get; init; }

    /// <summary>
    /// The SIDs of privileged groups this account was found in. Empty is the
    /// required state.
    /// </summary>
    public IReadOnlyList<string> PrivilegedGroupSids { get; init; } = [];

    /// <summary>
    /// Privileged groups that exist on this machine and could not be read.
    ///
    /// Not an error and not a pass: it means the question "is the child
    /// privileged" has no answer yet.
    /// </summary>
    public IReadOnlyList<string> UnreadablePrivilegedGroupSids { get; init; } = [];

    /// <summary>
    /// Whether at least one enabled administrator other than this account
    /// exists. The invariant that outranks everything.
    /// </summary>
    public bool EnabledRecoveryAdministratorExists { get; init; }
}

/// <summary>What the script should do next.</summary>
public enum ChildAccountAction
{
    /// <summary>Do nothing. The account is already exactly what it must be.</summary>
    None = 0,

    /// <summary>Create the account and put it in the standard Users group.</summary>
    Create = 1,

    /// <summary>The account exists and some state this script owns is missing.</summary>
    Repair = 2,

    /// <summary>Change nothing. Something is wrong that this script must not fix silently.</summary>
    Refuse = 3
}

/// <summary>
/// How a completed run should be reported.
///
/// SEPARATE FROM THE ACTION ON PURPOSE. The defect this type exists to stop was
/// a script that created an account, failed to add it to the Users group, and
/// then printed "Created 'KidShellChild'." in green. The action and the outcome
/// are different questions, and conflating them is how a half-finished stage
/// gets ticked off.
/// </summary>
public enum ChildAccountResult
{
    /// <summary>Everything this script owns is in place.</summary>
    Success = 0,

    /// <summary>The account exists but something it needs does not. Re-runnable.</summary>
    Partial = 1,

    /// <summary>Deliberately did nothing, because doing something would be unsafe.</summary>
    Refused = 2,

    /// <summary>An attempt was made and did not work.</summary>
    Failed = 3
}

/// <summary>
/// THE DECISION. Observed facts in, one action out.
///
/// WHY THIS IS A TYPE AND NOT AN IF-STATEMENT IN A SCRIPT
/// -----------------------------------------------------
/// On WILMA, `apply\01-create-child-account.ps1 -Apply` created KidShellChild
/// (SID ...-1003, enabled) and then failed to add it to the Users group,
/// because `Add-LocalGroupMember -Member` is typed `LocalPrincipal[]` and a
/// `SecurityIdentifier` object has no conversion to it. The script printed the
/// failure as a yellow "Note:", then printed "Created 'KidShellChild'." in
/// green and exited 0.
///
/// Worse, its first statement was:
///
///     if ($existing) { Write-Host 'already exists'; return }
///
/// so re-running it after the code was fixed would have reported the broken
/// account as fine and repaired nothing. A machine could only be recovered by
/// deleting the child account - which on a real device means destroying a
/// profile - or by hand.
///
/// So the decision lives here, where the eight states it has to get right can
/// be tested without touching Windows, and the script becomes three steps:
/// observe, ask, act.
///
/// WHAT THIS PLAN MAY AND MAY NOT REPAIR
/// -------------------------------------
/// It may add the missing standard-Users membership, because that is state this
/// script created and failed to finish. It may not remove privilege, enable a
/// disabled account, or reconcile a SID mismatch: each of those is somebody
/// else's decision about a machine whose history this script does not know.
/// Those states refuse, and say exactly what was found.
/// </summary>
public sealed record ChildAccountSetupPlan
{
    /// <summary>What to do.</summary>
    public required ChildAccountAction Action { get; init; }

    /// <summary>How to report the run if the action succeeds.</summary>
    public required ChildAccountResult ResultIfActionSucceeds { get; init; }

    /// <summary>Whether the standard Users membership has to be added.</summary>
    public bool AddStandardUsersMembership { get; init; }

    /// <summary>Whether a password has to be asked for. Only ever when creating.</summary>
    public bool NeedsPassword { get; init; }

    /// <summary>Why, in the order the reasons were established.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Whether the plan declines to act.</summary>
    public bool Refused => Action == ChildAccountAction.Refuse;

    /// <summary>
    /// Decides. The order of these checks is the safety argument.
    /// </summary>
    public static ChildAccountSetupPlan Decide(ChildAccountFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(facts.ConfiguredName))
        {
            return Refuse(["The validation config does not name a child account."]);
        }

        // FIRST, ALWAYS, WHETHER CREATING OR REPAIRING: somebody who is not the
        // child must still be able to sign in as an administrator. This is
        // checked even for a no-op, because a run that reports "all good" on a
        // machine with no recovery administrator has told the operator the
        // opposite of what they need to know.
        if (!facts.EnabledRecoveryAdministratorExists)
        {
            reasons.Add("No enabled administrator account other than the child was found. "
                        + "A second enabled administrator must exist at every moment.");

            return Refuse(reasons);
        }

        if (!facts.Exists)
        {
            reasons.Add($"The account '{facts.ConfiguredName}' does not exist, so it will be created "
                        + "as a standard account and added to the built-in Users group "
                        + $"({WellKnownSecurityGroups.UsersSid}).");

            return new ChildAccountSetupPlan
            {
                Action = ChildAccountAction.Create,
                ResultIfActionSucceeds = ChildAccountResult.Success,
                AddStandardUsersMembership = true,
                NeedsPassword = true,
                Reasons = reasons
            };
        }

        // ------------------------------------------------- it already exists
        //
        // Everything below is the recovery path. The old script returned here.

        reasons.Add($"The account '{facts.ConfiguredName}' already exists with SID {Describe(facts.Sid)}.");

        if (string.IsNullOrWhiteSpace(facts.Sid))
        {
            // An account whose SID cannot be read cannot be the subject of any
            // access list, so nothing may be concluded or changed.
            reasons.Add("Its SID could not be read, so it cannot be identified and will not be touched.");

            return Refuse(reasons);
        }

        if (!WellKnownSecurityGroups.LooksLikeSid(facts.Sid))
        {
            reasons.Add($"Its SID '{facts.Sid}' is not a SID, so the account cannot be identified.");

            return Refuse(reasons);
        }

        // A name that resolves to a different account than the config was
        // written against. Deleted and recreated, or a different machine's
        // config. Either way every ACL scoped to the old SID now refers to
        // nobody, and this script must not paper over it.
        if (!string.IsNullOrWhiteSpace(facts.ExpectedSid) &&
            !string.Equals(facts.ExpectedSid.Trim(), facts.Sid.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"The config expects SID {facts.ExpectedSid.Trim()}, so this is a different account "
                        + "under the same name. Every permission scoped to the expected SID now refers to "
                        + "nobody. Resolve this deliberately rather than automatically.");

            return Refuse(reasons);
        }

        // PRIVILEGE BEFORE ANYTHING ELSE. An account that is both the child and
        // privileged is the one state where carrying on would be actively
        // dangerous, so it outranks every repair below.
        if (facts.PrivilegedGroupSids.Count > 0)
        {
            foreach (var sid in facts.PrivilegedGroupSids)
            {
                reasons.Add($"It is in the privileged group {WellKnownSecurityGroups.LabelOf(sid)} ({sid}).");
            }

            reasons.Add("A child account must be a plain standard user. Privilege is not removed "
                        + "automatically: removing a group membership this script did not create could "
                        + "take away the only administrator on the machine.");

            return Refuse(reasons);
        }

        // "Could not look" is not "nothing there". Reported before the Users
        // membership repair, because repairing an account whose privileges are
        // unknown would produce a green stage over an open question.
        if (facts.UnreadablePrivilegedGroupSids.Count > 0)
        {
            foreach (var sid in facts.UnreadablePrivilegedGroupSids)
            {
                reasons.Add($"The privileged group {WellKnownSecurityGroups.LabelOf(sid)} ({sid}) exists on "
                            + "this machine and could not be enumerated.");
            }

            reasons.Add("Whether the account is privileged is unknown, which is not the same as clean.");

            return Refuse(reasons);
        }

        if (!facts.Enabled)
        {
            // Explicit and deliberate: a disabled account is NOT enabled here.
            // It may be disabled because somebody disabled it on purpose, and
            // enabling an account is granting a sign-in, which is not a repair.
            reasons.Add("It is disabled. This script does not enable accounts: enabling one grants a "
                        + "sign-in, and it may have been disabled deliberately. Enable it yourself, "
                        + "then run this again.");

            return Refuse(reasons);
        }

        if (facts.InStandardUsersGroup is null)
        {
            reasons.Add($"Whether it is in the built-in Users group ({WellKnownSecurityGroups.UsersSid}) "
                        + "could not be determined, so nothing will be changed.");

            return Refuse(reasons);
        }

        if (facts.InStandardUsersGroup == false)
        {
            // THE WILMA STATE. Exactly repairable, and the only thing repaired.
            reasons.Add($"It is NOT in the built-in Users group ({WellKnownSecurityGroups.UsersSid}). "
                        + "This is state this script creates and failed to finish, so it will be added "
                        + "and nothing else will change.");

            return new ChildAccountSetupPlan
            {
                Action = ChildAccountAction.Repair,
                ResultIfActionSucceeds = ChildAccountResult.Success,
                AddStandardUsersMembership = true,

                // The account exists. Asking for a password would imply it is
                // being recreated, and a password typed here would do nothing.
                NeedsPassword = false,
                Reasons = reasons
            };
        }

        reasons.Add($"It is enabled, in the built-in Users group ({WellKnownSecurityGroups.UsersSid}), "
                    + "and in no privileged group. There is nothing to do.");

        return new ChildAccountSetupPlan
        {
            Action = ChildAccountAction.None,
            ResultIfActionSucceeds = ChildAccountResult.Success,
            AddStandardUsersMembership = false,
            NeedsPassword = false,
            Reasons = reasons
        };
    }

    /// <summary>
    /// How to report a run where the account exists but the Users membership
    /// could not be added.
    ///
    /// PARTIAL, never Success. This is the exact outcome WILMA got, and the
    /// exact outcome that was reported as "Created 'KidShellChild'."
    /// </summary>
    public static ChildAccountResult ResultOfFailedMembership(bool accountWasJustCreated) =>
        accountWasJustCreated ? ChildAccountResult.Partial : ChildAccountResult.Failed;

    /// <summary>
    /// The process exit code for a result.
    ///
    /// Distinct non-zero codes, because "refused on purpose" and "left the
    /// machine half configured" need different responses from an operator, and a
    /// bare 1 cannot tell them apart.
    /// </summary>
    public static int ExitCodeOf(ChildAccountResult result) => result switch
    {
        ChildAccountResult.Success => 0,
        ChildAccountResult.Partial => 4,
        ChildAccountResult.Refused => 1,
        ChildAccountResult.Failed => 5,
        _ => 5
    };

    private static ChildAccountSetupPlan Refuse(IReadOnlyList<string> reasons) => new()
    {
        Action = ChildAccountAction.Refuse,
        ResultIfActionSucceeds = ChildAccountResult.Refused,
        AddStandardUsersMembership = false,
        NeedsPassword = false,
        Reasons = reasons
    };

    private static string Describe(string? sid) =>
        string.IsNullOrWhiteSpace(sid) ? "<unreadable>" : sid.Trim();
}
