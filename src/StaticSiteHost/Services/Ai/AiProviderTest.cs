using System.Diagnostics;
using StaticSiteHost.Functions;

namespace StaticSiteHost.Services.Ai;

/// <summary>
/// What a provider's test found: the answer, how long it took and what it cost, or why it failed.
/// The test sends <see cref="Prompt"/> asking for one word: one short request, billed like any other.
/// Shared by the AI page and the API.
/// </summary>
public sealed record AiProviderTest(
    bool Ok, long ElapsedMs, string? Text, string? Model, int InputTokens, int OutputTokens, string? StopReason, string? Error)
{
    /// <summary>As short a round trip as a model will make.</summary>
    public const string Prompt = "Reply with the single word OK.";

    public static async Task<AiProviderTest> RunAsync(AiChatService chat, Models.AiProviderRecord provider, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var answer = await chat.CompleteAsync(provider, new AiChatRequest
            {
                Messages = [AiMessage.User(Prompt)],
                MaxTokens = 16
            }, ct);

            return new AiProviderTest(true, clock.ElapsedMilliseconds, answer.Text.Trim(), answer.Model,
                answer.InputTokens, answer.OutputTokens, answer.StopReason, null);
        }
        catch (AiChatException ex)
        {
            return new AiProviderTest(false, clock.ElapsedMilliseconds, null, null, 0, 0, null, ex.Message);
        }
    }
}
