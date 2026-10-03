using StaticSiteHost.Functions;
using StaticSiteHost.Models;
using static StaticSiteHost.Tests.Ai.AiTestKit;

namespace StaticSiteHost.Tests.Ai;

/// <summary>
/// A request without tools goes to the provider byte for byte as it did before tool calling was
/// added. The expected bodies were recorded from the 0.2.0 code.
/// </summary>
public class AiBackCompatTests
{
    private static AiChatRequest Conversation() => new()
    {
        System = "Be brief.",
        MaxTokens = 200,
        Temperature = 0.5,
        Messages =
        [
            AiMessage.System("Answer in English."),
            AiMessage.User("Name a star."),
            AiMessage.Assistant("Vega."),
            AiMessage.User("Another?"),
            AiMessage.User("A bright one.")
        ]
    };

    [Fact]
    public void Openai_compatible_body_is_unchanged() => Assert.Equal(
        """{"model":"test-model","messages":[{"role":"system","content":"Be brief."},{"role":"system","content":"Answer in English."},{"role":"user","content":"Name a star."},{"role":"assistant","content":"Vega."},{"role":"user","content":"Another?"},{"role":"user","content":"A bright one."}],"max_tokens":200,"temperature":0.5}""",
        Body(AiProviderRecord.KindOpenAi, Conversation()));

    [Fact]
    public void Openai_compatible_streamed_body_is_unchanged() => Assert.Equal(
        """{"model":"test-model","messages":[{"role":"user","content":"Hello"}],"stream":true,"stream_options":{"include_usage":true}}""",
        Body(AiProviderRecord.KindOpenAi, Hello(), stream: true));

    [Fact]
    public void Anthropic_body_is_unchanged() => Assert.Equal(
        """{"model":"test-model","max_tokens":200,"messages":[{"role":"user","content":"Name a star."},{"role":"assistant","content":"Vega."},{"role":"user","content":"Another?\n\nA bright one."}],"system":"Be brief.\n\nAnswer in English.","temperature":0.5}""",
        Body(AiProviderRecord.KindAnthropic, Conversation()));

    [Fact]
    public void Anthropic_streamed_body_is_unchanged() => Assert.Equal(
        """{"model":"test-model","max_tokens":1024,"messages":[{"role":"user","content":"Hello"}],"stream":true}""",
        Body(AiProviderRecord.KindAnthropic, Hello(), stream: true));
}
