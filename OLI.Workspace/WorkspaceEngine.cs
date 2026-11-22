using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace OLI.Workspace;

/// <summary>
/// A hard-quota sandbox that the model can work in: create/read/edit/move/delete
/// files, zip and unzip archives, and run a small allow-list of command-line tools.
/// Every path is resolved through <see cref="PathGuard"/> so nothing can escape
/// <see cref="Root"/>; every write is charged against the quota (1 GB by default).
/// </summary>
public sealed class WorkspaceEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Root { get; }
    public string FilesRoot { get; }
    public string ArchiveRoot { get; }

    public long QuotaBytes { get; private set; }
    public long MaxFileBytes { get; private set; }
    public bool CommandsEnabled { get; private set; }
    public IReadOnlyList<string> AllowedCommands { get; private set; }

    public WorkspaceEngine(
        string root,
        string filesRoot,
        string archiveRoot,
        long quotaBytes,
        long maxFileBytes,
        bool commandsEnabled,
        IEnumerable<string> allowedCommands)
    {
        Root = Path.GetFullPath(root);
        FilesRoot = Path.GetFullPath(filesRoot);
        ArchiveRoot = Path.GetFullPath(archiveRoot);
        QuotaBytes = Math.Clamp(quotaBytes, 16L * 1024 * 1024, 20L * 1024 * 1024 * 1024);
        MaxFileBytes = Math.Clamp(maxFileBytes, 1024, QuotaBytes);
        CommandsEnabled = commandsEnabled;
        AllowedCommands = allowedCommands.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        EnsureRoots();
    }

    private void EnsureRoots()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(FilesRoot);
        Directory.CreateDirectory(ArchiveRoot);
    }

    public void Configure(long quotaBytes, long maxFileBytes, bool commandsEnabled, IEnumerable<string> allowedCommands)
    {
        QuotaBytes = Math.Clamp(quotaBytes, 16L * 1024 * 1024, 20L * 1024 * 1024 * 1024);
        MaxFileBytes = Math.Clamp(maxFileBytes, 1024, QuotaBytes);
        CommandsEnabled = commandsEnabled;
        AllowedCommands = allowedCommands.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
    }

    // ---------------------------------------------------------------- usage

    public WorkspaceUsage GetUsage()
    {
        var total = EnumerateTree(Root).Sum(e => e.Size);
        var files = 0;
        var archives = 0;
        foreach (var e in EnumerateTree(FilesRoot)) if (!e.IsDirectory) files++;
        foreach (var e in EnumerateTree(ArchiveRoot)) if (!e.IsDirectory) archives++;
        return new WorkspaceUsage(total, QuotaBytes, files, archives);
    }

    private static IEnumerable<WorkspaceEntry> EnumerateTree(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            yield return new WorkspaceEntry(
                Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'),
                entry is DirectoryInfo,
                entry is FileInfo fi ? fi.Length : 0,
                entry.LastWriteTimeUtc);
        }
    }

    private long UsedBytesSafe() => EnumerateTree(Root).Sum(e => e.Size);

    private void EnsureHeadroom(long additionalBytes)
    {
        var used = UsedBytesSafe();
        if (used + additionalBytes > QuotaBytes)
            throw new WorkspaceException(
                $"Не хватает места в workspace: занято {TextExtractors.FormatSize(used)} из " +
                $"{TextExtractors.FormatSize(QuotaBytes)}, запрошено ещё {TextExtractors.FormatSize(additionalBytes)}. " +
                "Удалите ненужные файлы (ws_delete) или очистите архивы.");
    }

    // ---------------------------------------------------------------- listing

    public IReadOnlyList<WorkspaceEntry> List(string? relativePath = "", int maxEntries = 200, bool recursive = false)
    {
        var target = PathGuard.Resolve(Root, relativePath, allowRoot: true);
        if (!Directory.Exists(target)) throw new WorkspaceException($"Папка не найдена: {Display(target)}");

        var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var dir = new DirectoryInfo(target);
        var list = dir.EnumerateFileSystemInfos("*", search)
            .Where(e => (e.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(e => e is DirectoryInfo)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxEntries)
            .Select(e => new WorkspaceEntry(
                Path.GetRelativePath(Root, e.FullName).Replace('\\', '/'),
                e is DirectoryInfo,
                e is FileInfo fi ? fi.Length : 0,
                e.LastWriteTimeUtc))
            .ToList();
        return list;
    }

    public string FormatListing(IReadOnlyList<WorkspaceEntry> entries)
    {
        if (entries.Count == 0) return "Папка пуста.";
        var sb = new StringBuilder();
        foreach (var e in entries)
            sb.AppendLine(e.IsDirectory
                ? $"📁 {e.RelativePath}/"
                : $"📄 {e.RelativePath}  ({TextExtractors.FormatSize(e.Size)}, {e.ModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm})");
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- files

    public async Task<string> WriteFileAsync(string relativePath, string content, bool append = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var full = PathGuard.Resolve(Root, relativePath);
            PathGuard.EnsureCreatableFileName(Path.GetFileName(full));

            var bytes = Encoding.UTF8.GetByteCount(content ?? "");
            if (bytes > MaxFileBytes)
                throw new WorkspaceException($"Файл слишком большой: {TextExtractors.FormatSize(bytes)} (лимит {TextExtractors.FormatSize(MaxFileBytes)}).");

            var existing = File.Exists(full) ? new FileInfo(full).Length : 0;
            var delta = append ? bytes : Math.Max(0, bytes - existing);
            EnsureHeadroom(delta);

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await (append
                ? File.AppendAllTextAsync(full, content ?? "", new UTF8Encoding(false), ct)
                : File.WriteAllTextAsync(full, content ?? "", new UTF8Encoding(false), ct));

            return $"✅ {(append ? "Дописано в" : "Создан")} {Display(full)} — {TextExtractors.FormatSize(new FileInfo(full).Length)}";
        }
        finally { _gate.Release(); }
    }

    public async Task<string> ReadFileAsync(string relativePath, int maxChars = 200_000, CancellationToken ct = default)
    {
        var full = PathGuard.Resolve(Root, relativePath);
        if (Directory.Exists(full)) throw new WorkspaceException("Это папка — используйте ws_list.");
        if (!File.Exists(full)) throw new WorkspaceException($"Файл не найден: {Display(full)}");
        return await TextExtractors.ExtractAsync(full, maxChars, ct);
    }

    public async Task<string> EditFileAsync(string relativePath, string find, string replace, bool all = false, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(find)) throw new WorkspaceException("Строка для замены пуста.");
        await _gate.WaitAsync(ct);
        try
        {
            var full = PathGuard.Resolve(Root, relativePath);
            if (!File.Exists(full)) throw new WorkspaceException($"Файл не найден: {Display(full)}");
            if (!TextExtractors.IsTextLike(Path.GetExtension(full)))
                throw new WorkspaceException("Редактировать можно только текстовые файлы.");

            var text = await File.ReadAllTextAsync(full, ct);
            var count = CountOccurrences(text, find);
            if (count == 0) throw new WorkspaceException("Фрагмент не найден в файле — ничего не изменено.");

            var updated = all ? text.Replace(find, replace) : ReplaceFirst(text, find, replace);
            if (Encoding.UTF8.GetByteCount(updated) > MaxFileBytes)
                throw new WorkspaceException("Результат превышает лимит размера файла.");

            await File.WriteAllTextAsync(full, updated, new UTF8Encoding(false), ct);
            return $"✅ {Display(full)}: заменено вхождений — {(all ? count : 1)}.";
        }
        finally { _gate.Release(); }
    }

    public string CreateDirectory(string relativePath)
    {
        var full = PathGuard.Resolve(Root, relativePath);
        Directory.CreateDirectory(full);
        return $"✅ Папка создана: {Display(full)}";
    }

    public string Delete(string relativePath, bool recursive = false)
    {
        var full = PathGuard.Resolve(Root, relativePath);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new WorkspaceException($"Не найдено: {Display(full)}");

        if (Directory.Exists(full))
        {
            if (!recursive && Directory.EnumerateFileSystemEntries(full).Any())
                throw new WorkspaceException("Папка не пуста. Передайте recursive=true, чтобы удалить её целиком.");
            Directory.Delete(full, recursive);
        }
        else File.Delete(full);

        return $"🗑 Удалено: {Display(full)}";
    }

    public string Move(string from, string to, bool overwrite = false)
    {
        var src = PathGuard.Resolve(Root, from);
        var dst = PathGuard.Resolve(Root, to);
        if (!File.Exists(src) && !Directory.Exists(src)) throw new WorkspaceException($"Не найдено: {Display(src)}");
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

        if (File.Exists(dst))
        {
            if (!overwrite) throw new WorkspaceException("Целевой файл уже существует (overwrite=false).");
            File.Delete(dst);
        }
        if (File.Exists(src)) File.Move(src, dst);
        else Directory.Move(src, dst);
        return $"✅ Перемещено: {Display(src)} → {Display(dst)}";
    }

    // ---------------------------------------------------------------- archives

    /// <summary>
    /// Creates a .zip from a workspace path. <paramref name="destination"/> is
    /// relative to the workspace (".zip" appended automatically when missing).
    /// </summary>
    public async Task<string> ZipAsync(string sourcePath, string? destination, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var src = PathGuard.Resolve(Root, sourcePath);
            if (!File.Exists(src) && !Directory.Exists(src)) throw new WorkspaceException($"Не найдено: {Display(src)}");

            var srcName = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar));
            var destRel = string.IsNullOrWhiteSpace(destination)
                ? Path.Combine(ArchiveRoot, $"{srcName}.zip")
                : PathGuard.Resolve(Root, destination);
            if (!destRel.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                destRel += ".zip";
            if (!destRel.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !PathGuard.IsInside(Root, destRel))
                throw new WorkspaceException("Не удалось определить путь архива.");
            var isDirectory = Directory.Exists(src);
            if (isDirectory && PathGuard.IsInside(src + Path.DirectorySeparatorChar, destRel))
                throw new WorkspaceException("Нельзя создать архив внутри папки, которую архивируете.");

            Directory.CreateDirectory(Path.GetDirectoryName(destRel)!);
            if (File.Exists(destRel)) File.Delete(destRel);

            var toAdd = isDirectory ? BuiltFiles(src).ToList() : [];
            long sourceBytes = isDirectory ? toAdd.Sum(f => new FileInfo(f).Length) : new FileInfo(src).Length;

            // Worst case: zip is stored (no compression) — charge the raw size.
            EnsureHeadroom(sourceBytes + 1024);

            var entries = 0;
            using (var zip = ZipFile.Open(destRel, ZipArchiveMode.Create, Encoding.UTF8))
            {
                if (File.Exists(src))
                {
                    zip.CreateEntryFromFile(src, srcName, CompressionLevel.Optimal);
                    entries = 1;
                }
                else
                {
                    var baseDir = new DirectoryInfo(src);
                    foreach (var file in toAdd)
                    {
                        ct.ThrowIfCancellationRequested();
                        var rel = Path.GetRelativePath(baseDir.FullName, file).Replace('\\', '/');
                        var entryRel = $"{srcName}/{rel}";
                        zip.CreateEntryFromFile(file, entryRel, CompressionLevel.Optimal);
                        entries++;
                    }
                }
            }

            var size = new FileInfo(destRel).Length;
            return $"🗜 Архив создан: {Display(destRel)} ({TextExtractors.FormatSize(size)}), файлов: {entries}.";
        }
        finally { _gate.Release(); }
    }

    /// <summary>Extracts a .zip from the workspace into an optional folder.</summary>
    public async Task<string> UnzipAsync(string archivePath, string? destination, bool overwrite = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var src = PathGuard.Resolve(Root, archivePath);
            if (!File.Exists(src)) throw new WorkspaceException($"Архив не найден: {Display(src)}");
            if (!src.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceException("Поддерживаются только .zip архивы.");

            var dest = string.IsNullOrWhiteSpace(destination)
                ? Path.Combine(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src))
                : PathGuard.Resolve(Root, destination);
            Directory.CreateDirectory(dest);

            using var zip = ZipFile.OpenRead(src);
            long total = 0;
            var extracted = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(Path.Combine(dest, entry.FullName)); continue; }

                var target = Path.GetFullPath(Path.Combine(dest, entry.FullName));
                if (!PathGuard.IsInside(dest, target))
                {
                    OliLog.Warning("WORKSPACE", $"zip-slip предотвращён: {entry.FullName}");
                    continue;
                }
                if (entry.Length > MaxFileBytes) throw new WorkspaceException($"Файл в архиве слишком большой: {entry.FullName}");

                total += entry.Length;
                if (UsedBytesSafe() + total > QuotaBytes) throw new WorkspaceException("Распаковка превысит квоту workspace.");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target) && !overwrite) continue;
                entry.ExtractToFile(target, overwrite);
                extracted++;
            }

            return $"📦 Распаковано {extracted} файлов из {Display(src)} в {Display(dest)}.";
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<(string Name, long Size, int Entries)> ListArchives()
    {
        var result = new List<(string, long, int)>();
        foreach (var fi in new DirectoryInfo(ArchiveRoot).EnumerateFiles("*.zip"))
        {
            try
            {
                using var zip = ZipFile.OpenRead(fi.FullName);
                result.Add((fi.Name, fi.Length, zip.Entries.Count));
            }
            catch { result.Add((fi.Name, fi.Length, 0)); }
        }
        return result;
    }

    // ---------------------------------------------------------------- commands

    public async Task<CommandResult> RunCommandAsync(
        string command,
        IReadOnlyList<string> args,
        int timeoutSeconds = 60,
        CancellationToken ct = default)
    {
        if (!CommandsEnabled) throw new WorkspaceException("Выполнение команд отключено в настройках workspace.");

        var exe = Path.GetFileName(command.Trim()).ToLowerInvariant();
        if (exe.EndsWith(".exe")) exe = exe[..^4];
        if (string.IsNullOrWhiteSpace(exe) || !AllowedCommands.Contains(exe))
            throw new WorkspaceException(
                $"Команда «{exe}» не разрешена. Разрешённые: {string.Join(", ", AllowedCommands)}.");

        foreach (var a in args)
        {
            if (a.Any(char.IsControl) || a.Contains('\n') || a.Contains('\r'))
                throw new WorkspaceException("Аргумент содержит недопустимые символы.");
            if (a.Length > 4096) throw new WorkspaceException("Аргумент слишком длинный.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = FilesRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["OLI_WORKSPACE"] = FilesRoot;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) throw new WorkspaceException("Не удалось запустить процесс.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new WorkspaceException($"Программа «{exe}» не установлена на этом компьютере.");
        }

        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300));
        var timedOut = false;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { timedOut = true; }
        }

        if (timedOut)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        var stdout = Truncate(await SafeAwait(stdoutTask), 20_000);
        var stderr = Truncate(await SafeAwait(stderrTask), 8_000);
        sw.Stop();

        var result = new CommandResult(timedOut ? -1 : process.HasExited ? process.ExitCode : -1, stdout, stderr, sw.Elapsed, timedOut);
        OliLog.Info("EXEC", $"{exe} {string.Join(' ', args)} → exit={result.ExitCode} in {sw.Elapsed.TotalSeconds:F1}s{(timedOut ? " (timeout)" : "")}");
        return result;
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { return ""; }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n…[вывод обрезан]";

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<string> BuiltFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0);

    private string Display(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    /// <summary>Public helper: human-readable path for UI.</summary>
    public string Describe(string relativePath) => Display(PathGuard.Resolve(Root, relativePath, allowRoot: true));

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string find, string replace)
    {
        var index = text.IndexOf(find, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + replace + text[(index + find.Length)..];
    }
}
