namespace OLI.Server;

/// <summary>Runtime configuration for <see cref="ServerHost"/>.</summary>
public sealed class ServerOptions
{
    /// <summary>Directory for accounts.json, per-user workspaces and logs.</summary>
    public string DataRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OLI", "server");

    /// <summary>Interface to bind. 0.0.0.0 exposes the API on the LAN (needed for the phone).</summary>
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8765;

    /// <summary>llama.cpp server the gateway talks to.</summary>
    public string LlamaBaseUrl { get; set; } = "http://127.0.0.1:8080";
    public string Model { get; set; } = "oli";
    public int MaxTokens { get; set; } = 768;
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 0.95;
    public int TopK { get; set; } = 20;
    public double RepeatPenalty { get; set; } = 1.05;

    /// <summary>Allow new registrations from the app.</summary>
    public bool AllowRegistration { get; set; } = true;
    /// <summary>Optional invite code required at registration (empty = not required).</summary>
    public string InviteCode { get; set; } = "";

    /// <summary>Per-user storage allowance (1 GB by default).</summary>
    public long UserQuotaBytes { get; set; } = 1024L * 1024 * 1024;
    public bool WebSearchEnabled { get; set; } = true;
    public bool OpenWeb { get; set; } = true;
    public bool WorkspaceToolsEnabled { get; set; } = true;

    /// <summary>
    /// Optional admin token for the /admin endpoints. When empty, admin endpoints
    /// accept accounts flagged IsAdmin or requests coming from loopback only.
    /// </summary>
    public string AdminToken { get; set; } = "";

    public string Preamble { get; set; } =
        "Ты OLI — локальный ИИ-помощник. Пользователь общается с тобой через мобильное приложение. " +
        "Отвечай кратко и по делу, используй markdown-списки; длинные ответы разбивай на абзацы.";

    public string BaseUrl => $"http://{Host}:{Port}";
}
