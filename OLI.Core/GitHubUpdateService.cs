using System.Net.Http.Headers;
using System.Text.Json;

namespace OLI.Core;

public sealed record GitHubReleaseInfo(
    string TagName,
    string Name,
    string HtmlUrl,
    string Body,
    bool Prerelease,
    DateTimeOffset? PublishedAt);

public sealed record UpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    GitHubReleaseInfo? Release,
    string? Error)
{
    public static UpdateCheckResult Failure(string currentVersion, string error) =>
        new(false, false, currentVersion, null, null, error);
}

public sealed class GitHubUpdateService : IDisposable
{
    private readonly HttpClient _http;
    private bool _disposed;

    public GitHubUpdateService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OLI", AppVersion.Current));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _http.Timeout = TimeSpan.FromSeconds(8);
    }

    public async Task<UpdateCheckResult> CheckAsync(string repositoryUrl, CancellationToken cancellationToken = default)
    {
        var current = AppVersion.Current;
        if (!TryBuildLatestReleaseUri(repositoryUrl, out var uri, out var error))
            return UpdateCheckResult.Failure(current, error!);

        try
        {
            using var response = await _http.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failure(current, $"GitHub вернул HTTP {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
                return UpdateCheckResult.Failure(current, "GitHub Release не содержит tag_name.");

            var latest = AppVersion.Normalize(tag);
            if (latest is null)
                return UpdateCheckResult.Failure(current, $"Не удалось распознать версию GitHub: {tag}");

            var release = new GitHubReleaseInfo(
                tag,
                root.TryGetProperty("name", out var name) ? name.GetString() ?? tag : tag,
                root.TryGetProperty("html_url", out var html) ? html.GetString() ?? repositoryUrl : repositoryUrl,
                root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean(),
                root.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String
                    ? published.GetDateTimeOffset()
                    : null);

            var currentVersion = AppVersion.Normalize(current) ?? new Version(0, 0, 0);
            var available = latest > currentVersion;
            return new UpdateCheckResult(true, available, current, latest.ToString(3), release, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failure(current, "Проверка обновлений превысила время ожидания.");
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failure(current, $"Не удалось проверить GitHub: {ex.Message}");
        }
    }

    private static bool TryBuildLatestReleaseUri(string repositoryUrl, out Uri uri, out string? error)
    {
        uri = default!;
        error = null;
        if (!Uri.TryCreate(repositoryUrl?.Trim(), UriKind.Absolute, out var repo) || repo.Host != "github.com")
        {
            error = "Укажите корректный URL репозитория GitHub, например https://github.com/USER/OLI.";
            return false;
        }

        var parts = repo.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            error = "URL GitHub должен иметь формат https://github.com/USER/REPOSITORY.";
            return false;
        }

        uri = new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/releases/latest");
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}

public static class AppVersion
{
    // Keep this synchronized with VERSION and the client assembly version.
    public const string Current = "1.8.0";

    public static Version? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        var separator = text.IndexOfAny(['_', '-', '+']);
        if (separator >= 0) text = text[..separator];
        return Version.TryParse(text, out var version) ? version : null;
    }
}
