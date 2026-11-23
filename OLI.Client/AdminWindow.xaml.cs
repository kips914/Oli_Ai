using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using OLI.Core;
using OLI.Server;
using Localization = OLI.Core.Localization;

namespace OLI.Client;

/// <summary>
/// The admin remote: starts/stops the local OLI server and manages accounts
/// (quota, blocking, password reset). It talks to the server over loopback HTTP,
/// so the same window could later administer a remote server by changing the URL.
/// </summary>
public partial class AdminWindow : Window
{
    private readonly ServerHost _host;
    private readonly HttpClient _http;
    private readonly TunnelSettings _tunnelSettings;
    private readonly TunnelManager _tunnel = new();
    private List<AdminUser> _users = [];

    public AdminWindow(ServerHost host, TunnelSettings? tunnelSettings = null)
    {
        InitializeComponent();
        _host = host;
        _tunnelSettings = tunnelSettings ?? new TunnelSettings();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Options.Port}"), Timeout = TimeSpan.FromSeconds(15) };
        _host.StatusChanged += OnServerStatusChanged;
        _tunnel.Changed += OnTunnelChanged;
        Loaded += async (_, _) =>
        {
            RefreshServerState();
            RefreshTunnelState();
            await LoadUsersAsync();
            if (_tunnelSettings.Enabled && _host.IsRunning) await StartTunnelAsync();
        };
        Closed += (_, _) =>
        {
            _host.StatusChanged -= OnServerStatusChanged;
            _tunnel.Changed -= OnTunnelChanged;
            _tunnel.Dispose();
        };
    }

    private void OnServerStatusChanged(string status) => Dispatcher.Invoke(() =>
    {
        ServerDetailsText.Text = status;
        RefreshServerState();
        _ = LoadUsersAsync();
    });

    private void RefreshServerState()
    {
        var running = _host.IsRunning;
        ServerDot.Fill = (System.Windows.Media.Brush)Application.Current.Resources[running ? "OnlineBrush" : "OfflineBrush"];
        ServerStatusText.Text = running ? "Сервер: работает" : "Сервер: остановлен";
        StartServerButton.IsEnabled = !running;
        StopServerButton.IsEnabled = running;

        var port = _host.Options.Port;
        var portOpen = FirewallHelper.RuleExists(port);
        var all = OLI.Core.NetworkAddresses.LocalDetailed()
            .Where(a => a.IsWifiOrEthernet)
            .Select(a => a.Address)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct()
            .ToList();
        var addresses = all.Count > 0 ? string.Join("  ·  ", all.Select(a => $"http://{a}:{port}")) : "—";

        ServerAddressText.Text = running
            ? $"Порт {port} · адрес для телефона по Wi-Fi: {addresses}" +
              (portOpen
                  ? "  · брандмауэр: порт открыт"
                  : $"  · ⚠ брандмауэр: порт {port} ЗАКРЫТ — телефон по Wi-Fi не увидит сервер, нажмите «🛡 Открыть порт»")
            : $"Порт {port}. Адрес для телефона появится после запуска сервера.";

        if (running && string.IsNullOrWhiteSpace(ServerDetailsText.Text))
            ServerDetailsText.Text = $"API: {_host.BaseUrl} · пользователей: {_host.RegisteredUsers}";
    }

    // ------------------------------------------------------------- server

    private async void StartServer_Click(object sender, RoutedEventArgs e)
    {
        StartServerButton.IsEnabled = false;
        AdminMessageText.Text = "Запускаю сервер...";
        try
        {
            await _host.StartAsync();
            AdminMessageText.Text = "Сервер запущен.";
        }
        catch (Exception ex)
        {
            OliLog.Error("SERVER", ex.ToString());
            AdminMessageText.Text = "⚠ " + ex.Message;
        }
        finally
        {
            RefreshServerState();
            await LoadUsersAsync();
        }
    }

    private async void StopServer_Click(object sender, RoutedEventArgs e)
    {
        AdminMessageText.Text = "Останавливаю сервер...";
        await _host.StopAsync();
        AdminMessageText.Text = "Сервер остановлен.";
        RefreshServerState();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadUsersAsync();

    // ------------------------------------------------------------- tunnel

    private async Task StartTunnelAsync()
    {
        if (!_host.IsRunning)
        {
            TunnelStatusText.Text = "Интернет-доступ: сначала запустите сервер.";
            return;
        }

        TunnelStartButton.IsEnabled = false;
        if (!FirewallHelper.RuleExists(_host.Options.Port))
            TunnelLogText.Text = "Порт закрыт брандмауэром — туннель всё равно сработает, но по Wi-Fi телефон не подключится.";

        var ok = await _tunnel.StartAsync(_tunnelSettings, _host.Options.Port);
        RefreshTunnelState();
        TunnelStartButton.IsEnabled = !_tunnel.IsRunning;
        if (ok)
        {
            _tunnelSettings.Enabled = true;
            _tunnelSettings.LastPublicUrl = _tunnel.PublicUrl;
            AdminMessageText.Text = "Телефону нужен только код подключения ниже — адрес вводить не нужно.";
        }
        else
        {
            AdminMessageText.Text = "⚠ " + (_tunnel.LastError.Length > 0 ? _tunnel.LastError : "Туннель не запустился.");
            TunnelLogText.Visibility = Visibility.Visible;
        }
    }

    private void OnTunnelChanged() => Dispatcher.Invoke(RefreshTunnelState);

    private void RefreshTunnelState()
    {
        if (TunnelDot is null) return;
        var running = _tunnel.IsRunning && _tunnel.PublicUrl.Length > 0;
        TunnelDot.Fill = (System.Windows.Media.Brush)Application.Current.Resources[running ? "OnlineBrush" : "OfflineBrush"];
        TunnelStatusText.Text = running ? "Интернет-доступ: работает" : "Интернет-доступ: выключен";
        TunnelAddressText.Text = running
            ? _tunnel.PublicUrl + "  ·  этот адрес меняется при каждом запуске, поэтому используйте код подключения"
            : _tunnel.StatusLine;

        var url = running ? _tunnel.PublicUrl : _tunnelSettings.LastPublicUrl;
        TunnelPairText.Text = string.IsNullOrWhiteSpace(url)
            ? ""
            : "Код подключения для телефона (в приложении: Настройки → «Вставить код»):\n" + BuildPairCode(url);

        if (TunnelLogText.Visibility == Visibility.Visible)
        {
            var lines = _tunnel.Log.TakeLast(3).ToList();
            TunnelLogText.Text = lines.Count == 0 ? "Журнал пуст." : string.Join(Environment.NewLine, lines);
        }

        TunnelStopButton.IsEnabled = _tunnel.IsRunning;
        TunnelStartButton.IsEnabled = !_tunnel.IsRunning;
    }

    private string BuildPairCode(string url) =>
        TunnelManager.BuildPairCode(url, _host.Options.InviteCode);

    private async void TunnelStart_Click(object sender, RoutedEventArgs e)
    {
        AdminMessageText.Text = "Запускаю туннель (первый запуск cloudflared может занять 10–20 секунд)…";
        await StartTunnelAsync();
    }

    private async void TunnelStop_Click(object sender, RoutedEventArgs e)
    {
        await _tunnel.StopAsync();
        _tunnelSettings.Enabled = false;
        TunnelPairText.Text = "";
        RefreshTunnelState();
        AdminMessageText.Text = "Интернет-доступ выключен: телефон снова подключается только по Wi-Fi.";
    }

    private void TunnelLog_Click(object sender, RoutedEventArgs e)
    {
        var showing = TunnelLogText.Visibility != Visibility.Visible;
        TunnelLogText.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        TunnelLogButton.Content = showing ? "Скрыть журнал" : "Журнал";
        RefreshTunnelState();
    }

    private void TunnelCopy_Click(object sender, RoutedEventArgs e)
    {
        var url = _tunnel.IsRunning ? _tunnel.PublicUrl : _tunnelSettings.LastPublicUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            AdminMessageText.Text = "Сначала включите интернет-доступ.";
            return;
        }
        var code = BuildPairCode(url);
        try
        {
            Clipboard.SetText(code);
            AdminMessageText.Text = "Код подключения скопирован. В приложении: Настройки → «Вставить код».";
        }
        catch (Exception ex)
        {
            AdminMessageText.Text = "⚠ " + ex.Message;
        }
    }

    // ------------------------------------------------------------- users

    private async Task LoadUsersAsync()
    {
        if (!_host.IsRunning)
        {
            _users = [];
            UsersGrid.ItemsSource = _users;
            return;
        }

        try
        {
            using var response = await _http.GetAsync("/api/admin/users");
            if (!response.IsSuccessStatusCode)
            {
                AdminMessageText.Text = $"Не удалось получить список: HTTP {(int)response.StatusCode}";
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var list = new List<AdminUser>();
            foreach (var user in doc.RootElement.GetProperty("users").EnumerateArray())
            {
                list.Add(new AdminUser
                {
                    Id = Str(user, "id"),
                    UserName = Str(user, "userName"),
                    CreatedText = Date(user, "createdAt"),
                    LastLoginText = Date(user, "lastLoginAt"),
                    UsedBytes = user.TryGetProperty("used", out var used) ? used.GetInt64() : 0,
                    QuotaBytes = user.TryGetProperty("quota", out var quota) ? quota.GetInt64() : 0,
                    Disabled = user.TryGetProperty("disabled", out var disabled) && disabled.GetBoolean(),
                });
            }

            _users = list;
            UsersGrid.ItemsSource = _users;
            AdminMessageText.Text = $"Пользователей: {_users.Count}. Пароли хранятся как PBKDF2-хеш (только пользователь знает свой пароль).";
        }
        catch (Exception ex)
        {
            AdminMessageText.Text = "⚠ " + ex.Message;
        }
    }

    private async void SetQuota_Click(object sender, RoutedEventArgs e)
    {
        var user = UsersGrid.SelectedItem as AdminUser;
        if (user is null) { AdminMessageText.Text = "Выберите пользователя."; return; }

        var dialog = new QuotaDialog(user.UserName, user.QuotaBytes) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var ok = await PostAsync("/api/admin/quota", new { accountId = user.Id, quotaBytes = dialog.QuotaBytes });
        AdminMessageText.Text = ok ? $"Квота {user.UserName} = {Human(dialog.QuotaBytes)}." : "Не удалось изменить квоту.";
        await LoadUsersAsync();
    }

    private async void ToggleUser_Click(object sender, RoutedEventArgs e)
    {
        var user = UsersGrid.SelectedItem as AdminUser;
        if (user is null) { AdminMessageText.Text = "Выберите пользователя."; return; }

        var disable = !user.Disabled;
        if (disable && MessageBox.Show(this, $"Заблокировать {user.UserName}? Все его токены будут отозваны.",
                "OLI", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var ok = await PostAsync("/api/admin/disable", new { accountId = user.Id, disabled = disable });
        AdminMessageText.Text = ok
            ? $"{(disable ? "Заблокирован" : "Разблокирован")}: {user.UserName}."
            : "Не удалось изменить статус.";
        await LoadUsersAsync();
    }

    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        var user = UsersGrid.SelectedItem as AdminUser;
        if (user is null) { AdminMessageText.Text = "Выберите пользователя."; return; }

        var dialog = new PasswordResetDialog(user.UserName) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var ok = await PostAsync("/api/admin/reset-password", new { accountId = user.Id, newPassword = dialog.NewPassword });
        AdminMessageText.Text = ok
            ? $"Пароль {user.UserName} изменён. Старый пароль восстановить невозможно — в базе только хеш."
            : "Не удалось сбросить пароль (проверьте требования к паролю).";
        await LoadUsersAsync();
    }

    private async void DeleteUser_Click(object sender, RoutedEventArgs e)
    {
        var user = UsersGrid.SelectedItem as AdminUser;
        if (user is null) { AdminMessageText.Text = "Выберите пользователя."; return; }

        if (MessageBox.Show(this,
                $"Удалить {user.UserName} и ВСЕ его файлы ({Human(user.UsedBytes)})? Это необратимо.",
                "OLI", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var ok = await PostAsync("/api/admin/delete", new { accountId = user.Id, disabled = true });
        AdminMessageText.Text = ok ? $"Удалён: {user.UserName}." : "Не удалось удалить пользователя.";
        await LoadUsersAsync();
    }

    private void OpenStorage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_host.Options.DataRoot}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AdminMessageText.Text = "⚠ " + ex.Message;
        }
    }

    private async Task<bool> PostAsync(string url, object payload)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(url, payload);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            AdminMessageText.Text = "⚠ " + ex.Message;
            return false;
        }
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

    private static string Date(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTimeOffset(out var date)
            ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "—";

    private static string Human(long bytes) => OLI.Workspace.TextExtractors.FormatSize(bytes);

    public sealed class AdminUser
    {
        public string Id { get; set; } = "";
        public string UserName { get; set; } = "";
        public string CreatedText { get; set; } = "";
        public string LastLoginText { get; set; } = "";
        public long UsedBytes { get; set; }
        public long QuotaBytes { get; set; }
        public bool Disabled { get; set; }
        public string QuotaText => Human(QuotaBytes);
        public string UsageText => $"{Human(UsedBytes)} / {Human(QuotaBytes)}";
        public string StatusText => Disabled ? "заблокирован" : "активен";
    }
}
