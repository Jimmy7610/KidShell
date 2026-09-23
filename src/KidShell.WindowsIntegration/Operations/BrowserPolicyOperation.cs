using System.Text.Json;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using KidShell.Core.Web;
using KidShell.WindowsIntegration.Platform;

namespace KidShell.WindowsIntegration.Operations;

/// <summary>One captured policy value, so rollback can put it back exactly.</summary>
internal sealed record CapturedPolicyValue
{
    public required string Name { get; init; }

    public string? PreviousValue { get; init; }

    public bool ExistedBefore { get; init; }
}

/// <summary>
/// Writes Microsoft Edge policy for the child.
///
/// WHAT THIS CAN AND CANNOT DO
/// ---------------------------
/// Edge policy is machine-wide: <c>HKLM\SOFTWARE\Policies\Microsoft\Edge</c>
/// applies to every account, including the parent's. KidShell therefore writes
/// the allowlist keys only when the parent has understood that, and the Säkerhet
/// page says so rather than implying a per-child browser policy exists.
///
/// It also does not make the web safe. It configures ONE browser. A different
/// browser, an in-app web view, a game's built-in store page and a help link
/// that opens a URL are all outside it - which is why the honest protection is
/// application control deciding what may run at all, with browser policy as a
/// second layer rather than the first.
///
/// Policy names are the documented Edge ones (URLAllowlist, URLBlocklist,
/// DownloadRestrictions, …). No undocumented keys.
/// See: https://learn.microsoft.com/deployedge/microsoft-edge-policies
/// </summary>
public sealed class BrowserPolicyOperation : SecurityOperationBase
{
    /// <summary>The documented Edge policy key.</summary>
    internal const string EdgePolicyKey = @"SOFTWARE\Policies\Microsoft\Edge";

    /// <summary>List policies are written as numbered subkey values.</summary>
    internal static readonly string[] ListPolicies = ["URLAllowlist", "URLBlocklist"];

    private readonly IRegistryStore _registry;
    private readonly BrowserPolicy _policy;

    private readonly List<CapturedPolicyValue> _captured = [];

    public BrowserPolicyOperation(IRegistryStore registry, BrowserPolicy policy, IKidShellLogger logger)
        : base(logger)
    {
        _registry = registry;
        _policy = policy;
    }

    public override string Id => "browser-policy";

    public override string Description => "Ställ in webbläsarregler för Microsoft Edge";

    public override ChangeRiskLevel RiskLevel => ChangeRiskLevel.Medium;

    public override RequiredCapability CapabilityRequired => RequiredCapability.None;

    protected override Task<OperationOutcome> DoPreflightAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        if (_policy.Settings.Count == 0)
        {
            return Task.FromResult(OperationOutcome.Fail(
                "Det finns inga webbläsarregler att skriva för det valda webbläget."));
        }

        foreach (var setting in _policy.Settings)
        {
            if (!IsKnownPolicy(setting.Name))
            {
                // A policy name KidShell does not recognise is not written. The
                // allowed set is a fixed list in code, not whatever reached
                // here from a settings file.
                return Task.FromResult(OperationOutcome.Fail(
                    $"Okänd webbläsarregel \"{setting.Name}\". KidShell skriver bara dokumenterade Edge-regler.",
                    setting.Name));
            }
        }

