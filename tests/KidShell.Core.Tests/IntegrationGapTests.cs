using KidShell.Core.Launching;
using KidShell.Core.Web;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// EXTERNAL AUDIT FINDING 08 — the gaps between a feature existing and a
/// feature being connected.
///
/// 08A (installed-app discovery) and 08B (web validation) had both been
/// wired up by the time this pass began, and are covered by
/// <see cref="ApplicationDiscoveryTests"/>, <see cref="AppDiscoveryDuplicateTests"/>
/// and <see cref="WebAllowlistEntryTests"/>. What is added here is the
/// awkward input those suites did not name, and the whole of 08C, which had
/// not been fixed at all.
/// </summary>
public class IntegrationGapTests
{
    // ------------------------------------------------ 08C: the manual picker

    /// <summary>
    /// The four extensions the picker used to offer. Only one of them is
    /// something KidShell can start.
    /// </summary>
    [Fact]
    public void The_picker_offers_programs_and_nothing_else() =>
        Assert.Equal([".exe"], ManualProgramPolicy.PickerFilter);

    [Theory]
    [InlineData(@"C:\Program Files\Paint\mspaint.exe")]
    [InlineData(@"C:\Program Files\Spel\spel.EXE")]
    [InlineData(@"""C:\Program Files\With Spaces\app.exe""")]
    public void A_program_is_accepted(string path) =>
        Assert.True(ManualProgramPolicy.Check(path).IsAllowed);

    /// <summary>
    /// A batch file is not a program. Running one runs cmd.exe - which this
    /// product's own AppLocker policy refuses by name, so accepting it here
    /// would promise a parent an app that Secure Mode then blocks.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Spel\start.bat")]
    [InlineData(@"C:\Spel\start.cmd")]
    [InlineData(@"C:\Spel\start.ps1")]
    [InlineData(@"C:\Spel\start.vbs")]
    [InlineData(@"C:\Spel\start.js")]
    [InlineData(@"C:\Spel\start.hta")]
    [InlineData(@"C:\Spel\start.wsf")]
    [InlineData(@"C:\Spel\start.py")]
    public void A_script_is_refused(string path)
    {
        var check = ManualProgramPolicy.Check(path);

        Assert.False(check.IsAllowed);
        Assert.Equal(ManualProgramVerdict.Script, check.Verdict);
    }

    /// <summary>
    /// What a parent approved and what would run are two different files, and
    /// the second can be changed without touching the first.
    /// </summary>
    [Fact]
    public void A_shortcut_is_refused()
    {
        var check = ManualProgramPolicy.Check(@"C:\Users\Lucas\Desktop\Spel.lnk");

        Assert.False(check.IsAllowed);
        Assert.Equal(ManualProgramVerdict.Shortcut, check.Verdict);
    }

    [Theory]
    [InlineData(@"C:\Spel\readme.txt")]
    [InlineData(@"C:\Spel\data.dll")]
    [InlineData(@"C:\Spel\installer.msi")]
    [InlineData(@"C:\Spel\noextension")]
    public void Anything_that_is_not_a_program_is_refused(string path) =>
        Assert.False(ManualProgramPolicy.Check(path).IsAllowed);

