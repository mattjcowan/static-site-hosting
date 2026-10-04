using StaticSiteHost.Functions;
using StaticSiteHost.Functions.Testing;
using StaticSiteHost.Models;
using StaticSiteHost.Services.Ai;
using static StaticSiteHost.Tests.Ai.AiTestKit;

namespace StaticSiteHost.Tests.Ai;

/// <summary>
/// Why a chat failed (<see cref="AiChatException.Reason"/>), read from the wording providers really
/// use, and the provider kind a site's chat reports.
/// </summary>
public sealed class AiFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssh-ai-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(400, "registry.ollama.ai/library/gemma2:latest does not support tools")]
    [InlineData(500, "registry.ollama.ai/library/llama2:latest does not support tools")]
    [InlineData(400, "litellm.UnsupportedParamsError: ollama does not support parameters: ['tools'], for model=llama2. To drop these, set `litellm.drop_params=True`")]
    [InlineData(400, "tools is not supported with this model.")]
    [InlineData(400, "Function calling is not supported for this model.")]
    [InlineData(400, "\"auto\" tool choice requires --enable-auto-tool-choice and --tool-call-parser to be set")]
    [InlineData(400, "This model does not support function calling.")]
    public void A_model_without_tools_is_told_apart(int status, string detail)
    {
        Assert.Equal(AiChatException.ToolsUnsupported, AiFailures.Classify(status, detail, sentTools: true));

        // Without tools in the request, tools cannot be why.
        Assert.NotEqual(AiChatException.ToolsUnsupported, AiFailures.Classify(status, detail, sentTools: false));
    }

    [Theory]
    [InlineData("Invalid schema for function 'get_element': In context=(), 'required' is required to be supplied and to be an array")]
    [InlineData("tools.0.custom.input_schema: JSON schema is invalid. It must match JSON Schema draft 2020-12")]
    [InlineData("tools.0.name: String should match pattern '^[a-zA-Z0-9_-]{1,64}$'")]
    [InlineData("Invalid 'tools[0].function.name': string does not match pattern.")]
    [InlineData("messages.2: tool_use ids were found without tool_result blocks immediately after")]
    public void A_malformed_tool_is_not_a_model_to_replace(string detail) =>
        Assert.Null(AiFailures.Classify(400, detail, sentTools: true));

    [Theory]
    [InlineData(400, "This model's maximum context length is 128000 tokens. However, your messages resulted in 131072 tokens.")]
    [InlineData(400, "prompt is too long: 210483 tokens > 200000 maximum")]
    [InlineData(400, "context_length_exceeded")]
    [InlineData(400, "The input exceeds the model's context window.")]
    [InlineData(413, null)]
    public void A_conversation_too_long_is_told_apart(int status, string? detail) =>
        Assert.Equal(AiChatException.ContextTooLong, AiFailures.Classify(status, detail, sentTools: true));

    [Theory]
    [InlineData(429, AiChatException.RateLimited)]
    [InlineData(401, AiChatException.Auth)]
    [InlineData(403, AiChatException.Auth)]
    [InlineData(408, AiChatException.Unavailable)]
    [InlineData(500, AiChatException.Unavailable)]
    [InlineData(502, AiChatException.Unavailable)]
    [InlineData(503, AiChatException.Unavailable)]
    [InlineData(529, AiChatException.Unavailable)]
    [InlineData(400, null)]
    [InlineData(404, null)]
    [InlineData(422, null)]
    public void Statuses_say_the_rest(int status, string? reason) =>
        Assert.Equal(reason, AiFailures.Classify(status, "Something went wrong.", sentTools: false));

    [Theory]
    [InlineData("overloaded_error", AiChatException.Unavailable)]
    [InlineData("api_error", AiChatException.Unavailable)]
    [InlineData("rate_limit_error", AiChatException.RateLimited)]
    [InlineData("authentication_error", AiChatException.Auth)]
    [InlineData("invalid_request_error", null)]
    [InlineData(null, null)]
    public void Streamed_errors_by_type(string? type, string? reason) =>
        Assert.Equal(reason, AiFailures.ClassifyStreamed(type, "Something went wrong.", sentTools: true));

    // ---- through the service -------------------------------------------------

    private static readonly AiTool Clock = new("time_now", "The time.", Json("""{"type":"object"}"""));

    private static AiChatRequest WithClock() => new() { Tools = [Clock], Messages = [AiMessage.User("Time?")] };

    [Fact]
    public async Task Ollama_refusing_tools_reaches_the_caller_as_tools_unsupported()
    {
        var (service, _) = Service(_root, new Recorder("application/json",
            """{"error":"registry.ollama.ai/library/gemma2:latest does not support tools"}""", 400));

        var failure = await Assert.ThrowsAsync<AiChatException>(() => service.CompleteAsync(Provider(AiProviderRecord.KindOpenAi), WithClock()));

        Assert.Equal(AiChatException.ToolsUnsupported, failure.Reason);
        Assert.Equal(400, failure.StatusCode);
        Assert.Contains("does not support tools", failure.Message);
    }

    [Fact]
    public async Task A_rate_limited_key_reaches_the_caller()
    {
        var (service, _) = Service(_root, new Recorder("application/json",
            """{"type":"error","error":{"type":"rate_limit_error","message":"Number of requests has exceeded your rate limit."}}""", 429));

        var failure = await Assert.ThrowsAsync<AiChatException>(() => service.CompleteAsync(Provider(AiProviderRecord.KindAnthropic), WithClock()));
        Assert.Equal(AiChatException.RateLimited, failure.Reason);
    }

    [Fact]
    public async Task A_provider_that_cannot_be_reached_is_unavailable()
    {
        var (service, _) = Service(_root, new Recorder("application/json", "", status: 0));

        var failure = await Assert.ThrowsAsync<AiChatException>(() => service.CompleteAsync(Provider(AiProviderRecord.KindOpenAi), Hello()));
        Assert.Equal(AiChatException.Unavailable, failure.Reason);
        Assert.DoesNotContain("llm.example.com", failure.Message);
    }

    [Fact]
    public async Task An_overloaded_error_part_way_through_a_stream_is_unavailable()
    {
        var sse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"m\",\"usage\":{\"input_tokens\":1}}}\n\n" +
            "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";
        var (service, _) = Service(_root, new Recorder("text/event-stream", sse));

        var failure = await Assert.ThrowsAsync<AiChatException>(async () =>
        {
            await foreach (var _ in service.StreamAsync(Provider(AiProviderRecord.KindAnthropic), Hello())) { }
        });

        Assert.Equal(AiChatException.Unavailable, failure.Reason);
    }

    // ---- provider kind ---------------------------------------------------------

    [Fact]
    public async Task A_site_chat_reports_its_providers_kind_and_nothing_else()
    {
        var providers = Providers(_root, out _);
        await providers.LoadAsync();
        var (provider, error) = await providers.AddAsync("Claude", AiProviderRecord.KindAnthropic, "https://api.anthropic.com", "sk-ant-secret", "claude-sonnet-5-5", "test");
        Assert.Null(error);

        var site = new SiteRecord { Domain = "blog.example.com", Ai = new SiteAiSettings { ProviderId = provider!.Id } };
        var (service, _) = Service(_root, new Recorder("application/json", "{}"));
        var chat = new SiteAiChat(site.Domain, () => site, service, providers);

        Assert.Equal("anthropic", chat.ProviderKind);

        site.Ai = null;
        Assert.Null(chat.ProviderKind);
    }

    [Fact]
    public async Task The_fake_reports_a_kind_and_fails_on_request()
    {
        var ai = new FakeAiChat()
            .Fail(new AiChatException("The model does not support tools.", 400) { Reason = AiChatException.ToolsUnsupported })
            .Reply("Fine.");

        Assert.Equal("openai", ai.ProviderKind);

        var failure = await Assert.ThrowsAsync<AiChatException>(() => ai.CompleteAsync(WithClock()));
        Assert.Equal(AiChatException.ToolsUnsupported, failure.Reason);
        Assert.Equal("Fine.", (await ai.CompleteAsync(Hello())).Text);

        ai.ProviderKind = "anthropic";
        Assert.Equal("anthropic", ai.ProviderKind);
        ai.IsConfigured = false;
        Assert.Null(ai.ProviderKind);
    }
}
