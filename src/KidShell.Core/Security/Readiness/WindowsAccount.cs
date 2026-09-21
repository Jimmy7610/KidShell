namespace KidShell.Core.Security.Readiness;

/// <summary>
/// Safe metadata about a local Windows account.
///
/// Deliberately minimal: enough to tell a parent which accounts exist and
/// whether one would suit a child, and nothing more. No password state, no
/// profile paths, no tokens.
/// </summary>
public sealed record WindowsAccount
{
    public required string Username { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public required string Sid { get; init; }

    public required bool IsAdministrator { get; init; }

    public required bool IsEnabled { get; init; }

    /// <summary>True for built-in accounts such as DefaultAccount or the guest account.</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// Whether this account could serve as the child's: enabled, not an
    /// administrator, and not one of the Windows built-ins.
    /// </summary>
    public bool IsCandidateChildAccount => IsEnabled && !IsAdministrator && !IsBuiltIn;

    /// <summary>
    /// Whether this account could be the parent's way back in if secure setup
    /// went wrong: an enabled administrator.
    /// </summary>
    public bool IsRecoveryCandidate => IsEnabled && IsAdministrator;
}

/// <summary>
/// Lists local Windows accounts. Read-only by contract.
///
/// There is deliberately no create, delete, rename, password or group method
/// on this interface. Account management belongs to a later milestone and will
/// need a new, explicitly named interface to exist at all.
/// </summary>
public interface IWindowsAccountDiscovery
{
    Task<IReadOnlyList<WindowsAccount>> ListLocalAccountsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Account discovery that returns nothing. Used when enumeration is unavailable.</summary>
public sealed class EmptyAccountDiscovery : IWindowsAccountDiscovery
{
    public static readonly EmptyAccountDiscovery Instance = new();

    public Task<IReadOnlyList<WindowsAccount>> ListLocalAccountsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WindowsAccount>>([]);
}
