using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OLI.Core;

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
        base.OnStartup(e);
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
