using OLI.Server;

// OLI.Server.exe — runs the always-on gateway on this PC.
//   OLI.Server.exe [--port 8765] [--host 0.0.0.0] [--data <dir>] [--admin-token <token>]
// Ctrl+C stops it. The desktop app can also start/stop it through the admin remote.

var options = new ServerOptions();
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--port": options.Port = int.TryParse(args[i + 1], out var port) ? port : options.Port; break;
        case "--host": options.Host = args[i + 1]; break;
        case "--data": options.DataRoot = args[i + 1]; break;
        case "--admin-token": options.AdminToken = args[i + 1]; break;
        case "--llama": options.LlamaBaseUrl = args[i + 1]; break;
        case "--quota-mb" when long.TryParse(args[i + 1], out var mb): options.UserQuotaBytes = mb * 1024 * 1024; break;
        case "--invite": options.InviteCode = args[i + 1]; break;
        case "--no-registration": options.AllowRegistration = false; break;
    }
}

await using var host = new ServerHost(options);
await host.StartAsync();

Console.WriteLine("======================================");
Console.WriteLine(" OLI Server");
Console.WriteLine("======================================");
Console.WriteLine($" API:        {host.BaseUrl}");
Console.WriteLine($" Данные:     {options.DataRoot}");
Console.WriteLine($" Квота:      {options.UserQuotaBytes / 1024 / 1024} МБ на пользователя");
Console.WriteLine($" Регистрация: {(options.AllowRegistration ? "открыта" : "закрыта")}");
Console.WriteLine();
Console.WriteLine(" Ctrl+C — остановить. Телефон подключайте по адресу http://<IP-этого-ПК>:"
                  + options.Port);

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
await host.StopAsync();
Console.WriteLine("Сервер остановлен.");
