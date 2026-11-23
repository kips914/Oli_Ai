using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OLI.Core;

namespace OLI.Engine;

/// <summary>
/// Thin client for an OpenAI-compatible local server.
///
/// OLI is built around llama.cpp, but the same contract is spoken by Ollama (port 11434),
/// LM Studio (1234), vLLM and llama-server (8080) — so the user's already-downloaded
/// Ollama model can serve OLI without installing llama.cpp at all:
///   * liveness is probed the way each backend understands it (<c>/health</c>, then
///     <c>GET /</c> for Ollama, then <c>/v1/models</c>);
///   * when the model name OLI asks for ("oli", "oli-pass"…) does not exist on the
///     backend, the real model name is taken from <c>/v1/models</c> (or <c>/api/tags</c>)
///     and used instead — no <c>ollama cp</c> aliases needed;
///   * an error body from the backend is turned into a Russian sentence instead of a
///     bare "500 Internal Server Error".
/// </summary>
public sealed class LlamaClient : IDisposable
{
    private readonly HttpClient _http;
    private string _servedModel = "";
    private DateTimeOffset _modelCheck = DateTimeOffset.MinValue;
    private bool? _ollama;

    public string BaseUrl { get; }

    public LlamaClient(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>True when the backend answered like Ollama (helps the UI name it correctly).</summary>
    public bool? IsOllama => _ollama;

    public async Task<string> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        double temperature,
        int maxTokens,
        double topP = 0.95,
        int topK = 20,
        double repeatPenalty = 1.05,
        CancellationToken ct = default)
    {
        var body = BuildBody(messages, await ResolveModelAsync(model, ct), temperature, maxTokens, topP, topK, repeatPenalty, stream: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.PostAsJsonAsync("v1/chat/completions", body, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(Explain(response.StatusCode, payload, model), null, response.StatusCode);

        using var doc = JsonDocument.Parse(payload);
        var text = ReadContent(doc.RootElement);
        sw.Stop();
        OliLog.Info("ENGINE", $"Generation finished in {sw.Elapsed.TotalSeconds:F2}s, chars={text.Length}");
        return text.Trim();
    }

    /// <summary>
    /// Streams tokens through <paramref name="onDelta"/> and still returns the full
    /// text. Falls back to the non-streaming call if the server rejects streaming.
    /// </summary>
    public async Task<string> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        double temperature,
        int maxTokens,
        Action<string> onDelta,
        double topP = 0.95,
        int topK = 20,
        double repeatPenalty = 1.05,
        CancellationToken ct = default)
    {
        var body = BuildBody(messages, await ResolveModelAsync(model, ct), temperature, maxTokens, topP, topK, repeatPenalty, stream: true);
        var sb = new StringBuilder();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(Explain(response.StatusCode, error, model), null, response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = line[5..].Trim();
                if (payload == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                        continue;
                    // llama.cpp streams "delta", some backends (and non-stream fallbacks) use "message".
                    var delta = choices[0].TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var c)
                        ? c.GetString()
                        : choices[0].TryGetProperty("message", out var m) && m.TryGetProperty("content", out var mc)
                            ? mc.GetString()
                            : null;
                    if (!string.IsNullOrEmpty(delta))
                    {
                        sb.Append(delta);
                        onDelta(delta);
                    }
                }
                catch (JsonException) { /* keep-alive or partial chunk */ }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OliLog.Warning("ENGINE", $"Стриминг недоступен ({ex.Message}); использую обычный режим.");
            return await ChatAsync(messages, model, temperature, maxTokens, topP, topK, repeatPenalty, ct);
        }

        sw.Stop();
        OliLog.Info("ENGINE", $"Stream finished in {sw.Elapsed.TotalSeconds:F2}s, chars={sb.Length}");
        return sb.ToString().Trim();
    }

    private static Dictionary<string, object?> BuildBody(
        IReadOnlyList<ChatMessage> messages,
        string model,
        double temperature,
        int maxTokens,
        double topP,
        int topK,
        double repeatPenalty,
        bool stream) => new()
    {
        ["model"] = model,
        ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        ["temperature"] = Math.Round(temperature, 2),
        ["top_p"] = Math.Round(topP, 3),
        ["top_k"] = topK,
        ["repeat_penalty"] = Math.Round(repeatPenalty, 2),
        ["max_tokens"] = maxTokens,
        ["stream"] = stream,
        ["cache_prompt"] = true,
    };

    // ------------------------------------------------------------------ liveness

    /// <summary>
    /// Works with llama.cpp (<c>/health</c>), Ollama (<c>/</c> and <c>/api/tags</c>) and
    /// anything else that lists models. Returns false only when nothing answers.
    /// </summary>
    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        // 1. llama.cpp and friends
        try
        {
            using var health = await _http.GetAsync("health", ct);
            if (health.IsSuccessStatusCode)
            {
                _ollama = false;
                return true;
            }
        }
        catch { return false; } // socket refused → nothing is listening at all

