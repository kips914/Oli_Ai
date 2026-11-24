using System.Text.Json;
using OLI.Core;

namespace OLI.Memory;

public sealed record MemoryItem(string Id, string Text, string Category, double Confidence, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed class MemoryStore
{
    private readonly string _file;
    private readonly List<MemoryItem> _items;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MemoryStore(string file)
    {
        _file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        try { _items = File.Exists(_file) ? JsonSerializer.Deserialize<List<MemoryItem>>(File.ReadAllText(_file)) ?? [] : []; }
        catch { _items = []; OliLog.Warning("MEMORY", "Файл памяти повреждён; создана пустая память"); }
    }

    public IReadOnlyList<MemoryItem> Items => _items;

    public async Task AddAsync(string text, string category = "general", double confidence = 0.7)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        await _gate.WaitAsync();
        try
        {
            var existing = _items.FirstOrDefault(x => x.Text.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return;
            _items.Add(new MemoryItem(Guid.NewGuid().ToString("N"), text.Trim(), category, Math.Clamp(confidence, 0, 1), DateTimeOffset.Now, DateTimeOffset.Now));
            await SaveAsyncCore();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync();
        try { await SaveAsyncCore(); }
        finally { _gate.Release(); }
    }

    private Task SaveAsyncCore()
    {
        var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
        SettingsStore.AtomicWrite(_file, json);
        return Task.CompletedTask;
    }

    public string BuildContext(int maxItems = 8)
    {
        return string.Join(Environment.NewLine,
            _items.OrderByDescending(x => x.UpdatedAt).Take(maxItems).Select(x => $"- [{x.Category}] {x.Text}"));
    }
}
