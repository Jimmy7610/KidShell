using System.Text.Json;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Readiness;
using KidShell.Core.Security.Transactions;
using KidShell.WindowsIntegration.Platform;

namespace KidShell.WindowsIntegration.Operations;

/// <summary>
/// What a child-account snapshot records. Serialised into the recovery
/// manifest, so a human can read it and undo the change by hand.
/// </summary>
internal sealed record ChildAccountState
{
    public bool AccountExisted { get; init; }

    public string Sid { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public bool WasAdministrator { get; init; }

    public bool WasEnabled { get; init; }
}

/// <summary>
/// Creates the child's local Windows account.
///
/// The account is created standard and never created as an administrator and
/// demoted afterwards - that would leave a window in which the child's account
/// had rights nobody intended.
///
/// Rollback deletes the account, and only if this operation created it. An
/// account that already existed is never deleted by a rollback: the parent may
/// have been using it for years, and "undo" must not mean "destroy the profile
/// and everything in it".
/// </summary>
public sealed class CreateChildAccountOperation : SecurityOperationBase
{
    private readonly ILocalAccountService _accounts;
    private readonly string _username;
    private readonly string? _fullName;
    private readonly string? _password;

    private string? _createdSid;

    public CreateChildAccountOperation(
        ILocalAccountService accounts,
        string username,
        string? fullName,
        string? password,
        IKidShellLogger logger) : base(logger)
    {
        _accounts = accounts;
        _username = username;
        _fullName = fullName;
        _password = password;
    }

    public override string Id => "child-account-create";

    public override string Description => $"Skapa barnkontot \"{_username}\"";

    public override ChangeRiskLevel RiskLevel => ChangeRiskLevel.High;

    public override RequiredCapability CapabilityRequired => RequiredCapability.LocalAccountManagement;

    protected override async Task<OperationOutcome> DoPreflightAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_username))
        {
            return OperationOutcome.Fail("Barnkontot saknar namn.");
        }

        if (_username.Length > 20)
        {
            return OperationOutcome.Fail("Kontonamnet är för långt. Windows tillåter högst 20 tecken.");
        }

        var existing = await _accounts.FindByNameAsync(_username, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            return OperationOutcome.Fail($"Det finns redan ett konto som heter \"{_username}\".");
        }

        // The invariant that outranks everything: somebody who is not the child
        // must still be able to sign in as an administrator afterwards.
        var all = await _accounts.ListAsync(cancellationToken).ConfigureAwait(false);
        var recovery = all.Where(a => a.IsAdministrator && a.IsEnabled).ToList();

        if (recovery.Count == 0)
        {
            return OperationOutcome.Fail(
                "Det finns inget aktivt administratörskonto att logga in med om något går fel.");
        }

        return OperationOutcome.Ok($"Kontot \"{_username}\" kan skapas.");
    }

    protected override Task<OperationSnapshot> DoCaptureStateAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new OperationSnapshot
        {
            OperationId = Id,
            Description = $"Kontot \"{_username}\" fanns inte innan KidShell konfigurerades.",

            PreviousValue = JsonSerializer.Serialize(new ChildAccountState
            {
                AccountExisted = false,
                Username = _username
            }),

            // The distinction rollback depends on: it did not exist, so undoing
            // means deleting rather than restoring.
            ExistedBefore = false
        });

    protected override async Task<OperationOutcome> DoApplyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var created = await _accounts
            .CreateStandardAccountAsync(_username, _fullName, _password, cancellationToken)
            .ConfigureAwait(false);

        _createdSid = created.Sid;

        return OperationOutcome.Ok($"Kontot \"{_username}\" skapades.");
    }

    protected override async Task<OperationOutcome> DoVerifyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        // Read it back rather than trusting that creation returned success.
        var account = await _accounts.FindByNameAsync(_username, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            return OperationOutcome.Fail($"Kontot \"{_username}\" kunde inte hittas efter att det skapats.");
        }

        if (string.IsNullOrEmpty(account.Sid))
        {
            return OperationOutcome.Fail($"Kontot \"{_username}\" saknar SID, så det kan inte användas säkert.");
        }

        if (account.IsAdministrator)
        {
            // Never expected, and far too important to let through.
            return OperationOutcome.Fail($"Kontot \"{_username}\" blev administratör. Det är inte tillåtet för ett barnkonto.");
        }

        if (!account.IsEnabled)
        {
            return OperationOutcome.Fail($"Kontot \"{_username}\" är avstängt.");
        }

        _createdSid = account.Sid;

        return OperationOutcome.Ok($"Kontot \"{_username}\" finns och är ett standardkonto.");
    }

    protected override async Task<OperationOutcome> DoRollbackAsync(
        OperationSnapshot snapshot, SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        if (snapshot.ExistedBefore)
        {
            // Defensive: this operation only ever creates. Deleting an account
            // that predates KidShell would destroy a profile.
            return OperationOutcome.Fail(
                "Kontot fanns redan innan. KidShell tar inte bort ett konto det inte skapade.");
        }

        var sid = _createdSid;

        if (sid is null)
        {
            var account = await _accounts.FindByNameAsync(_username, cancellationToken).ConfigureAwait(false);
            sid = account?.Sid;
        }

        if (sid is null)
        {
            return OperationOutcome.Ok("Kontot finns inte, så det behövde inte tas bort.");
        }

        await _accounts.DeleteAccountAsync(sid, cancellationToken).ConfigureAwait(false);

        var stillThere = await _accounts.FindByNameAsync(_username, cancellationToken).ConfigureAwait(false);

        return stillThere is null
            ? OperationOutcome.Ok($"Kontot \"{_username}\" togs bort.")
            : OperationOutcome.Fail($"Kontot \"{_username}\" kunde inte tas bort.");
    }

    /// <summary>The SID of the account this operation created, once it has.</summary>
    public string? CreatedSid => _createdSid;
}

