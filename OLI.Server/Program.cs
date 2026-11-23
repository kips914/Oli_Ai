using System.Diagnostics;
using OLI.Core;
using OLI.Server;

// OLI.Server.exe — runs the always-on gateway on this PC.
//   OLI.Server.exe [--port 8765] [--host 0.0.0.0] [--data <dir>] [--admin-token <token>]
// Settings come from the desktop app (settings.json → "Server"); CLI flags win.
// Ctrl+C stops it. The desktop app can also start/stop the server in-process.

// Readable Russian instead of "?" in a double-clicked console window.
ConsoleUi.Prepare("OLI Server");

var settings = SettingsStore.Load();
var s = settings.Server;

var options = new ServerOptions
{
    DataRoot = Path.Combine(AppPaths.DataRoot, "server"),
    Host = string.IsNullOrWhiteSpace(s.Host) ? "0.0.0.0" : s.Host,
    Port = s.Port,
    LlamaBaseUrl = settings.LlamaBaseUrl,
    Model = settings.Model,
    MaxTokens = settings.MaxTokens,
    Temperature = settings.Temperature,
    TopP = settings.TopP,
    TopK = settings.TopK,
    RepeatPenalty = settings.RepeatPenalty,
    AllowRegistration = s.AllowRegistration,
    InviteCode = s.InviteCode,
    UserQuotaBytes = s.UserQuotaBytes,
    AdminToken = s.AdminToken,
    WebSearchEnabled = settings.WebSearchEnabled,
    OpenWeb = settings.WebSearchOpenWeb,
    WorkspaceToolsEnabled = s.WorkspaceToolsEnabled,
    LiteModel = string.IsNullOrWhiteSpace(s.LiteModel) ? "oli-son" : s.LiteModel,
    SoloHourlyTokens = s.SoloHourlyTokens,
    SoloDailyTokens = s.SoloDailyTokens,
    MinHourlyTokens = s.MinHourlyTokens,
    MinDailyTokens = s.MinDailyTokens,
    BusyUsers = s.BusyUsers,
    Preamble = string.IsNullOrWhiteSpace(s.Preamble) ? new ServerOptions().Preamble : s.Preamble,
};

for (var i = 0; i < args.Length; i++)
{
    var next = i + 1 < args.Length ? args[i + 1] : null;
    switch (args[i])
    {
        case "--port" when int.TryParse(next, out var port): options.Port = port; break;
        case "--host" when next is not null: options.Host = next; break;
        case "--data" when next is not null: options.DataRoot = next; break;
        case "--admin-token" when next is not null: options.AdminToken = next; break;
        case "--llama" when next is not null: options.LlamaBaseUrl = next; break;
        case "--quota-mb" when long.TryParse(next, out var mb): options.UserQuotaBytes = mb * 1024 * 1024; break;
        case "--invite" when next is not null: options.InviteCode = next; break;
        case "--cert" when next is not null: options.CertificatePath = next; break;
        case "--cert-password" when next is not null: options.CertificatePassword = next; break;
        case "--no-registration": options.AllowRegistration = false; break;
        case "--help" or "-h":
            Console.WriteLine("OLI Server — многопользовательский шлюз для приложения OLI Mobile.");
            Console.WriteLine("  --port <n>         порт (по умолчанию 8765)");
            Console.WriteLine("  --host <addr>      адрес привязки (0.0.0.0 — доступ по сети)");
            Console.WriteLine("  --data <dir>       папка данных (accounts.json, workspaces)");
            Console.WriteLine("  --admin-token <t>  токен для /api/admin (заголовок X-OLI-Admin)");
            Console.WriteLine("  --llama <url>      адрес llama.cpp (по умолчанию http://127.0.0.1:8080)");
            Console.WriteLine("  --quota-mb <n>     квота на пользователя в МБ (по умолчанию 1024)");
            Console.WriteLine("  --invite <code>    код приглашения для регистрации");
            Console.WriteLine("  --cert <file.pfx>  сертификат для HTTPS (иначе обычный HTTP)");
            Console.WriteLine("  --cert-password <p> пароль сертификата");
            Console.WriteLine("  --no-registration  запретить регистрацию новых пользователей");
            return 0;
    }
}

AppPaths.EnsureDirectories();

