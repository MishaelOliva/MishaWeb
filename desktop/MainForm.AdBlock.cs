using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MishaWeb;

public sealed partial class MainForm
{
    private static readonly string[] AdBlockWebResourceFilterPatterns =
    [
        "http://*",
        "https://*",
        "ws://*",
        "wss://*"
    ];

    private static void AddAdBlockResourceFilters(
        CoreWebView2 core,
        CoreWebView2WebResourceRequestSourceKinds sourceKinds)
    {
        foreach (var pattern in AdBlockWebResourceFilterPatterns)
        {
            try
            {
                core.AddWebResourceRequestedFilter(
                    pattern,
                    CoreWebView2WebResourceContext.All,
                    sourceKinds);
            }
            catch (ArgumentException) when (
                pattern.StartsWith("ws", StringComparison.OrdinalIgnoreCase))
            {
            }
        }
    }

    private static void RemoveAdBlockResourceFilters(
        CoreWebView2 core,
        CoreWebView2WebResourceRequestSourceKinds sourceKinds)
    {
        foreach (var pattern in AdBlockWebResourceFilterPatterns)
        {
            try
            {
                core.RemoveWebResourceRequestedFilter(
                    pattern,
                    CoreWebView2WebResourceContext.All,
                    sourceKinds);
            }
            catch (ArgumentException) when (
                pattern.StartsWith("ws", StringComparison.OrdinalIgnoreCase))
            {
            }
        }
    }

    private async Task InstallAdBlockFilteringAsync(BrowserTab tab)
    {
        var core = tab.Core;
        if (!adBlockEnabled
            || core is null
            || tab.ResourceFilterInstalled
            || tab.DeferResourceFilteringUntilPopupAttached)
        {
            return;
        }

        AddAdBlockResourceFilters(core, DocumentRequestSources);
        EventHandler<CoreWebView2WebResourceRequestedEventArgs> handler = (_, e) =>
        {
            if (!tab.IsClosed && ReferenceEquals(tab.Core, core)) OnWebResourceRequested(tab, e);
        };
        tab.ResourceRequestHandler = handler;
        core.WebResourceRequested += handler;
        // Also register the detach with the tab's teardown list. This filter is
        // installed separately from TrackCoreEvent so it can be deferred for
        // popups, and without this the unsubscribe only ran when
        // RemoveAdBlockFiltering happened to run first. A tab disposed by any
        // other path kept the closure alive on a live core, holding the tab and
        // the core together. Detaching twice is a no-op.
        tab.TrackCoreEventHandler(() =>
        {
            if (tab.ResourceRequestHandler is null) return;
            try { core.WebResourceRequested -= tab.ResourceRequestHandler; }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (COMException) { }
        });
        tab.ResourceFilterInstalled = true;
        TryAssignWorkerAdBlockFilter(tab);
        adBlocker.AcquireConsumer();
        _ = adBlocker.LoadAsync();
        await Task.CompletedTask;
    }

