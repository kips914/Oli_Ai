using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace OLI.Telegram.Tunnel;

/// <summary>
/// Resolves host names through DNS-over-HTTPS (Cloudflare / Google / Yandex),
/// bypassing a poisoned or blocked system resolver. Results are cached for
/// 10 minutes and shared across all tunnels.
/// </summary>
public static class DohResolver
{
    private sealed record CacheEntry(IPAddress? Address, DateTimeOffset ExpiresAt);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = CreateClient();

    private static readonly string[] Endpoints =
    [
        "https://1.1.1.1/dns-query",
        "https://dns.google/resolve",
        "https://common.dot.dns.yandex.net/resolve",
    ];

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(6),
        })
        { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
        return client;
    }

    /// <summary>First IPv4 (falls back to IPv6) for the host, or null if DoH failed.</summary>
    public static async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;

        if (Cache.TryGetValue(host, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Address;

        foreach (var endpoint in Endpoints)
        {
            try
            {
                var json = await Http.GetStringAsync($"{endpoint}?name={Uri.EscapeDataString(host)}&type=A", ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Answer", out var answers)) continue;

                var fallback = (IPAddress?)null;
                foreach (var answer in answers.EnumerateArray())
                {
                    var type = answer.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
                    var data = answer.TryGetProperty("data", out var d) ? d.GetString() : null;
                    if (data is null || !IPAddress.TryParse(data, out var ip)) continue;

                    if (type == 1)
                    {
                        Cache[host] = new CacheEntry(ip, DateTimeOffset.UtcNow.AddMinutes(10));
                        OliLog.Info("TELEGRAM", $"DoH: {host} → {ip} ({new Uri(endpoint).Host})");
                        return ip;
                    }
                    fallback ??= ip;
                }

                if (fallback is not null)
                {
                    Cache[host] = new CacheEntry(fallback, DateTimeOffset.UtcNow.AddMinutes(10));
                    return fallback;
                }
            }
            catch (Exception ex)
            {
                OliLog.Debug("TELEGRAM", $"DoH {endpoint}: {ex.Message}");
            }
        }

        Cache[host] = new CacheEntry(null, DateTimeOffset.UtcNow.AddMinutes(1));
        return null;
    }

    /// <summary>Drops the cached address (call after a connection failure).</summary>
    public static void Invalidate(string host) => Cache.TryRemove(host, out _);
}

/// <summary>
/// Connects to the host at a DoH-resolved IP while keeping the original host name
/// for SNI/Host, so TLS certificates still validate. That defeats DNS poisoning
/// and "no such host" failures — the two most common non-DPI Telegram blocks.
/// </summary>
public sealed class DohPinnedTunnel : HttpTunnelBase
{
    private readonly string _host;

    public DohPinnedTunnel(HttpClient? http = null, string host = "api.telegram.org")
        : base(http ?? CreateClient(host)) => _host = host;

    public override string Name => "doh-direct";

    protected override string UrlTemplate => $"https://{_host}/bot{{token}}/{{method}}";

    /// <summary>Resolves the host once, for the "Проверить" button and diagnostics.</summary>
    public Task<IPAddress?> ResolveAsync(CancellationToken ct = default) => DohResolver.ResolveAsync(_host, ct);

    private static HttpClient CreateClient(string host) => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(12),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectCallback = async (context, ct) =>
        {
            var endpoint = context.DnsEndPoint;
            var ip = await DohResolver.ResolveAsync(endpoint.Host, ct);

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                var target = ip is null
                    ? new DnsEndPoint(endpoint.Host, endpoint.Port)
                    : new IPEndPoint(ip, endpoint.Port);
                await socket.ConnectAsync(target, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                DohResolver.Invalidate(endpoint.Host);
                throw;
            }
        },
    })
    { Timeout = TimeSpan.FromSeconds(80) };
}
