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
    /// <summary>
    /// Environment variable that switches the GPU rasterization flags off for a
    /// single run. WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS can only append, so
    /// without this the memory acceptance probe cannot A/B the flags that
    /// dominate GPU-process commit.
    /// </summary>
    internal const string DisableGpuRasterizationVariable = "MISHAWEB_DISABLE_GPU_RASTERIZATION";

    private static readonly string? InitialExternalBrowserArguments =
        Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");

    private static CoreWebView2EnvironmentOptions CreateEnvironmentOptions()
    {
        var mergedArgs = BuildAdditionalBrowserArguments(
            InitialExternalBrowserArguments,
            IsGpuRasterizationDisabled(),
            SystemResourceInfo.CaptureMemory());

        return new()
        {
            EnableTrackingPrevention = true,
            AreBrowserExtensionsEnabled = true,
            ExclusiveUserDataFolderAccess = true,
            AdditionalBrowserArguments = mergedArgs
        };
    }

    internal static string BuildAdditionalBrowserArguments(
        string? extraArgs,
        bool disableGpuRasterization,
        SystemMemorySnapshot memory)
    {
        var externalTokens = string.IsNullOrWhiteSpace(extraArgs)
            ? Array.Empty<string>()
            : extraArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var hasExternalAutoplayPolicy = false;
        var hasExternalHardwareOverlays = false;
        var hasDiskCacheSize = false;
        var hasMediaCacheSize = false;
        var hasRendererLimit = false;

        var enableFeatures = new List<string>();
        var enableFeatureSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!disableGpuRasterization)
        {
            foreach (var feature in new[] { "CanvasOopRasterization", "ParallelDownloading" })
            {
                if (enableFeatureSet.Add(feature)) enableFeatures.Add(feature);
            }
        }

        var disableFeatures = new List<string>();
        var disableFeatureSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in new[]
        {
            "BackForwardCache",
            "SpareRendererForSitePerProcess",
            "PeriodicBackgroundSync"
        })
        {
            if (disableFeatureSet.Add(feature)) disableFeatures.Add(feature);
        }

        var passthroughExternal = new List<string>(externalTokens.Length);
        foreach (var token in externalTokens)
        {
            if (token.StartsWith("--enable-features=", StringComparison.OrdinalIgnoreCase))
            {
                AppendFeatureTokens(token.AsSpan("--enable-features=".Length), enableFeatures, enableFeatureSet);
                continue;
            }
            if (token.StartsWith("--disable-features=", StringComparison.OrdinalIgnoreCase))
            {
                AppendFeatureTokens(token.AsSpan("--disable-features=".Length), disableFeatures, disableFeatureSet);
                continue;
            }
            if (token.StartsWith("--autoplay-policy=", StringComparison.OrdinalIgnoreCase)
                || token.Equals("--autoplay-policy", StringComparison.OrdinalIgnoreCase))
            {
                hasExternalAutoplayPolicy = true;
            }
            else if (token.StartsWith("--hardware-overlays", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("--enable-hardware-overlays=", StringComparison.OrdinalIgnoreCase))
            {
                hasExternalHardwareOverlays = true;
            }
            else if (token.StartsWith("--disk-cache-size=", StringComparison.OrdinalIgnoreCase))
            {
                hasDiskCacheSize = true;
            }
            else if (token.StartsWith("--media-cache-size=", StringComparison.OrdinalIgnoreCase))
            {
                hasMediaCacheSize = true;
            }
            else if (token.StartsWith("--renderer-process-limit=", StringComparison.OrdinalIgnoreCase))
            {
                hasRendererLimit = true;
            }
            else if (token.Equals("--enable-quic", StringComparison.OrdinalIgnoreCase)
                || (!disableGpuRasterization
                    && (token.Equals("--enable-gpu-rasterization", StringComparison.OrdinalIgnoreCase)
                        || token.Equals("--enable-zero-copy", StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            passthroughExternal.Add(token);
        }

        var args = new List<string>(14 + passthroughExternal.Count);
        if (!hasExternalAutoplayPolicy)
        {
            args.Add("--autoplay-policy=no-user-gesture-required");
        }
        if (!disableGpuRasterization)
        {
            args.Add("--enable-gpu-rasterization");
            args.Add("--enable-zero-copy");
        }
        if (enableFeatures.Count > 0)
        {
            args.Add("--enable-features=" + string.Join(',', enableFeatures));
        }
        args.Add("--disable-features=" + string.Join(',', disableFeatures));
        args.Add("--enable-quic");
        if (!hasExternalHardwareOverlays)
        {
            args.Add("--enable-hardware-overlays=single-fullscreen,single-on-top");
        }

        if (memory.IsValid && memory.TotalPhysicalBytes > 0)
        {
            const ulong eightGibibytes = 8UL * 1024 * 1024 * 1024;
            if (memory.IsTinyMachine)
            {
                if (!hasDiskCacheSize) args.Add("--disk-cache-size=52428800");
                if (!hasMediaCacheSize) args.Add("--media-cache-size=26214400");
                if (!hasRendererLimit) args.Add("--renderer-process-limit=4");
            }
            else if (memory.TotalPhysicalBytes <= eightGibibytes)
            {
                if (!hasDiskCacheSize) args.Add("--disk-cache-size=104857600");
                if (!hasMediaCacheSize) args.Add("--media-cache-size=52428800");
                if (!hasRendererLimit) args.Add("--renderer-process-limit=8");
            }
            else
            {
                if (!hasDiskCacheSize) args.Add("--disk-cache-size=209715200");
                if (!hasMediaCacheSize) args.Add("--media-cache-size=104857600");
            }
        }

        args.AddRange(passthroughExternal);
        return string.Join(' ', args);
    }

    private static void AppendFeatureTokens(
        ReadOnlySpan<char> value,
        List<string> destination,
        HashSet<string> seen)
    {
        while (!value.IsEmpty)
        {
            var commaIndex = value.IndexOf(',');
            var rawToken = commaIndex < 0 ? value : value[..commaIndex];
            var token = rawToken.Trim();
            if (!token.IsEmpty)
            {
                var feature = token.ToString();
                if (seen.Add(feature)) destination.Add(feature);
            }
            if (commaIndex < 0) break;
            value = value[(commaIndex + 1)..];
        }
    }

    private static bool IsGpuRasterizationDisabled()
    {
        var value = Environment.GetEnvironmentVariable(DisableGpuRasterizationVariable);
        return value is not null
            && (value.Length == 0
                || value.Equals("1", StringComparison.Ordinal)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(string? userDataFolderOverride = null)
    {
        WebView2LoaderBootstrap.EnsureLoaded();
        var userDataFolder = string.IsNullOrWhiteSpace(userDataFolderOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MishaWeb",
                "WebView2")
            : userDataFolderOverride;
        Directory.CreateDirectory(userDataFolder);
        var options = CreateEnvironmentOptions();
        return await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
    }

    private Task<CoreWebView2Environment> GetOrCreateEnvironmentAsync()
    {
        if (environment is not null) return Task.FromResult(environment);
        if (environmentTask is null)
        {
            var generation = ++environmentGeneration;
            environmentTask = CreateEnvironmentAndRememberAsync(generation);
        }
        return environmentTask;
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAndRememberAsync(long generation)
    {
        try
        {
            var created = await CreateEnvironmentWithRetryAsync(generation);
            if (!isClosing && generation == environmentGeneration)
            {
                environment = created;
                AttachBrowserVersionAvailableHandler(created, generation);
                if (!IsDisposed && !Disposing && IsHandleCreated)
                {
                    if (InvokeRequired) PostUi(RefreshMemorySweepTimer);
                    else RefreshMemorySweepTimer();
                }
            }
            return created;
        }
        catch
        {
            if (generation == environmentGeneration)
            {
                environment = null;
                environmentTask = null;
            }
            throw;
        }
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentWithRetryAsync(long generation)
    {
        int[] retryDelaysMs = [150, 350, 700];
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await CreateEnvironmentAsync(webViewUserDataFolderOverride);
            }
            catch (Exception error) when (
                attempt < retryDelaysMs.Length
                && !isClosing
                && generation == environmentGeneration
                && error is COMException or IOException or UnauthorizedAccessException)
            {
                stateStore.Log("Retrying WebView2 environment creation after transient lock", error);
                await Task.Delay(retryDelaysMs[attempt]);
            }
        }
    }

    private void InvalidateBrowserEnvironment()
    {
        DetachBrowserVersionAvailableHandler();
        var manager = extensionsManager;
        extensionsManager = null;
        try { manager?.Dispose(); }
        catch { }
        DisposeExtensionProfileController();
        environmentGeneration++;
        environment = null;
        environmentTask = null;
        profileMediaPermissionsSeeded = false;
        hasGrantedMediaPermissionOrigins = false;
        profileGrantedMediaPermissionOrigins.Clear();
        memoryTimer.Stop();
    }

    private void AttachBrowserVersionAvailableHandler(
        CoreWebView2Environment created,
        long generation)
    {
        DetachBrowserVersionAvailableHandler();
        EventHandler<object> handler = (_, _) =>
            QueueBrowserRuntimeUpdateAvailable(created, generation);
        created.NewBrowserVersionAvailable += handler;
        browserVersionEventEnvironment = created;
        browserVersionAvailableHandler = handler;
    }

    private void DetachBrowserVersionAvailableHandler()
    {
        var subscribedEnvironment = browserVersionEventEnvironment;
        var handler = browserVersionAvailableHandler;
        browserVersionEventEnvironment = null;
        browserVersionAvailableHandler = null;
        if (subscribedEnvironment is null || handler is null) return;
        try { subscribedEnvironment.NewBrowserVersionAvailable -= handler; }
        catch { }
    }

    private void QueueBrowserRuntimeUpdateAvailable(
        CoreWebView2Environment sourceEnvironment,
        long generation)
    {
        void Publish()
        {
            if (isClosing
                || IsDisposed
                || generation != environmentGeneration
                || !ReferenceEquals(environment, sourceEnvironment))
            {
                return;
            }

            browserRuntimeUpdateAvailable = true;
            restartBrowserForUpdateMenuItem.Visible = true;
            ShowTransientStatus(IsBrowserEngineRestartUnsafe()
                ? "Browser engine update ready — finish calls and downloads, then restart"
                : "Browser engine update ready — restart from the menu");
        }

        if (isClosing || IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired) PostUi(Publish);
        else Publish();
    }

    private bool IsBrowserEngineRestartUnsafe() =>
        downloads.Any(item => !item.IsTerminal)
        || tabs.Any(tab => !tab.IsClosed && tab.HasMediaCapturePermission);

    private void OnContainsFullScreenElementChanged(BrowserTab tab, CoreWebView2 core)
    {
        if (isClosing || IsDisposed || Disposing || !IsHandleCreated) return;
        if (!IsTabVisibleInAnyPane(tab)) return;

        void Apply()
        {
            if (tab.IsClosed || !IsTabVisibleInAnyPane(tab) || !ReferenceEquals(tab.Core, core)) return;
            try
            {
                var entering = core.ContainsFullScreenElement;
                if (entering && activeTab != tab && multiViewState.IsMultiViewActive)
                {
                    var paneIndex = multiViewState.IndexOfTab(tab.Id);
                    if (paneIndex >= 0) FocusMultiViewPane(paneIndex, focusContent: false);
                }
                if (activeTab == tab) SetDomFullScreen(entering);
            }
            catch (Exception error) when (error is InvalidOperationException or COMException or ObjectDisposedException)
            {
                stateStore.Log("Could not inspect fullscreen state", error);
            }
        }

        if (InvokeRequired) PostUi(Apply);
        else Apply();
    }

    private void SyncDomFullScreenFromActiveTab()
    {
        if (isClosing || IsDisposed || activeTab?.Core is null)
        {
            if (isDomFullScreen)
            {
                isDomFullScreen = false;
                UpdateChromeRowsForFullscreen();
            }

            return;
        }

        try
        {
            SetDomFullScreen(activeTab.Core.ContainsFullScreenElement);
        }
        catch (Exception error) when (
            error is InvalidOperationException
                or COMException
                or ObjectDisposedException)
        {
            if (isDomFullScreen) SetDomFullScreen(false);
            stateStore.Log("Could not inspect fullscreen state", error);
        }
    }

    private void SetDomFullScreen(bool value)
    {
        if (isDomFullScreen == value) return;
        if (value)
        {
            findBarWasVisibleBeforeDomFullScreen = findBar.Visible;
            if (!isFullScreen)
            {
                normalWindowStateBeforeDomFullScreen = WindowState;
                normalBoundsBeforeDomFullScreen = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                hasDomFullScreenWindowState = true;
                WindowState = FormWindowState.Normal;
                Padding = Padding.Empty;
                Bounds = Screen.FromControl(this).Bounds;
            }
        }
        else if (hasDomFullScreenWindowState)
        {
            if (!isFullScreen)
            {
                Bounds = normalBoundsBeforeDomFullScreen;
                WindowState = normalWindowStateBeforeDomFullScreen;
            }
            else
            {
                normalBounds = normalBoundsBeforeDomFullScreen;
                normalWindowState = normalWindowStateBeforeDomFullScreen;
            }
            hasDomFullScreenWindowState = false;
        }

        isDomFullScreen = value;
        if (!value) findBar.Visible = findBarWasVisibleBeforeDomFullScreen;
        UpdateChromeRowsForFullscreen();
        if (multiViewState.IsMultiViewActive)
        {
            ApplyMultiViewLayout();
            RefreshLiveTabMemoryTargets();
            RefreshMemorySweepTimer();
        }
        UpdateWindowControls();
    }

    private void ExitDomFullScreen()
    {
        var tab = activeTab;
        var core = tab?.Core;
        if (tab is null || core is null)
        {
            SetDomFullScreen(false);
            return;
        }
        RunUiTask(
            async () =>
            {
                try
                {
                    await core.ExecuteScriptAsync(
                        "if(document.fullscreenElement&&document.exitFullscreen)document.exitFullscreen();");
                }
                finally
                {
                    if (!tab.IsClosed && ReferenceEquals(tab.Core, core)) SetDomFullScreen(false);
                }
            },
            "Could not leave page fullscreen");
    }

    private void UpdateChromeRowsForFullscreen()
    {
        var isChromeHidden = isFullScreen || isDomFullScreen;
        titleBar.Visible = !isChromeHidden;
        toolbar.Visible = !isChromeHidden;
        rootLayout.RowStyles[0].Height = isChromeHidden ? 0 : ScaleChromeLogical(33);
        rootLayout.RowStyles[1].Height = isChromeHidden ? 0 : ScaleChromeLogical(46);
        if (isChromeHidden) HideAddressSuggestions();
        rootLayout.PerformLayout();

        if (isChromeHidden)
        {
            findBar.Visible = false;
            rootLayout.RowStyles[2].Height = 0;
            UpdateWindowControls();
            return;
        }

        var shouldShowFindBar = findBar.Visible;
        findBar.Visible = shouldShowFindBar;
        rootLayout.RowStyles[2].Height = shouldShowFindBar ? ScaleChromeLogical(40) : 0;
        UpdateFindBarLayout();
        UpdateWindowControls();
    }

    private async Task OnNavigationCompletedAsync(
        BrowserTab tab,
        CoreWebView2 core,
        bool isSuccess,
        CoreWebView2WebErrorStatus webErrorStatus)
    {
        if (isClosing || tab.IsClosed || !ReferenceEquals(tab.Core, core)) return;
        var navigationGeneration = tab.DocumentNavigationGeneration;
        tab.IsLoading = false;
        if (tab.IgnoreNextExternalFailure
            && webErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted)
        {
            tab.IgnoreNextExternalFailure = false;
            tab.StatusText = tab.ExternalNavigationStatus;
            tab.ExternalNavigationStatus = string.Empty;
        }
        else if (isSuccess)
        {
            tab.StatusText = "Ready";
            if (!tab.IsStartPage)
            {
                string source;
                try { source = core.Source; }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                UpdateTabUrlFromSource(tab, source);
                ApplySitePreferences(tab, persist: false);
                AddHistory(tab);
                if (darkModeEnabled && state.DarkModeExcludedHosts.Count > 0)
                {
                    await ApplyWebsiteThemeIfChangedAsync(tab, source);
                    if (isClosing
                        || tab.IsClosed
                        || !ReferenceEquals(tab.Core, core)
                        || tab.DocumentNavigationGeneration != navigationGeneration)
                    {
                        return;
                    }
                }
                if (tab.PendingRestoreScrollY > 0)
                {
                    var restoreY = tab.PendingRestoreScrollY;
                    tab.PendingRestoreScrollY = 0;
                    tab.SavedScrollY = restoreY;
                    try
                    {
                        await core.ExecuteScriptAsync($"(() => {{ try {{ window.scrollTo(0, {restoreY}); }} catch (_) {{}} }})();");
                    }
                    catch { }
                    if (isClosing
                        || tab.IsClosed
                        || !ReferenceEquals(tab.Core, core)
                        || tab.DocumentNavigationGeneration != navigationGeneration)
                    {
                        return;
                    }
                }
                try
                {
                    if (!string.IsNullOrWhiteSpace(core.FaviconUri) && !TryApplyCachedFavicon(tab))
                    {
                        RunUiTask(() => RefreshTabFaviconAsync(tab, core, navigationGeneration), "Could not load page icon");
                    }
                }
                catch { }
                await RefreshTabMediaPermissionStateAsync(tab, navigationGeneration);
                if (isClosing
                    || tab.IsClosed
                    || !ReferenceEquals(tab.Core, core)
                    || tab.DocumentNavigationGeneration != navigationGeneration)
                {
                    return;
                }
            }
            tab.ConnectionState = tab.IsStartPage
                ? ConnectionState.Local
                : BrowserPolicy.IsLocalFileUrl(tab.Url)
                    ? ConnectionState.LocalFile
                    : IsTrustedExtensionPage(tab.Url, tab.ExtensionOriginId)
                        ? ConnectionState.Local
                        : Uri.TryCreate(tab.Url, UriKind.Absolute, out var successfulUri)
                            && successfulUri.Scheme == Uri.UriSchemeHttps
                                ? ConnectionState.Secure
                                : ConnectionState.Insecure;
        }
        else
        {
            tab.StatusText = FriendlyNavigationError(webErrorStatus);
            tab.ConnectionState = IsCertificateError(webErrorStatus)
                ? ConnectionState.CertificateError
                : ConnectionState.Failed;
        }

        if (activeTab == tab)
        {
            SyncAddressBar();
            UpdateNavigationChrome();
            UpdateStatus();
        }
        UpdateTabHeader(tab);
        if ((activeTab != tab || isWindowMinimized) && !IsBackgroundProtected(tab))
        {
            QueueBackgroundTabReduction(tab);
        }
    }

    private void OnSourceChanged(BrowserTab tab, CoreWebView2SourceChangedEventArgs e)
    {
        if (tab.IsStartPage || tab.Core is not { } core) return;
        string source;
        try
        {
            source = core.Source;
        }
        catch (Exception error) when (error is InvalidOperationException or COMException or ObjectDisposedException)
        {
            stateStore.Log("Could not read tab URL on source change", error);
            return;
        }
        var keepAwakeChanged = UpdateTabUrlFromSource(tab, source);
        ApplySitePreferences(tab, persist: false);
        if (darkModeEnabled && state.DarkModeExcludedHosts.Count > 0)
        {
            RunUiTask(() => ApplyWebsiteThemeIfChangedAsync(tab, source), "Could not update the website color preference");
        }
        if (!e.IsNewDocument) AddHistory(tab);
        if (keepAwakeChanged) UpdateTabHeader(tab);
        if (activeTab == tab && !addressBarEditing)
        {
            SyncAddressBar();
            UpdateSiteInfo();
        }
    }

    private void OnDocumentTitleChanged(BrowserTab tab)
    {
        string? title;
        if (tab.IsStartPage)
        {
            title = isPrivateMode ? "Incognito" : "New tab";
        }
        else if (tab.Core is { } core)
        {
            try
            {
                title = core.DocumentTitle;
            }
            catch (Exception error) when (error is InvalidOperationException or COMException or ObjectDisposedException)
            {
                stateStore.Log("Could not read tab title", error);
                return;
            }
        }
        else
        {
            return;
        }
        var sanitizedTitle = BrowserStateStore.SanitizeTitle(title);
        tab.Title = sanitizedTitle.Length == 0 ? HostFromUrl(tab.Url) : sanitizedTitle;
        UpdateTabHeader(tab);
        UpdateHistoryTitle(tab);
        if (activeTab == tab) UpdateWindowTitle();
    }

    private void OnNewWindowRequested(
        BrowserTab sourceTab,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        CoreWebView2Deferral deferral;
        try { deferral = e.GetDeferral(); }
        catch (Exception error)
        {
            try { e.Handled = true; }
            catch { }
            stateStore.Log("Could not defer popup creation", error);
            if (!isClosing) ShowTransientStatus("Blocked a popup that could not be opened safely");
            return;
        }
        RunUiTask(
            () => OnNewWindowRequestedAsync(sourceTab, e, deferral),
            "Could not open the popup");
    }

    private async Task OnNewWindowRequestedAsync(
        BrowserTab sourceTab,
        CoreWebView2NewWindowRequestedEventArgs e,
        CoreWebView2Deferral deferral)
    {
        BrowserTab? popup = null;
        var popupAttached = false;
        var sourceCore = sourceTab.Core;
        var sourceGeneration = sourceTab.InitializationGeneration;
        string? sourceDocument = null;
        try
        {
            sourceDocument = sourceCore?.Source;
            var popupSource = e.OriginalSourceFrameInfo.Source;
            var trustedCallPopup = CommunicationCompatibilityPolicy.IsTrustedPopup(
                popupSource,
                e.Uri);
            var trustedExtensionId = IsTrustedExtensionPage(
                    popupSource,
                    sourceTab.ExtensionOriginId)
                && IsTrustedExtensionPage(e.Uri, sourceTab.ExtensionOriginId)
                ? sourceTab.ExtensionOriginId
                : null;
            if (isClosing
                || (!BrowserPolicy.IsSafeTopLevelUrl(e.Uri)
                    && !BrowserPolicy.IsPopupBootstrapUrl(e.Uri)
                    && trustedExtensionId is null))
            {
                e.Handled = true;
                if (!e.IsUserInitiated) ShowTransientStatus("Blocked a scripted popup");
                return;
            }

            if (adBlockEnabled
                && !trustedCallPopup
                && trustedExtensionId is null
                && !IsExceptionHost(HostFromUrl(sourceTab.Url))
                && !IsExceptionHost(HostFromUrl(e.Uri))
                && adBlocker.ShouldBlockPopup(
                    e.Uri,
                    sourceTab.Url,
                    cacheEvaluation: !isPrivateMode))
            {
                e.Handled = true;
                sourceTab.RecordBlockedHost(HostFromUrl(e.Uri), "Ad");
                if (sourceTab.BlockedRequestCount == 1)
                {
                    ShowTransientStatus("Shield blocked a cross-site popup");
                }
                if (activeTab == sourceTab) UpdateStatus();
                return;
            }

            if (!e.IsUserInitiated && !trustedCallPopup && trustedExtensionId is null)
            {
                e.Handled = true;
                ShowTransientStatus("Blocked a scripted popup");
                return;
            }

            popup = await OpenNewTabAsync(
                e.Uri,
                activate: true,
                waitForPopupNavigation: true,
                canScriptClose: true,
                trustedExtensionId: trustedExtensionId);
            if (popup?.Core is null)
            {
                e.Handled = true;
                if (popup is not null && !popup.IsClosed)
                {
                    CloseTab(popup, remember: false, ensureReplacement: false);
                }
                popup = null;
                return;
            }

            if (isClosing
                || sourceTab.IsClosed
                || sourceTab.Core != sourceCore
                || sourceTab.InitializationGeneration != sourceGeneration
                || !string.Equals(sourceCore?.Source, sourceDocument, StringComparison.Ordinal))
            {
                e.Handled = true;
                CloseTab(popup, remember: false, ensureReplacement: false);
                popup = null;
                return;
            }

            e.NewWindow = popup.Core;
            e.Handled = true;
            popupAttached = true;
            popup.DeferResourceFilteringUntilPopupAttached = false;
            if (adBlockEnabled)
            {
                // WebView2 requires resource filters for popup contents to be
                // added only after the CoreWebView2 has been assigned here.
                await InstallAdBlockFilteringAsync(popup);
                if (isClosing || IsDisposed || popup.IsClosed || popup.Core is null)
                {
                    e.NewWindow = null;
                    popupAttached = false;
                    if (!popup.IsClosed)
                    {
                        CloseTab(popup, remember: false, ensureReplacement: false);
                    }
                    popup = null;
                    return;
                }
            }
            popup.Url = e.Uri;
            popup.IsStartPage = false;
            popup.StatusText = "Loading\u2026";
            UpdateTabHeader(popup);
        }
        catch (Exception error)
        {
            try { e.Handled = true; }
            catch { }
            if (popupAttached)
            {
                // Do not complete the WebView2 deferral with a partially
                // configured popup. Once NewWindow has been assigned, a later
                // filter/setup failure must still fail closed.
                try { e.NewWindow = null; }
                catch { }
                popupAttached = false;
            }
            if (popup is not null && !popup.IsClosed)
            {
                CloseTab(popup, remember: false, ensureReplacement: false);
            }
            stateStore.Log("Popup creation failed", error);
            ShowTransientStatus("Could not open the popup");
        }
        finally
        {
            try { deferral.Complete(); }
            catch (Exception error) { stateStore.Log("Could not complete popup creation", error); }
        }
    }

    private void OnProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedKind failedKind)
    {
        if (isClosing || tab.IsClosed) return;
        if (isDomFullScreen
            && (activeTab == tab
                || failedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited))
        {
            SetDomFullScreen(false);
        }
        StopFindSession(tab);
        tab.ReaderModeActive = false;

        switch (failedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                FailActiveDownloadsAfterBrowserProcessExit();
                InvalidateBrowserEnvironment();
                foreach (var item in tabs.ToArray())
                {
                    if (item.IsStartPage && item.View is null)
                    {
                        continue;
                    }
                    ReplaceTabView(item);
                    item.StatusText = "Browser process stopped";
                    item.ConnectionState = ConnectionState.Failed;
                    ShowTabOverlay(
                        item,
                        "The browser process stopped",
                        "Your tabs are preserved and can be restored.",
                        "Restart browser",
                        RestartBrowserAsync);
                }
                UpdateNavigationChrome();
                UpdateStatus();
                break;

            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                ReplaceTabView(tab);
                tab.StatusText = "Page crashed";
                tab.ConnectionState = ConnectionState.Failed;
                ShowTabOverlay(
                    tab,
                    "This page stopped working",
                    "Reload the tab to create a fresh page process.",
                    "Reload tab",
                    () => RecreateTabAsync(tab));
                UpdateTabHeader(tab);
                UpdateStatus();
                break;

            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                tab.StatusText = "Page not responding";
                if (activeTab == tab) ShowTransientStatus("This page is not responding");
                break;

            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                tab.StatusText = "A page frame stopped";
                if (activeTab == tab) ShowTransientStatus("An embedded frame on this page stopped");
                UpdateStatus();
                break;

            case CoreWebView2ProcessFailedKind.GpuProcessExited:
                stateStore.Log(
                    "WebView2 GPU process exited",
                    new InvalidOperationException($"WebView2 process failed ({failedKind})"));
                if (activeTab == tab) ShowTransientStatus("Graphics process restarted");
                break;

            case CoreWebView2ProcessFailedKind.UtilityProcessExited:
            case CoreWebView2ProcessFailedKind.SandboxHelperProcessExited:
            case CoreWebView2ProcessFailedKind.PpapiPluginProcessExited:
            case CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited:
                stateStore.Log(
                    $"WebView2 helper process exited ({failedKind})",
                    new InvalidOperationException($"WebView2 process failed ({failedKind})"));
                if (activeTab == tab) ShowTransientStatus("A browser helper process stopped");
                break;

            case CoreWebView2ProcessFailedKind.UnknownProcessExited:
            default:
                stateStore.Log(
                    $"WebView2 process failed ({failedKind})",
                    new InvalidOperationException($"WebView2 process failed ({failedKind})"));
                if (tab.Core is null)
                {
                    ReplaceTabView(tab);
                    tab.StatusText = "Page crashed";
                    tab.ConnectionState = ConnectionState.Failed;
                    ShowTabOverlay(
                        tab,
                        "This page stopped working",
                        "Reload the tab to create a fresh page process.",
                        "Reload tab",
                        () => RecreateTabAsync(tab));
                    UpdateTabHeader(tab);
                }
                else
                {
                    tab.StatusText = "Page process interrupted";
                    if (activeTab == tab) ShowTransientStatus("A browser process stopped unexpectedly");
                }
                UpdateStatus();
                break;
        }
    }

    private int FailActiveDownloadsAfterBrowserProcessExit()
    {
        var interruptedCount = 0;
        foreach (var download in downloads.Where(item => !item.IsTerminal).ToArray())
        {
            // Once the browser process is gone these COM operations cannot
            // make further progress or reliably raise StateChanged. Detach
            // first, then leave an honest terminal record for the downloads UI.
            DetachDownloadHandlers(download);
            download.IsTerminal = true;
            download.State = "Interrupted";
            download.CanResume = false;
            download.EstimatedEndTimeText = string.Empty;
            download.InterruptReason = "Browser process stopped";
            interruptedCount++;
        }

        foreach (var item in tabs)
        {
            item.ActiveDownloads = 0;
        }

        if (interruptedCount == 0) return 0;

        TrimDownloadRecords();
        SyncRecentDownloadsToState();
        BeginDownloadGracePeriod();
        RefreshDownloadsUiNow();
        return interruptedCount;
    }

    private static long SafeDownloadBytes(ulong? value)
    {
        var bytes = value.GetValueOrDefault();
        return bytes > long.MaxValue ? long.MaxValue : (long)bytes;
    }

    private static long SafeDownloadBytes(long? value)
    {
        return Math.Max(0, value.GetValueOrDefault());
    }

    private void OnDownloadStarting(BrowserTab tab, CoreWebView2DownloadStartingEventArgs e)
    {
        if (TryHandleChromeStoreExtensionDownload(tab, e)) return;
        if (isClosing || IsDisposed || tab.IsClosed)
        {
            try
            {
                e.Cancel = true;
                e.Handled = true;
            }
            catch (Exception error) when (
                error is InvalidOperationException
                    or COMException
                    or ObjectDisposedException)
            {
            }
            return;
        }
        if (downloads.Count(item => !item.IsTerminal) >= MaximumActiveDownloads)
        {
            e.Cancel = true;
            e.Handled = true;
            ShowTransientStatus("Too many active downloads; finish or cancel one first");
            return;
        }

        string resultFilePath;
        CoreWebView2DownloadOperation candidateOperation;
        string? downloadUri;
        try
        {
            resultFilePath = e.ResultFilePath;
            candidateOperation = e.DownloadOperation;
            downloadUri = candidateOperation.Uri;
        }
        catch (Exception error) when (
            error is InvalidOperationException
                or COMException
                or ObjectDisposedException)
        {
            stateStore.Log("Could not inspect a download request", error);
            return;
        }

        if (!BrowserPolicy.IsSafeDownloadUri(downloadUri))
        {
            try
            {
                e.Cancel = true;
                e.Handled = true;
            }
            catch (Exception error) when (
                error is InvalidOperationException
                    or COMException
                    or ObjectDisposedException)
            {
            }
            ShowTransientStatus("Blocked an unsafe download address");
            return;
        }

        if (BrowserPolicy.IsHighRiskDownloadExtension(resultFilePath))
        {
            CoreWebView2Deferral? deferral;
            try
            {
                e.Cancel = true;
                e.Handled = true;
                deferral = e.GetDeferral();
            }
            catch (Exception error) when (
                error is InvalidOperationException
                    or COMException
                    or ObjectDisposedException)
            {
                stateStore.Log("Could not defer a high-risk download prompt", error);
                return;
            }

            var displayName = SanitizeDownloadDisplayName(Path.GetFileName(resultFilePath));
            var sourceLabel = SanitizeExtensionPromptText(
                TextSafety.FormatUrlForDisplay(downloadUri, 180),
                180);
            if (!PostUi(() =>
            {
                try
                {
                    if (isClosing || IsDisposed || tab.IsClosed) return;

                    var promptText = string.IsNullOrWhiteSpace(sourceLabel)
                        ? $"\"{displayName}\" is an executable or script file that could harm your computer. Download it anyway?"
                        : $"\"{displayName}\" is an executable or script file that could harm your computer.{Environment.NewLine}{Environment.NewLine}Source: {sourceLabel}{Environment.NewLine}{Environment.NewLine}Download it anyway?";
                    var allowed = NativeAlertDialog.Confirm(
                        this,
                        "Keep potentially harmful file?",
                        promptText,
                        confirmText: "Keep file",
                        cancelText: "Discard");
                    if (isClosing || IsDisposed || tab.IsClosed) return;
                    if (!allowed)
                    {
                        ShowTransientStatus($"Discarded {displayName}");
                        return;
                    }

                    e.Cancel = false;
                    BeginTrackedDownload(tab, candidateOperation, resultFilePath);
                }
                catch (Exception error) when (
                    error is InvalidOperationException
                        or COMException
                        or ObjectDisposedException)
                {
                    stateStore.Log("Could not complete a high-risk download prompt", error);
                }
                finally
                {
                    try { deferral.Complete(); }
                    catch { }
                }
            }))
            {
                try { deferral.Complete(); }
                catch { }
            }
            return;
        }

        // MishaWeb owns the download surface and lifecycle from this point.
        // Suppress WebView2's separate default dialog so each download appears
        // exactly once and remains governed by the browser's close safeguards.
        e.Handled = true;
        var operation = e.DownloadOperation;
        BeginTrackedDownload(tab, operation, resultFilePath);
    }

    private void BeginTrackedDownload(
        BrowserTab tab,
        CoreWebView2DownloadOperation operation,
        string resultFilePath)
    {
        if (isClosing || IsDisposed || tab.IsClosed) return;

        DownloadEntry entry;
        try
        {
            entry = new DownloadEntry(
                SanitizeDownloadDisplayName(Path.GetFileName(resultFilePath)),
                resultFilePath,
                operation.Uri,
                DateTime.Now,
                "Downloading");
            entry.Operation = operation;
            entry.BytesReceived = SafeDownloadBytes(operation.BytesReceived);
            entry.TotalBytesToReceive = SafeDownloadBytes(operation.TotalBytesToReceive);
            entry.CancelAction = operation.Cancel;
            entry.PauseAction = operation.Pause;
            entry.ResumeAction = operation.Resume;
            entry.CanResume = operation.CanResume;
            entry.EstimatedEndTimeText = FormatEstimatedEndTime(operation.EstimatedEndTime);
            entry.InterruptReason = MapDownloadInterruptReason(operation.InterruptReason);
        }
        catch (Exception error) when (
            error is InvalidOperationException
                or COMException
                or ObjectDisposedException)
        {
            stateStore.Log("Could not start tracking a download", error);
            return;
        }

        downloads.Insert(0, entry);
        TrimDownloadRecords();
        downloadsButtonAutoVisible = true;
        downloadsButton.GlyphState = "active";
        downloadGraceTimer.Stop();
        UpdateResponsiveToolbar();
        tab.ActiveDownloads++;
        tab.ConsecutiveSuspendFailures = 0;
        ApplyLiveTabMemoryTarget(tab, IsTabForegroundForLifecycle(tab));
        UpdateStatus();
        ShowDownloadNotification(entry);

        var weakTab = new WeakReference<BrowserTab>(tab);
        EventHandler<object>? stateChanged = null;
        stateChanged = (_, _) =>
        {
            if (isClosing || IsDisposed) return;
            CoreWebView2DownloadState currentState;
            CoreWebView2DownloadInterruptReason currentInterruptReason;
            bool canResume;
            bool isUserPaused;
            try
            {
                entry.BytesReceived = SafeDownloadBytes(operation.BytesReceived);
                entry.TotalBytesToReceive = SafeDownloadBytes(operation.TotalBytesToReceive);
                entry.FilePath = operation.ResultFilePath;
                entry.FileName = SanitizeDownloadDisplayName(
                    Path.GetFileName(operation.ResultFilePath));
                currentState = operation.State;
                currentInterruptReason = operation.InterruptReason;
                canResume = operation.CanResume;
                isUserPaused = currentState == CoreWebView2DownloadState.Interrupted
                    && currentInterruptReason.ToString() is "UserCanceled" or "UserPaused"
                    && canResume;
                entry.State = currentState switch
                {
                    CoreWebView2DownloadState.Completed => "Complete",
                    CoreWebView2DownloadState.Interrupted when isUserPaused => "Paused",
                    CoreWebView2DownloadState.Interrupted => "Interrupted",
                    _ => "Downloading"
                };
                entry.CanResume = canResume;
                entry.EstimatedEndTimeText = FormatEstimatedEndTime(operation.EstimatedEndTime);
                entry.InterruptReason = isUserPaused ? "Paused by user" : MapDownloadInterruptReason(currentInterruptReason);
            }
            catch (Exception error) when (
                error is InvalidOperationException
                    or COMException
                    or ObjectDisposedException)
            {
                stateStore.Log("Could not read download state update", error);
                return;
            }

            var isTerminal = currentState == CoreWebView2DownloadState.Completed
                || (currentState == CoreWebView2DownloadState.Interrupted && !isUserPaused);
            if (!entry.IsTerminal && isTerminal)
            {
                entry.IsTerminal = true;
                entry.CancelAction = null;
                entry.PauseAction = null;
                entry.ResumeAction = null;
                DetachDownloadHandlers(entry);
                if (weakTab.TryGetTarget(out var downloadTab) && !downloadTab.IsClosed)
                {
                    downloadTab.ActiveDownloads = Math.Max(0, downloadTab.ActiveDownloads - 1);
                    ApplyLiveTabMemoryTarget(
                        downloadTab,
                        IsTabForegroundForLifecycle(downloadTab));
                    if (!IsTabForegroundForLifecycle(downloadTab))
                    {
                        QueueBackgroundTabReduction(downloadTab);
                    }
                }
                if (currentState == CoreWebView2DownloadState.Completed)
                {
                    ShowTransientStatus($"Downloaded {entry.FileName}");
                }
                else
                {
                    ShowTransientStatus($"Download interrupted: {entry.FileName}");
                }
                TrimDownloadRecords();
                SyncRecentDownloadsToState();
                if (tabs.All(item => item.ActiveDownloads == 0)) BeginDownloadGracePeriod();
                ReleaseBrowserEnvironmentIfIdle();
                if (ReferenceEquals(entry, notificationDownloadEntry)) UpdateDownloadNotificationIfVisible();
            }
            RefreshDownloadsUi();
        };
        entry.StateChangedHandler = stateChanged;
        operation.StateChanged += stateChanged;
        EventHandler<object> bytesChanged = (_, _) =>
        {
            if (isClosing || IsDisposed) return;
            try
            {
                entry.BytesReceived = SafeDownloadBytes(operation.BytesReceived);
                entry.TotalBytesToReceive = SafeDownloadBytes(operation.TotalBytesToReceive);
            }
            catch (Exception error) when (
                error is InvalidOperationException
                    or COMException
                    or ObjectDisposedException)
            {
                return;
            }
            RefreshDownloadsUi();
        };
        entry.BytesReceivedChangedHandler = bytesChanged;
        operation.BytesReceivedChanged += bytesChanged;
        EventHandler<object> estimatedEndTimeChanged = (_, _) =>
        {
            if (isClosing || IsDisposed) return;
            try
            {
                entry.EstimatedEndTimeText = FormatEstimatedEndTime(operation.EstimatedEndTime);
            }
            catch (Exception error) when (
                error is InvalidOperationException
                    or COMException
                    or ObjectDisposedException)
            {
                return;
            }
            RefreshDownloadsUi();
        };
        entry.EstimatedEndTimeChangedHandler = estimatedEndTimeChanged;
        operation.EstimatedEndTimeChanged += estimatedEndTimeChanged;
        RefreshDownloadsUi();
    }

    private void OnLaunchingExternalUriScheme(BrowserTab tab, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        string requestedUri;
        string? initiatingOrigin;
        bool isUserInitiated;
        var documentGeneration = tab.DocumentNavigationGeneration;
        try
        {
            // Default to deny before dereferencing any apartment-bound event
            // properties. If the browser process disconnects during the prompt,
            // this request must not silently fall through to the OS handler.
            e.Cancel = true;
            isUserInitiated = e.IsUserInitiated;
            requestedUri = e.Uri;
            initiatingOrigin = e.InitiatingOrigin;
        }
        catch (Exception error)
        {
            stateStore.Log("Could not inspect an external-app request", error);
            if (!isClosing) ShowTransientStatus("Blocked an external-app request that could not be verified");
            return;
        }

        if (!isUserInitiated)
        {
            ShowTransientStatus("Blocked an external-app request without a user gesture");
            return;
        }

        if (!IsAllowedExternalNavigation(requestedUri, isUserInitiated))
        {
            ShowTransientStatus("Blocked a restricted external protocol request");
            return;
        }

        CoreWebView2Deferral? deferral;
        try
        {
            deferral = e.GetDeferral();
        }
        catch (Exception error)
        {
            stateStore.Log("Could not defer external-app confirmation", error);
            return;
        }

        var scheme = Uri.TryCreate(requestedUri, UriKind.Absolute, out var uri) ? uri.Scheme : "external";
        var safeScheme = SanitizeExtensionPromptText(scheme, 40);
        var uriPreview = SanitizeExtensionPromptText(TextSafety.FormatUrlForDisplay(requestedUri, 180), 180);
        var originValue = NormalizePermissionOrigin(initiatingOrigin)
            ?? NormalizePermissionOrigin(tab.Url)
            ?? HostFromUrl(tab.Url);
        var origin = SanitizeExtensionPromptText(originValue, 160);

        if (!PostUi(() =>
        {
            try
            {
                if (isClosing
                    || tab.IsClosed
                    || tab.DocumentNavigationGeneration != documentGeneration)
                {
                    return;
                }

                var promptDetail = string.IsNullOrWhiteSpace(uriPreview)
                    ? $"Allow {origin} to open the '{safeScheme}' app?"
                    : $"Allow {origin} to open the '{safeScheme}' app?{Environment.NewLine}{Environment.NewLine}{uriPreview}";
                var allowed = NativeAlertDialog.Confirm(
                    this,
                    "Open an external app?",
                    promptDetail,
                    confirmText: "Open app",
                    cancelText: "Cancel");
                if (isClosing
                    || tab.IsClosed
                    || tab.DocumentNavigationGeneration != documentGeneration)
                {
                    return;
                }
                e.Cancel = !allowed;
                tab.IgnoreNextExternalFailure = true;
                tab.ExternalNavigationStatus = allowed ? "Opened external app" : "External app canceled";
            }
            catch (Exception error)
            {
                stateStore.Log("Could not complete an external-app request", error);
                if (!isClosing) ShowTransientStatus("The external-app request was canceled");
            }
            finally
            {
                try { deferral.Complete(); }
                catch { }
            }
        }))
        {
            try { deferral.Complete(); }
            catch { }
        }
    }

    private async Task RecreateTabAsync(BrowserTab tab)
    {
        if (isClosing || tab.IsClosed) return;
        if (environment is null)
        {
            await RestartBrowserAsync();
            return;
        }

        if (!tab.IsStartPage)
        {
            await CaptureTabScrollOffsetAsync(tab);
            if (isClosing || tab.IsClosed) return;
        }
        var targetUrl = tab.Url;
        var showStartPage = tab.IsStartPage || targetUrl.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase);
        ReplaceTabView(tab);
        if (showStartPage)
        {
            ShowStartPage(tab, false);
            return;
        }
        var navigationRequestId = ++tab.NavigationRequestId;
        if (!await InitializeTabAsync(tab, requireActive: true))
        {
            PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
            return;
        }
        if (!CanContinueRequiredTabInitialization(tab))
        {
            PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
            return;
        }
        if (tab.NavigationRequestId != navigationRequestId
            || tab.IsStartPage
            || !BrowserPolicy.UrlEquals(tab.Url, targetUrl)) return;
        await EnsureUrlScopedScriptsRegisteredIfNeededAsync(tab, targetUrl);
        if (isClosing
            || tab.IsClosed
            || tab.NavigationRequestId != navigationRequestId
            || tab.IsStartPage
            || !BrowserPolicy.UrlEquals(tab.Url, targetUrl)
            || tab.Core is null)
        {
            return;
        }
        tab.PendingRestoreScrollY = tab.SavedScrollY;
        NavigateTabCore(tab, targetUrl);
    }

    private Task RestartBrowserForUpdateAsync()
    {
        if (!browserRuntimeUpdateAvailable) return Task.CompletedTask;
        var result = Program.RequestBrowserProcessRestart(this);
        switch (result)
        {
            case Program.BrowserRestartRequestResult.Started:
                break;
            case Program.BrowserRestartRequestResult.Canceled:
                ShowTransientStatus("Browser update restart canceled");
                break;
            case Program.BrowserRestartRequestResult.StateChanged:
                ShowTransientStatus("Browser windows changed — review them and try the restart again");
                break;
            case Program.BrowserRestartRequestResult.ExtensionOperationInProgress:
                ShowTransientStatus("Finish or cancel the extension operation before restarting MishaWeb");
                extensionsManager?.Activate();
                break;
            case Program.BrowserRestartRequestResult.SessionSaveFailed:
                ShowTransientStatus("Restart canceled because the current session could not be saved");
                NativeAlertDialog.ShowInfo(
                    this,
                    "Session could not be saved",
                    "MishaWeb could not safely save your open tabs, so the browser update restart was canceled and every window was left open.");
                break;
            case Program.BrowserRestartRequestResult.LaunchFailed:
                ShowTransientStatus("Could not start the updated browser process");
                NativeAlertDialog.ShowInfo(
                    this,
                    "Could not restart MishaWeb",
                    "MishaWeb could not start its replacement process. Your windows were left open and the update restart was canceled.");
                break;
            default:
                ShowTransientStatus("The browser update restart is unavailable");
                break;
        }
        return Task.CompletedTask;
    }

    private async Task RestartBrowserAsync()
    {
        if (isClosing || recoveringBrowser) return;
        recoveringBrowser = true;
        ShowTransientStatus("Restarting browser\u2026", false);

        try
        {
            foreach (var tab in tabs) ReplaceTabView(tab);
            InvalidateBrowserEnvironment();
            await GetOrCreateEnvironmentAsync();
            if (isClosing) return;

            var tabToRestore = activeTab ?? tabs.FirstOrDefault();
            foreach (var tab in tabs.Where(tab => tab != tabToRestore).ToArray())
            {
                if (tab.IsStartPage || tab.Url.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase))
                {
                    ShowStartPage(tab, false);
                }
                else
                {
                    MarkTabDiscarded(tab, "Waiting after browser recovery");
                }
            }

            if (tabToRestore is not null && !tabToRestore.IsClosed)
            {
                if (tabToRestore.IsStartPage
                    || tabToRestore.Url.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase))
                {
                    ShowStartPage(tabToRestore, false);
                }
                else
                {
                    var targetUrl = tabToRestore.Url;
                    var navigationRequestId = ++tabToRestore.NavigationRequestId;
                    var initialized = await InitializeTabAsync(tabToRestore, requireActive: true);
                    if (!initialized)
                    {
                        PreserveCanceledInitializationAsDiscarded(tabToRestore, navigationRequestId);
                    }
                    else if (!CanContinueRequiredTabInitialization(tabToRestore))
                    {
                        PreserveCanceledInitializationAsDiscarded(tabToRestore, navigationRequestId);
                    }
                    else if (tabToRestore.NavigationRequestId == navigationRequestId
                        && BrowserPolicy.UrlEquals(tabToRestore.Url, targetUrl))
                    {
                        await EnsureUrlScopedScriptsRegisteredIfNeededAsync(tabToRestore, targetUrl);
                        if (!isClosing
                            && !tabToRestore.IsClosed
                            && tabToRestore.NavigationRequestId == navigationRequestId
                            && BrowserPolicy.UrlEquals(tabToRestore.Url, targetUrl)
                            && tabToRestore.Core is not null)
                        {
                            NavigateTabCore(tabToRestore, targetUrl);
                        }
                    }
                }
            }

            if (tabs.Count == 0)
            {
                await OpenNewTabAsync(StartPage.Url);
            }
            else if (tabToRestore is not null && activeTab == tabToRestore && !tabToRestore.IsClosed)
            {
                ActivateTab(tabToRestore, focusPage: false);
            }
            browserRuntimeUpdateAvailable = false;
            restartBrowserForUpdateMenuItem.Visible = false;
            ShowTransientStatus("Browser restored");
        }
        catch (Exception error)
        {
            InvalidateBrowserEnvironment();
            stateStore.Log("Browser recovery failed", error);
            foreach (var tab in tabs)
            {
                ShowTabOverlay(
                    tab,
                    "Browser recovery failed",
                    error.Message,
                    "Try again",
                    RestartBrowserAsync);
            }
            ShowTransientStatus("Browser recovery failed");
        }
        finally
        {
            recoveringBrowser = false;
            ReleaseBrowserEnvironmentIfIdle();
            RefreshMemorySweepTimer();
        }
    }

    private async Task FindOnPageAsync(bool backwards)
    {
        var core = activeTab?.Core;
        var query = findBox.Text;
        if (core is null)
        {
            ShowTransientStatus("Find is unavailable until a website is open");
            return;
        }
        if (string.IsNullOrEmpty(query))
        {
            StopFindSession(activeTab);
            findMatchLabel.Text = string.Empty;
            ShowTransientStatus("Type a word to find on this page");
            return;
        }

        var tab = activeTab!;
        if (tab.FindUsingNativeApi && tab.FindSession is not null
            && tab.FindCaseSensitive == findMatchCase
            && tab.FindQuery.Equals(query, StringComparison.Ordinal))
        {
            if (backwards) tab.FindSession.FindPrevious();
            else tab.FindSession.FindNext();
            UpdateFindMatchLabel(tab);
            return;
        }

        await StartFindSession(backwards);
    }

    private void ShowFindBar()
    {
        if (environment is null || activeTab?.Core is null)
        {
            ShowTransientStatus("Find is unavailable until a website is open");
            return;
        }
        var openGeneration = ++findOpenGeneration;
        var editGeneration = findBoxEditGeneration;
        findBar.Visible = true;
        rootLayout.RowStyles[2].Height = ScaleChromeLogical(44);
        UpdateFindBarLayout();
        findBox.Focus();
        findBox.SelectAll();
        findDebounceTimer.Stop();
        if (findBox.TextLength > 0) RunUiTask(StartFindSession, "Find failed");
        RunUiTask(() => PrefillFindSelectionAsync(activeTab, openGeneration, editGeneration), "Find failed");
    }

    private async Task PrefillFindSelectionAsync(BrowserTab? tab, long openGeneration, long editGeneration)
    {
        if (tab?.Core is not { } core || tab.IsStartPage || tab.IsClosed) return;
        var documentGeneration = tab.DocumentNavigationGeneration;
        string? rawSelection;
        try
        {
            rawSelection = await core.ExecuteScriptAsync(
                "(() => { try { const text = window.getSelection ? String(window.getSelection() || '') : ''; return text.length > 0 && text.length <= 200 ? text : ''; } catch (_) { return ''; } })()");
        }
        catch
        {
            return;
        }

        if (isClosing
            || !findBar.Visible
            || openGeneration != findOpenGeneration
            || editGeneration != findBoxEditGeneration
            || activeTab != tab
            || tab.IsClosed
            || !ReferenceEquals(tab.Core, core)
            || tab.DocumentNavigationGeneration != documentGeneration)
        {
            return;
        }

        string? selected = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(rawSelection) && !rawSelection.Equals("null", StringComparison.Ordinal))
            {
                selected = JsonSerializer.Deserialize(rawSelection, BrowserStateJsonContext.Default.String);
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(selected)) return;
        var normalized = selected.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length == 0 || normalized.Length > 200) return;

        suppressFindEditTracking = true;
        try
        {
            findBox.Text = normalized;
            findBox.SelectAll();
        }
        finally
        {
            suppressFindEditTracking = false;
        }
        findDebounceTimer.Stop();
        await StartFindSession();
    }

    private void HideFindBar()
    {
        findOpenGeneration++;
        findDebounceTimer.Stop();
        StopFindSession(activeTab);
        findBar.Visible = false;
        rootLayout.RowStyles[2].Height = 0;
        findMatchLabel.Text = string.Empty;
        activeTab?.View?.Focus();
    }

    private Task StartFindSession() => StartFindSession(backwards: false);

    private async Task StartFindSession(bool backwards)
    {
        var tab = activeTab;
        var core = tab?.Core;
        var query = findBox.Text;
        if (tab is null || core is null || string.IsNullOrEmpty(query))
        {
            StopFindSession(tab);
            findMatchLabel.Text = string.Empty;
            return;
        }

        var caseSensitive = findMatchCase;
        var documentGeneration = tab.DocumentNavigationGeneration;
        StopFindSession(tab);
        var requestGeneration = tab.FindRequestGeneration;
        CoreWebView2Find? find = null;
        EventHandler<object>? matchCountChanged = null;
        EventHandler<object>? activeMatchChanged = null;
        try
        {
            find = core.Find;
            matchCountChanged = (_, _) =>
            {
                if (IsFindContinuationCurrent(
                        tab,
                        core,
                        documentGeneration,
                        requestGeneration,
                        find,
                        matchCountChanged,
                        activeMatchChanged,
                        query))
                {
                    UpdateFindMatchLabel(tab);
                }
            };
            activeMatchChanged = (_, _) =>
            {
                if (IsFindContinuationCurrent(
                        tab,
                        core,
                        documentGeneration,
                        requestGeneration,
                        find,
                        matchCountChanged,
                        activeMatchChanged,
                        query))
                {
                    UpdateFindMatchLabel(tab);
                }
            };
            tab.FindSession = find;
            tab.FindQuery = query;
            tab.FindCaseSensitive = caseSensitive;
            tab.FindUsingNativeApi = true;
            tab.FindMatchCountChangedHandler = matchCountChanged;
            tab.FindActiveMatchIndexChangedHandler = activeMatchChanged;
            find.MatchCountChanged += matchCountChanged;
            find.ActiveMatchIndexChanged += activeMatchChanged;
            var options = (environment ?? throw new InvalidOperationException("Browser environment is not ready"))
                .CreateFindOptions();
            options.FindTerm = query;
            options.IsCaseSensitive = caseSensitive;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;
            await find.StartAsync(options);
            if (!IsFindContinuationCurrent(
                    tab,
                    core,
                    documentGeneration,
                    requestGeneration,
                    find,
                    matchCountChanged,
                    activeMatchChanged,
                    query))
            {
                ReleaseFindSession(tab, find, matchCountChanged, activeMatchChanged);
                return;
            }
            if (backwards) find.FindPrevious();
            UpdateFindMatchLabel(tab);
        }
        catch (Exception error) when (IsUnsupportedFindException(error))
        {
            var requestIsCurrent = find is null
                ? IsLegacyFindContinuationCurrent(
                    tab,
                    core,
                    documentGeneration,
                    requestGeneration,
                    query)
                : IsFindContinuationCurrent(
                    tab,
                    core,
                    documentGeneration,
                    requestGeneration,
                    find,
                    matchCountChanged,
                    activeMatchChanged,
                    query);
            if (find is not null)
            {
                ReleaseFindSession(tab, find, matchCountChanged, activeMatchChanged);
            }
            if (!requestIsCurrent) return;
            tab.FindUsingNativeApi = false;
            await RunLegacyFindAsync(
                tab,
                core,
                documentGeneration,
                requestGeneration,
                query,
                caseSensitive,
                backwards);
        }
        catch
        {
            if (find is not null)
            {
                ReleaseFindSession(tab, find, matchCountChanged, activeMatchChanged);
            }
            throw;
        }
    }

    private async Task RunLegacyFindAsync(
        BrowserTab tab,
        CoreWebView2 core,
        long documentGeneration,
        long requestGeneration,
        string query,
        bool caseSensitive,
        bool backwards)
    {
        if (!IsLegacyFindContinuationCurrent(
                tab,
                core,
                documentGeneration,
                requestGeneration,
                query)) return;
        var encoded = JsonSerializer.Serialize(query, BrowserStateJsonContext.Default.String);
        var script = $"window.find({encoded}, {(caseSensitive ? "true" : "false")}, {(backwards ? "true" : "false")}, true, false, true, false)";
        var result = await core.ExecuteScriptAsync(script);
        if (!IsLegacyFindContinuationCurrent(
                tab,
                core,
                documentGeneration,
                requestGeneration,
                query)) return;
        findMatchLabel.Text = result.Equals("true", StringComparison.OrdinalIgnoreCase)
            ? "Match found"
            : "No match";
    }

    private void StopFindSession(BrowserTab? tab)
    {
        if (tab is null) return;
        tab.FindRequestGeneration++;
        var find = tab.FindSession;
        if (find is null)
        {
            tab.FindQuery = string.Empty;
            tab.FindUsingNativeApi = false;
            if (activeTab == tab) findMatchLabel.Text = string.Empty;
            return;
        }
        ReleaseFindSession(
            tab,
            find,
            tab.FindMatchCountChangedHandler,
            tab.FindActiveMatchIndexChangedHandler);
    }

    private void ReleaseFindSession(
        BrowserTab tab,
        CoreWebView2Find find,
        EventHandler<object>? matchCountChanged,
        EventHandler<object>? activeMatchChanged)
    {
        var ownsCurrentSession = IsFindSessionOwner(
            ReferenceEquals(tab.FindSession, find),
            ReferenceEquals(tab.FindMatchCountChangedHandler, matchCountChanged),
            ReferenceEquals(tab.FindActiveMatchIndexChangedHandler, activeMatchChanged));
        try
        {
            if (matchCountChanged is not null)
            {
                find.MatchCountChanged -= matchCountChanged;
            }
            if (activeMatchChanged is not null)
            {
                find.ActiveMatchIndexChanged -= activeMatchChanged;
            }
            // CoreWebView2.Find can return the same per-core object to
            // overlapping requests. A stale completion may remove only its
            // own handlers; stopping the shared object would cancel the newer
            // request that now owns it.
            if (ownsCurrentSession) find.Stop();
        }
        catch (Exception error)
        {
            stateStore.Log("Could not stop page find", error);
        }
        if (!ownsCurrentSession) return;
        tab.FindSession = null;
        tab.FindMatchCountChangedHandler = null;
        tab.FindActiveMatchIndexChangedHandler = null;
        tab.FindQuery = string.Empty;
        tab.FindUsingNativeApi = false;
        if (activeTab == tab) findMatchLabel.Text = string.Empty;
    }

    private void UpdateFindMatchLabel(BrowserTab tab)
    {
        if (tab.FindSession is null || activeTab != tab) return;
        try
        {
            var active = tab.FindSession.ActiveMatchIndex;
            var total = tab.FindSession.MatchCount;
            findMatchLabel.Text = total > 0 && active > 0
                ? $"{active} / {total}"
                : total == 0 ? "No matches" : $"\u2014 / {total}";
        }
        catch (Exception error) when (IsUnsupportedFindException(error))
        {
            StopFindSession(tab);
        }
    }

    private bool IsFindContinuationCurrent(
        BrowserTab tab,
        CoreWebView2 core,
        long documentGeneration,
        long requestGeneration,
        CoreWebView2Find find,
        EventHandler<object>? matchCountChanged,
        EventHandler<object>? activeMatchChanged,
        string query) =>
        IsFindContinuationEligible(
            isClosing,
            tab.IsClosed,
            ReferenceEquals(activeTab, tab),
            ReferenceEquals(tab.Core, core),
            tab.DocumentNavigationGeneration == documentGeneration,
            tab.FindRequestGeneration == requestGeneration
                && IsFindSessionOwner(
                    ReferenceEquals(tab.FindSession, find),
                    ReferenceEquals(tab.FindMatchCountChangedHandler, matchCountChanged),
                    ReferenceEquals(tab.FindActiveMatchIndexChangedHandler, activeMatchChanged)),
            findBar.Visible,
            findBox.Text.Equals(query, StringComparison.Ordinal));

    private bool IsLegacyFindContinuationCurrent(
        BrowserTab tab,
        CoreWebView2 core,
        long documentGeneration,
        long requestGeneration,
        string query) =>
        IsFindContinuationEligible(
            isClosing,
            tab.IsClosed,
            ReferenceEquals(activeTab, tab),
            ReferenceEquals(tab.Core, core),
            tab.DocumentNavigationGeneration == documentGeneration,
            tab.FindRequestGeneration == requestGeneration
                && tab.FindSession is null,
            findBar.Visible,
            findBox.Text.Equals(query, StringComparison.Ordinal));

    internal static bool IsFindSessionOwner(
        bool sessionIsCurrent,
        bool matchHandlerIsCurrent,
        bool activeHandlerIsCurrent) =>
        sessionIsCurrent
        && matchHandlerIsCurrent
        && activeHandlerIsCurrent;

    internal static bool IsFindContinuationEligible(
        bool isClosing,
        bool tabIsClosed,
        bool tabIsActive,
        bool coreIsCurrent,
        bool documentIsCurrent,
        bool sessionIsCurrent,
        bool findBarIsVisible,
        bool queryIsCurrent) =>
        !isClosing
        && !tabIsClosed
        && tabIsActive
        && coreIsCurrent
        && documentIsCurrent
        && sessionIsCurrent
        && findBarIsVisible
        && queryIsCurrent;

    private static bool IsUnsupportedFindException(Exception error)
    {
        return error is NotSupportedException
            or InvalidOperationException
            or System.Runtime.InteropServices.COMException;
    }

    private void ChangeZoom(double delta, bool reset = false)
    {
        if (environment is null) return;
        try
        {
            var tab = activeTab;
            var view = tab?.View;
            if (tab is null || view?.CoreWebView2 is null) return;
            var next = reset ? 1.0 : Math.Clamp(view.ZoomFactor + delta, 0.25, 3.0);
            tab.ZoomFactor = next;
            applyingZoomPreference = true;
            try { view.ZoomFactor = next; }
            finally { applyingZoomPreference = false; }
            SaveSiteZoomPreference(tab);
            ApplyZoomPreferenceToMatchingTabs(tab.Url, next);
            ShowTransientStatus($"Zoom {tab.ZoomFactor:P0}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not change page zoom", error);
            ShowTransientStatus("Zoom is unavailable while the browser restarts");
        }
    }

    private void ApplySitePreferences(BrowserTab tab, bool persist)
    {
        if (tab.IsClosed) return;
        if (tab.IsStartPage || !BrowserPolicy.IsHttpUrl(tab.Url))
        {
            tab.ZoomFactor = 1.0;
            tab.IsMuted = false;
        }
        else
        {
            tab.ZoomFactor = isPrivateMode
                ? tab.ZoomFactor
                : SitePreferencePolicy.GetZoom(state.SiteZoom, tab.Url);
            var siteMuted = isPrivateMode
                ? tab.IsMuted
                : SitePreferencePolicy.ShouldMuteAudio(state.AudioOutputPolicy, state.AllowedAudioHosts, state.MutedHosts, tab.Url);
            tab.IsMuted = tab.TabMuteOverride ?? siteMuted;
        }

        if (tab.View is { CoreWebView2: not null } view)
        {
            try
            {
                applyingZoomPreference = true;
                view.ZoomFactor = Math.Clamp(tab.ZoomFactor, BrowserStateStore.MinimumSiteZoom, BrowserStateStore.MaximumSiteZoom);
                view.CoreWebView2.IsMuted = tab.IsMuted;
            }
            catch (Exception error)
            {
                stateStore.Log("Could not apply site preferences", error);
            }
            finally
            {
                applyingZoomPreference = false;
            }
        }

        if (persist && !isPrivateMode) ScheduleStateSave();
        UpdateTabHeader(tab);
    }

    private void ApplyZoomPreferenceToMatchingTabs(string url, double factor)
    {
        var host = SitePreferencePolicy.GetHost(url);
        if (host is null) return;
        foreach (var tab in tabs.Where(item =>
                     !item.IsClosed
                     && item.Core is not null
                     && SitePreferencePolicy.GetHost(item.Url)?.Equals(host, StringComparison.OrdinalIgnoreCase) == true))
        {
            tab.ZoomFactor = factor;
            if (tab.View is not { CoreWebView2: not null } view) continue;
            try
            {
                applyingZoomPreference = true;
                view.ZoomFactor = factor;
            }
            catch (Exception error)
            {
                stateStore.Log("Could not apply exact-host zoom", error);
            }
            finally
            {
                applyingZoomPreference = false;
            }
        }
    }

    private void SaveSiteZoomPreference(BrowserTab tab)
    {
        if (isPrivateMode || tab.IsStartPage) return;
        var host = SitePreferencePolicy.GetHost(tab.Url);
        if (host is null) return;
        state.SiteZoom.RemoveAll(item => BrowserPolicy.IsExactHost(item.Host, host));
        if (Math.Abs(tab.ZoomFactor - 1.0) >= 0.000_001)
        {
            state.SiteZoom.Insert(0, new SiteZoomEntry(
                host,
                Math.Clamp(tab.ZoomFactor, BrowserStateStore.MinimumSiteZoom, BrowserStateStore.MaximumSiteZoom)));
            if (state.SiteZoom.Count > BrowserStateStore.MaximumSiteZoomEntries)
            {
                state.SiteZoom.RemoveRange(
                    BrowserStateStore.MaximumSiteZoomEntries,
                    state.SiteZoom.Count - BrowserStateStore.MaximumSiteZoomEntries);
            }
        }
        ScheduleStateSave();
    }

    private void ToggleActiveTabAudioMute()
    {
        var tab = activeTab;
        if (tab is null || tab.IsClosed)
        {
            ShowTransientStatus("No active tab to mute");
            return;
        }
        ToggleTabAudioMute(tab);
    }

    private void ToggleTabAudioMute(BrowserTab tab)
    {
        if (tab.IsClosed) return;
        var nextMuted = !IsTabMuted(tab);
        tab.TabMuteOverride = nextMuted;
        tab.IsMuted = nextMuted;
        if (tab.Core is not null)
        {
            try { tab.Core.IsMuted = nextMuted; }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        UpdateTabHeader(tab);
        ShowTransientStatus(nextMuted ? "Muted tab audio" : "Unmuted tab audio");
    }

    private void ToggleActiveSiteMute()
    {
        var tab = activeTab;
        if (tab is null || tab.IsStartPage)
        {
            ShowTransientStatus("Open a website before changing site audio");
            return;
        }
        ToggleSiteMute(tab);
    }

    private void ToggleSiteMute(BrowserTab tab)
    {
        if (isPrivateMode)
        {
            var nextMuted = !IsTabMuted(tab);
            tab.TabMuteOverride = nextMuted;
            tab.IsMuted = nextMuted;
            if (tab.Core is not null)
            {
                try { tab.Core.IsMuted = nextMuted; }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
            UpdateTabHeader(tab);
            return;
        }

        var host = SitePreferencePolicy.GetHost(tab.Url);
        if (host is null) return;

        bool muted;
        if (state.AudioOutputPolicy == AudioOutputPolicy.SpecificSitesOnly)
        {
            var currentlyAllowed = state.AllowedAudioHosts.Any(item => BrowserPolicy.IsExactHost(item, host));
            state.AllowedAudioHosts.RemoveAll(item => BrowserPolicy.IsExactHost(item, host));
            if (!currentlyAllowed)
            {
                state.AllowedAudioHosts.Insert(0, host);
                if (state.AllowedAudioHosts.Count > BrowserStateStore.MaximumAudioHosts)
                {
                    state.AllowedAudioHosts.RemoveRange(
                        BrowserStateStore.MaximumAudioHosts,
                        state.AllowedAudioHosts.Count - BrowserStateStore.MaximumAudioHosts);
                }
                muted = false;
            }
            else
            {
                muted = true;
            }
        }
        else if (state.AudioOutputPolicy == AudioOutputPolicy.MuteAll)
        {
            ShowTransientStatus("Web audio is muted globally in Voice & Audio settings");
            return;
        }
        else
        {
            muted = !SitePreferencePolicy.IsMuted(state.MutedHosts, host);
            state.MutedHosts.RemoveAll(item => BrowserPolicy.IsExactHost(item, host));
            if (muted) state.MutedHosts.Insert(0, host);
            if (state.MutedHosts.Count > BrowserStateStore.MaximumMutedHosts)
            {
                state.MutedHosts.RemoveRange(
                    BrowserStateStore.MaximumMutedHosts,
                    state.MutedHosts.Count - BrowserStateStore.MaximumMutedHosts);
            }
        }

        foreach (var liveTab in tabs.Where(item =>
                     !item.IsClosed
                     && SitePreferencePolicy.GetHost(item.Url)?.Equals(host, StringComparison.OrdinalIgnoreCase) == true))
        {
            liveTab.TabMuteOverride = null;
            liveTab.IsMuted = muted;
            if (liveTab.Core is not null)
            {
                try { liveTab.Core.IsMuted = muted; }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
            UpdateTabHeader(liveTab);
        }
        ScheduleStateSave();
        ShowTransientStatus(muted ? $"Muted {host}" : $"Unmuted {host}");
    }

    private void PrintPage()
    {
        var tab = activeTab;
        if (tab is null || tab.IsStartPage || tab.Core is null)
        {
            ShowTransientStatus("Open a website before printing");
            return;
        }

        try
        {
            tab.Core.ShowPrintUI(CoreWebView2PrintDialogKind.System);
        }
        catch (Exception error)
        {
            stateStore.Log("Could not open the print dialog", error);
            ShowTransientStatus("Printing is unavailable for this page");
        }
    }

    private async Task CapturePageAsync()
    {
        var tab = activeTab;
        if (tab is null)
        {
            ShowTransientStatus("No page is available to capture");
            return;
        }

        var picturesFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "png",
            Filter = "PNG image (*.png)|*.png",
            InitialDirectory = Directory.Exists(picturesFolder) ? picturesFolder : null,
            FileName = $"{MakeSafeFileName(tab.Title)}-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            OverwritePrompt = true,
            Title = "Capture page as PNG"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (isClosing || IsDisposed || tab.IsClosed) return;

        try
        {
            await using var stream = new FileStream(
                dialog.FileName,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                useAsync: true);
            if (tab.IsStartPage)
            {
                var startPageView = EnsureNativeStartPage(tab);
                using var image = new Bitmap(
                    Math.Max(1, startPageView.ClientSize.Width),
                    Math.Max(1, startPageView.ClientSize.Height));
                startPageView.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            }
            else if (tab.Core is not null)
            {
                await tab.Core.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png,
                    stream);
                if (isClosing || IsDisposed || tab.IsClosed) return;
            }
            else
            {
                ShowTransientStatus("Wait for the page to finish starting");
                return;
            }

            if (!isClosing && !IsDisposed) ShowTransientStatus($"Saved {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not capture the page", error);
            ShowTransientStatus("Could not save the page capture");
        }
    }

    internal static string MakeSafeFileName(string value)
    {
        var title = string.IsNullOrWhiteSpace(value)
            ? "MishaWeb"
            : TextSafety.RemoveUnpairedSurrogates(value.Trim());
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var characters = title
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray();
        var safe = new string(characters).Trim(' ', '.', '-');
        if (safe.Length == 0) safe = "MishaWeb";
        return TextSafety.Truncate(safe, 64).TrimEnd();
    }

    private void GoBack()
    {
        try
        {
            if (activeTab?.Core?.CanGoBack == true) activeTab.Core.GoBack();
        }
        catch (Exception error)
        {
            stateStore.Log("Back navigation failed", error);
        }
    }

    private void GoForward()
    {
        try
        {
            if (activeTab?.Core?.CanGoForward == true) activeTab.Core.GoForward();
        }
        catch (Exception error)
        {
            stateStore.Log("Forward navigation failed", error);
        }
    }

    private void ReloadOrStop()
    {
        try
        {
            var tab = activeTab;
            if (tab?.Core is null) return;
            if (tab.IsLoading) tab.Core.Stop();
            else if (tab.IsStartPage) ShowStartPage(tab, false);
            else tab.Core.Reload();
        }
        catch (Exception error)
        {
            stateStore.Log("Reload or stop failed", error);
        }
    }

    private void OnContextMenuRequested(
        BrowserTab tab,
        CoreWebView2ContextMenuRequestedEventArgs args)
    {
        tab.ContextLinkTarget = null;
        try
        {
            var target = args.ContextMenuTarget;
            if (!target.HasLinkUri) return;
            var link = target.LinkUri;
            if (string.IsNullOrWhiteSpace(link) || !BrowserPolicy.IsHttpUrl(link)) return;
            tab.ContextLinkTarget = link;
            var item = tab.CleanLinkMenuItem;
            if (item is null)
            {
                var menuEnvironment = environment;
                if (menuEnvironment is null) return;
                item = menuEnvironment.CreateContextMenuItem(
                    "Copy clean link",
                    null,
                    CoreWebView2ContextMenuItemKind.Command);
                EventHandler<object> selectedHandler = (_, _) => CopyContextLink(tab);
                item.CustomItemSelected += selectedHandler;
                tab.CleanLinkMenuItem = item;
                tab.CleanLinkMenuItemSelectedHandler = selectedHandler;
            }
            item.IsEnabled = true;
            args.MenuItems.Add(item);

            var splitItem = tab.SplitViewLinkMenuItem;
            if (splitItem is null && environment is { } splitMenuEnvironment)
            {
                splitItem = splitMenuEnvironment.CreateContextMenuItem(
                    "Open link in split view",
                    null,
                    CoreWebView2ContextMenuItemKind.Command);
                EventHandler<object> splitSelectedHandler = (_, _) => OpenContextLinkInSplitView(tab);
                splitItem.CustomItemSelected += splitSelectedHandler;
                tab.SplitViewLinkMenuItem = splitItem;
                tab.SplitViewLinkMenuItemSelectedHandler = splitSelectedHandler;
            }
            if (splitItem is not null)
            {
                splitItem.IsEnabled = true;
                args.MenuItems.Add(splitItem);
            }
        }
        catch (Exception error)
        {
            stateStore.Log("Could not add the clean-link context menu item", error);
        }
    }

    private void CopyContextLink(BrowserTab tab)
    {
        var target = tab.ContextLinkTarget;
        if (tab.IsClosed
            || tab.Core is null
            || string.IsNullOrWhiteSpace(target)
            || !BrowserPolicy.IsHttpUrl(target))
        {
            ShowTransientStatus("The link is no longer available");
            return;
        }
        try
        {
            Clipboard.SetText(CleanLinkPolicy.Clean(target));
            ShowTransientStatus("Clean link copied");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not copy the context link", error);
            ShowTransientStatus("Could not copy the link");
        }
    }

    private async Task RefreshTabMediaPermissionStateAsync(
        BrowserTab tab,
        long expectedDocumentGeneration)
    {
        var core = tab.Core;
        var origin = NormalizePermissionOrigin(core?.Source ?? tab.Url);
        if (core is null || origin is null || tab.IsClosed) return;

        await RefreshTabMediaPermissionOriginAsync(
            tab,
            origin,
            expectedDocumentGeneration,
            () => tab.Core == core
                && origin.Equals(
                    NormalizePermissionOrigin(core.Source),
                    StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshTabMediaPermissionOriginAsync(
        BrowserTab tab,
        string? originOrUrl,
        long expectedDocumentGeneration,
        Func<bool> isCurrent)
    {
        var core = tab.Core;
        var origin = NormalizePermissionOrigin(originOrUrl);
        if (core is null
            || origin is null
            || tab.IsClosed
            || tab.DocumentNavigationGeneration != expectedDocumentGeneration)
        {
            return;
        }

        if (isPrivateMode)
        {
            profileMediaPermissionsSeeded = true;
        }

        if (profileMediaPermissionsSeeded
            && !tab.HasMediaCapturePermissionForOrigin(origin)
            && (!hasGrantedMediaPermissionOrigins || !profileGrantedMediaPermissionOrigins.Contains(origin)))
        {
            return;
        }

        tab.PendingMediaPermissionRefreshes++;
        try
        {
            var microphoneAllowed = false;
            var cameraAllowed = false;
            try
            {
                var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
                profileGrantedMediaPermissionOrigins.Clear();
                for (var index = 0; index < settings.Count; index++)
                {
                    var setting = settings[index];
                    if (setting.PermissionState != CoreWebView2PermissionState.Allow)
                    {
                        continue;
                    }

                    var isMicrophone = setting.PermissionKind == CoreWebView2PermissionKind.Microphone;
                    var isCamera = setting.PermissionKind == CoreWebView2PermissionKind.Camera;
                    if (!isMicrophone && !isCamera)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(setting.PermissionOrigin))
                    {
                        profileGrantedMediaPermissionOrigins.Add(setting.PermissionOrigin);
                    }

                    if (!setting.PermissionOrigin.Equals(origin, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (isMicrophone)
                    {
                        microphoneAllowed = true;
                    }
                    else
                    {
                        cameraAllowed = true;
                    }
                }
                hasGrantedMediaPermissionOrigins = profileGrantedMediaPermissionOrigins.Count > 0;
                profileMediaPermissionsSeeded = true;
            }
            catch (Exception error)
            {
                stateStore.Log("Could not refresh camera and microphone permissions", error);
                return;
            }

            if (tab.IsClosed
                || tab.Core != core
                || tab.DocumentNavigationGeneration != expectedDocumentGeneration
                || !isCurrent())
            {
                return;
            }
            ApplyTabMediaPermissions(tab, origin, microphoneAllowed, cameraAllowed);
        }
        finally
        {
            if (tab.DocumentNavigationGeneration == expectedDocumentGeneration)
            {
                tab.PendingMediaPermissionRefreshes = Math.Max(
                    0,
                    tab.PendingMediaPermissionRefreshes - 1);
            }
        }
    }

    private void UpdateTabMediaPermissionState(
        BrowserTab tab,
        string origin,
        CoreWebView2PermissionKind kind,
        CoreWebView2PermissionState permissionState)
    {
        if (kind is not (CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera)) return;
        var microphoneAllowed = tab.MicrophoneAllowedOrigins.Contains(origin);
        var cameraAllowed = tab.CameraAllowedOrigins.Contains(origin);
        var allowed = permissionState == CoreWebView2PermissionState.Allow;
        if (allowed)
        {
            profileGrantedMediaPermissionOrigins.Add(origin);
            hasGrantedMediaPermissionOrigins = true;
        }
        else
        {
            profileMediaPermissionsSeeded = false;
        }
        if (kind == CoreWebView2PermissionKind.Microphone) microphoneAllowed = allowed;
        else cameraAllowed = allowed;
        ApplyTabMediaPermissions(tab, origin, microphoneAllowed, cameraAllowed);
    }

    private void UpdateLiveTabsForMediaPermission(
        string origin,
        CoreWebView2PermissionKind kind,
        CoreWebView2PermissionState permissionState)
    {
        if (kind is CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera)
        {
            if (permissionState == CoreWebView2PermissionState.Allow)
            {
                profileGrantedMediaPermissionOrigins.Add(origin);
                hasGrantedMediaPermissionOrigins = true;
            }
            else
            {
                profileMediaPermissionsSeeded = false;
            }
        }

        foreach (var tab in tabs.Where(item =>
                     !item.IsClosed && TabContainsMediaOrigin(item, origin)))
        {
            UpdateTabMediaPermissionState(tab, origin, kind, permissionState);
        }
    }

    private static bool TabContainsMediaOrigin(BrowserTab tab, string origin)
    {
        return origin.Equals(
                NormalizePermissionOrigin(tab.Core?.Source ?? tab.Url),
                StringComparison.OrdinalIgnoreCase)
            || tab.MicrophoneAllowedOrigins.Contains(origin)
            || tab.CameraAllowedOrigins.Contains(origin)
            || tab.FrameOrigins.Values.Any(value =>
                value.Equals(origin, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshLiveTabMediaPermissionsAsync()
    {
        profileMediaPermissionsSeeded = false;
        foreach (var tab in tabs.Where(item => !item.IsClosed && item.Core is not null).ToArray())
        {
            var core = tab.Core!;
            var documentGeneration = tab.DocumentNavigationGeneration;
            var origins = new HashSet<string>(tab.MicrophoneAllowedOrigins, StringComparer.OrdinalIgnoreCase);
            origins.UnionWith(tab.CameraAllowedOrigins);
            origins.UnionWith(tab.FrameOrigins.Values);
            var topLevelOrigin = NormalizePermissionOrigin(core.Source);
            if (topLevelOrigin is not null) origins.Add(topLevelOrigin);
            foreach (var origin in origins)
            {
                await RefreshTabMediaPermissionOriginAsync(
                    tab,
                    origin,
                    documentGeneration,
                    () => !tab.IsClosed && tab.Core == core);
            }
        }
    }

    private void ApplyTabMediaPermissions(
        BrowserTab tab,
        string origin,
        bool microphoneAllowed,
        bool cameraAllowed)
    {
        var changed = SetOriginPermission(tab.MicrophoneAllowedOrigins, origin, microphoneAllowed)
            | SetOriginPermission(tab.CameraAllowedOrigins, origin, cameraAllowed);
        if (!changed) return;

        OnTabMediaPermissionStateChanged(tab);
    }

    private static bool SetOriginPermission(HashSet<string> origins, string origin, bool allowed)
    {
        return allowed ? origins.Add(origin) : origins.Remove(origin);
    }

    private void OnTabMediaPermissionStateChanged(BrowserTab tab)
    {
        if (tab.HasMediaCapturePermission)
        {
            tab.ConsecutiveSuspendFailures = 0;
            ResumeTab(tab);
        }
        ApplyLiveTabMemoryTarget(tab, IsTabForegroundForLifecycle(tab));
        RefreshMemorySweepTimer();
        UpdateTabHeader(tab);
    }

    private void ClearTabMediaPermissions(BrowserTab tab)
    {
        tab.PendingMediaPermissionRequests = 0;
        tab.PendingMediaPermissionOrigin = null;
        tab.PendingMediaPermissionRefreshes = 0;
        var changed = tab.MicrophoneAllowedOrigins.Count > 0 || tab.CameraAllowedOrigins.Count > 0;
        tab.MicrophoneAllowedOrigins.Clear();
        tab.CameraAllowedOrigins.Clear();
        tab.FrameOrigins.Clear();
        if (changed) OnTabMediaPermissionStateChanged(tab);
    }

    private static bool IsMediaCapturePermission(CoreWebView2PermissionKind kind)
    {
        return kind is CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera;
    }

    private void OnPermissionRequested(
        BrowserTab tab,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        CoreWebView2Deferral? deferral = null;
        var mediaRequestRegistered = false;
        try
        {
            deferral = args.GetDeferral();
            args.Handled = true;
            var origin = NormalizePermissionOrigin(args.Uri);
            if (origin is null || args.PermissionKind == CoreWebView2PermissionKind.UnknownPermission)
            {
                args.State = CoreWebView2PermissionState.Deny;
                args.SavesInProfile = false;
                deferral.Complete();
                return;
            }

            if (args.PermissionKind == CoreWebView2PermissionKind.Autoplay)
            {
                args.State = CoreWebView2PermissionState.Allow;
                args.SavesInProfile = !isPrivateMode;
                deferral.Complete();
                return;
            }

            if (args.PermissionKind == CoreWebView2PermissionKind.Microphone)
            {
                if (!SitePreferencePolicy.CanRequestMicrophone(
                    state.AudioInputPolicy,
                    state.AllowedMicrophoneHosts,
                    state.BlockedMicrophoneHosts,
                    origin,
                    out var autoDecision))
                {
                    args.State = CoreWebView2PermissionState.Deny;
                    args.SavesInProfile = false;
                    deferral.Complete();
                    ShowTransientStatus(state.AudioInputPolicy == AudioInputPolicy.BlockAll
                        ? "Microphone access blocked by Voice & Audio settings"
                        : $"Microphone access blocked for {origin} by Voice & Audio settings");
                    return;
                }
                if (autoDecision == true)
                {
                    args.State = CoreWebView2PermissionState.Allow;
                    args.SavesInProfile = !isPrivateMode;
                    UpdateTabMediaPermissionState(tab, origin, args.PermissionKind, CoreWebView2PermissionState.Allow);
                    deferral.Complete();
                    return;
                }
            }

            if (IsMediaCapturePermission(args.PermissionKind))
            {
                if (tab.PendingMediaPermissionRequests == 0)
                {
                    tab.PendingMediaPermissionOrigin = origin;
                }
                else if (!origin.Equals(
                    tab.PendingMediaPermissionOrigin,
                    StringComparison.OrdinalIgnoreCase))
                {
                    tab.PendingMediaPermissionOrigin = null;
                }
                tab.PendingMediaPermissionRequests++;
                mediaRequestRegistered = true;
            }

            if (permissionQueue.Count + (activePermissionRequest is null ? 0 : 1)
                >= MaximumPendingPermissionRequests)
            {
                if (mediaRequestRegistered)
                {
                    tab.PendingMediaPermissionRequests = Math.Max(0, tab.PendingMediaPermissionRequests - 1);
                    if (tab.PendingMediaPermissionRequests == 0) tab.PendingMediaPermissionOrigin = null;
                    mediaRequestRegistered = false;
                }
                args.State = CoreWebView2PermissionState.Deny;
                args.SavesInProfile = false;
                deferral.Complete();
                deferral = null;
                ShowTransientStatus("Blocked excessive website permission prompts");
                return;
            }

            permissionQueue.Enqueue(new PermissionRequest(
                tab,
                tab.Core!,
                tab.DocumentNavigationGeneration,
                args,
                deferral,
                origin,
                GetPermissionDisplayName(args.PermissionKind),
                args.PermissionKind,
                args.IsUserInitiated));
            deferral = null;
            ShowNextPermissionRequest();
        }
        catch (Exception error)
        {
            if (mediaRequestRegistered)
            {
                tab.PendingMediaPermissionRequests = Math.Max(0, tab.PendingMediaPermissionRequests - 1);
                if (tab.PendingMediaPermissionRequests == 0) tab.PendingMediaPermissionOrigin = null;
            }
            stateStore.Log("Could not queue a website permission request", error);
            try
            {
                args.Handled = true;
                args.State = CoreWebView2PermissionState.Deny;
                args.SavesInProfile = false;
            }
            catch { }
            try { deferral?.Complete(); }
            catch { }
        }
    }

    private void ShowNextPermissionRequest()
    {
        if (isClosing || activePermissionRequest is not null) return;
        while (permissionQueue.Count > 0)
        {
            var request = permissionQueue.Dequeue();
            if (request.Tab.IsClosed
                || request.Tab.Core is null
                || !ReferenceEquals(request.Core, request.Tab.Core)
                || request.DocumentGeneration != request.Tab.DocumentNavigationGeneration)
            {
                CompletePermissionRequest(request, PermissionPromptDecision.Cancel);
                continue;
            }
            activePermissionRequest = request;
            permissionPrompt ??= CreatePermissionPrompt();
            permissionPrompt.ShowRequest(
                this,
                request.Origin,
                request.Permission,
                request.IsUserInitiated,
                allowAlways: !isPrivateMode);
            return;
        }
    }

    private PermissionPromptForm CreatePermissionPrompt()
    {
        var prompt = new PermissionPromptForm();
        prompt.DecisionRequested += (_, decision) =>
        {
            var request = activePermissionRequest;
            activePermissionRequest = null;
            if (request is not null) CompletePermissionRequest(request, decision);
            ShowNextPermissionRequest();
        };
        return prompt;
    }

    private void CompletePermissionRequest(
        PermissionRequest request,
        PermissionPromptDecision decision)
    {
        if (!request.TryBeginCompletion()) return;
        try
        {
            var requestIsCurrent = !request.Tab.IsClosed
                && ReferenceEquals(request.Core, request.Tab.Core)
                && request.DocumentGeneration == request.Tab.DocumentNavigationGeneration;
            var effectiveDecision = !requestIsCurrent
                ? PermissionPromptDecision.Cancel
                : isPrivateMode && decision == PermissionPromptDecision.AllowAlways
                ? PermissionPromptDecision.AllowOnce
                : decision;
            var permissionState = effectiveDecision switch
            {
                PermissionPromptDecision.AllowOnce or PermissionPromptDecision.AllowAlways
                    => CoreWebView2PermissionState.Allow,
                _ => CoreWebView2PermissionState.Deny
            };
            var savesInProfile = !isPrivateMode
                && effectiveDecision is PermissionPromptDecision.AllowAlways or PermissionPromptDecision.Block;
            request.Args.State = permissionState;
            request.Args.SavesInProfile = savesInProfile;
            UpdateTabMediaPermissionState(
                request.Tab,
                request.Origin,
                request.PermissionKind,
                permissionState);
            if (savesInProfile)
            {
                UpdateLiveTabsForMediaPermission(
                    request.Origin,
                    request.PermissionKind,
                    permissionState);
            }
        }
        catch (Exception error)
        {
            stateStore.Log("Could not apply a website permission decision", error);
        }
        finally
        {
            if (IsMediaCapturePermission(request.PermissionKind))
            {
                request.Tab.PendingMediaPermissionRequests = Math.Max(
                    0,
                    request.Tab.PendingMediaPermissionRequests - 1);
                if (request.Tab.PendingMediaPermissionRequests == 0)
                {
                    request.Tab.PendingMediaPermissionOrigin = null;
                }
            }
            try { request.Deferral.Complete(); }
            catch (Exception error) { stateStore.Log("Could not complete a website permission request", error); }
        }
    }

    private void CancelPermissionsForTab(BrowserTab tab)
    {
        if (activePermissionRequest?.Tab == tab)
        {
            permissionPrompt?.CloseRequest();
            var request = activePermissionRequest;
            activePermissionRequest = null;
            if (request is not null) CompletePermissionRequest(request, PermissionPromptDecision.Cancel);
        }

        foreach (var request in permissionQueue.Where(item => item.Tab == tab).ToArray())
        {
            CompletePermissionRequest(request, PermissionPromptDecision.Cancel);
        }
        var remaining = permissionQueue.Where(item => item.Tab != tab).ToArray();
        permissionQueue.Clear();
        foreach (var request in remaining) permissionQueue.Enqueue(request);
        if (!replacingWindow) ShowNextPermissionRequest();
    }

    private void ToggleReducedWebsiteMotion()
    {
        reduceWebsiteMotionEnabled = !reduceWebsiteMotionEnabled;
        state.ReduceWebsiteMotionEnabled = reduceWebsiteMotionEnabled;
        foreach (var tab in tabs.Where(item => item.Core is not null))
        {
            RunUiTask(
                () => ApplyMotionPolicyAsync(tab),
                "Could not update website motion settings");
        }
        if (!isPrivateMode) SavePreferences();
        ShowTransientStatus(reduceWebsiteMotionEnabled
            ? "Reduced website motion on"
            : "Reduced website motion off; reload may restore already-canceled animations");
    }

    private async Task ApplyMotionPolicyAsync(BrowserTab tab)
    {
        var core = tab.Core;
        if (core is null) return;
        var requestGeneration = ++tab.MotionPolicyGeneration;
        var requestedReducedMotion = reduceWebsiteMotionEnabled;
        bool IsCurrent() => IsMotionPolicyContinuationEligible(
            isClosing,
            tab.IsClosed,
            ReferenceEquals(tab.Core, core),
            tab.MotionPolicyGeneration == requestGeneration,
            reduceWebsiteMotionEnabled == requestedReducedMotion);

        if (tab.NoMotionScriptId is not null)
        {
            var existingScriptId = tab.NoMotionScriptId;
            try
            {
                core.RemoveScriptToExecuteOnDocumentCreated(existingScriptId);
            }
            catch (Exception error)
            {
                stateStore.Log("Could not remove the no-motion page policy", error);
            }
            if (IsCurrent()
                && string.Equals(tab.NoMotionScriptId, existingScriptId, StringComparison.Ordinal))
            {
                tab.NoMotionScriptId = null;
            }
        }

        if (!IsCurrent()) return;
        if (requestedReducedMotion)
        {
            string? installedScriptId = null;
            try
            {
                installedScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    BrowserPerformance.NoMotionDocumentScript);
                if (!IsCurrent())
                {
                    try { core.RemoveScriptToExecuteOnDocumentCreated(installedScriptId); }
                    catch (Exception error)
                    {
                        stateStore.Log("Could not remove a stale no-motion page policy", error);
                    }
                    return;
                }
                tab.NoMotionScriptId = installedScriptId;
            }
            catch (Exception error)
            {
                if (IsCurrent())
                {
                    stateStore.Log("Could not install the no-motion page policy", error);
                }
            }
        }
        else
        {
            try { await core.ExecuteScriptAsync(BrowserPerformance.RestoreMotionDocumentScript); }
            catch (Exception error) { stateStore.Log("Could not restore website motion", error); }
        }
    }

    internal static bool IsMotionPolicyContinuationEligible(
        bool isClosing,
        bool tabIsClosed,
        bool coreIsCurrent,
        bool generationIsCurrent,
        bool settingIsCurrent) =>
        !isClosing
        && !tabIsClosed
        && coreIsCurrent
        && generationIsCurrent
        && settingIsCurrent;

    private void OnSystemPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not UserPreferenceCategory.Color and not UserPreferenceCategory.General) return;
        if (isClosing || IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            PostUi(ApplyChromeColorPolicy);
            return;
        }
        ApplyChromeColorPolicy();
    }

    private async Task ToggleReaderModeAsync()
    {
        var tab = activeTab;
        var core = tab?.Core;
        if (tab is null || tab.IsStartPage || core is null)
        {
            ShowTransientStatus("Reader mode is available on an open web page");
            return;
        }
        var documentGeneration = tab.DocumentNavigationGeneration;
        bool IsCurrent() => IsReaderModeContinuationEligible(
            isClosing,
            tab.IsClosed,
            ReferenceEquals(activeTab, tab),
            ReferenceEquals(tab.Core, core),
            tab.DocumentNavigationGeneration == documentGeneration);

        try
        {
            var result = await core.ExecuteScriptAsync(ReaderModeScript.Toggle);
            if (!IsCurrent()) return;
            var enabled = result.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (!enabled && !tab.ReaderModeActive)
            {
                ShowTransientStatus("Reader mode is unavailable for this page");
                return;
            }
            tab.ReaderModeActive = !tab.ReaderModeActive;
            StopFindSession(tab);
            if (findBar.Visible && findBox.TextLength > 0)
            {
                try { await StartFindSession(); }
                catch (Exception findError)
                {
                    if (IsCurrent()) stateStore.Log("Could not refresh find after reader mode", findError);
                }
            }
            if (!IsCurrent()) return;
            UpdateSiteInfo();
            ShowTransientStatus(tab.ReaderModeActive ? "Reader mode on" : "Reader mode off");
        }
        catch (Exception error)
        {
            if (!IsCurrent()) return;
            tab.ReaderModeActive = false;
            stateStore.Log("Could not toggle reader mode", error);
            ShowTransientStatus("Reader mode is unavailable for this page");
        }
    }

    internal static bool IsReaderModeContinuationEligible(
        bool isClosing,
        bool tabIsClosed,
        bool tabIsActive,
        bool coreIsCurrent,
        bool documentIsCurrent) =>
        !isClosing
        && !tabIsClosed
        && tabIsActive
        && coreIsCurrent
        && documentIsCurrent;

    private static string? GetKeepAwakeHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !BrowserPolicy.IsHttpUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return BrowserPolicy.NormalizeExactHost(uri.Host);
    }

    private bool UpdateTabUrlFromSource(BrowserTab tab, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;

        if (BrowserPolicy.TryNormalizeLocalFileUrl(source, out var localFileUrl))
        {
            var keepAwakeChanged = tab.KeepAwake;
            tab.Url = localFileUrl;
            tab.KeepAwake = false;
            if (keepAwakeChanged)
            {
                ApplyLiveTabMemoryTarget(tab, IsTabForegroundForLifecycle(tab));
                RefreshMemorySweepTimer();
            }
            return keepAwakeChanged;
        }
        if (!BrowserPolicy.IsHttpUrl(source)) return false;

        tab.Url = source;
        var keepAwake = IsKeepAwakeHost(source);
        if (tab.KeepAwake == keepAwake) return false;

        tab.KeepAwake = keepAwake;
        if (keepAwake) tab.ConsecutiveSuspendFailures = 0;
        ApplyLiveTabMemoryTarget(tab, IsTabForegroundForLifecycle(tab));
        RefreshMemorySweepTimer();
        return true;
    }

    private bool IsKeepAwakeHost(string? url)
    {
        var host = GetKeepAwakeHost(url);
        if (host is null) return false;
        return state.KeepAwakeHosts.Any(item => BrowserPolicy.IsExactHost(item, host));
    }

    private void SyncKeepAwakeHostState(BrowserTab tab)
    {
        var host = GetKeepAwakeHost(tab.Url);
        if (host is null) return;

        if (tab.KeepAwake)
        {
            state.KeepAwakeHosts.RemoveAll(item => BrowserPolicy.IsExactHost(item, host));
            state.KeepAwakeHosts.Insert(0, host);
            if (state.KeepAwakeHosts.Count > BrowserStateStore.MaximumKeepAwakeHosts)
            {
                state.KeepAwakeHosts.RemoveRange(
                    BrowserStateStore.MaximumKeepAwakeHosts,
                    state.KeepAwakeHosts.Count - BrowserStateStore.MaximumKeepAwakeHosts);
            }
        }
        else
        {
            state.KeepAwakeHosts.RemoveAll(item => BrowserPolicy.IsExactHost(item, host));
        }

        if (!isPrivateMode) ScheduleStateSave();
    }

    private Task<bool> InitializeTabAsync(
        BrowserTab tab,
        bool requireActive = false)
    {
        if (isClosing || tab.IsClosed) return Task.FromResult(false);
        if (requireActive && !CanContinueRequiredTabInitialization(tab))
        {
            return Task.FromResult(false);
        }
        if (tab.InitializationTask is not null) return tab.InitializationTask;
        if (tab.Core is not null) return Task.FromResult(true);

        tab.IsInitializing = true;
        tab.StatusText = "Starting\u2026";
        ShowTabOverlay(tab, "Starting\u2026", "Preparing the browser engine", null, null);
        UpdateTabHeader(tab);
        UpdateStatus();
        RefreshMemorySweepTimer();

        var initializationGeneration = tab.InitializationGeneration;
        var initialization = InitializeTabAfterEnvironmentAsync(
            tab,
            requireActive,
            initializationGeneration);
        tab.InitializationTask = initialization;
        return ClearInitializationWhenCompleteAsync(tab, initialization);
    }

    private async Task<bool> InitializeTabAfterEnvironmentAsync(
        BrowserTab tab,
        bool requireActive,
        long initializationGeneration)
    {
        CoreWebView2Environment initializingEnvironment;
        try
        {
            initializingEnvironment = await GetOrCreateEnvironmentAsync();
        }
        catch (Exception error)
        {
            if (isClosing || tab.IsClosed || tab.InitializationGeneration != initializationGeneration)
            {
                return false;
            }
            tab.IsInitializing = false;
            tab.StatusText = "Tab failed";
            stateStore.Log("Browser engine initialization failed", error);
            ReplaceTabView(tab);
            ShowTabOverlay(
                tab,
                "This tab could not start",
                error.Message,
                "Try again",
                () => RecreateTabAsync(tab));
            UpdateTabHeader(tab);
            UpdateNavigationChrome();
            return false;
        }
        if (!IsTabInitializationCurrent(tab, initializationGeneration, requireActive)
            || environment != initializingEnvironment)
        {
            return false;
        }
        var initializingView = EnsureTabView(tab);
        return await InitializeTabCoreAsync(
            tab,
            initializingView,
            initializingEnvironment,
            requireActive,
            initializationGeneration);
    }

    private bool IsTabInitializationCurrent(
        BrowserTab tab,
        long initializationGeneration,
        bool requireActive)
    {
        return !isClosing
            && !tab.IsClosed
            && !IsDisposed
            && !tab.IsStartPage
            && tab.InitializationGeneration == initializationGeneration
            && (!requireActive || CanContinueRequiredTabInitialization(tab));
    }

    private bool CanContinueRequiredTabInitialization(BrowserTab tab)
    {
        return ShouldContinueRequiredTabInitialization(
            isActive: IsTabVisibleInAnyPane(tab),
            isWindowMinimized,
            isBackgroundProtected: IsBackgroundProtected(tab),
            protectedResidentRank: GetProtectedResidentRank(tab));
    }

    internal static bool ShouldContinueRequiredTabInitialization(
        bool isActive,
        bool isWindowMinimized,
        bool isBackgroundProtected,
        int protectedResidentRank)
    {
        return isActive && !isWindowMinimized
            || isBackgroundProtected
            || protectedResidentRank is >= 0 and < TabLifecyclePolicy.ProtectedResidentTabCount;
    }

    private async Task<bool> ClearInitializationWhenCompleteAsync(
        BrowserTab tab,
        Task<bool> initialization)
    {
        try
        {
            return await initialization;
        }
        finally
        {
            if (ReferenceEquals(tab.InitializationTask, initialization))
            {
                tab.InitializationTask = null;
                if (tab.Core is null) tab.IsInitializing = false;
            }
            ReleaseBrowserEnvironmentIfIdle();
            if (activeTab == tab
                && tab.IsDiscarded
                && !tab.IsClosed
                && !isClosing
                && !tab.IsInitializing
                && tab.InitializationTask is null)
            {
                RunUiTask(() => RestoreDiscardedTabAsync(tab, focusPage: true), "Could not restore the active tab");
            }
        }
    }

    private async Task<bool> InitializeTabCoreAsync(
        BrowserTab tab,
        WebView2 initializingView,
        CoreWebView2Environment initializingEnvironment,
        bool requireActive,
        long initializationGeneration)
    {
        try
        {
            var useDarkTheme = WebsiteThemePolicy.ShouldApplyAutoDark(
                darkModeEnabled,
                tab.Url,
                state.DarkModeExcludedHosts);
            initializingView.DefaultBackgroundColor = WebsiteThemePolicy.GetLoadingBackgroundColor(useDarkTheme);
            var controllerOptions = initializingEnvironment.CreateCoreWebView2ControllerOptions();
            var profileOptions = GetControllerProfileSnapshotForTesting(isPrivateMode);
            controllerOptions.IsInPrivateModeEnabled = profileOptions.IsInPrivateModeEnabled;
            if (profileOptions.ProfileName is not null) controllerOptions.ProfileName = profileOptions.ProfileName;
            await initializingView.EnsureCoreWebView2Async(initializingEnvironment, controllerOptions);
            if (!IsTabInitializationCurrent(tab, initializationGeneration, requireActive)
                || tab.View != initializingView
                || environment != initializingEnvironment)
            {
                ReleaseAbandonedInitialization(tab, initializingView, requireActive);
                return false;
            }

            await ConfigureWebView(tab);
            if (!IsTabInitializationCurrent(tab, initializationGeneration, requireActive)
                || tab.View != initializingView
                || environment != initializingEnvironment)
            {
                ReleaseAbandonedInitialization(tab, initializingView, requireActive);
                return false;
            }
            tab.IsInitializing = false;
            tab.IsDiscarded = false;
            tab.StatusText = "Ready";
            tab.Overlay.Visible = false;
            if (tab.StartPageView is not null) tab.StartPageView.Visible = false;
            initializingView.Visible = IsTabForegroundForLifecycle(tab);
            UpdateTabHeader(tab);
            if (multiViewState.IsMultiViewActive && multiViewState.ContainsTab(tab.Id))
            {
                EnsureTabHostLayout(tab);
            }
            UpdateNavigationChrome();
            return true;
        }
        catch (Exception error)
        {
            if (isClosing || tab.IsClosed || tab.View != initializingView) return false;
            tab.StatusText = "Tab failed";
            stateStore.Log("Tab initialization failed", error);
            ReplaceTabView(tab);
            ShowTabOverlay(
                tab,
                "This tab could not start",
                error.Message,
                "Try again",
                () => RecreateTabAsync(tab));
            UpdateTabHeader(tab);
            UpdateNavigationChrome();
            return false;
        }
    }

    private void PreserveCanceledInitializationAsDiscarded(
        BrowserTab tab,
        long expectedNavigationRequestId)
    {
        if (isClosing
            || tab.IsClosed
            || tab.NavigationRequestId != expectedNavigationRequestId
            || tab.IsStartPage
            || IsBackgroundProtected(tab)
            || (!tab.IsDiscarded && tab.RetryAction is not null)
            || (tab.IsDiscarded && tab.StatusText == "Unloaded to save memory"))
        {
            return;
        }

        if (tab.View is not null) ReplaceTabView(tab);
        MarkTabDiscarded(tab, "Waiting in the background");
        if (activeTab == tab && !isClosing && !tab.IsClosed)
        {
            RunUiTask(() => RestoreDiscardedTabAsync(tab, focusPage: true), "Could not restore the active tab");
        }
    }

    private void ReleaseAbandonedInitialization(
        BrowserTab tab,
        WebView2 initializingView,
        bool retainDiscardedShell)
    {
        if (tab.IsClosed || tab.View != initializingView) return;

        var hasRecoveryAction = !tab.IsDiscarded && tab.RetryAction is not null;
        ReplaceTabView(tab);
        if (retainDiscardedShell
            && !hasRecoveryAction
            && !tab.IsStartPage
            && !tab.IsClosed)
        {
            MarkTabDiscarded(tab, "Waiting in the background");
        }
    }

    private async Task EnsureUrlScopedScriptsRegisteredIfNeededAsync(BrowserTab tab, string? url)
    {
        if (isClosing || tab.IsClosed || tab.Core is not { } core)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(url)
            && tab.LastScopedScriptRegistrationUrl is { } lastUrl
            && string.Equals(lastUrl, url, StringComparison.Ordinal)
            && tab.LastScopedScriptAdBlockGeneration == tab.AdBlockScriptGeneration)
        {
            return;
        }

        await EnsureUrlScopedScriptsRegisteredAsync(tab, url);
        if (!isClosing && !tab.IsClosed && ReferenceEquals(tab.Core, core))
        {
            tab.LastScopedScriptRegistrationUrl = url;
            tab.LastScopedScriptAdBlockGeneration = tab.AdBlockScriptGeneration;
        }
    }

    private void SyncWebsiteThemeBeforeNavigation(BrowserTab tab, string? targetUrl)
    {
        if (isClosing || tab.IsClosed || tab.IsStartPage || tab.View is null) return;

        var url = string.IsNullOrWhiteSpace(targetUrl) ? tab.Url : targetUrl;
        var applyAutoDark = WebsiteThemePolicy.ShouldApplyAutoDark(
            darkModeEnabled,
            url,
            state.DarkModeExcludedHosts);
        var targetBg = WebsiteThemePolicy.GetLoadingBackgroundColor(applyAutoDark);
        try
        {
            if (tab.View.DefaultBackgroundColor != targetBg)
            {
                tab.View.DefaultBackgroundColor = targetBg;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or COMException or ObjectDisposedException)
        {
        }

        var currentGeneration = Interlocked.Read(ref websiteThemeGeneration);
        if (tab.Core is not null
            && (tab.AppliedWebsiteThemeGeneration != currentGeneration
                || tab.AppliedAutoDarkOverride != applyAutoDark
                || tab.AppliedPreferredColorSchemeDark != darkModeEnabled))
        {
            RunUiTask(
                () => ApplyWebsiteThemeIfChangedAsync(tab, url),
                "Could not update the website color preference");
        }
    }

    private async Task ApplyWebsiteThemeIfChangedAsync(BrowserTab tab, string? targetUrl = null)
    {
        if (isClosing || tab.IsClosed || tab.Core is not { } core) return;

        var useDarkTheme = darkModeEnabled;
        var currentGeneration = Interlocked.Read(ref websiteThemeGeneration);
        var url = targetUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = tab.Url;
            try
            {
                if (!string.IsNullOrWhiteSpace(core.Source)) url = core.Source;
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        var applyAutoDark = WebsiteThemePolicy.ShouldApplyAutoDark(
            useDarkTheme,
            url,
            state.DarkModeExcludedHosts);

        if (tab.View is { IsDisposed: false } view)
        {
            var targetBg = WebsiteThemePolicy.GetLoadingBackgroundColor(applyAutoDark);
            try
            {
                if (view.DefaultBackgroundColor != targetBg)
                {
                    view.DefaultBackgroundColor = targetBg;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or COMException or ObjectDisposedException)
            {
            }
        }

        if (tab.AppliedWebsiteThemeGeneration == currentGeneration
            && tab.AppliedAutoDarkOverride == applyAutoDark
            && tab.AppliedPreferredColorSchemeDark == useDarkTheme)
        {
            return;
        }

        try
        {
            if (tab.AppliedPreferredColorSchemeDark != useDarkTheme)
            {
                WebsiteThemePolicy.ApplyPreferredColorScheme(core, useDarkTheme);
                tab.AppliedPreferredColorSchemeDark = useDarkTheme;
            }
            if (tab.AppliedAutoDarkOverride != applyAutoDark
                || tab.AppliedWebsiteThemeGeneration != currentGeneration)
            {
                await WebsiteThemePolicy.ApplyAutoDarkModeOverrideAsync(core, applyAutoDark);
                if (!isClosing && !tab.IsClosed && ReferenceEquals(tab.Core, core))
                {
                    tab.AppliedAutoDarkOverride = applyAutoDark;
                    tab.AppliedWebsiteThemeGeneration = currentGeneration;
                }
            }
        }
        catch (Exception error)
        {
            stateStore.Log("Could not apply the automatic website color policy", error);
        }
    }

    private async Task ConfigureWebView(BrowserTab tab)
    {
        var view = tab.View ?? throw new InvalidOperationException("The tab view was not created.");
        var core = view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = true;
        core.Settings.IsPasswordAutosaveEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = true;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        bool IsCurrentCore() => !tab.IsClosed && ReferenceEquals(tab.Core, core);
        ApplySitePreferences(tab, persist: false);
        core.IsMuted = tab.IsMuted;
        tab.IsAudible = core.IsDocumentPlayingAudio;
        tab.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        QueueManagedExtensionReconciliation(core.Profile);
        applyingZoomPreference = true;
        try { view.ZoomFactor = tab.ZoomFactor; }
        finally { applyingZoomPreference = false; }
        TrackCoreEvent<object>(
            tab,
            handler => view.ZoomFactorChanged += handler,
            handler => view.ZoomFactorChanged -= handler,
            (_, _) =>
            {
                if (isClosing || IsDisposed || !IsCurrentCore()) return;
                try
                {
                    tab.ZoomFactor = Math.Clamp(view.ZoomFactor, BrowserStateStore.MinimumSiteZoom, BrowserStateStore.MaximumSiteZoom);
                    if (!applyingZoomPreference) SaveSiteZoomPreference(tab);
                }
                catch (Exception error) when (
                    error is InvalidOperationException
                        or COMException
                        or ObjectDisposedException)
                {
                    stateStore.Log("Could not read tab zoom factor", error);
                }
            });
        // None avoids WebView2's built-in tracking prevention from partitioning
        // IndexedDB, WebCrypto, and WebSockets across communication domains
        // (such as Messenger / Facebook). The native shield continues to block
        // its audited network and cosmetic rules independently.
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None;
        await ApplyWebsiteThemeIfChangedAsync(tab, tab.Url);
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                BrowserPerformance.SiteResponsivenessDocumentScript);
        }
        catch (Exception error)
        {
            stateStore.Log("Could not install the site responsiveness script", error);
        }

        if (adBlockEnabled && !tab.DeferResourceFilteringUntilPopupAttached)
        {
            await InstallAdBlockFilteringAsync(tab);
        }

        if (reduceWebsiteMotionEnabled) await ApplyMotionPolicyAsync(tab);

        if (adBlockEnabled)
        {
            if (!adBlocker.IsReady && !adBlockerLoadStarted)
            {
                adBlockerLoadStarted = true;
                _ = adBlocker.LoadAsync();
            }
            try { await InstallAdBlockPageScriptsAsync(tab); }
            catch (Exception error) { stateStore.Log("Could not install the adblock page shield", error); }
        }

        if (!isPrivateMode)
        {
            TrackCoreEvent<CoreWebView2WebMessageReceivedEventArgs>(
                tab,
                handler => core.WebMessageReceived += handler,
                handler => core.WebMessageReceived -= handler,
                (_, e) =>
                {
                    if (IsCurrentCore()) OnChromeStoreWebMessage(tab, e);
                });
        }

        await EnsureUrlScopedScriptsRegisteredIfNeededAsync(tab, tab.Url);

        TrackCoreEvent<CoreWebView2NavigationStartingEventArgs>(
            tab,
            handler => core.NavigationStarting += handler,
            handler => core.NavigationStarting -= handler,
            (_, e) =>
            {
                if (!IsCurrentCore()) return;
                OnNavigationStarting(tab, e);
                if (!e.Cancel) SyncWebsiteThemeBeforeNavigation(tab, e.Uri);
            });
        TrackCoreEvent<CoreWebView2DOMContentLoadedEventArgs>(
            tab,
            handler => core.DOMContentLoaded += handler,
            handler => core.DOMContentLoaded -= handler,
            (_, _) => { if (IsCurrentCore()) OnDomContentLoaded(tab); });
        TrackCoreEvent<CoreWebView2FrameCreatedEventArgs>(
            tab,
            handler => core.FrameCreated += handler,
            handler => core.FrameCreated -= handler,
            (_, e) => { if (IsCurrentCore()) OnFrameCreated(tab, e.Frame); });
        TrackCoreEvent<CoreWebView2NavigationCompletedEventArgs>(
            tab,
            handler => core.NavigationCompleted += handler,
            handler => core.NavigationCompleted -= handler,
            (_, e) =>
            {
                if (!IsCurrentCore()) return;
                var isSuccess = e.IsSuccess;
                var webErrorStatus = e.WebErrorStatus;
                RunUiTask(
                    () => OnNavigationCompletedAsync(tab, core, isSuccess, webErrorStatus),
                    "Could not finish page navigation");
            });
        TrackCoreEvent<CoreWebView2SourceChangedEventArgs>(
            tab,
            handler => core.SourceChanged += handler,
            handler => core.SourceChanged -= handler,
            (_, e) => { if (IsCurrentCore()) OnSourceChanged(tab, e); });
        TrackCoreEvent<object>(
            tab,
            handler => core.DocumentTitleChanged += handler,
            handler => core.DocumentTitleChanged -= handler,
            (_, _) => { if (IsCurrentCore()) OnDocumentTitleChanged(tab); });
        TrackCoreEvent<object>(
            tab,
            handler => core.ContainsFullScreenElementChanged += handler,
            handler => core.ContainsFullScreenElementChanged -= handler,
            (_, _) =>
            {
                if (IsCurrentCore()) OnContainsFullScreenElementChanged(tab, core);
            });
        TrackCoreEvent<object>(
            tab,
            handler => core.HistoryChanged += handler,
            handler => core.HistoryChanged -= handler,
            (_, _) =>
            {
                if (IsCurrentCore() && activeTab == tab) UpdateNavigationChrome();
            });
        TrackCoreEvent<CoreWebView2NewWindowRequestedEventArgs>(
            tab,
            handler => core.NewWindowRequested += handler,
            handler => core.NewWindowRequested -= handler,
            (_, e) => { if (IsCurrentCore()) OnNewWindowRequested(tab, e); });
        TrackCoreEvent<CoreWebView2PermissionRequestedEventArgs>(
            tab,
            handler => core.PermissionRequested += handler,
            handler => core.PermissionRequested -= handler,
            (_, e) => { if (IsCurrentCore()) OnPermissionRequested(tab, e); });
        TrackCoreEvent<CoreWebView2ContextMenuRequestedEventArgs>(
            tab,
            handler => core.ContextMenuRequested += handler,
            handler => core.ContextMenuRequested -= handler,
            (_, e) => { if (IsCurrentCore()) OnContextMenuRequested(tab, e); });
        TrackCoreEvent<object>(
            tab,
            handler => core.WindowCloseRequested += handler,
            handler => core.WindowCloseRequested -= handler,
            (_, _) =>
            {
                if (!IsCurrentCore()) return;
                PostUi(() =>
                {
                    if (!tab.IsClosed && IsCurrentCore())
                    {
                        if (tab.CanScriptClose) CloseTab(tab);
                        else ShowTransientStatus("This page cannot close a user-created tab");
                    }
                });
            });
        TrackCoreEvent<CoreWebView2ProcessFailedEventArgs>(
            tab,
            handler => core.ProcessFailed += handler,
            handler => core.ProcessFailed -= handler,
            (_, e) =>
            {
                if (!IsCurrentCore() || isClosing || !IsHandleCreated) return;
                CoreWebView2ProcessFailedKind failedKind;
                try { failedKind = e.ProcessFailedKind; }
                catch (Exception error) when (error is InvalidOperationException or COMException)
                {
                    stateStore.Log("Could not inspect the failed browser process", error);
                    return;
                }

                // WebView2 may still be unwinding ProcessFailed. Replacing and
                // disposing its controller inside that callback is re-entrant
                // native teardown and can raise STATUS_BREAKPOINT. Snapshot the
                // enum only, then recover on the next UI turn.
                PostUi(() =>
                {
                    if (IsCurrentCore()) OnProcessFailed(tab, failedKind);
                });
            });
        TrackCoreEvent<CoreWebView2DownloadStartingEventArgs>(
            tab,
            handler => core.DownloadStarting += handler,
            handler => core.DownloadStarting -= handler,
            (_, e) => { if (IsCurrentCore()) OnDownloadStarting(tab, e); });
        TrackCoreEvent<object>(
            tab,
            handler => core.StatusBarTextChanged += handler,
            handler => core.StatusBarTextChanged -= handler,
            (_, _) =>
            {
                if (!IsCurrentCore()) return;
                try
                {
                    // StatusBarTextChanged can fire for every link crossed by the
                    // pointer. Keep only the newest bounded raw value here and do
                    // URI parsing/sanitization once when the 100 ms UI timer drains.
                    var pendingStatus = TextSafety.Truncate(
                        core.StatusBarText ?? string.Empty,
                        MaximumPendingHoverStatusCharacters);
                    if (pendingStatus.Equals(tab.LastHoverStatusRaw, StringComparison.Ordinal)) return;
                    tab.LastHoverStatusRaw = pendingStatus;
                    tab.PendingHoverStatus = pendingStatus;
                    tab.HasPendingHoverStatus = true;
                }
                catch (Exception error) when (error is InvalidOperationException or COMException)
                {
                    tab.LastHoverStatusRaw = string.Empty;
                    tab.PendingHoverStatus = null;
                    tab.HasPendingHoverStatus = false;
                    tab.HoverStatus = string.Empty;
                    stateStore.Log("Could not read the page link status", error);
                }
                if (activeTab == tab && !statusUiTimer.Enabled) statusUiTimer.Start();
            });
        TrackCoreEvent<object>(
            tab,
            handler => core.IsDocumentPlayingAudioChanged += handler,
            handler => core.IsDocumentPlayingAudioChanged -= handler,
            (_, _) =>
            {
                if (isClosing || IsDisposed || !IsCurrentCore()) return;
                try
                {
                    tab.IsAudible = core.IsDocumentPlayingAudio;
                }
                catch (Exception error) when (
                    error is InvalidOperationException
                        or COMException
                        or ObjectDisposedException)
                {
                    stateStore.Log("Could not read tab audio state", error);
                    return;
                }
                UpdateTabHeader(tab);
                if (tab.IsAudible) tab.ConsecutiveSuspendFailures = 0;
                ApplyLiveTabMemoryTarget(tab, IsTabForegroundForLifecycle(tab));
                RefreshMemorySweepTimer();
                if (!tab.IsAudible && !IsTabForegroundForLifecycle(tab))
                {
                    QueueBackgroundTabReduction(tab);
                }
            });
        TrackCoreEvent<object>(
            tab,
            handler => core.FaviconChanged += handler,
            handler => core.FaviconChanged -= handler,
            (_, _) =>
            {
                if (!IsCurrentCore() || tab.IsStartPage) return;
                var docGeneration = tab.DocumentNavigationGeneration;
                RunUiTask(() => RefreshTabFaviconAsync(tab, core, docGeneration), "Could not load site icon");
            });
        TrackCoreEvent<CoreWebView2LaunchingExternalUriSchemeEventArgs>(
            tab,
            handler => core.LaunchingExternalUriScheme += handler,
            handler => core.LaunchingExternalUriScheme -= handler,
            (_, e) =>
            {
                if (IsCurrentCore()) OnLaunchingExternalUriScheme(tab, e);
            });
        ApplyLiveTabMemoryTarget(tab, IsTabForegroundForLifecycle(tab));
    }

    private static void TrackCoreEvent<TEventArgs>(
        BrowserTab tab,
        Action<EventHandler<TEventArgs>> subscribe,
        Action<EventHandler<TEventArgs>> unsubscribe,
        EventHandler<TEventArgs> handler)
    {
        subscribe(handler);
        tab.TrackCoreEventHandler(() => unsubscribe(handler));
    }

}
