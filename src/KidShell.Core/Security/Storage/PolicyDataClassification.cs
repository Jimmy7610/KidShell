namespace KidShell.Core.Security.Storage;

/// <summary>
/// What kind of data a piece of KidShell's state is, and therefore where it
/// has to live.
/// </summary>
public enum PolicyDataClass
{
    /// <summary>
    /// The child's own choices: name, avatar, theme.
    ///
    /// Not security state. A child who edits their own avatar has changed
    /// their avatar, and protecting it would cost complexity for nothing.
    /// </summary>
    ChildPersonalisation = 0,

    /// <summary>
    /// What the parent decided: which apps, which websites, how much time,
    /// which security mode.
    ///
    /// A child who can edit this can give themselves anything. It is the
    /// product.
    /// </summary>
    ParentPolicy = 1,

    /// <summary>
    /// How much of today has been used, and what a recovery would restore.
    ///
    /// Separate from policy because it changes constantly and policy does not.
    /// A child who can delete this file gets an unlimited day.
    /// </summary>
    EnforcementState = 2,

    /// <summary>
    /// The parent PIN material.
    ///
    /// A hash and a salt rather than a PIN, so reading it is not immediately
    /// the same as knowing the code - but a six-digit PIN has a million
    /// candidates, which a laptop exhausts in seconds. Read access is
    /// effectively the PIN.
    /// </summary>
    Secret = 3
}

/// <summary>One item of KidShell state, classified.</summary>
/// <param name="Name">What it is, in the configuration document.</param>
/// <param name="Class">Which trust class it belongs to.</param>
/// <param name="Consequence">What a child who could write it would gain.</param>
public sealed record PolicyDataItem(string Name, PolicyDataClass Class, string Consequence);

/// <summary>
/// EXTERNAL AUDIT FINDING 03 — the trust boundary, written down.
///
/// THE PROBLEM
/// -----------
/// Everything KidShell knows lives in one JSON file in the signed-in user's
/// own LocalState. In the shipped architecture that user is the CHILD, and a
/// standard user has full control of their own profile. So today the child can
/// open the file that says which apps they may use, how long they may use
/// them, and what the parent's PIN hashes to - and edit it.
///
/// No amount of checksumming fixes that. An HMAC whose key sits beside the
/// file, readable by the same account, is a speed bump with a ceremony
/// attached: whoever can edit the data can recompute the tag. Integrity
/// without a boundary the attacker cannot cross is theatre, and shipping
/// theatre is worse than shipping nothing, because a parent would believe it.
///
/// THE BOUNDARY
/// ------------
/// Windows already has the mechanism: file system ACLs, and an account that is
/// not an administrator. The child runs KidShell unelevated; the authoritative
/// parent state lives somewhere the child's account can READ and not WRITE.
/// Writing it requires the parent, who is an administrator, and goes through
/// the elevated helper that already exists for security operations.
///
/// That is the whole design. It is not clever, and it does not need to be: it
/// is the same boundary Windows uses for Program Files.
///
/// WHAT MOVES AND WHAT DOES NOT
/// ----------------------------
/// Only what has to. Protecting the child's chosen avatar would mean a UAC
/// prompt to change a picture, which is how a product teaches people to click
/// through UAC prompts. The table below is the decision, item by item, and
/// <see cref="ProtectedStorePlan"/> turns it into paths and permissions.
/// </summary>
public static class PolicyDataClassification
{
    public static readonly IReadOnlyList<PolicyDataItem> Items =
    [
        // ------------------------------------------------ stays with the child
        new("child.name", PolicyDataClass.ChildPersonalisation,
            "Their own name on their own screen."),
        new("child.avatarId", PolicyDataClass.ChildPersonalisation,
            "A different picture."),
        new("child.themeId", PolicyDataClass.ChildPersonalisation,
            "A different background."),
        new("child.age", PolicyDataClass.ChildPersonalisation,
            "Nothing on its own; the age tunes the presentation, not the rules."),

        // ------------------------------------------------ the parent's decisions
        new("apps", PolicyDataClass.ParentPolicy,
            "Add any program on the machine to their own allowed list."),
        new("web.mode", PolicyDataClass.ParentPolicy,
            "Turn the web filter off."),
        new("web.allowedDomains", PolicyDataClass.ParentPolicy,
            "Allow any site."),
        new("screenTime.isEnabled", PolicyDataClass.ParentPolicy,
            "Turn the time limit off."),
        new("screenTime.weekdayMinutes", PolicyDataClass.ParentPolicy,
            "Give themselves a longer day."),
        new("screenTime.weekendMinutes", PolicyDataClass.ParentPolicy,
            "Give themselves a longer weekend."),
        new("screenTime.restrictHours", PolicyDataClass.ParentPolicy,
            "Use the computer at three in the morning."),
        new("securityMode", PolicyDataClass.ParentPolicy,
            "Turn the lockdown off."),

        // ------------------------------------------------ enforcement
        new("screenTimeState.usedSeconds", PolicyDataClass.EnforcementState,
            "Reset today's counter and carry on."),
        new("screenTimeState.localDate", PolicyDataClass.EnforcementState,
            "Convince KidShell it is a new day."),
        new("screenTimeState.bonusMinutes", PolicyDataClass.EnforcementState,
            "Grant themselves extra time the parent did not."),
        new("recovery.manifests", PolicyDataClass.EnforcementState,
            "Destroy the record of how to undo a security change."),

        // ------------------------------------------------ secrets
        new("parentPin.hash", PolicyDataClass.Secret,
            "Replace the PIN with one they know, or crack it offline - six digits is a million guesses."),
        new("parentPin.salt", PolicyDataClass.Secret,
            "Makes the hash crackable in the usual way."),
    ];

    /// <summary>Everything that must be out of the child's reach.</summary>
    public static IEnumerable<PolicyDataItem> Protected =>
        Items.Where(i => i.Class != PolicyDataClass.ChildPersonalisation);

    /// <summary>Everything that may stay in the child's own profile.</summary>
    public static IEnumerable<PolicyDataItem> ChildWritable =>
        Items.Where(i => i.Class == PolicyDataClass.ChildPersonalisation);
}
