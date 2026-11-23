using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OLI.Agent;
using OLI.Core;
using Localization = OLI.Core.Localization;
using OLI.Engine;
using OLI.Memory;
using OLI.Server;
using OLI.Telegram;
using OLI.Web;
using OLI.Workspace;

namespace OLI.Client;

public partial class MainWindow : Window
{
    private readonly OliSettings _settings = SettingsStore.Load();
    private readonly LlamaClient _llama;
    private readonly WebSearch _web;
    private readonly MemoryStore _memory;
    private readonly UserProfileStore _profile;
    private readonly WorkspaceEngine _workspace;
    private readonly AgentSession _session;
    private readonly GitHubUpdateService _updateService = new();
    private readonly ObservableCollection<ChatMessage> _display = [];
    private readonly ObservableCollection<AllowedSite> _allowedSites;
    private readonly List<WorkspaceEntry> _workspaceEntries = [];

    private LanBeacon? _beacon;
    private readonly OliServerClient _phoneClient = new();
    private TelegramBot? _bot;
    private ServerHost? _serverHost;
    private ChatMessage? _streamingMessage;
    private string _workspaceCwd = "";
    private bool _busy;
    private bool _initializing = true;

    public MainWindow()
    {
        InitializeComponent();
        AppPaths.EnsureDirectories();
        Localization.Initialize(_settings.Language);
        App.ApplyTheme(_settings.Theme);
        Title = $"OLI {AppVersion.Current}";
        VersionText.Text = $"v{AppVersion.Current}";

        _settings.MaxTokens = _settings.Mode == GenerationMode.Performance ? _settings.PerformanceMaxTokens : _settings.FastMaxTokens;
        _llama = new LlamaClient(_settings.LlamaBaseUrl);
        _web = new WebSearch(_settings.SearchTimeoutSeconds, _settings.SearchProvider);
        _memory = new MemoryStore(AppPaths.MemoryFile);
        _profile = new UserProfileStore(AppPaths.ProfileFile);
        _workspace = new WorkspaceEngine(
            AppPaths.WorkspaceRoot,
            AppPaths.WorkspaceFilesRoot,
            AppPaths.WorkspaceArchiveRoot,
            _settings.WorkspaceQuotaBytes,
            _settings.WorkspaceMaxFileBytes,
            _settings.AllowCommandExecution,
            _settings.AllowedCommands);
        _session = new AgentSession(_settings, _llama, _web, _memory, _profile, _workspace);

        _allowedSites = new ObservableCollection<AllowedSite>(_settings.AllowedSites);
        SitesList.ItemsSource = _allowedSites;
        ChatList.ItemsSource = _display;

        OliLog.EntryAdded += OnLogEntry;
        foreach (var entry in OliLog.Snapshot()) AddConsoleEntry(entry);

        UiLocalization.ApplyMain(this);
        ApplySettingsToUi();
        _initializing = false;
        Nav_Checked(NavChat, new RoutedEventArgs());
        _workspaceCwd = "";
        RefreshWorkspace();

        Loaded += async (_, _) =>
        {
            await RefreshStatusAsync();
            _ = CheckForUpdatesOnStartupAsync();
            if (_settings.Telegram.Enabled && !string.IsNullOrWhiteSpace(_settings.Telegram.BotToken))
                await StartBotAsync(silent: true);
            if (_settings.Server.AutoStartWithApp && _settings.Server.Enabled)
                await StartServerAsync(silent: true);
            else if (_settings.Server.AutoStartWithApp && !_settings.Server.Enabled)
                OliLog.Info("SERVER", "Сервер не запускается автоматически: его выключили кнопкой «Остановить».");
            RefreshServerUi();
        };
        Closed += (_, _) =>
        {
            OliLog.EntryAdded -= OnLogEntry;
            _ = _bot?.DisposeAsync();
            if (_serverHost is not null) _ = _serverHost.DisposeAsync();
            _beacon?.Dispose();
            _phoneClient.Dispose();
            _llama.Dispose();
            _web.Dispose();
            _updateService.Dispose();
        };
    }

    // ================================================================ state

