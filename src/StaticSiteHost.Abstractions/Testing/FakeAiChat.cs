using System.Runtime.CompilerServices;

namespace StaticSiteHost.Functions.Testing;

/// <summary>
/// An <see cref="IAiChat"/> that answers with replies you script, for running a handler outside
/// the server without calling a paid API:
/// <code>
/// var ai = new FakeAiChat().Reply("Orion, Cassiopeia and Pegasus.");
/// var answer = await ai.CompleteAsync(new AiChatRequest { Messages = [AiMessage.User("Name three.")] });
/// </code>
/// Each call takes the next reply in the order they were queued, and <see cref="Requests"/>
/// records what the handler sent, so a test can check the prompt it built.
/// </summary>
public sealed class FakeAiChat : IAiChat
{
    private readonly object _gate = new();
    private readonly Queue<string> _replies = new();
    private readonly List<AiChatRequest> _requests = [];

    /// <inheritdoc />
    /// <remarks>True unless you set it false, which makes both methods throw as the server's do.</remarks>
    public bool IsConfigured { get; set; } = true;

    /// <inheritdoc />
    /// <remarks>Defaults to <c>fake-model</c>, and answers name it unless a request names another.</remarks>
    public string? Model { get; set; } = "fake-model";

    /// <summary>Every request sent so far, in order, including ones that failed for want of a reply.</summary>
    public IReadOnlyList<AiChatRequest> Requests
    {
        get
        {
            lock (_gate) return [.. _requests];
        }
    }

    /// <summary>Queues the text of the next answer.</summary>
    /// <returns>This instance, so replies can be chained.</returns>
    public FakeAiChat Reply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_gate) _replies.Enqueue(text);
        return this;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No reply is queued. Call <see cref="Reply"/> first.</exception>
    public Task<AiChatResponse> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Answer(request));
    }

    /// <inheritdoc />
    /// <remarks>Yields the reply in a few pieces, split between words, then the whole response.</remarks>
    /// <exception cref="InvalidOperationException">No reply is queued. Call <see cref="Reply"/> first.</exception>
    public async IAsyncEnumerable<AiChatChunk> StreamAsync(
        AiChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var answer = Answer(request);

        foreach (var piece in Pieces(answer.Text, count: 3))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new AiChatChunk(piece, null);
        }

        ct.ThrowIfCancellationRequested();
        yield return new AiChatChunk(null, answer);
    }

    private AiChatResponse Answer(AiChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string text;
        lock (_gate)
        {
            _requests.Add(request);

            if (!IsConfigured)
                throw new AiChatException("AI is not set up for this site. (FakeAiChat.IsConfigured is false.)");

            if (!_replies.TryDequeue(out var queued))
            {
                throw new InvalidOperationException(
                    $"FakeAiChat has no reply left for request {_requests.Count}. Queue one with Reply(\"…\") for every call the handler makes.");
            }

            text = queued;
        }

        var input = Words(request.System) + (request.Messages ?? []).Sum(message => Words(message?.Content));
        return new AiChatResponse(text, request.Model ?? Model ?? "fake-model", input, Words(text), "stop");
    }

    /// <summary>A stand-in for a token count: whitespace-separated words, which is close enough to test with.</summary>
    private static int Words(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Splits text into about <paramref name="count"/> pieces at word boundaries, keeping every character.</summary>
    private static IEnumerable<string> Pieces(string text, int count)
    {
        if (text.Length == 0) yield break;

        var size = Math.Max(1, (int)Math.Ceiling(text.Length / (double)count));
        var start = 0;

        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + size);
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;

            yield return text[start..end];
            start = end;
        }
    }
}
