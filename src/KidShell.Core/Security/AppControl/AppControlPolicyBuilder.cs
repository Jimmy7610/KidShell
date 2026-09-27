using KidShell.Core.Apps;
using KidShell.Core.Configuration;

namespace KidShell.Core.Security.AppControl;

/// <summary>
/// Turns KidShell's configuration into an application-control policy.
///
/// Default-deny throughout: the output lists what is allowed and nothing else.
/// Three groups of rules, in descending order of how non-negotiable they are:
///
///  1. **Windows itself.** Without %WINDIR% the machine does not boot to a
///     desktop. A policy that omits this is not strict, it is broken.
///  2. **KidShell.** If the shell cannot start, the child gets nothing and the
///     parent has no way to fix it from inside the product.
///  3. **The child's apps**, plus whatever each app's profile says it needs -
///     a launcher's game process, an updater if the parent allowed it.
///
/// Path rules under a user-writable directory are flagged rather than dropped.
/// Dropping them would silently break an app a parent chose; emitting them
/// quietly would pretend to a protection that a child could defeat by copying
/// a file. Saying so is the only honest option.
/// </summary>
public static class AppControlPolicyBuilder
{
    /// <summary>Directories a standard user can write to, so a path rule there is weak.</summary>
    private static readonly string[] UserWritablePrefixes =
    [
        @"%OSDRIVE%\USERS\",
        @"C:\USERS\",
        @"%LOCALAPPDATA%",
        @"%APPDATA%",
        @"%TEMP%",
        @"%USERPROFILE%"
    ];

    public static AppControlPolicy Build(
        KidShellConfiguration configuration,
        IApplicationProfileLibrary profiles,
        string kidShellExecutablePath = "",
        string targetUserSid = "")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(profiles);

        var rules = new List<AppControlRule>();
        var warnings = new List<PolicyWarning>();

        AddSystemRules(rules, kidShellExecutablePath, warnings);
        AddApplicationRules(rules, configuration, profiles, warnings);

        var deduplicated = Deduplicate(rules);

        if (!deduplicated.Any(r => !r.IsSystemRequirement))
        {
            warnings.Add(new PolicyWarning(
                "no-application-rules",
                "Inga appar är påslagna, så barnet skulle bara kunna köra Windows och KidShell."));
        }

        foreach (var weak in deduplicated.Where(r => r.IsWeak))
        {
            warnings.Add(new PolicyWarning(
                "weak-path-rule",
                $"Regeln för {weak.Name} pekar på en mapp som barnet kan skriva till, " +
                "så den hindrar inte att andra program startas därifrån."));
        }

        AddSafetyWarnings(deduplicated, warnings);

