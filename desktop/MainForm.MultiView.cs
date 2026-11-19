namespace MishaWeb;

public sealed partial class MainForm
{
    private bool applyingMultiViewLayout;
    private readonly BrowserTab?[] multiViewVisibleTabs = new BrowserTab?[4];
    private readonly (Control Control, Rectangle Bounds)[] multiViewPlacements = new (Control Control, Rectangle Bounds)[10];

    private void BuildMultiViewSurface()
    {
        multiViewMenu.ShowImageMargin = false;
        multiViewMenu.Renderer = menuRenderer;
        multiViewMenu.BackColor = ToolbarColor;
        multiViewMenu.ForeColor = PageTextColor;
        NativeUiTheme.ApplyMenuWindowFrame(multiViewMenu);

        paneSwitchMenu.ShowImageMargin = false;
        paneSwitchMenu.Renderer = menuRenderer;
        paneSwitchMenu.BackColor = ToolbarColor;
        paneSwitchMenu.ForeColor = PageTextColor;
        NativeUiTheme.ApplyMenuWindowFrame(paneSwitchMenu);

        multiViewSplitters[0] = new MultiViewSplitter(isVertical: true) { Visible = false };
        multiViewSplitters[0].RatioDragged += ratio =>
        {
            multiViewState.SetSplitRatios(ratio, null);
            ApplyMultiViewLayout();
        };
        multiViewSplitters[0].DragCompleted += () => ScheduleStateSave();
        multiViewSplitters[0].ResetRequested += () =>
        {
            multiViewState.ResetRatios();
            ApplyMultiViewLayout();
            ScheduleStateSave();
        };

        multiViewSplitters[1] = new MultiViewSplitter(isVertical: false) { Visible = false };
        multiViewSplitters[1].RatioDragged += ratio =>
        {
            multiViewState.SetSplitRatios(null, ratio);
            ApplyMultiViewLayout();
        };
        multiViewSplitters[1].DragCompleted += () => ScheduleStateSave();
        multiViewSplitters[1].ResetRequested += () =>
        {
            multiViewState.ResetRatios();
            ApplyMultiViewLayout();
            ScheduleStateSave();
        };

        for (var i = 0; i < paneHeaders.Length; i++)
        {
            var paneIndex = i;
            var header = new PaneHeaderBar(paneIndex) { Visible = false };
            header.PaneActivated += (_, _) => FocusMultiViewPane(paneIndex, focusContent: true);
            header.CloseRequested += (_, _) => CloseMultiViewPane(paneIndex);
            header.TabSwitchRequested += (_, screenPoint) => ShowPaneTabSwitchMenu(paneIndex, screenPoint);
            header.AudioToggleRequested += (_, _) => ToggleMultiViewPaneAudioMute(paneIndex);
            header.MaximizeToggleRequested += (_, _) => ToggleMultiViewPaneMaximized(paneIndex);
            paneHeaders[i] = header;
            pageHost.Controls.Add(header);
        }

        pageHost.Controls.Add(multiViewSplitters[0]);
        pageHost.Controls.Add(multiViewSplitters[1]);
    }

