using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OLI.Core;

/// <summary>What the phone can conclude after one probe. Every value maps to one sentence for the user.</summary>
public enum ConnectionState
{
    /// <summary>Not probed yet.</summary>
    Unknown = 0,
    /// <summary>The address answered and its model engine works: chat will answer.</summary>
    Ready,
    /// <summary>The address answered, but the PC's model engine is off: chat would fail.</summary>
    EngineDown,
    /// <summary>Nothing answered: wrong address, firewall, different Wi-Fi, or OLI is closed.</summary>
    ServerDown,
    /// <summary>Something answered, but it is not an OLI server.</summary>
    NotOli,
}

/// <summary>
/// One connection core for the desktop app, the server and the phone.
///
/// Everything that decides "is this address usable" lives here and nowhere else, because
/// the old code had the same rule implemented three times with three different results.
/// All methods are pure, so <c>OLI.SelfTest</c> covers them without a network.
/// </summary>
public static class ConnectionCore
{
    /// <summary>Default OLI server port (settings may change it; discovery must survive that).</summary>
    public const int DefaultPort = 8765;

    /// <summary>UDP port the server broadcasts its presence on (see <see cref="Beacon"/>).</summary>
    public const int BeaconPort = 52345;

    public const string JoinPrefix = "OLI1";
    public const string JoinScheme = "oli://";

    // ------------------------------------------------------------------ addresses

    /// <summary>
    /// Canonical address: scheme added when missing, no trailing slash, host lower-cased,
    /// default port dropped for http/https. Returns null for something that cannot be an address.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().Trim('"', '\'');

        // Users paste "192.168.1.10:8765" or "localhost:6767" from a chat — both are fine.
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (string.IsNullOrWhiteSpace(uri.Host)) return null;

        var builder = new UriBuilder(uri)
        {
            Host = uri.Host.ToLowerInvariant(),
            Path = uri.AbsolutePath.TrimEnd('/'),
        };
        // Keep an explicit port (6767, 11434…), drop the scheme default (80/443).
        if (uri.IsDefaultPort) builder.Port = -1;

