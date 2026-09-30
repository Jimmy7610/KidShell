using System.Text.Json;
using System.Text.Json.Serialization;
using KidShell.Core.Diagnostics;
using KidShell.Core.Runtime;
using KidShell.Core.Security.Storage;

namespace KidShell.Core.ScreenTime;

/// <summary>
/// The screen-time counter, read from the protected store and written through
/// the privileged broker.
///
/// The counter is enforcement state, not personalisation: a child who can
/// reset it gets an unlimited day. PolicyDataClassification says so, with the
/// consequence written beside it.
///
/// READ HERE, WRITTEN THERE
/// ------------------------
/// This is the same split as the policy store, for the same reason. The
/// protected directory is trustworthy only when the account KidShell runs as
/// cannot write to it, so a version of this class that wrote directly could
/// only work on a machine where the boundary did not hold. That was OPSV
/// retest 2 finding 01.
/// </summary>
public sealed class ProtectedScreenTimeStateStore : IScreenTimeStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IScreenTimeStateStore _fallback;
    private readonly IProtectedStateReader _reader;
    private readonly IProtectedStateWriter _writer;
    private readonly IRuntimeEnvironment _environment;
    private readonly IKidShellLogger _logger;

    public ProtectedScreenTimeStateStore(
        IScreenTimeStateStore fallback,
        IProtectedStateReader reader,
        IProtectedStateWriter writer,
        IRuntimeEnvironment environment,
        IKidShellLogger logger)
    {
        _fallback = fallback;
        _reader = reader;
        _writer = writer;
        _environment = environment;
        _logger = logger;
    }

    public ScreenTimeStateLoad Load()
    {
        var state = _reader.Probe();

        if (!state.IsTrustworthy)
        {
            if (ProtectedStoreGate.MayUseUnprotectedStorage(state, _environment.IsDevelopment, _logger))
            {
                return _fallback.Load();
            }

            // Production with no boundary. Reading the counter from a file the
            // child controls would be worse than not reading one, because the
            // number would be believed.
            return Unknown($"protected storage is {state.Status}");
        }

        var document = _reader.Read(ProtectedDocument.ScreenTimeState);

        if (string.IsNullOrWhiteSpace(document))
        {
            // Provisioned and nothing counted yet. A genuine first run on a
            // prepared machine - and the ONLY absence that means zero, because
            // this store is one the child cannot empty.
            return ScreenTimeStateLoad.FirstRun();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ScreenTimeState>(document, Options);

            if (parsed is null || parsed.SchemaVersion > ScreenTimeState.CurrentSchemaVersion)
            {
                return Unknown("the protected counter is not in a form this build understands");
            }

            parsed.UsedSeconds = Math.Max(0, parsed.UsedSeconds);
            parsed.BonusMinutes = Math.Max(0, parsed.BonusMinutes);

            // Carried as the primary so the journal rules can take the highest
            // figure for today rather than simply the one that parsed.
            return new ScreenTimeStateLoad(parsed, ScreenTimeLoadOutcome.Primary, string.Empty, parsed);
        }
        catch (Exception ex)
        {
            _logger.Warning("ScreenTime", "The protected screen-time counter could not be read.", ex);
            return Unknown("the protected counter could not be parsed");
        }
    }

    /// <summary>
    /// Unknown usage, not zero usage.
    ///
    /// The engine treats this as a spent day. Damaging the file must not be a
    /// way to earn an afternoon.
    /// </summary>
    private static ScreenTimeStateLoad Unknown(string detail) =>
        new(new ScreenTimeState(), ScreenTimeLoadOutcome.Unreadable, detail);

    public bool Save(ScreenTimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var probe = _reader.Probe();

        if (!probe.IsTrustworthy)
        {
            return ProtectedStoreGate.MayUseUnprotectedStorage(probe, _environment.IsDevelopment, _logger)
                && _fallback.Save(state);
        }

        var write = _writer.SaveScreenTimeState(JsonSerializer.Serialize(state, Options));

        if (!write.Success)
        {
            // Logged by the caller once rather than every tick; here it is
            // enough to answer honestly so the engine can fail closed.
            return false;
        }

        return true;
    }
}
