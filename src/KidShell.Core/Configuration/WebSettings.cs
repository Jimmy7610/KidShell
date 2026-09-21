namespace KidShell.Core.Configuration;

/// <summary>
/// Web filtering configuration. MVP 0.1 persists the choice only; no browser
/// or Edge policy is touched.
/// </summary>
public sealed class WebSettings
{
    public WebMode Mode { get; set; } = WebMode.NoBrowser;

    public List<string> AllowedDomains { get; set; } = [];

    public WebSettings Clone() => new()
    {
        Mode = Mode,
        AllowedDomains = [.. AllowedDomains]
    };
}
