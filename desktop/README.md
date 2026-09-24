# MishaWeb

MishaWeb 2.2.0 is a compact native Windows browser built with WinForms and the installed Microsoft Edge WebView2 Runtime. It has no frontend framework, background service, telemetry client, or bundled browser engine.

## Highlights

- Renderer-free native start page with search, three focused continue cards, and a modern bunny night-garden backdrop inspired by v1
- New generated kawaii bunny identity shared by the start page, title bar, executable, and Windows shell icon sizes, with a native high-contrast fallback
- One optimized 275 KB static backdrop and one compact embedded logo, each decoded once and shared; a bounded 24-bit viewport frame lets child surfaces copy exact background slices without repeated JPEG scaling, reuses the frame across start-page tab switches, and releases it when the last page is hidden, minimized, or disposed
- Modern captionless deep-berry frame with blush-pink/orchid accents, several cute bunny motifs, vector window controls, and accessible tab controls
- Native smart command bar with local favorites/history suggestions and a consistent Google search action
- Allocation-light local suggestion index that rebuilds only when favorites, history, dismissals, or learned ranking data changes
- Responsive low-glare toolbar, tabs, menus, and focus states
- Tabs, middle-click close, duplicate/close-others menu, and reopen-closed-tab
- Text-only command palette with bounded local ranking, MRU tab switching, and persistent pinned tabs
- Native named sessions, restart-persistent recently closed tabs, and lazy 64-tab session restoration
- Bare-domain, localhost, IP-address, and search-aware address parsing
- Back, forward, reload/stop, home, in-page find, zoom, and fullscreen
- Exact-origin microphone and camera controls, voice and audio input/output settings with strict whitelist policies, saved-permission management, and Windows device/privacy-settings shortcuts
- Explicit clean-link copy, link-context cleanup, and address-bar Paste and go
- On-demand dependency-free reader mode and manual tab unloading
- Favorites plus a capped, local browsing-history list
- Three ordered pinned quick links, with local filtering and management in **Saved items…**
- Searchable tab switcher and a saved-items dialog that never loads WebView2 or favicons
- Private windows with isolated WebView2 InPrivate profiles and no MishaWeb-managed history, favorites, pins, extensions, or session persistence
- Anchored site information and exact-host shield exceptions with reload guidance
- Adaptive download popup with pause/resume, open, reveal, cancel, copy-address, remove, and folder actions
- Native browser-extension manager with Chrome Web Store URL/ID import, local CRX/ZIP and unpacked-package loading, enable/disable, removal, and popup/options access
- Native print support and PNG viewport capture without an extra renderer or capture library
- Balanced WebView2 tracking prevention plus EasyList, EasyPrivacy, uBO, and Brave-aligned network and cosmetic filtering
- Bounded blocker URI/suffix caches, allocation-free candidate traversal, compile diagnostics, and conditional HTTP list refresh
- Document-start YouTube protection for first-party player ad metadata, feed/player cosmetics, skip controls, and guarded SSAP markers
- Standard Memory Saver keeps the three most recently used ordinary sites ready without automatic discard or reload, then releases older inactive pages on a bounded schedule
- Ultra-light may suspend those three recent sites without refreshing them, while discard-first pressure handling trims only older eligible pages and skips audible, downloading, and explicitly protected work
- Explicit WebView/frame event teardown, weak or bounded native caches, and hidden-surface cleanup prevent closed tabs and browser-owned COM objects from accumulating over long sessions
- Low-memory WebView targets for background tabs that must keep audio, downloads, or live connections running
- Dark/light website preference without reloading active forms
- User-gesture popup policy with compatible `window.opener` handling and narrow related-call exceptions
- Confirmation for external application links and a browsing-data clear action
- Renderer/browser-process recovery with preserved tab URLs
- Atomic local settings and a small rotating diagnostics log
- Safe repeat-launch managed-code profiling on multicore PCs, with no WebView2 prewarming or first-run background service
- Active pages can opt into a compatibility-aware reduced-motion policy that compresses CSS transitions and animations while leaving Web Animations and page prototypes untouched; it is off by default

