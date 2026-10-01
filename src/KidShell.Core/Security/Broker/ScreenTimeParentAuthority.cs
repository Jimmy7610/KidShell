using KidShell.Core.Diagnostics;

namespace KidShell.Core.Security.Broker;

/// <summary>
/// The screen-time changes a parent may make, performed where they can be
/// trusted.
///
/// WHY THESE THREE ARE NOT ORDINARY SAVES
/// --------------------------------------
/// Everything else the engine writes makes the child's situation stricter, so
/// the privileged side can accept it from the child's own session under
/// monotonic rules. These three make it looser: extra minutes, an unlimited
/// day, a cleared counter.
///
/// If they travelled as an ordinary SaveScreenTimeState the rules would have
/// to allow a lower counter and a higher bonus from the child's session, and
/// then the rules would permit exactly the write they exist to refuse - a
/// modified KidShell.App would simply send "used seconds: 0" and be obeyed by
/// SYSTEM.
///
/// So the caller says what it wants DONE and never what the result should be.
/// The privileged side reads the counter it holds, applies the change, and
/// writes the outcome. The difference between "grant fifteen minutes" and
/// "here is the new state" is the whole security property.
/// </summary>
public interface IScreenTimeParentAuthority
{
    /// <summary>Whether the privileged side can be reached and a parent is authenticated.</summary>
    bool IsAvailable { get; }

    bool GrantMinutes(int minutes);

    bool GrantRestOfDay();

    /// <summary>Clears today's counter. The one write that may lower it.</summary>
    bool ResetToday();
}

/// <summary>
/// Sends the three parent grants to the security service, with the capability
/// the service itself issued.
/// </summary>
public sealed class BrokeredScreenTimeParentAuthority : IScreenTimeParentAuthority
{
    private readonly IElevatedBrokerClient _broker;
    private readonly ParentCapabilityHolder _capability;
    private readonly IKidShellLogger _logger;

    public BrokeredScreenTimeParentAuthority(
        IElevatedBrokerClient broker,
        ParentCapabilityHolder capability,
        IKidShellLogger logger)
    {
        _broker = broker;
        _capability = capability;
        _logger = logger;
    }

    /// <summary>
    /// Available only while a parent is actually authenticated.
    ///
    /// Not "while the service is reachable". Without a capability the service
    /// would refuse these anyway; checking here means the engine falls back
    /// to its local behaviour in a development build instead of silently
    /// losing a parent's grant in a production one.
    /// </summary>
    public bool IsAvailable => _capability.IsHeld && _broker.IsAvailable;

    public bool GrantMinutes(int minutes) => Send(new ElevatedRequest
    {
        Kind = ElevatedOperationKind.GrantScreenTime,
        RequestId = Guid.NewGuid().ToString("n"),
        GrantMinutes = minutes,
        ParentCapability = _capability.Value,
        DryRun = false
    });

    public bool GrantRestOfDay() => Send(new ElevatedRequest
    {
        Kind = ElevatedOperationKind.GrantScreenTime,
        RequestId = Guid.NewGuid().ToString("n"),
        GrantRestOfDay = true,
        ParentCapability = _capability.Value,
        DryRun = false
    });

    public bool ResetToday() => Send(new ElevatedRequest
    {
        Kind = ElevatedOperationKind.ResetScreenTimeToday,
        RequestId = Guid.NewGuid().ToString("n"),
        ParentCapability = _capability.Value,
        DryRun = false
    });

    private bool Send(ElevatedRequest request)
    {
        var response = _broker.Send(request);

        if (!response.Success)
        {
            _logger.Warning(BrokerAudit.Category,
                $"{request.Kind} was not applied ({response.Reason}): {response.Message}");
        }

        return response.Success;
    }
}