    private async Task InstallAdBlockPageScriptsAsync(BrowserTab tab)
    {
        var core = tab.Core;
        if (!adBlockEnabled || core is null || tab.IsClosed) return;
        RemoveAdBlockPageScripts(tab, core);
        var generation = tab.AdBlockScriptGeneration;

        var exceptions = state.AdBlockExceptionHosts
            .Select(BrowserPolicy.NormalizeExactHost)
            .Where(host => host is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var exceptionJson = JsonSerializer.Serialize(exceptions, BrowserStateJsonContext.Default.StringArray);
        var policyScript =
            "(() => { const hosts = new Set("
            + exceptionJson
            + "); let topHost=location.hostname.toLowerCase();"
            + "try { topHost=top.location.hostname.toLowerCase(); } catch (_) {"
            + "try { topHost=new URL(document.referrer).hostname.toLowerCase(); } catch (_) {} }"
            + "if (hosts.has(location.hostname.toLowerCase()) || hosts.has(topHost)) "
            + "window.__mishaAdBlockEnabled = false; })();";
        var policyId = await core.AddScriptToExecuteOnDocumentCreatedAsync(policyScript);
        if (!adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || tab.AdBlockScriptGeneration != generation)
        {
            TryRemoveAdBlockScript(core, policyId, "Could not remove a stale adblock site policy");
            return;
        }
        tab.AdBlockPolicyScriptId = policyId;

        var controlChannel = tab.AdBlockControlChannel ??=
            "__misha_adblock_control_" + Guid.NewGuid().ToString("N");
        var scriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(
            AdBlockEngine.CreateUniversalBootstrapScript(controlChannel));
        if (!adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || tab.AdBlockScriptGeneration != generation)
        {
            TryRemoveAdBlockScript(core, scriptId, "Could not remove a stale adblock page shield");
            if (tab.AdBlockPolicyScriptId == policyId)
            {
                TryRemoveAdBlockScript(core, policyId, "Could not remove a stale adblock site policy");
                tab.AdBlockPolicyScriptId = null;
            }
            return;
        }
        tab.AdBlockScriptId = scriptId;

        if (IsYouTubeHost(HostFromUrl(tab.Url)))
        {
            await EnsureYouTubeDocumentScriptAsync(tab, core, generation);
        }
    }

    private async Task EnsureYouTubeDocumentScriptAsync(
        BrowserTab tab,
        CoreWebView2 core,
        int generation)
    {
        if (!adBlockEnabled
            || tab.IsClosed
            || tab.YouTubeDocumentScriptId is not null
            || tab.Core != core
            || tab.AdBlockScriptGeneration != generation)
        {
            return;
        }

        var ytScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(
            AdBlockEngine.CreateYouTubeDocumentScript());
        if (!adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || tab.AdBlockScriptGeneration != generation
            || tab.YouTubeDocumentScriptId is not null)
        {
            TryRemoveAdBlockScript(core, ytScriptId, "Could not remove a stale YouTube document shield");
            return;
        }
        tab.YouTubeDocumentScriptId = ytScriptId;
    }

    private bool NeedsUrlScopedScriptsBeforeNavigation(BrowserTab tab, string? url)
    {
        if (isClosing || tab.IsClosed || tab.Core is null || string.IsNullOrWhiteSpace(url)) return false;
        if (adBlockEnabled && tab.YouTubeDocumentScriptId is null && IsYouTubeHost(HostFromUrl(url)))
        {
            return true;
        }
        if (!isPrivateMode)
        {
            if (tab.ChromeStoreBridgeScriptId is null
                && ShouldEnableChromeStoreInstallBridge(url, isPrivateMode))
            {
                return true;
            }
            if (browserExtensions.HasUninstalledScriptsForUrl(url, tab.InstalledUserScriptKeys))
            {
                return true;
            }
        }
        return false;
    }

    private async Task EnsureUrlScopedScriptsRegisteredAsync(BrowserTab tab, string? url)
    {
        if (isClosing || tab.IsClosed || tab.Core is not { } core || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (adBlockEnabled && tab.YouTubeDocumentScriptId is null && IsYouTubeHost(HostFromUrl(url)))
        {
            try
            {
                await EnsureYouTubeDocumentScriptAsync(tab, core, tab.AdBlockScriptGeneration);
            }
            catch (Exception error)
            {
                stateStore.Log("Could not install the YouTube document shield", error);
            }
        }

        if (!isPrivateMode)
        {
            if (tab.ChromeStoreBridgeScriptId is null
                && ShouldEnableChromeStoreInstallBridge(url, isPrivateMode))
            {
                try
                {
                    var channel = tab.ChromeStoreInstallChannel ??= Guid.NewGuid().ToString("N");
                    var bridgeId = await core.AddScriptToExecuteOnDocumentCreatedAsync(
                        BrowserExtensions.CreateChromeWebStoreInstallBridge(channel));
                    if (tab.IsClosed || !ReferenceEquals(tab.Core, core) || tab.ChromeStoreBridgeScriptId is not null)
                    {
                        TryRemoveAdBlockScript(core, bridgeId, "Could not remove a stale Web Store install bridge");
                    }
                    else
                    {
                        tab.ChromeStoreBridgeScriptId = bridgeId;
                    }
                }
                catch (Exception error)
                {
                    stateStore.Log("Could not install the Chrome Web Store button bridge", error);
                }
            }

            foreach (var (key, script) in browserExtensions.GetScriptsForUrl(url))
            {
                if (tab.IsClosed || !ReferenceEquals(tab.Core, core) || isClosing) break;
                if (!tab.InstalledUserScriptKeys.Add(key)) continue;
                try
                {
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
                }
                catch (Exception error)
                {
                    tab.InstalledUserScriptKeys.Remove(key);
                    stateStore.Log("Could not load a browser extension", error);
                }
            }
        }

        tab.PendingScopedScriptNavigationUrl = url;
    }

    private void RemoveAdBlockPageScripts(BrowserTab tab, CoreWebView2 core)
    {
        tab.AdBlockScriptGeneration++;
        if (tab.AdBlockPolicyScriptId is not null)
        {
            TryRemoveAdBlockScript(core, tab.AdBlockPolicyScriptId, "Could not remove the adblock site policy");
            tab.AdBlockPolicyScriptId = null;
        }
        if (tab.AdBlockScriptId is not null)
        {
            TryRemoveAdBlockScript(core, tab.AdBlockScriptId, "Could not remove the adblock page shield");
            tab.AdBlockScriptId = null;
        }
        if (tab.YouTubeDocumentScriptId is not null)
        {
            TryRemoveAdBlockScript(core, tab.YouTubeDocumentScriptId, "Could not remove the YouTube document shield");
            tab.YouTubeDocumentScriptId = null;
        }
    }

    private void TryRemoveAdBlockScript(CoreWebView2 core, string scriptId, string logMessage)
    {
        try { core.RemoveScriptToExecuteOnDocumentCreated(scriptId); }
        catch (Exception error) { stateStore.Log(logMessage, error); }
    }

    private void RemoveAdBlockFiltering(BrowserTab tab)
    {
        if (!tab.ResourceFilterInstalled) return;
        var core = tab.Core;
        if (core is not null)
        {
            if (tab.WorkerResourceFilterInstalled)
            {
                try
                {
                    RemoveAdBlockResourceFilters(core, WorkerRequestSources);
                }
                catch (Exception error) { stateStore.Log("Could not remove the worker adblock filter", error); }
            }
            if (tab.ResourceRequestHandler is not null)
            {
                try { core.WebResourceRequested -= tab.ResourceRequestHandler; }
                catch (Exception error) { stateStore.Log("Could not detach the adblock resource handler", error); }
            }
            try
            {
                RemoveAdBlockResourceFilters(core, DocumentRequestSources);
            }
            catch (Exception error) { stateStore.Log("Could not remove the adblock resource filter", error); }
        }
        if (workerAdBlockFilterOwner == tab) workerAdBlockFilterOwner = null;
        tab.WorkerResourceFilterInstalled = false;
        tab.ResourceRequestHandler = null;
        tab.ResourceFilterInstalled = false;
        adBlocker.ReleaseConsumer();
        if (adBlockEnabled)
        {
            var replacement = tabs.FirstOrDefault(item =>
                item != tab && item.ResourceFilterInstalled && !item.IsClosed && item.Core is not null);
            if (replacement is not null) TryAssignWorkerAdBlockFilter(replacement);
        }
    }

    private void TryAssignWorkerAdBlockFilter(BrowserTab tab)
    {
        if (workerAdBlockFilterOwner is { IsClosed: false, WorkerResourceFilterInstalled: true, Core: not null })
        {
            return;
        }
        if (tab.Core is null || tab.IsClosed || !tab.ResourceFilterInstalled) return;

        try
        {
            AddAdBlockResourceFilters(tab.Core, WorkerRequestSources);
            tab.WorkerResourceFilterInstalled = true;
            workerAdBlockFilterOwner = tab;
        }
        catch (Exception error)
        {
            stateStore.Log("Could not install the worker adblock filter", error);
        }
    }

    private void OnNavigationStarting(BrowserTab tab, CoreWebView2NavigationStartingEventArgs e)
    {
        var pendingFileNavigation = tab.PendingFileNavigationUrl;
        tab.PendingFileNavigationUrl = null;
        var isLocalFileNavigation = BrowserPolicy.TryNormalizeLocalFileUrl(e.Uri, out var normalizedFileUrl);
        var isAuthorizedLocalFileNavigation = isLocalFileNavigation
            && ((pendingFileNavigation is not null
                    && BrowserPolicy.UrlEquals(pendingFileNavigation, normalizedFileUrl))
                || BrowserPolicy.IsLocalFileUrl(tab.Url));
        var targetHost = HostFromUrl(e.Uri);
        var sourceHost = HostFromUrl(tab.Url);
        if (adBlockEnabled
            && !IsExceptionHost(targetHost)
            && !IsExceptionHost(sourceHost)
            && adBlocker.ShouldBlockNavigation(
                e.Uri,
                tab.Url,
                applyRedirectHostHeuristics: e.IsRedirected || !e.IsUserInitiated,
                cacheEvaluation: !isPrivateMode))
        {
            e.Cancel = true;
            tab.RecordBlockedHost(targetHost, "Ad");
            tab.IsLoading = false;
            tab.StatusText = "Blocked an ad redirect";
            if (tab.BlockedRequestCount == 1) ShowTransientStatus("Shield blocked an ad redirect");
            UpdateNavigationChrome();
            UpdateStatus();
            return;
        }

        var trustedExtensionNavigation = IsTrustedExtensionPage(e.Uri, tab.ExtensionOriginId);
        var allowedPopupBootstrap = tab.AllowedPopupBootstrapUrl;
        // This is a one-navigation capability, not a reusable scheme exception.
        // A newly attached CoreWebView2 may already be at about:blank without
        // raising NavigationStarting, so consume it even when the next URI does
        // not match. Otherwise a later page could replay the stale allowance.
        tab.AllowedPopupBootstrapUrl = null;
        var authorizedPopupBootstrap = allowedPopupBootstrap is not null
            && BrowserPolicy.UrlEquals(allowedPopupBootstrap, e.Uri);
        var isAllowedExternalNavigation = IsAllowedExternalNavigation(e.Uri, e.IsUserInitiated);
        if (!authorizedPopupBootstrap
            && !BrowserPolicy.IsSafeTopLevelUrl(
                e.Uri,
                allowFileScheme: isAuthorizedLocalFileNavigation)
            && !trustedExtensionNavigation
            && !isAllowedExternalNavigation)
        {
            e.Cancel = true;
            tab.IsLoading = false;
            tab.StatusText = "Blocked unsafe navigation";
            ShowTransientStatus("Blocked an unsupported or unsafe link");
            UpdateNavigationChrome();
            return;
        }
        if (!authorizedPopupBootstrap
            && isAllowedExternalNavigation
            && !BrowserPolicy.IsSafeTopLevelUrl(
                e.Uri,
                allowFileScheme: isAuthorizedLocalFileNavigation)
            && !trustedExtensionNavigation)
        {
            // LaunchingExternalUriScheme owns confirmation and status. The
            // current document remains loaded, so do not revoke its state.
            return;
        }
        if (StartPage.IsStartPageUrl(e.Uri))
        {
            e.Cancel = true;
            var navigationCore = tab.Core;
            PostUi(() =>
            {
                if (!tab.IsClosed && ReferenceEquals(tab.Core, navigationCore)) ShowStartPage(tab);
            });
            return;
        }

        if (NeedsUrlScopedScriptsBeforeNavigation(tab, e.Uri)
            && !string.Equals(tab.PendingScopedScriptNavigationUrl, e.Uri, StringComparison.Ordinal)
            && tab.Core is { } scopedNavigationCore)
        {
            var targetUri = e.Uri;
            tab.PendingScopedScriptNavigationUrl = targetUri;
            tab.PendingFileNavigationUrl = pendingFileNavigation;
            e.Cancel = true;
            RunUiTask(
                async () =>
                {
                    await EnsureUrlScopedScriptsRegisteredAsync(tab, targetUri);
                    if (isClosing
                        || tab.IsClosed
                        || !ReferenceEquals(tab.Core, scopedNavigationCore))
                    {
                        return;
                    }
                    NavigateTabCore(tab, targetUri);
                },
                "Navigation script setup failed");
            return;
        }

        tab.PendingScopedScriptNavigationUrl = null;
        ClearTabHoverStatus(tab);
        StopFindSession(tab);
        tab.ReaderModeActive = false;
        tab.ContextLinkTarget = null;
        if (!trustedExtensionNavigation) tab.ExtensionOriginId = null;
        try
        {
            if (tab.Core is { } navigationCore)
            {
                navigationCore.Settings.IsWebMessageEnabled =
                    ShouldEnableChromeStoreInstallBridge(e.Uri, isPrivateMode);
            }
        }
        catch (InvalidOperationException)
        {
            // A browser process failure can dispose the core during navigation teardown.
        }

        CancelPermissionsForTab(tab);
        ClearTabMediaPermissions(tab);
        // A top-level navigation replaces the entire child-frame tree. Detach
        // every frame event sink now instead of waiting for COM Destroyed
        // callbacks, which are not guaranteed after a renderer/process exit.
        // Otherwise repeated navigations can leave old frame wrappers and
        // their closures rooted for the rest of the browser session.
        AbandonPendingFrameSetups(tab);
        tab.ReleaseTrackedFrames();
        // Bumping the generation is also what retires the tab's subframe
        // identities: they are recorded against a generation, so a URL seen as a
        // subframe under the outgoing document stops matching here without any
        // explicit reset to forget. See AdBlockSubframeState.
        tab.DocumentNavigationGeneration++;
        tab.IsLoading = true;
        tab.ConsecutiveSuspendFailures = 0;
        tab.StatusText = "Loading\u2026";
        tab.ResetBlockedStats();
        if (tab.PendingRestoreScrollY == 0 && !BrowserPolicy.UrlEquals(e.Uri, tab.Url))
        {
            tab.SavedScrollY = 0;
        }
        tab.IsStartPage = false;
        ReleaseNativeStartPage(tab);
        if (IsTabVisibleInAnyPane(tab) && !isWindowMinimized && !tab.Overlay.Visible)
        {
            if (tab.View is not null)
            {
                var useDarkTheme = WebsiteThemePolicy.ShouldApplyAutoDark(
                    darkModeEnabled,
                    e.Uri,
                    state.DarkModeExcludedHosts);
                tab.View.DefaultBackgroundColor = WebsiteThemePolicy.GetLoadingBackgroundColor(useDarkTheme);
                tab.View.Visible = true;
            }
        }
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            tab.Url = e.Uri;
            tab.ConnectionState = ConnectionState.Loading;
        }
        else if (isAuthorizedLocalFileNavigation)
        {
            tab.Url = normalizedFileUrl;
            tab.KeepAwake = false;
            tab.ConnectionState = ConnectionState.Loading;
        }
        else if (trustedExtensionNavigation)
        {
            tab.Url = e.Uri;
            tab.ConnectionState = ConnectionState.Local;
        }
        if (activeTab == tab)
        {
            SyncAddressBar();
            UpdateNavigationChrome();
            UpdateStatus();
        }
        UpdateTabHeader(tab);
    }

    private void OnWebResourceRequested(
        BrowserTab tab,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!adBlockEnabled || isClosing || tab.IsClosed) return;

        var request = e.Request;
        var requestUri = request.Uri;
        var requestHeaders = request.Headers;
        var resourceContext = e.ResourceContext;
        var requestedSourceKind = e.RequestedSourceKind;
        var topLevelUrl = !string.IsNullOrEmpty(tab.Url) && !tab.IsStartPage
            ? tab.Url
            : (tab.Core?.Source ?? tab.Url);
        var documentSource = (requestedSourceKind & DocumentRequestSources) != 0;
        var workerSource = (requestedSourceKind & WorkerRequestSources) != 0;
        var resourceType = MapResourceType(
            resourceContext,
            resourceContext == CoreWebView2WebResourceContext.Document
                ? GetRequestHeader(requestHeaders, "Sec-Fetch-Dest")
                : null,
            resourceContext == CoreWebView2WebResourceContext.Document
                && tab.AdBlockSubframeUrls.Contains(requestUri, tab.DocumentNavigationGeneration));
        var sourceUrl = GetAdBlockSourceUrl(
            tab,
            requestHeaders,
            requestedSourceKind,
            resourceType,
            topLevelUrl,
            out var requestSourceAttributed);
        if (adBlockExceptionHosts.Count > 0
            && (IsExceptionHost(HostFromUrl(requestUri))
                || (documentSource && IsExceptionHost(HostFromUrl(topLevelUrl)))
                || IsExceptionHost(HostFromUrl(sourceUrl)))) return;
        // A media stream is not an ad simply because it comes from one of
        // YouTube's rotating googlevideo hosts. Filter-list false positives
        // here produce the exact black/error player which a reload may hide.
        // Keep the narrow player bootstrap and videoplayback transport alive;
        // ad response pruning and player-side skip logic still handle ads.
        if (ShouldBypassYouTubePlaybackRequest(topLevelUrl, sourceUrl, requestUri, resourceType)) return;
        if (ShouldBypassCommunicationRequest(topLevelUrl, sourceUrl, requestUri, resourceType, workerSource)) return;
        if (resourceType is AdBlockResourceType.WebSocket or AdBlockResourceType.Media)
        {
            var topLevelOrigin = NormalizePermissionOrigin(topLevelUrl);
            var requestSourceOrigin = requestSourceAttributed
                ? NormalizePermissionOrigin(sourceUrl)
                : null;
            var sourceMatchesGrantedOrigin = tab.HasMediaCapturePermissionForOrigin(requestSourceOrigin);
            var topLevelMatchesGrantedOrigin = tab.HasMediaCapturePermissionForOrigin(topLevelOrigin);
            var hasDistinctRequestSource = requestSourceAttributed
                && requestSourceOrigin is not null
                && !requestSourceOrigin.Equals(topLevelOrigin, StringComparison.OrdinalIgnoreCase);
            var compatibilityPageUrl = workerSource || hasDistinctRequestSource
                ? sourceUrl
                : topLevelUrl;
            var compatibilityOrigin = NormalizePermissionOrigin(compatibilityPageUrl);
            var mediaPermissionGranted = workerSource
                ? requestSourceAttributed
                    && compatibilityOrigin is not null && tabs.Any(item =>
                    !item.IsClosed
                    && item.HasMediaCapturePermissionForOrigin(compatibilityOrigin))
                : requestSourceAttributed
                    && (hasDistinctRequestSource
                        ? sourceMatchesGrantedOrigin
                        : topLevelMatchesGrantedOrigin || sourceMatchesGrantedOrigin);
            if (CommunicationCompatibilityPolicy.ShouldBypassShield(
                    compatibilityPageUrl,
                    requestUri,
                    resourceType,
                    mediaPermissionGranted)) return;
        }
        var evaluation = adBlocker.EvaluateRequest(
            requestUri,
            sourceUrl,
            resourceType,
            cacheEvaluation: !isPrivateMode);
        if (!evaluation.Blocked) return;

        e.Response = CreateBlockedResourceResponse(resourceType, requestUri, topLevelUrl, requestHeaders);
        tab.RecordBlockedAttempt(evaluation);
        if (tab.BlockedRequestCount == 1) ShowTransientStatus("Shield blocked ad and tracker requests");
        else if (activeTab == tab && !statusUiTimer.Enabled) statusUiTimer.Start();
    }