    private BrowserTab? FindTabById(int tabId)
    {
        if (tabId <= 0) return null;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].Id == tabId) return tabs[i];
        }
        return null;
    }

    private bool IsTabVisibleInAnyPane(BrowserTab? tab)
    {
        if (tab is null || tab.IsClosed) return false;
        if (tab == activeTab) return true;
        return multiViewState.IsMultiViewActive
            && !isDomFullScreen
            && multiViewState.ContainsTab(tab.Id);
    }

    private bool IsTabForegroundForLifecycle(BrowserTab tab) =>
        !isWindowMinimized && IsTabVisibleInAnyPane(tab);

    private void HandleMultiViewButtonClick()
    {
        if (isClosing) return;
        if (multiViewState.Preset == MultiViewPreset.Quad)
        {
            ShowMultiViewMenu(multiViewButton);
            return;
        }
        RunUiTask(AddMultiViewPaneAsync, "Could not add a new view");
    }

    private async Task AddMultiViewPaneAsync()
    {
        if (isClosing || multiViewState.Preset == MultiViewPreset.Quad) return;

        if (!multiViewState.IsMultiViewActive && activeTab is not null)
        {
            multiViewState.SetSingle(activeTab.Id);
        }

        var newTab = await OpenNewTabAsync(StartPage.Url, activate: false);
        if (newTab is null || isClosing || newTab.IsClosed) return;

        ResumeTab(newTab);
        PrepareNativeStartPage(newTab);
        var added = multiViewState.AddPane(newTab.Id);
        if (!added && !multiViewState.ContainsTab(newTab.Id)) return;

        FocusMultiViewPane(multiViewState.FocusedPaneIndex, focusContent: true);
        ApplyMultiViewLayout();
        RefreshLiveTabMemoryTargets();
        RefreshMemorySweepTimer();
        UpdateStatus();
        ScheduleStateSave();
    }

    private async Task SetMultiViewPresetAsync(MultiViewPreset preset)
    {
        if (isClosing) return;

        if (preset == MultiViewPreset.Single)
        {
            var focusedTab = FindTabById(multiViewState.FocusedTabId) ?? activeTab ?? tabs.FirstOrDefault();
            if (focusedTab is null) return;
            multiViewState.SetSingle(focusedTab.Id);
            ActivateTab(focusedTab, focusPage: true);
            ApplyMultiViewLayout();
            RefreshLiveTabMemoryTargets();
            ScheduleStateSave();
            return;
        }

        var orderedIds = new List<int>(4);
        if (activeTab is not null && !activeTab.IsClosed)
        {
            orderedIds.Add(activeTab.Id);
        }
        foreach (var existingId in multiViewState.GetVisibleTabIds())
        {
            if (existingId > 0 && !orderedIds.Contains(existingId) && FindTabById(existingId) is { IsClosed: false })
            {
                orderedIds.Add(existingId);
            }
        }
        foreach (var candidate in tabs)
        {
            if (!candidate.IsClosed && !orderedIds.Contains(candidate.Id))
            {
                orderedIds.Add(candidate.Id);
            }
            if (orderedIds.Count >= (int)preset) break;
        }

        while (orderedIds.Count < (int)preset && tabs.Count < MaximumTabs)
        {
            var fresh = await OpenNewTabAsync(StartPage.Url, activate: false);
            if (fresh is null || fresh.IsClosed) break;
            PrepareNativeStartPage(fresh);
            orderedIds.Add(fresh.Id);
        }

        multiViewState.ApplyPreset(preset, orderedIds);
        foreach (var visibleId in multiViewState.GetVisibleTabIds())
        {
            var visibleTab = FindTabById(visibleId);
            if (visibleTab is null || visibleTab.IsClosed) continue;
            ResumeTab(visibleTab);
            if (visibleTab.IsStartPage) PrepareNativeStartPage(visibleTab);
        }

        FocusMultiViewPane(multiViewState.FocusedPaneIndex, focusContent: false);
        ApplyMultiViewLayout();
        RefreshLiveTabMemoryTargets();
        RefreshMemorySweepTimer();
        UpdateStatus();
        ScheduleStateSave();
    }

    private void CloseMultiViewPane(int paneIndex)
    {
        if (isClosing || !multiViewState.IsMultiViewActive) return;

        var closingTabId = multiViewState.GetTabIdForPane(paneIndex);
        if (!multiViewState.ClosePane(paneIndex)) return;

        var nextActive = FindTabById(multiViewState.FocusedTabId) ?? tabs.FirstOrDefault();
        if (nextActive is not null)
        {
            if (!multiViewState.IsMultiViewActive)
            {
                ActivateTab(nextActive, focusPage: true);
            }
            else
            {
                FocusMultiViewPane(multiViewState.FocusedPaneIndex, focusContent: true);
                ApplyMultiViewLayout();
            }
        }

        var closingTab = FindTabById(closingTabId);
        if (closingTab is { IsClosed: false })
        {
            if (closingTab.IsStartPage && tabs.Count > multiViewState.PaneCount)
            {
                CloseTab(closingTab, remember: false, ensureReplacement: false);
            }
            else
            {
                QueueBackgroundTabReduction(closingTab);
            }
        }

        RefreshLiveTabMemoryTargets();
        RefreshMemorySweepTimer();
        ScheduleStateSave();
    }

    private void FocusMultiViewPane(int paneIndex, bool focusContent)
    {
        if (!multiViewState.IsMultiViewActive) return;

        multiViewState.FocusPane(paneIndex);
        var tab = FindTabById(multiViewState.FocusedTabId);
        if (tab is null || tab.IsClosed) return;

        var previous = activeTab != tab ? activeTab : null;
        if (previous is not null)
        {
            previous.LastActiveUtc = DateTime.UtcNow;
            StopFindSession(previous);
        }

        activeTab = tab;
        tab.LastActiveUtc = DateTime.UtcNow;
        if (!restoringSession) mruTabs.ObserveActivation(tab);
        ResumeTab(tab);

        if (previous is not null) UpdateTabHeader(previous);
        UpdateTabHeader(tab);
        RefreshMultiViewHeaders();

        addressBarEditing = false;
        SyncAddressBar();
        UpdateWindowTitle();
        UpdateNavigationChrome();
        UpdateStatus();
        RefreshLiveTabMemoryTargets();

        if (findBar.Visible && findBox.TextLength > 0 && tab.Core is not null)
        {
            RunUiTask(StartFindSession, "Find failed");
        }
        else if (findBar.Visible)
        {
            findMatchLabel.Text = string.Empty;
        }

        if (tab.IsDiscarded && !tab.IsInitializing)
        {
            RunUiTask(() => RestoreDiscardedTabAsync(tab, focusContent), "Could not reload the active pane tab");
            return;
        }

        if (focusContent && !tab.IsInitializing)
        {
            if (tab.StartPageView is { Visible: true } startPage)
            {
                startPage.FocusSearch();
            }
            else if (tab.View is { Visible: true, IsDisposed: false } view)
            {
                view.Focus();
            }
        }
    }

    private void OnPaneContentFocused(BrowserTab tab)
    {
        if (isClosing || tab.IsClosed || !multiViewState.IsMultiViewActive || isDomFullScreen) return;

        var paneIndex = multiViewState.IndexOfTab(tab.Id);
        if (paneIndex < 0) return;
        if (multiViewState.FocusedPaneIndex == paneIndex && activeTab == tab) return;

        FocusMultiViewPane(paneIndex, focusContent: false);
    }

    private void ShowPaneTabSwitchMenu(int paneIndex, Point screenLocation)
    {
        if (isClosing) return;
        ClearAndDisposeMenuItems(paneSwitchMenu.Items);
        var currentTabId = multiViewState.GetTabIdForPane(paneIndex);

        for (var i = 0; i < tabs.Count; i++)
        {
            var candidate = tabs[i];
            if (candidate.IsClosed) continue;
            var label = TrimMenuText(
                string.IsNullOrWhiteSpace(candidate.Title) ? HostFromUrl(candidate.Url) : candidate.Title,
                42);
            var item = new ToolStripMenuItem(label)
            {
                Checked = candidate.Id == currentTabId
            };
            var captured = candidate;
            item.Click += (_, _) =>
            {
                if (captured.IsStartPage) PrepareNativeStartPage(captured);
                multiViewState.AssignTabToPane(paneIndex, captured.Id);
                FocusMultiViewPane(paneIndex, focusContent: true);
                ApplyMultiViewLayout();
                RefreshLiveTabMemoryTargets();
                ScheduleStateSave();
            };
            paneSwitchMenu.Items.Add(item);
        }

        if (paneSwitchMenu.Items.Count > 0)
        {
            paneSwitchMenu.Show(screenLocation);
        }
    }

    private void ShowMultiViewMenu(Control anchor)
    {
        ClearAndDisposeMenuItems(multiViewMenu.Items);

        AddPresetItem("Single view (1 pane)", MultiViewPreset.Single);
        AddPresetItem("Split view (2 panes)", MultiViewPreset.Split);
        AddPresetItem("Tri view (3 panes)", MultiViewPreset.Tri);
        AddPresetItem("Quad view (4 panes)", MultiViewPreset.Quad);
        multiViewMenu.Items.Add(new ToolStripSeparator());

        var swapItem = new ToolStripMenuItem("Swap panes")
        {
            Enabled = multiViewState.IsMultiViewActive
        };
        swapItem.Click += (_, _) => SwapFocusedMultiViewPane();
        multiViewMenu.Items.Add(swapItem);

        var maximizeItem = new ToolStripMenuItem(
            multiViewState.IsFocusedPaneMaximized ? "Restore pane grid" : "Maximize focused pane")
        {
            Enabled = multiViewState.IsMultiViewActive
        };
        maximizeItem.Click += (_, _) => ToggleMultiViewPaneMaximized(multiViewState.FocusedPaneIndex);
        multiViewMenu.Items.Add(maximizeItem);

        var resetItem = new ToolStripMenuItem("Reset split sizes")
        {
            Enabled = multiViewState.IsMultiViewActive
        };
        resetItem.Click += (_, _) =>
        {
            multiViewState.ResetRatios();
            ApplyMultiViewLayout();
            ScheduleStateSave();
        };
        multiViewMenu.Items.Add(resetItem);

        var closeFocusedItem = new ToolStripMenuItem("Close focused pane")
        {
            Enabled = multiViewState.IsMultiViewActive
        };
        closeFocusedItem.Click += (_, _) => CloseMultiViewPane(multiViewState.FocusedPaneIndex);
        multiViewMenu.Items.Add(closeFocusedItem);

        multiViewMenu.Show(anchor, new Point(0, anchor.Height), ToolStripDropDownDirection.BelowLeft);

        void AddPresetItem(string text, MultiViewPreset preset)
        {
            var item = new ToolStripMenuItem(text)
            {
                Checked = multiViewState.Preset == preset
            };
            item.Click += (_, _) => RunUiTask(
                () => SetMultiViewPresetAsync(preset),
                "Could not update the multi-view layout");
            multiViewMenu.Items.Add(item);
        }
    }

    private void ToggleMultiViewPaneAudioMute(int paneIndex)
    {
        if (isClosing || !multiViewState.IsMultiViewActive) return;
        var tab = FindTabById(multiViewState.GetTabIdForPane(paneIndex));
        if (tab is { IsClosed: false })
        {
            ToggleTabAudioMute(tab);
        }
    }

    internal void ToggleMultiViewPaneMaximized(int paneIndex)
    {
        if (isClosing || !multiViewState.IsMultiViewActive) return;
        if (paneIndex >= 0 && paneIndex < multiViewState.PaneCount)
        {
            multiViewState.FocusPane(paneIndex);
        }
        if (!multiViewState.ToggleFocusedPaneMaximized()) return;
        FocusMultiViewPane(multiViewState.FocusedPaneIndex, focusContent: true);
        ApplyMultiViewLayout();
        RefreshLiveTabMemoryTargets();
    }

    internal void SwapFocusedMultiViewPane()
    {
        if (isClosing || !multiViewState.IsMultiViewActive) return;
        if (!multiViewState.SwapFocusedPaneWithNext()) return;
        FocusMultiViewPane(multiViewState.FocusedPaneIndex, focusContent: true);
        ApplyMultiViewLayout();
        ScheduleStateSave();
    }

    private void OpenContextLinkInSplitView(BrowserTab sourceTab)
    {
        var target = sourceTab.ContextLinkTarget;
        if (isClosing
            || sourceTab.IsClosed
            || string.IsNullOrWhiteSpace(target)
            || !BrowserPolicy.IsHttpUrl(target))
        {
            ShowTransientStatus("The link is no longer available");
            return;
        }

        RunUiTask(
            () => OpenUrlInSplitViewAsync(sourceTab, target),
            "Could not open link in split view");
    }

    private async Task OpenUrlInSplitViewAsync(BrowserTab? anchorTab, string targetUrl)
    {
        if (isClosing || string.IsNullOrWhiteSpace(targetUrl)) return;

        var baseTab = anchorTab is { IsClosed: false } ? anchorTab : activeTab;
        if (!multiViewState.IsMultiViewActive && baseTab is not null)
        {
            multiViewState.SetSingle(baseTab.Id);
        }

        var newTab = await OpenNewTabAsync(targetUrl, activate: false);
        if (newTab is null || isClosing || newTab.IsClosed) return;

        if (multiViewState.Preset == MultiViewPreset.Quad)
        {
            var nextPane = (multiViewState.FocusedPaneIndex + 1) % multiViewState.PaneCount;
            multiViewState.AssignTabToPane(nextPane, newTab.Id);
        }
        else
        {
            multiViewState.AddPane(newTab.Id);
        }

        FocusMultiViewPane(multiViewState.FocusedPaneIndex, focusContent: true);
        ApplyMultiViewLayout();
        if (newTab.IsDiscarded && !newTab.IsInitializing)
        {
            await RestoreDiscardedTabAsync(newTab, focusPage: true);
        }
        RefreshLiveTabMemoryTargets();
        RefreshMemorySweepTimer();
        UpdateStatus();
        ScheduleStateSave();
    }

    private bool TryDropTabOntoMultiViewPane(BrowserTab tab, Point screenLocation)
    {
        if (isClosing || tab.IsClosed || !tabs.Contains(tab) || isDomFullScreen) return false;
        if (pageHost.ClientSize.Width <= 0 || pageHost.ClientSize.Height <= 0) return false;

        var localPoint = pageHost.PointToClient(screenLocation);
        var hostRect = pageHost.ClientRectangle;
        if (!hostRect.Contains(localPoint)) return false;

        if (!multiViewState.IsMultiViewActive)
        {
            var anchor = activeTabBeforeDrag is { IsClosed: false } prev && prev != tab
                ? prev
                : tabs.FirstOrDefault(item => !item.IsClosed && item != tab);
            if (anchor is null) return false;

            var dropLeft = localPoint.X < hostRect.Left + (hostRect.Width / 2);
            var orderedIds = dropLeft
                ? new[] { tab.Id, anchor.Id }
                : new[] { anchor.Id, tab.Id };
            multiViewState.ApplyPreset(MultiViewPreset.Split, orderedIds);
            multiViewState.FocusPane(dropLeft ? 0 : 1);
        }
        else
        {
            var layout = lastMultiViewLayout ?? MultiViewLayoutEngine.ComputeLayout(
                hostRect,
                multiViewState,
                ScaleChromeLogical(4),
                ScaleChromeLogical(26));
            var hitPane = -1;
            for (var i = 0; i < layout.PaneCount; i++)
            {
                if (layout.PaneBounds[i].Contains(localPoint))
                {
                    hitPane = i;
                    break;
                }
            }
            if (hitPane < 0) hitPane = multiViewState.FocusedPaneIndex;

            if (multiViewState.PaneCount < 4 && !multiViewState.ContainsTab(tab.Id))
            {
                var paneRect = layout.PaneBounds[hitPane];
                var nearOuterEdge = localPoint.X >= paneRect.Right - Math.Max(48, paneRect.Width / 4)
                    || localPoint.Y >= paneRect.Bottom - Math.Max(48, paneRect.Height / 4);
                if (nearOuterEdge)
                {
                    multiViewState.AddPane(tab.Id);
                }
                else
                {
                    multiViewState.AssignTabToPane(hitPane, tab.Id);
                }
            }
            else
            {
                multiViewState.AssignTabToPane(hitPane, tab.Id);
            }
        }

        if (tab.IsStartPage) PrepareNativeStartPage(tab);
        FocusMultiViewPane(multiViewState.FocusedPaneIndex, focusContent: true);
        ApplyMultiViewLayout();
        RefreshLiveTabMemoryTargets();
        RefreshMemorySweepTimer();
        UpdateStatus();
        ScheduleStateSave();
        return true;
    }

    private void RefreshMultiViewHeaders()
    {
        var hc = IsHighContrastActive;
        var visibleIds = multiViewState.GetVisibleTabIds();
        for (var i = 0; i < paneHeaders.Length; i++)
        {
            if (!multiViewState.IsMultiViewActive || isDomFullScreen || i >= visibleIds.Length)
            {
                continue;
            }
            var tab = FindTabById(visibleIds[i]);
            var title = tab?.Title ?? "New tab";
            paneHeaders[i].UpdateMetadata(
                title,
                focused: i == multiViewState.FocusedPaneIndex,
                privateMode: isPrivateMode,
                highContrastActive: hc,
                audible: tab is not null && IsTabAudible(tab),
                muted: tab is not null && IsTabMuted(tab));
        }
        multiViewButton.ActiveViewCount = multiViewState.PaneCount;
    }

    private void ApplyMultiViewLayout()
    {
        if (isClosing || IsDisposed || applyingMultiViewLayout) return;

        applyingMultiViewLayout = true;
        try
        {
            ApplyMultiViewLayoutCore();
        }
        finally
        {
            applyingMultiViewLayout = false;
        }
    }

    private void ApplyMultiViewLayoutCore()
    {
        multiViewButton.ActiveViewCount = multiViewState.PaneCount;

        if (!multiViewState.IsMultiViewActive || isDomFullScreen)
        {
            for (var i = 0; i < paneHeaders.Length; i++) paneHeaders[i].Visible = false;
            for (var i = 0; i < multiViewSplitters.Length; i++) multiViewSplitters[i].Visible = false;
            lastMultiViewLayout = null;
            for (var i = 0; i < tabs.Count; i++)
            {
                var t = tabs[i];
                if (t == activeTab) continue;
                if (t.Host.Visible) t.Host.Visible = false;
                if (t.View is not null && t.View.Visible) t.View.Visible = false;
            }
            if (activeTab is not null)
            {
                if (!activeTab.Host.Visible) activeTab.Host.Visible = true;
                EnsureTabHostLayout(activeTab);
            }
            return;
        }

        if (pageHost.ClientSize.Width <= 0 || pageHost.ClientSize.Height <= 0) return;

        var layout = MultiViewLayoutEngine.ComputeLayout(
            pageHost.ClientRectangle,
            multiViewState,
            ScaleChromeLogical(4),
            ScaleChromeLogical(26));
        lastMultiViewLayout = layout;

        var visibleIds = multiViewState.GetVisibleTabIds();
        var paneCount = layout.PaneCount;
        var visibleTabs = multiViewVisibleTabs;
        var placementCount = 0;
        var focusedPaneMaximized = multiViewState.IsFocusedPaneMaximized;
        var focusedPaneIndex = Math.Clamp(multiViewState.FocusedPaneIndex, 0, paneCount - 1);
        var fullHostRect = pageHost.ClientRectangle;
        var fullHeaderHeight = Math.Min(ScaleChromeLogical(26), Math.Max(18, fullHostRect.Height / 4));
        var maximizedHeaderBounds = new Rectangle(fullHostRect.X, fullHostRect.Y, fullHostRect.Width, fullHeaderHeight);
        var maximizedContentBounds = new Rectangle(
            fullHostRect.X,
            fullHostRect.Y + fullHeaderHeight,
            fullHostRect.Width,
            Math.Max(1, fullHostRect.Height - fullHeaderHeight));

        try
        {
            for (var i = 0; i < paneCount; i++)
            {
                var paneTab = i < visibleIds.Length ? FindTabById(visibleIds[i]) : null;
                visibleTabs[i] = paneTab;
                if (paneTab is not null && !paneTab.IsClosed)
                {
                    if (paneTab.IsStartPage && paneTab.StartPageView is null) PrepareNativeStartPage(paneTab);
                    if (paneTab.IsDiscarded && !paneTab.IsInitializing)
                    {
                        var captured = paneTab;
                        RunUiTask(
                            () => RestoreDiscardedTabAsync(captured, captured == activeTab),
                            "Could not restore pane tab");
                    }
                }
            }

            for (var i = 0; i < tabs.Count; i++)
            {
                var tab = tabs[i];
                var isVisiblePane = false;
                for (var p = 0; p < paneCount; p++)
                {
                    if (focusedPaneMaximized && p != focusedPaneIndex) continue;
                    if (ReferenceEquals(visibleTabs[p], tab))
                    {
                        isVisiblePane = true;
                        break;
                    }
                }
                if (!isVisiblePane)
                {
                    if (tab.Host.Visible) tab.Host.Visible = false;
                    if (tab.View is not null && tab.View.Visible) tab.View.Visible = false;
                }
            }

            for (var i = 0; i < paneHeaders.Length; i++)
            {
                if (i < paneCount && (!focusedPaneMaximized || i == focusedPaneIndex))
                {
                    var header = paneHeaders[i];
                    var paneTab = visibleTabs[i];
                    var targetHeaderBounds = focusedPaneMaximized ? maximizedHeaderBounds : layout.HeaderBounds[i];
                    var targetContentBounds = focusedPaneMaximized ? maximizedContentBounds : layout.ContentBounds[i];
                    header.UpdateMetadata(
                        paneTab?.Title ?? "New tab",
                        focused: i == multiViewState.FocusedPaneIndex,
                        privateMode: isPrivateMode,
                        highContrastActive: IsHighContrastActive,
                        audible: paneTab is not null && IsTabAudible(paneTab),
                        muted: paneTab is not null && IsTabMuted(paneTab));
                    if (!header.Visible) header.Visible = true;
                    multiViewPlacements[placementCount++] = (header, targetHeaderBounds);
                    if (paneTab is not null && !paneTab.IsClosed)
                    {
                        if (paneTab.Host.Dock != DockStyle.None) paneTab.Host.Dock = DockStyle.None;
                        if (!paneTab.Host.Visible) paneTab.Host.Visible = true;
                        multiViewPlacements[placementCount++] = (paneTab.Host, targetContentBounds);
                    }
                }
                else
                {
                    if (paneHeaders[i].Visible) paneHeaders[i].Visible = false;
                }
            }

            for (var s = 0; s < multiViewSplitters.Length; s++)
            {
                var splitter = multiViewSplitters[s];
                if (!focusedPaneMaximized && s < layout.Splitters.Length)
                {
                    var desc = layout.Splitters[s];
                    splitter.UpdateDragRegion(desc.DragRegionBounds);
                    if (!splitter.Visible) splitter.Visible = true;
                    multiViewPlacements[placementCount++] = (splitter, desc.Bounds);
                }
                else
                {
                    if (splitter.Visible) splitter.Visible = false;
                }
            }

            if (placementCount > 0)
            {
                NativeMethods.ApplyDeferredBounds(multiViewPlacements.AsSpan(0, placementCount));
            }

            for (var i = 0; i < paneCount; i++)
            {
                if (focusedPaneMaximized && i != focusedPaneIndex) continue;
                var targetHeaderBounds = focusedPaneMaximized ? maximizedHeaderBounds : layout.HeaderBounds[i];
                var targetContentBounds = focusedPaneMaximized ? maximizedContentBounds : layout.ContentBounds[i];
                if (paneHeaders[i].Bounds != targetHeaderBounds)
                {
                    paneHeaders[i].Bounds = targetHeaderBounds;
                }
                var paneTab = visibleTabs[i];
                if (paneTab is not null && !paneTab.IsClosed)
                {
                    if (paneTab.Host.Dock != DockStyle.None) paneTab.Host.Dock = DockStyle.None;
                    if (paneTab.Host.Bounds != targetContentBounds)
                    {
                        paneTab.Host.Bounds = targetContentBounds;
                    }
                    paneTab.Host.PerformLayout();
                }
            }
            if (!focusedPaneMaximized)
            {
                for (var s = 0; s < layout.Splitters.Length; s++)
                {
                    if (multiViewSplitters[s].Bounds != layout.Splitters[s].Bounds)
                    {
                        multiViewSplitters[s].Bounds = layout.Splitters[s].Bounds;
                    }
                }
            }

            for (var i = 0; i < paneCount; i++)
            {
                if (focusedPaneMaximized && i != focusedPaneIndex) continue;
                var paneTab = visibleTabs[i];
                if (paneTab is null || paneTab.IsClosed) continue;
                if (!isWindowMinimized && paneTab.IsSuspended) ResumeTab(paneTab);

                var targetContentBounds = focusedPaneMaximized ? maximizedContentBounds : layout.ContentBounds[i];
                var innerBounds = new Rectangle(Point.Empty, targetContentBounds.Size);
                if (paneTab.StartPageView is not null)
                {
                    var showStart = paneTab.IsStartPage && !paneTab.Overlay.Visible;
                    if (paneTab.StartPageView.Visible != showStart) paneTab.StartPageView.Visible = showStart;
                    if (showStart)
                    {
                        if (paneTab.StartPageView.Bounds != innerBounds) paneTab.StartPageView.Bounds = innerBounds;
                        paneTab.StartPageView.PerformLayout();
                        paneTab.StartPageView.BringToFront();
                    }
                }

                if (paneTab.View is not null)
                {
                    var showView = !isWindowMinimized
                        && !paneTab.Overlay.Visible
                        && paneTab.StartPageView?.Visible != true;
                    if (paneTab.View.Visible != showView) paneTab.View.Visible = showView;
                    if (showView)
                    {
                        if (paneTab.View.Bounds != innerBounds) paneTab.View.Bounds = innerBounds;
                        paneTab.View.BringToFront();
                    }
                }

                if (paneTab.Overlay.Visible)
                {
                    if (paneTab.Overlay.Bounds != innerBounds) paneTab.Overlay.Bounds = innerBounds;
                    paneTab.Overlay.BringToFront();
                }
            }

            for (var i = 0; i < paneCount; i++) paneHeaders[i].BringToFront();
            for (var s = 0; s < layout.Splitters.Length; s++) multiViewSplitters[s].BringToFront();
        }
        finally
        {
            Array.Clear(visibleTabs, 0, paneCount);
            Array.Clear(multiViewPlacements, 0, placementCount);
        }
    }

    private async Task<bool> RestoreSavedMultiViewLayoutAsync(SavedMultiViewLayout saved)
    {
        if (isPrivateMode || isClosing || saved.PaneTabIndices is null || saved.PaneTabIndices.Length < 2)
        {
            return false;
        }

        var mappedIds = new List<int>(4);
        foreach (var tabIndex in saved.PaneTabIndices)
        {
            if (tabIndex < 0 || tabIndex >= tabs.Count) continue;
            var candidate = tabs[tabIndex];
            if (!candidate.IsClosed && !mappedIds.Contains(candidate.Id))
            {
                mappedIds.Add(candidate.Id);
                if (mappedIds.Count >= 4) break;
            }
        }

        if (mappedIds.Count < 2) return false;

        var targetPreset = mappedIds.Count switch
        {
            2 => MultiViewPreset.Split,
            3 => MultiViewPreset.Tri,
            _ => MultiViewPreset.Quad
        };
        multiViewState.ApplyPreset(targetPreset, mappedIds);
        multiViewState.SetSplitRatios(saved.HorizontalRatio, saved.VerticalRatio);
        multiViewState.FocusPane(saved.FocusedPaneIndex);

        var restoreTasks = new List<Task>(4);
        foreach (var visibleId in multiViewState.GetVisibleTabIds())
        {
            var paneTab = FindTabById(visibleId);
            if (paneTab is null || paneTab.IsClosed) continue;
            if (paneTab.IsStartPage) PrepareNativeStartPage(paneTab);
            else if (paneTab.IsDiscarded && !paneTab.IsInitializing)
            {
                restoreTasks.Add(RestoreDiscardedTabAsync(paneTab, focusPage: false));
            }
        }
        if (restoreTasks.Count > 0)
        {
            await Task.WhenAll(restoreTasks);
        }

        FocusMultiViewPane(multiViewState.FocusedPaneIndex, focusContent: false);
        ApplyMultiViewLayout();
        RefreshLiveTabMemoryTargets();
        return true;
    }

    internal Task<bool> AddMultiViewPaneForTestingAsync() =>
        AddMultiViewPaneAndReportAsync();

    private async Task<bool> AddMultiViewPaneAndReportAsync()
    {
        var before = multiViewState.PaneCount;
        await AddMultiViewPaneAsync();
        return multiViewState.PaneCount > before;
    }

    internal Task SetMultiViewPresetForTestingAsync(MultiViewPreset preset) =>
        SetMultiViewPresetAsync(preset);

    internal void FocusMultiViewPaneForTesting(int paneIndex) =>
        FocusMultiViewPane(paneIndex, focusContent: false);

    internal MultiViewSnapshot GetMultiViewSnapshotForTesting()
    {
        var layout = lastMultiViewLayout ?? MultiViewLayoutEngine.ComputeLayout(
            pageHost.ClientRectangle,
            multiViewState,
            ScaleChromeLogical(4),
            ScaleChromeLogical(26));
        return new MultiViewSnapshot(
            multiViewState.Preset,
            multiViewState.PaneCount,
            multiViewState.FocusedPaneIndex,
            multiViewState.HorizontalSplitRatio,
            multiViewState.VerticalSplitRatio,
            multiViewState.GetVisibleTabIds(),
            layout.PaneBounds,
            layout.HeaderBounds,
            layout.ContentBounds);
    }

    internal Task NavigateActiveTabForTestingAsync(string url) =>
        NavigateActiveAsync(url);

    internal void CloseMultiViewPaneForTesting(int paneIndex) =>
        CloseMultiViewPane(paneIndex);

    internal int GetLiveVisiblePaneWebViewCountForTesting()
    {
        var count = 0;
        var visibleIds = multiViewState.GetVisibleTabIds();
        for (var i = 0; i < visibleIds.Length; i++)
        {
            var tab = FindTabById(visibleIds[i]);
            if (tab is { IsClosed: false, Core: not null, View: { Visible: true, Width: > 0, Height: > 0 } })
            {
                count++;
            }
        }
        return count;
    }

    internal string[] GetVisiblePaneUrlsForTesting()
    {
        var visibleIds = multiViewState.GetVisibleTabIds();
        var urls = new string[visibleIds.Length];
        for (var i = 0; i < visibleIds.Length; i++)
        {
            var tab = FindTabById(visibleIds[i]);
            urls[i] = tab is null || tab.IsClosed
                ? string.Empty
                : (tab.Core?.Source ?? tab.Url);
        }
        return urls;
    }

    internal (Rectangle[] HostBounds, Rectangle[] HeaderBounds, IntPtr[] HostHandles, IntPtr[] ViewHandles, IntPtr[] HeaderHandles)
        GetActualMultiViewGeometryForTesting()
    {
        var count = multiViewState.PaneCount;
        var visibleIds = multiViewState.GetVisibleTabIds();
        var hostBounds = new Rectangle[count];
        var headerBounds = new Rectangle[count];
        var hostHandles = new IntPtr[count];
        var viewHandles = new IntPtr[count];
        var headerHandles = new IntPtr[count];
        for (var i = 0; i < count; i++)
        {
            var paneTab = i < visibleIds.Length ? FindTabById(visibleIds[i]) : null;
            if (paneTab is not null && !paneTab.IsClosed)
            {
                hostBounds[i] = paneTab.Host.Bounds;
                hostHandles[i] = paneTab.Host.IsHandleCreated ? paneTab.Host.Handle : IntPtr.Zero;
                viewHandles[i] = paneTab.View is { IsDisposed: false, IsHandleCreated: true } view
                    ? view.Handle
                    : IntPtr.Zero;
            }
            headerBounds[i] = i < paneHeaders.Length ? paneHeaders[i].Bounds : Rectangle.Empty;
            headerHandles[i] = i < paneHeaders.Length && paneHeaders[i].IsHandleCreated
                ? paneHeaders[i].Handle
                : IntPtr.Zero;
        }
        return (hostBounds, headerBounds, hostHandles, viewHandles, headerHandles);
    }

    internal Rectangle GetPageHostScreenRectangleForTesting() =>
        pageHost.RectangleToScreen(pageHost.ClientRectangle);

    internal void ActivateTabAtIndexForTesting(int index)
    {
        if (index < 0 || index >= tabs.Count) return;
        ActivateTab(tabs[index], focusPage: false);
    }
}
