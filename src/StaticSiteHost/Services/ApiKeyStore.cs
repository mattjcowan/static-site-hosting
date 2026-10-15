using StaticSiteHost.Models;
using StaticSiteHost.Security;

namespace StaticSiteHost.Services;

/// <summary>
/// API keys look like <c>sshost_&lt;id&gt;_&lt;secret&gt;</c>. The id identifies the record so
/// lookup is a dictionary hit; only a SHA-256 of the secret is stored.
/// </summary>
public sealed class ApiKeyStore
{
    public const string Prefix = "sshost";

    private readonly JsonFileStore<List<ApiKeyRecord>> _store;

    public ApiKeyStore(DataPaths paths) => _store = new JsonFileStore<List<ApiKeyRecord>>(paths.ApiKeysFile);

    public async Task<IReadOnlyList<ApiKeyRecord>> ListForUserAsync(string userId)
    {
        var keys = await _store.ReadAsync();
        return keys.Where(k => k.UserId == userId)
                   .OrderByDescending(k => k.CreatedUtc)
                   .ToList();
    }

    public async Task<(ApiKeyRecord Record, string PlainTextKey)> CreateAsync(
        string userId, string name, DateTimeOffset? expiresUtc)
    {
        // Alphanumeric only: '_' separates the three parts of a key.
        var id = Tokens.NewAlphanumeric(12);
        var secret = Tokens.NewAlphanumeric(43);
        var plainText = $"{Prefix}_{id}_{secret}";

        var record = new ApiKeyRecord
        {
            Id = id,
            UserId = userId,
            Name = string.IsNullOrWhiteSpace(name) ? "API key" : name.Trim(),
            SecretHash = Tokens.Sha256Hex(secret),
            Display = $"{Prefix}_{id}_{new string('•', 8)}",
            ExpiresUtc = expiresUtc
        };

        await _store.UpdateAsync(keys =>
        {
            keys.Add(record);
            return (true, true);
        });

        return (record, plainText);
    }

    /// <summary>Returns the matching, usable key record or null. Records last-used at most once a minute.</summary>
    public async Task<ApiKeyRecord?> ValidateAsync(string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented)) return null;

        var parts = presented.Trim().Split('_', 3);
        if (parts.Length != 3 || parts[0] != Prefix) return null;

        var (id, secret) = (parts[1], parts[2]);
        var keys = await _store.ReadAsync();
        var record = keys.FirstOrDefault(k => k.Id == id);
        if (record is null || !record.IsUsable) return null;
        if (!Tokens.FixedTimeEquals(Tokens.Sha256Hex(secret), record.SecretHash)) return null;

        if (record.LastUsedUtc is null || DateTimeOffset.UtcNow - record.LastUsedUtc > TimeSpan.FromMinutes(1))
        {
            await _store.UpdateAsync(all =>
            {
                var target = all.FirstOrDefault(k => k.Id == id);
                if (target is null) return (false, false);
                target.LastUsedUtc = DateTimeOffset.UtcNow;
                return (true, true);
            });
        }

        return record;
    }

    /// <summary>Revokes a key. <paramref name="ownerId"/> null means an administrator is acting.</summary>
    public Task<bool> RevokeAsync(string keyId, string? ownerId) =>
        _store.UpdateAsync(keys =>
        {
            var key = keys.FirstOrDefault(k => k.Id == keyId);
            if (key is null) return (false, false);
            if (ownerId is not null && key.UserId != ownerId) return (false, false);
            if (key.Revoked) return (true, false);

            key.Revoked = true;
            return (true, true);
        });

    /// <summary>
    /// Makes the key given in configuration (<c>Bootstrap:ApiKey</c>) usable for
    /// <paramref name="userId"/>, and revokes every earlier configured key. Returns why the key
    /// cannot be used, or null.
    ///
    /// Fails closed. A setting that cannot be read still revokes the key it replaces, so a typo
    /// while rotating a leaked key never leaves the leaked one working. A key that was revoked,
    /// in the UI or over the API, stays revoked even though the setting still names it: only a
    /// new key in the setting gives the server a usable one again.
    /// </summary>
    public Task<string?> EnsureConfiguredAsync(string userId, string? plainText)
    {
        string? id = null, secret = null, problem = null;

        if (!string.IsNullOrWhiteSpace(plainText))
        {
            var parts = plainText.Trim().Split('_');
            if (parts.Length != 3 || parts[0] != Prefix)
                problem = $"it must look like {Prefix}_<id>_<secret>";
            else if (parts[1].Length is < 8 or > 64 || !parts[1].All(char.IsAsciiLetterOrDigit))
                problem = "the id must be 8 to 64 letters and digits";
            else if (parts[2].Length < 32 || !parts[2].All(char.IsAsciiLetterOrDigit))
                problem = "the secret must be at least 32 letters and digits";
            else
                (id, secret) = (parts[1], parts[2]);
        }

        var hash = secret is null ? null : Tokens.Sha256Hex(secret);

        return _store.UpdateAsync(keys =>
        {
            var changed = false;

            // Every configured key but the one named now, and that one too unless it is the same key.
            foreach (var old in keys.Where(k => k.FromConfiguration && !k.Revoked && (k.Id != id || k.SecretHash != hash)))
            {
                old.Revoked = true;
                changed = true;
            }

            if (problem is not null) return (problem, changed);
            if (id is null) return ((string?)null, changed);

            var existing = keys.FirstOrDefault(k => k.Id == id);
            if (existing is { FromConfiguration: false })
                return ("another API key already has that id; choose a different one", changed);

            if (existing is null)
            {
                keys.Add(new ApiKeyRecord
                {
                    Id = id,
                    UserId = userId,
                    Name = "From configuration (Bootstrap:ApiKey)",
                    SecretHash = hash!,
                    Display = $"{Prefix}_{id}_{new string('•', 8)}",
                    FromConfiguration = true
                });
                return (null, true);
            }

            if (existing.SecretHash != hash)
            {
                // The same id with a new secret: a new key, usable.
                existing.SecretHash = hash!;
                existing.UserId = userId;
                existing.Revoked = false;
                existing.ExpiresUtc = null;
                return (null, true);
            }

            if (existing.Revoked)
                return ("that key was revoked; put a new key in the setting to have one again", changed);

            if (existing.UserId != userId)
            {
                existing.UserId = userId;
                changed = true;
            }

            return (null, changed);
        });
    }

    public Task DeleteForUserAsync(string userId) =>
        _store.UpdateAsync(keys =>
        {
            var removed = keys.RemoveAll(k => k.UserId == userId);
            return (removed, removed > 0);
        });
}
