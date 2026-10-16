using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using StaticSiteHost.Functions;
using StaticSiteHost.Services.Ai;
using StaticSiteHost.Tools;

namespace StaticSiteHost.Services.Assistant;

/// <summary>Where the assistant's answers come from: an AI provider in the server, a fake in tests.</summary>
public interface IAssistantModel
{
    IAsyncEnumerable<AiChatChunk> StreamAsync(AiChatRequest request, CancellationToken ct);
}

/// <summary>
/// One conversation with the assistant, for one user: held in memory, and saved to disk after
/// every message by <see cref="AssistantConversationStore"/>, so a restart does not end it.
/// </summary>
public sealed class AssistantConversation
{
    /// <summary>The most turns kept; a conversation that grows past it asks for a new one.</summary>
    public const int MaxMessages = 200;

    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public required string UserId { get; init; }
    public List<AiMessage> Messages { get; } = [];

    /// <summary>
    /// Calls the model made that have not run yet, because the first of them needs the user's
    /// yes or no. Empty when nothing waits.
    /// </summary>
    public List<AiToolCall> Waiting { get; } = [];

    public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>One request at a time per conversation: turns must not interleave.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

/// <summary>What the page sends: a message, or a decision about the call that waits.</summary>
/// <param name="Conversation">The conversation to continue, or null to start one.</param>
/// <param name="Message">What the user typed.</param>
/// <param name="Decision">"run" or "cancel" for the call that waits for a decision.</param>
/// <param name="Domain">The site the page shows, if it shows one, so the model need not ask which.</param>
public sealed record AssistantRequest(string? Conversation, string? Message, string? Decision, string? Domain);

/// <summary>
/// The management assistant: a conversation with an AI model that can use the server's tools
/// (<see cref="SiteHostTools"/>) as the signed-in user. Each user message runs the usual loop: the
/// model answers, streamed to the page as it writes; when it asks for tools they run and their
/// results go back to it; repeat until it answers in words. Built to be quick:
///
///   * the instructions and tools are the same on every call, so the provider caches them;
///   * every call carries what the page shows and the list of sites, so the model need not look
///     those up first;
///   * every tool the model asks for in one turn runs before the model is called again;
///   * tool results are cut to <see cref="MaxToolResultChars"/>, since everything sent is read
///     again on every later call.
///
/// A destructive tool (<see cref="ToolEffect.Destructive"/>) does not run until the user says so:
/// the loop stops, the page shows Run and Cancel, and the answer continues the loop.
/// </summary>
public sealed class AssistantService
{
    /// <summary>Model calls one user message may take, so a confused model cannot run up the bill.</summary>
    public const int MaxRounds = 12;

    /// <summary>The longest tool result passed back to the model.</summary>
    public const int MaxToolResultChars = 6000;

    /// <summary>
    /// The longest one answer may be, in tokens. Generous, because writing a function puts its whole
    /// source in a tool call, and a call cut off part way is lost; only what is written is billed.
    /// </summary>
    public const int MaxAnswerTokens = 8192;

    /// <summary>How long an idle conversation is kept.</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, AssistantConversation> _conversations = new();
    private readonly IReadOnlyList<SiteHostTool> _tools;
    private readonly IReadOnlyList<AiTool> _offered;
    private readonly AssistantConversationStore? _store;
    private readonly ILogger<AssistantService> _logger;
    private DateTimeOffset _lastDiskSweep = DateTimeOffset.MinValue;

    public AssistantService(AssistantConversationStore store, ILogger<AssistantService> logger) : this(SiteHostTools.All, logger, store)
    {
    }

    /// <summary>For tests, with tools of their own, and conversations on disk only when given a store.</summary>
    public AssistantService(IReadOnlyList<SiteHostTool> tools, ILogger<AssistantService> logger, AssistantConversationStore? store = null)
    {
        _tools = tools;
        _offered = tools.Select(tool => new AiTool(tool.Function.Name, tool.Function.Description, tool.Function.JsonSchema)).ToList();
        _store = store;
        _logger = logger;
    }

    /// <summary>What the model is told before anything else. The same on every call, so it is cached.</summary>
    public static string SystemPrompt =>
        SiteHostTools.Instructions + """

            You are the assistant inside this server's management web page. The user is signed in, and every tool
            acts as them. Text you write is shown as plain text: no Markdown tables or headings.
            """;

