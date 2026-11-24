namespace OLI.Core;

/// <summary>
/// One selectable local model. <see cref="Gguf"/> is the exact Hugging Face
/// repository + quant spec understood by llama.cpp's <c>-hf</c> flag, so the
/// server downloads and caches it on first launch.
/// </summary>
public sealed record OliModelInfo(
    string Id,
    string DisplayName,
    string Gguf,
    string Family,
    long SizeBytes,
    int ContextSize,
    string Notes);

/// <summary>
/// Curated catalogue of GGUF models that run well on the target machine
/// (6 GB VRAM / 8 GB RAM). Qwen3.5 4B is the default.
/// </summary>
public static class ModelCatalog
{
    public const string DefaultId = "oli-pass";
    /// <summary>Entry-level model used when a user's token limit is exhausted.</summary>
    public const string LiteId = "oli-son";

    public static IReadOnlyList<OliModelInfo> All { get; } =
    [
        new("oli-pass", "Olim Pass", "unsloth/Qwen3.5-4B-GGUF:Q4_K_M", "oli", 2_940_000_000, 32768,
            "Флагман OLI: максимальное качество ответов, сложные задачи, код и анализ."),
        new("oli-son", "Olim Son", "unsloth/Qwen3.5-2B-GGUF:Q4_K_M", "oli", 1_600_000_000, 32768,
            "Самая лёгкая модель OLI: быстрые ответы на слабом железе и когда лимит Olim Pass исчерпан."),
        new("qwen3.5-4b-q4km", "Olim Pass · Q4_K_M (флагман)",
            "unsloth/Qwen3.5-4B-GGUF:Q4_K_M", "Qwen3.5", 2_940_000_000, 32768,
            "Лучший баланс качества и скорости для 6 ГБ VRAM. Мультимодальная, мультиязычная."),
        new("qwen3.5-4b-iq4xs", "Olim Pass · IQ4_XS (экономно)",
            "unsloth/Qwen3.5-4B-GGUF:IQ4_XS", "Qwen3.5", 2_300_000_000, 32768,
            "Меньше памяти и быстрее, чуть ниже качество."),
        new("qwen3.5-4b-q6k", "Olim Pass · Q6_K (качество)",
            "unsloth/Qwen3.5-4B-GGUF:Q6_K", "Qwen3.5", 3_400_000_000, 32768,
            "Заметно точнее Q4, но требует больше VRAM."),
        new("qwen3.5-2b-q4km", "Olim Son · Q4_K_M (лёгкая)",
            "unsloth/Qwen3.5-2B-GGUF:Q4_K_M", "Qwen3.5", 1_600_000_000, 32768,
            "Очень быстрая, подходит для слабых ПК и одновременной работы с Telegram-ботом."),
        new("qwen3.5-9b-q4km", "Olim Pass · 9B Q4_K_M (максимум качества)",
            "unsloth/Qwen3.5-9B-GGUF:Q4_K_M", "Qwen3.5", 5_800_000_000, 32768,
            "Нужно ≥6 ГБ VRAM или частичный offload на CPU; заметно медленнее."),
        new("qwen3-4b-instruct", "Olim Pass · резервный вариант",
            "Qwen/Qwen3-4B-Instruct-2507-GGUF:Q4_K_M", "Qwen3", 2_500_000_000, 32768,
            "Предыдущее поколение — если Qwen3.5 почему-то не запускается."),
    ];

    public static OliModelInfo Resolve(string? idOrGguf)
    {
        if (string.IsNullOrWhiteSpace(idOrGguf)) return ById(DefaultId);
        var byId = All.FirstOrDefault(m => m.Id.Equals(idOrGguf, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        var byGguf = All.FirstOrDefault(m => m.Gguf.Equals(idOrGguf, StringComparison.OrdinalIgnoreCase));
        if (byGguf is not null) return byGguf;
        // Unknown custom spec — treat as a raw -hf value.
        return new OliModelInfo("custom", idOrGguf, idOrGguf, "custom", 0, 8192, "Пользовательская модель GGUF.");
    }

    public static OliModelInfo ById(string id) =>
        All.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>Applies a catalogue entry to settings (keeps everything else intact).</summary>
    public static void Apply(OliSettings settings, OliModelInfo model)
    {
        settings.ModelGguf = model.Gguf;
        settings.ModelDisplayName = model.DisplayName;
        settings.Model = "oli";
        if (settings.ContextSize > model.ContextSize) settings.ContextSize = model.ContextSize;
    }
}