Browser profile data is stored under `%LOCALAPPDATA%\MishaWeb\WebView2`. Preferences, favorites, and the capped history list are stored in `%LOCALAPPDATA%\MishaWeb\settings.json`.

## Ad and tracker blocking

The built-in shield starts with offline fallback rules, then atomically loads a 20-file set covering EasyList, EasyPrivacy, the current and historical uBlock Origin filter components, uBO link-shortener/resource-abuse rules, Brave first-party/specific rules, Brave Unbreak, and Brave first-party regional rules from `%LOCALAPPDATA%\MishaWeb\Filters`. Lists refresh after eight hours, retain the last known-good copy when a source is unavailable, and are bounded and validated before background compilation. Refreshes use ETag/Last-Modified validators, so a `304 Not Modified` response avoids downloading the list body or recompiling unchanged rules. Byte-identical responses also skip recompilation, and unrelated or oversized cache files are ignored. Chromium conditional sections, standard network rules, domain/type constraints, scoped hiding/blocking exceptions, and standard CSS element-hiding rules are supported in top documents and frames. Unknown modifiers are skipped instead of being broadened into unsafe blocks, compile diagnostics expose unsupported and capacity-dropped rules, and remote lists are never allowed to execute JavaScript.

YouTube also receives an audited document-start module. It removes ad placements from initial, Fetch, cloned Fetch, and XMLHttpRequest player, playlist, watch, and get-watch data; hides desktop, mobile, feed, Shorts, and player ad surfaces; activates skip controls; and applies guarded `SSAP, AD` seeking only on unfinished, non-Premium watch playback. A bounded two-attempt recovery handles both inline ad-block enforcement and the legacy server-contract error without broad Promise or Map prototype hooks, restores the original request context after playback returns, and removes stale enforcement UI only after playable media is ready. Ordinary browse, search, comments, thumbnails, and `googlevideo.com` playback remain allowed. YouTube changes its delivery frequently, so no browser-side blocker can promise permanent coverage for every server-side experiment; the lists refresh every eight hours while the audited runtime fallback covers the currently known player contract.

## Browser calls

Any HTTPS site that has been granted microphone or camera access keeps its same-origin secure WebSocket and media transport available while the shield continues filtering ordinary and third-party requests. Known Messenger/Facebook, Discord, and Zoom call transports also receive a narrow preflight exception so their connection setup can reach the permission step. Related HTTPS and `about:blank` popup compatibility stays limited to those known providers; temporary popup bootstrap URLs remain un-navigated until WebView2 attaches the real call tab, and device permission alone never grants a site scripted-popup access. Granting microphone or camera access also protects that tab from Memory Saver and Ultra-light suspension until it navigates, so a silent connecting call is not mistaken for an idle page.

Use the site-information button beside the address bar to set **Microphone** or **Camera** to **Allow**, **Ask every time**, or **Block** for the current origin. The site-information button, command palette, and main menu also offer **Voice and audio settings…** to configure strict sound output policies (allow all, only specific sites, or mute all) and microphone input policies (ask every time, only specific sites, or block all). Direct shortcuts also open Windows sound device preferences and Windows microphone privacy pages. Windows must also have **Let desktop apps access your microphone/camera** enabled; website permission cannot override an operating-system privacy block.

## Resource modes

Standard Memory Saver is the default for new sessions. It immediately gives every inactive WebView2 page the low memory target and restores the normal target when selected, while scripts, timers, media connections, and downloads keep working. The active ordinary site plus the next two most recently used ordinary sites form a per-window resident set: these three are never automatically unloaded, so switching among them does not refresh the page or lose its live state. Native start pages do not consume this quota. Standard mode does not mix the low/normal target pair with WebView suspension; instead, completed pages outside the resident set unload after 15 inactive minutes, or after two minutes only when a valid system reading reports at least 75% memory use. Stalled loading or initialization receives a longer 30-minute grace period, reduced to five minutes under verified pressure.

