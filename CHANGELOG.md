# Changelog

## Unreleased

### Ad blocking correctness follow-up

- Restored 315 wildcard `||host` rules that a single-label-host relaxation had silently disabled. `*` and `?` are host terminators, so `||cacheserve.*/promodisplay/` was being read as the host `cacheserve`, yielding a path pattern of `*/promodisplay/*` that can never match. A wildcard host is now a glob and falls back to the generic matching path.
- Fixed a compile abort introduced while optimizing the `$badfilter` identity key. A hand-written span buffer overflowed on any rule with more options than its fixed capacity, and because the compile has no per-line recovery the entire filter set was discarded: the shipped 20-source catalog silently reduced to the 88 built-in fallback rules while reporting success. The key is built with a bounded, allocation-safe sort again, and is skipped entirely for lists that contain no `$badfilter`. Measured over the real 20-list catalog: 164,754 unique lines, 115,602 network rules, 31,135 cosmetic rules, 17,574 unsupported candidates, 0 capacity drops, identical to the pre-regression rule set. Compile time is within run-to-run noise of the unguarded pipeline (about 0.5 s either way) and allocation is about 1.6 MiB higher than the unguarded baseline, not lower: the guard saves work only for lists that contain no `$badfilter`, and the standard catalog does contain them.
- Bounded `$denyallow` to its own value. Absorbing the whole remainder of the option text turned a trailing `$third-party` into a denyallow domain and dropped the rule's third-party scoping, so it began firing on first-party requests. A value now continues only until a recognized option keyword, so single-label hosts and CIDR blocks stay in the list. This engine has no CIDR matching, so a range entry is dropped rather than allowed to fail the parse and delete the whole rule.
- Restored `$badfilter` matching for space-padded options, where a hand-written trim measured its length from the wrong end and left a trailing space in the comparison key.
- Classified subframes from URLs recorded by each frame's own navigation event. `RequestedSourceKind` cannot identify them: per the WebView2 SDK it reads `Document` for the main page, dedicated workers, iframes, and the shared-worker main script alike, so the previous fallback never fired for an iframe and instead mislabelled worker scripts as subdocuments.
- Accept the comma form of `$denyallow`. Splitting the option text on commas truncated the value, left its tail looking like a separate option, and deleted the whole rule by failure rather than by error.
- Restored the reordered-`$badfilter` regression test's second assertion, which had been an exact duplicate of the first and so proved nothing.
- Scoped the subframe URL set to the document generation it was recorded in, instead of clearing it by hand at four separate transition sites. Bounded at 128 entries. A URL recorded as a subframe under one document no longer classifies as one under the next, and the four explicit reset calls are gone, so there is no longer a call that can be forgotten. Two of the four sites were missed by the source-scanning guard that was meant to catch exactly that.
- Reaching the subframe URL bound now evicts the oldest entry instead of clearing the whole set. Clearing dropped every known subframe at once, so a document with more distinct frame URLs than the bound lost all of its classifications at the moment it crossed the line and those iframes were treated as top-level documents, which is the failure the feature exists to prevent.
- Gave the toolbar status label its own single-line, vertically centred paint using `TextRenderer` with an end ellipsis. The label previously wrapped onto a second line in the loaded chrome. An intermediate attempt pinned `MaximumSize.Height`, which caps the control rather than the text and pushed the status out of line with the surrounding buttons by 11 px, so the label now carries no size cap at all; the toolbar column already bounds its width. Measured on a fresh render, the status ink centres at y 79.5, matching the omnibox field and within 0.5 px of every glyph in the button row.

### Known issue

