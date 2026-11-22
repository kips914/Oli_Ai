using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OLI.Agent;
using OLI.Core;
using OLI.Engine;
using OLI.Memory;
using OLI.Server.Accounts;
using OLI.Server.Storage;
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
    private readonly LlamaClient _llama;
    private readonly WebSearch _web;
    private readonly MemoryStore _memory;
    private readonly UserProfileStore _profile;

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
            if (IPAddress.TryParse(_options.Host, out var address))
                options.Listen(address, _options.Port);
            else
                options.ListenAnyIP(_options.Port);
        });

        var app = builder.Build();
        MapEndpoints(app);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await app.StartAsync(_cts.Token);
        _app = app;
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
            api = 2,
            model = _options.Model,
            registration = _options.AllowRegistration,
            endpoints = new[] { "/api/health", "/api/register", "/api/login", "/api/me", "/api/chat", "/api/files" },
        }));

        app.MapGet("/api/health", () => Results.Json(new
        {
            status = "ok",
            service = "oli-server",
            version = AppVersion.Current,
            time = DateTimeOffset.UtcNow,
            registration = _options.AllowRegistration,
            llama = _llama.IsAliveAsync().GetAwaiter().GetResult(),
        }));

        // ------------------------------------------------------------ auth

        app.MapPost("/api/register", async (RegisterRequest request, HttpContext http, CancellationToken ct) =>
        {
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
            var account = await _accounts.AuthenticateAsync(request.UserName ?? "", request.Password ?? "");
            if (account is null)
                return Results.Json(new { error = "Неверное имя пользователя или пароль." }, statusCode: 401);

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

            var gate = _sessionLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(TimeSpan.FromSeconds(2), ct))
                return Results.Json(new { error = "Предыдущий ответ ещё готовится." }, statusCode: 429);

            try
            {
                var session = GetSession(account);
                var answer = await session.SendAsync(text, progress: null, ct);
                return Results.Json(new
                {
                    text = answer.Text,
                    files = answer.FilesToSend.Select(SafeRelative).ToList(),
                    model = _options.Model,
                });
            }
            finally { gate.Release(); }
        });

        app.MapPost("/api/chat/reset", (HttpContext http) =>
        {
            var account = CurrentAccount(http);
            if (account is null) return Unauthorized();
            if (_sessions.TryGetValue(account.Id, out var session)) session.Reset();
            return Results.Json(new { ok = true });
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
            if (ok) _workspaces.Remove(request.AccountId!);
            return ok ? Results.Json(new { ok = true }) : Results.Json(new { error = "Пользователь не найден." }, statusCode: 404);
        });

        app.MapGet("/api/admin/stats", (HttpContext http) =>
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
                llama = _llama.IsAliveAsync().GetAwaiter().GetResult(),
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

    private string SafeRelative(string path) => path.Replace('\\', '/');

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
    public sealed record ChatRequestDto(string? Message);
    public sealed record WriteFileRequest(string? Path, string? Content, bool Append);
    public sealed record DeleteFileRequest(string? Path, bool Recursive);
    public sealed record AdminQuotaRequest(string? AccountId, long QuotaBytes);
    public sealed record AdminDisableRequest(string? AccountId, bool Disabled);
    public sealed record AdminResetRequest(string? AccountId, string? NewPassword);
}
