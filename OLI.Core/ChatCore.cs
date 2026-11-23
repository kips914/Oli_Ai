using System.Text;

namespace OLI.Core;

/// <summary>Who wrote a message. Mirrors the wire format used by the API and the app.</summary>
public enum ChatRole { User, Assistant, System }

/// <summary>
/// The one chat message shape used by the desktop app, the server and (through the API)
/// the phone. Replaces the previous mix of <c>ChatMessage</c>, <c>StoredMessage</c> and
/// three different JSON shapes that disagreed about ids, timestamps and roles.
/// </summary>
public sealed record ChatEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public ChatRole Role { get; init; } = ChatRole.User;
    /// <summary>Plain text of the message. Never null.</summary>
    public string Text { get; init; } = "";
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Workspace paths the answer produced (files, archives).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>Set while the answer is still being generated; never persisted.</summary>
    public bool Pending { get; init; }
    /// <summary>Engine or server error attached to this message (shown in red).</summary>
    public string? Error { get; init; }

    public static ChatEntry User(string text) =>
        new() { Role = ChatRole.User, Text = ChatCore.Clean(text) };

    public static ChatEntry Assistant(string text, IReadOnlyList<string>? files = null) =>
        new() { Role = ChatRole.Assistant, Text = ChatCore.Clean(text), Files = files ?? [] };

    public static ChatEntry Failure(string text) =>
        new() { Role = ChatRole.Assistant, Text = "", Error = ChatCore.Clean(text) };

    public static ChatEntry PendingAnswer() =>
        new() { Role = ChatRole.Assistant, Pending = true };
}

/// <summary>A saved conversation. <see cref="Messages"/> is empty in list responses.</summary>
public sealed record ChatThreadSummary(
    string Id,
    string Title,
    string ModelId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount);

/// <summary>
/// Rules the whole product shares: what a thread title is, how history is trimmed for
/// the model context, how the visible transcript is shaped. Pure functions, fully tested.
/// </summary>
public static class ChatCore
{
    public const int MaxTitleLength = 60;
    public const int MaxMessagesPerThread = 400;
    public const int MaxTextPerMessage = 20_000;
    /// <summary>Messages handed to the model (older ones are dropped, newest win).</summary>
    public const int ContextMessages = 24;

    /// <summary>Normalises whitespace, strips control characters, enforces the length cap.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\r') continue;
            // Keep \n and \t; drop other control characters that break JSON and UI.
            if (char.IsControl(c) && c is not ('\n' or '\t')) continue;
            builder.Append(c);
        }
        var result = builder.ToString().Trim();
        return result.Length <= MaxTextPerMessage ? result : result[..MaxTextPerMessage].TrimEnd() + "…";
    }

    /// <summary>First user message becomes the title; the drawer shows one line.</summary>
    public static string MakeTitle(string? firstMessage, string fallback = "Новый чат")
    {
        var text = (firstMessage ?? "").Replace('\n', ' ').Trim();
        if (text.Length == 0) return fallback;
        // A question mark or an ellipsis is not punctuation we want at the cut.
        var cut = text.Length <= MaxTitleLength ? text : text[..MaxTitleLength];
        cut = cut.TrimEnd(' ', '.', ',', ':', ';', '!', '?', '…');
        if (cut.Length == 0) return fallback;
        return text.Length <= MaxTitleLength ? cut : cut + "…";
    }

    /// <summary>
    /// Keeps the transcript inside the storage cap: newest messages survive, but the
    /// first user message is kept so the thread keeps its title and its context.
    /// </summary>
    public static IReadOnlyList<ChatEntry> Trim(IReadOnlyList<ChatEntry> messages)
    {
        if (messages.Count <= MaxMessagesPerThread) return messages;

        var firstUser = messages.FirstOrDefault(m => m.Role == ChatRole.User);
        var tail = messages.TakeLast(MaxMessagesPerThread - (firstUser is null ? 0 : 1)).ToList();
        if (firstUser is not null && !tail.Contains(firstUser)) tail.Insert(0, firstUser);
        return tail;
    }

    /// <summary>
    /// History handed to the model: no failures, no pending bubbles, no system noise,
    /// newest <see cref="ContextMessages"/> messages in chronological order.
    /// </summary>
    public static IReadOnlyList<ChatMessage> ToModelHistory(IReadOnlyList<ChatEntry> messages)
    {
        return messages
            .Where(m => m.Role != ChatRole.System && !m.Pending && m.Error is null)
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .TakeLast(ContextMessages)
            .Select(m => new ChatMessage(m.Role == ChatRole.User ? "user" : "assistant", m.Text))
            .ToList();
    }

    /// <summary>
    /// Text shown to the user. Assistant messages that arrived empty (a stream that died)
    /// are replaced by an explicit notice instead of an invisible bubble.
    /// </summary>
    public static string VisibleText(ChatEntry entry) => entry switch
    {
        { Error: { Length: > 0 } error } => "⚠ " + error,
        { Pending: true } => "",
        { Text.Length: > 0 } => entry.Text,
        _ when entry.Role == ChatRole.Assistant => "⚠ Пустой ответ модели — попробуйте ещё раз.",
        _ => "",
    };

    /// <summary>True when the message is worth saving to disk.</summary>
    public static bool IsPersistable(ChatEntry entry) =>
        !entry.Pending && !string.IsNullOrWhiteSpace(entry.Text) && entry.Role != ChatRole.System;
}
