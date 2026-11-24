using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OLI.Core;

namespace OLI.Telegram.Tunnel;

/// <summary>
/// Resilient Bot API client. Every call walks the tunnel chain and remembers the
/// last one that worked, so a blocked network costs one failed request, not all of them.
/// </summary>
public sealed class TelegramApi : IDisposable
{
    private readonly string _token;
    private readonly List<IHttpTunnel> _tunnels;
    private int _preferred;
    private readonly DohPinnedTunnel? _dohTunnel;

    public TelegramApi(string token, bool useDoh, string? relayBaseUrl, string? proxyUrl, string? relaySecret = null)
    {
        _token = NormalizeToken(token);
        _tunnels = [];

        if (!string.IsNullOrWhiteSpace(relayBaseUrl))
            _tunnels.Add(new RelayTunnel(relayBaseUrl, DirectTunnel.Create(), relaySecret));
        if (!string.IsNullOrWhiteSpace(proxyUrl))
            _tunnels.Add(new ProxyTunnel(proxyUrl));
        if (useDoh)
        {
            _dohTunnel = new DohPinnedTunnel();
            _tunnels.Add(_dohTunnel);
        }
        _tunnels.Add(new DirectTunnel(http: DirectTunnel.Create()));

        OliLog.Info("TELEGRAM", "Цепочка транспортов: " + string.Join(" → ", _tunnels.Select(t => t.Name)));
    }

    public static string NormalizeToken(string raw)
    {
        var token = (raw ?? "").Trim();
        if (token.StartsWith("bot", StringComparison.OrdinalIgnoreCase) && token.Contains(':'))
            token = token[3..];
        return token;
    }

    public string ActiveTunnel => _tunnels.Count == 0 ? "none" : _tunnels[_preferred].Name;

    /// <summary>IP the DoH transport resolved for api.telegram.org (diagnostics).</summary>
    public Task<System.Net.IPAddress?> ResolveDohAsync(CancellationToken ct = default) =>
        _dohTunnel?.ResolveAsync(ct) ?? Task.FromResult<System.Net.IPAddress?>(null);

    public async Task<JsonElement> CallAsync(string method, Dictionary<string, object?>? parameters, CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < _tunnels.Count; attempt++)
        {
            var index = (_preferred + attempt) % _tunnels.Count;
            var tunnel = _tunnels[index];
            // Fresh content per attempt: an HttpClient may consume/dispose the body
            // of a failed request, which would break the next transport.
            using var content = BuildContent(parameters);
            try
            {
                using var response = await tunnel.SendAsync(method, content, _token, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if ((int)response.StatusCode is 400 or 401 or 403 or 404)
                {
                    // Genuine API error (bad token, chat not found) — retrying through
                    // another transport would fail identically.
                    _preferred = index;
                    throw new TelegramApiException(ParseDescription(body) ?? $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);
                }

                if ((int)response.StatusCode == 429)
                {
                    // Rate limited: Telegram asked us to wait. Surface it to the caller
                    // instead of hammering the API through every tunnel.
                    throw new TelegramRateLimitedException(
                        ParseDescription(body) ?? "Too Many Requests",
                        ParseRetryAfter(body));

                    static int ParseRetryAfter(string json)
                    {
                        try
                        {
                            using var parsed = JsonDocument.Parse(json);
                            if (parsed.RootElement.TryGetProperty("parameters", out var pars) &&
                                pars.TryGetProperty("retry_after", out var retry) && retry.TryGetInt32(out var value))
                                return Math.Clamp(value, 1, 300);
                        }
                        catch { }
                        return 5;
                    }
                }

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                    throw new TelegramApiException(ParseDescription(body) ?? "Telegram вернул ok=false", (int)response.StatusCode);

                if (_preferred != index)
                {
                    OliLog.Info("TELEGRAM", $"Транспорт переключён на «{tunnel.Name}»");
                    _preferred = index;
                }
                return doc.RootElement.TryGetProperty("result", out var result)
                    ? result.Clone()
                    : JsonDocument.Parse("null").RootElement.Clone();
            }
            catch (TelegramApiException) { throw; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = new TimeoutException("таймаут");
                OliLog.Warning("TELEGRAM", $"«{tunnel.Name}»: таймаут");
            }
            catch (Exception ex)
            {
                lastError = ex;
                OliLog.Warning("TELEGRAM", $"«{tunnel.Name}» недоступен: {Short(ex)}");
            }
        }

        throw new TelegramUnavailableException(
            "Не удалось связаться с Telegram ни одним способом " +
            $"({string.Join(", ", _tunnels.Select(t => t.Name))}). Последняя ошибка: {Short(lastError)}", lastError);
    }

    private static string Short(Exception? ex) => ex switch
    {
        null => "неизвестно",
        HttpRequestException { InnerException: not null } he => $"{he.Message} ({he.InnerException.Message})",
        _ => ex.Message,
    };

    private static HttpContent BuildContent(Dictionary<string, object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return new StringContent("{}", Encoding.UTF8, "application/json");

        var hasFile = parameters.Values.Any(v => v is TelegramFile);
        if (!hasFile)
        {
            var json = JsonSerializer.Serialize(parameters);
            return new StringContent(json, Encoding.UTF8, "application/json");
        }

        var form = new MultipartFormDataContent();
        foreach (var (key, value) in parameters)
        {
            switch (value)
            {
                case null: break;
                case TelegramFile file:
                {
                    var bytes = File.ReadAllBytes(file.Path);
                    var part = new ByteArrayContent(bytes);
                    part.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
                    form.Add(part, key, file.FileName);
                    break;
                }
                default:
                    form.Add(new StringContent(value.ToString() ?? ""), key);
                    break;
            }
        }
        return form;
    }

    private static string? ParseDescription(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        foreach (var tunnel in _tunnels) tunnel.Dispose();
    }
}

/// <summary>A file argument for sendDocument / sendPhoto (multipart upload).</summary>
public sealed record TelegramFile(string Path, string FileName, string ContentType);

public sealed class TelegramApiException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Telegram answered 429; the bot should pause for RetryAfterSeconds.</summary>
public sealed class TelegramRateLimitedException(string message, int retryAfterSeconds) : Exception(message)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed class TelegramUnavailableException(string message, Exception? inner) : Exception(message, inner);
