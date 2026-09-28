using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using KidShell.WindowsIntegration.Broker;
using KidShell.WindowsIntegration.Operations;
using KidShell.WindowsIntegration.Platform;

namespace KidShell.SecurityHost;

/// <summary>
/// Turns typed requests into operations, and refuses anything else.
///
/// THE SHAPE OF THE TRUST BOUNDARY
/// -------------------------------
/// Everything arriving here is untrusted, even though the only intended caller
/// is KidShell's own UI. The order is always the same and never varies:
///
///     parse -> validate -> map to a fixed operation -> run
///
/// If parsing fails the line is rejected. If validation fails the request is
/// rejected. The mapping is a switch over a closed enum, so there is no request
/// that names an operation the helper was not built with, and no field whose
/// contents become a command.
///
/// WHY EVERY MUTATION STILL REFUSES IN THIS BUILD
/// ----------------------------------------------
/// The operations require an Apply-mode <see cref="SecurityExecutionContext"/>,
/// and no KidShell build can construct one: the type has a private constructor
/// and a single public factory that returns AuditOnly. So the helper compiles
/// with the complete production implementation and still cannot change this
/// machine. Enabling Apply is a deliberate, reviewable code change, not a flag.
/// </summary>
public sealed class ElevatedDispatcher
{
    private readonly IKidShellLogger _logger;
    private readonly ILocalAccountService _accounts;
    private readonly IRegistryStore _registry;
    private readonly IToolRunner _tools;
    private readonly IServiceControl _services;
    private readonly IFileSystem _files;
    private readonly string _workingDirectory;

    public ElevatedDispatcher(IKidShellLogger logger)
        : this(logger, BuildDefaults(logger))
    {
    }

    internal ElevatedDispatcher(IKidShellLogger logger, HostServices services)
    {
        _logger = logger;
        _accounts = services.Accounts;
        _registry = services.Registry;
        _tools = services.Tools;
        _services = services.Services;
        _files = services.Files;
        _workingDirectory = services.WorkingDirectory;
    }

