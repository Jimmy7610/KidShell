using KidShell.Core.Security.Validation;

namespace KidShell.Core.Security.AppControl;

/// <summary>
/// Security identifiers AppLocker rules are written against.
///
/// These were four separate SID literals across this assembly and the
/// integration layer. They are now one: <see cref="WellKnownSecurityGroups"/>.
/// Two copies of a security identity are two chances for one of them to be
/// wrong, and the copy nobody is looking at is the one that rots.
/// </summary>
public static class WellKnownSids
{
    /// <summary>Everyone. The audience when no account has been chosen yet.</summary>
    public static readonly string Everyone =
        WellKnownSecurityGroups.SidOf("Everyone")!;

    /// <summary>
    /// BUILTIN\Administrators.
    ///
    /// The recovery audience. Microsoft's own default rule set includes an
    /// "All files" rule scoped to this group in every collection, and it is
    /// what keeps an administrator able to repair a machine whose policy is
    /// wrong - including the machine where the repair tool is itself blocked.
    /// </summary>
    public static readonly string Administrators =
        WellKnownSecurityGroups.AdministratorsSid;
}

/// <summary>
/// The one answer to "may this policy be put on a machine".
///
/// OPSV FINDING 06A — there were two answers. AppControlPolicy.CanActivate
/// asked whether the policy carried blocking warnings, and the deployment side
/// asked a different question, so a policy could be reported as activatable and
/// then refused. Two validators is one validator too many: whichever is
/// laxer is the one that decides, and nobody knows which that is.
///
/// Every caller uses this. CanActivate delegates to it, the writer's enforcing
/// guard delegates to it, and the parent-facing report renders it.
/// </summary>
public sealed record AppControlValidation
{
    public required IReadOnlyList<PolicyWarning> Findings { get; init; }

    public IEnumerable<PolicyWarning> Blocking =>
        Findings.Where(f => f.Severity == PolicySeverity.Blocking);

    public IEnumerable<PolicyWarning> Advisory =>
        Findings.Where(f => f.Severity == PolicySeverity.Advisory);

    /// <summary>
    /// Whether this policy may be ENFORCED.
    ///
    /// The only question that matters for a machine. A policy that would not
    /// actually constrain the child is worse than no policy, because a parent
    /// reading "Säkert läge är på" would believe something untrue and act on
    /// it.
    /// </summary>
    public bool CanEnforce => !Blocking.Any();

    /// <summary>
    /// Whether an AUDIT-mode artifact may be produced.
    ///
    /// Always. Audit blocks nothing and writes what WOULD have been refused to
    /// the event log, which is exactly what somebody investigating a bad
    /// policy needs - refusing to generate it would remove the tool for
    /// diagnosing the very problem that blocked enforcement.
    ///
    /// The artifact says NOT SAFE TO ENFORCE on its face when CanEnforce is
    /// false, so it cannot be mistaken for an approved policy.
    /// </summary>
    public bool CanAudit => true;
}

/// <summary>
/// Reads a finished policy and decides whether it is worth applying.
///
/// Pure. It changes nothing, needs no machine, and is the same on any host.
/// </summary>
public static class AppControlValidator
{
    public static AppControlValidation Validate(AppControlPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // Warnings the builder already recorded - unresolved paths, an empty
        // app list - carried through rather than recomputed, so one policy has
        // one set of findings.
        var findings = new List<PolicyWarning>(policy.Warnings);

        RequireRecoveryPath(policy, findings);

        foreach (var rule in policy.Rules)
        {
            // A recovery rule is deliberately broad and is scoped to
            // administrators. Judging it by the rules for the CHILD's policy
            // would block every policy that has a recovery path, which is
            // every policy that should exist.
            if (rule.IsRecoveryRule)
            {
                continue;
            }

            InspectChildRule(rule, findings);
        }

        return new AppControlValidation { Findings = findings };
    }

