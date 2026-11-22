using OLI.Core;
using OLI.Server;

// OLI.Server.exe — runs the always-on gateway on this PC.
//   OLI.Server.exe [--port 8765] [--host 0.0.0.0] [--data <dir>] [--admin-token <token>]
// Settings come from the desktop app (settings.json → "Server"); CLI flags win.
// Ctrl+C stops it. The desktop app can also start/stop the server in-process.

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
            Console.WriteLine("  --no-registration  запретить регистрацию новых пользователей");
            return 0;
    }
}

AppPaths.EnsureDirectories();
await using var host = new ServerHost(options);

try
{
    await host.StartAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Не удалось запустить сервер на {options.Host}:{options.Port}: {ex.Message}");
    Console.Error.WriteLine("Возможно, порт занят (запущен второй OLI или другая программа). Выберите другой порт: --port 8766");
    return 1;
}

Console.WriteLine("======================================");
Console.WriteLine(" OLI Server");
Console.WriteLine("======================================");
Console.WriteLine($" API:         {host.BaseUrl}");
Console.WriteLine($" Данные:      {options.DataRoot}");
Console.WriteLine($" Модель:      {options.Model} @ {options.LlamaBaseUrl}");
Console.WriteLine($" Квота:       {options.UserQuotaBytes / 1024 / 1024} МБ на пользователя");
Console.WriteLine($" Регистрация: {(options.AllowRegistration ? "открыта" : "закрыта")}");
Console.WriteLine();

foreach (var ip in NetworkAddresses.Local())
    Console.WriteLine($" Телефону указывать: http://{ip}:{options.Port}");

Console.WriteLine();
Console.WriteLine(" Ctrl+C — остановить сервер.");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
await host.StopAsync();
Console.WriteLine("Сервер остановлен.");
return 0;
