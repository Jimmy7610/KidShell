using KidShell.Core.Security.Broker;
using KidShell.Core.Security.Storage;

namespace KidShell.Core.Security.Validation;

/// <summary>
/// What a script actually observed about the protected store's permissions.
///
/// EFFECTIVE RIGHTS, NOT THE TEXT OF AN ACL
/// ----------------------------------------
/// The difference matters enough to be in the type's name. An access list can
/// read correctly and grant something else: an inherited entry nobody noticed,
/// a group the child happens to be in, CREATOR OWNER on a directory the child
/// created. So the gatherer resolves what each principal can actually do and
/// records that, and a real write probe from the child's own session is what
/// confirms it - see <see cref="ProtectedStoreProbeOutcome"/>.
/// </summary>
public sealed record ProtectedStoreObservation
{
    public required string Directory { get; init; }

    public required bool Exists { get; init; }

    public string Owner { get; init; } = string.Empty;

    /// <summary>Whether inherited permissions have been removed.</summary>
    public bool InheritanceRemoved { get; init; }

    /// <summary>The resolved rights per principal.</summary>
    public IReadOnlyDictionary<ProtectedStorePrincipal, ProtectedStoreRights> Effective { get; init; }
        = new Dictionary<ProtectedStorePrincipal, ProtectedStoreRights>();

    /// <summary>The raw access list, for the evidence file and a human.</summary>
    public IReadOnlyList<string> AccessEntries { get; init; } = [];
}

/// <summary>What a real write attempt from the child's session did.</summary>
/// <param name="Operation">create, overwrite, append, rename or delete.</param>
/// <param name="Denied">
/// True when Windows refused it. The only acceptable answer for authoritative
/// state, and the only one that is evidence rather than inference.
/// </param>
public sealed record ProtectedStoreProbeOutcome(string Operation, bool Denied, string Detail = "");

/// <summary>
/// Compares an observation against <see cref="ProtectedStorePlan"/>.
///
/// The plan is what KidShell says the store should be. This is what decides
/// whether the machine agrees, and it is deliberately separate from the code
/// that gathers the facts: the rules are the part worth testing, and they do
/// not need a machine to be tested on.
/// </summary>
public static class ProtectedStoreExpectation
{
    /// <summary>
    /// The operations a child must be refused, in the order they are probed.
    ///
    /// All five, because they are five different Windows permissions and a
    /// store that refuses a create and allows a delete is not protected.
    /// Append in particular is the one people forget: write-denied and
    /// append-allowed is enough to corrupt a JSON document into unreadable,
    /// which this product treats as a reason to fail closed - so a child could
    /// turn "I may not change the policy" into "there is no usable policy".
    /// </summary>
    public static readonly string[] RequiredDenials =
        ["create", "overwrite", "append", "rename", "delete"];

