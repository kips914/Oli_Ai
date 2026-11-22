namespace OLI.Core;

public static class AppPaths
{
    public static string InstallRoot => AppContext.BaseDirectory;
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OLI");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string MemoryRoot => Path.Combine(DataRoot, "memory");
    public static string CacheRoot => Path.Combine(DataRoot, "cache");
    public static string ConversationsRoot => Path.Combine(DataRoot, "conversations");
    public static string LogsRoot => Path.Combine(DataRoot, "logs");
    public static string ProfileFile => Path.Combine(MemoryRoot, "user_profile.json");
    public static string MemoryFile => Path.Combine(MemoryRoot, "memory.json");

    /// <summary>Root of the model's file workspace (sandbox, 1 GB by default).</summary>
    public static string WorkspaceRoot => Path.Combine(DataRoot, "workspace");
    public static string WorkspaceFilesRoot => Path.Combine(WorkspaceRoot, "files");
    public static string WorkspaceArchiveRoot => Path.Combine(WorkspaceRoot, "archives");
    public static string WorkspaceTrashRoot => Path.Combine(WorkspaceRoot, "trash");
    public static string WorkspaceIndexFile => Path.Combine(WorkspaceRoot, "workspace.json");

    public static void EnsureDirectories()
    {
        foreach (var path in new[]
                 {
                     DataRoot, MemoryRoot, CacheRoot, ConversationsRoot, LogsRoot,
                     WorkspaceRoot, WorkspaceFilesRoot, WorkspaceArchiveRoot, WorkspaceTrashRoot
                 })
            Directory.CreateDirectory(path);
    }
}
