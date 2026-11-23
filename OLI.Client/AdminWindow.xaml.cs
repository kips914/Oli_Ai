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
    private List<AdminUser> _users = [];

    public AdminWindow(ServerHost host)
    {
        InitializeComponent();
        _host = host;
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Options.Port}"), Timeout = TimeSpan.FromSeconds(15) };
        _host.StatusChanged += OnServerStatusChanged;
        Loaded += async (_, _) => { RefreshServerState(); await LoadUsersAsync(); };
        Closed += (_, _) => _host.StatusChanged -= OnServerStatusChanged;
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

        ServerAddressText.Text = running
            ? $"Адрес для телефона: http://{OLI.Core.NetworkAddresses.Preferred() ?? "—"}:{_host.Options.Port}"
            : $"Порт {_host.Options.Port}. Адрес для телефона появится после запуска сервера.";

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
