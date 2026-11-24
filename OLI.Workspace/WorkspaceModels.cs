namespace OLI.Workspace;

public sealed record WorkspaceEntry(
    string RelativePath,
    bool IsDirectory,
    long Size,
    DateTimeOffset ModifiedUtc);

public sealed record WorkspaceUsage(
    long UsedBytes,
    long QuotaBytes,
    int FileCount,
    int ArchiveCount)
{
    public double UsedPercent => QuotaBytes <= 0 ? 0 : Math.Round(UsedBytes * 100.0 / QuotaBytes, 1);
    public long FreeBytes => Math.Max(0, QuotaBytes - UsedBytes);
}

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration, bool TimedOut);

/// <summary>Raised for anything the model tried to do that the workspace refuses.</summary>
public sealed class WorkspaceException(string message) : Exception(message);