    private void ApplySettingsToUi()
    {
        WebSearchMasterToggle.IsChecked = _settings.WebSearchEnabled;
        OpenWebToggle.IsChecked = _settings.WebSearchOpenWeb;
        SearchChip.IsChecked = _settings.WebSearchEnabled;
        WorkspaceChip.IsChecked = _settings.WorkspaceEnabled;
        ResultLimitSlider.Value = Math.Clamp(_settings.WebSearchResultLimit, 3, 15);
        ResultLimitText.Text = _settings.WebSearchResultLimit.ToString();
        BlockedDomainsBox.Text = string.Join(", ", _settings.WebSearchBlockedDomains);
        ModelText.Text = _settings.ModelDisplayName;
        ModeText.Text = _settings.Mode == GenerationMode.Performance ? "🧠 PERFORMANCE" : "⚡ FAST";

        SearchProviderCombo.Items.Clear();
        SearchProviderCombo.Items.Add(Localization.Current == OliLanguage.English ? "Auto (all providers)" : "Авто (все провайдеры)");
        SearchProviderCombo.Items.Add("DuckDuckGo");
        SearchProviderCombo.Items.Add("Bing");
        SearchProviderCombo.Items.Add("Mojeek");
        SearchProviderCombo.Items.Add("Wikipedia");
        SearchProviderCombo.SelectedIndex = ProviderToIndex(_settings.SearchProvider);

        ServerAutoStartToggle.IsChecked = _settings.Server.AutoStartWithApp;
        ServerRegistrationToggle.IsChecked = _settings.Server.AllowRegistration;
        ServerWorkspaceToggle.IsChecked = _settings.Server.WorkspaceToolsEnabled;
        ServerPortBox.Text = _settings.Server.Port.ToString();
        ServerQuotaSlider.Value = Math.Clamp(_settings.Server.UserQuotaBytes / 1024 / 1024, 64, 10240);
        ServerQuotaText.Text = TextExtractors.FormatSize(_settings.Server.UserQuotaBytes);
        ServerInviteBox.Text = _settings.Server.InviteCode;
        ServerAdminTokenBox.Text = _settings.Server.AdminToken;

        TelegramEnabledToggle.IsChecked = _settings.Telegram.Enabled;
        TelegramTokenBox.Password = _settings.Telegram.BotToken;
        TelegramRelayBox.Text = _settings.Telegram.RelayBaseUrl;
        TelegramRelaySecretBox.Text = _settings.Telegram.RelaySecret;
        TelegramProxyBox.Text = _settings.Telegram.ProxyUrl;
        TelegramDohToggle.IsChecked = _settings.Telegram.DohFallback;
        TelegramAllowedBox.Text = _settings.Telegram.AllowedUserIds;
        TelegramAllowCommandsToggle.IsChecked = _settings.Telegram.AllowCommands;
        TelegramModeCombo.Items.Clear();
        TelegramModeCombo.Items.Add("0 — Cloudflare-релей (рекомендуется)");
        TelegramModeCombo.Items.Add("1 — Прокси / SOCKS5");
        TelegramModeCombo.Items.Add("2 — DNS-over-HTTPS");
        TelegramModeCombo.Items.Add("3 — Прямое подключение");
        TelegramModeCombo.SelectedIndex = DetectTelegramMode();
        UpdateTelegramFieldState();
    }

