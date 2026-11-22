namespace OLI.Client;

/// <summary>Finds the LAN IPv4 addresses a phone can use to reach this PC.</summary>
public static class NetworkInfo
{
    public static List<string> LocalAddresses() => OLI.Core.NetworkAddresses.Local();

    public static string? PreferredAddress() => OLI.Core.NetworkAddresses.Preferred();
}
