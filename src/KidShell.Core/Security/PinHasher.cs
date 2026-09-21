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
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

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

        if (iterations <= 0)
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
