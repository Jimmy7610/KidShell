using KidShell.Core.Web;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// What a parent may type into the allowlist, and what must be refused.
///
/// The Webb page used to run typed input through a local string trim that
/// stripped a scheme prefix and accepted whatever was left. That turned
/// "javascript:alert(1)" into the host "alert(1)" and added it - a dangerous
/// scheme laundered into the allowlist by the sanitiser meant to stop it.
///
/// These tests pin the rule that replaced it: input is validated, not
/// sanitised. Anything that is not a host is refused, with a reason.
/// </summary>
public class WebAllowlistEntryTests
{
    private static (UrlValidation Result, AllowlistEntry? Entry) Create(
        string input, params string[] existingHosts)
    {
        var existing = existingHosts
            .Select(h => new AllowlistEntry { Host = h })
            .ToList();

        return WebAllowlist.TryCreate(input, existing);
    }

    [Theory]
    [InlineData("svt.se")]
    [InlineData("https://svt.se")]
    [InlineData("http://www.svt.se/barn")]
    [InlineData("  SVT.se  ")]
    public void Ordinary_input_becomes_the_host(string input)
    {
        var (result, entry) = Create(input);

        Assert.Equal(UrlValidation.Ok, result);
        Assert.Equal("svt.se", entry!.Host);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32")]
    [InlineData("shell:AppsFolder")]
    [InlineData("ms-settings:privacy")]
    [InlineData("data:text/html,<script>")]
    public void A_dangerous_scheme_is_refused_rather_than_stripped(string input)
    {
        // The whole point. Stripping the scheme and keeping the remainder is
        // how a sanitiser becomes an attack surface.
        var (result, entry) = Create(input);

        Assert.NotEqual(UrlValidation.Ok, result);
        Assert.Null(entry);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_is_refused(string input)
    {
        var (result, _) = Create(input);
        Assert.Equal(UrlValidation.Empty, result);
    }

    [Fact]
    public void A_duplicate_is_refused_and_named_as_such()
    {
        // A parent adding svt.se twice needs "already in the list", not silence
        // and not a second identical row.
        var (result, entry) = Create("svt.se", "svt.se");

        Assert.Equal(UrlValidation.Duplicate, result);
        Assert.Null(entry);
    }

    [Fact]
    public void A_duplicate_is_detected_across_different_spellings()
    {
        var (result, _) = Create("HTTPS://WWW.SVT.SE/", "svt.se");

        Assert.Equal(UrlValidation.Duplicate, result);
    }

    [Fact]
    public void An_IP_address_is_allowed_but_recorded_distinctly()
    {
        var (result, entry) = Create("192.168.1.10");

        Assert.Equal(UrlValidation.IpAddress, result);
        Assert.NotNull(entry);

        // Subdomains of an IP address are meaningless, so the entry does not
        // claim to cover them.
        Assert.False(entry!.IncludeSubdomains);
    }

    [Theory]
    [InlineData(UrlValidation.Empty)]
    [InlineData(UrlValidation.UnsupportedScheme)]
    [InlineData(UrlValidation.Malformed)]
    [InlineData(UrlValidation.Duplicate)]
    [InlineData(UrlValidation.IpAddress)]
    public void Every_refusal_has_parent_facing_wording(UrlValidation result)
    {
        var text = WebAllowlist.Describe(result);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("Exception", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("URL", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Success_says_nothing()
    {
        // Deliberately empty: a message after a successful add would be noise,
        // and the page shows the new row as the confirmation.
        Assert.Empty(WebAllowlist.Describe(UrlValidation.Ok));
    }

    [Fact]
    public void An_entry_that_covers_subdomains_permits_them()
    {
        var (_, entry) = Create("svt.se");
        var list = new[] { entry! };

        Assert.True(WebAllowlist.Permits(list, "https://barn.svt.se/klipp"));
        Assert.True(WebAllowlist.Permits(list, "svt.se"));
    }

    [Fact]
    public void A_lookalike_domain_is_not_permitted()
    {
        var (_, entry) = Create("svt.se");
        var list = new[] { entry! };

        // "notsvt.se" ends with "svt.se" as a string. Suffix matching without a
        // dot boundary is the classic way an allowlist leaks.
        Assert.False(WebAllowlist.Permits(list, "https://notsvt.se"));
        Assert.False(WebAllowlist.Permits(list, "https://svt.se.evil.com"));
    }

    [Fact]
    public void A_dangerous_scheme_is_never_permitted_even_if_the_host_matches()
    {
        var (_, entry) = Create("svt.se");
        var list = new[] { entry! };

        Assert.False(WebAllowlist.Permits(list, "javascript:alert('svt.se')"));
        Assert.False(WebAllowlist.Permits(list, "file://svt.se/share"));
    }
}