        // The port answers but /health 404s: most likely Ollama ("Ollama is running" on /).
        try
        {
            using var root = await _http.GetAsync("", ct);
            if (root.IsSuccessStatusCode)
            {
                var text = await root.Content.ReadAsStringAsync(ct);
                var looksOllama = root.Content.Headers.ContentType?.MediaType?.Contains("text/plain") == true ||
                                  text.Contains("Ollama", StringComparison.OrdinalIgnoreCase) ||
                                  await OllamaTagsAsync(ct) is { Count: > 0 };
                _ollama = looksOllama;
                return looksOllama;
            }
        }
        catch { /* keep trying */ }

        // 2. OpenAI-compatible model list is the last honest signal.
        var models = await ListModelsAsync(ct);
        return models.Count > 0;
    }

    /// <summary>Model ids the backend actually serves (empty when it cannot be read).</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("v1/models", ct);
            if (!response.IsSuccessStatusCode) return [];
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];
            return data.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "")
                .Where(id => id.Length > 0)
                .ToList();
        }
        catch { return []; }
    }

    private async Task<List<string>> OllamaTagsAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("api/tags", ct);
            if (!response.IsSuccessStatusCode) return [];
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return [];
            return models.EnumerateArray()
                .Select(item => item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "")
                .Where(name => name.Length > 0)
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// OLI's own model ids ("oli", "oli-pass", "oli-son") usually do not exist on Ollama.
    /// In that case the first real model of the backend is used, so a user who already
    /// downloaded a model in Ollama gets answers without creating aliases by hand.
    /// </summary>
    private async Task<string> ResolveModelAsync(string requested, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _modelCheck < TimeSpan.FromMinutes(2))
            return _servedModel.Length > 0 ? _servedModel : requested;

        _modelCheck = DateTimeOffset.UtcNow;
        var available = await ListModelsAsync(ct);
        if (available.Count == 0 && await OllamaTagsAsync(ct) is { Count: > 0 } tags) available = tags;

        if (available.Count == 0)
        {
            _servedModel = "";
            return requested;
        }

        // Exact match wins (llama.cpp serves the id given by --alias).
        var match = available.FirstOrDefault(m => m.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            _servedModel = "";
            return requested;
        }

        var chosen = available[0];
        _servedModel = chosen;
        OliLog.Info("ENGINE", $"Бэкенд не знает модель «{requested}» — использую «{chosen}» ({BaseUrl})");
        return chosen;
    }

    private static string ReadContent(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
                return content.GetString() ?? "";
            // llama.cpp with --reasoning-format sometimes returns completed content only here.
            if (first.TryGetProperty("text", out var text)) return text.GetString() ?? "";
        }
        return "";
    }

    /// <summary>Turns a backend error body into something a student can act on.</summary>
    private string Explain(System.Net.HttpStatusCode status, string payload, string requestedModel)
    {
        var detail = payload.Length > 400 ? payload[..400] : payload;
        var notFoundModel = detail.Contains("model", StringComparison.OrdinalIgnoreCase) &&
                            (detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                             detail.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
        var hint = (int)status switch
        {
            404 when notFoundModel =>
                $"Модель «{requestedModel}» не найдена на {BaseUrl}. Скачайте модель (например: ollama pull qwen3:4b) " +
                "или выберите её в настройках OLI.",
            404 => $"Адрес {BaseUrl} отвечает, но у него нет /v1/chat/completions — это не llama-server и не Ollama. " +
                   "Проверьте порт: Ollama — 11434, llama.cpp — 8080, LM Studio — 1234.",
            401 or 403 => $"Сервер моделей отклонил запрос ({(int)status}). Если это облако — нужен ключ доступа.",
            429 => "Сервер моделей просит подождать: слишком много запросов подряд.",
            500 or 502 or 503 =>
                $"Движок модели ответил {(int)status}: {Short(detail)}. Обычно это значит, что модель ещё загружается " +
                "или не хватает памяти — подождите минуту и повторите.",
            _ => $"Движок модели ответил {(int)status}: {Short(detail)}",
        };
        return hint;
    }

    private static string Short(string text) => text.Length <= 160 ? text : text[..160] + "…";

    // ------------------------------------------------------------------ legacy helpers

    public async Task<string?> GetPropsAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync("props", ct);
            if (!r.IsSuccessStatusCode) return null;
            return await r.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }

    /// <summary>The model identifier the backend is actually serving, if it reports one.</summary>
    public async Task<string?> GetServedModelAsync(CancellationToken ct = default)
    {
        if (_servedModel.Length > 0) return _servedModel;
        var models = await ListModelsAsync(ct);
        return models.Count > 0 ? models[0] : null;
    }

    public void Dispose() => _http.Dispose();
}
