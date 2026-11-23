using System.Text.Json.Serialization;

namespace OLI.Core;

public record ChatMessage(string Role, string Content);
public record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages, double Temperature = 0.7, int MaxTokens = 512);
public record ChatResponse(string Text);
public record SearchResult(string Title, string Url, string Snippet);

public enum GenerationMode { Fast, Performance }
public enum OliTheme { Dark, Light, System }

public enum OliLanguage { Auto, Russian, English }

/// <summary>Search back-ends OLI can use. Auto tries them in a sensible order.</summary>
public enum OliSearchProvider { Auto, DuckDuckGo, Bing, Mojeek, Searx, Wikipedia }

/// <summary>
/// Telegram bot transport settings. Telegram is partially throttled/blocked in
/// Russia, so OLI can reach the Bot API through (in order) a proxy, a relay
/// (e.g. the bundled Cloudflare Worker), or a direct connection with a DNS-over-HTTPS
/// bootstrap that defeats DNS poisoning.
/// </summary>
public sealed class TelegramSettings
{
    public bool Enabled { get; set; }
    public string BotToken { get; set; } = "";
    /// <summary>http:// or socks5:// proxy, e.g. socks5://127.0.0.1:1080</summary>
    public string ProxyUrl { get; set; } = "";
    /// <summary>Reverse-proxy / relay base URL that speaks the Bot API, e.g. https://oli-bot.example.workers.dev</summary>
    public string RelayBaseUrl { get; set; } = "";
    /// <summary>Shared secret for the Cloudflare Worker (RELAY_SECRET), optional.</summary>
    public string RelaySecret { get; set; } = "";
    /// <summary>Resolve api.telegram.org through DNS-over-HTTPS and connect by IP (defeats DNS blocking).</summary>
    public bool DohFallback { get; set; } = true;
    /// <summary>Comma separated numeric Telegram user ids allowed to talk to the bot. Empty = anyone.</summary>
    public string AllowedUserIds { get; set; } = "";
    /// <summary>Allow the bot to run workspace commands (off by default for remote users).</summary>
    public bool AllowCommands { get; set; }
    public int PollTimeoutSeconds { get; set; } = 50;
    public long MaxDocumentBytes { get; set; } = 50L * 1024 * 1024;
}

/// <summary>
/// Settings for turning this PC into the OLI server that the Android app talks to.
/// </summary>
public sealed class ServerSettings
{
    /// <summary>Server was started once and should come back with the app (phone pairs by itself).</summary>
    public bool Enabled { get; set; } = true;
    public bool AutoStartWithApp { get; set; } = true;
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8765;
    public bool AllowRegistration { get; set; } = true;
    public string InviteCode { get; set; } = "";
    /// <summary>Per-user workspace/file allowance on the server (1 GB by default).</summary>
    public long UserQuotaBytes { get; set; } = 1024L * 1024 * 1024;
    public string AdminToken { get; set; } = "";
    public bool WebSearchEnabled { get; set; } = true;
    public bool OpenWeb { get; set; } = true;
    public bool WorkspaceToolsEnabled { get; set; } = true;

    /// <summary>Token allowance (in + out) for a user who is alone on the server.</summary>
    public int SoloHourlyTokens { get; set; } = 150_000;
    public int SoloDailyTokens { get; set; } = 600_000;
    /// <summary>Floor of the allowance when the server is crowded.</summary>
    public int MinHourlyTokens { get; set; } = 20_000;
    public int MinDailyTokens { get; set; } = 80_000;
    public int BusyUsers { get; set; } = 8;

    /// <summary>Light model used after the token limit is exhausted (Olim Son).</summary>
    public string LiteModel { get; set; } = "oli-son";

    public string Preamble { get; set; } =
        "Ты OLI — локальный ИИ-помощник. Пользователь общается с тобой через мобильное приложение. " +
        "Отвечай кратко и по делу; длинные ответы структурируй списками.";
}

public sealed class OliSettings
{
    public string LlamaBaseUrl { get; set; } = "http://127.0.0.1:8080";
    public string Model { get; set; } = "oli";

    // ---- model selection -------------------------------------------------
    /// <summary>Hugging Face GGUF spec passed to llama-server's -hf.</summary>
    public string ModelGguf { get; set; } = "unsloth/Qwen3.5-4B-GGUF:Q4_K_M";
    public string ModelDisplayName { get; set; } = "Qwen3.5 4B (Q4_K_M)";
    public bool ThinkingEnabled { get; set; }
    public double TopP { get; set; } = 0.95;
    public int TopK { get; set; } = 20;
    public double MinP { get; set; } = 0.0;
    public double RepeatPenalty { get; set; } = 1.05;
    public int MaxTokens { get; set; } = 512;
    public double Temperature { get; set; } = 0.7;

    // ---- web search ------------------------------------------------------
    public bool WebSearchEnabled { get; set; } = true;
    public int WebSearchResultLimit { get; set; } = 5;
    /// <summary>true = the whole web (minus the block list), false = only AllowedSites.</summary>
    public bool WebSearchOpenWeb { get; set; } = true;
    public OliSearchProvider SearchProvider { get; set; } = OliSearchProvider.Auto;
    public int SearchTimeoutSeconds { get; set; } = 20;
    public List<string> WebSearchBlockedDomains { get; set; } = DefaultAllowedSites.DefaultBlocked();
    public List<AllowedSite> AllowedSites { get; set; } = DefaultAllowedSites.Create();

    // ---- generation / runtime -------------------------------------------
    public GenerationMode Mode { get; set; } = GenerationMode.Fast;
    public OliTheme Theme { get; set; } = OliTheme.Dark;
    public int FastMaxTokens { get; set; } = 512;
    public int PerformanceMaxTokens { get; set; } = 1536;
    public int ContextSize { get; set; } = 8192;
    public int BatchSize { get; set; } = 128;
    public int UBatchSize { get; set; } = 64;
    public int Parallel { get; set; } = 1;
    public int CpuThreads { get; set; } = 8;
    public int GpuLayers { get; set; } = 99;

    // ---- workspace (sandboxed file sandbox for the model) ----------------
    public bool WorkspaceEnabled { get; set; } = true;
    public long WorkspaceQuotaBytes { get; set; } = 1024L * 1024 * 1024; // 1 GB
    public long WorkspaceMaxFileBytes { get; set; } = 64L * 1024 * 1024;
    public bool AllowCommandExecution { get; set; } = true;
    public List<string> AllowedCommands { get; set; } = DefaultCommands.Create();

    // ---- telegram --------------------------------------------------------
    public TelegramSettings Telegram { get; set; } = new();

    // ---- server (PC as backend for the Android app) -----------------------
    public ServerSettings Server { get; set; } = new();

    // ---- misc ------------------------------------------------------------
    public string SystemPromptExtra { get; set; } = "";
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public string GitHubRepositoryUrl { get; set; } = "https://github.com/kips914/Oli_Ai";
    public OliLanguage Language { get; set; } = OliLanguage.Auto;
}

/// <summary>Safe-by-default list of programs the workspace run_command tool may start.</summary>
public static class DefaultCommands
{
    public static List<string> Create() =>
    [
        "python", "python3", "py", "node", "git", "dotnet", "tar", "7z"
    ];
}
