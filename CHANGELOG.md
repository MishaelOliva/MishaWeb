# Changelog

## Unreleased

### Reader mode and toolbar status

- Gave the toolbar status label its own single-line, vertically centred paint using `TextRenderer` with an end ellipsis. The label previously wrapped onto a second line in the loaded chrome. An intermediate attempt pinned `MaximumSize.Height`, which caps the control rather than the text and pushed the status out of line with the surrounding buttons by 11 px, so the label now carries no size cap at all; the toolbar column already bounds its width.
- Widened that column from 150 to 210 logical px. At 150 the label ellipsized `Ready - 13 blocked - 1 downloading` down to `Ready - 13 blocked - 1 dow…`, hiding the download count entirely. `UpdateResponsiveToolbar` protects the omnibox minimum before showing the column, so the wider status gives up its own space rather than squeezing the address bar. Confirmed in a fresh render: the full text on one line, on the same baseline as the address bar and the surrounding buttons.
- Reader mode scored each candidate twice. The comparison recomputed `score(winner)` on every iteration, so N candidates cost about 2N scores, and each score is four descendant walks plus a read of `innerText` on every anchor, which forces a synchronous layout. Candidates nest, so the same paragraph subtree was re-walked repeatedly. The incumbent's score is now carried in a local and each candidate is scored exactly once; the ranking is unchanged, because nothing in the loop mutates the document and candidates are already deduplicated.
- Added `desktop.tests/ReaderModeScript.test.mjs`, which extracts the injected reader-mode script from the C# source and executes it against a DOM stub. It asserts the highest scoring candidate is the one shown, that each candidate is scored exactly once, and that a page with no substantial content block is left alone. Reintroducing the previous loop fails it with `article was scored 5 times, expected exactly 1`. The assertion counts scorings rather than descendant walks, because consolidating the four selector queries inside `score()` would be a strict improvement to the function this test exists to protect. The script is executed rather than pattern-matched because an earlier ink-probe and file-wide source assertion in this change both passed against the defects they were written to catch.

### Ad blocking correctness follow-up

