# StaticSiteHost.Abstractions

The types you use to write C# functions for [Static Site Host](https://github.com/mattjcowan/static-site-hosting),
a server that hosts static sites and runs each site's C# code.

The package contains:

| Type | What it is |
| ---- | ---------- |
| `ISite` | the site a request is for: its domain, data folder, variables, services, realtime connections and AI |
| `ISiteVariables` | the site's variables, secrets included |
| `SiteHttpContextExtensions` | `context.Site()`, to reach the `ISite` from any code that has the `HttpContext` |
| `IRealtime`, `RealtimeConnection`, `RealtimeExtensions` | send events to the pages open on the site |
| `IAiChat`, `AiChatRequest`, `AiMessage`, `AiChatResponse`, `AiChatChunk`, `AiChatException` | chat with the site's AI provider |
| `AiTool`, `AiToolChoice`, `AiToolCall` | offer the model tools, and read the calls it makes |
| `[Middleware]`, `[ConfigureServices]`, `[BackgroundService]`, `[Schedule]`, `[Every]` | attributes for middleware, services, background services and jobs |
| `[RealtimeConnect]`, `[RealtimeJoin]`, `[AiAccess]` | attributes for the hooks that decide who may use realtime and AI |
| `FakeSite`, `FakeSiteVariables`, `FakeRealtime`, `FakeAiChat` | fakes, in `StaticSiteHost.Functions.Testing`, to run a handler outside the server |

