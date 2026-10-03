using System.Text.Json;
using StaticSiteHost.Functions;
using StaticSiteHost.Models;
using static StaticSiteHost.Tests.Ai.AiTestKit;

namespace StaticSiteHost.Tests.Ai;

/// <summary>
/// Answers with tool calls in them, whole and streamed, from recorded provider responses: OpenAI's
/// chat completions (which LiteLLM and Ollama copy) and Anthropic's Messages.
/// </summary>
public class AiToolResponseTests
{
    private const string OpenAi = AiProviderRecord.KindOpenAi;
    private const string Anthropic = AiProviderRecord.KindAnthropic;

    // ---- OpenAI-compatible, whole -------------------------------------------

    [Fact]
    public void Openai_text_answer_has_no_calls()
    {
        var answer = Read(OpenAi, """
            {"id":"chatcmpl-1","object":"chat.completion","model":"gpt-5-mini","choices":[{"index":0,
             "message":{"role":"assistant","content":"Hi there."},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":9,"completion_tokens":3,"total_tokens":12}}
            """);

        Assert.Equal("Hi there.", answer.Text);
        Assert.Equal("stop", answer.StopReason);
        Assert.Empty(answer.ToolCalls);
    }

    [Fact]
    public void Openai_single_call()
    {
        var answer = Read(OpenAi, """
            {"id":"chatcmpl-2","model":"gpt-5-mini","choices":[{"index":0,"message":{"role":"assistant","content":null,
             "tool_calls":[{"id":"call_abc","type":"function","function":{"name":"look_up_star","arguments":"{\"name\":\"Vega\"}"}}]},
             "finish_reason":"tool_calls"}],"usage":{"prompt_tokens":80,"completion_tokens":17}}
            """);

        Assert.Equal("", answer.Text);
        Assert.Equal(AiChatResponse.ToolCallsStopReason, answer.StopReason);
        var call = Assert.Single(answer.ToolCalls);
        Assert.Equal("call_abc", call.Id);
        Assert.Equal("look_up_star", call.Name);
        Assert.Equal("Vega", call.Arguments.GetProperty("name").GetString());
        Assert.Equal(80, answer.InputTokens);
    }

    [Fact]
    public void Openai_two_parallel_calls_keep_their_order()
    {
        var answer = Read(OpenAi, """
            {"model":"gpt-5-mini","choices":[{"message":{"role":"assistant","content":"Checking both.",
             "tool_calls":[
               {"id":"call_1","type":"function","function":{"name":"look_up_star","arguments":"{\"name\":\"Vega\"}"}},
               {"id":"call_2","type":"function","function":{"name":"time_now","arguments":"{}"}}]},
             "finish_reason":"tool_calls"}]}
            """);

        Assert.Equal("Checking both.", answer.Text);
        Assert.Equal(["call_1", "call_2"], answer.ToolCalls.Select(c => c.Id));
        Assert.Equal(JsonValueKind.Object, answer.ToolCalls[1].Arguments.ValueKind);
    }

    [Fact]
    public void Openai_malformed_arguments_come_back_as_the_raw_text()
    {
        var answer = Read(OpenAi, """
            {"model":"m","choices":[{"message":{"role":"assistant","content":null,
             "tool_calls":[{"id":"call_1","type":"function","function":{"name":"look_up_star","arguments":"{name: Vega"}}]},
             "finish_reason":"tool_calls"}]}
            """);

        var call = Assert.Single(answer.ToolCalls);
        Assert.Equal(JsonValueKind.String, call.Arguments.ValueKind);
        Assert.Equal("{name: Vega", call.Arguments.GetString());
    }

    [Fact]
    public void Ollama_style_answer_with_object_arguments_no_id_and_a_stop_reason_of_stop()
    {
        // Some servers that copy the API send the arguments as an object, leave out the id, or
        // finish a turn of calls with "stop".
        var answer = Read(OpenAi, """
            {"model":"llama3.1","choices":[{"index":0,"message":{"role":"assistant","content":"",
             "tool_calls":[{"function":{"name":"time_now","arguments":{"zone":"UTC"}}}]},"finish_reason":"stop"}]}
            """);

        var call = Assert.Single(answer.ToolCalls);
        Assert.StartsWith("call_0_", call.Id);
        Assert.Equal("UTC", call.Arguments.GetProperty("zone").GetString());
        Assert.Equal(AiChatResponse.ToolCallsStopReason, answer.StopReason);
    }

    [Fact]
    public void Openai_empty_arguments_are_an_empty_object()
    {
        var answer = Read(OpenAi, """
            {"model":"m","choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c","type":"function",
             "function":{"name":"time_now","arguments":""}}]},"finish_reason":"tool_calls"}]}
            """);

        Assert.Equal("{}", Assert.Single(answer.ToolCalls).Arguments.GetRawText());
    }