- The memory acceptance probe (`npm run desktop:memory-churn`) fails on this machine, and which gates fail varies between runs of the same tree. Measured runs of this change have failed four, three and two gates respectively; the latest run failed two, `final-private-mib=490.8` against 450 and `churn-retained-private-mib=151.2` against 96, with `peak-private` passing at 500.3 against 525. A gate whose failure count moves between two and four, with roughly 50 MiB of run-to-run spread against a 450 MiB threshold, cannot accept or reject a change. The limits are left unchanged here because silently re-tuning a gate to make it pass is worse than a documented failure, but the gate needs a per-environment WebView2 baseline with the assertion expressed as `baseline + headroom`, or a machine-readable waiver keyed to that baseline so CI reports a known environmental breach instead of a bare `FAIL`. That is a change to the probe's criteria, not to the product.

  Every gate that can actually detect a managed leak passes: per-cycle slope 2.9 MiB against a limit of 24, cooldown plateau range 9.5 against 48, retained working set −484.1 MiB against 160, process delta 0, handle delta 218, thread delta 5, and the managed host process flat at 99.7 MiB. The breach is the WebView2 GPU process holding 289.8 to 331 MiB of private commit against roughly 9 MiB of working set: reserved, untouched address space in a Chromium helper rather than MishaWeb memory. Across 12 churn cycles the host grows about 0.8 MiB per cycle and settles, the renderer is flat, and working set falls. Memory is handed back, which a leak does not do.

  The GPU rasterization flags were the obvious suspect and were measured rather than assumed. `MISHAWEB_DISABLE_GPU_RASTERIZATION=1` is a diagnostic switch that disables them for a single run; it exists in the app because `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` can only append flags, so there is no way to reach them from outside for an A/B. A 12-cycle A/B on identical hardware:

  | | GPU private | final private | churn retained | verdict |
  | --- | --- | --- | --- | --- |
  | flags on (default) | 329.0 MiB | 538.3 | 201.4 | FAIL |
  | flags off | 317.1 MiB | 530.2 | 190.7 | FAIL |

  The flags account for about 12 MiB, under 4% of the breach, and the same private-bytes gates still fail without them. The GPU process commits 329.0 MiB with the flags and 317.1 MiB without, so it is not near zero either way. The flags are retained, since they are worth far more for rendering than the 12 MiB they cost.

### Ad blocking correctness

- Stopped `$redirect=`, `$empty`, `$mp4`, `$all` and `$priority=` rules from silently becoming hard blocks. A request-cancellation engine cannot honour a response rewrite, and compiling one as a block turned a 1x1 pixel swap into a broken image and a `noopjs` redirect into a thrown error. These rules are now dropped and counted as unsupported, matching the existing `$redirect-rule=` behaviour.
- Fixed `$badfilter` so it disables its rule even when the list reorders options between the two (`||x^$domain=a.com,script` vs `@@||x^$script,domain=a.com`). Comparison is now on a canonical pattern-plus-sorted-options key instead of raw text.
- Fixed cosmetic `$badfilter`. `example.com##.ad$badfilter` was parsed as a live rule hiding a selector literally named `.ad$badfilter`, and the real `example.com##.ad` rule stayed active. Badfilters are now resolved before the cosmetic and hide-disable parsers run, and a `$` is rejected in cosmetic selectors.
- Fixed `$denyallow`, which was evaluated backwards *and* against the wrong URL. It is a source-document condition, but it was merged into the exclusion list matched against the request host, and the `~`-negated entries were treated as further exclusions. Every `$denyallow` rule from the configured lists was mis-evaluated.
- Fixed an off-by-N in `||host` rules with dot padding: `||.ads.example.com/banner` sliced its path remainder from the untrimmed pattern and matched `/m/banner`.
- `||host` rules now accept single-label hosts, so `||localhost^` and `||intranet^` cover the host and its subdomains instead of never matching.
- Classified subframes correctly when WebView2 refuses the `Sec-Fetch-Dest` header read. Previously every subframe was reported as a top-level document, so `$document` rules blocked iframe loads and `$subdocument` rules never fired. `RequestedSourceKind` is used as the fallback.
- Cosmetic element hiding now covers `about:blank`, `about:srcdoc` and `data:` frames, which inherit the parent origin and are a standard container for injected ad markup.
- Cosmetic CSS no longer silently disappears when a client-side redirect or a History API rewrite changes the URL before `DOMContentLoaded`, and it is removed when a page ends up with no cosmetic rules instead of leaving the previous page's stylesheet applied.
- "Disable shield for this site" now also reverts YouTube's network experiment flag overrides, and the per-tab control channel survives a shield toggle so a later "disable on this page" still reaches the live document.

