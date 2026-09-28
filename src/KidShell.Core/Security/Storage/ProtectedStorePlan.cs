namespace KidShell.Core.Security.Storage;

/// <summary>Who a permission entry is about.</summary>
public enum ProtectedStorePrincipal
{
    /// <summary>The local Administrators group. The parent, elevated.</summary>
    Administrators = 0,

    /// <summary>The operating system.</summary>
    System = 1,

    /// <summary>The child's own account.</summary>
    Child = 2,

    /// <summary>Everybody else with an account on this machine.</summary>
    Users = 3
}

/// <summary>What a principal may do.</summary>
[Flags]
public enum ProtectedStoreRights
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    ChangePermissions = 8,

    ReadOnly = Read,
    FullControl = Read | Write | Delete | ChangePermissions
}

/// <summary>One access-control entry in the plan.</summary>
public sealed record ProtectedStoreAce(ProtectedStorePrincipal Principal, ProtectedStoreRights Rights);

/// <summary>
/// Where the protected store goes and who may touch it.
///
/// A PLAN, not an action. Nothing in this type changes a machine: it produces
/// a description that an elevated security operation would carry out on a
/// dedicated device, and that the tests can check without touching anything.
/// That separation is the same one the rest of KidShell's security work uses -
/// deciding and doing are different things, and only one of them needs a real
/// computer.
/// </summary>
public sealed record ProtectedStorePlan
{
    /// <summary>
    /// %ProgramData%\KidShell\policy.
    ///
    /// Machine-wide rather than per-user, because the point is that it does
    /// NOT belong to the signed-in child. ProgramData's default permissions
    /// let any user create files but not modify other users' - so the plan
    /// replaces inheritance rather than relying on the default.
    /// </summary>
    public required string Directory { get; init; }

    public required IReadOnlyList<ProtectedStoreAce> Entries { get; init; }

    /// <summary>
    /// Whether inherited permissions are removed first.
    ///
    /// They must be. ProgramData grants CREATOR OWNER full control of what it
    /// creates, so a store written without breaking inheritance would be owned
    /// and fully controlled by whoever wrote it - which, on a child's machine,
    /// could be the child.
    /// </summary>
    public bool RemoveInheritance { get; init; } = true;

    /// <summary>
    /// The plan for a machine whose child account is known.
    /// </summary>
    public static ProtectedStorePlan For(string programDataPath) => new()
    {
        Directory = Combine(programDataPath, "KidShell", "policy"),
        Entries =
        [
            // The parent, elevated. The only principal that may change policy.
            new(ProtectedStorePrincipal.Administrators, ProtectedStoreRights.FullControl),

            // The operating system, so backup, servicing and the elevated
            // helper all work.
            new(ProtectedStorePrincipal.System, ProtectedStoreRights.FullControl),

            // The child: read, and only read.
            //
            // Read rather than nothing, because KidShell runs AS the child and
            // has to load the policy it is enforcing. Read is not a weakness
            // here: the PIN is a salted hash and everything else is a list of
            // the rules the child is already being shown.
            new(ProtectedStorePrincipal.Child, ProtectedStoreRights.ReadOnly)

            // No entry for Users. Anyone else's account has no business
            // reading one family member's policy, and silence is denial.
        ]
    };

    /// <summary>Whether this plan actually keeps the child out.</summary>
    public bool IsChildWriteProtected =>
        !Entries.Any(e => e.Principal is ProtectedStorePrincipal.Child or ProtectedStorePrincipal.Users
                          && (e.Rights & (ProtectedStoreRights.Write
                                          | ProtectedStoreRights.Delete
                                          | ProtectedStoreRights.ChangePermissions)) != 0);

    /// <summary>Whether the parent can still get in to fix things.</summary>
    public bool IsAdministratorRecoverable =>
        Entries.Any(e => e.Principal == ProtectedStorePrincipal.Administrators
                         && e.Rights.HasFlag(ProtectedStoreRights.Write));

    /// <summary>Whether KidShell, running as the child, can read its own policy.</summary>
    public bool IsChildReadable =>
        Entries.Any(e => e.Principal == ProtectedStorePrincipal.Child
                         && e.Rights.HasFlag(ProtectedStoreRights.Read));

    /// <summary>
    /// Path building without System.IO.Path, so the plan is the same whatever
    /// host computes it. The separator is Windows' because the target is.
    /// </summary>
    private static string Combine(string root, params string[] parts) =>
        string.Join('\\', new[] { root.TrimEnd('\\', '/') }.Concat(parts));
}
