using System.Text.Json;
using System.Text.Json.Serialization;
using KidShell.Core.Diagnostics;
using KidShell.Core.Runtime;
using KidShell.Core.Security;
using KidShell.Core.Security.Broker;
using KidShell.Core.Security.Storage;

namespace KidShell.Core.Configuration;

/// <summary>
/// The parent's decisions, as they are stored on the far side of the boundary.
///
/// A projection of <see cref="KidShellConfiguration"/> rather than the whole
/// document: only what <see cref="PolicyDataClassification"/> says must be out
/// of the child's reach. Protecting the chosen avatar would mean an elevation
/// prompt to change a picture, which is how a product teaches people to click
/// through elevation prompts.
/// </summary>
public sealed class ParentPolicyDocument
{
    /// <summary>The only schema this build understands.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<KidAppDefinition> Apps { get; set; } = [];

    public WebSettings Web { get; set; } = new();

    public ScreenTimeSettings ScreenTime { get; set; } = new();

    public ParentPinSettings ParentPin { get; set; } = new();
}

/// <summary>
/// Routes each part of the configuration to the store its trust class
/// requires, and decides what to do when the protected side cannot answer.
///
/// TWO FINDINGS LIVE HERE
/// ----------------------
/// OPSV retest 1 finding 01: the protected store existed and nothing used it.
///
/// OPSV retest 2 finding 02: it was used, and a Ready store with no policy in
/// it silently handed authority back to the child-writable file - PIN, apps,
/// web rules and screen-time limits included. The absence of a policy was read
/// as "nothing to load" when it can equally mean "the policy is gone", and
/// those are not the same fact. See <see cref="ProtectedPolicyTrust"/>.
///
/// WRITING GOES THE OTHER WAY ROUND
/// --------------------------------
/// The store is trustworthy only when the account KidShell runs as CANNOT
/// write to it, so this process does not write there. It asks
/// <see cref="IProtectedStateWriter"/>, which is the unprivileged end of a
/// typed request to the elevated helper. That was OPSV retest 2 finding 01: a
/// store that was Ready precisely when the writer could not use it.
///
/// ORDER OF COMMIT
/// ---------------
/// Authoritative first, personalisation second. It used to be the other way
/// round, so a failed protected write returned false with the child-writable
/// file already changed - a save that reported failure and had half happened.
/// </summary>
public sealed class ProtectedConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IConfigurationStore _profileStore;
    private readonly IProtectedStateReader _reader;
    private readonly IProtectedStateWriter _writer;
    private readonly IRuntimeEnvironment _environment;
    private readonly IKidShellLogger _logger;
    private readonly TimeProvider _time;
    private readonly IParentPolicyApprovalChannel? _approval;

    public ProtectedConfigurationStore(
        IConfigurationStore profileStore,
        IProtectedStateReader reader,
        IProtectedStateWriter writer,
        IRuntimeEnvironment environment,
        IKidShellLogger logger,
        TimeProvider? time = null,
        IParentPolicyApprovalChannel? approval = null)
    {
        _profileStore = profileStore;
        _reader = reader;
        _writer = writer;
        _environment = environment;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _approval = approval;
    }

    public string ConfigurationFilePath => _profileStore.ConfigurationFilePath;

    public ProtectedStoreState Probe() => _reader.Probe();

    public ConfigurationLoadResult Load()
    {
        var load = ProtectedPolicyTrustEvaluator.Evaluate(_reader, _logger);

        if (!load.MayProceed)
        {
            return Refuse(load);
        }

        // Personalisation always comes from the child's own file. It is not
        // security state, and it is the same in every trust state that gets
        // this far.
        var profile = _profileStore.Load();

        if (load.Trust == ProtectedPolicyTrust.GenuineFirstRun)
        {
            // A device that has never been set up. The policy is KidShell's
            // own defaults, NOT whatever happens to be in the child-writable
            // file - reading that here would be the original defect with a
            // first-run label on it.
            _logger.Info("Storage", "Protected storage is ready and this machine has not been provisioned yet.");

            var fresh = profile.Configuration;
            ApplyPolicy(fresh, new ParentPolicyDocument());

            return profile with { Configuration = fresh, Status = ConfigurationLoadStatus.CreatedDefaults };
        }

        ParentPolicyDocument? policy;

        try
        {
            policy = JsonSerializer.Deserialize<ParentPolicyDocument>(load.Document!, Options);
        }
        catch (Exception ex)
        {
            _logger.Error("Storage", "The protected policy document could not be parsed.", ex);
            policy = null;
        }

        if (policy is null)
        {
            return Refuse(new ProtectedPolicyLoad(
                ProtectedPolicyTrust.Corrupt, null, "the protected policy document is not valid JSON"));
        }

        if (policy.SchemaVersion > ParentPolicyDocument.CurrentSchemaVersion)
        {
            return Refuse(new ProtectedPolicyLoad(
                ProtectedPolicyTrust.VersionUnsupported, null,
                $"the protected policy is schema {policy.SchemaVersion}"));
        }

        var merged = profile.Configuration;
        ApplyPolicy(merged, policy);

        return profile with { Configuration = merged };
    }

    /// <summary>
    /// Fails closed.
    ///
    /// In a development build the gate allows carrying on, loudly, and the
    /// ordinary file is used for everything. In production this is a refusal:
    /// defaults, a Failed status, and no authoritative value taken from a file
    /// the child can edit.
    /// </summary>
    private ConfigurationLoadResult Refuse(ProtectedPolicyLoad load)
    {
        var state = new ProtectedStoreState(ProtectedStoreStatus.Unavailable, load.Detail);

        if (ProtectedStoreGate.MayUseUnprotectedStorage(state, _environment.IsDevelopment, _logger))
        {
            _logger.Warning("Storage",
                $"Protected policy is {load.Trust} ({load.Detail}). " +
                "A development build continues on the unprotected file; a shipped build would refuse.");

            return _profileStore.Load();
        }

        _logger.Error("Storage",
            $"Protected policy is {load.Trust} ({load.Detail}). " +
            "KidShell will not take the parent's decisions from storage the child can write.");

        return new ConfigurationLoadResult(
            KidShellConfiguration.CreateDefault(),
            ConfigurationLoadStatus.Failed,
            $"Protected policy is {load.Trust}: {load.Detail}");
    }

    private static void ApplyPolicy(KidShellConfiguration configuration, ParentPolicyDocument policy)
    {
        configuration.Apps = policy.Apps ?? [];
        configuration.Web = policy.Web ?? new WebSettings();
        configuration.ScreenTime = policy.ScreenTime ?? new ScreenTimeSettings();
        configuration.ParentPin = policy.ParentPin ?? new ParentPinSettings();
    }

    public bool Save(KidShellConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var state = _reader.Probe();

        if (!state.IsTrustworthy)
        {
            if (!ProtectedStoreGate.MayUseUnprotectedStorage(state, _environment.IsDevelopment, _logger))
            {
                _logger.Error("Storage",
                    $"Refusing to save: protected storage is {state.Status}. " +
                    "Writing the parent's decisions to the child's own file is not a fallback.");

                return false;
            }

            // Development: the ordinary file holds everything, and the gate has
            // already said so in the log.
            return _profileStore.Save(configuration);
        }

        // ---------------------------------------------------- authoritative
        //
        // First, because a failure here must leave everything unchanged. The
        // opposite order returned false with the child-writable file already
        // rewritten, which is a save that reported failure and had half
        // happened.
        var policy = new ParentPolicyDocument
        {
            Apps = configuration.Apps,
            Web = configuration.Web,
            ScreenTime = configuration.ScreenTime,
            ParentPin = configuration.ParentPin
        };

        var document = JsonSerializer.Serialize(policy, Options);

        var write = _approval is null
            ? _writer.SaveParentPolicy(document)
            : StageAndApprove(document);

        if (!write.Success)
        {
            _logger.Error("Storage",
                $"The parent's policy was not saved ({write.Status}: {write.Detail}). " +
                "Nothing has been changed.");

            return false;
        }

        // A first save on a fresh device also records that the device has been
        // set up, so the next absence of a policy is a missing policy rather
        // than another first run.
        if (string.IsNullOrWhiteSpace(_reader.Read(ProtectedDocument.ProvisioningMarker)))
        {
            var marker = _writer.MarkProvisioned(
                ProtectedPolicyTrustEvaluator.MarkerDocument(_time.GetUtcNow()));

            if (!marker.Success)
            {
                _logger.Warning("Storage",
                    "The provisioning marker could not be written; this machine will look unprovisioned.");
            }
        }

        // ---------------------------------------------------- personalisation
        //
        // Only once the authoritative half is durable. A failure here loses a
        // chosen avatar, which is a far smaller loss than a policy that half
        // committed.
        return _profileStore.Save(configuration);
    }

    /// <summary>
    /// Proposes the policy, then asks for the authority to make it live.
    ///
    /// PRIVILEGED BROKER HARDENING. The write that used to happen here was
    /// authorized by nothing but the fact that KidShell asked for it, and
    /// KidShell runs as the child. Now the child's session can only propose:
    /// the staged document goes to a slot nothing enforces, and an elevated
    /// administrator turns it into the policy.
    ///
    /// The digest travels between the two steps so that what was approved
    /// and what becomes live are provably the same bytes. Without it a
    /// modified child process could stage something harmless, wait for the
    /// parent to answer the prompt, and replace it in between.
    /// </summary>
    private ProtectedWriteResult StageAndApprove(string document)
    {
        var staged = _writer.StageParentPolicy(document);

        if (!staged.IsStaged)
        {
            return new ProtectedWriteResult(staged.Status, staged.Detail);
        }

        var approval = _approval!.Approve(staged.Digest);

        if (!approval.Success)
        {
            // Nothing authoritative changed, and the staged slot is not read
            // by anything. A refused approval therefore leaves the machine
            // exactly as it was, which is what the caller is told.
            _logger.Warning("Storage",
                $"A parent policy was staged and not approved ({approval.Status}).");
        }

        return approval;
    }
}
