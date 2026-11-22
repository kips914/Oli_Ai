using OLI.Core;
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
