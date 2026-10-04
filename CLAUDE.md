# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Publish

```bash
# Build both projects (Release)
dotnet build -c Release

# Publish both to the same directory.
# csproj sets SelfContained=true + PublishSingleFile=true + IncludeNativeLibrariesForSelfExtract=true,
# so the output is just 2 exes + web/ — no dlls, no pdbs — and needs NO runtime on the target.
# Cost: each exe bundles its own copy of the runtime (~231 MB total, they do NOT share).
dotnet publish src/WebDAVService/WebDAVService.csproj -c Release -o publish
dotnet publish src/WebDAVConfigurator/WebDAVConfigurator.csproj -c Release -o publish

# Smaller framework-dependent variant (~4 MB, requires the .NET 8 Desktop Runtime on the target):
#   add  -p:SelfContained=false

# `web\**` is copied by the csproj's <Content Include="web\**"> — do not cp it manually.
# If the service is installed, redeploying means overwriting the exes in its install dir
# (delete the stale dlls from an older folder build first) and restarting the service.

# Clean
dotnet clean
```

## GitHub Release

```bash
# Create release zip
powershell -Command "Compress-Archive -Path publish/* -DestinationPath Easy_WebDAV_v{version}_win-x64.zip -Force"

# Create tag and release (uses stored GitHub token)
# NOTE: Push requires SOCKS5 proxy in this environment
git -c http.proxy=socks5://127.0.0.1:3067 -c https.proxy=socks5://127.0.0.1:3067 push

# Create release via API (token from git credential-manager)
curl -s -x socks5://127.0.0.1:3067 \
  -H "Authorization: token $(git credential-manager get <<< 'protocol=https\nhost=github.com' | grep password | head -1 | cut -d= -f2)" \
  -H "Content-Type: application/json" \
  -d @release_body.json \
  https://api.github.com/repos/anturong/easy_webeav/releases

# Upload asset
curl -s -x socks5://127.0.0.1:3067 \
  -H "Authorization: token $(...)" \
  -H "Content-Type: application/zip" \
  --data-binary @Easy_WebDAV_v{version}_win-x64.zip \
  "https://uploads.github.com/repos/anturong/easy_webeav/releases/{release_id}/assets?name=Easy_WebDAV_v{version}_win-x64.zip"
```

## Architecture

Two .NET 8 projects in one solution (`WebDAVService.sln`), zero external runtime dependencies. Both default to `SelfContained=true` in csproj.

### WebDAVService (Console → Windows Service)
- **Program.cs** — Entry point. `Host.CreateDefaultBuilder` + `UseWindowsService()` + Serilog file logging to `logs/service.log`. Target: `net8.0`.
- **Worker.cs** — `BackgroundService`. Loads config via `WebDAVConfig.Load()`, starts `WebDAVServer`.
- **Config.cs** — JSON config loader. Uses `JsonDocument.Parse` with hand-written field mapping (NOT `JsonSerializer.Deserialize<WebDAVConfig>`) to avoid circular-deserialization bugs. Reads `webdav_config.json` from exe directory. Static factory `Load()` + `Save()`. Also defines `UserInfo` class (duplicated in ConfigHelper.cs).
- **WebDAVServer.cs** — Core WebDAV server via `HttpListener`. Dual auth: session cookie (browser) + Basic Auth (WebDAV clients). Handles all WebDAV methods: OPTIONS, PROPFIND, GET, HEAD, PUT, DELETE, MKCOL, COPY, MOVE, PROPPATCH, LOCK, UNLOCK, plus a SPA-only **POST = 打包下载** (streams a `ZipArchive` over a chunked response). Read-only user enforcement. Health check timer every 20 minutes.
  - **`WWW-Authenticate` challenge is suppressed for browsers.** A `401` carries `WWW-Authenticate: Basic` only when the method is in `ChallengeMethods` **and** the request is not browser-originated (`IsBrowserRequest` = has a `Sec-Fetch-Mode`/`Sec-Fetch-Site` header). Chromium/Edge show the native Basic-auth dialog for **any** 401 + challenge — including `fetch`/XHR (verified empirically: a wrong-password PROPFIND pops the dialog) — so the SPA's own requests must get a bare `401` and let the SPA render its error. Real WebDAV clients (Explorer, rclone, Cyberduck, curl, phone apps) send no `Sec-Fetch-*`, so they still receive the challenge and authenticate. `/favicon.ico` is answered `204`.
