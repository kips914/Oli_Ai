using System.Diagnostics;
using OLI.Core;

namespace OLI.Client;

/// <summary>
/// Windows Firewall blocks unsolicited LAN connections by default, which makes the
/// phone see "сервер недоступен". This helper adds an inbound rule for the OLI port
/// (elevation prompt) and can tell whether the rule already exists.
/// </summary>
public static class FirewallHelper
{
    private const string RuleName = "OLI Server (phone app)";
    private const string LegacyRuleName = "OLI Server (phone app) 8765";

    /// <summary>Set once the rule was confirmed, so a restart does not re-add it.</summary>
    private static bool _seenOpen;

    /// <summary>Runs netsh and returns (exitCode, output). No elevation.</summary>
    private static (int Code, string Output) RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return (-1, "не удалось запустить netsh");
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(8000);
            return (process.HasExited ? process.ExitCode : -1, output.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    public static bool RuleExists(int port)
    {
        // Both names are checked: the current one and the one used by older builds, so an
        // existing rule is never duplicated and an old port-8765 rule is not trusted.
        foreach (var name in new[] { RuleName, LegacyRuleName })
        {
            var (code, output) = RunNetsh($"advfirewall firewall show rule name=\"{name}\"");
            if (code != 0) continue;
            var localPorts = output.Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("Локальный порт", StringComparison.OrdinalIgnoreCase) ||
                                        line.StartsWith("LocalPort", StringComparison.OrdinalIgnoreCase));
            if (localPorts is not null && localPorts.Contains(port.ToString(), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Adds (or refreshes) the inbound rule. Returns null on success, otherwise a
    /// human-readable reason. Asks for elevation (UAC) when the app is not admin —
    /// without that Windows silently refuses and the phone sees "сервер не отвечает".
    /// </summary>
    public static string? EnsurePortOpen(int port, bool elevated = true)
    {
        if (RuleExists(port)) { _seenOpen = true; return null; }

        var arguments =
            $"advfirewall firewall delete rule name=\"{RuleName}\" & " +
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} " +
            "profile=any & " +
            // The app itself is allowed too: some third-party firewalls filter by program.
            $"advfirewall firewall add rule name=\"{RuleName} (app)\" dir=in action=allow program=\"{Environment.ProcessPath}\" " +
            "enable=yes profile=any";

        // 1. Try silently — works when OLI already runs elevated (no prompt at all).
        try
        {
            using var quiet = Process.Start(new ProcessStartInfo("cmd.exe", $"/c netsh {arguments}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            quiet?.WaitForExit(15_000);
        }
        catch { /* fall through to elevation */ }
        if (RuleExists(port)) { _seenOpen = true; return null; }
        if (!elevated) return "правило не добавлено: нужны права администратора";

        // 2. Ask Windows for elevation. This is the step that was missing before.
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c netsh {arguments}")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Verb = "runas",
            };
            using var elevatedProcess = Process.Start(psi);
            if (elevatedProcess is null) return "не удалось запросить права администратора";
            elevatedProcess.WaitForExit(60_000);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = the user pressed "No" on the UAC prompt.
            return ex.NativeErrorCode == 1223
                ? "запрос прав администратора отклонён — порт остался закрытым"
                : $"не удалось открыть порт: {ex.Message}";
        }
        catch (Exception ex)
        {
            OliLog.Warning("SERVER", $"Брандмауэр: {ex.Message}");
            return ex.Message;
        }

        if (RuleExists(port)) { _seenOpen = true; return null; }
        return $"правило для порта {port} не появилось. Откройте PowerShell от администратора и выполните: " +
               $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=any";
    }

    /// <summary>Command the user can paste into an elevated PowerShell / cmd.</summary>
    public static string ManualCommand(int port) =>
        $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} profile=any";

    /// <summary>Firewall/profile hint shown next to the server address.</summary>
    public static string Hint(int port) =>
        $"Если телефон не видит сервер: разрешите порт {port} в брандмауэре Windows " +
        "(кнопка «Открыть порт») и проверьте, что телефон и ПК в одной Wi-Fi-сети. " +
        "Правило для порта работает при любом профиле сети.";
}
