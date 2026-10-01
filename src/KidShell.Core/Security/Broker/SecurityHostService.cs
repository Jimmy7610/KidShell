namespace KidShell.Core.Security.Broker;

/// <summary>
/// What the KidShell security service is, as a plan rather than an action.
///
/// Nothing in this type installs anything. It describes the service the
/// install operation would create on a dedicated device, and it is what the
/// tests and the documentation agree against.
///
/// WHY LOCALSYSTEM AND NOT A SERVICE ACCOUNT
/// -----------------------------------------
/// The service's whole purpose is to write files the child's account cannot
/// write and to read a token the child's account cannot forge. A virtual
/// service account would be narrower and is the usual advice; it is not
/// usable here, because the protected store's access list has to name a
/// principal that exists before the service is installed and survives it
/// being reinstalled. LocalSystem is the honest answer, and the narrowing is
/// done by the contract rather than by the account: a closed enum, an
/// authorization matrix and a set of monotonic rules.
///
/// THERE IS ALREADY ANOTHER SERVICE, AND THEY DO NOT OVERLAP
/// ---------------------------------------------------------
/// KidShell.Watchdog restarts the shell when it stops. It accepts no
/// commands, reads no configuration, exposes no endpoint and monitors one
/// compiled-in target. This one holds security state and accepts typed
/// requests over an endpoint. Keeping them apart is deliberate: giving the
/// watchdog an endpoint would turn "restart the shell" into "do what this
/// message says, as SYSTEM", and giving the broker a restart loop would put
/// a privileged request handler in the one component that must never be
/// interesting to talk to.
/// </summary>
public static class SecurityHostService
{
    /// <summary>The service name. A constant, never configuration.</summary>
    public const string Name = "KidShellSecurityHost";

    public const string DisplayName = "KidShell Security Host";

    /// <summary>
    /// The account the service runs as.
    ///
    /// Expressed as the service control manager's own spelling, because that
    /// is what ends up in the recovery manifest and what an administrator
    /// would compare against.
    /// </summary>
    public const string Account = "LocalSystem";

    /// <summary>
    /// Automatic. The service must be listening before the child signs in.
    ///
    /// Not only for availability. A named pipe belongs to whoever creates it
    /// first, so a service that started after an interactive logon would
    /// leave a window in which something running as the child could stand up
    /// KidShell.Security.v1 itself and answer questions as the service. The
    /// access list denies instance creation to everyone but SYSTEM, and
    /// starting first is what makes that denial arrive in time.
    /// </summary>
    public const string StartType = "auto";

    /// <summary>
    /// Where the binary must live.
    ///
    /// Under Program Files, which ordinary accounts cannot write. A
    /// LocalSystem service whose image the child can replace is a privilege
    /// escalation with a service name, and no access list on the pipe would
    /// matter.
    /// </summary>
    public static string ExpectedDirectory(string programFilesPath) =>
        string.Join('\\', programFilesPath.TrimEnd('\\', '/'), "KidShell");

    public const string ExecutableName = "KidShell.SecurityHost.exe";

    /// <summary>
    /// Whether a path is somewhere this service may be installed from.
    ///
    /// Checked rather than assumed, because the path reaches the service
    /// control manager and becomes what Windows starts as SYSTEM at every
    /// boot.
    /// </summary>
    public static bool IsAcceptableImagePath(string? path, string programFilesPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(programFilesPath))
        {
            return false;
        }

        if (path.Contains("..", StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            return false;
        }

        if (!path.EndsWith(ExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expected = ExpectedDirectory(programFilesPath);

        return path.StartsWith(expected + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
