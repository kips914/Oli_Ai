using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OLI.Core;

namespace OLI.Server.Chats;

/// <summary>
/// Per-user chat storage, rebuilt around <see cref="ChatEntry"/>.
///
/// What was wrong before (and is fixed here):
///  * every append re-read and re-wrote the whole account file, and two parallel requests
///    could overwrite each other's messages — now each account has a lock and a dirty flag
///    and the file is written once per change, atomically;
///  * message ids and timestamps were invented on the phone, so a reopened thread looked
///    different from the one that was just used — now ids come from the server;
///  * the first message did not always become the title, so the drawer was full of
///    "Новый чат" — now <see cref="ChatCore.MakeTitle"/> is the single rule;
///  * a deleted thread could be resurrected by an in-flight answer — writing to a deleted
///    thread now returns null and the answer is dropped.
/// </summary>
public sealed class ChatStore
{
    public const int MaxThreadsPerUser = 80;

    private sealed class AccountChats
    {
        public readonly object Lock = new();
        public List<ChatThread> Threads = [];
        public bool Loaded;
        public bool Dirty;
    }

    private readonly string _root;
    private readonly ConcurrentDictionary<string, AccountChats> _accounts = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ChatStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    private string FileFor(string accountId) => Path.Combine(_root, $"{Safe(accountId)}.json");