    /// <summary>
    /// OPSV FINDING 06B — the policy must leave an adult a way back in.
    ///
    /// Microsoft's default rule set includes an "All files" rule for
    /// BUILTIN\Administrators in every collection, and the reason is not
    /// convenience. An application-control policy is the one change that can
    /// make a machine unable to run the tool that would undo it. Without a
    /// recovery path, a wrong rule set means reinstalling Windows.
    ///
    /// The answer is NOT to loosen the child's policy. The recovery rules are
    /// scoped to a different principal entirely, so the child's restriction is
    /// unchanged by their presence.
    /// </summary>
    private static void RequireRecoveryPath(AppControlPolicy policy, List<PolicyWarning> findings)
    {
        var collections = policy.Rules
            .Where(r => !r.IsRecoveryRule)
            .Select(r => r.Collection)
            .Distinct();

        foreach (var collection in collections)
        {
            var hasRecovery = policy.Rules.Any(r =>
                r.IsRecoveryRule &&
                r.Collection == collection &&
                string.Equals(r.UserOrGroupSid, WellKnownSids.Administrators, StringComparison.OrdinalIgnoreCase));

            if (!hasRecovery)
            {
                findings.Add(new PolicyWarning(
                    "missing-administrator-recovery-rule",
                    $"Policyn saknar en återställningsregel för administratörer i {collection}. " +
                    "Utan den kan en vuxen inte laga datorn om reglerna blir fel.",
                    PolicySeverity.Blocking));
            }
        }
    }

    private static void InspectChildRule(AppControlRule rule, List<PolicyWarning> findings)
    {
        // OPSV FINDING 06E. A path the child can write to is not a
        // restriction: they copy anything they like into it and run it. This
        // was an advisory, so a policy full of them reported CanActivate =
        // true - a lock whose key is left in it, described as locked.
        //
        // Microsoft make the same point about their own default rules: the
        // %WINDIR% path rule covers Windows\Temp, where the Users group may
        // create files, and they warn that allowing execution from there
        // "might conflict with your organization's security policy".
        //
        // Blocking applies to PATH rules. A publisher rule carries a signing
        // identity that a file copied into a writable folder does not inherit,
        // so where the file sits does not decide what may run.
        if (rule.Strategy == RuleStrategy.Path && rule.IsWeak)
        {
            findings.Add(new PolicyWarning(
                "child-writable-allow-path",
                $"Regeln för {rule.Name} pekar på \"{rule.Value}\", en mapp barnet kan skriva till. " +
                "Barnet kan lägga vilket program som helst där och starta det.",
                PolicySeverity.Blocking));
        }

        if (IsBlanketPath(rule))
        {
            findings.Add(new PolicyWarning(
                "blanket-path-rule",
                $"Regeln \"{rule.Value}\" släpper igenom allt i en hel systemmapp. " +
                "En sådan regel gör listan över tillåtna appar meningslös.",
                PolicySeverity.Blocking));
        }

        if (rule.Strategy == RuleStrategy.Publisher && rule.Value.Trim() == "*")
        {
            findings.Add(new PolicyWarning(
                "blanket-publisher-rule",
                "En utgivarregel som matchar alla utgivare tillåter varje paketerad app på datorn.",
                PolicySeverity.Blocking));
        }

        if (EscapeSurfaces.Matching(rule.Value) is { } surface)
        {
            findings.Add(new PolicyWarning(
                "escape-surface-allowed",
                $"Policyn skulle tillåta {surface.FileName}. {surface.Reason}",
                PolicySeverity.Blocking));
        }
    }

    /// <summary>
    /// Whether a path rule opens a whole system folder.
    ///
    /// A trailing \* under a system root allows every file there, now and in
    /// future. Acceptable for one application's own directory; never for
    /// Windows or System32.
    /// </summary>
    private static bool IsBlanketPath(AppControlRule rule)
    {
        if (rule.Strategy != RuleStrategy.Path)
        {
            return false;
        }

        var value = Runtime.WindowsPath.Canonical(rule.Value);

        if (!value.EndsWith(@"\*", StringComparison.Ordinal) && !value.EndsWith("*", StringComparison.Ordinal))
        {
            return false;
        }

        string[] systemRoots =
        [
            @"%WINDIR%", @"%SYSTEM32%", @"C:\WINDOWS", @"%PROGRAMFILES%", @"C:\PROGRAM FILES"
        ];

        var trimmed = value.TrimEnd('*').TrimEnd('\\');

        return systemRoots.Any(root =>
            string.Equals(trimmed, root.TrimEnd('\\'), StringComparison.Ordinal));
    }
}
