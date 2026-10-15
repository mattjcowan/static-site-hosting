using System.Text.Json;
using StaticSiteHost.Services.Ai;

namespace StaticSiteHost.Tests.Ai;

public class AiModelCatalogTests
{
    private static IReadOnlyList<AiModelInfo> Order(string json) =>
        AiModelCatalog.Order(AiModelCatalog.Read(JsonDocument.Parse(json).RootElement));

    [Fact]
    public void Reads_anthropic_with_its_display_names_newest_first_and_fast_ones_first()
    {
        var models = Order("""
            {"data":[
              {"id":"claude-sonnet-5-5","display_name":"Claude Sonnet 5.5","created_at":"2026-09-01T00:00:00Z"},
              {"id":"claude-haiku-5-5","display_name":"Claude Haiku 5.5","created_at":"2026-09-01T00:00:00Z"},
              {"id":"claude-opus-5-5","display_name":"Claude Opus 5.5","created_at":"2026-09-02T00:00:00Z"},
              {"id":"claude-3-5-haiku-20241022","display_name":"Claude Haiku 3.5","created_at":"2024-10-22T00:00:00Z"}
            ],"has_more":false}
            """);

        Assert.Equal(["claude-haiku-5-5", "claude-3-5-haiku-20241022", "claude-opus-5-5", "claude-sonnet-5-5"], models.Select(m => m.Id));
        Assert.Equal("Claude Haiku 5.5", models[0].Name);
        Assert.True(models[0].Fast);
        Assert.False(models[2].Fast);
    }

    [Fact]
    public void Keeps_only_openais_chat_models_with_plain_names_before_snapshots()
    {
        var models = Order("""
            {"object":"list","data":[
              {"id":"gpt-5.4","created":300},
              {"id":"gpt-5.4-mini","created":300},
              {"id":"gpt-5.4-mini-2026-03-01","created":310},
              {"id":"text-embedding-3-large","created":100},
              {"id":"whisper-1","created":100},
              {"id":"gpt-4o-mini-tts","created":200},
              {"id":"dall-e-3","created":100},
              {"id":"omni-moderation-latest","created":100},
              {"id":"gpt-image-1","created":100},
              {"id":"gpt-4o-realtime-preview","created":100},
              {"id":"gpt-5.4-nano","created":290}
            ]}
            """);

        Assert.Equal(["gpt-5.4-mini", "gpt-5.4-nano", "gpt-5.4-mini-2026-03-01", "gpt-5.4"], models.Select(m => m.Id));
        Assert.Null(models[0].Name);
    }

    [Theory]
    [InlineData("gemini-2.5-pro", false)]
    [InlineData("gemini-2.5-flash", true)]
    [InlineData("llama3.2:1b", false)]
    [InlineData("openai/gpt-5.4-mini", true)]
    [InlineData("qwen-small", true)]
    public void Fast_means_a_whole_part_of_the_name(string id, bool fast) =>
        Assert.Equal(fast, Order($$"""{"data":[{"id":"{{id}}"}]}""").Single().Fast);

    [Fact]
    public void Reads_openrouters_names_and_ignores_what_is_not_a_model()
    {
        var models = Order("""
            {"data":[
              {"id":"anthropic/claude-haiku-5-5","name":"Anthropic: Claude Haiku 5.5"},
              {"id":""},
              {"name":"no id"},
              "not an object",
              {"id":"anthropic/claude-haiku-5-5","name":"duplicate"}
            ]}
            """);

        Assert.Equal("Anthropic: Claude Haiku 5.5", Assert.Single(models).Name);
        Assert.Empty(Order("""{"error":"nope"}"""));
    }

    [Fact]
    public void Leaves_out_models_that_answer_only_on_apis_this_server_does_not_use()
    {
        const string list = """
            {"data":[
              {"id":"gpt-5.4"},{"id":"gpt-5.1-codex-mini"},{"id":"o4-mini-deep-research"},{"id":"gpt-5-search-api"},
              {"id":"gpt-3.5-turbo-instruct"},{"id":"gpt-5.4-pro"},{"id":"o1-pro"},{"id":"gemini-2.5-pro"}
            ]}
            """;

        // OpenAI itself: its "-pro" models answer only on the Responses API.
        var fromOpenAi = AiModelCatalog.Order(AiModelCatalog.Read(JsonDocument.Parse(list).RootElement), openAiItself: true);
        Assert.Equal(["gpt-5.4"], fromOpenAi.Select(m => m.Id));

        // Through a proxy, a "-pro" model may well be a chat model (gemini-2.5-pro), so it stays.

        var throughAProxy = AiModelCatalog.Order(AiModelCatalog.Read(JsonDocument.Parse(list).RootElement));
        Assert.Equal(["gemini-2.5-pro", "gpt-5.4", "gpt-5.4-pro", "o1-pro"], throughAProxy.Select(m => m.Id).Order());
    }
}