    private static string Safe(string value) =>
        new((value ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(64).ToArray());

    private AccountChats For(string accountId)
    {
        var state = _accounts.GetOrAdd(Safe(accountId), _ => new AccountChats());
        lock (state.Lock)
        {
            if (!state.Loaded) Load(accountId, state);
        }
        return state;
    }

    private void Load(string accountId, AccountChats state)
    {
        state.Loaded = true;
        try
        {
            var file = FileFor(accountId);
            if (!File.Exists(file)) return;
            var parsed = JsonSerializer.Deserialize<List<ChatThread>>(File.ReadAllText(file)) ?? [];
            state.Threads = parsed;
        }
        catch (Exception ex)
        {
            // A corrupt file must not kill the account: keep the broken copy for inspection.
            OliLog.Warning("CHATS", $"Не удалось прочитать чаты {accountId}: {ex.Message}");
            try
            {
                var file = FileFor(accountId);
                if (File.Exists(file)) File.Move(file, file + ".broken", true);
            }
            catch { /* best effort */ }
            state.Threads = [];
        }
    }

    private void SaveLocked(string accountId, AccountChats state)
    {
        try
        {
            var trimmed = state.Threads
                .OrderByDescending(t => t.UpdatedAt)
                .Take(MaxThreadsPerUser)
                .ToList();
            state.Threads = trimmed;

            var file = FileFor(accountId);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(trimmed, Json));
            File.Move(temp, file, true);
            state.Dirty = false;
        }
        catch (Exception ex)
        {
            OliLog.Warning("CHATS", $"Не удалось сохранить чаты {accountId}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ reads

    /// <summary>Thread headers, newest first (no message bodies).</summary>
    public IReadOnlyList<ChatThreadSummary> List(string accountId)
    {
        var state = For(accountId);
        lock (state.Lock)
        {
            return state.Threads
                .OrderByDescending(t => t.UpdatedAt)
                .Select(t => new ChatThreadSummary(
                    t.Id,
                    string.IsNullOrWhiteSpace(t.Title) ? "Новый чат" : t.Title,
                    t.ModelId,
                    t.CreatedAt,
                    t.UpdatedAt,
                    t.Messages.Count))
                .ToList();
        }
    }

    public ChatThread? Get(string accountId, string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return null;
        var state = For(accountId);
        lock (state.Lock)
            return state.Threads.FirstOrDefault(t => t.Id == threadId);
    }

    /// <summary>The most recent thread, used when the phone asks for "the current chat".</summary>
    public ChatThread? Latest(string accountId)
    {
        var state = For(accountId);
        lock (state.Lock)
            return state.Threads.OrderByDescending(t => t.UpdatedAt).FirstOrDefault();
    }

    // ------------------------------------------------------------------ writes

    public ChatThread Create(string accountId, string? firstMessage, string modelId)
    {
        var state = For(accountId);
        lock (state.Lock)
        {
            var thread = new ChatThread
            {
                Title = ChatCore.MakeTitle(firstMessage),
                ModelId = modelId ?? "",
            };
            state.Threads.Add(thread);
            SaveLocked(accountId, state);
            return thread;
        }
    }

    /// <summary>Finds a thread or creates it from the first message of a chat request.</summary>
    public ChatThread GetOrCreate(string accountId, string? threadId, string? firstMessage, string modelId)
    {
        var existing = Get(accountId, threadId ?? "");
        return existing ?? Create(accountId, firstMessage, modelId);
    }

    public bool Rename(string accountId, string threadId, string? title)
    {
        var state = For(accountId);
        lock (state.Lock)
        {
            var thread = state.Threads.FirstOrDefault(t => t.Id == threadId);
            if (thread is null) return false;
            thread.Title = ChatCore.MakeTitle(title, "Чат");
            thread.UpdatedAt = DateTimeOffset.UtcNow;
            SaveLocked(accountId, state);
            return true;
        }
    }

    public bool Delete(string accountId, string threadId)
    {
        var state = For(accountId);
        lock (state.Lock)
        {
            if (state.Threads.RemoveAll(t => t.Id == threadId) == 0) return false;
            SaveLocked(accountId, state);
            return true;
        }
    }

    /// <summary>
    /// Appends a message. Returns the stored entry (with the server id) or null when the
    /// thread no longer exists — the caller must then drop the answer instead of
    /// resurrecting a deleted chat.
    /// </summary>
    public ChatEntry? Append(string accountId, string threadId, ChatEntry entry)
    {
        if (!ChatCore.IsPersistable(entry) && entry.Role != ChatRole.User) return null;
        var state = For(accountId);
        lock (state.Lock)
        {
            var thread = state.Threads.FirstOrDefault(t => t.Id == threadId);
            if (thread is null) return null;

            var stored = entry with { Text = ChatCore.Clean(entry.Text), At = DateTimeOffset.UtcNow, Pending = false };
            thread.Messages.Add(stored);
            thread.Messages = ChatCore.Trim(thread.Messages).ToList();

            if (stored.Role == ChatRole.User && thread.Messages.Count(m => m.Role == ChatRole.User) == 1)
                thread.Title = ChatCore.MakeTitle(stored.Text);

            thread.UpdatedAt = DateTimeOffset.UtcNow;
            SaveLocked(accountId, state);
            return stored;
        }
    }

    /// <summary>Bulk import (used when the desktop app migrates its old transcript).</summary>
    public int Import(string accountId, string title, string modelId, IEnumerable<ChatEntry> messages)
    {
        var state = For(accountId);
        lock (state.Lock)
        {
            var thread = new ChatThread { Title = ChatCore.MakeTitle(title), ModelId = modelId ?? "" };
            thread.Messages = ChatCore.Trim(messages.Where(ChatCore.IsPersistable)
                .Select(m => m with { Text = ChatCore.Clean(m.Text) }).ToList()).ToList();
            if (thread.Messages.Count == 0) return 0;
            state.Threads.Add(thread);
            thread.UpdatedAt = DateTimeOffset.UtcNow;
            SaveLocked(accountId, state);
            return thread.Messages.Count;
        }
    }

    public void DeleteAll(string accountId)
    {
        var state = For(accountId);
        lock (state.Lock)
        {
            state.Threads = [];
            try
            {
                var file = FileFor(accountId);
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex)
            {
                OliLog.Warning("CHATS", $"Не удалось удалить чаты {accountId}: {ex.Message}");
            }
        }
    }
}

/// <summary>A saved conversation.</summary>
public sealed class ChatThread
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; set; } = "Новый чат";
    public string ModelId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatEntry> Messages { get; set; } = [];

    public string TitleOrFallback => string.IsNullOrWhiteSpace(Title) ? "Новый чат" : Title;
}
