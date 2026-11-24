using System.Text.RegularExpressions;
using OLI.Core;

namespace OLI.Web;

/// <summary>One search back-end. Returns raw hits; the caller filters and dedupes.</summary>
public interface ISearchProvider
{
    string Name { get; }

    Task<List<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct);
}

public sealed partial class SearchHttp
{
    public static HttpClient Create(int timeoutSeconds)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer(),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120)) };
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/131.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
        return http;
    }
}

/// <summary>DuckDuckGo HTML endpoint — no API key, works through most ISPs.</summary>
public sealed partial class DuckDuckGoProvider(HttpClient http) : ISearchProvider
{
    public string Name => "DuckDuckGo";

    [GeneratedRegex(@"<a[^>]*class=""[^""]*result__a[^""]*""[^>]*href=""(?<href>[^""]+)""[^>]*>(?<title>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkRx();

    [GeneratedRegex(@"<(?:a|div)[^>]*class=""[^""]*result__snippet[^""]*""[^>]*>(?<s>.*?)</(?:a|div)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SnippetRx();

    public async Task<List<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var url = "https://html.duckduckgo.com/html/?kl=ru-ru&q=" + Uri.EscapeDataString(query);
        var html = await http.GetStringAsync(url, ct);
        var results = new List<SearchResult>();

        var links = LinkRx().Matches(html);
        var snippets = SnippetRx().Matches(html);
        for (var i = 0; i < links.Count && results.Count < limit; i++)
        {
            var rawHref = links[i].Groups["href"].Value;
            var title = HtmlTools.ToPlainSnippet(links[i].Groups["title"].Value, 200);
            var link = UrlResolver.Resolve(rawHref);
            if (title.Length == 0 || !UrlResolver.IsHttp(link)) continue;

            var snippet = i < snippets.Count ? HtmlTools.ToPlainSnippet(snippets[i].Groups["s"].Value, 350) : "";
            results.Add(new SearchResult(title, link, snippet));
        }
        return results;
    }
}

/// <summary>Bing HTML endpoint — different network path, so it works when DDG is filtered.</summary>
public sealed partial class BingProvider(HttpClient http) : ISearchProvider
{
    public string Name => "Bing";

    [GeneratedRegex(@"<li class=""b_algo"".*?<h2>\s*<a[^>]*href=""(?<href>[^""]+)""[^>]*>(?<title>.*?)</a>.*?</h2>(?<rest>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ResultRx();

    [GeneratedRegex(@"<p[^>]*>(?<s>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ParagraphRx();

    public async Task<List<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var url = "https://www.bing.com/search?setlang=ru&count=" + Math.Max(limit, 10) + "&q=" + Uri.EscapeDataString(query);
        var html = await http.GetStringAsync(url, ct);
        var results = new List<SearchResult>();

        foreach (Match m in ResultRx().Matches(html))
        {
            if (results.Count >= limit) break;
            var link = UrlResolver.Resolve(m.Groups["href"].Value);
            if (!UrlResolver.IsHttp(link) || link.Contains("bing.com/aclk", StringComparison.OrdinalIgnoreCase)) continue;

            var title = HtmlTools.ToPlainSnippet(m.Groups["title"].Value, 200);
            if (title.Length == 0) continue;

            var p = ParagraphRx().Match(m.Groups["rest"].Value);
            var snippet = p.Success ? HtmlTools.ToPlainSnippet(p.Groups["s"].Value, 350) : "";
            results.Add(new SearchResult(title, link, snippet));
        }
        return results;
    }
}

/// <summary>Mojeek — independent index, JSON API, no key required.</summary>
public sealed class MojeekProvider(HttpClient http) : ISearchProvider
{
    public string Name => "Mojeek";

    public async Task<List<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var url = "https://www.mojeek.com/search?fmt=json&t=" + Math.Max(limit, 10) + "&q=" + Uri.EscapeDataString(query);
        var json = await http.GetStringAsync(url, ct);
        var results = new List<SearchResult>();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response)) return results;
        if (!response.TryGetProperty("results", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array) return results;

        foreach (var item in items.EnumerateArray())
        {
            if (results.Count >= limit) break;
            var link = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var desc = item.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
            if (link.Length == 0 || title.Length == 0) continue;
            results.Add(new SearchResult(HtmlTools.Decode(title), link, HtmlTools.ToPlainSnippet(desc, 350)));
        }
        return results;
    }
}

/// <summary>Any SearXNG instance with JSON output (user-configurable, e.g. a self-hosted one).</summary>
public sealed class SearxProvider(HttpClient http, string baseUrl) : ISearchProvider
{
    public string Name => "SearxNG";

    public async Task<List<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/search?format=json&language=ru&q=" + Uri.EscapeDataString(query);
        var json = await http.GetStringAsync(url, ct);
        var results = new List<SearchResult>();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var items)) return results;

        foreach (var item in items.EnumerateArray())
        {
            if (results.Count >= limit) break;
            var link = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var content = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            if (link.Length == 0 || title.Length == 0) continue;
            results.Add(new SearchResult(HtmlTools.Decode(title), link, HtmlTools.ToPlainSnippet(content, 350)));
        }
        return results;
    }
}

/// <summary>Wikipedia (ru + en) via the public API — always available, great for facts.</summary>
public sealed class WikipediaProvider(HttpClient http) : ISearchProvider
{
    public string Name => "Wikipedia";

