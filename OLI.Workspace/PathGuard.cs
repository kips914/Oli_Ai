namespace OLI.Workspace;

/// <summary>
/// Turns model-supplied relative paths into absolute paths that are guaranteed to
/// stay inside the workspace root. Rejects absolute paths, drive letters, ".."
/// traversal, UNC paths, symlinks pointing outside and control characters.
/// </summary>
public static class PathGuard
{
    private static readonly char[] InvalidChars = ['\0', '\r', '\n', '<', '>', '|', '"'];

    public static string Resolve(string root, string? relativePath, bool allowRoot = false)
    {
        var fullRoot = Path.GetFullPath(root);
        var raw = (relativePath ?? "").Trim().Replace('\\', '/');

        if (raw.Length == 0 || raw == "." || raw == "/")
        {
            if (!allowRoot) throw new WorkspaceException("Нужно указать путь к файлу внутри workspace.");
            return fullRoot;
        }

        if (raw.Contains(InvalidChars)) throw new WorkspaceException("Путь содержит недопустимые символы.");
        if (raw.Contains("://", StringComparison.Ordinal)) throw new WorkspaceException("Путь не может быть URL.");
        if (raw.StartsWith("~", StringComparison.Ordinal)) throw new WorkspaceException("Путь не может начинаться с '~'.");
        if (raw.Any(char.IsControl)) throw new WorkspaceException("Путь содержит управляющие символы.");
        if (Path.IsPathRooted(raw)) throw new WorkspaceException("Абсолютные пути запрещены — только пути внутри workspace.");
        if (raw.Length >= 2 && raw[1] == ':' ) throw new WorkspaceException("Диск/буква диска запрещены.");
        if (raw.Split('/').Any(seg => seg == "..")) throw new WorkspaceException("Выход за пределы workspace ('..') запрещён.");

        var combined = Path.GetFullPath(Path.Combine(fullRoot, raw));

        // Final belt-and-braces containment check (also catches symlinked parents).
        if (!IsInside(fullRoot, combined))
            throw new WorkspaceException("Путь выходит за пределы workspace.");

        return combined;
    }

    public static bool IsInside(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        if (fullCandidate.Length + 1 == fullRoot.Length &&
            fullCandidate.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return true;
        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>Windows-reserved device names cannot be created as files.</summary>
    public static void EnsureCreatableFileName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (reserved.Contains(stem)) throw new WorkspaceException($"Имя «{name}» зарезервировано Windows.");
        if (name.EndsWith('.') || name.EndsWith(' ')) throw new WorkspaceException("Имя файла не может заканчиваться точкой или пробелом.");
        if (name.Length > 180) throw new WorkspaceException("Слишком длинное имя файла.");
    }

    /// <summary>Sanitises an external/generated name into a safe single file name.</summary>
    public static string SafeFileName(string? name, string fallback = "file")
    {
        var cleaned = new string((name ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c is not ('/' or '\\')).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = fallback;
        if (cleaned.Length > 180) cleaned = cleaned[..180];
        var stem = Path.GetFileNameWithoutExtension(cleaned).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL") cleaned = "_" + cleaned;
        return cleaned;
    }
}
