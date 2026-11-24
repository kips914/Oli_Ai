using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using OLI.Core;

namespace OLI.Client;

/// <summary>
/// Makes the OLI server reachable from a phone that is NOT on the same Wi-Fi —
/// mobile data at university, a friend's network, another city.
///
/// Plain NAT on a home PC cannot be reached from the internet, so OLI starts a
/// reverse tunnel next to the server and publishes the public address:
///
///   * <b>Cloudflare quick tunnel</b> — <c>cloudflared tunnel --url http://127.0.0.1:8765</c>.
///     No account, no domain, works from Russian mobile networks, address looks like
///     <c>https://random-words.trycloudflare.com</c>. The address changes every restart,
///     which is exactly why the pairing code is printed next to it.
///   * <b>ngrok</b> — used when <c>ngrok.exe</c> is on PATH and <c>authToken</c> is set
///     (a reserved domain gives a stable address: <c>domain</c>).
///   * <b>own relay</b> — any command that prints a public URL (Cloudflare named tunnel,
///     localtunnel, a VPS with ssh -R, frp…). OLI simply reads the first https:// URL
///     from the program's output and asks the user to confirm it by probing from inside.
///
/// The tunnel process lives as long as OLI does; stopping the app closes the public
/// port, so nothing stays exposed by accident.
/// </summary>
public sealed class TunnelManager : IDisposable
{
    public enum TunnelKind { None, Cloudflare, Ngrok, Custom }

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly List<string> _log = [];
    private readonly object _lock = new();
    private Process? _process;
    private Timer? _watchdog;
    private CancellationTokenSource? _restart;
    private int _restarts;
    private bool _stoppedByUser = true;
    private TunnelSettings? _settings;
    private int _port;

    /// <summary>How many automatic restarts are attempted before asking the user.</summary>
    public const int MaxRestarts = 3;

    /// <summary>Raised on the UI thread's behalf whenever status/address changes.</summary>
    public event Action? Changed;

