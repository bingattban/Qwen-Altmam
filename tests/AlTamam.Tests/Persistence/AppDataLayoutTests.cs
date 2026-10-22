using AlTamam.Infrastructure.Persistence;
using Xunit;

namespace AlTamam.Tests.Persistence;

/// <summary>
/// Phase 01 step 01.07 — local writable AppData paths vs read-only installation
/// resources; portable/install distinction; never store the database under
/// Program Files. Path-shape assertions are platform-safe so they also run in
/// Linux CI smoke jobs; full Windows behavior is proven on windows-latest.
/// </summary>
public sealed class AppDataLayoutTests : IDisposable
{
    private readonly bool _originalPortable = AppDataLayout.IsPortable;

    [Fact]
    public void InstalledMode_DataRoot_UnderLocalApplicationData()
    {
        AppDataLayout.IsPortable = false;
        string root = AppDataLayout.DataRoot;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localAppData, root, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AppDataLayout.ProductFolderName, root);
        Assert.DoesNotContain("Program Files", root, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PortableMode_DataRoot_SitsBesideExecutable()
    {
        AppDataLayout.IsPortable = true;
        try
        {
            string root = AppDataLayout.DataRoot;
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "data"), root);
        }
        finally
        {
            AppDataLayout.IsPortable = false;
        }
    }

    [Fact]
    public void ResourceRoot_IsExecutableDirectory_ReadonlyResourceLocation()
    {
        Assert.Equal(AppContext.BaseDirectory, AppDataLayout.ResourceRoot);
    }

    [Theory]
    [InlineData("/tmp/altamam-test.db")]
    [InlineData(@"C:\Users\tester\AppData\Local\AlTamam\altamam.db")]
    public void AssertWritableLocation_AcceptsUserDataPaths(string path)
    {
        AppDataLayout.AssertWritableLocation(path); // must not throw
    }

    [Theory]
    [InlineData(@"C:\Program Files\AlTamam\data\altamam.db")]
    [InlineData(@"C:\PROGRAM FILES\ALTAMAM\DB\ALTAMAM.DB")]
    [InlineData(@"C:\Program Files (x86)\AlTamam\altamam.db")]
    public void AssertWritableLocation_RejectsProgramFilesPaths(string path)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AppDataLayout.AssertWritableLocation(path));
        Assert.Contains("program files", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssertWritableLocation_RejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => AppDataLayout.AssertWritableLocation(" "));
    }

    public void Dispose() => AppDataLayout.IsPortable = _originalPortable;
}
