using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OLI.Core;

namespace OLI.Engine;

/// <summary>
/// Thin client for the llama.cpp OpenAI-compatible server.
/// Supports Qwen-style sampling (top_p / top_k / min_p / repeat_penalty),
/// thinking-mode control and optional token streaming.
/// </summary>
public sealed class LlamaClient : IDisposable
{
    private readonly HttpClient _http;
    public string BaseUrl { get; }

    public LlamaClient(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromMinutes(10) };
    }

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
        var body = BuildBody(messages, model, temperature, maxTokens, topP, topK, repeatPenalty, stream: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.PostAsJsonAsync("v1/chat/completions", body, ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
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
        var body = BuildBody(messages, model, temperature, maxTokens, topP, topK, repeatPenalty, stream: true);
        var sb = new StringBuilder();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

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
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() == 0) continue;
                    var delta = choices[0].TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var c)
                        ? c.GetString()
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

    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        try { using var r = await _http.GetAsync("health", ct); return r.IsSuccessStatusCode; }
        catch { return false; }
    }

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

    /// <summary>The model identifier llama-server is actually serving, if it reports one.</summary>
    public async Task<string?> GetServedModelAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync("v1/models", ct);
            if (!r.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0 &&
                data[0].TryGetProperty("id", out var id))
                return id.GetString();
        }
        catch { }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
