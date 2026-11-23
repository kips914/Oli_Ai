using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OLI.Core;

namespace OLI.Engine;

/// <summary>One probe of one address: what is there and what it can do.</summary>
public sealed record ProbeResult(ConnectionState State, string Address, string Version, string Engine, string Model, int? HttpStatus, string Detail)
{
    public bool CanChat => State == ConnectionState.Ready;

    public string Message => ConnectionCore.Describe(State, Address, Engine, Detail);
}

/// <summary>
/// The single HTTP client for an OLI server, used by the desktop "Путь телефона" test,
/// by <c>OLI.SelfTest</c> against a real server, and (through the same JSON contract) by
/// the Android app. It replaces <c>LlamaClient.IsAliveAsync</c>-style guessing with one
/// explicit state machine (<see cref="ConnectionState"/>) and one retry policy.
/// </summary>
public sealed class OliServerClient : IDisposable
{
    private readonly HttpClient _http;

    public OliServerClient(string? baseUrl = null, TimeSpan? timeout = null)
    {
        BaseUrl = ConnectionCore.Normalize(baseUrl) ?? "";
        _http = new HttpClient { BaseAddress = new Uri("http://placeholder/"), Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OLI-Client/" + AppVersion.Current);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string BaseUrl { get; private set; }

    public string? Token { get; set; }

    public void Use(string baseUrl)
    {
        var normalized = ConnectionCore.Normalize(baseUrl);
        if (normalized is null) throw new ArgumentException($"Некорректный адрес: {baseUrl}", nameof(baseUrl));
        BaseUrl = normalized;
    }

    // ------------------------------------------------------------------ health

    /// <summary>
    /// Probes <c>/api/health</c> with the shared retry policy and classifies the answer.
    /// Never throws: a dead address is a result, not an exception.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(string? address = null, CancellationToken ct = default)
    {
        var target = ConnectionCore.Normalize(address ?? BaseUrl);
        if (target is null)
            return new ProbeResult(ConnectionState.ServerDown, address ?? "", "", "", "", null, "Адрес не указан или некорректен.");

        string? lastDetail = null;
        int? lastStatus = null;

        foreach (var delay in RetryPolicy.ProbeDelaysMs)
        {
            if (delay > 0)
            {
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            }

            try
            {
                using var response = await _http.GetAsync(target + "/api/health", ct);
                lastStatus = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    lastDetail = $"HTTP {(int)response.StatusCode}";
                    // 404 = something is listening but it is not OLI.
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return new ProbeResult(ConnectionState.NotOli, target, "", "", "", lastStatus, "По адресу отвечает не OLI.");
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var service = Str(root, "service");
                var version = Str(root, "version");
                var engine = Str(root, "engine");
                var model = Str(root, "model");
                var engineAlive = root.TryGetProperty("llama", out var llama) && llama.ValueKind == JsonValueKind.True;

                var state = ConnectionCore.Classify(true, service, engineAlive, string.IsNullOrWhiteSpace(service) ? null : service == "oli-server");
                return new ProbeResult(state, target, version, engine, model, lastStatus, ConnectionCore.Describe(state, target, engine));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                lastDetail = "таймаут";
            }
            catch (HttpRequestException ex)
            {
                lastDetail = ex.Message;
            }
            catch (JsonException)
            {
                return new ProbeResult(ConnectionState.NotOli, target, "", "", "", lastStatus, "По адресу отвечает не OLI (не JSON-ответ).");
            }
        }

        return new ProbeResult(ConnectionState.ServerDown, target, "", "", "", lastStatus, lastDetail ?? "нет ответа");
    }

    /// <summary>Tries every candidate address in order and returns the first usable one.</summary>
    public async Task<ProbeResult?> FindFirstAsync(IEnumerable<string?> candidates, CancellationToken ct = default)
    {
        ProbeResult? lastFailure = null;
        foreach (var candidate in ConnectionCore.CandidateOrder(candidates))
        {
            var result = await ProbeAsync(candidate, ct);
            if (result.CanChat) return result;
            lastFailure ??= result;
        }
        return lastFailure;
    }

    // ------------------------------------------------------------------ auth

    public async Task<AuthOutcome> LoginAsync(string userName, string password, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { userName, password });
        return await SendAuthAsync("/api/login", payload, ct);
    }

    public async Task<AuthOutcome> RegisterAsync(string userName, string password, string displayName, string inviteCode, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { userName, password, displayName, inviteCode });
        return await SendAuthAsync("/api/register", payload, ct);
    }

