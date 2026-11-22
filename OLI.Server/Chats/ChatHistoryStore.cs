using System.Text.Json;
using System.Text.Json.Serialization;

namespace OLI.Server.Chats;

public sealed class StoredMessage
{
    public string Role { get; set; } = "user"; // user | assistant
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A saved conversation; the phone shows these in the "чаты" drawer.</summary>
public sealed class ChatThread
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; set; } = "Новый чат";
    public string ModelId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<StoredMessage> Messages { get; set; } = [];

    public string TitleOrFallback => string.IsNullOrWhiteSpace(Title) ? "Новый чат" : Title;
}

/// <summary>
/// Per-user chat storage on the server (JSON per account, capped at
/// <see cref="MaxThreadsPerUser"/> threads and trimmed message bodies).
/// </summary>
public sealed class ChatHistoryStore
{
    public const int MaxThreadsPerUser = 80;
    public const int MaxMessagesPerThread = 200;
    public const int MaxTextLength = 20_000;

    private readonly string _root;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ChatHistoryStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    private string FileFor(string accountId) => Path.Combine(_root, $"{Sanitize(accountId)}.json");

    private static string Sanitize(string value) =>
        new(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    private List<ChatThread> Load(string accountId)
    {
        try
        {
            var file = FileFor(accountId);
            if (!File.Exists(file)) return [];
            return JsonSerializer.Deserialize<List<ChatThread>>(File.ReadAllText(file)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void Save(string accountId, List<ChatThread> threads)
    {
        var trimmed = threads
            .OrderByDescending(t => t.UpdatedAt)
            .Take(MaxThreadsPerUser)
            .ToList();

        var temp = FileFor(accountId) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(trimmed, Json));
        File.Move(temp, FileFor(accountId), true);
    }

    /// <summary>Threads without message bodies — the drawer only needs the headers.</summary>
    public IReadOnlyList<ChatThread> List(string accountId)
    {
        var threads = Load(accountId).OrderByDescending(t => t.UpdatedAt).ToList();
        foreach (var thread in threads) thread.Messages = [];
        return threads;
    }

    public ChatThread? Get(string accountId, string threadId) =>
        Load(accountId).FirstOrDefault(t => t.Id == threadId);

    public ChatThread Create(string accountId, string? title, string modelId)
    {
        var threads = Load(accountId);
        var thread = new ChatThread
        {
            Title = Shorten(title ?? "Новый чат", 60),
            ModelId = modelId,
        };
        threads.Add(thread);
        Save(accountId, threads);
        return thread;
    }

    public bool Rename(string accountId, string threadId, string title)
    {
        var threads = Load(accountId);
        var thread = threads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return false;
        thread.Title = Shorten(title, 60);
        thread.UpdatedAt = DateTimeOffset.UtcNow;
        Save(accountId, threads);
        return true;
    }

    public bool Delete(string accountId, string threadId)
    {
        var threads = Load(accountId);
        if (threads.RemoveAll(t => t.Id == threadId) == 0) return false;
        Save(accountId, threads);
        return true;
    }

    /// <summary>Appends a message; the first user message becomes the thread title.</summary>
    public ChatThread? Append(string accountId, string threadId, StoredMessage message)
    {
        var threads = Load(accountId);
        var thread = threads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return null;

        message.Text = Shorten(message.Text, MaxTextLength);
        thread.Messages.Add(message);
        if (thread.Messages.Count > MaxMessagesPerThread)
            thread.Messages = thread.Messages.TakeLast(MaxMessagesPerThread).ToList();

        if (message.Role == "user" && (thread.Title == "Новый чат" || string.IsNullOrWhiteSpace(thread.Title)))
            thread.Title = Shorten(message.Text, 48);

        thread.UpdatedAt = DateTimeOffset.UtcNow;
        Save(accountId, threads);
        return thread;
    }

    public void DeleteAll(string accountId)
    {
        try
        {
            if (File.Exists(FileFor(accountId))) File.Delete(FileFor(accountId));
        }
        catch
        {
        }
    }

    private static string Shorten(string text, int max)
    {
        var clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length <= max ? clean : clean[..max].TrimEnd() + "…";
    }
}
