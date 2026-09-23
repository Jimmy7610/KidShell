using System.Text;
using KidShell.Core.Security.Transactions;

namespace KidShell.Recovery;

/// <summary>
/// Turns a recovery manifest into something a worried adult can act on.
///
/// WRITTEN FOR A BAD DAY
/// ---------------------
/// The reader may be signed in as a different account, on a machine that is
/// behaving oddly, possibly weeks after the change, possibly not the person who
/// made it. So: Swedish, plain, ordered, and every step says what to do rather
/// than what happened. No HRESULTs, no registry paths in the headline, no
/// implication that the reader already knows what AppLocker is.
///
/// Pure formatting, no I/O, so every line below is covered by a test.
/// </summary>
public static class RecoveryReport
{
    /// <summary>A one-line summary for a list of manifests.</summary>
    public static string Summarize(RecoveryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var when = manifest.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        var state = manifest.FinalState switch
        {
            null => "AVBRUTEN - kan behöva åtgärdas",
            TransactionState.Committed => "genomförd",
            TransactionState.RolledBack => "återställd",
            TransactionState.RollbackFailed => "MISSLYCKAD ÅTERSTÄLLNING - behöver åtgärdas",
            TransactionState.Refused => "avbröts, inget ändrades",
            TransactionState.Cancelled => "avbruten, inget ändrades",
            _ => manifest.FinalState.ToString()!
        };

        return $"{when}  {manifest.TransactionId}  {state}";
    }

    /// <summary>The full, human-readable account of one transaction.</summary>
    public static string Describe(RecoveryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var text = new StringBuilder();

        text.AppendLine("KidShell - återställningsinformation");
        text.AppendLine("====================================");
        text.AppendLine();
        text.AppendLine($"Ändring:     {manifest.TransactionId}");
        text.AppendLine($"Tidpunkt:    {manifest.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        text.AppendLine($"Dator:       {manifest.Machine.MachineName}");
        text.AppendLine($"Windows:     {manifest.Machine.WindowsEdition} (build {manifest.Machine.BuildNumber})");

        if (!string.IsNullOrWhiteSpace(manifest.Machine.RecoveryAdministrator))
        {
            // The first thing somebody locked out needs to know.
            text.AppendLine($"Logga in som: {manifest.Machine.RecoveryAdministrator}");
        }

        text.AppendLine();
        text.AppendLine(StateExplanation(manifest));
        text.AppendLine();

        if (manifest.Steps.Count == 0)
        {
            text.AppendLine("Inga steg finns registrerade.");
            return text.ToString();
        }

        text.AppendLine("Så här ångrar du ändringarna för hand");
        text.AppendLine("-------------------------------------");

        // Reverse order. Undoing in the order things were applied can restore a
        // value a later step depended on.
        text.AppendLine("Gå igenom stegen i den ordning de står här (omvänd ordning mot hur de gjordes).");
        text.AppendLine();

        var number = 1;

        foreach (var step in manifest.Steps.Reverse())
        {
            text.AppendLine($"{number}. {step.Description}");

            if (!string.IsNullOrWhiteSpace(step.ManualRollbackHint))
            {
                text.AppendLine($"   {step.ManualRollbackHint}");
            }

            if (step.ExistedBefore && !string.IsNullOrWhiteSpace(step.PreviousValue))
            {
                text.AppendLine($"   Tidigare värde: {step.PreviousValue}");
            }
            else if (!step.ExistedBefore)
            {
                text.AppendLine("   Det här fanns inte innan. Ta bort det som skapades.");
            }

            text.AppendLine($"   (teknisk referens: {step.OperationId})");
            text.AppendLine();
            number++;
        }

        return text.ToString();
    }

    internal static string StateExplanation(RecoveryManifest manifest) => manifest.FinalState switch
    {
        TransactionState.Committed =>
            "Ändringen genomfördes och kontrollerades. Datorn är inställd som det var tänkt.\n" +
            "Du behöver inte göra något. Stegen nedan finns om du vill ångra ändringen.",

        TransactionState.RolledBack =>
            "Något gick fel, och KidShell ångrade allt som hade hunnit ändras.\n" +
            "Datorn ska vara som innan. Du behöver inte göra något.",

        TransactionState.RollbackFailed =>
            "VIKTIGT: något gick fel OCH återställningen misslyckades.\n" +
            "Datorn kan vara i ett läge som ingen valt. Följ stegen nedan.",

        TransactionState.Refused =>
            "KidShell avbröt innan något ändrades. Datorn är orörd.",

        TransactionState.Cancelled =>
            "Ändringen avbröts innan något hann ändras. Datorn är orörd.",

        null =>
            "Den här ändringen avslutades aldrig. Det kan betyda att datorn stängdes av mitt i,\n" +
            "eller att KidShell stoppades. Kontrollera stegen nedan.",

        _ => $"Sluttillstånd: {manifest.FinalState}."
    };

    /// <summary>Whether a human still has to do something about this manifest.</summary>
    public static bool NeedsAttention(RecoveryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.RequiresAttention;
    }
}