    private string GetAdBlockSourceUrl(
        BrowserTab tab,
        CoreWebView2HttpRequestHeaders headers,
        CoreWebView2WebResourceRequestSourceKinds sourceKind,
        AdBlockResourceType resourceType,
        string topLevelUrl,
        out bool sourceWasAttributed)
    {
        sourceWasAttributed = false;
        var isWorkerSource = (sourceKind & WorkerRequestSources) != 0;
        if (!isWorkerSource
            && resourceType is not (AdBlockResourceType.WebSocket or AdBlockResourceType.Media)
            && tab.AdBlockSubframeUrls.Remaining == 0
            && tab.FrameOrigins.Count == 0
            && BrowserPolicy.IsHttpUrl(topLevelUrl))
        {
            sourceWasAttributed = true;
            return topLevelUrl;
        }
        foreach (var name in RequestSourceHeaderNames)
        {
            try
            {
                if (!headers.Contains(name)) continue;
                var value = headers.GetHeader(name);
                if (BrowserPolicy.IsHttpUrl(value))
                {
                    sourceWasAttributed = true;
                    return value;
                }
            }
            catch (ArgumentException) { }
            catch (COMException) { }
        }
        if (isWorkerSource)
        {
            // Worker events are environment-wide and may be raised on a tab
            // unrelated to the worker. Unknown is safer than pretending the
            // request target is its own first-party source.
            return string.Empty;
        }
        // Reuse the top-level URL the caller already resolved. Reading
        // CoreWebView2.Source is a marshalled COM getter on the UI thread and
        // this handler runs for every subresource of every page.
        return topLevelUrl;
    }

    private static string? GetRequestHeader(
        CoreWebView2HttpRequestHeaders headers,
        string name)
    {
        try
        {
            if (!headers.Contains(name)) return null;
            return headers.GetHeader(name);
        }
        catch (ArgumentException) { return null; }
        catch (COMException) { return null; }
    }

    /// <summary>
    /// Per-tab record of which document URLs belong to a subframe.
    ///
    /// The set is scoped to a document generation rather than cleared by hand at
    /// each transition. Four separate call sites used to have to remember to
    /// clear it, two of them were missed by the test that was supposed to catch
    /// exactly that, and ShowStartPage's no-view branch relies on its reset being
    /// the only one on that path. Scoping the entries to the generation they were
    /// recorded in makes a stale classification unreachable instead: a URL
    /// recorded under one document simply does not match under the next.
    /// </summary>
    internal sealed class AdBlockSubframeState
    {
        private readonly HashSet<string> urls = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> insertionOrder = new();
        private long recordedGeneration = -1;

        public int Remaining => urls.Count;

