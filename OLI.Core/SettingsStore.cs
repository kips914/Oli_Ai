using System.Text.Json;

namespace OLI.Core;

public static class SettingsStore
{
    public static string Root => AppPaths.InstallRoot;
    public static string DataRoot => AppPaths.DataRoot;
    public static string SettingsFile => AppPaths.SettingsFile;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static OliSettings Load()
    {
        AppPaths.EnsureDirectories();
        MigrateLegacyData();
        try
        {
            if (!File.Exists(SettingsFile)) return new OliSettings();
            return JsonSerializer.Deserialize<OliSettings>(File.ReadAllText(SettingsFile)) ?? new OliSettings();
        }
        catch (Exception ex)
        {
            OliLog.Warning("SETTINGS", $"Не удалось прочитать настройки: {ex.Message}");
            TryBackupCorrupt(SettingsFile);
            return new OliSettings();
        }
    }

    public static void Save(OliSettings settings)
    {
        AppPaths.EnsureDirectories();
        AtomicWrite(SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static void MigrateLegacyData()
    {
        var legacyRoot = Path.Combine(AppPaths.InstallRoot, "data");
        if (!Directory.Exists(legacyRoot) || Directory.Exists(Path.Combine(AppPaths.DataRoot, "migration-v1.3.1.done"))) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(legacyRoot, file);
                var destination = Path.Combine(AppPaths.DataRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination)) File.Copy(file, destination);
            }
            File.WriteAllText(Path.Combine(AppPaths.DataRoot, "migration-v1.3.1.done"), DateTimeOffset.Now.ToString("O"));
            OliLog.Info("DATA", "Выполнена миграция данных старой версии в %LOCALAPPDATA%\\OLI");
        }
        catch (Exception ex) { OliLog.Warning("DATA", $"Миграция старых данных не завершена: {ex.Message}"); }
    }

    public static void ClearCache()
    {
        if (Directory.Exists(AppPaths.CacheRoot)) Directory.Delete(AppPaths.CacheRoot, true);
        Directory.CreateDirectory(AppPaths.CacheRoot);
        OliLog.Info("CACHE", "Кэш очищен");
    }

    public static void ResetUserData()
    {
        foreach (var dir in new[] { AppPaths.MemoryRoot, AppPaths.ConversationsRoot, Path.Combine(DataRoot, "user"), AppPaths.CacheRoot })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        AppPaths.EnsureDirectories();
        AtomicWrite(AppPaths.MemoryFile, "[]");
        AtomicWrite(AppPaths.ProfileFile, "{}");
        OliLog.Info("DATA", "Пользовательские данные сброшены");
    }

    public static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
    }

    private static void TryBackupCorrupt(string path)
    {
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".corrupt.bak", true);
        }
        catch { }
    }
}
