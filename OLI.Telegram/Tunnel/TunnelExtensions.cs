using System.Net.Http.Headers;

namespace OLI.Telegram.Tunnel;

/// <summary>
/// Base class for tunnels that are just "POST JSON to a URL template". The token is
/// injected at send time (<c>{token}</c>), so tunnels never store it.
/// </summary>
public abstract class HttpTunnelBase : IHttpTunnel
{
    protected HttpClient Http { get; }

    protected HttpTunnelBase(HttpClient http) => Http = http;

    public abstract string Name { get; }

    /// <summary>Either &lt;apiRoot&gt;/bot{token}/{method} or .../bot{token}/... .</summary>
    protected abstract string UrlTemplate { get; }

    /// <summary>Extra headers (e.g. a relay shared secret).</summary>
    protected virtual void Decorate(HttpRequestMessage request) { }

    public Task<HttpResponseMessage> SendAsync(string method, HttpContent? content, CancellationToken ct) =>
        SendAsync(method, content, "{token}", ct);

    public async Task<HttpResponseMessage> SendAsync(string method, HttpContent? content, string token, CancellationToken ct)
    {
        var url = UrlTemplate.Replace("{token}", token).Replace("{method}", method);
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        Decorate(request);
        return await Http.SendAsync(request, ct);
    }

    /// <summary>Calls the method with a JSON body (used by tests/probes).</summary>
    protected static StringContent Json(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");

    public void Dispose() => Http.Dispose();
}

/// <summary>
/// Plain HTTPS to api.telegram.org. Works where Telegram is not blocked; where it
/// is (RF DPI filtering) it fails fast and the next tunnel takes over.
/// </summary>
public sealed class DirectTunnel : HttpTunnelBase
{
    public const string ApiRoot = "https://api.telegram.org";
    private readonly string _apiRoot;

    public DirectTunnel(HttpClient? http = null, string apiRoot = ApiRoot) : base(http ?? Create())
        => _apiRoot = apiRoot.TrimEnd('/');

    public override string Name => "direct";

    protected override string UrlTemplate => $"{_apiRoot}/bot{{token}}/{{method}}";

    public static HttpClient Create() => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(12),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(80) };
}

/// <summary>
/// Reverse-proxy / relay tunnel: a Cloudflare Worker (or your own VPS) that speaks
/// the same Bot API paths — <c>https://relay.example.dev/bot{token}/{method}</c>.
/// Cloudflare's edge is normally reachable from Russia even when Telegram is not.
/// </summary>
public sealed class RelayTunnel : HttpTunnelBase
{
    private readonly string _baseUrl;
    private readonly string? _secret;

    public RelayTunnel(string baseUrl, HttpClient? http = null, string? secret = null) : base(http ?? DirectTunnel.Create())
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _secret = secret;
    }

    public override string Name => "relay";

    protected override string UrlTemplate => $"{_baseUrl}/bot{{token}}/{{method}}";

    protected override void Decorate(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_secret))
            request.Headers.Add("X-OLI-Relay-Secret", _secret);
    }
}

/// <summary>HTTP/SOCKS proxy tunnel (the user's own VPN/proxy client).</summary>
public sealed class ProxyTunnel : HttpTunnelBase
{
    private readonly string _proxyUrl;

    public ProxyTunnel(string proxyUrl) : base(new HttpClient(new HttpClientHandler
    {
        Proxy = new System.Net.WebProxy(proxyUrl),
        UseProxy = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(90) })
        => _proxyUrl = proxyUrl;

    public override string Name => $"proxy({_proxyUrl})";

    protected override string UrlTemplate => "https://api.telegram.org/bot{token}/{method}";
}

/// <summary>
/// DNS-blocking workaround: resolves api.telegram.org through DNS-over-HTTPS first,
/// then lets the normal TLS stack connect (DoH also settles the host for the
/// connection). This defeats DNS poisoning and "no such host" without a proxy.
/// </summary>
public sealed class DohPinnedTunnel : HttpTunnelBase
{
    private readonly string _host;
    private readonly string[] _dohEndpoints;
    private string? _pinnedIp;
    private DateTimeOffset _pinnedAt = DateTimeOffset.MinValue;

    public DohPinnedTunnel(string host = "api.telegram.org", string[]? dohEndpoints = null, HttpClient? http = null)
        : base(http ?? DirectTunnel.Create())
    {
        _host = host;
        _dohEndpoints = dohEndpoints ??
        [
            "https://1.1.1.1/dns-query",
            "https://dns.google/resolve",
            "https://common.dot.dns.yandex.net/resolve",
        ];
    }

    public override string Name => "doh-direct";

    protected override string UrlTemplate => $"https://{_host}/bot{{token}}/{{method}}";

    public string? PinnedIp => _pinnedIp;

    /// <summary>Resolves the host via DoH (cached 10 min) and returns the first IPv4.</summary>
    public async Task<string?> ResolveAsync(CancellationToken ct = default)
    {
        if (_pinnedIp is not null && DateTimeOffset.UtcNow - _pinnedAt < TimeSpan.FromMinutes(10))
            return _pinnedIp;

        foreach (var endpoint in _dohEndpoints)
        {
            try
            {
                using var client = new HttpClient(new HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                })
                { Timeout = TimeSpan.FromSeconds(8) };
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));

                var json = await client.GetStringAsync($"{endpoint}?name={_host}&type=A", ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Answer", out var answers)) continue;

                foreach (var answer in answers.EnumerateArray())
                {
                    var type = answer.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
                    var data = answer.TryGetProperty("data", out var d) ? d.GetString() : null;
                    if (type == 1 && data is not null && System.Net.IPAddress.TryParse(data, out var ip) &&
                        ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        _pinnedIp = data;
                        _pinnedAt = DateTimeOffset.UtcNow;
                        OliLog.Info("TELEGRAM", $"DoH: {_host} → {data}");
                        return data;
                    }
                }
            }
            catch (Exception ex)
            {
                OliLog.Debug("TELEGRAM", $"DoH {endpoint}: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>Force re-resolution on the next call (e.g. after a failure).</summary>
    public void ResetPin()
    {
        _pinnedIp = null;
        _pinnedAt = DateTimeOffset.MinValue;
    }
}
