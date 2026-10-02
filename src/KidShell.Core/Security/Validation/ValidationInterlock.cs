namespace KidShell.Core.Security.Validation;

/// <summary>One reason a validation run is not allowed to mutate this machine.</summary>
public enum InterlockRefusal
{
    /// <summary>No config file, or one that does not parse.</summary>
    ConfigMissing = 0,

    /// <summary>The config is incomplete or self-contradictory.</summary>
    ConfigIncomplete = 1,

    /// <summary>The config does not say this is a dedicated test device.</summary>
    NotDeclaredDedicated = 2,

    /// <summary>The config names a different machine than the one running.</summary>
    MachineNameMismatch = 3,

    /// <summary>The on-disk dedicated-device marker is absent.</summary>
    MarkerFileMissing = 4,

    /// <summary>The operator did not type the confirmation phrase, or typed it wrongly.</summary>
    ConfirmationPhraseWrong = 5,

    /// <summary>The process is not elevated.</summary>
    NotElevated = 6,

    /// <summary>
    /// This build identifies itself as a development build.
    ///
    /// Separate from every other condition because it is the one that travels
    /// with the code rather than with the machine: a developer's working copy
    /// must not be able to apply real lockdown even on a machine that is
    /// genuinely expendable.
    /// </summary>
    DevelopmentBuild = 7,

    /// <summary>The machine looks like somebody's actual computer.</summary>
    LooksLikeAWorkingMachine = 8
}

/// <summary>What the interlock could see when it decided.</summary>
public sealed record InterlockFacts
{
    public required string MachineName { get; init; }

    public required bool IsElevated { get; init; }

    /// <summary>Whether the marker file exists at the one path that counts.</summary>
    public required bool MarkerFilePresent { get; init; }

    /// <summary>What the operator typed, verbatim.</summary>
    public string ConfirmationPhrase { get; init; } = string.Empty;

    /// <summary>Whether this build calls itself a development build.</summary>
    public required bool IsDevelopmentBuild { get; init; }

    /// <summary>
    /// Signs that this is somebody's real computer rather than a test rig.
    ///
    /// Not authoritative and not a substitute for the marker - a clean test
    /// machine could have a domain, and a sacrificial one could have a user
    /// profile. It is a last line against the case the other conditions
    /// cannot catch: a correctly prepared config and marker copied onto the
    /// wrong computer by somebody in a hurry.
    /// </summary>
    public IReadOnlyList<string> WorkingMachineSigns { get; init; } = [];
}

/// <summary>Whether the run may proceed, and everything stopping it.</summary>
public sealed record InterlockDecision(bool Allowed, IReadOnlyList<InterlockRefusal> Refusals)
{
    /// <summary>A sentence per refusal, for the operator rather than the log.</summary>
    public IReadOnlyList<string> Explain() =>
        [.. Refusals.Select(ValidationInterlock.Explain)];
}

/// <summary>
/// The gate in front of every script that could change the machine under test.
///
/// WHY IT IS NOT A SWITCH
/// ----------------------
/// A single <c>-Force</c> is one typo, one copied command line, or one
/// half-remembered example away from applying AppLocker to a family computer.
/// So nothing here is a switch. Every condition below has to be true at the
/// same time, and they are deliberately of different KINDS - a file on the
/// machine, a phrase the operator types, a declaration in a config, the
/// machine's own name, the process's token, and the build's own identity.
///
/// Getting all six wrong at once is not something a stray flag can do. That is
/// the entire design.
///
/// NOTHING HERE CHANGES ANYTHING EITHER. It answers a question; the caller
/// decides what to do with the answer, and the mutating scripts default to a
/// dry run even once it says yes.
/// </summary>
public static class ValidationInterlock
{
    /// <summary>
    /// The one path the marker is read from.
    ///
    /// Under ProgramData rather than the repository, because the marker is a
    /// statement about the MACHINE. A marker that travelled with a checkout
    /// would be a marker that arrives on every machine the checkout does.
    /// </summary>
    public const string MarkerDirectory = @"C:\ProgramData\KidShell-TestDevice";

