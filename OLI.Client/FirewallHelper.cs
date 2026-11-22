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
        if (RuleExists(port)) return null;

        var arguments =
            $"advfirewall firewall delete rule name=\"{RuleName}\" & " +
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} " +
            "profile=private,domain";

        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c netsh {arguments}")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevated) psi.Verb = "runas";

            using var process = Process.Start(psi);
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

    /// <summary>Private/public network hint: phones on the same Wi-Fi need the Private profile.</summary>
    public static string Hint(int port) =>
        $"Если телефон не видит сервер, разрешите порт {port} в брандмауэре Windows " +
        "(кнопка «Открыть порт» на этой странице) и убедитесь, что сеть помечена как «Частная».";
}