    /// <summary>
    /// Runs one request through the conversation and reports what happens through
    /// <paramref name="emit"/>, as it happens. <paramref name="context"/> is what the page shows,
    /// sent to the model with every call.
    /// </summary>
    /// <param name="services">The request's services: the tools act as whoever the request is signed in as.</param>
    /// <param name="admit">
    /// Asked before a new message is taken (not for a Run or Cancel): null lets it through, anything
    /// else is the reason it is refused, shown to the user. For the daily limit on members.
    /// </param>
    /// <param name="record">Told the tokens the request used, once it is done.</param>
    public async Task RunAsync(
        string userId, AssistantRequest request, string context, IAssistantModel model, IServiceProvider services,
        Func<object, Task> emit, CancellationToken ct,
        Func<Task<string?>>? admit = null, Func<int, int, Task>? record = null)
    {
        Sweep();

        var conversation = request.Conversation is { } id ? await FindAsync(id, userId) : null;

        if (conversation is null)
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                await emit(new { type = "error", message = "That conversation has ended. Start a new one." });
                return;
            }

            conversation = new AssistantConversation { UserId = userId };
            _conversations[conversation.Id] = conversation;
        }

        if (!await conversation.Gate.WaitAsync(TimeSpan.Zero, ct))
        {
            await emit(new { type = "error", message = "The assistant is still working on your last message." });
            return;
        }

        var usage = new Usage();
        try
        {
            conversation.LastUsedUtc = DateTimeOffset.UtcNow;
            await emit(new { type = "start", conversation = conversation.Id });

            if (conversation.Waiting.Count > 0)
            {
                if (request.Decision is not ("run" or "cancel"))
                {
                    await emit(new { type = "error", message = "Choose Run or Cancel for the step that is waiting first." });
                    return;
                }

                if (!await RunCallsAsync(conversation, request.Decision == "run", services, emit, usage, ct)) return;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    await emit(new { type = "error", message = "Type a message." });
                    return;
                }

                if (conversation.Messages.Count >= AssistantConversation.MaxMessages)
                {
                    await emit(new { type = "error", message = "This conversation is long enough to slow every answer down. Start a new one." });
                    return;
                }

                if (admit is not null && await admit() is { } refused)
                {
                    await emit(new { type = "error", message = refused });
                    return;
                }

                conversation.Messages.Add(AiMessage.User(request.Message.Trim()));
            }

