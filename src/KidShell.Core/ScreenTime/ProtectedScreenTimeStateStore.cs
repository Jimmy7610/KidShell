using System.Text.Json;
using System.Text.Json.Serialization;
using KidShell.Core.Diagnostics;
using KidShell.Core.Runtime;
using KidShell.Core.Security.Storage;

namespace KidShell.Core.ScreenTime;

/// <summary>
/// The screen-time counter, kept where the child cannot edit it.
///
/// WHY THE COUNTER IS SECURITY STATE
/// ---------------------------------
/// PolicyDataClassification classes it as EnforcementState, with the
/// consequence written next to it: "Reset today's counter and carry on." It
/// changes every thirty seconds and the parent's settings change once a month,
/// which is why it lives in its own file - but a file in the child's own
/// profile is a file the child owns, and deleting it used to be an afternoon.
///
/// Finding 04 made an unreadable counter fail safe rather than reset. That
/// closes the damage; this closes the door.
///
/// THE SAME RULES AS THE POLICY STORE
/// ----------------------------------
/// Production with no usable protected store refuses; development falls back
/// to the ordinary file, loudly. Both decisions go through
/// <see cref="ProtectedStoreGate"/> so there is one answer rather than two
/// that can drift.
/// </summary>
public sealed class ProtectedScreenTimeStateStore : IScreenTimeStateStore
{
    /// <summary>The document name inside the protected store.</summary>
    public const string DocumentName = "screen-time-state.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IScreenTimeStateStore _fallback;
    private readonly IProtectedPolicyStore _protectedStore;
    private readonly IRuntimeEnvironment _environment;
    private readonly IKidShellLogger _logger;

    public ProtectedScreenTimeStateStore(
        IScreenTimeStateStore fallback,
        IProtectedPolicyStore protectedStore,
        IRuntimeEnvironment environment,
        IKidShellLogger logger)
    {
        _fallback = fallback;
        _protectedStore = protectedStore;
        _environment = environment;
        _logger = logger;
    }

    public ScreenTimeStateLoad Load()
    {
        var state = _protectedStore.Probe();

        if (!state.IsTrustworthy)
        {
            if (ProtectedStoreGate.MayUseUnprotectedStorage(state, _environment.IsDevelopment, _logger))
            {
                return _fallback.Load();
            }

            // Production with no boundary. Reading the counter out of a file
            // the child controls would be worse than not reading one, because
            // the number would be believed.
            return new ScreenTimeStateLoad(
                new ScreenTimeState(),
                ScreenTimeLoadOutcome.Unreadable,
                $"protected storage is {state.Status}");
        }

        var document = _protectedStore.Read(DocumentName);

        if (string.IsNullOrWhiteSpace(document))
        {
            // Provisioned, and nothing counted yet. A genuine first run on a
            // prepared machine.
            return ScreenTimeStateLoad.FirstRun();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ScreenTimeState>(document, Options);

            if (parsed is null || parsed.SchemaVersion > ScreenTimeState.CurrentSchemaVersion)
            {
                return Unreadable("the protected counter is not in a form this build understands");
            }

            parsed.UsedSeconds = Math.Max(0, parsed.UsedSeconds);
            parsed.BonusMinutes = Math.Max(0, parsed.BonusMinutes);

            return new ScreenTimeStateLoad(parsed, ScreenTimeLoadOutcome.Primary);
        }
        catch (Exception ex)
        {
            _logger.Warning("ScreenTime", "The protected screen-time counter could not be read.", ex);
            return Unreadable("the protected counter could not be parsed");
        }
    }

    /// <summary>
    /// Unknown usage, not zero usage.
    ///
    /// The engine treats this as a spent day. Damaging the file must not be a
    /// way to earn an afternoon, which is the whole of finding 04 applied to
    /// the protected copy as well.
    /// </summary>
    private static ScreenTimeStateLoad Unreadable(string detail) =>
        new(new ScreenTimeState(), ScreenTimeLoadOutcome.Unreadable, detail);

    public bool Save(ScreenTimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var probe = _protectedStore.Probe();

        if (!probe.IsTrustworthy)
        {
            return ProtectedStoreGate.MayUseUnprotectedStorage(probe, _environment.IsDevelopment, _logger)
                && _fallback.Save(state);
        }

        return _protectedStore.Write(DocumentName, JsonSerializer.Serialize(state, Options));
    }
}
