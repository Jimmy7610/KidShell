using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using KidShell.WindowsIntegration.Platform;

namespace KidShell.WindowsIntegration.Operations;

/// <summary>
/// Starts KidShell when the child signs in.
///
/// SCOPE IS THE WHOLE DESIGN HERE
/// ------------------------------
/// The value is written to the CHILD's <c>Run</c> key
/// (<c>HKEY_USERS\&lt;child SID&gt;\Software\Microsoft\Windows\CurrentVersion\Run</c>),
/// never to <c>HKEY_LOCAL_MACHINE</c>. A machine-wide autorun would launch
/// KidShell for the parent too, which is both wrong and the sort of thing that
/// makes people uninstall a product angrily. There is deliberately no code path
/// in KidShell that writes a machine-wide Run value.
///
/// A packaged app has no .exe path to point at, so the documented launch route
/// is <c>explorer.exe shell:AppsFolder\&lt;AUMID&gt;</c> - the same mechanism the
/// Start menu uses.
/// See: https://learn.microsoft.com/windows/win32/shell/app-registration
/// </summary>
public sealed class ChildAutostartOperation : SecurityOperationBase
{
    /// <summary>The documented per-user startup key.</summary>
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>KidShell's own value name. Nothing else in the key is touched.</summary>
    internal const string ValueName = "KidShell";

    private readonly IRegistryStore _registry;
    private readonly string _childSid;
    private readonly string _aumid;

    private string? _previousValue;
    private bool _valueExistedBefore;

    public ChildAutostartOperation(
        IRegistryStore registry,
        string childSid,
        string aumid,
        IKidShellLogger logger) : base(logger)
    {
        _registry = registry;
        _childSid = childSid;
        _aumid = aumid;
    }

    public override string Id => "child-autostart";

    public override string Description => "Starta KidShell när barnet loggar in";

    public override ChangeRiskLevel RiskLevel => ChangeRiskLevel.Medium;

    public override RequiredCapability CapabilityRequired => RequiredCapability.None;

    /// <summary>The command the Run value holds.</summary>
    internal string LaunchCommand => $"explorer.exe shell:AppsFolder\\{_aumid}";

    protected override async Task<OperationOutcome> DoPreflightAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_childSid))
        {
            return OperationOutcome.Fail("Barnkontots SID saknas.");
        }

        if (string.IsNullOrWhiteSpace(_aumid))
        {
            return OperationOutcome.Fail("KidShells app-id (AUMID) kunde inte läsas.");
        }

        // The child's hive is only present in HKEY_USERS once that account has
        // signed in at least once. Saying so plainly beats failing at Apply
        // with a registry error the parent cannot act on.
        try
        {
            await _registry.KeyExistsAsync(RegistryScope.NamedUser, _childSid, RunKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return OperationOutcome.Fail(
                "Barnkontots inställningar kunde inte läsas. Logga in på barnkontot en gång först.",
                ex.ToString());
        }

        return OperationOutcome.Ok("KidShell kan ställas in att starta automatiskt.");
    }

    protected override async Task<OperationSnapshot> DoCaptureStateAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        _previousValue = await _registry
            .ReadStringAsync(RegistryScope.NamedUser, _childSid, RunKey, ValueName, cancellationToken)
            .ConfigureAwait(false);

        _valueExistedBefore = _previousValue is not null;

        return new OperationSnapshot
        {
            OperationId = Id,
            Description = _valueExistedBefore
                ? "KidShell startade redan automatiskt för barnet."
                : "KidShell startade inte automatiskt för barnet.",
            PreviousValue = _previousValue,
            ExistedBefore = _valueExistedBefore
        };
    }

    protected override async Task<OperationOutcome> DoApplyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        await _registry
            .WriteStringAsync(RegistryScope.NamedUser, _childSid, RunKey, ValueName, LaunchCommand, cancellationToken)
            .ConfigureAwait(false);

        return OperationOutcome.Ok("KidShell startar nu när barnet loggar in.");
    }

    protected override async Task<OperationOutcome> DoVerifyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var written = await _registry
            .ReadStringAsync(RegistryScope.NamedUser, _childSid, RunKey, ValueName, cancellationToken)
            .ConfigureAwait(false);

        if (written is null)
        {
            return OperationOutcome.Fail("Autostarten kunde inte hittas efter att den skrivits.");
        }

        if (!string.Equals(written, LaunchCommand, StringComparison.Ordinal))
        {
            return OperationOutcome.Fail(
                "Autostarten fick ett annat värde än det KidShell skrev.",
                $"expected '{LaunchCommand}', found '{written}'");
        }

        return OperationOutcome.Ok("Autostarten är bekräftad.");
    }

    protected override async Task<OperationOutcome> DoRollbackAsync(
        OperationSnapshot snapshot, SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        if (snapshot.ExistedBefore && snapshot.PreviousValue is not null)
        {
            // Something was there before KidShell. Put exactly that back rather
            // than deleting the key and losing whatever the family had set up.
            await _registry
                .WriteStringAsync(RegistryScope.NamedUser, _childSid, RunKey, ValueName,
                    snapshot.PreviousValue, cancellationToken)
                .ConfigureAwait(false);

            return OperationOutcome.Ok("Den tidigare autostarten återställdes.");
        }

        await _registry
            .DeleteValueAsync(RegistryScope.NamedUser, _childSid, RunKey, ValueName, cancellationToken)
            .ConfigureAwait(false);

        var remaining = await _registry
            .ReadStringAsync(RegistryScope.NamedUser, _childSid, RunKey, ValueName, cancellationToken)
            .ConfigureAwait(false);

        return remaining is null
            ? OperationOutcome.Ok("Autostarten togs bort.")
            : OperationOutcome.Fail("Autostarten kunde inte tas bort.");
    }
}
