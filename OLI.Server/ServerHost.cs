using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.HttpOverrides;
using OLI.Agent;
using OLI.Core;
using OLI.Engine;
using OLI.Memory;
using OLI.Server.Accounts;
using OLI.Server.Chats;
using OLI.Server.Storage;
using OLI.Server.Usage;
using OLI.Web;
using OLI.Workspace;

namespace OLI.Server;

/// <summary>
/// The always-on gateway that turns this PC into the OLI backend: user accounts,
/// per-user chats and per-user 1 GB workspaces, served over HTTP(S) to the
/// Android app. Runs a real Kestrel server (started by <c>OLI.Server.exe</c> and
/// controllable from the desktop admin remote).
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private WebApplication? _app;
    private CancellationTokenSource? _cts;

    private readonly AccountStore _accounts;
    private readonly UserWorkspaceRegistry _workspaces;
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Until)> _throttle = new();
    private readonly LlamaClient _llama;
    private readonly WebSearch _web;
    private readonly MemoryStore _memory;
    private readonly UserProfileStore _profile;
    private readonly UsageStore _usage;
    private readonly ChatStore _chats;
    private readonly LimitPolicy _limits;
    private LanBeacon? _beacon;

    public ServerHost(ServerOptions options)
    {
        _options = options;
        Directory.CreateDirectory(_options.DataRoot);
        _accounts = new AccountStore(Path.Combine(_options.DataRoot, "accounts.json"));
        _workspaces = new UserWorkspaceRegistry(Path.Combine(_options.DataRoot, "workspaces"));
        _llama = new LlamaClient(_options.LlamaBaseUrl);
        _web = new WebSearch(20, OliSearchProvider.Auto)
        {
            OpenWeb = _options.OpenWeb,
        };
        _memory = new MemoryStore(Path.Combine(_options.DataRoot, "memory", "server.json"));
        _profile = new UserProfileStore(Path.Combine(_options.DataRoot, "memory", "profile.json"));
        _usage = new UsageStore(Path.Combine(_options.DataRoot, "usage", "tokens.json"));
        _chats = new ChatStore(Path.Combine(_options.DataRoot, "chats"));
        _limits = new LimitPolicy
        {
            SoloHourly = _options.SoloHourlyTokens,
            SoloDaily = _options.SoloDailyTokens,
            MinHourly = _options.MinHourlyTokens,
            MinDaily = _options.MinDailyTokens,
            BusyUsers = _options.BusyUsers,
        };
    }

    public bool IsRunning => _app is not null;
    public string BaseUrl => _options.BaseUrl;
    public ServerOptions Options => _options;
    public int RegisteredUsers => _accounts.List().Count;

    public event Action<string>? StatusChanged;

    private void Report(string status)
    {
        OliLog.Info("SERVER", status);
        StatusChanged?.Invoke(status);
    }

    // ================================================================ lifecycle

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseKestrel(options =>
        {
            void Configure(ListenOptions listen)
            {
                if (_options.HttpsEnabled)
                {
                    listen.UseHttps(_options.CertificatePath, _options.CertificatePassword);
                }
            }

            if (IPAddress.TryParse(_options.Host, out var address))
                options.Listen(address, _options.Port, Configure);
            else
                options.ListenAnyIP(_options.Port, Configure);
        });

        // Behind a Cloudflare Tunnel / nginx the client IP arrives in headers.
        var app = builder.Build();
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            KnownNetworks = { },
            KnownProxies = { },
        });
        MapEndpoints(app);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await app.StartAsync(_cts.Token);
        _app = app;

        // Presence packet for the phone: it carries the real port, so discovery works even
        // when the PC uses 6767 instead of the default.
        _beacon?.Dispose();
        _beacon = new LanBeacon(_options.Port, AppVersion.Current);
        _beacon.Start();

        Report($"сервер запущен на {_options.BaseUrl} (пользователей: {RegisteredUsers})");
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        try
        {
            _cts?.Cancel();
            await _app.StopAsync(TimeSpan.FromSeconds(5));
            await _app.DisposeAsync();
        }
        catch (Exception ex)
        {
            OliLog.Warning("SERVER", $"Остановка сервера: {ex.Message}");
        }
        finally
        {
            _beacon?.Dispose();
            _beacon = null;
            _app = null;
            _cts?.Dispose();
            _cts = null;
            Report("сервер остановлен");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        foreach (var session in _sessions.Values) session.Dispose();
        _llama.Dispose();
        _web.Dispose();
    }

    // ================================================================ endpoints

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", () => Results.Json(new
        {
            service = "OLI Server",
            version = AppVersion.Current,
            api = 3,
            models = new[] { ModelInfo(_options.Model, "oli-pass", "Olim Pass"), ModelInfo(_options.LiteModel, "oli-son", "Olim Son") },
            registration = _options.AllowRegistration,
            endpoints = new[] { "/api/health", "/api/register", "/api/login", "/api/me", "/api/chat", "/api/files" },
        }));

        app.MapGet("/api/health", async (CancellationToken ct) => Results.Json(new
        {
            status = "ok",
            service = "oli-server",
            version = AppVersion.Current,
            time = DateTimeOffset.UtcNow,
            registration = _options.AllowRegistration,
            llama = await _llama.IsAliveAsync(ct),
            // Lets the phone tell "my PC is reachable but the model is off" from "wrong address",
            // and name the engine the user actually runs (llama.cpp or Ollama).
            model = _options.Model,
            engine = _llama.IsOllama == true ? "ollama" : "llama.cpp",
            server = "oli-server",
            // Discovery data for a phone that can already reach this address but wants to
            // remember the port / name; harmless for older clients.
            port = _options.Port,
            name = Environment.MachineName,
        }));

        // ------------------------------------------------------------ auth

        app.MapPost("/api/register", async (RegisterRequest request, HttpContext http, CancellationToken ct) =>
        {
            if (await IsThrottledAsync(http)) return TooManyAttempts();
            if (!_options.AllowRegistration)
                return Results.Json(new { error = "Регистрация закрыта администратором." }, statusCode: 403);

            if (!string.IsNullOrWhiteSpace(_options.InviteCode) &&
                !string.Equals(request.InviteCode?.Trim(), _options.InviteCode, StringComparison.Ordinal))
                return Results.Json(new { error = "Неверный код приглашения." }, statusCode: 403);

            var (account, error) = await _accounts.RegisterAsync(
                request.UserName ?? "", request.Password ?? "", request.DisplayName, DeviceOf(http));
            if (account is null) return Results.Json(new { error }, statusCode: 400);

            await _accounts.SetQuotaAsync(account.Id, _options.UserQuotaBytes);
            account.QuotaBytes = _options.UserQuotaBytes;
            _workspaces.Get(account.Id, account.QuotaBytes); // create the 1 GB sandbox eagerly

            var (token, expiresAt) = await _accounts.IssueTokenAsync(account.Id, DeviceOf(http));
            return Results.Json(ToAuthResponse(account, token, expiresAt));
        });

        app.MapPost("/api/login", async (LoginRequest request, HttpContext http) =>
        {
            if (await IsThrottledAsync(http)) return TooManyAttempts();

            var account = await _accounts.AuthenticateAsync(request.UserName ?? "", request.Password ?? "");
            if (account is null)
            {
                RegisterFailure(http);
                return Results.Json(new { error = "Неверное имя пользователя или пароль." }, statusCode: 401);
            }
            ClearFailures(http);

            var (token, expiresAt) = await _accounts.IssueTokenAsync(account.Id, DeviceOf(http));
            return Results.Json(ToAuthResponse(account, token, expiresAt));
        });

        app.MapGet("/api/me", (HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            return Results.Json(ToProfile(account));
        });

        app.MapPost("/api/logout", async (HttpContext http) =>
        {
            var token = BearerToken(http);
            if (token is not null) await _accounts.RevokeTokenAsync(token);
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/profile", async (UpdateProfileRequest request, HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();

            var (ok, error) = await _accounts.UpdateDisplayNameAsync(account.Id, request.DisplayName ?? "");
            if (!ok) return Results.Json(new { error }, statusCode: 400);

            var updated = _accounts.FindById(account.Id)!;
            return Results.Json(new { ok = true, user = ToProfile(updated) });
        });

        app.MapPost("/api/password", async (ChangePasswordRequest request, HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            var (ok, error) = await _accounts.ChangePasswordAsync(account.Id, request.OldPassword ?? "", request.NewPassword ?? "");
            return ok
                ? Results.Json(new { ok = true, relogin = true })
                : Results.Json(new { error }, statusCode: 400);
        });

        // ------------------------------------------------------------ chat

        app.MapPost("/api/chat", async (ChatRequestDto request, HttpContext http, CancellationToken ct) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();

            var text = (request.Message ?? "").Trim();
            if (text.Length == 0) return Results.Json(new { error = "Пустое сообщение." }, statusCode: 400);
            if (text.Length > 8000) return Results.Json(new { error = "Сообщение слишком длинное." }, statusCode: 400);

            // --- dynamic token allowance
            var active = Math.Max(1, _usage.ActiveUsers() + 1);
            var (hourlyLimit, dailyLimit) = _limits.Allowance(active);
            var (hourTokens, dayTokens) = _usage.For(account.Id);
            var promptTokens = TokenEstimator.Estimate(text) + 700; // prompt + system overhead

            var hourLeft = Math.Max(0, hourlyLimit - hourTokens);
            var dayLeft = Math.Max(0, dailyLimit - dayTokens);
            var limitLeft = Math.Min(hourLeft, dayLeft);
            var exhausted = limitLeft <= promptTokens + 32;

            // After the limit: only the light model, and a short answer budget.
            var modelId = exhausted ? _options.LiteModel : (string.IsNullOrWhiteSpace(account.ModelId) ? _options.Model : account.ModelId);
            if (modelId is not ("oli-pass" or "oli-son")) modelId = _options.Model;

            var gate = _sessionLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(TimeSpan.FromSeconds(2), ct))
                return Results.Json(new { error = "Предыдущий ответ ещё готовится." }, statusCode: 429);

            try
            {
                // --- chat thread: found, or created from this first message
                var thread = _chats.Resolve(account.Id, request.ThreadId, text, modelId);
                if (thread is null)
                    return Results.Json(new { error = "Чат был удалён на другом устройстве.", deleted = true }, statusCode: 409);

                var threadId = thread.Id;

                var session = GetSession(account);
                // The model must only remember THIS thread: switching chats used to leave
                // the previous conversation in the context, so answers mixed two chats.
                session.UseThread(threadId, thread.Messages);

                if (_chats.Append(account.Id, threadId, ChatEntry.User(text)).ThreadMissing)
                    return Results.Json(new { error = "Чат был удалён, сообщение не сохранено.", deleted = true }, statusCode: 409);

                session.SelectModel(modelId);
                session.SetAnswerBudget(exhausted ? 220 : 0);

                AgentAnswer answer;
                try
                {
                    answer = await session.SendAsync(text, progress: null, ct);
                }
                catch (HttpRequestException ex)
                {
                    // The PC is fine; the model engine is not. Say exactly that, and say
                    // what to do: an empty bubble is the worst possible answer here.
                    var reason = string.IsNullOrWhiteSpace(ex.Message)
                        ? "Движок модели не ответил."
                        : ex.Message;
                    OliLog.Warning("CHAT", "Движок модели: " + reason);
                    return Results.Json(new
                    {
                        error = reason + " Откройте OLI на ПК: «Пульт админа» → «Проверить всё».",
                        hint = "engine",
                        threadId,
                        engine = "down",
                        httpStatus = ex.StatusCode is null ? 0 : (int)ex.StatusCode,
                    }, statusCode: 503);
                }

                // A deleted thread must not be resurrected by an answer that was in flight.
                var append = _chats.Append(account.Id, threadId, ChatEntry.Assistant(answer.Text, answer.FilesToSend));
                if (append.ThreadMissing)
                    return Results.Json(new { error = "Чат был удалён, ответ не сохранён.", deleted = true }, statusCode: 409);
                // An empty answer is not an error the user caused: say it plainly instead of
                // returning text = "" (the phone then showed a "⚠ Пустой ответ" bubble).
                if (append.Entry is null && string.IsNullOrWhiteSpace(answer.Text))
                    return Results.Json(new
                    {
                        error = "Модель не вернула текст. Попробуйте ещё раз или выберите другую модель.",
                        hint = "empty",
                        threadId,
                    }, statusCode: 502);

                var completionTokens = TokenEstimator.Estimate(answer.Text);
                _usage.Add(new UsageEntry
                {
                    AccountId = account.Id,
                    ModelId = modelId,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                });

                var (hourNow, dayNow) = _usage.For(account.Id);
                return Results.Json(new
                {
                    text = answer.Text,
                    files = answer.FilesToSend.Select(SafeRelative).ToList(),
                    model = modelId == _options.LiteModel ? "Olim Son" : "Olim Pass",
                    modelId,
                    limited = exhausted,
                    threadId,
                    usage = new
                    {
                        prompt = promptTokens,
                        completion = completionTokens,
                        hourUsed = hourNow,
                        hourLimit = hourlyLimit,
                        dayUsed = dayNow,
                        dayLimit = dailyLimit,
                        hourLeft = Math.Max(0, hourlyLimit - hourNow),
                        dayLeft = Math.Max(0, dailyLimit - dayNow),
                        activeUsers = active,
                    },
                });
            }
            finally { gate.Release(); }
        });

        app.MapGet("/api/models", async (HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();

            var (hourlyLimit, dailyLimit) = _limits.Allowance(Math.Max(1, _usage.ActiveUsers() + 1));
            var (hourUsed, dayUsed) = _usage.For(account.Id);
            var selected = string.IsNullOrWhiteSpace(account.ModelId) ? _options.Model : account.ModelId;

            // The real backend may be Ollama, whose model names differ from OLI's own ids.
            var backendModels = await _llama.ListModelsAsync();
            var served = backendModels.Count > 0 ? backendModels[0] : "";

            return Results.Json(new
            {
                selected,
                backend = new
                {
                    url = _options.LlamaBaseUrl,
                    // "ollama" or "llama.cpp" — lets the phone say which engine answers.
                    kind = _llama.IsOllama == true ? "ollama" : "llama.cpp",
                    models = backendModels,
                    served,
                },
                models = new object[]
                {
                    ModelInfo(_options.Model, "oli-pass", "Olim Pass"),
                    ModelInfo(_options.LiteModel, "oli-son", "Olim Son"),
                },
                limit = new
                {
                    hourUsed,
                    hourLimit = hourlyLimit,
                    dayUsed,
                    dayLimit = dailyLimit,
                    activeUsers = Math.Max(1, _usage.ActiveUsers() + 1),
                    exhausted = hourUsed >= hourlyLimit || dayUsed >= dailyLimit,
                },
            });
        });

        app.MapPost("/api/models", async (ModelChoiceRequest request, HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();

            var id = (request.ModelId ?? "").Trim();
            if (id is not ("oli-pass" or "oli-son"))
                return Results.Json(new { error = "Неизвестная модель." }, statusCode: 400);

            await _accounts.SetModelAsync(account.Id, id);
            if (_sessions.TryGetValue(account.Id, out var session)) session.SelectModel(id);
            return Results.Json(new { ok = true, modelId = id });
        });

        app.MapGet("/api/usage", (HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();

            var (hourlyLimit, dailyLimit) = _limits.Allowance(Math.Max(1, _usage.ActiveUsers() + 1));
            var (hourUsed, dayUsed) = _usage.For(account.Id);
            return Results.Json(new
            {
                hourUsed,
                hourLimit = hourlyLimit,
                dayUsed,
                dayLimit = dailyLimit,
                totalAllTime = _usage.TotalFor(account.Id),
                activeUsers = Math.Max(1, _usage.ActiveUsers() + 1),
                exhausted = hourUsed >= hourlyLimit || dayUsed >= dailyLimit,
                recent = _usage.Recent(account.Id, 10).Select(e => new
                {
                    at = e.At,
                    model = e.ModelId,
                    prompt = e.PromptTokens,
                    completion = e.CompletionTokens,
                }),
            });
        });

        app.MapPost("/api/chat/reset", (HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            if (_sessions.TryGetValue(account.Id, out var session)) session.Reset();
            return Results.Json(new { ok = true });
        });

        // ------------------------------------------------------------ chats

        app.MapGet("/api/chats", (HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            return Results.Json(new
            {
                chats = _chats.List(account.Id).Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    modelId = t.ModelId,
                    createdAt = t.CreatedAt,
                    updatedAt = t.UpdatedAt,
                    messageCount = t.MessageCount,
                }),
            });
        });

        app.MapGet("/api/chats/{id}", (HttpContext http, string id) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            var thread = _chats.Get(account.Id, id);
            if (thread is null) return Results.Json(new { error = "Чат не найден." }, statusCode: 404);
            return Results.Json(new
            {
                id = thread.Id,
                title = thread.TitleOrFallback,
                modelId = thread.ModelId,
                messages = thread.Messages.Select(MessageJson),
            });
        });

        app.MapGet("/api/chats/latest", (HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            var thread = _chats.Latest(account.Id);
            return thread is null
                ? Results.Json(new { threadId = (string?)null })
                : Results.Json(new { threadId = thread.Id, title = thread.TitleOrFallback });
        });

        app.MapPost("/api/chats", (NewChatRequest request, HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            var model = string.IsNullOrWhiteSpace(request.ModelId) ? _options.Model : request.ModelId;
            // An empty "+" chat must be called "Новый чат", not stuck at "Чат" forever.
            var thread = _chats.Create(account.Id, request.Title, model);
            return Results.Json(new { id = thread.Id, title = thread.TitleOrFallback });
        });

        app.MapPost("/api/chats/{id}/rename", (HttpContext http, string id, RenameChatRequest request) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            return _chats.Rename(account.Id, id, request.Title ?? "Чат")
                ? Results.Json(new { ok = true })
                : Results.Json(new { error = "Чат не найден." }, statusCode: 404);
        });

        app.MapDelete("/api/chats/{id}", (HttpContext http, string id) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            return _chats.Delete(account.Id, id)
                ? Results.Json(new { ok = true })
                : Results.Json(new { error = "Чат не найден." }, statusCode: 404);
        });

        // ------------------------------------------------------------ files

        app.MapGet("/api/files", (HttpContext http, string? path, bool? recursive) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            var workspace = _workspaces.Get(account.Id, account.QuotaBytes);
            try
            {
                var entries = workspace.List(path ?? "", 500, recursive ?? false);
                return Results.Json(new
                {
                    path = path ?? "",
                    entries = entries.Select(e => new
                    {
                        name = Path.GetFileName(e.RelativePath.TrimEnd('/')),
                        path = e.RelativePath,
                        isDirectory = e.IsDirectory,
                        size = e.Size,
                        modified = e.ModifiedUtc,
                    }),
                });
            }
            catch (WorkspaceException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });

        // ------------------------------------------------------------ uploads
        // The phone sends the raw bytes of one file (an image for analysis, a document for
        // the workspace). Small and dependency-free on purpose: no multipart parsing.
        app.MapPost("/api/files/upload", async (HttpContext http, CancellationToken ct) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();

            string name;
            try { name = (http.Request.Query["name"].ToString() ?? "").Trim(); }
            catch { name = ""; }
            if (string.IsNullOrWhiteSpace(name)) name = "upload.bin";

            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0) return Results.Json(new { error = "Пустой файл." }, statusCode: 400);

            var isImage = IsImageName(name);
            if (isImage && bytes.Length > ImageAnalyzer.MaxBytes)
                return Results.Json(new { error = $"Изображение больше {ImageAnalyzer.MaxBytes / 1024 / 1024} МБ." }, statusCode: 413);

            var safeName = SafeUploadName(name);
            var folder = isImage ? "images" : "files";
            var path = $"{folder}/{DateTime.UtcNow:yyyy-MM-dd}-{safeName}";

            var workspace = _workspaces.Get(account.Id, account.QuotaBytes);
            try
            {
                await workspace.Engine.WriteBytesAsync(path, bytes, ct);
            }
            catch (WorkspaceException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }

            return Results.Json(new
            {
                ok = true,
                name = safeName,
                path,
                isImage,
                size = bytes.Length,
                url = $"/api/files/raw?path={Uri.EscapeDataString(path)}",
            });
        });

        // Serves a workspace file (an uploaded photo) back to the phone.
        app.MapGet("/api/files/raw", (HttpContext http, string path) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            try
            {
                var workspace = _workspaces.Get(account.Id, account.QuotaBytes);
                var bytes = workspace.Engine.ReadBytes(path);
                return Results.File(bytes, ContentTypeFor(path));
            }
            catch (Exception ex) when (ex is WorkspaceException or FileNotFoundException or DirectoryNotFoundException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 404);
            }
        });

        // ------------------------------------------------------------ image analysis

        app.MapPost("/api/vision", async (VisionRequest request, HttpContext http, CancellationToken ct) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();

            var question = (request.Question ?? "").Trim();
            if (question.Length == 0) question = "Что на этом изображении? Опиши подробно.";
            if (question.Length > 4000) return Results.Json(new { error = "Вопрос слишком длинный." }, statusCode: 400);

            var workspace = _workspaces.Get(account.Id, account.QuotaBytes);
            byte[] bytes;
            try
            {
                bytes = workspace.Engine.ReadBytes(request.Path ?? "");
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "Файл не найден: " + ex.Message }, statusCode: 404);
            }

            if (bytes.Length > ImageAnalyzer.MaxBytes)
                return Results.Json(new { error = "Изображение слишком большое для анализа." }, statusCode: 413);

            // --- dynamic token allowance (same policy as /api/chat)
            var activeUsers = Math.Max(1, _usage.ActiveUsers() + 1);
            var (hourlyLimit, dailyLimit) = _limits.Allowance(activeUsers);
            var (hourTokens, dayTokens) = _usage.For(account.Id);
            var hourLeft = Math.Max(0, hourlyLimit - hourTokens);
            var dayLeft = Math.Max(0, dailyLimit - dayTokens);
            var limitLeft = Math.Min(hourLeft, dayLeft);
            var exhausted = limitLeft <= 900;

            var modelId = exhausted ? _options.LiteModel
                : (string.IsNullOrWhiteSpace(account.ModelId) ? _options.Model : account.ModelId);
            if (modelId is not ("oli-pass" or "oli-son")) modelId = _options.Model;

            var fileName = Path.GetFileName((request.Path ?? "").Replace('\\', '/'));
            var block = ImageAnalyzer.Describe(fileName, bytes);
            var prompt = block + "\n\nВопрос пользователя: " + question;

            var session = GetSession(account);
            session.SelectModel(modelId);
            session.SetAnswerBudget(exhausted ? 220 : 0);

            var gate = _sessionLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(TimeSpan.FromSeconds(2), ct))
                return Results.Json(new { error = "Предыдущий ответ ещё готовится." }, statusCode: 429);

            try
            {
                // Vision goes into the same chat history as normal turns: a photo sent from
                // the phone used to vanish on restart, because only /api/chat wrote to the
                // thread store.
                var thread = _chats.Resolve(account.Id, request.ThreadId, question, modelId);
                if (thread is null)
                    return Results.Json(new { error = "Чат был удалён на другом устройстве.", deleted = true }, statusCode: 409);

                var threadId = thread.Id;
                session.UseThread(threadId, thread.Messages);
                if (request.ThreadId is null)
                    _chats.Append(account.Id, threadId, ChatEntry.User(question, files: [SafeRelative(request.Path ?? "")]));

                var answer = await session.SendAsync(prompt, progress: null, ct);

                var visionAppend = _chats.Append(account.Id, threadId, ChatEntry.Assistant(answer.Text));
                if (visionAppend.ThreadMissing)
                    return Results.Json(new { error = "Чат был удалён, ответ не сохранён.", deleted = true }, statusCode: 409);

                var promptTokens = TokenEstimator.Estimate(prompt) + 700;
                var completionTokens = TokenEstimator.Estimate(answer.Text);
                _usage.Add(new UsageEntry
                {
                    AccountId = account.Id,
                    ModelId = modelId,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                });
                var (hourNow, dayNow) = _usage.For(account.Id);

                return Results.Json(new
                {
                    text = answer.Text,
                    path = request.Path,
                    threadId,
                    model = modelId == _options.LiteModel ? "Olim Son" : "Olim Pass",
                    modelId,
                    limited = exhausted,
                    usage = new
                    {
                        hourUsed = hourNow,
                        hourLimit = hourlyLimit,
                        dayUsed = dayNow,
                        dayLimit = dailyLimit,
                        activeUsers = activeUsers,
                    },
                });
            }
            finally { gate.Release(); }
        });

        app.MapGet("/api/files/usage", (HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            var usage = _workspaces.Get(account.Id, account.QuotaBytes).Usage();
            return Results.Json(new
            {
                used = usage.UsedBytes,
                quota = usage.QuotaBytes,
                files = usage.FileCount,
                archives = usage.ArchiveCount,
                percent = usage.UsedPercent,
            });
        });

        app.MapGet("/api/files/read", async (HttpContext http, string path, CancellationToken ct) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            try
            {
                var workspace = _workspaces.Get(account.Id, account.QuotaBytes);
                var content = await workspace.ReadTextAsync(path, 200_000, ct);
                return Results.Json(new { path, content });
            }
            catch (WorkspaceException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });

        app.MapGet("/api/files/download", (HttpContext http, string path) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            try
            {
                var workspace = _workspaces.Get(account.Id, account.QuotaBytes);
                var full = workspace.ResolveForDownload(path);
                return Results.File(full, workspace.ContentTypeFor(path), Path.GetFileName(full));
            }
            catch (WorkspaceException ex) { return Results.Json(new { error = ex.Message }, statusCode: 404); }
        });

        app.MapGet("/api/files/archive", async (HttpContext http, CancellationToken ct) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            var workspace = _workspaces.Get(account.Id, account.QuotaBytes);
            var bytes = await workspace.ZipAllAsync(ct);
            return Results.File(bytes, "application/zip", $"workspace-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        });

        app.MapPost("/api/files/upload", async (HttpContext http, CancellationToken ct) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            if (!http.Request.HasFormContentType)
                return Results.Json(new { error = "Ожидается multipart/form-data." }, statusCode: 400);

            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.Json(new { error = "Файл не передан." }, statusCode: 400);

            var directory = form["path"].ToString().Trim().Trim('/');
            var name = UserWorkspace.SanitizeName(file.FileName);
            var relative = string.IsNullOrEmpty(directory) ? name : $"{directory}/{name}";

            try
            {
                var workspace = _workspaces.Get(account.Id, account.QuotaBytes);
                await using var stream = file.OpenReadStream();
                var message = await workspace.SaveUploadAsync(relative, stream, file.Length, ct);
                return Results.Json(new { ok = true, path = relative, message });
            }
            catch (WorkspaceException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });

        app.MapPost("/api/files/write", async (WriteFileRequest request, HttpContext http, CancellationToken ct) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            if (string.IsNullOrWhiteSpace(request.Path))
                return Results.Json(new { error = "Не указан путь." }, statusCode: 400);
            try
            {
                var workspace = _workspaces.Get(account.Id, account.QuotaBytes);
                var message = request.Append
                    ? await workspace.AppendTextAsync(request.Path, request.Content ?? "", ct)
                    : await workspace.WriteTextAsync(request.Path, request.Content ?? "", ct);
                return Results.Json(new { ok = true, message });
            }
            catch (WorkspaceException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });

        app.MapPost("/api/files/delete", (DeleteFileRequest request, HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            try
            {
                var workspace = _workspaces.Get(account.Id, account.QuotaBytes);
                return Results.Json(new { ok = true, message = workspace.Delete(request.Path ?? "", request.Recursive) });
            }
            catch (WorkspaceException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });

        app.MapPost("/api/files/mkdir", (DeleteFileRequest request, HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            try
            {
                var workspace = _workspaces.Get(account.Id, account.QuotaBytes);
                return Results.Json(new { ok = true, message = workspace.CreateDirectory(request.Path ?? "") });
            }
            catch (WorkspaceException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });

        // ------------------------------------------------------------ admin

        app.MapGet("/api/admin/users", (HttpContext http) =>
        {
            if (!IsAdminRequest(http)) return Unauthorized();
            var users = _accounts.List().Select(a => new
            {
                id = a.Id,
                userName = a.UserName,
                displayName = a.DisplayName,
                createdAt = a.CreatedAt,
                lastLoginAt = a.LastLoginAt,
                disabled = a.Disabled,
                isAdmin = a.IsAdmin,
                quota = a.QuotaBytes,
                used = _workspaces.Get(a.Id, a.QuotaBytes).Usage().UsedBytes,
                password = new { algorithm = "PBKDF2-HMAC-SHA256", iterations = a.PasswordIterations, storedAs = "hash+salt" },
            });
            return Results.Json(new { users });
        });

        app.MapPost("/api/admin/quota", async (AdminQuotaRequest request, HttpContext http) =>
        {
            if (!IsAdminRequest(http)) return Unauthorized();
            var ok = await _accounts.SetQuotaAsync(request.AccountId ?? "", request.QuotaBytes);
            return ok ? Results.Json(new { ok = true }) : Results.Json(new { error = "Пользователь не найден." }, statusCode: 404);
        });

        app.MapPost("/api/admin/disable", async (AdminDisableRequest request, HttpContext http) =>
        {
            if (!IsAdminRequest(http)) return Unauthorized();
            var ok = await _accounts.SetDisabledAsync(request.AccountId ?? "", request.Disabled);
            return ok ? Results.Json(new { ok = true }) : Results.Json(new { error = "Пользователь не найден." }, statusCode: 404);
        });

        app.MapPost("/api/admin/reset-password", async (AdminResetRequest request, HttpContext http) =>
        {
            if (!IsAdminRequest(http)) return Unauthorized();
            var (ok, error) = await _accounts.ResetPasswordAsync(request.AccountId ?? "", request.NewPassword ?? "");
            return ok ? Results.Json(new { ok = true }) : Results.Json(new { error }, statusCode: 400);
        });

        app.MapPost("/api/admin/delete", async (AdminDisableRequest request, HttpContext http) =>
        {
            if (!IsAdminRequest(http)) return Unauthorized();
            var ok = await _accounts.DeleteAsync(request.AccountId ?? "");
            if (ok)
            {
                _workspaces.Remove(request.AccountId!);
                _chats.DeleteAll(request.AccountId!);
            }
            return ok ? Results.Json(new { ok = true }) : Results.Json(new { error = "Пользователь не найден." }, statusCode: 404);
        });

        app.MapGet("/api/admin/stats", async (HttpContext http, CancellationToken ct) =>
        {
            if (!IsAdminRequest(http)) return Unauthorized();
            var users = _accounts.List();
            return Results.Json(new
            {
                users = users.Count,
                disabled = users.Count(u => u.Disabled),
                quotaPerUser = _options.UserQuotaBytes,
                totalQuota = users.Sum(u => u.QuotaBytes),
                used = users.Sum(u => _workspaces.Get(u.Id, u.QuotaBytes).Usage().UsedBytes),
                dataRoot = _options.DataRoot,
                model = _options.Model,
                llama = await _llama.IsAliveAsync(ct),
            });
        });
    }

    // ================================================================ helpers

    private AgentSession GetSession(Account account)
    {
        return _sessions.GetOrAdd(account.Id, _ =>
        {
            var workspace = _workspaces.Get(account.Id, account.QuotaBytes);
            var settings = new OliSettings
            {
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                Temperature = _options.Temperature,
                TopP = _options.TopP,
                TopK = _options.TopK,
                RepeatPenalty = _options.RepeatPenalty,
                WebSearchEnabled = _options.WebSearchEnabled,
                WebSearchOpenWeb = _options.OpenWeb,
                WorkspaceEnabled = _options.WorkspaceToolsEnabled,
                AllowCommandExecution = false,
                AllowedCommands = [],
                WorkspaceQuotaBytes = account.QuotaBytes,
                SystemPromptExtra = _options.Preamble,
            };

            var memory = new MemoryStore(Path.Combine(_options.DataRoot, "memory", $"chat-{account.Id}.json"));
            var profile = new UserProfileStore(Path.Combine(_options.DataRoot, "profiles", $"{account.Id}.json"));

            return new AgentSession(settings, _llama, _web, memory, profile, workspace.Engine,
                ownsWebSearch: false, allowCommands: false);
        });
    }

    private object ModelInfo(string model, string id, string title) => new
    {
        id,
        title,
        description = id == "oli-son"
            ? "Лёгкая и быстрая модель OLI — когда важна скорость или лимит исчерпан."
            : "Флагманская модель OLI — сложные задачи, код, анализ.",
        maxTokens = id == "oli-son" ? 320 : 640,
    };

    private static string SafeRelative(string path) => path.Replace('\\', '/');

    /// <summary>
    /// One message on the wire. Shared by the desktop and the phone, so a bubble looks
    /// identical everywhere: role, visible text, time, attachments.
    /// </summary>
    private static object MessageJson(ChatEntry m) => new
    {
        id = m.Id,
        role = m.Role == ChatRole.User ? "user" : "assistant",
        text = ChatCore.VisibleText(m),
        at = m.At,
        files = m.Files,
    };

    private static bool IsImageName(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp";
    }

    private static string SafeUploadName(string name)
    {
        var clean = Path.GetFileName(name.Replace('\\', '/')).Trim();
        foreach (var bad in Path.GetInvalidFileNameChars()) clean = clean.Replace(bad, '_');
        if (clean.Length == 0) clean = "upload.bin";
        if (clean.Length > 80)
        {
            var ext = Path.GetExtension(clean);
            clean = clean[..Math.Max(1, 80 - ext.Length)] + ext;
        }
        return clean;
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".pdf" => "application/pdf",
        ".txt" or ".md" or ".json" or ".cs" or ".kt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    // ---------------------------------------------------------------- throttling

    private static string ClientKey(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Six failed logins per IP in a minute → one minute of cooldown.</summary>
    private async Task<bool> IsThrottledAsync(HttpContext http)
    {
        var key = ClientKey(http);
        if (!_throttle.TryGetValue(key, out var state)) return false;
        if (state.Until > DateTimeOffset.UtcNow) return true;
        if (state.Until != default) _throttle.TryRemove(key, out _);

        // Slow every login attempt a little: makes online guessing expensive.
        await Task.Delay(250);
        return false;
    }

    private void RegisterFailure(HttpContext http)
    {
        var key = ClientKey(http);
        var state = _throttle.TryGetValue(key, out var current) ? current : (Count: 0, Until: default(DateTimeOffset));
        var count = state.Count + 1;
        _throttle[key] = count >= 6
            ? (0, DateTimeOffset.UtcNow.AddMinutes(1))
            : (count, default);
    }

    private void ClearFailures(HttpContext http) => _throttle.TryRemove(ClientKey(http), out _);

    private static IResult TooManyAttempts() =>
        Results.Json(new { error = "Слишком много попыток входа. Подождите минуту." }, statusCode: 429);

    private Account? CurrentAccount(HttpContext http)
    {
        var token = BearerToken(http);
        if (token is null) return null;
        return _accounts.ResolveToken(token, out _);
    }

    private static string? BearerToken(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue("Authorization", out var header)) return null;
        var value = header.ToString();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim() : null;
    }

    private bool IsAdminRequest(HttpContext http)
    {
        if (!string.IsNullOrWhiteSpace(_options.AdminToken))
        {
            if (http.Request.Headers.TryGetValue("X-OLI-Admin", out var adminHeader) &&
                string.Equals(adminHeader.ToString(), _options.AdminToken, StringComparison.Ordinal))
                return true;
        }

        var account = CurrentAccount(http);
        if (account?.IsAdmin == true) return true;

        // Loopback = the PC itself (the desktop admin remote).
        var remote = http.Connection.RemoteIpAddress;
        return remote is not null && IPAddress.IsLoopback(remote);
    }

    private static object ToAuthResponse(Account account, string token, DateTimeOffset expiresAt) => new
    {
        token,
        expiresAt,
        user = ToProfile(account),
    };

    private static object ToProfile(Account account) => new
    {
        id = account.Id,
        userName = account.UserName,
        displayName = account.DisplayName,
        createdAt = account.CreatedAt,
        quota = account.QuotaBytes,
        isAdmin = account.IsAdmin,
    };

    private static string DeviceOf(HttpContext http) =>
        http.Request.Headers.TryGetValue("X-OLI-Device", out var device)
            ? device.ToString()[..Math.Min(120, device.ToString().Length)]
            : http.Request.Headers.UserAgent.ToString()[..Math.Min(120, http.Request.Headers.UserAgent.ToString().Length)];

    private static IResult Unauthorized() =>
        Results.Json(new { error = "Требуется вход.", code = "unauthorized" }, statusCode: 401);

    // ================================================================ DTOs

    public sealed record RegisterRequest(string? UserName, string? Password, string? DisplayName, string? InviteCode);
    public sealed record LoginRequest(string? UserName, string? Password);
    public sealed record ChangePasswordRequest(string? OldPassword, string? NewPassword);
    public sealed record UpdateProfileRequest(string? DisplayName);
    public sealed record ChatRequestDto(string? Message, string? ThreadId);
    public sealed record ModelChoiceRequest(string? ModelId);
    public sealed record NewChatRequest(string? Title, string? ModelId);
    public sealed record RenameChatRequest(string? Title);
    public sealed record WriteFileRequest(string? Path, string? Content, bool Append);
    public sealed record VisionRequest(string? Path, string? Question, string? ThreadId);
    public sealed record DeleteFileRequest(string? Path, bool Recursive);
    public sealed record AdminQuotaRequest(string? AccountId, long QuotaBytes);
    public sealed record AdminDisableRequest(string? AccountId, bool Disabled);
    public sealed record AdminResetRequest(string? AccountId, string? NewPassword);
}
