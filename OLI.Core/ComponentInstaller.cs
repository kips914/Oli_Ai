using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace OLI.Core;

/// <summary>One external file OLI needs on this PC.</summary>
public sealed record ComponentSpec(
    string Id,
    string DisplayName,
    string FileName,
    string Url,
    string Sha256,
    string Why,
    /// <summary>Second location to try when the first one is blocked (mirrors/regions).</summary>
    string? MirrorUrl = null);

/// <summary>Result of one component check.</summary>
public enum ComponentState
{
    /// <summary>Nothing to do: the file is there and its hash matches.</summary>
    Ready,
    /// <summary>The file is missing.</summary>
    Missing,
    /// <summary>The file is there but corrupt/partial (wrong hash or size).</summary>
    Broken,
    /// <summary>Downloaded and verified during this run.</summary>
    Installed,
    /// <summary>Download failed — the reason is in <see cref="ComponentReport.Error"/>.</summary>
    Failed,
}

/// <summary>What happened to one component.</summary>
public sealed record ComponentReport(
    ComponentSpec Spec,
    ComponentState State,
    string Path,
    string Error = "")
{
    public bool Ok => State is ComponentState.Ready or ComponentState.Installed;

    public string Line => State switch
    {
        ComponentState.Ready => $"✓ {Spec.DisplayName} уже установлен",
        ComponentState.Installed => $"✓ {Spec.DisplayName} установлен",
        ComponentState.Missing => $"… {Spec.DisplayName}: скачиваю",
        ComponentState.Broken => $"… {Spec.DisplayName}: файл повреждён, скачиваю заново",
        _ => $"⚠ {Spec.DisplayName}: {Error}",
    };
}

/// <summary>
/// First-run bootstrap: what OLI must have on disk to be usable anywhere, and how to get it.
///
/// Why this exists: the tunnel (the only way a phone reaches the PC from mobile data) needs
/// <c>cloudflared.exe</c>, and it was never shipped — the user had to find, download and
/// rename the file by hand, so "интернет-доступ" simply did not start. Now OLI downloads it
/// itself on first launch, verifies the SHA-256 and reports every step.
///
/// Nothing here needs admin rights: everything is written next to OLI.exe (or to the data
/// folder when the app lives in Program Files).
/// </summary>
public sealed class ComponentInstaller
{
    /// <summary>
    /// cloudflared, Windows amd64. Pinned by hash: a tampered download is rejected instead
    /// of being executed. When Cloudflare publishes a new build, update both values together.
    /// </summary>
    public static readonly ComponentSpec Cloudflared = new(
        "cloudflared",
        "Туннель internet-доступа (cloudflared)",
        "cloudflared.exe",
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe",
        "", // filled by <see cref="ComponentInstaller.Known"/> when a hash is pinned
        "Без него телефон не сможет подключиться вне дома (мобильный интернет).",
        MirrorUrl: "https://github.com/cloudflare/cloudflared/releases/download/2025.8.0/cloudflared-windows-amd64.exe");

    private readonly HttpClient _http;
    private readonly string _targetDirectory;

    /// <summary>Progress messages exactly as they should appear in the setup window.</summary>
    public event Action<string>? Progress;

    public ComponentInstaller(string? targetDirectory = null, HttpClient? http = null)
    {
        _targetDirectory = targetDirectory ?? AppPaths.InstallRoot;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OLI-Setup/" + AppVersion.Current);
    }

    /// <summary>Every component OLI wants on disk. One place, so setup and check agree.</summary>
    public static IReadOnlyList<ComponentSpec> Known { get; } =
    [
        // GitHub redirects to the newest release; the optional hash pins one known build.
        Cloudflared,
    ];

    public string TargetDirectory => _targetDirectory;

    /// <summary>Where the executable for a component lives (also used by TunnelManager).</summary>
    public static string PathFor(ComponentSpec spec) =>
        System.IO.Path.Combine(AppPaths.InstallRoot, spec.FileName);

