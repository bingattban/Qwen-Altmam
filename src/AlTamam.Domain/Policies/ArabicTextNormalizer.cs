using System.Text;

namespace AlTamam.Domain.Policies;

/// <summary>
/// Performs non-destructive comparison/search normalization for Arabic text and digits
/// while the raw user-entered string is preserved in display columns.
/// </summary>
public static class ArabicTextNormalizer
{
    public static string NormalizeForSearch(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var trimmed = input.Trim();
        var sb = new StringBuilder(trimmed.Length);
        bool lastWasSpace = false;

        foreach (char c in trimmed)
        {
            // Skip Arabic diacritics (Tashkeel) and Tatweel
            if ((c >= '\u064B' && c <= '\u065F') || c == '\u0670' || c == '\u0640')
                continue;

            char normalized = c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                'ؤ' => 'و',
                'ئ' => 'ي',
                // Eastern Arabic-Indic digits (٠-٩) -> Western (0-9)
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                // Extended Arabic-Indic digits (۰-۹) -> Western (0-9)
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                _ => char.ToLowerInvariant(c)
            };

            if (char.IsWhiteSpace(normalized))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(normalized);
                lastWasSpace = false;
            }
        }

        return sb.ToString();
    }

    public static string NormalizeDigitsOnly(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var sb = new StringBuilder(input.Length);
        foreach (char c in input.Trim())
        {
            if (c >= '٠' && c <= '٩')
                sb.Append((char)('0' + (c - '٠')));
            else if (c >= '۰' && c <= '۹')
                sb.Append((char)('0' + (c - '۰')));
            else if (!char.IsWhiteSpace(c))
                sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }
}
