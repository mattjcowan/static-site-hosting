# Design: proxy routes, certificates and DNS

Status: phase 1 built (2026-10-15, not yet committed); phases 2–4 planned. See
[Progress](#progress) at the end for what was built, where it differs from the design, and
what comes next. User-facing documentation lives in the README's
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

## Where this compares to Nginx Proxy Manager

| Nginx Proxy Manager | Here | Phase |
| ------------------- | ---- | ----- |
| Proxy host | A site with a `/` route | 1 |
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
   second, HTTP/2-only listener (a small addition, not built) until phase 3 puts TLS in the
   server and ALPN does the negotiation.

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
* An HTTP/2-only listener option (for example `SiteHosting:Http2Port`), so gRPC can pass
  through a TLS proxy in front before phase 3.
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

## Phases 3 and 4: planned

As sketched above. Phase 3 should start by choosing the ACME client (decision 5), and should add
the h2c/ALPN listener work, since TLS in Kestrel gives HTTP/2 to browsers and gRPC end to end.
