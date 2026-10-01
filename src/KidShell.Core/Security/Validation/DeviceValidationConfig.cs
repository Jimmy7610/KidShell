using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidShell.Core.Security.Validation;

/// <summary>
/// What the operator has declared about the machine under test.
///
/// A FILE, AND NOT THE ONLY THING THAT HAS TO AGREE
/// ------------------------------------------------
/// This is a declaration, not proof. It names the machine, the two accounts
/// and the child's SID, and every one of those is checked against what Windows
/// actually reports before anything is allowed to run. The point of writing
/// them down is that a mismatch becomes a refusal: a config copied from one
/// test machine to another stops the run instead of validating the wrong
/// computer.
///
/// There is deliberately no password field, and no field that could hold one.
/// </summary>
public sealed record DeviceValidationConfig
{
    /// <summary>The machine this config is for. Compared against the real name.</summary>
    public string MachineName { get; init; } = string.Empty;

    /// <summary>
    /// The operator's assertion that this computer is expendable.
    ///
    /// False is the default and the safe answer, so a config that somebody
    /// half-filled in refuses rather than proceeds.
    /// </summary>
    public bool DedicatedTestDevice { get; init; }

    /// <summary>The administrator account that must keep working throughout.</summary>
    public string ParentAdminUser { get; init; } = string.Empty;

    /// <summary>The standard account KidShell will run as.</summary>
    public string ChildUser { get; init; } = string.Empty;

    /// <summary>
    /// The child's SID, as captured once at setup.
    ///
    /// Recorded because the pipe's access list and the protected store's ACL
    /// are both scoped to it, and because an account deleted and recreated
    /// with the same NAME has a different SID - which would silently validate
    /// permissions that no longer refer to the account being tested.
    /// </summary>
    public string ExpectedChildSid { get; init; } = string.Empty;

    public string ExpectedWindowsEdition { get; init; } = string.Empty;

    public string ExpectedBuild { get; init; } = string.Empty;

    public string KidShellInstallRoot { get; init; } = string.Empty;

    public string EvidenceDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Everything wrong with this config, or an empty list.
    ///
    /// Shape only. Whether the machine actually matches is
    /// <see cref="ValidationInterlock"/>'s job, because that needs to look at
    /// the machine.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(MachineName))
        {
            problems.Add("MachineName is empty, so nothing can be compared against the real machine.");
        }

        if (string.IsNullOrWhiteSpace(ParentAdminUser))
        {
            problems.Add("ParentAdminUser is empty. A recovery administrator must be named.");
        }

        if (string.IsNullOrWhiteSpace(ChildUser))
        {
            problems.Add("ChildUser is empty.");
        }

        if (!string.IsNullOrWhiteSpace(ParentAdminUser) &&
            string.Equals(ParentAdminUser.Trim(), ChildUser.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // The whole architecture is about the difference between these
            // two. If they are the same account, every denial this validation
            // checks would pass for the wrong reason.
            problems.Add("ParentAdminUser and ChildUser are the same account.");
        }

        if (!string.IsNullOrWhiteSpace(ExpectedChildSid) && !LooksLikeSid(ExpectedChildSid))
        {
            problems.Add($"ExpectedChildSid '{ExpectedChildSid}' is not a SID.");
        }

        if (string.IsNullOrWhiteSpace(EvidenceDirectory))
        {
            problems.Add("EvidenceDirectory is empty, so a run would produce no evidence.");
        }

        foreach (var field in new[] { MachineName, ParentAdminUser, ChildUser })
        {
            if (field.Contains("example", StringComparison.OrdinalIgnoreCase) ||
                field.Contains("CHANGE-ME", StringComparison.OrdinalIgnoreCase))
            {
                // The template is meant to be edited. An unedited copy must
                // not be mistaken for a decision somebody made.
                problems.Add($"'{field}' still looks like the template placeholder.");
            }
        }

        return problems;
    }

    /// <summary>Whether this config is usable at all.</summary>
    [JsonIgnore]
    public bool IsComplete => Problems().Count == 0;

    private static bool LooksLikeSid(string value)
    {
        if (!value.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value.Split('-');

        return parts.Length is >= 3 and <= 15 &&
               parts.Skip(1).All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>Reads a config, or null when the text is not one.</summary>
    public static DeviceValidationConfig? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeviceValidationConfig>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The template an operator edits. Deliberately refuses as written.</summary>
    public static DeviceValidationConfig Template() => new()
    {
        MachineName = "CHANGE-ME",
        DedicatedTestDevice = false,
        ParentAdminUser = "CHANGE-ME",
        ChildUser = "CHANGE-ME",
        ExpectedChildSid = string.Empty,
        ExpectedWindowsEdition = "Professional",
        ExpectedBuild = "26100",
        KidShellInstallRoot = @"C:\Program Files\KidShell",
        EvidenceDirectory = @"C:\KidShell-Validation"
    };
}
