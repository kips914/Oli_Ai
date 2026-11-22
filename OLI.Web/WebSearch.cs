using System.Net;
using System.Text;
using OLI.Core;

namespace OLI.Web;

/// <summary>
/// Thrown when a web search or page fetch could not be completed (network error,
/// blocked request, disallowed host, unexpected response, etc). Callers should
/// catch this and degrade gracefully instead of failing the whole chat turn.
/// </summary>
public sealed class WebSearchException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Full web access for OLI: multi-provider search (DuckDuckGo / Bing / Mojeek /
/// SearXNG / Wikipedia) plus real page reading with SSRF protection.
/// Two modes: "open web" (everything except <see cref="BlockedDomains"/>) and
/// "allow-list only" (legacy behaviour, restricted to <see cref="AllowedDomains"/>).
/// </summary>
public sealed class WebSearch : IDisposable
{
    private readonly HttpClient _http;
    private readonly List<ISearchProvider> _providers;

    private static readonly SemaphoreSlim ProviderGate = new(2, 2);

    public WebSearch(int timeoutSeconds = 20, OliSearchProvider preferred = OliSearchProvider.Auto, string? searxBaseUrl = null)
    {
        _http = SearchHttp.Create(timeoutSeconds);
        _providers = BuildProviders(preferred, searxBaseUrl);
    }

    private List<ISearchProvider> BuildProviders(OliSearchProvider preferred, string? searxBaseUrl)
    {
        var all = new Dictionary<OliSearchProvider, Func<ISearchProvider>>
        {
            [OliSearchProvider.DuckDuckGo] = () => new DuckDuckGoProvider(_http),
            [OliSearchProvider.Bing] = () => new BingProvider(_http),
            [OliSearchProvider.Mojeek] = () => new MojeekProvider(_http),
            [OliSearchProvider.Wikipedia] = () => new WikipediaProvider(_http),
        };
        if (!string.IsNullOrWhiteSpace(searxBaseUrl))
            all[OliSearchProvider.Searx] = () => new SearxProvider(_http, searxBaseUrl!);

        if (preferred != OliSearchProvider.Auto && all.TryGetValue(preferred, out var factory))
            return [factory(), .. all.Where(kv => kv.Key != preferred).Select(kv => kv.Value())];

        return
        [
            new DuckDuckGoProvider(_http),
            new BingProvider(_http),
            new MojeekProvider(_http),
            new WikipediaProvider(_http),
        ];
    }

    // ---------------------------------------------------------------- config

    /// <summary>When true, every site except <see cref="BlockedDomains"/> may be used.</summary>
    public bool OpenWeb { get; set; } = true;
    public List<string> BlockedDomains { get; set; } = [];
    public List<string> AllowedDomains { get; set; } = [];
    public int MaxPageChars { get; set; } = 12_000;

    // ---------------------------------------------------------------- search

    /// <summary>
    /// Runs the query across providers, merging and de-duplicating hits until
    /// <paramref name="limit"/> good, policy-allowed results are collected.
    /// Providers that fail (blocked, timeout, captcha) are skipped silently so one
    /// filtered provider can't take the whole search down.
    /// </summary>
    public async Task<List<SearchResult>> SearchAsync(
        string query,
        int limit = 5,
        bool? siteRestricted = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        limit = Math.Clamp(limit, 1, 20);

        var restrict = siteRestricted ?? !OpenWeb;
        var effectiveQuery = query;
        if (restrict && AllowedDomains.Count > 0)
            effectiveQuery = $"{query} ({string.Join(" OR ", AllowedDomains.Select(d => $"site:{d}"))})";

        var merged = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        await ProviderGate.WaitAsync(ct);
        try
        {
            foreach (var provider in _providers)
            {
                if (merged.Count >= limit) break;
                try
                {
                    var hits = await provider.SearchAsync(effectiveQuery, limit * 2, ct);
                    foreach (var hit in hits)
                    {
                        if (merged.Count >= limit) break;
                        var key = NormalizeUrl(hit.Url);
                        if (key.Length == 0 || !seen.Add(key)) continue;
                        if (!IsAllowed(hit.Url, restrict)) continue;
                        merged.Add(hit);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    errors.Add($"{provider.Name}: {Short(ex)}");
                    OliLog.Warning("WEB", $"Провайдер {provider.Name} не ответил: {Short(ex)}");
                }
            }
        }
        finally { ProviderGate.Release(); }

        if (merged.Count == 0 && errors.Count > 0)
            throw new WebSearchException("Все поисковики недоступны: " + string.Join("; ", errors.Take(3)));

        return merged;
    }

    // ---------------------------------------------------------------- pages

    /// <summary>Fetches a page and returns readable text plus title/description metadata.</summary>
    public async Task<PageContent> ReadPageAsync(string url, int? maxChars = null, CancellationToken ct = default)
    {
        if (!TryValidateUrl(url, out var uri, out var reason))
            throw new WebSearchException(reason ?? "Некорректная ссылка.");

        var restrict = !OpenWeb;
        if (!IsAllowed(uri.ToString(), restrict))
            throw new WebSearchException(restrict
                ? "Эта страница не входит в список разрешённых сайтов."
                : "Этот сайт запрещён настройками (чёрный список).");

        string body;
        string? contentType;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Referer", uri.GetLeftPart(UriPartial.Authority) + "/");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                throw new WebSearchException($"Сервер ответил HTTP {(int)response.StatusCode}.");

            contentType = response.Content.Headers.ContentType?.ToString() ?? "";
            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength > 8 * 1024 * 1024)
                throw new WebSearchException("Страница слишком большая для чтения.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var limited = new MemoryStream();
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > 8 * 1024 * 1024) break;
                await limited.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            var bytes = limited.ToArray();
            body = DecodeBody(bytes, contentType);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WebSearchException("Загрузка страницы заняла слишком много времени.");
        }
        catch (HttpRequestException ex)
        {
            throw new WebSearchException($"Не удалось открыть страницу: {Short(ex)}", ex);
        }

        var isHtml = contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) != false;
        var title = isHtml ? PageMeta.Title(body) : Path.GetFileName(uri.AbsolutePath);
        var description = isHtml ? PageMeta.Description(body) : "";
        var text = isHtml ? HtmlTools.ToReadableText(body, maxChars ?? MaxPageChars) : Truncate(body, maxChars ?? MaxPageChars);

        return new PageContent(url, uri.Host, title, description, text);
    }

    public async Task<string> FetchPageTextAsync(string url, int maxChars = 4000, CancellationToken ct = default) =>
        (await ReadPageAsync(url, maxChars, ct)).Text;

    private static string DecodeBody(byte[] bytes, string? contentType)
    {
        var charset = "utf-8";
        if (contentType is not null)
        {
            var idx = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var value = contentType[(idx + 8)..].Split(';', ' ')[0].Trim('"', ' ');
                if (value.Length is > 0 and < 40) charset = value;
            }
        }
        try { return Encoding.GetEncoding(charset).GetString(bytes); }
        catch (ArgumentException) { return Encoding.UTF8.GetString(bytes); }
    }

    // ---------------------------------------------------------------- policy

    /// <summary>Absolute http(s) URL, not pointing at a private/loopback/internal address.</summary>
    public static bool TryValidateUrl(string url, out Uri uri, out string? reason)
    {
        uri = default!;
        reason = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
        {
            reason = "Некорректная ссылка.";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = "Разрешены только http/https ссылки.";
            return false;
        }
        if (uri.IsLoopback || IsPrivateHost(uri.Host))
        {
            reason = "Локальные и внутренние адреса запрещены.";
            return false;
        }
        return true;
    }

    private static bool IsPrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                                                          host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return b[0] == 10
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254)
                   || b[0] == 127
                   || b[0] == 0;
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(IPAddress.IPv6Loopback);
    }

    /// <summary>Applies either the block list (open web) or the allow list (restricted mode).</summary>
    public bool IsAllowed(string url, bool? restricted = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();

        foreach (var blocked in BlockedDomains)
        {
            var b = NormalizeDomain(blocked);
            if (b.Length == 0) continue;
            if (host == b || host.EndsWith("." + b, StringComparison.Ordinal)) return false;
        }

        if (IsPrivateHost(host)) return false;

        var restrict = restricted ?? !OpenWeb;
        if (!restrict) return true;
        if (AllowedDomains.Count == 0) return false;

        foreach (var domain in AllowedDomains)
        {
            var d = NormalizeDomain(domain);
            if (d.Length == 0) continue;
            if (host == d || host.EndsWith("." + d, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string NormalizeDomain(string value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        if (text.StartsWith("www.")) text = text[4..];
        if (text.Contains("://") && Uri.TryCreate(text, UriKind.Absolute, out var u)) text = u.Host;
        return text.TrimEnd('/');
    }

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();
        var query = uri.Query.TrimStart('?');
        return query.Length > 0 ? normalized + "?" + query : normalized;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n…[обрезано]";

    private static string Short(Exception ex) => ex.Message.Length > 160 ? ex.Message[..160] : ex.Message;

    public void Dispose() => _http.Dispose();
}

/// <summary>Result of reading a page: everything the model and the UI need.</summary>
public sealed record PageContent(string Url, string Host, string Title, string Description, string Text)
{
    public string ToPromptBlock(int maxChars = 12_000)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {Title}");
        sb.AppendLine($"URL: {Url}");
        if (!string.IsNullOrWhiteSpace(Description)) sb.AppendLine($"Описание: {Description}");
        sb.AppendLine();
        sb.AppendLine(Text.Length > maxChars ? Text[..maxChars] + " …[обрезано]" : Text);
        return sb.ToString();
    }
}
