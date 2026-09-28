using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using KidShell.WindowsIntegration.Platform;

namespace KidShell.WindowsIntegration.Operations;

/// <summary>
/// Registers the KidShell watchdog service.
///
/// WHAT THE WATCHDOG IS AND IS NOT
/// -------------------------------
/// It restarts KidShell if KidShell stops. That is the whole job. It does not
/// accept commands, does not read the child's configuration, does not expose an
/// endpoint, and monitors exactly one executable path fixed at install time.
///
/// The narrowness is the security design. A service running as LocalSystem that
/// takes instructions from a file a child account can write is a privilege
/// escalation with a friendly name, so the watchdog takes no instructions at
/// all.
///
/// It also never falls back to "show the desktop". A watchdog that gives up and
/// exposes an unrestricted session has done worse than nothing; this one shows
/// a calm failure state and keeps the session contained.
/// </summary>
public sealed class WatchdogServiceOperation : SecurityOperationBase
{
    /// <summary>The service name. A constant, never configuration.</summary>
    public const string ServiceName = "KidShellWatchdog";

    public const string DisplayName = "KidShell Watchdog";

    private readonly IServiceControl _services;
    private readonly IFileSystem _files;
    private readonly string _executablePath;

    public WatchdogServiceOperation(
        IServiceControl services,
        IFileSystem files,
        string executablePath,
        IKidShellLogger logger) : base(logger)
    {
        _services = services;
        _files = files;
        _executablePath = executablePath;
    }

    public override string Id => "watchdog-service";

    public override string Description => "Installera KidShells vakttjänst";

    public override ChangeRiskLevel RiskLevel => ChangeRiskLevel.Medium;

    public override RequiredCapability CapabilityRequired => RequiredCapability.None;

    protected override async Task<OperationOutcome> DoPreflightAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_executablePath))
        {
            return OperationOutcome.Fail("Vakttjänstens sökväg saknas.");
        }

        if (!Path.IsPathFullyQualified(_executablePath))
        {
            // A relative path resolves against whatever the service control
            // manager's working directory happens to be. That is a way to start
            // the wrong binary.
            return OperationOutcome.Fail("Vakttjänstens sökväg måste vara fullständig.");
        }

        if (!await _files.FileExistsAsync(_executablePath, cancellationToken).ConfigureAwait(false))
        {
            return OperationOutcome.Fail("Vakttjänstens program hittades inte.");
        }

        var status = await _services.QueryAsync(ServiceName, cancellationToken).ConfigureAwait(false);

        if (status.IsInstalled)
        {
            return OperationOutcome.Ok("Vakttjänsten finns redan och konfigureras om.");
        }

        return OperationOutcome.Ok("Vakttjänsten kan installeras.");
    }

    protected override async Task<OperationSnapshot> DoCaptureStateAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var status = await _services.QueryAsync(ServiceName, cancellationToken).ConfigureAwait(false);

        return new OperationSnapshot
        {
            OperationId = Id,
            Description = status.IsInstalled
                ? $"Vakttjänsten fanns redan (start: {status.StartType})."
                : "Vakttjänsten fanns inte.",
            PreviousValue = status.IsInstalled ? status.StartType : null,
            ExistedBefore = status.IsInstalled
        };
    }

    protected override async Task<OperationOutcome> DoApplyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var status = await _services.QueryAsync(ServiceName, cancellationToken).ConfigureAwait(false);

        if (status.IsInstalled)
        {
            await _services.SetStartTypeAsync(ServiceName, "auto", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _services
                .InstallAsync(ServiceName, DisplayName, _executablePath, "auto", cancellationToken)
                .ConfigureAwait(false);
        }

        await _services.StartAsync(ServiceName, cancellationToken).ConfigureAwait(false);

        return OperationOutcome.Ok("Vakttjänsten installerades och startades.");
    }

    protected override async Task<OperationOutcome> DoVerifyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var status = await _services.QueryAsync(ServiceName, cancellationToken).ConfigureAwait(false);

        if (!status.IsInstalled)
        {
            return OperationOutcome.Fail("Vakttjänsten kunde inte hittas efter installationen.");
        }

        if (!status.IsRunning)
        {
            return OperationOutcome.Fail("Vakttjänsten installerades men körs inte.");
        }

        return OperationOutcome.Ok("Vakttjänsten körs.");
    }

    protected override async Task<OperationOutcome> DoRollbackAsync(
        OperationSnapshot snapshot, SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        if (snapshot.ExistedBefore)
        {
            // It was there before KidShell touched it. Put its start type back
            // rather than removing a service somebody else installed.
            var previous = snapshot.PreviousValue switch
            {
                "Automatic" => "auto",
                "Manual" => "demand",
                "Disabled" => "disabled",
                _ => null
            };

            if (previous is null)
            {
                return OperationOutcome.Fail("Vakttjänstens tidigare starttyp kunde inte tolkas.");
            }

            await _services.SetStartTypeAsync(ServiceName, previous, cancellationToken).ConfigureAwait(false);
            return OperationOutcome.Ok("Vakttjänstens tidigare inställning återställdes.");
        }

        await _services.UninstallAsync(ServiceName, cancellationToken).ConfigureAwait(false);

        var status = await _services.QueryAsync(ServiceName, cancellationToken).ConfigureAwait(false);

        return status.IsInstalled
            ? OperationOutcome.Fail("Vakttjänsten kunde inte tas bort.")
            : OperationOutcome.Ok("Vakttjänsten togs bort.");
    }
}

