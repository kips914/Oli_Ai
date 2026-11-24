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
    /// <summary>
    /// True when the port can be bound right now on the address the server will really use.
    /// Checking 0.0.0.0 while the server binds 127.0.0.1 gives a false "free", and Kestrel
    /// then fails to bind — the exact mismatch that produced the reported stack trace.
    /// </summary>
    public static bool IsFree(int port, string? host = null)
    {
        if (port is < 1 or > 65535) return false;
        var address = string.IsNullOrWhiteSpace(host) || host == "0.0.0.0" || host == "*"
            ? IPAddress.Any
            : IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Any;

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(address, port);
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
    /// True when something already accepts connections on the address. This is the reliable
    /// question: on Unix a second bind may succeed thanks to SO_REUSEADDR while Kestrel's
    /// exclusive bind still fails, so "can I bind" alone is not enough.
    /// </summary>
    public static bool IsListening(int port, string? host = null)
    {
        if (port is < 1 or > 65535) return false;
        var address = string.IsNullOrWhiteSpace(host) || host is "0.0.0.0" or "*"
            ? "127.0.0.1"
            : host;

        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(address, port);
            return task.Wait(TimeSpan.FromMilliseconds(400)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when an OLI server (not some other app) answers on this port. This is the one
    /// question worth asking before starting a second copy: a live OLI should be reused,
    /// while a foreign program just means "pick another port".
    ///
    /// Two addresses are tried because they answer for different reasons: <c>/</c> is a fixed
    /// JSON banner (fast, no dependencies), while <c>/api/health</c> also probes the model
    /// engine and can be slow or briefly failing while the engine is loading. Relying on
    /// health alone made a working OLI look like a stranger.
    /// </summary>
    public static async Task<bool> OliAnswersAsync(int port, CancellationToken ct = default, TimeSpan? perRequestTimeout = null)
    {
        if (port is < 1 or > 65535) return false;

        foreach (var path in new[] { "/", "/api/health" })
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                {
                    try { await Task.Delay(200, ct); } catch (OperationCanceledException) { return false; }
                }

                try
                {
                    using var http = new HttpClient { Timeout = perRequestTimeout ?? TimeSpan.FromSeconds(4) };
                    using var response = await http.GetAsync($"http://127.0.0.1:{port}{path}", ct);
                    if (!response.IsSuccessStatusCode) continue;
                    var body = await response.Content.ReadAsStringAsync(ct);
                    if (body.Contains("oli-server", StringComparison.OrdinalIgnoreCase) ||
                        body.Contains("OLI Server", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return false;
                }
                catch
                {
                    // Try the next address / attempt: a foreign program, or a server that is
                    // still warming up.
                }
            }
        }

        return false;
    }

    /// <summary>
    /// First free port at or after <paramref name="preferred"/> — used to move OLI onto a
    /// working port automatically instead of asking the user to read netstat.
    /// </summary>
    public static int FindFree(int preferred, int attempts = 20, string? host = null)
    {
        for (var offset = 0; offset < attempts; offset++)
        {
            var port = preferred + offset;
            if (port > 65535) break;
            if (port < 1024) continue;
            if (IsFree(port, host)) return port;
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
