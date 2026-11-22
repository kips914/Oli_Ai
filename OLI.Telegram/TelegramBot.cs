using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using OLI.Agent;
using OLI.Core;
using OLI.Telegram.Tunnel;
using OLI.Workspace;

namespace OLI.Telegram;

/// <summary>
/// Long-polling Telegram bot that fronts the same OLI agent as the desktop app.
/// Each Telegram chat gets its own conversation history; workspace files can be
/// sent back as documents/photos and received as incoming uploads.
/// </summary>
public sealed class TelegramBot : IAsyncDisposable
{
    private readonly OliSettings _settings;
    private readonly Func<AgentSession> _sessionFactory;
    private readonly WorkspaceEngine? _workspace;
    private readonly ConcurrentDictionary<long, AgentSession> _sessions = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly HashSet<long> _allowedUsers;

    private TelegramApi? _api;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private long _offset;
    private volatile bool _running;
    private string _status = "остановлен";

    public TelegramBot(OliSettings settings, Func<AgentSession> sessionFactory, WorkspaceEngine? workspace)
    {
        _settings = settings;
        _sessionFactory = sessionFactory;
        _workspace = workspace;
        _allowedUsers = (settings.Telegram.AllowedUserIds ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => long.TryParse(x.Trim(), out var v) ? v : (long?)null)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToHashSet();
    }

    public event Action<string>? StatusChanged;
    public bool IsRunning => _running;
    public string Status => _status;
    public string? BotUsername { get; private set; }

    private void SetStatus(string status)
    {
        _status = status;
        OliLog.Info("TELEGRAM", status);
        StatusChanged?.Invoke(status);
    }

    public async Task<string> StartAsync(CancellationToken ct = default)
    {
        if (_running) return "Бот уже запущен.";

        var token = ( _settings.Telegram.BotToken ?? "").Trim();
        if (token.Length == 0) throw new InvalidOperationException("Не указан токен Telegram-бота (Telegram → BotToken).");

        _api = new TelegramApi(token, _settings.Telegram.DohFallback, _settings.Telegram.RelayBaseUrl,
            _settings.Telegram.ProxyUrl, _settings.Telegram.RelaySecret);

        SetStatus("подключаюсь к Telegram...");
        var me = await _api.CallAsync("getMe", null, ct);
        BotUsername = me.TryGetProperty("username", out var u) ? u.GetString() : null;
        SetStatus($"подключён как @{BotUsername} через {_api.ActiveTunnel}");

        try
        {
            await _api.CallAsync("setMyCommands", new Dictionary<string, object?>
            {
                ["commands"] = new object[]
                {
                    new { command = "start", description = "Начать / сбросить диалог" },
                    new { command = "new", description = "Новый диалог (очистить историю)" },
                    new { command = "files", description = "Файлы workspace" },
                    new { command = "zip", description = "Упаковать всё в архив" },
                    new { command = "status", description = "Статус OLI" },
                    new { command = "help", description = "Помощь" },
                },
            }, ct);
        }
        catch (Exception ex) { OliLog.Warning("TELEGRAM", $"setMyCommands: {ex.Message}"); }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = true;
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
        return $"Бот @{BotUsername} запущен через {_api.ActiveTunnel}.";
    }

