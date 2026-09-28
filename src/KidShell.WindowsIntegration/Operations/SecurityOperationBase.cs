using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;

namespace KidShell.WindowsIntegration.Operations;

/// <summary>
/// Shared plumbing for every operation that can change Windows.
///
/// THE CONTRACT THIS ENFORCES
/// --------------------------
/// Three things must be true of every mutating operation, and getting any of
/// them wrong is how a parent ends up locked out of their own computer:
///
///  1. **Apply refuses without an Apply context.** Checked here, in a sealed
///     override, so a subclass cannot forget it or weaken it. No KidShell build
///     can construct an Apply context, so on this machine every one of these
///     operations refuses.
///  2. **Exceptions become outcomes.** A stage that throws must return a failed
///     outcome the coordinator can roll back from, not unwind past it.
///  3. **Rollback is honest about doing nothing.** An operation that never
///     applied must not report a successful rollback it did not perform.
///
/// Subclasses implement the <c>Do*</c> methods and never see a context they
/// are not allowed to act on.
/// </summary>
public abstract class SecurityOperationBase : ISecurityOperation
{
    protected SecurityOperationBase(IKidShellLogger logger) => Logger = logger;

    protected IKidShellLogger Logger { get; }

    public abstract string Id { get; }

    public abstract string Description { get; }

    public abstract ChangeRiskLevel RiskLevel { get; }

    public virtual bool RequiresAdministrator => true;

    /// <summary>
    /// Every operation in this assembly can roll back. An operation that
    /// answered false could not take part in a transaction at all, so the
    /// honest options are "make it reversible" or "do not build it".
    /// </summary>
    public virtual bool CanRollback => true;

    public abstract RequiredCapability CapabilityRequired { get; }

    /// <summary>
    /// Whether Apply ran far enough that rollback has something to undo. Set
    /// by the base class, read by <see cref="DoRollbackAsync"/>.
    /// </summary>
    protected bool HasApplied { get; private set; }

    // --------------------------------------------------------------- stages

    public async Task<OperationOutcome> PreflightAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return await DoPreflightAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(SecurityAuditEvents.Category, $"{Id}: preflight threw.", ex);
            return OperationOutcome.Fail($"{Description} kunde inte kontrolleras.", ex.ToString());
        }
    }

    public async Task<OperationSnapshot> CaptureStateAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await DoCaptureStateAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sealed. The Apply-context check is the one guarantee no subclass gets to
    /// reimplement, because every implementation would be one more place to get
    /// it wrong.
    /// </summary>
    public async Task<OperationOutcome> ApplyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Mode != SecurityExecutionMode.Apply)
        {
            // Not an exception: the coordinator refuses long before this, and
            // an operation reached in audit mode is a bug worth reporting as a
            // failed outcome rather than a crash.
            Logger.Warning(SecurityAuditEvents.Category,
                $"{Id}: Apply refused because the context is {context.Mode}.");

            return OperationOutcome.Fail(
                "KidShell körs i granskningsläge och ändrar ingenting.",
                $"{Id}: Apply called with mode {context.Mode}.");
        }

        try
        {
            var outcome = await DoApplyAsync(context, cancellationToken).ConfigureAwait(false);

            if (outcome.Success)
            {
                HasApplied = true;
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            // The change may have landed before the cancellation was noticed,
            // so rollback must treat this operation as applied.
            HasApplied = true;
            throw;
        }
        catch (Exception ex)
        {
            // Same reasoning: a throwing Apply has unknown effect.
            HasApplied = true;
            Logger.Error(SecurityAuditEvents.Category, $"{Id}: apply threw.", ex);
            return OperationOutcome.Fail($"{Description} kunde inte genomföras.", ex.ToString());
        }
    }

    public async Task<OperationOutcome> VerifyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return await DoVerifyAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An unverifiable change is treated as a failed one. Windows can
            // accept a write and not honour it, so "I could not check" and
            // "it did not work" get the same answer.
            Logger.Error(SecurityAuditEvents.Category, $"{Id}: verify threw.", ex);
            return OperationOutcome.Fail($"{Description} kunde inte bekräftas.", ex.ToString());
        }
    }

    public async Task<OperationOutcome> RollbackAsync(
        OperationSnapshot snapshot,
        SecurityExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);

        if (!HasApplied)
        {
            // Nothing was done, so nothing is undone. Reporting success here is
            // accurate rather than generous: the machine is in the state the
            // snapshot describes.
            return OperationOutcome.Ok($"{Description}: ingenting hade ändrats.");
        }

        try
        {
            var outcome = await DoRollbackAsync(snapshot, context, cancellationToken).ConfigureAwait(false);

            if (outcome.Success)
            {
                HasApplied = false;
                Logger.Info(SecurityAuditEvents.Category, $"{Id}: rolled back.");
            }
            else
            {
                Logger.Error(SecurityAuditEvents.Category, $"{Id}: rollback FAILED: {outcome.Detail ?? outcome.Message}");
            }

            return outcome;
        }
        catch (Exception ex)
        {
            Logger.Error(SecurityAuditEvents.Category, $"{Id}: rollback threw.", ex);
            return OperationOutcome.Fail($"{Description} kunde inte återställas.", ex.ToString());
        }
    }

    // ------------------------------------------------------------ subclass

    protected abstract Task<OperationOutcome> DoPreflightAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken);

    protected abstract Task<OperationSnapshot> DoCaptureStateAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken);

    protected abstract Task<OperationOutcome> DoApplyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken);

    protected abstract Task<OperationOutcome> DoVerifyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken);

    protected abstract Task<OperationOutcome> DoRollbackAsync(
        OperationSnapshot snapshot, SecurityExecutionContext context, CancellationToken cancellationToken);
}