        var result = builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        return result.TrimEnd('/');
    }

    /// <summary>True when the address is a loopback one (only the PC itself can use it).</summary>
    public static bool IsLoopback(string? address)
    {
        var normalized = Normalize(address);
        if (normalized is null) return false;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return false;
        if (uri.IsLoopback) return true;
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Order in which the phone should try addresses: the one that worked last time
    /// first, then the configured one, then anything remembered, then the LAN default.
    /// Duplicates are removed, invalid entries dropped.
    /// </summary>
    public static IReadOnlyList<string> CandidateOrder(IEnumerable<string?> addresses)
    {
        var result = new List<string>();
        foreach (var raw in addresses)
        {
            var normalized = Normalize(raw);
            if (normalized is null) continue;
            if (result.Contains(normalized, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(normalized);
        }
        return result;
    }

    // ------------------------------------------------------------------ join code

    /// <summary>
    /// Short code the PC shows and the phone pastes: base64url of
    /// <c>OLI1|https://addr:port|invite</c>. Contains the address, the port and the
    /// invite code, so a student never types either.
    /// </summary>
    public static string BuildJoinCode(string address, string? inviteCode = null)
    {
        var normalized = Normalize(address) ?? address.Trim();
        var invite = (inviteCode ?? "").Trim().Replace("|", "/");
        var raw = $"{JoinPrefix}|{normalized}|{invite}";
        return ToBase64Url(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>
    /// Reads a join code (or a bare address the user typed in the same field).
    /// Returns null only when the text is neither.
    /// </summary>
    public static JoinCodeInfo? ParseJoinCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var raw = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        raw = raw.Trim('"', '\'');

        // QR scanners and messengers add "oli://" to the code; strip it before anything
        // else, otherwise the scheme check below mistakes the prefix for an address.
        if (raw.StartsWith(JoinScheme, StringComparison.OrdinalIgnoreCase))
            raw = raw[JoinScheme.Length..];

        // A plain address is accepted too: "192.168.1.10:6767" or "https://x.trycloudflare.com".
        if (raw.Contains("://", StringComparison.Ordinal) || LooksLikeAddress(raw))
        {
            var normalized = Normalize(raw);
            return normalized is null ? null : new JoinCodeInfo(normalized, "", false);
        }

        // The code is base64 of "OLI1|…", so it never *starts* with "OLI1" on the wire.
        // Decode first, then check the payload; a literal "OLI1|url|invite" still works.
        try
        {
            var decoded = raw.StartsWith(JoinPrefix, StringComparison.Ordinal)
                ? raw
                : Encoding.UTF8.GetString(FromBase64Url(raw));
            var parts = decoded.Split('|');
            if (parts.Length < 2 || parts[0] != JoinPrefix) return null;
            var address = Normalize(parts[1]);
            if (address is null) return null;
            var invite = parts.Length > 2 ? parts[2].Trim() : "";
            return new JoinCodeInfo(address, invite, true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Cheap shape test for a typed address: "host:port" or a dotted host. A single word
    /// ("привет") must not be mistaken for an address, otherwise every typo becomes a
    /// connection attempt and the user gets a confusing "компьютер не ответил".
    /// </summary>
    private static bool LooksLikeAddress(string text)
    {
        if (text.Length is < 3 or > 300) return false;
        if (text.Contains(' ')) return false;
        var hasPort = text.Contains(':');
        var host = hasPort ? text[..text.LastIndexOf(':')] : text;
        if (host.Length == 0) return false;
        if (!host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')) return false;
        // "192.168.1.10", "my-pc.local", "localhost" or anything with an explicit port.
        return hasPort || host.Contains('.') || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var value = text.Replace('-', '+').Replace('_', '/');
        value = (value.Length % 4) switch
        {
            2 => value + "==",
            3 => value + "=",
            _ => value,
        };
        return Convert.FromBase64String(value);
    }

    // ------------------------------------------------------------------ health rules

    /// <summary>
    /// The single rule that decides what the phone should tell the user after a probe.
    /// Old code checked <c>status</c> only, so an OLI server whose engine was dead looked
    /// "connected" while chat failed, and an Ollama answer looked like "not OLI".
    /// </summary>
    public static ConnectionState Classify(bool answered, string? service, bool? engineAlive, bool? isOliServer)
    {
        if (!answered) return ConnectionState.ServerDown;
        if (!string.Equals(service, "oli-server", StringComparison.OrdinalIgnoreCase)) return ConnectionState.NotOli;
        if (isOliServer == false) return ConnectionState.NotOli;
        return engineAlive == true ? ConnectionState.Ready : ConnectionState.EngineDown;
    }

    /// <summary>
    /// Classification of a real /api/health payload. Kept here so the PC's "Проверить путь
    /// телефона", the phone and the tests all read the same fields with the same rules.
    /// </summary>
    public static ConnectionState ClassifyHealth(bool answered, string? service, bool? engineAlive) =>
        Classify(answered, service, engineAlive, null);

    /// <summary>One sentence for the state — the same words on the PC and on the phone.</summary>
    public static string Describe(ConnectionState state, string? address = null, string? engine = null, string? detail = null)
    {
        var where = string.IsNullOrWhiteSpace(address) ? "" : " (" + Short(address!) + ")";
        return state switch
        {
            ConnectionState.Ready =>
                "ПК отвечает" + where + ", движок модели работает" +
                (string.IsNullOrWhiteSpace(engine) ? "." : $" ({engine})."),

            ConnectionState.EngineDown =>
                "ПК отвечает" + where + ", но движок модели не запущен — чат не сможет ответить. " +
                "Запустите Ollama (`ollama serve`) или llama.cpp (`scripts\\start-llama.ps1`).",

            ConnectionState.NotOli =>
                "По адресу" + where + " ответил не OLI. Проверьте порт: OLI-сервер по умолчанию " + DefaultPort + ".",

            ConnectionState.ServerDown =>
                "Компьютер не ответил" + where + ". " +
                "Дома: на ПК «Пульт админа» → «🛡 Открыть порт» (без этого порт закрыт) и проверьте, " +
                "что сервер запущен. Вне дома: «🌍 Дам доступ из интернета» и вставьте код подключения.",

            _ when !string.IsNullOrWhiteSpace(detail) => detail!,
            _ => "Ещё не проверено.",
        };
    }

    private static string Short(string address) =>
        address.Replace("http://", "").Replace("https://", "");

    // ------------------------------------------------------------------ LAN beacon

    /// <summary>
    /// UDP presence packet. The phone cannot know the port the user typed in OLI (6767,
    /// 8765, …) and cannot scan a subnet in 2 seconds on mobile data, so the server
    /// shouts one small packet with its port and the phone listens.
    /// </summary>
    public static class Beacon
    {
        public const string Magic = "OLI-BEACON/2";

        public static byte[] Encode(int port, string? name, string version)
        {
            var text = $"{Magic}|{port}|{Sanitize(name ?? Environment.MachineName)}|{version}";
            return Encoding.UTF8.GetBytes(text);
        }

        /// <summary>Null when the packet is not ours (another app may use the port).</summary>
        public static BeaconInfo? Decode(ReadOnlySpan<byte> payload)
        {
            if (payload.Length is 0 or > 512) return null;
            var text = Encoding.UTF8.GetString(payload).TrimEnd('\0');
            var parts = text.Split('|');
            if (parts.Length < 2 || parts[0] != Magic) return null;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) return null;
            if (port is < 1 or > 65535) return null;
            return new BeaconInfo(port,
                parts.Length > 2 ? parts[2].Trim() : "",
                parts.Length > 3 ? parts[3].Trim() : "");
        }

        private static string Sanitize(string value) =>
            new(value.Where(c => c is not ('|' or '\n' or '\r')).Take(48).ToArray());
    }
}

/// <summary>Result of parsing a join code / typed address.</summary>
public sealed record JoinCodeInfo(string Address, string InviteCode, bool FromCode);

/// <summary>What a server put into its UDP beacon.</summary>
public sealed record BeaconInfo(int Port, string Name, string Version);

/// <summary>
/// Retry budget for probes and chat calls, kept in one place so the phone and the PC
/// stop disagreeing about how long "trying" lasts.
/// </summary>
public static class RetryPolicy
{
    /// <summary>Delays between probe attempts (milliseconds). Three tries ≈ 1.5 s of waiting.</summary>
    public static readonly int[] ProbeDelaysMs = [0, 400, 1_100];

    /// <summary>Delays between chat attempts on a transport error. 503 (engine) is not retried.</summary>
    public static readonly int[] ChatDelaysMs = [0, 700];

    /// <summary>True when the failure is worth retrying automatically.</summary>
    public static bool ShouldRetry(int? httpStatus, bool timeout) =>
        timeout || httpStatus is null or 0 or 408 or 429 or >= 500 && httpStatus != 503;
}
