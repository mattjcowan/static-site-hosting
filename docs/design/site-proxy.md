# Design: proxy routes, certificates and DNS

Status: phases 1–5 built. Phases 1–4 are released (0.5.0 to 0.10.0); phase 5, other domains for
a site, was built on 2026-10-23 and is not released yet. See [Progress](#progress) at the end for
what was built, where it differs from the design, and what comes next. User-facing documentation lives in the README's
[Proxy routes](../../README.md#proxy-routes) section.

## The goal in one paragraph

Today a site is a zip of files, plus functions. This design adds a third thing a site can do:
**forward a path to a backend URL**. You create a site with no files, open its **Proxy** tab, and
add routes such as `/api → http://orders-api:8080` and `/ → http://web:3000`. Each route has its
own switches for WebSockets, streaming, HTTP/2, timeouts, body size, cookies and headers. Because
it sits behind the passcode gate, the site's middleware and its header rules, any Docker container
or private-network service gets a passcode or a sign-in page in front of it for free.

Later phases turn the server into a complete front door: server-wide routes (one API for many
sites), TLS in the server itself with Let's Encrypt, self-signed or uploaded certificates, and a
Cloudflare integration that creates DNS records. Together that is a better
[Nginx Proxy Manager](https://nginxproxymanager.com/): the same "proxy hosts with a UI" idea,
plus static sites, functions, realtime and AI on the same domains.

| Phase | What it delivers | Detail in this document |
| ----- | ---------------- | ----------------------- |
| 1 | Sites with no files; per-site proxy routes with per-route settings; API and UI | Full design |
| 1b | Everything over the API, and an MCP endpoint, so an agent or a CI job can set up a server and its sites without the UI | Design |
| 1c | An assistant in the management UI that sets things up by conversation, with the same tools | Built |
| 2 | Server-wide routes for a set of sites; replaces `tmp/ApiProxy.cs` | Built |
| 3 | TLS in the server: Let's Encrypt, self-signed, uploaded certificates (Cloudflare Origin CA included) | Sketch |
| 4 | Cloudflare: DNS records on site create/rename/delete, DNS-01 challenges, wildcard certificates | Sketch |
| 5 | Other domains for a site (aliases): more names for one site, wildcards such as `*.app.example.com` included | Full design; built |

## Where this compares to Nginx Proxy Manager

| Nginx Proxy Manager | Here | Phase |
| ------------------- | ---- | ----- |
| Proxy host | A site with a `/` route | 1 |
| Several domain names per proxy host, wildcards included | Other domains on a site | 5 |
| Custom locations | More routes on the same site, longest path wins | 1 |
| "Websockets support" switch | Per route | 1 |
| "HTTP/2 support" switch | Per route, to the backend; from the browser once TLS is in the server | 1, 3 |
| Redirection hosts | Already here: [redirect rules](../../README.md#redirects-and-rewrites) | – |
| Access lists (basic auth) | Already here: site passcode, plus the site's own middleware | – |
| Access lists (IP allow/deny) | New, small: a per-site address list | 1 (optional) |
| Let's Encrypt (HTTP and DNS challenges) | Built in | 3, 4 |
| Custom certificates | Upload PEM, Cloudflare Origin CA included | 3 |
| Streams (raw TCP/UDP) | Not planned | – |
| — | Static files, functions, middleware, jobs, realtime and AI on the same domain | – |

---

# Phase 1: per-site proxy routes

## What a person does

1. **Sites → New site.** Enter a domain and nothing else. The site exists with no release. Today
   the only way to create a site is to deploy a zip to it (`Pages/Index.cshtml.cs`,
   `OnPostDeployAsync`). Anyone who may deploy may create an empty site, since deploying to a
   new domain already creates one.
2. The new site opens on its **Proxy** tab (administrators) or its overview (members).
3. **Add route.** Path `/api`, target `http://orders-api:8080`. Save.
4. Expand the route to change its settings, such as turning on WebSockets or choosing HTTP/2.
5. **Test** sends a `GET` to the target from the server and shows the status, the time it took and
   the HTTP version used, so a typo in a container name shows up at once and not on the first
   visitor's request.

A site can mix all three kinds of answer. For example, files for the app shell, `/api` proxied to
a container, and a function at `/api/preview` that answers that one path itself.

## Where proxying sits in a request

```
passcode gate
  → /_host/ endpoints and the realtime hub
    → global middleware → site middleware
      → site functions → global functions
        → proxy routes            ← new
          → redirects and rewrites → files
```

Implemented as the `serveContent` delegate `SiteHostingMiddleware` already passes to
`FunctionHost.HandleAsync`: it becomes "forward if a route matches, otherwise serve content".

Why there:

* **After middleware**, so a site's own sign-in (`HttpContext.User` set in site middleware, as the
  blog sample does) protects proxied paths as well as files. `tmp/ApiProxy.cs` runs at
  `Order = -100` and so skips every other middleware. That was a workaround, not a goal.
* **After functions**, so a function can take over one path inside a proxied prefix (a mock, a
  preview, a health page) without the route being changed.
* **Before redirects and files**, so a route is never shadowed by a stray file in a later deploy.
  The route's **Files first** setting reverses this for one route, for the common "SPA files,
  anything missing goes to the backend" setup (e.g. a Next.js server behind exported assets).
  Redirect rules do not apply to a path a route answers. A redirect that sends visitors *into* a
  proxied path works as normal.

**A site with routes and no release.** A request no route matches gets the server's `404`
page, not today's `503 Nothing deployed yet`. That message is for a site that has nothing at all.

## Matching

* A route's **path** is a prefix matched by whole segments, case-insensitively: `/api` matches
  `/api`, `/api/` and `/api/users/1`, but not `/apis`. `/` matches everything.
* The **longest** matching path wins. Two routes may not have the same path.
* Not a glob, on purpose. Prefix-and-longest is how nginx `location` and every proxy manager
  behave, it needs no ordering in the UI, and it can't be ambiguous.
* Paths may not start with `/_host` or be `/healthz`. Those belong to the host.
* At most 50 routes per site.

## Route settings

Everything an administrator can set on a route. Defaults are chosen so that adding a path and a
target and pressing Save is right for a typical JSON API.

| Setting | Default | What it does | How it is done |
| ------- | ------- | ------------ | -------------- |
| **Path** | – | Prefix to match, as above | Route table |
| **Target** | – | `http://` or `https://` URL, optionally with a base path: `http://api:8080/v2`. `${env:NAME}` allowed anywhere in it | Destination prefix |
| **Remove the path** | on | `/api/users/1` → `{target}/users/1`. Off: → `{target}/api/users/1` | Transformer builds the URI |
| **Enabled** | on | Off keeps the route but passes its requests on as if it were not there | Route table |
| **WebSockets** | off | Off refuses an upgrade with `403` before anything is forwarded. On forwards HTTP/1.1 upgrades and WebSockets over HTTP/2 | Checked before `SendAsync`; YARP forwards both kinds |
| **WebSockets from other sites** | off | When off, an upgrade whose `Origin` is not this site's own is refused. Cookies are forwarded, so without this any page on the web could open a socket as the visitor | Same idea as `RealtimeRequestCheck` |
| **Streaming** | off | For server-sent events, long polls and large downloads: the response is not compressed by this server, is never buffered, and carries `X-Accel-Buffering: no` so an nginx in front doesn't buffer it either | `IHttpsCompressionFeature.Mode = DoNotCompress`, `AllowResponseBuffering = false` |
| **HTTP version** | Auto | **Auto:** HTTP/1.1 to `http://`, HTTP/2 when the backend offers it over `https://`. **HTTP/1.1 only.** **HTTP/2 only:** HTTP/2 even over plain `http://` (h2c), which gRPC to a container needs | `ForwarderRequestConfig.Version` and `VersionPolicy` |
| **Timeout** | 100 s | Longest the backend may go without sending a byte. Covers the wait for headers, gaps in a streamed body, and idle WebSockets | `ActivityTimeout` |
| **Largest request body** | the server's `MaxUploadBytes` | Lower it for an API that should never get big bodies, or raise it for an upload service | `IHttpMaxRequestBodySizeFeature` |
| **Host header** | the target's | **Target's:** what most backends expect. **Visitor's:** for a backend that does its own virtual hosting by name | Transformer |
| **Forwarded headers** | set | **Set:** `X-Forwarded-For/-Proto/-Host`, and `X-Forwarded-Prefix` when the path is removed, replacing any the visitor sent. **Off:** sends none | Transformer |
| **Request headers** | none | Name/value pairs to add or replace, or remove (empty value). Values may use `${env:NAME}`. This is how to give a backend an API key that the browser never sees | Transformer |
| **Cookie domain** | this host | Where cookies the backend sets are scoped. **This host:** `Domain=` is removed. **As sent:** left alone. **A parent domain:** must be a suffix of the site's domain, for subdomains that share a sign-in (what `ParentDomain` does in `ApiProxy.cs`) | Response transformer |
| **Rewrite redirects** | on | A `Location` pointing at the target becomes the same path on this site, with the route's path put back in front | Response transformer, as `RewriteLocation` in `ApiProxy.cs` |
| **Files first** | off | Serve the site's file if one exists at the path, and forward only when none does | Route table checks `SitePathResolver` |
| **Accept any certificate** | off | For an `https://` backend with a self-signed certificate on a private network. Shown with a warning | A second, separate `HttpMessageInvoker` |

Response headers are **not** a route setting. The site's existing [header rules](../../README.md#header-rules)
already do that by path, and they now apply to proxied responses as well (after the backend's
headers, so a rule wins). The reserved list in `SiteHeaderRules` still applies, so a rule still
cannot set `Set-Cookie` or `Content-Encoding`.

### Settings deliberately left out of phase 1

* **Several targets for one route** (load balancing, failover). Round robin on top of the forwarder
  is a few lines. Health checks and passive failover are what YARP's full config model is for,
  and that model doesn't fit here. Revisit if needed.
* **HTTP/3 to the backend.** Needs `libmsquic` in the image. Easy to add to the HTTP version list
  later.
* **Response caching.** Out of scope. A CDN does it better.
* **Rewriting paths inside response bodies.** Never. If a backend needs to know its prefix, it
  reads `X-Forwarded-Prefix`.

## What is forwarded, and what is not

**Request.** Method, path (after the route's path is removed or kept), query string, body
(streamed, never buffered) and headers, except the following:

* Hop-by-hop headers, which YARP removes.
* **The server's own cookies.** Every cookie whose name starts with `ssh.` (`ssh.auth`, `ssh.pass`)
  is removed from `Cookie`, always, with no setting. The backend never sees a session or passcode
  cookie for this server.
* Headers the route removes or replaces.

**Response.** Status, headers and body, with these changes:

* **Cookies from the backend:**
  * Any `Set-Cookie` whose name starts with `ssh.` is dropped. Otherwise a backend could plant a
    cookie that collides with the management session or a passcode cookie, which is exactly the
    sibling-subdomain risk `SiteHeaderRules` already guards against for static sites.
  * Every other cookie has its `Domain` handled as the **Cookie domain** setting says.
* **`Location`** is rewritten when **Rewrite redirects** is on.
* **Header rules** are applied (see above).
* **On a passcode-protected site:** `Cache-Control` is made `private` and `X-Robots-Tag: noindex,
  nofollow` is added, the same as for its files (`SiteContentServer.KeepPrivate`). Not optional.
  The gate is in this server, so a cache in front must not hand the response to someone who
  never passed it.

**When the backend fails.** The visitor gets a short answer, with nothing about the target in it.
Internal host names and ports are not the visitor's business. The server log gets the route, the
target and the YARP `ForwarderError`.

| Failure | Status |
| ------- | ------ |
| Can't connect, refused, DNS fails, TLS fails | `502` |
| No byte within the timeout | `504` |
| Target uses `${env:NAME}` and the variable is unset or not a valid URL | `502`, and it is logged once per change. The request is **not** passed on to the files: a misconfigured route fails closed |
| Visitor went away | nothing is sent |

The body is JSON (`{ "error": "..." }`) when the request accepts JSON, and otherwise the same small
HTML page the server uses for its other errors.

## Who may change routes, and SSRF

A proxy route makes the server send requests anywhere it can reach: other containers, the
management app on `localhost:8080`, a cloud metadata service. So:

* **Only administrators may add, change or remove routes**, in the UI and the API. Members can see
  a site's routes. `${env:NAME}` is shown as written, never expanded. This is the same line the
  code already draws for secrets, `${env:...}` values and AI settings.
* **A deploy can never add routes.** No `_proxy` file is read from a zip. Routes belong to the site,
  like its own header and redirect rules, and survive deploys and rollbacks. That keeps the rule
  that a deploy cannot widen what a site can do.
* **Blocked addresses.** The connection to a backend is checked against the address it actually
  resolved to, at connect time (`SocketsHttpHandler.ConnectCallback`). Checking then, not when
  the route is saved, means DNS rebinding can't get round it. Blocked by default:
  * link-local `169.254.0.0/16` and `fe80::/10`, which includes the cloud metadata services;
  * loopback `127.0.0.0/8` and `::1`, which is this server itself, including the management app.
    (As built: only the server's own ports, at any of this machine's addresses. See Progress.)

  Both lists can be changed with `SiteHosting:ProxyBlockedNetworks`. Private ranges
  (`10/8`, `172.16/12`, `192.168/16`, Docker networks) are **allowed**, because they are the whole
  point. This guards against mistakes and a stolen administrator session reaching the metadata
  service. It is not a boundary against administrators, who could run arbitrary code with functions
  anyway.
* Every change is written to the audit log (`site.proxy`, with the paths and targets).

## How it is built

### The YARP dependency

Add `Yarp.ReverseProxy` (2.3.0 at the time of writing; pin the exact version) to
`src/StaticSiteHost` only, and use only its **direct forwarding** API: `AddHttpForwarder()` and
`IHttpForwarder`. Not its config-file routes, clusters or `MapReverseProxy`. Matching, storage,
validation and UI stay this repo's own. YARP only moves bytes, which is the part with the edge
cases: streaming in both directions, the upgrade handshake for WebSockets over HTTP/1.1 and
HTTP/2, trailers, gRPC, cancellation on either side, and turning each failure into the right status.

This is the host's first NuGet package. What does **not** change:

* `StaticSiteHost.Abstractions` still has no dependencies (`StaticSiteHost.Abstractions.csproj:10`).
  Functions compile against it alone and never see YARP.
* A function bundle that brings its own `Yarp.ReverseProxy`, any version, still gets its own copy.
  `FunctionLoadContext.Load` defers to the default context only for the Abstractions assembly.
* The comment in `StaticSiteHost.Abstractions.csproj` that calls the host "the host, which takes
  no NuGet dependencies" is updated to say why YARP is the one exception.

### New and changed code

| File | What |
| ---- | ---- |
| `Models/ProxyRoute.cs` | The settings above. `SiteRecord.Proxy: List<ProxyRoute>`, replaced as a whole on every change, never edited in place, like `Variables` |
| `Services/SiteProxyService.cs` | Validates and saves routes, writes the audit entry, evicts the compiled table. Shared by the page and the API, as `SiteRuleService` is |
| `Serving/SiteProxy.cs` | The runtime: compiled route table per domain (cached, evicted on change, as `SiteContentServer` caches rules), `TryForwardAsync(context, site)`, and the error answers |
| `Serving/ProxyTransformer.cs` | `HttpTransformer` subclass: target URI, Host, forwarded headers, request header changes, `ssh.*` cookie removal. Response side: `Set-Cookie`, `Location`, header rules, private-site headers |
| `Serving/ProxyInvokers.cs` | Two long-lived `HttpMessageInvoker`s for the whole server: one validates certificates, one doesn't. Settings: `UseProxy = false`, `AllowAutoRedirect = false`, `UseCookies = false`, `AutomaticDecompression = None`, `EnableMultipleHttp2Connections = true`, `ConnectTimeout = 10 s`, `PooledConnectionLifetime = 5 min` (a restarted container usually comes back on a new IP address), `ConnectCallback` for blocked addresses |
| `Serving/SiteHostingMiddleware.cs` | `serveContent` becomes `() => _proxy.TryForwardAsync(...) ?? _content.ServeAsync(...)` |
| `Serving/SiteContentServer.cs` | `404`, not `503`, for a site with routes and no release. Header rules exposed for the transformer |
| `Pages/Sites/Index` | **New site** form |
| `Pages/Sites/Details` | **Proxy** section: route list, add/edit, Test |
| `Endpoints/ApiEndpoints.cs` | See below |
| `Program.cs` | `AddHttpForwarder()`, `SiteProxy`, `SiteProxyService`, `ProxyInvokers` |
| `Configuration/SiteHostingOptions.cs` | `ProxyBlockedNetworks`, `ProxyConnectTimeoutSeconds` |
| `README.md` | New **Proxy routes** section; updated "How hosting works" table, "Request resolution", roles table, configuration table and glossary; line 399 ("it is not a proxy") rewritten |

Sketch of the hot path, to show how little YARP surfaces:

```csharp
var error = await _forwarder.SendAsync(
    context,
    route.TargetPrefix,                     // expanded and validated when the table was compiled
    route.AcceptAnyCertificate ? _invokers.Lenient : _invokers.Strict,
    route.RequestConfig,                    // ActivityTimeout, Version, VersionPolicy, AllowResponseBuffering
    route.Transformer);                     // one per route, built with the table

if (error != ForwarderError.None)
    await WriteFailureAsync(context, route, error, context.Features.Get<IForwarderErrorFeature>());
```

### API

All under `/api/v1`, with API keys, like the rest:

| Method and path | Who | What |
| --------------- | --- | ---- |
| `POST /sites/{domain}` | members | Create an empty site. `409` if it exists |
| `GET /sites/{domain}/proxy` | members | The routes, with `${env:...}` unexpanded |
| `PUT /sites/{domain}/proxy` | administrators | Replace all routes. `400` with every validation error at once |
| `DELETE /sites/{domain}/proxy` | administrators | Remove all routes |
| `POST /sites/{domain}/proxy/test` | administrators | Body: a route or a path. Answers with status, time, HTTP version, or the error |

`GET /sites/{domain}` gains `proxyRoutes: <count>`.

### Tests

In `tests/StaticSiteHost.Tests`, with a real Kestrel backend on a random port, not mocks:

* matching: segments, longest wins, case, `Files first`, `/_host` and `/healthz` refused;
* path removal and base paths, query strings, encoded characters (`%2F`), trailing slashes;
* `ssh.*` cookies never reach the backend; `ssh.*` `Set-Cookie` never reaches the browser;
  each cookie-domain mode;
* `Location` rewriting, with and without path removal;
* WebSockets: refused when off, refused cross-site, echoed when on (HTTP/1.1, and HTTP/2 extended
  CONNECT if the test server can do it);
* streaming: an SSE backend's events arrive one by one, not all at the end;
* HTTP/2-only to an h2c backend;
* timeout → `504`, refused → `502`, unset `${env:}` → `502` and not the files;
* blocked networks: `127.0.0.1` and `169.254.169.254` refused, a private address allowed;
* header rules and private-site headers applied to proxied responses;
* a member cannot `PUT` routes; a deploy with a `_proxy` file adds nothing.

---

# Phase 1b: everything over the API, and an MCP endpoint

**Goal.** A fresh server can be set up end to end by a script, a CI job or an AI agent, with no one
signing in to the UI: create sites, deploy, configure rules, variables, proxy routes, functions and
AI, check the result, and read back what changed. An MCP endpoint gives agents the same abilities
as tools, with the same keys and the same rules.

**Principle.** Nothing new is allowed. Every endpoint and tool calls the service the UI already
uses, with the same validation, the same role checks (an API key can do what its owner can) and the
same audit entries. Where a rule currently lives in a Razor page handler, it moves to a service
first, so the page, the API and MCP cannot drift apart.

## Gaps closed

| Today UI-only | API | Who |
| ------------- | --- | --- |
| Making the first API key, which needs a sign-in | `Bootstrap:ApiKey`: a key given in configuration (`sshost_<id>_<secret>`) belongs to the bootstrap administrator and works from the first start. Changing or removing it revokes the old one. | operator |
| API keys | `GET/POST /api/v1/keys`, `DELETE /api/v1/keys/{id}`: the caller's own keys | anyone |
| Checking functions without going live (the editor's Check) | `POST /api/v1/sites/{domain}/functions/check`, `POST /api/v1/functions/check`: same upload formats and `?mode=` as `PUT`; returns a draft id, routes and diagnostics | administrators |
| Running a request against a draft or the live functions (the editor's Run) | `POST …/functions/test`: `{target, method, path, query, headers, body, host}`; returns status, headers, timing and the body (text, or base64 for binary) | administrators |
| AI providers | `POST /api/v1/ai/providers`, `PUT`/`DELETE /api/v1/ai/providers/{id}`, `POST …/{id}/test` | administrators |
| Users | `GET/POST /api/v1/users`, `PUT /api/v1/users/{id}` (role, disabled), `POST …/{id}/invite`, `DELETE …/{id}`; the same guards as the page (not yourself, at least one administrator) | administrators |
| The audit log | `GET /api/v1/audit?limit=&action=&domain=&since=`: newest first | administrators |

## The MCP endpoint

* **Where:** `/mcp` on the management host, Streamable HTTP, built with the official C# SDK
  (`ModelContextProtocol.AspNetCore`, pinned). It is the host's second NuGet dependency, after
  YARP. Stateless mode: every call is one HTTP request, authenticated like the API.
* **Auth:** the same API keys (`X-Api-Key` or `Authorization: Bearer`). A tool an API key's owner
  could not use through the API refuses with the same message.
* **Connecting:** `claude mcp add --transport http site-host https://deploy.example.com/mcp --header "X-Api-Key: sshost_…"`.
* **Tools** map onto the API, with two changes that suit an agent better than raw HTTP:
  * **Deploying takes files, not a zip:** `deploy_files(domain, files: [{path, content, base64?}])`.
    The server zips them and deploys as usual. Functions likewise take `[{name, content}]`.
  * **Rules take their text formats** (`_headers`, `_redirects`), the same as the UI's editors,
    which agents write well.
* **Not offered as tools:** changing your own password, API keys (so a conversation cannot
  create or revoke credentials), release downloads (the file tools cover what an agent needs),
  and anything the API does not do either. Everything else the API does has a tool since 0.6.1.

## Order of work

1. Bootstrap key, then the keys API.
2. Functions check and test.
3. AI providers.
4. Audit log.
5. Users (moving the page's rules into a service first).
6. MCP endpoint and tools, with tests that call the tools over HTTP.
7. README: API table, a "Setting up a server from a script" walkthrough, an MCP section.

---

# Phase 1c: an assistant in the management UI (sketch)

**Goal.** A panel in the management UI where an administrator types *"create shop.example.com,
send / to http://web.railway.internal:3000 and /api to http://api.railway.internal:8080"* and it
is done, with nothing to install.

**Why it is small.** The tools already exist, defined once in `Tools/SiteHostTools.cs` as
`AIFunction`s. MCP serves them over HTTP. The assistant would call the same functions in-process,
in the signed-in user's request, so it can do exactly what that user can and no more. The server
also already speaks to Anthropic and OpenAI-compatible providers with tool calling
(`AiChatService`).

**Shape.**

* **Admin → AI:** choose which provider and model the assistant uses. Off until chosen.
* **The panel:** a chat on every management page, streaming over the same request (cookie
  session and antiforgery, like the rest of the UI).
* **The loop:** the server sends the conversation and the tools (`AIFunction.JsonSchema` mapped
  to `AiTool`), runs each tool call the model makes with `AIFunction.InvokeAsync` (arguments, and
  the request's services so the tool sees the signed-in user), sends the results back, and
  repeats until the model answers in words. Capped at a number of rounds per message.
* **Confirmation:** a tool marked `Destructive` (`delete_site`, `remove_functions`, and, after the
  code review, `remove_proxy_route`, `remove_variable`, `remove_passcode`) is shown to
  the user with Run and Cancel, and runs only after Run. `Change` tools run and show what they did.
* **Context:** `SiteHostTools.Instructions` as the system prompt, plus the page the user is on
  (the site's domain on a site page). The manual comes through `read_docs` when needed, not
  pasted into every request.
* **Cost:** every message is billed to the chosen provider's key. Show the tokens each answer
  cost.

---

# Phase 2: server-wide routes

What `tmp/ApiProxy.cs` does today, without code: one backend serving `/api` for many sites.

* **Admin → Proxy** lists server-wide routes. Each route has the same settings as a site route,
  plus **Sites**: `*` for every site, or a pattern like `*.apps.example.com`.
* For a request, the site's own routes and the server-wide routes that apply to it go into one
  table. **A site route with the same path wins**, which replaces the `API_UPSTREAM` variable.
  A site can also **turn off** a server-wide route by path, which replaces `API_PROXY=off`.
* The site's Proxy tab shows the server-wide routes that apply to it, read-only, with where each
  comes from.

Mapping from `ApiProxy.cs`:

| `ApiProxy.cs` | Built in |
| ------------- | -------- |
| `ParentDomain`, which sites it answers for | Route's **Sites** pattern |
| `ParentDomain`, the cookie domain | **Cookie domain: a parent domain** |
| `DefaultUpstream` | **Target** |
| `Prefix = "/api"`, removed | **Path** `/api`, **Remove the path** on |
| `API_UPSTREAM` variable | A site route with the same path |
| `API_PROXY=off` variable | Turn the route off for that site |
| Removes `ssh.*` cookies | Always, every route |
| `RewriteLocation` | **Rewrite redirects** |
| 60 s `HttpClient` timeout | **Timeout** (an idle timeout, which is kinder to slow streams) |
| Buffers nothing, no WebSockets, HTTP/1.1 only | Streaming, WebSockets and HTTP/2 switches |
| Fails every site with `503` if it doesn't compile | No code to fail |

Why the variables are not carried over: members can set plain variables, and a variable that
chooses a proxy target would let a member point the server at any address.

---

# Phase 3: TLS in the server (sketch)

Today the server speaks plain HTTP on 8080, and the README says to put Caddy or nginx in front for
TLS. Phase 3 makes that optional. **Off stays the default**, so existing deployments don't change.

**Listening.** With TLS turned on (`SiteHosting:Tls:Enabled`), Kestrel listens on 80 and 443 (in the
container, 8080 and 8443, published as 80 and 443). Port 443 picks each connection's certificate by
SNI, using `ServerCertificateSelector`, from an in-memory map that changes without a restart.
Port 80 answers ACME challenges and redirects everything else to HTTPS, per site, unless the site
says otherwise. ALPN gives browsers HTTP/2 natively, so gRPC works end to end with no second proxy.
HTTP/3 needs `libmsquic`, and is a later switch.

**Certificate source, per site** (and for the management host):

| Source | When to use it | How |
| ------ | -------------- | --- |
| **Let's Encrypt, HTTP challenge** | The domain's DNS points at this server and port 80 is reachable. Works behind Cloudflare's proxy too | `/.well-known/acme-challenge/` answered by the host before any site, as `/healthz` is |
| **Let's Encrypt, DNS challenge** | Wildcards, or port 80 can't be reached | Phase 4, through Cloudflare's API |
| **Self-signed** | Behind Cloudflare in "Full" mode, or private networks | Generated with `CertificateRequest`, 1 year, renewed automatically |
| **Uploaded** | Cloudflare Origin CA (for "Full (strict)"), or a company CA | PEM certificate and key, administrators only |

**Renewal.** A hosted service checks daily, renews 30 days before expiry, retries with backoff,
and shows the state on the site's page: issued, expires, last error. Each failure goes to the
audit log. Tests and first setup use the Let's Encrypt **staging** directory, because the
production rate limits are easy to hit while experimenting.

**Storage.** `/data/config/certs/<domain>/`, private keys encrypted at rest with the existing Data
Protection key ring. The ACME account key is kept the same way.

**HSTS.** A per-site switch, off by default. Once it is on, a browser holds it for months, so
turning it on is shown with a warning.

**Choice to make at implementation: the ACME client.** Options:

* A NuGet ACME library (e.g. Certes). Check maintenance status first. Several .NET ACME libraries
  have gone quiet, and this would be a second dependency.
* **Recommended:** a small client of our own. RFC 8555 with HTTP-01 and DNS-01 only comes to new
  account, new order, authorizations, finalize and download. That is JWS signing with ECDSA
  P-256, which `System.Security.Cryptography` has, over `HttpClient`. Roughly 500 lines plus tests
  against [Pebble](https://github.com/letsencrypt/pebble), Let's Encrypt's test server. It keeps the
  dependency count at one, YARP, and keeps the code readable.

What phase 1 does to stay compatible: forwarded headers come from `HttpContext.Request`, so they
are right whether TLS ends in Kestrel or in a proxy in front. Nothing in phase 1 assumes port 8080
or plain HTTP.

# Phase 4: Cloudflare (sketch)

* **Admin → Integrations → Cloudflare:** an API token with `Zone:DNS:Edit` on the zones this server
  handles. Stored like a secret, administrators only, never shown again.
* **DNS records follow sites.** Creating, renaming or deleting a site creates, moves or deletes its
  `A`/`AAAA` record (or `CNAME` to a name you choose), with a per-site **Proxied by Cloudflare**
  switch. Only records this server created are touched: each is tagged with a comment such as
  `managed-by: static-site-host`.
* **DNS-01** challenges for Let's Encrypt, which allows wildcard certificates such as
  `*.apps.example.com`. One certificate then covers every site under that domain.
* **Recommended pairing:** Cloudflare "Full (strict)" with either Let's Encrypt or a Cloudflare
  Origin CA certificate here.
* **Worth considering instead of opening ports:** Cloudflare Tunnel (`cloudflared` as a sidecar
  container). The server keeps speaking plain HTTP inside, and no inbound port is opened at all.
  It needs no code here, just a documented compose file.

---

# Phase 5: other domains for a site (design)

**Goal.** One site answers at more than one domain: exact names such as `def.app.example.com`,
and wildcards such as `*.app.example.com`. Every name gets the same site: the same release,
functions, proxy routes, rules, variables and passcode. This is for every kind of site, not only
proxy routes. The case that drives it is a single-page app deployed once as an ordinary zip, then
opened at `abc.app.example.com`, `def.app.example.com` and any other name under
`app.example.com`. The app reads its subdomain from `location.hostname` and uses it in its API
calls. A proxy-only site gets the same thing Nginx Proxy Manager's "Domain names" field gives a
proxy host: several names, wildcards included.

**Today a site answers at exactly one domain.** `SiteStore` is a dictionary keyed by domain, the
middleware looks the `Host` header up in it exactly (`SiteHostingMiddleware.InvokeAsync`), and
`NormalizeDomain` refuses `*`. The pieces around that lookup already handle wildcards: shared
certificates (`*.apps.example.com` from Let's Encrypt over DNS-01, or a Cloudflare Origin one),
the **Sites** pattern of server-wide routes, and a Cloudflare Tunnel with a `*.example.com`
route. Only the step from a name to a site is missing.

## What a person does

1. Deploy the app to `app.example.com`, as today.
2. **Settings → Other domains** (administrators): add `*.app.example.com`, and save.
3. With Cloudflare connected, the server makes the `*.app` DNS record. With HTTPS in the server
   and no shared certificate that covers the wildcard, the site's HTTPS card says so and offers
   **Get a certificate**: the request on the HTTPS page, with the name filled in.
4. `https://abc.app.example.com/` now serves the same files. The app reads `abc` from
   `location.hostname`.

For the app's API calls, two shapes work, and neither needs anything new:

* The app calls its API at an absolute address and sends the tenant: `https://api.example.com/t/abc/…`.
  The API must then allow CORS from every name under the wildcard.
* **Simpler:** a proxy route `/api` on the site. The app calls `/api/…` on its own origin, so
  there is no CORS, and the site's passcode, middleware and header rules sit in front of the API.
  The target reads the name from `X-Forwarded-Host` (sent by default), or from `Host` with
  **Host header: visitor's**. A function reads it from `HttpContext.Request.Host`.

## The model

```csharp
// SiteRecord
public List<string> Aliases { get; set; } = [];
```

Each entry is a domain (`def.app.example.com`), or `*.` and a domain (`*.app.example.com`).
The site's `Domain` stays what it is today: the site's identity, its folder (`sites/<domain>/`),
the `{domain}` in every API path, and the key of everything kept per site (compiled rules,
limits, the passcode cookie's protector, realtime). Aliases are only more names to answer at.
Like the other lists on `SiteRecord`, the list is replaced as a whole on every change.

`Aliases` in code and the API; **Other domains** in the UI, since the UI's word for the address
a site answers at is *domain*.

**A wildcard covers one level.** `*.app.example.com` answers `abc.app.example.com`, but not
`x.abc.app.example.com`, and not `app.example.com` itself. That is what a wildcard certificate
covers, so every name an alias answers can have a certificate. A deeper level is another alias
(`*.abc.app.example.com`). The bare name is the site's domain, or an exact alias. (The **Sites**
pattern of a server-wide route matches at any depth. That pattern picks sites, which have
certificates of their own, so the two can differ.)

## Which site answers

```
a management host                         → the management app   (unchanged, always first)
a site's domain, or an exact alias        → that site
*. and the name without its first label   → the site with that wildcard alias
anything else                             → 404, or the management app when no management
                                            hosts are set (unchanged)
```

An exact name always wins over a wildcard, so a site at `admin.app.example.com` keeps its name
under another site's `*.app.example.com`. Because a wildcard covers one level, the wildcard step
is one dictionary lookup (drop the first label, look up `*.` and the rest), with no scan and no
"longest match".

`SiteStore` keeps two indexes beside `_sites`: exact names (domains and exact aliases) to site,
and wildcards to site. Both are rebuilt and swapped whole on every load, save, rename and delete,
so a request never sees half a change. `TryGet(domain)` stays for the API and the pages, by the
site's domain only. A new `Resolve(host)` is for requests.

## Rules for an alias

Checked on save, with every error reported at once, as for proxy routes:

* a domain `NormalizeDomain` accepts, or `*.` and one with at least two labels (`*.example.com`,
  not `*.com`);
* not the site's own domain, and no repeats;
* not another site's domain or exact alias, and not a wildcard another site already has;
* not a management host. A wildcard may cover one, since management hosts are matched first;
* **not the name the management app is being used at, when no management hosts are set.** The
  management app then answers at any name with no site, and an alias would take that name away,
  locking the administrator out. The error says to set `SiteHosting:ManagementHosts` first;
* at most 50 per site. Wildcards make long lists unnecessary.

**Who.** Administrators only, in the UI, the API and the tools; members see them. An alias makes
the server answer at a name, get certificates for it and make DNS records for it. That is the
same reach as rename, HTTPS and DNS, which are administrators-only. A deploy never brings aliases.

**A new site under another site's wildcard.** Creating a site, or renaming one, at a name another
site's wildcard covers takes that name from the other site. Visitors at a live name then see
something else, so it needs an administrator; a member is told which site answers that name
today. A name that is another site's exact alias is refused for everyone ("`def.app.example.com`
is a domain of `app.example.com`"): the administrator removes the alias first.

**Rename and delete.** Aliases are in `site.json`, so they move with a rename and go with a
delete. Renaming a site to one of its own aliases takes that name out of the list. DNS records
and certificates follow through the loops that already follow sites.

## What changes, and what does not

Most of the server keys on the site, not on the name, and needs nothing:

| Part | At an alias |
| ---- | ----------- |
| Files, SPA fallback, header rules, redirects | Unchanged: the same release and rules at every name. A redirect to a path stays on the name asked for |
| Functions | Unchanged. `ISite.Domain` is the site's domain; the name asked for is `HttpContext.Request.Host` |
| `/_host/site.js` | `site.domain` stays the site's domain. A page reads `location.hostname` for the name it was opened at |
| Proxy routes | Unchanged. The cross-site WebSocket check already compares `Origin` with the request's own host (`SiteProxy.IsCrossSite`). A parent cookie domain that does not cover an alias gives host-only cookies there, as after a rename (`ProxyTransformer`) |
| Server-wide routes | Matched on the site's domain only, so one site never has different routes at different names |
| Passcode | Works at every name. The cookie is host-only, so a visitor enters the passcode once per name. The attempt limit is per site, so more names do not give more tries |
| Limits (AI chat, realtime) | Per site, so a wildcard cannot multiply them |
| Variables, AI settings, branding | Unchanged |

Four places change:

* **Realtime.** `RealtimeRequestCheck.Refusal` compares the page's `Origin` with `site.Domain`, so
  a page at an alias would be refused. It takes the request's host instead: a page connects to the
  hub at its own name, and a page at any other name is refused, another alias of the same site
  included, as today. Groups belong to the site, so pages at `abc.` and `def.` share them. A site
  that serves many tenants puts the name in its group names and checks it in `[RealtimeJoin]`;
  the README says so.
* **HTTPS** (`Tls/CertificateManager.cs`, `Tls/TlsMiddleware.cs`):
  * `Desired()` adds each exact alias, with the site's certificate source. **Uploaded** is one
    certificate for the site's domain, so its aliases get automatic instead. Each exact alias
    gets a certificate of its own, stored per host as today.
  * Wildcard aliases get no certificate per name. A name that reaches a site through one is
    served the shared certificate that covers it, unless the site's source is **None**; without
    one, it gets the stand-in, as an unknown name does today. `Select` and `Serves` both do this,
    so HTTP is sent to HTTPS at those names too.
  * `TlsMiddleware` finds the site with `Resolve`, so the site's redirect and HSTS settings apply
    at every name.
  * The site's HTTPS card lists each alias with its certificate. A wildcard that no shared
    certificate covers is shown as a problem, with **Get a certificate**.
* **DNS** (`Services/Dns/DnsService.cs`). Reconciling walks names instead of sites: each site's
  domain and its aliases. A wildcard alias is a `*.app` record, which Cloudflare accepts.
  `SiteDnsRecords` is already one entry per record name, so it needs no new shape. An alias's
  record is proxied when the site's own is, and **Excluded** works per name. A DNS wildcard only
  answers names that have no records of their own, which matches the order above. Cloudflare's
  free certificate covers one level (`*.example.com`), so a proxied `*.app.example.com` needs
  Advanced Certificate Manager, or **DNS only** with this server's certificate. The README already
  says this for Railway and the tunnel.
* **The function editor's Run** (`FunctionTestService`) finds the site with `Resolve`, so a test
  with host `abc.app.example.com` runs as the site, as a visitor's request would.

With HTTPS ended in front of the server (Caddy, Railway, a Cloudflare Tunnel), only the lookup
matters. The proxy in front must send the names on: a Caddy site block for `*.app.example.com`
(with its own DNS challenge), a Railway wildcard domain, or the tunnel's wildcard route.

## API, tools and UI

| Method and path | Who | What |
| --------------- | --- | ---- |
| `GET /api/v1/sites/{domain}` | members | Gains `aliases` |
| `PUT /api/v1/sites/{domain}/aliases` | administrators | Body `{ "aliases": [...] }`. Replaces the list; an empty list removes them all. `400` with every error at once |

`{domain}` is always the site's domain. An alias in an API path is a `404` that names the site
it belongs to, so a script never acts on a site it did not name.

* **Tools:** `get_site` returns `aliases`; `set_site_aliases(domain, aliases)` (a change). The
  instructions get a recipe: "one app at every subdomain".
* **UI:** an **Other domains** card on **Settings**, above **Rename**: a list with **Add** and
  **Remove**, and a hint that every name gets the whole site and that a wildcard covers one level.
  The overview lists the other domains under the site's domain, and the sites list shows a count.
  **DNS** and **HTTPS** in the menu list aliases by name, with the site each belongs to.
* **Audit:** `site.aliases`, with the list before and after.

## Tests

* lookup: an exact name over a wildcard, one level only, the bare name not covered, management
  hosts first, case and a trailing dot;
* rules: each refusal above, the management lock-out included; a member cannot set aliases; a
  member cannot create a site under another site's wildcard, and an administrator can;
* rename to one of the site's own aliases; rename and delete take the aliases with them;
* a request at an alias gets the site's files, SPA fallback, functions, proxy routes and passcode,
  and the passcode is asked for again at another name;
* realtime from a page at an alias is accepted, and from another alias of the same site refused;
* HTTPS: an exact alias gets its own certificate; a name under a wildcard alias gets the covering
  shared certificate, or the stand-in without one, and is sent to HTTPS; **None** stays plain HTTP;
* DNS: alias records made, moved by a rename, and deleted with the alias and with the site,
  against the fake Cloudflare API the DNS tests use.

## Not in this design

* **Sending an alias on to the site's domain**, such as a 301 from `www.example.com` to
  `example.com`. Aliases serve. A per-alias **Redirect** switch is small and can come later without
  changing the model. Today a second site with the rule `/* https://example.com/:splat 301` does it.
  Search engines see the same pages at several names; `<link rel="canonical">` in the pages
  handles that.
* **A certificate per name under a wildcard, got on the first visit** (Caddy's on-demand TLS).
  Every name a visitor tried under the wildcard would make the server ask Let's Encrypt, which
  allows 50 certificates per registered domain a week. One shared wildcard certificate covers
  them all.
* **Different settings per name**, such as variables, routes or a passcode for `abc.` only. That
  is a separate site. For data per tenant, the app or its API reads the name.
* **Wildcards in the middle, or over several levels** (`abc.*.example.com`, `**.example.com`).

## Decisions

Confirmed on 2026-10-23: the user went with all four recommendations.

1. **Aliases serve, with no redirect switch yet.** *Recommended*, for the case above. The switch
   can be added later without changing the model.
2. **A wildcard covers one level**, as a certificate does. *Recommended.*
3. **Administrators only**, and a site under another site's wildcard needs an administrator.
   *Recommended.*
4. **Exact aliases follow the site's certificate source; wildcard aliases need a shared
   certificate**, got with one click from the card. *Recommended.* Asking for the shared
   certificate by itself when a wildcard alias is saved is possible with Cloudflare connected,
   but it would add a shared certificate nobody asked for.

## Order of work

1. `SiteRecord.Aliases`, the rules, the `SiteStore` indexes and `Resolve`, the middleware's
   lookup, the create and rename checks, and their tests. This alone makes the app above work
   behind a proxy that ends HTTPS (the tunnel, Railway, Caddy).
2. Realtime's check.
3. API, tools, the **Other domains** card, the audit entry, and the README: a "One app at every
   subdomain" section, the glossary (a site is everything served at its domain and its other
   domains), and the API table.
4. HTTPS: exact aliases in `Desired`, the wildcard step in `Select` and `Serves`, the HTTPS card.
5. DNS: names instead of sites when reconciling.

---

# Decisions

Confirmed by going ahead with phase 1 (2026-10-15); items 1–4 and 6 are built as described.

1. **Proxy routes run after functions**, so functions can override a path. The alternative is
   running them before middleware, as `ApiProxy.cs` does, but then middleware can't protect them.
   *Recommended: after functions.*
2. **Only administrators change routes**, and members can view them. *Recommended.* The other
   option is to let members target an administrator-approved list of hosts. That can be added
   later without changing the model.
3. **Loopback and link-local blocked by default**, private ranges allowed. *Recommended.*
4. **Phase 2 replaces `ApiProxy.cs`**, and the `API_UPSTREAM`/`API_PROXY` variables are not
   carried over. *Recommended*, for the reason given in phase 2.
5. **Phase 3: our own ACME client**, not a library. *Recommended*, subject to checking how the
   libraries are maintained when the work starts.
6. **Verified while building phase 1:** Kestrel does **not** take HTTP/2 without TLS (h2c) on a
   port that also speaks HTTP/1.1. It answers `HTTP_1_1_REQUIRED`. So gRPC through Caddy needs a
   second, HTTP/2-only listener until phase 3 puts TLS in the server and ALPN does the
   negotiation. That listener came later as `SiteHosting:H2cPort` (see "0.9.0: gRPC-Web and h2c").

---

# Progress

## Phase 1: built

Built on 2026-10-15 and left uncommitted for review. `dotnet build` is clean, and
`dotnet test tests/StaticSiteHost.Tests` passes: 317 tests, 68 of them new for proxy routes. Also
checked by hand in the running app: create an empty site, add a route through the page's form,
traffic forwarded, the Test button, validation messages, and the Proxy card's layout.

| Piece | Where |
| ----- | ----- |
| Route model, `SiteRecord.Proxy` | `Models/ProxyRoute.cs`, `Models/SiteRecord.cs` |
| Validation, target reading, header text format | `Serving/ProxyRouteRules.cs` |
| Runtime: route table, matching, WebSocket checks, errors | `Serving/SiteProxy.cs` |
| Per-route request/response changes | `Serving/ProxyTransformer.cs` |
| Shared clients, blocked-network check at connect time | `Serving/ProxyInvokers.cs` |
| No compression on streaming routes | `Serving/SiteResponseCompressionProvider.cs` |
| Save/remove/test, audit | `Services/SiteProxyService.cs` |
| Empty sites | `ZipDeploymentService.CreateAsync`, **Sites → New site**, `POST /api/v1/sites/{domain}` |
| UI | `Pages/Sites/Details.cshtml` (Proxy card), `Pages/Sites/_ProxyRoute.cshtml` |
| API | `GET/PUT/DELETE /api/v1/sites/{domain}/proxy`, `POST …/proxy/test` |
| Tests | `ProxyRouteRulesTests.cs` (rules, rewriting), `ProxyForwardingTests.cs` (real loopback servers: paths, bodies, cookies, redirects, header rules, timeouts, 502s, SSE, WebSockets, h2c, blocked networks) |

### Where the build differs from the design above

* **No route ids.** A route is identified by its path, which is unique per site. Editing a route
  posts the path it was saved under, so a path can be changed in the same save.
* **No cache eviction.** Compiled routes are cached in a `ConditionalWeakTable` keyed by the
  `List<ProxyRoute>` itself. Every save replaces the list, so a stale table can't be found and is
  collected with the old list. Renames and deletes need no extra code.
* **Redirect rewriting covers absolute URLs only.** A `Location` with a path and no host is left
  alone, as nginx's `proxy_redirect default` does. Rewriting bare paths would double the prefix for
  backends that already honour `X-Forwarded-Prefix`. This is documented in the README.
* **"Files first"** means what a redirect rule's `!` means: the release has a real file at the path
  (`SitePathResolver.Exists`), not that the SPA fallback would answer.
* **Members can't see request header values**, in the UI or the API: a value may be a key typed in
  rather than read from `${env:…}`.
* **Streaming turns compression off with a custom `ResponseCompressionProvider`.** The framework's
  `IHttpsCompressionFeature` only applies to HTTPS requests, and behind a TLS proxy requests
  arrive here as plain HTTP.
* **`X-Forwarded-For` is replaced, not appended.** An appended chain would carry whatever the
  visitor claimed. The value comes from `HttpContext.Connection`, which the forwarded headers
  middleware has already corrected when `TrustForwardedHeaders` is on.
* **`X-Content-Type-Options: nosniff`** is set on proxied answers, as on every file.
* **Loopback: only the server's own ports are refused** (changed after the first build, at the
  user's request). The design blocked all of loopback. Now `ProxyInvokers` refuses a port the
  server listens on (read from Kestrel's `IServerAddressesFeature`) at any address of this
  machine: loopback, `0.0.0.0/8`, `::`, or one of its interface addresses (so a container's own
  Docker address counts too). Every other port on this machine is allowed. `ProxyBlockedNetworks`
  defaults to link-local only, in development and production alike.
* **Loops are stopped with a hop count.** A route whose target comes back through a public name
  or a published port (`host.docker.internal:80` mapped to the server) gets past any address
  check. Each forward sets `X-Site-Host-Hops` to the count it arrived with plus one, and a request
  arriving with 5 is refused with `508 Loop Detected`.

### Small follow-ups worth doing

* ~~**Functions on a site with no release.**~~ Done after phase 1c. A site with no release that is
  given functions gets an empty release (source `functions`, no files) to carry them
  (`ZipDeploymentService.SetFunctionsAsync`), so a proxy-only site can have a sign-in middleware
  in front of its routes. A later zip deploy carries the functions forward as usual.
* ~~An HTTP/2-only listener option~~, so gRPC can pass through a TLS proxy in front. Done as
  `SiteHosting:H2cPort` (see "0.9.0: gRPC-Web and h2c").
* Validation messages number routes by their position in the sorted list ("Route 2 (/x)"). On
  the page's single-route forms the number adds nothing and could be dropped.

## Phase 1b: built

Built on 2026-10-15, uncommitted, right after phase 1. `dotnet test` passes 329 tests (10 new in
`AutomationTests.cs`). The image builds (`docker build --target runtime`). Checked by hand against
the running app:
* a fresh server with `Bootstrap__ApiKey` answered `GET /me` as the administrator;
* a user was created over the API, with an invite link;
* the audit log read back the changes;
* over MCP with curl: `initialize`, `tools/list` (33 tools), `create_site`, two `set_proxy_route`
  calls (`/` and `/api`), `test_proxy_route`, `read_docs`, `deploy_files`. Traffic flowed through
  both routes, and the deployed page was served;
* `/mcp` without a key answered 401.

| Piece | Where |
| ----- | ----- |
| Key from configuration | `BootstrapOptions.ApiKey`, `ApiKeyStore.EnsureConfiguredAsync`, `ApiKeyRecord.FromConfiguration`; `ADMIN_API_KEY` in `.env` |
| Keys, AI providers, users, audit over the API | `Endpoints/AdminEndpoints.cs` |
| Functions check and test over the API | `Endpoints/FunctionEndpoints.cs`, `Services/FunctionTestService.cs` (shared with the editor) |
| Rules moved out of pages into services | `Services/UserAdminService.cs` (Users page), `Services/Ai/AiProviderTest.cs` (AI page), `ZipDeploymentService.DeleteAsync` (site page and API) |
| Tools, defined once | `Tools/SiteHostTools.cs` (`AIFunction`s built with `AIFunctionFactory`), `Tools/Manual.cs` (the README, embedded) |
| MCP endpoint | `Program.cs`: `AddMcpServer().WithHttpTransport(Stateless).WithTools(…)`, `MapMcp("/mcp").RequireAuthorization(ApiUser)` |

Notes:
* **Dependency:** `ModelContextProtocol.AspNetCore` 2.2.0, which brings
  `Microsoft.Extensions.AI.Abstractions`.
* **Docker:** the image now copies `README.md` (it was in `.dockerignore`), because the server
  embeds it as the manual.
* **Failing tools:** a tool that fails returns `{ "error" }` in a normal result, not an MCP error
  result, so the same tool reads the same way to the 1c assistant. If agents turn out to need the
  `isError` flag, set it in an MCP-only filter.
* **Not tools yet:** realtime publish, AI provider update and delete, user role, disable and
  delete, and removing AI from a site. They are all in the API. Add them if an agent needs them.

## Phase 1c: built

Built on 2026-10-15, uncommitted. `dotnet test` passes 340 tests (11 new in `AssistantTests.cs`:
the loop, confirmation, failures, the round cap, conversation ownership, the real tools passing
the server's request checks, prompt caching, step summaries).

**Tested with real models on 2026-10-15** (see "Real-model results" below). Before that, no
provider key was available, and the whole path was checked as follows. The whole path
was checked against the running app with a fake OpenAI-compatible provider that streams real
tool calls, signed in with a cookie as a browser is:
* three tool calls in one round, streamed text;
* the site created, and traffic flowing through both routes;
* a delete stopping for Run, then running on Run;
* a request without the antiforgery token refused;
* the panel, screenshotted in headless Chrome, both with steps and with the confirmation card.

The server's own time for a two-call exchange was about 0.14 s, so what a user waits for is the
model. **Before relying on it, try it with the provider and model you mean to use.**

| Piece | Where |
| ----- | ----- |
| Loop, conversations, confirmation | `Services/Assistant/AssistantService.cs` |
| Settings (provider, model, members) | `Services/Assistant/AssistantSettings.cs`, **AI → Assistant**, `GET/PUT /api/v1/assistant` |
| Endpoint (NDJSON stream, cookie + antiforgery) | `Endpoints/AssistantEndpoints.cs`, which also builds the per-call context |
| Panel | `Pages/Shared/_Layout.cshtml`, `wwwroot/js/assistant.js`, `site.css` |
| Prompt caching | `AiCall.CachePrefix`, `AnthropicChat` (`cache_control` on the last tool and the fixed system block); site chats unchanged |

**Speed work that also helps MCP:**
* the instructions were rewritten as recipes plus "work fast" rules;
* the descriptions now carry the rule formats, the functions guide and the page APIs, so
  `read_docs` should be rare;
* `set_proxy_route` checks the target itself, with a 3 s cap;
* the functions guide is in `deploy_functions` only. The tool definitions are about 5,200 tokens,
  cached after the first call.

**After the first build:**
* **The page updates in place.** "Refresh this page" (a full reload) was replaced. The panel
  fetches the page again and swaps in its `<main>`, inside `document.startViewTransition` where
  the browser has it, keeping scroll and open `<details>`. It does this by itself after a change,
  unless the page has unsaved input, or is the function editor (`data-no-swap`); then it offers
  "Update it anyway". `app.js` binds its element handlers through `window.siteHost.bind(root)`,
  so swapped content works. `@view-transition { navigation: auto; }` cross-fades ordinary page
  navigations too, with the top bar and panel named so they stay still. All of it is off under
  `prefers-reduced-motion`.
* **Fixed: repeated tool-call ids.** A model that numbers its calls from the start each turn
  (`call_0`, as some OpenAI-compatible servers do) broke the second message: the server's request
  check refused the repeated id, and the stream ended as "connection lost". Ids are now made
  unique per conversation (`WithUniqueIds`). Any unexpected fault now reaches the page as an
  error line. The panel matches steps within the current reply. The tests' scripted model now
  runs the real request check.

**Real-model results (2026-10-15).** Six scenarios with `claude-haiku-5-5`, `gpt-5.4-mini` and
`gpt-5.4-nano`, run through `/assistant/chat` as the panel does. Each end state was checked
through the API: routes and traffic, passcode, header rules, a working `/api/time` function, a
delete only after Run, and the bad-target case. All three models got every scenario right.

Time to the end of the answer, final run:

| Scenario | Haiku 5.5 | GPT-5.4 mini | GPT-5.4 nano |
| -------- | --------- | ------------ | ------------ |
| Site in front of two servers ("/" and "/api", WebSockets) | 3.5 s, 2 calls | 2.6 s, 2 calls | 2.7 s, 2 calls |
| "This site": passcode | 1.6 s | 1.8 s | 2.1 s |
| "This site": cache /assets, X-Frame-Options | 2.2 s | 2.0 s | 2.8 s |
| New site with a page and a function | 8.1 s | 6.1 s | 11.6 s (one compile retry) |
| Delete: ask, then run | 1.1 s + 0.9 s | 0.8 s + 0.7 s | 1.0 s + 1.0 s |
| `ftp://` target given | asked first | refused, said why | used http:// and said so |

Prompt caching, checked with a logging pass-through to Anthropic: each call after the first read
8,237 tokens from the cache and sent 350 to 1,150 new ones.

What the runs changed:
* **Defaulted the base URL.** A provider with no `baseUrl` gets OpenAI's or Anthropic's own. The
  `add_ai_provider` description already said so, wrongly, before this.
* **Fewer calls for a site setup.** `set_proxy_route` creates the site when it does not exist,
  and the instructions say calls in one turn run in order. The GPT models had spent a call on
  `create_site` alone; a site setup went from 3 calls to 2.
* **No lookup before changing rules.** The context now includes the page's site's own header
  rules and redirects (or "none"); Haiku had called `get_rules` first.
* **No compile error for the package line.** A function file that uses `StaticSiteHost.Functions`
  gets the abstractions reference without the `#:package` line
  (`FunctionProjectGenerator.Merge`). Nano had hit CS0246 on it; that run went from 16 s to 9 s.

**Possible next steps:**
* Keep conversations across restarts, if that proves wanted.
* Let a member's assistant be capped per day.

## Phase 2: built

Built on 2026-10-15, after 0.5.0. Server-wide routes, as designed above, with these decisions:

* **Model.** `ServerProxyRoute` is a `ProxyRoute` with an `Id` and a `Sites` pattern, kept in
  `config/proxy-routes.json` (`SiteProxyService`). Each site has `DisabledServerRoutes`, a list
  of paths.
* **Merging** (`Serving/ProxySites.cs`, `Effective`). A site's own enabled routes come first. Then
  each enabled server-wide route that matches the site, at a path none of its own enabled routes
  has, and not turned off for it. Between server-wide routes at the same path, the closest
  pattern wins (a domain, then the longest `*.` suffix, then `*`). `SiteProxy` caches each site's
  table against the three lists it came from, and compiles it again when any of them is replaced.
* **Identity.** Server-wide routes are known by id, not path, since two may share a path for
  different sites. Validation refuses two at the same path with the same Sites.
* **Cookies.** A parent cookie domain must cover every pattern but `*`. Under `*`, sites it does
  not cover get host-only cookies, as after a rename.
* **UI.** **Admin → Proxy** reuses the site route form, with the Sites field added, and lists the
  sites each route matches with its state on each. A site's Proxy card lists the server-wide
  routes that match it, with **Turn off here**.
* **API.** `GET/PUT/DELETE /api/v1/proxy`, `POST /api/v1/proxy/test`, and
  `PUT /api/v1/sites/{domain}/proxy/server-routes`. `GET …/sites/{domain}/proxy` gains
  `serverRoutes`.
* **Tools.** `get_server_proxy_routes`, `set_server_proxy_route` (which checks its target, like
  `set_proxy_route`), `remove_server_proxy_route` (destructive), and `set_server_route_for_site`.
  The assistant's context lists the server-wide routes, and how each applies to the page's site.
* **Tests.** `ServerProxyRouteTests.cs` covers patterns, specificity, merging, states and
  validation. `ProxyForwardingTests` forwards through a server-wide route, then through a site
  override, then with the route turned off.

**Found while testing phase 2 with a real model (Haiku 5.5).** Asked to "create an API at
/api/weather/{zip} that returns the current weather as JSON" on a site with no files, the
assistant first hit its 2,048-token answer limit: a function's source goes inside the tool call,
and the call was cut off. `AssistantService.MaxAnswerTokens` is now 8,192, and only what is
written is billed. After that it wrote, compiled, tested and deployed a working function. It used
zippopotam.us and Open-Meteo, which need no key, with input checks and 400/404/502 errors, in
about 25 seconds. It kept an `HttpClient` of its own, so the functions guide in the tool
descriptions now says to take `IHttpClientFactory`, and the next run did.

**Retiring `tmp/ApiProxy.cs`:** add a server-wide route with Sites `*.apps.example.com` (or `*`),
path `/api`, the API as target, and cookie mode "a parent domain" if the sites share cookies.
Then remove the global function. While both exist, the function's middleware answers `/api`
first: it runs before any proxy route.

## Alongside phase 2: changing some files, and downloading releases

Also found while testing with a real model: asked to change one heading, the assistant could only
`deploy_files`, which replaces the whole release, so the other files would have been lost. Built
for 0.6.0:

* **Updating.** `ZipDeploymentService.UpdateFilesAsync` copies the live release, writes and
  deletes the named files, and makes the copy a new release, under the same deploy gate and
  limits. `_headers`, `_redirects` and `_variables.json` are replaced when written, removed when
  deleted, and otherwise carried over, as are the functions. `_functions/` is refused. Audit
  action `site.files`.
* **Reading.** `Services/SiteFiles.cs` lists a release, reads one file, and writes the release as
  a zip that deploys back as it was: the configuration files are written out again from the
  release's rules (`VariableFileText.Format` leaves out secret defaults). The zip is built in a
  temporary file first, since a zip writes its directory synchronously as it closes.
* **API.** `GET …/files`, `GET …/files/{path}`, `PATCH …/files`, and
  `GET …/releases/{release}/download`. The UI has **Download** on each release.
* **Tools.** `list_site_files`, `read_site_file` (up to 60,000 characters), `edit_site_file`
  (find and replace, each find exactly once), `write_site_files`, `delete_site_files`
  (destructive). `deploy_files` now says it replaces the whole release, and the assistant's
  instructions say to read and edit to change a site.
* **Measured with Haiku 5.5.** Changing a heading and a colour on a three-file site took 4.7
  seconds and four tool calls, with every other file, rule and variable kept. A weather page on
  a site with no files (a page plus a `/api/weather/{zip}` function) took 28 seconds and two
  model calls.

## 0.6.1: a tool for everything the API does

A check after 0.6.0 found API features with no tool. They were added, each calling the service
its endpoint calls:

* **Functions:** `read_function_files` (the source as deployed, up to 60,000 characters, like
  `read_site_file`), `edit_function_file` (find and replace, then compile and deploy, keeping the
  other files), `remove_function_file` (destructive). Before this, changing a function meant
  rewriting it without seeing it.
* **AI:** `update_ai_provider`, `remove_ai_provider` (destructive; it reports the sites and the
  assistant left without a provider), `remove_site_ai` (destructive), `get_assistant_settings`,
  `set_assistant_settings`.
* **Users:** `invite_user`, `set_user_role`, `disable_user` (destructive), `delete_user`
  (destructive). The service's own rules apply: nobody can demote, disable or delete themselves,
  and the last administrator stays.
* **Realtime:** `get_realtime`, `publish_realtime_event`. The endpoint's checks moved to
  `ApiEndpoints.PublishToSiteAsync`, which both use.
* Users and providers are named by id or name, and roles in any case, since models send
  "member" as often as "Member".

**Measured with Haiku 5.5.** "Change the hello function so it returns JSON with a message and
the current UTC time" took 7.5 seconds: `read_function_files`, `edit_function_file`,
`test_functions`, with the other function file left as it was. Removing a function file and
disabling an account each stopped for confirmation within two seconds. Publishing an event
reached a page open in Chrome. The tool catalog is now 57 tools, about 10,000 tokens with the
instructions, and still cached.

## After 0.6.1: for running on Railway

Four items for deployments behind someone else's edge, built before phase 3:

* **Conversations survive restarts.** `AssistantConversationStore` writes each conversation after
  every message to `config/assistant-conversations/<id>.bin`, encrypted with Data Protection
  (tool results can hold file text, invitation links or temporary passwords). After a restart
  one is loaded on first use, a step waiting for Run included. The 2-hour idle limit is
  unchanged; idle files are swept every 10 minutes. A file that cannot be decrypted (a lost key
  ring) is dropped and a new conversation starts. Checked with Haiku: a delete waiting for Run
  across a restart ran on Run, and the model still had the earlier messages.
* **A daily limit for members.** `AssistantSettings.MemberMessagesPerDay` (default 50, 0 for no
  limit), counted per UTC day by `AssistantUsageStore` in `config/assistant-usage.json`, so a
  restart does not reset it. Run and Cancel are not messages. Administrators are counted, never
  limited. The AI page lists today's use with tokens; the API and `get_assistant_settings`
  return it as `usageToday`.
* **Screenshots** of proxy routes, server-wide routes, the assistant and releases, taken by
  `scripts/screenshots.sh` (the assistant only when `ANTHROPIC_API_KEY` is set). Taking them
  showed the releases table overflowing its card since Download was added; it is now four
  columns.
* **Cloudflare Tunnel:** `deploy/cloudflare-tunnel/` (Compose with the server and a pinned
  `cloudflared`, no port published) and a README section. Checked as far as Compose and the
  server go: the server answers by host name on the Compose network, and nothing is published.
  The Cloudflare side follows Cloudflare's documentation; the dashboard has been reported to
  refuse wildcard routes, so the README sets them with the API.

**gRPC, dropped for now.** Railway's edge forwards every request to the service as HTTP/1.1, so
an HTTP/2-only listener here would not bring native gRPC through a Railway domain. gRPC-Web
translation on a route would, and is a possible later item; native gRPC comes with phase 3,
where TLS in the server gives HTTP/2 by ALPN. Both were built after phase 3 (see "0.9.0: gRPC-Web and
h2c").

## Phase 3: built

Built on 2026-10-16. HTTPS in the server, off by default (`SiteHosting:Tls:Enabled`).

* **ACME client: our own** (decision 5). Checked first: Certes came back in September 2026
  after three quiet years, with BouncyCastle as a dependency and three majors in a week;
  LettuceEncrypt has not released since 2024. `Tls/AcmeClient.cs` is RFC 8555 with HTTP-01:
  account, order, authorizations, challenge, finalize with a CSR, download, ES256 JWS with an
  ECDSA P-256 account key from `System.Security.Cryptography`, `badNonce` retried once. No new
  dependency.
* **Listening.** With TLS on, Kestrel listens on `HttpPort` (8080) and `HttpsPort` (8443) itself,
  in place of `ASPNETCORE_URLS`. The HTTPS listener uses `TlsHandshakeCallbackOptions`: each
  handshake gets an `SslStreamCertificateContext` for the SNI name from memory, with the
  intermediates, and ALPN offers h2 and http/1.1. An unknown name gets a stand-in self-signed
  certificate, so it sees a warning rather than a dropped connection.
* **Sources** (`Models/SiteTls.cs`): automatic, letsencrypt, selfsigned, uploaded, shared, none,
  chosen per site (`SiteRecord.Tls`). Automatic is a covering shared certificate, else
  `DefaultSource`, and self-signed for names no public CA issues for (`*.localhost`, `*.test`,
  addresses). Management hosts are always automatic.
* **Storage** (`Tls/CertificateStore.cs`): `config/certs/hosts/<host>/<source>/`, keys encrypted
  with Data Protection, one folder per source so switching back does not reissue (Let's Encrypt
  allows 5 duplicates a week); a Let's Encrypt certificate records the directory it came from,
  so staging → production reissues. `shared/<id>/` and `acme/<directory>/` alongside.
* **Renewal** (`Tls/CertificateManager.cs`, a hosted service): hourly, on every site change
  (`SiteStore.Changed`), and on Renew now. Each pass serves what is on disk first, then issues
  what is missing or due, one at a time. Due is 30 days before expiry or a third of the lifetime
  before, whichever is later: Pebble handed out a 6-day certificate in testing, which a fixed 30
  days would have renewed every hour. Failures back off 1, 2, 4… 24 hours, persisted, so
  restarts do not hammer Let's Encrypt. A settings change applies at once what needs no
  issuing (none, shared, a certificate on disk); only issuing waits for the loop.
* **Per request** (`Tls/TlsMiddleware.cs`), after forwarded headers, the exception handler and
  response compression, and before anything that picks a site: the
  HTTP-01 answer for a token in progress, on any host and port; HTTP → HTTPS with 308 for a host
  that has a certificate, unless its site turned it off, `/healthz` excepted; HSTS
  (`max-age=31536000`) on HTTPS answers for a site that turned it on.
* **UI, API, tools.** **HTTPS** in the top bar (server, every host, shared certificates); an
  HTTPS card on each site; `/api/v1/tls…` and `/api/v1/sites/{domain}/tls…`; `get_tls`,
  `set_site_tls`, `renew_certificate`. No upload tool: the private key would pass through the
  conversation.
* **Container.** `EXPOSE 8080 8443`; Compose publishes `HTTPS_PORT` and passes `TLS_ENABLED`,
  `ACME_EMAIL`, `ACME_DIRECTORY`.

**Checked end to end against Pebble 2.10.1** (with `pebble-challtestsrv` answering every name with
127.0.0.1, and `AcmeCaFile` trusting Pebble's own HTTPS): the management host and a new site got
certificates within seconds, curl verified the chain against Pebble's root over HTTP/2, HTTP
redirected with 308 and `/healthz` did not; a restart served from disk with no new order;
switching to self-signed and back reused the stored certificate; none stopped HTTPS and the
redirect at once; HSTS appeared; an upload with the wrong host, the wrong key or no PEM was
refused and a good one served with its chain; a shared `*.example.net` took over every
automatic host and its removal restored theirs at once; a name pointed at an unreachable address
failed with Pebble's error and a 1-hour wait, and Renew now issued it once the DNS was fixed;
rename issued for the new name and dropped the old; delete dropped it. gRPC (`grpcurl` →
HTTPS:5543 → an HTTP/2-only proxy route → `moul/grpcbin` over h2c) answered. 15 tests (some with several cases) in
`TlsTests.cs`.

**Not done:** DNS-01 (wildcards from Let's Encrypt) is phase 4; HTTP/3; OCSP stapling; ACME
renewal information (ARI).

## 0.9.0: gRPC-Web and h2c

Two ways for gRPC to reach a route when the server does not end HTTPS itself.

**gRPC-Web translation**, a route setting (`ProxyRoute.GrpcWeb`, "Translate gRPC-Web", off by
default). Railway's edge forwards HTTP/1.1 only and browsers cannot speak gRPC; gRPC-Web works
over either. `Serving/GrpcWeb.cs` does what Envoy's gRPC-Web filter does, on YARP's direct
forwarder, with no new dependency:

* `SiteProxy.ForwardAsync` sees `application/grpc-web[-text][+fmt]` on a translating route. For
  `-text` it wraps the request body in a base64 decoder (pieces padded on their own, line breaks
  and spaces allowed, as the protocol says) and the response body in an encoder that is only
  switched on once the target answers with gRPC. The request goes with `GrpcConfig`: HTTP/2,
  `RequestVersionExact`, so h2c to an `http://` target.
* `ProxyTransformer` sends `application/grpc[+fmt]` and `TE: trailers`. A response in
  `application/grpc…` goes back as `application/grpc-web…` without `Content-Length`, with
  `X-Accel-Buffering: no`, and `TransformResponseTrailersAsync` writes the trailers into the body
  as the `0x80` frame instead of as HTTP trailers. A trailers-only answer has its status in the
  headers already and gets no frame. Anything else from the target passes through untouched.
* A translating route cannot be set to HTTP/1.1 only.

Native gRPC also uses `GrpcConfig` now, so a route on **Auto** carries it to an `http://` target;
before, only **HTTP/2 only** did. The route's Test uses HTTP/2 on a translating route.

**The h2c port**, `SiteHosting:H2cPort` (0, off, by default). Kestrel on a plain port with
`Http1AndHttp2` logs that HTTP/2 needs TLS and speaks HTTP/1.1 only (checked again on .NET 10), so
h2c needs a port of its own. With HTTPS in the server it is one more `ListenAnyIP`; otherwise the
port is added to the URLs the server already has (`urls`, else `http_ports`, else Kestrel's
default), since binding a port in code would replace them, and `ConfigureEndpointDefaults` makes
that one port HTTP/2 only. `ProxyInvokers` reads it from the server's addresses with the others,
so a route cannot point at it.

**Found while testing:** a gRPC error sent after the headers has no body, and Kestrel answered it
with `Content-Length: 0` before the trailers. nginx's `grpc_pass` takes that for the end and drops
the trailers, so the client saw no `grpc-status`; Caddy and direct clients did not mind. When the
target's answer has trailers and the response has not started, the transformer now starts it
before passing them on, so no length is sent.

**Checked:** a .NET gRPC server on h2c behind a route. Clients: .NET's `GrpcWebHandler` (binary and
text, HTTP/1.1 and HTTP/2), Connect's gRPC-Web transport in Chromium (from a page the site served),
and `Grpc.Net.Client` for native gRPC. Paths: straight in over HTTP/1.1; into the h2c port; with
HTTPS in the server; through Caddy (`h2c://`) and nginx (`grpc_pass`), each sending gRPC to the
h2c port and the rest to the usual one, as the README shows. Each run covered unary calls, header
and trailer metadata, a 3 MB message both ways, a server stream arriving 400 ms apart as sent, an
error after the headers and a trailers-only error. Also: a route without translation passes
gRPC-Web on untouched; an unreachable target answers 502; a route to the h2c port is refused; bad
base64 is a 400. Tests: `GrpcWebTests.cs`, and four in `ProxyForwardingTests.cs` against a second
front that speaks h2c.

**After review:** the h2c port refuses to start beside `Kestrel:Endpoints` (which would replace it
silently) or on a port the server already uses, and keeps `https_ports` when it rebuilds the
addresses. The trailers frame is Latin-1, as HttpClient read the bytes, so a UTF-8 `grpc-message`
passes intact, and a value with a line break is left out rather than starting a trailer of its
own. A visitor leaving just as the trailers go out no longer surfaces as an unhandled error.
Known: YARP lifts the request body limit for gRPC over HTTP/2 (it did with HTTPS in the server
already), and a `-text` body counts as its base64; the README says so.

**Not done:** CORS for gRPC-Web from other origins (a site's own pages need none); gRPC-Web through
a Cloudflare Tunnel, and on Railway itself, not tried.

## Phase 4: built (Cloudflare)

* **Token:** one Cloudflare API token under **DNS** (Zone: Read, DNS: Edit; SSL and Certificates:
  Edit for Origin certificates), checked with `/user/tokens/verify` and the zone list, stored
  encrypted with Data Protection in `config/dns.json` (`Services/Dns/DnsService.cs`).
* **Records follow sites** by reconciling, not by hooks: on every `SiteStore.Changed`, after each
  settings change, and hourly, each site that should have a record gets an `A`/`AAAA` (or a
  `CNAME`) at the address set, proxied as chosen, and the records of sites that are gone are
  deleted. A rename carries the site's choices to the new name. Only records whose ids this
  server keeps are touched; a name with a record made elsewhere is reported as a conflict.
* **DNS-01:** `AcmeClient` takes an `IAcmeChallengeSolver` (`http-01` or `dns-01`). Every name's
  proof is put in place first, then all are answered; the Cloudflare solver writes TXT records at
  `_acme-challenge.<name>` and waits (up to two minutes) until 1.1.1.1 sees them. Automatic
  Let's Encrypt certificates use DNS-01 whenever a zone the token can edit holds the name.
* **Shared certificates got here:** `*.apps.example.com` (with its bare name) from Let's Encrypt
  by DNS-01, or as a Cloudflare Origin certificate, renewed by the certificate loop like a host's.
* **Cloudflare Origin certificates** per site (`cloudflare-origin` source) or shared: ECDSA key
  made here, 15-year certificate from `POST /certificates`, revoked when replaced or when its site
  or shared entry goes.
* **Other providers later:** the provider is behind `ICloudflareApi`; a second one means a second
  API client and solver.

## Phase 5: built (other domains)

Built on 2026-10-23, as designed above, with the four recommended decisions. `dotnet test` passes
657 tests, 33 of them new in `SiteAliasTests.cs`: lookup, every rule, restarts, renames and
deletes, certificates for exact and wildcard names, the HTTPS redirect at an alias, DNS records
following the site, the realtime checks, the shared passcode cookie, proxy cookie parents, and the
tools and assistant context; one in `RealtimeLimitsTests.cs` closes a site's connections at one
name only.

| Piece | Where |
| ----- | ----- |
| Model | `SiteRecord.Aliases` |
| Rules, one-level matching, taking a name | `Services/SiteAliases.cs` (`Normalize`, `Check`, `Covers`, `Taken`) |
| Lookup | `SiteStore.Resolve` and `AliasOwner`, over an index rebuilt under a lock and swapped after every load, save, rename and delete |
| Saving, and the create, deploy and rename checks | `ZipDeploymentService.SetAliasesAsync`, `CreateAsync`, `DeployAsync`, `RenameAsync`, all under the deploy gate |
| Requests | `SiteHostingMiddleware` (`Resolve`, and the realtime check against the name asked for), `TlsMiddleware` |
| HTTPS | `CertificateManager`: exact aliases in `Desired` with their site's source (uploaded becomes automatic), `UnderWildcard` in `Select` and `Serves`, `SharedCovering`, `HostCertificateStatus.AliasOf` |
| DNS | `DnsService.ReconcileAsync` walks names: each site's domain and its aliases |
| API, tools | `PUT /api/v1/sites/{domain}/aliases`, `aliases` on every site description, `aliases` in `GET …/tls`; `set_site_aliases`, and a recipe line in the instructions |
| UI | **Settings → Other domains** card; the list under the site's name; the HTTPS card's **Other domains** list; a count on the sites list; alias rows on **HTTPS** and **DNS** |

### Where the build differs from the design above

* **The card is a text box**, one domain per line (commas and spaces separate them too), not a list
  with Add and Remove. It replaces the whole list, as the API does, and shows what was typed again
  with every error when a save is refused.
* **DNS: other domains follow their site entirely.** The design had **Excluded** per name. Built,
  an alias has records while its site has them (its own entry, or records for every site and the
  site not excluded), proxied as the site's are, and loses them when the site's are removed by
  hand. There is nothing to set per alias, and the DNS page shows alias rows with no actions.
* **Visitor screens show the name asked for.** The passcode page and the "nothing deployed yet"
  page name the host the visitor typed (`SiteHostingMiddleware.HostOf`), not the site's domain.
* **Every API address that names a site** says, for one of its other domains, which site that is
  (`ApiEndpoints.NoSite`), in the site, TLS, DNS, branding and functions endpoints and in the tools.
* **Get a certificate from the HTTPS card.** A wildcard no shared certificate covers links to
  `/admin/https?request=<wildcard>#request`, which opens the request form with the name filled in.
* **The function editor's Run** keeps the host typed for a site's functions when it is one of that
  site's names, and uses the domain otherwise.
* **One passcode unlock per wildcard** (asked for after the build; the design had one per name).
  Entered at a name under one of the site's wildcards, or at the domain it sits on, the passcode
  sets a second cookie, `ssh.pass.{site domain}`, on that domain (the widest when several
  wildcards cover the name), beside the host-only `ssh.pass`, which stays for a browser that
  refuses the shared one (a wildcard on a public suffix, such as `*.co.uk`). Both carry the same
  value, good for this site alone, so another site under the domain is not unlocked and its own
  `ssh.pass` is never shadowed. The shared cookie does reach every name under the domain, other
  sites and servers elsewhere included; proxy routes strip `ssh.*` cookies, and only administrators
  write functions. `SitePasscodeGate.SharedDomain`, `SharedCookieName`.
* **A proxy route's parent cookie domain may cover an other domain.** The check on save took the
  site's domain only; it now takes a parent of the domain or of any of its other domains (for a
  wildcard, the domain it sits on). At a name the parent does not cover, the route's cookies stay
  host-only, as before. `ProxyRouteRules.Validate(domain, routes, aliases)`.
* **Tools never take a name by creating a site.** `set_proxy_route` and `deploy_files` create a
  missing site, so a model asked about `abc.app.example.com` would have made a site there and taken
  the name from the wildcard's site. At another site's other domain they are refused, naming that
  site; `create_site` still makes one there on purpose. The assistant's context lists each site's
  other domains, `get_dns` carries `aliasOf` (`SiteDnsStatus.AliasOf`), and a wildcard given where
  a site is asked for names its site (`ApiEndpoints.NoSite`).

**Checked by hand** against the running app, with its data in a scratch folder: the sample site
deployed to `app.localhost` with `*.app.localhost` and `www.example.test`; the domain, the exact
alias and a wildcard name served the site, SPA fallback included, while a name two levels down and
an unknown name got the no-site page; a bad list refused with every reason; `GET` at an alias
answered `404` naming the site; creating a site at the exact alias refused, and under the wildcard
allowed for an administrator; the passcode page at a wildcard name showed that name, and the
unlock worked there; realtime negotiation accepted from the page's own name and refused from
another alias and from the domain; renaming the site to its own alias took that name off the list.
The shared passcode cookie in Chromium over HTTPS: unlocking at `abc.app.localhost` set
`ssh.pass` on that host and `ssh.pass.{domain}` on `.app.localhost` (Secure, HttpOnly, Lax), and
`def` and `xyz.app.localhost` then opened without the form, while `admin.app.localhost`, a site of
its own with a passcode of its own, still asked. Firefox and WebKit were checked later (below). Over MCP
(`tools/list` and calls with an API key) and in the management assistant with Claude Haiku: asked
to add `*.tenants.localhost` and an `/api` route sharing cookies on `app.localhost`, it called
`set_site_aliases` with the old list kept and `set_proxy_route` with `cookieMode: parent`; asked for
a route on `abc.app.localhost`, it put it on the site that answers there and said so.
With HTTPS in the server: the exact alias got its own self-signed certificate, a wildcard name the
stand-in until a shared `*.app.localhost` was uploaded and then that one, and HTTP at those names
went to HTTPS; the site's HTTPS card, the HTTPS page, the sites list and the Settings card were
looked at in Chromium at 1280 and 390 pixels wide. The screenshots found two things, fixed: the
card's text box and the card shared an id, and the alias list sat against the form below it.

**Checked against Cloudflare** on 2026-10-24, with a real zone and token and a server with no
other sites. A site with a wildcard and an exact other domain got one A record at each of the
three names, each with the server's comment, and the two other domains showed `aliasOf`.
`name.exact` matches a `*.` name literally: two refreshes found the records already made, made no
more, and reported no conflict. Removing the wildcard deleted its record alone; deleting the site
deleted the rest. Google's resolver and 1.1.1.1, asked over HTTPS, answered the site's name, two
names under the wildcard and the exact one within seconds. A DNS wildcard also matches names
further down (`deep.abc.…` resolved), which the server answers as any name it has no site for; the
README says so. The records pointed at 192.0.2.10, and plain DNS from the WSL machine used drops
answers with reserved addresses, so the lookups went over HTTPS.

**Checked before release** on 2026-10-26, after the commit:

* **Upgrade from v0.10.0.** The v0.10.0 tag, built on its own, filled an empty data folder with
  HTTPS in the server: a site with a passcode, two proxy routes (one sharing cookies on a parent),
  header rules, redirects, a plain and a secret variable, its own branding, a self-signed
  certificate with HSTS; a site with an uploaded certificate and functions; two sites on an
  uploaded shared wildcard certificate, with a server-wide route that one of them turns off; the
  server's branding, a member, an API key and an AI provider. A visitor unlocked the private site.
  This build then started on the same folder. Every API answer was the same apart from the new
  `aliases` and `aliasOf` fields and timestamps; every host served the same certificate; the old
  visitor cookie still opened the private site; the routes, rules, function, server-wide route and
  branding answered as before; the API key made under v0.10.0 worked; and every management page
  (each site's seven tabs included) loaded with no page or console error. Other domains added to
  the upgraded private site then worked: its records kept, the exact one got its own self-signed
  certificate and redirect to HTTPS, a wildcard name the stand-in, and one unlock under the
  wildcard opened the other names.
* **Back to v0.10.0** on that folder: it loads every site and serves each at its domain. It
  ignores other domains (they get the no-site page), and drops them from a site's `site.json` the
  next time it saves that site, so they have to be set again after upgrading once more. The
  release notes should say so.
* **MCP** (`mcp.py`, on a copy of the test data): every tool answered as before. With the site's
  HTTPS off, `get_tls` gave its wildcard `effective: none` and no problem, and `shared` again when
  set back to automatic. `get_dns` called straight after `set_site_aliases` still listed the old
  names: the DNS status is rebuilt in the background about a second after any change, as for a
  new site. A second later it was right. The exact other domain `www.example.test2` is not a
  reserved name, so automatic asked Let's Encrypt, which refused it; the error is in `lastError`,
  as for a site.
* **The shared passcode cookie in Firefox 144 and WebKit 26**, as in Chromium 143, with
  Playwright's builds and a small CONNECT proxy sending every name to the server (no root needed):
  both `*.app.example.test` and `*.app.localhost`. In all three, unlocking at `abc` set `ssh.pass`
  on that host and `ssh.pass.www.example.test` on the wildcard's domain (Secure, HttpOnly, Lax);
  `def` and a deep link at `xyz` opened without the form; another site under the same domain, the
  exact other domain and the site's own domain still asked.
* **The DNS page with Cloudflare connected**, against the real test zone. v0.10.0 gave a site its
  record; after the upgrade the record was kept as it was (same id), and the site's wildcard and
  exact other domains each got one A record, `ok`, with `aliasOf`. A name that already had a
  record made elsewhere showed a conflict and was left alone. Proxying the site's row proxied its
  other domains' records too, the wildcard's included. Deleting the site deleted its three records
  and left the one made elsewhere. The page, at 1280 and 390 pixels: each other domain's row says
  "other domain of …", links to the site's Other domains card, and has no buttons; no page error,
  and the page is never wider than the screen. At 390 pixels the table scrolls sideways inside its
  card, as it already did (the long "other domain of …" badge widens its first column). Every
  record with the run's label was deleted after, and the token forgotten.

**Not checked:** Safari on a Mac, with mkcert certificates: unlocking at one name under a
wildcard, then opening another without the form. Only the user can do this.

**Reviewed** on 2026-10-23 by Fable 5.1 at xhigh effort, read-only, over the whole uncommitted
change. Three defects, each confirmed in the code and fixed:

* **Realtime at an alias closed as it opened.** `SiteHub.IsStillServed`, the check after a
  connection registers, compared the request's host with the site's domain, so every connection at
  an other domain was aborted after a successful negotiate (reproduced in Chromium: connected at
  the domain, `Disconnected` at two wildcard names). It now checks that the name asked for still
  resolves to the same record, which also catches a removed alias and a name taken by a new site.
  Pages already connected at a name the site stops answering are closed too, and only those: each
  connection keeps the name it was made at, and `RealtimeRegistry.DisconnectAt` closes the ones at a
  removed alias, or at a name under the wildcard that a new site (created, deployed to or renamed
  to) takes. Closing all the site's connections would not do: SignalR tells a page the server
  closed not to reconnect, so its pages at every other name would lose realtime until reloaded
  (seen in Chromium with `DisconnectSite`, then checked again with `DisconnectAt`: the domain's and
  `def`'s pages stayed connected when `abc.app.localhost` got a site of its own).
* **Visitor screens and `/_host/brand/brand.css` at an alias used the server's branding.**
  `VisitorPage` and `BrandingMiddleware` looked the site up with `TryGet`; they use `Resolve` now.
  Checked in Chromium with a site brand name and accent colour at the domain and a wildcard name.
* **An exact alias of a site on a shared certificate got none when that certificate did not
  cover it.** An alias now takes automatic for a shared source as for an uploaded one
  (`CertificateManager.ForAliases`); automatic still prefers a shared certificate that covers it.

Its fourth note, that functions on a sibling site under the same domain can read the shared
passcode cookie, is the trade-off described above: only administrators write functions.

**Reviewed again** the same day, after those fixes, by Fable 5.1 at xhigh effort, read-only. It
found the three fixes right and four more defects, each confirmed in the code and fixed:

* **A management host under a site's wildcard took the site's HTTPS settings.** `TlsMiddleware`
  looked the host up without checking for a management host first, as the other request-path
  lookups do. With the site's redirect turned off, the management app's sign-in page was served
  over plain HTTP (the new test gets a 200 there without the fix), and the site's HSTS reached the
  management host. It now leaves management hosts to their own settings.
* **An exact other domain taken from under another site's wildcard left that site's pages there
  connected.** `SetAliasesAsync` now closes them, as making a site at the name does; checked in
  Chromium: the page at `abc.app.localhost` closed, those at the domain and `def` stayed.
* **`get_tls` and `GET /api/v1/sites/{domain}/tls` said a wildcard of a site with no HTTPS had no
  certificate covering it.** They now give `effective: none` and no problem, as the site's page
  does, since its names are served none and HTTP there is not redirected.
* **The README said every exact other domain asks for the passcode again.** One under a wildcard's
  domain gets the shared cookie and opens with it; the README says so.

**Known:** a redirect switch per alias (decision 1) and certificates per name on first visit
remain out, as designed.
