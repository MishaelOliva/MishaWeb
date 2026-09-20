# MishaWeb — High-Performance Desktop Browser & Custom Engine Subsystems

[![Windows Release Verification](https://github.com/MishaelOliva/browser-engine/actions/workflows/windows.yml/badge.svg)](https://github.com/MishaelOliva/browser-engine/actions/workflows/windows.yml)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-blue.svg)]()
[![Framework](https://img.shields.io/badge/.NET-10.0%20LTS-purple.svg)]()
[![Language](https://img.shields.io/badge/language-C%23%2013-blue.svg)]()
[![Tests](https://img.shields.io/badge/smoke%20tests-549%20passed-success.svg)]()
[![Telemetry](https://img.shields.io/badge/telemetry-zero%20(100%25%20local)-orange.svg)]()

> **MishaWeb** is an enterprise-grade, lightweight Windows desktop web browser engineered from first principles in **C# (.NET 10 LTS)** and **WinForms**, hosting the **Microsoft Edge WebView2 Evergreen Runtime**. It incorporates custom high-throughput subsystems for network and cosmetic ad-blocking, multi-tier tab lifecycle memory management, native Chrome Extension (CRX) ingestion, zero-allocation address suggestion indexing, and resilient media playback recovery.

---

## Table of Contents

- [Architectural Overview](#architectural-overview)
- [System Architecture Diagram](#system-architecture-diagram)
- [Core Engineering Subsystems](#core-engineering-subsystems)
  - [1. High-Throughput AdBlock & Content Shield Engine](#1-high-throughput-adblock--content-shield-engine)
  - [2. Adaptive Tab Lifecycle & Memory Management](#2-adaptive-tab-lifecycle--memory-management)
  - [3. Native Chrome Extension (CRX) Subsystem](#3-native-chrome-extension-crx-subsystem)
  - [4. Zero-Allocation Local Suggestion Index](#4-zero-allocation-local-suggestion-index)
  - [5. Document-Start YouTube Recovery Engine](#5-document-start-youtube-recovery-engine)
- [Empirical Benchmarks & Verification](#empirical-benchmarks--verification)
- [Q&A](#technical-interview-talking-points)
- [Project Structure](#project-structure)
- [Getting Started & Build Commands](#getting-started--build-commands)
- [License & Attributions](#license--attributions)

---

## Architectural Overview

Modern desktop web applications frequently rely on bloated frameworks like Electron or Chromium Embedded Framework (CEF), shipping an entire Chromium binary and Node.js runtime per application. This results in 300+ MB baseline memory footprints and sluggish cold-start latency.

**MishaWeb takes a different engineering approach:**
- **Zero Frontend Framework Overhead:** The desktop shell, tab strip, smart command bar, and modal interfaces are rendered natively in C# WinForms with custom double-buffered GDI+ drawing routines.
- **Evergreen Chromium Servicing:** Backed by the installed Microsoft Edge WebView2 Evergreen Runtime, ensuring up-to-date web standards compliance (HTML5, WebAssembly, WebGL, WebRTC), hardware video acceleration, and security patches without bundling browser binaries.
- **Strict Memory & Concurrency Bounds:** Thread-safe STA event loops, deterministic COM object detachment, and tiered memory discarding ensure long-running sessions remain stable without memory leaks.

---

## System Architecture Diagram

```
+---------------------------------------------------------------------------------------+
|                                  MISHAWEB DESKTOP HOST                                 |
|                               (C# 13 / .NET 10 LTS WinForms)                          |
+---------------------------------------------------------------------------------------+
        |                                       |                               |
        v                                       v                               v
+-----------------------+       +-------------------------------+       +---------------+
|   NATIVE WINFORMS UI  |       |   CORE SUBSYSTEM ENGINES      |       | EXTENSION MGR |
| - Tab Strip & MRU     |       | - AdBlockEngine (150k+ rules) |       | - CRX3/CRX2   |
| - Smart Command Bar   | <---> | - AddressSuggestionEngine     | <---> | - Store Inter |
| - Native Start Page   |       | - TabLifecyclePolicy          |       | - Manifest    |
| - Double-Buffer GDI+  |       | - YouTubePlaybackRecovery     |       |   Auditing    |
+-----------------------+       +-------------------------------+       +---------------+
        |                                       |                               |
        +---------------------------------------+-------------------------------+
                                                |
                                                v
                        +-----------------------------------------------+
                        |       MICROSOFT EDGE WEBVIEW2 EVERGREEN       |
                        | - CoreWebView2 Environment & Profiles         |
                        | - Normal Profile  vs.  InPrivate Isolated UDF |
                        | - Low Memory Targets & Tab Suspension Engine  |
                        | - Script & Style Document-Start Injection     |
                        +-----------------------------------------------+
                                                |
                                                v
                        +-----------------------------------------------+
                        |          WINDOWS OS & HARDWARE ACCEL          |
                        +-----------------------------------------------+
```

---

## Core Engineering Subsystems

### 1. High-Throughput AdBlock & Content Shield Engine
*Implementation: [`desktop/AdBlockEngine.cs`](desktop/AdBlockEngine.cs), [`desktop/AdBlockDocumentScript.cs`](desktop/AdBlockDocumentScript.cs)*

- **Comprehensive Catalog Compilation:** Automatically loads, parses, and compiles a 20-source filter list catalog including **EasyList**, **EasyPrivacy**, **uBlock Origin** filters, **Brave Unbreak**, and regional rules into an optimized in-memory lookup graph.
- **Zero Capacity Drops:** Release validation verifies compilation of **121,951 network rules** and **31,409 cosmetic rules** without dropping candidate patterns.
- **Zero-Allocation Host & Token Indexing:** Instead of allocating multi-level lists and regex objects for every URL check, rules are mapped into singleton host/token buckets with bounded candidate scans and URI prefix matching.
- **Conditional HTTP Refresh:** Leverages HTTP `ETag` and `Last-Modified` headers; `304 Not Modified` responses prevent downloading list bodies or triggering redundant re-compilations.
- **Document-Start Cosmetic Hiding:** Injects scoped CSS element-hiding rules before the DOM parses, preventing ad flickering and layout shifts.

### 2. Adaptive Tab Lifecycle & Memory Management
*Implementation: [`desktop/TabLifecyclePolicy.cs`](desktop/TabLifecyclePolicy.cs), [`desktop/BrowserPerformance.cs`](desktop/BrowserPerformance.cs), [`desktop/MainForm.cs`](desktop/MainForm.cs)*

- **Multi-Tier Memory Model:**
  1. **Active & Resident MRU Set:** The active site plus the top 2 Most Recently Used (MRU) ordinary sites form a guaranteed resident set. These are never automatically discarded, preserving live form input, scroll position, and instant tab switching.
  2. **Low Memory Target:** Background tabs running active audio, ongoing file downloads, or WebRTC calls receive WebView2's low-memory constraint target while remaining alive.
  3. **Tab Suspension & Discard:** Inactive background pages outside the resident set are gracefully suspended, and discarded after 15 minutes (or 2 minutes under verified system RAM pressure $\ge 75\%$).
- **Deterministic COM Object Lifecycle:** Explicitly detaches WebView2 frame, core, and context-menu handlers upon tab disposal, eliminating memory leaks and resolving the notorious `0x80000003` breakpoint crash triggered when closing iframe-heavy pages.
- **Private Profile Isolation:** `Ctrl+Shift+N` instantiates an isolated `InPrivate` User Data Folder (`UDF`), ensuring zero leakage of cookies, cache, pins, or extensions from normal profiles.

### 3. Native Chrome Extension (CRX) Subsystem
*Implementation: [`desktop/BrowserExtensions.cs`](desktop/BrowserExtensions.cs), [`desktop/ExtensionsManagerForm.cs`](desktop/ExtensionsManagerForm.cs)*

- **Chrome Web Store Ingestion:** Directly parses extension IDs and Store URLs, intercepting the native "Add to Chrome" action to download signed packages via Google's official update API.
- **Archive Extraction & Hardening:** Decodes CRX3, CRX2, and ZIP archives with bounded extraction guards against directory traversal, zip bombs, and symlink attacks.
- **Permission Auditing:** Parses `manifest.json` to present clear, accessible permission disclosures (host permissions, content scripts, background workers) for user approval before installation.
- **Lifecycle Management:** Native WinForms UI for dynamically enabling, disabling, inspecting options/popups, checking updates, and completely purging extensions.

### 4. Zero-Allocation Local Suggestion Index
*Implementation: [`desktop/AddressSuggestionEngine.cs`](desktop/AddressSuggestionEngine.cs), [`desktop/SearchSuggestionService.cs`](desktop/SearchSuggestionService.cs)*

- **Radical Allocation Reduction:** In a 500-entry stress fixture, rebuilding and querying the suggestion engine was optimized from **1,223,208 allocated bytes** down to **832 allocated bytes per query** ($\approx 99.93\%$ memory reduction) while maintaining ranking checksum consistency.
- **Zero Network Telemetry:** Unlike modern browsers that leak every keystroke to third-party search engines, suggestion indexing is performed 100% locally from bookmarks, local history, and typed keywords.
- **Search vs. URL Disambiguation:** Smartly classifies IP addresses, localhost, bare domains, and search queries, ensuring bare `Enter` submits clean search terms without accidental history navigation.

### 5. Document-Start YouTube Recovery Engine
*Implementation: [`desktop/AdBlockDocumentScript.cs`](desktop/AdBlockDocumentScript.cs)*

- **Payload Ad Pruning:** Intercepts and cleans ad metadata from initial player data, `Fetch`, cloned `Fetch`, and `XMLHttpRequest` endpoints before the YouTube player initializes.
- **Anti-Stall Recovery:** Automatically detects and recovers from client-side ad-block enforcement stalls (e.g. `enforcementMessageViewModel` or zero-buffer stalls) via bounded retries, restoring smooth playback without requiring broad `Promise` or `Map` prototype poisoning.

---

## Empirical Benchmarks & Verification

MishaWeb is verified through an automated suite combining unit tests, regression contracts, and live memory soak probes.

```powershell
# Run the complete test suite (restore + build + smoke tests)
npm run desktop:check
```

### Verified Test Results

| Test Category | Contract / Target | Result | Notes |
|---|---|---|---|
| **Automated Smoke Suite** | 549 individual assertions | **100% PASS (549/549)** | Executed via `MishaWeb.SmokeTests.csproj` |
| **Compiler Enforcement** | Warning-As-Error (`-warnaserror`) | **0 Warnings, 0 Errors** | Enforced across all build targets |
| **Filter Compilation** | 20 upstream filter sets | **153,360 rules compiled** | 121,951 network + 31,409 cosmetic, 0 drops |
| **Suggestion Index** | 500-entry stress fixture | **832 bytes / query** | Slashed from 1,223,208 bytes ($\approx 99.9\%$) |
| **Memory Churn Probe** | Bounded tab churn & cooldown | **PASS** | Validated no monotonic handle/COM growth |
| **Profile Isolation** | Normal vs. InPrivate UDF | **PASS** | Complete session and extension separation |

---

## Technical Interview Talking Points

### 1. Why WinForms + WebView2 instead of Electron or Chromium Embedded Framework (CEF)?
> *"Electron bundles both Chromium and Node.js with every application, resulting in 200–300 MB of base RAM usage and binary sizes exceeding 100 MB. By building MishaWeb in C# WinForms on top of Microsoft Edge WebView2 Evergreen, we leverage the OS-shared Chromium binaries and hardware acceleration while keeping our own host binary under 4 MB. Cold startup is sub-300ms, and we retain absolute programmatic control over process lifecycle, network request filtering, and memory targets."*

### 2. How did you handle COM object lifecycles and memory leaks with WebView2?
> *"WebView2 relies heavily on COM interop. If a background tab is closed while asynchronous frame-navigation or context-menu event handlers are still registered, native COM objects remain referenced, leading to the infamous `0x80000003` breakpoint crash. In MishaWeb, we implemented strict deterministic detachment patterns in `TabLifecyclePolicy`: explicitly unhooking DOM listeners, releasing COM pointers, and tearing down frame event subscribers before disposing the parent control."*

### 3. How did you reduce the suggestion engine allocation by 99.9%?
> *"The original suggestion engine repeatedly allocated temporary `List<T>` instances, LINQ iterators, and string substrings on every keystroke. We redesigned the search pipeline around state-aware indexing, pre-allocated bucket arrays, `ReadOnlySpan<char>` slicing, and bounded top-K priority heaps. This dropped allocation from over 1.2 MB per query to just 832 bytes without altering ranking correctness."*

### 4. How does MishaWeb manage memory without disrupting the user?
> *"A naive browser either leaves all tabs in memory until the OS swaps, or aggressively sleeps tabs causing annoying reloads. MishaWeb uses an MRU-backed 3-tier model: the top 3 most recently used tabs form a resident set that is never discarded. Inactive tabs outside this set drop to a low-memory target. Under measured OS memory pressure ($\ge 75\%$), older background tabs are discarded, while tabs playing audio, downloading, or participating in WebRTC calls are explicitly protected."*

---

## Project Structure

```
MishaWeb/
├── desktop/                           # Core C# Desktop Browser Application
│   ├── AdBlockEngine.cs               # 20-source filter compiler & network matcher
│   ├── AdBlockDocumentScript.cs       # Document-start JS injection for cosmetic filtering
│   ├── AddressSuggestionEngine.cs     # Zero-allocation in-memory search index
│   ├── BrowserExtensions.cs           # CRX2/CRX3 unpacker & Chrome Web Store bridge
│   ├── BrowserPerformance.cs          # Working set & memory target management
│   ├── ExtensionsManagerForm.cs       # Extension management WinForms UI
│   ├── MainForm.cs                    # Main browser window, tab strip, & event loop
│   ├── NativeStartPage.cs             # Double-buffered native start page
│   ├── TabLifecyclePolicy.cs          # MRU tab suspension & discard logic
│   ├── WebView2LoaderBootstrap.cs     # Evergreen runtime detection & initialization
│   └── MishaWeb.csproj                # .NET 10 LTS WinForms project definition
│
├── desktop.tests/                     # Comprehensive Verification & Smoke Suite
│   ├── Program.cs                     # 549 smoke tests & performance benchmarks
│   ├── MishaWeb.SmokeTests.csproj     # Test runner project
│   └── packages.lock.json             # Locked NuGet dependency graph
│
├── desktop.youtubeprobe/              # Isolated Memory Soak & Churn Launcher
│   ├── Program.cs                     # Loopback multi-tab churn tester
│   └── MishaWeb.YouTubeProbe.csproj   # Diagnostic probe project
│
├── build/                             # CI/CD & Deployment Automation
│   └── Publish-MishaWeb.ps1           # Transactional staging & single-file packager
│
├── .github/workflows/                 # GitHub Actions CI
│   └── ci.yml                         # Automated build, test, & publish workflow
│
├── global.json                        # Pinned .NET 10.0.103 SDK manifest
├── package.json                       # Developer workflow scripts (`npm run ...`)
├── CHANGELOG.md                       # Comprehensive version history (v2.0 -> v2.2.0)
└── README.md                          # Repository documentation & architecture guide
```

---

## Getting Started & Build Commands

### Prerequisites
- **Operating System:** Windows 10 or Windows 11 (x64)
- **Runtime:** [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (pre-installed on modern Windows)
- **SDK:** [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (Version 10.0.103+ pinned in `global.json`)
- **Node.js:** v18+ (optional, for convenience scripts)

### All-in-One Installer (Automated Setup)

You can install MishaWeb with a single click or command. The installer configures the application in `%LOCALAPPDATA%\Programs\MishaWeb`, validates or compiles single-file `MishaWeb.exe`, creates Desktop and Start Menu shortcuts, and presents a native completion pop-up dialog with an option to immediately launch `MishaWeb.exe`.

- **Option A: 1-Click Batch Launcher**  
  Double-click [`Install-MishaWeb.bat`](Install-MishaWeb.bat) at the root of the repository.

- **Option B: PowerShell Script**
  ```powershell
  # Run directly from local clone:
  .\build\Install-MishaWeb.ps1

  # Or run via npm:
  npm run desktop:install

  # Or standalone 1-liner from PowerShell (downloads entire repo from GitHub):
  irm https://raw.githubusercontent.com/MishaelOliva/browser-engine/main/build/Install-MishaWeb.ps1 | iex
  ```

### Development Workflow

```powershell
# 1. Restore locked dependencies
npm run desktop:restore

# 2. Run full verification suite (strict warning-as-error build + 549 smoke checks)
npm run desktop:check

# 3. Launch MishaWeb directly from source
npm run desktop:run

# 4. Package optimized single-file production release
npm run desktop:publish

# 5. Run live memory churn soak probe (optional)
npm run desktop:memory-churn
```

---

## License & Attributions

Developed by **Mishael Dioneda Oliva** ([@MishaelOliva](https://github.com/MishaelOliva)).  
Filter list rules sourced from EasyList, EasyPrivacy, and uBlock Origin under GPLv3/MPL-2.0. Third-party library notices documented in [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt).
