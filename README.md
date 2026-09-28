# Static Site Host

Static Site Host is an ASP.NET Core (.NET 10) server that hosts static sites from zip files and
runs each site's own C# functions.

You upload a zip, choose a domain, and the site is live. One app hosts any number of sites from
one data volume. It has accounts, invitation links, optional [per-site passcodes](#private-sites)
and an API for CI. There is no database: users, API keys and site metadata are JSON files on the
volume.

A site can be a plain folder of HTML. It can also be a whole application from the same zip:

* C# [functions](#functions), with [middleware](#middleware) and [services and jobs](#services-and-jobs)
* per-site [variables](#variables), whose values are kept on the server, not in the zip
* [realtime](#realtime) events sent to the pages open on the site
* an [AI](#ai) chat that uses an API key the server keeps

Every deploy replaces the site's code in place. Nothing needs a restart.

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

Run the published image. It is on Docker Hub, and mirrored at `ghcr.io/mattjcowan/static-site-hosting`:

```bash
docker run -d --name static-site-host -p 8080:8080 -v site-data:/data mattjcowan/static-site-hosting
docker logs static-site-host     # the generated administrator password
```

Or, from a clone of this repo, use Docker Compose:

```bash
cp .env.example .env                          # optional — sensible defaults work as-is
docker compose pull && docker compose up -d   # the published image
docker compose up -d --build                  # or build it from source
```

| Tag | What it is |
|---|---|
| `latest`, `1.2.3`, `1.2` | Includes the .NET SDK, so [functions](#functions) can be compiled (~930 MB) |
| `slim`, `1.2.3-slim`, `1.2-slim` | Static hosting only (~230 MB) |

Both images are built for `linux/amd64` and `linux/arm64`. With Compose, set `IMAGE_TAG=slim` in
`.env` to run the smaller image.

Open <http://localhost:8080>. On the first run, the container prints the generated
administrator password:

```
==========================================================================
 A password was generated for the administrator account 'admin':

     Kx7pQm2vRt9wLc3bZn4d

 It must be changed at first sign-in. Also saved to /data/config/bootstrap-password.txt.
 Set Bootstrap__Password to choose your own and avoid this message.
==========================================================================
```

Sign in as `admin`. Change the password when the server asks. Then deploy a site.

To choose the password yourself, set `ADMIN_PASSWORD` in `.env` before the first start.

### Something to upload

`sample-site.zip`, at the root of this repo, is a small demo site. It has a home page, a
stylesheet, a script, a `404.html`, and a `blog/` folder with three posts and its own index.
Drop it on the deploy screen, choose a domain, and follow the links. Every page shows a banner at
the bottom. The banner shows the path you requested and the file the server sent. This lets you
watch the [request resolution](#request-resolution) rules work.

The demo also has these files:

* `_headers`: every response carries `X-Demo-Rules: on`. Open the browser's network tab to see a
  [header rule](#header-rules) apply.
* `_redirects`: `/posts/hello-world` redirects to `/blog/hello-world`, and `/preview` shows a
  post while the address stays `/preview`. See [redirects and rewrites](#redirects-and-rewrites).
* `_variables.json`: declares `SITE_TITLE` (shown in the banner on the home page),
  `SUPPORT_EMAIL` and a secret `ANALYTICS_KEY`. See [variables](#variables).

The demo's sources are in `samples/demo-site/`. After you edit any sample, rebuild the zip files:

```bash
scripts/build-samples.sh             # both zips
scripts/build-samples.sh blog-site   # or just one
```

`blog-site.zip` is a larger sample: **Night Sky Field Notes**, an astronomy journal that uses
[functions](#functions). Its pages show a live Moon phase, a constellation of the season, a
dark-adaptation timer and a red-light mode. Its `_functions/` folder adds:

* sign-in, user management and a Markdown journal, stored in SQLite (through Dapper) in the
  site's [data folder](#functions) by a database [service](#services-and-jobs)
* [realtime](#realtime) notices to the journal's readers when posts change, a count of how many
  are reading, and a note to a writer when an administrator changes their account
* an "Ask the sky" panel and a **Suggest** button for a post's summary, for its writers, which use
  [AI](#ai)
* [jobs](#services-and-jobs) that tidy the database every hour and back it up every night

An administrator must deploy it, because the server refuses a zip with a `_functions/` folder
from anyone else. The first request creates an `admin` account for the blog. Its generated
password is printed in the server log (`docker compose logs`). It must be changed at first
sign-in. The sources are in `samples/blog-site/`, and `scripts/build-samples.sh` rebuilds the
zip.

---

## What a site can do

| Capability | What it is for | How you turn it on | Who may | Read more |
| ---------- | -------------- | ------------------ | ------- | --------- |
| Static files | Serve HTML, CSS, scripts and images from a zip | Upload the zip on the **Deploy** page, or `POST /api/v1/sites/{domain}/deploy` | Member or administrator | [How hosting works](#how-hosting-works) |
| Header rules | Set response headers, such as `Cache-Control`, per path | A `_headers` file in the zip, **Sites → the domain → Header rules**, or `PUT /api/v1/sites/{domain}/headers` | Member or administrator | [Header rules](#header-rules) |
| Redirects and rewrites | Send a visitor to another address, or answer a path with another file | A `_redirects` file in the zip, **Sites → the domain → Redirects and rewrites**, or `PUT /api/v1/sites/{domain}/redirects` | Member or administrator | [Redirects and rewrites](#redirects-and-rewrites) |
| Variables | Keep settings, keys and text outside the build | Declare them in `_variables.json` in the zip; set values under **Sites → the domain → Variables** or with `PUT /api/v1/sites/{domain}/variables` | Members set plain values; only administrators set secrets and `${env:…}` values | [Variables](#variables) |
| Passcodes | Keep a site private behind one shared passcode | **Sites → the domain → Visibility**, or `PUT /api/v1/sites/{domain}/passcode` | Member or administrator | [Private sites](#private-sites) |
| Functions | Answer requests with C# code | A `_functions/` folder in the zip, an upload on the site's **Functions** card, the browser editor, or `PUT /api/v1/sites/{domain}/functions` | Administrator | [Functions](#functions) |
| Middleware | Run code before every request to the site, files included | A `[Middleware]` method in a function file | Administrator | [Middleware](#middleware) |
| Services and jobs | Register services; run code in the background or on a timetable | `[ConfigureServices]`, `[BackgroundService]`, `[Schedule]` or `[Every]` on a method in a function file | Administrator | [Services and jobs](#services-and-jobs) |
| Realtime | Send events from the server to the pages open on the site | On for every site. Pages use `site.realtime` from `/_host/site.js`; functions use `IRealtime`; anything else uses `POST /api/v1/sites/{domain}/realtime/publish`. `[RealtimeConnect]` and `[RealtimeJoin]` hooks decide who may connect and join | Any page may listen; any API key may publish; administrators write the hooks | [Realtime](#realtime) |
| AI | Chat with an AI model through the server, which keeps the API key | Add a provider under **AI** in the top bar, then choose it under **Sites → the domain → AI**. Pages use `site.ai.chat`; functions use `IAiChat`; an `[AiAccess]` hook decides which visitors may chat | Administrator | [AI](#ai) |
| The API | Deploy and manage sites from scripts and CI jobs | Create an API key under **Account → API keys** | Member or administrator (a key can do what its owner can do) | [API](#api) |
| Accounts | Give people access to the management UI and the API | An administrator creates accounts under **Users** | Administrator | [Accounts](#accounts) |

**The deploy loop.** A deploy works the same way for every capability. You upload a zip, in the
UI or with the API. The server unpacks it into a new release. If the zip has functions, the
server compiles them first, and nothing goes live if they do not compile. The server then makes
the release live in one step. New functions, services and jobs replace the old ones in place,
and the old ones stop in the background. The server does not restart. What is saved on the site
itself (variable values, the passcode, rules saved in the UI, AI settings) stays as it was.

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

`scripts/screenshots.sh` takes these screenshots. It starts a temporary instance, deploys the
samples through the UI and captures each screen. Run it again after you change the UI. It needs
Node, and it downloads Chromium the first time. The app itself needs neither.

---

## How hosting works

**What it is.** The `Host` header of a request decides which app answers it:

| Host                                        | Response                                    |
| ------------------------------------------- | ------------------------------------------- |
| listed in `SiteHosting:ManagementHosts`      | the management UI and API                   |
| a domain with a site published to it         | that site: its `/_host/` endpoints, functions and middleware, then its files |
| anything else                                | the management UI when no management hosts are configured, otherwise `404` |

A request to `/healthz` on any host goes to the management app, which answers
`{ "status": "ok" }`. A site cannot serve a file at `/healthz`.

**When `ManagementHosts` is empty**, the management UI answers on any host name that has no site.
This is the simplest setup. In production, name the management host
(`MANAGEMENT_HOST=deploy.example.com`). Then a request for an unknown domain gets a `404` page,
not your sign-in page.

**How to use it.** Point every hosted domain's DNS at this server. Publish the container on
port 80.

**Changing a site's domain.** An administrator can move a site to another domain under
**Sites → the domain → Change domain**, or with `POST /api/v1/sites/{domain}/rename`. The site
keeps its releases, rules, passcode, variables and data folder. The old domain stops answering
at once, and nothing redirects from it. Point DNS at the server first, and update any deploy
scripts, because API calls name the domain.

### Request resolution

**What it is.** A site behaves like a standard single-page-app host. For a request to
`abc.def.com/abc/def/gemini`, the server tries, in order:

1. `/abc/def/gemini` — the exact file, if it exists
2. `/abc/def/gemini.html` — the `.html` file of the same name, when the last segment has no extension
3. if `/abc/def/gemini/` is a directory → `301` to `/abc/def/gemini/`
4. `/abc/def/index.html`, then `/abc/index.html`, then `/index.html` — walking up the tree
5. `/404.html`, sent with a `404` status, if the site has one

A request with a trailing slash (`abc.def.com/abc/def/`) starts at step 4. It resolves to
`/abc/def/index.html` and walks up the tree from there.

Because of step 2, a site's links can leave `.html` off: a link to `/about` is answered by
`/about.html`, and the address bar keeps `/about`.

[Redirects and rewrites](#redirects-and-rewrites) are checked before these steps. So a rule can
answer a request with a different file, or send the visitor somewhere else.

**Assets are handled differently on purpose.** The server skips step 4 for a request that wants
an asset, such as `/assets/app.js`. A request wants an asset when its path has an extension other
than `.html` or `.htm`, and its `Accept` header does not include `text/html`. Such a request gets
a `404`, not the index page. Serving HTML in place of a missing script breaks module loading in
ways that are hard to debug. Set `SiteHosting:SpaFallbackForAllRequests=true` to apply step 4 to
every request.

**Caching.** HTML files, and every answer from step 4 or step 5, are sent with
`Cache-Control: no-cache`. Other files get `public, max-age=3600`. The number comes from
`SiteHosting:AssetCacheSeconds`. ETags, conditional requests and range requests all work.
[Header rules](#header-rules) can replace these defaults per path.

**Rules and limits.**

* Files answer `GET` and `HEAD` only. Any other method gets `405` with `Allow: GET, HEAD`, unless a
  [function](#functions) answers it first.
* A path with more than 64 segments, a `.` or `..` segment, a segment longer than 255
  characters, or a control character or `\` gets a `404`.

**Errors.** A path that resolves to nothing gets a `404`: the site's `/404.html` if it has one,
otherwise a short "Not found" page from the server.

### What goes in the zip

**What it is.** A zip file holds the site's files. Each upload becomes a new release.

**How the server reads it:**

* **A single folder at the root is unwrapped.** A zip that holds `dist/index.html` and
  `dist/assets/…` publishes `/index.html` and `/assets/…`. If any file sits at the root of the
  zip, nothing is unwrapped.
* The server drops entries with path traversal (`..`) and entries with absolute paths.
* The server drops names that are not portable: names with `:`, `*`, `?`, `"`, `<`, `>`, `|` or
  control characters, and names longer than 255 characters.
* The server drops `.git`, `.svn`, `.hg`, `.bzr`, `.env` and `.env.*`, `__MACOSX`, `.DS_Store`,
  `Thumbs.db` and `.htpasswd`. It keeps other names that start with a dot, so `/.well-known/`
  works.
* A `_headers` file at the root is read as [header rules](#header-rules). A `_redirects` file is
  read as [redirects and rewrites](#redirects-and-rewrites). A `_variables.json` file is read as
  the site's [variables](#variables). None of the three is published, so a visitor cannot fetch
  them. Each can be at most 64 KB; a larger one is ignored, with a warning.
* A top-level `_functions/` folder holds the site's [functions](#functions). It is compiled and
  never published.
* The response says how many entries the server skipped, and whether it found an `index.html`
  at the root.

**Releases.** Each upload becomes a new release directory, which never changes afterwards. The
server switches the site to the new release only after the whole zip has been extracted. So a
failed or partial upload never reaches a visitor. The server keeps the last three releases on disk
(`SiteHosting:ReleasesToKeep`). You can make any of them live again from the site's page.

**Rules and limits.**

| Limit | Default | Setting |
| ----- | ------- | ------- |
| Largest zip | 512 MiB | `SiteHosting:MaxUploadBytes` |
| Largest total after extraction | 2 GiB | `SiteHosting:MaxExtractedBytes` |
| Largest single file | 512 MiB | `SiteHosting:MaxEntryBytes` |
| Most entries | 50,000 | `SiteHosting:MaxEntries` |

**Errors.** A zip over a limit, or with no usable files, is refused, and nothing is deployed. A
deploy with no `index.html` at the root still goes live, with a warning.

### Header rules

**What it is.** A header rule sets response headers on the paths that match it.

**When to use it.** The default caching suits most files, but not a file that changes while its
name stays the same. For example, an `index.json` that each build rewrites would still be cached
by browsers for an hour. A header rule fixes that:

```
# a fingerprinted filename can be held forever
/assets/**
  Cache-Control: public, max-age=31536000, immutable

# these are rewritten in place on every deploy
/*.json
  Cache-Control: no-cache
```

**How to write a rule.**

* A line that starts with `/` starts a rule. It is the path pattern.
* Each indented `Name: value` line under it is a header the rule sets.
* `#` starts a comment.
* In a pattern, `*` matches any characters except `/`. `**` matches any characters, `/`
  included. `?` matches one character except `/`. Everything else is literal.
* Matching ignores case. A rule matches the URL the visitor requested and also the file that
  answered it. So a rule for `/index.html` also matches `/`.

**Where rules come from.** A site can have rules from both places at once:

| Where | Scope | Good for |
| ----- | ----- | -------- |
| a `_headers` file at the root of the zip | that release; a rollback brings back that release's rules | rules that belong to the build and live in the repo |
| **Sites → the domain → Header rules** (or the [API](#api)) | the site; a rollback does not change them | fixing caching on something already deployed |

**Order.** The server applies the release's rules first, then the site's rules, top to bottom.
When two rules set the same header, the last one wins. So a rule you save in the UI replaces a
header of the same name from the zip.

**Rules and limits.**

* `Cache-Control: no-cache` means "check with the server every time". It does not mean "never
  store". ETags still work, so a request for unchanged content costs a `304`, not a full
  download. Use `no-store` when nothing may be kept at all.
* A rule cannot set these headers: `Connection`, `Content-Length`, `Content-Range`, `Date`,
  `Host`, `Keep-Alive`, `Proxy-Authenticate`, `Proxy-Authorization`, `Server`, `TE`, `Trailer`,
  `Transfer-Encoding`, `Upgrade` (the transport sets them), `Content-Encoding` (the compression
  middleware sets it), `Set-Cookie`, and `X-Content-Type-Options` (the server sets it on every
  response).
* On a [private site](#private-sites), the passcode protection comes first. The server removes `public` from
  any `Cache-Control` a rule sets, and sets `X-Robots-Tag: noindex, nofollow` again. So a rule
  cannot put gated content in a shared cache.
* Limits:

  | Limit | Value |
  | ----- | ----- |
  | Rules per site (for each source) | 100 |
  | Headers per rule | 25 |
  | Header name length | 64 characters |
  | Header value length | 2,048 characters |
  | Pattern length | 400 characters |
  | Wildcards per pattern | 9 |

**Errors.** A `_headers` file that does not parse is ignored, and the deploy reports a warning.
The deploy still goes live. The UI and the API refuse a rule that is not valid, and save
nothing.

### Redirects and rewrites

**What it is.** A redirect sends the visitor to another address. A rewrite answers a path with a
different file, and the address in the browser does not change.

**How to use it.** Write the rules in a `_redirects` file at the root of the zip, or under
**Sites → the domain → Redirects and rewrites**. Each line is one rule: the path it matches, the
target, and the status.

```
# from            to                            status

/old-page         /new-page                     301
/blog/*           /articles/:1                  301
/docs/**          https://docs.example.com/:1   302
/app/**           /app/index.html               200
/removed          /404.html                     404
/legacy/*         /new/:1                       301!
```

The status is optional. The default is `301`. The status decides what kind of rule it is:

| Status | What happens | The address bar |
| ------ | ------------ | --------------- |
| `301` `302` `303` `307` `308` | **redirect** — the visitor is sent to the new address | changes |
| `200` | **rewrite** — a different file answers at the same address | does not change |
| `404` | **rewrite**, answered with a `404` status | does not change |

Patterns use the same wildcards as header rules. In the target, `:1` to `:9` stand for what each
wildcard matched, in order. `:splat` is another name for `:1`:

```
/blog/*     /articles/:1     301      # /blog/hello   → /articles/hello
/docs/**    /manual/:splat   301      # /docs/a/b     → /manual/a/b
```

**Real content wins.** If the site has a file at the requested path, the server sends that file
and ignores the rule. (The file must match by step 1, 2 or 3 of
[request resolution](#request-resolution), or be the `index.html` of a directory request.) This
lets a broad rule such as `/**` leave the site's stylesheets and scripts alone. To apply a rule
even when a file exists, put `!` after the status:

```
/legacy/*   /new/:1   301!
```

**Rules and limits.**

* **The first rule that matches answers the request.** Put specific rules above broad ones. This
  is the opposite of header rules, where every match applies and the last one wins. In both cases
  a rule saved on the site wins over a rule from the release: the server checks the site's
  redirect rules before the release's.
* A redirect target can be a path on this site or a full `http://` or `https://` address. A
  rewrite target must be a path on this site. The server serves files; it is not a proxy.
* The server adds the visitor's query string to the target, unless the target has its own.
* The server looks up a rewrite's target directly among the files. It never checks the target
  against the rules again. So one rewrite cannot trigger another, and rules cannot loop.
* A rewrite is sent with `Cache-Control: no-cache`, like the other fallbacks. A header rule can
  change that, because header rules apply after the server has chosen the file.
* A redirect has no `Cache-Control` header of its own, and browsers keep a `301` for a long time.
  [Header rules](#header-rules) also apply to redirect responses. So add a rule such as
  `Cache-Control: no-store` for the same path if you may want to remove the redirect later.
* Redirect rules apply to requests that no [function](#functions) answered.
* You rarely need `/** /index.html 200`: paths that match nothing already
  [fall back to `index.html`](#request-resolution) without a rule.
* A site can have 100 redirect rules (for each source). A target can be 1,000 characters.

**Errors.** A `_redirects` file that does not parse is ignored, and the deploy reports a warning.
The UI and the API refuse a rule that is not valid, and save nothing.

### Variables

**What it is.** A variable is a named value that a site needs but that is not part of its build:
a title, a contact address, the key for an analytics account. The zip declares which variables
it expects, in a `_variables.json` file at its root. The values are set on the site, so a deploy
never resets them.

**When to use it.** Use a variable for a value that should change without a new build. Also use
one for a value that must not be in the repo, such as an API key.

**How to use it.**

1. Declare the variables in `_variables.json`:

   ```json
   {
     "SITE_TITLE": "My site",
     "SUPPORT_EMAIL": { "description": "Shown in the footer", "public": true, "required": true },
     "STRIPE_KEY": { "description": "Used by the checkout function", "secret": true }
   }
   ```

   Each entry is either the default value, as a string, or an object with any of these keys
   (key names ignore case):

   | Key           | Meaning |
   | ------------- | ------- |
   | `default`     | the value until the site sets its own |
   | `description` | shown next to the variable on the site page |
   | `public`      | sent to the browser in `/_host/site.js` |
   | `secret`      | stored encrypted and never shown again; cannot also be `public` |
   | `required`    | an empty value is flagged on the site page and as a deploy warning |

   A variable that is neither public nor secret is for functions only. The file may contain
   comments and trailing commas.

2. Set values under **Sites → the domain → Variables**, or with the [API](#api). Any signed-in
   user may set plain values. Only administrators may set, change or remove secrets and values
   that use `${env:…}` (below). The card lists every variable the live release declares, with
   its description and its value. It also shows where the value comes from: set on the site,
   the release default, or nothing. You can also add variables that no release declares.

3. Read the public variables in a page, from `/_host/site.js`:

   ```html
   <script src="/_host/site.js"></script>
   <script>
     document.title = site.get('SITE_TITLE', document.title);
   </script>
   ```

4. Read every variable in a function, as an `IReadOnlyDictionary<string, string>` parameter:

   ```csharp
   [HttpGet("/contact")]
   public static string Contact(IReadOnlyDictionary<string, string> variables) =>
       $"Write to {variables.GetValueOrDefault("SUPPORT_EMAIL", "the site owner")}.";
   ```

**Values.** Values are stored in the site's `site.json`, not in a release. So they stay the same
across deploys and rollbacks. A rollback brings back the older release's declarations and leaves
the values alone. **Use default** removes the site's value, so the release default applies
again. An empty value is still a value: it replaces the default.

**Values from the server's environment.** `${env:NAME}`, anywhere in a value or a default, is
replaced with that environment variable of the server when the value is read. This lets a key
stay in the container's environment instead of in a file:

```json
{ "STRIPE_KEY": { "secret": true, "default": "${env:STRIPE_KEY}" } }
```

* An environment variable that is not set reads as empty. The site page says which ones are not
  set.
* The replacement runs once. Text that an environment variable brings in is not replaced again.
* The environment also holds the server's own configuration. So only administrators may write
  `${env:…}`, in a value or in a default they deploy. Only administrators may change or remove a
  value that uses it.
* When a member deploys a zip whose `_variables.json` has a default with `${env:…}`, the server
  keeps the variable but drops that default, with a warning.

**Secrets.** The server encrypts a secret with its data-protection keys before it writes it. This
applies to a secret set on the site and to a secret's default in the zip. The server never sends
a secret back: the site page and the API only say whether one is set. On the site page, leaving a
secret's box empty keeps the stored value. A secret is usually a credential that an administrator
was trusted with, so members cannot set, change or remove one. The site page shows such a
variable to a member with "administrators only" in place of its controls. A member's whole-list
`PUT` over the API keeps every secret, and every value that uses `${env:…}`, exactly as it was.

**Who may see a value.** Anyone who can deploy can declare a name public or secret. So for a name
the release declares, the release can make a value more private than the person who saved it
asked for, but never less private:

* A value is **secret** if the release declares it secret, or if it was saved as a secret.
* For a name the release declares, the value is **public** only if the release declares it
  public and it is not secret.
* For a name no release declares, the flags saved with the value decide.
* A value saved as a secret stays a secret until someone saves it again.
* A value saved as plain text is encrypted in `site.json` as soon as a deploy or a rollback
  declares its name secret.
* A value saved on the site that uses `${env:…}` reaches the browser only if it was saved with
  `"public": true`, whatever the release declares.

**In the browser.** Every site answers `/_host/site.js`. It defines a frozen `window.site` object:

| Name | What it is |
| ------ | ---------- |
| `domain` | the domain the page was served for |
| `vars` | the public variables, by name, all strings |
| `version` | the host's version |
| `get(name, fallback)` | the variable's value, or `fallback` when it is missing or empty |
| `realtime` | see [Realtime](#realtime) |
| `ai` | see [AI](#ai) |

`/_host/variables.json` returns the same public variables as a plain JSON object. Both files are
sent `no-cache` with an ETag. So a page always sees current values, and pays only for a `304`
when nothing changed. The server never sends anything but public variables to the browser.

**In functions.** A parameter of type `IReadOnlyDictionary<string, string>` receives every
variable of the site the request is for. It includes secrets, with `${env:…}` already replaced. A
declared variable with no value is present, with an empty string.

* Global functions get the variables of the site they are answering for.
* A test in the editor gets what a live request to its host would get.
* Helper code without the parameter finds the same dictionary in
  `HttpContext.Items["StaticSiteHost.Variables"]`.
* An `ISiteVariables` parameter, or `ISite.Variables`, gives the same values with helper methods
  (see [Functions](#functions)).

A function can also read the host's own configuration, with
`context.RequestServices.GetRequiredService<IConfiguration>()`, and the host's environment, with
`Environment.GetEnvironmentVariable`. Both belong to the host and are the same for every site.
Variables are how one site gets settings of its own.

**The `/_host/` paths.** `/_host/` is reserved on every site:

| Path | Methods | What it answers |
| ---- | ------- | --------------- |
| `/_host/site.js` | `GET`, `HEAD` | the browser library, with the site's public variables |
| `/_host/variables.json` | `GET`, `HEAD` | the public variables as a JSON object |
| `/_host/signalr.js` | `GET`, `HEAD` | the SignalR browser client, which `site.realtime` loads |
| `/_host/ai/chat` | `POST` | [AI chat](#in-the-browser-1) |
| `/_host/realtime` | SignalR | the [realtime](#realtime) hub |

The server answers `/_host/` after the [passcode](#private-sites) gate and before functions and
files. So a file or a function at that path never answers. Every answer under `/_host/` carries
`Cross-Origin-Resource-Policy: same-origin`. This stops a page on another site (including a
sibling under the same parent domain) from loading `site.js` with the visitor's cookies and
reading a private site's variables.

**Rules and limits.**

| Rule | Value |
| ---- | ----- |
| Name | letters, digits and `_`; starts with a letter or `_`; case-sensitive |
| Name length | 1 to 64 characters |
| Variables a release can declare | 200 |
| Values a site can set | 200 |
| Value or default size | 8 KB (UTF-8) |
| Description length | 500 characters |

**Errors.**

* A `_variables.json` that does not parse is ignored, and the deploy reports a warning, as for a
  bad `_headers` file.
* A required variable with an empty value is flagged on the site page and reported as a deploy
  warning.
* Another path under `/_host/` gets a `404` with a JSON body `{ "error": "…" }`. A method the
  path does not answer gets a `405`.
* On a private site, a visitor who has not entered the passcode gets a `401` with
  `{ "error": "This site needs a passcode." }` under `/_host/`, not the passcode form. A script
  could do nothing with a form.

### Private sites

**What it is.** A passcode on a site. Until a visitor enters it, the domain serves nothing (no
page, no stylesheet, no image), only a form that asks for the passcode:

```
visitor ──GET abc.def.com/report ──▶  401 + passcode form
        ──POST /__passcode ────────▶  302 back to /report  + cookie
        ──GET abc.def.com/report ──▶  the file
```

**When to use it.** Use it to stop a link that leaks from being readable by whoever finds it. It
is a visibility gate. Content that would cause harm if disclosed needs a real account system, not
a shared passcode.

**How to use it.** Set the passcode under **Sites → the domain → Visibility**, or with
`PUT /api/v1/sites/{domain}/passcode`. Remove it the same way.

**Rules and limits.**

* The server stores the passcode as a PBKDF2 hash in `site.json`. It cannot be read back, so keep
  a copy wherever you keep the link.
* A passcode is 8 to 200 characters. The minimum comes from `SiteHosting:MinPasscodeLength`.
* Entering the passcode sets a host-only cookie. It lasts `SiteHosting:PasscodeSessionHours`
  (7 days). It works for that one domain only. Unlocking one private site never unlocks another.
* Replacing or removing the passcode makes every cookie issued for the old one invalid.
* The server slows down failed attempts per domain and client address, with a growing delay, and
  locks out an address after eight misses. Visitors who are already in are not affected.
* A private site's responses are marked `private` and `X-Robots-Tag: noindex`. So a CDN or proxy
  in front of it cannot give a cached copy to someone who never entered the passcode.
* The passcode belongs to the domain, not to a release. Deploys, rollbacks and release history do
  not change it.
* The server intercepts only `POST` requests to `/__passcode`. So a site that has a file at that
  path can still serve it once unlocked.

**Errors.** A visitor who has not entered the passcode gets `401` and the form. Under `/_host/`,
the answer is `401` with a JSON body (see [Variables](#variables)).

### Functions

**What it is.** A site can answer requests with C# code as well as with files. This is like a
small Cloudflare Worker. You write functions as `.cs` files (.NET 10 file-based apps) or as
LINQPad `.linq` queries. They run inside this server.

There are two kinds:

* **A site's functions** answer requests to that site only.
* **Global functions** answer on every site. You upload them under **Functions** in the top bar.

**When to use it.** Use functions when a site needs code on the server: to store data, check a
sign-in, call another service with a key, or build a response such as an image.

**Who may.** Only administrators, because a function is code that runs inside this server.

**How to use it.** There are three ways to deploy functions:

* **Upload** `.cs` and `.linq` files, or a `.zip` of them (such as the one **Download source**
  gives you). Use **Sites → the domain → Functions** for a site, or **Functions** in the top bar
  for global functions. By default an upload **adds or updates**: a file with the same name is
  replaced, and the other live files stay. Choose **Replace all files** to make the upload the
  complete list of files.
* **Write them in the browser** with **Open the editor**. The editor has a tab per file, syntax
  highlighting, **Check** (compile without going live), and a test panel (below).
* **Put them in the zip**, in a top-level `_functions/` folder. The server compiles them as part
  of the deploy and never serves them. Only `.cs` and `.linq` files directly inside
  `_functions/` are compiled; the server ignores other files there, with a warning. The files in
  `_functions/` become the site's complete list of functions. If they do not compile, nothing is
  deployed. If `_functions/` has no `.cs` or `.linq` file, the site keeps the functions it had.
  The server refuses the whole zip when the person deploying it is not an administrator.

A complete function file:

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

`samples/functions/` has the same handlers in both formats. Each file has a `#if LINQPAD` block,
so it also runs as-is in LINQPad 9. `samples/functions/AllFeatures.cs` is the reference file: one
file that uses every attribute and every parameter type in the tables below, each with a comment
that says why. `samples/global-functions/` has global functions to upload under **Functions** in
the top bar: default security headers and a log line for every site, a `/whoami` handler, and a
job.

**Handlers and routes.** A handler is a `public static` method of a `public` class, with a route
attribute. The route attributes are ASP.NET's own: `[HttpGet("/path")]`, `[HttpPost("/path")]`,
`[HttpPut("/path")]`, `[HttpPatch("/path")]`, `[HttpDelete("/path")]`, or `[Route("/path")]` for
any HTTP method. Other `Http…` attributes, such as `[HttpHead("/path")]`, work the same way.

* The attribute must include the path. The server ignores a route attribute without one.
* The path is used as written: `[HttpPost("/ping")]` answers at `demo.example.com/ping`.
* `{name}` captures one path segment. `{name?}` makes that segment optional.
* Literal segments match without regard to case.
* When two routes match a path, the one with more literal segments wins. So `/draw/fixed` wins
  over `/draw/{text}`.
* A path that matches a route but not its HTTP method gets `405`.

**Parameters.** The server fills each parameter by its type. The same rules apply to
[middleware](#middleware), [hooks](#who-may-connect-and-join) and the methods in
[services and jobs](#services-and-jobs), with the differences shown:

| Parameter type | What it receives | Handler | Middleware | Hook | Job | Background service | `[ConfigureServices]` |
| -------------- | ---------------- | :-----: | :--------: | :--: | :-: | :----------------: | :-------------------: |
| `HttpContext` | the request (for a realtime hook, the request that made the connection) | ✅ | ✅ | ✅ | — | — | — |
| `HttpRequest`, `HttpResponse` | the request's parts | ✅ | ✅ | ✅ | — | — | — |
| `Func<Task>` | runs the rest of the request | — | ✅ required | — | — | — | — |
| `CancellationToken` | in a request: cancelled when the request is aborted; outside one: cancelled when the functions are replaced or removed, or the server stops | ✅ | ✅ | ✅ | ✅ | ✅ required | ✅ |
| `ISite` | the site: domain, data folder, variables, services, realtime, AI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `ISiteVariables` | the site's [variables](#variables), with helper methods | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `IReadOnlyDictionary<string, string>` | every [variable](#variables), secrets included, `${env:…}` replaced | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `DirectoryInfo` | the data folder (below) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `IRealtime` | the site's [realtime](#in-functions) side | ✅ | ✅ | ✅ | ✅ ¹ | ✅ ¹ | ✅ ¹ |
| `IAiChat` | the site's [AI](#chatting-from-functions) | ✅ | ✅ | ✅ | ✅ ¹ | ✅ ¹ | ✅ ¹ |
| `ILogger` (not generic) | a logger with the category `functions:<domain>` (`functions:global` for global functions) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `IServiceProvider` | the functions' [services](#services-and-jobs) | ✅ request scope | ✅ request scope | ✅ request scope | ✅ a scope for the run | ✅ the root provider | — |
| `IServiceCollection` | where to register services | — | — | — | — | — | ✅ required |
| any other type the functions' services hold | the service, such as `ILogger<T>`, `IHttpClientFactory`, `TimeProvider` or your own | ✅ | ✅ | ✅ | ✅ | ✅ | — |
| a simple value ² | a value converted from text | ✅ route, then query string | ✅ query string | — ³ | — | — | — |

¹ Global functions belong to no site outside a request. There, `IRealtime` and `IAiChat` throw an
`InvalidOperationException`. A job that needs them belongs in the site's own functions.

² `string`, a number type, `bool`, `char`, `decimal`, an enum, `Guid`, `DateTime`,
`DateTimeOffset`, `TimeSpan`, `DateOnly` or `TimeOnly`, or a nullable form of one. The server
matches it by the parameter's name. A missing value gives the parameter's default value, or
`null`, or the type's default (such as `0`). A value that does not convert gets a `400`.

³ The one exception: a `[RealtimeJoin]` hook must take `string group`, which receives the group
name.

A handler or middleware parameter of any other type receives `null` (or its type's default
value). The parameters of a job, a background service and a `[ConfigureServices]` method are
checked at upload (see [Services and jobs](#services-and-jobs)). A job or background service
parameter whose type is not among the services, and that has no default value, throws an
`InvalidOperationException` when the method runs.

**Return values.** A handler returns one of these, or a `Task` or `ValueTask` of one:

| Return type | Response |
| ----------- | -------- |
| `IResult` (`Results.Json`, `Results.File`, `Results.Text`, …) | whatever the result writes |
| `string` | the text, as `text/plain` |
| `int` | an empty response with that status code |
| nothing (`void`, `Task`, `ValueTask`, or `null`) | `204`, unless the handler already wrote to the response; then what it wrote stands |

The server refuses any other object. Serialise it yourself with `Results.Json(value, options)`,
using options held in a static field of your file, as in the example above. Options owned by the
server would keep every version of your code in memory for as long as the server runs.

**The data folder.** A `DirectoryInfo` parameter receives the site's data folder,
`/data/sites/<domain>/data/`. Put a SQLite database, uploads or JSON files there.

* It sits next to the releases, so the server never serves it.
* Deploys and rollbacks do not change it.
* Renaming the site moves it. Deleting the site deletes it.
* Global functions share `/data/config/functions-data/`.
* The server creates the folder the first time a function uses it.
* Helper code finds the path in `HttpContext.Items["StaticSiteHost.DataDirectory"]`.
* Editor tests use the same folder, so they see and change real data.

**StaticSiteHost.Abstractions.** This is a small NuGet package of types for functions. It
contains:

* `ISite`: the site a request is for, with its domain, data folder, variables, services, the
  pages connected to it (`site.Realtime`) and its AI (`site.Ai`). Take an `ISite site`
  parameter, or call `context.Site()` from helper code that has the `HttpContext`.
* `ISiteVariables`, `IRealtime` and `IAiChat`.
* `[Middleware]`, the attributes for [services and jobs](#services-and-jobs), and the hooks that
  decide who may use the site's [realtime](#who-may-connect-and-join) and [AI](#who-may-chat).
* Test fakes in `StaticSiteHost.Functions.Testing`: `FakeSite`, with a `FakeRealtime` and a
  `FakeAiChat`. They keep a LINQPad harness to a few lines.
  `samples/functions/SiteFunctions.cs` and `SiteFunctions.linq` show both.

To use it, reference it like any package, and add `using StaticSiteHost.Functions;`:

| File type | Reference |
| --------- | --------- |
| `.cs` | `#:package StaticSiteHost.Abstractions@*` (editors and `dotnet run` need a version; any version works) |
| `.linq` | `<NuGetReference>StaticSiteHost.Abstractions</NuGetReference>` |

The server never downloads this package. It compiles your files against its own copy, so it
ignores the version in the file, and the build reports the version it used. The package on
nuget.org gives your editor and LINQPad IntelliSense. Its README lists every type, property and
method.

* A file that uses a property or method the server's copy does not have fails to build. The
  compiler error names it.
* Functions compiled against a newer package than the server runs are not loaded, and answer
  nothing. This can happen after the server is rolled back to an older version. The Functions
  card says so. Deploy them again to compile them against the server's copy.

**Directives and packages.** A `.cs` file starts with directives, before any code:

| Directive | Effect |
| --------- | ------ |
| `#:sdk Microsoft.NET.Sdk.Web` | accepted; the server always references ASP.NET Core |
| `#:package Name@Version` | a NuGet package, restored on the server |
| `#:property Name=Value` | an MSBuild property for the project |
| `#:project …` | refused: the server cannot resolve a local project; use `#:package` |

A `.linq` file gives the same information in its XML header (`<NuGetReference>`, `<Namespace>`).

**Several files.** All the files compile together as one project, so they can share helper
classes.

* Give every class its own name. Two files that both declare `class Handlers` fail with
  `CS0101`, which names the file and line.
* The server merges the files' packages and imports.
* The server refuses two files that pin different versions of the same package. A reference
  without a version (`*`, as every `.linq` reference is) accepts the version another file pins.
* The server refuses two files that set the same property to different values.
* The server refuses two handlers with the same HTTP method and the same path shape.

**Imports.** Every file compiles with .NET's implicit usings and with LINQPad's default
namespace imports (`System`, `System.Collections`, `System.Collections.Generic`, `System.Data`,
`System.Diagnostics`, `System.IO`, `System.Linq`, `System.Linq.Expressions`, `System.Reflection`,
`System.Text`, `System.Text.RegularExpressions`, `System.Threading`, `System.Transactions`,
`System.Xml`, `System.Xml.Linq`, `System.Xml.XPath`). So a query that runs in LINQPad compiles
here. Imports added in LINQPad's own settings, rather than in the query, are not saved in the
file. Add them to the query.

**Order of a request.** For each request, the server runs [middleware](#middleware) first: the
global middleware, then the site's. Then it runs the site's handlers, then the global handlers,
then the site's files. A path that no handler matches is served as a file, as before. Functions
are behind a site's passcode, like everything else.

**Building.** When you upload functions, the server:

1. compiles the files with the .NET SDK (`dotnet publish`);
2. loads the result once, to find its routes, middleware, services, jobs and hooks;
3. only then makes the functions live.

A compile error names the file and line you wrote, and the functions already live keep
answering. The first build restores packages and can take a minute. Later builds take a few
seconds. The server runs one build at a time.

**The Functions card.** The site page's **Functions** card lists each route, and any
[middleware](#middleware), [services and jobs](#services-and-jobs) and hooks (for
[realtime](#who-may-connect-and-join) and [AI](#who-may-chat)). It shows the file and line each is
written on, which it reads from the build's debug information. Click an entry to open the editor
at that line. The × next to a file removes that file: the server compiles and deploys the other
files in one step. If another file still uses the removed one, the build fails and nothing
changes.

**Testing in the editor.** The test panel runs one request in memory, against the **Check**
build or the live one. Pick a route, fill in its parameters, query string, headers and body, and
click **Run**.

* DNS, the passcode and cross-origin rules do not apply to a test.
* An exception comes back with its stack trace.
* The request goes through the build's own middleware first, so a gate applies. Put what the gate
  asks for (a cookie or a token) in **Headers**.
* JSON is formatted. Text and HTML source are shown. HTML is also shown as a sandboxed preview
  that cannot run scripts. Images and PDFs are shown inline. Anything else, or anything sent as
  an attachment, is offered as a download.
* A test runs your real code. Whatever a handler writes or sends, it writes or sends. A test that
  publishes a realtime event reaches the pages open on the site, and a test that chats is billed
  like any other chat.

**Releases.** Functions belong to the live release. Deploying new content without `_functions/`
keeps the current functions. Rolling back brings back the functions that release had. You can
instead keep the functions that are running now: the choice appears next to **Make live**
whenever the two differ. Global functions have no history; an upload replaces them on every site.

**Packages that register process-wide hooks.** Some libraries (Microsoft.Data.Sqlite, for
example) subscribe to process-wide events or start background timers. These would keep an old
build in memory forever. When a build is replaced, the server removes those event handlers and
timers. It also clears the old build's database connection pools (it calls the static
`ClearAllPools` of every ADO.NET connection type in the build, such as `SqliteConnection`), so
the old build holds no database file open once it has retired. It logs a warning if a build is
still in memory five minutes later.

**Rules and limits.**

| Limit | Value |
| ----- | ----- |
| Files | 50 |
| Size of one file | 1 MB |
| Size of all files together | 4 MB |
| Entries in an uploaded `.zip` of function files | 500 |
| Time for one build | 5 minutes |
| Time for one editor test | 30 seconds |
| Request body of an editor test | 4 MB |
| Response body shown by an editor test | 25 MB (the rest is cut off) |
| Age of a **Check** build | the server deletes **Check** builds older than 2 hours when it makes a new one |

Functions need the `runtime-functions` Docker image (`latest`, the default), which includes the
.NET SDK. On the `slim` image, an upload is refused with "The .NET SDK is not available on the
server, so functions cannot be compiled."

**Errors.**

| What happens | What the caller sees | Where the reason is |
| ------------ | -------------------- | ------------------- |
| The files do not compile | the upload is refused; the API returns `400` with `error` and a `diagnostics` list (file and line of each problem) | the editor marks the lines |
| A handler throws | `500` with the text "The function failed. The details are in the server log." (if nothing was sent yet) | server log, with the site, the HTTP method and the path |
| A route or query value does not convert | `400` with the text `'<value>' is not a valid value for '<name>'.` | — |
| A handler returns an unsupported object | `500`, as for an exception | server log |
| The path matches, the HTTP method does not | `405` | — |
| The functions fail to load | with middleware: `503` for the whole site; without middleware: handlers answer nothing and files are served as usual | the Functions card, and the server log |

#### Attributes

Every attribute that functions use, in one place. The server finds each one by its name, so an
attribute of your own with the same name (and the same constructor arguments and properties)
works the same.

| Attribute | Method shape | What it does | Documented in |
| --------- | ------------ | ------------ | ------------- |
| `[HttpGet("/path")]`, `[HttpPost("/path")]`, `[HttpPut("/path")]`, `[HttpPatch("/path")]`, `[HttpDelete("/path")]` | `public static`, on a `public` class; returns `IResult`, `string`, `int` or nothing, or a `Task`/`ValueTask` of one | answers requests with that HTTP method at that path | [Functions](#functions) |
| `[Route("/path")]` | as above | answers requests with any HTTP method at that path | [Functions](#functions) |
| `[Middleware(Order = 0)]` | `public static`, on a `public` class; takes `Func<Task> next`; returns `Task`, `ValueTask` or `void` (not `async void`) | runs on every request to the site, before handlers and files | [Middleware](#middleware) |
| `[ConfigureServices]` | `public static void`, on a `public` class; takes `IServiceCollection`; not `async` | registers services once, as the functions load | [Services and jobs](#services-and-jobs) |
| `[BackgroundService]` | `public static`, on a `public` class; takes `CancellationToken`; returns `Task` or `ValueTask` | runs for as long as the functions are live | [Services and jobs](#services-and-jobs) |
| `[Schedule("cron", RunOnStart = false)]` | `public static`, on a `public` class; returns `Task`, `ValueTask` or `void` (not `async void`) | runs on a five-field cron schedule, in UTC | [Services and jobs](#services-and-jobs) |
| `[Every("interval", RunOnStart = false)]` | as for `[Schedule]` | runs at a fixed interval | [Services and jobs](#services-and-jobs) |
| `[RealtimeConnect]` | `public static`, on a `public` class; returns `string?`, `bool`, or a `Task`/`ValueTask` of either | decides whether a page may connect to the realtime hub, and who its user is | [Who may connect and join](#who-may-connect-and-join) |
| `[RealtimeJoin]` | `public static`, on a `public` class; takes `string group`; returns `bool`, `Task<bool>` or `ValueTask<bool>` | decides whether a page may join a group | [Who may connect and join](#who-may-connect-and-join) |
| `[AiAccess]` | `public static`, on a `public` class; returns `bool`, `Task<bool>` or `ValueTask<bool>` | decides whether a browser may chat at `/_host/ai/chat` | [Who may chat](#who-may-chat) |

The route attributes come from `Microsoft.AspNetCore.Mvc`. The others come from
`StaticSiteHost.Functions`.

* A method can have only one of these roles: middleware, `[ConfigureServices]`,
  `[BackgroundService]`, `[Schedule]`, `[Every]`, or a hook. The build fails for a method with
  two. A job may also have a route attribute, so it can also be run on request as a handler.
* The build fails for a generic method, or a method with a `ref` or `out` parameter, marked
  `[ConfigureServices]`, `[BackgroundService]`, `[Schedule]`, `[Every]` or a hook attribute.
* The functions can have at most one method with each hook attribute.

### Middleware

**What it is.** Middleware is a method in a function file that runs on every request to the site
before anything answers it. It decides whether the request goes further.

**When to use it.** Use middleware to put a sign-in gate over a whole section (static files
included), to tell the rest of the request who is signed in, to add a header to every response, or
to log each request.

**How to use it.**

```csharp
#:sdk Microsoft.NET.Sdk.Web
#:package StaticSiteHost.Abstractions@*

using Microsoft.AspNetCore.Http;
using StaticSiteHost.Functions;

public static class Gates
{
    [Middleware(Order = 10)]
    public static async Task Members(HttpContext context, Func<Task> next)
    {
        if (context.Request.Path.StartsWithSegments("/members") &&
            !context.Request.Cookies.ContainsKey("member"))
        {
            context.Response.Redirect("/join.html");
            return;
        }

        await next();
    }
}
```

A request to a site goes through these steps, in order:

1. the [passcode](#private-sites) gate
2. `/_host/`, which middleware never sees
3. the global functions' middleware
4. the site's middleware
5. the site's handlers
6. the global handlers
7. the site's files

**The method.** It is `public static`, on a `public` class, and marked `[Middleware]`. It takes a
`Func<Task> next`, and usually the `HttpContext`. It may also take any parameter a handler can
take (`ISite`, `DirectoryInfo`, a value from the query string, and so on; see the
[parameter table](#functions)). Middleware has no route, so its simple values come from the query
string only. It returns `Task`, `ValueTask` or nothing. `[Middleware]` comes from
StaticSiteHost.Abstractions, but the server finds it by name. So an attribute of your own named
`MiddlewareAttribute`, with an `int Order` property, works the same.

**`next`.** Calling `next()` runs everything after this middleware, once.

* Not calling it ends the request with whatever the method wrote: a status, a redirect, a body.
* Await it, and call it at most once. A second call throws, so the request is never answered
  twice.
* Code after `await next()` runs once the rest of the request has been answered. By then a file's
  headers have usually been sent, so set headers before you call `next()`.

**`Order`.** `Order` sets the order among the site's middleware, or among the global middleware.
Lower numbers run first, and so wrap the rest. The default is 0. Ties run in order of class name,
then method name. The Functions card lists the middleware in that order. Global middleware always
runs outside (before) the site's middleware, whatever the numbers.

**Data.** The site's middleware sees the site's [data folder](#functions) and `ISite`, as its
handlers do. The global middleware sees the shared folder, as the global handlers do.

**Signing a visitor in for the rest of the request.** A site that has its own accounts can tell
everything after it who the visitor is. A middleware reads the site's cookie and, when it belongs
to someone, sets `HttpContext.User`. Later middleware, the site's handlers and the global handlers
then see `context.User.Identity.IsAuthenticated`, the name (`User.Identity.Name`) and the roles
(`User.IsInRole("admin")`):

```csharp
[Middleware(Order = 0)] // lower than the middleware that reads it
public static async Task SignIn(HttpContext context, Func<Task> next)
{
    if (await Accounts.CurrentUserAsync(context) is { } user)   // your own sign-in code, reading your cookie
    {
        Claim[] claims = [new(ClaimTypes.Name, user.Name), new(ClaimTypes.Role, user.Role)];
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "MySite"));
    }

    await next();
}
```

* Put only framework types on it (`ClaimsPrincipal`, `ClaimsIdentity`, `Claim`, strings), so code
  that has never seen your classes can read it. The second argument of `ClaimsIdentity`, any
  non-empty name, is what makes it authenticated.
* To save the handlers a second look, keep your user object in `HttpContext.Items` as well.
  `Items` last for one request.
* A visitor who is not signed in keeps an empty `User`, with `IsAuthenticated` false.
* The [passcode](#private-sites) gate and the management UI's sign-in are separate from this.
  Entering a site's passcode does not set `User`, and signing in to the management UI does not
  sign anyone in to a site.
* The [realtime](#who-may-connect-and-join) and [AI](#who-may-chat) hooks run without middleware,
  so they never see this `User`. They must read the cookie themselves.

The [blog sample](samples/blog-site) does this in `_functions/Session.cs`.

**Testing.** The editor's test runs the request through the build's own middleware before its
routes, as a live request does. A test of the site's functions does not run the global
middleware, and a test of the global functions does not run the site's. If a gate refuses the
test, give it what it asks for (a cookie or a token) in **Headers**.

**Rules and limits.** The build fails, with a message that says what to change, for a
`[Middleware]` method that:

* is not `public static` on a `public` class;
* has no `Func<Task>` parameter;
* returns anything but `Task`, `ValueTask` or nothing;
* is `async void`.

A file with only middleware, and no routes, is fine.

**Errors.** An exception in middleware is handled as a handler's exception is. The server logs
it with the name of the functions it came from. The visitor gets a `500` if nothing was sent yet.
If the functions that hold the middleware cannot be loaded at all, the site answers `503` for
every request, rather than serve pages without the gate. See [Services and jobs](#services-and-jobs).

The [blog sample](samples/blog-site) uses middleware twice: `_functions/Session.cs` signs the
visitor in for the rest of the request, and `_functions/Gate.cs` keeps its studio for signed-in
writers.

### Services and jobs

**What it is.** A function file can register **services** for its handlers. It can also run code
that no request starts:

* a **background service** runs for as long as the functions are live;
* a **job** runs on a timetable.

The attributes are in StaticSiteHost.Abstractions. Like `[Middleware]`, the server finds them by
name.

**When to use it.** Use a service to share one object (a database connection pool, a cache, an
HTTP client) between handlers. Use a background service for a loop that polls something or
consumes a queue. Use a job for work at set times, such as a backup or a feed refresh.

**How to use it.**

```csharp
#:sdk Microsoft.NET.Sdk.Web
#:package StaticSiteHost.Abstractions@*

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions;

public sealed class Visits
{
    private int _count;
    public int Next() => Interlocked.Increment(ref _count);
}

public static class Setup
{
    [ConfigureServices]
    public static void Configure(IServiceCollection services) => services.AddSingleton<Visits>();
}

public static class Handlers
{
    [HttpGet("/visits")]
    public static string Count(Visits visits) => visits.Next().ToString();
}

public static class Work
{
    // 03:00 UTC every day.
    [Schedule("0 3 * * *")]
    public static void Backup(DirectoryInfo data) =>
        File.Copy(Path.Combine(data.FullName, "blog.db"),
                  Path.Combine(data.FullName, $"blog-{DateTime.UtcNow:yyyyMMdd}.db"), overwrite: true);

    // Every ten minutes, and once as soon as the functions load.
    [Every("10m", RunOnStart = true)]
    public static async Task RefreshFeed(IHttpClientFactory http, ISite site, CancellationToken stoppingToken)
    {
        var feed = await http.CreateClient().GetStringAsync("https://example.com/feed.xml", stoppingToken);
        await File.WriteAllTextAsync(Path.Combine(site.Data.FullName, "feed.xml"), feed, stoppingToken);
    }

    // For as long as the functions are live.
    [BackgroundService]
    public static async Task Heartbeat(CancellationToken stoppingToken, ILogger log)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            log.LogInformation("Still here");
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
```

**`[ConfigureServices]`.** Put it on a `public static void` method that takes an
`IServiceCollection`. The method registers services, once, as the functions load.

* You can have several such methods, in any files. They run in order of class name, then method
  name.
* Such a method may also take `ISite`, `ISiteVariables`, `IRealtime`, `IAiChat`, `DirectoryInfo`,
  the variables dictionary, `ILogger` and a `CancellationToken`. Nothing else, because the
  services it is adding do not exist yet.
* The collection already holds what the server provides: `ILoggerFactory`, `ILogger<T>`, a plain
  `ILogger`, `IDataProtectionProvider`, `IHttpClientFactory` (named and typed clients configured
  here work), `TimeProvider`, `ISite`, `ISiteVariables`, `IRealtime` and `IAiChat`.
* Once every method has run, the server builds the services and checks them. So a service that
  cannot be constructed fails then, not on a later request.
* `AddHostedService` does nothing here. Use `[BackgroundService]`.

**Using services.** Handlers, middleware, hooks, jobs and background services take any
registered service as a parameter. `IServiceProvider` or `ISite.Services` gives the services
themselves.

* In a request, the services are a scope made for the request and disposed when it ends. So
  scoped services behave as they do in ASP.NET.
* Each job run gets a scope of its own.
* A background service gets the root provider, which lives as long as the functions do. Create a
  scope for each piece of work that needs scoped services.
* Outside a request, `ISite` is the functions' own site. Its domain is `global` for the global
  functions. Each read of its variables sees the latest values. It also gives the data folder,
  the realtime side and the AI.
* The global functions belong to no site outside a request. There, `ISite.Realtime` and
  `ISite.Ai` throw, with a message that says why. So does a service or a job that takes an
  `IRealtime` or an `IAiChat`. A job that needs them belongs in the site's own functions.

**`[BackgroundService]`.** Put it on a `public static` method that returns `Task` or `ValueTask`
and takes a `CancellationToken`.

* It runs from when the functions load until they are replaced or removed, or the server stops.
  Each of these cancels the token.
* If it throws, the server logs the exception and starts it again after a pause. The pause starts
  at 1 second and doubles up to 1 minute. It goes back to 1 second after a run that lasted longer
  than the pause.
* If it returns, it is finished until the functions next load.

**`[Schedule("…")]`.** It runs a method on a five-field cron schedule, **in UTC**. The fields are
minute (0-59), hour (0-23), day of month (1-31), month (1-12) and day of week (0-7, where 0 and 7
are both Sunday).

* A field is `*`, a number, a range `a-b`, a list `a,b,c`, or a step `*/n` or `a-b/n`.
* The build refuses names such as `MON`, seconds, `@daily`, and the `L`, `W` and `#` extensions.
  It does not guess what they mean.
* When both day fields are restricted (neither starts with `*`), a day that matches either field
  counts, as in Vixie cron. `0 9 13 * 5` runs on the 13th of each month and on every Friday.

**`[Every("…")]`.** It runs a method at a fixed interval: `30s`, `5m`, `2h`, `1d`, or a `TimeSpan`
such as `01:30:00`. The interval must be from 10 seconds to 365 days. The first run is one
interval after the functions load. Each later run is timed from when the previous run was due,
so runs do not drift.

**Both kinds of job:**

* `RunOnStart = true` also runs the job once as soon as the functions load.
* A job returns `Task`, `ValueTask` or nothing.
* Runs never overlap. If a run comes due while the previous one is still running, the server
  skips it and logs a warning.
* Runs that came due while the server was down, or while the functions were being replaced, are
  not made up.
* An exception is logged with the site and the method, and counted on the Functions card. The
  next run goes ahead as usual.

**Loaded early.** Functions with a background service or a job cannot wait for a visitor. The
server loads them as soon as it is listening, and again right after anything that changes them:
a deploy, a rollback, a rename, or functions uploaded or removed. Other functions still load on
their first request. This costs memory: functions with jobs keep their assemblies and packages
loaded for as long as the server runs, even on a site nobody visits.

**Replacing functions.** Replacing functions never needs a restart. The new build goes live
first. Then the old build stops, in this order:

1. The server cancels its token. Its background services and jobs get 15 seconds to stop. After
   that, a warning names the ones that did not stop.
2. Requests still running on it get 30 seconds to finish.
3. The server disposes its services.
4. The server unloads it.

This happens in the background, and the deploy does not wait for it. Deleting or renaming a site
does wait, so it can take up to those 45 seconds. The reason: no old function may still be
writing to the site's data folder when the server deletes or moves it.

Once the old functions are unloaded, the `ISite` they were given throws instead of reaching the
site. So do the variables, realtime side and AI it gave out. So a job that ignores its token
cannot keep acting for a site that is gone, or for a new site that now has its domain.

**HTTP clients.** A build that made HTTP requests through `IHttpClientFactory` is unloaded the
same way. The server's clients keep their handlers, instead of replacing them every two minutes.
(The timer that replaces them would keep the old build in memory for those two minutes.) Pooled
connections are still closed after five minutes, so a changed DNS record is picked up. A client
of your own with `SetHandlerLifetime` brings back the timer, and that delay.

**The Functions card** shows:

* whether the functions are loaded, and since when, or why they failed;
* the services;
* each background service, with its state, restarts and last error;
* each job, with its schedule, last run and how long it took, next run, number of runs and
  failures, and last error.

**Testing.** A test in the editor runs the `[ConfigureServices]` methods, so a handler under test
has its services. A test never starts background services or jobs. Each test builds the services
again, so a singleton starts over on every run.

**Rules and limits.** These methods run without a request. The build fails, with a message that
names the method and says what to change, for a method that:

* takes `HttpContext`, `HttpRequest`, `HttpResponse`, `Func<Task>`, or a simple value a handler
  would read from the query string;
* has a schedule or interval the server cannot run;
* is a background service without a `CancellationToken`;
* is not `public static` on a `public` class.

A file with only jobs, and no routes, is fine.

Background services and jobs run inside the server like any function, with the same access, and
no one is waiting for them. A loop that never sleeps, or a job that fills the disk, slows or
stops every site. Keep them small, and watch the log after you deploy one.

**Errors.** When the functions fail to load (because a `[ConfigureServices]` method threw, or a
service cannot be built), they answer nothing. The Functions card says why.

* A site whose functions include [middleware](#middleware) answers `503` until the functions are
  fixed and deployed again. The body is "This site's functions could not be loaded, so it is not
  being served until they are. The details are in the server log." A missing gate would let
  everyone through, which is worse than a site that is visibly down.
* Global functions with middleware make every site answer `503` in the same way.
* Without middleware, the site's files are served as usual.

The build cannot catch this in advance, because none of your code runs until the functions go
live. But a test in the editor does run `[ConfigureServices]`, and shows the exception.

---

## Accounts

**What it is.** Accounts let people sign in to the management UI and create API keys. There are
two roles: **Member** and **Administrator**.

**How to use it.**

* An administrator creates each account under **Users**. New accounts are **Members**. An
  administrator can promote an account to **Administrator** at any time.
* The administrator chooses how to give the person their first credential:
  * **Private link** (the default). The account has no password. The link is shown once on the
    user management page. Copy it and send it to the person yourself. It works once, and expires
    after `SiteHosting:InviteLifetimeHours` (7 days by default).
  * **Temporary password.** The server generates it and shows it once. The user must change it at
    first sign-in, and cannot reach any other page until they do.
* **New link** on any user creates a fresh private link. This is also how to reset a password.
* Everyone can change their own password at any time under **Account**. A new password must have
  at least `SiteHosting:MinPasswordLength` characters (12).
* Disabling or deleting an account, or changing its password, changes the account's security
  stamp. This ends every open session for that user on their next request. Deleting an account
  also deletes its API keys, and the API keys of a disabled account stop working.

**What each role can do**

|                                              | Member | Administrator |
| -------------------------------------------- | :----: | :-----------: |
| Deploy to any domain                         |   ✅   |      ✅       |
| Roll back a release                          |   ✅   |      ✅       |
| Set or clear a site passcode                 |   ✅   |      ✅       |
| Set plain variables                          |   ✅   |      ✅       |
| Publish realtime events over the API         |   ✅   |      ✅       |
| Create API keys                              |   ✅   |      ✅       |
| Set secret variables and `${env:…}`          |   —    |      ✅       |
| Deploy or edit functions                     |   —    |      ✅       |
| Rename or delete a site                      |   —    |      ✅       |
| Manage users                                 |   —    |      ✅       |
| Manage AI providers and a site's AI settings |   —    |      ✅       |

**The first administrator.** The server creates the account named by `Bootstrap:Username` from
configuration on startup. It is created once. A password changed in the UI is not reset by a
restart. If the account is ever locked out, set `Bootstrap__ResetPasswordOnStartup=true` for one
start.

**Rules and limits.** The server slows down failed sign-ins and then locks them out. The
counters are in memory:

| Counted per | Failures | Within | Lockout |
| ----------- | -------- | ------ | ------- |
| user name | 8 | 15 minutes | 15 minutes |
| client address | 25 | 15 minutes | 30 minutes |

* From the second failure for a user name, the server waits before it answers each failed
  attempt: 1 second, then 2, then 4, then 8 seconds at most.
* A loopback address is not counted per address. Behind a proxy, set
  `SiteHosting:TrustForwardedHeaders`, so the server sees the real client address.

---

## Realtime

**What it is.** The server side of a site can tell the pages open on the site that something
has happened. The pages hear it at once. For example: a post was published, an order changed,
or a new version is live.

* Pages listen with `site.realtime`, from `/_host/site.js`.
* The site's functions publish with `IRealtime`.
* Anything else, such as a CI job, publishes through the [API](#api).

It uses SignalR. This app serves it on each site's own domain, so there is nothing else to run.

```
function or CI ──publish "post.published"──▶  abc.def.com/_host/realtime  ──▶  every open page,
                                                                               a group of them,
                                                                               one user's, or one
```

**When to use it.** Use it when a page must update without the visitor reloading: new content, a
changed status, or a new version of the site.

Realtime is on for every site. To turn it off for the whole server, set
`SiteHosting:RealtimeMaxConnectionsPerSite` to `0`.

### In the browser

**How to use it.**

```html
<script src="/_host/site.js"></script>
<script>
  site.realtime.on('post.published', function (post) {
    showToast('New: ' + post.title);
  });

  site.realtime.join('comments:' + postId);
  site.realtime.on('comment.added', function (comment, details) {
    // details is { event: 'comment.added', group: 'comments:…' }
  });
</script>
```

The first `on`, `join` or `connect()` opens the connection. It also loads the SignalR client
from `/_host/signalr.js` (about 48 KB, cached for a day). So a page that never uses realtime never
downloads it. `site.realtime` needs `Promise`.

A handler receives the event's payload, then `{ event, group }`. `group` is the group the event
was sent to, or `null`. If a handler throws, the error is reported on the console and the other
handlers still run.

| Name | What it does |
| ------ | ------------ |
| `on(event, handler)` | calls the handler for every event of that name; returns a function that removes it |
| `off(event, handler)` | removes the handler; without a handler, removes every handler for that event |
| `join(group)` | puts the page in a group, and keeps it there across reconnects; rejects with the server's reason |
| `leave(group)` | takes the page out of the group |
| `connect()`, `disconnect()` | open and close the connection yourself; `disconnect()` also forgets the groups |
| `state` | `disconnected`, `connecting`, `connected` or `reconnecting` |
| `connectionId` | this page's connection id while it is connected, so a function can send to this page alone |
| `onStateChange(handler)` | calls `handler(state, previous)` on every change; returns a function that stops it |

**Reconnecting.** If the connection drops, the page tries again at once, then after 2, 5, 10 and
30 seconds. When it is back, it joins its groups again, with a new `connectionId`. If the last
try fails too, it stays `disconnected` until the page calls `connect()` again. A connection that
the server closes on purpose (the site was deleted, renamed or given a new passcode) is not tried
again.

**Transport.** It uses WebSockets. Where a browser or a proxy between them does not allow
WebSockets, it falls back to server-sent events, then long polling.

**Rules and limits.** A page can listen, join and leave. It cannot publish, and it cannot put
another page in a group. Everything that decides who hears what happens on the server.

**Errors.** A failure rejects with an `Error` whose `status` is the HTTP status behind it, or 0,
as `site.ai.chat` does. For example: `401` on a private site the visitor has not unlocked, `403`
from a page on another site, `503` when the site has as many connections as it allows. The
message is the server's own sentence, where there is one.

### In functions

**How to use it.** A function reaches the site's pages with `IRealtime`, from
[StaticSiteHost.Abstractions](#functions). Take an `IRealtime` parameter, or use `ISite.Realtime`.

```csharp
private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

[HttpPut("/api/posts/{slug}")]
public static async Task<IResult> Save(HttpContext context, IRealtime realtime, string slug)
{
    // …save the post…
    await realtime.PublishToGroupAsync("journal", "post.changed", new { slug, action = "updated" }, Json);
    return Results.NoContent();
}
```

| Name | What it does |
| ------ | ------------ |
| `PublishAsync(eventName, payload, ct)` | sends an event to every page connected to the site |
| `PublishToGroupAsync(group, eventName, payload, ct)` | sends to the pages in a group |
| `PublishToUserAsync(user, eventName, payload, ct)` | sends to every connection of one user, in every tab |
| `PublishToConnectionAsync(connectionId, eventName, payload, ct)` | sends to one connection |
| `AddToGroupAsync(connectionId, group, ct)` | puts a connection in a group |
| `RemoveFromGroupAsync(connectionId, group, ct)` | takes a connection out of a group |
| `RemoveGroupAsync(group, ct)` | takes every connection out of a group |
| `DisconnectAsync(connectionId, ct)` | closes a connection; the page does not reconnect on its own |
| `ConnectionCount`, `Connections`, `Groups`, `Members(group)` | the connections now, each with its user and its groups |

A payload is a `JsonElement`, or any object with `JsonSerializerOptions` of your own. Hold the
options in a static field, for the same reason a handler does. The package README lists every
signature.

**Which site.** In a request, `IRealtime` is the realtime side of the site the request is for.
This is also true for global functions. In a job, a background service, or a service the
functions registered, it is the functions' own site. The global functions have no site outside a
request, so there it throws.

**Errors.** An event or group name that is not valid, or a payload over 256 KB, throws an
`ArgumentException`. A connection id that is not connected is ignored. `AddToGroupAsync` throws an
`InvalidOperationException` when the connection or the site is at its group limit.

### Who may connect and join

**What it is.** By default, every page may connect, with no user, and join any group. Two hooks
let the site decide instead. Each is a `public static` method in the site's functions, marked
with an attribute from StaticSiteHost.Abstractions. The server finds the attributes by name, like
`[Middleware]`.

**How to use it.**

```csharp
// Who the page belongs to: a string is its user, null refuses it. Or return bool, for yes or no alone.
[RealtimeConnect]
public static async Task<string?> Who(HttpContext context, DirectoryInfo data) =>
    (await Accounts.CurrentUserAsync(context, data))?.Name ?? "guest";

// Whether a page may join a group, which arrives in the parameter called group.
[RealtimeJoin]
public static async Task<bool> MayJoin(HttpContext context, DirectoryInfo data, string group) =>
    group == "news" || await Accounts.CurrentUserAsync(context, data) is not null;
```

(`Accounts.CurrentUserAsync` stands for your own sign-in code.)

* **`[RealtimeConnect]`** runs as each page connects. It gets the request that makes the
  connection, cookies included, so it recognises a signed-in visitor the way a handler does. It
  returns `string?` or `bool`, or a `Task` or `ValueTask` of either.
  * A string becomes the connection's user. `PublishToUserAsync` reaches that user in every tab
    they have open.
  * `null` or an empty string refuses the page. `false` refuses it. `true` lets it connect with no
    user.
  * A refused page is closed as soon as it connects, and does not try again.
* **`[RealtimeJoin]`** runs each time a page asks to join a group. This includes when a page that
  reconnected joins its groups again. It must take a `string group` parameter. It returns `bool`,
  or a `Task` or `ValueTask` of one. `false` rejects the page's `join` with "This site did not
  let this page join …". Leaving is always allowed. A function can still put any connection in
  any group.
* Either hook may take what a handler takes from a request (`HttpContext`, `ISite`,
  `DirectoryInfo`, services and so on; see the [parameter table](#functions)). It may not take a
  value from the query string, because on this request the query string belongs to SignalR.
* [Middleware](#middleware) does not run for the hub. The hook is the whole check.

**Rules and limits.**

* **Leave the `HttpContext` as it is.** It belongs to the connection, and lives as long as the
  connection does, through any number of deploys. The server restores its `Items` and `User`
  after each hook. It cannot undo anything registered on it. So register nothing: no
  `Response.OnCompleted`, no `Response.RegisterForDispose`, no feature.
* **Only the site's own functions are asked.** The global functions' hooks never decide for a
  site.
* The functions can have at most one of each hook. A second one, or one the server cannot call,
  fails the build with a message that names it. The Functions card lists the hooks under
  **Hooks**.

**Errors.** A hook that throws refuses the page, and the exception goes to the server log.
Functions that declare a hook but cannot be loaded also refuse every page. A gate that let people
in when it failed would let in exactly the people it was written to keep out.

The [blog sample](samples/blog-site) lets anyone follow its journal, and keeps its studio's group
for signed-in writers, in `_functions/Realtime.cs`.

### When a new version goes live

**What it is.** The server sends a `site.deployed` event to every page open on the site after
each of these:

* a deploy goes live;
* a rollback goes live;
* the site's functions are deployed or removed.

The payload:

```json
{ "release": "20260803-041429-5ab6", "functions": "20260803-052210-9c1f", "source": "deploy" }
```

| Field | Meaning |
| ----- | ------- |
| `release` | the release now live |
| `functions` | the id of the functions it runs, or `null` for none |
| `source` | `deploy`, `rollback` or `functions` |

**How to use it.** A page can offer the new version to a visitor who has the old one open. The
deploy job needs no extra step:

```js
site.realtime.on('site.deployed', function () {
  if (confirm('A new version of this site is up. Reload?')) location.reload();
});
```

**Errors.** The server sends the event separately from the deploy. If sending fails, the server
logs a warning and does nothing else. So the event never fails or slows a deploy.

### From anywhere else

**How to use it.** Any API key can publish to a site's pages:

```bash
curl -X POST -H "X-Api-Key: $SSH_KEY" -H "Content-Type: application/json" \
     -d '{"event":"notice","payload":"Back in five minutes."}' \
     https://deploy.example.com/api/v1/sites/abc.def.com/realtime/publish
```

```json
{ "ok": true, "domain": "abc.def.com", "delivered": 3 }
```

| Body field | Meaning |
| ---------- | ------- |
| `event` | required; the event name |
| `payload` | optional; any JSON, which the pages receive as it was sent |
| `group`, `user` or `connection` | optional; at most one; sends only to that group, user or connection. With none, every page gets the event |

`delivered` is the number of connections the event was sent to.

**Sites → the domain → Realtime** shows the same call, ready to paste. It also shows how many
pages are connected, and in which groups. `GET /api/v1/sites/{domain}/realtime` returns the same
numbers.

**Errors.** The server answers `400` with `{ "error": "…" }` when:

* the event name is missing or not valid;
* the body has more than one of `group`, `user` and `connection`;
* `group` is not a valid group name, or `user` or `connection` is empty;
* the payload is over 256 KB.

### What to know

**Each site is separate.** Group names belong to the site: `editors` on one site is not `editors`
on another. No function, API call or page can reach another site's connections, even with a
connection id from that site.

**Names.** Event names and group names are 1 to 64 characters. Each character is a letter, a
digit, `_`, `.`, `:` or `-`. Names are case-sensitive.

**Limits.**

| Limit | Default | Setting |
| ----- | ------- | ------- |
| Payload size | 256 KB of JSON | — (for anything bigger, send an id and let the page fetch the rest) |
| Pages connected to one site | 1000 | `SiteHosting:RealtimeMaxConnectionsPerSite` |
| Groups in use on one site | 1000 | `SiteHosting:RealtimeMaxGroupsPerSite` |
| Groups one page can be in | 100 | `SiteHosting:RealtimeMaxGroupsPerConnection` |
| Pages one client address can have connected to one site | 20 | `SiteHosting:RealtimeMaxConnectionsPerAddress` |
| New connections one client address can start to one site per minute | 60 | `SiteHosting:RealtimeNegotiationsPerMinute` |

* The per-address connection limit stops one client from taking every place on a site.
* The per-minute limit is counted before the site's `[RealtimeConnect]` hook runs. So a script
  cannot make the site run the hook as fast as it likes.
* An IPv6 client is counted by its /64 network, as the AI limiter counts it.

**Errors from the limits.**

| Situation | Answer |
| --------- | ------ |
| The site is full, or the address has its 20 connections | `503`, until a connection closes |
| The address has started too many connections this minute | `429`, with `Retry-After` |
| A join over either group limit | the `join` rejects, with a message that names the limit |

**Private sites.** A connection goes through the [passcode](#private-sites) like everything else
under `/_host/`. A visitor who has not entered it gets a `401`. Setting or replacing the
passcode, renaming the site, and deleting it each close every connection the site has open. Those
pages are told not to reconnect.

**Only the site's own pages can connect.** Anything else gets a `403`, on every request the
connection makes. Here is why. CORS does not stop a WebSocket, or a plain `GET` such as an
image's. Either one carries the visitor's cookies for this site, a passcode cookie included, even
from a sibling site under the same parent domain. With long polling, the server keeps the request
that opened the connection for as long as the connection lasts, and the hooks judge that request.
So the server checks each request in this order:

1. A request with a `Sec-Fetch-Site` header (every current browser sends one) must say
   `same-origin`, or `none` for a request the visitor made themselves.
2. Otherwise, a request with an `Origin` header must name the site.
3. Otherwise, the request must be a WebSocket (a browser never opens one without an `Origin`),
   or carry `X-Requested-With: XMLHttpRequest`. SignalR's JavaScript client sends that header,
   and a page on another site cannot add it without the server's permission.

A client that is not a browser can send the header itself. SignalR's JavaScript client under Node
sends it with every request except its server-sent events stream. So give it the header in
`headers`.

**One process.** Connections and groups live in the server's memory. A restart drops them. Pages
reconnect and join their groups again on their own. A group is the list of pages open now. It is
not a subscription stored anywhere.

---

## AI

**What it is.** A site can chat with an AI model through the server. A page streams an answer
into itself with `site.ai.chat`. A function asks with `IAiChat`. The server holds the API key, so
a page never sees it. Administrators decide which sites may use the key, and whether their
visitors may.

```
browser ──POST /_host/ai/chat──▶  abc.def.com  ──(key, model, system prompt)──▶  provider
        ◀──data: {"text":"…"}────              ◀──────────streamed answer───────
```

**When to use it.** Use it to add an assistant, a summary or a writing aid to a site, without
putting an API key in the page.

**How to use it.** An administrator adds a provider, then chooses it for the site. Then pages or
functions can chat.

### Providers

**How to use it.** Administrators add the services the server may call, under **AI** in the top
bar. Each provider has a name, a kind, a base URL, an API key and a default model:

| Kind        | Base URL                     | For |
| ----------- | ---------------------------- | --- |
| `openai`    | `https://api.openai.com/v1`  | OpenAI, and any service with the same chat completions API: a [LiteLLM](https://docs.litellm.ai/) proxy (`http://litellm:4000/v1`), [Ollama](https://ollama.com/) (`http://ollama:11434/v1`), [OpenRouter](https://openrouter.ai/) (`https://openrouter.ai/api/v1`) |
| `anthropic` | `https://api.anthropic.com`  | Anthropic's Messages API, with a model such as `claude-sonnet-5` |

**Rules and limits.**

* The server adds the endpoint to the base URL (`/chat/completions` or `/v1/messages`). It does
  not follow redirects. So give the exact address, including `https`.
* A base URL is an absolute `http://` or `https://` address, up to 2,048 characters, with no
  query string, fragment or user name.
* The server encrypts the key with its data-protection keys before it writes it to
  `config/ai-providers.json`. The key is never shown again, on the page or in the API. When you
  edit a provider, leaving the key box empty keeps the stored key. A provider that needs no key,
  such as a local Ollama, can have none.
* **Test** sends "Reply with the single word OK." It shows the answer, the model, the tokens it
  used and how long it took, or the provider's error.
* Deleting a provider leaves the sites that used it with no AI until they choose another.

A LiteLLM proxy is useful when you use several providers. One key here reaches Bedrock, Vertex,
Azure and others, and LiteLLM keeps budgets and logs per key.

### Per site

**How to use it.** Under **Sites → the domain → AI**, an administrator chooses:

* **Provider**, and a **model** if not the provider's default.
* **System prompt**, up to 8 KB. It goes first in every chat, from pages and from functions. A
  caller's own system prompt follows it as a new paragraph. So a caller can add instructions,
  but cannot remove the site's.
* **Let every visitor chat**, off by default. When it is off, only the site's functions can chat.
  When it is on, any browser that can load the site can call `/_host/ai/chat`. If the site's
  functions have an [`[AiAccess]` hook](#who-may-chat), the hook decides instead, and this
  setting is not used.

**Rules and limits.** Members see which provider a site uses, but cannot change it, because the
settings spend someone's money. The same applies in the [API](#api). The settings live in
`site.json`, so deploys and rollbacks do not change them.

### In the browser

**How to use it.**

```html
<script src="/_host/site.js"></script>
<p id="answer"></p>
<script>
  var answer = document.getElementById('answer');
  if (site.ai.enabled) {
    site.ai.chat('What should I read first?', {
      onText: function (text) { answer.textContent += text; }
    }).then(function (reply) {
      console.log(reply.model, reply.inputTokens + reply.outputTokens + ' tokens');
    }, function (error) {
      answer.textContent = error.status === 429 ? 'One moment, please.' : error.message;
    });
  }
</script>
```

**`site.ai.enabled`** is `true` when the site has a provider and either lets every visitor chat
or has an [`[AiAccess]` hook](#who-may-chat). The hook decides per request, so a visitor it
refuses also sees `true`, and gets a `403` when they chat.

**`site.ai.chat(messages, options)`**:

* `messages` is a string (one user message), or an array of
  `{ role: "user" | "assistant", content }`, oldest first. The page keeps the conversation and
  sends all of it each time.
* `options.onText(delta)` is called with each piece of the answer as it arrives.
* `options.system` adds instructions of the page's own.
* `options.signal` is an `AbortSignal` that stops the chat. A stopped chat rejects with an error
  named `AbortError` and `status` 0.
* `options.stream: false` waits for the whole answer.
* It resolves with `{ text, model, inputTokens, outputTokens, stopReason }`.
* It rejects with an `Error` whose `status` is the HTTP status, or 0 when there was no answer.
* It needs `fetch`. It reads the answer as it streams with `ReadableStream` and `TextDecoder`.
  Without those two, it waits for the whole answer, and `onText` gets it in one piece.

**The endpoint.** Underneath is a plain endpoint:

```bash
curl -N -H "Content-Type: application/json" \
     -d '{"messages":[{"role":"user","content":"Hello"}]}' \
     https://abc.def.com/_host/ai/chat
```

```
data: {"text":"Hello! "}

data: {"text":"How can I help?"}

data: {"done":true,"model":"claude-sonnet-5","inputTokens":24,"outputTokens":9,"stopReason":"end_turn"}
```

* The body is `{ "messages": [...], "stream": true, "system": "…" }`. `stream` defaults to `true`.
  `"stream": false` returns the same fields as one JSON object. `"system"` adds the page's
  instructions.
* The server chooses the answer's length. It stops at `SiteHosting:AiVisitorMaxTokens` tokens
  (1024).
* If the provider fails part way through a streamed answer, the stream ends with
  `data: {"error":"…"}`.

**Errors.** The server runs these checks in this order:

| Status | When |
| ------ | ---- |
| `405`  | the method is not `POST` |
| `404`  | the site has no provider, or the one it chose was deleted |
| `415`  | the body is not `application/json` |
| `413`  | the body is over `SiteHosting:AiMaxRequestBytes` (64 KB) |
| `400`  | the body is not a conversation: no messages, more than 64, a message with no text, or a role other than `user` or `assistant` |
| `429`  | this address has sent `SiteHosting:AiVisitorRequestsPerMinute` requests (20) to this site in the last minute; `Retry-After` says how long to wait |
| `403`  | this browser may not chat: the site's [`[AiAccess]` hook](#who-may-chat) said no, or the site has no hook and does not let visitors chat |
| `502`  | the provider failed; `error` says why, and never includes the key or the provider's address |

Each refusal has a JSON body `{ "error": "…" }`. The check of who may chat runs last. So the
site's hook, which is its own code, runs only for a request that passed every cheaper check and
was counted against the limiter. A request the hook refuses still counts.

**Rules and limits.**

* The limiter counts requests in a sliding minute per site and client address, in memory. It
  counts every caller, loopback included.
* An IPv6 client is counted by its /64 network, so a new address in the same network does not get
  a new allowance.
* Behind a proxy, set `TRUST_FORWARDED_HEADERS`. Otherwise every visitor has the proxy's address,
  and they all share one allowance.
* The endpoint accepts JSON only and answers no CORS preflight. So another site's pages cannot
  use this site's allowance from their visitors' browsers. Anyone with `curl` still can, and the
  limiter is there for that.
* On a private site, the endpoint is behind the [passcode](#private-sites), like the rest of
  `/_host/`.
* Chat answers are sent with `Cache-Control: no-store`.

### Chatting from functions

**How to use it.** A function chats with `IAiChat`, from [StaticSiteHost.Abstractions](#functions).
Take an `IAiChat` parameter, or use `ISite.Ai`. It is the same chat a page gets, with the site's
provider, model and system prompt. It works whether or not visitors may chat, and without the
page's limits. A function sets its own `MaxTokens`, and decides for itself who may make it spend.

```csharp
[HttpPost("/api/summary")]
public static async Task<IResult> Summary(IAiChat ai, HttpContext context)
{
    if (!ai.IsConfigured) return Results.Text("This site has no AI.", statusCode: 503);

    var answer = await ai.CompleteAsync(new AiChatRequest
    {
        Messages = [AiMessage.User("Summarise tonight's sky in one sentence.")],
        MaxTokens = 100
    }, context.RequestAborted);

    return Results.Text(answer.Text);
}
```

| Name | What it does |
| ------ | ------------ |
| `IsConfigured` | `true` when the site has a provider |
| `Model` | the model a request uses when it names none, or `null` without a provider |
| `CompleteAsync(request, ct)` | sends the conversation and returns the whole answer (`AiChatResponse`) |
| `StreamAsync(request, ct)` | returns the answer as it is written, as `AiChatChunk`s; the last chunk's `Final` holds the whole response |

`AiChatRequest` has `Messages` (required), `System`, `Model`, `MaxTokens` and `Temperature`. Only
`Messages` is needed. `MaxTokens` left `null` leaves the length to the provider; Anthropic
requires a limit and gets 1024.

**Which site.** In a request, `IAiChat` is the AI of the site the request is for. This is also
true for global functions. In a job or a background service, it is the functions' own site's AI.
The global functions have no site outside a request, so they cannot use it there.

**Errors.** Without a provider, both methods throw an `AiChatException`. So does a provider that
fails, with the provider's HTTP status in `StatusCode` when it gave one. A request with no
messages, or with an unknown role, throws an `ArgumentException`.

### Who may chat

**What it is.** "Let every visitor chat" is all or nothing. A site that wants only its own
signed-in users to chat uses an `[AiAccess]` hook. The hook is a `public static` method in the site's
functions, and it answers for each request to `/_host/ai/chat`.

**How to use it.**

```csharp
[AiAccess]
public static async Task<bool> Members(HttpContext context, DirectoryInfo data) =>
    await Accounts.CurrentUserAsync(context, data) is not null;
```

* When the site has the hook, its answer is final. `true` lets the chat go ahead. `false` gives a
  `403`. The site's "let every visitor chat" setting is not used, and the site page says so next
  to it. An administrator still chooses the provider, and without one there is no chat.
* It runs last of the [checks](#in-the-browser-1): after the body has been read and found to be
  a conversation, and after the request was counted against the limiter. So a stranger cannot
  make the site run the hook faster than the limiter allows. A request the hook refuses still
  counts.
* It returns `bool`, `Task<bool>` or `ValueTask<bool>`. It may take what a handler takes from a
  request, cookies included, but no value from the query string. The body has already been read,
  so the hook cannot read it.

**Rules and limits.**

* Like the realtime hooks, it must leave the `HttpContext` as it found it. It must register
  nothing on it (`Response.OnCompleted`, `Response.RegisterForDispose`, a feature).
* **Only the site's own functions are asked**, as with the
  [realtime hooks](#who-may-connect-and-join). The functions can have at most one `[AiAccess]`
  hook.
* Functions that call `ISite.Ai` are not affected by the hook. They decide for themselves.

**Errors.** A hook that throws refuses the chat (`403`), and the exception goes to the server log.
Functions that declare the hook but cannot be loaded also refuse every chat.

The [blog sample](samples/blog-site) keeps its studio's "Ask the sky" panel for signed-in writers
this way, in `_functions/Ai.cs`.

### What it costs

* Every chat is billed to the owner of the provider's key. This server keeps no running total.
  Put the spending limit on the key itself: a project budget at OpenAI, a workspace limit at
  Anthropic, or a budget in LiteLLM.
* Letting visitors chat lets anyone on the internet spend that money, 20 questions a minute from
  every address they have. Turn it on only for a site you watch. Otherwise let the site's
  [`[AiAccess]` hook](#who-may-chat), or a function of its own, decide who may ask.
* Visitors cannot choose the model or the answer's length. An answer to a browser stops at
  `SiteHosting:AiVisitorMaxTokens` tokens, 1024 unless you change it. `0` leaves the length to the
  provider: 1024 for Anthropic, and the model's own limit for an OpenAI-compatible provider. A
  function asks for as many tokens as it likes.
* The whole conversation is sent, and billed, on every turn. So a long chat costs more with each
  question.

---

## API

**What it is.** An HTTP API under `/api/v1` on the management host. An API key can do every
deploy the UI can do. A key can do exactly what its owner can do.

**How to use it.** Create a key under **Account → API keys**. You can give it a name and an
expiry of 1 to 3,650 days. The key is shown once. Revoke it on the same page.

Authenticate with either header:

```
X-Api-Key: sshost_ppoc7CxI1CNd_Vr6kVNxk6CsN0z6bdylnNhPwfIQxizQHSZqTRTEBhlo
Authorization: Bearer sshost_ppoc7CxI1CNd_Vr6kVNxk6CsN0z6bdylnNhPwfIQxizQHSZqTRTEBhlo
```

**Endpoints.** `{domain}` is the site's domain. The server takes the host name from what you
send (it drops a scheme, a path and a port) and uses it in lowercase.

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
| `GET`    | `/api/v1/sites/{domain}/variables`          | variables, their values and where each comes from |
| `PUT`    | `/api/v1/sites/{domain}/variables`          | replace every value the site sets |
| `DELETE` | `/api/v1/sites/{domain}/variables`          | remove them, back to the release defaults |
| `PUT`    | `/api/v1/sites/{domain}/variables/{name}`   | `{"value":"…"}` — set one   |
| `DELETE` | `/api/v1/sites/{domain}/variables/{name}`   | remove one, back to its default |
| `GET`    | `/api/v1/sites/{domain}/realtime`           | the pages connected to its [realtime](#realtime) hub, and their groups |
| `POST`   | `/api/v1/sites/{domain}/realtime/publish`   | `{"event":"…","payload":…}` — send an event to its open pages; `group`, `user` or `connection` narrows it |
| `GET`    | `/api/v1/sites/{domain}/ai`                 | the site's [AI](#ai) settings; administrators only |
| `PUT`    | `/api/v1/sites/{domain}/ai`                 | `{"providerId":"…","model":"…","systemPrompt":"…","allowVisitors":false}` — replace them; administrators only |
| `DELETE` | `/api/v1/sites/{domain}/ai`                 | remove them, so the site has no AI; administrators only |
| `GET`    | `/api/v1/ai/providers`                      | the AI providers, without their keys; administrators only |
| `POST`   | `/api/v1/sites/{domain}/rename`             | `{"domain":"…"}` — move to a new domain; administrators only |
| `GET`    | `/api/v1/sites/{domain}/functions`          | the live functions, their routes, middleware, jobs and hooks, and whether they are loaded |
| `GET`    | `/api/v1/sites/{domain}/functions/source`   | download the source as uploaded |
| `PUT`    | `/api/v1/sites/{domain}/functions`          | upload `.cs`/`.linq` files; administrators only |
| `DELETE` | `/api/v1/sites/{domain}/functions`          | stop running functions; administrators only |
| `DELETE` | `/api/v1/sites/{domain}/functions/files/{name}` | remove one file and redeploy the rest; administrators only |
| `GET`    | `/api/v1/functions`                         | the global functions and their status |
| `GET`    | `/api/v1/functions/source`                  | their source                |
| `PUT`    | `/api/v1/functions`                         | upload global functions; administrators only |
| `DELETE` | `/api/v1/functions`                         | remove them; administrators only |
| `DELETE` | `/api/v1/functions/files/{name}`            | remove one global file and redeploy the rest; administrators only |
| `DELETE` | `/api/v1/sites/{domain}`                    | administrators only         |

**Rollback.** A rollback brings back the functions the release had. Add `?functions=keep` to run
the current functions on it instead.

**Uploading functions.**

* Send the files as multipart form fields (`-F file=@Orders.cs -F file=@Reports.linq`, or
  `-F file=@functions.zip`). Any field names work.
* Or send one file as the raw request body, with its name in `X-File-Name`. Without the header,
  the name is `Functions.cs`.
* The extension chooses the `.cs` or `.linq` reader. Any file may be a `.zip` of such files.
* An upload merges into the live files unless you add `?mode=replace` (`?mode=merge` is the
  default). The response's `kept` field lists the live files a merge kept.
* A successful upload returns `ok`, `functions` (the functions record), `kept` and
  `diagnostics`.
* A refused upload returns `400` with `error` and a `diagnostics` list. Each diagnostic has the
  problem's file and line.
* The source downloads as the file itself, or as a zip when there are several files.

**Deploying.** Deploy takes the zip as a multipart form field named `file` (or else the first
file in the form):

```bash
curl -H "X-Api-Key: $SSH_KEY" \
     -F "file=@site.zip" \
     https://deploy.example.com/api/v1/sites/abc.def.com/deploy
```

Or it takes the zip as the raw request body:

```bash
curl -H "X-Api-Key: $SSH_KEY" \
     -H "Content-Type: application/zip" \
     -H "X-Archive-Name: site.zip" \
     --data-binary @site.zip \
     https://deploy.example.com/api/v1/sites/abc.def.com/deploy
```

You do not have to write either command yourself. **Sites → the domain → Deploy from the command
line** shows both: a multipart upload, and a command that zips a folder and streams it up. Both
already name that domain and this server, and each has a copy button.

Deploy creates the domain if it does not exist, and replaces its content if it does. A successful
deploy returns:

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

The response also has a `functions` field: the functions record when the zip had a `_functions/`
folder, otherwise `null`. The `release` object also lists the release's rules, its variables (by
name and flags only) and the id of its functions. A refused deploy returns `400` with `error`,
and `diagnostics` when the functions did not compile.

**Passcodes.** To put a site behind a passcode, and to remove it:

```bash
curl -X PUT -H "X-Api-Key: $SSH_KEY" -H "Content-Type: application/json" \
     -d '{"passcode":"quarterly-numbers-2026"}' \
     https://deploy.example.com/api/v1/sites/abc.def.com/passcode

curl -X DELETE -H "X-Api-Key: $SSH_KEY" \
     https://deploy.example.com/api/v1/sites/abc.def.com/passcode
```

`GET /api/v1/sites` and `GET /api/v1/sites/{domain}` report `passcodeProtected` and
`passcodeSetUtc`. The passcode itself is never returned; the server stores only its hash.

**Header rules and redirects.** [Header rules](#header-rules) are JSON over the API. `PUT`
replaces the site's whole list:

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

* `from`, `to` and `status` are the three columns of the text format. Add `"force": true` for the
  `!`.
* Both `GET`s return `siteRules`, and the read-only `releaseRules` from the current release's
  `_headers` or `_redirects` file.
* A rejected rule list returns `{ "error": "…", "errors": [ … ] }`, and nothing is saved.

**Variables.** [Variables](#variables) can be set one at a time, or all at once:

```bash
curl -X PUT -H "X-Api-Key: $SSH_KEY" -H "Content-Type: application/json" \
     -d '{"value":"help@example.com"}' \
     https://deploy.example.com/api/v1/sites/abc.def.com/variables/SUPPORT_EMAIL

curl -X PUT -H "X-Api-Key: $SSH_KEY" -H "Content-Type: application/json" \
     -d '{"variables":[{"name":"SUPPORT_EMAIL","value":"help@example.com"},
                       {"name":"STRIPE_KEY","value":"sk_live_…"}]}' \
     https://deploy.example.com/api/v1/sites/abc.def.com/variables
```

* Values are sent in plain text. The server encrypts secrets when they arrive.
* `"public": true` or `"secret": true` can go next to the value. For a name the release declares,
  the release's declaration also decides, as [Variables](#variables) explains.
* Setting one variable keeps its flags unless you send them. It keeps its value unless you send
  one. So a `PUT` without `"value"` changes only the flags, a secret's included. Only
  `"value": ""` empties it.
* In the whole list, a flag you leave out is `false`.
* A member's key cannot set, change or remove a secret, or a value that uses `${env:…}`. The
  server answers `400` and says so. A member's whole-list `PUT` or `DELETE` leaves every such
  value as it was.
* `GET` returns each variable's `value` and `default` (both left out for a secret), `source`
  (`site`, `default` or `none`) and `hasValue`. It also returns `missing` and
  `unsetEnvironment`, which list what needs attention. The `PUT` and `DELETE` calls return the
  same.
* `GET /api/v1/sites/{domain}` reports `variables` and `missingVariables` counts. It lists each
  release's variables by name and flags only, without their defaults.
* `DELETE /api/v1/sites/{domain}/variables/{name}` returns `404` when the site has no value of
  its own for that name.

**Errors.**

| Status | Meaning | Body |
| ------ | ------- | ---- |
| `400` | the request is not valid | `{ "error": "…" }`, sometimes with `errors` or `diagnostics` |
| `401` | the API key is missing, unknown, revoked or expired, or its owner is disabled | none |
| `403` | the key's owner does not have the role | none |
| `404` | no site is published at that domain (`{ "error": "No site is published at '…'." }`), or the item asked for does not exist | `{ "error": "…" }` |

---

## Configuration

Every setting is a normal ASP.NET Core configuration key. So you can use `appsettings.json`, an
environment variable with `__` as the separator (`SiteHosting__MaxUploadBytes`), or a compose
`environment:` entry. `ManagementHosts` is a list: set one entry with
`SiteHosting__ManagementHosts__0=deploy.example.com`.

| Key                                    | Default      | Meaning |
| -------------------------------------- | ------------ | ------- |
| `SiteHosting:DataRoot`                 | `/data`      | Root of the data volume |
| `SiteHosting:ManagementHosts`          | *(empty)*    | Host names that serve the UI/API |
| `SiteHosting:TrustForwardedHeaders`    | `false`      | Use `X-Forwarded-Host`/`-Proto`/`-For` |
| `SiteHosting:MaxUploadBytes`           | 512 MiB      | Largest accepted zip |
| `SiteHosting:MaxExtractedBytes`        | 2 GiB        | Largest accepted total after extraction |
| `SiteHosting:MaxEntryBytes`            | 512 MiB      | Largest accepted single file |
| `SiteHosting:MaxEntries`               | `50000`      | Largest accepted entry count |
| `SiteHosting:ReleasesToKeep`           | `3`          | Releases kept per site |
| `SiteHosting:AssetCacheSeconds`        | `3600`       | `max-age` for files that are not HTML |
| `SiteHosting:SpaFallbackForAllRequests`| `false`      | Fall back to `index.html` for assets too |
| `SiteHosting:InviteLifetimeHours`      | `168`        | Private link lifetime |
| `SiteHosting:MinPasswordLength`        | `12`         | Enforced when a password is set |
| `SiteHosting:MinPasscodeLength`        | `8`          | Enforced when a site passcode is set |
| `SiteHosting:PasscodeSessionHours`     | `168`        | How long a visitor stays unlocked |
| `SiteHosting:AiTimeoutSeconds`         | `120`        | How long an AI provider may take: a whole answer, or the silence between two pieces of a streamed one |
| `SiteHosting:AiVisitorRequestsPerMinute`| `20`        | Chats one address may start on one site in a minute; `0` turns the limit off |
| `SiteHosting:AiMaxRequestBytes`        | `65536`      | Largest conversation `/_host/ai/chat` accepts |
| `SiteHosting:AiVisitorMaxTokens`       | `1024`       | Longest answer, in tokens, a browser's chat gets; `0` leaves it to the provider. Functions set their own |
| `SiteHosting:RealtimeMaxConnectionsPerSite` | `1000`  | Pages one site may have connected to its [realtime](#realtime) hub at once; `0` turns realtime off |
| `SiteHosting:RealtimeMaxConnectionsPerAddress` | `20` | Pages one client address may have connected to one site's realtime hub at once (an IPv6 client by its /64); `0` turns the limit off |
| `SiteHosting:RealtimeNegotiationsPerMinute` | `60`    | Realtime connections one address may start to one site in a minute, counted before the site's `[RealtimeConnect]` hook runs; `0` turns the limit off |
| `SiteHosting:RealtimeMaxGroupsPerSite` | `1000`       | Realtime groups one site may have in use at once |
| `SiteHosting:RealtimeMaxGroupsPerConnection` | `100`  | Realtime groups one page may be in |
| `Bootstrap:Username`                   | `admin`      | The first administrator |
| `Bootstrap:Password`                   | *(empty)*    | Empty generates one on first run |
| `Bootstrap:DisplayName`                | `Administrator` | The first administrator's display name |
| `Bootstrap:MustChangePassword`         | `false`      | Require a change even with a configured password |
| `Bootstrap:ResetPasswordOnStartup`     | `false`      | Recovery switch — applies the configured password again on every start |

With Docker Compose, `.env` sets these names (see `.env.example`):

| `.env` name | Sets |
| ----------- | ---- |
| `IMAGE_TAG` | the image tag: `latest` or `slim` |
| `IMAGE_TARGET` | the build target for `docker compose up --build`: `runtime-functions` (matches `latest`) or `runtime` (matches `slim`) |
| `HTTP_PORT` | the host port (8080 by default; use 80 in production) |
| `MANAGEMENT_HOST` | `SiteHosting:ManagementHosts` (one host) |
| `TRUST_FORWARDED_HEADERS` | `SiteHosting:TrustForwardedHeaders` |
| `ADMIN_USERNAME`, `ADMIN_PASSWORD`, `ADMIN_DISPLAY_NAME` | `Bootstrap:Username`, `Bootstrap:Password`, `Bootstrap:DisplayName` |
| `ADMIN_RESET_PASSWORD_ON_STARTUP` | `Bootstrap:ResetPasswordOnStartup` |
| `MAX_UPLOAD_BYTES` | `SiteHosting:MaxUploadBytes` |
| `RELEASES_TO_KEEP` | `SiteHosting:ReleasesToKeep` |

### On disk

```
/data
├── config/
│   ├── users.json                 accounts (PBKDF2-hashed passwords, hashed invite tokens)
│   ├── apikeys.json               API keys (hashed secrets)
│   ├── ai-providers.json          AI providers (keys encrypted)
│   ├── audit.log                  JSON lines: deploys, logins, user and key changes
│   ├── keys/                      data-protection keys: sessions, secret variables and AI keys need them
│   ├── functions.json             which global functions are live
│   ├── functions/<id>/            each upload of the global functions: src/ as uploaded, bin/ as built
│   ├── functions-data/            the global functions' data folder
│   └── bootstrap-password.txt     written only when a password was generated
├── sites/
│   └── abc.def.com/
│       ├── site.json              which release is live, plus history, rules, variable values
│       │                          (secrets encrypted), AI settings and any passcode hash
│       ├── data/                  functions' own data (SQLite, uploads…); never served
│       ├── releases/
│       │   └── 20260803-041429-5ab6/   the files being served
│       └── functions/
│           └── 20260803-052210-9c1f/   one version of the site's functions: src/ as uploaded, bin/ as built
└── tmp/                           temporary space for uploads, cleared at startup
```

Back up `/data`, and you have backed up everything.

---

## Production notes

* **TLS.** The app speaks plain HTTP on port 8080. Terminate TLS at a reverse proxy (Caddy handles
  wildcard certificates well for this kind of workload), and publish on ports 80 and 443.
* **Forwarded headers.** Set `TRUST_FORWARDED_HEADERS=true` only when a proxy you control is in
  front of the app. Routing uses the `Host` header, so a forged `X-Forwarded-Host` would let a
  caller choose which site answers.
* **Single instance.** Users, keys and site metadata are JSON files owned by one process. Give
  the server more resources; do not run several copies. The passcode and sign-in failure
  counters are in memory, so they reset on restart.
* **Caching in front.** Responses from a passcode-protected site are marked `private`, which a
  well-behaved shared cache respects. If your proxy is set to cache everything regardless,
  exclude those domains. The passcode gate runs in this app, not in the cache.
* **WebSockets.** [Realtime](#realtime) needs WebSockets to reach the app through the proxy.
  Caddy passes them on by default. nginx needs `proxy_http_version 1.1;`,
  `proxy_set_header Upgrade $http_upgrade;` and `proxy_set_header Connection $connection_upgrade;`
  (with a `map` of `$http_upgrade` to `upgrade`, or `close` when it is empty). Without them,
  SignalR falls back to server-sent events, then long polling. These work, but use more requests
  and arrive later.
* **The data volume.** `docker-compose.yml` uses a named volume, so the container's non-root user
  owns it. If you use a bind mount instead, `chown` the host directory to UID 1654 first.
* **Image size.** The default image (`latest`, ~930 MB) includes the .NET SDK, so it can compile
  uploaded functions. The NuGet cache is on the volume at `/data/nuget`. The `slim` image
  (~230 MB) serves static sites only. A function build briefly uses 0.5–1 GB of memory.

---

## Local development

```bash
dotnet run --project src/StaticSiteHost
```

It starts on <http://localhost:8080>, the same port as the container, so every URL in this
README works either way. Stop the container first if it is running, or the port is already in
use.

The Development profile writes to `src/StaticSiteHost/.data`. It treats `localhost` and
`127.0.0.1` as management hosts, and creates the account `admin` with the password
`development-password`.

**Hosting sites locally.** Publish them under `.localhost` names, such as `blog.localhost` and
`docs.localhost`. Then open <http://blog.localhost:8080>. Browsers resolve every `*.localhost`
name to your own machine, so you need no hosts file entries and no proxy. Each site gets its own
origin, as it would in production. The server tells sites apart by host name only, so
`localhost:8010` becomes plain `localhost`, which is the management UI. Command-line tools older
than curl 7.85 do not resolve `*.localhost` on their own. Use
`curl --resolve blog.localhost:8080:127.0.0.1 http://blog.localhost:8080/`.

**Deploying the sample site from the command line.** Run this from the repo root, with a key from
**Account → API keys**:

```bash
export SSH_KEY=sshost_...

curl -H "X-Api-Key: $SSH_KEY" \
     -F "file=@sample-site.zip" \
     http://localhost:8080/api/v1/sites/abc.def.com/deploy
```

Sites are chosen by the `Host` header. To open one without changing DNS, either add
`abc.def.com` to `/etc/hosts` with the address `127.0.0.1`, or send the header yourself:

```bash
curl -H "Host: abc.def.com" http://localhost:8080/blog/hello-world
```

**Tests.** The unit tests cover the parts that are pure logic, such as the cron and interval
parsers, realtime names and the hook signature rules. The rest is tested against a running
instance. `.github/workflows/build.yml` runs the build and the tests on every push.

```bash
dotnet test tests/StaticSiteHost.Tests
```

**Source layout.** The whole app is in `src/StaticSiteHost`. The package that functions compile
against is in `src/StaticSiteHost.Abstractions`.

| Path             | What is there                                                |
| ---------------- | ------------------------------------------------------------ |
| `Serving/`       | The `Host` header switch, path resolution, static file serving |
| `Services/`      | JSON stores, zip extraction and release management, functions, realtime and AI |
| `Hubs/`          | The realtime hub every site's pages connect to                |
| `Security/`      | Password hashing, tokens, API key authentication, throttling  |
| `Endpoints/`     | The `/api/v1` endpoints                                       |
| `Pages/`         | The management UI (Razor Pages)                               |

### Publishing images

Pushing a version tag publishes both images to Docker Hub and to GitHub's registry, built for
amd64 and arm64 by `.github/workflows/publish-image.yml`:

```bash
git tag v1.2.3 && git push origin v1.2.3
```

Only plain `vMAJOR.MINOR.PATCH` tags publish. The workflow needs two repository secrets:
`DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` (a Docker Hub access token).

The same tags publish the `StaticSiteHost.Abstractions` package to nuget.org, with the same
version as the images, through `.github/workflows/publish-package.yml`. That needs a
`NUGET_API_KEY` secret: an API key from nuget.org that may push the package. Reserve the package
id on nuget.org before the first tag.

---

## Glossary

| Term | Meaning |
| ---- | ------- |
| **site** | Everything published at one domain: its releases, rules, variables, passcode, functions, data folder and AI settings. |
| **release** | One uploaded zip, unpacked into its own directory. One release is live at a time. |
| **functions** | A site's C# code (`.cs` and `.linq` files), compiled by the server. **Global functions** answer on every site. |
| **handler** | A `public static` method in the functions with a route attribute, such as `[HttpGet("/path")]`. It answers requests. |
| **middleware** | A `[Middleware]` method that runs before handlers and files on every request to the site. |
| **service** | An object registered in a `[ConfigureServices]` method, which handlers and jobs take as a parameter. |
| **background service** | A `[BackgroundService]` method that runs for as long as the functions are live. |
| **job** | A `[Schedule]` or `[Every]` method that runs on a timetable. |
| **hook** | A method that decides who may do something from a browser: `[RealtimeConnect]`, `[RealtimeJoin]` or `[AiAccess]`. |
| **variable** | A named value for a site. A release declares it in `_variables.json`, and its value is set on the site. |
| **secret** | A variable whose value is stored encrypted and never shown again. Only administrators may set it. |
| **provider** | An AI service (OpenAI-compatible or Anthropic) that an administrator adds under **AI**. |
| **group** | A named list of realtime connections on one site, which an event can be sent to. |
| **connection** | One page's link to the site's realtime hub. It has an id, an optional user, and its groups. |
| **page** | An HTML page of the site, open in a visitor's browser. |
| **visitor** | Anyone who opens a site in a browser. |
| **member** | A signed-in user who can deploy and manage sites, but not functions, secrets, users or AI. |
| **administrator** | A signed-in user who can do everything, including functions, secrets, users and AI. |
| **API key** | A key that lets a script or CI job call the [API](#api) with its owner's rights. |
