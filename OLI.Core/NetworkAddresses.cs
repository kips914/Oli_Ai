using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OLI.Core;

/// <summary>LAN IPv4 addresses of this PC — what the phone must type as the server URL.</summary>
public static class NetworkAddresses
{
    public static List<string> Local()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = info.Address;
                    if (IPAddress.IsLoopback(ip)) continue;
                    var bytes = ip.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue; // APIPA
                    var text = ip.ToString();
                    if (!result.Contains(text)) result.Add(text);
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>Best guess to show the user (prefers 192.168.x.x home networks).</summary>
    public static string? Preferred() =>
        Local().OrderByDescending(a => a.StartsWith("192.168.")).ThenBy(a => a, StringComparer.Ordinal).FirstOrDefault();
}
