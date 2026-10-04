using System.Runtime.CompilerServices;
using System.Text.Json;

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
///
/// Every request is checked as the server checks it, and one the server would refuse throws the
/// same <see cref="ArgumentException"/>: no messages, an unknown role, a tool call without its
/// result, a bad tool name and the rest.
///
/// For a handler that offers tools, queue the calls the model makes with <see cref="ReplyToolCall"/>
/// or <see cref="ReplyToolCalls(AiToolCall[])"/>, then the answer it gives once it has the results:
/// <code>
/// var ai = new FakeAiChat()
///     .ReplyToolCall("look_up_star", new { name = "Vega" })
///     .Reply("Vega is magnitude 0.03.");
/// </code>
/// </summary>
public sealed class FakeAiChat : IAiChat
{
    private readonly object _gate = new();
    private readonly Queue<(string Text, AiToolCall[] Calls, AiChatException? Failure)> _replies = new();
    private readonly List<AiChatRequest> _requests = [];
    private int _callIds;

    /// <inheritdoc />
    /// <remarks>True unless you set it false, which makes both methods throw as the server's do.</remarks>
    public bool IsConfigured { get; set; } = true;

    /// <inheritdoc />
    /// <remarks>Defaults to <c>fake-model</c>, and answers name it unless a request names another.</remarks>
    public string? Model { get; set; } = "fake-model";

    /// <inheritdoc />
    /// <remarks>Defaults to <c>openai</c>. Reads as null while <see cref="IsConfigured"/> is false, as the server's does.</remarks>
    public string? ProviderKind
    {
        get => IsConfigured ? _providerKind : null;
        set => _providerKind = value;
    }

    private string? _providerKind = "openai";

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

        lock (_gate) _replies.Enqueue((text, [], null));
        return this;
    }

    /// <summary>
    /// Queues a turn in which the model calls one tool with <paramref name="arguments"/>, serialised
    /// as JSON (null for none). Its id is made up, as <c>call_1</c>, <c>call_2</c> and so on.
    /// </summary>
    /// <returns>This instance, so replies can be chained.</returns>
    public FakeAiChat ReplyToolCall(string name, object? arguments = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        lock (_gate)
        {
            var id = $"call_{++_callIds}";
            var input = arguments is null ? JsonSerializer.SerializeToElement(new { }) : JsonSerializer.SerializeToElement(arguments);
            _replies.Enqueue(("", [new AiToolCall(id, name, input)], null));
        }

        return this;
    }

    /// <summary>
    /// Queues a failure: the next call throws <paramref name="exception"/>, as the server's does when
    /// the provider refuses. Set its <see cref="AiChatException.Reason"/> to test how a handler
    /// answers each kind of failure:
    /// <code>
    /// ai.Fail(new AiChatException("The model does not support tools.", 400) { Reason = AiChatException.ToolsUnsupported });
    /// </code>
    /// </summary>
    /// <returns>This instance, so replies can be chained.</returns>
    public FakeAiChat Fail(AiChatException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_gate) _replies.Enqueue(("", [], exception));
        return this;
    }

    /// <summary>Queues a turn in which the model calls the given tools, with no text.</summary>
    /// <returns>This instance, so replies can be chained.</returns>
    public FakeAiChat ReplyToolCalls(params AiToolCall[] calls) => ReplyToolCalls("", calls);

    /// <summary>Queues a turn in which the model writes <paramref name="text"/> and calls the given tools.</summary>
    /// <returns>This instance, so replies can be chained.</returns>
    public FakeAiChat ReplyToolCalls(string text, params AiToolCall[] calls)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Length == 0) throw new ArgumentException("Queue at least one call, or use Reply for a text answer.", nameof(calls));

        lock (_gate) _replies.Enqueue((text, [.. calls], null));
        return this;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No reply is queued. Call <see cref="Reply"/> first.</exception>
    /// <exception cref="ArgumentException">The server would refuse the request; the message says why.</exception>
    public Task<AiChatResponse> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Answer(request));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Yields the reply in a few pieces, split between words, then the whole response, which holds
    /// any tool calls, as the server's does.
    /// </remarks>
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
        AiToolCall[] calls;
        lock (_gate)
        {
            _requests.Add(request);

            if (!IsConfigured)
                throw new AiChatException("AI is not set up for this site. (FakeAiChat.IsConfigured is false.)");

            // The server's own checks, so a conversation the server would refuse fails here too,
            // without taking a reply.
            AiChatRules.Validate(request);

            if (!_replies.TryDequeue(out var queued))
            {
                throw new InvalidOperationException(
                    $"FakeAiChat has no reply left for request {_requests.Count}. Queue one with Reply(\"…\") or ReplyToolCall(…) " +
                    "for every call the handler makes.");
            }

            if (queued.Failure is { } failure) throw failure;
            (text, calls, _) = queued;
        }

        var input = Words(request.System) + (request.Messages ?? []).Sum(message => Words(message?.Content));
        return new AiChatResponse(text, request.Model ?? Model ?? "fake-model", input, Words(text),
            calls.Length > 0 ? AiChatResponse.ToolCallsStopReason : "stop")
        {
            ToolCalls = calls
        };
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