// Keep the console alive on a fatal error instead of closing the window instantly.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var ex = e.ExceptionObject as Exception;
    Console.Error.WriteLine();
    Console.Error.WriteLine("Ошибка сервера OLI: " + (ex?.Message ?? "неизвестная ошибка"));
    OliLog.Error("SERVER", ex?.ToString() ?? "unknown");
};

await using var host = new ServerHost(options);

try
{
    await host.StartAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Не удалось запустить сервер на {options.Host}:{options.Port}: {ex.Message}");
    Console.Error.WriteLine("Возможно, порт занят (запущен второй OLI или другая программа). Выберите другой порт: --port 8766");
    OliLog.Error("SERVER", ex.ToString());
    ConsoleUi.PauseIfNeeded();
    return 1;
}

var phoneAddresses = NetworkAddresses.LocalDetailed();
var primary = phoneAddresses.FirstOrDefault(a => a.IsWifiOrEthernet)?.Address
              ?? phoneAddresses.FirstOrDefault()?.Address;

Console.WriteLine("======================================");
Console.WriteLine(" OLI Server  " + AppVersion.Current);
Console.WriteLine("======================================");
Console.WriteLine($" Адрес для телефона: http://{primary ?? "—"}:{options.Port}");
if (phoneAddresses.Count > 1)
    Console.WriteLine($" Все адреса ПК:       {NetworkAddresses.DescribeAll()}");
Console.WriteLine($" Данные:             {options.DataRoot}");
Console.WriteLine($" Модель:             {options.Model} @ {options.LlamaBaseUrl}");
Console.WriteLine($" Квота:              {options.UserQuotaBytes / 1024 / 1024} МБ на пользователя");
Console.WriteLine($" Регистрация:        {(options.AllowRegistration ? "открыта" : "закрыта")}");
Console.WriteLine();
Console.WriteLine(" В приложении на телефоне: «Свой ПК как сервер» → «Найти мой ПК».");
Console.WriteLine(" Сервер найдётся сам — вписывать адрес не нужно.");
Console.WriteLine();
Console.WriteLine(" Ctrl+C — остановить сервер.");

// The phone can reach the server, but answers need llama.cpp. Tell the owner plainly
// instead of letting the first chat message fail with a cryptic error.
_ = Task.Run(async () =>
{
    using var probe = new OLI.Engine.LlamaClient(options.LlamaBaseUrl);
    var alive = await probe.IsAliveAsync();
    Console.WriteLine(alive
        ? $" Движок модели:      на связи ({options.LlamaBaseUrl})"
        : $" Движок модели:      НЕ ОТВЕЧАЕТ ({options.LlamaBaseUrl}) — чат вернёт ошибку, " +
          "запустите OLI на ПК или llama-server.");
});

// Open the firewall port in the background: without it the phone sees nothing.
if (OperatingSystem.IsWindows())
{
    _ = Task.Run(() =>
    {
        try
        {
            var (code, output) = FirewallCheck.Run(options.Port);
            if (code == 0) return;
            var add = FirewallCheck.Add(options.Port);
            Console.WriteLine(add.Ok
                ? $" Порт {options.Port} открыт в брандмауэре Windows."
                : $" Порт {options.Port}: {add.Message} — разрешите OLI доступ к сети в запросе Windows.");
        }
        catch (Exception ex)
        {
            OliLog.Warning("SERVER", "Брандмауэр: " + ex.Message);
        }
    });
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
await host.StopAsync();
Console.WriteLine("Сервер остановлен.");
return 0;

// ---------------------------------------------------------------- helpers

/// <summary>Minimal netsh wrapper: the server must be reachable from the phone out of the box.</summary>
internal static class FirewallCheck
{
    private const string RuleName = "OLI Server (phone app)";

    public static (int Code, string Output) Run(int port)
    {
        var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi);
        if (process is null) return (-1, "netsh не запустился");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(8000);
        var ok = process.HasExited && process.ExitCode == 0 && output.Contains(port.ToString(), StringComparison.Ordinal);
        return (ok ? 0 : -1, output.Trim());
    }

    public static (bool Ok, string Message) Add(int port)
    {
        var psi = new ProcessStartInfo("cmd.exe",
            $"/c netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
            $"protocol=TCP localport={port} profile=any")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi);
        if (process is null) return (false, "netsh не запустился");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        return Run(port).Code == 0
            ? (true, "")
            : (false, "нужны права администратора, чтобы добавить правило брандмауэра");
    }
}