### Ad blocking stability and performance

- Fixed a lock-safety defect in the filter loader: `CancellationTokenSource.Cancel()` ran while `loadSync` was held, so task continuations could resume on the locked thread and the loader's `finally` could dispose a source that was still mid-cancel.
- Gave the HTTP cache-validator metadata a GUID-suffixed temp name, matching the list payloads. Overlapping generations shared one fixed `.tmp` path and deleted each other's file.
- Swept abandoned `*.tmp` download leftovers older than the cache lifetime, which previously accumulated across restarts after a hard kill.
- Replaced wholesale `Clear()` on the bounded per-thread request/source/host/suffix caches with partial eviction, and raised the source-URL cache from 16 to 64 entries. The 16-entry cache was exhausted by a handful of requests, forcing a `Uri` reparse on every WebView2 policy hook.
- Registered the `WebResourceRequested` filter with the tab's teardown list, so it no longer depends on `RemoveAdBlockFiltering` running before disposal.
- Read the top-level `CoreWebView2.Source` COM property once per request instead of twice, and hoisted the blocked-response stub bodies out of the per-request path.
- Aligned the two divergent copies of `MapResourceType` and documented that they must stay in step.
- YouTube player: the ad-time mute and 16x speed-up are now reverted on client-side navigation as well as on ad completion, and the blocker's own `volumechange` no longer gets recorded as a user mute preference. Previously navigating away mid-ad left YouTube playing silently at 16x for the rest of the session.

### Documentation

- Added a prominent Windows-only warning to the readme, with an explanation of what a macOS edition would require. MishaWeb is built on Windows Forms and WebView2; neither has a macOS implementation, and a Mac port would need both a new UI framework and a WebKit-based ad blocker using content-rule syntax rather than the ABP/uBO rules compiled here.

- YouTube ad blocker: stopped cosmetic `:has()` rules from hiding an entire Shorts shelf or search row when only one promoted item inside it was an ad, and extended coverage to the current desktop, mobile, Shorts, and player ad renderers.
- YouTube ad blocker: matched network-layer and document-layer YouTube domains from one audited list (`youtube.com`, `youtube-nocookie.com`, `youtubekids.com`) by DNS suffix instead of URL substring, so nocookie embeds keep their player bootstrap and lookalike hosts such as `myyoutube.com` no longer receive YouTube-shaped ad stubs or a reflected request `Origin`.
- YouTube ad blocker: cut per-poll main-thread work — the watch query string is parsed once per navigation, `getStatsForNerds`/`getPlayerStateObject` are called at most once per pass, the skip-button lookup is a single combined selector that clicks each control once per ad, the per-poll enforcement probe no longer runs document-wide `:has()` compounds, and ad-class churn no longer drives an unbounded pass per mutation.
- YouTube ad blocker: parse the player's buffer-health and resolution stats numerically instead of comparing display strings, so the transport-stall recovery keeps working if YouTube changes its formatting, while a missing field still never reads as a stall.

