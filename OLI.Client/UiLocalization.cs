using OLI.Core;

namespace OLI.Client;

/// <summary>
/// Applies localized strings to controls by name. Kept apart from the XAML so the
/// design can change without touching translation keys.
/// </summary>
public static class UiLocalization
{
    public static void ApplyMain(MainWindow w)
    {
        w.SettingsButton.Content = "⚙  " + Localization.T("settings");
        w.NewChatButton.Content = Localization.Current == OliLanguage.English ? "＋ New chat" : "＋ Новый чат";
        w.SearchChip.Content = Localization.Current == OliLanguage.English ? "Search" : "Поиск";
        w.WorkspaceChip.Content = Localization.Current == OliLanguage.English ? "Files" : "Файлы";
        w.SendButton.Content = Localization.T("send") + "  ➤";
        w.InputHelpText.Text = Localization.Current == OliLanguage.English
            ? "Enter — send · Shift+Enter — new line"
            : "Enter — отправить · Shift+Enter — новая строка";
        w.WebSearchMasterToggle.Content = Localization.T("web.allow");
        if (!w.IsLoaded) w.StatusText.Text = Localization.T("status.starting");
    }

    public static void ApplySettings(SettingsWindow w)
    {
        w.Title = Localization.T("settings.title");
        w.SettingsTitleText.Text = Localization.T("settings.title");
        w.AppearanceText.Text = Localization.T("appearance");
        w.DarkButton.Content = Localization.T("theme.dark");
        w.LightButton.Content = Localization.T("theme.light");
        w.SystemButton.Content = Localization.T("theme.system");
        w.AiModeText.Text = Localization.T("ai.mode");
        w.FastMode.Content = Localization.T("mode.fast");
        w.PerformanceMode.Content = Localization.T("mode.performance");
        w.PerformanceText.Text = Localization.T("performance");
        w.ContextText.Text = Localization.T("context");
        w.ThreadsText.Text = Localization.T("cpuThreads");
        w.GpuText.Text = Localization.T("gpuLayers");
        w.LanguageText.Text = Localization.T("language");
        w.UpdatesText.Text = Localization.T("updates");
        w.CheckUpdatesStartup.Content = Localization.T("updates.startup");
        w.GitHubText.Text = Localization.T("updates.repo");
        w.CheckUpdatesButton.Content = Localization.T("updates.check");
        w.MaintenanceText.Text = Localization.T("maintenance");
        w.ClearCacheButton.Content = Localization.T("cache.clear");
        w.RestartEngineButton.Content = Localization.T("engine.restart");
        w.DiagnosticsButton.Content = Localization.T("diagnostics");
        w.ResetTitleText.Text = Localization.T("reset.title");
        w.ResetDescriptionText.Text = Localization.T("reset.description");
        w.ResetButton.Content = Localization.T("reset.button");
        w.CancelButton.Content = Localization.T("cancel");
        w.SaveButton.Content = Localization.T("save");

        var english = Localization.Current == OliLanguage.English;
        w.ModelSectionText.Text = english ? "Model" : "Модель";
        w.WorkspaceSectionText.Text = "Workspace";
        w.ThinkingToggle.Content = english
            ? "Thinking mode — slower, but more accurate"
            : "Режим размышлений (thinking) — медленнее, но точнее";
        w.WorkspaceEnabledToggle.Content = english
            ? "Let the model use the workspace"
            : "Разрешить модели использовать workspace";
        w.CommandToggle.Content = english
            ? "Allow allow-listed commands (python, node, git...)"
            : "Разрешить выполнение разрешённых команд (python, node, git...)";
        w.OpenLogsButton.Content = english ? "Open logs" : "Открыть логи";
        w.SettingsSubtitleText.Text = english
            ? "Model, memory, appearance and maintenance"
            : "Модель, память, внешний вид и обслуживание";

        w.LanguageCombo.Items.Clear();
        w.LanguageCombo.Items.Add(new KeyValuePair<OliLanguage, string>(OliLanguage.Auto, Localization.T("language.auto")));
        w.LanguageCombo.Items.Add(new KeyValuePair<OliLanguage, string>(OliLanguage.Russian, Localization.T("language.ru")));
        w.LanguageCombo.Items.Add(new KeyValuePair<OliLanguage, string>(OliLanguage.English, Localization.T("language.en")));
    }
}
