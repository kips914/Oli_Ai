namespace OLI.Core;

/// <summary>
/// A single site OLI is allowed to use as a source when the search mode is
/// "allow-list only". When "open web" mode is on this list is used as extra
/// preferred/pre-seeded sources in the UI, while <see cref="DefaultAllowedSites.Blocked"/>
/// decides what is never opened.
/// </summary>
public sealed class AllowedSite(string name, string domain, bool isEnabled = true)
{
    public string Name { get; set; } = name;
    public string Domain { get; set; } = domain;
    public bool IsEnabled { get; set; } = isEnabled;
}

public static class DefaultAllowedSites
{
    /// <summary>Frequently useful, trusted starting points (news, reference, retail, tech).</summary>
    public static List<AllowedSite> Create() =>
    [
        new AllowedSite("Википедия", "wikipedia.org"),
        new AllowedSite("Яндекс", "yandex.ru"),
        new AllowedSite("Habr", "habr.com"),
        new AllowedSite("GitHub", "github.com"),
        new AllowedSite("Stack Overflow", "stackoverflow.com"),
        new AllowedSite("Microsoft Learn", "learn.microsoft.com"),
        new AllowedSite("Ozon", "ozon.ru"),
        new AllowedSite("DNS", "dns-shop.ru"),
        new AllowedSite("Яндекс.Маркет", "market.yandex.ru"),
        new AllowedSite("Wildberries", "wildberries.ru"),
        new AllowedSite("М.Видео", "mvideo.ru"),
        new AllowedSite("РБК", "rbc.ru"),
    ];

    /// <summary>Never fetched and never cited, whatever the search provider returns.</summary>
    public static List<string> DefaultBlocked() =>
    [
        "localhost", "127.0.0.1", "0.0.0.1", "::1",
        "metadata.google.internal", "169.254.169.254",
        "doubleclick.net", "googlesyndication.com", "adservice.google.com",
        "2gzyxa5ihm7nsggfxnu52rck2vv4rvmdlkiu3zzui5du4xyclen53wid.onion"
    ];
}
