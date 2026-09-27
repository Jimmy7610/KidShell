namespace KidShell.Core.Runtime;

/// <summary>
/// Windows path semantics, decided by KidShell rather than by whatever host
/// the code happens to be running on.
///
/// WHY NOT System.IO.Path
/// ----------------------
/// Because it answers about the host, and the question is about the target.
/// <c>Path.IsPathRooted(@"C:\Program Files\App\app.exe")</c> is true on
/// Windows and FALSE on Linux, where a backslash is an ordinary character in a
/// file name. The policy builder used it to decide whether a configured
/// program had a real, full path, so the same configuration produced a
/// different policy depending on where the test ran - nine of them failed on
/// the audit's machine for exactly this reason.
///
/// The policy is always about a Windows machine, whatever is generating it.
/// These rules are therefore fixed, small and testable, and they give the same
/// answer everywhere.
/// </summary>
public static class WindowsPath
{
    /// <summary>
    /// Whether this is a fully qualified Windows path: a drive-letter path, a
    /// UNC path, or one anchored to an AppLocker path variable.
    ///
    /// A bare command name like "calc.exe" is not, and neither is a relative
    /// one - both are resolved by Windows at launch time from somewhere this
    /// policy cannot see, so no meaningful rule can be written for them.
    /// </summary>
    public static bool IsFullyQualified(string? path)
    {
        var value = Clean(path);

        if (value.Length == 0)
        {
            return false;
        }

        // An AppLocker variable: %WINDIR%\..., %SYSTEM32%\..., %PROGRAMFILES%\...
        if (value.StartsWith('%') && value.IndexOf('%', 1) > 0)
        {
            return true;
        }

        // \\server\share\... - a UNC path.
        if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return value.Length > 2;
        }

        // C:\... - a drive letter, a colon, then a separator. "C:app.exe" is
        // drive-relative and resolves against that drive's current directory,
        // which is not something a policy can pin down.
        return value.Length >= 3 &&
               char.IsAsciiLetter(value[0]) &&
               value[1] == ':' &&
               IsSeparator(value[2]);
    }

    /// <summary>
    /// The file name at the end of a Windows path.
    ///
    /// Deliberately not <c>Path.GetFileName</c>: on Linux that returns the
    /// whole string for a backslash-separated path, because it sees one long
    /// file name. This splits on both separators, as Windows does.
    /// </summary>
    public static string FileName(string? path)
    {
        var value = Clean(path);

        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (IsSeparator(value[i]))
            {
                return value[(i + 1)..];
            }
        }

        return value;
    }

    /// <summary>The path with its final segment removed, or empty.</summary>
    public static string DirectoryName(string? path)
    {
        var value = Clean(path);

        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (IsSeparator(value[i]))
            {
                return value[..i];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Compares two Windows paths as Windows would: case-insensitively, and
    /// treating both separators as the same character.
    /// </summary>
    public static bool AreEquivalent(string? left, string? right) =>
        string.Equals(Canonical(left), Canonical(right), StringComparison.Ordinal);

    /// <summary>
    /// A comparable form: trimmed of quotes and spaces, forward slashes turned
    /// into backslashes, upper-cased with the invariant culture.
    ///
    /// Invariant rather than current culture on purpose. Turkish lower-cases
    /// "I" to a dotless one, so a machine in that locale would decide two
    /// identical paths differed - and a security rule that depends on the
    /// user's language is a security rule that fails somewhere.
    /// </summary>
    public static string Canonical(string? path) =>
        Clean(path).Replace('/', '\\').ToUpperInvariant();

    private static bool IsSeparator(char c) => c is '\\' or '/';

    private static string Clean(string? path) =>
        (path ?? string.Empty).Trim().Trim('"').Trim();
}
