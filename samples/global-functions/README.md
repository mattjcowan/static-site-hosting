# Global functions: a sample

`Global.cs` is a set of global functions. Global functions answer on every site on the server,
not on one site. `Global.linq` is the same code as a LINQPad query. Each file has a `#if LINQPAD`
block, so it also runs as-is in LINQPad.

## How to upload it

Only an administrator can upload global functions.

* **In the UI:** open **Functions** in the top bar, choose `Global.cs`, and upload it.
* **With the API:**

  ```bash
  curl -X PUT -H "X-Api-Key: $SSH_KEY" -F "file=@samples/global-functions/Global.cs" \
       http://localhost:8080/api/v1/functions
  ```

Upload `Global.cs` or `Global.linq`, not both. They declare the same class, so together they fail to
build with `CS0101`.

The server needs the `latest` image, which can compile functions. The first build restores packages
and can take a minute.

## What to expect

* **Headers.** Every response of every site carries these three headers, unless the site sent its
  own value (from a handler, or a [header rule](../../README.md#header-rules)). Middleware never
  sees `/_host/` or a private site's passcode form, so those keep the server's own headers.

  | Header | Value |
  | ------ | ----- |
  | `X-Content-Type-Options` | `nosniff` |
  | `Referrer-Policy` | `strict-origin-when-cross-origin` |
  | `Permissions-Policy` | `camera=(), microphone=(), geolocation=()` |

  The server already sends `X-Content-Type-Options` with every file. The middleware adds it to what
  functions send too. The blog sample's `_headers` file sets its own `Referrer-Policy` and
  `Permissions-Policy`, so the blog's pages keep the blog's values. Header rules apply to files, so
  the answers from the blog's functions get these defaults.

* **A log line per request.** The server log shows one line for each request to any site, under the
  category `functions:global`:

  ```
  info: functions:global[0]
        GET blog.localhost/about.html 200 3 ms
  ```

* **`/whoami` on every site.** `GET /whoami` answers with the domain the request is for, and the
  data folder that the global functions share:

  ```json
  { "domain": "blog.localhost", "globalData": "/data/config/functions-data" }
  ```

  A site with its own `/whoami` handler answers first. The answer shows a path on the server, so
  remove the handler once you have checked your setup.

* **A job.** Every quarter of an hour (at :00, :15, :30 and :45, UTC), a job writes `heartbeat.txt`
  in `/data/config/functions-data/`. The file holds `global` and the time. The **Functions** page
  shows the job, with its last and next run.

## Before you change it

Global middleware runs in front of every site. If the global functions cannot be loaded, every site
answers `503` until they are fixed. Open the editor under **Functions** and use **Check** before you
upload a change.

To remove them, use **Remove global functions** on the **Functions** page, or
`DELETE /api/v1/functions`.
