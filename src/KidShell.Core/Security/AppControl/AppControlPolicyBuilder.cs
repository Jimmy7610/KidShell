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
        rules.Add(new AppControlRule
        {
            Id = "system-windows",
            Name = "Windows",
            Collection = RuleCollection.Exe,
            Strategy = RuleStrategy.Path,
            Value = @"%WINDIR%\*",
            Reason = "Windows måste kunna starta.",
            IsSystemRequirement = true
        });

        rules.Add(new AppControlRule
        {
            Id = "system-program-files",
            Name = "Installerade program",
            Collection = RuleCollection.Exe,
            Strategy = RuleStrategy.Path,
            Value = @"%PROGRAMFILES%\*",
            Reason = "Program installerade av en administratör.",
            IsSystemRequirement = true
        });

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

        // Packaged apps are a separate collection; without this, no Store app
        // runs at all, including Calculator and Paint.
        rules.Add(new AppControlRule
        {
            Id = "system-signed-packaged",
            Name = "Signerade Microsoft Store-appar",
            Collection = RuleCollection.Appx,
            Strategy = RuleStrategy.Publisher,
            Value = "*",
            Reason = "Paketerade appar måste kunna starta.",
            IsSystemRequirement = true
        });
    }

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
            if (!Path.IsPathRooted(path))
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
                p.ExecutableNames.Any(n => string.Equals(n, SafeFileName(path), StringComparison.OrdinalIgnoreCase)));

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
    /// Whether a path lives somewhere a standard user can write, which makes a
    /// path rule there decorative.
    /// </summary>
    public static bool IsUserWritable(string path)
    {
        var upper = (path ?? string.Empty).Trim().Trim('"').ToUpperInvariant();

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
            var key = $"{rule.Collection}|{rule.Strategy}|{rule.Value.Trim().ToUpperInvariant()}";

            if (!seen.ContainsKey(key))
            {
                seen[key] = rule;
            }
        }

        return [.. seen.Values];
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path.Trim().Trim('"'));
        }
        catch
        {
            return string.Empty;
        }
    }
}