    [Fact]
    public void Openai_calls_cut_off_at_the_token_limit_are_dropped()
    {
        var answer = Read(OpenAi, """
            {"model":"m","choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
               {"id":"c1","type":"function","function":{"name":"time_now","arguments":"{}"}},
               {"id":"c2","type":"function","function":{"name":"look_up_star","arguments":"{\"na"}}]},
             "finish_reason":"length"}]}
            """);

        Assert.Empty(answer.ToolCalls);
        Assert.Equal("length", answer.StopReason);
    }

    // ---- OpenAI-compatible, streamed ----------------------------------------

    [Fact]
    public void Openai_streamed_calls_are_assembled_from_their_fragments()
    {
        var (pieces, final) = Stream(OpenAi,
            ("", """{"model":"gpt-5-mini","choices":[{"index":0,"delta":{"role":"assistant","content":"On it."}}]}"""),
            ("", """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"look_up_star","arguments":""}}]}}]}"""),
            ("", """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"na"}}]}}]}"""),
            ("", """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_2","type":"function","function":{"name":"time_now","arguments":"{}"}}]}}]}"""),
            ("", """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"me\":\"Vega\"}"}}]}}]}"""),
            ("", """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""),
            ("", """{"choices":[],"usage":{"prompt_tokens":50,"completion_tokens":20}}"""),
            ("", "[DONE]"));

        Assert.Equal(["On it."], pieces);
        Assert.Equal("On it.", final.Text);
        Assert.Equal(AiChatResponse.ToolCallsStopReason, final.StopReason);
        Assert.Equal(["call_1", "call_2"], final.ToolCalls.Select(c => c.Id));
        Assert.Equal("Vega", final.ToolCalls[0].Arguments.GetProperty("name").GetString());
        Assert.Equal(50, final.InputTokens);
    }

    [Fact]
    public void Ollama_style_stream_with_whole_calls_and_no_index()
    {
        var (_, final) = Stream(OpenAi,
            ("", """{"model":"llama3.1","choices":[{"index":0,"delta":{"role":"assistant","content":"","tool_calls":[{"id":"call_x","function":{"name":"time_now","arguments":"{\"zone\":\"UTC\"}"}},{"id":"call_y","function":{"name":"look_up_star","arguments":"{\"name\":\"Vega\"}"}}]}}]}"""),
            ("", """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""),
            ("", "[DONE]"));

        Assert.Equal(["time_now", "look_up_star"], final.ToolCalls.Select(c => c.Name));
        Assert.Equal("UTC", final.ToolCalls[0].Arguments.GetProperty("zone").GetString());
    }

    [Fact]
    public void Openai_streamed_calls_cut_off_at_the_token_limit_are_dropped()
    {
        var (_, final) = Stream(OpenAi,
            ("", """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c","type":"function","function":{"name":"look_up_star","arguments":"{\"na"}}]}}]}"""),
            ("", """{"choices":[{"index":0,"delta":{},"finish_reason":"length"}]}"""),
            ("", "[DONE]"));

        Assert.Empty(final.ToolCalls);
        Assert.Equal("length", final.StopReason);
    }

    [Fact]
    public void Openai_streamed_text_answer_is_as_before()
    {
        var (pieces, final) = Stream(OpenAi,
            ("", """{"model":"m","choices":[{"index":0,"delta":{"content":"Hel"}}]}"""),
            ("", """{"choices":[{"index":0,"delta":{"content":"lo."},"finish_reason":"stop"}]}"""),
            ("", "[DONE]"));

        Assert.Equal(["Hel", "lo."], pieces);
        Assert.Equal("stop", final.StopReason);
        Assert.Empty(final.ToolCalls);
    }

    // ---- Anthropic, whole ---------------------------------------------------

    [Fact]
    public void Anthropic_text_answer_has_no_calls()
    {
        var answer = Read(Anthropic, """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5-5",
             "content":[{"type":"text","text":"Hi there."}],"stop_reason":"end_turn",
             "usage":{"input_tokens":10,"output_tokens":4}}
            """);

        Assert.Equal("Hi there.", answer.Text);
        Assert.Equal("end_turn", answer.StopReason);
        Assert.Empty(answer.ToolCalls);
    }

    [Fact]
    public void Anthropic_single_call()
    {
        var answer = Read(Anthropic, """
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-sonnet-5-5",
             "content":[{"type":"text","text":"I'll look that up."},
                        {"type":"tool_use","id":"toolu_01","name":"look_up_star","input":{"name":"Vega"}}],
             "stop_reason":"tool_use","usage":{"input_tokens":300,"output_tokens":60}}
            """);

        Assert.Equal("I'll look that up.", answer.Text);
        Assert.Equal(AiChatResponse.ToolCallsStopReason, answer.StopReason);
        var call = Assert.Single(answer.ToolCalls);
        Assert.Equal("toolu_01", call.Id);
        Assert.Equal("Vega", call.Arguments.GetProperty("name").GetString());
    }

