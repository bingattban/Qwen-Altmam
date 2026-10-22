namespace AlTamam.Infrastructure.Persistence;

/// <summary>
/// Resolves the local writable application-data layout on Windows.
/// Installed mode stores the encrypted database under %LOCALAPPDATA% (per user);
/// portable mode stores it in a "data" folder beside the executable.
/// The database is NEVER placed under Program Files (read-only for non-admins).
/// Installation resources (templates, bundled assets) are read-only and live
/// under the installation directory (<see cref="ResourceRoot"/>).
/// </summary>
public static class AppDataLayout
{
    public const string ProductFolderName = "AlTamam";
    public const string DatabaseFileName = "altamam.db";

    /// <summary>True when running as a portable build (data lives beside the executable).</summary>
    public static bool IsPortable { get; set; }

    /// <summary>Directory that contains the running executable — read-only resource root.</summary>
    public static string ResourceRoot => AppContext.BaseDirectory;

    /// <summary>The single writable data directory for this installation mode.</summary>
    public static string DataRoot
    {
        get
        {
            if (IsPortable)
                return Path.Combine(ResourceRoot, "data");
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