- **AppInfo.cs** — Shared constants (app name, version, author info). Linked as a compile-time shared file from the Configurator project.
- **CertificateHelper.cs** — Self-signed SSL cert creation + `netsh http add sslcert` binding for HTTPS. Cert stored in LocalMachine\My store, bound to port via netsh.

### WebDAVConfigurator (WPF GUI)
- **Program.cs** — Entry + admin elevation. Detects `--install`/`--uninstall`/`--restart` args, reruns with `runas` for UAC via `Process.Start(Verb="runas")`. Target: `net8.0-windows`.
- **MainWindow.xaml + MainWindow.xaml.cs** — WPF dark-theme UI with 2-column control panel (settings card + user management card left, service controls bottom-left, log console right). Color-coded log console (RichTextBox, 500-line cap). Uses `System.Windows.Forms.FolderBrowserDialog` via WinForms interop for folder picking.
- **ServiceHelper.cs** — Service lifecycle via `sc.exe`. Creates service with auto-start, description, failure recovery (3x restart/5s). Cleans up SSL cert binding on uninstall.
- **FirewallHelper.cs** — `netsh advfirewall` rule management for service ports.
- **ConfigHelper.cs** — JSON config model (`WebDAVConfigData`) matching `WebDAVService`'s schema. Uses `JsonSerializer.Deserialize<T>` for reading/writing `webdav_config.json`. Also defines a SECOND `UserInfo` class (duplicated from Config.cs — both must be kept in sync).

### Web Templates (WebDAVService/web/*.html)
Three HTML files served by `WebDAVServer`, cached in static fields with lazy loading:
- **browse.html** — Full SPA file manager (~800 lines). Three global JS objects:
  - `Dav` — WebDAV communication layer (PROPFIND XML parsing, fetch/XHR for all methods). `Dav.zip(urls,name)` POSTs `{"paths":[...]}` to `/` for batch download; `Dav.saveBlob()` triggers the browser download.
  - `U` — Utilities: `fico()` maps extensions to icons + hash-based HSL colors, `size()` formats bytes, `time()` formats dates
  - `A` — App state: file list, breadcrumbs, search, selection, preview, sort. Key methods: `loadDir()`, `render()`, `toggleSort()`, `rowH()`/`gridH()` for rendering, `dlSel()` for batch zip download.
  Features: list/grid views, breadcrumbs, search/filter, upload progress, context menus, action sheets, drag-drop, toast notifications, file type labels (raw extension), hash-based color generation for unknown extensions, image/video preview modal. Font Awesome 6.5.0 CDN for icons.
  - **Navigation back to root** — a home crumb (`data-bp="/"`, 根目录) in the breadcrumb plus a `#btnUp` 上一级 button (shown only when not at root, hidden label on mobile).
  - **Batch download** — grid cells carry a `.chk` checkbox; selecting files enables `#dDl`/context-menu `dlsel`; a folder can also be "打包下载" (server zips it recursively).
- **login.html** — Standalone login form (POST, fallback for when JS is disabled). Also supports JSON POST from SPA.
- **logout.html** — Confirmation page with link back to `/login`.

Templates use `{{APP_NAME}}`, `{{AUTHOR}}`, `{{AUTHOR_URL}}`, `{{VERSION}}` placeholders replaced server-side. Templates are cached in static fields (`_browseTemplate` etc.) — service restart required to pick up changes.

## Key Design Decisions

