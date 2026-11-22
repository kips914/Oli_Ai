namespace OLI.Core;

public sealed class ContextBuilder
{
    public IReadOnlyList<ChatMessage> Build(IReadOnlyList<ChatMessage> history, string systemPrompt, int maxContextTokens)
    {
        var result = new List<ChatMessage> { new("system", systemPrompt) };
        if (history.Count == 0) return result;

        // Conservative character budget: roughly 4 chars/token. Keep the newest turns.
        var budgetChars = Math.Max(4000, maxContextTokens * 4);
        var used = systemPrompt.Length;
        var selected = new List<ChatMessage>();
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var message = history[i];
            var cost = message.Content.Length + 40;
            if (selected.Count > 0 && used + cost > budgetChars) break;
            selected.Add(message);
            used += cost;
        }
        selected.Reverse();
        result.AddRange(selected);
        return result;
    }
}
