using System.Security.Cryptography;
using System.Text;

namespace AlTamam.Infrastructure.Security;

/// <summary>
/// Versioned, offline, password-wrapped 256-bit SQLCipher recovery key.
/// The recovery file is independent from machine-bound DPAPI and MUST be kept
/// separate from the database backup. No app UI should ever receive raw key bytes.
/// </summary>
public static class PortableRecoveryKeyPackage
{
    private static readonly byte[] Magic=Encoding.ASCII.GetBytes("ALTAMAM-RECOVERY-KEY-V1");
    private const int SaltLength=32,NonceLength=12,TagLength=16,KeyLength=32;
    private const int KdfIterations=600_000;
    private static int SaltOffset=>Magic.Length;
    private static int NonceOffset=>SaltOffset+SaltLength;
    private static int TagOffset=>NonceOffset+NonceLength;
    private static int CipherOffset=>TagOffset+TagLength;
    public static int FileLength=>CipherOffset+KeyLength;

    /// <summary>Creates the encrypted package contents; caller controls its own
    /// secure native file picker. The password is never stored in the package.</summary>
    public static byte[] Seal(string keyHex,string passphrase)
    {
        CheckPassphrase(passphrase);
        if(keyHex is null || keyHex.Length!=64)
            throw new ArgumentException("SQLCipher key must have 64 hex digits.",nameof(keyHex));
        byte[] rawKey;
        try{rawKey=Convert.FromHexString(keyHex);}
        catch(FormatException ex){throw new ArgumentException("Invalid SQLCipher key.",nameof(keyHex),ex);}
        byte[] kdfKey=Array.Empty<byte>();
        try
        {
            byte[] package=new byte[FileLength];
            Magic.CopyTo(package,0);
            RandomNumberGenerator.Fill(package.AsSpan(SaltOffset,SaltLength));
            RandomNumberGenerator.Fill(package.AsSpan(NonceOffset,NonceLength));
            kdfKey=Rfc2898DeriveBytes.Pbkdf2(passphrase,
                package.AsSpan(SaltOffset,SaltLength),KdfIterations,HashAlgorithmName.SHA256,KeyLength);
            using var aes=new AesGcm(kdfKey,TagLength);
            aes.Encrypt(package.AsSpan(NonceOffset,NonceLength),rawKey,
                package.AsSpan(CipherOffset,KeyLength),package.AsSpan(TagOffset,TagLength),
                package.AsSpan(0,NonceOffset));
            return package;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawKey);
            if(kdfKey.Length>0)CryptographicOperations.ZeroMemory(kdfKey);
        }
    }

    /// <summary>Decrypt with a user-held password on ANY machine. An incorrect
    /// password or damaged package produces the same generic error.</summary>
    public static string Unseal(ReadOnlySpan<byte> package,string passphrase)
    {
        CheckPassphrase(passphrase);
        if(package.Length!=FileLength ||
           !package.Slice(0,Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("حزمة الاسترداد غير صالحة أو غير معروفة.");
        byte[] derived=Array.Empty<byte>(),raw=new byte[KeyLength];
        try
        {
            derived=Rfc2898DeriveBytes.Pbkdf2(passphrase,
                package.Slice(SaltOffset,SaltLength),KdfIterations,
                HashAlgorithmName.SHA256,KeyLength);
            using var aes=new AesGcm(derived,TagLength);
            aes.Decrypt(package.Slice(NonceOffset,NonceLength),
                package.Slice(CipherOffset,KeyLength),
                package.Slice(TagOffset,TagLength),raw,package.Slice(0,NonceOffset));
            return Convert.ToHexString(raw);
        }
        catch(CryptographicException)
        {
            throw new CryptographicException(
                "تعذر فتح حزمة الاسترداد؛ كلمة المرور غير صحيحة أو الحزمة تالفة.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
            if(derived.Length>0)CryptographicOperations.ZeroMemory(derived);
        }
    }

    private static void CheckPassphrase(string? password)
    {
        if(string.IsNullOrWhiteSpace(password)||password.Length<16||password.Length>1024)
            throw new ArgumentException("Recovery passphrase must be 16–1024 characters.");
    }
}
