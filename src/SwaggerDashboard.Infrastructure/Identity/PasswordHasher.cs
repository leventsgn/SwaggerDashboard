using System.Security.Cryptography;
using System.Text;

namespace SwaggerDashboard.Infrastructure.Identity;

/// <summary>
/// PBKDF2 password hashing for dashboard accounts.
/// </summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 210_000;

    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    public static (string Hash, string Salt, int Iterations) Hash(string password, int? iterations = null)
    {
        var rounds = iterations ?? DefaultIterations;
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Derive(password, salt, rounds);

        return (Convert.ToBase64String(key), Convert.ToBase64String(salt), rounds);
    }

    public static bool Verify(string password, string hash, string salt, int iterations)
    {
        byte[] saltBytes;
        byte[] expected;

        try
        {
            saltBytes = Convert.FromBase64String(salt);
            expected = Convert.FromBase64String(hash);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Derive(password, saltBytes, iterations);

        // Fixed time comparison keeps the check from leaking how much of the hash matched.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
}
