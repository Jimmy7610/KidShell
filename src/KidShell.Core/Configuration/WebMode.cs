namespace KidShell.Core.Configuration;

/// <summary>
/// How much of the web the child is allowed to reach.
/// MVP 0.1 stores the choice only; nothing is enforced yet.
/// </summary>
public enum WebMode
{
    /// <summary>Ingen webbläsare.</summary>
    NoBrowser = 0,

    /// <summary>Endast godkända sidor.</summary>
    Allowlist = 1,

    /// <summary>Friare webb.</summary>
    Open = 2
}
