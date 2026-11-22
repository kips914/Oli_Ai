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
            var settings = JsonSerializer.Deserialize<OliSettings>(File.ReadAllText(SettingsFile)) ?? new OliSettings();
            Upgrade(settings);
            return settings;
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

    /// <summary>
    /// Brings an older settings.json up to date: fills empty collections, adds new
    /// default sites, repairs out-of-range values. Never throws.
    /// </summary>
    private static void Upgrade(OliSettings settings)
    {
        try
        {
            settings.Telegram ??= new TelegramSettings();
            settings.Server ??= new ServerSettings();
            settings.Server.Port = Math.Clamp(settings.Server.Port, 1024, 65535);
            settings.Server.UserQuotaBytes = Math.Clamp(settings.Server.UserQuotaBytes,
                64L * 1024 * 1024, 50L * 1024 * 1024 * 1024);

            if (settings.AllowedSites is null || settings.AllowedSites.Count == 0)
                settings.AllowedSites = DefaultAllowedSites.Create();
            else
                foreach (var site in DefaultAllowedSites.Create())
                    if (!settings.AllowedSites.Any(s => s.Domain.Equals(site.Domain, StringComparison.OrdinalIgnoreCase)))
                        settings.AllowedSites.Add(site);

            if (settings.WebSearchBlockedDomains is null || settings.WebSearchBlockedDomains.Count == 0)
                settings.WebSearchBlockedDomains = DefaultAllowedSites.DefaultBlocked();

            if (settings.AllowedCommands is null || settings.AllowedCommands.Count == 0)
                settings.AllowedCommands = DefaultCommands.Create();

            if (string.IsNullOrWhiteSpace(settings.ModelGguf))
            {
                settings.ModelGguf = ModelCatalog.ById(ModelCatalog.DefaultId).Gguf;
                settings.ModelDisplayName = ModelCatalog.ById(ModelCatalog.DefaultId).DisplayName;
            }

            settings.ContextSize = Math.Clamp(settings.ContextSize, 2048, 32768);
            settings.CpuThreads = Math.Clamp(settings.CpuThreads, 1, 32);
            settings.GpuLayers = Math.Clamp(settings.GpuLayers, 0, 128);
            settings.WebSearchResultLimit = Math.Clamp(settings.WebSearchResultLimit, 1, 20);
            settings.WorkspaceQuotaBytes = Math.Clamp(settings.WorkspaceQuotaBytes, 16L * 1024 * 1024, 20L * 1024 * 1024 * 1024);
            settings.WorkspaceMaxFileBytes = Math.Clamp(settings.WorkspaceMaxFileBytes, 1024, settings.WorkspaceQuotaBytes);
            if (string.IsNullOrWhiteSpace(settings.GitHubRepositoryUrl) || settings.GitHubRepositoryUrl.Contains("REPLACE_ME"))
                settings.GitHubRepositoryUrl = "https://github.com/kips914/Oli_Ai";
        }
        catch (Exception ex)
        {
            OliLog.Warning("SETTINGS", $"Не удалось обновить настройки: {ex.Message}");
        }
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
