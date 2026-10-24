using AlTamam.Domain.Policies;

namespace AlTamam.Application.Bridge;

/// <summary>
/// الرقم (ع) is an identifier of at least four numeric digits, not the
/// ordinary row serial number in printed attendance rosters.
/// Store ASCII digits in SQLCipher to keep duplicate checks unambiguous.
/// </summary>
internal static class ServiceNumberPolicy
{
    public static bool TryCanonicalize(string? input, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        input = input.Trim();
        if (input.Length is < 4 or > 70) return false;
        var chars = new char[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c >= '0' && c <= '9') chars[i] = c;
            else if (c >= '\u0660' && c <= '\u0669') chars[i] = (char)('0' + c - '\u0660');
            else if (c >= '\u06F0' && c <= '\u06F9') chars[i] = (char)('0' + c - '\u06F0');
            else return false;
        }
        value = new string(chars);
        return true;
    }
}