- Restored 315 wildcard `||host` rules that a single-label-host relaxation had silently disabled. `*` and `?` are host terminators, so `||cacheserve.*/promodisplay/` was being read as the host `cacheserve`, yielding a path pattern of `*/promodisplay/*` that can never match. A wildcard host is now a glob and falls back to the generic matching path.
- Fixed a compile abort introduced while optimizing the `$badfilter` identity key. A hand-written span buffer overflowed on any rule with more options than its fixed capacity, and because the compile has no per-line recovery the entire filter set was discarded: the shipped 20-source catalog silently reduced to the 88 built-in fallback rules while reporting success. The key is built with a bounded, allocation-safe sort again, and is skipped entirely for lists that contain no `$badfilter`. Measured over the real 20-list catalog: 164,754 unique lines, 115,602 network rules, 31,135 cosmetic rules, 17,574 unsupported candidates, 0 capacity drops, identical to the pre-regression rule set. Compile time is within run-to-run noise of the unguarded pipeline (about 0.5 s either way) and allocation is about 1.6 MiB higher than the unguarded baseline, not lower: the guard saves work only for lists that contain no `$badfilter`, and the standard catalog does contain them.
- Bounded `$denyallow` to its own value. Absorbing the whole remainder of the option text turned a trailing `$third-party` into a denyallow domain and dropped the rule's third-party scoping, so it began firing on first-party requests. A value now continues until a token the option parser actually recognizes, so single-label hosts, CIDR blocks and plain domains stay in the list. This engine has no CIDR matching, so a range entry is dropped rather than allowed to fail the parse and delete the whole rule.
- Accepted `$texttrack`, `$eventsource` and `$main_frame` as resource types, which filter lists use and the option loop previously rejected, silently discarding any rule that carried them. Each maps to the same resource class the request classifier assigns, so the rule can actually fire: `$texttrack` to media, `$eventsource` to XHR, and `$main_frame` to a top-level document, matching the ABP meaning. `$main_frame` shares the document class with `$document`, so it also matches iframes; the engine has no top-level-only resource class, so a `$main_frame` rule cannot be narrowed further.
- The `$denyallow` value boundary is derived from the parser's own type mapping rather than a transcribed list, and matched case-insensitively as the option loop is. A name accepted as a boundary but rejected by the option loop would truncate a `$denyallow` value and then delete the whole rule. A resource-type name is now also checked to map to a class the request classifier can produce, which is the check the `$eventsource` mapping was missing.
- A `$denyallow` value now absorbs an unrecognised bare token rather than ending at it, so a rule naming a host the engine cannot parse keeps the rest of its value instead of being deleted. This means `$replace`, `$removeparam`, `$remove`, `$header` and `$csp`, which this engine does not implement, are treated as part of a `$denyallow` value rather than as options. That is inert here because none of them are supported options, and it is the same outcome the engine already gives unsupported options: the rule is dropped, by the same path.
- Restored `$badfilter` matching for space-padded options, where a hand-written trim measured its length from the wrong end and left a trailing space in the comparison key.
- Classified subframes from URLs recorded by each frame's own navigation event. `RequestedSourceKind` cannot identify them: per the WebView2 SDK it reads `Document` for the main page, dedicated workers, iframes, and the shared-worker main script alike, so the previous fallback never fired for an iframe and instead mislabelled worker scripts as subdocuments.
- Accept the comma form of `$denyallow`. Splitting the option text on commas truncated the value, left its tail looking like a separate option, and deleted the whole rule by failure rather than by error.
- Restored the reordered-`$badfilter` regression test's second assertion, which had been an exact duplicate of the first and so proved nothing.
- Scoped the subframe URL set to the document generation it was recorded in, instead of clearing it by hand at four separate transition sites. Bounded at 128 entries. A URL recorded as a subframe under one document no longer classifies as one under the next, and the four explicit reset calls are gone, so there is no longer a call that can be forgotten. Two of the four sites were missed by the source-scanning guard that was meant to catch exactly that.
- Reaching the subframe URL bound now evicts the oldest entry instead of clearing the whole set. Clearing dropped every known subframe at once, so a document with more distinct frame URLs than the bound lost all of its classifications at the moment it crossed the line and those iframes were treated as top-level documents, which is the failure the feature exists to prevent.

### Memory probe