            await LoopAsync(conversation, context, model, services, emit, usage, ct);
        }
        catch (AiChatException ex)
        {
            Abandon(conversation);
            await emit(new { type = "error", message = ex.Message });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The page went away part way through. What finished stays in the conversation.
            Abandon(conversation);
        }
        catch (Exception ex)
        {
            // Anything else is a fault here, not the user's: say so on the page rather than cutting
            // the answer off, and keep the conversation usable.
            _logger.LogError(ex, "The assistant failed part way through an answer");
            Abandon(conversation);
            await emit(new { type = "error", message = "The assistant ran into a problem on the server. The details are in the server log." });
        }
        finally
        {
            if (_store is not null) await _store.SaveAsync(conversation);
            conversation.Gate.Release();
            if (record is not null)
            {
                try
                {
                    await record(usage.Input, usage.Output);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The assistant's token use could not be recorded");
                }
            }

            await emit(new
            {
                type = "done",
                conversation = conversation.Id,
                waiting = conversation.Waiting.Count > 0,
                changed = usage.Changed,
                inputTokens = usage.Input,
                outputTokens = usage.Output,
                rounds = usage.Rounds
            });
        }
    }

    private async Task LoopAsync(
        AssistantConversation conversation, string context, IAssistantModel model, IServiceProvider services,
        Func<object, Task> emit, Usage usage, CancellationToken ct)
    {
        while (usage.Rounds < MaxRounds)
        {
            usage.Rounds++;

            var request = new AiChatRequest
            {
                System = SystemPrompt,
                Messages = [AiMessage.System(context), .. conversation.Messages],
                Tools = _offered,
                MaxTokens = MaxAnswerTokens
            };

            AiChatResponse? answer = null;
            await foreach (var chunk in model.StreamAsync(request, ct))
            {
                if (chunk.Text is { Length: > 0 } text) await emit(new { type = "text", text });
                if (chunk.Final is { } final) answer = final;
            }

            if (answer is null) throw new AiChatException("The AI provider ended its answer without finishing it. Try again.");

            usage.Input += answer.InputTokens;
            usage.Output += answer.OutputTokens;

            if (answer.ToolCalls.Count == 0)
            {
                conversation.Messages.Add(AiMessage.Assistant(answer.Text.Length > 0 ? answer.Text : "(no answer)"));
                if (answer.StopReason is "length" or "max_tokens")
                    await emit(new { type = "error", message = "The answer was cut off at its length limit." });
                return;
            }

            var calls = WithUniqueIds(conversation, answer.ToolCalls);
            conversation.Messages.Add(AiMessage.AssistantToolCalls(answer.Text, calls));
            conversation.Waiting.Clear();
            conversation.Waiting.AddRange(calls);

            if (!await RunCallsAsync(conversation, decision: null, services, emit, usage, ct)) return;
        }

        await emit(new { type = "error", message = $"Stopped after {MaxRounds} steps. Say what to do next, more precisely." });
        conversation.Messages.Add(AiMessage.Assistant($"(stopped after {MaxRounds} steps)"));
    }

    /// <summary>
    /// Runs the calls that wait, in order. Stops at a destructive one that has no decision yet and
    /// returns false; the user's Run or Cancel resumes from there. <paramref name="decision"/> is
    /// that answer, for the first call only.
    /// </summary>
    private async Task<bool> RunCallsAsync(
        AssistantConversation conversation, bool? decision, IServiceProvider services, Func<object, Task> emit, Usage usage, CancellationToken ct)
    {
        while (conversation.Waiting.Count > 0)
        {
            var call = conversation.Waiting[0];
            var tool = _tools.FirstOrDefault(t => t.Function.Name == call.Name);
            var summary = Summarize(call);

            if (tool?.Effect == ToolEffect.Destructive && decision is null)
            {
                await emit(new { type = "confirm", id = call.Id, name = call.Name, summary });
                return false;
            }

            string result;
            bool failed;

            if (tool is null)
            {
                (result, failed) = ($"{{\"error\":\"There is no tool called {call.Name}.\"}}", true);
            }
            else if (decision == false)
            {
                (result, failed) = ("{\"error\":\"The user chose not to run this. Do not try it again unless they ask.\"}", true);
                await emit(new { type = "tool", id = call.Id, name = call.Name, summary, status = "cancelled" });
            }
            else
            {
                await emit(new { type = "tool", id = call.Id, name = call.Name, summary, status = "running" });
                (result, failed) = await InvokeAsync(tool, call, services, ct);
                if (!failed && tool.Effect != ToolEffect.Read) usage.Changed = true;
                await emit(new { type = "tool", id = call.Id, name = call.Name, summary, status = failed ? "failed" : "done", detail = failed ? ErrorOf(result) : null });
            }

            conversation.Messages.Add(AiMessage.ToolResult(call.Id, Trim(result, call.Name), failed));
            conversation.Waiting.RemoveAt(0);
            decision = null;
        }

        return true;
    }

    private async Task<(string Result, bool Failed)> InvokeAsync(SiteHostTool tool, AiToolCall call, IServiceProvider services, CancellationToken ct)
    {
        var arguments = new AIFunctionArguments { Services = services };
        if (call.Arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in call.Arguments.EnumerateObject()) arguments[property.Name] = property.Value.Clone();
        }

        try
        {
            var value = await tool.Function.InvokeAsync(arguments, ct);
            var text = value is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(value, Json);
            return (text, HasError(text));
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or FormatException)
        {
            // Arguments the model got wrong: tell it, so it can fix them.
            return (JsonSerializer.Serialize(new { error = $"The arguments for {call.Name} could not be used: {ex.Message}" }, Json), true);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Assistant tool {Tool} failed", call.Name);
            return (JsonSerializer.Serialize(new { error = $"{call.Name} failed on the server. The details are in the server log." }, Json), true);
        }
    }

    /// <summary>
    /// The calls with ids no earlier call in the conversation used. Some OpenAI-compatible servers
    /// number their calls from the start every turn ("call_0"), and a conversation with the same id
    /// twice is one no provider accepts. The id only has to match between a call and its result,
    /// and both are kept here, so a renamed one works the same.
    /// </summary>
    internal static IReadOnlyList<AiToolCall> WithUniqueIds(AssistantConversation conversation, IReadOnlyList<AiToolCall> calls)
    {
        var used = conversation.Messages
            .SelectMany(m => m.ToolCalls ?? [])
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<AiToolCall>(calls.Count);
        foreach (var call in calls)
        {
            var id = string.IsNullOrEmpty(call.Id) ? "call" : call.Id;
            for (var n = 2; used.Contains(id); n++) id = $"{call.Id}_{n}";
            used.Add(id);
            result.Add(id == call.Id ? call : call with { Id = id });
        }

        return result;
    }

    /// <summary>
    /// Answers calls that will now never run, so the conversation stays one a model accepts: every
    /// call needs a result. A call that waits for the user's decision is left waiting.
    /// </summary>
    private void Abandon(AssistantConversation conversation)
    {
        if (conversation.Waiting.Count == 0) return;

        var first = _tools.FirstOrDefault(t => t.Function.Name == conversation.Waiting[0].Name);
        var awaitingDecision = first?.Effect == ToolEffect.Destructive &&
                               !conversation.Messages.Any(m => m.ToolCallId == conversation.Waiting[0].Id);
        if (awaitingDecision) return;

        foreach (var call in conversation.Waiting)
            conversation.Messages.Add(AiMessage.ToolResult(call.Id, "{\"error\":\"Interrupted before it ran.\"}", isError: true));
        conversation.Waiting.Clear();
    }

    /// <summary>A short line for the page: the tool and the arguments that say what it touched.</summary>
    internal static string Summarize(AiToolCall call)
    {
        if (call.Arguments.ValueKind != JsonValueKind.Object) return call.Name;

        var parts = new List<string>();
        foreach (var key in new[] { "domain", "path", "name", "newDomain", "releaseId", "username", "user", "provider", "event" })
        {
            if (call.Arguments.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                parts.Add(text);
        }

        if (call.Arguments.TryGetProperty("route", out var route) && route.ValueKind == JsonValueKind.Object)
        {
            var path = route.TryGetProperty("path", out var p) ? p.GetString() : null;
            var target = route.TryGetProperty("target", out var t) ? t.GetString() : null;
            if (path is not null) parts.Add(target is null ? path : $"{path} → {target}");
        }

        if (call.Arguments.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            parts.Add($"{files.GetArrayLength()} file{(files.GetArrayLength() == 1 ? "" : "s")}");

        if (call.Arguments.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array)
            parts.Add(string.Join(", ", paths.EnumerateArray().Take(5).Select(p => p.GetString())));

        if (call.Arguments.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
            parts.Add($"{edits.GetArrayLength()} edit{(edits.GetArrayLength() == 1 ? "" : "s")}");

        return parts.Count == 0 ? call.Name : $"{call.Name} {string.Join(" ", parts)}";
    }

    private static bool HasError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null ||
                    document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ErrorOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String) return error.GetString();
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                return string.Join(" ", errors.EnumerateArray().Select(e => e.ToString()));
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// A result cut to its tool's limit. Reading a file has a far higher one: a model that saw half a
    /// file could write the half back. read_site_file caps its own text and says when it has.
    /// </summary>
    private static string Trim(string result, string tool)
    {
        var limit = tool is "read_site_file" or "read_function_files" ? MaxFileReadChars : MaxToolResultChars;
        return result.Length <= limit
            ? result
            : result[..limit] + $"… (cut at {limit} of {result.Length} characters)";
    }

    /// <summary>The longest read_site_file or read_function_files result passed back, a little over the text they return at most.</summary>
    public const int MaxFileReadChars = 64_000;

    /// <summary>
    /// The user's conversation with that id: from memory, or from disk after a restart. Null when
    /// there is none, it belongs to someone else, or it has been idle too long.
    /// </summary>
    private async Task<AssistantConversation?> FindAsync(string id, string userId)
    {
        if (_conversations.TryGetValue(id, out var found)) return found.UserId == userId ? found : null;
        if (_store is null) return null;

        var loaded = await _store.LoadAsync(id, DateTimeOffset.UtcNow - IdleLifetime);
        if (loaded is null || loaded.UserId != userId) return null;

        // Two requests may load it at once; both use whichever was kept.
        return _conversations.GetOrAdd(id, loaded);
    }

    /// <summary>
    /// Drops conversations nobody has used for <see cref="IdleLifetime"/>, from memory every time
    /// and from disk every few minutes.
    /// </summary>
    private void Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleLifetime;
        foreach (var (id, conversation) in _conversations)
        {
            if (conversation.LastUsedUtc < cutoff) _conversations.TryRemove(id, out _);
        }

        if (_store is not null && DateTimeOffset.UtcNow - _lastDiskSweep > TimeSpan.FromMinutes(10))
        {
            _lastDiskSweep = DateTimeOffset.UtcNow;
            _store.DeleteIdle(cutoff);
        }
    }

    /// <summary>Forgets a conversation, as "New conversation" asks, from memory and from disk.</summary>
    public async Task EndAsync(string? conversationId, string userId)
    {
        if (conversationId is null || await FindAsync(conversationId, userId) is null) return;

        _conversations.TryRemove(conversationId, out _);
        _store?.Delete(conversationId);
    }

    private sealed class Usage
    {
        public int Rounds;
        public int Input;
        public int Output;
        public bool Changed;
    }
}

/// <summary>The assistant's model: an AI provider, asked to cache what stays the same between calls.</summary>
internal sealed class ProviderAssistantModel(AiChatService chat, Models.AiProviderRecord provider, string? model) : IAssistantModel
{
    public IAsyncEnumerable<AiChatChunk> StreamAsync(AiChatRequest request, CancellationToken ct) =>
        chat.StreamAsync(provider, new AiChatRequest
        {
            System = request.System,
            Messages = request.Messages,
            Tools = request.Tools,
            MaxTokens = request.MaxTokens,
            Model = model
        }, cachePrefix: true, ct);
}
