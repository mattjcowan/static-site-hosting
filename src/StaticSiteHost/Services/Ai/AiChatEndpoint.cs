using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Functions;
using StaticSiteHost.Models;
using StaticSiteHost.Serving;

namespace StaticSiteHost.Services.Ai;

/// <summary>
/// <c>POST /_host/ai/chat</c>: chat for a site's browsers, through the site's provider, with the
/// key, the model and the pinned system prompt kept on the server. <see cref="SiteHostEndpoints"/>
/// hands it every request to that path, after the passcode gate.
///
/// The body is <c>{ "messages": [ { "role": "user", "content": "…" } ], "stream": true, "system": "…" }</c>.
/// Only user and assistant turns are taken, since the system prompt is the site's to pin, and a
/// page's own <c>system</c> is appended after the site's. The answer's length is the server's to
/// choose too: <see cref="SiteHostingOptions.AiVisitorMaxTokens"/>. The checks run in this order,
/// each with its own status: 405 for another method, 404 when the site has no provider, 415 for a
/// body that is not JSON, 413 for one over <see cref="SiteHostingOptions.AiMaxRequestBytes"/>, 400 for
/// one that is not a conversation, 429 once the address has used its allowance
/// (<see cref="AiVisitorLimiter"/>), and 403 when this browser may not chat.
///
/// Who may chat is the site's to decide when its functions declare an <c>[AiAccess]</c> hook: its
/// answer is final, and the "let every visitor chat" setting (<see cref="SiteAiSettings.AllowVisitors"/>)
/// is not consulted. Without one, that setting decides. The hook fails closed: when it throws, or
/// the functions cannot be loaded, the browser is refused. It is asked last, once the request has
/// passed every cheaper check and been counted against the limiter, so a stranger cannot make the
/// site run its code, often a database lookup, faster than the limiter allows; a refused request
/// still counts.
///
/// A streamed answer is <c>text/event-stream</c>, one <c>data:</c> event per piece of text
/// (<c>{"text":"…"}</c>), then <c>{"done":true,…}</c> with the model, the token counts and the stop
/// reason. The first piece is awaited before anything is written, so a provider that refuses the
/// request outright gets an error status of its own; a failure after that ends the stream with
/// <c>{"error":"…"}</c>. An answer that is not streamed is plain JSON. Provider failures are 502,
/// with the reason from <see cref="AiChatService"/>, which never carries the key or the base URL.
/// </summary>
public sealed class AiChatEndpoint
{
    /// <summary>Where it answers, under <see cref="SiteHostEndpoints.Prefix"/>.</summary>
    public const string Path = "ai/chat";

    /// <summary>The longest conversation a browser may send.</summary>
    public const int MaxMessages = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SiteAiChatFactory _chats;
    private readonly AiVisitorLimiter _limiter;
    private readonly FunctionHost _functions;
    private readonly SiteHostingOptions _options;
    private readonly ILogger<AiChatEndpoint> _logger;

    public AiChatEndpoint(
        SiteAiChatFactory chats,
        AiVisitorLimiter limiter,
        FunctionHost functions,
        IOptions<SiteHostingOptions> options,
        ILogger<AiChatEndpoint> logger)
    {
        _chats = chats;
        _limiter = limiter;
        _functions = functions;
        _options = options.Value;
        _logger = logger;
    }

    private sealed record ChatBody(List<ChatBodyMessage?>? Messages, bool? Stream, string? System);

    private sealed record ChatBodyMessage(string? Role, string? Content);

    public async Task HandleAsync(HttpContext context, SiteRecord site)
    {
        var request = context.Request;

        if (!HttpMethods.IsPost(request.Method))
        {
            context.Response.Headers.Allow = "POST";
            await FailAsync(context, StatusCodes.Status405MethodNotAllowed, $"{request.Path} answers POST only.");
            return;
        }

        var chat = _chats.Create(site);

        if (!chat.IsConfigured)
        {
            await FailAsync(context, StatusCodes.Status404NotFound,
                "This site has no AI provider. An administrator can choose one on the site's page, under AI.");
            return;
        }

        // JSON only. Besides saying what the endpoint takes, it keeps another site's page from
        // posting a plain form here: a cross-origin JSON request needs a preflight, which this
        // endpoint does not answer, so browsers only ever call it from the site's own pages.
        if (!request.HasJsonContentType())
        {
            await FailAsync(context, StatusCodes.Status415UnsupportedMediaType,
                "Send the conversation as JSON, with Content-Type: application/json.");
            return;
        }

        var limit = Math.Max(1, _options.AiMaxRequestBytes);
        byte[]? bytes;
        try
        {
            bytes = await ReadBodyAsync(request, limit, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return; // The visitor left before sending the whole body.
        }
        catch (BadHttpRequestException ex)
        {
            await FailAsync(context, ex.StatusCode, "The request body could not be read. Send the conversation as JSON.");
            return;
        }

        if (bytes is null)
        {
            await FailAsync(context, StatusCodes.Status413PayloadTooLarge,
                $"The conversation is over {limit / 1024.0:0.#} KB. Send fewer or shorter messages.");
            return;
        }

        var (chatRequest, stream, error) = Parse(bytes, _options.AiVisitorMaxTokens > 0 ? _options.AiVisitorMaxTokens : null);
        if (chatRequest is null)
        {
            await FailAsync(context, StatusCodes.Status400BadRequest, error!);
            return;
        }

        if (!_limiter.TryAcquire(site.Domain, context.Connection.RemoteIpAddress, _options.AiVisitorRequestsPerMinute, out var retryAfter))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            await FailAsync(context, StatusCodes.Status429TooManyRequests,
                $"Too many questions from this address for now. Try again in {seconds} second{(seconds == 1 ? "" : "s")}.");
            return;
        }

        // Who may chat, last of all (see the class remarks): the site's [AiAccess] hook when its
        // functions declare one, and otherwise the site's setting.
        var access = await _functions.InvokeAiAccessAsync(site, context);
        if (access.Declared)
        {
            if (!access.Allowed)
            {
                await FailAsync(context, StatusCodes.Status403Forbidden, access.Error is null
                    ? "This site does not let this visitor use its AI."
                    : "This site could not check whether this visitor may use its AI, so it may not. The details are in the server log.");
                return;
            }
        }
        else if (!chat.AllowsVisitors)
        {
            await FailAsync(context, StatusCodes.Status403Forbidden,
                "This site's AI is not open to visitors. An administrator can allow it on the site's page, under AI.");
            return;
        }

        if (stream) await StreamAsync(context, chat, chatRequest);
        else await CompleteAsync(context, chat, chatRequest);
    }

