using System.Text.Json;
using KidShell.Core.Configuration;

namespace KidShell.Core.Web;

/// <summary>A single browser policy value KidShell would set.</summary>
public sealed record BrowserPolicySetting
{
    public required string Name { get; init; }

    /// <summary>Serialised value, as the policy would carry it.</summary>
    public required string Value { get; init; }

    /// <summary>Why this exists, in Swedish, for the dry-run preview.</summary>
    public required string Reason { get; init; }

    /// <summary>The documented registry location, for the preview only.</summary>
    public string PolicyPath { get; init; } = @"HKLM\SOFTWARE\Policies\Microsoft\Edge";
}

/// <summary>A complete browser policy, as an artifact.</summary>
public sealed record BrowserPolicy
{
    public required WebMode Mode { get; init; }

    public required IReadOnlyList<BrowserPolicySetting> Settings { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Always false in this milestone. There is no code that writes these
    /// values, and a test asserts the generator has no such method.
    /// </summary>
    public bool WasApplied => false;
}

/// <summary>
/// Generates Microsoft Edge policy from KidShell's web settings.
///
/// ARTIFACT ONLY. This produces a description of policy values. It does not
/// write the registry, does not touch HKLM\SOFTWARE\Policies, and has no
/// method that could. Applying browser policy is a machine change and belongs
/// behind the transaction boundary like any other.
///
/// The policy names used are the documented Microsoft Edge ones:
/// URLAllowlist, URLBlocklist, and the download and popup controls. No
/// undocumented keys.
/// </summary>
public static class BrowserPolicyGenerator
{
    /// <summary>Blocks everything, so the allowlist can then carve out exceptions.</summary>
    private const string BlockEverything = "*";

    public static BrowserPolicy Generate(WebSettings settings, IReadOnlyList<AllowlistEntry>? allowlist = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var entries = allowlist ?? [];
        var policies = new List<BrowserPolicySetting>();
        var warnings = new List<string>();

        switch (settings.Mode)
        {
            case WebMode.NoBrowser:
                // Nothing to configure: with no browser allowed, the app list
                // is what keeps one from starting. Saying so beats emitting a
                // policy that implies more.
                warnings.Add(
                    "Ingen webbläsare är tillåten, så ingen webbläsarpolicy behövs. " +
                    "Det som hindrar en webbläsare från att starta är applistan.");
                break;

            case WebMode.Allowlist:
                policies.Add(new BrowserPolicySetting
                {
                    Name = "URLBlocklist",
                    Value = JsonSerializer.Serialize(new[] { BlockEverything }),
                    Reason = "Blockera allt som standard."
                });

                if (entries.Count == 0)
                {
                    // A blocklist of "*" with nothing allowed is a browser that
                    // opens nothing. Valid, but never what a parent intended.
                    warnings.Add(
                        "Inga godkända sidor är tillagda, så webbläsaren skulle inte kunna öppna någonting.");
                }
                else
                {
                    policies.Add(new BrowserPolicySetting
                    {
                        Name = "URLAllowlist",
                        Value = JsonSerializer.Serialize(entries.Select(e => e.ToPolicyPattern()).ToArray()),
                        Reason = $"Tillåt {entries.Count} godkända sidor."
                    });
                }

                policies.Add(new BrowserPolicySetting
                {
                    Name = "DownloadRestrictions",

                    // 3 = block all downloads, per the documented enumeration.
                    Value = "3",
                    Reason = "Hindra nedladdningar, som annars är en väg till andra program."
                });

                policies.Add(new BrowserPolicySetting
                {
                    Name = "DefaultPopupsSetting",

                    // 2 = do not allow any site to show popups.
                    Value = "2",
                    Reason = "Hindra popup-fönster."
                });

                policies.Add(new BrowserPolicySetting
                {
                    Name = "InPrivateModeAvailability",

                    // 1 = InPrivate disabled.
                    Value = "1",
                    Reason = "Stäng av InPrivate, som annars kringgår historiken."
                });
                break;

            case WebMode.Open:
                warnings.Add(
                    "Friare webb innebär att barnet når hela internet. " +
                    "KidShell begränsar ingenting i det läget.");
                break;
        }

        return new BrowserPolicy
        {
            Mode = settings.Mode,
            Settings = policies,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Renders the policy as a .reg file for review.
    ///
    /// A preview artifact: it is shown to a parent and can be saved, and
    /// KidShell never executes it. Producing something a human can read and
    /// check beats applying something they cannot.
    /// </summary>
    public static string ToRegistryPreview(BrowserPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var builder = new System.Text.StringBuilder();

        builder.AppendLine("Windows Registry Editor Version 5.00");
        builder.AppendLine();
        builder.AppendLine("; KidShell - FÖRHANDSGRANSKNING. Ingenting av detta har tillämpats.");
        builder.AppendLine($"; Läge: {policy.Mode}");
        builder.AppendLine();

        if (policy.Settings.Count == 0)
        {
            builder.AppendLine("; Inga policyvärden behövs för det här läget.");
            return builder.ToString();
        }

        builder.AppendLine(@"[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge]");

        foreach (var setting in policy.Settings)
        {
            builder.AppendLine($"; {setting.Reason}");

            // A JSON array becomes a Edge list policy, which lives in a subkey
            // with numbered values rather than a single value.
            if (setting.Value.StartsWith('['))
            {
                builder.AppendLine($"; {setting.Name} är en listpolicy: se undernyckeln nedan.");
            }
            else
            {
                builder.AppendLine($"\"{setting.Name}\"=dword:{int.Parse(setting.Value):x8}");
            }
        }

        foreach (var setting in policy.Settings.Where(s => s.Value.StartsWith('[')))
        {
            builder.AppendLine();
            builder.AppendLine($@"[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge\{setting.Name}]");

            var items = JsonSerializer.Deserialize<string[]>(setting.Value) ?? [];

            for (var i = 0; i < items.Length; i++)
            {
                builder.AppendLine($"\"{i + 1}\"=\"{items[i]}\"");
            }
        }

        return builder.ToString();
    }
}
