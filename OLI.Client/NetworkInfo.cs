using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OLI.Client;

/// <summary>Finds the LAN IPv4 addresses a phone can use to reach this PC.</summary>
public static class NetworkInfo
{
    public static List<string> LocalAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = address.Address;
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

    /// <summary>Best guess for the address to show the user (prefers 192.168.x).</summary>
    public static string? PreferredAddress() =>
        LocalAddresses().OrderByDescending(a => a.StartsWith("192.168.")).ThenBy(a => a).FirstOrDefault();
}
