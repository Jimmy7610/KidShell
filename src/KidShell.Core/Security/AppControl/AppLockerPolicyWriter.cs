using System.Xml;
using System.Xml.Linq;

namespace KidShell.Core.Security.AppControl;

/// <summary>
/// Renders an <see cref="AppControlPolicy"/> as AppLocker XML.
///
/// ARTIFACT ONLY. This produces a string. It does not call
/// Set-AppLockerPolicy, does not write to the policy store, does not touch
/// SrpV2 and does not start the Application Identity service. Nothing in
/// KidShell deploys what this generates — see
/// <see cref="IAppControlDeploymentChannel"/> for why that is a separate,
/// currently-unimplemented concern.
///
/// The shape follows the documented AppLocker schema: an AppLockerPolicy with
/// a Version attribute, one RuleCollection per file type, each with an
/// EnforcementMode, containing FilePathRule / FilePublisherRule elements with
/// a UserOrGroupSid and an Action.
/// </summary>
public static class AppLockerPolicyWriter
{
    /// <summary>Everyone. The default audience when no specific SID is given.</summary>
    public const string EveryoneSid = "S-1-1-0";

    /// <summary>
    /// Enforcement mode for a generated policy.
    ///
    /// AuditOnly is the default on purpose: the first thing anyone should do
    /// with a generated policy is run it in audit and read the event log, not
    /// enforce it on a child's account and discover what broke.
    /// </summary>
    public enum EnforcementMode
    {
        /// <summary>Log what would have been blocked; block nothing.</summary>
        AuditOnly,

        /// <summary>Actually block.</summary>
        Enabled,

        /// <summary>Collection present but inactive.</summary>
        NotConfigured
    }

    public static string Write(
        AppControlPolicy policy,
        EnforcementMode enforcement = EnforcementMode.AuditOnly,
        string? userOrGroupSid = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var sid = string.IsNullOrWhiteSpace(userOrGroupSid)
            ? string.IsNullOrWhiteSpace(policy.TargetUserSid) ? EveryoneSid : policy.TargetUserSid
            : userOrGroupSid;

        var root = new XElement("AppLockerPolicy", new XAttribute("Version", "1"));

        // Only emit collections that have rules. An empty collection set to
        // Enabled is a default-deny for that file type, which would block
        // things the parent never considered.
        foreach (var collection in Enum.GetValues<RuleCollection>())
        {
            var rules = policy.Rules.Where(r => r.Collection == collection).ToList();

            if (rules.Count == 0)
            {
                continue;
            }

            root.Add(BuildCollection(collection, rules, enforcement, sid));
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

        using var writer = new StringWriter();
        using var xml = XmlWriter.Create(writer, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = System.Text.Encoding.UTF8,
            OmitXmlDeclaration = false
        });

        document.Save(xml);
        xml.Flush();

        return writer.ToString();
    }

    private static XElement BuildCollection(
        RuleCollection collection,
        IReadOnlyList<AppControlRule> rules,
        EnforcementMode enforcement,
        string sid)
    {
        var element = new XElement("RuleCollection",
            new XAttribute("Type", collection.ToString()),
            new XAttribute("EnforcementMode", enforcement.ToString()));

        foreach (var rule in rules)
        {
            element.Add(BuildRule(rule, sid));
        }

        return element;
    }

