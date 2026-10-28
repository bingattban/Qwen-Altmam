namespace AlTamam.Infrastructure.Persistence;

/// <summary>
/// Resolves the local writable application-data layout on Windows.
/// Both direct-run Portable ZIP and Installer store the encrypted database under
/// the SAME per-Windows-user %LOCALAPPDATA% folder, independent of EXE location.
/// The database is NEVER placed under Program Files (read-only for non-admins).
/// Installation resources (templates, bundled assets) are read-only and live
/// under the installation directory (<see cref="ResourceRoot"/>).
/// </summary>
public static class AppDataLayout
{
    public const string ProductFolderName = "AlTamam";
    public const string DatabaseFileName = "altamam.db";

    // Owner decision (2026-10-28): packaging type must never select a second
    // writable data root. No implicit migration, copy or relocation is performed.

    /// <summary>Directory that contains the running executable — read-only resource root.</summary>
    public static string ResourceRoot => AppContext.BaseDirectory;

    /// <summary>The single writable data directory, shared by Portable and Installer.</summary>
    public static string DataRoot
    {
        get
        {
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create);
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new InvalidOperationException(
                    "Local application data folder is unavailable; cannot resolve a writable database location.");
            return Path.Combine(localAppData, ProductFolderName);
        }
    }

    /// <summary>Full path of the encrypted SQLite database file (parent directory created on demand).</summary>
    public static string ResolveDatabasePath()
    {
        string dir = DataRoot;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, DatabaseFileName);
    }

    /// <summary>Sibling directory for pre-migration/backup artifacts (never inside ResourceRoot).</summary>
    public static string ResolveBackupDirectory()
    {
        string dir = Path.Combine(DataRoot, "backups");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Guards against accidentally targeting a read-only program-files location.</summary>
    public static void AssertWritableLocation(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (string root in new[] { programFiles, programFilesX86 })
        {
            if (!string.IsNullOrWhiteSpace(root)
                && full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Refusing to place writable data under a program files location: '{full}'.");
        }
    }
}
