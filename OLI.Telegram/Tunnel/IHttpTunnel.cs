namespace OLI.Telegram.Tunnel;

/// <summary>
/// One way of reaching the Telegram Bot API. <see cref="TelegramApi"/> tries tunnels
/// in order (relay, proxy, DoH-direct, direct) and sticks with the first that works.
/// </summary>
public interface IHttpTunnel : IDisposable
{
    string Name { get; }

    Task<HttpResponseMessage> SendAsync(string method, HttpContent? content, CancellationToken ct);

    /// <summary>Same call, with the bot token injected into the URL template.</summary>
    Task<HttpResponseMessage> SendAsync(string method, HttpContent? content, string token, CancellationToken ct);
}
