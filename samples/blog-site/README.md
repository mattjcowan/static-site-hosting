# Night Sky Field Notes: a demo site with functions

A static site with C# functions in `_functions/`, which the host compiles when the zip is
deployed. Sign-in, accounts and the journal live in SQLite (via Dapper) in the site's data
folder, and posts are Markdown rendered with Markdig.

Build `blog-site.zip` at the root of the repo (this README stays out of it):

```bash
scripts/build-samples.sh blog-site
```

Deploy it as an administrator (a `_functions/` folder is refused otherwise). The first request
creates an `admin` account with a generated password, printed to the server log and saved to
`initial-admin-password.txt` in the site's data folder. It must be changed at first sign-in.
