using AlTamam.Domain.Policies;
using Xunit;

namespace AlTamam.Tests.Security;

/// <summary>
/// Section 03.09: passwords are strong printable secrets, never contain hidden
/// control characters that can be silently stripped by a form or clipboard.
/// </summary>
public sealed class Section03PasswordControlCharacterTests
{
    [Theory]
    [InlineData("Abc123!\\0")]
    [InlineData("Abc123!\\u001B")]
    [InlineData("Abc123!\\u007F")]
    [InlineData("Abc123!\\r")]
    public void PasswordPolicy_RejectsInvisibleControlCharacters(string encoded)
    {
        string password = encoded.Replace("\\0", "\0", StringComparison.Ordinal)
            .Replace("\\u001B", "\u001B", StringComparison.Ordinal)
            .Replace("\\u007F", "\u007F", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal);
        Assert.False(PasswordPolicyValidator.Validate(password).IsValid);
    }

    [Theory]
    [InlineData("Abc123!secure")]
    [InlineData("ABC123#xyz")]
    public void PasswordPolicy_StillAcceptsStrongPrintablePasswords(string password)
        => Assert.True(PasswordPolicyValidator.Validate(password).IsValid);
}
