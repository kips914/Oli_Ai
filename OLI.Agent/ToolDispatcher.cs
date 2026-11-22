using System.Text;
using OLI.Workspace;

namespace OLI.Agent;

/// <summary>
/// Executes parsed <see cref="ToolCall"/>s against the workspace and returns a
/// plain-text result block that is fed back to the model as the next user turn.
/// Every failure is reported as text (never an exception to the UI), so a
/// confused model gets a chance to correct itself.
/// </summary>
public sealed class ToolDispatcher
{
    private readonly WorkspaceEngine _workspace;

    public ToolDispatcher(WorkspaceEngine workspace) => _workspace = workspace;

    /// <summary>Files produced/requested by send_file, consumed by the Telegram bot.</summary>
    public List<string> FilesToSend { get; } = [];

    public string DescribeCall(ToolCall call) => call.Name switch
    {
        "save_file" => $"📝 Сохраняю {call.Str("path")}",
        "append_file" => $"📝 Дописываю {call.Str("path")}",
        "read_file" => $"📖 Читаю {call.Str("path")}",
        "edit_file" => $"✏️ Правлю {call.Str("path")}",
        "list_files" => "📂 Смотрю файлы",
        "make_dir" => $"📁 Создаю папку {call.Str("path")}",
        "move_path" => $"↔️ Перемещаю {call.Str("from")}",
        "delete_path" => $"🗑 Удаляю {call.Str("path")}",
        "zip_files" => $"🗜 Упаковываю {call.Str("path")} в zip",
        "unzip_files" => $"📦 Распаковываю {call.Str("path")}",
        "list_archives" => "🗄 Смотрю архивы",
        "run_command" => $"⌨️ Запускаю {call.Str("command")}",
        "send_file" => $"📤 Отправляю {call.Str("path")}",
        _ => $"🔧 {call.Name}",
    };

    public async Task<string> ExecuteAsync(IReadOnlyList<ToolCall> calls, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Результаты инструментов (это системное сообщение, не от пользователя). " +
                      "Продолжай задачу: если нужен ещё инструмент — вызови его, иначе дай итоговый ответ.");

        foreach (var call in calls)
        {
            sb.AppendLine();
            sb.AppendLine($"### {call.Name}");
            try
            {
                sb.AppendLine(await ExecuteOneAsync(call, ct));
            }
            catch (WorkspaceException ex)
            {
                sb.AppendLine($"❌ {ex.Message}");
            }
            catch (UnauthorizedAccessException)
            {
                sb.AppendLine("❌ Нет доступа к этому пути.");
            }
            catch (IOException ex)
            {
                sb.AppendLine($"❌ Ошибка ввода-вывода: {ex.Message}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"❌ Ошибка: {ex.Message}");
            }
            Warp(sb);
        }

        return sb.ToString();
    }

    private static void Warp(StringBuilder sb)
    {
        if (sb.Length > 60_000) sb.Length = 60_000;
    }

    private async Task<string> ExecuteOneAsync(ToolCall call, CancellationToken ct)
    {
        if (call.Arguments.ContainsKey("_parse_error"))
            return "❌ Не удалось разобрать JSON аргументов. Вызови инструмент заново с корректным JSON.";

        switch (call.Name)
        {
            case "save_file":
            {
                var path = Require(call, "path");
                var content = call.Str("content") ?? call.Str("text") ?? "";
                return await _workspace.WriteFileAsync(path, content, append: false, ct);
            }
            case "append_file":
            {
                var path = Require(call, "path");
                var content = call.Str("content") ?? call.Str("text") ?? "";
                return await _workspace.WriteFileAsync(path, content, append: true, ct);
            }
            case "read_file":
            {
                var path = Require(call, "path");
                var maxChars = call.Int("max_chars", 60_000);
                return await _workspace.ReadFileAsync(path, Math.Clamp(maxChars, 500, 200_000), ct);
            }
            case "edit_file":
            {
                var path = Require(call, "path");
                var find = call.Str("find") ?? throw new WorkspaceException("Нужен аргумент find.");
                var replace = call.Str("replace") ?? "";
                return await _workspace.EditFileAsync(path, find, replace, call.Bool("all"), ct);
            }
            case "list_files":
            {
                var entries = _workspace.List(call.Str("path") ?? "", 300, call.Bool("recursive"));
                return _workspace.FormatListing(entries);
            }
            case "make_dir":
                return _workspace.CreateDirectory(Require(call, "path"));
            case "move_path":
                return _workspace.Move(Require(call, "from"), Require(call, "to"), call.Bool("overwrite"));
            case "delete_path":
                return _workspace.Delete(Require(call, "path"), call.Bool("recursive"));
            case "zip_files":
            {
                var source = call.Str("path") ?? call.Str("source") ?? throw new WorkspaceException("Нужен аргумент path.");
                return await _workspace.ZipAsync(source, call.Str("destination"), ct);
            }
            case "unzip_files":
            {
                var archive = call.Str("path") ?? call.Str("archive") ?? throw new WorkspaceException("Нужен аргумент path.");
                return await _workspace.UnzipAsync(archive, call.Str("destination"), overwrite: !call.Bool("no_overwrite"), ct);
            }
            case "list_archives":
            {
                var archives = _workspace.ListArchives();
                if (archives.Count == 0) return "Архивов пока нет. Создайте через zip_files.";
                return string.Join("\n", archives.Select(a => $"🗜 archives/{a.Name} — {TextExtractors.FormatSize(a.Size)}, файлов: {a.Entries}"));
            }
            case "run_command":
            {
                var command = Require(call, "command");
                if (command.Contains(' ') || command.Contains('/') || command.Contains('\\'))
                    throw new WorkspaceException("command должен быть просто именем программы, аргументы передаются в args.");
                var args = call.StrList("args");
                var timeout = call.Int("timeout", 60);
                var result = await _workspace.RunCommandAsync(command, args, timeout, ct);
                var sb = new StringBuilder();
                sb.AppendLine($"exit code: {result.ExitCode}, время: {result.Duration.TotalSeconds:F1} с{(result.TimedOut ? " (таймаут)" : "")}");
                if (result.StdOut.Length > 0) sb.AppendLine("stdout:\n" + result.StdOut);
                if (result.StdErr.Length > 0) sb.AppendLine("stderr:\n" + result.StdErr);
                if (result.StdOut.Length == 0 && result.StdErr.Length == 0) sb.AppendLine("(пустой вывод)");
                return sb.ToString().TrimEnd();
            }
            case "send_file":
            {
                var path = Require(call, "path");
                FilesToSend.Add(path);
                return $"Файл {path} будет отправлен пользователю в Telegram.";
            }
            default:
                return $"❌ Неизвестный инструмент «{call.Name}». Доступные: save_file, append_file, read_file, edit_file, " +
                       "list_files, make_dir, move_path, delete_path, zip_files, unzip_files, list_archives, run_command, send_file.";
        }
    }

    private static string Require(ToolCall call, string key) =>
        call.Str(key) is { Length: > 0 } value ? value : throw new WorkspaceException($"Нужен аргумент {key}.");
}
