using System.Security.Cryptography;
using System.Text;

namespace KidShell.Core.Security;

/// <summary>
/// PBKDF2-SHA256 hashing for the parent PIN. The PIN itself is never stored;
/// only salt + hash + iteration count reach disk.
/// </summary>
public static class PinHasher
{
    public const int DefaultIterations = 210_000;

    /// <summary>
    /// The fewest iterations a stored hash may claim.
    ///
    /// OPSV FINDING 05C. The iteration count comes off disk, which means it
    /// comes from whoever can write that file - today, the child. A count of 1
    /// would make the stored hash cheap enough to attack offline, so a
    /// tampered-down value has to be refused rather than obeyed.
    ///
    /// 100,000 is below the current default, so an older configuration written
    /// by an earlier build still verifies, and well above the point where
    /// PBKDF2-SHA256 stops being worth attacking for a six-digit PIN.
    /// </summary>
    public const int MinimumIterations = 100_000;

    /// <summary>
    /// The most iterations a stored hash may claim.
    ///
    /// The other half of the same finding, and the more interesting one: a
    /// count of int.MaxValue is not a weak hash, it is a denial of service.
    /// PBKDF2 with two billion iterations pins a core for minutes, and it
    /// would be started by anyone typing a digit into the PIN pad - including
    /// the parent trying to undo the tampering.
    ///
    /// Ten times the default leaves room for a future build to raise the cost
    /// deliberately, and still bounds the work at something a person would
    /// describe as a slow unlock rather than a hang.
    /// </summary>
    public const int MaximumIterations = DefaultIterations * 10;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>
    /// Whether a persisted iteration count is one this build will act on.
    ///
    /// Checked BEFORE any derivation. Validating afterwards would mean the
    /// expensive work had already been done, which is precisely what the upper
    /// bound exists to prevent.
    /// </summary>
    public static bool IsAcceptableIterationCount(int iterations) =>
        iterations >= MinimumIterations && iterations <= MaximumIterations;

    public static (string Hash, string Salt) Hash(string pin, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(pin, salt, iterations);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string pin, string hashBase64, string saltBase64, int iterations)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(hashBase64) || string.IsNullOrEmpty(saltBase64))
        {
            return false;
        }

        byte[] expected;
        byte[] salt;
        try
        {
            expected = Convert.FromBase64String(hashBase64);
            salt = Convert.FromBase64String(saltBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        // Before Derive, never after. An out-of-range count is untrusted
        // input, and the whole point of the upper bound is that the work it
        // would cause must not be started.
        if (!IsAcceptableIterationCount(iterations))
        {
            return false;
        }

        var actual = Derive(pin, salt, iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Constant-time comparison for the development fallback PIN.</summary>
    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static byte[] Derive(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
