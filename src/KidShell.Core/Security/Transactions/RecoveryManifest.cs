using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;

namespace KidShell.Core.Security.Transactions;

/// <summary>One recorded step, with enough detail to undo it by hand.</summary>
public sealed record RecoveryStep
{
    public required string OperationId { get; init; }

    public required string Description { get; init; }

    /// <summary>What the state was before KidShell touched it.</summary>
    public string? PreviousValue { get; init; }

    public bool ExistedBefore { get; init; }

    /// <summary>Plain-language instructions for a human undoing this manually.</summary>
    public string ManualRollbackHint { get; init; } = string.Empty;

    public bool Applied { get; init; }

    public bool RolledBack { get; init; }
}

/// <summary>
/// The written record of a security transaction.
///
/// It exists for one scenario: a change was applied, the rollback failed, and
/// somebody has to put the machine back by hand — possibly from a different
/// account, possibly days later, possibly not the person who ran it. So it is
/// written BEFORE anything is applied, it is plain JSON a human can read, and
/// it contains the previous values rather than a description of them.
///
/// It deliberately contains no PIN, no hash, no salt and no password. A file
/// whose whole purpose is to be readable during a crisis is the worst possible
/// place for a secret.
/// </summary>
public sealed record RecoveryManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string TransactionId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>What KidShell believed about the machine when it started.</summary>
    public required RecoveryMachineSummary Machine { get; init; }

    public required IReadOnlyList<RecoveryStep> Steps { get; init; }

    /// <summary>Filled in once the transaction finishes.</summary>
    public TransactionState? FinalState { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>
    /// True when a human still has to do something. The manifest is kept for
    /// exactly as long as this is true.
    /// </summary>
    [JsonIgnore]
    public bool RequiresAttention =>
        FinalState is null or TransactionState.RollbackFailed;
}

/// <summary>Machine facts worth having when reading a manifest later.</summary>
public sealed record RecoveryMachineSummary
{
    public required string WindowsEdition { get; init; }

    public required int BuildNumber { get; init; }

    public required string MachineName { get; init; }

    /// <summary>
    /// The account that must still work afterwards. Named so a person reading
    /// this knows which login to try first.
    /// </summary>
    public string RecoveryAdministrator { get; init; } = string.Empty;

    public bool UacEnabled { get; init; }

    public string ExecutionMode { get; init; } = nameof(SecurityExecutionMode.AuditOnly);
}

/// <summary>Reads and writes recovery manifests.</summary>
public interface IRecoveryManifestStore
{
    /// <summary>Writes the manifest before any change is applied.</summary>
    Task<bool> WriteAsync(RecoveryManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>Updates the manifest once the transaction finishes.</summary>
    Task<bool> CompleteAsync(string transactionId, TransactionState finalState, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecoveryManifest>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Manifests still needing human attention.</summary>
    Task<IReadOnlyList<RecoveryManifest>> ListOutstandingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// JSON manifests in a directory, one file per transaction.
///
/// Indented on purpose: this file is meant to be opened in Notepad by a worried
/// parent, so readability beats compactness.
/// </summary>
public sealed class RecoveryManifestStore : IRecoveryManifestStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },

        // Swedish text must survive as Swedish. The default encoder writes
        // "Inställningar", which is unreadable in Notepad - and this file
        // exists precisely to be opened in Notepad by a worried parent.
        // HTML-sensitive characters are still escaped, since only the Latin
        // ranges are whitelisted.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Latin1Supplement)
    };

    private readonly string _directory;
    private readonly IKidShellLogger _logger;

    public RecoveryManifestStore(string directory, IKidShellLogger logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public string Directory => _directory;

    public async Task<bool> WriteAsync(RecoveryManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            var path = PathFor(manifest.TransactionId);
            var json = JsonSerializer.Serialize(manifest, Options);

            // The same temp-then-replace dance the configuration store uses:
            // a half-written recovery manifest is worse than none.
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);

            _logger.Info(SecurityAuditEvents.Category, $"Recovery manifest written for transaction {manifest.TransactionId}.");
            return true;
        }
        catch (Exception ex)
        {
            // A transaction whose manifest cannot be written must not proceed,
            // which is why the caller treats false as a preflight failure.
            _logger.Error(SecurityAuditEvents.Category, "Could not write the recovery manifest.", ex);
            return false;
        }
    }

    public async Task<bool> CompleteAsync(
        string transactionId,
        TransactionState finalState,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = PathFor(transactionId);

            if (!File.Exists(path))
            {
                return false;
            }

            var existing = JsonSerializer.Deserialize<RecoveryManifest>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), Options);

            if (existing is null)
            {
                return false;
            }

            var updated = existing with
            {
                FinalState = finalState,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };

            return await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(SecurityAuditEvents.Category, "Could not complete the recovery manifest.", ex);
            return false;
        }
    }

    public async Task<IReadOnlyList<RecoveryManifest>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<RecoveryManifest>();

        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return results;
            }

            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var manifest = JsonSerializer.Deserialize<RecoveryManifest>(
                        await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false), Options);

                    if (manifest is not null)
                    {
                        results.Add(manifest);
                    }
                }
                catch
                {
                    // One unreadable manifest must not hide the others - the
                    // remaining ones may be the ones that matter.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(SecurityAuditEvents.Category, "Could not list recovery manifests.", ex);
        }

        return [.. results.OrderByDescending(m => m.CreatedAtUtc)];
    }

    public async Task<IReadOnlyList<RecoveryManifest>> ListOutstandingAsync(CancellationToken cancellationToken = default)
    {
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.Where(m => m.RequiresAttention)];
    }

    private string PathFor(string transactionId) =>
        Path.Combine(_directory, $"recovery-{transactionId}.json");
}