/// <summary>
/// Signs the child's Windows session out.
///
/// This is what "Avsluta barnläget" must do once a verified secure
/// configuration is active. Simply closing KidShell would drop the child onto
/// the desktop of an account that is still signed in - the opposite of what a
/// parent pressing that button intends.
///
/// IT IS NOT REVERSIBLE, AND THAT IS WHY IT IS NOT IN A TRANSACTION
/// ----------------------------------------------------------------
/// You cannot un-sign-out. <see cref="CanRollback"/> is therefore false, and
/// the coordinator refuses any transaction containing it - deliberately. Ending
/// a session is the LAST thing that happens, after every reversible change has
/// been applied and verified, and it is invoked on its own rather than as part
/// of a set.
/// </summary>
public sealed class ChildSessionLogoutOperation : SecurityOperationBase
{
    private readonly ISessionControl _sessions;

    public ChildSessionLogoutOperation(ISessionControl sessions, IKidShellLogger logger)
        : base(logger) => _sessions = sessions;

    public override string Id => "child-session-logout";

    public override string Description => "Logga ut barnets Windows-session";

    public override ChangeRiskLevel RiskLevel => ChangeRiskLevel.High;

    public override bool RequiresAdministrator => false;

    /// <summary>
    /// False, and honestly so. Signing a session out cannot be undone, so this
    /// operation may never be admitted to a transaction.
    /// </summary>
    public override bool CanRollback => false;

    public override RequiredCapability CapabilityRequired => RequiredCapability.None;

    protected override Task<OperationOutcome> DoPreflightAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(OperationOutcome.Ok("Sessionen kan loggas ut."));

    protected override Task<OperationSnapshot> DoCaptureStateAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new OperationSnapshot
        {
            OperationId = Id,
            Description = "En session som loggats ut kan inte återställas.",
            PreviousValue = null,
            ExistedBefore = true
        });

    protected override async Task<OperationOutcome> DoApplyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        await _sessions.LogOffCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
        return OperationOutcome.Ok("Sessionen loggas ut.");
    }

    protected override Task<OperationOutcome> DoVerifyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken) =>
        // Windows is tearing the session down; there is nothing left to read
        // back, and claiming a verification happened would be an invention.
        Task.FromResult(OperationOutcome.Ok("Windows loggar ut sessionen."));

    protected override Task<OperationOutcome> DoRollbackAsync(
        OperationSnapshot snapshot, SecurityExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(OperationOutcome.Fail(
            "En utloggad session kan inte återställas. Logga in igen i Windows."));
}
