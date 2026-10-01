using System.Runtime.Versioning;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Broker;
using KidShell.Core.Security.Storage;
using KidShell.WindowsIntegration.Broker;
using Microsoft.Extensions.Hosting;

namespace KidShell.SecurityHost;

/// <summary>
/// The service's one job: listen on the broker pipe.
///
/// WHY THIS IS A SERVICE AND THE OLD HELPER WAS NOT
/// ------------------------------------------------
/// The helper was a process started per request and expected to be elevated,
/// and nothing ever elevated it. Adding a consent prompt would not have
/// repaired it: screen time is written on a timer, the PIN throttle on every
/// attempt, and a shell a child operates cannot raise a prompt during
/// ordinary enforcement.
///
/// So the privilege is held once, by something already running, and the
/// unprivileged side connects to it. The cost is a resident LocalSystem
/// process with an endpoint, which is a larger thing to get right - and the
/// whole of <see cref="BrokerAuthorizationPolicy"/> and
/// <see cref="ProtectedStateTransitionRules"/> is the work of getting it
/// right.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrokerWorker : BackgroundService
{
    private readonly IKidShellLogger _logger;

    public BrokerWorker(IKidShellLogger logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var store = new PrivilegedProtectedStateStore(_logger);
        var capabilities = new ParentCapabilityRegistry();

        var server = new ElevatedBrokerServer(
            store, capabilities, _logger, TimeProvider.System,
            new DispatcherAdapter(new ElevatedDispatcher(_logger)));

        // The account to scope the pipe to comes from the provisioning
        // marker, which lives in the protected store and is therefore not
        // something the child can write. A device without one gets the
        // wider access list, and every request still goes through the same
        // authorization matrix.
        var childSid = ProtectedPolicyTrustEvaluator.ChildSidFrom(
            store.Read(ProtectedDocument.ProvisioningMarker));

        var plan = childSid.Length > 0
            ? BrokerPipeAccessPlan.ForChild(childSid)
            : BrokerPipeAccessPlan.Unprovisioned();

        await using var listener = new NamedPipeBrokerListener(
            BrokerEndpoint.PipeName, plan, server, _logger, childSid);

        await listener.RunAsync(stoppingToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Lets the broker hand machine mutations to the existing dispatcher.
///
/// The two are deliberately separate. Writing a protected document and
/// changing Windows are different kinds of act with different blast radii,
/// and the dispatcher's own gate - an Apply-mode context no build can
/// construct - stays exactly where it was.
/// </summary>
internal sealed class DispatcherAdapter : IElevatedOperationDispatcher
{
    private readonly ElevatedDispatcher _dispatcher;

    public DispatcherAdapter(ElevatedDispatcher dispatcher) => _dispatcher = dispatcher;

    public ElevatedResponse Dispatch(ElevatedRequest request) =>
        _dispatcher.HandleAsync(request, CancellationToken.None).GetAwaiter().GetResult();
}
