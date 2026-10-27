using System.Security.Cryptography;

namespace AlTamam.Infrastructure.Security;

/// <summary>
/// Typed, user-actionable offline startup errors. Keep machine-bound DPAPI
/// loss distinct from missing storage or a missing WebView2 prerequisite;
/// no SQLCipher/DPAPI key values are ever returned to the WPF view.
/// </summary>
public static class DatabaseStartupFailureClassifier
{
    public const string MissingEncryptionKey =
        "تعذر فتح قاعدة البيانات لعدم توفر مفتاح التشفير المطلوب؛ يمكن المحاولة باستخدام نسخة استعادة صالحة";
    public const string InvalidStoragePath =
        "هذا المسار غير صالح أو غير متاح، يُرجى تحديد مسار سليم وصحيح";

    public static string? UserMessage(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            CryptographicException => MissingEncryptionKey,
            UnauthorizedAccessException or DirectoryNotFoundException => InvalidStoragePath,
            IOException and not FileNotFoundException => InvalidStoragePath,
            _ => null
        };
    }
}
