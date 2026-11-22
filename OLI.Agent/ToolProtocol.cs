using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OLI.Agent;

/// <summary>
/// A single tool invocation requested by the model, e.g.
/// <c>OLI_TOOL: save_file {"path":"notes.txt","content":"hi"}</c>.
/// </summary>
public sealed record ToolCall(string Name, Dictionary<string, JsonElement> Arguments)
{
    public string? Str(string key)
    {
        if (!Arguments.TryGetValue(key, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null,
        };
    }

    public bool Bool(string key)
    {
        if (!Arguments.TryGetValue(key, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            _ => false,
        };
    }

    public int Int(string key, int fallback)
    {
        if (!Arguments.TryGetValue(key, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var number) ? number : fallback,
            JsonValueKind.String => int.TryParse(value.GetString(), out var number) ? number : fallback,
            _ => fallback,
        };
    }

    public IReadOnlyList<string> StrList(string key)
    {
        if (!Arguments.TryGetValue(key, out var v)) return [];
        if (v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString())
            .Where(x => x.Length > 0)
            .ToList();
    }
}

/// <summary>
/// The text-only tool protocol OLI speaks. It is deliberately plain (no JSON-RPC,
/// no native tool-calling API) because small local GGUFs follow a literal marker
/// far more reliably than a schema, and it works identically in the WPF client
/// and the Telegram bot.
/// </summary>
public static partial class ToolProtocol
{
    public const string Prefix = "OLI_TOOL:";

    [GeneratedRegex(@"^[ \t]*OLI_TOOL:[ \t]*(?<name>[a-z_][a-z0-9_]*)[ \t]*(?<json>\{.*\})?[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ToolLineRegex();

    [GeneratedRegex(@"```[a-zA-Z0-9_+-]*\s*OLI_TOOL:[ \t]*(?<name>[a-z_][a-z0-9_]*)[ \t]*(?<json>\{.*?\})\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ToolCodeBlockRegex();

    /// <summary>
    /// Extracts every tool call from a model reply, in order. Tolerates pretty-
    /// printed JSON: the JSON object may span several lines after the marker.
    /// </summary>
    public static List<ToolCall> Parse(string reply)
    {
        var calls = new List<ToolCall>();
        if (string.IsNullOrWhiteSpace(reply)) return calls;

        var lines = reply.Replace("\r\n", "\n").Split('\n');

        // Pass 1: markdown-fenced ```OLI_TOOL: ... ``` blocks (whole reply).
        foreach (Match m in ToolCodeBlockRegex().Matches(reply))
            TryBuild(m.Groups["name"].Value, m.Groups["json"].Value, calls);
        if (calls.Count > 0) return calls;

        // Pass 2: line-oriented scan with multi-line JSON accumulation.
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = line[Prefix.Length..].Trim();
            var space = rest.IndexOf(' ');
            var name = space < 0 ? rest : rest[..space];
            if (name.Length == 0) continue;
            var jsonPart = space < 0 ? "" : rest[(space + 1)..].Trim();

            if (jsonPart.Length == 0)
            {
                calls.Add(new ToolCall(name.ToLowerInvariant(), new(StringComparer.OrdinalIgnoreCase)));
                continue;
            }

            // Accumulate following lines until the braces balance.
            var buffer = jsonPart;
            var depth = CountBraces(buffer);
            var guard = 0;
            while (depth > 0 && i + 1 < lines.Length && guard++ < 200)
            {
                buffer += "\n" + lines[++i];
                depth += CountBraces(lines[i]);
            }

            TryBuild(name, buffer, calls);
        }

        return calls;
    }

    private static int CountBraces(string value)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        foreach (var c in value)
        {
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
        }
        return depth;
    }

    private static bool TryBuild(string name, string? json, List<ToolCall> sink)
    {
        name = name.Trim().ToLowerInvariant();
        var args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                        args[p.Name] = p.Value.Clone();
            }
            catch (JsonException)
            {
                // Malformed JSON: the dispatcher will report a helpful error.
                args["_parse_error"] = JsonDocument.Parse("\"invalid json\"").RootElement.Clone();
            }
        }
        sink.Add(new ToolCall(name, args));
        return true;
    }

    /// <summary>Replaces tool lines with human-readable activity notes for the chat UI.</summary>
    public static string StripToolLines(string reply, Func<ToolCall, string>? describe = null)
    {
        var withoutBlocks = ToolCodeBlockRegex().Replace(reply, m => Describe(m.Groups["name"].Value, m.Groups["json"].Value, describe));
        var withoutLines = ToolLineRegex().Replace(withoutBlocks, m => Describe(m.Groups["name"].Value, m.Groups["json"].Value, describe));
        return Regex.Replace(withoutLines, @"\n{3,}", "\n\n").Trim();
    }

    private static string Describe(string name, string? json, Func<ToolCall, string>? describe)
    {
        if (describe is null) return "";
        var list = new List<ToolCall>();
        TryBuild(name, json, list);
        return list.Count == 0 ? "" : "\n" + describe(list[0]) + "\n";
    }

    public static bool LooksLikeOnlyToolCalls(string reply)
    {
        var trimmed = reply.Trim();
        if (trimmed.Length == 0) return false;
        var cleaned = ToolCodeBlockRegex().Replace(trimmed, "");
        cleaned = ToolLineRegex().Replace(cleaned, "");
        return cleaned.Trim().Length == 0;
    }
}
