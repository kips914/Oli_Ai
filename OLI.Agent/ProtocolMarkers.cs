using System.Text.RegularExpressions;
using OLI.Core;

namespace OLI.Agent;

/// <summary>
/// Parsers for the text markers OLI uses to request side effects:
/// <c>OLI_SEARCH:</c>, <c>OLI_OPEN:</c> and <c>OLI_REMEMBER:</c>.
/// Kept in one place so the GUI and the Telegram bot agree exactly.
/// </summary>
public static partial class ProtocolMarkers
{
    public const int MaxOpenPages = Persona.MaxOpenPages;

    [GeneratedRegex(@"^\s*OLI_SEARCH:\s*(?<q>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SearchRx();

    [GeneratedRegex(@"^\s*OLI_OPEN:\s*(?<url>\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex OpenLineRx();

    [GeneratedRegex(@"(?:^|\n)\s*OLI_OPEN:\s*(?<url>https?://\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex AnyOpenRx();

    [GeneratedRegex(@"^[ \t]*OLI_REMEMBER:[ \t]*(?<fact>.+?)[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RememberRx();

    /// <summary>Only when the whole reply is one "OLI_SEARCH: ..." line.</summary>
    public static string? ExtractSearchQuery(string trimmedAnswer)
    {
        var m = SearchRx().Match(trimmedAnswer);
        if (!m.Success) return null;
        return m.Groups["q"].Value.Trim().Trim('"', '«', '»').Trim();
    }

    /// <summary>Only when every non-empty line is an "OLI_OPEN: &lt;url&gt;" line.</summary>
    public static List<string> ExtractOpenUrls(string trimmedAnswer)
    {
        var lines = trimmedAnswer.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) return [];
        if (!lines.All(l => OpenLineRx().IsMatch(l))) return [];

        return lines
            .Select(l => OpenLineRx().Match(l).Groups["url"].Value.Trim())
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
            .Distinct()
            .Take(MaxOpenPages)
            .ToList();
    }

    /// <summary>Any OLI_OPEN url mentioned anywhere (used to avoid leaking markers).</summary>
    public static List<string> FindOpenUrlsAnywhere(string text) =>
        AnyOpenRx().Matches(text).Select(m => m.Groups["url"].Value).Distinct().ToList();

    public static (string Clean, List<string> Facts) ExtractMemoryFacts(string answer)
    {
        var facts = new List<string>();
        var clean = RememberRx().Replace(answer, m =>
        {
            var fact = m.Groups["fact"].Value.Trim();
            if (fact.Length > 0) facts.Add(fact);
            return "";
        });
        clean = Regex.Replace(clean, @"\n{3,}", "\n\n").Trim();
        return (clean, facts);
    }
}