Optional Ultra-light mode owns the separate sleep/discard strategy. It may suspend and resume the three recent sites while retaining their WebView state, but it may never automatically discard them. Older eligible pages converge on a machine-sized warm-core budget: one with up to 4 GiB of RAM or two logical CPUs, two with up to 8 GiB or four logical CPUs, and three on larger machines. Pages explicitly kept active, making sound, using media permission, or downloading remain independently protected and can intentionally keep more than three live sites. Repeated failed suspension attempts are capped; a recent site that refuses sleep stays live at the low target instead of creating an endless COM retry/log cycle. Discards are processed before new suspension requests and revalidate the current MRU rank immediately before teardown.

When reduced website motion is enabled, active pages receive a small document-start stylesheet that compresses CSS transitions and animations to 0.001 ms, limits CSS animations to one iteration, and disables smooth scrolling. The tiny non-zero duration preserves CSS completion events, and Web Animations, page prototypes, and JavaScript timers remain untouched for compatibility. The option is off by default; the browser's sleep/discard lifecycle handles inactive pages without changing active-site behavior.

**Website theme → dark** keeps the standard WebView2 dark preference for theme-aware pages and adds a reversible renderer-level automatic-dark override for apps that keep explicit light colors, including Gmail and Google Drive. It is applied once per awake WebView and persists across that tab's navigations; sleeping tabs defer the tiny update until they wake, so changing the setting never resumes them. There is no DOM scanner, injected theme stylesheet, polling timer, or automatic reload. Chromium's selective media classifier leaves photo-like artwork unchanged while adapting icon-like artwork when contrast requires it. Choosing **Website theme → light** clears the renderer override and sends the light preference immediately; if the optional Chromium capability is unavailable, MishaWeb falls back safely to the standard `prefers-color-scheme` signal.

An unloaded tab remains open and selectable with its URL and title intact. Cookies, cache, passwords, autofill, and the shared browser profile remain available, then the page reloads when selected; live DOM state such as partially filled forms, scroll position, and that tab's back/forward list can be lost. Right-click a tab and choose **Keep active in background** for messaging, monitoring, audio, downloads, or form-heavy pages that must keep running. Those protected hidden tabs use WebView2's low-memory target instead of suspension, although arbitrary live websites still have an irreducible RAM and CPU cost.

Standard memory saver is the default: the three most recently used ordinary sites stay resident without reloads, while older inactive pages are eventually released so long-running sessions can settle instead of growing with every opened tab. Choose **Resource mode → Off**, **Memory saver**, or **Ultra-light** from the main menu. Active websites retain the normal Evergreen WebView2 feature set: JavaScript, WebAssembly, WebGL, media, downloads, passwords, and autofill are not disabled. No unstable Chromium command-line switches are used.

MishaWeb supports up to 128 open tabs. Saved sessions restore every tab as a lightweight shell—including duplicate URLs—and initialize only the selected web page. A native new-tab-only session does not launch the WebView2 browser process at all.

## Browser extensions and user scripts

Open **Extensions…** from the main menu to install and manage Chromium browser extensions. Paste a Chrome Web Store listing address or its 32-character extension ID, choose a local `.crx`/`.zip` package, or load an unpacked folder containing `manifest.json`. MishaWeb validates and copies extension files into stable app-owned storage before asking WebView2 to install them. The manager lists installed extensions and can check a managed Store extension for an update, enable, disable, remove, or open a declared action popup/options page.

On a Chrome Web Store extension listing, click **Add to Chrome** or MishaWeb's visible **Install in MishaWeb** button. MishaWeb intercepts that trusted click, downloads the compatible signed package through Google's official update service, and shows its permissions for approval before installation. The **Install in MishaWeb** button remains available if the Store changes its own button markup. You can also use **Extensions…** while viewing a listing—the current address is filled in automatically. **Check update** performs a bounded, user-initiated check; it verifies the Store publisher proof and extension identity, compares dotted versions, and shows all requested access plus newly declared capabilities before activation. The previous package is retained until the replacement is installed and recorded so a failed transaction can roll back. Because WebView2 runs a same-ID replacement immediately, a disabled extension must be enabled before it can be updated; this avoids a hidden enable/disable execution window. Updates and extensions remain unavailable in private windows. Extension compatibility varies, especially for add-ons that depend on Chrome-only browser UI or APIs.

