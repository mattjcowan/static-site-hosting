# Vendored files

## signalr.min.js

The SignalR browser client, served to every site at `/_host/signalr.js`. `site.js` loads it the
first time a page uses `site.realtime`, so a page that never does never downloads it.

| | |
| --- | --- |
| Package | [`@microsoft/signalr`](https://www.npmjs.com/package/@microsoft/signalr) **10.0.11**, the release that matches the host's .NET 10 SignalR server |
| File | `dist/browser/signalr.min.js`, the UMD browser bundle, which defines `window.signalR` |
| From | <https://cdn.jsdelivr.net/npm/@microsoft/signalr@10.0.11/dist/browser/signalr.min.js> (the same bytes as in the npm tarball) |
| Upstream SHA-256 | `97e9b97e642a72e5a470917147a2bf79f86cad829a5c6786adb10614d248bb95` |
| Licence | MIT, copyright .NET Foundation and Contributors; the source is [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore) (`src/SignalR/clients/ts`), and the licence text is its [`LICENSE.txt`](https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt) |

**One change from upstream:** a first line naming the package, its version and its licence. The
published bundle carries no licence banner of its own, and the MIT licence asks that the notice
travel with copies. Everything after that line is upstream's file byte for byte, which this checks:

```bash
tail -n +2 src/StaticSiteHost/wwwroot/host/signalr.min.js | sha256sum
```

The file ends with upstream's `//# sourceMappingURL=signalr.min.js.map` comment. The map is not
vendored, so a browser's developer tools may note that it could not load it; nothing else asks for it.

### Updating it

1. Pick the newest `@microsoft/signalr` release whose major version matches the host's .NET
   (`npm view @microsoft/signalr versions`).
2. Download it, and put the banner line back in front:

   ```bash
   V=10.0.11
   curl -sSfL -o /tmp/signalr.min.js "https://cdn.jsdelivr.net/npm/@microsoft/signalr@$V/dist/browser/signalr.min.js"
   { printf '%s\n' "/*! @microsoft/signalr $V | Copyright (c) .NET Foundation and Contributors | MIT License | https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt */"
     cat /tmp/signalr.min.js; } > src/StaticSiteHost/wwwroot/host/signalr.min.js
   ```

3. Check that it is still the UMD bundle (its last line of code ends by assigning `t.signalR`) and
   that `HubConnectionBuilder` still has `withUrl`, `withAutomaticReconnect` and
   `configureLogging`, which `site.js` uses.
4. Update the version, the URL and the hash above.

`site.js` asks for `/_host/signalr.js?v=<host version>` and the file is cached for a day, so a new
host version is fetched fresh; nothing else needs changing.
