using System.Text;
using System.Text.Json;
using StaticSiteHost.Functions;
using StaticSiteHost.Functions.Testing;
using StaticSiteHost.Services.Ai;
using static StaticSiteHost.Tests.Ai.AiTestKit;

namespace StaticSiteHost.Tests.Ai;

/// <summary>The checks a request with tools passes before any provider is called, and the parts around them.</summary>
public class AiToolValidationTests
{
    private static readonly AiTool Clock = new("time_now", "The time.", Json("""{"type":"object","properties":{}}"""));

    private static AiToolCall Call(string id = "c1") => new(id, "time_now", Json("{}"));

    private static string Refusal(AiChatRequest request) =>
        Assert.Throws<ArgumentException>(() => AiChatService.Validate(request)).Message;

    private static AiChatRequest WithTools(params AiMessage[] messages) => new() { Tools = [Clock], Messages = messages };

    [Fact]
    public void A_whole_loop_passes() => AiChatService.Validate(WithTools(
        AiMessage.User("Time?"),
        AiMessage.AssistantToolCalls(null, [Call("c1"), Call("c2")]),
        AiMessage.ToolResult("c2", "noon"),
        AiMessage.ToolResult("c1", "noon"),
        AiMessage.Assistant("It is noon."),
        AiMessage.User("Thanks.")));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("dot.ted")]
    [InlineData("étoile")]
    public void Tool_names_are_letters_digits_underscores_and_hyphens(string name) =>
        Assert.Contains("A tool name is 1 to 64", Refusal(new AiChatRequest
        {
            Tools = [new AiTool(name, "d", Json("{}"))],
            Messages = [AiMessage.User("Hi")]
        }));

    [Fact]
    public void A_tool_name_may_be_64_characters_but_not_65()
    {
        AiChatService.Validate(new AiChatRequest { Tools = [new AiTool(new string('a', 64), "d", Json("{}"))], Messages = [AiMessage.User("Hi")] });
        Assert.Contains("1 to 64", Refusal(new AiChatRequest { Tools = [new AiTool(new string('a', 65), "d", Json("{}"))], Messages = [AiMessage.User("Hi")] }));
    }

    [Fact]
    public void Tool_names_are_unique() =>
        Assert.Contains("Two tools are named", Refusal(new AiChatRequest { Tools = [Clock, Clock], Messages = [AiMessage.User("Hi")] }));

    [Fact]
    public void At_most_128_tools() =>
        Assert.Contains("at most 128 tools", Refusal(new AiChatRequest
        {
            Tools = Enumerable.Range(0, 129).Select(i => new AiTool($"t{i}", "d", Json("{}"))).ToList(),
            Messages = [AiMessage.User("Hi")]
        }));

    [Theory]
    [InlineData("[]")]
    [InlineData("\"object\"")]
    [InlineData("null")]
    public void An_input_schema_is_an_object(string schema) =>
        Assert.Contains("not a JSON object", Refusal(new AiChatRequest { Tools = [new AiTool("t", "d", Json(schema))], Messages = [AiMessage.User("Hi")] }));

    [Fact]
    public void A_tool_choice_names_a_tool_in_the_request() =>
        Assert.Contains("not one of the request's Tools", Refusal(new AiChatRequest
        {
            Tools = [Clock],
            ToolChoice = AiToolChoice.Tool("look_up_star"),
            Messages = [AiMessage.User("Hi")]
        }));

    [Fact]
    public void A_tool_choice_needs_tools() =>
        Assert.Contains("no Tools", Refusal(new AiChatRequest { ToolChoice = AiToolChoice.Auto, Messages = [AiMessage.User("Hi")] }));

    [Fact]
    public void A_tool_choice_mode_is_one_of_four() =>
        Assert.Contains("mode \"any\"", Refusal(new AiChatRequest { Tools = [Clock], ToolChoice = new AiToolChoice("any"), Messages = [AiMessage.User("Hi")] }));

    [Fact]
    public void A_result_answers_a_call_that_was_made() =>
        Assert.Contains("did not make", Refusal(WithTools(
            AiMessage.User("Time?"),
            AiMessage.AssistantToolCalls(null, [Call("c1")]),
            AiMessage.ToolResult("c9", "noon"))));

    [Fact]
    public void A_call_is_answered_once() =>
        Assert.Contains("already has its result", Refusal(WithTools(
            AiMessage.User("Time?"),
            AiMessage.AssistantToolCalls(null, [Call("c1")]),
            AiMessage.ToolResult("c1", "noon"),
            AiMessage.ToolResult("c1", "noon"))));

    [Fact]
    public void Every_call_is_answered_before_the_user_speaks() =>
        Assert.Contains("\"c2\" still need one", Refusal(WithTools(
            AiMessage.User("Time?"),
            AiMessage.AssistantToolCalls(null, [Call("c1"), Call("c2")]),
            AiMessage.ToolResult("c1", "noon"),
            AiMessage.User("Well?"))));

    [Fact]
    public void A_system_message_cannot_come_between_a_call_and_its_result() =>
        Assert.Contains("comes before every tool call of message 2", Refusal(WithTools(
            AiMessage.User("Time?"),
            AiMessage.AssistantToolCalls(null, [Call("c1")]),
            AiMessage.System("Be quick."),
            AiMessage.ToolResult("c1", "noon"))));

    [Fact]
    public void Calls_left_unanswered_at_the_end_are_refused() =>
        Assert.Contains("need their results before the chat is sent", Refusal(WithTools(
            AiMessage.User("Time?"),
            AiMessage.AssistantToolCalls(null, [Call("c1")]))));

    [Fact]
    public void A_result_with_no_call_before_it_is_refused() =>
        Assert.Contains("did not make", Refusal(WithTools(AiMessage.User("Time?"), AiMessage.ToolResult("c1", "noon"))));

    [Fact]
    public void Only_an_assistant_turn_makes_calls() =>
        Assert.Contains("only an assistant turn", Refusal(WithTools(new AiMessage(AiMessage.UserRole, "Hi") { ToolCalls = [Call()] })));

    [Fact]
    public void Call_ids_are_unique() =>
        Assert.Contains("used twice", Refusal(WithTools(
            AiMessage.User("Time?"),
            AiMessage.AssistantToolCalls(null, [Call("c1"), Call("c1")]))));

    [Fact]
    public void A_conversation_with_calls_keeps_sending_its_tools() =>
        Assert.Contains("has no Tools", Refusal(new AiChatRequest
        {
            Messages = [AiMessage.User("Time?"), AiMessage.AssistantToolCalls(null, [Call()]), AiMessage.ToolResult("c1", "noon")]
        }));

    [Fact]
    public void Requests_without_tools_are_checked_as_before()
    {
        AiChatService.Validate(Hello());
        Assert.Contains("role \"robot\"", Refusal(new AiChatRequest { Messages = [new AiMessage("robot", "Hi")] }));
    }

    // ---- the browser endpoint ----------------------------------------------

    [Theory]
    [InlineData("""{"messages":[{"role":"user","content":"Hi"}],"tools":[{"name":"x"}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":"Hi"}],"tool_choice":"auto"}""")]
    [InlineData("""{"messages":[{"role":"user","content":"Hi"}],"tools":[]}""")]
    public void Browsers_cannot_send_tools(string body)
    {
        var (request, _, error) = AiChatEndpoint.Parse(Encoding.UTF8.GetBytes(body), null);
        Assert.Null(request);
        Assert.Contains("Tool calling is for the site's functions", error);
    }

    [Fact]
    public void Browsers_still_chat_without_tools()
    {
        var (request, stream, error) = AiChatEndpoint.Parse(Encoding.UTF8.GetBytes("""{"messages":[{"role":"user","content":"Hi"}],"tools":null}"""), 500);
        Assert.Null(error);
        Assert.True(stream);
        Assert.Null(request!.Tools);
        Assert.Equal(500, request.MaxTokens);
    }

    // ---- the fake ------------------------------------------------------------

    [Fact]
    public async Task The_fake_scripts_a_loop()
    {
        var ai = new FakeAiChat()
            .ReplyToolCall("time_now")
            .ReplyToolCall("look_up_star", new { name = "Vega" })
            .Reply("Noon, and Vega is bright.");

        var first = await ai.CompleteAsync(WithTools(AiMessage.User("Time?")));
        var call = Assert.Single(first.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("{}", call.Arguments.GetRawText());
        Assert.Equal(AiChatResponse.ToolCallsStopReason, first.StopReason);

        var second = await ai.CompleteAsync(WithTools(AiMessage.User("Vega?")));
        Assert.Equal("call_2", second.ToolCalls[0].Id);
        Assert.Equal("Vega", second.ToolCalls[0].Arguments.GetProperty("name").GetString());

        var third = await ai.CompleteAsync(WithTools(AiMessage.User("So?")));
        Assert.Empty(third.ToolCalls);
        Assert.Equal("stop", third.StopReason);
    }

    [Fact]
    public async Task The_fake_streams_calls_in_the_final_chunk()
    {
        var ai = new FakeAiChat().ReplyToolCalls("Checking the clock.", Call("x1"));

        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in ai.StreamAsync(WithTools(AiMessage.User("Time?")))) chunks.Add(chunk);

        Assert.Equal("Checking the clock.", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal("x1", Assert.Single(chunks[^1].Final!.ToolCalls).Id);
        Assert.All(chunks[..^1], c => Assert.Null(c.Final));
    }

    [Fact]
    public async Task The_fake_refuses_what_the_server_refuses()
    {
        var ai = new FakeAiChat().Reply("unused").Reply("used");

        // A loop that forgot one of the results.
        var broken = WithTools(
            AiMessage.User("Time?"),
            AiMessage.AssistantToolCalls(null, [Call("c1"), Call("c2")]),
            AiMessage.ToolResult("c1", "noon"));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => ai.CompleteAsync(broken));
        Assert.Contains("need their results", refused.Message);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in ai.StreamAsync(new AiChatRequest { Messages = [] })) { }
        });

        // A refused request takes no reply, and is still recorded.
        Assert.Equal("unused", (await ai.CompleteAsync(Hello())).Text);
        Assert.Equal(3, ai.Requests.Count);
    }

    [Fact]
    public void The_fake_wants_at_least_one_call() =>
        Assert.Throws<ArgumentException>(() => new FakeAiChat().ReplyToolCalls("text"));

    [Fact]
    public void Message_factories_set_the_tool_fields()
    {
        var result = AiMessage.ToolResult("c1", "boom", isError: true);
        Assert.Equal(AiMessage.ToolRole, result.Role);
        Assert.Equal("c1", result.ToolCallId);
        Assert.True(result.IsError);

        var turn = AiMessage.AssistantToolCalls(null, [Call()]);
        Assert.Equal("", turn.Content);
        Assert.Single(turn.ToolCalls!);
    }
}
