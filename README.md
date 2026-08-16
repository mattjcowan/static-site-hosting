# Static Site Host

Drop a zip, pick a domain, and it is live. A single ASP.NET Core app that hosts any
number of static sites out of one data volume, with accounts, invitation links, optional
[per-site passcodes](#private-sites) and an API for CI. No database — users, keys and site
metadata are JSON files on the volume.

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

The sources live in `samples/demo-site/`. To rebuild the archive after editing them:

```bash
cd samples/demo-site && zip -qr ../../sample-site.zip . -x '.*'
```

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

**Assets are treated differently on purpose.** A request for a path with a non-HTML
extension (`/assets/app.js`) that the browser did not ask HTML for returns `404` rather
than the index page — serving HTML in place of a missing script breaks module loading
in ways that are hard to debug. Set `SiteHosting:SpaFallbackForAllRequests=true` if you
want the walk-up applied to everything.

HTML is served `no-cache`; other assets get `public, max-age=3600`
(`SiteHosting:AssetCacheSeconds`). ETags, conditional requests and range requests all work.

### What goes in the zip

* **A single folder at the root is unwrapped.** `dist/index.html`, `dist/assets/…`
  publishes as `/index.html`, `/assets/…`. If anything sits at the root of the archive,
  nothing is unwrapped.
* Path traversal, absolute paths and unportable filenames are dropped.
* `.git`, `.svn`, `.hg`, `.env*`, `__MACOSX`, `.DS_Store`, `Thumbs.db` and `.htpasswd`
  are dropped. Other dot-paths are kept, so `/.well-known/` works.
* The response tells you how many entries were skipped and whether a root `index.html`
  was found.

Each upload becomes a new immutable release directory; the site's pointer is flipped
only once extraction succeeds, so a failed or half-finished upload never reaches a
visitor. The last three releases stay on disk (`SiteHosting:ReleasesToKeep`) and any of
them can be made live again from the site's detail page.

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
| `DELETE` | `/api/v1/sites/{domain}`                    | administrators only         |

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
│   └── bootstrap-password.txt     written only when a password was generated
├── sites/
│   └── abc.def.com/
│       ├── site.json              which release is live, plus history and any passcode hash
│       └── releases/
│           └── 20260803-041429-5ab6/   the files being served
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