    private async Task<AuthOutcome> SendAuthAsync(string path, string payload, CancellationToken ct)
    {
        if (BaseUrl.Length == 0) return new AuthOutcome(false, "Адрес сервера не задан.", null, null, 0);

        foreach (var delay in RetryPolicy.ProbeDelaysMs)
        {
            if (delay > 0) { try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; } }
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(BaseUrl + path, content, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
                var root = doc.RootElement;

                if (!response.IsSuccessStatusCode)
                {
                    var error = Str(root, "error");
                    // 4xx is the user's problem (wrong password, weak password): do not retry.
                    return new AuthOutcome(false, error.Length > 0 ? error : $"Сервер ответил {(int)response.StatusCode}.", null, null, (int)response.StatusCode);
                }

                var token = Str(root, "token");
                return new AuthOutcome(true, "", token.Length > 0 ? token : null, root.TryGetProperty("user", out var user) ? user.Clone() : null, (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (delay == RetryPolicy.ProbeDelaysMs[^1])
                    return new AuthOutcome(false, "Компьютер не ответил: " + ex.Message, null, null, 0);
            }
        }
        return new AuthOutcome(false, "Компьютер не ответил.", null, null, 0);
    }

    // ------------------------------------------------------------------ chat

    /// <summary>
    /// One chat turn. A 503 ("engine down") is returned as a failure without retrying —
    /// repeating it would only waste the user's time; transport errors are retried once.
    /// </summary>
    public async Task<ChatOutcome> SendAsync(string message, string? threadId, CancellationToken ct = default)
    {
        if (BaseUrl.Length == 0) return new ChatOutcome(false, null, null, "Адрес сервера не задан.", 0, false);

        var payload = JsonSerializer.Serialize(new { message, threadId });
        string? lastError = null;

        foreach (var delay in RetryPolicy.ChatDelaysMs)
        {
            if (delay > 0) { try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; } }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/chat")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrWhiteSpace(Token))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

                using var response = await _http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                JsonElement root;
                try { root = JsonDocument.Parse(body.Length == 0 ? "{}" : body).RootElement.Clone(); }
                catch (JsonException) { return new ChatOutcome(false, null, null, "Сервер вернул не JSON — проверьте версию OLI.", (int)response.StatusCode, false); }

                if (response.IsSuccessStatusCode)
                {
                    var text = Str(root, "text");
                    var id = Str(root, "threadId");
                    var files = root.TryGetProperty("files", out var filesNode) && filesNode.ValueKind == JsonValueKind.Array
                        ? filesNode.EnumerateArray().Select(f => f.GetString() ?? "").Where(f => f.Length > 0).ToList()
                        : new List<string>();
                    return new ChatOutcome(true, ChatCore.VisibleText(ChatEntry.Assistant(text, files)), id.Length > 0 ? id : null, "", (int)response.StatusCode, false);
                }

                var error = Str(root, "error");
                var status = (int)response.StatusCode;
                if (status == 503)
                    return new ChatOutcome(false, null, Str(root, "threadId") is { Length: > 0 } tid ? tid : null,
                        error.Length > 0 ? error : "Движок модели не запущен на ПК.", status, true);
                if (status == 409)
                    return new ChatOutcome(false, null, null, error.Length > 0 ? error : "Чат был удалён.", status, false);

                lastError = error.Length > 0 ? error : $"Сервер ответил {status}.";
                if (!RetryPolicy.ShouldRetry(status, timeout: false)) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (TaskCanceledException) { lastError = "Сервер не ответил за отведённое время (модель могла ещё загружаться)."; }
            catch (HttpRequestException ex) { lastError = ex.Message; }
        }

        return new ChatOutcome(false, null, null, lastError ?? "Не удалось отправить сообщение.", 0, false);
    }

    // ------------------------------------------------------------------ charts

    /// <summary>Threads for the drawer; tolerant of old servers (missing fields).</summary>
    public async Task<IReadOnlyList<ChatThreadSummary>> ListChatsAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync("/api/chats", ct);
        if (json is null) return [];
        if (!json.Value.TryGetProperty("chats", out var chats) || chats.ValueKind != JsonValueKind.Array) return [];

        var result = new List<ChatThreadSummary>();
        foreach (var item in chats.EnumerateArray())
        {
            var id = Str(item, "id");
            if (id.Length == 0) continue;
            result.Add(new ChatThreadSummary(
                id,
                Str(item, "title"),
                Str(item, "modelId"),
                Date(item, "createdAt"),
                Date(item, "updatedAt"),
                item.TryGetProperty("messageCount", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 0));
        }
        return result;
    }

    /// <summary>Full transcript of one thread, as visible bubbles.</summary>
    public async Task<IReadOnlyList<ChatEntry>> LoadChatAsync(string threadId, CancellationToken ct = default)
    {
        var json = await GetJsonAsync("/api/chats/" + Uri.EscapeDataString(threadId), ct);
        if (json is null) return [];
        if (!json.Value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return [];

        var result = new List<ChatEntry>();
        foreach (var item in messages.EnumerateArray())
        {
            var role = Str(item, "role") == "user" ? ChatRole.User : ChatRole.Assistant;
            var text = Str(item, "text");
            var id = Str(item, "id");
            var at = Date(item, "at");
            var files = item.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : new List<string>();
            result.Add(new ChatEntry
            {
                Id = id.Length > 0 ? id : Guid.NewGuid().ToString("N")[..12],
                Role = role,
                Text = text,
                At = at,
                Files = files,
            });
        }
        return result;
    }

    private async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct)
    {
        if (BaseUrl.Length == 0) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            if (!string.IsNullOrWhiteSpace(Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length == 0) return null;
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static DateTimeOffset Date(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var date)
            ? date
            : DateTimeOffset.UtcNow;

    public void Dispose() => _http.Dispose();
}

public sealed record AuthOutcome(bool Ok, string Error, string? Token, JsonElement? User, int HttpStatus);

public sealed record ChatOutcome(bool Ok, string? Text, string? ThreadId, string Error, int HttpStatus, bool EngineDown);
