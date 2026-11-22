using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OLI.Server.Usage;

/// <summary>
/// Cheap token estimator (≈4 characters per token for mixed Russian/English text).
/// Used when the model server does not report usage and to pre-check requests.
/// </summary>
public static class TokenEstimator
{
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var chars = 0;
        var wide = 0;
        foreach (var c in text)
        {
            chars++;
            if (c > 0x2E80) wide++; // CJK & friends need more tokens per character
        }
        return Math.Max(1, (chars + wide * 2) / 4 + 2);
    }

    public static int Estimate(IEnumerable<string> parts) => parts.Sum(Estimate);
}

public sealed class UsageEntry
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string AccountId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int Total => PromptTokens + CompletionTokens;
}

/// <summary>Rolling per-user token usage (hour + day windows), persisted to JSON.</summary>
public sealed class UsageStore
{
    private readonly string _file;
    private readonly object _gate = new();
    private readonly List<UsageEntry> _entries = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public UsageStore(string file)
    {
        _file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var list = JsonSerializer.Deserialize<List<UsageEntry>>(File.ReadAllText(_file));
            if (list is not null)
            {
                // Keep only the last 3 days so the file stays small.
                var cutoff = DateTimeOffset.UtcNow.AddDays(-3);
                _entries.AddRange(list.Where(e => e.At >= cutoff));
            }
        }
        catch
        {
            // A broken usage file must never block chat; start clean.
        }
    }

    private void Persist()
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-3);
            _entries.RemoveAll(e => e.At < cutoff);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, Json));
            File.Move(temp, _file, true);
        }
        catch
        {
        }
    }

    public void Add(UsageEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            Persist();
        }
    }

    public (int Hour, int Day) For(string accountId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var day = _entries
                .Where(e => e.AccountId == accountId && e.At >= now.AddDays(-1))
                .Sum(e => e.Total);
            var hour = _entries
                .Where(e => e.AccountId == accountId && e.At >= now.AddHours(-1))
                .Sum(e => e.Total);
            return (hour, day);
        }
    }

    public int TotalFor(string accountId)
    {
        lock (_gate) return _entries.Where(e => e.AccountId == accountId).Sum(e => e.Total);
    }

    /// <summary>Number of distinct users active in the last 10 minutes — drives the dynamic limit.</summary>
    public int ActiveUsers(int withinMinutes = 10)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
            return _entries
                .Where(e => e.At >= now.AddMinutes(-withinMinutes))
                .Select(e => e.AccountId)
                .Distinct()
                .Count();
    }

    public IReadOnlyList<UsageEntry> Recent(string accountId, int take = 20)
    {
        lock (_gate)
            return _entries.Where(e => e.AccountId == accountId).TakeLast(take).Reverse().ToList();
    }
}

/// <summary>
/// Dynamic limit: the more people are using the server at once, the smaller the
/// per-user allowance (tokens in + out). A minimum stays for everyone.
/// </summary>
public sealed class LimitPolicy
{
    /// <summary>Allowance when the user is alone on the server.</summary>
    public int SoloHourly { get; set; } = 150_000;
    public int SoloDaily { get; set; } = 600_000;
    /// <summary>Never go below this, otherwise the app becomes unusable.</summary>
    public int MinHourly { get; set; } = 20_000;
    public int MinDaily { get; set; } = 80_000;

    /// <summary>How many concurrent users we consider "busy" for scaling.</summary>
    public int BusyUsers { get; set; } = 8;

    public (int Hourly, int Daily) Allowance(int activeUsers)
    {
        var users = Math.Clamp(activeUsers, 1, BusyUsers);
        // Linear scale between solo and min allowance.
        var t = (users - 1) / (double)Math.Max(1, BusyUsers - 1);
        var hourly = (int)Math.Round(SoloHourly + (MinHourly - SoloHourly) * t);
        var daily = (int)Math.Round(SoloDaily + (MinDaily - SoloDaily) * t);
        return (Math.Max(MinHourly, hourly), Math.Max(MinDaily, daily));
    }
}
