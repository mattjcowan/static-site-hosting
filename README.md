# Static Site Host

Drop a zip, pick a domain, and it is live. A single ASP.NET Core app that hosts any
number of static sites out of one data volume, with accounts, invitation links, optional
[per-site passcodes](#private-sites) and an API for CI. No database — users, keys and site
metadata are JSON files on the volume.

![Deploying a site: drop a zip, pick a domain, and it is live](docs/screenshots/deploy.png)

```
you ──upload site.zip──▶  deploy.example.com   (management UI + API)
                                │
                                ▼
                          /data/sites/<domain>/releases/<id>/
                                │
visitor ──GET abc.def.com/──────┘   served straight from disk
```

---

## Quick start

```bash
cp .env.example .env      # optional — sensible defaults work as-is
docker compose up --build
```

Open <http://localhost:8080>. On the first run the container prints the generated
administrator password:

```
==========================================================================
 A password was generated for the administrator account 'admin':

     Kx7pQm2vRt9wLc3bZn4d

 It must be changed at first sign-in. Also saved to /data/config/bootstrap-password.txt.
 Set Bootstrap__Password to choose your own and avoid this message.
==========================================================================
```

Sign in as `admin`, change the password when prompted, and deploy something.

To choose the password yourself, set `ADMIN_PASSWORD` in `.env` before the first start.

### Something to upload

`sample-site.zip` at the root of this repo is a small demo site — a home page, a
stylesheet, a script, a `404.html`, and a `blog/` folder with three posts and its own
index. Drop it on the deploy screen, pick a domain, and click through the links: every
page pins a banner to the bottom showing which path you requested and which file was
actually served, which makes the resolution rules below easy to watch.

It also carries a `_headers` file, so every response comes back with `X-Demo-Rules: on` —
open the network tab and you can watch a [header rule](#header-rules) apply — and a
`_redirects` file, so `/posts/hello-world` bounces to `/blog/hello-world` and `/preview`
serves a post without the address changing. See [redirects and rewrites](#redirects-and-rewrites).

The sources live in `samples/demo-site/`. After editing any sample, rebuild the archives
with:

```bash
scripts/build-samples.sh             # both zips
scripts/build-samples.sh blog-site   # or just one
```

`blog-site.zip` is a bigger one: **Night Sky Field Notes**, an astronomy journal that shows
off [functions](#functions). It has a live Moon phase, a constellation of the season, a
dark-adaptation timer and a red-light mode. Its `_functions/` folder adds sign-in, user
management and a Markdown journal, stored in SQLite (through Dapper) in the site's
[data folder](#functions). Deploy it as an administrator, since a `_functions/` folder is
refused otherwise. The first request creates an `admin` account with a generated password,
printed to the server log (`docker compose logs`), and it must be changed at first sign-in.
The sources are in `samples/blog-site/`, and `scripts/build-samples.sh` rebuilds the zip.

---

## Screenshots

<table>
  <tr>
    <td width="50%"><a href="docs/screenshots/sites.png"><img src="docs/screenshots/sites.png" alt="The list of hosted sites"></a><br><sub><b>Sites</b>: every domain on the server, with size and last deploy.</sub></td>
    <td width="50%"><a href="docs/screenshots/functions.png"><img src="docs/screenshots/functions.png" alt="A site's Functions card with its routes"></a><br><sub><b>Functions</b>: each route with the file and line it is written on.</sub></td>
  </tr>
  <tr>
    <td><a href="docs/screenshots/editor.png"><img src="docs/screenshots/editor.png" alt="The function editor marking a compile error"></a><br><sub><b>Editor</b>: <b>Check</b> compiles without going live and marks errors in the code.</sub></td>
    <td><a href="docs/screenshots/editor-test.png"><img src="docs/screenshots/editor-test.png" alt="Testing a function that returns a PNG"></a><br><sub><b>Test</b>: run a request against the checked build; images, PDFs and JSON render inline.</sub></td>
  </tr>
  <tr>
    <td><a href="docs/screenshots/blog-home.png"><img src="docs/screenshots/blog-home.png" alt="Night Sky Field Notes home page"></a><br><sub><b>blog-site.zip</b>: an astronomy journal with a live Moon phase and a starfield.</sub></td>
    <td><a href="docs/screenshots/blog-studio.png"><img src="docs/screenshots/blog-studio.png" alt="The blog's studio editing a post"></a><br><sub>Its studio: sign-in, accounts and Markdown posts, all C# functions and SQLite.</sub></td>
  </tr>
</table>

The screenshots are taken by `scripts/screenshots.sh`, which starts a throwaway instance,
deploys the samples through the UI and captures each screen. Run it again after changing the
UI. It needs Node, and downloads a Chromium the first time; the app itself needs neither.

---

## How hosting works

The **Host header** decides which app answers a request:

| Host                                        | Response                                    |
| ------------------------------------------- | ------------------------------------------- |
| listed in `SiteHosting:ManagementHosts`      | the management UI and API                   |
| a domain with a site published to it         | that site's files                           |
| anything else                                | the management UI when no management hosts are configured, otherwise `404` |

Leaving `ManagementHosts` empty is the easy mode: the management UI answers on any
hostname that has no site. In production, name it explicitly (`MANAGEMENT_HOST=deploy.example.com`)
so an unrecognised domain gets a clean 404 instead of your admin login page.

Point every hosted domain's DNS at this server and publish the container on port 80.

### Request resolution

Sites behave like a standard SPA host. For `abc.def.com/abc/def/gemini`:

1. `/abc/def/gemini` — the exact file, if it exists
2. `/abc/def/gemini.html` — the `.html` sibling
3. if `/abc/def/gemini/` is a directory → `301` to `/abc/def/gemini/`
4. `/abc/def/index.html`, then `/abc/index.html`, then `/index.html` — walking up the tree
5. `/404.html`, served with a `404` status, if the site has one

A trailing slash (`abc.def.com/abc/def/`) starts at step 4, so it resolves to
`/abc/def/index.html` and falls back up the tree from there.

[Redirects and rewrites](#redirects-and-rewrites) are consulted before any of this, so a
rule can answer a request from somewhere else entirely.

**Assets are treated differently on purpose.** A request for a path with a non-HTML
extension (`/assets/app.js`) that the browser did not ask HTML for returns `404` rather
than the index page — serving HTML in place of a missing script breaks module loading
in ways that are hard to debug. Set `SiteHosting:SpaFallbackForAllRequests=true` if you
want the walk-up applied to everything.

HTML is served `no-cache`; other assets get `public, max-age=3600`
(`SiteHosting:AssetCacheSeconds`). ETags, conditional requests and range requests all work.
[Header rules](#header-rules) override those defaults per path.

### What goes in the zip

* **A single folder at the root is unwrapped.** `dist/index.html`, `dist/assets/…`
  publishes as `/index.html`, `/assets/…`. If anything sits at the root of the archive,
  nothing is unwrapped.
* Path traversal, absolute paths and unportable filenames are dropped.
* `.git`, `.svn`, `.hg`, `.env*`, `__MACOSX`, `.DS_Store`, `Thumbs.db` and `.htpasswd`
  are dropped. Other dot-paths are kept, so `/.well-known/` works.
* A `_headers` file at the root is read as [header rules](#header-rules), and a `_redirects`
  file as [redirects and rewrites](#redirects-and-rewrites). Both are left out of the
  published files, so neither can be fetched from the site.
* The response tells you how many entries were skipped and whether a root `index.html`
  was found.

Each upload becomes a new immutable release directory; the site's pointer is flipped
only once extraction succeeds, so a failed or half-finished upload never reaches a
visitor. The last three releases stay on disk (`SiteHosting:ReleasesToKeep`) and any of
them can be made live again from the site's detail page.

### Header rules

The defaults above suit most sites, but not a file that changes without its name changing.
`index.json` regenerated on every build still answers with an hour-old copy for an hour.
**Header rules** set response headers on the paths that need something else:

```
# a fingerprinted filename can be held forever
/assets/**
  Cache-Control: public, max-age=31536000, immutable

# these are rewritten in place on every deploy
/*.json
  Cache-Control: no-cache
```

A line starting with `/` opens a rule; the indented `Name: value` lines under it are the
headers it sets. `#` starts a comment. In a pattern, `*` matches any run of characters
except `/`, `**` matches across `/`, and `?` matches one character. Matching is
case-insensitive and covers both the URL requested and the file that answered it — a rule
for `/index.html` also catches `/`.

Rules reach a site two ways, and both can be in play at once:

| Where | Scope | Good for |
| ----- | ----- | -------- |
| a `_headers` file at the root of the archive | that release — it rolls back with it | rules a build owns, checked into the repo |
| **Sites → the domain → Header rules** (or the API) | the site — it survives a rollback | fixing caching on something already deployed |

Rules run top to bottom, release rules first, and the last rule to name a header wins. So
a rule set in the UI overrides one of the same name from the archive.

* `Cache-Control: no-cache` means *revalidate every time*, not *never store*. ETags still
  work, so a request for unchanged content costs a `304` rather than a full download. Use
  `no-store` when nothing may be kept at all.
* Headers the transport owns (`Content-Length`, `Transfer-Encoding`, `Connection`, `Host`,
  `Date`, `Server`, …) cannot be set, nor can `Content-Encoding` (the compression
  middleware's), `Set-Cookie`, or `X-Content-Type-Options` (asserted on every response).
* On a [private site](#private-sites) the gate wins: `public` is stripped out of any
  `Cache-Control` a rule sets and `X-Robots-Tag: noindex, nofollow` is reasserted, so a
  rule cannot park gated content in a shared cache.
* A site can hold 100 rules of 25 headers each. A `_headers` file that will not parse is
  reported as a deploy warning and ignored, rather than failing the deploy.

### Redirects and rewrites

A `_redirects` file at the root of the archive, or **Sites → the domain → Redirects and
rewrites**, says where a request should be answered from when it is not the path the
visitor named. One rule per line — where it comes from, where it goes, and the status:

```
# from            to                            status

/old-page         /new-page                     301
/blog/*           /articles/:1                  301
/docs/**          https://docs.example.com/:1   302
/app/**           /app/index.html               200
/removed          /404.html                     404
/legacy/*         /new/:1                       301!
```

The status is optional and defaults to `301`. It decides what kind of rule this is:

| Status | What happens | The address bar |
| ------ | ------------ | --------------- |
| `301` `302` `303` `307` `308` | **redirect** — the visitor is sent to the new address | changes |
| `200` | **rewrite** — a different file answers where they are | stays put |
| `404` | **rewrite**, answered with a `404` status | stays put |

Patterns use the same globs as header rules. `:1`…`:9` in the target stand for what each
wildcard matched, in order, and `:splat` is another name for `:1`:

```
/blog/*     /articles/:1     301      # /blog/hello   → /articles/hello
/docs/**    /manual/:splat   301      # /docs/a/b     → /manual/a/b
```

**A rule stands aside for real content.** If the site actually has a file at the requested
path, that file wins and the rule does not fire. This is what keeps a broad rule like
`/**` from swallowing the stylesheets it was meant to sit behind. Put `!` after the status
to apply the rule anyway:

```
/legacy/*   /new/:1   301!
```

Other things worth knowing:

* **The first rule that matches answers the request**, so put specific rules above broad
  ones. That is the opposite of header rules, where every match applies and the last one
  wins — but the effect is the same in both: a rule you set on the site takes precedence
  over one that came with the release.
* A redirect target can be a path here or a full `http(s)://` address. A rewrite target
  must be a path on this site — this is a file server, not a proxy.
* The visitor's query string is carried across unless the target brings its own.
* A rewrite's target is resolved straight against the files and never re-matched against
  the rules, so one rewrite cannot trigger another and a rule set cannot loop.
* A rewrite is served `no-cache`, like the other fallbacks. A header rule can override
  that, since header rules are applied after the file has been chosen.
* A redirect carries no `Cache-Control` of its own and browsers hold a `301` for a long
  time. [Header rules](#header-rules) apply to redirect responses too, so a rule such as
  `Cache-Control: no-store` on the same path is what keeps a permanent redirect from
  outliving your decision to make it.
* You rarely need `/** /index.html 200`: unmatched paths already
  [fall back to `index.html`](#request-resolution) without a rule.
* A site can hold 100 redirect rules.

### Private sites

Any site can be given a **passcode** under **Sites → the domain → Visibility**. Until a
visitor enters it, that domain serves nothing — no page, no stylesheet, no image — just a
form asking for the passcode:

```
visitor ──GET abc.def.com/report ──▶  401 + passcode form
        ──POST /__passcode ────────▶  302 back to /report  + cookie
        ──GET abc.def.com/report ──▶  the file
```

* The passcode is stored PBKDF2-hashed in `site.json`, so it cannot be read back — keep a
  copy wherever you keep the link.
* Unlocking sets a host-only cookie that lasts `SiteHosting:PasscodeSessionHours` (7 days).
  It works for that one domain; unlocking one private site never unlocks another.
* Replacing or removing the passcode invalidates every cookie issued under the old one.
* Failed attempts are throttled per domain and client address, with an escalating delay
  and a lockout after eight misses. Visitors who are already in are unaffected.
* A protected site's responses are marked `private` and `X-Robots-Tag: noindex`, so a CDN
  or proxy in front of it cannot hand a cached copy to someone who never passed the gate.
* Deploys, rollbacks and release history are untouched by it — the passcode belongs to the
  domain, not to a release.

Only POSTs to `/__passcode` are intercepted, so a site that happens to ship a file at that
path is still reachable once unlocked.

This is a visibility gate: it keeps a link that leaks out of being readable by whoever
finds it. Content that would be damaging to disclose wants a real account system, not a
shared passcode.

### Functions

A site can answer requests with C# as well as files: something like a small Cloudflare
Worker, written as `.cs` files or LINQPad `.linq` queries. There are three ways in, all
for administrators only, since a function is code running inside this server:

* **Upload** `.cs` and `.linq` files, or a `.zip` of them (such as the one **Download
  source** gives you), under **Sites → the domain → Functions**, or under **Functions** in
  the top bar for handlers every site should answer. By default an upload **adds or
  updates**: a file with the same name is replaced and the other live files stay. Choose
  **Replace all files** to make the upload the whole set.
* **Write them in the browser** with **Open the editor**: tabs per file, syntax
  highlighting, **Check** to compile without going live, and a test panel (below).
* **Ship them in the zip** in a top-level `_functions/` folder. They are compiled with the
  deploy and never served. If they fail to compile, nothing is deployed. A zip containing
  `_functions/` is refused outright when the person deploying it is not an administrator.

```csharp
#:sdk Microsoft.NET.Sdk.Web
// NuGet packages work too, restored on the server: #:package ScottPlot@5.1.59

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

public static class Handlers
{
    // Owned by this file, so its cache is thrown away with the file on the next deploy.
    private static readonly JsonSerializerOptions Json = new();

    [HttpPost("/ping/{name?}")]
    public static IResult Ping(HttpContext context, string? name) =>
        Results.Json(new { pong = name is null ? "Hi" : $"Hi {name}" }, Json);
}
```

`samples/functions/` has the same handlers in both formats, with a `#if LINQPAD` block so
the file also runs as-is in LINQPad 9.

* **The Functions table** lists each route with the file and line its handler is written
  on, read from the build's debug information. Click one to open the editor there. The ×
  beside a file removes it by compiling and deploying the others in one step. If another
  file still uses it, the build fails and nothing changes.
* **Several files** compile together as one project, so they can share helper classes. Give
  every class its own name: two files that both declare `class Handlers` fail with
  `CS0101`, naming the file and line. Their packages and imports are merged. Two files
  pinning different versions of the same package are refused, and so are two handlers
  claiming the same route.
* **Routes** come from ASP.NET's own attributes on `public static` methods:
  `[HttpGet("/path")]`, `[HttpPost]`, `[HttpPut]`, `[HttpPatch]`, `[HttpDelete]`, or
  `[Route]` for any method. `{name}` captures a segment and `{name?}` makes it optional.
  The path is used as written: `[HttpPost("/ping")]` answers at `demo.example.com/ping`.
* **Parameters** are filled by name: `HttpContext`, `HttpRequest`, `HttpResponse`,
  `CancellationToken`, or a simple value taken from the route, then the query string. A
  value that does not convert gets a 400.
* **Data:** ask for a `DirectoryInfo` and you get the site's data folder,
  `/data/sites/<domain>/data/`. Put a SQLite database, uploads or JSON files there. It sits
  beside the releases, so it is never served, and deploys and rollbacks leave it alone. A
  rename moves it with the site, and deleting the site deletes it. Global functions share
  `/data/config/functions-data/`. The path is also in
  `HttpContext.Items["StaticSiteHost.DataDirectory"]`. Editor tests use the same folder, so
  they see and change real data.
* **Packages that hook into the process** are handled for you. Some libraries (Microsoft.Data.Sqlite,
  for one) subscribe to process-wide events or start background timers that would keep an
  old build in memory forever. When a build is replaced, the host releases those hooks, and
  logs a warning if a build still lingers five minutes later.
* **Return** an `IResult` (`Results.Json`, `Results.File`, `Results.Text`, …), a `string`,
  an `int` status code, or nothing, optionally wrapped in a `Task`. Returning any other
  object is refused. Serialise it yourself with `Results.Json(value, options)`, using
  options held in a static field of your file as above. Options owned by the server would
  keep every version of your code in memory forever.
* **Order:** the site's own functions, then the global functions, then the site's files.
  A path no function matches is served exactly as before; a matching path with the wrong
  method gets a 405. Functions sit behind a site's passcode like everything else.
* **Uploading** compiles the files with the .NET SDK (`dotnet publish`), loads them once
  to find their routes, and only then makes them live. A compile error names the file and
  line you wrote, and the functions already live keep answering. The first build restores
  packages and can take a minute; later ones take a few seconds.
* **Testing** in the editor runs one request in memory against the checked build or the
  live one: pick a route, fill in its parameters, query, headers and body, and **Run**. No
  DNS, passcode or cross-origin rules get in the way, and an exception comes back with its
  stack trace. JSON is pretty-printed, text and HTML source are shown (HTML also as a
  sandboxed preview that cannot run scripts), images and PDFs render inline, and anything
  else, or anything sent as an attachment, is offered as a download. A test runs your real
  code: whatever a handler writes or sends, it writes or sends.
* **Releases:** functions belong to the live release. Deploying new content keeps them.
  Rolling back brings back the functions that release had, or, if you choose, keeps the
  ones running now: the choice appears next to **Make live** whenever the two differ.
  Global functions have no history; uploading replaces them everywhere.
* A `.linq` file compiles with LINQPad's default namespace imports, so a query that runs
  in LINQPad compiles here. Imports added in LINQPad's own settings rather than the query's
  are not saved in the file; add them to the query.

Functions need the `runtime-functions` Docker image (the default), which includes the SDK.

---

## Accounts

* An administrator creates the account. New accounts are **Members**; an administrator
  can promote them to **Administrator** at any time.
* Two ways to hand over the first credential, chosen at creation time:
  * **Private link** (default) — the account has no password. The link is displayed once
    on the user management screen; copy it and send it to the person yourself. It works
    once and expires after `SiteHosting:InviteLifetimeHours` (7 days by default).
  * **Temporary password** — generated and shown once. The user is forced to change it
    at first sign-in and cannot reach any other page until they do.
* "New link" on any user generates a fresh private link — that is also the password
  reset path.
* Everyone can change their own password at any time from **Account**.
* Disabling, deleting or changing a password rotates the account's security stamp, which
  drops every open session for that user on their next request.

**What each role can do**

|                              | Member | Administrator |
| ---------------------------- | :----: | :-----------: |
| Deploy to any domain         |   ✅   |      ✅       |
| Roll back a release          |   ✅   |      ✅       |
| Set or clear a site passcode |   ✅   |      ✅       |
| Create API keys              |   ✅   |      ✅       |
| Delete a site                |   —    |      ✅       |
| Manage users                 |   —    |      ✅       |

The account named by `Bootstrap:Username` is seeded from configuration on startup. It is
created once — changing its password in the UI is not undone by a restart. If it is ever
locked out, set `Bootstrap__ResetPasswordOnStartup=true` for a single boot.

---

## API

Every deploy the UI can do is available to an API key. Create one under **Account →
API keys**; it is shown once. A key can do exactly what its owner can do.

Authenticate with either header:

```
X-Api-Key: sshost_ppoc7CxI1CNd_Vr6kVNxk6CsN0z6bdylnNhPwfIQxizQHSZqTRTEBhlo
Authorization: Bearer sshost_ppoc7CxI1CNd_Vr6kVNxk6CsN0z6bdylnNhPwfIQxizQHSZqTRTEBhlo
```

| Method   | Path                                        | Notes                       |
| -------- | ------------------------------------------- | --------------------------- |
| `GET`    | `/api/v1/me`                                | who the key belongs to      |
| `GET`    | `/api/v1/sites`                             | every published domain      |
| `GET`    | `/api/v1/sites/{domain}`                    | one domain, with releases   |
| `POST`   | `/api/v1/sites/{domain}/deploy`             | upload a zip                |
| `POST`   | `/api/v1/sites/{domain}/rollback/{release}` | make an earlier release live |
| `PUT`    | `/api/v1/sites/{domain}/passcode`           | `{"passcode":"…"}` — make it private |
| `DELETE` | `/api/v1/sites/{domain}/passcode`           | make it public again        |
| `GET`    | `/api/v1/sites/{domain}/headers`            | header rules, site and release |
| `PUT`    | `/api/v1/sites/{domain}/headers`            | replace the site's header rules |
| `DELETE` | `/api/v1/sites/{domain}/headers`            | remove them                 |
| `GET`    | `/api/v1/sites/{domain}/redirects`          | redirects, site and release |
| `PUT`    | `/api/v1/sites/{domain}/redirects`          | replace the site's redirects |
| `DELETE` | `/api/v1/sites/{domain}/redirects`          | remove them                 |
| `POST`   | `/api/v1/sites/{domain}/rename`             | `{"domain":"…"}` — move to a new domain; administrators only |
| `GET`    | `/api/v1/sites/{domain}/functions`          | the live functions and their routes |
| `GET`    | `/api/v1/sites/{domain}/functions/source`   | download the source as uploaded |
| `PUT`    | `/api/v1/sites/{domain}/functions`          | upload `.cs`/`.linq` files; administrators only |
| `DELETE` | `/api/v1/sites/{domain}/functions`          | stop running functions; administrators only |
| `DELETE` | `/api/v1/sites/{domain}/functions/files/{name}` | remove one file and redeploy the rest; administrators only |
| `GET`    | `/api/v1/functions`                         | the global functions        |
| `GET`    | `/api/v1/functions/source`                  | their source                |
| `PUT`    | `/api/v1/functions`                         | upload global functions; administrators only |
| `DELETE` | `/api/v1/functions`                         | remove them; administrators only |
| `DELETE` | `/api/v1/functions/files/{name}`            | remove one global file and redeploy the rest; administrators only |
| `DELETE` | `/api/v1/sites/{domain}`                    | administrators only         |

Rollback brings back the functions the release had; add `?functions=keep` to run the
current ones on it instead. Functions upload as multipart fields (`-F file=@Orders.cs -F
file=@Reports.linq`, or `-F file=@functions.zip`), or one file as the raw body with its
name in `X-File-Name`. The extension picks the `.cs` or `.linq` reader. An upload merges
into the live files unless you add `?mode=replace`; the response's `kept` lists the live
files a merge carried over. A refused upload returns 400 with `error` and
a `diagnostics` list carrying each problem's file and line. Source downloads as the file
itself, or as a zip when there are several.

Deploy accepts a multipart form field named `file`:

```bash
curl -H "X-Api-Key: $SSH_KEY" \
     -F "file=@site.zip" \
     https://deploy.example.com/api/v1/sites/abc.def.com/deploy
```

…or the archive as the raw request body:

```bash
curl -H "X-Api-Key: $SSH_KEY" \
     -H "Content-Type: application/zip" \
     -H "X-Archive-Name: site.zip" \
     --data-binary @site.zip \
     https://deploy.example.com/api/v1/sites/abc.def.com/deploy
```

You do not have to assemble either by hand: **Sites → the domain → Deploy from the command
line** shows both shapes — a multipart upload, and one that zips a folder and streams it
straight up — already pointed at that domain and at this server, each with a copy button.

The domain is created if it does not exist and replaced if it does. A successful deploy
returns:

```json
{
  "ok": true,
  "domain": "abc.def.com",
  "url": "https://abc.def.com/",
  "release": {
    "id": "20260803-041429-5ab6",
    "fileCount": 8,
    "totalBytes": 24576,
    "strippedRootFolder": true,
    "hasRootIndex": true
  },
  "warnings": []
}
```

Putting a site behind a passcode, and taking it out again:

```bash
curl -X PUT -H "X-Api-Key: $SSH_KEY" -H "Content-Type: application/json" \
     -d '{"passcode":"quarterly-numbers-2026"}' \
     https://deploy.example.com/api/v1/sites/abc.def.com/passcode

curl -X DELETE -H "X-Api-Key: $SSH_KEY" \
     https://deploy.example.com/api/v1/sites/abc.def.com/passcode
```

`GET /api/v1/sites` and `GET /api/v1/sites/{domain}` report `passcodeProtected` and
`passcodeSetUtc`. The passcode itself is never returned — only its hash is stored.

[Header rules](#header-rules) are JSON over the API. `PUT` replaces the site's whole list:

```bash
curl -X PUT -H "X-Api-Key: $SSH_KEY" -H "Content-Type: application/json" \
     -d '{"headers":[{"for":"/*.json","set":{"Cache-Control":"no-cache"}}]}' \
     https://deploy.example.com/api/v1/sites/abc.def.com/headers
```

[Redirects](#redirects-and-rewrites) work the same way:

```bash
curl -X PUT -H "X-Api-Key: $SSH_KEY" -H "Content-Type: application/json" \
     -d '{"redirects":[{"from":"/blog/*","to":"/articles/:1","status":301}]}' \
     https://deploy.example.com/api/v1/sites/abc.def.com/redirects
```

`from`, `to` and `status` are the three columns of the text form; add `"force": true` for
the `!`. Both `GET`s return `siteRules` alongside the read-only `releaseRules` that came
from the current release's `_headers` / `_redirects` file. A rejected rule comes back as
`{ "error": "…", "errors": [ … ] }` and nothing is saved.

Failures return `{ "ok": false, "error": "…" }` with a `4xx` status. `401` means the key
is missing, unknown, revoked or expired; `403` means the key's owner lacks the role.

---

## Configuration

Every setting is a normal ASP.NET Core configuration key, so `appsettings.json`, an
environment variable with `__` separators, or a compose `environment:` entry all work.

| Key                                    | Default      | Meaning |
| -------------------------------------- | ------------ | ------- |
| `SiteHosting:DataRoot`                 | `/data`      | Root of the data volume |
| `SiteHosting:ManagementHosts`          | *(empty)*    | Hostnames that serve the UI/API |
| `SiteHosting:TrustForwardedHeaders`    | `false`      | Honour `X-Forwarded-Host`/`-Proto`/`-For` |
| `SiteHosting:MaxUploadBytes`           | 512 MiB      | Largest accepted archive |
| `SiteHosting:MaxExtractedBytes`        | 2 GiB        | Largest accepted total after extraction |
| `SiteHosting:MaxEntryBytes`            | 512 MiB      | Largest accepted single file |
| `SiteHosting:MaxEntries`               | `50000`      | Largest accepted entry count |
| `SiteHosting:ReleasesToKeep`           | `3`          | Releases retained per site |
| `SiteHosting:AssetCacheSeconds`        | `3600`       | `max-age` for non-HTML assets |
| `SiteHosting:SpaFallbackForAllRequests`| `false`      | Fall back to `index.html` for assets too |
| `SiteHosting:InviteLifetimeHours`      | `168`        | Private link lifetime |
| `SiteHosting:MinPasswordLength`        | `12`         | Enforced when a password is set |
| `SiteHosting:MinPasscodeLength`        | `8`          | Enforced when a site passcode is set |
| `SiteHosting:PasscodeSessionHours`     | `168`        | How long a visitor stays unlocked |
| `Bootstrap:Username`                   | `admin`      | Seeded administrator |
| `Bootstrap:Password`                   | *(empty)*    | Empty generates one on first run |
| `Bootstrap:MustChangePassword`         | `false`      | Force a change even with a configured password |
| `Bootstrap:ResetPasswordOnStartup`     | `false`      | Recovery switch — re-applies the configured password |

### On disk

```
/data
├── config/
│   ├── users.json                 accounts (PBKDF2-hashed passwords, hashed invite tokens)
│   ├── apikeys.json               API keys (hashed secrets)
│   ├── audit.log                  JSON lines: deploys, logins, user and key changes
│   ├── keys/                      data-protection keys, so cookies survive restarts
│   ├── functions.json             which global functions are live
│   ├── functions/<id>/            global function bundles: src/ as uploaded, bin/ as built
│   └── bootstrap-password.txt     written only when a password was generated
├── sites/
│   └── abc.def.com/
│       ├── site.json              which release is live, plus history, rules and any passcode hash
│       ├── data/                  functions' own data (SQLite, uploads…); never served
│       ├── releases/
│       │   └── 20260803-041429-5ab6/   the files being served
│       └── functions/
│           └── 20260803-052210-9c1f/   a function bundle: src/ as uploaded, bin/ as built
└── tmp/                           streaming scratch space, cleared at startup
```

Back up `/data` and you have backed up everything.

---

## Production notes

* **TLS.** The app speaks plain HTTP on `:8080`. Terminate TLS at a reverse proxy
  (Caddy handles wildcard certificates for this shape of workload well) or in front of
  it, and publish on `:80`/`:443`.
* **Forwarded headers.** Only turn `TRUST_FORWARDED_HEADERS=true` on when a proxy you
  control sits in front. Routing is driven by the Host header, so a spoofable
  `X-Forwarded-Host` would let a caller choose which site answers.
* **Single instance.** Users, keys and site metadata are JSON files owned by one
  process. Scale up, not out. Passcode throttling counters live in memory, so they reset
  on restart.
* **Caching in front.** Responses from a passcode-protected site are marked `private`,
  which a well-behaved shared cache honours. If your proxy is configured to cache
  aggressively regardless, exclude those domains — the gate runs in this app, not in the
  cache.
* **The data volume.** `docker-compose.yml` uses a named volume so the non-root
  container user owns it. If you swap in a bind mount, `chown` the host directory to
  UID 1654 first.
* **Image size.** The default image (`runtime-functions`, ~920 MB) includes the .NET SDK
  so uploaded functions can be compiled; the NuGet cache lives on the volume at
  `/data/nuget`. Set `IMAGE_TARGET=runtime` for a ~230 MB image that serves static sites
  only. A function build briefly uses 0.5–1 GB of memory.

---

## Local development

```bash
dotnet run --project src/StaticSiteHost
```

It comes up on <http://localhost:8080> — the same port as the container, so every URL in
this README works either way. Stop the container first if it is running, or the port is
already taken.

The Development profile writes to `src/StaticSiteHost/.data`, treats `localhost` and
`127.0.0.1` as management hosts, and seeds `admin` / `development-password`.

To host sites locally, publish them under `.localhost` names such as `blog.localhost`
and `docs.localhost`, then open <http://blog.localhost:8080>. Browsers resolve every
`*.localhost` name to your own machine, so no hosts file entries or proxy are needed, and
each site gets its own origin just as it would in production. Sites are told apart by
hostname only, so `localhost:8010` becomes plain `localhost` — the management UI.
Command-line tools older than curl 7.85 do not resolve `*.localhost` on their own; use
`curl --resolve blog.localhost:8080:127.0.0.1 http://blog.localhost:8080/`.

Deploying the sample site from the command line, start to finish — run this from the
repo root, with a key from **Account → API keys**:

```bash
export SSH_KEY=sshost_...

curl -H "X-Api-Key: $SSH_KEY" \
     -F "file=@sample-site.zip" \
     http://localhost:8080/api/v1/sites/abc.def.com/deploy
```

Sites are keyed off the Host header, so to open one without touching DNS either add
`abc.def.com` to `/etc/hosts` pointing at `127.0.0.1`, or send the header yourself:

```bash
curl -H "Host: abc.def.com" http://localhost:8080/blog/hello-world
```

The whole app is under `src/StaticSiteHost`:

| Path             | What lives there                                             |
| ---------------- | ------------------------------------------------------------ |
| `Serving/`       | Host-header fork, path resolution, static file serving        |
| `Services/`      | JSON stores, zip extraction and release management            |
| `Security/`      | Password hashing, tokens, API key authentication, throttling  |
| `Endpoints/`     | The `/api/v1` surface                                         |
| `Pages/`         | The management UI (Razor Pages)                               |
