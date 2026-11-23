using System.Text;
using OLI.Core;
using OLI.Workspace;

namespace OLI.Agent;

/// <summary>
/// Turns an attached image into text for the model.
///
/// The OLI engine (llama.cpp with a text model) cannot see pictures, so the image is
/// inspected on the server: real colours are sampled from the pixels, and, when the model
/// is multimodal, the picture is forwarded as base64 in the OpenAI "image_url" form.
/// The result is a factual description block that is injected into the conversation
/// together with the user's question, so the answer is about the real picture.
/// </summary>
public static class ImageAnalyzer
{
    public const long MaxBytes = 8L * 1024 * 1024;

    /// <summary>True when the model may accept images (gguf name suggests a vision build).</summary>
    public static bool ModelSeesImages(OliSettings settings) =>
        settings.ModelGguf.Contains("vl", StringComparison.OrdinalIgnoreCase) ||
        settings.ModelGguf.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
        settings.ModelGguf.Contains("llava", StringComparison.OrdinalIgnoreCase) ||
        settings.ModelGguf.Contains("gemma-3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the prompt block that describes the attached image. Never throws:
    /// a broken file still gets a short, honest description.
    /// </summary>
    public static string Describe(string fileName, byte[] bytes)
    {
        var kind = Detect(bytes);
        var facts = Inspect(bytes);
        var text = new StringBuilder();
        text.AppendLine("📎 Пользователь приложил изображение.");
        text.AppendLine($"Файл: {fileName} ({TextExtractors.FormatSize(bytes.Length)}, {kind}).");
        text.AppendLine();
        text.AppendLine("Что видно по самому файлу:");
        foreach (var fact in facts) text.AppendLine("• " + fact);
        text.AppendLine();
        text.AppendLine("Скажи, что это за изображение и чем помочь: описать, разобрать текст, " +
                        "предложить подпись, найти проблемы на скриншоте и т.п. " +
                        "Не выдумывай детали, которых не видно.");
        return text.ToString();
    }

    private static string Detect(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50) return "PNG";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) return "JPEG";
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49) return "GIF";
        if (bytes.Length >= 12 && bytes[8] == 0x57 && bytes[9] == 0x45) return "WEBP";
        if (bytes.Length >= 4 && bytes[0] == 0x42 && bytes[1] == 0x4D) return "BMP";
        return "изображение";
    }

    /// <summary>Dimensions plus a palette read from the actual pixels (BMP) or the header.</summary>
    private static List<string> Inspect(byte[] bytes)
    {
        var facts = new List<string>();
        var (width, height) = Size(bytes);
        if (width > 0 && height > 0)
        {
            facts.Add($"размер {width}×{height} пикселей, соотношение сторон {(double)width / height:0.00}");
            facts.Add(width > height
                ? "ориентация горизонтальная (похоже на скриншот или фото сцены)"
                : "ориентация вертикальная (похоже на фото экрана телефона)");
        }

        if (facts.Count == 0) facts.Add("размер определить не удалось — файл может быть повреждён");

        if (Palette(bytes) is { } palette)
        {
            facts.Add($"основные цвета по пикселям: {palette.Colors}");
            facts.Add(palette.Light
                ? "в целом изображение светлое (много белого/светлого фона)"
                : "в целом изображение тёмное (тёмный фон или ночной кадр)");
        }

        return facts;
    }

    private static (int Width, int Height) Size(byte[] b)
    {
        try
        {
            // PNG: IHDR at offset 16
            if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50)
                return (Be(b, 16), Be(b, 20));

            // GIF
            if (b.Length > 10 && b[0] == 0x47 && b[1] == 0x49)
                return (b[6] | (b[7] << 8), b[8] | (b[9] << 8));

            // BMP (BITMAPINFOHEADER, little-endian, height может быть отрицательным)
            if (b.Length > 26 && b[0] == 0x42 && b[1] == 0x4D)
                return (Le(b, 18), Math.Abs(Le(b, 22)));

            // WEBP: VP8/VP8L/VP8X
            if (b.Length > 30 && b[8] == 0x57 && b[9] == 0x45)
            {
                var chunk = Encoding.ASCII.GetString(b, 12, 4);
                if (chunk == "VP8X")
                    return (Le24(b, 24) + 1, Le24(b, 27) + 1);
                if (chunk == "VP8 ")
                    return ((b[26] | (b[27] << 8)) & 0x3FFF, (b[28] | (b[29] << 8)) & 0x3FFF);
                if (chunk == "VP8L")
                {
                    var bits = b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24);
                    return ((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
                }
            }

            // JPEG: walk the markers to the SOFn segment
            if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                var i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    var marker = b[i + 1];
                    if (marker is 0xD8 or 0xD9 or 0x01 or >= 0xD0 and <= 0xD7) { i += 2; continue; }
                    var length = (b[i + 2] << 8) | b[i + 3];
                    if (marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC)
                        return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                    i += 2 + length;
                }
            }
        }
        catch
        {
            // fall through: unknown size
        }
        return (0, 0);
    }

    private sealed record PaletteInfo(string Colors, bool Light);

    /// <summary>
    /// BMP pixels are raw, so the dominant colours can be measured for real. For compressed
    /// formats the header only gives the size, and the palette is skipped honestly.
    /// </summary>
    private static PaletteInfo? Palette(byte[] b)
    {
        try
        {
            if (!(b.Length > 54 && b[0] == 0x42 && b[1] == 0x4D)) return null;

            var offset = Le(b, 10);
            var bpp = b[28] | (b[29] << 8);
            if (bpp is not (24 or 32) || offset <= 0 || offset >= b.Length) return null;

            var bytesPerPixel = bpp / 8;
            long sum = 0;
            long count = 0;
            var buckets = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = offset; i + bytesPerPixel <= b.Length; i += bytesPerPixel * 997) // sparse sample
            {
                var bl = b[i];
                var gr = b[i + 1];
                var rd = b[i + 2];
                var luma = (rd * 299 + gr * 587 + bl * 114) / 1000;
                sum += luma;
                count++;
                var key = $"{rd / 48 * 48},{gr / 48 * 48},{bl / 48 * 48}";
                buckets[key] = buckets.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            if (count == 0) return null;
            var top = buckets.OrderByDescending(kv => kv.Value).Take(3)
                .Select(kv => kv.Key.Split(',').Select(x => int.Parse(x)))
                .Select(rgb => $"#{rgb.ElementAt(0):X2}{rgb.ElementAt(1):X2}{rgb.ElementAt(2):X2}");
            var average = sum / count;
            return new PaletteInfo(string.Join(", ", top), average >= 140);
        }
        catch
        {
            return null;
        }
    }

    private static int Be(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    private static int Le(byte[] b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
    private static int Le24(byte[] b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16);
}
