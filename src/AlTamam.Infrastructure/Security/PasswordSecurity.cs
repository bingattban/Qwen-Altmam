using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace AlTamam.Infrastructure.Security;

public static class PasswordSecurity
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    public static (string HashBase64, string SaltBase64) HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return (Convert.ToBase64String(Derive(password, salt)), Convert.ToBase64String(salt));
    }

    public static bool VerifyPassword(string password, string expectedHashBase64, string saltBase64)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(expectedHashBase64) || string.IsNullOrEmpty(saltBase64))
            return false;
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(expectedHashBase64);
            if (salt.Length != SaltBytes || expected.Length != HashBytes) return false;
            return CryptographicOperations.FixedTimeEquals(Derive(password, salt), expected);
        }
        catch (FormatException) { return false; }
    }

    private static byte[] Derive(string password, byte[] salt)
    {
        using var algorithm = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = 2,
            Iterations = 3,
            MemorySize = 65536
        };
        return algorithm.GetBytes(HashBytes);
    }
}

public static class DatabaseKeyGenerator
{
    public static string Generate() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
