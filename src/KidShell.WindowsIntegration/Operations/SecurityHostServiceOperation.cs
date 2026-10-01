using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Broker;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using KidShell.WindowsIntegration.Platform;

namespace KidShell.WindowsIntegration.Operations;

/// <summary>
/// Registers the KidShell security service.
///
/// NOT RUN ON A DEVELOPMENT MACHINE, AND NOT RUNNABLE ON ONE
/// ---------------------------------------------------------
/// Like every operation in this assembly it requires an Apply-mode
/// <see cref="SecurityExecutionContext"/>, and no KidShell build can
/// construct one: the type has a private constructor and a single public
/// factory that returns AuditOnly. The complete implementation is here, it is
/// covered by tests against fake platform services, and it has never changed
/// a machine.
///
/// WHAT MAKES THIS ONE DIFFERENT FROM THE WATCHDOG
/// -----------------------------------------------
/// The watchdog takes no instructions. This service listens on an endpoint
/// and acts on what it is told, which raises the stakes on two things the
/// preflight therefore checks rather than assumes:
///
///   * the image path is under Program Files, because a LocalSystem service
///     whose binary the child can replace is a privilege escalation with a
///     service name, and no access list on the pipe would matter;
///   * the start type is Automatic, because a named pipe belongs to whoever
///     creates it first and the service has to be there before anyone signs
///     in.
/// </summary>
public sealed class SecurityHostServiceOperation : SecurityOperationBase
{
    private readonly IServiceControl _services;
    private readonly IFileSystem _files;
    private readonly string _executablePath;
    private readonly string _programFilesPath;

    public SecurityHostServiceOperation(
        IServiceControl services,
        IFileSystem files,
        string executablePath,
        string programFilesPath,
        IKidShellLogger logger) : base(logger)
    {
        _services = services;
        _files = files;
        _executablePath = executablePath;
        _programFilesPath = programFilesPath;
    }

    public override string Id => "security-host-service";

    public override string Description => "Installera KidShells rättighetstjänst";

    /// <summary>
    /// High, and deliberately higher than the watchdog's.
    ///
    /// This installs a LocalSystem service that accepts requests. The
    /// watchdog installs one that accepts nothing. A review that treats them
    /// as the same risk has not read either of them.
    /// </summary>
    public override ChangeRiskLevel RiskLevel => ChangeRiskLevel.High;

    public override RequiredCapability CapabilityRequired => RequiredCapability.None;

    protected override async Task<OperationOutcome> DoPreflightAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_executablePath))
        {
            return OperationOutcome.Fail("Rättighetstjänstens sökväg saknas.");
        }

        if (!Path.IsPathFullyQualified(_executablePath))
        {
            // A relative path resolves against whatever the service control
            // manager's working directory happens to be, which is a way to
            // start the wrong binary as SYSTEM at every boot.
            return OperationOutcome.Fail("Rättighetstjänstens sökväg måste vara fullständig.");
        }

        if (!SecurityHostService.IsAcceptableImagePath(_executablePath, _programFilesPath))
        {
            return OperationOutcome.Fail(
                "Rättighetstjänsten måste installeras från en skyddad mapp under Program Files.");
        }

        if (!await _files.FileExistsAsync(_executablePath, cancellationToken).ConfigureAwait(false))
        {
            return OperationOutcome.Fail("Rättighetstjänstens program hittades inte.");
        }

        var status = await _services.QueryAsync(SecurityHostService.Name, cancellationToken)
            .ConfigureAwait(false);

        return status.IsInstalled
            ? OperationOutcome.Ok("Rättighetstjänsten finns redan och konfigureras om.")
            : OperationOutcome.Ok("Rättighetstjänsten kan installeras.");
    }

    protected override async Task<OperationSnapshot> DoCaptureStateAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var status = await _services.QueryAsync(SecurityHostService.Name, cancellationToken)
            .ConfigureAwait(false);

        return new OperationSnapshot
        {
            OperationId = Id,
            Description = status.IsInstalled
                ? $"Rättighetstjänsten fanns redan (start: {status.StartType})."
                : "Rättighetstjänsten fanns inte.",
            PreviousValue = status.IsInstalled ? status.StartType : null,
            ExistedBefore = status.IsInstalled
        };
    }

    protected override async Task<OperationOutcome> DoApplyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var status = await _services.QueryAsync(SecurityHostService.Name, cancellationToken)
            .ConfigureAwait(false);

        if (status.IsInstalled)
        {
            await _services
                .SetStartTypeAsync(SecurityHostService.Name, SecurityHostService.StartType, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _services
                .InstallAsync(
                    SecurityHostService.Name,
                    SecurityHostService.DisplayName,
                    _executablePath,
                    SecurityHostService.StartType,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await _services.StartAsync(SecurityHostService.Name, cancellationToken).ConfigureAwait(false);

        return OperationOutcome.Ok("Rättighetstjänsten installerades och startades.");
    }

    protected override async Task<OperationOutcome> DoVerifyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var status = await _services.QueryAsync(SecurityHostService.Name, cancellationToken)
            .ConfigureAwait(false);

        if (!status.IsInstalled)
        {
            return OperationOutcome.Fail("Rättighetstjänsten kunde inte hittas efter installationen.");
        }

        if (!status.IsRunning)
        {
            // An installed service that is not running is worse than one that
            // is missing: the product would look configured and every
            // protected write would fail.
            return OperationOutcome.Fail("Rättighetstjänsten installerades men körs inte.");
        }

        if (!string.Equals(status.StartType, "Automatic", StringComparison.OrdinalIgnoreCase))
        {
            return OperationOutcome.Fail(
                "Rättighetstjänsten startar inte automatiskt och hinner då inte bli först med sitt namn.");
        }

        return OperationOutcome.Ok("Rättighetstjänsten körs.");
    }

    protected override async Task<OperationOutcome> DoRollbackAsync(
        OperationSnapshot snapshot, SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        if (snapshot.ExistedBefore)
        {
            var previous = snapshot.PreviousValue switch
            {
                "Automatic" => "auto",
                "Manual" => "demand",
                "Disabled" => "disabled",
                _ => null
            };

            if (previous is null)
            {
                return OperationOutcome.Fail("Rättighetstjänstens tidigare starttyp kunde inte tolkas.");
            }

            await _services.SetStartTypeAsync(SecurityHostService.Name, previous, cancellationToken)
                .ConfigureAwait(false);

            return OperationOutcome.Ok("Rättighetstjänstens tidigare inställning återställdes.");
        }

        // Stopped before it is removed. A service left running with its
        // registration deleted keeps its pipe open, and the next install
        // would find the name taken by a process nothing can address.
        await _services.StopAsync(SecurityHostService.Name, cancellationToken).ConfigureAwait(false);
        await _services.UninstallAsync(SecurityHostService.Name, cancellationToken).ConfigureAwait(false);

        var status = await _services.QueryAsync(SecurityHostService.Name, cancellationToken)
            .ConfigureAwait(false);

        return status.IsInstalled
            ? OperationOutcome.Fail("Rättighetstjänsten kunde inte tas bort.")
            : OperationOutcome.Ok("Rättighetstjänsten togs bort.");
    }
}
