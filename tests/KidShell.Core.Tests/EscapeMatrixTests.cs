using KidShell.Core.Security;
using Xunit;

namespace KidShell.Core.Tests;

/// <summary>
/// The rules that keep the escape matrix honest.
///
/// A markdown table of green ticks drifts: a row gets marked protected when a
/// feature is written rather than when it is verified, and nothing notices.
/// These tests are what notices. They do not check that KidShell is secure -
/// they check that it does not claim to be.
/// </summary>
public class EscapeMatrixTests
{
    public static TheoryData<EscapeRoute> AllRoutes() => [.. EscapeMatrix.Routes];

    public static TheoryData<ProtectionMode> AllModes() =>
        [.. Enum.GetValues<ProtectionMode>()];

    [Fact]
    public void The_matrix_covers_every_route_the_documentation_promises()
    {
        // The spec lists the routes that must appear. A shorter matrix is a
        // matrix somebody trimmed to look better.
        var required = new[]
        {
            "Windows-tangenten", "Alt+Tab", "Alt+F4", "Aktivitetshanteraren", "Win+R", "Win+X",
            "Ctrl+Alt+Delete", "Öppna-dialogen", "Spara som", "Öppna mappen", "ShellExecute",
            "protokollhanterare", "Omdirigering", "Nedladdningar", "USB", "Genvägar",
            "URL-hanterare", "Underprocesser", "uppdaterare", "kraschar", "Starta om",
            "Viloläge", "klockan", "Windows Update"
        };

        foreach (var fragment in required)
        {
            Assert.Contains(EscapeMatrix.Routes,
                r => r.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public void Every_route_explains_itself(EscapeRoute route)
    {
        // A status with no reason is a number somebody can argue with and
        // nobody can check.
        Assert.False(string.IsNullOrWhiteSpace(route.Note));
        Assert.True(route.Note.Length > 20, $"route {route.Number} needs a real explanation");
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public void Every_route_is_named_in_Swedish_without_jargon(EscapeRoute route)
    {
        Assert.False(string.IsNullOrWhiteSpace(route.Name));
        Assert.DoesNotContain("0x", route.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HKEY", route.Note, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public void No_route_claims_protection_it_has_not_verified(EscapeRoute route)
    {
        // THE RULE THAT MATTERS. Protected means "verified blocked by reading
        // the state back", so a row may only claim it if somebody has actually
        // checked on hardware.
        var claimsProtection =
            route.AppOnly == ProtectionLevel.Protected ||
            route.Standard == ProtectionLevel.Protected ||
            route.Secure == ProtectionLevel.Protected;

        if (claimsProtection)
        {
            Assert.True(route.VerifiedOnDevice,
                $"route {route.Number} ({route.Name}) claims Protected without device verification");
        }
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public void Secure_mode_is_never_weaker_than_standard(EscapeRoute route)
    {
        // Secure is Standard plus Assigned Access, so a row where Secure is
        // worse is a data-entry error, not a finding.
        if (route.Standard == ProtectionLevel.Protected)
        {
            Assert.Equal(ProtectionLevel.Protected, route.Secure);
        }

        if (route.Standard == ProtectionLevel.Mitigated)
        {
            Assert.NotEqual(ProtectionLevel.NotProtected, route.Secure);
        }
    }

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public void Nothing_is_more_protected_before_Windows_is_configured(EscapeRoute route)
    {
        // A route cannot be better off with no Windows changes applied than
        // with them. If one looks that way, the row is wrong.
        if (route.AppOnly == ProtectionLevel.Protected)
        {
            Assert.Equal(ProtectionLevel.Protected, route.Standard);
        }
    }

    [Fact]
    public void Ctrl_Alt_Delete_is_marked_as_impossible_in_every_mode()
    {
        // Windows owns it. A product claiming to intercept it is wrong, and a
        // matrix that leaves this row hopeful is misleading.
        var route = EscapeMatrix.Routes.Single(r => r.Name.Contains("Ctrl+Alt+Delete", StringComparison.Ordinal));

        Assert.Equal(ProtectionLevel.CannotBeProtected, route.AppOnly);
        Assert.Equal(ProtectionLevel.CannotBeProtected, route.Standard);
        Assert.Equal(ProtectionLevel.CannotBeProtected, route.Secure);
    }

    [Fact]
    public void Booting_another_operating_system_is_marked_as_impossible()
    {
        var route = EscapeMatrix.Routes.Single(r => r.Name.Contains("USB eller återställningsmedia", StringComparison.Ordinal));

        Assert.Equal(ProtectionLevel.CannotBeProtected, route.Secure);
    }

    [Fact]
    public void The_app_only_mode_protects_almost_nothing_and_says_so()
    {
        // The uncomfortable number, asserted so nobody can quietly improve it
        // without doing the work.
        var protectedCount = EscapeMatrix.Count(ProtectionMode.AppOnly, ProtectionLevel.Protected);
        var notProtected = EscapeMatrix.Count(ProtectionMode.AppOnly, ProtectionLevel.NotProtected);

        Assert.True(protectedCount <= 2,
            $"app-only mode claims {protectedCount} protected routes; it protects almost nothing");

        Assert.True(notProtected > EscapeMatrix.Routes.Count / 2,
            "most routes should be open when Windows is untouched");
    }

    [Fact]
    public void The_app_only_summary_says_Windows_is_not_locked()
    {
        var summary = EscapeMatrix.Summarize(ProtectionMode.AppOnly);

        Assert.Contains("Windows är inte låst", summary);
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Every_mode_has_an_honest_summary(ProtectionMode mode)
    {
        var summary = EscapeMatrix.Summarize(mode);

        Assert.False(string.IsNullOrWhiteSpace(summary));

        // No mode may summarise itself as complete protection.
        Assert.DoesNotContain("helt skyddad", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("100", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProtectionLevel.Protected)]
    [InlineData(ProtectionLevel.Mitigated)]
    [InlineData(ProtectionLevel.NotProtected)]
    [InlineData(ProtectionLevel.RequiresStandard)]
    [InlineData(ProtectionLevel.RequiresSecure)]
    [InlineData(ProtectionLevel.RequiresDeviceTest)]
    [InlineData(ProtectionLevel.CannotBeProtected)]
    [InlineData(ProtectionLevel.NotApplicable)]
    public void Every_level_has_parent_facing_wording(ProtectionLevel level)
    {
        var text = EscapeMatrix.Describe(level);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.NotEqual("Okänt", text);
    }

    [Fact]
    public void Route_numbers_are_unique_and_contiguous()
    {
        var numbers = EscapeMatrix.Routes.Select(r => r.Number).ToList();

        Assert.Equal(numbers.Count, numbers.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers.Order());
    }

    [Fact]
    public void Secure_mode_still_leaves_routes_that_must_be_tested_on_hardware()
    {
        // A Secure column with no "needs a device test" left would mean
        // somebody decided the matrix was finished without running it.
        Assert.True(EscapeMatrix.Count(ProtectionMode.Secure, ProtectionLevel.RequiresDeviceTest) > 0);
    }
}
