using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OLI.Core;

/// <summary>One IPv4 address of this PC, with the info needed to explain it to the user.</summary>
public sealed record LanAddress(string Address, string Interface, NetworkInterfaceType Type, bool IsVirtual)
{
    public bool IsWifiOrEthernet =>
        Type is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
        && !IsVirtual;

    public string Display => $"{Address} ({Interface}{(IsVirtual ? ", виртуальный" : "")})";
}

/// <summary>LAN IPv4 addresses of this PC — what the phone must type as the server URL.</summary>
public static class NetworkAddresses
{
    /// <summary>Every usable IPv4 address, best candidate first (real Wi-Fi/Ethernet, then VPN).</summary>
    public static List<LanAddress> LocalDetailed()
    {
        var result = new List<LanAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var name = NicName(nic);
                var isVirtual = IsVirtualNic(nic, name);

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = info.Address;
                    if (IPAddress.IsLoopback(ip)) continue;
                    var bytes = ip.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue; // APIPA: no DHCP, phone cannot reach it
                    if (result.Any(a => a.Address == ip.ToString())) continue;
                    result.Add(new LanAddress(ip.ToString(), name, nic.NetworkInterfaceType, isVirtual));
                }
            }
        }
        catch { }

        return result
            .OrderByDescending(a => a.IsWifiOrEthernet)
            .ThenByDescending(a => a.Address.StartsWith("192.168."))
            .ThenByDescending(a => a.Address.StartsWith("10."))
            .ThenBy(a => a.Address, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Plain address list, best candidate first.</summary>
    public static List<string> Local() => LocalDetailed().Select(a => a.Address).ToList();

    /// <summary>Best guess to show the user (real Wi-Fi/Ethernet before virtual adapters).</summary>
    public static string? Preferred() => LocalDetailed().Select(a => a.Address).FirstOrDefault();

    /// <summary>All addresses together as one diagnostic line.</summary>
    public static string DescribeAll() =>
        LocalDetailed().Count == 0 ? "не найден (нет сети)" : string.Join(", ", LocalDetailed().Select(a => a.Display));

    /// <summary>true when the adapter is a VPN / hypervisor / tunnel, which phones usually cannot use.</summary>
    private static bool IsVirtualNic(NetworkInterface nic, string name)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return true;
        var keys = new[]
        {
            "vethernet", "vmware", "virtualbox", "hyper-v", "loopback", "tunnel", "tap", "tailscale",
            "zerotier", "wireguard", "openvpn", "nordlynx", "radmin", "hamachi", "docker", "wsl",
            "bluetooth", "npcap", "vpn",
        };
        return keys.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string NicName(NetworkInterface nic)
    {
        try
        {
            var name = nic.Name?.Trim();
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { }
        return nic.Description ?? nic.Id ?? "?";
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The /24 prefix (e.g. "192.168.1.") of an address — used for LAN scanning.</summary>
    public static string? SubnetPrefix(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
        var bytes = ip.GetAddressBytes();
        if (bytes[0] == 169 && bytes[1] == 254) return null;
        return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.";
    }

    /// <summary>
    /// Best local address to reach the internet, asked from the OS routing table.
    /// Never sends a packet: a UDP socket gets "connected" to a public address.
    /// </summary>
    public static string? LocalAddressForInternet()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53));
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Finds the OLI server of the same owner on the local network. Used by the phone app
/// (as a mirror of <see cref="LanScanner"/> in Android) and by the desktop self-test.
/// </summary>
public static class LanScanner
{
    /// <summary>
    /// Probes every host of the /24 around <paramref name="localAddress"/> for
    /// <c>GET /api/health</c> and returns the first base URL that answers like an OLI server.
    /// </summary>
    public static async Task<string?> FindServerAsync(
        string localAddress,
        int port,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var prefix = NetworkAddresses.SubnetPrefix(localAddress);
        if (prefix is null) return null;
        var perHost = timeout ?? TimeSpan.FromMilliseconds(350);

        using var http = new HttpClient { Timeout = perHost };
        var targets = Enumerable.Range(1, 254).Where(i => !Equals(localAddress, prefix + i)).ToList();

        using var gate = new SemaphoreSlim(64);
        var found = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = targets.Select(async host =>
        {
            if (found.Task.IsCompleted) return;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (found.Task.IsCompleted) return;
                var url = $"http://{prefix}{host}:{port}";
                if (await IsOliServerAsync(http, url, ct).ConfigureAwait(false))
                    found.TrySetResult(url);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        _ = Task.WhenAll(tasks);
        var finished = await Task.WhenAny(found.Task, Task.Delay(perHost * 12, ct)).ConfigureAwait(false);
        return finished == found.Task ? found.Task.Result : (found.Task.IsCompletedSuccessfully ? found.Task.Result : null);
    }

    /// <summary>true when the URL answers /api/health with status ok.</summary>
    public static async Task<bool> IsOliServerAsync(HttpClient http, string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync(baseUrl.TrimEnd('/') + "/api/health", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Contains("\"status\"", StringComparison.Ordinal) &&
                   text.Contains("ok", StringComparison.OrdinalIgnoreCase) &&
                   text.Contains("oli", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