- **Zero external dependencies** — No nssm.exe, .bat, Python. Service via `sc.exe` + `ServiceController`.
- **UAC elevation** — Single prompt via `Process.Start(Verb="runas")` with `--command` args.
- **Config sharing** — Both projects read/write `webdav_config.json` from exe directory. Service uses hand-parsed JSON (avoiding circular deserialization), Configurator uses `JsonSerializer.Deserialize<T>`. UserInfo class is duplicated in both projects.
- **HttpListener binding** — `0.0.0.0` → `+` for HttpListener prefix (`http://+:port/`). Cert creation falls back to `localhost` when host is `+`. Binding to `+` requires admin rights. Use `localhost` to avoid admin URL ACL.
- **SPA auth flow** — Browser GET to any directory serves the SPA template without requiring auth. All data operations (PROPFIND, MKCOL, DELETE, etc.) are made by client-side JS via Basic Auth. Login page sets a session cookie for convenience but JS uses Base64-encoded Basic Auth headers directly.
- **Auth dispatch** — `HandleRequestAsync`: route to `/login`/`/logout`/`/favicon.ico` first, then check Accept header for SPA, then authenticate (session cookie > Basic Auth), then read-only check, then dispatch by HTTP method (POST → zip download). Failed auth: browser GET → `302 /login`; everything else → bare `401` (challenge only for real WebDAV clients, see above).
- **Session management** — In-memory `ConcurrentDictionary` with 24h expiry. Logout clears session + expires cookie. Cookie header manually parsed (HttpListenerRequest.Cookies has parsing bugs).
- **Read-only enforcement** — Per-user read-only flag checked server-side before write operations (PUT, DELETE, MKCOL, MOVE, COPY, PROPPATCH).
- **Template caching** — Static fields with null-coalescing assignment (`_template ??= LoadWebTemplate("file.html")`). No cache invalidation — requires service restart.
- **Health check** — Timer runs every 20 min, logs uptime, directory status, cert validity.
- **Port conflict** — Old service processes linger. Use a different port or clean via Configurator uninstall.
- **AppInfo sharing** — `AppInfo.cs` lives in WebDAVService project, linked from WebDAVConfigurator csproj via `<Compile Include="..\WebDAVService\AppInfo.cs" Link="AppInfo.cs" />`.
- **WPF/WinForms interop naming conflicts** — Both `UseWPF` and `UseWindowsForms` set in csproj (for `FolderBrowserDialog`). This causes ambiguity between `System.Windows.MessageBox` and `System.Windows.Forms.MessageBox`, and between `System.Windows.Media.Brushes` and `System.Drawing.Brushes`. Always use fully qualified names: `System.Windows.MessageBox.Show()` and `System.Windows.Media.Brushes.LightGreen`.
- **System.IO not in implicit usings** — WPF projects with `UseWPF=true` do not auto-import `System.IO`. Files using `File`, `Path`, or `FileNotFoundException` must add `using System.IO;` explicitly.

## Web Template Development

When modifying `browse.html`:
1. Edit the source file at `src/WebDAVService/web/browse.html`
2. Rebuild and publish, then copy to deployment dirs (see Build & Publish)
3. Restart the service to pick up the new template (static field cache)

Key SPA client-side patterns:
- `Dav` object handles all WebDAV communication (PROPFIND XML parsing, fetch/XHR)
- `U.fico()` maps file extensions to icons + hash-based HSL colors for unknown types
- `U.size()` / `U.time()` format file sizes and timestamps
- `A` object manages app state: file list, breadcrumbs, search, selection, preview, sorting
- All authenticated requests use `btoa(username + ":" + password)` for Basic Auth
- The SPA has its own login view that POSTs JSON to `/login` to create a session cookie
- Column widths, grid columns, and responsive breakpoints are in CSS, not JS

## Common Issues

- **Service won't start**: Check `logs/service.log`. Missing `UseWindowsService()` causes immediate exit.
- **Port conflict**: Old service process still running. Configurator → Restart Service, or `sc stop WebDAVService`.
- **Stack overflow on startup**: Config loading loop — ensure JSON deserialization doesn't call config class constructor (use static factory).
- **HTTPS fails**: Check logs for cert binding errors. Requires admin for `netsh http add sslcert`. Self-signed cert triggers browser warning.
- **Web template changes not showing**: Templates are cached in static fields. Service restart required.
- **Mkdir/delete fails**: Check if user account is configured as read-only (`readonly: true` in config).
- **Git push fails with connection reset**: Network blocks direct GitHub access. Use SOCKS5 proxy:
  ```bash
  git -c http.proxy=socks5://127.0.0.1:3067 -c https.proxy=socks5://127.0.0.1:3067 push
  ```
