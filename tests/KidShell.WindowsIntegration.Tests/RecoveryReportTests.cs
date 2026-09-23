using KidShell.Core.Security.Transactions;
using KidShell.Recovery;
using Xunit;

namespace KidShell.WindowsIntegration.Tests;

/// <summary>
/// What a recovery administrator reads on a bad day.
///
/// Every assertion here is about comprehension rather than correctness of
/// data: the reader may be a different person, on a different account, weeks
/// later, with the machine misbehaving. A report that is accurate and
/// unreadable has failed.
/// </summary>
public class RecoveryReportTests
{
    private static RecoveryManifest Manifest(
        TransactionState? finalState = null,
        string recoveryAdministrator = "Jimmy") => new()
    {
        TransactionId = "abc123",
        CreatedAtUtc = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero),
        Machine = new RecoveryMachineSummary
        {
            WindowsEdition = "Windows 11 Home",
            BuildNumber = 26200,
            MachineName = "FAMILJEDATORN",
            RecoveryAdministrator = recoveryAdministrator,
            UacEnabled = true
        },
        Steps =
        [
            new RecoveryStep
            {
                OperationId = "child-account-create",
                Description = "Barnkontot Lucas skapades",
                ExistedBefore = false,
                ManualRollbackHint = "Ta bort kontot i Inställningar > Konton."
            },
            new RecoveryStep
            {
                OperationId = "child-autostart",
                Description = "KidShell ställdes in att starta automatiskt",
                ExistedBefore = true,
                PreviousValue = "",
                ManualRollbackHint = "Återställ värdet som står under previousValue."
            }
        ],
        FinalState = finalState
    };

    [Fact]
    public void The_report_names_the_account_to_sign_in_as()
    {
        // The single most useful fact for somebody who cannot get in.
        var text = RecoveryReport.Describe(Manifest());

        Assert.Contains("Logga in som: Jimmy", text);
    }

    [Fact]
    public void Steps_are_listed_in_reverse_order()
    {
        var text = RecoveryReport.Describe(Manifest());

        var autostartAt = text.IndexOf("startar automatiskt", StringComparison.Ordinal);
        var accountAt = text.IndexOf("Lucas skapades", StringComparison.Ordinal);

        // Newest first. Undoing in application order can restore a value a
        // later step depended on.
        Assert.True(autostartAt < accountAt,
            "the most recent change should be undone first");
    }

    [Fact]
    public void A_failed_rollback_is_called_out_prominently()
    {
        var text = RecoveryReport.Describe(Manifest(TransactionState.RollbackFailed));

        Assert.Contains("VIKTIGT", text);
        Assert.Contains("ingen valt", text);
    }

    [Fact]
    public void A_committed_transaction_tells_the_reader_to_do_nothing()
    {
        var text = RecoveryReport.Describe(Manifest(TransactionState.Committed));

        Assert.Contains("behöver inte göra något", text);
        Assert.DoesNotContain("VIKTIGT", text);
    }

    [Fact]
    public void A_rolled_back_transaction_says_the_machine_is_as_it_was()
    {
        var text = RecoveryReport.Describe(Manifest(TransactionState.RolledBack));

        Assert.Contains("ångrade allt", text);
        Assert.Contains("som innan", text);
    }

    [Fact]
    public void An_unfinished_transaction_is_not_presented_as_fine()
    {
        // No final state means KidShell never got to write one - the machine
        // was turned off mid-change, or the process was killed.
        var text = RecoveryReport.Describe(Manifest(finalState: null));

        Assert.Contains("avslutades aldrig", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TransactionState.Committed)]
    [InlineData(TransactionState.RolledBack)]
    [InlineData(TransactionState.RollbackFailed)]
    [InlineData(TransactionState.Refused)]
    [InlineData(TransactionState.Cancelled)]
    public void No_state_produces_technical_noise(TransactionState? state)
    {
        var text = RecoveryReport.Describe(Manifest(state));

        // A parent reading this during a crisis must not meet an HRESULT.
        Assert.DoesNotContain("0x", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HKEY_", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TransactionState.Committed)]
    [InlineData(TransactionState.RolledBack)]
    [InlineData(TransactionState.RollbackFailed)]
    [InlineData(TransactionState.Refused)]
    [InlineData(TransactionState.Cancelled)]
    public void Every_state_has_a_plain_language_explanation(TransactionState? state)
    {
        var explanation = RecoveryReport.StateExplanation(Manifest(state));

        Assert.False(string.IsNullOrWhiteSpace(explanation));

        // No enum member leaking into the text.
        Assert.DoesNotContain("TransactionState", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_with_no_recovery_administrator_does_not_print_an_empty_line()
    {
        var text = RecoveryReport.Describe(Manifest(recoveryAdministrator: ""));

        Assert.DoesNotContain("Logga in som:", text);
    }

    [Fact]
    public void Only_an_unfinished_or_failed_transaction_needs_attention()
    {
        Assert.True(RecoveryReport.NeedsAttention(Manifest(finalState: null)));
        Assert.True(RecoveryReport.NeedsAttention(Manifest(TransactionState.RollbackFailed)));

        Assert.False(RecoveryReport.NeedsAttention(Manifest(TransactionState.Committed)));
        Assert.False(RecoveryReport.NeedsAttention(Manifest(TransactionState.RolledBack)));
        Assert.False(RecoveryReport.NeedsAttention(Manifest(TransactionState.Cancelled)));
    }

    [Fact]
    public void The_summary_line_is_short_and_says_what_happened()
    {
        var summary = RecoveryReport.Summarize(Manifest(TransactionState.RollbackFailed));

        Assert.Contains("abc123", summary);
        Assert.Contains("MISSLYCKAD", summary);
        Assert.DoesNotContain('\n', summary);
    }

    [Fact]
    public void A_step_that_did_not_exist_before_says_to_remove_it()
    {
        var text = RecoveryReport.Describe(Manifest());

        // "Restore the previous value" is meaningless when there wasn't one.
        Assert.Contains("fanns inte innan", text);
    }

    [Fact]
    public void A_manifest_with_no_steps_says_so_rather_than_printing_nothing()
    {
        var empty = Manifest() with { Steps = [] };

        Assert.Contains("Inga steg", RecoveryReport.Describe(empty));
    }
}