    public async Task<List<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        foreach (var lang in new[] { "ru", "en" })
        {
            if (results.Count >= limit) break;
            var url = $"https://{lang}.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit={limit}" +
                      "&srsearch=" + Uri.EscapeDataString(query);
            try
            {
                var json = await http.GetStringAsync(url, ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("query", out var q) ||
                    !q.TryGetProperty("search", out var items)) continue;

                foreach (var item in items.EnumerateArray())
                {
                    if (results.Count >= limit) break;
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";
                    if (title.Length == 0) continue;
                    var pageUrl = $"https://{lang}.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_'));
                    results.Add(new SearchResult(title, pageUrl, HtmlTools.ToPlainSnippet(snippet, 350)));
                }
            }
            catch (HttpRequestException)
            {
                // Try the next language.
            }
        }
        return results;
    }
}

public static partial class UrlResolver
{
    [GeneratedRegex(@"uddg=(?<u>[^&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DdgRx();

    /// <summary>Unwraps provider redirect links (DDG "uddg", Bing/Google "/url?q=") to the real target.</summary>
    public static string Resolve(string rawHref)
    {
        var href = HtmlTools.Decode(rawHref).Trim();
        if (href.Length == 0) return "";

        var ddg = DdgRx().Match(href);
        if (ddg.Success)
        {
            try { return Uri.UnescapeDataString(ddg.Groups["u"].Value); } catch (UriFormatException) { }
        }

        if (href.StartsWith("/url?", StringComparison.OrdinalIgnoreCase) ||
            href.Contains("bing.com/ck/a", StringComparison.OrdinalIgnoreCase))
        {
            var q = QueryValue(href, "q") ?? QueryValue(href, "u");
            if (q is not null)
            {
                var decoded = q.StartsWith("a1", StringComparison.Ordinal) ? q[2..] : q;
                try
                {
                    var normalized = decoded.Replace('-', '+').Replace('_', '/');
                    var padded = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
                    var bytes = Convert.FromBase64String(padded);
                    var text = System.Text.Encoding.UTF8.GetString(bytes);
                    if (text.Contains("://")) return text;
                }
                catch { }
                try { return Uri.UnescapeDataString(q); } catch (UriFormatException) { }
            }
        }

        if (href.StartsWith("//", StringComparison.Ordinal)) return "https:" + href;
        return href;
    }

    private static string? QueryValue(string href, string key)
    {
        var index = href.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var start = index + key.Length + 1;
        var end = href.IndexOf('&', start);
        var value = end < 0 ? href[start..] : href[start..end];
        return value.Length == 0 ? null : value;
    }

    public static bool IsHttp(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
