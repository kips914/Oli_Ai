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

    /// <summary>
    /// Public reverse-tunnel settings. Kept here so the desktop UI owns the state and
    /// the server can report whether internet access is available.
    /// </summary>
    public OLI.Core.TunnelSettings Tunnel { get; set; } = new();

    /// <summary>llama.cpp server the gateway talks to.</summary>
    public string LlamaBaseUrl { get; set; } = "http://127.0.0.1:8080";
    /// <summary>Full model used while the user still has tokens (oli-pass).</summary>
    public string Model { get; set; } = "oli-pass";
    /// <summary>Light model used after the token limit is exhausted (oli-son).</summary>
    public string LiteModel { get; set; } = "oli-son";
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

    /// <summary>Dynamic per-user token limits (tokens in + out).</summary>
    public int SoloHourlyTokens { get; set; } = 150_000;
    public int SoloDailyTokens { get; set; } = 600_000;
    public int MinHourlyTokens { get; set; } = 20_000;
    public int MinDailyTokens { get; set; } = 80_000;
    public int BusyUsers { get; set; } = 8;

    public string Preamble { get; set; } =
        "Ты OLI — локальный ИИ-помощник. Пользователь общается с тобой через мобильное приложение. " +
        "Отвечай кратко и по делу, используй markdown-списки; длинные ответы разбивай на абзацы.";

    /// <summary>PFX certificate to serve HTTPS directly (e.g. behind a Cloudflare Tunnel we still use HTTP).</summary>
    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";

    /// <summary>Origins allowed to call the API from a browser (the app does not care).</summary>
    public string[] AllowedOrigins { get; set; } = ["*"];

    public bool HttpsEnabled => !string.IsNullOrWhiteSpace(CertificatePath) && File.Exists(CertificatePath);

    public string BaseUrl => $"{(HttpsEnabled ? "https" : "http")}://{(Host is "0.0.0.0" or "::" or "*" ? "localhost" : Host)}:{Port}";
}