    public async Task StopAsync()
    {
        if (!_running) return;
        _running = false;
        _cts?.Cancel();
        if (_pollTask is not null)
        {
            try { await _pollTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
        _api?.Dispose();
        _api = null;
        SetStatus("остановлен");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                var updates = await _api!.CallAsync("getUpdates", new Dictionary<string, object?>
                {
                    ["offset"] = _offset,
                    ["timeout"] = Math.Clamp(_settings.Telegram.PollTimeoutSeconds, 5, 50),
                    ["allowed_updates"] = new[] { "message", "edited_message" },
                }, ct);

                backoff = TimeSpan.FromSeconds(2);

                if (updates.ValueKind != JsonValueKind.Array) continue;
                foreach (var update in updates.EnumerateArray())
                {
                    if (update.TryGetProperty("update_id", out var id)) _offset = id.GetInt64() + 1;

                    var message = update.TryGetProperty("message", out var m) ? m
                        : update.TryGetProperty("edited_message", out var em) ? em : default;
                    if (message.ValueKind != JsonValueKind.Object) continue;

                    _ = Task.Run(() => HandleMessageAsync(message, ct), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (TelegramUnavailableException ex)
            {
                SetStatus($"нет связи: {ex.Message}");
                await DelaySafe(TimeSpan.FromSeconds(20), ct);
            }
            catch (TelegramApiException ex) when (ex.Code == 409)
            {
                SetStatus("конфликт getUpdates (бот уже запущен в другом месте)");
                return;
            }
            catch (Exception ex)
            {
                OliLog.Warning("TELEGRAM", $"poll: {ex.Message}");
                await DelaySafe(backoff, ct);
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
        }
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }

    private async Task HandleMessageAsync(JsonElement message, CancellationToken ct)
    {
        try
        {
            if (!message.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var chatIdProp)) return;
            var chatId = chatIdProp.GetInt64();
            var sender = message.TryGetProperty("from", out var fromElement) ? fromElement : default;
            var userId = sender.ValueKind == JsonValueKind.Object && sender.TryGetProperty("id", out var uid) ? uid.GetInt64() : 0;
            var userName = (sender.ValueKind == JsonValueKind.Object && sender.TryGetProperty("first_name", out var fn)
                ? fn.GetString()
                : null) ?? "друг";

            if (_allowedUsers.Count > 0 && !_allowedUsers.Contains(userId))
            {
                await SendTextAsync(chatId, "⛔ Доступ закрыт. Добавьте ваш Telegram ID в настройки OLI → Telegram → AllowedUserIds.", ct);
                return;
            }

            var text = message.TryGetProperty("text", out var t) ? t.GetString() : null;
            var caption = message.TryGetProperty("caption", out var c) ? c.GetString() : null;

            if (message.TryGetProperty("document", out var document))
            {
                await ReceiveFileAsync(chatId, document, caption, ct);
                return;
            }
            if (message.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.Array && photo.GetArrayLength() > 0)
            {
                await ReceiveFileAsync(chatId, photo[photo.GetArrayLength() - 1], caption, ct);
                return;
            }
            if (message.TryGetProperty("voice", out var voice) || message.TryGetProperty("audio", out _))
            {
                await SendTextAsync(chatId, "🎙 Голосовые сообщения пока не поддерживаются — напишите текстом.", ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                await SendTextAsync(chatId, "Напишите вопрос текстом или пришлите файл.", ct);
                return;
            }

            if (text.StartsWith('/'))
            {
                await HandleCommandAsync(chatId, userName, text.Trim(), ct);
                return;
            }

            await RunTurnAsync(chatId, text, ct);
        }
        catch (Exception ex)
        {
            OliLog.Error("TELEGRAM", ex.ToString());
        }
    }

    private async Task HandleCommandAsync(long chatId, string userName, string commandText, CancellationToken ct)
    {
        var command = commandText.Split(' ', 2)[0].ToLowerInvariant();
        switch (command)
        {
            case "/start":
            case "/new":
            {
                await ResetSessionAsync(chatId, ct);
                await SendTextAsync(chatId,
                    $"👋 Привет, {userName}! Я OLI — локальный ИИ-помощник.\n\n" +
                    "Спросите что угодно, пришлите файл на анализ или попросите собрать архив — " +
                    "я создам файлы в workspace и отправлю их сюда.\n\n" +
                    "/new — новый диалог · /files — файлы · /zip — архив · /status — статус",
                    ct);
                break;
            }
            case "/files":
                await SendWorkspaceListAsync(chatId, ct);
                break;
            case "/zip":
                await ZipEverythingAsync(chatId, ct);
                break;
            case "/status":
            {
                var usage = _workspace?.GetUsage();
                var sb = new StringBuilder();
                sb.AppendLine("🟢 OLI работает");
                sb.AppendLine($"Модель: {_settings.ModelDisplayName}");
                sb.AppendLine($"Транспорт Telegram: {_api?.ActiveTunnel}");
                if (usage is not null)
                    sb.AppendLine($"Workspace: {TextExtractors.FormatSize(usage.UsedBytes)} из " +
                                  $"{TextExtractors.FormatSize(usage.QuotaBytes)} ({usage.UsedPercent}%), файлов: {usage.FileCount}");
                await SendTextAsync(chatId, sb.ToString(), ct);
                break;
            }
            case "/help":
                await SendTextAsync(chatId,
                    "Команды:\n/new — новый диалог\n/files — список файлов workspace\n" +
                    "/zip — упаковать workspace в архив и прислать\n/status — статус\n\n" +
                    "Просто напишите задачу, и я при необходимости создам файлы и пришлю их.",
                    ct);
                break;
            default:
                await SendTextAsync(chatId, "Неизвестная команда. /help — список команд.", ct);
                break;
        }
    }

    private async Task<AgentSession> GetSessionAsync(long chatId, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(chatId, out var existing)) return existing;
            var session = _sessionFactory();
            _sessions[chatId] = session;
            return session;
        }
        finally { _sessionGate.Release(); }
    }

    private async Task ResetSessionAsync(long chatId, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(chatId, out var session)) session.Reset();
        }
        finally { _sessionGate.Release(); }
    }

