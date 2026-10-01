using System.Diagnostics;
using KidShell.Core.Diagnostics;
using KidShell.Core.Security.Storage;

namespace KidShell.Core.Security.Broker;

/// <summary>
/// Turns a staged policy into the live one, with authority the child's
/// session does not have.
///
/// WHY A POLICY CHANGE COSTS A PROMPT AND A SCREEN-TIME TICK DOES NOT
/// -------------------------------------------------------------------
/// This is the honest answer to the question the hardening pass was built
/// around: what stops a modified KidShell.App from writing a parent policy of
/// its choosing? Nothing, as long as the authority for that write is "the
/// request came from the child's session". A parent typing a PIN into a
/// program running as the child does not turn that program into an
/// administrator, and a capability issued for a PIN cannot be stronger than
/// the process that collected it.
///
/// So the authority for a policy change is a Windows one: an elevated
/// administrator, and therefore a consent prompt.
///
/// The product cost is bounded, which is the only reason this is acceptable.
/// A prompt appears when a parent saves settings - an action they took
/// deliberately, perhaps a handful of times in the life of the machine. It
/// does NOT appear for the writes that happen during ordinary use: the
/// screen-time counter on its timer, the PIN throttle on each attempt, the
/// session markers. Those go to the service as the child's session, under
/// transition rules that only let them tighten. A product that prompted for
/// those would teach a parent to click through prompts, and that is a worse
/// outcome than the hole it would be closing.
/// </summary>
public interface IParentPolicyApprovalChannel
{
    /// <summary>Whether approval can be requested at all on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Asks for the staged policy with this digest to be made live.
    ///
    /// The digest, not the content. The approver reads the staged document
    /// from the privileged store itself; passing the content here would mean
    /// the thing being approved travelled through the process that wants it
    /// approved.
    /// </summary>
    ProtectedWriteResult Approve(string digest);
}

/// <summary>
/// Production: one consent prompt, then an elevated commit.
///
/// THE ONE PLACE RUNAS BELONGS
/// ---------------------------
/// The transport this pass removed used a per-request process launch that
/// never elevated anything. Here the same mechanism is correct, and the
/// difference is the frequency: this runs when a parent saves settings, not
/// on a thirty-second timer, and it needs no redirected streams - the exit
/// code is the whole answer - so ShellExecute and the verb are compatible
/// with what it does.
///
/// The elevated instance connects to the same service as an administrator
/// and sends CommitStagedParentPolicy. It does not carry the policy: it
/// carries the digest of what the parent was shown, and the service refuses
/// if what is staged no longer matches.
/// </summary>
public sealed class ElevatedCommitApprovalChannel : IParentPolicyApprovalChannel
{
    private readonly string _hostPath;
    private readonly IKidShellLogger _logger;

    public ElevatedCommitApprovalChannel(string hostPath, IKidShellLogger logger)
    {
        _hostPath = hostPath;
        _logger = logger;
    }

    public bool IsAvailable => File.Exists(_hostPath);

    /// <summary>
    /// What this channel does, as a value a test can check.
    ///
    /// Viable, unlike the transport it replaces, and prompting - which here
    /// is the intended behaviour rather than the defect.
    /// </summary>
    public BrokerLaunchPlan LaunchPlan => new()
    {
        FileName = _hostPath,
        Verb = "runas",
        UseShellExecute = true,
        RedirectStandardInput = false,
        RedirectStandardOutput = false,
        Requires = BrokerElevationRequirement.Administrator,
        Manifest = BrokerManifestElevation.AsInvoker
    };

    public ProtectedWriteResult Approve(string digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return ProtectedWriteResult.Reject("Ingen kontrollsumma att godkänna.");
        }

        if (!IsAvailable)
        {
            return ProtectedWriteResult.Unavailable("Rättighetshjälparen finns inte.");
        }

        try
        {
            var plan = LaunchPlan;

            var start = new ProcessStartInfo
            {
                FileName = plan.FileName,
                UseShellExecute = plan.UseShellExecute,
                Verb = plan.Verb
            };

            // The digest is the only argument, and its shape was validated
            // before it got here. It is hexadecimal, so there is nothing in
            // it a command line could interpret.
            start.ArgumentList.Add(ApproveArgument);
            start.ArgumentList.Add(digest);

            using var process = Process.Start(start);

            if (process is null)
            {
                return ProtectedWriteResult.Fail("Godkännandet kunde inte startas.");
            }

            if (!process.WaitForExit(ApprovalTimeoutMilliseconds))
            {
                return ProtectedWriteResult.Fail("Godkännandet tog för lång tid.");
            }

            return process.ExitCode == 0
                ? ProtectedWriteResult.Ok()
                : ProtectedWriteResult.Reject("Ändringen godkändes inte.");
        }
        catch (Exception ex)
        {
            // A cancelled UAC prompt arrives here. It is an ordinary answer
            // to a question, not an error: the parent said no.
            _logger.Warning(BrokerAudit.Category, "The policy change was not approved.", ex);
            return ProtectedWriteResult.Reject("Ändringen godkändes inte.");
        }
    }

    /// <summary>The flag the elevated instance recognises.</summary>
    public const string ApproveArgument = "--approve-policy";

    /// <summary>
    /// Two minutes. Long enough for a parent to read a consent prompt and
    /// find the password; short enough that a prompt nobody is looking at
    /// does not wedge the settings screen forever.
    /// </summary>
    private const int ApprovalTimeoutMilliseconds = 120_000;
}

/// <summary>
/// Development: approves immediately, through the same two steps.
///
/// Labelled rather than hidden, and deliberately not a shortcut past the
/// staging slot. A development build that wrote the policy directly would
/// leave the staged-then-approved path untested until a dedicated device
/// existed, which is the same mistake as having a broker nothing could
/// reach.
/// </summary>
public sealed class LocalParentPolicyApprovalChannel : IParentPolicyApprovalChannel
{
    private readonly DirectProtectedStateWriter _writer;
    private readonly IKidShellLogger _logger;

    public LocalParentPolicyApprovalChannel(DirectProtectedStateWriter writer, IKidShellLogger logger)
    {
        _writer = writer;
        _logger = logger;
    }

    public bool IsAvailable => true;

    public ProtectedWriteResult Approve(string digest)
    {
        var staged = _writer.ReadStaged();

        if (staged is null)
        {
            return ProtectedWriteResult.Reject("Det finns inget förslag att godkänna.");
        }

        if (!string.Equals(ProtectedDocumentNames.DigestOf(staged), digest, StringComparison.Ordinal))
        {
            _logger.Error(BrokerAudit.Category,
                "The staged policy changed between staging and approval. Nothing was applied.");

            return ProtectedWriteResult.Reject("Förslaget har ändrats sedan det visades.");
        }

        var write = _writer.SaveParentPolicy(staged);

        if (write.Success)
        {
            _writer.ClearStaged();
        }

        return write;
    }
}

/// <summary>
/// No approval is possible here.
///
/// What a production build gets when the security service is not installed.
/// Returning "unavailable" rather than quietly writing is the whole point:
/// the settings screen reports that nothing was saved, which is true.
/// </summary>
public sealed class UnavailableParentPolicyApprovalChannel : IParentPolicyApprovalChannel
{
    public bool IsAvailable => false;

    public ProtectedWriteResult Approve(string digest) =>
        ProtectedWriteResult.Unavailable("Rättighetstjänsten är inte installerad.");
}
