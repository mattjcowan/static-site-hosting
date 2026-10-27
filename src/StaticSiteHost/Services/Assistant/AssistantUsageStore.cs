using StaticSiteHost.Localization;

namespace StaticSiteHost.Services.Assistant;

/// <summary>One person's use of the assistant on one day.</summary>
public sealed class AssistantUserUsage
{
    public string Username { get; set; } = "";
    public bool Administrator { get; set; }
    public int Messages { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

/// <summary>The assistant's use today, by user id. Persisted as config/assistant-usage.json.</summary>
public sealed class AssistantUsage
{
    /// <summary>The UTC day the counts are for, as yyyy-MM-dd. Another day starts them again.</summary>
    public string Day { get; set; } = "";

    public Dictionary<string, AssistantUserUsage> Users { get; set; } = [];
}

/// <summary>
/// Counts each person's messages to the assistant and the tokens they cost, per UTC day, so a
/// member can be held to <see cref="AssistantSettings.MemberMessagesPerDay"/>. Kept on disk, so a
/// restart does not hand everyone a fresh allowance. Administrators are counted, never limited.
/// </summary>
public sealed class AssistantUsageStore
{
    private readonly JsonFileStore<AssistantUsage> _store;
    private readonly TimeProvider _time;

    public AssistantUsageStore(DataPaths paths, TimeProvider? time = null)
    {
        _store = new JsonFileStore<AssistantUsage>(Path.Combine(paths.ConfigDir, "assistant-usage.json"));
        _time = time ?? TimeProvider.System;
    }

    private string Today => _time.GetUtcNow().ToString("yyyy-MM-dd");

    /// <summary>
    /// Counts one new message, unless a member has already sent <paramref name="limit"/> today
    /// (0 is no limit). Returns null when it may go ahead, otherwise what to tell them.
    /// </summary>
    public Task<string?> AdmitAsync(string userId, string username, bool administrator, int limit) =>
        _store.UpdateAsync(usage =>
        {
            StartDay(usage);
            var mine = Of(usage, userId, username, administrator);

            if (!administrator && limit > 0 && mine.Messages >= limit)
            {
                var midnight = _time.GetUtcNow().UtcDateTime.Date.AddDays(1);
                var hours = Math.Max(1, (int)Math.Ceiling((midnight - _time.GetUtcNow().UtcDateTime).TotalHours));
                return ((string?)ServerText.Get("ai.assistant.limit.message", new
                {
                    sent = ServerText.Plural("ai.assistant.limit.sent", limit),
                    resets = ServerText.Plural("ai.assistant.limit.resets", hours)
                }), false);
            }

            mine.Messages++;
            return ((string?)null, true);
        });

    /// <summary>Adds the tokens a message used, once its answer is done.</summary>
    public Task RecordAsync(string userId, string username, bool administrator, int inputTokens, int outputTokens) =>
        inputTokens == 0 && outputTokens == 0
            ? Task.CompletedTask
            : _store.UpdateAsync(usage =>
            {
                StartDay(usage);
                var mine = Of(usage, userId, username, administrator);
                mine.InputTokens += inputTokens;
                mine.OutputTokens += outputTokens;
                return (true, true);
            });

    /// <summary>Today's use, most messages first: a copy, safe to keep.</summary>
    public Task<(string Day, IReadOnlyList<AssistantUserUsage> Users)> TodayAsync() =>
        _store.UpdateAsync(usage =>
        {
            var changed = StartDay(usage);
            IReadOnlyList<AssistantUserUsage> users = usage.Users.Values
                .OrderByDescending(u => u.Messages).ThenBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
                .Select(u => new AssistantUserUsage
                {
                    Username = u.Username, Administrator = u.Administrator, Messages = u.Messages,
                    InputTokens = u.InputTokens, OutputTokens = u.OutputTokens
                })
                .ToList();
            return ((usage.Day, users), changed);
        });

    /// <summary>Clears the counts when the day has turned. True when it did.</summary>
    private bool StartDay(AssistantUsage usage)
    {
        if (usage.Day == Today) return false;

        usage.Day = Today;
        usage.Users = [];
        return true;
    }

    private static AssistantUserUsage Of(AssistantUsage usage, string userId, string username, bool administrator)
    {
        if (!usage.Users.TryGetValue(userId, out var mine)) usage.Users[userId] = mine = new AssistantUserUsage();
        mine.Username = username;
        mine.Administrator = administrator;
        return mine;
    }
}
