using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OLI.Core;

/// <summary>
/// One UDP packet every two seconds tells the whole Wi-Fi network: "OLI is here, on port N".
///
/// Why it exists: the phone used to scan 254 addresses on the hard-coded port 8765, which
/// broke as soon as the user typed 6767 in OLI — and it cannot scan at all when the phone
/// is on mobile data. A beacon carries the real port, the PC name and the version, so
/// discovery is instant and port-independent.
/// </summary>
public sealed class LanBeacon : IDisposable
{
    private readonly int _port;
    private readonly string _version;
    private readonly string _name;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public LanBeacon(int port, string version, string? name = null)
    {
        _port = port;
        _version = version;
        _name = name ?? Environment.MachineName;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var payload = ConnectionCore.Beacon.Encode(_port, _name, _version);
        using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 255.255.255.255 reaches every interface that allows broadcast; the
                // subnet-broadcast addresses are tried as a fallback for locked-down Wi-Fi.
                await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, ConnectionCore.BeaconPort));
                foreach (var subnetBroadcast in NetworkAddresses.BroadcastAddresses())
                {
                    try { await client.SendAsync(payload, payload.Length, new IPEndPoint(subnetBroadcast, ConnectionCore.BeaconPort)); }
                    catch (SocketException) { /* this interface does not allow it */ }
                }
            }
            catch (Exception ex)
            {
                OliLog.Debug("BEACON", $"broadcast: {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }
}

/// <summary>Client side of the same protocol (used by tests and by the desktop app).</summary>
public static class LanBeaconListener
{
    /// <summary>
    /// Collects beacons for <paramref name="window"/> and returns unique
    /// <c>http://address:port</c> URLs, best first (non-virtual interfaces first).
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListenAsync(TimeSpan window, CancellationToken ct = default)
    {
        var found = new List<(string Url, bool Virtual)>();
        try
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Any, ConnectionCore.BeaconPort))
            {
                EnableBroadcast = true,
            };
            var deadline = DateTime.UtcNow + window;
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(remaining);

                UdpReceiveResult result;
                try { result = await client.ReceiveAsync(timeout.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }

                var info = ConnectionCore.Beacon.Decode(result.Buffer);
                if (info is null) continue;
                var address = result.RemoteEndPoint.Address.ToString();
                var url = ConnectionCore.Normalize($"http://{address}:{info.Port}");
                if (url is null || found.Any(f => f.Url == url)) continue;

                var virtualNic = NetworkAddresses.IsVirtualAddress(address);
                found.Add((url, virtualNic));
            }
        }
        catch (SocketException ex)
        {
            OliLog.Debug("BEACON", $"listen: {ex.Message}");
        }

        return found.OrderBy(f => f.Virtual).Select(f => f.Url).ToList();
    }
}
