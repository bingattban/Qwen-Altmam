namespace AlTamam.Domain.Policies;

/// <summary>Enforces minimum password strength without embedding operational credentials.</summary>
public static class PasswordPolicyValidator
{
    public static (bool IsValid, string? ArabicErrorMessage) Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return (false, "كلمة المرور مطلوبة ولا يمكن تركها فارغة.");
        if (password.Length < 8)
            return (false, "يجب أن تتكون كلمة المرور من 8 خانات على الأقل.");
        if (password.Any(char.IsWhiteSpace))
            return (false, "لا يُسمح بوجود مسافات داخل كلمة المرور.");

        var letters = password.Count(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
        var digits = password.Count(c => c is >= '0' and <= '9');
        // Non-Latin letters and non-ASCII numerals are not special characters.
        var symbols = password.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && !char.IsControl(c));
        if (letters < 3 || digits < 3 || symbols < 1)
            return (false, "يجب أن تحتوي كلمة المرور على ثلاثة أحرف إنجليزية وثلاثة أرقام ورمز خاص على الأقل.");
        return (true, null);
    }
}