    public bool IsRunning { get; private set; }
    public TunnelKind Kind { get; private set; } = TunnelKind.None;
    public string PublicUrl { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public string StatusLine { get; private set; } = "Туннель выключен: телефон подключается только по Wi-Fi.";

    /// <summary>The port cloudflared/ngrok forwards (the OLI server port).</summary>
    public int TargetPort { get; private set; } = 8765;

    public IReadOnlyList<string> Log
    {
        get { lock (_lock) return _log.ToList(); }
    }

    // ------------------------------------------------------------------ start

    public async Task<bool> StartAsync(TunnelSettings settings, int port, string? customCommand = null, bool isRestart = false)
    {
        // Not "stopped by user": the watchdog restarts the tunnel through this same method.
        await StopAsync(byUser: false);
        TargetPort = port;
        _settings = settings;
        _port = port;
        if (!isRestart) _restarts = 0;
        _stoppedByUser = false;
        LastError = "";
        PublicUrl = "";

        if (!settings.Enabled)
        {
            Set("Туннель выключен: телефон подключается только по Wi-Fi.", running: false);
            return false;
        }

        // The address of the local server must answer first, otherwise the tunnel is useless.
        if (!await LocalServerAliveAsync(port))
        {
            LastError = $"Локальный сервер на порту {port} не отвечает — сначала запустите сервер OLI.";
            Set(LastError, running: false);
            return false;
        }

        var kind = settings.Mode;
        if (kind == "ngrok" && !string.IsNullOrWhiteSpace(settings.AuthToken)) Kind = TunnelKind.Ngrok;
        else if (kind == "custom") Kind = TunnelKind.Custom;
        else Kind = TunnelKind.Cloudflare;

        var exe = Kind switch
        {
            TunnelKind.Ngrok => settings.NgrokPath,
            TunnelKind.Custom => settings.CustomCommand,
            _ => settings.CloudflaredPath,
        };

        var resolved = ResolveExecutable(exe, Kind switch
        {
            TunnelKind.Ngrok => "ngrok",
            TunnelKind.Custom => "",
            _ => "cloudflared",
        });

        if (resolved is null && Kind == TunnelKind.Cloudflare)
        {
            // The one reason the tunnel never started: cloudflared.exe was simply not there.
            // OLI now fetches it itself (with a hash/size check) instead of sending the user
            // to GitHub to find, rename and place a file.
            Set("Туннель: скачиваю cloudflared.exe (один раз)…", running: true);
            var installer = new ComponentInstaller();
            var report = await installer.EnsureAsync(ComponentInstaller.Cloudflared);
            if (!report.Ok)
            {
                LastError = report.Error.Length > 0
                    ? $"Не удалось скачать cloudflared: {report.Error}"
                    : "Не удалось скачать cloudflared.";
                Set(LastError, running: false);
                return false;
            }
            resolved = report.Path;
        }

        if (resolved is null)
        {
            LastError = Kind switch
            {
                TunnelKind.Ngrok => "ngrok.exe не найден. Укажите путь к ngrok.exe в настройках туннеля.",
                TunnelKind.Custom => "Не задана команда туннеля в настройках.",
                _ => "cloudflared.exe не найден. Нажмите «Установить компоненты» в пульте админа.",
            };
            Set(LastError, running: false);
            return false;
        }

        _triedPlainFlags = false;
        var psi = BuildStartInfo(resolved, settings, port);
        try
        {
            if (!Launch(psi))
            {
                Set(LastError.Length > 0 ? LastError : "Не удалось запустить процесс туннеля.", running: false);
                return false;
            }
            Set("Туннель запускается, жду публичный адрес…", running: true);

            // cloudflared prints the URL within ~5-20 s; give up after 45 s.
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(700);
                if (!string.IsNullOrWhiteSpace(PublicUrl)) break;
                if (_process is null || _process.HasExited) break;
            }

            if (string.IsNullOrWhiteSpace(PublicUrl) && Kind == TunnelKind.Cloudflare && !_triedPlainFlags)
            {
                // Some networks only work with cloudflared's own defaults; try them once
                // instead of making the user guess which flag to remove.
                _triedPlainFlags = true;
                OliLog.Warning("TUNNEL", "Адрес не появился с --protocol http2; пробую режим по умолчанию.");
                await StopAsync(byUser: false);
                var plain = BuildStartInfo(resolved, settings, port, plain: true);
                var retry = Launch(plain);
                if (retry)
                {
                    var deadline2 = DateTime.UtcNow.AddSeconds(45);
                    while (DateTime.UtcNow < deadline2 && string.IsNullOrWhiteSpace(PublicUrl))
                    {
                        await Task.Delay(700);
                        if (_process is null || _process.HasExited) break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(PublicUrl))
            {
                LastError = LastError.Length > 0
                    ? LastError
                    : "Туннель запустился, но адрес не появился за 45 секунд. " +
                      "Чаще всего это блокировка провайдера: включите VPN на ПК или раздайте интернет с телефона.";
                Set(LastError, running: IsRunning);
                return false;
            }

            if (!await PublicAliveAsync(PublicUrl))
            {
                // The tunnel exists but nothing answers from outside: most often the local
                // server is bound to 127.0.0.1 only, or the port is closed.
                LastError = $"Адрес {PublicUrl} выдан, но снаружи не открывается. " +
                            "Проверьте, что сервер слушает 0.0.0.0 и порт открыт в брандмауэре.";
                Set(LastError, running: IsRunning);
                return false;
            }

            StartWatchdog();
            Set($"Туннель работает: {PublicUrl}", running: true);
            return true;
        }
        catch (Exception ex)
        {
            LastError = "Ошибка запуска туннеля: " + ex.Message;
            OliLog.Error("TUNNEL", ex.ToString());
            Set(LastError, running: false);
            return false;
        }
    }

    public Task StopAsync(bool byUser = true)
    {
        if (byUser) _stoppedByUser = true;
        _watchdog?.Dispose();
        _watchdog = null;
        _restart?.Cancel();
        _restart?.Dispose();
        _restart = null;

        var process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
            process.Dispose();
        }
        IsRunning = false;
        PublicUrl = "";
        Kind = TunnelKind.None;
        StatusLine = byUser ? "Туннель выключен: телефон подключается только по Wi-Fi." : StatusLine;
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------- watchdog

    /// <summary>
    /// Keeps the tunnel alive. cloudflared occasionally dies on a flaky network (and Windows
    /// may kill it on sleep/resume); the watchdog notices within 20 seconds, re-creates the
    /// public address and reports it, so the phone can reconnect with a fresh code instead of
    /// the user wondering why "интернет-доступ" died overnight.
    /// </summary>
    private void StartWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    private async Task CheckAsync()
    {
        if (_stoppedByUser || _settings is null) return;

        var process = _process;
        if (process is not null && !process.HasExited) return; // alive: nothing to do

        if (_restarts >= MaxRestarts)
        {
            _watchdog?.Dispose();
            _watchdog = null;
            LastError = "Туннель падает повторно. Проверьте интернет на ПК и журнал туннеля " +
                        $"({_log.Count} строк) — затем нажмите «Включить доступ» ещё раз.";
            Set(LastError, running: false);
            return;
        }

        _restarts++;
        Set($"Туннель отключился — перезапускаю ({_restarts}/{MaxRestarts})…", running: true);
        try
        {
            var settings = _settings;
            var port = _port;
            await StartAsync(settings, port, isRestart: true);
        }
        catch (Exception ex)
        {
            OliLog.Warning("TUNNEL", "Автоперезапуск: " + ex.Message);
        }
    }

    // ------------------------------------------------------------- pairing code

    /// <summary>
    /// Compact "join code" the student can type on the phone once. The format lives in
    /// <see cref="ConnectionCore"/> now, so the phone, the PC and the LAN path all agree.
    /// </summary>
    public static string BuildPairCode(string publicUrl, string inviteCode) =>
        ConnectionCore.BuildJoinCode(publicUrl, inviteCode);

    public static (string Url, string Invite)? ParsePairCode(string code) =>
        ConnectionCore.ParseJoinCode(code) is { } info ? (info.Address, info.InviteCode) : null;

    // ------------------------------------------------------------- internals

    /// <summary>Second attempt without the extra cloudflared flags (see StartAsync).</summary>
    private bool _triedPlainFlags;

    /// <summary>Starts the tunnel process and wires its output; false when it did not start.</summary>
    private bool Launch(ProcessStartInfo psi)
    {
        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            process.Exited += (_, _) => OnExit();
            if (!process.Start()) return false;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            IsRunning = true;
            return true;
        }
        catch (Exception ex)
        {
            OliLog.Error("TUNNEL", ex.ToString());
            LastError = "Ошибка запуска туннеля: " + ex.Message;
            return false;
        }
    }

    private ProcessStartInfo BuildStartInfo(string exe, TunnelSettings settings, int port, bool plain = false)
    {
        if (Kind == TunnelKind.Custom)
        {
            // cmd /c keeps the quoting the user typed intact.
            var psi = new ProcessStartInfo("cmd.exe", $"/c {exe}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            return psi;
        }

        // IPv4 edges and http2 avoid the two most common reasons a quick tunnel never
        // comes up on locked-down networks: broken IPv6 routes and blocked QUIC/UDP 7844.
        var args = Kind == TunnelKind.Ngrok
            ? $"http http://127.0.0.1:{port} --log stdout --log-format logfmt"
            : plain
                ? $"tunnel --no-autoupdate --url http://127.0.0.1:{port}"
                : $"tunnel --no-autoupdate --edge-ip-version 4 --protocol http2 --url http://127.0.0.1:{port}";

        var start = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Kind == TunnelKind.Ngrok && !string.IsNullOrWhiteSpace(settings.AuthToken))
            start.Environment["NGROK_AUTHTOKEN"] = settings.AuthToken;
        return start;
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_lock)
        {
            _log.Add(line.Trim());
            if (_log.Count > 300) _log.RemoveAt(0);
        }

        OliLog.Debug("TUNNEL", line.Trim());

        if (!string.IsNullOrWhiteSpace(PublicUrl)) return;

        var match = Regex.Match(line, @"https://[a-z0-9][a-z0-9.-]*\.(?:trycloudflare\.com|ngrok(?:-free)?\.(?:app|io)|loca\.lt|[a-z0-9-]+\.(?:ru|com|net|dev|site|online))[^\s""']*", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var url = match.Value.TrimEnd('.', ',', ')', '"', '\'');
            PublicUrl = url;
            Set($"Туннель работает: {url}", running: true);
            return;
        }

        if (line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error", StringComparison.OrdinalIgnoreCase))
            LastError = line.Trim();
    }

    private void OnExit()
    {
        var wasRunning = IsRunning;
        IsRunning = false;
        // Keep the last public address in LastPublicUrl: a restarted quick tunnel gets a new
        // one, and the pairing code shown in the UI must not silently point at a dead host.
        PublicUrl = "";
        _process = null;
        if (_stoppedByUser)
        {
            Set("Туннель выключен.", running: false);
            return;
        }
        Set(wasRunning
            ? "Туннель закрылся — перезапущу автоматически через 20 секунд…"
            : "Туннель не запустился.", running: false);
    }

    private void Set(string status, bool running)
    {
        StatusLine = status;
        IsRunning = running;
        try { Changed?.Invoke(); } catch { /* UI gone */ }
    }

    private async Task<bool> LocalServerAliveAsync(int port)
    {
        try
        {
            using var response = await _http.GetAsync($"http://127.0.0.1:{port}/api/health");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<bool> PublicAliveAsync(string url)
    {
        // trycloudflare can take a few seconds to publish the route; retry a bit.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(url.TrimEnd('/') + "/api/health");
                if (response.IsSuccessStatusCode) return true;
            }
            catch { /* not published yet */ }
            await Task.Delay(1500);
        }
        return false;
    }

    /// <summary>Full path when the value has a directory, otherwise look next to OLI.exe and on PATH.</summary>
    private static string? ResolveExecutable(string? configured, string fallbackName)
    {
        var value = configured?.Trim().Trim('"') ?? "";
        if (value.Length > 0)
        {
            if (File.Exists(value)) return value;
            var beside = Path.Combine(AppPaths.InstallRoot, value);
            if (File.Exists(beside)) return beside;
            // A bare name ("cloudflared") — fall through to PATH lookup.
            if (fallbackName.Length == 0) return null;
        }

        if (fallbackName.Length == 0) return null;

        var local = Path.Combine(AppPaths.InstallRoot, fallbackName + ".exe");
        if (File.Exists(local)) return local;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fallbackName + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* bad PATH entry */ }
        }
        return null;
    }

    public void Dispose()
    {
        _stoppedByUser = true;
        _watchdog?.Dispose();
        _watchdog = null;
        try { _process?.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _process = null;
        _http.Dispose();
    }
}
