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
        var (code, output) = RunNetsh($"advfirewall firewall show rule name=\"{RuleName}\"");
        return code == 0 && output.Contains(port.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds (or refreshes) the inbound rule. Returns null on success, otherwise a
    /// human-readable reason. Shows a UAC prompt when needed.
    /// </summary>
    public static string? EnsurePortOpen(int port, bool elevated = true)
    {
        // Delete + add in one go, so a stale rule (old profile/port) is replaced instead
        // of silencing the phone. A rule that already allows the port is fine too.
        var alreadyOpen = RuleExists(port);
        if (alreadyOpen && !_seenOpen) { _seenOpen = true; return null; }

        var arguments =
            $"advfirewall firewall delete rule name=\"{RuleName}\" & " +
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} " +
            "profile=any";

        // Firewall rules need admin rights. Try silently first: if the app already runs
        // elevated this works with no UAC prompt at all; otherwise we ask for elevation.
        var candidate = new ProcessStartInfo("cmd.exe", $"/c netsh {arguments}")
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (!elevated) candidate.Verb = "";

        try
        {
            if (elevated)
            {
                using (var quiet = Process.Start(new ProcessStartInfo("cmd.exe", $"/c netsh {arguments}")
                       {
                           UseShellExecute = false,
                           CreateNoWindow = true,
                           RedirectStandardOutput = true,
                           RedirectStandardError = true,
                       }))
                {
                    quiet?.WaitForExit(15_000);
                }
                if (RuleExists(port)) return null;
            }

            using var process = Process.Start(candidate);
            if (process is null) return "не удалось запустить netsh";
            process.WaitForExit(30_000);

            if (RuleExists(port)) return null;
            return "правило не добавлено (возможно, отказ в правах администратора)";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "отменено запрос прав администратора";
        }
        catch (Exception ex)
        {
            OliLog.Warning("SERVER", $"Брандмауэр: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>Firewall/profile hint shown next to the server address.</summary>
    public static string Hint(int port) =>
        $"Если телефон не видит сервер: разрешите порт {port} в брандмауэре Windows " +
        "(кнопка «Открыть порт») и проверьте, что телефон и ПК в одной Wi-Fi-сети. " +
        "Правило для порта работает при любом профиле сети.";
}
