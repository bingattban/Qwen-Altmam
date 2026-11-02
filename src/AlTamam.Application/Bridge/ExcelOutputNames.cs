using System.Globalization;

namespace AlTamam.Application.Bridge;

/// <summary>
/// File-name captions come exclusively from authoritative SQLCipher records.
/// Sanitization is deliberately platform-independent: the same names are safe
/// on offline x86/x64 Windows and in cross-platform synthetic tests.
/// </summary>
internal static class ExcelOutputNames
{
    internal static string Caption(string? value, int maxLength = 55)
    {
        string input = value?.Trim() ?? "";
        var clean = new string(input.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '(' or ')'
                ? ch : '_').ToArray()).Trim(' ', '_', '-');
        if(clean.Length > maxLength)
            clean = clean[..maxLength].Trim(' ', '_', '-');
        return clean.Length == 0 ? "غير محدد" : clean;
    }

    internal static string Monthly(int year, int month, string siteName)
    {
        string unique = Guid.NewGuid().ToString("N")[..8];
        return $"التمام الشهري_{Caption(siteName)}_{year:D4}-{month:D2}_" +
               DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture) +
               $"_{unique}.xlsx";
    }

    internal static string NamedReport(string description, string id)
        => Caption(description, 70) + "_" +
           DateTime.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture) +
           "_" + id[..12] + ".xlsx";

    internal static string ImportIssues(string uploadedFileName)
    {
        var input = uploadedFileName;
        if(input.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase))
            input = input[..^5];
        return "أخطاء الاستيراد_" + Caption(input, 42) + "_" +
               DateTime.Now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture) +
               "_" + Guid.NewGuid().ToString("N")[..8] + ".xlsx";
    }
}
