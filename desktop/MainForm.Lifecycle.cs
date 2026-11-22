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
            && (tab.Core is not null || tab.IsInitializing)
            && tabs.Contains(tab);
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
        for (var index = 0; index < tabs.Count; index++)
        {
            var tab = tabs[index];
            if (!tab.IsClosed)
            {
                ApplyLiveTabMemoryTarget(tab, IsTabForegroundForLifecycle(tab));
            }
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
            systemMemory = SystemResourceInfo.CaptureMemory();
            var allWindowsMinimized = Program.AreAllOpenWindowsMinimized(this);
            var hasSuspendedTabs = tabs.Any(item => !item.IsClosed && item.IsSuspended);
            var underMemoryPressure = systemMemory.IsValid && systemMemory.MemoryLoadPercent >= 75;
            var desiredEcoQoS = allWindowsMinimized ? 1 : 0;
            var ecoQoSChanged = Volatile.Read(ref lastAppliedRendererEcoQoS) != desiredEcoQoS;
            if (isWindowMinimized || hasSuspendedTabs || underMemoryPressure || ecoQoSChanged)
            {
                TrimAllProcessMemory(allowManagedGc: false);
            }
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
            var snapshots = new TabLifecycleSnapshot[snapshotTabs.Length];
            for (var index = 0; index < snapshotTabs.Length; index++)
            {
                var tab = snapshotTabs[index];
                snapshots[index] = new TabLifecycleSnapshot(
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
                    !isWindowMinimized && IsTabVisibleInAnyPane(tab));
            }
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
                if (isClosing) return;
                if (tab.IsClosed) continue;
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

    private static long globalEcoQoSGeneration;
    private static int lastAppliedRendererEcoQoS = -1;

    private void ClearRendererEfficiencyModeImmediately()
    {
        Interlocked.Increment(ref globalEcoQoSGeneration);
        Volatile.Write(ref lastAppliedRendererEcoQoS, 0);
        if (environment is not { } activeEnvironment) return;

        List<int>? rendererPids = null;
        try
        {
            var processInfos = activeEnvironment.GetProcessInfos();
            if (processInfos is { Count: > 0 })
            {
                rendererPids = new List<int>(processInfos.Count);
                foreach (var processInfo in processInfos)
                {
                    if (processInfo.Kind == CoreWebView2ProcessKind.Renderer && processInfo.ProcessId > 0)
                    {
                        rendererPids.Add(processInfo.ProcessId);
                    }
                }
            }
        }
        catch (Exception error)
        {
            stateStore.Log("Could not inspect WebView2 renderers on restore", error);
        }

        if (rendererPids is not { Count: > 0 }) return;
        _ = Task.Run(() =>
        {
            foreach (var pid in rendererPids)
            {
                SystemResourceInfo.TrySetProcessEfficiencyMode(pid, enable: false);
            }
        });
    }

    private void TrimAllProcessMemory(bool includeActiveForeground = false, bool allowManagedGc = false)
    {
        if (isClosing) return;

        var trimCurrent = includeActiveForeground || isWindowMinimized;
        var hasSuspendedTabs = tabs.Any(item => !item.IsClosed && item.IsSuspended);
        var underMemoryPressure = systemMemory.IsValid && systemMemory.MemoryLoadPercent >= 75;
        var shouldTrimWorkingSets = trimCurrent || allowManagedGc || hasSuspendedTabs || underMemoryPressure;
        var throttleRenderersForEcoQoS = Program.AreAllOpenWindowsMinimized(this);
        var desiredEcoQoS = throttleRenderersForEcoQoS ? 1 : 0;
        var ecoQoSChanged = Volatile.Read(ref lastAppliedRendererEcoQoS) != desiredEcoQoS;
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

        if (!shouldTrimWorkingSets && !shouldCollectGc && !ecoQoSChanged) return;
        if (Interlocked.CompareExchange(ref processTrimInFlight, 1, 0) != 0) return;

        var foregroundFrameIds = !trimCurrent && shouldTrimWorkingSets
            ? Program.CollectForegroundFrameIds(this)
            : null;
        var capturedEcoQoSGeneration = Volatile.Read(ref globalEcoQoSGeneration);
        var activeEnvironment = environment;

        _ = TrimAllProcessMemoryAsync(
            activeEnvironment,
            trimCurrent,
            shouldTrimWorkingSets,
            throttleRenderersForEcoQoS,
            desiredEcoQoS,
            ecoQoSChanged,
            shouldCollectGc,
            foregroundFrameIds,
            capturedEcoQoSGeneration);
    }

    private async Task TrimAllProcessMemoryAsync(
        CoreWebView2Environment? activeEnvironment,
        bool trimCurrent,
        bool shouldTrimWorkingSets,
        bool throttleRenderersForEcoQoS,
        int desiredEcoQoS,
        bool ecoQoSChanged,
        bool shouldCollectGc,
        HashSet<uint>? foregroundFrameIds,
        long capturedEcoQoSGeneration)
    {
        try
        {
            List<(int ProcessId, bool IsRenderer, bool ShouldTrim)>? childProcesses = null;
            if (activeEnvironment is not null && !isClosing && !IsDisposed)
            {
                var usedExtended = false;
                if (foregroundFrameIds is { Count: > 0 })
                {
                    try
                    {
                        var extendedInfos = await activeEnvironment.GetProcessExtendedInfosAsync();
                        usedExtended = true;
                        if (extendedInfos is { Count: > 0 })
                        {
                            childProcesses = new List<(int ProcessId, bool IsRenderer, bool ShouldTrim)>(extendedInfos.Count);
                            foreach (var extendedInfo in extendedInfos)
                            {
                                var processInfo = extendedInfo.ProcessInfo;
                                if (!trimCurrent && processInfo.Kind == CoreWebView2ProcessKind.Gpu)
                                {
                                    continue;
                                }
                                if (processInfo.ProcessId <= 0) continue;

                                var isRenderer = processInfo.Kind == CoreWebView2ProcessKind.Renderer;
                                var isForegroundRenderer = false;
                                if (isRenderer && extendedInfo.AssociatedFrameInfos is { Count: > 0 } frames)
                                {
                                    foreach (var frame in frames)
                                    {
                                        if (foregroundFrameIds.Contains(frame.FrameId))
                                        {
                                            isForegroundRenderer = true;
                                            break;
                                        }
                                    }
                                }

                                childProcesses.Add((
                                    processInfo.ProcessId,
                                    isRenderer,
                                    shouldTrimWorkingSets && !isForegroundRenderer));
                            }
                        }
                    }
                    catch
                    {
                        usedExtended = false;
                    }
                }

                if (!usedExtended && !isClosing && !IsDisposed)
                {
                    try
                    {
                        var processInfos = activeEnvironment.GetProcessInfos();
                        if (processInfos is { Count: > 0 })
                        {
                            var skipRendererTrim = !trimCurrent && foregroundFrameIds is { Count: > 0 };
                            childProcesses = new List<(int ProcessId, bool IsRenderer, bool ShouldTrim)>(processInfos.Count);
                            foreach (var processInfo in processInfos)
                            {
                                if (!trimCurrent && processInfo.Kind == CoreWebView2ProcessKind.Gpu)
                                {
                                    continue;
                                }
                                if (processInfo.ProcessId <= 0) continue;
                                var isRenderer = processInfo.Kind == CoreWebView2ProcessKind.Renderer;
                                childProcesses.Add((
                                    processInfo.ProcessId,
                                    isRenderer,
                                    shouldTrimWorkingSets && (!skipRendererTrim || !isRenderer)));
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        stateStore.Log("Could not inspect WebView2 child processes", error);
                    }
                }
            }

            await Task.Run(() =>
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
                    var appliedEcoQoS = false;
                    foreach (var (pid, isRenderer, shouldTrimProcess) in childProcesses)
                    {
                        if (isRenderer)
                        {
                            if (throttleRenderersForEcoQoS
                                && Volatile.Read(ref globalEcoQoSGeneration) != capturedEcoQoSGeneration)
                            {
                                // A window un-minimized while this sweep was queued.
                            }
                            else if (ecoQoSChanged || !throttleRenderersForEcoQoS)
                            {
                                SystemResourceInfo.TrySetProcessEfficiencyMode(pid, throttleRenderersForEcoQoS);
                                appliedEcoQoS = true;
                            }
                        }
                        if (shouldTrimProcess)
                        {
                            SystemResourceInfo.TrimProcessWorkingSet(pid);
                        }
                    }
                    if (appliedEcoQoS && Volatile.Read(ref globalEcoQoSGeneration) == capturedEcoQoSGeneration)
                    {
                        Volatile.Write(ref lastAppliedRendererEcoQoS, desiredEcoQoS);
                    }
                }
            });
        }
        catch (Exception error)
        {
            stateStore.Log("Could not trim process memory", error);
        }
        finally
        {
            Volatile.Write(ref processTrimInFlight, 0);
        }
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
            var minimizingTabs = new List<BrowserTab>(4);
            if (multiViewState.IsMultiViewActive && !isDomFullScreen)
            {
                foreach (var visibleId in multiViewState.GetVisibleTabIds())
                {
                    var paneTab = FindTabById(visibleId);
                    if (paneTab is not null && !paneTab.IsClosed && !minimizingTabs.Contains(paneTab))
                    {
                        minimizingTabs.Add(paneTab);
                    }
                }
            }
            if (activeTab is not null && !activeTab.IsClosed && !minimizingTabs.Contains(activeTab))
            {
                minimizingTabs.Add(activeTab);
            }

            var minimizedAtUtc = DateTime.UtcNow;
            foreach (var minimizingTab in minimizingTabs)
            {
                if (minimizingTab.View is not null) minimizingTab.View.Visible = false;
                if (minimizingTab.StartPageView is not null)
                {
                    minimizingTab.StartPageView.Visible = false;
                }
                minimizingTab.LastActiveUtc = minimizedAtUtc;
                minimizingTab.ConsecutiveSuspendFailures = 0;
                ApplyLiveTabMemoryTarget(minimizingTab, foreground: false);
            }
            if (TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled) == TabLifecycleMode.Ultra)
            {
                foreach (var minimizingTab in minimizingTabs)
                {
                    if (minimizingTab.IsClosed) continue;
                    var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
                    if (mode != TabLifecycleMode.Ultra) break;
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
            }
            TrimAllProcessMemory(includeActiveForeground: true, allowManagedGc: true);
            RefreshMemorySweepTimer();
            return;
        }

        ClearRendererEfficiencyModeImmediately();
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

    private void SetResourceMode(TabLifecycleMode mode, string? statusOverride = null)
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
        ShowTransientStatus(statusOverride ?? normalizedMode switch
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
        var nextMode = memorySaverEnabled ? TabLifecycleMode.Off : TabLifecycleMode.Standard;
        SetResourceMode(nextMode, nextMode == TabLifecycleMode.Standard ? "Memory saver on" : "Memory saver off");
    }

    private void ToggleUltraLightMode()
    {
        if (ultraLightEnabled)
        {
            foreach (var tab in tabs.Where(item => item.IsSuspended)) ResumeTab(tab);
            SetResourceMode(TabLifecycleMode.Standard, "Ultra-light mode off");
            return;
        }

        SetResourceMode(
            TabLifecycleMode.Ultra,
            "Ultra-light mode on \u2014 very old inactive tabs may reload");
    }

    private void CycleResourceMode()
    {
        var nextMode = ultraLightEnabled
            ? TabLifecycleMode.Off
            : memorySaverEnabled
                ? TabLifecycleMode.Ultra
                : TabLifecycleMode.Standard;
        var mode = nextMode switch
        {
            TabLifecycleMode.Ultra => "Ultra-light mode",
            TabLifecycleMode.Standard => "Memory saver",
            _ => "Resource saver off"
        };
        SetResourceMode(nextMode, $"{mode} selected");
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
