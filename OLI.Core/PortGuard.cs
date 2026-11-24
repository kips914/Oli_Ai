using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OLI.Core;

/// <summary>Who holds a TCP port, as far as Windows will tell us.</summary>
public sealed record PortOwner(int Port, int ProcessId, string ProcessName, string Path)
{
    public bool IsOli => ProcessName.Contains("OLI", StringComparison.OrdinalIgnoreCase);

    public string Describe => ProcessId == 0
        ? $"порт {Port} занят другой программой"
        : $"порт {Port} занят: {ProcessName} (PID {ProcessId})";
}

/// <summary>
/// Everything OLI needs to know before it binds a port.
///
/// It exists because "address already in use" used to arrive as a six-frame stack trace
/// six times in the log, with no hint at all: the port was held either by a second OLI
/// window (the app had no single-instance guard) or by the temporary server that the
/// first-run wizard had started. Neither is a crash — both are explainable and fixable.
/// </summary>
public static class PortGuard
{
    /// <summary>True when the port can be bound right now (0.0.0.0, the way Kestrel does it).</summary>
    public static bool IsFree(int port)
    {
        if (port is < 1 or > 65535) return false;
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { /* nothing to release */ }
        }
    }

    /// <summary>
    /// First free port at or after <paramref name="preferred"/> — used to move OLI onto a
    /// working port automatically instead of asking the user to read netstat.
    /// </summary>
    public static int FindFree(int preferred, int attempts = 20)
    {
        for (var offset = 0; offset < attempts; offset++)
        {
            var port = preferred + offset;
            if (port > 65535) break;
            if (port < 1024) continue;
            if (IsFree(port)) return port;
        }
        return 0;
    }

    /// <summary>
    /// Best-effort owner of a port: parse <c>netstat -ano</c> and resolve the PID name.
    /// Never throws; an unknown owner is an honest answer too.
    /// </summary>
    public static PortOwner? FindOwner(int port)
    {
        if (!OperatingSystem.IsWindows()) return new PortOwner(port, 0, "", "");
        try
        {
            using var netstat = Process.Start(new ProcessStartInfo("netstat.exe", "-ano -p TCP")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (netstat is null) return new PortOwner(port, 0, "", "");
            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit(3000);

            var pid = 0;
            foreach (var raw in output.Split('\n'))
            {
                // TCP    0.0.0.0:6767    0.0.0.0:0    LISTENING    1234
                var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!parts[0].StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                if (!parts[1].EndsWith(":" + port, StringComparison.Ordinal)) continue;
                if (!parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(parts[4], out pid)) continue;
                break;
            }

            if (pid <= 0) return new PortOwner(port, 0, "", "");

            try
            {
                using var process = Process.GetProcessById(pid);
                var path = "";
                try { path = process.MainModule?.FileName ?? ""; } catch { /* access denied is fine */ }
                return new PortOwner(port, pid, process.ProcessName, path);
            }
            catch
            {
                return new PortOwner(port, pid, "неизвестный процесс", "");
            }
        }
        catch
        {
            return new PortOwner(port, 0, "", "");
        }
    }
}