    private async Task RunTurnAsync(long chatId, string text, CancellationToken ct)
    {
        var session = await GetSessionAsync(chatId, ct);
        var working = await SendTextAsync(chatId, "⏳ Думаю...", ct);
        var messageId = working.ValueKind == JsonValueKind.Object && working.TryGetProperty("message_id", out var mid)
            ? mid.GetInt64()
            : 0;
        var lastEdit = DateTimeOffset.MinValue;
        var status = "думаю";

        var progress = new Progress<AgentEvent>(e =>
        {
            switch (e.Kind)
            {
                case AgentEventKind.Search: status = $"🔎 ищу: {e.Text}"; break;
                case AgentEventKind.Page: status = "📄 читаю страницу"; break;
                case AgentEventKind.Tool:
                    status = e.Text.Length > 60 ? e.Text[..60] : e.Text;
                    break;
            }

            if (messageId != 0 && DateTimeOffset.UtcNow - lastEdit > TimeSpan.FromSeconds(2.5))
            {
                lastEdit = DateTimeOffset.UtcNow;
                _ = SafeEditAsync(chatId, messageId, "⏳ " + status);
            }
        });

        try
        {
            var answer = await session.SendAsync(text, progress, ct);

            if (messageId != 0)
                await SafeEditAsync(chatId, messageId, "✅ Готово");
            else
                await DeleteMessageAsync(chatId, messageId, ct);

            foreach (var chunk in SplitForTelegram(answer.Text))
                await SendTextAsync(chatId, chunk, ct);

            foreach (var file in answer.FilesToSend)
                await SendWorkspaceFileAsync(chatId, file, ct);
        }
        catch (TelegramUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OliLog.Error("TELEGRAM", ex.ToString());
            await SendTextAsync(chatId, $"⚠ Ошибка: {ex.Message}", ct);
        }
    }

    private static IEnumerable<string> SplitForTelegram(string text)
    {
        const int max = 3800;
        if (string.IsNullOrEmpty(text)) { yield return "(пустой ответ)"; yield break; }
        var remaining = text;
        while (remaining.Length > max)
        {
            var cut = remaining.LastIndexOf('\n', max);
            if (cut < max / 2) cut = max;
            yield return remaining[..cut];
            remaining = remaining[cut..].TrimStart();
        }
        yield return remaining;
    }

