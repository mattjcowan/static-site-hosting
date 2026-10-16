using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using StaticSiteHost.Functions;

namespace StaticSiteHost.Services.Assistant;

/// <summary>
/// Keeps the assistant's conversations on disk, one file each under
/// config/assistant-conversations/, so a restart or a redeploy does not end them. A conversation
/// holds what its tools returned, which can be a file's text, an invitation link or a temporary
/// password, so each file is encrypted with Data Protection, like the AI providers' keys.
/// </summary>
public sealed class AssistantConversationStore
{
    private const string ProtectorPurpose = "StaticSiteHost.AssistantConversations";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dir;
    private readonly IDataProtector _protector;
    private readonly ILogger<AssistantConversationStore> _logger;

    public AssistantConversationStore(DataPaths paths, IDataProtectionProvider protection, ILogger<AssistantConversationStore> logger)
    {
        _dir = Path.Combine(paths.ConfigDir, "assistant-conversations");
        _protector = protection.CreateProtector(ProtectorPurpose);
        _logger = logger;
    }

    /// <summary>Writes the conversation as it stands. A failure is logged, never thrown: the conversation still works from memory.</summary>
    public async Task SaveAsync(AssistantConversation conversation)
    {
        if (!IsId(conversation.Id)) return;

        try
        {
            var saved = new Saved(conversation.Id, conversation.UserId, conversation.LastUsedUtc, conversation.Messages, conversation.Waiting);
            var bytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(saved, Json));

            Directory.CreateDirectory(_dir);
            var path = PathOf(conversation.Id);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogWarning(ex, "The assistant conversation {Id} could not be saved; it lasts until the server restarts", conversation.Id);
        }
    }

    /// <summary>
    /// The conversation with that id, or null when there is none, it is older than
    /// <paramref name="idleSince"/>, or it cannot be read (then it is removed).
    /// </summary>
    public async Task<AssistantConversation?> LoadAsync(string id, DateTimeOffset idleSince)
    {
        if (!IsId(id)) return null;

        var path = PathOf(id);
        if (!File.Exists(path)) return null;

        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(_protector.Unprotect(await File.ReadAllBytesAsync(path)), Json);
            if (saved is null || saved.Id != id || saved.LastUsedUtc < idleSince)
            {
                Delete(id);
                return null;
            }

            var conversation = new AssistantConversation { Id = saved.Id, UserId = saved.UserId, LastUsedUtc = saved.LastUsedUtc };
            conversation.Messages.AddRange(saved.Messages ?? []);
            conversation.Waiting.AddRange(saved.Waiting ?? []);
            return conversation;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException)
        {
            // Damaged, or written under a key ring that is gone: it cannot come back.
            _logger.LogWarning(ex, "The assistant conversation {Id} could not be read and was removed", id);
            Delete(id);
            return null;
        }
    }

    public void Delete(string id)
    {
        if (!IsId(id)) return;

        try
        {
            File.Delete(PathOf(id));
        }
        catch (IOException)
        {
            // Swept again later.
        }
    }

    /// <summary>Removes every saved conversation last used before <paramref name="cutoff"/>.</summary>
    public void DeleteIdle(DateTimeOffset cutoff)
    {
        if (!Directory.Exists(_dir)) return;

        foreach (var file in Directory.EnumerateFiles(_dir))
        {
            try
            {
                // Each file is written whenever its conversation is used, so the time it was
                // written is when it was last used.
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime) File.Delete(file);
            }
            catch (IOException)
            {
                // Swept again later.
            }
        }
    }

    private string PathOf(string id) => Path.Combine(_dir, id + ".bin");

    /// <summary>Ids are 32 hex digits (<see cref="AssistantConversation.Id"/>), so one can never name a path.</summary>
    private static bool IsId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigit);

    private sealed record Saved(
        string Id, string UserId, DateTimeOffset LastUsedUtc, List<AiMessage>? Messages, List<AiToolCall>? Waiting);
}