Extensions can read or change webpages and may handle sensitive data. Install only packages you trust. Before installation, MishaWeb shows the parsed extension name, version, declared permissions, optional permissions, site access, and content-script match patterns for approval. Store packages are fetched over HTTPS from Google's official Chrome update service; signed CRX2/CRX3 packages are authenticated before extraction, imports are bounded against oversized archives, links, overlapping folders, and unsafe paths, and management metadata stays outside the installed extension root. Private windows use a separate InPrivate profile with no normal-profile extensions.

The earlier lightweight user-script feature remains separate. Place top-level `.js` files in `%LOCALAPPDATA%\MishaWeb\Extensions`, then use **Extensions… → User scripts** to open that folder. User scripts are registered when a WebView tab is created; recreate the tab or restart MishaWeb after changing a script. They run with page-level access, so the same trust warning applies.

## Search and privacy

The start page opens with focus in the smart command bar. Empty input shows `SEARCH OR OPEN A SITE`; search terms show Google and `Search`; recognized addresses show `OPEN WEBSITE` and `Open`. Suggestions are local only—MishaWeb never fetches autocomplete or favicons. Typing never replaces unfinished text with a history URL: press `Enter` to submit exactly what you typed, or use `Up` / `Down` and then `Enter` to explicitly open a local suggestion. Search text always uses Google, while complete addresses still open directly. The toolbar omnibox remains available through `Ctrl+L` and `F6`.

`Ctrl+Shift+N` opens a private window. Private windows use a dedicated WebView2 InPrivate profile, application defaults, an empty in-memory saved-data view, and no normal-profile extensions or user scripts. MishaWeb does not save their tabs, history, favorites, pins, shield exceptions, or preferences. Downloads, captures, printing, clipboard use, and external applications can still leave data outside the private window.

The site-information button opens an anchored menu with the host, connection state, blocked-request count, exact-origin microphone/camera choices, Windows privacy shortcuts, an exact-host **Enable/Disable shield for this site** action, and **Reset saved permissions for this site**. Disabling the shield takes effect immediately. Re-enabling it requires a reload so document-start filtering can run before the site's scripts.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+L` or `F6` | Focus the address bar |
| `Ctrl+K` | Focus the smart command bar on the start page, otherwise the address bar |
| `Ctrl+T` / `Ctrl+W` | Open / close a tab |
| `Ctrl+Shift+N` | Open a private window |
| `Ctrl+Shift+P` | Open the command palette |
| `Ctrl+Shift+T` | Reopen the last closed tab |
| `Ctrl+Shift+A` | Search and switch open tabs |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Preview and switch tabs by MRU order |
| `Ctrl+PageUp` / `Ctrl+PageDown` | Cycle tabs in strip order |
| `Ctrl+J` | Open downloads |
| `F9` | Toggle reader mode |
| `Ctrl+1`…`Ctrl+9` | Select a tab |
| `Alt+Left` / `Alt+Right` | Back / forward |
| `Ctrl+R` or `F5` | Reload; `Esc` stops loading |
| `Ctrl+F`, `F3`, `Shift+F3` | Find on page |
| `Up` / `Down`, `Enter`, `Escape` | Navigate, accept, or close local suggestions |
| `Shift+Delete` | Remove the selected local suggestion |
| `Ctrl+D` | Add or remove a favorite |
| `Ctrl+P` | Open the system print dialog |
| `Ctrl+Shift+S` | Capture the visible page as a PNG |
| `Ctrl++`, `Ctrl+-`, `Ctrl+0` | Change or reset zoom |
| `F11` | Toggle fullscreen |

