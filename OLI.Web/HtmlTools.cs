using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OLI.Web;

/// <summary>Small, dependency-free HTML helpers: entity decoding and readable-text extraction.</summary>
public static partial class HtmlTools
{
    [GeneratedRegex(@"<(script|style|noscript|svg|head|template)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyleRx();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRx();

    [GeneratedRegex(@"<(br|/p|/div|/li|/h[1-6]|/tr)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRx();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRx();

    [GeneratedRegex(@"[ \t\f\v]{2,}")]
    private static partial Regex MultiSpaceRx();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRx();

    /// <summary>Plain "what a human reads" text of an HTML document.</summary>
    public static string ToReadableText(string html, int maxChars = 12000)
    {
        if (string.IsNullOrEmpty(html)) return "";

        // Prefer the main content region when the page marks it up.
        var main = TrySliceMain(html) ?? html;

        var text = CommentRx().Replace(main, " ");
        text = ScriptOrStyleRx().Replace(text, " ");
        text = BlockBreakRx().Replace(text, "\n");
        text = TagRx().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace('\u00a0', ' ');
        text = MultiSpaceRx().Replace(text, " ");
        text = MultiSpaceRx().Replace(text.Replace("\r", ""), " ");
        text = MultiNewlineRx().Replace(text, "\n\n").Trim();

        if (text.Length > maxChars) text = text[..maxChars] + "\n…[текст страницы обрезан]";
        return text;
    }

    private static string? TrySliceMain(string html)
    {
        foreach (var (open, close) in new[] { ("<main", "</main>"), ("<article", "</article>") })
        {
            var start = html.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            var end = html.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) continue;
            var slice = html[start..end];
            if (slice.Length > 800) return slice;
        }
        return null;
    }

    public static string ToPlainSnippet(string rawHtml, int maxChars = 400)
    {
        var text = ToReadableText(rawHtml, maxChars * 2);
        return text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "…";
    }

    public static string Decode(string value) => WebUtility.HtmlDecode(value);
}

/// <summary>Minimal extraction of the &lt;title&gt; and a description meta tag.</summary>
public static partial class PageMeta
{
    [GeneratedRegex(@"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRx();

    [GeneratedRegex(@"<meta[^>]+name=[""']description[""'][^>]+content=[""'](?<c>[^""']*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionRx();

    [GeneratedRegex(@"<meta[^>]+property=[""']og:image[""'][^>]+content=[""'](?<c>[^""']*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex OgImageRx();

    public static string Title(string html)
    {
        var m = TitleRx().Match(html);
        return m.Success ? HtmlTools.Decode(m.Groups["t"].Value).Trim() : "";
    }

    public static string Description(string html)
    {
        var m = DescriptionRx().Match(html);
        return m.Success ? HtmlTools.Decode(m.Groups["c"].Value).Trim() : "";
    }

    public static string OgImage(string html)
    {
        var m = OgImageRx().Match(html);
        return m.Success ? HtmlTools.Decode(m.Groups["c"].Value).Trim() : "";
    }
}