    private int DetectTelegramMode()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Telegram.RelayBaseUrl)) return 0;
        if (!string.IsNullOrWhiteSpace(_settings.Telegram.ProxyUrl)) return 1;
        return _settings.Telegram.DohFallback ? 2 : 3;
    }

    private void UpdateTelegramFieldState()
    {
        TelegramRelayBox.IsEnabled = TelegramModeCombo.SelectedIndex == 0 || TelegramModeCombo.SelectedIndex == -1;
        TelegramProxyBox.IsEnabled = TelegramModeCombo.SelectedIndex == 1;
        TelegramDohToggle.IsEnabled = TelegramModeCombo.SelectedIndex == 2;
    }

    private void TelegramMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing) UpdateTelegramFieldState();
    }

    /// <summary>Persists settings, but leaves the port/token alone while the server is running.</summary>
    private void PersistSettings()
    {
        var serverRunning = _serverHost?.IsRunning == true;

        _settings.WebSearchEnabled = WebSearchMasterToggle.IsChecked == true;
        _settings.WebSearchOpenWeb = OpenWebToggle.IsChecked == true;
        _settings.WorkspaceEnabled = WorkspaceChip.IsChecked == true;
        _settings.WebSearchResultLimit = (int)ResultLimitSlider.Value;
        _settings.SearchProvider = IndexToProvider(SearchProviderCombo.SelectedIndex);
        _settings.WebSearchBlockedDomains = BlockedDomainsBox.Text
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _settings.Server.AutoStartWithApp = ServerAutoStartToggle.IsChecked == true;
        _settings.Server.AllowRegistration = ServerRegistrationToggle.IsChecked == true;
        _settings.Server.WorkspaceToolsEnabled = ServerWorkspaceToggle.IsChecked == true;
        _settings.Server.UserQuotaBytes = (long)ServerQuotaSlider.Value * 1024 * 1024;
        if (!serverRunning &&
            int.TryParse(ServerPortBox.Text.Trim(), out var serverPort) && serverPort is > 1024 and < 65536)
            _settings.Server.Port = serverPort;
        _settings.Server.InviteCode = ServerInviteBox.Text.Trim();
        _settings.Server.AdminToken = ServerAdminTokenBox.Text.Trim();
        // Keep the desktop app's 1 GB workspace setting in sync with the server quota default.
        _settings.WorkspaceQuotaBytes = _settings.Server.UserQuotaBytes;
        // Remember that the server was running, so it comes back on the next launch.
        if (serverRunning) _settings.Server.Enabled = true;

        _settings.Telegram.Enabled = TelegramEnabledToggle.IsChecked == true;
        _settings.Telegram.BotToken = TelegramTokenBox.Password.Trim();
        _settings.Telegram.RelayBaseUrl = TelegramRelayBox.Text.Trim();
        _settings.Telegram.RelaySecret = TelegramRelaySecretBox.Text.Trim();
        _settings.Telegram.ProxyUrl = TelegramProxyBox.Text.Trim();
        _settings.Telegram.DohFallback = TelegramDohToggle.IsChecked == true;
        _settings.Telegram.AllowedUserIds = TelegramAllowedBox.Text.Trim();
        _settings.Telegram.AllowCommands = TelegramAllowCommandsToggle.IsChecked == true;

        _session.ApplySearchSettings();
        SettingsStore.Save(_settings);
    }

    // ================================================================ chat

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text) || _busy) return;

        _busy = true;
        SendButton.IsEnabled = false;
        InputBox.IsEnabled = false;
        InputBox.Clear();
        _display.Add(new ChatMessage("user", text));
        ScrollToEnd();
        OliLog.Info("CHAT", $"Запрос пользователя: {Math.Min(text.Length, 120)} символов");

        var progress = new Progress<AgentEvent>(OnAgentEvent);
        try
        {
            PersistSettings();
            var answer = await _session.SendAsync(text, progress);

            foreach (var file in answer.FilesToSend)
            {
                _display.Add(new ChatMessage("system", $"📤 Файл готов: {RelativeToWorkspace(file)}"));
            }
            ScrollToEnd();
            StatusText.Text = Localization.T("status.ready");
            RefreshWorkspace();
        }
        catch (Exception ex)
        {
            OliLog.Error("CHAT", ex.ToString());
            _display.Add(new ChatMessage("assistant", Localization.T("error.prefix", ex.Message)));
            StatusText.Text = Localization.T("status.error");
        }
        finally
        {
            _busy = false;
            SendButton.IsEnabled = true;
            InputBox.IsEnabled = true;
            InputBox.Focus();
            _ = RefreshStatusAsync();
        }
    }

    private void OnAgentEvent(AgentEvent e)
    {
        switch (e.Kind)
        {
            case AgentEventKind.Status:
                StatusText.Text = "OLI: " + e.Text;
                break;
            case AgentEventKind.Search:
                ResetStreaming();
                _display.Add(new ChatMessage("system", Localization.T("searching", e.Text)));
                ScrollToEnd();
                break;
            case AgentEventKind.Page:
                ResetStreaming();
                _display.Add(new ChatMessage("system", $"📄 Читаю {Shorten(e.Text, 70)}"));
                ScrollToEnd();
                break;
            case AgentEventKind.Tool:
                ResetStreaming();
                _display.Add(new ChatMessage("system", e.Text));
                ScrollToEnd();
                break;
            case AgentEventKind.File:
                _display.Add(new ChatMessage("system", $"📎 {e.Text}"));
                ScrollToEnd();
                break;
            case AgentEventKind.Error:
                _display.Add(new ChatMessage("system", "⚠ " + e.Text));
                ScrollToEnd();
                break;
            case AgentEventKind.Answer:
                if (_streamingMessage is not null)
                {
                    ReplaceLastMessage(e.Text);
                    _streamingMessage = null;
                }
                else
                {
                    _display.Add(new ChatMessage("assistant", e.Text));
                }
                ScrollToEnd();
                break;
            case AgentEventKind.Delta:
                if (_streamingMessage is null)
                {
                    _display.Add(new ChatMessage("assistant", e.Text));
                    _streamingMessage = _display[^1];
                }
                else
                {
                    ReplaceLastMessage(_streamingMessage.Content + e.Text);
                }
                ScrollToEnd();
                break;
        }
    }

    /// <summary>
    /// Drops the bubble of an intermediate (tool/search) generation so the next
    /// streamed answer starts fresh instead of appending to stale text.
    /// </summary>
    private void ResetStreaming()
    {
        if (_streamingMessage is null) return;
        if (_display.Count > 0 && ReferenceEquals(_display[^1], _streamingMessage)) _display.RemoveAt(_display.Count - 1);
        _streamingMessage = null;
    }

    private void ReplaceLastMessage(string content)
    {
        if (_display.Count == 0) _display.Add(new ChatMessage("assistant", content));
        else _display[^1] = new ChatMessage(_display[^1].Role, content);
        ScrollToEnd();
    }

    private string RelativeToWorkspace(string path)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(_workspace.Root, path);
        return Path.GetRelativePath(_workspace.Root, full).Replace('\\', '/');
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _session.Reset();
        _display.Clear();
        _display.Add(new ChatMessage("system", "🆕 Новый диалог. История очищена — память о вас сохранена."));
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers == ModifierKeys.Shift) return;
        e.Handled = true;
        Send_Click(sender, new RoutedEventArgs());
    }

    private void ScrollToEnd() => Dispatcher.InvokeAsync(() => ChatScroll.ScrollToEnd(),
        System.Windows.Threading.DispatcherPriority.Background);

    // ================================================================ status

    private async Task RefreshStatusAsync()
    {
        var alive = await _llama.IsAliveAsync();
        if (alive)
        {
            // Say which engine answers and which model it actually serves.
            var served = await _llama.GetServedModelAsync();
            var engine = _llama.IsOllama == true ? "Ollama" : "llama.cpp";
            StatusText.Text = served is { Length: > 0 }
                ? $"OLI: {engine} · {served}"
                : Localization.T("status.connected");
            StatusDot.Fill = (Brush)Application.Current.Resources["OnlineBrush"];
            StatusText.ToolTip = $"{engine}: {_settings.LlamaBaseUrl}";
            return;
        }

        // Nothing answers on the configured port — tell the user what to start, and where.
        StatusText.Text = $"OLI: движок не отвечает на {_settings.LlamaBaseUrl}";
        StatusText.ToolTip =
            "Запустите движок модели: ollama serve (порт 11434) или scripts\\start-llama.ps1 (порт 8080). " +
            "Адрес меняется в настройках: «Настройки» → движок.";
        StatusDot.Fill = (Brush)Application.Current.Resources["OfflineBrush"];
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (!_settings.CheckForUpdatesOnStartup) return;
        try
        {
            var result = await _updateService.CheckAsync(_settings.GitHubRepositoryUrl);
            if (!result.Success)
            {
                OliLog.Debug("UPDATE", result.Error ?? "нет связи с GitHub");
                return;
            }
            if (!result.UpdateAvailable)
            {
                OliLog.Info("UPDATE", Localization.T("update.latest", result.CurrentVersion));
                return;
            }
            OliLog.Info("UPDATE", Localization.T("update.available", result.LatestVersion));
        }
        catch (Exception ex)
        {
            OliLog.Warning("UPDATE", Localization.T("update.error", ex.Message));
        }
    }

    // ================================================================ navigation

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (ChatPage is null) return;

        ChatPage.Visibility = NavChat.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        WorkspacePage.Visibility = NavWorkspace.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        WebPage.Visibility = NavWeb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TelegramPage.Visibility = NavTelegram.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ConsolePage.Visibility = NavConsole.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AdminPage.Visibility = NavAdmin.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ComposerBorder.Visibility = NavChat.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavChat.IsChecked == true) { PageTitle.Text = "Чат с OLI"; PageSubtitle.Text = Localization.T("app.tagline"); }
        else if (NavWorkspace.IsChecked == true) { PageTitle.Text = "Workspace"; PageSubtitle.Text = "Файлы, архивы и команды — всё внутри лимита 1 ГБ"; RefreshWorkspace(); }
        else if (NavWeb.IsChecked == true) { PageTitle.Text = "Поиск в интернете"; PageSubtitle.Text = "Провайдеры, область поиска и чёрный список"; }
        else if (NavTelegram.IsChecked == true) { PageTitle.Text = "Telegram-бот"; PageSubtitle.Text = "Обход блокировок: релей, прокси, DoH"; }
        else if (NavConsole.IsChecked == true) { PageTitle.Text = "Консоль"; PageSubtitle.Text = "Живые логи OLI"; }
        else { PageTitle.Text = "Пульт администратора"; PageSubtitle.Text = "Сервер для телефона и пользователи"; RefreshServerUi(); }
    }

    private void OpenFullSettings_Click(object sender, RoutedEventArgs e)
    {
        PersistSettings();
        var previousModel = _settings.ModelGguf;
        var window = new SettingsWindow(_settings) { Owner = this };
        window.EngineUrlChanged += url =>
        {
            OliLog.Info("ENGINE", $"Адрес движка: {url}");
            _settings.LlamaBaseUrl = url;
            // LlamaClient binds its HttpClient to the old address: restart OLI to apply.
        };

        if (window.ShowDialog() != true) return;

        _settings.MaxTokens = _settings.Mode == GenerationMode.Performance
            ? _settings.PerformanceMaxTokens
            : _settings.FastMaxTokens;
        SettingsStore.Save(_settings);
        ApplySettingsToUi();
        ModelText.Text = _settings.ModelDisplayName;
        ModeText.Text = _settings.Mode == GenerationMode.Performance ? "🧠 PERFORMANCE" : "⚡ FAST";
        _session.ApplySearchSettings();
        _workspace.Configure(_settings.WorkspaceQuotaBytes, _settings.WorkspaceMaxFileBytes,
            _settings.AllowCommandExecution, _settings.AllowedCommands);
        RefreshWorkspace();

        if (!string.Equals(previousModel, _settings.ModelGguf, StringComparison.OrdinalIgnoreCase))
            RestartEngine();
        if (window.RestartRequested) RestartApplication();
    }

    /// <summary>
    /// Restarts llama-server in the background (used after switching the GGUF model,
    /// so the new -hf spec actually takes effect without closing OLI).
    /// </summary>
    private void RestartEngine()
    {
        try
        {
            var script = Path.Combine(AppPaths.InstallRoot, "scripts", "start-llama.ps1");
            if (!File.Exists(script))
            {
                OliLog.Warning("ENGINE", "start-llama.ps1 не найден рядом с OLI.exe — движок не перезапущен");
                return;
            }
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Restart")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            OliLog.Info("ENGINE", "Движок перезапускается с новой моделью");
        }
        catch (Exception ex)
        {
            OliLog.Error("ENGINE", ex.Message);
        }
    }

    private void RestartApplication()
    {
        OliLog.Info("APP", Localization.T("console.restart"));
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe)) Process.Start(exe);
        Application.Current.Shutdown();
    }

    // ================================================================ workspace

    private void RefreshWorkspace()
    {
        try
        {
            WorkspacePathText.Text = "/" + (_workspaceCwd.Length == 0 ? "" : _workspaceCwd + "/");
            var entries = _workspace.List(_workspaceCwd, 500, false);
            _workspaceEntries.Clear();
            _workspaceEntries.AddRange(entries);
            WorkspaceList.Items.Clear();
            if (_workspaceCwd.Length > 0)
                WorkspaceList.Items.Add("⬆ ..");
            foreach (var entry in entries)
            {
                WorkspaceList.Items.Add(entry.IsDirectory
                    ? $"📁 {Path.GetFileName(entry.RelativePath)}/"
                    : $"📄 {Path.GetFileName(entry.RelativePath)}    {TextExtractors.FormatSize(entry.Size)}");
            }

            var usage = _workspace.GetUsage();
            WsUsageText.Text = $"{TextExtractors.FormatSize(usage.UsedBytes)} из {TextExtractors.FormatSize(usage.QuotaBytes)}";
            WsUsageDetail.Text = $"Файлов: {usage.FileCount} · архивов: {usage.ArchiveCount} · свободно: {TextExtractors.FormatSize(usage.FreeBytes)}";
            WsUsageBar.Width = Math.Clamp(248 * usage.UsedPercent / 100.0, 0, 248);
            WsUsageBar.Background = usage.UsedPercent > 90
                ? (Brush)Application.Current.Resources["OfflineBrush"]
                : (Brush)Application.Current.Resources["AccentGradient"];

            ArchiveList.Items.Clear();
            foreach (var archive in _workspace.ListArchives())
                ArchiveList.Items.Add($"🗜 {archive.Name} · {TextExtractors.FormatSize(archive.Size)}");
            if (ArchiveList.Items.Count == 0) ArchiveList.Items.Add("(пока пусто)");
        }
        catch (Exception ex)
        {
            OliLog.Warning("WORKSPACE", ex.Message);
        }
    }

    private void WsRefresh_Click(object sender, RoutedEventArgs e) => RefreshWorkspace();

    private void WsUp_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceCwd.Length == 0) return;
        var parent = Path.GetDirectoryName(_workspaceCwd.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        _workspaceCwd = parent.Replace('\\', '/');
        RefreshWorkspace();
    }

    private void WsOpen_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedWorkspacePath();
        if (path is null) return;
        var full = Path.Combine(_workspace.Root, path);
        if (Directory.Exists(full))
        {
            _workspaceCwd = path;
            RefreshWorkspace();
            return;
        }
        try
        {
            var filePath = Path.Combine(_workspace.Root, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(filePath)) { MessageBox.Show(this, "Файл не найден.", "OLI"); return; }
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "OLI");
        }
    }

    private void WorkspaceList_DoubleClick(object sender, MouseButtonEventArgs e) => WsOpen_Click(sender, e);

    private void WsDelete_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedWorkspacePath();
        if (path is null) return;
        if (MessageBox.Show(this, $"Удалить «{path}»?", "OLI", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _workspace.Delete(path, recursive: true);
            RefreshWorkspace();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "OLI");
        }
    }

    private async void WsZip_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedWorkspacePath() ?? "";
        try
        {
            var stem = path.Length == 0 ? "workspace" : Path.GetFileName(path.TrimEnd('/'));
            var name = $"archives/{stem}-{DateTime.Now:HHmmss}.zip";
            var result = await _workspace.ZipAsync(path, name);
            OliLog.Info("WORKSPACE", result);
            MessageBox.Show(this, result, "OLI");
            RefreshWorkspace();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "OLI");
        }
    }

    private async void ZipAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = $"archives/workspace-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            var result = await _workspace.ZipAsync("", name);
            MessageBox.Show(this, result, "OLI");
            RefreshWorkspace();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "OLI");
        }
    }

    private string? SelectedWorkspacePath()
    {
        var index = WorkspaceList.SelectedIndex;
        if (index < 0) return null;
        var offset = _workspaceCwd.Length > 0 ? 1 : 0;
        if (index < offset) return null;
        var entry = _workspaceEntries[index - offset];
        return entry.RelativePath;
    }

    private void OpenWorkspaceFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.WorkspaceFilesRoot}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "OLI");
        }
    }

    // ================================================================ web / telegram settings

    private void WebToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _initializing = true;
        SearchChip.IsChecked = WebSearchMasterToggle.IsChecked == true;
        _initializing = false;
        PersistSettings();
    }

    private void SearchChip_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _initializing = true;
        WebSearchMasterToggle.IsChecked = SearchChip.IsChecked == true;
        _initializing = false;
        PersistSettings();
    }

    private void WorkspaceChip_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.WorkspaceEnabled = WorkspaceChip.IsChecked == true;
        PersistSettings();
    }

    /// <summary>Any Telegram-related toggle persists immediately (no Save button there).</summary>
    private void TelegramSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        PersistSettings();
    }

    private void SearchProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.SearchProvider = IndexToProvider(SearchProviderCombo.SelectedIndex);
        PersistSettings();
    }

    private static readonly OliSearchProvider[] ProviderOrder =
    [
        OliSearchProvider.Auto, OliSearchProvider.DuckDuckGo, OliSearchProvider.Bing,
        OliSearchProvider.Mojeek, OliSearchProvider.Wikipedia,
    ];

    private static int ProviderToIndex(OliSearchProvider provider)
    {
        var index = Array.IndexOf(ProviderOrder, provider);
        return index < 0 ? 0 : index;
    }

    private static OliSearchProvider IndexToProvider(int index) =>
        index >= 0 && index < ProviderOrder.Length ? ProviderOrder[index] : OliSearchProvider.Auto;

    private void ResultLimit_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ResultLimitText is null) return;
        ResultLimitText.Text = ((int)e.NewValue).ToString();
        if (_initializing) return;
        _settings.WebSearchResultLimit = (int)e.NewValue;
        PersistSettings();
    }

    private async void ProbeBot_Click(object sender, RoutedEventArgs e)
    {
        PersistSettings();
        ProbeBotButton.IsEnabled = false;
        TelegramStatusText.Text = "Проверяю токен и транспорт...";
        try
        {
            var result = await TelegramBot.ProbeAsync(_settings.Telegram, CancellationToken.None);
            TelegramStatusText.Text = "✅ " + result;
        }
        catch (Exception ex)
        {
            TelegramStatusText.Text = "⚠ " + ex.Message;
        }
        finally
        {
            ProbeBotButton.IsEnabled = true;
        }
    }

    private async void TelegramStart_Click(object sender, RoutedEventArgs e)
    {
        PersistSettings();
        await StartBotAsync(silent: false);
    }

    private async Task StartBotAsync(bool silent)
    {
        try
        {
            if (_bot is not null && _bot.IsRunning) return;

            _settings.Telegram.AllowCommands = TelegramAllowCommandsToggle.IsChecked == true;
            if (_workspace is null) return;

            var settings = _settings;
            // One shared WebSearch (and its HttpClient pool) for every Telegram chat.
            var botWeb = new WebSearch(settings.SearchTimeoutSeconds, settings.SearchProvider);
            _bot = new TelegramBot(settings, () =>
                new AgentSession(settings, _llama, botWeb, _memory, _profile, _workspace,
                    ownsWebSearch: false, allowCommands: settings.Telegram.AllowCommands),
                _workspace);
            _bot.StatusChanged += status => Dispatcher.Invoke(() => TelegramStatusText.Text = status);

            TelegramStatusText.Text = "Запускаю бота...";
            var result = await _bot.StartAsync(CancellationToken.None);
            TelegramStatusText.Text = "✅ " + result;
        }
        catch (Exception ex)
        {
            OliLog.Error("TELEGRAM", ex.Message);
            TelegramStatusText.Text = "⚠ " + ex.Message;
            if (!silent) MessageBox.Show(this, ex.Message, "Telegram", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void TelegramStop_Click(object sender, RoutedEventArgs e)
    {
        if (_bot is null) return;
        await _bot.DisposeAsync();
        _bot = null;
        TelegramStatusText.Text = "Бот остановлен.";
    }

    // ================================================================ server

    private void ServerSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        PersistSettings();
        RefreshServerUi();
    }

    private void ServerStart_Click(object sender, RoutedEventArgs e) => _ = StartServerAsync(silent: false);

    private void ServerFirewall_Click(object sender, RoutedEventArgs e)
    {
        var port = _settings.Server.Port;
        var error = FirewallHelper.EnsurePortOpen(port);
        if (error is null)
        {
            ServerAddressLine.Text = $"Порт {port} разрешён в брандмауэре. " +
                (NetworkInfo.PreferredAddress() is { } address
                    ? $"Телефону указывать: http://{address}:{port}"
                    : "Адрес появится после запуска сервера.");
            return;
        }

        MessageBox.Show(this,
            $"Не удалось открыть порт {port}: {error}.\n\n" + FirewallHelper.Hint(port) +
            $"\n\nОткройте PowerShell от имени администратора и выполните:\n{FirewallHelper.ManualCommand(port)}",
            "Брандмауэр Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void ServerStop_Click(object sender, RoutedEventArgs e) => _ = StopServerAsync();

    private async Task StartServerAsync(bool silent)
    {
        try
        {
            _settings.Server.Enabled = true;
            PersistSettings();
            _serverHost ??= CreateServerHost();
            await _serverHost.StartAsync();

            // Tell the Wi-Fi (and the phone) that OLI is here, on whichever port is used.
            _beacon?.Dispose();
            _beacon = new LanBeacon(_settings.Server.Port, AppVersion.Current);
            _beacon.Start();

            // A phone cannot reach a port that Windows Firewall blocks: open it right away.
            // Silent when the app is elevated; otherwise Windows shows its own prompt.
            var port = _settings.Server.Port;
            if (!FirewallHelper.RuleExists(port))
            {
                var firewall = await Task.Run(() => FirewallHelper.EnsurePortOpen(port));
                if (firewall is null) OliLog.Info("SERVER", $"Порт {port} открыт в брандмауэре.");
                else
                    OliLog.Warning("SERVER",
                        $"Брандмауэр: {firewall}. Телефон по Wi-Fi не подключится, пока порт закрыт. " +
                        $"Решение: OLI → «Пульт админа» → «🛡 Открыть порт», или PowerShell от администратора: " +
                        FirewallHelper.ManualCommand(port));
            }

            RefreshServerUi();
            var address = NetworkInfo.PreferredAddress();
            ServerStatusLine.Text += address is null
                ? " · адрес для телефона не найден (нет Wi-Fi/Ethernet)"
                : $" · телефону указывать http://{address}:{port}";
        }
        catch (Exception ex)
        {
            OliLog.Error("SERVER", ex.ToString());
            ServerStatusLine.Text = "⚠ " + ex.Message;
            if (!silent) MessageBox.Show(this, ex.Message, "Сервер OLI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StopServerAsync()
    {
        _beacon?.Dispose();
        _beacon = null;
        if (_serverHost is null) return;
        await _serverHost.StopAsync();
        _settings.Server.Enabled = false;
        PersistSettings();
        RefreshServerUi();
    }

    private ServerHost CreateServerHost() => new(new ServerOptions
    {
        DataRoot = Path.Combine(AppPaths.DataRoot, "server"),
        Host = _settings.Server.Host,
        Port = _settings.Server.Port,
        LlamaBaseUrl = _settings.LlamaBaseUrl,
        Model = _settings.Model,
        MaxTokens = _settings.MaxTokens,
        Temperature = _settings.Temperature,
        TopP = _settings.TopP,
        TopK = _settings.TopK,
        RepeatPenalty = _settings.RepeatPenalty,
        AllowRegistration = _settings.Server.AllowRegistration,
        InviteCode = _settings.Server.InviteCode,
        UserQuotaBytes = _settings.Server.UserQuotaBytes,
        AdminToken = _settings.Server.AdminToken,
        WebSearchEnabled = _settings.WebSearchEnabled,
        OpenWeb = _settings.WebSearchOpenWeb,
        WorkspaceToolsEnabled = _settings.Server.WorkspaceToolsEnabled,
        Preamble = _settings.Server.Preamble,
        Tunnel = _settings.Server.Tunnel,
    });

    private void RefreshServerUi()
    {
        if (ServerStatusLine is null) return;
        var running = _serverHost?.IsRunning == true;
        ServerStatusLine.Text = running
            ? $"🟢 Сервер работает на порту {_settings.Server.Port} · пользователей: {_serverHost!.RegisteredUsers}" +
              (FirewallHelper.RuleExists(_settings.Server.Port)
                  ? " · порт открыт в брандмауэре"
                  : $" · ⚠ порт {_settings.Server.Port} закрыт брандмауэром: нажмите «🛡 Открыть порт» " +
                    "(или подключитесь через «Пульт админа» → интернет-доступ)")
            : "◻ Сервер остановлен — телефон не сможет подключиться.";
        ServerStartButton.IsEnabled = !running;
        ServerStopButton.IsEnabled = running;

        var address = NetworkInfo.PreferredAddress();
        var allAddresses = NetworkInfo.LocalAddresses();
        var publicUrl = _settings.Server.Tunnel.LastPublicUrl;
        ServerAddressLine.Text = running && address is not null
            ? $"Адрес для телефона по Wi-Fi: http://{address}:{_settings.Server.Port}" +
              (allAddresses.Count > 1 ? $"   (ещё: {string.Join(", ", allAddresses.Where(a => a != address))})" : "") +
              "  · в приложении кнопка «Найти мой ПК» находит сервер сама" +
              (publicUrl.Length > 0
                  ? $"{Environment.NewLine}Через интернет: {publicUrl} — включите в пульте администратора «Дам доступ из интернета» (мобильный интернет, учёба)."
                  : $"{Environment.NewLine}Мобильный интернет / учёба: пульт администратора → «🌍 Дам доступ из интернета» и код подключения для телефона.")
            : "Адрес для телефона появится после запуска (нужен Wi-Fi в той же сети или VPN).";
        ServerQuotaText.Text = TextExtractors.FormatSize((long)ServerQuotaSlider.Value * 1024 * 1024);
    }

    private void OpenAdmin_Click(object sender, RoutedEventArgs e)
    {
        PersistSettings();
        _serverHost ??= CreateServerHost();
        var window = new AdminWindow(_serverHost, _settings.Server.Tunnel) { Owner = this };
        window.ShowDialog();
        SettingsStore.Save(_settings);
        RefreshServerUi();
    }

    // ================================================================ console

    private void OnLogEntry(OliLogEntry entry) => Dispatcher.Invoke(() => AddConsoleEntry(entry));

    private void AddConsoleEntry(OliLogEntry entry)
    {
        // Cyrillic must survive the font: WPF's default console font (Consolas) has the
        // glyphs, but a missing fallback shows "?" — keep the list on Segoe UI.
        if (ConsoleList.FontFamily is null || ConsoleList.FontFamily.Source.StartsWith("Cascadia Mono", StringComparison.Ordinal))
            ConsoleList.FontFamily = new System.Windows.Media.FontFamily("Consolas, Segoe UI");
        ConsoleList.Items.Add($"[{entry.Time:HH:mm:ss}] {entry.Level.ToString().ToUpperInvariant(),-7} {entry.Source,-10} {entry.Message}");
        if (ConsoleAutoScroll.IsChecked == true && ConsoleList.Items.Count > 0)
            ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e) => ConsoleList.Items.Clear();

    private void CopyConsole_Click(object sender, RoutedEventArgs e) =>
        Clipboard.SetText(string.Join(Environment.NewLine, ConsoleList.Items.Cast<object>()));
}
