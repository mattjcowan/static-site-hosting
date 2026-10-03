using System.Text.Json;

namespace StaticSiteHost.Functions;

/// <summary>
/// A tool the model may ask to call, for <see cref="AiChatRequest.Tools"/>. The model never runs
/// it: it answers with an <see cref="AiToolCall"/>, your function runs the tool, and sends the
/// result back with <see cref="AiMessage.ToolResult"/>.
/// </summary>
/// <param name="Name">
/// What the model calls it by: letters, digits, <c>_</c> and <c>-</c>, at most 64 characters, and
/// unique in the request.
/// </param>
/// <param name="Description">What the tool does and when to use it. The model reads this to decide, so say it plainly.</param>
/// <param name="InputSchema">
/// The JSON Schema of the tool's input, an object schema such as
/// <c>{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}</c>. It is copied, so
/// the document it came from may be disposed.
/// </param>
/// <example>
/// <code>
/// var lookUp = new AiTool("look_up_star", "Finds a star by name and returns its magnitude.",
///     JsonSerializer.SerializeToElement(new
///     {
///         type = "object",
///         properties = new { name = new { type = "string" } },
///         required = new[] { "name" }
///     }));
/// </code>
/// </example>
public sealed record AiTool(string Name, string Description, JsonElement InputSchema)
{
    /// <summary>The JSON Schema of the tool's input, as a copy that outlives the document it came from.</summary>
    public JsonElement InputSchema { get; init; } = AiJsonCopy.Of(InputSchema);
}

/// <summary>
/// How the model may use the request's tools, for <see cref="AiChatRequest.ToolChoice"/>: as it
/// sees fit (<see cref="Auto"/>, the default), not at all (<see cref="None"/>), at least once
/// (<see cref="Required"/>), or one tool in particular (<see cref="Tool"/>).
/// </summary>
/// <param name="Mode"><c>auto</c>, <c>none</c>, <c>required</c> or <c>tool</c>.</param>
/// <param name="Name">The tool to call when <paramref name="Mode"/> is <c>tool</c>; otherwise null.</param>
public sealed record AiToolChoice(string Mode, string? Name = null)
{
    /// <summary>The model decides whether to call tools, and which.</summary>
    public static AiToolChoice Auto { get; } = new("auto");

    /// <summary>
    /// The model answers in text and calls nothing. The tools are still sent, so a conversation
    /// that has called them can be brought to an answer.
    /// </summary>
    public static AiToolChoice None { get; } = new("none");

    /// <summary>The model calls at least one tool. Anthropic calls this <c>any</c>.</summary>
    public static AiToolChoice Required { get; } = new("required");

    /// <summary>The model calls the tool named <paramref name="name"/>.</summary>
    public static AiToolChoice Tool(string name) => new("tool", name);
}

/// <summary>A call the model asked for, in <see cref="AiChatResponse.ToolCalls"/>.</summary>
/// <param name="Id">
/// The provider's id for the call. Answer it with <see cref="AiMessage.ToolResult"/> under the same id.
/// </param>
/// <param name="Name">The tool's <see cref="AiTool.Name"/>.</param>
/// <param name="Arguments">
/// The tool's input, parsed: normally a JSON object, as the tool's schema asks. When a model writes
/// input that is not JSON at all, this is a JSON <em>string</em> holding the raw text instead, so the
/// chat does not fail: check <see cref="JsonElement.ValueKind"/>, and answer such a call with an
/// error result (<see cref="AiMessage.ToolResult"/> with <c>isError: true</c>) so the model can try
/// again. It is copied, so the document it came from may be disposed.
/// </param>
public sealed record AiToolCall(string Id, string Name, JsonElement Arguments)
{
    /// <summary>The tool's input, as a copy that outlives the document it came from.</summary>
    public JsonElement Arguments { get; init; } = AiJsonCopy.Of(Arguments);
}

/// <summary>Copies a <see cref="JsonElement"/> out of its document, leaving an empty one empty.</summary>
internal static class AiJsonCopy
{
    public static JsonElement Of(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? element : element.Clone();
}
