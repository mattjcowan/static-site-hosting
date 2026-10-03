using System.Text.Json;
using StaticSiteHost.Functions;
using StaticSiteHost.Models;
using static StaticSiteHost.Tests.Ai.AiTestKit;

namespace StaticSiteHost.Tests.Ai;

/// <summary>How a conversation with tools in it goes on the wire, for each kind of provider.</summary>
public class AiToolRequestTests
{
    private static readonly AiTool LookUp = new("look_up_star", "Finds a star by name.",
        Json("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""));

    private static readonly AiTool Clock = new("time_now", "The time.", Json("""{"type":"object","properties":{}}"""));

    /// <summary>A whole loop: a question, a turn of two calls, their results (one failed), and a follow-up.</summary>
    private static AiChatRequest Loop(AiToolChoice? choice = null) => new()
    {
        System = "Be brief.",
        Tools = [LookUp, Clock],
        ToolChoice = choice,
        Messages =
        [
            AiMessage.User("How bright is Vega, and what time is it?"),
            AiMessage.AssistantToolCalls("Let me check.",
            [
                new AiToolCall("call_1", "look_up_star", Json("""{"name":"Vega"}""")),
                new AiToolCall("call_2", "time_now", Json("{}"))
            ]),
            AiMessage.ToolResult("call_1", """{"magnitude":0.03}"""),
            AiMessage.ToolResult("call_2", "The clock is broken.", isError: true),
            AiMessage.User("Just the magnitude, then.")
        ]
    };

    [Fact]
    public void Openai_compatible_sends_tools_as_functions()
    {
        var body = BodyJson(AiProviderRecord.KindOpenAi, Loop());
        var tools = body.GetProperty("tools");

        Assert.Equal(2, tools.GetArrayLength());
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal("look_up_star", tools[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Finds a star by name.", tools[0].GetProperty("function").GetProperty("description").GetString());
        Assert.Equal("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
            tools[0].GetProperty("function").GetProperty("parameters").GetRawText());
        Assert.False(body.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public void Openai_compatible_sends_calls_and_one_message_per_result()
    {
        var messages = BodyJson(AiProviderRecord.KindOpenAi, Loop()).GetProperty("messages");

        Assert.Equal(["system", "user", "assistant", "tool", "tool", "user"],
            messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));

        var assistant = messages[2];
        Assert.Equal("Let me check.", assistant.GetProperty("content").GetString());
        var call = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("call_1", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("look_up_star", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"name":"Vega"}""", call.GetProperty("function").GetProperty("arguments").GetString());

        Assert.Equal("call_1", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("""{"magnitude":0.03}""", messages[3].GetProperty("content").GetString());
        Assert.Equal("Error: The clock is broken.", messages[4].GetProperty("content").GetString());
    }

    [Fact]
    public void Openai_compatible_sends_no_content_for_a_turn_of_calls_alone()
    {
        var request = new AiChatRequest
        {
            Tools = [Clock],
            Messages =
            [
                AiMessage.User("Time?"),
                AiMessage.AssistantToolCalls(null, [new AiToolCall("c", "time_now", Json("{}"))]),
                AiMessage.ToolResult("c", "noon")
            ]
        };

        var assistant = BodyJson(AiProviderRecord.KindOpenAi, request).GetProperty("messages")[1];
        Assert.Equal(JsonValueKind.Null, assistant.GetProperty("content").ValueKind);
    }

    [Fact]
    public void Openai_compatible_sends_input_that_was_not_json_back_as_it_came()
    {
        var request = new AiChatRequest
        {
            Tools = [LookUp],
            Messages =
            [
                AiMessage.User("Vega?"),
                AiMessage.AssistantToolCalls("", [new AiToolCall("c", "look_up_star", JsonSerializer.SerializeToElement("{name: Vega"))]),
                AiMessage.ToolResult("c", "Your input was not JSON.", isError: true)
            ]
        };

        var call = BodyJson(AiProviderRecord.KindOpenAi, request).GetProperty("messages")[1].GetProperty("tool_calls")[0];
        Assert.Equal("{name: Vega", call.GetProperty("function").GetProperty("arguments").GetString());
    }

    [Theory]
    [InlineData("auto", "\"auto\"")]
    [InlineData("none", "\"none\"")]
    [InlineData("required", "\"required\"")]
    public void Openai_compatible_tool_choice_modes(string mode, string expected) =>
        Assert.Equal(expected, BodyJson(AiProviderRecord.KindOpenAi, Loop(new AiToolChoice(mode))).GetProperty("tool_choice").GetRawText());

    [Fact]
    public void Openai_compatible_tool_choice_naming_a_tool() => Assert.Equal(
        """{"type":"function","function":{"name":"time_now"}}""",
        BodyJson(AiProviderRecord.KindOpenAi, Loop(AiToolChoice.Tool("time_now"))).GetProperty("tool_choice").GetRawText());

    [Fact]
    public void Anthropic_sends_tools_with_their_input_schema()
    {
        var tools = BodyJson(AiProviderRecord.KindAnthropic, Loop()).GetProperty("tools");

        Assert.Equal("look_up_star", tools[0].GetProperty("name").GetString());
        Assert.Equal("Finds a star by name.", tools[0].GetProperty("description").GetString());
        Assert.Equal("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
            tools[0].GetProperty("input_schema").GetRawText());
    }

    [Fact]
    public void Anthropic_sends_calls_as_blocks_and_joins_the_results_into_one_user_turn()
    {
        var body = BodyJson(AiProviderRecord.KindAnthropic, Loop());
        var messages = body.GetProperty("messages");

        Assert.Equal("Be brief.", body.GetProperty("system").GetString());
        Assert.Equal(["user", "assistant", "user"], messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));

        Assert.Equal("""[{"type":"text","text":"How bright is Vega, and what time is it?"}]""", messages[0].GetProperty("content").GetRawText());

        Assert.Equal(
            """[{"type":"text","text":"Let me check."},{"type":"tool_use","id":"call_1","name":"look_up_star","input":{"name":"Vega"}},{"type":"tool_use","id":"call_2","name":"time_now","input":{}}]""",
            messages[1].GetProperty("content").GetRawText());

        // The results first, as the API requires, and the user's next words after them in the same turn.
        var blocks = messages[2].GetProperty("content");
        Assert.Equal(3, blocks.GetArrayLength());
        Assert.Equal("tool_result", blocks[0].GetProperty("type").GetString());
        Assert.Equal("call_1", blocks[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("""{"magnitude":0.03}""", blocks[0].GetProperty("content").GetString());
        Assert.False(blocks[0].TryGetProperty("is_error", out _));
        Assert.Equal("""{"type":"tool_result","tool_use_id":"call_2","content":"The clock is broken.","is_error":true}""", blocks[1].GetRawText());
        Assert.Equal("""{"type":"text","text":"Just the magnitude, then."}""", blocks[2].GetRawText());
    }

    [Fact]
    public void Anthropic_leaves_out_empty_text_and_sends_unreadable_input_as_an_empty_object()
    {
        var request = new AiChatRequest
        {
            Tools = [LookUp],
            Messages =
            [
                AiMessage.User("Vega?"),
                AiMessage.AssistantToolCalls(null, [new AiToolCall("c", "look_up_star", JsonSerializer.SerializeToElement("{name: Vega"))]),
                AiMessage.ToolResult("c", "Your input was not JSON.", isError: true)
            ]
        };

        var assistant = BodyJson(AiProviderRecord.KindAnthropic, request).GetProperty("messages")[1];
        Assert.Equal("""[{"type":"tool_use","id":"c","name":"look_up_star","input":{}}]""", assistant.GetProperty("content").GetRawText());
    }

    [Fact]
    public void Anthropic_takes_system_messages_out_of_a_conversation_with_tools()
    {
        var request = new AiChatRequest
        {
            Tools = [Clock],
            Messages = [AiMessage.System("Use the tools."), AiMessage.User("Time?")]
        };

        var body = BodyJson(AiProviderRecord.KindAnthropic, request);
        Assert.Equal("Use the tools.", body.GetProperty("system").GetString());
        Assert.Equal(1, body.GetProperty("messages").GetArrayLength());
    }

    [Theory]
    [InlineData("auto", """{"type":"auto"}""")]
    [InlineData("none", """{"type":"none"}""")]
    [InlineData("required", """{"type":"any"}""")]
    public void Anthropic_tool_choice_modes(string mode, string expected) =>
        Assert.Equal(expected, BodyJson(AiProviderRecord.KindAnthropic, Loop(new AiToolChoice(mode))).GetProperty("tool_choice").GetRawText());

    [Fact]
    public void Anthropic_tool_choice_naming_a_tool() => Assert.Equal(
        """{"type":"tool","name":"time_now"}""",
        BodyJson(AiProviderRecord.KindAnthropic, Loop(AiToolChoice.Tool("time_now"))).GetProperty("tool_choice").GetRawText());

    [Theory]
    [InlineData(AiProviderRecord.KindOpenAi)]
    [InlineData(AiProviderRecord.KindAnthropic)]
    public void Empty_tools_send_the_request_as_before(string kind)
    {
        var request = new AiChatRequest { Messages = [AiMessage.User("Hello")], Tools = [] };
        Assert.Equal(Body(kind, Hello()), Body(kind, request));
    }

    [Fact]
    public void The_site_chat_passes_the_tools_on()
    {
        var request = Loop(AiToolChoice.Required);
        var applied = Services.Ai.SiteAiChat.Apply(new SiteAiSettings { SystemPrompt = "Site rules." }, request);

        Assert.Same(request.Tools, applied.Tools);
        Assert.Same(request.ToolChoice, applied.ToolChoice);
        Assert.Equal("Site rules.\n\nBe brief.", applied.System);
    }
}
