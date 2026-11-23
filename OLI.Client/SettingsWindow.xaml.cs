using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using OLI.Core;
using Localization = OLI.Core.Localization;

namespace OLI.Client;

public partial class SettingsWindow : Window
{
    private readonly OliSettings _settings;
    private bool _initializing;
    private OliLanguage _selectedLanguage;

    private static readonly (string Label, long Bytes)[] Quotas =
    [
        ("256 МБ", 256L * 1024 * 1024),
        ("512 МБ", 512L * 1024 * 1024),
        ("1 ГБ (по умолчанию)", 1024L * 1024 * 1024),
        ("2 ГБ", 2L * 1024 * 1024 * 1024),
        ("5 ГБ", 5L * 1024 * 1024 * 1024),
    ];

    public bool RestartRequested { get; private set; }

    /// <summary>Raised with the new llama.cpp / Ollama address when the user edits it.</summary>
    public event Action<string>? EngineUrlChanged;

    public SettingsWindow(OliSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _selectedLanguage = settings.Language;
        _initializing = true;

        // _initializing is already true, so this assignment does not fire EngineUrlChanged.
        EngineUrlBox.Text = settings.LlamaBaseUrl;
        FastMode.IsChecked = settings.Mode == GenerationMode.Fast;
        PerformanceMode.IsChecked = settings.Mode == GenerationMode.Performance;
        ContextSlider.Value = Math.Clamp(settings.ContextSize, 2048, 32768);
        ThreadsSlider.Value = Math.Clamp(settings.CpuThreads, 1, 32);
        GpuSlider.Value = Math.Clamp(settings.GpuLayers, 0, 128);
        CheckUpdatesStartup.IsChecked = settings.CheckForUpdatesOnStartup;
        GitHubRepositoryBox.Text = settings.GitHubRepositoryUrl;
        ThinkingToggle.IsChecked = settings.ThinkingEnabled;
        WorkspaceEnabledToggle.IsChecked = settings.WorkspaceEnabled;
        CommandToggle.IsChecked = settings.AllowCommandExecution;
        AllowedCommandsBox.Text = string.Join(", ", settings.AllowedCommands);

        ModelCombo.Items.Clear();
        foreach (var model in ModelCatalog.All) ModelCombo.Items.Add(model.DisplayName);
        var current = ModelCatalog.Resolve(settings.ModelGguf);
        var index = ModelCatalog.All.ToList().FindIndex(m => m.Id == current.Id);
        ModelCombo.SelectedIndex = index >= 0 ? index : 0;
        UpdateModelDescription();

        QuotaCombo.Items.Clear();
        foreach (var (label, _) in Quotas) QuotaCombo.Items.Add(label);
        var quotaIndex = Array.FindIndex(Quotas, q => q.Bytes == settings.WorkspaceQuotaBytes);
        QuotaCombo.SelectedIndex = quotaIndex >= 0 ? quotaIndex : 2;

        LanguageCombo.Items.Add(new KeyValuePair<OliLanguage, string>(OliLanguage.Auto, Localization.T("language.auto")));
        LanguageCombo.Items.Add(new KeyValuePair<OliLanguage, string>(OliLanguage.Russian, Localization.T("language.ru")));
        LanguageCombo.Items.Add(new KeyValuePair<OliLanguage, string>(OliLanguage.English, Localization.T("language.en")));
        LanguageCombo.SelectedValue = _selectedLanguage;

        UpdateLabels();
        UiLocalization.ApplySettings(this);
        LanguageCombo.SelectedValue = _selectedLanguage;
        _initializing = false;
    }

