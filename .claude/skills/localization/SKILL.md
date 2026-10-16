---
name: localization
description: How the app's text is stored and shown in several languages - string files, keys, placeholders, plurals, Razor and JavaScript usage, adding a language. Use when adding or changing any UI text, moving text out of pages, or adding a translation.
---

# Strings and languages

All text the management app and the visitor screens show lives in
`src/StaticSiteHost/Resources/strings/{language}/{area}.json`, embedded in the build. Write it
following the `ui-copy` skill.

## Files and keys

- One folder per language: `en` (complete, and what every other language falls back to, key by
  key), then `fr`, `es`, `zh-Hans`. One file per area of the app; each file is a flat JSON object
  of key → text, in the order the text appears on the page, so a translator reads it in context.
- A key is `area.section.item`, lower camel case segments: `site.proxy.title`,
  `site.proxy.addRoute`, `users.created`. The first segment is the file's name. A key lives in
  one file only. Text for scripts goes in `js-{area}.json` with keys `js.{area}.*`
  (`js-editor.json` holds `js.editor.runFailed`; `js-files.json` holds the site's Files tab,
  `js.files.*`).
- Areas: `common` (menu, shared buttons and words), `auth` (sign in, private links, password),
  `deploy`, `sites` (the list), `site` (a site's page, every tab), `branding`, `users`, `functions` (global
  functions and the function parts shared with a site), `editor` (the function editor), `proxy`
  (server-wide routes), `https`, `ai`, `account`, `visitor` (the passcode, 404 and error screens
  visitors see on a site's domain), `js` (text scripts show).

## What a string may contain

Plain text, plus four marks, so translators never touch HTML:

| Mark | Shows | Example |
| ---- | ----- | ------- |
| `{name}` | a value from the code | `Saved {count} header rules for {domain}.` |
| `` `code` `` | fixed-width text that is never translated | ``Set `Cache-Control` on the paths you name.`` |
| `**words**` | bold | `Changes take effect **immediately**.` |
| `[words](name)` | a link whose address is the value `name` | `To publish files, [deploy a zip](deployUrl) instead.` |

Values are always HTML-encoded, unless the code passes HTML on purpose (an `IHtmlContent`, such as
a link built with a tag helper). Markup is found in the string before values go in, so a value
can never turn into markup.

## Counts

Two keys, `.one` and `.other`, under one name:

```json
"site.proxy.routeCount.one": "{count} route",
"site.proxy.routeCount.other": "{count} routes"
```

`{count}` is filled in. French uses `.one` for 0 and 1; Chinese uses only `.other` (give `.one`
too, the same text, so the files stay alike).

## Using strings

**Razor pages** have `L` (an `IStringLocalizer<Strings>`, from `_ViewImports.cshtml`):

```cshtml
<h2>@L["site.proxy.title"]</h2>                                   @* plain text *@
<input placeholder="@L["site.proxy.pathPlaceholder"]" />           @* attributes too *@
<p class="hint">@L.Html("site.proxy.hint")</p>                     @* with `code`, **bold** or links *@
<p>@L.Html("sites.empty", new { deployUrl = Url.Page("/Index") })</p>
<span class="badge">@L.Plural("site.proxy.routeCount", Model.Site.Proxy.Count)</span>
<form data-confirm="@L.Text("site.delete.confirm", new { domain = Model.Site.Domain })">
```

`@L["key"]` and `L.Text(...)` give plain text (marks are removed). Use `L.Html(...)` wherever the
string has marks or links.

**Page models and services** take `IStringLocalizer<Strings>` in the constructor:

```csharp
TempData["StatusMessage"] = _strings.Text("site.headers.saved", new { count, domain = site.Domain });
```

**Scripts** call `siteHost.t(key, values)`; only keys that start with `js.` reach the browser
(the layout writes them into the page for the viewer's language):

```js
button.textContent = siteHost.t('js.copy.done');
label.textContent = siteHost.t('js.dropzone.files', { count: files.length, names: list });
```

`siteHost.tp(key, count, values)` picks `.one` or `.other`.

## What stays English

The API's answers, MCP tool names and descriptions, log lines, and the README: they are read by
scripts and AI agents, and a contract that changes language breaks them. Server-side validation
messages shared with the API are English for now.

## The languages there are

`en`, `fr`, `es` and `zh-Hans`. A change to English text changes the other three: translate the
new or changed keys in the same change, keeping each language's own conventions.

- **fr:** "vous"; a no-break space (U+00A0) before `:`, `;`, `?`, `!` and inside « ».
- **es:** neutral Spanish for Spain and Latin America; "tú"; ¿ and ¡ where needed.
- **zh-Hans:** full-width punctuation; a space between Chinese and Latin words, numbers or a
  `{placeholder}`, none next to full-width punctuation; `.one` and `.other` hold the same text.
- **Words** follow the English glossary in `ui-copy`. The translations use: release → version /
  versión / 版本; deploy → déployer / desplegar / 部署; live → en ligne / publicada / 线上;
  Duplicate → Dupliquer / Duplicar / 复制; Make live → Mettre en ligne / Publicar / 设为线上版本.
  Search the language's files for a label before translating a string that names it, so the two
  match.

## Adding a language

1. Copy `Resources/strings/en` to `Resources/strings/{code}` (a BCP 47 code: `de`, `pt-BR`,
   `zh-Hant`).
2. Translate every value. Keep the keys, every `{placeholder}`, every `` `code` `` span as it is,
   and the `[words](name)` link names; translate the words.
3. Run `dotnet test tests/StaticSiteHost.Tests`: `StringFileTests` fails on a missing or extra
   key, a placeholder that differs from English, or broken JSON.
4. Look at every page in the new language (`scripts/screenshots.sh` or by hand): longer words
   can wrap where English did not.

The language appears in the picker as soon as its folder exists. A key a translation lacks shows
the English text.