        return Task.FromResult(OperationOutcome.Ok(
            $"{_policy.Settings.Count} webbläsarregler kan skrivas."));
    }

    /// <summary>
    /// The complete set of Edge policies KidShell will ever write. A fixed
    /// list, because "whatever the generator produced" is not a security
    /// boundary.
    /// </summary>
    internal static bool IsKnownPolicy(string name) => name is
        "URLAllowlist" or "URLBlocklist" or "DownloadRestrictions" or
        "DefaultPopupsSetting" or "InPrivateModeAvailability" or
        "BrowserSignin" or "SyncDisabled" or "DefaultGeolocationSetting" or
        "AllowDeletingBrowserHistory" or "EditFavoritesEnabled";

    protected override async Task<OperationSnapshot> DoCaptureStateAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        _captured.Clear();

        foreach (var setting in _policy.Settings)
        {
            if (IsListPolicy(setting.Name))
            {
                // List policies live in a subkey with numbered values. Capture
                // the whole subkey's existence; rollback removes it or leaves
                // it alone.
                var exists = await _registry
                    .KeyExistsAsync(RegistryScope.LocalMachine, null, $"{EdgePolicyKey}\\{setting.Name}", cancellationToken)
                    .ConfigureAwait(false);

                _captured.Add(new CapturedPolicyValue { Name = setting.Name, ExistedBefore = exists });
                continue;
            }

            var previous = await _registry
                .ReadDWordAsync(RegistryScope.LocalMachine, null, EdgePolicyKey, setting.Name, cancellationToken)
                .ConfigureAwait(false);

            _captured.Add(new CapturedPolicyValue
            {
                Name = setting.Name,
                PreviousValue = previous?.ToString(),
                ExistedBefore = previous is not null
            });
        }

        var anyExisted = _captured.Any(c => c.ExistedBefore);

        return new OperationSnapshot
        {
            OperationId = Id,
            Description = anyExisted
                ? "Datorn hade redan några Edge-regler inställda."
                : "Datorn hade inga Edge-regler inställda.",
            PreviousValue = JsonSerializer.Serialize(_captured),
            ExistedBefore = anyExisted
        };
    }

    internal static bool IsListPolicy(string name) => ListPolicies.Contains(name, StringComparer.Ordinal);

    protected override async Task<OperationOutcome> DoApplyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        foreach (var setting in _policy.Settings)
        {
            if (IsListPolicy(setting.Name))
            {
                await WriteListAsync(setting.Name, setting.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Non-list Edge policies are DWORDs.
            if (int.TryParse(setting.Value, out var number))
            {
                await _registry
                    .WriteDWordAsync(RegistryScope.LocalMachine, null, EdgePolicyKey, setting.Name, number, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _registry
                    .WriteStringAsync(RegistryScope.LocalMachine, null, EdgePolicyKey, setting.Name, setting.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return OperationOutcome.Ok($"{_policy.Settings.Count} webbläsarregler skrevs.");
    }

    private async Task WriteListAsync(string policyName, string jsonArray, CancellationToken cancellationToken)
    {
        var entries = JsonSerializer.Deserialize<string[]>(jsonArray) ?? [];
        var subKey = $"{EdgePolicyKey}\\{policyName}";

        // Numbered from 1, as Edge documents for list policies.
        for (var i = 0; i < entries.Length; i++)
        {
            await _registry
                .WriteStringAsync(RegistryScope.LocalMachine, null, subKey, (i + 1).ToString(), entries[i], cancellationToken)
                .ConfigureAwait(false);
        }
    }

    protected override async Task<OperationOutcome> DoVerifyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        foreach (var setting in _policy.Settings)
        {
            if (IsListPolicy(setting.Name))
            {
                var expected = JsonSerializer.Deserialize<string[]>(setting.Value) ?? [];
                var subKey = $"{EdgePolicyKey}\\{setting.Name}";

                for (var i = 0; i < expected.Length; i++)
                {
                    var actual = await _registry
                        .ReadStringAsync(RegistryScope.LocalMachine, null, subKey, (i + 1).ToString(), cancellationToken)
                        .ConfigureAwait(false);

                    if (!string.Equals(actual, expected[i], StringComparison.Ordinal))
                    {
                        return OperationOutcome.Fail(
                            $"Webbläsarregeln \"{setting.Name}\" stämmer inte efter skrivning.",
                            $"{subKey}\\{i + 1}: expected '{expected[i]}', found '{actual}'");
                    }
                }

                continue;
            }

            if (int.TryParse(setting.Value, out var number))
            {
                var actual = await _registry
                    .ReadDWordAsync(RegistryScope.LocalMachine, null, EdgePolicyKey, setting.Name, cancellationToken)
                    .ConfigureAwait(false);

                if (actual != number)
                {
                    return OperationOutcome.Fail(
                        $"Webbläsarregeln \"{setting.Name}\" stämmer inte efter skrivning.",
                        $"expected {number}, found {actual?.ToString() ?? "nothing"}");
                }
            }
        }

        return OperationOutcome.Ok("Webbläsarreglerna är bekräftade.");
    }

    protected override async Task<OperationOutcome> DoRollbackAsync(
        OperationSnapshot snapshot, SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var captured = Deserialize(snapshot.PreviousValue);

        if (captured is null)
        {
            return OperationOutcome.Fail(
                "De tidigare webbläsarreglerna kunde inte läsas, så de kunde inte återställas.");
        }

        var failures = new List<string>();

        foreach (var value in captured)
        {
            try
            {
                if (IsListPolicy(value.Name))
                {
                    if (!value.ExistedBefore)
                    {
                        await _registry
                            .DeleteKeyAsync(RegistryScope.LocalMachine, null, $"{EdgePolicyKey}\\{value.Name}", cancellationToken)
                            .ConfigureAwait(false);
                    }

                    // A list that existed before is left alone: its previous
                    // contents were not KidShell's to enumerate or restore, and
                    // guessing would be worse than leaving the parent's own
                    // policy in place.
                    continue;
                }

                if (value.ExistedBefore && int.TryParse(value.PreviousValue, out var previous))
                {
                    await _registry
                        .WriteDWordAsync(RegistryScope.LocalMachine, null, EdgePolicyKey, value.Name, previous, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _registry
                        .DeleteValueAsync(RegistryScope.LocalMachine, null, EdgePolicyKey, value.Name, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Keep going: one value that cannot be restored must not strand
                // the rest.
                failures.Add($"{value.Name}: {ex.Message}");
            }
        }

        return failures.Count == 0
            ? OperationOutcome.Ok("Webbläsarreglerna återställdes.")
            : OperationOutcome.Fail(
                "Några webbläsarregler kunde inte återställas.", string.Join("; ", failures));
    }

    private static List<CapturedPolicyValue>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<CapturedPolicyValue>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