    public static ValidationFinding[] Compare(
        ProtectedStorePlan plan,
        ProtectedStoreObservation observed,
        IReadOnlyList<ProtectedStoreProbeOutcome> probes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(probes);

        var findings = new List<ValidationFinding>();

        if (!observed.Exists)
        {
            findings.Add(ValidationFinding.NotRun(
                $"{observed.Directory} does not exist, so its permissions cannot be checked."));

            return [.. findings];
        }

        if (!observed.InheritanceRemoved)
        {
            // ProgramData grants CREATOR OWNER full control of what it
            // creates. A store that kept inheritance would be fully
            // controlled by whoever wrote it - on a locked-down machine,
            // possibly the child.
            findings.Add(ValidationFinding.Fail(
                "Inherited permissions are still in place on the protected store."));
        }

        foreach (var expected in plan.Entries)
        {
            var actual = observed.Effective.GetValueOrDefault(expected.Principal, ProtectedStoreRights.None);

            if (actual == expected.Rights)
            {
                continue;
            }

            var extra = actual & ~expected.Rights;

            // Extra rights are a failure; missing ones can be, so both are
            // reported, and which it is goes in the sentence.
            findings.Add(extra != ProtectedStoreRights.None
                ? ValidationFinding.Fail(
                    $"{expected.Principal} has {extra} beyond the plan's {expected.Rights}.")
                : ValidationFinding.Fail(
                    $"{expected.Principal} has {actual}, and the plan expects {expected.Rights}."));
        }

        // Nobody the plan does not mention may have anything.
        foreach (var (principal, rights) in observed.Effective)
        {
            if (rights == ProtectedStoreRights.None)
            {
                continue;
            }

            if (!plan.Entries.Any(e => e.Principal == principal))
            {
                findings.Add(ValidationFinding.Fail(
                    $"{principal} has {rights} and the plan gives it nothing."));
            }
        }

        foreach (var required in RequiredDenials)
        {
            var probe = probes.FirstOrDefault(
                p => string.Equals(p.Operation, required, StringComparison.OrdinalIgnoreCase));

            if (probe is null)
            {
                findings.Add(ValidationFinding.NotRun(
                    $"The child's '{required}' attempt was not probed, so the denial is inference."));
            }
            else if (!probe.Denied)
            {
                findings.Add(ValidationFinding.Fail(
                    $"The child's '{required}' attempt SUCCEEDED. {probe.Detail}".TrimEnd()));
            }
        }

        // A completely correct observation used to return an empty findings
        // array. ValidationStage deliberately treats an empty stage as NOT RUN,
        // so real hardware with the exact planned ACL and all five child
        // denials observed could never produce PASS. Record an explicit pass
        // only when there is literally nothing left to report.
        if (findings.Count == 0)
        {
            findings.Add(ValidationFinding.Pass(
                "The protected store matches the plan and all required child write/delete attempts were refused."));
        }

        return [.. findings];
    }
}

/// <summary>What a script observed about the security service.</summary>
public sealed record SecurityHostObservation
{
    public required bool Installed { get; init; }

    public bool Running { get; init; }

    /// <summary>As the service control manager spells it: Automatic, Manual, Disabled.</summary>
    public string StartType { get; init; } = string.Empty;

    /// <summary>The account the service logs on as.</summary>
    public string Account { get; init; } = string.Empty;

    /// <summary>The full image path, arguments included, as registered.</summary>
    public string ImagePath { get; init; } = string.Empty;

    public bool ImageExists { get; init; }

    /// <summary>SHA-256 of the binary, for the evidence file.</summary>
    public string ImageSha256 { get; init; } = string.Empty;

    /// <summary>Whether the child's account can write the image. Must be false.</summary>
    public bool ImageChildWritable { get; init; } = true;

    /// <summary>Whether a restart action is configured in the SCM.</summary>
    public bool RecoveryConfigured { get; init; }

    /// <summary>Whether the child was actually refused when it tried to stop it.</summary>
    public bool? ChildStopDenied { get; init; }

    /// <summary>Whether the child was actually refused when it tried to reconfigure it.</summary>
    public bool? ChildReconfigureDenied { get; init; }
}

