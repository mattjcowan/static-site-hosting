using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StaticSiteHost.Configuration;
using StaticSiteHost.Functions;
using StaticSiteHost.Models;

namespace StaticSiteHost.Services.Ai;

/// <summary>
/// Calls an AI provider: the one place a chat leaves this server. The admin page's Test button,
/// the site chats functions and browsers use (<see cref="SiteAiChat"/>) and anything added later
/// all come through here, so they share one timeout, one way of reading a streamed answer and one
/// set of rules for what an error may say.
///
/// Those rules: an <see cref="AiChatException"/> never carries the provider's key or its base URL,
/// since its message can end up in a browser. The provider's own explanation is passed on, cut to
/// one line and with the key and the address taken out of it. Connection failures say only that
/// the provider could not be reached, and the details go to the server log.
///
/// A streamed answer is read event by event as it arrives, never buffered whole. The timeout
/// (<see cref="SiteHostingOptions.AiTimeoutSeconds"/>) covers the whole of an answer that is not
/// streamed, and the silence before and between the events of one that is, so a long answer
/// arriving steadily is never cut off and a stalled one does not hang forever.
/// </summary>
public sealed class AiChatService
{
    /// <summary>The named <see cref="HttpClient"/> every provider call uses. Registered in Program.cs.</summary>
    public const string HttpClientName = "StaticSiteHost.Ai";

    /// <summary>The most of an error body read for the provider's explanation.</summary>
    private const int MaxErrorBodyBytes = 64 * 1024;

    /// <summary>The longest explanation passed on from a provider.</summary>
    private const int MaxDetailLength = 300;

    private readonly IHttpClientFactory _http;
    private readonly AiProviderStore _providers;
    private readonly ILogger<AiChatService> _logger;

    public AiChatService(
        IHttpClientFactory http, AiProviderStore providers, IOptions<SiteHostingOptions> options, ILogger<AiChatService> logger)
    {
        _http = http;
        _providers = providers;
        _logger = logger;
        Timeout = TimeoutFor(options.Value);
    }

    /// <summary>How long a call, or a silence in a streamed answer, may last.</summary>
    public TimeSpan Timeout { get; }

    public static TimeSpan TimeoutFor(SiteHostingOptions options) =>
        TimeSpan.FromSeconds(Math.Clamp(options.AiTimeoutSeconds, 5, 3600));

    /// <summary>Sends a chat to <paramref name="provider"/> and waits for the whole answer.</summary>
    /// <exception cref="AiChatException">The provider could not be reached, timed out or refused the request.</exception>
    /// <exception cref="ArgumentException">The request is not a chat that can be sent. See <see cref="Validate"/>.</exception>
    public async Task<AiChatResponse> CompleteAsync(AiProviderRecord provider, AiChatRequest request, CancellationToken ct = default)
    {
        var call = Prepare(provider, request);
        var format = FormatOf(provider);

        using var message = format.CreateRequest(call, stream: false);
        using var response = await SendAsync(call, message, HttpCompletionOption.ResponseContentRead, ct);
        using var body = await ReadJsonAsync(call, response, ct);

        return format.ReadResponse(body.RootElement, call);
    }

    /// <summary>
    /// Sends a chat to <paramref name="provider"/> and yields its answer as it is written: a chunk
    /// per piece of text, then one with the whole response. Nothing is sent until enumeration
    /// starts; stopping early closes the connection.
    /// </summary>
    /// <exception cref="AiChatException">
    /// The provider could not be reached, timed out, refused the request, or failed or went quiet
    /// part way through its answer.
    /// </exception>
    /// <exception cref="ArgumentException">The request is not a chat that can be sent. See <see cref="Validate"/>.</exception>
    public async IAsyncEnumerable<AiChatChunk> StreamAsync(
        AiProviderRecord provider, AiChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var call = Prepare(provider, request);
        var format = FormatOf(provider);

        using var message = format.CreateRequest(call, stream: true);
        using var response = await SendAsync(call, message, HttpCompletionOption.ResponseHeadersRead, ct);

        // A server that does not stream answers with the whole response as JSON. Pass it on as one piece.
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            using var body = await ReadJsonAsync(call, response, ct);
            var whole = format.ReadResponse(body.RootElement, call);

            if (whole.Text.Length > 0) yield return new AiChatChunk(whole.Text, null);
            yield return new AiChatChunk(null, whole);
            yield break;
        }