- Fixed the memory acceptance probe, which had been failing for a reason that was not a leak. Its private-bytes gates summed every WebView2 process, including the `gpu` helper, which reserves 290 to 331 MiB of address space it never touches: its working set stays near 9 MiB while its private commit is several hundred, and neither the reservation nor its release is this codebase's. Every private-bytes gate now covers the groups MishaWeb owns, `host`, `browser`, `renderer` and `utility`. The `crashpad` handler is also excluded, as a crash reporter rather than browser memory. The total and the GPU commit are reported alongside them, ungated, so a real change in either stays visible. No threshold was moved.
- Two of those gates could not have failed. `peak-private-mib` read each group's metric as a raw number, but the peak summary wraps every metric in `{ value, atElapsedMs, mib }`, so every group was skipped and the gate reported `actual: 0 passed: true` against a 525 MiB limit. It would have passed for any total the app could reach. The reader now accepts both shapes, and returns null rather than 0 when no owned group is readable, because a zero is indistinguishable from an app that used nothing.
- The monotonic-growth and cooldown-plateau gates were still measured on the totals while retention and slope were measured on the owned groups. Per-sample totals swing between 337 and 659 MiB while the owned series stays flat, so those two gates were reading the GPU helper's churn: one run produced a monotonic run of 4 against a limit of 4, with the owned half never moving. Both now measure owned groups, and a check asserts that a trace which is flat in its owned groups while the GPU oscillates by hundreds of MiB does not read as monotonic growth.
- Replaced a share-of-total floor on the owned groups with a direct presence check. The floor was meant to catch a process group being renamed on the emitting side, which would drop its terms from every private-bytes ceiling, and it caught one rename in four: renaming `browser` removes 60 MiB, `renderer` 24 MiB and `utility` 22 MiB, all while still leaving a large owned total against a large total. The one it did catch, `host`, it caught by 0.001 of a share, roughly twenty times smaller than the run-to-run swing in the GPU helper that sets the share. `owned-groups-readable` now names whichever group is missing, on the final sample, the peak sample and the churn trace. No threshold is involved. The floor was also not listed in the flag table, so nothing could reach it from the command line.
- An unreadable owned group no longer aborts the run. The abort landed in the SKIP bucket, which the probe documents as "we could not run", so a caller distinguishing exit 1 from exit 2 saw a green build on a run where all sixteen acceptance checks had been discarded and `violations` was empty. The group is now a failing check instead: the run is FAIL, the other checks are still reported, and the violation names the group. The null return and the throw remain in the analysis module, which is pure and unit-tested.
- Exported `evaluateAcceptance` behind a `main()` guard so the acceptance gates can be tested. The module ended in `process.exitCode = await main()`, so importing it launched a probe, which is how the floor above shipped with two defects in eight lines, both found by a five-minute browser run rather than a millisecond test. New `desktop.tests/MemoryAcceptanceProbe.test.mjs` covers a healthy run, a peak-shaped sample, and each of the four group renames.
- "Peak private" is a sum of each owned group's highest reading across the run, not the highest total the process set ever reached: the groups peak on different samples, so the sum exceeds any real total, by about 25% in a representative run. This is now stated in the help text as well as in the output comment.
- The probe now passes end to end with every gate real, and no threshold was moved to get there. A representative 12-cycle run on this machine: owned private final 205.3 MiB against 450 and peak 213.6 against 525, churn-retained 22.3 against 96, slope 0.9 against 24, plateau 7.7 against 48, handle delta 270 against 512, thread delta 11 against 32, process delta 0 against 2, retained working set 11 MiB against 160. Owned groups are 38.4% of the total; the remaining 61.6% is the GPU helper, reported ungated at 325.7 MiB final and 178.3 MiB retained.
- The probe's warmup also settled too early to be meaningful: it accepted a steady window taken while the GPU helper was still initialising, when that process reported a 102.9 MiB working set against roughly 9 MiB for every later sample. It now requires the GPU working set to be inside the steady tolerance before declaring steady state.
- Renamed the summary keys. `finalPrivateMiB` and `peakPrivateMiB` printed the totals beside identically named checks that measure only the owned groups, so the summary read as a breach against limits it no longer used, and the owned peak was never printed at all, which is what kept the silent zero invisible. The gated figures are now `finalOwnedPrivateMiB` and `peakOwnedPrivateMiB`, and they are read back from the checks rather than recomputed, so the summary cannot drift from the ceiling it documents. Note that the peak total is a sum taken across samples, so it exceeds any single sample and is not a real peak for the process set.
- `owned-private-share` reported `actual: 0` on a first run because the fraction was passed through a bytes-to-MiB converter as well as its own division, rounding every real figure to zero. It also read its limit from an option the argument parser never populated, so the comparison was against `undefined` and the gate failed for the wrong reason on that run.
- Added churn-analysis coverage for the gate itself: a run whose growth is entirely in the GPU helper passes, the same growth in an owned group still fails, a peak-shaped sample sums rather than zeroing, and a trace with no readable owned groups aborts.
- The GPU rasterization flags were measured rather than assumed. `MISHAWEB_DISABLE_GPU_RASTERIZATION=1` is a diagnostic switch that disables them for a single run; it exists in the app because `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` can only append flags, so there is no way to reach them from outside for an A/B. Over 12 cycles it moved the GPU private commit by about 12 MiB, under 4% of what it was being credited with, so the flags are not the cause and are retained: they are worth far more for rendering than the 12 MiB they cost.

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
