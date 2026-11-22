using System.IO.Compression;
using System.Text;

namespace OLI.Workspace;

/// <summary>
/// Dependency-free text extraction for the file types an assistant realistically
/// needs to read: plain text/code, DOCX, EPUB, ODT and (as a readable blob) PDF.
/// Everything is best-effort: callers get a short marker instead of an exception.
/// </summary>
public static class TextExtractors
{
    private const int DefaultMaxChars = 200_000;

    public static async Task<string> ExtractAsync(string path, int maxChars = DefaultMaxChars, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".docx" or ".epub" or ".odt" => ExtractZipXml(path, ext, maxChars),
                ".pdf" => "[PDF] Текстовый слой PDF не извлекается без внешней библиотеки. " +
                          "Файл доступен по пути, его можно открыть в системе или архивировать.\n" +
                          Describe(path),
                _ => await ReadTextFileAsync(path, maxChars, ct),
            };
        }
        catch (Exception ex)
        {
            return $"[не удалось прочитать {Path.GetFileName(path)}: {ex.Message}]";
        }
    }

    public static bool IsTextLike(string extension)
    {
        string[] text = [".txt", ".md", ".markdown", ".json", ".jsonl", ".csv", ".tsv", ".xml", ".yml", ".yaml",
            ".cs", ".js", ".ts", ".jsx", ".tsx", ".py", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".rs", ".go",
            ".php", ".rb", ".sh", ".ps1", ".bat", ".cmd", ".html", ".htm", ".css", ".scss", ".sql", ".ini", ".cfg",
            ".conf", ".toml", ".log", ".gitignore", ".env", ".srt", ".vtt", ".tex"];
        return text.Contains(extension.ToLowerInvariant());
    }

    private static async Task<string> ReadTextFileAsync(string path, int maxChars, CancellationToken ct)
    {
        var info = new FileInfo(path);

        // Binary sniff: NUL byte in the first 8 KB ⇒ treat as binary.
        await using (var probe = File.OpenRead(path))
        {
            var buffer = new byte[(int)Math.Min(8192, Math.Max(info.Length, 1))];
            var read = await probe.ReadAsync(buffer, ct);
            for (var i = 0; i < read; i++)
                if (buffer[i] == 0) return $"[бинарный файл, {info.Length} байт] {Describe(path)}";
        }

        var encoding = DetectUtf8Bom(path) ? Encoding.UTF8 : Encoding.UTF8;
        using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
        var sb = new StringBuilder();
        var chunk = new char[8192];
        while (sb.Length < maxChars)
        {
            var read = await reader.ReadAsync(chunk, ct);
            if (read == 0) break;
            sb.Append(chunk, 0, Math.Min(read, maxChars - sb.Length));
        }
        if (!reader.EndOfStream) sb.Append("\n…[файл обрезан]");
        return sb.ToString();
    }

    private static bool DetectUtf8Bom(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> bom = stackalloc byte[3];
        return fs.Read(bom) == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF;
    }

    private static string ExtractZipXml(string path, string ext, int maxChars)
    {
        using var archive = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        var partPrefix = ext switch
        {
            ".docx" => "word/document.xml",
            ".odt" => "content.xml",
            _ => null,
        };

        IEnumerable<ZipArchiveEntry> entries = partPrefix is null
            ? archive.Entries.Where(e => e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                                         e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(e => e.FullName, StringComparer.Ordinal)
            : archive.Entries.Where(e => e.FullName.Equals(partPrefix, StringComparison.OrdinalIgnoreCase));

        foreach (var entry in entries)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var xml = reader.ReadToEnd();
            sb.AppendLine(StripXml(xml));
            if (sb.Length >= maxChars) break;
        }

        var text = sb.ToString();
        if (text.Length > maxChars) text = text[..maxChars] + "\n…[обрезано]";
        return text.Length == 0 ? "[документ не содержит извлекаемого текста] " + Describe(path) : text;
    }

    private static string StripXml(string xml)
    {
        // Paragraph/heading tags become newlines, everything else is dropped.
        var withBreaks = System.Text.RegularExpressions.Regex.Replace(
            xml.Replace("<w:p", "\n<w:p").Replace("<text:p", "\n<text:p").Replace("<p>", "\n<p>").Replace("<p ", "\n<p "),
            "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(withBreaks);
        var collapsed = System.Text.RegularExpressions.Regex.Replace(decoded, @"[ \t]{2,}", " ");
        return System.Text.RegularExpressions.Regex.Replace(collapsed, @"(\r?\n){3,}", "\n\n").Trim();
    }

    public static string Describe(string path)
    {
        var info = new FileInfo(path);
        return $"Файл: {Path.GetFileName(path)}, размер: {FormatSize(info.Length)}, изменён: {info.LastWriteTime:yyyy-MM-dd HH:mm}.";
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} МБ",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} ГБ",
    };
}