        public void Record(string url, long documentGeneration)
        {
            if (recordedGeneration != documentGeneration)
            {
                urls.Clear();
                insertionOrder.Clear();
                recordedGeneration = documentGeneration;
            }
            if (urls.Add(url)) insertionOrder.Enqueue(url);
            // Evict the oldest entry rather than clearing the whole set. Clearing
            // at the bound dropped every known subframe at once, so a document
            // with more than MaximumTrackedSubframeUrlsPerTab distinct frames lost
            // all of its classifications and those iframes fell back to being
            // treated as top-level documents, which is the exact failure this
            // exists to prevent.
            while (urls.Count > MaximumTrackedSubframeUrlsPerTab && insertionOrder.Count > 0)
            {
                var oldest = insertionOrder.Dequeue();
                urls.Remove(oldest);
            }
        }

        /// <summary>
        /// True only when this URL was recorded as a subframe of the document
        /// identified by <paramref name="documentGeneration"/>.
        /// </summary>
        public bool Contains(string url, long documentGeneration) =>
            recordedGeneration == documentGeneration && urls.Contains(url);
    }

    /// <summary>
    /// Creates a subframe state pre-loaded with <paramref name="seedCount"/>
    /// entries for one generation, so tests can exercise the bound, the eviction
    /// order and the generation scoping without constructing a WebView2-backed
    /// tab. The seed count is a parameter because seeding to the full bound made
    /// the eviction path fire on the next insert, which masked a missing
    /// generation reset in the tests that were meant to cover it.
    /// </summary>
    internal static AdBlockSubframeState CreateAdBlockSubframeStateForTesting(
        long documentGeneration = 0,
        int seedCount = MaximumTrackedSubframeUrlsPerTab)
    {
        var state = new AdBlockSubframeState();
        for (var index = 0; index < seedCount; index++)
        {
            state.Record($"https://preloaded{index}.invalid/page", documentGeneration);
        }
        return state;
    }
    /// <summary>
    /// Test seam for the resource-type decision. The WebView2 enum is taken by
    /// name so callers outside this assembly, which do not reference the WebView2
    /// SDK, can still drive the table that the live request path uses.
    /// </summary>
    internal static AdBlockResourceType MapResourceTypeForTesting(
        string? webViewContext,
        string? fetchDestination = null,
        bool requestUrlIsKnownSubframe = false) =>
        MapResourceType(
            webViewContext switch
            {
                "Document" => CoreWebView2WebResourceContext.Document,
                "Stylesheet" => CoreWebView2WebResourceContext.Stylesheet,
                "Image" => CoreWebView2WebResourceContext.Image,
                "Media" => CoreWebView2WebResourceContext.Media,
                "Font" => CoreWebView2WebResourceContext.Font,
                "Script" => CoreWebView2WebResourceContext.Script,
                "XmlHttpRequest" => CoreWebView2WebResourceContext.XmlHttpRequest,
                "Fetch" => CoreWebView2WebResourceContext.Fetch,
                "Websocket" => CoreWebView2WebResourceContext.Websocket,
                "Ping" => CoreWebView2WebResourceContext.Ping,
                "EventSource" => CoreWebView2WebResourceContext.EventSource,
                "TextTrack" => CoreWebView2WebResourceContext.TextTrack,
                _ => CoreWebView2WebResourceContext.Other
            },
            fetchDestination,
            requestUrlIsKnownSubframe);

    private static AdBlockResourceType MapResourceType(
        CoreWebView2WebResourceContext webViewContext,
        string? fetchDestination = null,
        bool requestUrlIsKnownSubframe = false)
    {
        if (webViewContext == CoreWebView2WebResourceContext.Document)
        {
            if (fetchDestination is not null
                && (fetchDestination.Equals("iframe", StringComparison.OrdinalIgnoreCase)
                    || fetchDestination.Equals("frame", StringComparison.OrdinalIgnoreCase)))
            {
                return AdBlockResourceType.SubDocument;
            }
            // GetRequestHeader returns null when WebView2 refuses the COM call, and
            // RequestedSourceKind cannot fill the gap: per the WebView2 SDK that
            // flag reads "Document" for requests from the main page, dedicated
            // workers, iframes, and the shared-worker main script alike, so every
            // subframe arrives labelled Document. The only reliable local signal
            // is whether this URL is one we already saw a subframe navigate to.
            if (fetchDestination is null && requestUrlIsKnownSubframe)
            {
                return AdBlockResourceType.SubDocument;
            }
            return AdBlockResourceType.Document;
        }
        return webViewContext switch
        {
            CoreWebView2WebResourceContext.Stylesheet => AdBlockResourceType.Stylesheet,
            CoreWebView2WebResourceContext.Image => AdBlockResourceType.Image,
            CoreWebView2WebResourceContext.Media => AdBlockResourceType.Media,
            CoreWebView2WebResourceContext.Font => AdBlockResourceType.Font,
            CoreWebView2WebResourceContext.Script => AdBlockResourceType.Script,
            CoreWebView2WebResourceContext.XmlHttpRequest => AdBlockResourceType.XmlHttpRequest,
            CoreWebView2WebResourceContext.Fetch => AdBlockResourceType.Fetch,
            CoreWebView2WebResourceContext.Websocket => AdBlockResourceType.WebSocket,
            CoreWebView2WebResourceContext.Ping or CoreWebView2WebResourceContext.CspViolationReport => AdBlockResourceType.Ping,
            CoreWebView2WebResourceContext.EventSource => AdBlockResourceType.XmlHttpRequest,
            CoreWebView2WebResourceContext.TextTrack => AdBlockResourceType.Media,
            _ => AdBlockResourceType.Other
        };
    }

    internal static bool ShouldBypassUnknownWorkerRequest(
        bool workerSource,
        string sourceUrl,
        IEnumerable<string> openPageUrls,
        IEnumerable<string> exceptionHosts)
    {
        // An unattributed worker cannot safely inherit a shield exception from
        // some unrelated open tab. Known source/request origins are evaluated
        // by the normal exact-host checks in OnWebResourceRequested.
        _ = workerSource;
        _ = sourceUrl;
        _ = openPageUrls;
        _ = exceptionHosts;
        return false;
    }