        return new AppControlPolicy
        {
            Rules = deduplicated,
            Warnings = warnings,
            TargetUserSid = targetUserSid
        };
    }

    /// <summary>
    /// Windows and KidShell. Both are survival requirements rather than
    /// preferences: without the first the desktop does not work, and without
    /// the second the child has no shell and the parent no way back in.
    /// </summary>
    private static void AddSystemRules(List<AppControlRule> rules, string kidShellPath, List<PolicyWarning> warnings)
    {
        // One rule per Windows component, each with the reason it is here.
        //
        // This used to be %WINDIR%\* and %PROGRAMFILES%\*, which is the
        // starter policy from Microsoft's own wizard - and their guidance says
        // in terms that it is a starting point for testing, not a security
        // boundary, because %WINDIR%\Temp is writable by the Users group. A
        // child who can write a file into a folder the policy allows can run
        // anything they like, which is the whole allowlist gone.
        //
        // %PROGRAMFILES%\* was not even a Windows requirement. It allowed
        // every installed program on the machine: exactly the set the parent
        // was choosing between.
        foreach (var dependency in SystemDependencyManifest.Required)
        {
            rules.Add(new AppControlRule
            {
                Id = $"system-{dependency.FileName.Replace(".exe", string.Empty).ToLowerInvariant()}",
                Name = dependency.FileName,
                Collection = RuleCollection.Exe,
                Strategy = RuleStrategy.Path,
                Value = SystemDependencyManifest.PathFor(dependency),
                Reason = dependency.Reason,
                IsSystemRequirement = true
            });
        }

        if (!string.IsNullOrWhiteSpace(kidShellPath))
        {
            rules.Add(new AppControlRule
            {
                Id = "system-kidshell",
                Name = "KidShell",
                Collection = RuleCollection.Exe,
                Strategy = RuleStrategy.Path,
                Value = kidShellPath,
                Reason = "KidShell måste kunna starta.",
                IsSystemRequirement = true,
                IsWeak = IsUserWritable(kidShellPath)
            });
        }
        else
        {
            // A policy that cannot start the shell would lock the child out of
            // their own machine, which is the failure mode this whole design
            // exists to avoid.
            warnings.Add(new PolicyWarning(
                "kidshell-path-unknown",
                "KidShells egen sökväg är okänd, så policyn kan inte garantera att KidShell startar."));
        }

        // Packaged apps: the Windows shell components, by publisher, and
        // nothing else.
        //
        // This was a publisher rule of "*", which allows every packaged
        // application on the machine, signed by anybody. A parent choosing
        // four apps would have been handing over the Store, the browser and
        // every other packaged program installed - and packaged apps are
        // exactly where a modern Windows install keeps its browser.
        //
        // Microsoft's own shell pieces still have to run, so they are named by
        // their publisher. Approved packaged apps the parent picked are added
        // beside them by AddApplicationRules, individually.
        rules.Add(new AppControlRule
        {
            Id = "system-windows-packaged-shell",
            Name = "Windows-komponenter",
            Collection = RuleCollection.Appx,
            Strategy = RuleStrategy.Publisher,
            Value = WindowsComponentPublisher,
            ProductName = "Microsoft.Windows.ShellExperienceHost",
            Reason = "Startmenyn och Windows egna skal-delar måste kunna köras.",
            IsSystemRequirement = true
        });

        rules.Add(new AppControlRule
        {
            Id = "system-windows-packaged-start",
            Name = "Startmenyn",
            Collection = RuleCollection.Appx,
            Strategy = RuleStrategy.Publisher,
            Value = WindowsComponentPublisher,
            ProductName = "Microsoft.Windows.StartMenuExperienceHost",
            Reason = "Utan detta finns ingen startmeny i Standardläge.",
            IsSystemRequirement = true
        });
    }

    /// <summary>
    /// The signing identity Windows' own packaged components carry.
    ///
    /// Named rather than wildcarded: a publisher rule of "*" is not a
    /// publisher rule, it is an allow-everything rule wearing one.
    /// </summary>
    public const string WindowsComponentPublisher =
        "O=MICROSOFT CORPORATION, L=REDMOND, S=WASHINGTON, C=US";

    private static void AddApplicationRules(
        List<AppControlRule> rules,
        KidShellConfiguration configuration,
        IApplicationProfileLibrary profiles,
        List<PolicyWarning> warnings)
    {
        foreach (var app in configuration.EnabledApps)
        {
            var path = (app.ExecutablePath ?? string.Empty).Trim();

            if (path.Length == 0)
            {
                // An app with no program is a placeholder card, not something
                // to write a rule for.
                continue;
            }

            // A bare command name is resolved by Windows, not by us. Writing a
            // rule for "calc.exe" would be a rule for a path that does not
            // exist.
            //
            // Asked of WindowsPath rather than System.IO.Path: the latter
            // answers about the host, and on Linux a backslash is an ordinary
            // character, so the same configuration produced a different policy
            // depending on where the build ran.
            if (!WindowsPath.IsFullyQualified(path))
            {
                warnings.Add(new PolicyWarning(
                    "unresolved-path",
                    $"{app.EffectiveProgramName} är inte kopplad till en fullständig sökväg, " +
                    "så ingen regel kunde skapas."));
                continue;
            }

            rules.Add(new AppControlRule
            {
                Id = $"app-{app.Id}",
                Name = app.EffectiveProgramName,
                Collection = RuleCollection.Exe,
                Strategy = RuleStrategy.Path,
                Value = path,
                Reason = $"Barnet får använda {app.DisplayName}.",
                IsWeak = IsUserWritable(path)
            });

            // The profile knows what else the program needs. A launcher whose
            // game process is missing starts and then fails.
            var profile = profiles.Profiles.FirstOrDefault(p =>
                p.ExecutableNames.Any(n => string.Equals(
                    n, WindowsPath.FileName(path), StringComparison.OrdinalIgnoreCase)));

            if (profile is null)
            {
                continue;
            }

            foreach (var child in profile.ChildProcesses)
            {
                rules.Add(new AppControlRule
                {
                    Id = $"app-{app.Id}-child-{child.ToLowerInvariant()}",
                    Name = $"{app.EffectiveProgramName} ({child})",
                    Collection = RuleCollection.Exe,
                    Strategy = RuleStrategy.Path,
                    Value = child,
                    Reason = $"{app.EffectiveProgramName} startar {child}.",
                    IsWeak = false
                });
            }
        }
    }

    /// <summary>
    /// The last look over a finished policy, asking whether it is worth
    /// applying at all.
    ///
    /// These are blocking rather than advisory. A policy that does not
    /// constrain the child is worse than no policy: a parent reading "Säkert
    /// läge är på" would believe something untrue, and act on it.
    /// </summary>
    private static void AddSafetyWarnings(
        IReadOnlyList<AppControlRule> rules, List<PolicyWarning> warnings)
    {
        foreach (var rule in rules)
        {
            // A rule ending in \* allows every file in that folder, now and
            // in future. Acceptable for a single application's own directory
            // under Program Files; never acceptable for a Windows folder.
            if (IsBlanketRule(rule.Value))
            {
                warnings.Add(new PolicyWarning(
                    "blanket-path-rule",
                    $"Regeln \"{rule.Value}\" släpper igenom allt i en hel systemmapp. " +
                    "En sådan regel gör listan över tillåtna appar meningslös.",
                    PolicySeverity.Blocking));
            }

            if (rule.Strategy == RuleStrategy.Publisher && rule.Value.Trim() == "*")
            {
                warnings.Add(new PolicyWarning(
                    "blanket-publisher-rule",
                    "En utgivarregel som matchar alla utgivare tillåter varje paketerad app på datorn.",
                    PolicySeverity.Blocking));
            }

            if (EscapeSurfaces.Matching(rule.Value) is { } surface)
            {
                warnings.Add(new PolicyWarning(
                    "escape-surface-allowed",
                    $"Policyn skulle tillåta {surface.FileName}. {surface.Reason}",
                    PolicySeverity.Blocking));
            }
        }
    }

    /// <summary>
    /// Whether a value allows a whole system folder.
    ///
    /// A trailing wildcard on an application's own folder is ordinary and
    /// fine - that is how a program and its helpers are allowed together. The
    /// dangerous shape is a wildcard directly under a Windows or Program Files
    /// root, which is the shape Microsoft's own guidance warns about.
    /// </summary>
    internal static bool IsBlanketRule(string value)
    {
        var normalized = (value ?? string.Empty).Trim().Trim('"').ToUpperInvariant().Replace('/', '\\');

        if (!normalized.EndsWith('*'))
        {
            return false;
        }

        var withoutWildcard = normalized.TrimEnd('*').TrimEnd('\\');

        return withoutWildcard.Length == 0 ||
               withoutWildcard is "%WINDIR%" or "%SYSTEM32%" or "%PROGRAMFILES%" or "%OSDRIVE%" ||
               withoutWildcard is "C:" or "D:";
    }

    /// <summary>
    /// Whether a path lives somewhere a standard user can write, which makes a
    /// path rule there decorative.
    /// </summary>
    public static bool IsUserWritable(string path)
    {
        var upper = WindowsPath.Canonical(path);

        return UserWritablePrefixes.Any(prefix => upper.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Removes duplicates. The same executable can be reached from two
    /// configured apps, and emitting it twice produces a policy Windows
    /// rejects.
    /// </summary>
    private static IReadOnlyList<AppControlRule> Deduplicate(IEnumerable<AppControlRule> rules)
    {
        var seen = new Dictionary<string, AppControlRule>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            var key = $"{rule.Collection}|{rule.Strategy}|{WindowsPath.Canonical(rule.Value)}|{rule.ProductName}";

            if (!seen.ContainsKey(key))
            {
                seen[key] = rule;
            }
        }

        return [.. seen.Values];
    }

}
