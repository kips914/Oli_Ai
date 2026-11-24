using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using OLI.Core;

namespace OLI.Client;

/// <summary>
/// First-run window: installs what OLI needs (cloudflared for the tunnel), starts the server,
/// opens the firewall port and runs the mini connectivity test before the user ever touches
/// the phone.
///
/// Everything here is idempotent: the window can be opened again from "Пульт админа" to
/// re-check a broken setup, and it never re-downloads a component that is already on disk.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly OliSettings _settings;
    private readonly Func<Task> _startServer;
    private readonly Func<int, Task<bool>>? _openFirewall;
    private readonly Func<Task<string?>>? _startTunnel;
    private readonly ComponentInstaller _installer = new();

    private SetupCheck? _lastCheck;
    private bool _portReleased;
    private bool _finishing;
    private string _publicUrl = "";
    private bool _running;

    /// <summary>True when the check found nothing broken — the app may open the chat.</summary>
    public bool Succeeded { get; private set; }

    public SetupWindow(
        OliSettings settings,
        Func<Task> startServer,
        Func<int, Task<bool>>? openFirewall = null,
        Func<Task<string?>>? startTunnel = null)
    {
        InitializeComponent();
        _settings = settings;
        _startServer = startServer;
        _openFirewall = openFirewall;
        _startTunnel = startTunnel;
        App.ApplyTheme(settings.Theme);

        // On an upgrade, say so: the user already knows OLI and only wants the check.
        if (settings.SetupCompleted)
        {
            Title = "OLI — проверка подключения";
            SubtitleText.Text = "Проверю, почему телефон не подключается, и исправлю, что смогу.";
            StepText.Text = "1. Компоненты";
        }

        Loaded += async (_, _) => await RunAsync();
    }

    // ------------------------------------------------------------------ run

    private async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        DoneButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        FixButton.Visibility = Visibility.Collapsed;
        LogText.Text = "";
        Log("OLI " + AppVersion.Current);
        Log($"Папка программы: {AppPaths.InstallRoot}");
        Log("");

        try
        {
            // 1. Components: the tunnel binary was the missing piece, and without it the
            //    phone could only connect over the same Wi-Fi.
            StepText.Text = "1. Компоненты";
            var progress = new Progress<string>(Log);
            var reports = await EnsureComponentsAsync(progress);
            Log("");

            // 2. Server. Without it nothing else can be tested.
            StepText.Text = "2. Сервер и брандмауэр";
            Log("· Запускаю сервер OLI…");
            try
            {
                await _startServer();
                Log($"✓ Сервер запущен на порту {_settings.Server.Port}");
            }
            catch (Exception ex)
            {
                Log("⚠ Сервер не запустился: " + ex.Message);
            }

            var port = _settings.Server.Port;
            Log(FirewallHelper.RuleExists(port)
                ? $"✓ Порт {port} уже открыт в брандмауэре"
                : $"· Открываю порт {port} в брандмауэре (Windows может спросить разрешение)…");

            if (!FirewallHelper.RuleExists(port) && _openFirewall is not null)
            {
                var opened = await _openFirewall(port);
                Log(opened
                    ? $"✓ Порт {port} открыт"
                    : $"⚠ Порт {port} не открыт. Откройте «Пульт админа» → «🛡 Открыть порт», " +
                      "или выполните от администратора:\n   " + FirewallHelper.ManualCommand(port));
            }
            Log("");

            // 3. Tunnel. Started here on purpose: a user who sets it up in the admin panel
            //    later usually never notices it failed, because nothing tests it.
            StepText.Text = "3. Доступ из интернета";
            if (_startTunnel is not null)
            {
                Log("· Поднимаю туннель для мобильного интернета…");
                try
                {
                    _publicUrl = await _startTunnel() ?? "";
                    Log(_publicUrl.Length > 0
                        ? $"✓ Туннель работает: {_publicUrl}"
                        : "⚠ Туннель не поднялся — телефон подключится только по домашнему Wi-Fi. " +
                          "Причина в журнале «Пульта админа».");
                }
                catch (Exception ex)
                {
                    Log("⚠ Туннель: " + ex.Message);
                }
            }
            else
            {
                Log("· Туннель не требуется в этом режиме.");
            }
            Log("");

            // 4. The mini-test: same questions the phone asks, answered on the PC.
            StepText.Text = "4. Проверка подключения";
            var lan = NetworkAddresses.LocalDetailed()
                .Where(a => a.IsWifiOrEthernet)
                .Select(a => a.Address)
                .ToList();

            _lastCheck = await SetupCheckCore.RunAsync(
                port,
                serverExpected: true,
                publicUrl: _publicUrl,
                lanAddresses: lan,
                probe: ProbeAsync);

            foreach (var line in _lastCheck.Lines) Log(line);
            Log("");
            SummaryText.Text = _lastCheck.Summary;
            SummaryBorder.Background = (Brush)Application.Current.Resources[
                _lastCheck.AllOk ? "AccentSoftBrush" : "BgPanelBrush"];

            if (_publicUrl.Length > 0 && _lastCheck.AllOk)
            {
                var code = ConnectionCore.BuildJoinCode(_publicUrl, _settings.Server.InviteCode);
                SummaryText.Text += "\n\nКод для телефона (Настройки → «Вставить код»):\n" + code;
            }

            Succeeded = _lastCheck.AllOk;
            DoneButton.IsEnabled = true;
            DoneButton.Content = Succeeded ? "Начать работу" : "Продолжить всё равно";

            // Suggest the automated fix for the first broken step.
            if (_lastCheck.Steps.FirstOrDefault(s => s.State == SetupStepState.Failed)?.Action is { } action)
            {
                FixButton.Content = action;
                FixButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            OliLog.Error("SETUP", ex.ToString());
            Log("⚠ Ошибка настройки: " + ex.Message);
            SummaryText.Text = "Настройка прервалась. Нажмите «Повторить» — уже сделанное повторять не нужно.";
            DoneButton.IsEnabled = true;
            DoneButton.Content = "Продолжить всё равно";
        }
        finally
        {
            RetryButton.IsEnabled = true;
            _running = false;
        }
    }

    private async Task<IReadOnlyList<ComponentReport>> EnsureComponentsAsync(IProgress<string> progress)
    {
        var reports = new List<ComponentReport>();
        foreach (var spec in ComponentInstaller.Known)
        {
            var report = await _installer.EnsureAsync(spec);
            progress.Report(report.Line);
            reports.Add(report);
        }
        progress.Report(ComponentInstaller.Summary(reports));
        return reports;
    }

    /// <summary>Probes one address through the shared client, so the PC and the phone agree.</summary>
    private async Task<bool> ProbeAsync(string url)
    {
        using var client = new OLI.Engine.OliServerClient();
        var result = await client.ProbeAsync(url);
        // EngineDown still means "the address works"; the phone can log in and only chat is
        // blocked, which is a different (and clearly reported) problem.
        return result.State is ConnectionState.Ready or ConnectionState.EngineDown;
    }

    private void Log(string line)
    {
        LogText.Text = LogText.Text.Length == 0 ? line : LogText.Text + Environment.NewLine + line;
    }

    // ------------------------------------------------------------------ buttons

    private async void Retry_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        var step = _lastCheck?.Steps.FirstOrDefault(s => s.State == SetupStepState.Failed);
        if (step is null) return;

        FixButton.IsEnabled = false;
        try
        {
            if (step.Action == "Открыть порт" && _openFirewall is not null)
            {
                Log($"· Открываю порт {_settings.Server.Port}…");
                var opened = await _openFirewall(_settings.Server.Port);
                Log(opened ? "✓ Порт открыт" : "⚠ Порт всё ещё закрыт — нужен запуск от администратора.");
            }
            else if (step.Action == "Запустить сервер")
            {
                await _startServer();
            }
            else if (step.Action == "Журнал туннеля")
            {
                MessageBox.Show(this,
                    "Журнал туннеля есть в «Пульт админа» → «Журнал».\n\n" +
                    "Частые причины: нет интернета на ПК, провайдер блокирует tunnel, " +
                    "не скачался cloudflared.exe.",
                    "Туннель", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this, step.Detail, "Что делать", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            FixButton.IsEnabled = true;
            // Re-run only the checks: components are already in place.
            StepText.Text = "4. Проверка подключения";
            var port = _settings.Server.Port;
            var lan = NetworkAddresses.LocalDetailed().Where(a => a.IsWifiOrEthernet)
                .Select(a => a.Address).ToList();
            _lastCheck = await SetupCheckCore.RunAsync(port, true, _publicUrl, lan, ProbeAsync);
            foreach (var line in _lastCheck.Lines) Log(line);
            SummaryText.Text = _lastCheck.Summary;
            Succeeded = _lastCheck.AllOk;
            DoneButton.Content = Succeeded ? "Начать работу" : "Продолжить всё равно";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Whatever way the window closes, the temporary server must let go of the port —
        // otherwise the chat window can never start its own server ("address already in use").
        if (!_portReleased)
        {
            // App.EndSetup() blocks until the temporary server is torn down. It must not run
            // on the UI thread: a close that waits on the dispatcher looks like a hung app.
            _portReleased = true;
            _ = Task.Run(App.EndSetup);
        }
        base.OnClosed(e);
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        // A double click used to run this whole method twice: two Save calls, two port
        // releases and a "DialogResult can be set only after Window is created and shown"
        // crash that took the app down right after setup. One run, then buttons stay off.
        if (_finishing) return;
        _finishing = true;
        DoneButton.IsEnabled = false;
        try
        {
            _settings.SetupCompleted = true;
            _settings.SetupVersion = AppVersion.Current;
            SettingsStore.Save(_settings);

            // Free port 6767 before the chat window starts its own server: two servers on one
            // port is the classic "сервер не запустился" after a successful setup.
            await App.EndSetupAsync();
            _portReleased = true;

            if (IsVisible) DialogResult = Succeeded;
            Close();
        }
        catch (Exception ex)
        {
            // Never leave the user with a closed wizard and no app: report and stay open.
            OliLog.Error("SETUP", "Завершение настройки: " + ex);
            _finishing = false;
            DoneButton.IsEnabled = true;
            SummaryText.Text = "Не удалось завершить настройку: " + ex.Message;
        }
    }
}
