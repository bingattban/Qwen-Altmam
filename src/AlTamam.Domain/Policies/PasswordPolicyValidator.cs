namespace AlTamam.Domain.Policies;

/// <summary>Enforces minimum password strength without embedding operational credentials.</summary>
public static class PasswordPolicyValidator
{
    public static (bool IsValid, string? ArabicErrorMessage) Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return (false, "كلمة المرور مطلوبة ولا يمكن تركها فارغة.");
        if (password.Length < 6)
            return (false, "يجب أن تتكون كلمة المرور من 6 خانات على الأقل، تشمل ثلاثة أحرف إنجليزية وثلاثة أرقام.");
        if (password.Any(char.IsWhiteSpace) || password.Any(char.IsControl))
            return (false, "لا يُسمح بوجود مسافات أو محارف تحكم مخفية داخل كلمة المرور.");

        var letters = password.Count(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
        var digits = password.Count(c => c is >= '0' and <= '9');
        // English ASCII letters and digits are required; symbols remain optional.
        if (letters < 3 || digits < 3)
            return (false, "يجب أن تحتوي كلمة المرور على ثلاثة أحرف إنجليزية وثلاثة أرقام على الأقل.");
        return (true, null);
    }
}