- Moved Windows builds to .NET 10 LTS with an SDK pin, locked NuGet dependency graphs, deterministic warning-as-error builds, and Windows CI coverage.
- Updated the pinned WebView2 SDK and embedded x64 loader to stable 1.0.4129.50, with refreshed lock and redistribution metadata.
- Replaced direct publish-to-destination commands with isolated, allowlisted staging and transactional promotion so stale development files cannot leak into release folders.
- Added x64/version validation, SHA-256 manifests, redistribution notices, optional pre-manifest Authenticode signing, and post-promotion release verification.
- Added native WebView2 browser-extension support with Chrome Web Store URL/ID import, authenticated and bounded CRX3/CRX2/ZIP extraction, stable managed storage, unpacked-folder loading, declared-permission review, and an accessible install/list/enable/disable/remove manager.
- Made the address and search bar select its full current value whenever it receives focus or is clicked.
- Connected trusted **Add to Chrome** clicks to MishaWeb's verified native installer and added an **Install in MishaWeb** fallback on Store listings, avoiding WebView2's interrupted browser-owned download path.
- Added extension popup/options access from the manager and preserved the existing top-level JavaScript user-script feature as a clearly separate surface.
- Isolated private windows onto a dedicated InPrivate profile so normal-profile extensions cannot appear there.
- Added offline archive/signature/ownership hardening checks, a live WebView2 lifecycle probe covering install, disable, enable, profile persistence, private isolation, and removal, and a live Chrome Web Store download/install/remove probe using the actual runtime version.
- Fixed Messenger call buttons that open a temporary `about:blank` window by preserving WebView2 popup bootstrap URLs until the call tab is attached.
- Added direct per-site microphone and camera Allow/Ask/Block controls, request-time permission prompts, and Windows privacy-settings shortcuts.
- Kept camera/microphone-enabled tabs awake during calls, including while backgrounded or minimized.
- Preserved same-origin secure call signalling on any HTTPS site after microphone or camera access is granted, with narrow preflight compatibility for Messenger/Facebook, Discord, and Zoom while retaining the site shield for ordinary and third-party requests.
- Switched WebView2 tracking prevention from Strict to Balanced to preserve tracking protection without breaking real-time communication sites.
- Kept unfinished address-bar and start-page input intact, made bare `Enter` submit the typed Google search instead of an implicit history match, and limited direct suggestion navigation to explicit selection.
- Recovered signed-in YouTube playback from the inline `enforcementMessageViewModel` response with a bounded player retry, then removed the stale enforcement surface only after playable media returned.
- Added fail-open first-load recovery for YouTube's exact zero-buffer/zero-resolution “This content isn't available, try again later” transport stall, preserving explicit start times and never retrying live, captcha, mismatched, or ordinary unavailable videos.
- Expanded YouTube ad-metadata pruning to current player, playlist, watch, and get-watch payloads while restoring temporary recovery context after success.
- Prevented asynchronous background-tab suspension from touching or logging WebView2 controls that were disposed during tab teardown.
- Made Memory Saver's three most recently used ordinary sites a per-window no-discard resident set, so normal switching among frequently used pages never forces a refresh; Ultra-light may suspend them but cannot unload them.
- Added bounded Standard-mode cleanup for older inactive pages, verified-pressure acceleration, and capped failed-suspend retries so long sessions converge without periodic COM or diagnostic growth.
- Explicitly detached WebView2 core/frame/context-menu handlers and released hidden dialog tokens, pending COM-backed work, and suggestion indexes during navigation, replacement, hiding, and disposal.
- Prevented WebView2 frame-destruction callbacks from calling native event removers on an already-destroyed frame, eliminating the deterministic `0x80000003` breakpoint crash seen on iframe-heavy YouTube pages.
- Added an isolated private-profile tab-churn memory probe with post-cleanup plateau, slope, process, handle, and thread acceptance checks; it cannot run against or modify the normal browser profile.

## 2.2.0 - 2026-07-26

This performance and protection release keeps the compact pink bunny interface while reducing repeated CPU work and allocation in the browser's busiest native paths.

### Lightweight and fluid