    // ------------------------------------------------------------- files

    private async Task ReceiveFileAsync(long chatId, JsonElement fileJson, string? caption, CancellationToken ct)
    {
        if (_api is null || _workspace is null) return;
        var fileId = fileJson.TryGetProperty("file_id", out var fid) ? fid.GetString() : null;
        var name = fileJson.TryGetProperty("file_name", out var fn) ? fn.GetString() : null;
        if (fileId is null) return;

        var info = await _api.CallAsync("getFile", new Dictionary<string, object?> { ["file_id"] = fileId }, ct);
        var path = info.TryGetProperty("file_path", out var fp) ? fp.GetString() : null;
        if (string.IsNullOrWhiteSpace(path)) { await SendTextAsync(chatId, "Не удалось получить файл.", ct); return; }

        var token = TelegramApi.NormalizeToken(_settings.Telegram.BotToken);
        var downloadUrl = $"https://api.telegram.org/file/bot{token}/{path}";
        var safeName = PathGuard.SafeFileName(name ?? Path.GetFileName(path), "upload.bin");
        var relative = "inbox/" + safeName;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bytes = await http.GetByteArrayAsync(downloadUrl, ct);
            if (bytes.Length > 64 * 1024 * 1024) { await SendTextAsync(chatId, "Файл слишком большой (лимит 64 МБ).", ct); return; }

            var content = LooksBinary(safeName) ? null : Encoding.UTF8.GetString(bytes);
            if (content is not null)
                await _workspace.WriteFileAsync(relative, content, false, ct);
            else
            {
                var full = Path.Combine(_workspace.Root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllBytesAsync(full, bytes, ct);
            }

            var note = string.IsNullOrWhiteSpace(caption) ? "" : $"\nПодпись: {caption}";
            await SendTextAsync(chatId, $"📥 Принял файл «{safeName}» ({TextExtractors.FormatSize(bytes.Length)}) в workspace/{relative}.{note}\n\nЧто с ним сделать?", ct);
        }
        catch (WorkspaceException ex)
        {
            await SendTextAsync(chatId, $"⚠ {ex.Message}", ct);
        }
    }

