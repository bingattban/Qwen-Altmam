using AlTamam.Infrastructure.Persistence;
using Xunit;

namespace AlTamam.Tests.Persistence;

/// <summary>
/// Phase 01 step 01.07 — local writable AppData paths vs read-only installation
/// resources. The owner has approved a single per-Windows-user AppData root for
/// BOTH installer and direct-run Portable ZIP. No per-executable database branch
/// or automated migration is allowed. The tests run on Windows CI.
/// </summary>
public sealed class AppDataLayoutTests
{

    [Fact]
    public void InstalledMode_DataRoot_UnderLocalApplicationData()
    {
        string root = AppDataLayout.DataRoot;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(localAppData, root, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AppDataLayout.ProductFolderName, root);
        Assert.DoesNotContain("Program Files", root, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PortableAndInstaller_AlwaysShareWindowsLocalAppDataRoot()
    {
        string expected = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create), AppDataLayout.ProductFolderName);
        Assert.Equal(expected, AppDataLayout.DataRoot, StringComparer.OrdinalIgnoreCase);
        // No second per-executable write mode may silently point to a new database.
        Assert.Null(typeof(AppDataLayout).GetProperty("IsPortable",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
    }

    [Fact]
    public void DatabaseAndBackupPaths_RetainTheirExistingWindowsUserLocation()
    {
        var root = AppDataLayout.DataRoot;
        Assert.Equal(Path.Combine(root, AppDataLayout.DatabaseFileName),
            AppDataLayout.ResolveDatabasePath(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(root, "backups"), AppDataLayout.ResolveBackupDirectory(),
            StringComparer.OrdinalIgnoreCase);
        // An extracted ZIP beside another folder must not become an implicit data source.
        Assert.NotEqual(Path.Combine(AppContext.BaseDirectory, "data"),
            AppDataLayout.DataRoot, StringComparer.OrdinalIgnoreCase);
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

}