    [Fact]
    public void Anthropic_two_parallel_calls_keep_their_order()
    {
        var answer = Read(Anthropic, """
            {"model":"claude-sonnet-5-5","content":[
               {"type":"tool_use","id":"toolu_01","name":"look_up_star","input":{"name":"Vega"}},
               {"type":"tool_use","id":"toolu_02","name":"time_now","input":{}}],
             "stop_reason":"tool_use","usage":{"input_tokens":1,"output_tokens":1}}
            """);

        Assert.Equal("", answer.Text);
        Assert.Equal(["toolu_01", "toolu_02"], answer.ToolCalls.Select(c => c.Id));
    }

    [Fact]
    public void Anthropic_calls_cut_off_at_the_token_limit_are_dropped()
    {
        var answer = Read(Anthropic, """
            {"model":"m","content":[{"type":"tool_use","id":"toolu_01","name":"look_up_star","input":{}}],
             "stop_reason":"max_tokens","usage":{"input_tokens":1,"output_tokens":1}}
            """);

        Assert.Empty(answer.ToolCalls);
        Assert.Equal("max_tokens", answer.StopReason);
    }

    // ---- Anthropic, streamed ------------------------------------------------

    [Fact]
    public void Anthropic_streamed_calls_are_assembled_from_their_fragments()
    {
        var (pieces, final) = Stream(Anthropic,
            ("message_start", """{"type":"message_start","message":{"id":"msg_1","model":"claude-sonnet-5-5","usage":{"input_tokens":120,"output_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Checking."}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            ("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_01","name":"look_up_star","input":{}}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"name\": \"Ve"}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"ga\"}"}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":1}"""),
            ("content_block_start", """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_02","name":"time_now","input":{}}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":2}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":45}}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        Assert.Equal(["Checking."], pieces);
        Assert.Equal(AiChatResponse.ToolCallsStopReason, final.StopReason);
        Assert.Equal(["toolu_01", "toolu_02"], final.ToolCalls.Select(c => c.Id));
        Assert.Equal("Vega", final.ToolCalls[0].Arguments.GetProperty("name").GetString());
        Assert.Equal("{}", final.ToolCalls[1].Arguments.GetRawText());
        Assert.Equal(120, final.InputTokens);
        Assert.Equal(45, final.OutputTokens);
    }

    [Fact]
    public void Anthropic_streamed_calls_cut_off_at_the_token_limit_are_dropped()
    {
        var (_, final) = Stream(Anthropic,
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_01","name":"look_up_star","input":{}}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"na"}}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"max_tokens"},"usage":{"output_tokens":5}}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        Assert.Empty(final.ToolCalls);
        Assert.Equal("max_tokens", final.StopReason);
    }

    // ---- both ---------------------------------------------------------------

    [Fact]
    public void A_request_that_offered_no_tools_gets_its_answer_as_before()
    {
        var openai = Read(OpenAi, """
            {"model":"m","choices":[{"message":{"role":"assistant","content":"Hi.","tool_calls":[{"id":"c","type":"function",
             "function":{"name":"time_now","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
            """, Hello());
        Assert.Empty(openai.ToolCalls);
        Assert.Equal("tool_calls", openai.StopReason);

        var anthropic = Read(Anthropic, """
            {"model":"m","content":[{"type":"tool_use","id":"t","name":"time_now","input":{}}],"stop_reason":"tool_use",
             "usage":{"input_tokens":1,"output_tokens":1}}
            """, Hello());
        Assert.Empty(anthropic.ToolCalls);
        Assert.Equal("tool_use", anthropic.StopReason);

        var (_, streamed) = Stream(Anthropic, Hello(),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"t","name":"time_now","input":{}}}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":1}}"""),
            ("message_stop", """{"type":"message_stop"}"""));
        Assert.Empty(streamed.ToolCalls);
        Assert.Equal("tool_use", streamed.StopReason);
    }

    [Fact]
    public void Arguments_outlive_the_document_they_were_read_from()
    {
        AiToolCall call;
        using (var document = JsonDocument.Parse("""{"name":"Vega"}"""))
        {
            call = new AiToolCall("c", "look_up_star", document.RootElement);
        }

        Assert.Equal("Vega", call.Arguments.GetProperty("name").GetString());

        AiTool tool;
        using (var document = JsonDocument.Parse("""{"type":"object"}"""))
        {
            tool = new AiTool("t", "d", document.RootElement);
        }

        Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
    }
}