- Added safe multicore managed-code startup profiling for faster repeat launches without prewarming WebView2, filters, tabs, or services.
- Replaced repeated nested start-page JPEG scaling with one bounded, shared 24-bit viewport frame; transparent descendants copy only their exact aligned slice, start-page tab switches reuse it, and the final hidden, minimized, or disposed page releases it instead of retaining about 4.5 MiB.
- Rebuilt local address suggestions around a state-aware index and bounded top-result selection. The 500-entry stress fixture fell from 1,223,208 to 832 allocated bytes per query while keeping the ranking checksum unchanged.
- Replaced allocation-heavy blocker candidate iterators, URI parsing, host suffix construction, regex-based compile indexing, and bad-filter option scanning with bounded caches and allocation-light scans.
- Stored singleton host/token buckets directly instead of allocating a list plus backing array for every filter key; the live 20-list fixture retained about 34.35 MiB, down from 43.44 MiB before the compact index pass.
- Kept the framework-dependent standard single-file package after measuring ReadyToRun: its small launch gain did not justify the larger executable and higher working memory.

### Stronger blocking without silent loss

- Expanded the verified filter catalog from 18 to 20 sources with Brave Unbreak and Brave first-party regional rules.
- Added conditional ETag/Last-Modified refresh metadata. Unchanged `304` responses no longer download list bodies or trigger recompilation, while last-known-good and atomic replacement behavior remain intact.
- Added compilation diagnostics for accepted network/cosmetic rules, unsupported candidates, and capacity drops. The release validation loaded 121,951 network and 31,409 cosmetic rules with zero capacity drops.
- Added a cache-miss benchmark with 4,096 unique request URLs in addition to the warm repeated-request benchmark.

### Verification and maintenance

- Added suggestion-index mutation tests, unique-URL and live-list blocker benchmarks, live capacity-drop gating, backdrop-cache ownership/disposal/offscreen-render tests, and console-safe test output.
- Updated product, assembly, package, manifest, documentation, and filter-download metadata to version 2.2.0.

## 2.1.5 - 2026-07-26

- Removed the “Light on memory. Built for the open web.” tagline and its reserved layout row.
- Reduced the central composition to a 680-logical-pixel maximum width with tighter logo, title, search, heading, and quick-link spacing.
- Added layout assertions that keep the composition compact and horizontally centered across DPI and window-size changes.

## 2.1.4 - 2026-07-26

- Made standard Memory Saver the default resource mode and added a one-time profile migration that preserves later manual mode changes.
- Removed the Lean browsing and Tab workspace cards from the start page to expose more of the bunny night-garden background.
- Reduced pinned and displayed quick links from six to three, producing a single focused row on normal-width windows.

## 2.1.3 - 2026-07-26

- Eliminated resize ghosting by making every custom search and start-page interaction surface paint a fully opaque frame instead of repeatedly alpha-compositing stale buffers.
- Added rounded clipping to the smart-search surface so child controls cannot escape its border at restored-window sizes.
- Added regression coverage that enforces opaque resize surfaces while retaining the transparent outer background composition.

## 2.1.2 - 2026-07-26

- Replaced unreliable WinForms child transparency with exact background-slice repainting, fixing the black outer composition in the real interactive window as well as off-screen renders.
- Made the search-glyph cell explicitly opaque and texture-free so no decorative bunny can bleed underneath it.
- Added production-source regression checks for the real-window painting path.

## 2.1.1 - 2026-07-26

- Removed the large outer start-page fill so the bunny night-garden artwork remains fully visible behind the floating content.
- Removed decorative bunny texture from beneath the smart-search glyph, leaving a clean standalone pink search icon.
- Added regression checks for both visual fixes and republished the versioned executable.

## 2.1.0 - 2026-07-26

This maintenance release restores the complete v2 interface after a frontend regression, keeps the lightweight native architecture, and introduces a new generated bunny identity.

### Frontend restoration and polish

