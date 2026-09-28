using System.Text;

namespace KidShell.Core.Security.AppControl;

/// <summary>
/// The generated policy, written out for a person to read.
///
/// The XML is for Windows. This is for the adult deciding whether to turn it
/// on, and it exists because "trust the tool" is not a security model: a
/// parent who cannot tell what a policy allows cannot consent to it, and the
/// external audit found rules nobody had read since they were written.
///
/// Every rule is listed with the reason it is there. Anything that would stop
/// the policy being applied is at the top, in plain Swedish, because that is
/// the part that changes what the reader should do.
/// </summary>
public static class PolicyAuditReport
{
    public static string Write(AppControlPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var report = new StringBuilder();

        report.AppendLine("KIDSHELL - GRANSKNING AV APPREGLER");
        report.AppendLine("==================================");
        report.AppendLine();
        report.AppendLine($"Skapad:      {policy.GeneratedAtUtc:yyyy-MM-dd HH:mm} UTC");
        report.AppendLine($"Gäller för:  {(string.IsNullOrWhiteSpace(policy.TargetUserSid) ? "(inget konto angivet)" : policy.TargetUserSid)}");
        report.AppendLine($"Antal regler: {policy.Rules.Count}");
        report.AppendLine();
        report.AppendLine(policy.CanActivate
            ? "STATUS: Policyn kan aktiveras."
            : "STATUS: Policyn får INTE aktiveras. Se hindren nedan.");
        report.AppendLine();

        if (policy.BlockingWarnings.Any())
        {
            report.AppendLine("HINDER");
            report.AppendLine("------");

            foreach (var warning in policy.BlockingWarnings)
            {
                report.AppendLine($"  [{warning.Code}] {warning.Message}");
            }

            report.AppendLine();
        }

        var advisories = policy.Warnings.Where(w => w.Severity == PolicySeverity.Advisory).ToList();

        if (advisories.Count > 0)
        {
            report.AppendLine("ATT KÄNNA TILL");
            report.AppendLine("--------------");

            foreach (var warning in advisories)
            {
                report.AppendLine($"  [{warning.Code}] {warning.Message}");
            }

            report.AppendLine();
        }

        AppendRules(report, "WINDOWS OCH KIDSHELL", policy.SystemRules);
        AppendRules(report, "BARNETS APPAR", policy.ApplicationRules);

        report.AppendLine("DETTA TILLÅTS INTE");
        report.AppendLine("------------------");
        report.AppendLine("  Allt som inte står ovan. Policyn är en tillåtelselista:");
        report.AppendLine("  program som inte nämns får inte startas.");
        report.AppendLine();
        report.AppendLine("  Kommandotolken, PowerShell, Registereditorn, Aktivitetshanteraren");
        report.AppendLine("  och skriptmotorerna finns medvetet inte med. Listan kontrolleras");
        report.AppendLine("  av ett test vid varje bygge.");

        return report.ToString();
    }

    private static void AppendRules(StringBuilder report, string heading, IEnumerable<AppControlRule> rules)
    {
        var listed = rules.ToList();

        report.AppendLine(heading);
        report.AppendLine(new string('-', heading.Length));

        if (listed.Count == 0)
        {
            report.AppendLine("  (inga)");
            report.AppendLine();
            return;
        }

        foreach (var rule in listed.OrderBy(r => r.Name, StringComparer.CurrentCulture))
        {
            var weak = rule.IsWeak ? "  [SVAG REGEL]" : string.Empty;

            report.AppendLine($"  {rule.Name}{weak}");
            report.AppendLine($"    {rule.Strategy}: {rule.Value}");

            if (!string.IsNullOrWhiteSpace(rule.Reason))
            {
                report.AppendLine($"    Varför: {rule.Reason}");
            }

            report.AppendLine();
        }
    }
}