    /// <summary>
    /// cmd.exe is an .exe, so the extension rule alone would admit it. A
    /// parent can browse to it as easily as to mspaint.exe.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\powershell.exe")]
    [InlineData(@"C:\Windows\regedit.exe")]
    [InlineData(@"C:\Windows\System32\taskmgr.exe")]
    [InlineData(@"C:\Windows\System32\mshta.exe")]
    public void A_system_tool_is_refused_even_though_it_is_a_program(string path)
    {
        var check = ManualProgramPolicy.Check(path);

        Assert.False(check.IsAllowed);
        Assert.Equal(ManualProgramVerdict.EscapeSurface, check.Verdict);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_chosen_is_refused_as_empty(string? path) =>
        Assert.Equal(ManualProgramVerdict.Empty, ManualProgramPolicy.Check(path).Verdict);

    [Fact]
    public void Every_refusal_has_something_to_say_to_a_parent()
    {
        foreach (var verdict in Enum.GetValues<ManualProgramVerdict>())
        {
            if (verdict == ManualProgramVerdict.Ok)
            {
                continue;
            }

            var sample = verdict switch
            {
                ManualProgramVerdict.Empty => "",
                ManualProgramVerdict.Script => @"C:\a\b.bat",
                ManualProgramVerdict.Shortcut => @"C:\a\b.lnk",
                ManualProgramVerdict.Unsupported => @"C:\a\b.txt",
                ManualProgramVerdict.EscapeSurface => @"C:\Windows\System32\cmd.exe",
                _ => throw new InvalidOperationException($"no sample for {verdict}")
            };

            var check = ManualProgramPolicy.Check(sample);

            Assert.Equal(verdict, check.Verdict);
            Assert.False(string.IsNullOrWhiteSpace(check.ResourceKey),
                $"{verdict} has no message for the parent");
        }
    }

    // ------------------------------------------------ 08B: more web input

    [Theory]
    [InlineData("powershell:-Command Get-Process")]
    [InlineData("cmd:/c dir")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("jar:http://x/y!/z")]
    [InlineData("ms-windows-store://home")]
    [InlineData("search-ms:query=x")]
    [InlineData("ldap://x/")]
    [InlineData("ftp://files.example.com")]
    [InlineData(@"\\server\share")]
    public void A_scheme_that_is_not_the_web_is_refused(string input)
    {
        var (result, _) = WebAllowlist.Normalize(input);

        Assert.NotEqual(UrlValidation.Ok, result);
    }

    [Theory]
    [InlineData("https://example.com", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("sub.example.com", "sub.example.com")]
    [InlineData("EXAMPLE.COM", "example.com")]
    [InlineData("https://example.com:8443", "example.com")]
    [InlineData("https://example.com/barn/spel", "example.com")]
    [InlineData("https://example.com/?q=1", "example.com")]
    [InlineData("https://example.com/#top", "example.com")]
    [InlineData("  https://example.com/  ", "example.com")]
    public void Everything_around_the_host_is_discarded(string input, string expected)
    {
        var (result, host) = WebAllowlist.Normalize(input);

        Assert.Equal(UrlValidation.Ok, result);
        Assert.Equal(expected, host);
    }

    /// <summary>
    /// A trailing dot is a valid way to write an absolute DNS name, and
    /// "svt.se." and "svt.se" are the same site. If they normalized
    /// differently, a parent could allow one and be surprised by the other.
    /// </summary>
    [Fact]
    public void A_trailing_dot_is_the_same_host()
    {
        var (withDot, hostWithDot) = WebAllowlist.Normalize("svt.se.");
        var (without, hostWithout) = WebAllowlist.Normalize("svt.se");

        if (withDot == UrlValidation.Ok && without == UrlValidation.Ok)
        {
            Assert.Equal(hostWithout, hostWithDot);
        }
        else
        {
            // Refusing it is also defensible; accepting it as a DIFFERENT
            // host is not.
            Assert.NotEqual(UrlValidation.Ok, withDot);
        }
    }

    /// <summary>
    /// An internationalised name has two spellings for the same site, and a
    /// policy written in one does not match a request made in the other.
    /// </summary>
    [Fact]
    public void An_international_name_normalizes_to_one_spelling()
    {
        var (unicode, unicodeHost) = WebAllowlist.Normalize("https://räksmörgås.se");
        var (punycode, punycodeHost) = WebAllowlist.Normalize("https://xn--rksmrgs-5wao1o.se");

        Assert.Equal(UrlValidation.Ok, unicode);
        Assert.Equal(UrlValidation.Ok, punycode);
        Assert.Equal(punycodeHost, unicodeHost);
    }

    [Fact]
    public void What_is_saved_is_the_host_and_nothing_else()
    {
        var (result, entry) = WebAllowlist.TryCreate("https://svt.se:443/barn?x=1#top", []);

        Assert.Equal(UrlValidation.Ok, result);
        Assert.NotNull(entry);
        Assert.Equal("svt.se", entry!.Host);
        Assert.DoesNotContain('/', entry.Host);
        Assert.DoesNotContain(':', entry.Host);
        Assert.DoesNotContain('?', entry.Host);
    }

    /// <summary>
    /// Rejected input must not reach policy generation by another route - a
    /// refusal that still leaves the text somewhere downstream is not a
    /// refusal.
    /// </summary>
    [Fact]
    public void Refused_input_creates_no_entry()
    {
        foreach (var bad in new[] { "javascript:alert(1)", "file:///C:/", "shell:AppsFolder", "cmd:/c dir" })
        {
            var (result, entry) = WebAllowlist.TryCreate(bad, []);

            Assert.NotEqual(UrlValidation.Ok, result);
            Assert.Null(entry);
        }
    }
}
