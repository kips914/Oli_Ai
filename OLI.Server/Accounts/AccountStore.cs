using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OLI.Core;

namespace OLI.Server.Accounts;

/// <summary>
/// A registered user. Only a PBKDF2 hash of the password is ever stored — the
/// plaintext never touches the disk and the admin cannot recover it.
/// </summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserName { get; set; } = "";
    public string NormalizedUserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public int PasswordIterations { get; set; } = PasswordHasher.Iterations;
    public int PasswordVersion { get; set; } = PasswordHasher.Version;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public bool Disabled { get; set; }
    /// <summary>Per-user workspace allowance. 1 GB by default.</summary>
    public long QuotaBytes { get; set; } = 1024L * 1024 * 1024;
    public bool IsAdmin { get; set; }
    /// <summary>Chosen model id ("oli-pass" | "oli-son"); empty = server default.</summary>
    public string ModelId { get; set; } = "";
}

/// <summary>An issued bearer token. Only its SHA-256 hash is stored.</summary>
public sealed class TokenRecord
{
    public string TokenHash { get; set; } = "";
    public string AccountId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(30);
    public string Device { get; set; } = "";
}

public sealed class AccountDatabase
{
    public List<Account> Accounts { get; set; } = [];
    public List<TokenRecord> Tokens { get; set; } = [];
    public int SchemaVersion { get; set; } = 1;
}

/// <summary>PBKDF2-SHA256 password hashing with a per-user random salt.</summary>
public static class PasswordHasher
{
    public const int Iterations = 210_000;
    public const int SaltBytes = 16;
    public const int HashBytes = 32;
    public const int Version = 1;