/// <summary>
/// Removes the administrator role from the account chosen for the child.
///
/// Separate from creation on purpose: demoting an account a family has been
/// using is a different decision from making a new one, and it is the one that
/// can strand a household if it is the last administrator. Preflight refuses
/// unless another enabled administrator will remain.
/// </summary>
public sealed class DemoteChildAccountOperation : SecurityOperationBase
{
    private readonly ILocalAccountService _accounts;
    private readonly string _sid;

    public DemoteChildAccountOperation(ILocalAccountService accounts, string sid, IKidShellLogger logger)
        : base(logger)
    {
        _accounts = accounts;
        _sid = sid;
    }

    public override string Id => "child-account-demote";

    public override string Description => "Ta bort administratörsrollen från barnkontot";

    public override ChangeRiskLevel RiskLevel => ChangeRiskLevel.High;

    public override RequiredCapability CapabilityRequired => RequiredCapability.LocalAccountManagement;

    protected override async Task<OperationOutcome> DoPreflightAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var account = await _accounts.FindBySidAsync(_sid, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            return OperationOutcome.Fail("Det valda kontot finns inte längre.");
        }

        if (account.IsBuiltIn)
        {
            return OperationOutcome.Fail("Det valda kontot är ett av Windows egna konton och ändras inte.");
        }

        if (!account.IsAdministrator)
        {
            return OperationOutcome.Ok("Kontot är redan ett standardkonto.");
        }

        var all = await _accounts.ListAsync(cancellationToken).ConfigureAwait(false);

        var otherAdministrators = all.Count(a =>
            a.IsAdministrator && a.IsEnabled &&
            !string.Equals(a.Sid, _sid, StringComparison.OrdinalIgnoreCase));

        if (otherAdministrators == 0)
        {
            return OperationOutcome.Fail(
                "Det här är det enda administratörskontot. Välj ett annat konto till barnet.");
        }

        return OperationOutcome.Ok($"Kontot \"{account.Username}\" kan göras till standardkonto.");
    }

    protected override async Task<OperationSnapshot> DoCaptureStateAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var account = await _accounts.FindBySidAsync(_sid, cancellationToken).ConfigureAwait(false);

        return new OperationSnapshot
        {
            OperationId = Id,
            Description = account is null
                ? "Kontot kunde inte läsas."
                : $"\"{account.Username}\" var {(account.IsAdministrator ? "administratör" : "standardanvändare")}.",

            PreviousValue = JsonSerializer.Serialize(new ChildAccountState
            {
                AccountExisted = account is not null,
                Sid = _sid,
                Username = account?.Username ?? string.Empty,
                WasAdministrator = account?.IsAdministrator ?? false,
                WasEnabled = account?.IsEnabled ?? false
            }),

            ExistedBefore = account is not null
        };
    }

    protected override async Task<OperationOutcome> DoApplyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        await _accounts.SetAdministratorAsync(_sid, isAdministrator: false, cancellationToken).ConfigureAwait(false);
        return OperationOutcome.Ok("Administratörsrollen togs bort.");
    }

    protected override async Task<OperationOutcome> DoVerifyAsync(
        SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var account = await _accounts.FindBySidAsync(_sid, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            return OperationOutcome.Fail("Kontot kunde inte läsas tillbaka.");
        }

        if (account.IsAdministrator)
        {
            return OperationOutcome.Fail("Kontot är fortfarande administratör.");
        }

        // The second half of the check, and the one that matters most: somebody
        // must still be able to get in.
        var all = await _accounts.ListAsync(cancellationToken).ConfigureAwait(false);

        if (!all.Any(a => a.IsAdministrator && a.IsEnabled))
        {
            return OperationOutcome.Fail(
                "Inget aktivt administratörskonto finns kvar. Ändringen måste ångras.");
        }

        return OperationOutcome.Ok($"\"{account.Username}\" är nu ett standardkonto.");
    }

    protected override async Task<OperationOutcome> DoRollbackAsync(
        OperationSnapshot snapshot, SecurityExecutionContext context, CancellationToken cancellationToken)
    {
        var previous = Deserialize(snapshot.PreviousValue);

        if (previous is null)
        {
            return OperationOutcome.Fail("Det tidigare läget kunde inte läsas, så rollen kunde inte återställas.");
        }

        if (!previous.WasAdministrator)
        {
            return OperationOutcome.Ok("Kontot var inte administratör innan, så ingenting behövde återställas.");
        }

        await _accounts.SetAdministratorAsync(_sid, isAdministrator: true, cancellationToken).ConfigureAwait(false);

        var account = await _accounts.FindBySidAsync(_sid, cancellationToken).ConfigureAwait(false);

        return account?.IsAdministrator == true
            ? OperationOutcome.Ok("Administratörsrollen återställdes.")
            : OperationOutcome.Fail("Administratörsrollen kunde inte återställas.");
    }

    private static ChildAccountState? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ChildAccountState>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