    private static HostServices BuildDefaults(IKidShellLogger logger)
    {
        var tools = new ToolRunner(logger);

        return new HostServices
        {
            Accounts = OperatingSystem.IsWindows()
                ? new WindowsLocalAccountService(logger)
                : throw new PlatformNotSupportedException("KidShell.SecurityHost runs on Windows only."),
            Registry = new WindowsRegistryStore(logger),
            Tools = tools,
            Services = new ScServiceControl(tools, logger),
            Files = new PhysicalFileSystem(),
            WorkingDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "KidShell", "security")
        };
    }

    /// <summary>
    /// Reads requests until the input closes.
    ///
    /// One JSON object per line. A line that does not parse is answered with a
    /// rejection and the loop continues - a malformed line is not a reason to
    /// abandon a conversation that may be mid-transaction.
    /// </summary>
    public async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        _logger.Info(SecurityAuditEvents.Category, "Elevated helper ready.");

        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var response = await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);

            await output.WriteLineAsync(ElevatedProtocol.Serialize(response)).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.Info(SecurityAuditEvents.Category, "Elevated helper finished.");
        return 0;
    }

    internal async Task<ElevatedResponse> HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        var request = ElevatedProtocol.DeserializeRequest(line);

        if (request is null)
        {
            return ElevatedResponse.Reject("unknown", "Begäran kunde inte tolkas.");
        }

        return await HandleAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ElevatedResponse> HandleAsync(ElevatedRequest request, CancellationToken cancellationToken)
    {
        var rejection = ElevatedRequestValidator.Validate(request);

        if (rejection is not null)
        {
            _logger.Warning(SecurityAuditEvents.Category,
                $"Rejected {request.Kind} request {request.RequestId}: {rejection}");

            return ElevatedResponse.Reject(request.RequestId, rejection);
        }

        if (request.Kind == ElevatedOperationKind.Probe)
        {
            return new ElevatedResponse
            {
                RequestId = request.RequestId,
                Success = true,
                Message = "Rättighetshjälparen svarar."
            };
        }

        var operation = Create(request);

        if (operation is null)
        {
            return ElevatedResponse.Reject(request.RequestId, "Åtgärden stöds inte.");
        }

        // AuditOnly is the only context that exists. Every operation below
        // refuses it, which is exactly what must happen on a machine that is
        // not a dedicated target device.
        var context = SecurityExecutionContext.AuditOnly();

        if (request.DryRun || context.Mode != SecurityExecutionMode.Apply)
        {
            var preflight = await operation.PreflightAsync(context, cancellationToken).ConfigureAwait(false);

            return new ElevatedResponse
            {
                RequestId = request.RequestId,
                Success = preflight.Success,
                Message = preflight.Success
                    ? $"{operation.Description}: kontrollerad, ingenting ändrades."
                    : preflight.Message,
                Detail = preflight.Detail
            };
        }

        // Unreachable in every build that has ever shipped, and kept so that
        // the day an Apply context exists this path is already written, already
        // reviewed and already inside a transaction.
        return await ApplyThroughTransactionAsync(request, operation, context, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ElevatedResponse> ApplyThroughTransactionAsync(
        ElevatedRequest request,
        ISecurityOperation operation,
        SecurityExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Never applied directly. Even a single operation goes through the
        // coordinator, so it gets a preflight, a snapshot, a recovery manifest
        // written before the first change, verification and rollback.
        var store = new RecoveryManifestStore(Path.Combine(_workingDirectory, "recovery"), _logger);

        var machine = new RecoveryMachineSummary
        {
            WindowsEdition = Environment.OSVersion.VersionString,
            BuildNumber = Environment.OSVersion.Version.Build,
            MachineName = Environment.MachineName,
            ExecutionMode = context.Mode.ToString()
        };

        var transaction = new SecurityTransaction([operation], store, machine, _logger);
        var result = await transaction.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);

        return new ElevatedResponse
        {
            RequestId = request.RequestId,
            Success = result.Succeeded,
            Message = result.FailureMessage ?? $"{operation.Description}: klart.",
            Detail = result.NeedsManualRecovery
                ? "Rollback misslyckades. Följ återställningsfilen."
                : null
        };
    }

    /// <summary>
    /// The complete mapping from request to operation.
    ///
    /// A switch over a closed enum. There is no reflection, no type name from
    /// the request, and no way to reach a type the helper was not compiled
    /// with.
    /// </summary>
    private ISecurityOperation? Create(ElevatedRequest request) => request.Kind switch
    {
        ElevatedOperationKind.CreateChildAccount => new CreateChildAccountOperation(
            _accounts, request.Username!, request.FullName, password: null, _logger),

        ElevatedOperationKind.DemoteChildAccount => new DemoteChildAccountOperation(
            _accounts, request.Sid!, _logger),

        ElevatedOperationKind.ConfigureAutostart => new ChildAutostartOperation(
            _registry, request.Sid!, request.Aumid!, _logger),

        ElevatedOperationKind.DeployAppLockerPolicy => new AppLockerDeploymentOperation(
            _tools, _files, request.PolicyXml!, _workingDirectory, _logger),

        ElevatedOperationKind.ConfigureApplicationIdentityService => new ApplicationIdentityServiceOperation(
            _services, _tools, _files, _workingDirectory, _logger),

        ElevatedOperationKind.InstallWatchdogService => new WatchdogServiceOperation(
            _services, _files, request.ExecutablePath!, _logger),

        // Assigned Access and browser policy need artifacts the caller builds
        // from settings; both are created by the caller-side factory so the
        // generated policy can be reviewed before it is sent.
        ElevatedOperationKind.ConfigureAssignedAccess => null,
        ElevatedOperationKind.DeployBrowserPolicy => null,

        _ => null
    };
}

/// <summary>The helper's dependencies, injectable so tests never touch Windows.</summary>
internal sealed record HostServices
{
    public required ILocalAccountService Accounts { get; init; }

    public required IRegistryStore Registry { get; init; }

    public required IToolRunner Tools { get; init; }

    public required IServiceControl Services { get; init; }

    public required IFileSystem Files { get; init; }

    public required string WorkingDirectory { get; init; }
}
