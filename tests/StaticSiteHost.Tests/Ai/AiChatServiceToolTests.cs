using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Functions;
using StaticSiteHost.Models;
using StaticSiteHost.Services;
using StaticSiteHost.Services.Ai;
using static StaticSiteHost.Tests.Ai.AiTestKit;

namespace StaticSiteHost.Tests.Ai;

/// <summary>
/// A whole round trip through <see cref="AiChatService"/>, over HTTP to a recorded provider: what a
/// function's chat sends, and the calls that come back whole or streamed through the real
/// server-sent event reader.
/// </summary>
public sealed class AiChatServiceToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssh-ai-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly AiTool Clock = new("time_now", "The time.", Json("""{"type":"object","properties":{}}"""));

    private (AiChatService Service, Recorder Http) Service(string contentType, string body) =>
        AiTestKit.Service(_root, new Recorder(contentType, body));

    [Fact]
    public async Task Anthropic_streamed_calls_arrive_whole_in_the_final_chunk()
    {
        var sse = string.Concat(
            Event("message_start", """{"type":"message_start","message":{"model":"claude-sonnet-5-5","usage":{"input_tokens":20}}}"""),
            Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"time_now","input":{}}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"zone\":"}}"""),
            Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"UTC\"}"}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":9}}"""),
            Event("message_stop", """{"type":"message_stop"}"""));

        var (service, http) = Service("text/event-stream", sse);
        var request = new AiChatRequest { Tools = [Clock], Messages = [AiMessage.User("Time?")] };

        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in service.StreamAsync(Provider(AiProviderRecord.KindAnthropic), request)) chunks.Add(chunk);

        var final = Assert.Single(chunks).Final!;
        Assert.Equal(AiChatResponse.ToolCallsStopReason, final.StopReason);
        Assert.Equal("UTC", Assert.Single(final.ToolCalls).Arguments.GetProperty("zone").GetString());
        Assert.Contains("\"tools\":[{\"name\":\"time_now\"", http.Sent);
    }

    [Fact]
    public async Task Openai_compatible_whole_answer_with_a_call()
    {
        var (service, http) = Service("application/json", """
            {"model":"gpt-5-mini","choices":[{"message":{"role":"assistant","content":null,
             "tool_calls":[{"id":"call_1","type":"function","function":{"name":"time_now","arguments":"{}"}}]},
             "finish_reason":"tool_calls"}],"usage":{"prompt_tokens":5,"completion_tokens":5}}
            """);

        var answer = await service.CompleteAsync(Provider(AiProviderRecord.KindOpenAi),
            new AiChatRequest { Tools = [Clock], ToolChoice = AiToolChoice.Required, Messages = [AiMessage.User("Time?")] });

        Assert.Equal("call_1", Assert.Single(answer.ToolCalls).Id);
        Assert.Contains("\"tool_choice\":\"required\"", http.Sent);
    }

    [Fact]
    public async Task Openai_compatible_server_that_answers_a_stream_with_json_still_gives_the_calls()
    {
        var (service, _) = Service("application/json", """
            {"model":"llama3.1","choices":[{"message":{"role":"assistant","content":"",
             "tool_calls":[{"function":{"name":"time_now","arguments":{}}}]},"finish_reason":"stop"}]}
            """);

        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in service.StreamAsync(Provider(AiProviderRecord.KindOpenAi),
            new AiChatRequest { Tools = [Clock], Messages = [AiMessage.User("Time?")] }))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("time_now", Assert.Single(chunks[^1].Final!.ToolCalls).Name);
    }

    [Fact]
    public async Task A_request_that_breaks_the_rules_never_reaches_the_provider()
    {
        var (service, http) = Service("application/json", "{}");

        await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteAsync(Provider(AiProviderRecord.KindOpenAi),
            new AiChatRequest { Tools = [Clock, Clock], Messages = [AiMessage.User("Time?")] }));

        Assert.Null(http.Sent);
    }

    private static string Event(string type, string data) => $"event: {type}\ndata: {data}\n\n";
}
