using System.Security.Cryptography;
using AlTamam.Infrastructure.Security;
using Xunit;

namespace AlTamam.Tests.Security;

public sealed class DatabaseStartupFailureClassifierTests
{
    [Fact]
    public void CryptographicKeyLoss_IsDistinctFromInaccessibleFilesystem()
    {
        Assert.Equal(
            DatabaseStartupFailureClassifier.MissingEncryptionKey,
            DatabaseStartupFailureClassifier.UserMessage(
                new CryptographicException("inaccessible DPAPI material")));
        Assert.Equal(
            DatabaseStartupFailureClassifier.InvalidStoragePath,
            DatabaseStartupFailureClassifier.UserMessage(
                new DirectoryNotFoundException("E: unavailable")));
        Assert.NotEqual(DatabaseStartupFailureClassifier.MissingEncryptionKey,
            DatabaseStartupFailureClassifier.InvalidStoragePath);
    }

    [Fact]
    public void MissingLocalPrerequisite_IsNotMislabelledAsLostEncryptionKey()
    {
        Assert.Null(DatabaseStartupFailureClassifier.UserMessage(
            new FileNotFoundException("WebView2 installer missing",
                "MicrosoftEdgeWebView2RuntimeInstaller.exe")));
        Assert.Null(DatabaseStartupFailureClassifier.UserMessage(
            new InvalidOperationException("unrelated WebView2 startup failure")));
        Assert.Equal(DatabaseStartupFailureClassifier.InvalidStoragePath,
            DatabaseStartupFailureClassifier.UserMessage(new UnauthorizedAccessException()));
    }
}
