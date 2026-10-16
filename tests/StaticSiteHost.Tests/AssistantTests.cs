using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StaticSiteHost.Functions;
using StaticSiteHost.Services.Ai;
using StaticSiteHost.Services.Assistant;
using StaticSiteHost.Tests.Ai;
using StaticSiteHost.Tools;

namespace StaticSiteHost.Tests;

/// <summary>The assistant's loop, with a scripted model and tools that record their calls.</summary>
public class AssistantTests : IDisposable
{
    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Answers each call with the next scripted turn, and keeps every request it was sent.</summary>
    private sealed class ScriptedModel(params AiChatResponse[] turns) : IAssistantModel
    {
        private readonly Queue<AiChatResponse> _turns = new(turns);
        public List<AiChatRequest> Requests { get; } = [];

        public async IAsyncEnumerable<AiChatChunk> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            // What the real service checks before sending anything, so a conversation no provider
            // would take fails here too.
            AiChatService.Validate(request);
            Requests.Add(request);
            var turn = _turns.Count > 0 ? _turns.Dequeue() : Say("(script ran out)");
            await Task.Yield();
            if (turn.Text.Length > 0) yield return new AiChatChunk(turn.Text, null);
            yield return new AiChatChunk(null, turn);
        }
    }

    private static AiChatResponse Say(string text) => new(text, "model", 10, 5, "end_turn");

    private static AiChatResponse Call(params (string Id, string Name, string Arguments)[] calls) =>
        new("", "model", 10, 5, AiChatResponse.ToolCallsStopReason)
        {
            ToolCalls = calls.Select(c => new AiToolCall(c.Id, c.Name, JsonDocument.Parse(c.Arguments).RootElement.Clone())).ToList()
        };

    private readonly List<string> _ran = [];

    private IReadOnlyList<SiteHostTool> Tools() =>
    [
        new(AIFunctionFactory.Create(
            ([Description("domain")] string domain) => { _ran.Add("create " + domain); return JsonSerializer.SerializeToElement(new { ok = true, domain }); },
            "create_site", "Creates a site."), ToolEffect.Change),
        new(AIFunctionFactory.Create(
            ([Description("domain")] string domain) => { _ran.Add("delete " + domain); return JsonSerializer.SerializeToElement(new { ok = true }); },
            "delete_site", "Deletes a site."), ToolEffect.Destructive),
        new(AIFunctionFactory.Create(
            () => JsonSerializer.SerializeToElement(new { error = "Only administrators can do this." }),
            "list_users", "Lists users."), ToolEffect.Read),
    ];

    private Task<(List<JsonElement> Events, ScriptedModel Model)> RunAsync(
        AssistantService assistant, AssistantRequest request, params AiChatResponse[] turns) =>
        RunAsAsync("user-1", null, assistant, request, turns);

    private static async Task<(List<JsonElement> Events, ScriptedModel Model)> RunAsAsync(
        string userId, Func<Task<string?>>? admit, AssistantService assistant, AssistantRequest request, params AiChatResponse[] turns)
    {
        var model = new ScriptedModel(turns);
        var events = new List<JsonElement>();
        await assistant.RunAsync(userId, request, "Signed in as ada (administrator).", model,
            new ServiceCollection().BuildServiceProvider(),
            item => { events.Add(JsonSerializer.SerializeToElement(item, new JsonSerializerOptions(JsonSerializerDefaults.Web))); return Task.CompletedTask; },
            CancellationToken.None, admit);
        return (events, model);
    }

    private static string Type(JsonElement e) => e.GetProperty("type").GetString()!;

    private static string ConversationOf(List<JsonElement> events) => events.First(e => Type(e) == "start").GetProperty("conversation").GetString()!;

    [Fact]
    public async Task Runs_the_tools_the_model_asks_for_and_streams_its_answer()
    {
        var assistant = new AssistantService(Tools(), NullLogger<AssistantService>.Instance);

        var (events, model) = await RunAsync(assistant, new AssistantRequest(null, "Make shop.test", null, null),
            Call(("c1", "create_site", """{"domain":"shop.test"}""")),
            Say("Created shop.test."));

        Assert.Equal(["create shop.test"], _ran);
        Assert.Equal(["start", "tool", "tool", "text", "done"], events.Select(Type));
        Assert.Equal("running", events[1].GetProperty("status").GetString());
        Assert.Equal("done", events[2].GetProperty("status").GetString());
        Assert.Equal("create_site shop.test", events[2].GetProperty("summary").GetString());
        Assert.Equal("Created shop.test.", events[3].GetProperty("text").GetString());

        var done = events[^1];
        Assert.True(done.GetProperty("changed").GetBoolean());
        Assert.Equal(2, done.GetProperty("rounds").GetInt32());
        Assert.False(done.GetProperty("waiting").GetBoolean());

        // Every call carries the fixed instructions, the page's context first, and the tools.
        var second = model.Requests[1];
        Assert.Equal(AssistantService.SystemPrompt, second.System);
        Assert.Equal("system", second.Messages[0].Role);
        Assert.Contains("ada", second.Messages[0].Content);
        Assert.Equal(3, second.Tools!.Count);
        Assert.Equal("tool", second.Messages[^1].Role);
    }

    [Fact]
    public async Task A_destructive_tool_waits_for_the_user_and_runs_only_on_run()
    {
        var assistant = new AssistantService(Tools(), NullLogger<AssistantService>.Instance);

        var (first, _) = await RunAsync(assistant, new AssistantRequest(null, "Delete old.test", null, null),
            Call(("c1", "delete_site", """{"domain":"old.test"}""")));

        Assert.Empty(_ran);
        Assert.Contains(first, e => Type(e) == "confirm" && e.GetProperty("summary").GetString() == "delete_site old.test");
        Assert.True(first[^1].GetProperty("waiting").GetBoolean());

        var conversation = ConversationOf(first);

        // A new message is not taken while the step waits.
        var (refused, _) = await RunAsync(assistant, new AssistantRequest(conversation, "something else", null, null));
        Assert.Contains(refused, e => Type(e) == "error");

        var (second, _) = await RunAsync(assistant, new AssistantRequest(conversation, null, "run", null), Say("Deleted."));
        Assert.Equal(["delete old.test"], _ran);
        Assert.False(second[^1].GetProperty("waiting").GetBoolean());
    }

    [Fact]
    public async Task Cancel_tells_the_model_the_user_said_no()
    {
        var assistant = new AssistantService(Tools(), NullLogger<AssistantService>.Instance);
        var (first, _) = await RunAsync(assistant, new AssistantRequest(null, "Delete old.test", null, null),
            Call(("c1", "delete_site", """{"domain":"old.test"}""")));

        var (second, model) = await RunAsync(assistant, new AssistantRequest(ConversationOf(first), null, "cancel", null), Say("Left it."));

        Assert.Empty(_ran);
        Assert.Contains(second, e => Type(e) == "tool" && e.GetProperty("status").GetString() == "cancelled");
        var result = model.Requests[0].Messages.Last(m => m.Role == "tool");
        Assert.True(result.IsError);
        Assert.Contains("chose not to run", result.Content);
    }

    [Fact]
    public async Task Failures_go_back_to_the_model_and_show_on_the_page()
    {
        var assistant = new AssistantService(Tools(), NullLogger<AssistantService>.Instance);

        var (events, model) = await RunAsync(assistant, new AssistantRequest(null, "go", null, null),
            Call(("c1", "list_users", "{}"), ("c2", "no_such_tool", "{}"), ("c3", "create_site", "{}")),
            Say("Some of that failed."));

        var finished = events.Where(e => Type(e) == "tool" && e.GetProperty("status").GetString() != "running").ToList();
        Assert.Equal("failed", finished[0].GetProperty("status").GetString());
        Assert.Equal("Only administrators can do this.", finished[0].GetProperty("detail").GetString());
        Assert.Equal("failed", finished[1].GetProperty("status").GetString()); // create_site without its domain

        var results = model.Requests[1].Messages.Where(m => m.Role == "tool").ToList();
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.IsError));
        Assert.Contains("no tool called no_such_tool", results[1].Content);
        Assert.False(events[^1].GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task A_model_that_numbers_its_calls_from_the_start_every_turn_still_works()
    {
        var assistant = new AssistantService(Tools(), NullLogger<AssistantService>.Instance);

        var (first, _) = await RunAsync(assistant, new AssistantRequest(null, "one", null, null),
            Call(("call_0", "create_site", """{"domain":"a.test"}""")), Say("Made a.test."));
        var (second, model) = await RunAsync(assistant, new AssistantRequest(ConversationOf(first), "two", null, null),
            Call(("call_0", "create_site", """{"domain":"b.test"}"""), ("call_0", "create_site", """{"domain":"c.test"}""")), Say("Made b and c."));

        Assert.Equal(["create a.test", "create b.test", "create c.test"], _ran);
        Assert.DoesNotContain(second, e => Type(e) == "error");

        // The renamed ids pair each call with its own result.
        var ids = model.Requests[^1].Messages.SelectMany(m => m.ToolCalls ?? []).Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(ids, model.Requests[^1].Messages.Where(m => m.Role == "tool").Select(m => m.ToolCallId));
    }

    [Fact]
    public async Task A_fault_on_the_server_is_reported_rather_than_cutting_the_answer_off()
    {
        var broken = new List<SiteHostTool>(Tools())
        {
            new(AIFunctionFactory.Create(
                (Func<JsonElement>)(() => throw new IOException("disk full")), "write_things", "Writes."), ToolEffect.Change)
        };
        var assistant = new AssistantService(broken, NullLogger<AssistantService>.Instance);

        var (events, _) = await RunAsync(assistant, new AssistantRequest(null, "go", null, null),
            Call(("c1", "write_things", "{}")), Say("That failed."));

        Assert.Contains(events, e => Type(e) == "tool" && e.GetProperty("status").GetString() == "failed");
        Assert.Equal("done", Type(events[^1]));
    }

    [Fact]
    public async Task A_model_that_never_stops_calling_tools_is_stopped()
    {
        var assistant = new AssistantService(Tools(), NullLogger<AssistantService>.Instance);
        var turns = Enumerable.Range(0, AssistantService.MaxRounds + 2)
            .Select(i => Call(($"c{i}", "list_users", "{}")))
            .ToArray();

        var (events, model) = await RunAsync(assistant, new AssistantRequest(null, "loop", null, null), turns);

        Assert.Equal(AssistantService.MaxRounds, model.Requests.Count);
        Assert.Contains(events, e => Type(e) == "error" && e.GetProperty("message").GetString()!.Contains("Stopped after"));
    }

    [Fact]
    public async Task A_conversation_belongs_to_the_user_who_started_it()
    {
        var assistant = new AssistantService(Tools(), NullLogger<AssistantService>.Instance);
        var (first, _) = await RunAsync(assistant, new AssistantRequest(null, "hi", null, null), Say("Hello."));

        var model = new ScriptedModel(Say("Hi stranger."));
        var events = new List<string>();
        await assistant.RunAsync("someone-else", new AssistantRequest(ConversationOf(first), "hi", null, null), "ctx", model,
            new ServiceCollection().BuildServiceProvider(),
            item => { events.Add(JsonSerializer.Serialize(item)); return Task.CompletedTask; }, CancellationToken.None);

        // Their message starts a conversation of their own; the first one's turns are not sent.
        Assert.Equal(2, model.Requests[0].Messages.Count);
    }

    [Fact]
    public void Every_real_tool_can_be_offered_to_a_model()
    {
        // The server checks every request it sends; the assistant offers all the tools on every call.
        var request = new AiChatRequest
        {
            Messages = [AiMessage.User("hi")],
            Tools = SiteHostTools.All.Select(t => new AiTool(t.Function.Name, t.Function.Description, t.Function.JsonSchema)).ToList()
        };

        AiChatService.Validate(request);
    }

    [Fact]
    public void The_assistant_asks_anthropic_to_cache_the_tools_and_instructions_and_site_chats_do_not()
    {
        var request = new AiChatRequest
        {
            System = "fixed instructions",
            Messages = [AiMessage.System("this page"), AiMessage.User("hi")],
            Tools = [new AiTool("a", "first", JsonDocument.Parse("""{"type":"object"}""").RootElement), new AiTool("b", "second", JsonDocument.Parse("""{"type":"object"}""").RootElement)]
        };

        JsonElement Body(bool cache)
        {
            using var http = AnthropicChat.Instance.CreateRequest(new AiCall(AiTestKit.Provider("anthropic"), "k", "m", request, cache), stream: false);
            return JsonDocument.Parse(http.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
        }

        var cached = Body(cache: true);
        var system = cached.GetProperty("system");
        Assert.Equal("fixed instructions", system[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("this page", system[1].GetProperty("text").GetString());
        Assert.False(system[1].TryGetProperty("cache_control", out _));
        Assert.False(cached.GetProperty("tools")[0].TryGetProperty("cache_control", out _));
        Assert.True(cached.GetProperty("tools")[1].TryGetProperty("cache_control", out _));

        var plain = Body(cache: false);
        Assert.Equal("fixed instructions\n\nthis page", plain.GetProperty("system").GetString());
        Assert.False(plain.GetProperty("tools")[1].TryGetProperty("cache_control", out _));
    }

    [Theory]
    [InlineData("set_proxy_route", """{"domain":"shop.test","route":{"path":"/api","target":"http://api:8080"}}""", "set_proxy_route shop.test /api → http://api:8080")]
    [InlineData("deploy_files", """{"domain":"a.test","files":[{"path":"index.html","content":"x"}]}""", "deploy_files a.test 1 file")]
    [InlineData("who_am_i", "{}", "who_am_i")]
    [InlineData("delete_user", """{"user":"ci-bot"}""", "delete_user ci-bot")]
    [InlineData("remove_ai_provider", """{"provider":"OpenAI"}""", "remove_ai_provider OpenAI")]
    [InlineData("edit_function_file", """{"domain":"a.test","name":"Weather.cs","edits":[{"find":"a","replace":"b"}]}""", "edit_function_file a.test Weather.cs 1 edit")]
    [InlineData("publish_realtime_event", """{"domain":"a.test","event":"site.deployed"}""", "publish_realtime_event a.test site.deployed")]
    public void Tool_steps_are_summed_up_in_a_line(string name, string arguments, string expected) =>
        Assert.Equal(expected, AssistantService.Summarize(new AiToolCall("c", name, JsonDocument.Parse(arguments).RootElement)));

    // ---------------------------------------------------------------- kept across restarts

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ssh-assistant-tests-" + Guid.NewGuid().ToString("n"));

    private static AssistantConversationStore Store(Microsoft.AspNetCore.DataProtection.IDataProtectionProvider protection) =>
        new(new StaticSiteHost.Services.DataPaths(Microsoft.Extensions.Options.Options.Create(
                new StaticSiteHost.Configuration.SiteHostingOptions { DataRoot = Root })),
            protection, NullLogger<AssistantConversationStore>.Instance);

    [Fact]
    public async Task A_conversation_and_its_waiting_step_survive_a_restart()
    {
        var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var before = new AssistantService(Tools(), NullLogger<AssistantService>.Instance, Store(protection));

        var (first, _) = await RunAsync(before, new AssistantRequest(null, "Delete old.test", null, null),
            Call(("c1", "delete_site", """{"domain":"old.test"}""")));
        var conversation = ConversationOf(first);
        Assert.Empty(_ran);

        // A new service and store, as after a restart: the same key ring, nothing in memory.
        var after = new AssistantService(Tools(), NullLogger<AssistantService>.Instance, Store(protection));

        // Someone else cannot pick it up.
        var (stranger, _) = await RunAsAsync("user-2", null, after, new AssistantRequest(conversation, null, "run", null));
        Assert.Contains(stranger, e => Type(e) == "error");
        Assert.Empty(_ran);

        var (resumed, model) = await RunAsync(after, new AssistantRequest(conversation, null, "run", null), Say("Deleted old.test."));
        Assert.Equal(["delete old.test"], _ran);
        Assert.Equal(conversation, ConversationOf(resumed));

        // The model sees the whole conversation from before the restart.
        var sent = model.Requests.Single().Messages;
        Assert.Contains(sent, m => m.Role == "user" && m.Content == "Delete old.test");
        Assert.Contains(sent, m => m.Role == "tool" && m.ToolCallId == "c1");

        // Ending it removes it from disk too.
        await after.EndAsync(conversation, "user-1");
        var again = new AssistantService(Tools(), NullLogger<AssistantService>.Instance, Store(protection));
        var (ended, _) = await RunAsync(again, new AssistantRequest(conversation, null, "run", null));
        Assert.Contains(ended, e => Type(e) == "error" && e.GetProperty("message").GetString()!.Contains("ended"));
    }

    [Fact]
    public async Task A_conversation_saved_under_another_key_ring_is_dropped_not_fatal()
    {
        var before = new AssistantService(Tools(), NullLogger<AssistantService>.Instance, Store(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        var (first, _) = await RunAsync(before, new AssistantRequest(null, "Hello", null, null), Say("Hi."));
        var conversation = ConversationOf(first);

        var after = new AssistantService(Tools(), NullLogger<AssistantService>.Instance, Store(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        var (events, _) = await RunAsync(after, new AssistantRequest(conversation, "Again", null, null), Say("Hi again."));

        // It starts a new conversation rather than failing.
        Assert.NotEqual(conversation, ConversationOf(events));
        Assert.DoesNotContain(events, e => Type(e) == "error");
    }

    // ---------------------------------------------------------------- the daily limit

    [Fact]
    public async Task A_refused_message_never_reaches_the_model_and_a_decision_is_never_counted()
    {
        var assistant = new AssistantService(Tools(), NullLogger<AssistantService>.Instance);
        var asked = 0;

        var (refused, model) = await RunAsAsync("user-1", () => { asked++; return Task.FromResult<string?>("No more today."); },
            assistant, new AssistantRequest(null, "Make shop.test", null, null), Say("never"));
        Assert.Empty(model.Requests);
        Assert.Contains(refused, e => Type(e) == "error" && e.GetProperty("message").GetString() == "No more today.");

        // A step waiting for Run or Cancel goes ahead whatever the count: it is part of a message already let in.
        var (first, _) = await RunAsAsync("user-1", () => { asked++; return Task.FromResult<string?>(null); },
            assistant, new AssistantRequest(null, "Delete old.test", null, null), Call(("c1", "delete_site", """{"domain":"old.test"}""")));
        var (run, _) = await RunAsAsync("user-1", () => { asked++; return Task.FromResult<string?>("No more today."); },
            assistant, new AssistantRequest(ConversationOf(first), null, "run", null), Say("Deleted."));
        Assert.Equal(["delete old.test"], _ran);
        Assert.DoesNotContain(run, e => Type(e) == "error");
        Assert.Equal(2, asked);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Members_are_held_to_the_daily_limit_and_administrators_are_only_counted()
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 16, 22, 30, 0, TimeSpan.Zero));
        var usage = new AssistantUsageStore(new StaticSiteHost.Services.DataPaths(Microsoft.Extensions.Options.Options.Create(
            new StaticSiteHost.Configuration.SiteHostingOptions { DataRoot = Path.Combine(Root, "usage-" + Guid.NewGuid().ToString("n")) })), clock);

        Assert.Null(await usage.AdmitAsync("m1", "bo", administrator: false, limit: 2));
        Assert.Null(await usage.AdmitAsync("m1", "bo", administrator: false, limit: 2));
        var refused = await usage.AdmitAsync("m1", "bo", administrator: false, limit: 2);
        Assert.NotNull(refused);
        Assert.Contains("2 messages", refused);
        Assert.Contains("about 2 hours", refused);

        for (var i = 0; i < 5; i++) Assert.Null(await usage.AdmitAsync("a1", "ada", administrator: true, limit: 2));
        Assert.Null(await usage.AdmitAsync("m2", "cy", administrator: false, limit: 0));

        await usage.RecordAsync("m1", "bo", false, 1200, 80);
        var (day, users) = await usage.TodayAsync();
        Assert.Equal("2026-10-16", day);
        Assert.Equal(["ada", "bo", "cy"], users.Select(u => u.Username));
        Assert.Equal(1200, users.Single(u => u.Username == "bo").InputTokens);

        // A new UTC day starts the counts again.
        clock.Now = clock.Now.AddHours(2);
        Assert.Null(await usage.AdmitAsync("m1", "bo", administrator: false, limit: 2));
        Assert.Equal(1, (await usage.TodayAsync()).Users.Single().Messages);
    }
}