/// <summary>
/// Compares an observation against <see cref="SecurityHostService"/>.
///
/// The product states what the service must be. This checks the machine says
/// the same thing, and refuses to treat silence as agreement: an unprobed
/// denial is NOT RUN, never PASS.
/// </summary>
public static class SecurityHostExpectation
{
    public static ValidationFinding[] Compare(
        SecurityHostObservation observed, string installRoot)
    {
        ArgumentNullException.ThrowIfNull(observed);

        var findings = new List<ValidationFinding>();

        if (!observed.Installed)
        {
            findings.Add(ValidationFinding.NotRun(
                $"The {SecurityHostService.Name} service is not installed."));

            return [.. findings];
        }

        if (!observed.Running)
        {
            // Worse than missing: the product would look configured and every
            // protected write would fail.
            findings.Add(ValidationFinding.Fail("The service is installed and not running."));
        }

        if (!string.Equals(observed.StartType, "Automatic", StringComparison.OrdinalIgnoreCase))
        {
            // A named pipe belongs to whoever creates it first, so the service
            // has to be there before anyone signs in.
            findings.Add(ValidationFinding.Fail(
                $"The start type is {observed.StartType} and must be Automatic."));
        }

        if (!IsLocalSystem(observed.Account))
        {
            findings.Add(ValidationFinding.Fail(
                $"The service runs as '{observed.Account}' and must run as {SecurityHostService.Account}."));
        }

        var image = ExecutableFrom(observed.ImagePath);

        if (!SecurityHostService.IsAcceptableImagePath(image, ProgramFilesFrom(installRoot)))
        {
            findings.Add(ValidationFinding.Fail(
                $"The image path '{image}' is not under the protected install root."));
        }

        if (!observed.ImageExists)
        {
            findings.Add(ValidationFinding.Fail("The registered image does not exist."));
        }

        if (observed.ImageChildWritable)
        {
            // A LocalSystem service whose binary the child can replace is a
            // privilege escalation with a service name, and no access list on
            // the pipe would matter.
            findings.Add(ValidationFinding.Fail("The child's account can write the service binary."));
        }

        if (HasUnexpectedArguments(observed.ImagePath))
        {
            findings.Add(ValidationFinding.Fail(
                $"The registration carries arguments: '{observed.ImagePath}'. " +
                "The service takes none, and anything there changes what SYSTEM runs at boot."));
        }

        if (!observed.RecoveryConfigured)
        {
            findings.Add(ValidationFinding.Warn(
                "No SCM restart action is configured. The product fails closed without one, " +
                "so this is a resilience gap rather than a hole."));
        }

        findings.Add(observed.ChildStopDenied switch
        {
            true => ValidationFinding.Pass("The child was refused when it tried to stop the service."),
            false => ValidationFinding.Fail("The child STOPPED the service."),
            null => ValidationFinding.NotRun("The child's stop attempt was not probed.")
        });

        findings.Add(observed.ChildReconfigureDenied switch
        {
            true => ValidationFinding.Pass("The child was refused when it tried to reconfigure the service."),
            false => ValidationFinding.Fail("The child RECONFIGURED the service."),
            null => ValidationFinding.NotRun("The child's reconfigure attempt was not probed.")
        });

        return [.. findings];
    }

    private static bool IsLocalSystem(string account) =>
        account.Trim() is "LocalSystem" or "localSystem" ||
        string.Equals(account.Trim(), @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The executable out of a registration that may be quoted and may carry
    /// arguments.
    /// </summary>
    public static string ExecutableFrom(string imagePath)
    {
        var value = (imagePath ?? string.Empty).Trim();

        if (value.StartsWith('"'))
        {
            var closing = value.IndexOf('"', 1);
            return closing > 0 ? value[1..closing] : value.Trim('"');
        }

        var exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);

        return exe >= 0 ? value[..(exe + 4)] : value;
    }

    /// <summary>
    /// Whether the registration carries anything after the executable.
    ///
    /// The service takes no arguments, so anything there changes what Windows
    /// runs as SYSTEM at every boot - and an unquoted path with a space is the
    /// classic way that becomes somebody else's program.
    /// </summary>
    private static bool HasUnexpectedArguments(string? imagePath)
    {
        var value = (imagePath ?? string.Empty).Trim();

        if (value.Length == 0)
        {
            return false;
        }

        var quoted = value.StartsWith('"');
        var consumed = ExecutableFrom(value).Length + (quoted ? 2 : 0);

        return value.Length > consumed;
    }

    /// <summary>
    /// The Program Files root an install root sits under.
    ///
    /// The expectation is expressed against Program Files rather than the
    /// install root itself, so a config pointing somewhere unprotected fails
    /// the path check rather than redefining what protected means.
    /// </summary>
    private static string ProgramFilesFrom(string installRoot)
    {
        var value = (installRoot ?? string.Empty).TrimEnd('\\', '/');
        var lastSlash = value.LastIndexOf('\\');

        return lastSlash > 2 ? value[..lastSlash] : value;
    }
}
