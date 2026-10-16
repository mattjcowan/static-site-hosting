---
name: ui-copy
description: How to write any text the management app or the visitor screens show (labels, hints, buttons, messages, errors), in plain English that translates cleanly. Use whenever adding or changing UI text, or moving it into the string files.
---

# Writing the app's text

People who use this server range from a hobbyist with one site to an operations team at a very
large company, many reading in their second language, some through a translation. Every word
has to be understood the first time, by all of them.

## The rules

1. **Say what it does for the reader first, then what they need to know.** "Forwards paths on
   this site to other servers." Not "Proxy routes are a mechanism that…".
2. **One idea per sentence.** Aim for 20 words or fewer. A hint is at most two sentences. If it
   needs more, the README has the rest: link to it, or fold the detail away.
3. **Plain words.** "Use", not "utilize"; "start", not "initiate"; "so", not "thus". No idioms,
   no jokes, no "simply", "just" or "easy". Write "such as", never "e.g."; no "i.e.", no "etc.".
4. **Active voice, present tense.** "The server checks the certificate." Say "you" when the
   reader acts and "this server" for the server. Never name the product: the product's name is
   whatever the owner brands it as.
5. **Buttons and links are a verb and its object**, in sentence case: "Save route", "Add a user",
   "Turn off". Use these verbs and keep to their meanings:
   - **Add** / **Remove**: put in or take out of a list. Removing can be undone by adding again.
   - **Delete**: gone for good.
   - **Save**, **Turn on** / **Turn off**, **Test**, **Copy**, **Download**, **Upload**,
     **Deploy**, **Roll back**, **Renew**, **Create**.
6. **Confirmations say what happens and whether it can be undone.** "Delete shop.example.com and
   all of its releases? This cannot be undone."
7. **Messages after an action say what happened, specifically.** "Saved 3 header rules for
   shop.example.com." Errors say what went wrong and how to fix it, with no apology.
8. **Numbers are digits**, with a space before the unit: "512 MB", "30 days", "5 seconds".
9. **Technical names stay as they are**, in backticks: headers (`Cache-Control`), paths
   (`/_host/site.js`), files (`_redirects`), variables (`${env:NAME}`), commands. They are never
   translated.
10. **Sentence case everywhere**: titles, labels, buttons, tabs. Only names are capitalised
    (HTTPS, Let's Encrypt, Docker, WebSockets, gRPC).
11. **Never cut a safety fact to save words.** What a setting exposes, what cannot be undone,
    who can see a secret: those stay, shortened if possible, never dropped.

## Writing for translation

- **One key holds one whole sentence or label.** Never build a sentence from pieces in code
  ("Saved " + n + " rules"): word order differs between languages. Use placeholders.
- **Placeholders have names that say what they hold**: `{domain}`, `{count}`, `{path}`, not `{0}`.
- **Counts use plural keys** (`.one` and `.other`): French says "0 route", Chinese has no plural.
- **Do not reuse a key because the English happens to match** in two places that mean different
  things. "Remove" on a route and "Remove" on a certificate can share `common.remove`; a
  heading "Releases" and a tab "Releases" should not.
- **No markup but the four marks** (see the `localization` skill): `{name}`, `` `code` ``,
  `**bold**`, `[link text](name)`.

## Glossary

Use these words, with these meanings, everywhere. Do not swap in synonyms.

| Word | Means | Not |
| ---- | ----- | --- |
| site | everything served at one domain | app, project, website (fine in prose about visitors) |
| domain | the address a site answers at, such as `shop.example.com` | host, hostname (except in HTTPS, where "host" is the certificate's word) |
| release | the files of one deploy, kept so you can roll back to it | version, build |
| deploy | publish a zip as a new release | upload (uploading is the first half of deploying) |
| roll back | make an earlier release the live one again | revert, restore |
| duplicate | copy a release as a new one that is not live, to go back to or to work on | clone, snapshot, back up |
| make live | make a release the one visitors get (a rollback is making an earlier one live) | publish, activate, promote |
| live | the release visitors get now | current, active |
| administrator / member | the two roles | admin (in text), user (for the role) |
| private link | the one-time link that lets someone set their password | invitation, invite |
| API key | a key for scripts and AI agents | token |
| private site / passcode | a site behind one shared passcode | password (that is for accounts) |
| proxy route | a path forwarded to another server | proxy rule, mapping |
| target | the address a proxy route forwards to | backend, upstream, destination |
| server-wide route | a proxy route for many sites, set under Proxy | global route |
| header rule | a rule that sets response headers for paths | header |
| redirect / rewrite | send the visitor elsewhere / answer with another file | |
| variable / secret | a named value a site reads / one stored encrypted and never shown again | setting, env var |
| function | C# code that answers requests on a site | handler, endpoint (except when describing what it is) |
| global function | a function for every site, set under Functions | |
| AI provider / model | the AI service and key / the model it runs | |
| assistant | the chat panel that manages the server for you | bot, agent |
| certificate | what HTTPS uses for a domain | cert |
| this server | the server you are managing | the host, the platform, the app's name |

## Before and after

> **Before:** Forward paths on this site to other servers, such as Docker containers or services
> on a private network, so one domain can serve files, functions and backends together. A route's
> requests have already passed the passcode, the site's middleware and its functions, so a site's
> sign-in protects them too, and a function can still answer one path inside a proxied one.
>
> **After:** Forward paths on this site to other servers, such as Docker containers. Requests pass
> the site's passcode and functions first, so they protect what a route forwards.
