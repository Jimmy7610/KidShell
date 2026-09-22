namespace KidShell.Core.Web;

/// <summary>Why an allowlist entry was rejected.</summary>
public enum UrlValidation
{
    Ok = 0,
    Empty = 1,

    /// <summary>A scheme KidShell will not allow, e.g. file: or javascript:.</summary>
    UnsupportedScheme = 2,

    /// <summary>Not parseable as a host or URL at all.</summary>
    Malformed = 3,

    /// <summary>A bare IP address. Allowed, but recorded distinctly.</summary>
    IpAddress = 4,

    /// <summary>Already present in the list.</summary>
    Duplicate = 5
}

/// <summary>One approved destination.</summary>
public sealed record AllowlistEntry
{
    /// <summary>Canonical host, lower-case, without scheme, port or path.</summary>
    public required string Host { get; init; }

    /// <summary>Whether subdomains are included.</summary>
    public bool IncludeSubdomains { get; init; } = true;

    /// <summary>What the parent typed, kept so the UI can show it back.</summary>
    public string OriginalInput { get; init; } = string.Empty;

    /// <summary>The Edge URLAllowlist pattern for this entry.</summary>
    public string ToPolicyPattern() => IncludeSubdomains ? Host : $"||{Host}^";
}

/// <summary>
/// Normalizes and validates web allowlist entries.
///
/// The awkward part is that parents type all of these meaning the same thing:
///
///     svt.se   www.svt.se   https://svt.se/   HTTPS://SVT.SE   svt.se/barn
///
/// and a list that treats them as five different sites is useless. Everything
/// is reduced to a host, lower-cased, with the scheme, port, path, query and a
/// leading "www." removed.
///
/// Schemes that are not http(s) are refused rather than normalized away.
/// "file:///C:/" in an allowlist would be a file browser, and
/// "javascript:" would be a script the parent did not read.
/// </summary>
public static class WebAllowlist
{
    /// <summary>Schemes that may appear in an allowlist entry.</summary>
    private static readonly string[] AllowedSchemes = ["http", "https"];

    /// <summary>
    /// Schemes refused outright. Each is a way out of the browser rather than
    /// a place on the web.
    /// </summary>
    private static readonly string[] DangerousSchemes =
    [
        "file", "javascript", "data", "vbscript", "about", "chrome",
        "ms-settings", "shell", "search-ms", "ms-appinstaller"
    ];

    /// <summary>
    /// Reduces an entry to a canonical host.
    ///
    /// Returns <see cref="UrlValidation.Ok"/> and the host, or the reason it
    /// could not be used.
    /// </summary>
    public static (UrlValidation Result, string Host) Normalize(string? input)
    {
        var trimmed = (input ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return (UrlValidation.Empty, string.Empty);
        }

        // A scheme-looking prefix is checked before anything else, so
        // "javascript:alert(1)" is refused rather than mangled into a host.
        var schemeSeparator = trimmed.IndexOf("://", StringComparison.Ordinal);
        var colon = trimmed.IndexOf(':');

        if (schemeSeparator > 0)
        {
            var scheme = trimmed[..schemeSeparator].ToLowerInvariant();

            if (!AllowedSchemes.Contains(scheme))
            {
                return (UrlValidation.UnsupportedScheme, string.Empty);
            }

            trimmed = trimmed[(schemeSeparator + 3)..];
        }
        else if (colon > 0 && !char.IsAsciiDigit(trimmed[colon + 1 < trimmed.Length ? colon + 1 : colon]))
        {
            // "javascript:..." and friends have a colon but no "//".
            var scheme = trimmed[..colon].ToLowerInvariant();

            if (DangerousSchemes.Contains(scheme))
            {
                return (UrlValidation.UnsupportedScheme, string.Empty);
            }
        }

        // Strip credentials, path, query and fragment.
        var at = trimmed.IndexOf('@');
        if (at >= 0)
        {
            trimmed = trimmed[(at + 1)..];
        }

        foreach (var terminator in new[] { '/', '?', '#' })
        {
            var index = trimmed.IndexOf(terminator);

            if (index >= 0)
            {
                trimmed = trimmed[..index];
            }
        }

        // Strip the port, but not an IPv6 literal's colons.
        if (!trimmed.StartsWith('[') && trimmed.LastIndexOf(':') is var portColon && portColon > 0)
        {
            trimmed = trimmed[..portColon];
        }

        trimmed = trimmed.Trim().TrimEnd('.').ToLowerInvariant();

        if (trimmed.Length == 0)
        {
            return (UrlValidation.Malformed, string.Empty);
        }

        // "www." is noise: a parent allowing svt.se means www.svt.se too.
        if (trimmed.StartsWith("www.", StringComparison.Ordinal) && trimmed.Length > 4)
        {
            trimmed = trimmed[4..];
        }

        if (System.Net.IPAddress.TryParse(trimmed.Trim('[', ']'), out _))
        {
            // Valid, but worth distinguishing: an IP has no subdomains and
            // will not survive the site moving.
            return (UrlValidation.IpAddress, trimmed);
        }

        if (!IsPlausibleHost(trimmed))
        {
            return (UrlValidation.Malformed, string.Empty);
        }

        return (UrlValidation.Ok, trimmed);
    }

    /// <summary>Adds an entry, refusing duplicates.</summary>
    public static (UrlValidation Result, AllowlistEntry? Entry) TryCreate(
        string? input,
        IEnumerable<AllowlistEntry> existing,
        bool includeSubdomains = true)
    {
        var (result, host) = Normalize(input);

        if (result is not (UrlValidation.Ok or UrlValidation.IpAddress))
        {
            return (result, null);
        }

        if (existing.Any(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase)))
        {
            return (UrlValidation.Duplicate, null);
        }

        return (result, new AllowlistEntry
        {
            Host = host,
            IncludeSubdomains = includeSubdomains && result != UrlValidation.IpAddress,
            OriginalInput = (input ?? string.Empty).Trim()
        });
    }

    /// <summary>
    /// Whether a URL would be permitted by the list. Used to explain a
    /// decision to a parent, not to enforce anything.
    /// </summary>
    public static bool Permits(IEnumerable<AllowlistEntry> allowlist, string url)
    {
        var (result, host) = Normalize(url);

        if (result is not (UrlValidation.Ok or UrlValidation.IpAddress))
        {
            return false;
        }

        foreach (var entry in allowlist)
        {
            if (string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // "svt.se" with subdomains covers "barn.svt.se" but must not
            // cover "notsvt.se" - the dot is what makes it a subdomain.
            if (entry.IncludeSubdomains &&
                host.EndsWith("." + entry.Host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPlausibleHost(string host)
    {
        if (host.Length > 253 || !host.Contains('.'))
        {
            // A single label with no dot is a machine name, not a website.
            return false;
        }

        return host.Split('.').All(label =>
            label.Length is > 0 and <= 63 &&
            label.All(c => char.IsLetterOrDigit(c) || c == '-') &&
            !label.StartsWith('-') &&
            !label.EndsWith('-'));
    }

    /// <summary>Parent-facing wording for a rejection.</summary>
    public static string Describe(UrlValidation result) => result switch
    {
        UrlValidation.Empty => "Skriv en webbadress.",
        UrlValidation.UnsupportedScheme => "Bara vanliga webbadresser (http och https) kan läggas till.",
        UrlValidation.Malformed => "Det där ser inte ut som en webbadress.",
        UrlValidation.Duplicate => "Sidan finns redan i listan.",
        UrlValidation.IpAddress => "Tillagd som IP-adress. Den fungerar inte om sidan flyttar.",
        _ => string.Empty
    };
}