    private void EngineUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        var url = EngineUrlBox.Text.Trim();
        if (url.Length == 0) return;
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "http://" + url;
        _settings.LlamaBaseUrl = url.TrimEnd('/');
        EngineUrlChanged?.Invoke(_settings.LlamaBaseUrl);
        EngineHintText.Text = "Сохранено: " + _settings.LlamaBaseUrl + " — перезапустите OLI, чтобы ответы пошли через этот адрес.";
    }

    private void EngineLlama_Click(object sender, RoutedEventArgs e) =>
        EngineUrlBox.Text = "http://127.0.0.1:8080";

    private void EngineOllama_Click(object sender, RoutedEventArgs e) =>
        EngineUrlBox.Text = "http://127.0.0.1:11434";

    private void UpdateModelDescription()
    {
        if (ModelCombo.SelectedIndex < 0 || ModelCombo.SelectedIndex >= ModelCatalog.All.Count) return;
        var model = ModelCatalog.All[ModelCombo.SelectedIndex];
        ModelDescriptionText.Text = $"{model.Notes}\nGGUF: {model.Gguf} · ~{model.SizeBytes / 1_000_000_000.0:F1} ГБ на диске";
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        UpdateModelDescription();
    }

    private void UpdateLabels()
    {
        var english = Localization.Current == OliLanguage.English;
        ContextValue.Text = $"{(int)ContextSlider.Value} {(english ? "tokens" : "токенов")}";
        ThreadsValue.Text = $"{(int)ThreadsSlider.Value}";
        GpuValue.Text = $"{(int)GpuSlider.Value}";
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) _settings.Mode = PerformanceMode.IsChecked == true ? GenerationMode.Performance : GenerationMode.Fast;
    }

    private void Dark_Click(object sender, RoutedEventArgs e) { _settings.Theme = OliTheme.Dark; App.ApplyTheme(OliTheme.Dark); }
    private void Light_Click(object sender, RoutedEventArgs e) { _settings.Theme = OliTheme.Light; App.ApplyTheme(OliTheme.Light); }
    private void System_Click(object sender, RoutedEventArgs e) { _settings.Theme = OliTheme.System; App.ApplyTheme(OliTheme.System); }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageCombo.SelectedValue is not OliLanguage language) return;
        _selectedLanguage = language;
        _initializing = true;
        Localization.Initialize(language);
        UiLocalization.ApplySettings(this);
        LanguageCombo.SelectedValue = language;
        _initializing = false;
        UpdateLabels();
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.ClearCache();
        MessageBox.Show(this, Localization.T("cache.cleared"), "OLI", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureDirectories();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogsRoot}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "OLI");
        }
    }

    private void Restart_Click(object sender, RoutedEventArgs e) { RestartRequested = true; DialogResult = true; }

    private void Diagnostics_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            $"OLI {AppVersion.Current}\n" +
            $"Модель: {_settings.ModelDisplayName}\nGGUF: {_settings.ModelGguf}\n" +
            $"Данные: {AppPaths.DataRoot}\nWorkspace: {AppPaths.WorkspaceRoot}\n" +
            $"GPU layers: {(int)GpuSlider.Value}\nКонтекст: {(int)ContextSlider.Value}\n" +
            $"Потоки CPU: {(int)ThreadsSlider.Value}\nРежим: {_settings.Mode}\n" +
            $"llama.cpp: {_settings.LlamaBaseUrl}\nGitHub: {_settings.GitHubRepositoryUrl}",
            Localization.T("diagnostics.title"), MessageBoxButton.OK, MessageBoxImage.Information);

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Localization.T("reset.confirm"), Localization.T("confirm"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SettingsStore.ResetUserData();
        RestartRequested = true;
        DialogResult = true;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = Localization.T("update.checking");
        try
        {
            using var service = new GitHubUpdateService();
            var result = await service.CheckAsync(GitHubRepositoryBox.Text.Trim());
            if (!result.Success) { UpdateStatusText.Text = "⚠ " + result.Error; return; }
            if (!result.UpdateAvailable) { UpdateStatusText.Text = Localization.T("update.latest", result.CurrentVersion); return; }
            UpdateStatusText.Text = Localization.T("update.available", result.LatestVersion);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = Localization.T("update.error", ex.Message);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.ContextSize = (int)ContextSlider.Value;
        _settings.CpuThreads = (int)ThreadsSlider.Value;
        _settings.GpuLayers = (int)GpuSlider.Value;
        _settings.Mode = PerformanceMode.IsChecked == true ? GenerationMode.Performance : GenerationMode.Fast;
        _settings.MaxTokens = _settings.Mode == GenerationMode.Performance ? _settings.PerformanceMaxTokens : _settings.FastMaxTokens;
        _settings.CheckForUpdatesOnStartup = CheckUpdatesStartup.IsChecked == true;
        _settings.GitHubRepositoryUrl = GitHubRepositoryBox.Text.Trim();
        _settings.Language = _selectedLanguage;
        _settings.ThinkingEnabled = ThinkingToggle.IsChecked == true;
        _settings.WorkspaceEnabled = WorkspaceEnabledToggle.IsChecked == true;
        _settings.AllowCommandExecution = CommandToggle.IsChecked == true;
        _settings.AllowedCommands = AllowedCommandsBox.Text
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        if (QuotaCombo.SelectedIndex >= 0) _settings.WorkspaceQuotaBytes = Quotas[QuotaCombo.SelectedIndex].Bytes;

        if (ModelCombo.SelectedIndex >= 0 && ModelCombo.SelectedIndex < ModelCatalog.All.Count)
        {
            var model = ModelCatalog.All[ModelCombo.SelectedIndex];
            if (!string.Equals(model.Gguf, _settings.ModelGguf, StringComparison.OrdinalIgnoreCase))
            {
                ModelCatalog.Apply(_settings, model);
                RestartRequested = true;
            }
        }

        Localization.Initialize(_selectedLanguage);
        SettingsStore.Save(_settings);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
