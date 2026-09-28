# Night Sky Field Notes: a demo site with functions

Night Sky Field Notes is a sample site for Static Site Host. It is an astronomy journal: static
pages, plus C# functions that the host compiles when the zip is deployed.

The functions keep the blog's sign-in, its accounts and its journal in SQLite (through Dapper), in
the site's data folder. Posts are written in Markdown and turned into HTML with Markdig. The
sample uses most of what functions can do: handlers, middleware, a service, jobs, a background
service, the realtime hub, AI, and the hooks that decide who may use realtime and AI.

The blog's accounts are its own. They are stored in the blog's database, and they are separate
from the accounts on the host.

## What an administrator sets up first

1. **Build the zip.** From the root of the repo, run:

   ```bash
   scripts/build-samples.sh blog-site
   ```

   This writes `blog-site.zip` at the root of the repo. This README is not included in the zip.

2. **Deploy it as an administrator.** The host refuses a zip with a `_functions/` folder from a
   member. The server must run the `latest` image, which can compile functions.

3. **Sign in to the blog.** The first request to the site creates the blog's `admin` account with
   a generated password. The password is printed in the server log and saved to
   `initial-admin-password.txt` in the site's data folder. Sign in at `/login`. The blog asks you
   to change the password at first sign-in, and then deletes that file.