    public const string MarkerFileName = "ALLOW-KIDSHELL-VALIDATION.txt";

    public static string MarkerPath => MarkerDirectory + "\\" + MarkerFileName;

    /// <summary>
    /// What the operator has to type, exactly.
    ///
    /// Long and awkward on purpose: it cannot be a reflex, and it cannot be
    /// answered by pressing return.
    /// </summary>
    public const string ConfirmationPhrase = "I UNDERSTAND THIS IS A DEDICATED KIDSHELL TEST DEVICE";

    public static InterlockDecision Evaluate(DeviceValidationConfig? config, InterlockFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var refusals = new List<InterlockRefusal>();

        if (config is null)
        {
            refusals.Add(InterlockRefusal.ConfigMissing);
        }
        else
        {
            if (!config.IsComplete)
            {
                refusals.Add(InterlockRefusal.ConfigIncomplete);
            }

            if (!config.DedicatedTestDevice)
            {
                refusals.Add(InterlockRefusal.NotDeclaredDedicated);
            }

            if (!string.Equals(config.MachineName.Trim(), facts.MachineName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                refusals.Add(InterlockRefusal.MachineNameMismatch);
            }
        }

        if (!facts.MarkerFilePresent)
        {
            refusals.Add(InterlockRefusal.MarkerFileMissing);
        }

        // Ordinal, and the whole string. Trimmed only for surrounding
        // whitespace, because a terminal paste often carries some.
        if (!string.Equals(facts.ConfirmationPhrase?.Trim(), ConfirmationPhrase, StringComparison.Ordinal))
        {
            refusals.Add(InterlockRefusal.ConfirmationPhraseWrong);
        }

        if (!facts.IsElevated)
        {
            refusals.Add(InterlockRefusal.NotElevated);
        }

        if (facts.IsDevelopmentBuild)
        {
            refusals.Add(InterlockRefusal.DevelopmentBuild);
        }

        if (facts.WorkingMachineSigns.Count > 0)
        {
            refusals.Add(InterlockRefusal.LooksLikeAWorkingMachine);
        }

        return new InterlockDecision(refusals.Count == 0, refusals);
    }

    /// <summary>The marker's contents. A sentence, so an accidental file is not one.</summary>
    public static string MarkerTemplate(string machineName, DateTimeOffset whenUtc) =>
        $"""
        This machine is a dedicated KidShell validation device.

        Everything on it may be destroyed by KidShell's security validation:
        accounts, policy, the shell, the ability to sign in.

        Machine: {machineName}
        Declared: {whenUtc:u}

        Delete this file to stop the validation tooling from running here.
        """;

    public static string Explain(InterlockRefusal refusal) => refusal switch
    {
        InterlockRefusal.ConfigMissing =>
            "There is no validation config, so nothing has been declared about this machine.",

        InterlockRefusal.ConfigIncomplete =>
            "The validation config is incomplete. Run the preflight for the specific problems.",

        InterlockRefusal.NotDeclaredDedicated =>
            "The config does not say DedicatedTestDevice = true.",

        InterlockRefusal.MachineNameMismatch =>
            "The config was written for a different machine than this one.",

        InterlockRefusal.MarkerFileMissing =>
            $"There is no dedicated-device marker at {MarkerPath}.",

        InterlockRefusal.ConfirmationPhraseWrong =>
            "The confirmation phrase was not typed exactly.",

        InterlockRefusal.NotElevated =>
            "This window is not elevated.",

        InterlockRefusal.DevelopmentBuild =>
            "This is a development build. A working copy does not apply real lockdown, " +
            "whatever the machine says about itself.",

        InterlockRefusal.LooksLikeAWorkingMachine =>
            "This machine shows signs of being somebody's actual computer. " +
            "If that is wrong, the signs are listed in the preflight evidence - read them before overriding anything.",

        _ => "Refused."
    };
}
