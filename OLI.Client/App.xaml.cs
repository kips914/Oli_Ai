using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OLI.Core;
using OLI.Server;

namespace OLI.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Unhandled exceptions must never take the assistant down silently.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) OliLog.Error("APP", ex.ToString());
        };

        // One OLI at a time. Two copies fight for port 6767 and the second one floods the log
        // with "address already in use" — which is exactly what users reported. The second
        // launch just brings the first window to the front.
        if (!TakeSingleInstance())
        {
            MessageBox.Show(
                "OLI уже запущен — окно открыто на панели задач.\n\n" +
                "Второй экземпляр не нужен: он спорит с первым за порт 6767.",
                "OLI", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // First launch (or a version upgrade): install the tunnel binary, open the port and
        // run the mini connectivity test before the chat appears. Without this the user had
        // to discover by hand that cloudflared.exe was missing, which is why the tunnel
        // "did not start" on a fresh install.
        try
        {
            var settings = SettingsStore.Load();
            AppPaths.EnsureDirectories();
            // The wizard must never run on top of a server that already answers: that was the
            // source of "address already in use" — the wizard's own server stayed bound and
            // the main window then tried to bind the same port.
            // A live OLI needs no wizard: its window is about to open and the port is taken
            // on purpose. A foreign program on the port is no reason to skip the wizard —
            // the server moves to a free port by itself.
            // Startup runs before any window exists, so a short synchronous probe here is
            // safe: the answer is "connection refused" in microseconds, and a busy port
            // resolves within the 2 s timeout instead of racing the main window.
            var alreadyServing = PortGuard.OliAnswersAsync(settings.Server.Port).GetAwaiter().GetResult();
            if (!alreadyServing &&
                (!settings.SetupCompleted || settings.SetupVersion != AppVersion.Current))
            {
                var setup = new SetupWindow(settings, StartServerForSetupAsync, OpenFirewallAsync, StartTunnelForSetupAsync);
                var proceed = setup.ShowDialog();
                // "Продолжить всё равно" is a valid choice: OLI works over Wi-Fi without the
                // tunnel, and the user can re-run the check from the admin panel later.
                _ = proceed;
            }
        }
        catch (Exception ex)
        {
            // A broken setup must never block the app: the chat is the main feature.
            OliLog.Error("APP", "Первая настройка не завершилась: " + ex);
        }

        // Startup window is created by hand (no StartupUri) so that a broken resource
        // dictionary or a missing file shows a readable message instead of a silent crash.
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            OliLog.Error("APP", "MainWindow failed to load:\n" + ex);
            MessageBox.Show(
                "OLI не смог открыть главное окно.\n\n" +
                $"{ex.GetType().Name}: {ex.Message}\n\n" +
                $"Подробности в логе: {Path.Combine(AppPaths.LogsRoot, "oli.log")}",
                "OLI", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }

    // ------------------------------------------------------------------ single instance

    private static Mutex? _instanceMutex;

    /// <summary>
    /// Named mutex per user session. Released by the OS even after a crash, so a killed OLI
    /// never locks the next start out.
    /// </summary>
    private static bool TakeSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, @"Local\OLI.Client.SingleInstance", out var createdNew);
            if (!createdNew)
            {
                _instanceMutex.Dispose();
                _instanceMutex = null;
            }
            return createdNew;
        }
        catch (Exception ex)
        {
            // A mutex problem must never block the app; the port guard still protects us.
            OliLog.Warning("APP", "Проверка единственного экземпляра: " + ex.Message);
            return true;
        }
    }

    // ------------------------------------------------------------------ setup hooks

    private static ServerHost? _setupServer;

    /// <summary>
    /// Starts the server for the setup window. The chat window owns its own instance, so a
    /// server started here is stopped right after the check — otherwise the port would be
    /// taken and the real server would fail to start with "address already in use".
    /// </summary>
    private static async Task StartServerForSetupAsync()
    {
        // Reuse one instance: the wizard's "Повторить" must not create a second server on the
        // same port, which is what pushed "address already in use" into the log.
        if (_setupServer is not null) return;

        var settings = SettingsStore.Load();
        _setupServer = new ServerHost(new ServerOptions
        {
            DataRoot = Path.Combine(AppPaths.DataRoot, "server"),
            Host = settings.Server.Host,
            Port = settings.Server.Port,
            LlamaBaseUrl = settings.LlamaBaseUrl,
            Model = settings.Model,
            AllowRegistration = settings.Server.AllowRegistration,
            InviteCode = settings.Server.InviteCode,
            UserQuotaBytes = settings.Server.UserQuotaBytes,
            AdminToken = settings.Server.AdminToken,
            Tunnel = settings.Server.Tunnel,
        });
        await _setupServer.StartAsync();
    }

    private static async Task<bool> OpenFirewallAsync(int port)
    {
        var result = await Task.Run(() => FirewallHelper.EnsurePortOpen(port));
        return result is null;
    }

    /// <summary>Starts (and keeps) the tunnel for the setup check, returning its public URL.</summary>
    private static async Task<string?> StartTunnelForSetupAsync()
    {
        var settings = SettingsStore.Load();
        if (!settings.Server.Tunnel.Enabled && string.IsNullOrWhiteSpace(settings.Server.Tunnel.LastPublicUrl))
        {
            // The user never asked for the tunnel; do not expose the PC behind their back.
            return null;
        }

        // Restart a tunnel that was left over from a previous run of the wizard.
        try { _setupTunnel?.Dispose(); } catch { /* already gone */ }
        _setupTunnel = null;

        var manager = new TunnelManager();
        // The tunnel must point at the port the server really bound (it may have moved).
        var port = _setupServer?.ActivePort ?? settings.Server.Port;
        var started = await manager.StartAsync(settings.Server.Tunnel, port);
        var url = started ? manager.PublicUrl : "";
        // Keep the manager alive for the lifetime of the app: closing it kills cloudflared.
        _setupTunnel = manager;
        return url.Length > 0 ? url : null;
    }

    private static TunnelManager? _setupTunnel;

    /// <summary>
    /// The tunnel started by the setup window. <see cref="TunnelManager"/> is bound to one
    /// server port; when the chat window starts the real server, it takes this over so the
    /// public address stays up instead of dying with the temporary server.
    /// </summary>
    public static TunnelManager? TakeSetupTunnel()
    {
        var tunnel = _setupTunnel;
        _setupTunnel = null;
        return tunnel;
    }

    /// <summary>Stops the temporary server/tunnel created for the setup check.</summary>
    /// <summary>Awaitable variant, used where the port must be free before the next bind.</summary>
    public static async Task EndSetupAsync()
    {
        try { _setupTunnel?.Dispose(); } catch { /* already gone */ }
        _setupTunnel = null;
        var server = _setupServer;
        _setupServer = null;
        if (server is not null)
        {
            try { await server.DisposeAsync(); } catch { /* best effort */ }
        }
    }

    public static void EndSetup()
    {
        // TakeSetupTunnel() sets the field to null when the chat window adopted it.
        try { _setupTunnel?.Dispose(); } catch { /* already gone */ }
        _setupTunnel = null;
        if (_setupServer is not null)
        {
            try { _setupServer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
            _setupServer = null;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        OliLog.Error("APP", e.Exception.ToString());
        MessageBox.Show(
            $"Произошла ошибка, но OLI продолжает работать:\n\n{e.Exception.Message}\n\n" +
            $"Подробности в логе: {Path.Combine(AppPaths.LogsRoot, "oli.log")}",
            "OLI", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    /// <summary>Re-writes every theme brush. Called on startup and from Settings.</summary>
    public static void ApplyTheme(OliTheme theme)
    {
        var dark = theme == OliTheme.Dark || (theme == OliTheme.System && !IsSystemLight());
        var r = Current.Resources;

        Set(r, "BgWindowBrush", dark ? "#0B0D12" : "#F5F6FA");
        Set(r, "BgPanelBrush", dark ? "#12151C" : "#FFFFFF");
        Set(r, "BgElevatedBrush", dark ? "#181C25" : "#FFFFFF");
        Set(r, "BgInputBrush", dark ? "#1C212C" : "#EEF0F6");
        Set(r, "BorderBrush2", dark ? "#252B38" : "#DCE0EA");
        Set(r, "BorderStrongBrush", dark ? "#333B4D" : "#C3C9D8");
        Set(r, "AccentBrush", dark ? "#7C5CFF" : "#6247E8");
        Set(r, "AccentHoverBrush", dark ? "#8F72FF" : "#7257F2");
        Set(r, "AccentSoftBrush", dark ? "#2A2350" : "#ECE7FF");
        Set(r, "TextPrimaryBrush", dark ? "#ECEFF7" : "#1B1F2A");
        Set(r, "TextMutedBrush", dark ? "#8A91A6" : "#667085");
        Set(r, "BubbleAssistantBrush", dark ? "#1B2030" : "#EAEDF5");

        Current.Resources["IsDark"] = dark;
    }

    private static bool IsSystemLight()
    {
        try
        {
            var key = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0);
            return key is int value && value == 1;
        }
        catch { return false; }
    }

    private static void Set(ResourceDictionary r, string key, string hex) =>
        r[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}