    public static (string Hash, string Salt) Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, Account account)
    {
        try
        {
            var salt = Convert.FromBase64String(account.PasswordSalt);
            var expected = Convert.FromBase64String(account.PasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt,
                Math.Clamp(account.PasswordIterations, 10_000, 2_000_000),
                HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Cheap strength gate so accounts are not trivially guessable.</summary>
    public static string? Validate(string password)
    {
        if (string.IsNullOrWhiteSpace(password)) return "Пароль не может быть пустым.";
        if (password.Length < 8) return "Пароль должен быть не короче 8 символов.";
        if (password.Length > 256) return "Пароль слишком длинный.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return "Пароль должен содержать и буквы, и цифры.";
        return null;
    }
}

/// <summary>File-backed account/token store with atomic writes and in-memory locking.</summary>
public sealed class AccountStore
{
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AccountDatabase _db = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AccountStore(string file)
    {
        _file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        Load();
    }

    private void Load()
    {
        try
        {
            _db = File.Exists(_file)
                ? JsonSerializer.Deserialize<AccountDatabase>(File.ReadAllText(_file)) ?? new AccountDatabase()
                : new AccountDatabase();
        }
        catch (Exception ex)
        {
            OliLog.Error("SERVER", $"accounts.json повреждён: {ex.Message}; создаю резервную копию");
            try { File.Copy(_file, _file + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.bak", true); } catch { }
            _db = new AccountDatabase();
        }
    }

    private async Task PersistAsync()
    {
        var temp = _file + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_db, JsonOptions));
        File.Move(temp, _file, true);
    }

    public static string Normalize(string userName) => (userName ?? "").Trim().ToLowerInvariant();

    public async Task<(Account? Account, string? Error)> RegisterAsync(string userName, string password, string? displayName, string? device)
    {
        userName = (userName ?? "").Trim();
        if (userName.Length < 3) return (null, "Имя пользователя должно быть не короче 3 символов.");
        if (userName.Length > 32) return (null, "Имя пользователя слишком длинное.");
        if (!userName.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
            return (null, "В имени пользователя допустимы буквы, цифры, '.', '_' и '-'.");
        if (PasswordHasher.Validate(password) is { } passwordError) return (null, passwordError);

        await _gate.WaitAsync();
        try
        {
            var normalized = Normalize(userName);
            if (_db.Accounts.Any(a => a.NormalizedUserName == normalized))
                return (null, "Это имя пользователя уже занято.");

            var (hash, salt) = PasswordHasher.Create(password!);
            var account = new Account
            {
                UserName = userName,
                NormalizedUserName = normalized,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName.Trim(),
                PasswordHash = hash,
                PasswordSalt = salt,
                PasswordIterations = PasswordHasher.Iterations,
                PasswordVersion = PasswordHasher.Version,
                LastLoginAt = DateTimeOffset.UtcNow,
            };
            _db.Accounts.Add(account);
            await PersistAsync();
            OliLog.Info("SERVER", $"Зарегистрирован пользователь {account.UserName} ({account.Id})");
            return (account, null);
        }
        finally { _gate.Release(); }
    }

    public async Task<Account?> AuthenticateAsync(string userName, string password)
    {
        Account? account;
        await _gate.WaitAsync();
        try
        {
            account = _db.Accounts.FirstOrDefault(a => a.NormalizedUserName == Normalize(userName));
        }
        finally { _gate.Release(); }

        if (account is null || account.Disabled) return null;
        if (!PasswordHasher.Verify(password, account)) return null;

        await _gate.WaitAsync();
        try
        {
            account.LastLoginAt = DateTimeOffset.UtcNow;
            await PersistAsync();
        }
        finally { _gate.Release(); }
        return account;
    }

    public Account? FindById(string id)
    {
        lock (_db) return _db.Accounts.FirstOrDefault(a => a.Id == id);
    }

    public IReadOnlyList<Account> List()
    {
        lock (_db) return _db.Accounts.ToList();
    }

    // ---------------------------------------------------------------- tokens

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<(string Token, DateTimeOffset ExpiresAt)> IssueTokenAsync(string accountId, string? device)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var record = new TokenRecord
        {
            TokenHash = HashToken(token),
            AccountId = accountId,
            Device = (device ?? "").Trim(),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };

        await _gate.WaitAsync();
        try
        {
            // Drop expired tokens, then cap each account at 8 concurrent devices.
            _db.Tokens.RemoveAll(t => t.ExpiresAt <= DateTimeOffset.UtcNow);
            var mine = _db.Tokens.Where(t => t.AccountId == accountId).OrderBy(t => t.CreatedAt).ToList();
            for (var i = 0; i <= mine.Count - 8; i++) _db.Tokens.Remove(mine[i]);
            _db.Tokens.Add(record);
            await PersistAsync();
        }
        finally { _gate.Release(); }

        return (token, record.ExpiresAt);
    }

    public Account? ResolveToken(string token, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        var hash = HashToken(token ?? "");
        TokenRecord? record;
        lock (_db) record = _db.Tokens.FirstOrDefault(t => t.TokenHash == hash);
        if (record is null) return null;
        expiresAt = record.ExpiresAt;
        if (record.ExpiresAt <= DateTimeOffset.UtcNow) return null;
        return FindById(record.AccountId);
    }

    public async Task RevokeTokenAsync(string token)
    {
        var hash = HashToken(token);
        await _gate.WaitAsync();
        try
        {
            if (_db.Tokens.RemoveAll(t => t.TokenHash == hash) > 0) await PersistAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task RevokeAllTokensAsync(string accountId)
    {
        await _gate.WaitAsync();
        try
        {
            if (_db.Tokens.RemoveAll(t => t.AccountId == accountId) > 0) await PersistAsync();
        }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------- admin ops

    public async Task<bool> SetQuotaAsync(string accountId, long bytes)
    {
        await _gate.WaitAsync();
        try
        {
            var account = _db.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account is null) return false;
            account.QuotaBytes = Math.Clamp(bytes, 64L * 1024 * 1024, 50L * 1024 * 1024 * 1024);
            await PersistAsync();
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> SetDisabledAsync(string accountId, bool disabled)
    {
        await _gate.WaitAsync();
        try
        {
            var account = _db.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account is null) return false;
            account.Disabled = disabled;
            if (disabled) _db.Tokens.RemoveAll(t => t.AccountId == accountId);
            await PersistAsync();
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Admin-initiated password reset: the admin can set a NEW password (the user
    /// then changes it), but can never learn the existing one — it is only a hash.
    /// </summary>
    public async Task<(bool Ok, string? Error)> ResetPasswordAsync(string accountId, string newPassword)
    {
        if (PasswordHasher.Validate(newPassword) is { } error) return (false, error);

        await _gate.WaitAsync();
        try
        {
            var account = _db.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account is null) return (false, "Пользователь не найден.");
            var (hash, salt) = PasswordHasher.Create(newPassword);
            account.PasswordHash = hash;
            account.PasswordSalt = salt;
            account.PasswordIterations = PasswordHasher.Iterations;
            _db.Tokens.RemoveAll(t => t.AccountId == accountId);
            await PersistAsync();
            return (true, null);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string accountId)
    {
        await _gate.WaitAsync();
        try
        {
            var removed = _db.Accounts.RemoveAll(a => a.Id == accountId) > 0;
            _db.Tokens.RemoveAll(t => t.AccountId == accountId);
            if (removed) await PersistAsync();
            return removed;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Lets the user rename themselves (display name only, never the login).</summary>
    public async Task<(bool Ok, string? Error)> UpdateDisplayNameAsync(string accountId, string displayName)
    {
        var name = (displayName ?? "").Trim();
        if (name.Length < 2) return (false, "Имя слишком короткое.");
        if (name.Length > 40) return (false, "Имя слишком длинное (до 40 символов).");
        if (name.Any(char.IsControl)) return (false, "В имени есть недопустимые символы.");

        await _gate.WaitAsync();
        try
        {
            var account = _db.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account is null) return (false, "Пользователь не найден.");
            account.DisplayName = name;
            await PersistAsync();
            return (true, null);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> SetModelAsync(string accountId, string modelId)
    {
        await _gate.WaitAsync();
        try
        {
            var account = _db.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account is null) return false;
            account.ModelId = (modelId ?? "").Trim();
            await PersistAsync();
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<(bool Ok, string? Error)> ChangePasswordAsync(string accountId, string oldPassword, string newPassword)
    {
        if (PasswordHasher.Validate(newPassword) is { } error) return (false, error);

        await _gate.WaitAsync();
        try
        {
            var account = _db.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (account is null) return (false, "Пользователь не найден.");
            if (!PasswordHasher.Verify(oldPassword, account)) return (false, "Текущий пароль неверен.");

            var (hash, salt) = PasswordHasher.Create(newPassword);
            account.PasswordHash = hash;
            account.PasswordSalt = salt;
            _db.Tokens.RemoveAll(t => t.AccountId == accountId);
            await PersistAsync();
            return (true, null);
        }
        finally { _gate.Release(); }
    }
}