## End-user requirements

- Windows 10 or Windows 11, x64
- Microsoft Edge WebView2 Evergreen Runtime
- .NET Desktop Runtime 10 for the small default build

The optional self-contained build includes .NET and is much larger. WebView2 remains a Windows runtime prerequisite in both cases.

The default release stays framework-dependent and uses the automatically updated Evergreen WebView2 Runtime, so it does not duplicate the .NET runtime or a browser engine inside the download. Trimming is deliberately disabled because [.NET currently marks WinForms trimming as unsupported](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities#windows-forms). Microsoft recommends [Evergreen WebView2 for most applications](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/evergreen-vs-fixed-version), including its shared disk, memory, servicing, and security benefits.

## Source-build requirements

- .NET 10.0.103 SDK (selected by `global.json`; later servicing patches in the same feature band are allowed)
- Node.js/npm for the convenience scripts below (the equivalent `dotnet` commands can also be run directly)

## Commands

```powershell
npm run desktop:check     # strict build plus policy smoke tests
npm run desktop:memory-churn # opt-in private loopback tab/frame memory soak
npm run desktop:run       # launch from source
npm run desktop:publish   # small framework-dependent single-file output
npm run desktop:one-click # small framework-dependent MishaWeb.exe in the repo root
npm run desktop:portable  # larger self-contained output in desktop/portable
```

Dependencies are restored from committed NuGet lock files. All three publish commands run the strict build and smoke suite first, publish into a unique staging directory, reject anything outside the expected single-file payload, and only then replace the destination. This prevents a successful release from retaining files left by an older build.

Each release includes `MishaWeb.release.json`, `MishaWeb.sha256`, and the applicable third-party notices. The manifest records the target, deployment model, executable version, byte size, SHA-256 hashes, and Authenticode status; validation is repeated after the staged output is promoted. Use `npm run desktop:verify-publish` or `npm run desktop:verify-portable` to recheck an existing output without rebuilding it.

CI performs a locked restore, warning-as-error build, smoke suite, clean publish of both deployment models, manifest/hash verification, and artifact upload on Windows.

Production artifacts should be signed during staging, before hashes are generated. With a code-signing certificate in the current user's certificate store, invoke the release script directly:

```powershell
.\build\Publish-MishaWeb.ps1 -Mode FrameworkDependent -SigningCertificateThumbprint <thumbprint> -TimestampServer <timestamp-url> -RequireSignature
```

Without an available certificate the script still produces a locally testable artifact, records `NotSigned` in its manifest, and emits a warning; do not treat that unsigned artifact as a public production release.

The optional live extension checks require the installed WebView2 Runtime. The Store bridge check stays offline by mapping a local fixture onto the exact Store origin, while the package check requires network access and performs a real download/install/remove cycle with the runtime's actual version:

```powershell
dotnet run --project desktop.tests/MishaWeb.SmokeTests.csproj -c Release --no-build --no-restore -- --check-browser-extensions-runtime
dotnet run --project desktop.tests/MishaWeb.SmokeTests.csproj -c Release --no-build --no-restore -- --check-chrome-store-bridge-runtime
dotnet run --project desktop.tests/MishaWeb.SmokeTests.csproj -c Release --no-build --no-restore -- --check-chrome-store-package <extension-id>
```

`npm run desktop:memory-churn` is an opt-in live regression for retained memory. It builds the test-only launcher, opens only tokenized `127.0.0.1` pages in `BrowserMode.Private`, repeatedly creates and closes bounded tab batches containing same-origin frames, then requires the process tree to settle near its pre-churn baseline. It records every cycle and cooldown sample in `artifacts/memory-churn/memory-result.json`, closes only the exact launcher PID, waits for its descendants, and deletes only its uniquely named temporary WebView2 profile. The normal MishaWeb profile is never selected by this command.

The release build targets x64 deliberately so it ships only the matching WebView2 loader. No MishaWeb source-code license is inferred or supplied by the release tooling.