    private static bool LooksBinary(string filename)
    {
        string[] binary = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".pdf", ".zip", ".7z", ".rar",
            ".mp3", ".mp4", ".wav", ".ogg", ".m4a", ".docx", ".xlsx", ".pptx", ".exe", ".dll", ".bin", ".iso"];
        return binary.Contains(Path.GetExtension(filename).ToLowerInvariant());
    }

    private async Task SendWorkspaceFileAsync(long chatId, string relativePath, CancellationToken ct)
    {
        if (_api is null || _workspace is null) return;
        try
        {
            var resolved = PathGuard.Resolve(_workspace.Root, relativePath);
            if (!File.Exists(resolved)) { await SendTextAsync(chatId, $"Файл {relativePath} не найден.", ct); return; }

            var info = new FileInfo(resolved);
            if (info.Length > _settings.Telegram.MaxDocumentBytes)
            {
                await SendTextAsync(chatId, $"Файл {relativePath} больше {_settings.Telegram.MaxDocumentBytes / 1024 / 1024} МБ — слишком большой для Telegram.", ct);
                return;
            }

            var ext = info.Extension.ToLowerInvariant();
            var isPhoto = ext is ".png" or ".jpg" or ".jpeg" or ".webp" && info.Length < 10 * 1024 * 1024;
            var contentType = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                ".txt" or ".md" or ".csv" or ".json" or ".py" or ".cs" => "text/plain",
                _ => "application/octet-stream",
            };

            var file = new TelegramFile(resolved, info.Name, contentType);
            await _api.CallAsync(isPhoto ? "sendPhoto" : "sendDocument", new Dictionary<string, object?>
            {
                ["chat_id"] = chatId,
                [isPhoto ? "photo" : "document"] = file,
            }, ct);
        }
        catch (TelegramApiException ex)
        {
            await SendTextAsync(chatId, $"⚠ Не удалось отправить {relativePath}: {ex.Message}", ct);
        }
        catch (Exception ex)
        {
            OliLog.Warning("TELEGRAM", $"send file: {ex.Message}");
        }
    }

    private async Task SendWorkspaceListAsync(long chatId, CancellationToken ct)
    {
        if (_workspace is null) { await SendTextAsync(chatId, "Workspace отключён.", ct); return; }
        try
        {
            var entries = _workspace.List("", 60, false);
            var usage = _workspace.GetUsage();
            var sb = new StringBuilder();
            sb.AppendLine($"📂 workspace — {TextExtractors.FormatSize(usage.UsedBytes)} / {TextExtractors.FormatSize(usage.QuotaBytes)}");
            sb.AppendLine(_workspace.FormatListing(entries));
            await SendTextAsync(chatId, sb.ToString(), ct);
        }
        catch (WorkspaceException ex)
        {
            await SendTextAsync(chatId, $"⚠ {ex.Message}", ct);
        }
    }

    private async Task ZipEverythingAsync(long chatId, CancellationToken ct)
    {
        if (_workspace is null) { await SendTextAsync(chatId, "Workspace отключён.", ct); return; }
        if (_workspace.GetUsage().FileCount == 0) { await SendTextAsync(chatId, "В workspace пока нет файлов.", ct); return; }
        try
        {
            var name = $"workspace-{DateTime.Now:yyyyMMdd-HHmm}.zip";
            var result = await _workspace.ZipAsync("", "archives/" + name, ct);
            await SendTextAsync(chatId, result, ct);
            await SendWorkspaceFileAsync(chatId, "archives/" + name, ct);
        }
        catch (Exception ex)
        {
            await SendTextAsync(chatId, $"⚠ {ex.Message}", ct);
        }
    }

    // ------------------------------------------------------------- api helpers

    private async Task<JsonElement> SendTextAsync(long chatId, string text, CancellationToken ct)
    {
        if (_api is null) return default;
        try
        {
            return await _api.CallAsync("sendMessage", new Dictionary<string, object?>
            {
                ["chat_id"] = chatId,
                ["text"] = text,
                ["disable_web_page_preview"] = true,
            }, ct);
        }
        catch (TelegramApiException ex)
        {
            OliLog.Warning("TELEGRAM", $"sendMessage: {ex.Message}");
            return default;
        }
    }

    private async Task SafeEditAsync(long chatId, long messageId, string text)
    {
        if (_api is null || messageId == 0) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _api.CallAsync("editMessageText", new Dictionary<string, object?>
            {
                ["chat_id"] = chatId,
                ["message_id"] = messageId,
                ["text"] = text,
                ["disable_web_page_preview"] = true,
            }, cts.Token);
        }
        catch { /* editing is best-effort */ }
    }

    private async Task DeleteMessageAsync(long chatId, long messageId, CancellationToken ct)
    {
        if (_api is null || messageId == 0) return;
        try
        {
            await _api.CallAsync("deleteMessage", new Dictionary<string, object?>
            {
                ["chat_id"] = chatId,
                ["message_id"] = messageId,
            }, ct);
        }
        catch { }
    }

    /// <summary>Verifies a token + finds a working transport without starting polling.</summary>
    public static async Task<string> ProbeAsync(TelegramSettings settings, CancellationToken ct)
    {
        using var api = new TelegramApi(settings.BotToken, settings.DohFallback, settings.RelayBaseUrl,
            settings.ProxyUrl, settings.RelaySecret);
        var me = await api.CallAsync("getMe", null, ct);
        var username = me.TryGetProperty("username", out var u) ? u.GetString() : "?";
        return $"OK @{username} через {api.ActiveTunnel}";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _sessionGate.Dispose();
    }
}
