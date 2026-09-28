namespace KidShell.Core.Launching;

/// <summary>
/// How an approved application is started.
///
/// WHY THIS IS STORED RATHER THAN INFERRED
/// ---------------------------------------
/// It used to be guessed from the string: a colon somewhere near the front
/// meant "protocol", anything else meant "path". That guess is the whole of
/// OPSV finding 03. A parent picking Calculator out of the installed-app list
/// got its packaged identity written into the same field a hand-typed path
/// goes in, and the field was then validated by the rule for hand-typed paths
/// - which requires .exe. So the browse list offered Store apps and the add
/// flow refused every one of them.
///
/// The kinds are genuinely different inputs with different trust properties,
/// different identities and different launch mechanisms. Recording which one
/// it is costs one enum and removes a class of bug rather than an instance.
/// </summary>
public enum ApplicationLaunchKind
{
    /// <summary>
    /// A classic program with an executable on disk.
    ///
    /// The default, so that a configuration written before this field existed
    /// deserialises to what it actually held.
    /// </summary>
    Win32Executable = 0,

    /// <summary>
    /// An MSIX/Store application, identified by its Application User Model ID.
    ///
    /// There is usually no executable a parent could point at, and the AUMID
    /// is the identity Windows application control uses - so it is what is
    /// kept, rather than a path derived from it.
    /// </summary>
    PackagedApp = 1,

    /// <summary>
    /// A protocol or shell activation, such as <c>ms-settings:</c>.
    ///
    /// Carries no executable identity of its own: the scheme says which
    /// application is registered for it today, and that registration can
    /// change without the approval changing. Allowed only from an explicit
    /// list - see <see cref="LaunchTargetPolicy"/>.
    /// </summary>
    UriProtocol = 2
}
