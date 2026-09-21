namespace KidShell.Core.Security.Readiness;

/// <summary>
/// Whether security operations may change the machine.
/// </summary>
public enum SecurityExecutionMode
{
    /// <summary>
    /// Detect, evaluate and plan. Nothing about Windows is written. Every
    /// 0.1.x build runs in this mode and has no way to leave it.
    /// </summary>
    AuditOnly = 0,

    /// <summary>
    /// Reserved for the milestone that actually applies security. The enum
    /// member exists so the architecture has somewhere to grow; there is no
    /// way to construct a context in this mode, and nothing that could act on
    /// it. See <see cref="SecurityExecutionContext"/>.
    /// </summary>
    Apply = 1
}

/// <summary>
/// The capability token every security operation must be handed.
///
/// The type has a private constructor and exactly one public factory, which
/// produces <see cref="SecurityExecutionMode.AuditOnly"/>. There is no public,
/// internal or test-visible way to build an Apply context, so a future
/// milestone has to deliberately add one — a mutating call cannot appear by
/// accident, by a flipped boolean, or by a configuration value.
/// </summary>
public sealed class SecurityExecutionContext
{
    private SecurityExecutionContext(SecurityExecutionMode mode) => Mode = mode;

    public SecurityExecutionMode Mode { get; }

    public bool IsAuditOnly => Mode == SecurityExecutionMode.AuditOnly;

    /// <summary>The only context KidShell can currently create.</summary>
    public static SecurityExecutionContext AuditOnly() => new(SecurityExecutionMode.AuditOnly);

    public override string ToString() => Mode.ToString();
}

/// <summary>
/// The future boundary at which KidShell would change Windows.
///
/// Declared now so the readiness work has a shape to aim at, and deliberately
/// left without any implementation anywhere in the solution. A test asserts
/// that no type implements it, so the day someone writes one it is a visible,
/// reviewed decision rather than a quiet addition.
///
/// Any implementation must refuse to run unless handed an Apply context, which
/// currently cannot be constructed.
/// </summary>
public interface ISecurityMutator
{
    /// <summary>Applies one planned action. Must throw unless context.Mode is Apply.</summary>
    Task ApplyAsync(PlannedAction action, SecurityExecutionContext context, CancellationToken cancellationToken = default);
}
