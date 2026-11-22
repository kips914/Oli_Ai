using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using OLI.Core;
using OLI.Workspace;

namespace OLI.Server.Storage;

/// <summary>
/// One user's private file area on the server: a sandboxed folder governed by a
/// per-user quota (1 GB by default). Every path goes through <see cref="PathGuard"/>,
/// so a user can never touch another user's files or the host filesystem.
/// </summary>
public sealed class UserWorkspace
{
    private readonly WorkspaceEngine _engine;
    public string AccountId { get; }
    public string Root { get; }

    public UserWorkspace(string accountId, string root, long quotaBytes)
    {
        AccountId = accountId;
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        _engine = new WorkspaceEngine(
            Root,
            Root,
            Path.Combine(Root, "archives"),
            quotaBytes,
            maxFileBytes: Math.Min(64L * 1024 * 1024, quotaBytes),
            commandsEnabled: false,
            allowedCommands: []);
    }

    public long QuotaBytes => _engine.QuotaBytes;
    public void SetQuota(long bytes) => _engine.Configure(bytes, Math.Min(64L * 1024 * 1024, bytes), false, []);

    public WorkspaceUsage Usage() => _engine.GetUsage();

    public IReadOnlyList<WorkspaceEntry> List(string path, int max = 500, bool recursive = false) =>
        _engine.List(path, max, recursive);

    public string Listing(string path, bool recursive = false) =>
        _engine.FormatListing(_engine.List(path, 500, recursive));

    public WorkspaceEntry? Info(string path) => _engine.EntryInfo(path);

    public Task<string> ReadTextAsync(string path, int maxChars = 200_000, CancellationToken ct = default) =>
        _engine.ReadFileAsync(path, maxChars, ct);

    public async Task<byte[]> ReadBytesAsync(string path, CancellationToken ct = default)
    {
        var full = PathGuard.Resolve(Root, path);
        if (!File.Exists(full)) throw new WorkspaceException("Файл не найден.");
        return await File.ReadAllBytesAsync(full, ct);
    }

    public async Task<string> WriteTextAsync(string path, string content, CancellationToken ct = default) =>
        await _engine.WriteFileAsync(path, content, append: false, ct);

    public async Task<string> AppendTextAsync(string path, string content, CancellationToken ct = default) =>
        await _engine.WriteFileAsync(path, content, append: true, ct);

    /// <summary>Stores an uploaded stream, enforcing the quota as it goes.</summary>
    public async Task<string> SaveUploadAsync(string path, Stream source, long declaredLength, CancellationToken ct = default)
    {
        var full = PathGuard.Resolve(Root, path);
        PathGuard.EnsureCreatableFileName(Path.GetFileName(full));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var usage = _engine.GetUsage();
        var existing = File.Exists(full) ? new FileInfo(full).Length : 0;
        var allowance = _engine.QuotaBytes - usage.UsedBytes + existing;
        if (declaredLength > allowance)
            throw new WorkspaceException(
                $"Не хватает места: свободно {TextExtractors.FormatSize(Math.Max(0, allowance))}, " +
                $"а файл {TextExtractors.FormatSize(declaredLength)}.");

        long written = 0;
        await using (var target = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                written += read;
                if (written > allowance)
                {
                    target.Close();
                    File.Delete(full);
                    throw new WorkspaceException("Файл не помещается в квоту — загрузка отменена.");
                }
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        return $"✅ Загружено {Path.GetRelativePath(Root, full).Replace('\\', '/')} — " +
               $"{TextExtractors.FormatSize(written)}";
    }

    public string Delete(string path, bool recursive) => _engine.Delete(path, recursive);

    public string Move(string from, string to) => _engine.Move(from, to);

    public string CreateDirectory(string path) => _engine.CreateDirectory(path);

    public Task<string> ZipAsync(string source, string? destination, CancellationToken ct = default) =>
        _engine.ZipAsync(source, destination, ct);

    public Task<string> UnzipAsync(string archive, string? destination, CancellationToken ct = default) =>
        _engine.UnzipAsync(archive, destination, overwrite: true, ct);

    /// <summary>Zips the whole workspace for the "download everything" button.</summary>
    public async Task<byte[]> ZipAllAsync(CancellationToken ct = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"oli-{AccountId}-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
                    if (relative.StartsWith("archives/", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".zip"))
                        continue;
                    var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                    await using var entryStream = entry.Open();
                    await using var source = File.OpenRead(file);
                    await source.CopyToAsync(entryStream, ct);
                }
            }
            return await File.ReadAllBytesAsync(temp, ct);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>Absolute path of an archive the user asked to download.</summary>
    public string ResolveForDownload(string path)
    {
        var full = PathGuard.Resolve(Root, path);
        if (!File.Exists(full)) throw new WorkspaceException("Файл не найден.");
        return full;
    }

    public string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".md" or ".csv" or ".json" or ".py" or ".cs" or ".js" or ".ts" or ".log" => "text/plain; charset=utf-8",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };

    public static string SanitizeName(string? name, string fallback = "file")
    {
        var cleaned = new string((name ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c is not ('/' or '\\')).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = fallback;
        if (cleaned.Length > 120) cleaned = cleaned[^120..];
        return cleaned;
    }
}

/// <summary>Creates and caches one <see cref="UserWorkspace"/> per account.</summary>
public sealed class UserWorkspaceRegistry
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, UserWorkspace> _workspaces = new();

    public UserWorkspaceRegistry(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public UserWorkspace Get(string accountId, long quotaBytes)
    {
        var workspace = _workspaces.GetOrAdd(accountId, id =>
            new UserWorkspace(id, Path.Combine(_root, id), quotaBytes));
        workspace.SetQuota(quotaBytes);
        return workspace;
    }

    public bool Exists(string accountId) => Directory.Exists(Path.Combine(_root, accountId));

    /// <summary>Deletes a user's files (admin action / account deletion).</summary>
    public void Remove(string accountId)
    {
        _workspaces.TryRemove(accountId, out _);
        var path = Path.Combine(_root, accountId);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public long UsedBytes(string accountId) =>
        _workspaces.TryGetValue(accountId, out var workspace) ? workspace.Usage().UsedBytes : 0;

    public string Root => _root;

    /// <summary>
    /// Reads server settings.json (written by the PC app) so quotas and the model
    /// name stay in sync, without OLI.Server depending on OLI.Core settings types.
    /// </summary>
    public static long DefaultQuotaFromSettings(long fallback = 1024L * 1024 * 1024)
    {
        try
        {
            var file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OLI", "settings.json");
            if (!File.Exists(file)) return fallback;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("ServerUserQuotaBytes", out var quota) && quota.TryGetInt64(out var value))
                return Math.Clamp(value, 64L * 1024 * 1024, 50L * 1024 * 1024 * 1024);
        }
        catch { }
        return fallback;
    }

    public static string DefaultQuotaLabel(long bytes) => TextExtractors.FormatSize(bytes);
}