This README lists every type, property and method. The main README explains how the server runs
functions: see [Functions](https://github.com/mattjcowan/static-site-hosting#functions).

## Referencing it

Add one directive, depending on the file type:

| File type | Directive |
| --------- | --------- |
| `.cs` | `#:package StaticSiteHost.Abstractions@*` |
| `.linq` | `<NuGetReference>StaticSiteHost.Abstractions</NuGetReference>` in the XML header |

```csharp
#:package StaticSiteHost.Abstractions@*
```

```xml
<NuGetReference>StaticSiteHost.Abstractions</NuGetReference>
```

Then add `using StaticSiteHost.Functions;`. In a `.linq` file, add it as a namespace import
(`<Namespace>StaticSiteHost.Functions</Namespace>`). The fakes are in
`StaticSiteHost.Functions.Testing`.

**The server compiles against its own copy.** The server never downloads this package. It
compiles your file against the copy it runs, so it ignores the version named in the file. The
build output says which version it used. The package on nuget.org is for your editor's
IntelliSense, and for running the file in LINQPad or with `dotnet run`. Those tools need a
version; `@*` works, and the server ignores it.

* If your file uses a property or method that the server's copy does not have, the build fails.
  The compiler error names it.
* Functions compiled against a newer version than the server runs (for example, after the server
  is rolled back) are not loaded and answer nothing. Deploy them again to compile them against
  the server's copy.
* Only Static Site Host implements `ISite`, `ISiteVariables`, `IRealtime` and `IAiChat`, and the
  fakes stand in for them outside the server. Do not implement them in your own code. Later
  versions add properties and methods, and your class would stop compiling.

## A handler

```csharp
#:sdk Microsoft.NET.Sdk.Web
#:package StaticSiteHost.Abstractions@*

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using StaticSiteHost.Functions;

public static class Handlers
{
    private static readonly JsonSerializerOptions Json = new();

    [HttpGet("/hello")]
    public static IResult Hello(HttpContext context)
    {
        ISite site = context.Site();
        return Results.Json(new { site = site.Domain, greeting = site.Variables.Get("GREETING", "Hello") }, Json);
    }
}
```

`context.Site()` works from any code that has the `HttpContext`, not only from the handler. A
handler can also take an `ISite` or an `ISiteVariables` parameter, and the server fills it in.
Routes, parameter types and return values are in the main README:
[Functions](https://github.com/mattjcowan/static-site-hosting#functions).

## ISite

The site a request is for, as the functions that answer it see it.

| Property | Type | What it is |
| ------ | ---- | ---------- |
| `Domain` | `string` | the domain the request is for, such as `demo.example.com`. For global functions, the host the request arrived on. Outside a request, the domain whose functions are running, or `global` for the global functions |
| `Data` | `DirectoryInfo` | the data folder: the site's own for a site's functions, one shared folder for the global functions. The server never serves it. Deploys and rollbacks do not change it. Renaming the site moves it; deleting the site deletes it. It exists once you have it |
| `Variables` | `ISiteVariables` | the site's variables, secrets included. In a request, they are read once, when the request starts. Outside a request, each read sees the latest values. The global functions have no variables of their own outside a request |
| `Services` | `IServiceProvider` | the functions' services (see [`[ConfigureServices]`](#configureservices)). In a request, a scope made for the request. In a job or background service, the root provider. Reading it while `[ConfigureServices]` methods are still running throws `InvalidOperationException` |
| `Realtime` | `IRealtime` | the pages connected to the site (see [IRealtime](#irealtime)) |
| `Ai` | `IAiChat` | the site's AI (see [IAiChat](#iaichat)) |

**Which site.** In a request, `Realtime` and `Ai` belong to the site the request is for. This is
also true for global functions. In a job or background service, they belong to the functions'
own site. The global functions belong to no site outside a request, so there `Realtime` and `Ai`
throw `InvalidOperationException`.

**After the functions are replaced.** The functions are replaced by a deploy, or retired when
their site is deleted or renamed. Once they have had their time to stop, every property but
`Domain` throws `InvalidOperationException`. So do the variables, realtime side and AI that the
`ISite` gave out. A request's `ISite` gives out no `Data` folder once its functions have been
unloaded.

## ISiteVariables

Every variable of the site, secrets included. A variable's value is the value set on the site, or
else the default in the site's `_variables.json`.

| Name | Returns | What it does |
| ------ | ------- | ------------ |
| `this[string name]` | `string?` | the value, or `null` when there is no such variable |
| `TryGet(string name, out string value)` | `bool` | `false` when there is no such variable (and `value` is `null`) |
| `Get(string name, string fallback = "")` | `string` | the value, or `fallback` when the variable is missing or its value is empty |
| `All` | `IReadOnlyDictionary<string, string>` | every variable by name, secrets included; take care what you send to a browser |
| `IsPublic(string name)` | `bool` | `true` when browsers can read the variable too; `false` for a private or secret variable, or a name the site does not have |

Names are case-sensitive. See [Variables](https://github.com/mattjcowan/static-site-hosting#variables).

## SiteHttpContextExtensions

| Name | What it does |
| ------ | ------------ |
| `ISite Site(this HttpContext context)` | the site the request is for; throws `InvalidOperationException` outside Static Site Host unless a test called `UseSite` |
| `bool TryGetSite(this HttpContext context, out ISite? site)` | the site, when there is one |
| `void UseSite(this HttpContext context, ISite site)` | makes `site` the request's site, so a handler can run outside the server |
| `const string ItemKey = "StaticSiteHost.Site"` | the `HttpContext.Items` key the server stores the site under; use the methods instead |

## Middleware

`[Middleware]` marks a method that runs on every request to the site, files included, before any
handler. Use it for a gate over a section, or a header on every response.

```csharp
[Middleware(Order = 10)]
public static async Task RequireToken(HttpContext context, Func<Task> next)
{
    if (context.Request.Path.StartsWithSegments("/private") && !context.Request.Headers.ContainsKey("X-Token"))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
}
```

| Rule | Detail |
| ---- | ------ |
| Signature | `public static Task Name(HttpContext context, Func<Task> next, …)` on a `public` class; returns `Task`, `ValueTask` or `void`, not `async void` |
| `Func<Task> next` | required; runs the rest of the request once. Await it, and call it at most once: a second call throws. Not calling it ends the request |
| `int Order { get; init; }` | lower runs first. Default 0. Ties run in order of class name, then method name |
| Other parameters | anything a handler takes; simple values come from the query string only |

See [Middleware](https://github.com/mattjcowan/static-site-hosting#middleware).

## Services and jobs

### ConfigureServices

`[ConfigureServices]` registers services, once, as the functions load. Handlers, middleware, hooks
and jobs then take them as parameters, or reach them through `site.Services`.

```csharp
public static void Name(IServiceCollection services, …)
```

* `public static void`, on a `public` class, not `async`. It must take an `IServiceCollection`.
* It may also take `ISite`, `ISiteVariables`, `IRealtime`, `IAiChat`, `DirectoryInfo`,
  `IReadOnlyDictionary<string, string>`, a plain `ILogger` and a `CancellationToken`. Nothing
  else, because the services do not exist yet.
* The collection already holds `ILoggerFactory`, `ILogger<T>`, a plain `ILogger`,
  `IDataProtectionProvider`, `IHttpClientFactory`, `TimeProvider`, `ISite`, `ISiteVariables`,
  `IRealtime` and `IAiChat`.
* Several such methods are allowed. They run in order of class name, then method name.

### BackgroundService

`[BackgroundService]` runs a method for as long as the functions are live. It must stop when its
token is cancelled.

```csharp
public static Task Name(CancellationToken stoppingToken, …)
```

* `public static`, on a `public` class. Returns `Task` or `ValueTask`. Must take a
  `CancellationToken`.
* The token is cancelled when the functions are replaced or removed, or the server stops. The
  server waits 15 seconds for it to return, then logs a warning.
* If it throws, the server logs it and starts it again after 1 second, doubling up to 1 minute.
* It may take `ISite`, `ISiteVariables`, `IRealtime`, `IAiChat`, `DirectoryInfo`,
  `IReadOnlyDictionary<string, string>`, a plain `ILogger`, `IServiceProvider` (the root
  provider) and any registered service.

### Schedule and Every

| Attribute | Constructor | Properties | Runs |
| --------- | ----------- | ---------- | ---- |
| `[Schedule("0 3 * * *")]` | `ScheduleAttribute(string cron)` | `string Cron`, `bool RunOnStart` | on a five-field cron schedule, in UTC |
| `[Every("10m")]` | `EveryAttribute(string interval)` | `string Interval`, `bool RunOnStart` | at a fixed interval: `30s`, `5m`, `2h`, `1d`, or a `TimeSpan` such as `01:30:00`; from 10 seconds to 365 days |

```csharp
public static Task Name(…)   // or ValueTask, or void (not async void)
```

* `public static`, on a `public` class. Returns `Task`, `ValueTask` or nothing.
* `RunOnStart = true` also runs the job once as soon as the functions load. The default is
  `false`.
* Cron fields: minute (0-59), hour (0-23), day of month (1-31), month (1-12), day of week (0-7;
  0 and 7 are Sunday). Each is `*`, a number, `a-b`, `a,b,c`, `*/n` or `a-b/n`. Names such as
  `MON`, seconds, `@daily`, `L`, `W` and `#` fail the build.
* Runs never overlap: a run that comes due while the last one is running is skipped, with a
  warning. Missed runs are not made up. An exception is logged and counted, and the next run goes
  ahead.
* It may take a `CancellationToken`, `ISite`, `ISiteVariables`, `IRealtime`, `IAiChat`,
  `DirectoryInfo`, `IReadOnlyDictionary<string, string>`, a plain `ILogger`, `IServiceProvider`
  (a scope made for the run) and any registered service.

### Example

```csharp
[ConfigureServices]
public static void Configure(IServiceCollection services) => services.AddSingleton<Visits>();

[HttpGet("/visits")]
public static string Count(Visits visits) => visits.Next().ToString();

[Every("10m", RunOnStart = true)]
public static Task Stamp(ISite site, CancellationToken stoppingToken) =>
    File.WriteAllTextAsync(Path.Combine(site.Data.FullName, "stamp.txt"), DateTimeOffset.UtcNow.ToString("O"), stoppingToken);

[BackgroundService]
public static async Task Heartbeat(CancellationToken stoppingToken, ILogger log)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        log.LogInformation("Still here");
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
    }
}
```

These methods run with no request. So they cannot take `HttpContext`, `HttpRequest`,
`HttpResponse`, `Func<Task>` or a query value. When you upload the files, the server refuses such
a method, a schedule it cannot run, and a background service without a `CancellationToken`. The
message says what to change.

When the functions are replaced, or their site is deleted or renamed, the token is cancelled and
the work gets a few seconds to finish. Stop when the token says so. Once the old functions are
unloaded, the `ISite` they were given throws, and so do the variables, realtime side and AI it
gave out. They never reach a site that may no longer be theirs.

See [Services and jobs](https://github.com/mattjcowan/static-site-hosting#services-and-jobs).

## IRealtime

`site.Realtime`, or an `IRealtime` parameter, reaches the pages open on the site that listen with
`site.realtime` from `/_host/site.js`.

| Name | What it does |
| ------ | ------------ |
| `int ConnectionCount { get; }` | how many pages are connected now |
| `IReadOnlyList<RealtimeConnection> Connections { get; }` | every connection, oldest first |
| `IReadOnlyList<string> Groups { get; }` | the groups with at least one connection, in name order |
| `IReadOnlyList<RealtimeConnection> Members(string group)` | the connections in a group, oldest first |
| `Task PublishAsync(string eventName, JsonElement? payload = null, CancellationToken ct = default)` | sends an event to every connection |
| `Task PublishToGroupAsync(string group, string eventName, JsonElement? payload = null, CancellationToken ct = default)` | sends to the connections in a group |
| `Task PublishToUserAsync(string user, string eventName, JsonElement? payload = null, CancellationToken ct = default)` | sends to every connection of one user |
| `Task PublishToConnectionAsync(string connectionId, string eventName, JsonElement? payload = null, CancellationToken ct = default)` | sends to one connection |
| `Task AddToGroupAsync(string connectionId, string group, CancellationToken ct = default)` | puts a connection in a group |
| `Task RemoveFromGroupAsync(string connectionId, string group, CancellationToken ct = default)` | takes a connection out of a group |
| `Task RemoveGroupAsync(string group, CancellationToken ct = default)` | takes every connection out of a group |
| `Task DisconnectAsync(string connectionId, CancellationToken ct = default)` | closes a connection; the page does not reconnect on its own |

`RealtimeExtensions` adds a generic overload of each publish method that takes any payload and
your own `JsonSerializerOptions`:

```csharp
Task PublishAsync<T>(this IRealtime realtime, string eventName, T payload, JsonSerializerOptions options, CancellationToken ct = default)
Task PublishToGroupAsync<T>(this IRealtime realtime, string group, string eventName, T payload, JsonSerializerOptions options, CancellationToken ct = default)
Task PublishToUserAsync<T>(this IRealtime realtime, string user, string eventName, T payload, JsonSerializerOptions options, CancellationToken ct = default)
Task PublishToConnectionAsync<T>(this IRealtime realtime, string connectionId, string eventName, T payload, JsonSerializerOptions options, CancellationToken ct = default)
```

Hold the options in a static field of your own file. Options owned by the server would keep every
version of your code in memory.

```csharp
private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

[HttpPost("/api/posts/{slug}/publish")]
public static async Task<IResult> Publish(ISite site, string slug)
{
    // …mark the post published…
    await site.Realtime.PublishToGroupAsync("journal", "post.published", new { slug }, Json);
    return Results.NoContent();
}
```

`RealtimeConnection` is a record:
`RealtimeConnection(string Id, string? User, DateTimeOffset ConnectedUtc, IReadOnlyList<string> Groups)`.
`Id` is what the page reads as `site.realtime.connectionId`, and it changes when the page
reconnects. `User` is what the site's `[RealtimeConnect]` hook returned, or `null`.

**Rules.**

* Event and group names are 1 to 64 characters, each a letter, a digit, `_`, `.`, `:` or `-`
  (`^[A-Za-z0-9_.:-]{1,64}$`). They are case-sensitive.
* A name that breaks the rule, an empty user or connection id, or a payload over 256 KB of JSON
  throws `ArgumentException`.
* `AddToGroupAsync` throws `InvalidOperationException` when the connection is in as many groups as
  it may join, or the site has as many groups as it may have.
* A connection id that is not connected to this site is ignored. Everything is private to the
  site: a connection id from another site is never reached.
* The lists are snapshots, taken when you read them.
* A page receives an event as `(payload, { event, group })`, where `group` is the group it was
  sent to, or `null`.

See [Realtime](https://github.com/mattjcowan/static-site-hosting#realtime).

## IAiChat

`site.Ai`, or an `IAiChat` parameter, chats with the provider an administrator chose for the
site, with its model and system prompt. It works whether or not the site lets its visitors chat.

| Name | What it does |
| ------ | ------------ |
| `bool IsConfigured { get; }` | `true` when the site has a provider; when `false`, both methods throw `AiChatException` |
| `string? Model { get; }` | the model a request uses when it names none: the site's choice, or the provider's default; `null` without a provider |
| `string? ProviderKind { get; }` | `openai` (OpenAI and every server that copies its API: LiteLLM, Ollama and the rest) or `anthropic`; `null` without a provider. Never the provider's name, address or key |
| `Task<AiChatResponse> CompleteAsync(AiChatRequest request, CancellationToken ct = default)` | sends the conversation and waits for the whole answer |
| `IAsyncEnumerable<AiChatChunk> StreamAsync(AiChatRequest request, CancellationToken ct = default)` | yields a chunk per piece of text, then a last chunk whose `Final` holds the whole response. Nothing is sent until you start the loop. Stopping early, or cancelling, closes the connection to the provider |

```csharp
[HttpPost("/api/summary")]
public static async Task<IResult> Summary(IAiChat ai, HttpContext context)
{
    if (!ai.IsConfigured) return Results.Text("No AI here.", statusCode: 503);

    var answer = await ai.CompleteAsync(new AiChatRequest
    {
        Messages = [AiMessage.User("Summarise tonight's sky in one sentence.")],
        MaxTokens = 100
    }, context.RequestAborted);

    return Results.Text(answer.Text);
}
```

**`AiChatRequest`** (a class with `init` properties):

| Property | Type | Meaning |
| -------- | ---- | ------- |
| `Messages` | `IReadOnlyList<AiMessage>` | required; the conversation, oldest first, ending with the turn to answer |
| `System` | `string?` | instructions; they follow the site's system prompt as a new paragraph |
| `Model` | `string?` | the model, in the provider's naming; `null` means `IAiChat.Model` |
| `MaxTokens` | `int?` | the longest answer; `null` leaves it to the provider, except Anthropic, which gets 1024 |
| `Temperature` | `double?` | `null` leaves it to the provider; some models refuse a request that sets it |
| `Tools` | `IReadOnlyList<AiTool>?` | tools the model may call; functions only, at most 128 |
| `ToolChoice` | `AiToolChoice?` | `AiToolChoice.Auto` (the default), `None`, `Required` or `Tool("name")` |

**`AiMessage(string Role, string Content)`** is a record. Its constants are `UserRole` (`"user"`),
`AssistantRole` (`"assistant"`), `SystemRole` (`"system"`) and `ToolRole` (`"tool"`). Its factory
methods are `AiMessage.User(content)`, `AiMessage.Assistant(content)`, `AiMessage.System(content)`,
`AiMessage.AssistantToolCalls(text, calls)` and `AiMessage.ToolResult(toolCallId, content, isError)`.
The tool fields are `ToolCalls`, `ToolCallId` and `IsError`.

**`AiChatResponse(string Text, string Model, int InputTokens, int OutputTokens, string? StopReason)`**
is a record. Token counts are 0 when the provider did not say. `StopReason` is in the provider's
words: `stop` or `length` from OpenAI-compatible providers, `end_turn` or `max_tokens` from
Anthropic; a turn that ends in tool calls is `tool_calls` from both. `ToolCalls` holds the calls,
empty for a text answer, and empty too when the model ran out of tokens part way through them.

**Tool calling.** `AiTool(Name, Description, InputSchema)` describes a tool, with the JSON Schema of
its input as a `JsonElement` object. `AiToolCall(Id, Name, Arguments)` is a call the model made;
`Arguments` is the parsed input, or a JSON string of the raw text when the model wrote input that
is not JSON. Loop until `ToolCalls` is empty: append `AiMessage.AssistantToolCalls(answer.Text,
answer.ToolCalls)`, then one `AiMessage.ToolResult` per call, and ask again. Every call needs its
result straight after the turn that made it, and a conversation with calls in it keeps sending
its tools (`AiToolChoice.None` makes the model answer in text). `StreamAsync` gives the calls
whole in the last chunk's `Final`. `FakeAiChat.ReplyToolCall(name, arguments)` and
`ReplyToolCalls(...)` script calls for tests.

**`AiChatChunk(string? Text, AiChatResponse? Final)`** is a record. `Text` is `null` on the last
chunk, and `Final` is set only on the last chunk.

**Errors.**

* `AiChatException` is thrown when the site has no provider, the provider cannot be reached or
  does not answer in time, or it refuses the request. Its `int? StatusCode` is the provider's HTTP
  status (such as 401 or 429), or `null` when there was no answer. Its message never contains the
  provider's key or address. Its `string? Reason` says why when the server can tell, as one of its
  constants: `ToolsUnsupported` (`"tools-unsupported"`: the model cannot use tools; choose another),
  `ContextTooLong` (`"context-too-long"`), `RateLimited` (`"rate-limited"`, a 429), `Auth` (`"auth"`,
  a 401 or 403) or `Unavailable` (`"unavailable"`: unreachable, timed out, 5xx, or broken off).
  Otherwise `null`. `ToolsUnsupported` is set only when the request offered tools and the provider
  said plainly that the model cannot take them, so a malformed tool schema is not mistaken for it.
* `ArgumentException` is thrown when the request has no messages, or a message has an unknown
  role, or its tools or tool calls break the rules above (a bad tool name or schema, a call
  without its result), before anything is sent.
* A failure part way through `StreamAsync` ends the loop with an `AiChatException`.

Every call is billed to the owner of the provider's key. So a handler that anyone can reach
should decide for itself who may ask. See
[Chatting from functions](https://github.com/mattjcowan/static-site-hosting#chatting-from-functions).

## Hooks

Three attributes let the site decide who may do what from a browser. Each marks one
`public static` method, on a `public` class, in the site's functions. The method runs with the
request (cookies included). It may take what a handler takes, except values from the query
string.

| Attribute | Returns | Extra parameter | Decides |
| --------- | ------- | --------------- | ------- |
| `[RealtimeConnect]` | `string?`, `Task<string?>`, `ValueTask<string?>`, `bool`, `Task<bool>` or `ValueTask<bool>` | — | whether a page may connect to the realtime hub. A string is the connection's user; `null` or `""` refuses; `true` connects with no user; `false` refuses |
| `[RealtimeJoin]` | `bool`, `Task<bool>` or `ValueTask<bool>` | `string group` (required) | whether a page may join a group |
| `[AiAccess]` | `bool`, `Task<bool>` or `ValueTask<bool>` | — | whether a browser may chat at `/_host/ai/chat`. With this hook, the site's "let every visitor chat" setting is not used |

```csharp
// Who a realtime connection belongs to: a string is its user, null refuses it. Or return bool.
[RealtimeConnect]
public static async Task<string?> Who(HttpContext context, DirectoryInfo data) =>
    (await Accounts.CurrentUserAsync(context, data))?.Name ?? "guest";

// Whether a page may join a group; the group comes in the parameter named group.
[RealtimeJoin]
public static async Task<bool> MayJoin(HttpContext context, DirectoryInfo data, string group) =>
    group != "staff" || await Accounts.CurrentUserAsync(context, data) is not null;

// Whether a browser may chat at /_host/ai/chat. With one, the site's "let every visitor chat"
// setting is not consulted.
[AiAccess]
public static async Task<bool> SignedIn(HttpContext context, DirectoryInfo data) =>
    await Accounts.CurrentUserAsync(context, data) is not null;
```

(`Accounts.CurrentUserAsync` stands for your own sign-in code.)

**Rules.**

* Without a hook, everything is allowed: every page may connect and join any group, and the
  site's "let every visitor chat" setting decides who may chat.
* With a hook, a failure refuses. A hook that throws refuses, and the exception goes to the server
  log. Functions that declare a hook but cannot be loaded also refuse.
* Only the site's own functions are asked. The global functions' hooks never decide for a site.
* The functions can have at most one method with each hook attribute. A second fails the build.
* Leave the `HttpContext` as you found it. The server restores its `Items` and its `User` after a
  hook returns, but it cannot undo anything registered on it. A realtime connection's context
  lives as long as the connection. So register nothing: no `Response.OnCompleted`, no
  `Response.RegisterForDispose`, and nothing set in its `Features`.
* The `[AiAccess]` hook runs after the request's body has been read, and after the request was
  counted against the per-address limit. So there is no body left for it to read.
* Middleware does not run for these requests. The hook is the whole check.

See [Who may connect and join](https://github.com/mattjcowan/static-site-hosting#who-may-connect-and-join)
and [Who may chat](https://github.com/mattjcowan/static-site-hosting#who-may-chat).

## Attributes found by name

The server finds every attribute in this package by its name, not by its type. So an attribute of
your own with the same name works the same, if it has the same shape:

| Attribute | Shape your own attribute needs |
| --------- | ------------------------------ |
| `MiddlewareAttribute` | an `int Order` property |
| `ScheduleAttribute`, `EveryAttribute` | the schedule or interval as the one constructor argument, and a `bool RunOnStart` property |
| `ConfigureServicesAttribute`, `BackgroundServiceAttribute`, `RealtimeConnectAttribute`, `RealtimeJoinAttribute`, `AiAccessAttribute` | nothing more |

A method can have only one of these attributes. The build fails for a method with two.

## Outside the server

`StaticSiteHost.Functions.Testing` has fakes, so you can run a handler in a LINQPad harness or a
unit test:

```csharp
var context = new DefaultHttpContext();
context.UseSite(new FakeSite { Domain = "demo.localhost" });
```

**`FakeSite`** implements `ISite`:

| Property | Default |
| ------ | ------- |
| `string Domain` | `test.localhost` |
| `DirectoryInfo Data` | a new empty folder under the system's temp folder, made on first use and left there so you can look at it |
| `FakeSiteVariables Variables` | empty |
| `IServiceProvider Services` | an empty provider |
| `FakeRealtime Realtime` | a new, empty `FakeRealtime` |
| `FakeAiChat Ai` | a new `FakeAiChat` with no replies queued |

All of them can be set. To give a handler what your `[ConfigureServices]` method registers, build
the services yourself:

```csharp
var services = new ServiceCollection();
Setup.Configure(services, site);
site.Services = services.BuildServiceProvider();
```

**`FakeSiteVariables`**: `Set(string name, string value, bool isPublic = false)` adds a variable
or replaces its value, and returns the same object, so calls can be chained:
`site.Variables.Set("GREETING", "Hi").Set("API_KEY", "not-a-real-key")`.

**`FakeRealtime`** implements `IRealtime`, in memory:

| Name | What it does |
| ------ | ------------ |
| `RealtimeConnection Connect(string id, string? user = null)` | makes a connection, as a page opening would; throws `ArgumentException` if the id is empty or already connected |
| `bool Disconnect(string id)` | ends a connection; `false` when there is none with that id |
| `IReadOnlyList<PublishedEvent> Published` | everything published so far, oldest first |

`PublishedEvent(string Target, string EventName, JsonElement? Payload)` is a record. `Target` is
`all`, `group:{name}`, `user:{user}` or `connection:{id}`. `FakeRealtime` checks names and
payloads as the server does, and throws the same exceptions. It records every publish, whether or
not any connection would receive it. It has none of the server's group limits.

**`FakeAiChat`** implements `IAiChat`, with replies you queue:

| Name | What it does |
| ------ | ------------ |
| `FakeAiChat Reply(string text)` | queues the text of the next answer; returns the same object, so calls can be chained |
| `FakeAiChat ReplyToolCall(string name, object? arguments = null)`, `ReplyToolCalls(...)` | queues a turn of tool calls |
| `FakeAiChat Fail(AiChatException exception)` | queues a failure: the next call throws it. Set its `Reason` to test each kind of failure |
| `IReadOnlyList<AiChatRequest> Requests` | every request sent so far, in order |
| `bool IsConfigured { get; set; }` | `true` by default; `false` makes both methods throw `AiChatException`, as the server does |
| `string? Model { get; set; }` | `fake-model` by default |
| `string? ProviderKind { get; set; }` | `openai` by default; `null` while `IsConfigured` is `false` |

Every request is checked as the server checks it, so one the server would refuse throws the same
`ArgumentException` without taking a reply. Each call takes the next queued reply. A call with no reply queued throws
`InvalidOperationException`. `StreamAsync` yields the reply in about three pieces, split between
words, then the whole response. Token counts are word counts, and `StopReason` is `stop`.

**A complete LINQPad harness.** Save this as `Ask.linq`. In LINQPad it runs `Main`. On the server,
the `#if LINQPAD` block is compiled away and only the handler is deployed.

```
<Query Kind="Program">
  <NuGetReference>StaticSiteHost.Abstractions</NuGetReference>
  <Namespace>Microsoft.AspNetCore.Http</Namespace>
  <Namespace>Microsoft.AspNetCore.Mvc</Namespace>
  <Namespace>StaticSiteHost.Functions</Namespace>
  <Namespace>StaticSiteHost.Functions.Testing</Namespace>
  <Namespace>System.Text.Json</Namespace>
  <IncludeAspNet>true</IncludeAspNet>
</Query>

#if LINQPAD
async Task Main()
{
    var site = new FakeSite { Domain = "demo.localhost" };
    site.Variables.Set("GREETING", "Hello", isPublic: true);
    site.Realtime.Connect("page-1", user: "ada");
    site.Ai.Reply("Orion, Cassiopeia and Pegasus.");

    var context = new DefaultHttpContext();
    context.UseSite(site);

    var result = await AskHandlers.Ask(context, site, "Name three constellations.");

    result.Dump();                    // the IResult the handler returned
    site.Realtime.Published.Dump();   // what it sent to the pages
    site.Ai.Requests.Dump();          // what it asked the AI
}
#endif

public static class AskHandlers
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HttpPost("/ask")]
    public static async Task<IResult> Ask(HttpContext context, ISite site, string question)
    {
        if (!site.Ai.IsConfigured) return Results.Text("This site has no AI.", statusCode: 503);

        var answer = await site.Ai.CompleteAsync(new AiChatRequest
        {
            Messages = [AiMessage.User(question)],
            MaxTokens = 200
        }, context.RequestAborted);

        await site.Realtime.PublishAsync("question.answered", new { question }, Json);

        return Results.Json(new { greeting = site.Variables.Get("GREETING", "Hi"), answer = answer.Text }, Json);
    }
}
```

`samples/functions/SiteFunctions.cs` and `SiteFunctions.linq` in the repository show the same
pattern in both file formats.

## More

The main README covers the rest:

* [Functions](https://github.com/mattjcowan/static-site-hosting#functions): routes, parameter types, return values, the data folder, building and testing
* [Attributes](https://github.com/mattjcowan/static-site-hosting#attributes): every attribute in one table
* [Middleware](https://github.com/mattjcowan/static-site-hosting#middleware)
* [Services and jobs](https://github.com/mattjcowan/static-site-hosting#services-and-jobs)
* [Variables](https://github.com/mattjcowan/static-site-hosting#variables)
* [Realtime](https://github.com/mattjcowan/static-site-hosting#realtime), and [IRealtime in functions](https://github.com/mattjcowan/static-site-hosting#in-functions)
* [AI](https://github.com/mattjcowan/static-site-hosting#ai)