    private static XElement BuildRule(AppControlRule rule, string sid)
    {
        // A stable GUID per rule id, so regenerating the same policy produces
        // the same XML. A random GUID would make every generation a diff and
        // make comparing two policies impossible.
        var id = DeterministicGuid(rule.Id);

        return rule.Strategy switch
        {
            RuleStrategy.Publisher => new XElement("FilePublisherRule",
                new XAttribute("Id", id),
                new XAttribute("Name", rule.Name),
                new XAttribute("Description", rule.Reason),
                new XAttribute("UserOrGroupSid", sid),
                new XAttribute("Action", "Allow"),
                new XElement("Conditions",
                    new XElement("FilePublisherCondition",
                        new XAttribute("PublisherName", rule.Value),
                        new XAttribute("ProductName", string.IsNullOrWhiteSpace(rule.ProductName) ? "*" : rule.ProductName),
                        new XAttribute("BinaryName", "*"),
                        new XElement("BinaryVersionRange",
                            new XAttribute("LowSection", "*"),
                            new XAttribute("HighSection", "*"))))),

            RuleStrategy.Hash => new XElement("FileHashRule",
                new XAttribute("Id", id),
                new XAttribute("Name", rule.Name),
                new XAttribute("Description", rule.Reason),
                new XAttribute("UserOrGroupSid", sid),
                new XAttribute("Action", "Allow"),
                new XElement("Conditions",
                    new XElement("FileHashCondition",
                        new XElement("FileHash",
                            new XAttribute("Type", "SHA256"),
                            new XAttribute("Data", rule.Value),
                            new XAttribute("SourceFileName", rule.Name),
                            new XAttribute("SourceFileLength", "0"))))),

            _ => new XElement("FilePathRule",
                new XAttribute("Id", id),
                new XAttribute("Name", rule.Name),
                new XAttribute("Description", rule.Reason),
                new XAttribute("UserOrGroupSid", sid),
                new XAttribute("Action", "Allow"),
                new XElement("Conditions",
                    new XElement("FilePathCondition",
                        new XAttribute("Path", rule.Value))))
        };
    }

    /// <summary>
    /// A stable GUID derived from the rule id, so the same policy always
    /// renders identically and two generations can be diffed.
    /// </summary>
    public static string DeterministicGuid(string ruleId)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(ruleId ?? string.Empty));

        return new Guid(bytes).ToString();
    }

    /// <summary>
    /// Parses generated XML back and checks it is structurally sound.
    ///
    /// Generating a policy nobody validates is how a milestone ships an
    /// artifact that Windows rejects on the day it finally matters.
    /// </summary>
    public static PolicyValidationResult Validate(string xml)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(xml))
        {
            return new PolicyValidationResult(false, ["Policyn är tom."], 0);
        }

        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            return new PolicyValidationResult(false, [$"Ogiltig XML: {ex.Message}"], 0);
        }

        var root = document.Root;

        if (root is null || root.Name != "AppLockerPolicy")
        {
            return new PolicyValidationResult(false, ["Rotelementet är inte AppLockerPolicy."], 0);
        }

        if (root.Attribute("Version") is null)
        {
            problems.Add("AppLockerPolicy saknar Version-attribut.");
        }

        var collections = root.Elements("RuleCollection").ToList();

        if (collections.Count == 0)
        {
            problems.Add("Policyn innehåller inga regelsamlingar.");
        }

        var ruleCount = 0;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var collection in collections)
        {
            if (collection.Attribute("Type") is null)
            {
                problems.Add("En regelsamling saknar Type.");
            }

            if (collection.Attribute("EnforcementMode") is null)
            {
                problems.Add("En regelsamling saknar EnforcementMode.");
            }

            foreach (var rule in collection.Elements())
            {
                ruleCount++;

                var id = rule.Attribute("Id")?.Value;

                if (string.IsNullOrWhiteSpace(id))
                {
                    problems.Add($"En regel i {collection.Attribute("Type")?.Value} saknar Id.");
                }
                else if (!seenIds.Add(id))
                {
                    // Windows rejects a policy with duplicate rule ids.
                    problems.Add($"Regel-id {id} förekommer mer än en gång.");
                }

                if (rule.Attribute("UserOrGroupSid") is null)
                {
                    problems.Add($"Regeln {rule.Attribute("Name")?.Value} saknar UserOrGroupSid.");
                }

                if (rule.Attribute("Action")?.Value is not ("Allow" or "Deny"))
                {
                    problems.Add($"Regeln {rule.Attribute("Name")?.Value} har ingen giltig Action.");
                }

                if (!rule.Elements("Conditions").Any())
                {
                    problems.Add($"Regeln {rule.Attribute("Name")?.Value} saknar villkor.");
                }
            }
        }

        return new PolicyValidationResult(problems.Count == 0, problems, ruleCount);
    }
}

/// <summary>Whether generated policy XML is structurally sound.</summary>
public sealed record PolicyValidationResult(bool IsValid, IReadOnlyList<string> Problems, int RuleCount);
