using System.Text.Json;
using System.Text.Json.Serialization;
using KidShell.Core.Diagnostics;
using KidShell.Core.Runtime;
using KidShell.Core.Security;
using KidShell.Core.Security.Storage;

namespace KidShell.Core.Configuration;

/// <summary>
/// The parent's decisions, as they are stored on the far side of the boundary.
///
/// A projection of <see cref="KidShellConfiguration"/> rather than the whole
/// document: only what <see cref="PolicyDataClassification"/> says must be out
/// of the child's reach. Protecting the chosen avatar would mean a UAC prompt
/// to change a picture, which is how a product teaches people to click through
/// UAC prompts.
/// </summary>
public sealed class ParentPolicyDocument
{
    public int SchemaVersion { get; set; } = 1;

    public List<KidAppDefinition> Apps { get; set; } = [];

    public WebSettings Web { get; set; } = new();

    public ScreenTimeSettings ScreenTime { get; set; } = new();

    public ParentPinSettings ParentPin { get; set; } = new();
}

/// <summary>
/// OPSV FINDING 01 — the protected store existed and nothing used it.
///
/// ProtectedStorePlan, ProtectedStoreGate and PolicyDataClassification were
/// all written, tested and registered nowhere. The production application went
/// on reading the parent's PIN, the approved apps and the screen-time policy
/// out of one JSON file in the signed-in user's own LocalState - which, in the
/// shipped architecture, is the child's profile. A standard user has full
/// control of their own profile. The design was right and the wiring was
/// absent, which is the least useful combination: it reads as solved.
///
/// WHAT THIS DOES
/// --------------
/// It routes each part of the configuration to the store its trust class
/// requires, and it is the single place that decides what to do when the
/// protected store is not usable.
///
///   parent policy      the protected store - apps, web, screen-time
///                      settings and the PIN material
///   child profile      the ordinary per-user file, deliberately - name,
///                      avatar, theme, age
///
/// WHEN THE PROTECTED STORE IS NOT READY
/// -------------------------------------
/// ProtectedStoreGate answers, in one place, and the answer differs by build:
///
///   development   fall back to the unprotected file, loudly
///   production    refuse
///
/// Refusing means the configuration loads as defaults with a Failed status and
/// nothing security-authoritative is written back. That is deliberately a
/// visible failure rather than a quiet degradation: a parent who was told
/// their child's settings were protected must not be silently returned to a
/// build where they are not.
/// </summary>
public sealed class ProtectedConfigurationStore : IConfigurationStore
{
    /// <summary>The document name inside the protected store.</summary>
    public const string PolicyDocumentName = "parent-policy.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IConfigurationStore _profileStore;
    private readonly IProtectedPolicyStore _protectedStore;
    private readonly IRuntimeEnvironment _environment;
    private readonly IKidShellLogger _logger;

    public ProtectedConfigurationStore(
        IConfigurationStore profileStore,
        IProtectedPolicyStore protectedStore,
        IRuntimeEnvironment environment,
        IKidShellLogger logger)
    {
        _profileStore = profileStore;
        _protectedStore = protectedStore;
        _environment = environment;
        _logger = logger;
    }

    public string ConfigurationFilePath => _profileStore.ConfigurationFilePath;

    /// <summary>The store's own view of whether it can be trusted.</summary>
    public ProtectedStoreState Probe() => _protectedStore.Probe();

    public ConfigurationLoadResult Load()
    {
        var state = _protectedStore.Probe();
        var profile = _profileStore.Load();

        if (state.IsTrustworthy)
        {
            return MergePolicy(profile, state);
        }

        if (ProtectedStoreGate.MayUseUnprotectedStorage(state, _environment.IsDevelopment, _logger))
        {
            // A development build, told plainly that nothing here is real.
            return profile;
        }

        // Production, with no usable boundary. The parent's policy is not
        // loaded from the child's own file as a consolation prize.
        return new ConfigurationLoadResult(
            KidShellConfiguration.CreateDefault(),
            ConfigurationLoadStatus.Failed,
            $"Protected policy storage is {state.Status}: {state.Detail}. " +
            "KidShell will not read the parent's settings from storage the child can write.");
    }

    private ConfigurationLoadResult MergePolicy(ConfigurationLoadResult profile, ProtectedStoreState state)
    {
        var document = _protectedStore.Read(PolicyDocumentName);

        if (string.IsNullOrWhiteSpace(document))
        {
            // The store is there and holds no policy yet. That is a machine
            // provisioned but not yet configured, which is a real state on a
            // fresh dedicated device - not a failure.
            _logger.Info("Storage", $"Protected policy storage is ready at {state.Detail}; no policy written yet.");
            return profile;
        }

        ParentPolicyDocument? policy;

        try
        {
            policy = JsonSerializer.Deserialize<ParentPolicyDocument>(document, Options);
        }
        catch (Exception ex)
        {
            policy = null;
            _logger.Error("Storage", "The protected policy document could not be parsed.", ex);
        }

        if (policy is null)
        {
            // Malformed protected data does NOT silently become "no policy".
            // Falling back to the child-writable copy here would mean damaging
            // one file was enough to remove the protection from all of it.
            return new ConfigurationLoadResult(
                KidShellConfiguration.CreateDefault(),
                ConfigurationLoadStatus.Failed,
                "The protected policy document is unreadable. It is not replaced by the unprotected copy.");
        }

        var merged = profile.Configuration;

        merged.Apps = policy.Apps ?? [];
        merged.Web = policy.Web ?? new WebSettings();
        merged.ScreenTime = policy.ScreenTime ?? new ScreenTimeSettings();
        merged.ParentPin = policy.ParentPin ?? new ParentPinSettings();

        return profile with { Configuration = merged };
    }

    public bool Save(KidShellConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var state = _protectedStore.Probe();

        if (!state.IsTrustworthy &&
            !ProtectedStoreGate.MayUseUnprotectedStorage(state, _environment.IsDevelopment, _logger))
        {
            _logger.Error("Storage",
                $"Refusing to save: protected policy storage is {state.Status}. " +
                "Writing the parent's decisions to the child's own file is not a fallback.");

            return false;
        }

        // The child's own choices always go to the ordinary file. They are not
        // security state and protecting them would cost an elevation prompt to
        // change an avatar.
        if (!_profileStore.Save(configuration))
        {
            return false;
        }

        if (!state.IsTrustworthy)
        {
            // Development: the profile store already holds everything,
            // including the policy, and the gate has said so in the log.
            return true;
        }

        var policy = new ParentPolicyDocument
        {
            Apps = configuration.Apps,
            Web = configuration.Web,
            ScreenTime = configuration.ScreenTime,
            ParentPin = configuration.ParentPin
        };

        if (_protectedStore.Write(PolicyDocumentName, JsonSerializer.Serialize(policy, Options)))
        {
            return true;
        }

        _logger.Error("Storage",
            "The parent's policy could not be written to protected storage. " +
            "This normally means the change was made without the parent's elevation.");

        return false;
    }
}
