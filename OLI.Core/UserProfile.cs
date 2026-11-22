using System.Text.Json;

namespace OLI.Core;

public sealed class UserProfile
{
    public string? Name { get; set; }
    public int? Age { get; set; }
    public List<string> Interests { get; set; } = [];
    public List<string> Skills { get; set; } = [];
    public List<string> Projects { get; set; } = [];

    public string BuildContext()
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(Name)) lines.Add($"Имя: {Name}");
        if (Age is not null) lines.Add($"Возраст: {Age}");
        if (Interests.Count > 0) lines.Add("Интересы: " + string.Join(", ", Interests));
        if (Skills.Count > 0) lines.Add("Навыки: " + string.Join(", ", Skills));
        if (Projects.Count > 0) lines.Add("Проекты: " + string.Join(", ", Projects));
        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class UserProfileStore
{
    private readonly string _file;
    public UserProfile Profile { get; private set; }

    public UserProfileStore(string file)
    {
        _file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        try { Profile = File.Exists(_file) ? JsonSerializer.Deserialize<UserProfile>(File.ReadAllText(_file)) ?? new() : new(); }
        catch (Exception ex) { OliLog.Warning("PROFILE", $"Не удалось прочитать профиль: {ex.Message}"); Profile = new(); }
    }

    public void Save() => SettingsStore.AtomicWrite(_file, JsonSerializer.Serialize(Profile, new JsonSerializerOptions { WriteIndented = true }));
    public void Reset() { Profile = new(); Save(); }

    public void LearnFrom(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(?:меня зовут|моё имя|мое имя)\s+([\p{L}][\p{L}\-]{1,30})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) Profile.Name = m.Groups[1].Value.Trim();
        m = System.Text.RegularExpressions.Regex.Match(text, @"(?:мне|мне уже)\s+(\d{1,3})\s*(?:лет|год|года)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var age) && age is >= 5 and <= 120) Profile.Age = age;

        var interest = System.Text.RegularExpressions.Regex.Match(text, @"(?:интересуюсь|увлекаюсь|люблю|нравится)\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (interest.Success)
        {
            var value = interest.Groups[1].Value.Trim().TrimEnd('.', '!', '?');
            if (value.Length <= 120 && !Profile.Interests.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase))) Profile.Interests.Add(value);
        }
        Save();
    }
}
