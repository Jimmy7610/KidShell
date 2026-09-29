using System.Text.Json;
using KidShell.Core.Diagnostics;

namespace KidShell.Core.Security.Storage;

/// <summary>
/// What is known about the protected policy on this machine.
///
/// OPSV RETEST 2, FINDING 02. A Ready store with no policy in it was treated as
/// "nothing to load", and the child-writable JSON beside it became
/// authoritative — including the parent's PIN, the approved apps and the
/// screen-time rules. A hostile file in the child's own profile was therefore
/// enough to replace every rule that file was supposed to be protected from.
///
/// The mistake underneath was inferring a first run from an absence. "This
/// device has never been set up" and "the policy that was here is gone" are
/// the same absence and completely different facts, and only the first of them
/// may initialise anything.
/// </summary>
public enum ProtectedPolicyTrust
{
    /// <summary>
    /// The store is ready and this machine has never been provisioned.
    ///
    /// The ONLY state in which a new protected policy may be created. It is
    /// established by the absence of the provisioning marker, which is itself
    /// protected — a child cannot manufacture this state by deleting a file in
    /// their own profile.
    /// </summary>
    GenuineFirstRun = 0,

    /// <summary>A policy is present and readable. The ordinary case.</summary>
    ExistingProtectedPolicy = 1,

    /// <summary>
    /// The machine is provisioned and the policy is gone.
    ///
    /// Someone removed it, or the write that should have created it failed.
    /// Either way the parent's decisions are unknown, and unknown is not the
    /// same as none.
    /// </summary>
    MissingButExpected = 2,

    /// <summary>The document exists and could not be read.</summary>
    ReadFailed = 3,

    /// <summary>The document was read and is not valid.</summary>
    Corrupt = 4,

    /// <summary>The store cannot be reached, or the child could write to it.</summary>
    AccessDenied = 5,

    /// <summary>A schema this build does not understand.</summary>
    VersionUnsupported = 6
}

/// <summary>What was found, and the document when there is one.</summary>
public sealed record ProtectedPolicyLoad(
    ProtectedPolicyTrust Trust,
    string? Document = null,
    string Detail = "")
{
    /// <summary>
    /// Whether the product may carry on using authoritative policy.
    ///
    /// Two states only. Everything else is a question with no answer, and a
    /// security product that guesses at those is worse than one that stops.
    /// </summary>
    public bool MayProceed =>
        Trust is ProtectedPolicyTrust.GenuineFirstRun or ProtectedPolicyTrust.ExistingProtectedPolicy;
}

/// <summary>
/// Establishes which of the trust states this machine is in.
///
/// Deliberately separate from the store that reads bytes: this is the
/// reasoning, and it is the part that has to be right.
/// </summary>
public static class ProtectedPolicyTrustEvaluator
{
    /// <summary>The marker's schema, so a future format is recognised rather than misread.</summary>
    public const int MarkerSchemaVersion = 1;

    public static ProtectedPolicyLoad Evaluate(IProtectedStateReader reader, IKidShellLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var state = reader.Probe();

        if (!state.IsTrustworthy)
        {
            // Includes PermissionsWrong, which is the dangerous one: the store
            // is THERE, so everything looks provisioned, and the child can
            // write it.
            return new ProtectedPolicyLoad(
                ProtectedPolicyTrust.AccessDenied, null, $"{state.Status}: {state.Detail}");
        }

        string? marker;
        string? policy;

        try
        {
            marker = reader.Read(ProtectedDocument.ProvisioningMarker);
            policy = reader.Read(ProtectedDocument.ParentPolicy);
        }
        catch (Exception ex)
        {
            logger?.Error("Storage", "The protected store could not be read.", ex);
            return new ProtectedPolicyLoad(ProtectedPolicyTrust.ReadFailed, null, ex.GetType().Name);
        }

        var provisioned = IsProvisioned(marker, logger);

        if (!string.IsNullOrWhiteSpace(policy))
        {
            return new ProtectedPolicyLoad(ProtectedPolicyTrust.ExistingProtectedPolicy, policy);
        }

        // No policy. Which absence is it?
        return provisioned
            ? new ProtectedPolicyLoad(
                ProtectedPolicyTrust.MissingButExpected,
                null,
                "the machine is provisioned and the parent policy is not there")
            : new ProtectedPolicyLoad(ProtectedPolicyTrust.GenuineFirstRun);
    }

    /// <summary>
    /// Whether the marker says this machine has been set up.
    ///
    /// A malformed marker counts as provisioned. The alternative would make
    /// damaging one small file a way to reach first-run initialisation, which
    /// is the shape of the bug this whole type exists to close.
    /// </summary>
    private static bool IsProvisioned(string? marker, IKidShellLogger? logger)
    {
        if (string.IsNullOrWhiteSpace(marker))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(marker);

            return document.RootElement.TryGetProperty("provisioned", out var value)
                   && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            logger?.Warning("Storage",
                "The provisioning marker is unreadable; treating this machine as provisioned.", ex);

            return true;
        }
    }

    /// <summary>The marker's contents. Small on purpose: it records a fact, not a state.</summary>
    public static string MarkerDocument(DateTimeOffset whenUtc) =>
        $$"""{"schemaVersion":{{MarkerSchemaVersion}},"provisioned":true,"provisionedUtc":"{{whenUtc:O}}"}""";
}
