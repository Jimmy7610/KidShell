namespace KidShell.Core.Security.Validation;

/// <summary>
/// One built-in Windows group, named by SID.
/// </summary>
/// <param name="Label">
/// The canonical English name, used in evidence and in reports so two runs on
/// differently-localized machines produce comparable documents. It is a LABEL.
/// Nothing resolves a group with it.
/// </param>
/// <param name="Sid">
/// What the group actually is. Windows' built-in groups have fixed SIDs and
/// localized display names, so this is the only part that means anything.
/// </param>
/// <param name="Privileged">
/// Whether membership of this group would make the child's account more than a
/// standard user.
/// </param>
/// <param name="OrdinaryAccount">
/// Whether this principal represents "anybody with an account on this machine".
/// Used when asking whether an ordinary account can write somewhere it must not.
/// </param>
public sealed record WellKnownSecurityGroup(
    string Label, string Sid, bool Privileged = false, bool OrdinaryAccount = false);

/// <summary>
/// The built-in Windows groups this product makes decisions about, by SID.
///
/// THE DEFECT THIS TYPE EXISTS TO END
/// ----------------------------------
/// Windows localizes the display names of its built-in groups. On the Swedish
/// machine used for physical validation, Administrators is "Administratörer"
/// and Users is "Användare" - and `Get-LocalGroup -Name 'Administrators'`
/// throws GroupNotFoundException. Measured, not assumed: every English name in
/// the validation toolset failed on that machine.
///
/// Three scripts asked Windows for groups by English name. Two would have
/// failed loudly during account validation, which is bad enough. The third was
/// worse: the install verifier decided whether an ordinary account could write
/// the install root by matching the access list's identity strings against
/// "Users|Everyone|Authenticated", and on a localized machine nothing matches -
/// so a check whose entire job is to catch a privilege escalation reported
/// "no ordinary account has write access" whatever the truth was. A security
/// check that fails OPEN.
///
/// So: the SID is the identity, the label is for reading, and no authorization
/// decision anywhere may be made from a display name.
///
/// NOT EVERY GROUP EXISTS EVERYWHERE
/// ---------------------------------
/// On the Windows Home machine above, only four of the seven privileged groups
/// exist at all - Power Users, Backup Operators and Remote Desktop Users are
/// absent. An absent group is not a failure and not a reason to stop: it is a
/// group the child cannot be in. Callers must tolerate it.
/// </summary>
public static class WellKnownSecurityGroups
{
    /// <summary>The local Administrators group. The parent's recovery path.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>The local Users group. Where a standard account belongs.</summary>
    public const string UsersSid = "S-1-5-32-545";

    /// <summary>LocalSystem.</summary>
    public const string SystemSid = "S-1-5-18";

    /// <summary>
    /// Every group and principal the toolset reasons about.
    ///
    /// Each SID is a fixed, documented Windows value. They do not vary by
    /// language, edition or installation.
    /// </summary>
    public static readonly IReadOnlyList<WellKnownSecurityGroup> All =
    [
        // -------------------------------------------------- privileged groups
        new("Administrators", AdministratorsSid, Privileged: true),
        new("Power Users", "S-1-5-32-547", Privileged: true),
        new("Backup Operators", "S-1-5-32-551", Privileged: true),
        new("Remote Desktop Users", "S-1-5-32-555", Privileged: true),
        new("Remote Management Users", "S-1-5-32-580", Privileged: true),
        new("Hyper-V Administrators", "S-1-5-32-578", Privileged: true),

        // Not a classic "admin" group, and it can replace a file in Program
        // Files, which is the thing that matters here.
        new("Device Owners", "S-1-5-32-583", Privileged: true),

        // ---------------------------------------------------- ordinary access
        //
        // "Anybody with an account on this machine". A write grant to any of
        // these on the install root or the protected store is the escalation the
        // verifier is looking for.
        new("Users", UsersSid, OrdinaryAccount: true),
        new("Everyone", "S-1-1-0", OrdinaryAccount: true),
        new("Authenticated Users", "S-1-5-11", OrdinaryAccount: true),
        new("Interactive", "S-1-5-4", OrdinaryAccount: true),

        // ---------------------------------------------------------- the system
        new("System", SystemSid),
        new("Guests", "S-1-5-32-546")
    ];

    /// <summary>The groups whose membership would make an account more than standard.</summary>
    public static IReadOnlyList<WellKnownSecurityGroup> Privileged =>
        [.. All.Where(g => g.Privileged)];

    /// <summary>The principals that mean "any account on this machine".</summary>
    public static IReadOnlyList<WellKnownSecurityGroup> OrdinaryAccounts =>
        [.. All.Where(g => g.OrdinaryAccount)];

    /// <summary>The canonical label for a SID, or the SID itself when unknown.</summary>
    public static string LabelOf(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return string.Empty;
        }

        return All.FirstOrDefault(
            g => string.Equals(g.Sid, sid.Trim(), StringComparison.OrdinalIgnoreCase))?.Label
            ?? sid.Trim();
    }

    /// <summary>
    /// The SID for a canonical label, or null.
    ///
    /// Matched against the canonical English label only, and deliberately so:
    /// this is for reading a configuration or an evidence file this product
    /// wrote, never for interpreting a name Windows displayed.
    /// </summary>
    public static string? SidOf(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        return All.FirstOrDefault(
            g => string.Equals(g.Label, label.Trim(), StringComparison.OrdinalIgnoreCase))?.Sid;
    }

    /// <summary>Whether this SID is one of the privileged groups.</summary>
    public static bool IsPrivileged(string? sid) =>
        Privileged.Any(g => string.Equals(g.Sid, (sid ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether this SID means "any account on this machine".</summary>
    public static bool IsOrdinaryAccount(string? sid) =>
        OrdinaryAccounts.Any(g => string.Equals(g.Sid, (sid ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a string is a SID rather than a name.
    ///
    /// Used to refuse a value that was supposed to be a SID and is a display
    /// name, which is how a localized name reaches a comparison in the first
    /// place.
    /// </summary>
    public static bool LooksLikeSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = value.Split('-');

        return parts.Length is >= 3 and <= 15 &&
               parts.Skip(1).All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }
}