    /// <summary>The body, or null when it is longer than <paramref name="limit"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int limit, CancellationToken ct)
    {
        if (request.ContentLength > limit) return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The request to send, and whether to stream the answer; or why the body is not a conversation.</summary>
    /// <param name="maxTokens">The longest answer a browser gets, or null to leave it to the provider.</param>
    private static (AiChatRequest? Request, bool Stream, string? Error) Parse(byte[] bytes, int? maxTokens)
    {
        const string shape = "Send { \"messages\": [ { \"role\": \"user\", \"content\": \"…\" } ] }.";

        ChatBody? body;
        try
        {
            body = JsonSerializer.Deserialize<ChatBody>(bytes, Json);
        }
        catch (JsonException ex)
        {
            var where = string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? "" : $" at {ex.Path}";
            return (null, false, $"The body is not the JSON this endpoint takes{where}. {shape}");
        }

        if (body?.Messages is not { Count: > 0 } items) return (null, false, $"There are no messages. {shape}");
        if (items.Count > MaxMessages)
            return (null, false, $"A conversation can have at most {MaxMessages} messages; this one has {items.Count}. Send the most recent ones.");

        var messages = new List<AiMessage>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];

            if (item?.Role is not (AiMessage.UserRole or AiMessage.AssistantRole))
            {
                return (null, false,
                    $"Message {i + 1}: the role must be \"user\" or \"assistant\". The site's system prompt is set on the " +
                    "server; add instructions of your own with \"system\" beside \"messages\".");
            }

            if (string.IsNullOrWhiteSpace(item.Content))
                return (null, false, $"Message {i + 1} has no content. Every message needs some text.");

            messages.Add(new AiMessage(item.Role, item.Content));
        }

        var request = new AiChatRequest
        {
            Messages = messages,
            System = string.IsNullOrWhiteSpace(body.System) ? null : body.System,
            MaxTokens = maxTokens
        };

        return (request, body.Stream ?? true, null);
    }

    private async Task CompleteAsync(HttpContext context, SiteAiChat chat, AiChatRequest request)
    {
        var ct = context.RequestAborted;

        AiChatResponse answer;
        try
        {
            answer = await chat.CompleteAsync(request, ct);
        }
        catch (AiChatException ex)
        {
            await FailAsync(context, StatusCodes.Status502BadGateway, ex.Message);
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // The visitor left, and the call to the provider went with them.
        }

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        SiteHostEndpoints.MarkSameOrigin(response);
        await response.WriteAsJsonAsync(
            new { text = answer.Text, model = answer.Model, inputTokens = answer.InputTokens, outputTokens = answer.OutputTokens, stopReason = answer.StopReason },
            Json, ct);
    }

    private async Task StreamAsync(HttpContext context, SiteAiChat chat, AiChatRequest request)
    {
        var ct = context.RequestAborted;
        var response = context.Response;

        await using var chunks = chat.StreamAsync(request, ct).GetAsyncEnumerator(ct);

        bool more;
        try
        {
            more = await chunks.MoveNextAsync();
        }
        catch (AiChatException ex)
        {
            await FailAsync(context, StatusCodes.Status502BadGateway, ex.Message);
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        SiteHostEndpoints.MarkSameOrigin(response);

        // Tells nginx, and proxies that copy it, to pass each event on as it comes.
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        string? failure = null;
        try
        {
            while (more)
            {
                var chunk = chunks.Current;

                if (chunk.Final is { } final)
                {
                    await WriteEventAsync(response, new
                    {
                        done = true,
                        model = final.Model,
                        inputTokens = final.InputTokens,
                        outputTokens = final.OutputTokens,
                        stopReason = final.StopReason
                    }, ct);
                }
                else if (!string.IsNullOrEmpty(chunk.Text))
                {
                    await WriteEventAsync(response, new { text = chunk.Text }, ct);
                }

                more = await chunks.MoveNextAsync();
            }
        }
        catch (AiChatException ex)
        {
            failure = ex.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The visitor left. Disposing the enumerator closes the connection to the provider.
            return;
        }
        catch (IOException ex)
        {
            // Writing to the visitor failed: they left before the request was marked aborted.
            // Failures on the provider's side arrive as AiChatException, above.
            _logger.LogDebug(ex, "A visitor's connection to /_host/ai/chat closed part way through an answer");
            return;
        }

        if (failure is null) return;

        try
        {
            await WriteEventAsync(response, new { error = failure }, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            _logger.LogDebug(ex, "Could not tell a visitor that their chat failed: the connection had closed");
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, object payload, CancellationToken ct)
    {
        // Serialised JSON is one line, so each event is one data: line.
        await response.WriteAsync($"data: {JsonSerializer.Serialize(payload, Json)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private static Task FailAsync(HttpContext context, int statusCode, string error) =>
        SiteHostEndpoints.WriteErrorAsync(context, statusCode, error);
}
