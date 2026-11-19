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
    private void RefreshMemorySweepTimer()
    {
        if (isClosing) return;
        var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
        if (mode == TabLifecycleMode.Off)
        {
            memoryTimer.Stop();
            return;
        }
        var canWatchPendingInitialization = tabs.Any(tab =>
            !tab.IsClosed && tab.IsInitializing);
        if (environment is null && !canWatchPendingInitialization)
        {
            memoryTimer.Stop();
            return;
        }

        var hasCandidates = tabs.Any(tab =>
            !tab.IsClosed
            && (tab.Core is not null || tab.IsInitializing)
            && !tab.IsAudible
            && !IsBackgroundProtected(tab)
            && (!IsProtectedResidentTab(tab)
                || (mode == TabLifecycleMode.Ultra
                    && !tab.IsSuspended
                    && !tab.IsLoading
                    && !tab.IsInitializing
                    && tab.ConsecutiveSuspendFailures
                        < TabLifecyclePolicy.SuspendFailureFallbackThreshold))
            && (!IsTabVisibleInAnyPane(tab) || isWindowMinimized)
            && tab.ActiveDownloads == 0);
        if (!hasCandidates)
        {
            var hasLiveWebViews = environment is not null && tabs.Any(tab => !tab.IsClosed && tab.Core is not null);
            if (!hasLiveWebViews)
            {
                memoryTimer.Stop();
                return;
            }
            if (memoryTimer.Interval != IdleMemoryMaintenanceIntervalMs)
            {
                memoryTimer.Interval = IdleMemoryMaintenanceIntervalMs;
            }
            if (!memoryTimer.Enabled) memoryTimer.Start();
            return;
        }

        nextLifecycleScanAt = DateTimeOffset.MinValue;
        var scanInterval = mode == TabLifecycleMode.Standard
            ? StandardMemoryScanIntervalMs
            : MemoryScanIntervalMs;
        if (memoryTimer.Interval != scanInterval) memoryTimer.Interval = scanInterval;
        if (!memoryTimer.Enabled) memoryTimer.Start();
    }

    private async Task<bool> RestoreOpenTabsFromStateAsync()
    {
        if (state.OpenTabs.Count == 0) return false;

        var targetActiveIndex = Math.Min(
            Math.Max(state.ActiveTabIndex.GetValueOrDefault(0), 0),
            state.OpenTabs.Count - 1);
        var maxRestoreCount = Math.Min(state.OpenTabs.Count, MaximumTabs);
        var visibleTab = activeTab;
        var restoredCount = 0;

        restoringSession = true;
        tabStrip.SuspendLayout();
        pageHost.SuspendLayout();
        try
        {
            for (var index = 0; index < maxRestoreCount; index++)
            {
                if (isClosing) return restoredCount > 0;

                var restoredTab = await OpenNewTabAsync(
                    state.OpenTabs[index],
                    activate: false,
                    isPinned: index < state.PinnedOpenTabCount);
                if (restoredTab is null) continue;

                restoredCount++;
                if (index == targetActiveIndex)
                {
                    visibleTab = restoredTab;
                }
            }
        }
        finally
        {
            restoringSession = false;
            pageHost.ResumeLayout(performLayout: true);
            tabStrip.ResumeLayout(performLayout: true);
            RefreshNativeStartPages(refreshLinks: true);
            ResizeTabHeaders();
            RefreshMemorySweepTimer();
        }

        if (tabs.Count > 0)
        {
            visibleTab ??= tabs[0];
            ActivateTab(visibleTab, focusPage: false, restoreDiscarded: false);
            mruTabs.Reset(new[] { visibleTab }.Concat(tabs.Where(item => item != visibleTab)));
            var restoredMultiView = false;
            if (!isPrivateMode && state.MultiViewLayout is { PaneTabIndices.Length: >= 2 } savedMultiView)
            {
                restoredMultiView = await RestoreSavedMultiViewLayoutAsync(savedMultiView);
            }
            if (!restoredMultiView && !visibleTab.IsStartPage && visibleTab.IsDiscarded)
            {
                await RestoreDiscardedTabAsync(visibleTab, focusPage: false);
            }
        }

        return restoredCount > 0;
    }

    private void QueueBackgroundTabReduction(BrowserTab tab)
    {
        var requestedMode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
        if (isClosing
            || restoringSession
            || tab.IsClosed
            || IsTabForegroundForLifecycle(tab)
            || requestedMode == TabLifecycleMode.Off)
        {
            return;
        }

        if (requestedMode == TabLifecycleMode.Ultra && tab.IsStartPage)
        {
            ReleaseNativeStartPage(tab);
            return;
        }

        ApplyLiveTabMemoryTarget(tab, foreground: false);
        RefreshMemorySweepTimer();
        var reductionGeneration = ++tab.BackgroundReductionGeneration;
        RunUiTask(
            async () =>
            {
                await Task.Delay(2500);
                if (isClosing || tab.IsClosed || tab.BackgroundReductionGeneration != reductionGeneration || IsTabForegroundForLifecycle(tab)) return;
                CompactBackgroundTabMemory(tab);

                var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
                if (mode != TabLifecycleMode.Ultra) return;
                await Task.Delay(2500);
                if (isClosing || tab.IsClosed || tab.BackgroundReductionGeneration != reductionGeneration || IsTabForegroundForLifecycle(tab)) return;
                var currentMode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
                if (currentMode != TabLifecycleMode.Ultra) return;
                var suspended = await TrySuspendTabAsync(
                    tab,
                    includeActiveTab: isWindowMinimized && IsTabVisibleInAnyPane(tab),
                    expectedMode: currentMode);
                if (!suspended) TryDiscardTabAfterFailedSuspend(tab);
            },
            "Could not rest a background tab");
    }

    private void CompactBackgroundTabMemory(BrowserTab tab)
    {
        if (isClosing
            || tab.IsClosed
            || tab.IsLoading
            || tab.IsInitializing
            || IsBackgroundProtected(tab)
            || IsTabForegroundForLifecycle(tab))
        {
            return;
        }

        if (isWindowMinimized
            || (systemMemory.IsValid && systemMemory.MemoryLoadPercent >= 75)
            || tabs.Any(item => !item.IsClosed && item.IsSuspended))
        {
            SystemResourceInfo.TrimCurrentProcessWorkingSet();
        }
    }

    private bool IsOrdinaryResidentCandidate(BrowserTab tab)
    {
        return !tab.IsClosed
            && !tab.IsStartPage
            && tabs.Contains(tab)
            && (tab.Core is not null || tab.IsInitializing);
    }

    private int GetProtectedResidentRank(BrowserTab target)
    {
        var rank = 0;
        if (activeTab is { } active && IsOrdinaryResidentCandidate(active))
        {
            if (ReferenceEquals(active, target)) return rank;
            rank++;
        }

        foreach (var candidate in mruTabs.Order)
        {
            if (ReferenceEquals(candidate, activeTab)
                || !IsOrdinaryResidentCandidate(candidate))
            {
                continue;
            }
            if (ReferenceEquals(candidate, target))
            {
                return rank < TabLifecyclePolicy.ProtectedResidentTabCount ? rank : -1;
            }
            rank++;
            if (rank >= TabLifecyclePolicy.ProtectedResidentTabCount) break;
        }
        return -1;
    }

    private bool IsProtectedResidentTab(BrowserTab tab)
    {
        return GetProtectedResidentRank(tab) >= 0;
    }

    private void ApplyLiveTabMemoryTarget(BrowserTab tab, bool foreground)
    {
        if (!tab.SupportsMemoryUsageTarget
            || tab.IsSuspended
            || tab.Core is not { } core)
        {
            return;
        }

        var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
        var requiresBackgroundExecution = IsBackgroundProtected(tab)
            || tab.ActiveDownloads > 0
            || tab.IsLoading
            || tab.IsInitializing
            || IsTabAudible(tab);
        var retainedSuspendFailures = IsProtectedResidentTab(tab)
            ? tab.ConsecutiveSuspendFailures
            : 0;
        var target = TabLifecyclePolicy.ShouldUseLowMemoryTarget(
                mode,
                foreground,
                requiresBackgroundExecution,
                retainedSuspendFailures)
            ? CoreWebView2MemoryUsageTargetLevel.Low
            : CoreWebView2MemoryUsageTargetLevel.Normal;
        SetTabMemoryTarget(tab, target);
    }

    private void RefreshLiveTabMemoryTargets()
    {
        foreach (var tab in tabs.Where(item => !item.IsClosed))
        {
            ApplyLiveTabMemoryTarget(tab, IsTabForegroundForLifecycle(tab));
        }
    }

    private void SetTabMemoryTarget(
        BrowserTab tab,
        CoreWebView2MemoryUsageTargetLevel target)
    {
        if (!tab.SupportsMemoryUsageTarget
            || tab.IsSuspended
            || tab.Core is not { } core
            || tab.MemoryUsageTargetLevel == target)
        {
            return;
        }

        try
        {
            core.MemoryUsageTargetLevel = target;
            tab.MemoryUsageTargetLevel = target;
        }
        catch (Exception error)
        {
            tab.SupportsMemoryUsageTarget = false;
            stateStore.Log("Could not set the live tab memory target", error);
        }
    }

    private bool TryDiscardTabAfterFailedSuspend(BrowserTab tab)
    {
        var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
        // A duplicate reduction request can observe IsSuspending and receive a
        // false result while the first TrySuspendAsync COM call is still in
        // flight. Never dispose that WebView until its suspension continuation
        // has finished validating the tab/core generation.
        if (mode != TabLifecycleMode.Ultra
            || tab.IsClosed
            || tab.IsSuspending)
        {
            return false;
        }

        var nowUtc = DateTime.UtcNow;
        var snapshot = new TabLifecycleSnapshot(
            Id: 0,
            tab.LastActiveUtc,
            IsActive: tab == activeTab && !isWindowMinimized,
            tab.IsClosed,
            tab.IsLoading,
            tab.IsInitializing,
            IsTabAudible(tab),
            HasActiveDownload: tab.ActiveDownloads > 0,
            HasCore: tab.Core is not null,
            tab.IsSuspended,
            IsBackgroundProtected(tab),
            ProtectedResidentRank: GetProtectedResidentRank(tab),
            IsVisibleInPane: !isWindowMinimized && IsTabVisibleInAnyPane(tab));
        var residentCoreCount = tabs.Count(item => !item.IsClosed && item.Core is not null);
        if (!TabLifecyclePolicy.ShouldDiscardAfterFailedSuspend(
                snapshot,
                mode,
                nowUtc,
                systemMemory.MemoryLoadPercent,
                systemMemory.IsValid,
                systemMemory.TotalPhysicalBytes,
                systemMemory.LogicalProcessorCount,
                residentCoreCount,
                tab.ConsecutiveSuspendFailures))
        {
            return false;
        }

        if (TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled) != TabLifecycleMode.Ultra)
        {
            return false;
        }
        ReplaceTabView(tab);
        MarkTabDiscarded(tab, "Ultra-light mode released a page that could not sleep");
        return true;
    }

    private async Task SleepInactiveTabsAsync()
    {
        var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
        if (isClosing
            || mode == TabLifecycleMode.Off
            || memorySweepRunning
            || (environment is null && !tabs.Any(tab => !tab.IsClosed && tab.IsInitializing)))
        {
            return;
        }
        CheckApplicationInactivity();
        if (DateTimeOffset.UtcNow < nextLifecycleScanAt) return;
        if (!tabs.Any(item =>
            (item.Core is not null || item.IsInitializing)
            && !item.IsClosed
            && !item.IsAudible
            && !IsBackgroundProtected(item)
            && (!IsProtectedResidentTab(item)
                || (mode == TabLifecycleMode.Ultra
                    && !item.IsSuspended
                    && !item.IsLoading
                    && !item.IsInitializing
                    && item.ConsecutiveSuspendFailures
                        < TabLifecyclePolicy.SuspendFailureFallbackThreshold))
            && (!IsTabVisibleInAnyPane(item) || isWindowMinimized)
            && item.ActiveDownloads == 0
            && !item.IsDiscarded))
        {
            TrimAllProcessMemory(allowManagedGc: false);
            RefreshMemorySweepTimer();
            nextLifecycleScanAt = DateTimeOffset.MinValue;
            return;
        }

        memorySweepRunning = true;
        nextLifecycleScanAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(MemoryScanCooldownSeconds);

        try
        {
            systemMemory = SystemResourceInfo.CaptureMemory();
            var nowUtc = DateTime.UtcNow;
            var snapshotTabs = tabs.ToArray();
            var snapshots = snapshotTabs
                .Select((tab, index) => new TabLifecycleSnapshot(
                    index,
                    tab.LastActiveUtc,
                    tab == activeTab && !isWindowMinimized,
                    tab.IsClosed,
                    tab.IsLoading,
                    tab.IsInitializing,
                    IsTabAudible(tab),
                    tab.ActiveDownloads > 0,
                    tab.Core is not null,
                    tab.IsSuspended,
                    IsBackgroundProtected(tab),
                    GetProtectedResidentRank(tab),
                    !isWindowMinimized && IsTabVisibleInAnyPane(tab)))
                .ToArray();
            var decisions = TabLifecyclePolicy.PlanSweep(
                snapshots,
                mode,
                nowUtc,
                systemMemory.MemoryLoadPercent,
                systemMemory.IsValid,
                systemMemory.TotalPhysicalBytes,
                systemMemory.LogicalProcessorCount,
                mode == TabLifecycleMode.Standard
                    ? Math.Min(MaximumStandardUnloadsPerSweep, snapshotTabs.Length)
                    : snapshotTabs.Length);

            var reducedAnyTab = false;
            foreach (var decision in decisions)
            {
                if (decision.Id < 0 || decision.Id >= snapshotTabs.Length) continue;
                var tab = snapshotTabs[decision.Id];
                if (isClosing || tab.IsClosed) return;
                var currentMode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
                if (currentMode != mode) break;
                try
                {
                    if (decision.Action == TabLifecycleAction.Suspend)
                    {
                        var suspended = await TrySuspendTabAsync(
                            tab,
                            includeActiveTab: isWindowMinimized && IsTabVisibleInAnyPane(tab),
                            expectedMode: mode);
                        if (suspended) reducedAnyTab = true;
                        else if (TryDiscardTabAfterFailedSuspend(tab)) reducedAnyTab = true;
                    }
                    else if (decision.Action == TabLifecycleAction.Discard
                        && currentMode is TabLifecycleMode.Standard or TabLifecycleMode.Ultra
                        && (currentMode == TabLifecycleMode.Standard
                            || tab.IsSuspended
                            || tab.IsLoading
                            || tab.IsInitializing)
                        && !tab.IsDiscarded
                        && (!IsTabVisibleInAnyPane(tab) || isWindowMinimized)
                        && !tab.IsAudible
                        && !IsBackgroundProtected(tab)
                        && !IsProtectedResidentTab(tab)
                        && tab.ActiveDownloads == 0)
                    {
                        ReplaceTabView(tab);
                        MarkTabDiscarded(
                            tab,
                            currentMode == TabLifecycleMode.Standard
                                ? "Memory saver released an older inactive page"
                                : "Ultra-light mode released its page process");
                        reducedAnyTab = true;
                    }
                }
                catch (Exception error)
                {
                    stateStore.Log("Could not reduce an inactive tab", error);
                }
            }
            UpdateStatus();
            TrimAllProcessMemory(allowManagedGc: reducedAnyTab);
        }
        finally
        {
            memorySweepRunning = false;
        }
    }

    private void TrimAllProcessMemory(bool includeActiveForeground = false, bool allowManagedGc = false)
    {
        if (isClosing) return;
        if (Interlocked.CompareExchange(ref processTrimInFlight, 1, 0) != 0) return;

        var trimCurrent = includeActiveForeground || isWindowMinimized;
        var hasSuspendedTabs = tabs.Any(item => !item.IsClosed && item.IsSuspended);
        var underMemoryPressure = systemMemory.IsValid && systemMemory.MemoryLoadPercent >= 75;
        var shouldTrimWorkingSets = trimCurrent || allowManagedGc || hasSuspendedTabs || underMemoryPressure;
        var throttleRenderersForEcoQoS = isWindowMinimized;
        var shouldCollectGc = false;
        if (allowManagedGc)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - lastManagedGcAtUtc >= TimeSpan.FromSeconds(60))
            {
                lastManagedGcAtUtc = now;
                shouldCollectGc = true;
            }
        }

        List<(int ProcessId, bool IsRenderer)>? childProcesses = null;
        if (environment is { } activeEnvironment)
        {
            try
            {
                var processInfos = activeEnvironment.GetProcessInfos();
                if (processInfos is { Count: > 0 })
                {
                    childProcesses = new List<(int ProcessId, bool IsRenderer)>(processInfos.Count);
                    foreach (var processInfo in processInfos)
                    {
                        if (!trimCurrent && processInfo.Kind == CoreWebView2ProcessKind.Gpu)
                        {
                            continue;
                        }
                        if (processInfo.ProcessId > 0)
                        {
                            childProcesses.Add((
                                processInfo.ProcessId,
                                processInfo.Kind == CoreWebView2ProcessKind.Renderer));
                        }
                    }
                }
            }
            catch (Exception error)
            {
                stateStore.Log("Could not inspect WebView2 child processes", error);
            }
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (shouldCollectGc)
                {
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                }
                if (trimCurrent && shouldTrimWorkingSets)
                {
                    SystemResourceInfo.TrimCurrentProcessWorkingSet();
                }
                if (childProcesses is not null)
                {
                    foreach (var (pid, isRenderer) in childProcesses)
                    {
                        if (isRenderer)
                        {
                            SystemResourceInfo.TrySetProcessEfficiencyMode(pid, throttleRenderersForEcoQoS);
                        }
                        if (shouldTrimWorkingSets)
                        {
                            SystemResourceInfo.TrimProcessWorkingSet(pid);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                stateStore.Log("Could not trim process memory", error);
            }
            finally
            {
                Volatile.Write(ref processTrimInFlight, 0);
            }
        });
    }

    private async Task<bool> TrySuspendTabAsync(
        BrowserTab tab,
        bool includeActiveTab = false,
        TabLifecycleMode? expectedMode = null)
    {
        if (isClosing
            || tab.IsClosed
            || tab.IsSuspended
            || tab.IsSuspending
            || tab.IsDiscarded
            || tab.IsLoading
            || tab.IsInitializing
            || IsBackgroundProtected(tab)
            || (IsProtectedResidentTab(tab)
                && tab.ConsecutiveSuspendFailures
                    >= TabLifecyclePolicy.SuspendFailureFallbackThreshold)
            || tab.ActiveDownloads > 0
            || (!includeActiveTab && IsTabVisibleInAnyPane(tab))
            || tab.View is null
            || tab.View.IsDisposed
            || tab.View.Disposing
            || tab.Core is null
            || IsTabAudible(tab))
        {
            return false;
        }

        var suspendingView = tab.View;
        var core = tab.Core;
        if (!tab.IsStartPage)
        {
            await CaptureTabScrollOffsetAsync(tab);
            if (isClosing
                || tab.IsClosed
                || tab.IsSuspended
                || tab.IsSuspending
                || tab.IsDiscarded
                || tab.IsLoading
                || tab.IsInitializing
                || IsBackgroundProtected(tab)
                || (IsProtectedResidentTab(tab)
                    && tab.ConsecutiveSuspendFailures
                        >= TabLifecyclePolicy.SuspendFailureFallbackThreshold)
                || tab.ActiveDownloads > 0
                || (!includeActiveTab && IsTabVisibleInAnyPane(tab))
                || !ReferenceEquals(tab.View, suspendingView)
                || suspendingView.IsDisposed
                || suspendingView.Disposing
                || !ReferenceEquals(tab.Core, core)
                || IsTabAudible(tab))
            {
                return false;
            }
        }
        // A page can leave the protected MRU set after using the Low target as
        // its failed-suspend fallback. Restore Normal before trying the separate
        // Ultra suspension strategy again.
        SetTabMemoryTarget(tab, CoreWebView2MemoryUsageTargetLevel.Normal);
        tab.IsSuspending = true;
        try
        {
            if (suspendingView.IsDisposed || suspendingView.Disposing) return false;
            try { suspendingView.Visible = false; }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) when (
                suspendingView.IsDisposed || suspendingView.Disposing)
            {
                return false;
            }
            bool suspended;
            try
            {
                suspended = await core.TrySuspendAsync();
            }
            catch (Exception error)
            {
                if (tab.View == suspendingView
                    && !suspendingView.IsDisposed
                    && !suspendingView.Disposing
                    && !isClosing
                    && !tab.IsClosed)
                {
                    var modeAfterFailure = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
                    var lifecycleChanged = modeAfterFailure == TabLifecycleMode.Off
                        || (expectedMode is not null && modeAfterFailure != expectedMode)
                        || IsBackgroundProtected(tab)
                        || tab.IsLoading
                        || tab.IsInitializing
                        || tab.IsAudible
                        || tab.ActiveDownloads > 0
                        || IsTabForegroundForLifecycle(tab);
                    if (lifecycleChanged)
                    {
                        tab.ConsecutiveSuspendFailures = 0;
                        if (IsTabForegroundForLifecycle(tab))
                        {
                            suspendingView.Visible = !tab.Overlay.Visible
                                && tab.StartPageView?.Visible != true;
                        }
                        ApplyLiveTabMemoryTarget(tab, IsTabForegroundForLifecycle(tab));
                    }
                    else
                    {
                        if (error is InvalidOperationException)
                        {
                            RecoverInvalidTabCore(tab, suspendingView);
                            return false;
                        }
                        tab.ConsecutiveSuspendFailures++;
                        ApplyLiveTabMemoryTarget(tab, IsTabForegroundForLifecycle(tab));
                    }
                    stateStore.Log("Could not suspend a background tab", error);
                }
                return false;
            }
            if (tab.View != suspendingView
                || suspendingView.IsDisposed
                || suspendingView.Disposing) return false;
            tab.IsSuspended = suspended;
            if (isClosing || tab.IsClosed) return false;
            var currentMode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
            if (currentMode == TabLifecycleMode.Off
                || (expectedMode is not null && currentMode != expectedMode)
                || IsBackgroundProtected(tab)
                || tab.IsLoading
                || tab.IsInitializing
                || tab.IsAudible
                || tab.ActiveDownloads > 0
                || IsTabForegroundForLifecycle(tab))
            {
                tab.ConsecutiveSuspendFailures = 0;
                ResumeTab(tab);
                if (IsTabForegroundForLifecycle(tab) && tab.View is not null)
                {
                    tab.View.Visible = !tab.Overlay.Visible;
                }
                ApplyLiveTabMemoryTarget(tab, IsTabForegroundForLifecycle(tab));
                return false;
            }
            if (!suspended)
            {
                tab.ConsecutiveSuspendFailures++;
                ApplyLiveTabMemoryTarget(tab, IsTabForegroundForLifecycle(tab));
                return false;
            }

            tab.ConsecutiveSuspendFailures = 0;
            tab.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            if (tab.IsSuspended)
            {
                UpdateTabHeader(tab);
                RefreshNativeStartPages();
            }
            return tab.IsSuspended;
        }
        finally
        {
            tab.IsSuspending = false;
        }
    }

    private void RecoverInvalidTabCore(BrowserTab tab, WebView2 expectedView)
    {
        if (isClosing
            || tab.IsClosed
            || tab.View != expectedView)
        {
            return;
        }

        ReplaceTabView(tab);
        tab.ConnectionState = ConnectionState.Failed;
        if (tab == activeTab && !isWindowMinimized)
        {
            tab.StatusText = "Page process stopped";
            ShowTabOverlay(
                tab,
                "This page stopped working",
                "The expired page process was released safely.",
                "Reload tab",
                () => RecreateTabAsync(tab));
        }
        else
        {
            MarkTabDiscarded(tab, "Released after its page process stopped");
        }
        UpdateTabHeader(tab);
        UpdateStatus();
    }

    private bool IsTabAudible(BrowserTab tab)
    {
        return tab.Core is not null && tab.IsAudible;
    }

    private static bool IsBackgroundProtected(BrowserTab tab)
    {
        var effectiveUrl = tab.Core?.Source ?? tab.Url;
        return tab.PendingMediaPermissionRefreshes > 0
            || tab.PendingMediaPermissionRequests > 0
            || CommunicationCompatibilityPolicy.IsCallSite(effectiveUrl)
            || (IsYouTubeWatchUrl(effectiveUrl)
                && (tab.IsLoading || tab.IsInitializing || tab.IsAudible))
            || CommunicationCompatibilityPolicy.ShouldProtectBackgroundTab(
                tab.KeepAwake,
                tab.MicrophoneAccessGranted,
                tab.CameraAccessGranted);
    }

    private static bool IsYouTubeWatchUrl(string? url)
    {
        return !string.IsNullOrEmpty(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && IsYouTubeHost(uri.Host)
            && (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.StartsWith("/shorts", StringComparison.OrdinalIgnoreCase));
    }

    private void ResumeTab(BrowserTab tab)
    {
        var expectedView = tab.View;
        var core = tab.Core;
        if (!tab.IsSuspended || core is null || expectedView is null) return;
        try
        {
            core.Resume();
            tab.IsSuspended = false;
            SetTabMemoryTarget(tab, CoreWebView2MemoryUsageTargetLevel.Normal);
            RunUiTask(
                () => ApplyWebsiteThemeAsync(tab),
                "Could not refresh the website color preference after waking a tab");
            UpdateTabHeader(tab);
            RefreshNativeStartPages();
        }
        catch (Exception error)
        {
            stateStore.Log("Could not resume a tab", error);
            RecoverInvalidTabCore(tab, expectedView);
        }
    }

    private async Task HandleWindowStateChangeAsync()
    {
        var minimized = WindowState == FormWindowState.Minimized;
        if (minimized == isWindowMinimized) return;
        isWindowMinimized = minimized;
        var transitionGeneration = ++windowStateGeneration;

        if (minimized)
        {
            CancelTabDrag();
            var minimizingTab = activeTab;
            if (minimizingTab?.View is not null) minimizingTab.View.Visible = false;
            if (minimizingTab?.StartPageView is not null)
            {
                minimizingTab.StartPageView.Visible = false;
            }
            if (minimizingTab is not null)
            {
                minimizingTab.LastActiveUtc = DateTime.UtcNow;
                minimizingTab.ConsecutiveSuspendFailures = 0;
                ApplyLiveTabMemoryTarget(minimizingTab, foreground: false);
            }
            if (TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled) == TabLifecycleMode.Ultra
                && minimizingTab is not null)
            {
                var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
                var suspended = await TrySuspendTabAsync(
                    minimizingTab,
                    includeActiveTab: true,
                    expectedMode: mode);
                if (transitionGeneration != windowStateGeneration || !isWindowMinimized || isClosing)
                {
                    return;
                }
                if (!suspended) TryDiscardTabAfterFailedSuspend(minimizingTab);
            }
            TrimAllProcessMemory(includeActiveForeground: true, allowManagedGc: true);
            RefreshMemorySweepTimer();
            return;
        }

        UpdateWindowControls();
        if (activeTab is not null)
        {
            var restoringTab = activeTab;
            restoringTab.Host.Visible = true;
            if (restoringTab.IsStartPage)
            {
                ShowNativeStartPage(restoringTab);
            }
            else if (restoringTab.IsDiscarded)
            {
                await RestoreDiscardedTabAsync(restoringTab, focusPage: false);
            }
            else
            {
                ResumeTab(restoringTab);
                if (restoringTab.View is not null)
                {
                    restoringTab.View.Visible = !restoringTab.Overlay.Visible
                        && restoringTab.StartPageView?.Visible != true;
                }
            }
            if (transitionGeneration != windowStateGeneration
                || isWindowMinimized
                || isClosing
                || activeTab != restoringTab)
            {
                if (!restoringTab.IsClosed)
                {
                    if (isWindowMinimized && restoringTab.View is not null) restoringTab.View.Visible = false;
                    ApplyLiveTabMemoryTarget(restoringTab, foreground: false);
                }
                RefreshMemorySweepTimer();
                return;
            }
            ApplyLiveTabMemoryTarget(restoringTab, foreground: true);
        }

        if (multiViewState.IsMultiViewActive && !isWindowMinimized)
        {
            ApplyMultiViewLayout();
            RefreshLiveTabMemoryTargets();
        }
        else if (!isWindowMinimized)
        {
            EnsureTabHostLayout(activeTab);
        }

        RefreshMemorySweepTimer();
    }

    private void SetResourceMode(TabLifecycleMode mode)
    {
        var normalizedMode = mode switch
        {
            TabLifecycleMode.Off => TabLifecycleMode.Off,
            TabLifecycleMode.Standard => TabLifecycleMode.Standard,
            TabLifecycleMode.Ultra => TabLifecycleMode.Ultra,
            _ => TabLifecycleMode.Standard
        };

        memorySaverEnabled = normalizedMode != TabLifecycleMode.Off;
        ultraLightEnabled = normalizedMode == TabLifecycleMode.Ultra;
        ResetSuspendFailureCounts();
        if (normalizedMode != TabLifecycleMode.Ultra)
        {
            foreach (var tab in tabs.Where(item => item.IsSuspended)) ResumeTab(tab);
        }
        RefreshLiveTabMemoryTargets();
        ReleaseInactiveNativeStartPagesForUltra();
        RefreshMemorySweepTimer();
        SavePreferences();
        RefreshNativeStartPages();
        ShowTransientStatus(normalizedMode switch
        {
            TabLifecycleMode.Ultra => "Ultra-light selected — 3 recent sites won't reload",
            TabLifecycleMode.Standard => "Memory saver selected — 3 recent sites stay ready",
            _ => "Resource saver off"
        });
    }

    private void ShowResourceModeMenu(Control? anchor = null)
    {
        var target = anchor ?? menuButton;
        resourceModeMenu.DropDown.Show(
            target,
            new Point(target.Width, target.Height),
            ToolStripDropDownDirection.BelowLeft);
    }

    private void ToggleMemorySaver()
    {
        memorySaverEnabled = !memorySaverEnabled;
        ResetSuspendFailureCounts();
        if (!memorySaverEnabled)
        {
            ultraLightEnabled = false;
            foreach (var tab in tabs.Where(item => item.IsSuspended)) ResumeTab(tab);
        }
        RefreshLiveTabMemoryTargets();
        RefreshMemorySweepTimer();
        SavePreferences();
        RefreshNativeStartPages();
        ShowTransientStatus(memorySaverEnabled ? "Memory saver on" : "Memory saver off");
    }

    private void ToggleUltraLightMode()
    {
        ultraLightEnabled = !ultraLightEnabled;
        if (ultraLightEnabled) memorySaverEnabled = true;
        else
        {
            foreach (var tab in tabs.Where(item => item.IsSuspended)) ResumeTab(tab);
        }
        ResetSuspendFailureCounts();
        RefreshLiveTabMemoryTargets();
        ReleaseInactiveNativeStartPagesForUltra();
        RefreshMemorySweepTimer();
        SavePreferences();
        RefreshNativeStartPages();
        ShowTransientStatus(ultraLightEnabled
            ? "Ultra-light mode on \u2014 very old inactive tabs may reload"
            : "Ultra-light mode off");
    }

    private void CycleResourceMode()
    {
        if (ultraLightEnabled)
        {
            ultraLightEnabled = false;
            memorySaverEnabled = false;
            foreach (var tab in tabs.Where(item => item.IsSuspended)) ResumeTab(tab);
        }
        else if (memorySaverEnabled)
        {
            ultraLightEnabled = true;
        }
        else
        {
            memorySaverEnabled = true;
        }

        ResetSuspendFailureCounts();
        RefreshLiveTabMemoryTargets();
        ReleaseInactiveNativeStartPagesForUltra();
        RefreshMemorySweepTimer();
        SavePreferences();
        RefreshNativeStartPages();
        var mode = ultraLightEnabled
            ? "Ultra-light mode"
            : memorySaverEnabled ? "Memory saver" : "Resource saver off";
        ShowTransientStatus($"{mode} selected");
    }

    private void ResetSuspendFailureCounts()
    {
        foreach (var tab in tabs) tab.ConsecutiveSuspendFailures = 0;
    }

    internal static void ApplyResourceModeDefaults(BrowserState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ResourceModeDefaultsVersion is not null) return;

        // One-time migration: standard Memory Saver is now the default.
        // Once saved, later manual resource-mode choices remain untouched.
        state.MemorySaverEnabled = true;
        state.UltraLightModeEnabled = false;
        state.ResourceModeDefaultsVersion = 1;
    }

}