- Restored the complete renderer-free start page: translucent feature card, tagline, live Lean browsing and Tab workspace chips, responsive six-item quick-link grid, contextual quick-link actions, and adaptive compact layouts.
- Reconnected quick-link population and refresh behavior in every native new tab, including saved-item management and local context actions.
- Replaced the old badge with a purpose-generated kawaii bunny logo across the start page, title bar, executable icon, and Windows shell sizes. The logo is decoded once from a compact embedded runtime asset; high contrast and decode failures retain the native vector fallback.
- Repaired responsive smart-search sizing so narrow windows genuinely enter compact mode instead of being silently clamped to desktop width.
- Removed corrupted UI glyphs and restored readable arrows, separators, ellipses, status text, private-window labels, and close controls.
- Hardened auxiliary dialogs, suggestion surfaces, and custom controls for high-DPI scaling, keyboard focus, high contrast, and usable on-screen bounds.

### Reliability and release safety

- Removed the smart-search catch-all fallback that could leave a partially initialized control tree or duplicate event handlers.
- Added geometry, generated-artwork, icon-frame, Unicode-integrity, responsive-DPI, and production-wiring regression coverage.
- Made every publish command run the strict build and smoke suite first, preventing a broken frontend from being packaged.
- Updated product, assembly, package, and documentation metadata to version 2.1.0.

## 2.0.0 - 2026-07-18

This release rebuilds MishaWeb around its two priorities: very low idle/resource cost and strong blocking that preserves normal site behavior.

### Resource efficiency

- Replaced the old multi-asset start-page package with one optimized 275 KB bunny night-garden backdrop, decoded once and shared process-wide. The richer new-tab experience remains renderer-free, static, and idle-work-free.
- Removed a redundant second WebView2 loader from the single-file bundle while retaining the verified embedded bootstrap copy.
- Reworked background-tab lifecycle planning into a bounded, allocation-light policy with pressure-aware discard, suspend, and low-memory targeting.
- Kept reduced website motion optional and compatibility-safe; it no longer replaces animation APIs or cancels page-owned animations.
- Streamed filter downloads into bounded temporary files, refreshed only stale sources, retained last-known-good lists, skipped recompilation when downloaded bytes are unchanged, and ignored unowned or oversized cache files.
- Reduced hot-path blocker allocations and bounded the cosmetic CSS cache by both entry count and total bytes.

### Blocking and compatibility

- Expanded parsing and regression coverage for network types, exceptions, third-party/domain constraints, Chromium conditionals, cosmetic rules, and `$popunder`.
- Made built-in whole-host safety rules third-party-only so directly visited sites and their first-party assets continue to load.
- Kept authored document rules effective while limiting shortener/redirect heuristics to redirected or scripted navigations; explicit visits remain usable.
- Scoped YouTube response filtering to known player/ad-metadata endpoints, leaving browse, search, comments, thumbnails, and media playback paths alone.
- Replaced continuous cleanup polling with mutation/event-driven cleanup plus adaptive, visibility-aware fallbacks.

### Interface and maintenance

- Re-established MishaWeb's pink identity with a modern deep-berry, blush, coral-pink, and soft-orchid native design across the frame, start page, suggestions, dialogs, menus, and feature surfaces.
- Evolved the v1 pink cosmic wallpaper into a modern bunny night garden with flowers, glowing ribbons, stars, hearts, a bunny moon, a sitting bunny, and two peeking friends; retained the native bunny badge and high-contrast fallback without animation or background work.
- Made the start-page smart search layout consistently DPI-scaled and its disabled controls fully opaque, preventing resize/maximize paint ghosts from appearing as overlapping controls.
- Made toolbar icon painting deterministic and DPI-aware: vector state no longer shares native button text, translucent layers clear cleanly between frames, and status yields before it can crowd the address bar or commands.
- Extended the global website theme beyond `prefers-color-scheme` with a reversible per-WebView automatic-dark renderer override, so explicitly light web apps such as Gmail and Drive follow dark mode while switching back to light clears the override without reloading pages.
- Added system high-contrast colors and focus treatment to the custom-drawn browser chrome.
- Updated product, assembly, package, documentation, and HTTP client metadata to version 2.0.0.
- Expanded strict smoke, compatibility, lifecycle, visual-render, and allocation benchmark coverage.