        // Cancelled by the caller, or by the provider going quiet for longer than the timeout while
        // the next event is awaited. See NextAsync.
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await using var stream = await response.Content.ReadAsStreamAsync(quiet.Token);
        await using var events = SseParser.Create(stream).EnumerateAsync(quiet.Token).GetAsyncEnumerator(quiet.Token);
        var answer = format.StartStream(call);

        while (await NextAsync(call, events, quiet, ct) is { } item)
        {
            string? text;
            try
            {
                text = answer.Take(item.EventType, item.Data);
            }
            catch (AiChatException ex)
            {
                var failure = new AiChatException(Scrub(ex.Message, call), ex.StatusCode);
                _logger.LogWarning("AI provider {Name} ({Id}) failed while streaming: {Message}",
                    provider.Name, provider.Id, failure.Message);
                throw failure;
            }

            if (!string.IsNullOrEmpty(text)) yield return new AiChatChunk(text, null);
            if (answer.IsDone) break;
        }

        if (!answer.IsComplete)
        {
            _logger.LogWarning("AI provider {Name} ({Id}) closed the stream before its answer was complete", provider.Name, provider.Id);
            throw new AiChatException("The AI provider stopped part way through its answer. Try again.");
        }

        yield return new AiChatChunk(null, answer.Result());
    }

    /// <summary>
    /// Checks that a request can be sent at all: at least one message, every message with a known
    /// role and some content, at least one of them from the user or the assistant, and sensible
    /// numbers.
    /// </summary>
    /// <exception cref="ArgumentException">The request is not a chat that can be sent; the message says why.</exception>
    public static void Validate(AiChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Messages is not { Count: > 0 } messages)
            throw new ArgumentException("A chat needs at least one message.", nameof(request));

        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not { } message)
                throw new ArgumentException($"Message {i + 1} is null.", nameof(request));

            if (message.Role is not (AiMessage.UserRole or AiMessage.AssistantRole or AiMessage.SystemRole))
            {
                throw new ArgumentException(
                    $"Message {i + 1} has the role \"{message.Role}\". Use \"user\", \"assistant\" or \"system\", or " +
                    "AiMessage.User, AiMessage.Assistant and AiMessage.System.", nameof(request));
            }

            if (message.Content is null)
                throw new ArgumentException($"Message {i + 1} has no content.", nameof(request));
        }

        if (messages.All(message => message.Role == AiMessage.SystemRole))
            throw new ArgumentException("A chat needs a user message: system messages alone give the model nothing to answer.", nameof(request));

        if (request.MaxTokens is < 1)
            throw new ArgumentException("MaxTokens must be at least 1, or null to leave it to the provider.", nameof(request));

        if (request.Temperature is { } temperature && (double.IsNaN(temperature) || temperature is < 0 or > 2))
            throw new ArgumentException("Temperature must be between 0 and 2, or null to leave it to the provider.", nameof(request));
    }

    // ---- sending ------------------------------------------------------------

    private AiCall Prepare(AiProviderRecord provider, AiChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Validate(request);

        var model = string.IsNullOrWhiteSpace(request.Model) ? provider.DefaultModel : request.Model.Trim();
        return new AiCall(provider, _providers.UnprotectKey(provider), model, request);
    }

    private static IAiWireFormat FormatOf(AiProviderRecord provider) => provider.Kind switch
    {
        AiProviderRecord.KindOpenAi => OpenAiCompatibleChat.Instance,
        AiProviderRecord.KindAnthropic => AnthropicChat.Instance,
        _ => throw new AiChatException($"The AI provider is of a kind this server cannot call ({provider.Kind}).")
    };

    /// <summary>Sends the request and returns a successful response, or throws the reason there is none.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        AiCall call, HttpRequestMessage message, HttpCompletionOption completion, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.CreateClient(HttpClientName).SendAsync(message, completion, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "AI provider {Name} ({Id}) did not answer within {Seconds} seconds",
                call.Provider.Name, call.Provider.Id, Timeout.TotalSeconds);
            throw TimedOut();
        }
        catch (HttpRequestException ex)
        {
            // The exception names the host, and it stays in the log: see the class remarks.
            _logger.LogWarning(ex, "Could not reach AI provider {Name} ({Id})", call.Provider.Name, call.Provider.Id);
            throw new AiChatException("The AI provider could not be reached. The server log says why.");
        }

        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var status = (int)response.StatusCode;
            var detail = await ReadErrorAsync(call, response, ct);

            _logger.LogWarning("AI provider {Name} ({Id}) answered {Status}: {Detail}",
                call.Provider.Name, call.Provider.Id, status, detail ?? "(no explanation)");

            var redirectHint = status is >= 300 and < 400
                ? " That is a redirect, which is not followed: check the provider's base URL, such as https rather than http."
                : "";

            throw new AiChatException(
                detail is null
                    ? $"The AI provider answered {status} {response.ReasonPhrase}.{redirectHint}"
                    : $"The AI provider answered {status}: {detail}{redirectHint}",
                status);
        }
    }

    /// <summary>A successful response's body as JSON.</summary>
    private async Task<JsonDocument> ReadJsonAsync(AiCall call, HttpResponseMessage response, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);

        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(limit.Token);
            return await JsonDocument.ParseAsync(body, cancellationToken: limit.Token);
        }
        catch (JsonException)
        {
            _logger.LogWarning("AI provider {Name} ({Id}) answered {Status} with a body that is not JSON",
                call.Provider.Name, call.Provider.Id, (int)response.StatusCode);
            throw new AiChatException(
                "The AI provider's answer was not the JSON expected. Check that the provider's base URL and kind are right.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TimedOut();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _logger.LogWarning(ex, "The answer from AI provider {Name} ({Id}) could not be read", call.Provider.Name, call.Provider.Id);
            throw new AiChatException("The connection to the AI provider was lost before its answer arrived.");
        }
    }

    /// <summary>
    /// The next streamed event, or null at the end of the stream. The provider has
    /// <see cref="Timeout"/> to send it: the clock runs only while this waits, not while the caller
    /// handles the last piece, so a visitor on a slow connection is never taken for a quiet provider.
    /// </summary>
    private async Task<SseItem<string>?> NextAsync(
        AiCall call, IAsyncEnumerator<SseItem<string>> events, CancellationTokenSource quiet, CancellationToken ct)
    {
        try
        {
            quiet.CancelAfter(Timeout);
            var more = await events.MoveNextAsync();
            quiet.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);

            return more ? events.Current : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("AI provider {Name} ({Id}) went quiet for {Seconds} seconds part way through its answer",
                call.Provider.Name, call.Provider.Id, Timeout.TotalSeconds);
            throw new AiChatException(
                $"The AI provider went quiet for {Timeout.TotalSeconds:0} seconds part way through its answer. Try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _logger.LogWarning(ex, "The stream from AI provider {Name} ({Id}) broke off", call.Provider.Name, call.Provider.Id);
            throw new AiChatException("The connection to the AI provider was lost part way through its answer.");
        }
    }

    /// <summary>The provider's explanation in an error response, cleaned for passing on, or null.</summary>
    private async Task<string?> ReadErrorAsync(AiCall call, HttpResponseMessage response, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(limit.Token);
            var buffer = new byte[MaxErrorBodyBytes];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(length), limit.Token)) > 0) length += read;

            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            return FormatOf(call.Provider).ReadError(document.RootElement) is { } detail ? Clean(detail, call) : null;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or OperationCanceledException)
        {
            // An HTML error page, a body cut off at the limit, or none at all: the status says enough.
            return null;
        }
    }

    private AiChatException TimedOut() =>
        new($"The AI provider did not answer within {Timeout.TotalSeconds:0} seconds.");

    /// <summary>One line of a provider's explanation, short enough to show, with nothing secret in it.</summary>
    private static string? Clean(string detail, AiCall call)
    {
        var line = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(line)) return null;

        line = Scrub(line, call);
        return line.Length <= MaxDetailLength ? line : line[..MaxDetailLength].TrimEnd() + "…";
    }

    /// <summary>Takes the provider's key and its base URL, with or without the scheme, out of text bound for a caller.</summary>
    private static string Scrub(string text, AiCall call)
    {
        // Four characters at least, so a placeholder key such as "x" does not blank out every x.
        if (call.ApiKey is { Length: >= 4 } key) text = text.Replace(key, "[redacted]", StringComparison.Ordinal);

        var baseUrl = call.Provider.BaseUrl;
        if (baseUrl.Length > 0)
        {
            text = text.Replace(baseUrl, "[provider]", StringComparison.OrdinalIgnoreCase);

            var withoutScheme = baseUrl[(baseUrl.IndexOf("://", StringComparison.Ordinal) + 3)..];
            if (withoutScheme.Length > 0) text = text.Replace(withoutScheme, "[provider]", StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }
}