    internal static bool ShouldBypassYouTubePlaybackRequest(
        string? topLevelUrl,
        string? sourceUrl,
        string? requestUrl,
        AdBlockResourceType resourceType)
    {
        if (string.IsNullOrWhiteSpace(requestUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request))
        {
            return false;
        }

        // 1. Any request to *.googlevideo.com is Google's video/audio streaming CDN.
        // It NEVER serves standalone display ads or third-party trackers.
        // Blocking it at the network level causes 0:00/0:00 black-screen player stalls.
        var isGooglevideoHost = request.Host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase)
            || request.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);
        if (isGooglevideoHost)
        {
            return true;
        }

        // 2. YouTube player API and video info endpoints must be bypassed regardless
        // of whether topLevelUrl or sourceUrl represents the YouTube page.
        var isYouTubeRequest = IsYouTubeHost(request.Host);
        if (!isYouTubeRequest)
        {
            return false;
        }

        var path = request.AbsolutePath;
        var isPlayerEndpoint = path.Equals("/youtubei/v1/player", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/get_watch", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/next", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/browse", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/search", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/reel/reel_item_watch", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/reel/reel_watch_sequence", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/att/get", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/get_video_info", StringComparison.OrdinalIgnoreCase);

        if (isPlayerEndpoint)
        {
            return resourceType is AdBlockResourceType.XmlHttpRequest
                or AdBlockResourceType.Fetch
                or AdBlockResourceType.Other;
        }

        var topLevelIsYouTube = !string.IsNullOrWhiteSpace(topLevelUrl)
            && Uri.TryCreate(topLevelUrl, UriKind.Absolute, out var topUri)
            && IsYouTubeHost(topUri.Host);
        var sourceIsYouTube = !string.IsNullOrWhiteSpace(sourceUrl)
            && Uri.TryCreate(sourceUrl, UriKind.Absolute, out var srcUri)
            && IsYouTubeHost(srcUri.Host);

        if (topLevelIsYouTube || sourceIsYouTube)
        {
            if (resourceType is AdBlockResourceType.Media)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ShouldBypassCommunicationRequest(
        string? topLevelUrl,
        string? sourceUrl,
        string? requestUrl,
        AdBlockResourceType resourceType,
        bool workerSource)
    {
        if (resourceType is not (AdBlockResourceType.WebSocket
                or AdBlockResourceType.Media
                or AdBlockResourceType.XmlHttpRequest
                or AdBlockResourceType.Fetch
                or AdBlockResourceType.Script
                or AdBlockResourceType.Ping
                or AdBlockResourceType.Other))
        {
            return false;
        }

        var targetUrl = !string.IsNullOrEmpty(topLevelUrl) && !topLevelUrl.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            ? topLevelUrl
            : sourceUrl;

        if (CommunicationCompatibilityPolicy.ShouldBypassCommunicationRequest(targetUrl, requestUrl, resourceType))
        {
            return true;
        }

        if (workerSource && Uri.TryCreate(requestUrl, UriKind.Absolute, out var reqUri))
        {
            if (CommunicationCompatibilityPolicy.IsProviderTransportHost(reqUri.IdnHost))
            {
                return true;
            }
        }

        return false;
    }

    // AdBlockDocumentScript.cs runs its YouTube runtime on every YouTube-owned
    // domain. The network layer has to agree with that list: if it only knew
    // about youtube.com, an embed served from youtube-nocookie.com could lose
    // its player bootstrap to a filter-list rule. Host matching is by DNS
    // suffix, never by substring, so myyoutube.com stays an ordinary site.
    internal static readonly string[] YouTubeOwnedDomains =
    [
        "youtube.com", "youtube-nocookie.com", "youtubekids.com"
    ];

    // Hosts that only ever carry ad traffic. A blocked request to one of these
    // is answered with an empty JSON body instead of an error status.
    internal static readonly string[] AdNetworkStubDomains =
    [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com"
    ];

    private static bool IsHostWithinDomains(string? host, string[] domains)
    {
        // Runs for every intercepted request, so the suffix test compares spans
        // instead of concatenating ".<domain>" for each candidate.
        if (string.IsNullOrWhiteSpace(host)) return false;
        var value = host.AsSpan().Trim();
        if (value.Length == 0 || value[0] == '[') return false;
        foreach (var domain in domains)
        {
            if (value.Equals(domain, StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Length <= domain.Length) continue;
            var suffixStart = value.Length - domain.Length;
            if (value[suffixStart - 1] == '.'
                && value[suffixStart..].Equals(domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool IsYouTubeHost(string? host) => IsHostWithinDomains(host, YouTubeOwnedDomains);

    internal static bool IsAdNetworkHost(string? host) => IsHostWithinDomains(host, AdNetworkStubDomains);

    private static bool IsYouTubeUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && IsYouTubeHost(uri.Host);

    private CoreWebView2WebResourceResponse? CreateBlockedResourceResponse(
        AdBlockResourceType resourceType,
        string? requestUrl = null,
        string? topLevelUrl = null,
        CoreWebView2HttpRequestHeaders? requestHeaders = null)
    {
        if (environment is null) return null;
        // Shape the stub after the parsed host, never after a substring of the
        // URL text. "https://myyoutube.com/" contains "youtube.com" as a
        // substring, and answering that site's fetch with a fabricated
        // /pagead/id payload would hand a non-YouTube page a YouTube response.
        var isYouTubeContext = IsYouTubeUrl(topLevelUrl)
            || IsYouTubeUrl(requestUrl)
            || (Uri.TryCreate(requestUrl, UriKind.Absolute, out var adRequest)
                && IsAdNetworkHost(adRequest.IdnHost));

        if (isYouTubeContext)
        {
            var origin = "https://www.youtube.com";
            // Echo the requester origin only after it parses as a plain
            // http(s) origin, so a crafted Origin header cannot be reflected
            // into the stub response headers.
            var headerOrigin = NormalizePermissionOrigin(
                requestHeaders is not null ? GetRequestHeader(requestHeaders, "Origin") : null);
            var topLevelOrigin = NormalizePermissionOrigin(topLevelUrl);
            origin = headerOrigin ?? topLevelOrigin ?? origin;

            if (requestUrl is not null && requestUrl.Contains("/pagead/id", StringComparison.OrdinalIgnoreCase))
            {
                var idPayload = YouTubePageAdStubBytes;
                return environment.CreateWebResourceResponse(
                    new MemoryStream(idPayload, writable: false),
                    200,
                    "OK",
                    $"Content-Type: application/json; charset=ISO-8859-1\r\nAccess-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Credentials: true\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: *\r\nVary: Origin");
            }

            if (resourceType is AdBlockResourceType.XmlHttpRequest or AdBlockResourceType.Fetch)
            {
                return environment.CreateWebResourceResponse(
                    new MemoryStream(EmptyJsonObjectBytes, writable: false),
                    200,
                    "OK",
                    $"Content-Type: application/json; charset=utf-8\r\nAccess-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Credentials: true\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: *\r\nVary: Origin");
            }

            if (resourceType is AdBlockResourceType.Script)
            {
                return environment.CreateWebResourceResponse(
                    new MemoryStream(YouTubeScriptStubBytes, writable: false),
                    200,
                    "OK",
                    "Content-Type: application/javascript; charset=utf-8\r\nAccess-Control-Allow-Origin: *");
            }

            if (resourceType is AdBlockResourceType.Ping or AdBlockResourceType.Other)
            {
                return environment.CreateWebResourceResponse(
                    Stream.Null,
                    204,
                    "No Content",
                    $"Access-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Credentials: true\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: *\r\nVary: Origin");
            }
        }

        return resourceType switch
        {
            AdBlockResourceType.Script => environment.CreateWebResourceResponse(
                Stream.Null, 200, "Blocked by MishaWeb", "Content-Type: application/javascript"),
            AdBlockResourceType.Stylesheet => environment.CreateWebResourceResponse(
                Stream.Null, 200, "Blocked by MishaWeb", "Content-Type: text/css"),
            AdBlockResourceType.Image => environment.CreateWebResourceResponse(
                new MemoryStream(TransparentGif, writable: false),
                200,
                "Blocked by MishaWeb",
                "Content-Type: image/gif"),
            AdBlockResourceType.Font or AdBlockResourceType.Media
                or AdBlockResourceType.XmlHttpRequest or AdBlockResourceType.Fetch
                or AdBlockResourceType.Ping => environment.CreateWebResourceResponse(
                    Stream.Null, 204, "Blocked by MishaWeb", string.Empty),
            _ => environment.CreateWebResourceResponse(
                Stream.Null, 403, "Blocked by MishaWeb", "Content-Type: text/plain")
        };
    }

    private void OnDomContentLoaded(BrowserTab tab)
    {
        RunUiTask(
            () => ApplyAdBlockCosmeticsAsync(tab),
            "Could not apply cosmetic ad filters");
    }

    private void OnFrameCreated(BrowserTab tab, CoreWebView2Frame frame)
    {
        var originatingCore = tab.Core;
        if (originatingCore is null
            || tab.AdBlockFrames.Count >= MaximumTrackedFramesPerTab)
        {
            return;
        }
        var frameDocumentGeneration = tab.DocumentNavigationGeneration;
        bool IsCurrentFrame() => originatingCore is not null
            && !tab.IsClosed
            && ReferenceEquals(tab.Core, originatingCore)
            && frameDocumentGeneration == tab.DocumentNavigationGeneration
            && tab.AdBlockFrames.Contains(frame);
        var frameUrl = string.Empty;
        var frameNavigationGeneration = 0L;
        EventHandler<object> destroyedHandler = (_, _) =>
        {
            // WebView2 reports IsDestroyed=true while this callback is running.
            // Calling any remove_* method on that frame can raise a native
            // STATUS_BREAKPOINT instead of returning a catchable COM error.
            // The destroyed frame releases its native event registrations; only
            // forget our managed bookkeeping and delegate owner here.
            AbandonPendingFrameSetup(frame);
            tab.ReleaseDestroyedFrame(frame);
        };
        EventHandler<CoreWebView2NavigationStartingEventArgs> navigationStartingHandler = (_, e) =>
        {
            if (!IsCurrentFrame()) return;
            frameNavigationGeneration++;
            AbandonPendingFrameSetup(frame);
            tab.FrameOrigins.Remove(frame);
            frameUrl = e.Uri;
            // Record the destination so a later request for this same document
            // can be told apart from a top-level navigation, which WebView2
            // labels identically. See MapResourceType.
            if (BrowserPolicy.IsHttpUrl(e.Uri))
            {
                tab.AdBlockSubframeUrls.Record(e.Uri, frameDocumentGeneration);
            }
        };
        EventHandler<CoreWebView2DOMContentLoadedEventArgs> domContentLoadedHandler = (_, _) =>
        {
            var loadedFrameUrl = frameUrl;
            var loadedFrameNavigationGeneration = frameNavigationGeneration;
            var loadedFrameOrigin = NormalizePermissionOrigin(loadedFrameUrl);
            bool IsCurrentLoadedFrame() => IsCurrentFrame()
                && loadedFrameNavigationGeneration == frameNavigationGeneration
                && BrowserPolicy.UrlEquals(frameUrl, loadedFrameUrl);
            if (!IsCurrentLoadedFrame())
            {
                return;
            }
            if (loadedFrameOrigin is not null)
            {
                tab.FrameOrigins[frame] = loadedFrameOrigin;
            }
            QueueFrameSetup(
                tab,
                frame,
                IsCurrentLoadedFrame,
                async () =>
                {
                    if (!IsCurrentLoadedFrame()) return;
                    await RefreshTabMediaPermissionOriginAsync(
                        tab,
                        loadedFrameUrl,
                        frameDocumentGeneration,
                        IsCurrentLoadedFrame);
                    if (IsCurrentLoadedFrame())
                    {
                        await ApplyAdBlockCosmeticsAsync(
                            tab,
                            frame,
                            loadedFrameUrl,
                            originatingCore,
                            frameDocumentGeneration,
                            IsCurrentLoadedFrame);
                    }
                });
        };
        EventHandler<CoreWebView2FrameCreatedEventArgs> frameCreatedHandler = (_, e) =>
        {
            if (IsCurrentFrame()) OnFrameCreated(tab, e.Frame);
        };

        var subscription = new FrameEventSubscription(
            frame,
            destroyedHandler,
            navigationStartingHandler,
            domContentLoadedHandler,
            frameCreatedHandler);
        if (!tab.TryTrackFrame(frame, subscription)) return;
        try
        {
            subscription.Attach();
        }
        catch (Exception error) when (
            error is ObjectDisposedException or InvalidOperationException or COMException)
        {
            tab.ReleaseTrackedFrame(frame);
            stateStore.Log("Could not track a page frame", error);
        }
    }

    private void QueueFrameSetup(
        BrowserTab tab,
        CoreWebView2Frame frame,
        Func<bool> isCurrent,
        Func<Task> execute)
    {
        // WebView2 and WinForms callbacks run on the window's UI apartment.
        // Keep both scheduling and execution there so frame COM wrappers never
        // cross threads. The global bound prevents frame-heavy pages from
        // retaining an unbounded chain of closures while permission work waits.
        Debug.Assert(!InvokeRequired);
        if (frameSetupQueueStopping || isClosing || !isCurrent()) return;

        tab.FrameSetupInProgress.Add(frame);
        if (pendingFrameSetupByFrame.TryGetValue(frame, out var existing))
        {
            // A frame can navigate again while its previous setup is queued or
            // active. Preserve one latest follow-up instead of running stale URL
            // work or allowing duplicate queue entries to accumulate.
            existing.Replace(isCurrent, execute);
            return;
        }

        if (pendingFrameSetups.Count >= MaximumPendingFrameSetups)
        {
            AbandonOldestPendingFrameSetup();
        }

        var work = new FrameSetupWork(tab, frame, isCurrent, execute);
        pendingFrameSetups.Enqueue(work);
        pendingFrameSetupByFrame.Add(frame, work);
        DrainFrameSetupQueue();
    }

    private void DrainFrameSetupQueue()
    {
        Debug.Assert(!InvokeRequired);
        if (drainingFrameSetupQueue || frameSetupQueueStopping || isClosing) return;

        drainingFrameSetupQueue = true;
        try
        {
            while (activeFrameSetupWorkers < MaximumConcurrentFrameSetups
                && pendingFrameSetups.Count > 0)
            {
                FrameSetupWork? next = null;
                var candidatesToInspect = pendingFrameSetups.Count;
                while (candidatesToInspect-- > 0)
                {
                    var candidate = pendingFrameSetups.Dequeue();
                    if (!pendingFrameSetupByFrame.TryGetValue(candidate.Frame, out var current)
                        || !ReferenceEquals(candidate, current))
                    {
                        continue;
                    }
                    if (activeFrameSetups.Contains(candidate.Frame))
                    {
                        pendingFrameSetups.Enqueue(candidate);
                        continue;
                    }

                    pendingFrameSetupByFrame.Remove(candidate.Frame);
                    if (!candidate.IsCurrent())
                    {
                        ReleaseFrameSetupTracking(candidate);
                        continue;
                    }

                    next = candidate;
                    break;
                }

                if (next is null) break;
                activeFrameSetups.Add(next.Frame);
                activeFrameSetupWorkers++;
                RunUiTask(
                    () => ExecuteFrameSetupAsync(next),
                    "Could not finish frame setup");
            }
        }
        finally
        {
            drainingFrameSetupQueue = false;
        }
    }

    private async Task ExecuteFrameSetupAsync(FrameSetupWork work)
    {
        try
        {
            if (!frameSetupQueueStopping && !isClosing && work.IsCurrent())
            {
                await work.Execute();
            }
        }
        finally
        {
            activeFrameSetups.Remove(work.Frame);
            activeFrameSetupWorkers = Math.Max(0, activeFrameSetupWorkers - 1);
            ReleaseFrameSetupTracking(work);
            DrainFrameSetupQueue();
        }
    }

    private void AbandonPendingFrameSetup(CoreWebView2Frame frame)
    {
        Debug.Assert(!InvokeRequired);
        if (!pendingFrameSetupByFrame.Remove(frame, out var abandoned)) return;

        var queuedCount = pendingFrameSetups.Count;
        while (queuedCount-- > 0)
        {
            var candidate = pendingFrameSetups.Dequeue();
            if (!ReferenceEquals(candidate, abandoned)) pendingFrameSetups.Enqueue(candidate);
        }
        ReleaseFrameSetupTracking(abandoned);
    }

    private void AbandonPendingFrameSetups(BrowserTab tab)
    {
        Debug.Assert(!InvokeRequired);
        var queuedCount = pendingFrameSetups.Count;
        while (queuedCount-- > 0)
        {
            var candidate = pendingFrameSetups.Dequeue();
            if (!ReferenceEquals(candidate.Tab, tab))
            {
                pendingFrameSetups.Enqueue(candidate);
                continue;
            }

            if (pendingFrameSetupByFrame.TryGetValue(candidate.Frame, out var current)
                && ReferenceEquals(candidate, current))
            {
                pendingFrameSetupByFrame.Remove(candidate.Frame);
                ReleaseFrameSetupTracking(candidate);
            }
        }
    }

    private void AbandonOldestPendingFrameSetup()
    {
        Debug.Assert(!InvokeRequired);
        while (pendingFrameSetups.Count > 0)
        {
            var abandoned = pendingFrameSetups.Dequeue();
            if (!pendingFrameSetupByFrame.TryGetValue(abandoned.Frame, out var current)
                || !ReferenceEquals(abandoned, current))
            {
                continue;
            }

            pendingFrameSetupByFrame.Remove(abandoned.Frame);
            ReleaseFrameSetupTracking(abandoned);
            return;
        }
    }

    private void StopFrameSetupQueue()
    {
        Debug.Assert(!InvokeRequired);
        frameSetupQueueStopping = true;
        while (pendingFrameSetups.Count > 0)
        {
            var abandoned = pendingFrameSetups.Dequeue();
            if (pendingFrameSetupByFrame.TryGetValue(abandoned.Frame, out var current)
                && ReferenceEquals(abandoned, current))
            {
                pendingFrameSetupByFrame.Remove(abandoned.Frame);
                ReleaseFrameSetupTracking(abandoned);
            }
        }
        pendingFrameSetupByFrame.Clear();
    }

    private void ReleaseFrameSetupTracking(FrameSetupWork work)
    {
        if (!activeFrameSetups.Contains(work.Frame)
            && !pendingFrameSetupByFrame.ContainsKey(work.Frame))
        {
            work.Tab.FrameSetupInProgress.Remove(work.Frame);
        }
    }

    private async Task ApplyAdBlockCosmeticsAsync(
        BrowserTab tab,
        CoreWebView2Frame frame,
        string frameUrl,
        CoreWebView2 originatingCore,
        long documentGeneration,
        Func<bool> isCurrentFrame)
    {
        var core = tab.Core;
        // about:blank, about:srcdoc and data: frames inherit the parent's origin
        // and are a standard container for injected ad markup. They carry no
        // addressable host of their own, so resolve cosmetic rules against the
        // top-level document instead of skipping element hiding for them.
        var ruleHostUrl = BrowserPolicy.IsHttpUrl(frameUrl) ? frameUrl : core?.Source ?? string.Empty;
        if (core is null
            || !ReferenceEquals(core, originatingCore)
            || documentGeneration != tab.DocumentNavigationGeneration
            || tab.IsClosed
            || isClosing
            || !isCurrentFrame()
            || !adBlockEnabled
            || !BrowserPolicy.IsHttpUrl(ruleHostUrl)
            || IsExceptionHost(HostFromUrl(core.Source))
            || IsExceptionHost(HostFromUrl(frameUrl))) return;

        await adBlocker.LoadAsync();
        if (!adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || documentGeneration != tab.DocumentNavigationGeneration
            || !isCurrentFrame()
            || IsExceptionHost(HostFromUrl(core.Source))) return;

        var css = await adBlocker.GetCosmeticCssAsync(ruleHostUrl, cacheEvaluation: !isPrivateMode);
        if (!adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || documentGeneration != tab.DocumentNavigationGeneration
            || !isCurrentFrame()
            || IsExceptionHost(HostFromUrl(core.Source))) return;
        await frame.ExecuteScriptAsync(css.Length == 0
            ? CreateCosmeticStyleRemovalScript()
            : CreateCosmeticStyleScript(
                css,
                ruleHostUrl,
                requireMatchingHost: BrowserPolicy.IsHttpUrl(frameUrl)));
    }

    private async Task ApplyAdBlockCosmeticsAsync(BrowserTab tab)
    {
        var core = tab.Core;
        if (core is null || tab.IsClosed || isClosing) return;
        var pageUrl = core.Source;
        var exception = !adBlockEnabled || IsExceptionHost(HostFromUrl(pageUrl));
        if (exception)
        {
            await DisableAdBlockForCurrentDocumentAsync(tab);
            return;
        }

        await adBlocker.LoadAsync();
        if (!adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || !BrowserPolicy.UrlEquals(core.Source, pageUrl)
            || IsExceptionHost(HostFromUrl(core.Source)))
        {
            return;
        }

        var css = await adBlocker.GetCosmeticCssAsync(pageUrl, cacheEvaluation: !isPrivateMode);
        if (!adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || IsExceptionHost(HostFromUrl(core.Source))) return;
        await core.ExecuteScriptAsync(css.Length == 0
            ? CreateCosmeticStyleRemovalScript()
            : CreateCosmeticStyleScript(css, pageUrl));
    }

    private const int MaximumCachedSerializedCosmeticCssEntries = 32;
    private static readonly object SerializedCosmeticCssCacheSync = new();
    private static readonly Dictionary<string, LinkedListNode<(string Css, string Serialized)>> SerializedCosmeticCssCacheMap =
        new(MaximumCachedSerializedCosmeticCssEntries, StringComparer.Ordinal);
    private static readonly LinkedList<(string Css, string Serialized)> SerializedCosmeticCssCacheOrder = new();

    private static string GetOrSerializeCosmeticCss(string css)
    {
        lock (SerializedCosmeticCssCacheSync)
        {
            if (SerializedCosmeticCssCacheMap.TryGetValue(css, out var existingNode))
            {
                SerializedCosmeticCssCacheOrder.Remove(existingNode);
                SerializedCosmeticCssCacheOrder.AddLast(existingNode);
                return existingNode.Value.Serialized;
            }
        }

        var serialized = JsonSerializer.Serialize(css, BrowserStateJsonContext.Default.String);
        lock (SerializedCosmeticCssCacheSync)
        {
            if (SerializedCosmeticCssCacheMap.TryGetValue(css, out var existingNode))
            {
                SerializedCosmeticCssCacheOrder.Remove(existingNode);
                SerializedCosmeticCssCacheOrder.AddLast(existingNode);
                return existingNode.Value.Serialized;
            }
            while (SerializedCosmeticCssCacheMap.Count >= MaximumCachedSerializedCosmeticCssEntries
                   && SerializedCosmeticCssCacheOrder.First is { } oldest)
            {
                SerializedCosmeticCssCacheOrder.RemoveFirst();
                SerializedCosmeticCssCacheMap.Remove(oldest.Value.Css);
            }
            var node = SerializedCosmeticCssCacheOrder.AddLast((css, serialized));
            SerializedCosmeticCssCacheMap[css] = node;
            return serialized;
        }
    }

    private static string CreateCosmeticStyleScript(
        string css,
        string pageUrl,
        bool requireMatchingHost = true)
    {
        var serializedCss = GetOrSerializeCosmeticCss(css);
        var serializedPageUrl = JsonSerializer.Serialize(pageUrl, BrowserStateJsonContext.Default.String);
        // The guard used to compare the full URL, so a client-side redirect or a
        // History API rewrite before DOMContentLoaded silently dropped the whole
        // stylesheet. Cosmetic rules are host-scoped, so a same-host URL change
        // cannot make them wrong; a host change still must not inherit them.
        // Inherited-origin frames (about:blank, data:) have no hostname of their
        // own to compare against, so the guard is skipped for them.
        return "(() => {"
            + "if (window.__mishaAdBlockEnabled === false || !document.documentElement) return;"
            + (requireMatchingHost
                ? "try {"
                    + $"if (new URL(location.href).hostname !== new URL({serializedPageUrl}).hostname) return;"
                    + "} catch (_) { return; }"
                : string.Empty)
            + "const id='__misha_adblock_remote_style';"
            + "let style=document.getElementById(id);"
            + "if (!style) { style=document.createElement('style'); style.id=id; document.documentElement.appendChild(style); }"
            + $"style.textContent={serializedCss};"
            + "})();";
    }

    /// <summary>
    /// Drops the remote cosmetic stylesheet. Applied when a page now has no
    /// cosmetic rules at all, which otherwise left the previous page's sheet in
    /// place for the lifetime of the document after a client-side navigation.
    /// </summary>
    private static string CreateCosmeticStyleRemovalScript() =>
        "(() => { try { document.getElementById('__misha_adblock_remote_style')?.remove(); } catch (_) { } })();";

    private void ToggleDarkMode()
    {
        darkModeEnabled = !darkModeEnabled;
        var themeGeneration = Interlocked.Increment(ref websiteThemeGeneration);
        foreach (var tab in tabs.Where(item =>
                     !item.IsClosed
                     && !item.IsSuspended
                     && item.Core is not null).ToArray())
        {
            ApplyWebsiteThemeLoadingBackground(
                tab,
                WebsiteThemePolicy.ShouldApplyAutoDark(
                    darkModeEnabled,
                    tab.Url,
                    state.DarkModeExcludedHosts));
            RunUiTask(
                () => ApplyWebsiteThemeAsync(tab, themeGeneration),
                "Could not change the website color preference");
        }

        foreach (var tab in tabs.Where(item => item.IsStartPage && item.Core is not null))
        {
            ShowStartPage(tab, false);
        }
        SavePreferences();
        ShowTransientStatus(darkModeEnabled ? "Website dark mode on" : "Website light mode restored");
    }

    private async Task ApplyWebsiteThemeAsync(BrowserTab tab, long? requestedGeneration = null)
    {
        var generation = requestedGeneration ?? Interlocked.Read(ref websiteThemeGeneration);

        while (!isClosing && !tab.IsClosed)
        {
            var core = tab.Core;
            if (core is null) return;

            var useDarkTheme = darkModeEnabled;
            if (generation != Interlocked.Read(ref websiteThemeGeneration))
            {
                generation = Interlocked.Read(ref websiteThemeGeneration);
                continue;
            }

            var currentUrl = tab.Url;
            try
            {
                if (!string.IsNullOrWhiteSpace(core.Source)) currentUrl = core.Source;
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            var applyAutoDark = WebsiteThemePolicy.ShouldApplyAutoDark(
                useDarkTheme,
                currentUrl,
                state.DarkModeExcludedHosts);
            ApplyWebsiteThemeLoadingBackground(tab, applyAutoDark);
            try
            {
                WebsiteThemePolicy.ApplyPreferredColorScheme(core, useDarkTheme);
                await WebsiteThemePolicy.ApplyAutoDarkModeOverrideAsync(core, applyAutoDark);
            }
            catch (Exception error)
            {
                // The profile preference still works when this optional Chromium capability
                // is unavailable. Theme enhancement failures must never break navigation.
                stateStore.Log("Could not apply the automatic website color policy", error);
            }

            if (ReferenceEquals(tab.Core, core)
                && generation == Interlocked.Read(ref websiteThemeGeneration)
                && useDarkTheme == darkModeEnabled
                && applyAutoDark == WebsiteThemePolicy.ShouldApplyAutoDark(
                    darkModeEnabled,
                    tab.Url,
                    state.DarkModeExcludedHosts))
            {
                return;
            }

            generation = Interlocked.Read(ref websiteThemeGeneration);
        }
    }

    private static void ApplyWebsiteThemeLoadingBackground(BrowserTab tab, bool useDarkTheme)
    {
        if (tab.View is not null)
        {
            tab.View.DefaultBackgroundColor = WebsiteThemePolicy.GetLoadingBackgroundColor(useDarkTheme);
        }
    }

    private void ToggleAdBlocker()
    {
        adBlockEnabled = !adBlockEnabled;
        if (adBlockEnabled)
        {
            foreach (var tab in tabs.Where(item => item.Core is not null))
            {
                // The control channel stays stable across the toggle for the same
                // reason as ToggleSiteShield: it is baked into the live document.
                RunUiTask(
                    async () =>
                    {
                        await InstallAdBlockFilteringAsync(tab);
                        if (tab.Core is not null)
                        {
                            await InstallAdBlockPageScriptsAsync(tab);
                            if (adBlockEnabled && tab.Core is not null) tab.Core.Reload();
                        }
                    },
                    "Could not enable the site shield");
            }
        }
        else
        {
            foreach (var tab in tabs.Where(item => item.Core is not null))
            {
                RemoveAdBlockFiltering(tab);
                RemoveAdBlockPageScripts(tab, tab.Core!);
                RunUiTask(
                    () => DisableAdBlockForCurrentDocumentAsync(tab),
                    "Could not disable the current page shield");
            }
        }
        SavePreferences();
        ShowTransientStatus(adBlockEnabled ? "Ad and tracker blocker on" : "Ad and tracker blocker off");
        UpdateStatus();
    }

    private bool IsExceptionHost(string host)
    {
        if (adBlockExceptionHosts.Count == 0) return false;
        var normalized = BrowserPolicy.NormalizeExactHost(host);
        return normalized is not null && adBlockExceptionHosts.Contains(normalized);
    }

    private void ToggleSiteShield(string host)
    {
        var normalized = BrowserPolicy.NormalizeExactHost(host);
        if (normalized is null) return;
        bool shieldEnabled;
        if (adBlockExceptionHosts.Remove(normalized))
        {
            var existing = state.AdBlockExceptionHosts.FindIndex(item =>
                item.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) state.AdBlockExceptionHosts.RemoveAt(existing);
            shieldEnabled = true;
            // The control channel is deliberately left alone. It is baked into the
            // already-loaded document at injection time, so minting a new name
            // here would leave that document listening on a name nothing
            // dispatches, and a later "disable on this page" would silently do
            // nothing. InstallAdBlockPageScriptsAsync reuses the existing name
            // for future navigations, which is what the listener needs.
            ShowTransientStatus($"Shield restored for {normalized}");
        }
        else
        {
            if (state.AdBlockExceptionHosts.Count >= BrowserStateStore.MaximumAdBlockExceptionHosts)
            {
                ShowTransientStatus("You can save up to 200 site shield exceptions");
                return;
            }
            state.AdBlockExceptionHosts.Add(normalized);
            adBlockExceptionHosts.Add(normalized);
            shieldEnabled = false;
            ShowTransientStatus($"Shield disabled for {normalized}");
        }

        RefreshAdBlockPagePolicies();
        foreach (var matchingTab in tabs.Where(item =>
                     !item.IsClosed
                     && !item.IsStartPage
                     && item.Core is not null
                     && BrowserPolicy.IsExactHost(HostFromUrl(item.Core.Source ?? item.Url), normalized)).ToArray())
        {
            matchingTab.ResetBlockedStats();
            RunUiTask(
                async () =>
                {
                    if (matchingTab.Core is null) return;
                    if (shieldEnabled && adBlockEnabled)
                    {
                        await InstallAdBlockPageScriptsAsync(matchingTab);
                    }
                    else if (!shieldEnabled)
                    {
                        await DisableAdBlockForCurrentDocumentAsync(matchingTab);
                    }
                    if (!isClosing && !matchingTab.IsClosed && matchingTab.Core is not null)
                    {
                        matchingTab.Core.Reload();
                    }
                },
                "Could not reload after changing the site shield");
        }
        if (!isPrivateMode) ScheduleStateSave();
    }

    private void ToggleActiveSiteShield()
    {
        var host = activeTab is null ? null : SitePreferencePolicy.GetHost(activeTab.Url);
        if (host is null)
        {
            ShowTransientStatus("Open a website before changing the site shield");
            return;
        }
        ToggleSiteShield(host);
    }

    private void ToggleActiveKeepAwake()
    {
        var tab = activeTab;
        if (tab is null || tab.IsStartPage)
        {
            ShowTransientStatus("Open a website before protecting it");
            return;
        }
        ToggleTabKeepAwake(tab);
    }

    private void ToggleTabKeepAwake(BrowserTab tab)
    {
        if (tab.IsClosed || tab.IsStartPage)
        {
            ShowTransientStatus("Open a website before protecting it");
            return;
        }
        tab.KeepAwake = !tab.KeepAwake;
        SyncKeepAwakeHostState(tab);
        if (tab.KeepAwake)
        {
            ResumeTab(tab);
            if (tab.IsDiscarded)
            {
                RunUiTask(
                    () => RestoreDiscardedTabAsync(tab, focusPage: false),
                    "Could not reload the protected tab");
            }
        }
        else if (tab != activeTab || isWindowMinimized)
        {
            QueueBackgroundTabReduction(tab);
        }
        ApplyLiveTabMemoryTarget(tab, tab == activeTab && !isWindowMinimized);
        RefreshMemorySweepTimer();
        UpdateTabHeader(tab);
        ShowTransientStatus(tab.KeepAwake ? "Tab kept active" : "Tab can sleep again");
    }

    private void ToggleSiteDarkModeExclusion(string host)
    {
        var normalized = BrowserPolicy.NormalizeExactHost(host);
        if (normalized is null) return;

        var removed = state.DarkModeExcludedHosts.RemoveAll(item =>
            item.Equals(normalized, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
        {
            if (state.DarkModeExcludedHosts.Count >= BrowserStateStore.MaximumDarkModeExcludedHosts)
            {
                ShowTransientStatus("You can exclude up to 200 sites from dark mode");
                return;
            }
            state.DarkModeExcludedHosts.Insert(0, normalized);
        }

        foreach (var matchingTab in tabs.Where(item =>
                     !item.IsClosed
                     && !item.IsSuspended
                     && !item.IsStartPage
                     && item.Core is not null
                     && BrowserPolicy.IsExactHost(HostFromUrl(item.Core.Source ?? item.Url), normalized)).ToArray())
        {
            RunUiTask(
                () => ApplyWebsiteThemeAsync(matchingTab),
                "Could not update the website color preference");
        }

        if (!isPrivateMode) ScheduleStateSave();
        ShowTransientStatus(removed
            ? $"Dark mode enabled for {normalized}"
            : $"Excluded {normalized} from dark mode");
    }

    private void ResetSitePreferences(Uri uri)
    {
        var host = SitePreferencePolicy.GetHost(uri.Host);
        if (host is null) return;
        state.SiteZoom.RemoveAll(item => BrowserPolicy.IsExactHost(item.Host, host));
        state.MutedHosts.RemoveAll(item => BrowserPolicy.IsExactHost(item, host));
        state.DarkModeExcludedHosts.RemoveAll(item => BrowserPolicy.IsExactHost(item, host));
        foreach (var tab in tabs.Where(item =>
                     !item.IsClosed
                     && SitePreferencePolicy.GetHost(item.Url)?.Equals(host, StringComparison.OrdinalIgnoreCase) == true))
        {
            tab.ZoomFactor = 1.0;
            tab.TabMuteOverride = null;
            tab.IsMuted = false;
            if (tab.View is { CoreWebView2: not null } view)
            {
                try
                {
                    applyingZoomPreference = true;
                    view.ZoomFactor = 1.0;
                    view.CoreWebView2.IsMuted = false;
                }
                catch (Exception error)
                {
                    stateStore.Log("Could not reset site preferences", error);
                }
                finally
                {
                    applyingZoomPreference = false;
                }
            }
            if (darkModeEnabled && !tab.IsSuspended && tab.Core is not null)
            {
                RunUiTask(() => ApplyWebsiteThemeAsync(tab), "Could not update the website color preference");
            }
            UpdateTabHeader(tab);
        }
        if (!isPrivateMode) ScheduleStateSave();
        ShowTransientStatus($"Site preferences reset for {host}");
    }

    private void RefreshAdBlockPagePolicies()
    {
        if (!adBlockEnabled) return;
        foreach (var tab in tabs.Where(item => item.Core is not null && !item.IsClosed))
        {
            RunUiTask(
                () => InstallAdBlockPageScriptsAsync(tab),
                "Could not update the site shield policy");
        }
    }

    private static string GetDisableAdBlockPageScript(BrowserTab tab)
    {
        var dispatch = tab.AdBlockControlChannel is null
            ? string.Empty
            : "window.dispatchEvent(new Event("
                + JsonSerializer.Serialize(tab.AdBlockControlChannel, BrowserStateJsonContext.Default.String)
                + "));";
        return dispatch
            + "document.getElementById('__misha_adblock_style')?.remove();"
            + "document.getElementById('__misha_adblock_remote_style')?.remove();";
    }

    private async Task DisableAdBlockForCurrentDocumentAsync(BrowserTab tab)
    {
        var script = GetDisableAdBlockPageScript(tab);
        if (tab.Core is not null)
        {
            try { await tab.Core.ExecuteScriptAsync(script); }
            catch (Exception error) { stateStore.Log("Could not disable the top-level page shield", error); }
        }
        foreach (var frame in tab.AdBlockFrames.ToArray())
        {
            try { await frame.ExecuteScriptAsync(script); }
            catch (Exception error) { stateStore.Log("Could not disable a frame page shield", error); }
        }
    }

}