4. **Turn on "Ask the sky" and Suggest (optional).** The studio's AI panel and its **Suggest**
   button need two steps on the host, both by an administrator:
   1. Add a provider under **AI** in the top bar.
   2. Choose it for the site under **Sites → the domain → AI**.

   Leave **Let every visitor chat** off. The site's `[AiAccess]` hook decides who may chat (see
   [Ask the sky](#ask-the-sky)).

5. **Rename the journal (optional).** Set `SITE_NAME` and `SITE_TAGLINE` under
   **Sites → the domain → Variables** (see [Variables](#variables)).

## The files

### Pages and scripts

Every page loads `/_host/site.js` and then `js/sky.js`.

| File | What it does |
| ---- | ------------ |
| `index.html`, `js/home.js` | The home page: tonight's Moon phase, what the sky is good for, the season's constellation, and the latest posts. |
| `journal.html`, `js/journal.js` | The journal: the list of posts, with a filter by topic. Visitors see published posts; signed-in writers also see drafts. It follows the `journal` realtime group (below), and shows how many pages are reading it. |
| `post.html`, `js/post.js` | One journal entry. Its address is `/journal/<slug>`; `_redirects` answers that address with this page. |
| `guide.html`, `js/guide.js` | Stargazing 101, with a 20-minute dark-adaptation timer. |
| `about.html` | About the journal. |
| `login.html`, `js/login.js` | Sign in, and choose a new password when someone else set the current one. |
| `studio.html`, `js/studio.js` | The studio, for signed-in writers: write posts (with a **Suggest** button for the summary), change your password, and use the "Ask the sky" panel. The blog's administrators also manage accounts, and see who is online, here. It follows the `studio` realtime group, and shows a note when an administrator changes your account. |
| `404.html` | The "Lost in space" page. |
| `js/sky.js` | Shared by every page: puts the variables in place, the Moon and sky calculations, the red-light mode, and the calls to the blog's API. |
| `css/sky.css`, `favicon.svg` | The style sheet and the icon. |

The pages link to each other without `.html`: `/journal`, `/guide`, `/about`, `/login`, `/studio`.
The host answers `/journal` with `journal.html` by itself, and the address bar keeps `/journal`.
`_redirects` sends the old `.html` addresses on to the clean ones.

`js/sky.js` sends an `X-Night-Sky` header with every call that changes something. The functions
refuse such a call without it. A page on another site cannot add that header without a CORS
preflight, which nothing here allows.

### Function files (`_functions/`)

| File | What it does |
| ---- | ------------ |
| `Blog.cs` | Shared code: password hashing, the session cookie, the check the other handlers use to require a signed-in writer, and `Blog.OpenAsync`, a database connection for code that has only the request. |
| `Session.cs` | Middleware that reads the session cookie on every request and tells the rest of the request who is signed in, in `HttpContext.User` (see [Who is signed in](#who-is-signed-in)). |
| `Database.cs` | The `BlogDatabase` service: where `blog.db` is, its connections, and its tables and first `admin` account, created the first time it is used (see [The database service](#the-database-service)). |
| `Auth.cs` | `/api/auth/*`: sign in, sign out, who am I, and changing your own password. |
| `Users.cs` | `/api/users`: accounts, for the blog's administrators. It tells a person's open pages when an administrator changes their account (see [Realtime](#realtime)). |
| `Posts.cs` | `/api/posts`: the journal, `/api/preview` for the Markdown preview, and `/api/posts/{slug}/summary` for the **Suggest** button (see [Ask the sky](#ask-the-sky)). It announces every save or delete to the pages open on the site (see [Realtime](#realtime)). |
| `StarterPosts.cs` | Three posts to start the journal with, written once when the database is created. |
| `Gate.cs` | Middleware that sends visitors who are not signed in away from the studio (see [The studio gate](#the-studio-gate)). |
| `Realtime.cs` | The realtime hooks: who a connection belongs to, and which groups a page may join. Also the count of readers, and `/api/online`. |
| `Ai.cs` | The AI hook: who may use the studio's "Ask the sky" panel. |
| `Housekeeping.cs` | Two jobs: one tidies the database every hour, one backs it up every night (see [The jobs](#the-jobs)). |

### Configuration files

| File | What it does |
| ---- | ------------ |
| `_variables.json` | Declares the two public variables `SITE_NAME` and `SITE_TAGLINE`. |
| `_headers` | Sets `Content-Security-Policy`, `X-Frame-Options`, `Referrer-Policy` and `Permissions-Policy` on every path, and `Cache-Control: no-cache` on every `.html` file. |
| `_redirects` | Answers `/journal/*` with `/post.html` (a rewrite, `200`), sends the old `.html` addresses to the clean ones (`/studio.html` to `/studio`, `/index.html` to `/`, with a `301`), and answers every other path that has no file with `/404.html` and a `404` status. |

## Variables

`_variables.json` declares two public variables. Every page reads them from `/_host/site.js`, and
`js/sky.js` puts them in place:

| Variable | Default | Where it shows |
| -------- | ------- | -------------- |
| `SITE_NAME` | Night Sky Field Notes | the header, the footer and every page's title |
| `SITE_TAGLINE` | A dark-sky observing journal | above the home page's headline |

Set them under **Sites → the domain → Variables** to rename the journal without deploying it
again. The pages keep the defaults if `site.js` does not load.

## The database service

`Database.cs` registers one service, in a `[ConfigureServices]` method:
`services.AddSingleton<BlogDatabase>()`. The host makes one, the first time something asks for it,
and hands it the site, so it knows the data folder. It opens connections to `blog.db`. The first
time anyone opens one, it creates the tables, the first `admin` account and the starter posts.

A handler asks for it by taking a parameter of that type, as `Posts.cs` does:

```csharp
[HttpGet("/api/posts")]
public static async Task<IResult> List(HttpContext context, BlogDatabase database, string? tag)
{
    await using var db = await database.OpenAsync(context.RequestAborted);
    // …
}
```

Code that has only the request, such as `Blog.RequireAsync`, the session middleware and the hooks,
calls `Blog.OpenAsync(context)`. It finds the same service through `context.Site().Services`.

## Who is signed in

`Session.cs` has one `[Middleware(Order = 0)]` method. It runs first, on every request to the site.
When the request carries the `nightsky_session` cookie, it asks the database once who that is. For
a writer, it then sets `HttpContext.User` to a principal with these claims:

| Claim | Value |
| ----- | ----- |
| `ClaimTypes.NameIdentifier` | the account's id |
| `ClaimTypes.Name` | the username, so `User.Identity.Name` returns it |
| `ClaimTypes.Role` | `admin` or `editor`, so `User.IsInRole("admin")` works |
| `display_name` | the display name |
| `must_change_password` | `true` or `false` |

Everything after it in the request sees who is signed in: the studio gate, the blog's handlers, the
host's global handlers and any later middleware. A global function such as `GET /api/ping` that
answers `context.User.Identity.IsAuthenticated` says `true` on the blog for a signed-in writer. A
visitor who is not signed in keeps an empty `User`.

It also keeps the writer in `HttpContext.Items`, under `NightSky.User`. `Blog.CurrentUserAsync`
answers from there, so a handler's `Blog.RequireAsync` does not ask the database again. `Items` last
for one request only.

The hooks in `Realtime.cs` and `Ai.cs` run without middleware. They read the cookie themselves,
through the same `Blog.CurrentUserAsync`.

## The studio gate

`Gate.cs` has one `[Middleware(Order = 10)]` method. It runs on every request to the site, after
`Session.cs`. For a request to the studio (`/studio`, or the file's own address `/studio.html`),
it checks `context.User.Identity.IsAuthenticated`. A visitor who is not signed in is redirected to
`/login?return=/studio`. After sign-in, the login page sends the visitor back to the studio. Every
other request goes on unchanged. The gate never opens the database itself.

The studio's handlers also check the session themselves. So the gate keeps the page from people
who cannot use it; it does not protect the data.

## Realtime

The pages listen with `site.realtime` from `/_host/site.js`:

* **The journal** (`/journal`) joins the `journal` group. When a post goes up, changes or is
  taken down, it shows a note in the corner, such as "A post was updated: *title*. Refresh to see
  it."
* **The studio** joins the `studio` group. It reloads its list of posts when another writer saves
  or deletes one.
* **How many are reading.** A `[BackgroundService]` in `Realtime.cs` counts the pages in the
  `journal` group every 30 seconds. When the number has changed, it sends `readers.online` with
  `{ count }` to that group. The journal shows it as "3 reading now".
* **Changes to your account.** When an administrator changes a writer's role, resets their
  password or removes their account, `Users.cs` sends `account.changed` with `{ change, message }`
  to that writer alone, with `PublishToUserAsync`. Every studio tab they have open shows the
  message in the corner. `change` is `role`, `password` or `deleted`.
* **Who is online.** `GET /api/online` lists every connection: its id, its user and its groups.
  Only signed-in writers may call it. The studio's **Users** tab shows it under **Online now**.

`Posts.cs` sends `post.changed` with `{ slug, title, action }`. `action` is `created`, `updated`,
`published`, `unpublished` or `deleted`. It sends the event:

* to the `studio` group for every post;
* to the `journal` group only for posts readers can see, or could see until this change. So a
  draft's title never reaches readers.

If sending fails, the post is still saved, and the function logs a warning.

`Realtime.cs` has the two hooks the host calls as pages connect and join:

* `[RealtimeConnect]` lets every page connect. A signed-in writer's connection gets their user
  name, and every other connection gets `guest`. So a function can reach one writer's open tabs
  with `PublishToUserAsync`, as `Users.cs` does.
* `[RealtimeJoin]` lets anyone join `journal`, only signed-in writers join `studio`, and nobody
  join any other group.

## Ask the sky

The studio has an "Ask the sky" panel. A writer types a question, and `site.ai.chat` streams the
answer into the page. The page adds instructions of its own: "You help an astronomy blogger write
field notes. Be brief."

* The panel shows only when `site.ai.enabled` is true. That needs a provider chosen for the site
  (see [What an administrator sets up first](#what-an-administrator-sets-up-first)).
* `Ai.cs` has an `[AiAccess]` hook. It lets a signed-in writer chat, unless the writer still has
  to change their password. Everyone else gets a `403`. So strangers cannot spend the provider's
  key through the site, and **Let every visitor chat** can stay off.
* When the host answers `429` (too many questions from one address), the panel says "That is a
  lot of questions. Try again in a minute."

The editor's **Suggest** button, next to the summary, asks for a summary of the post. It calls
`POST /api/posts/{slug}/summary` with the Markdown in the editor. That function in `Posts.cs`
chats through `IAiChat`, with a short instruction of its own and `MaxTokens = 120`, and answers
`{ summary }`.

* The `[AiAccess]` hook guards `/_host/ai/chat` only. A function is never asked, so this one checks
  for a signed-in writer itself.
* Without a provider chosen for the site, it answers `503` with "This site has no AI yet", and the
  studio shows that message.

## The jobs

`Housekeeping.cs` has two jobs. The site's **Functions** card shows their runs.

**Checkpoint** is marked `[Every("1h", RunOnStart = true)]`. It runs once as the functions load,
and then every hour.

* The database uses SQLite's WAL mode. SQLite copies the log (`blog.db-wal`) into `blog.db` on its
  own, but it never shrinks the log. So after a busy evening, `blog.db-wal` can stay large.
* The job runs `PRAGMA wal_checkpoint(TRUNCATE)`. This copies the log into `blog.db` and empties
  it. The job then logs one line. If a reader was busy, the log is not emptied, and the next run
  tries again.
* The job does nothing until the first request has created the database.

**Backup** is marked `[Schedule("0 3 * * *")]`. It runs at 03:00 UTC every day.

* It copies `blog.db` to `backups/blog-<yyyyMMdd>.db` in the data folder, with SQLite's backup API.
  The copy is consistent even while someone saves a post. It is one file, not in WAL mode.
* It keeps the newest seven backups and deletes older ones. A second run on the same day replaces
  that day's copy.
* The data folder is never served, so the backups are as private as the database. Copy them off
  the server with the rest of `/data`.
* It does nothing until the first request has created the database.