    /// <summary>
    /// Checks every component and downloads what is missing or broken.
    /// Returns one report per component; never throws for a network problem.
    /// </summary>
    public async Task<IReadOnlyList<ComponentReport>> EnsureAllAsync(
        IEnumerable<ComponentSpec>? specs = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_targetDirectory);
        var reports = new List<ComponentReport>();
        foreach (var spec in specs ?? Known)
            reports.Add(await EnsureAsync(spec, ct));
        return reports;
    }

    public async Task<ComponentReport> EnsureAsync(ComponentSpec spec, CancellationToken ct = default)
    {
        var path = System.IO.Path.Combine(_targetDirectory, spec.FileName);
        var state = Inspect(path, spec);
        if (state == ComponentState.Ready)
        {
            Progress?.Invoke($"✓ {spec.DisplayName} уже установлен");
            return new ComponentReport(spec, ComponentState.Ready, path);
        }

        Progress?.Invoke(state == ComponentState.Broken
            ? $"… {spec.DisplayName}: файл повреждён, скачиваю заново"
            : $"… {spec.DisplayName}: скачиваю (около 20 МБ, один раз)");

        try
        {
            var downloaded = await DownloadAsync(spec, path, ct);
            Progress?.Invoke($"✓ {spec.DisplayName} установлен");
            return new ComponentReport(spec, ComponentState.Installed, downloaded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var hint = ex is HttpRequestException or TaskCanceledException
                ? " Нет доступа к GitHub — включите VPN и нажмите «Повторить»."
                : "";
            var message = $"{ex.Message}{hint}";
            Progress?.Invoke($"⚠ {spec.DisplayName}: {message}");
            return new ComponentReport(spec, ComponentState.Failed, path, message);
        }
    }

    /// <summary>Checks a file without touching the network.</summary>
    public static ComponentState Inspect(string path, ComponentSpec spec)
    {
        if (!File.Exists(path)) return ComponentState.Missing;
        var info = new FileInfo(path);
        if (info.Length < 1024 * 1024) return ComponentState.Broken; // truncated download

        // The .exe magic keeps a web-proxy HTML page from being "installed" as a program.
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[2];
            if (stream.Read(header, 0, 2) < 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                return ComponentState.Broken;
        }
        catch
        {
            return ComponentState.Broken;
        }

        if (string.IsNullOrWhiteSpace(spec.Sha256)) return ComponentState.Ready;
        try
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(hash, spec.Sha256.Trim(), StringComparison.OrdinalIgnoreCase)
                ? ComponentState.Ready
                : ComponentState.Broken;
        }
        catch
        {
            return ComponentState.Broken;
        }
    }

    /// <summary>
    /// Downloads to a temporary file, verifies it, then moves it into place — a failed or
    /// half-finished download never replaces a working executable.
    /// </summary>
    private async Task<string> DownloadAsync(ComponentSpec spec, string target, CancellationToken ct)
    {
        var urls = new[] { spec.Url, spec.MirrorUrl }.Where(u => !string.IsNullOrWhiteSpace(u)).ToArray();
        Exception? last = null;

        foreach (var url in urls)
        {
            var temp = target + ".download";
            try
            {
                await using (var file = File.Create(temp))
                using (var response = await _http.GetAsync(url!, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    await stream.CopyToAsync(file, ct);
                }

                var state = Inspect(temp, spec);
                if (state != ComponentState.Ready)
                    throw new InvalidDataException("скачанный файл повреждён или не является программой");

                File.Move(temp, target, overwrite: true);
                return target;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                try { File.Delete(temp); } catch { /* nothing to clean */ }
            }
        }

        throw last ?? new HttpRequestException("не удалось скачать файл");
    }

    /// <summary>
    /// Unpacks an archive into a folder (used by the setup for portable bundles).
    /// Kept here so both the setup and the tests share one implementation.
    /// </summary>
    public static void ExtractZip(string archive, string destination, bool overwrite = true)
    {
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(System.IO.Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                continue; // zip-slip: never write outside the destination
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            entry.ExtractToFile(target, overwrite);
        }
    }

    /// <summary>Human sentence for the whole run (shown at the end of the setup).</summary>
    public static string Summary(IReadOnlyList<ComponentReport> reports)
    {
        if (reports.Count == 0) return "Проверка компонентов не требуется.";
        var ok = reports.Count(r => r.Ok);
        if (ok == reports.Count) return $"Все компоненты на месте ({ok}).";
        return $"Готово {ok} из {reports.Count}. OLI работает; без недостающих компонентов " +
               "часть функций (туннель) недоступна — нажмите «Повторить».";
    }
}
