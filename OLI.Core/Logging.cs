using System.Collections.Concurrent;

namespace OLI.Core;

public enum OliLogLevel { Debug, Info, Warning, Error }

public sealed record OliLogEntry(DateTimeOffset Time, OliLogLevel Level, string Source, string Message);

public static class OliLog
{
    private static readonly ConcurrentQueue<OliLogEntry> Entries = new();
    public static event Action<OliLogEntry>? EntryAdded;

    public static IReadOnlyCollection<OliLogEntry> Snapshot() => Entries.ToArray();

    public static void Write(OliLogLevel level, string source, string message)
    {
        var entry = new OliLogEntry(DateTimeOffset.Now, level, source, message);
        Entries.Enqueue(entry);
        while (Entries.Count > 2000 && Entries.TryDequeue(out _)) { }
        try
        {
            AppPaths.EnsureDirectories();
            File.AppendAllText(Path.Combine(AppPaths.LogsRoot, "oli.log"),
                $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff zzz} | {level.ToString().ToUpperInvariant()} | {source} | {message}{Environment.NewLine}");
        }
        catch { }
        EntryAdded?.Invoke(entry);
    }

    public static void Debug(string source, string message) => Write(OliLogLevel.Debug, source, message);
    public static void Info(string source, string message) => Write(OliLogLevel.Info, source, message);
    public static void Warning(string source, string message) => Write(OliLogLevel.Warning, source, message);
    public static void Error(string source, string message) => Write(OliLogLevel.Error, source, message);
}
