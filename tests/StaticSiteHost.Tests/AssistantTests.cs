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
public class AssistantTests
{
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

    private async Task<(List<JsonElement> Events, ScriptedModel Model)> RunAsync(
        AssistantService assistant, AssistantRequest request, params AiChatResponse[] turns)
    {
        var model = new ScriptedModel(turns);
        var events = new List<JsonElement>();
        await assistant.RunAsync("user-1", request, "Signed in as ada (administrator).", model,
            new ServiceCollection().BuildServiceProvider(),
            item => { events.Add(JsonSerializer.SerializeToElement(item, new JsonSerializerOptions(JsonSerializerDefaults.Web))); return Task.CompletedTask; },
            CancellationToken.None);
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
    public void Tool_steps_are_summed_up_in_a_line(string name, string arguments, string expected) =>
        Assert.Equal(expected, AssistantService.Summarize(new AiToolCall("c", name, JsonDocument.Parse(arguments).RootElement)));
}
