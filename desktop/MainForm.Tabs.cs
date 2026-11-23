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
    private async Task<BrowserTab?> OpenNewTabAsync(
        string input,
        bool activate = true,
        bool waitForPopupNavigation = false,
        bool canScriptClose = false,
        bool isPinned = false,
        string? trustedExtensionId = null,
        bool trimWhenDiscarded = true,
        bool? startDiscarded = null)
    {
        if (isClosing) return null;
        if (tabs.Count >= MaximumTabs)
        {
            ShowTransientStatus($"Tab limit reached ({MaximumTabs})");
            return null;
        }

        var resolution = ResolveNewTabInput(
            input,
            searchProviderId,
            waitForPopupNavigation,
            IsTrustedExtensionPage(input, trustedExtensionId));
        if (resolution.Error is not null)
        {
            ShowTransientStatus(resolution.Error);
            return null;
        }

        var initialUrl = resolution.IsStartPage ? StartPage.Url : resolution.Url!;
        var createDiscarded = !resolution.IsStartPage
            && !activate
            && !waitForPopupNavigation
            && (startDiscarded ?? true);
        var effectivePinned = isPinned && !resolution.IsStartPage;
        var effectiveKeepAwake = IsKeepAwakeHost(initialUrl);
        var tab = CreateBrowserTab(
            initialUrl,
            canScriptClose,
            startDiscarded: createDiscarded,
            discardedDetail: createDiscarded ? "Waiting in the background" : null,
            isPinned: effectivePinned,
            keepAwake: effectiveKeepAwake);
        tab.DeferResourceFilteringUntilPopupAttached = waitForPopupNavigation;
        tab.AllowedPopupBootstrapUrl = waitForPopupNavigation
            && BrowserPolicy.IsPopupBootstrapUrl(initialUrl)
            ? initialUrl
            : null;
        tab.ExtensionOriginId = trustedExtensionId;
        tabs.Add(tab);
        tabStrip.Controls.Add(tab.Header);
        if (tab.IsPinned) ReorderTabForPinState(tab);
        if (!restoringSession)
        {
            RefreshNativeStartPages();
            ResizeTabHeaders();
            RefreshMemorySweepTimer();
        }

        if (activate) ActivateTab(tab, focusPage: false);

        if (resolution.IsStartPage && !waitForPopupNavigation)
        {
            if (!activate)
            {
                tab.Overlay.Visible = false;
                UpdateTabHeader(tab);
            }
            else if (activeTab == tab)
            {
                PostUi(() =>
                {
                    if (activeTab == tab && !tab.IsClosed) FocusStartPageOrAddressBar();
                });
            }
            return tab;
        }

        if (!activate && !waitForPopupNavigation)
        {
            if (createDiscarded && !restoringSession && trimWhenDiscarded)
            {
                TrimAllProcessMemory(allowManagedGc: true);
            }
            return tab;
        }

        var navigationRequestId = waitForPopupNavigation ? 0 : ++tab.NavigationRequestId;
        var initialized = await InitializeTabAsync(
            tab,
            requireActive: !waitForPopupNavigation);
        if (!initialized || isClosing || tab.IsClosed)
        {
            if (!waitForPopupNavigation)
            {
                PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
            }
            return tab;
        }

        if (!waitForPopupNavigation
            && !CanContinueRequiredTabInitialization(tab))
        {
            PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
        }
        else if (!waitForPopupNavigation && tab.NavigationRequestId == navigationRequestId)
        {
            await EnsureUrlScopedScriptsRegisteredIfNeededAsync(tab, resolution.Url!);
            if (!isClosing
                && !tab.IsClosed
                && tab.NavigationRequestId == navigationRequestId
                && tab.Core is not null)
            {
                NavigateTabCore(tab, resolution.Url!);
            }
        }

        return tab;
    }

    internal static NavigationResolution ResolveNewTabInput(
        string input,
        string searchProviderId,
        bool waitForPopupNavigation,
        bool trustedExtensionPage)
    {
        // NewWindowRequested supplies an un-navigated CoreWebView2 for
        // about:blank/blob bootstrap windows. Preserve that URI instead of
        // translating it into MishaWeb's start page before attachment.
        return trustedExtensionPage
            || (waitForPopupNavigation && BrowserPolicy.IsPopupBootstrapUrl(input))
            ? NavigationResolution.Navigate(input)
            : BrowserPolicy.ResolveAddress(input, searchProviderId);
    }

    private BrowserTab CreateBrowserTab(
        string url,
        bool canScriptClose,
        bool startDiscarded = false,
        string? discardedDetail = null,
        bool isPinned = false,
        bool keepAwake = false)
    {
        var isStartPage = url.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase);
        var initiallyDiscarded = startDiscarded && !isStartPage;
        var host = new TransparentPanelHost
        {
            Dock = DockStyle.None,
            Margin = Padding.Empty,
            Visible = false
        };
        if (pageHost.ClientSize.Width > 0 && pageHost.ClientSize.Height > 0)
        {
            host.Bounds = pageHost.ClientRectangle;
        }

        var overlay = CreateTabOverlay(
            out var overlayCard,
            out var overlayTitle,
            out var overlayHost,
            out var overlayDetail,
            out var overlayAction);
        TrySetBackColor(overlay, Color.Transparent, pageHost.BackColor);
        host.Controls.Add(overlay);
        overlay.BringToFront();
        if (isStartPage)
        {
            overlay.Visible = false;
        }
        pageHost.Controls.Add(host);
        TrySetBackColor(host, Color.Transparent, pageHost.BackColor);

        var header = new TabHeader(toolTip) { IsPrivateMode = isPrivateMode };
        var tab = new BrowserTab(
            host,
            header,
            overlay,
            overlayCard,
            overlayTitle,
            overlayHost,
            overlayDetail,
            overlayAction,
            url)
        {
            CanScriptClose = canScriptClose,
            IsStartPage = isStartPage,
            IsPinned = isPinned && !isStartPage,
            KeepAwake = keepAwake,
            IsDiscarded = initiallyDiscarded,
            Title = isStartPage ? (isPrivateMode ? "Incognito" : "New tab") : HostFromUrl(url),
            StatusText = isStartPage
                ? "Ready"
                : initiallyDiscarded ? "Unloaded to save memory" : "Starting\u2026",
            ConnectionState = isStartPage ? ConnectionState.Local : ConnectionState.Unknown
        };

        overlay.VisibleChanged += (_, _) =>
        {
            if (overlay.Visible && !tab.IsClosed) SyncTabOverlayContext(tab);
        };

        header.Activated += (_, _) => ActivateTab(tab);
        header.AudioToggleRequested += (_, _) => ToggleTabAudioMute(tab);
        header.CloseRequested += (_, _) => CloseTab(tab);
        header.ContextRequested += (_, point) => ShowTabContextMenu(tab, point.ScreenLocation);
        header.DragStarted += (_, e) => BeginTabDrag(tab, e.ScreenLocation);
        header.DragMoved += (_, e) => UpdateTabDrag(e.ScreenLocation);
        header.DragCompleted += (_, e) => CompleteTabDrag(tab, e.ScreenLocation);
        header.DragCanceled += (_, _) => CancelTabDrag();
        header.KeyboardNavigationRequested += (_, e) =>
        {
            ActivateTab(tab, focusPage: false);
            if (e.KeyCode == Keys.Left) CycleTabs(-1, focusPage: false);
            else if (e.KeyCode == Keys.Right) CycleTabs(1, focusPage: false);
            else if (e.KeyCode == Keys.Home) ActivateTabByIndex(0, focusPage: false);
            else if (e.KeyCode == Keys.End) ActivateTabByIndex(tabs.Count - 1, focusPage: false);
            if (activeTab is not null)
            {
                activeTab.Header.Focus();
                tabStrip.ScrollControlIntoView(activeTab.Header);
            }
        };
        void TriggerTabRecovery()
        {
            if (tab.RetryAction is not null) RunUiTask(tab.RetryAction, "Recovery failed");
        }
        overlayAction.Click += (_, _) => TriggerTabRecovery();
        overlay.Click += (_, _) => { if (tab.IsDiscarded) TriggerTabRecovery(); };
        overlayCard.Click += (_, _) => { if (tab.IsDiscarded) TriggerTabRecovery(); };
        foreach (Control child in overlayCard.Controls)
        {
            if (child == overlayAction || child == overlayTitle || child == overlayHost || child == overlayDetail) continue;
            child.Click += (_, _) => { if (tab.IsDiscarded) TriggerTabRecovery(); };
        }
        overlayTitle.Click += (_, _) => { if (tab.IsDiscarded) TriggerTabRecovery(); };
        overlayHost.Click += (_, _) => { if (tab.IsDiscarded) TriggerTabRecovery(); };
        overlayDetail.Click += (_, _) => { if (tab.IsDiscarded) TriggerTabRecovery(); };

        if (initiallyDiscarded)
        {
            tab.NavigationRequestId++;
            var detail = string.IsNullOrWhiteSpace(discardedDetail) ? "Waiting in the background" : discardedDetail;
            ShowTabOverlay(
                tab,
                "Tab unloaded",
                $"{detail}. It will reload when selected.",
                "Reload tab",
                () => RestoreDiscardedTabAsync(tab, focusPage: true));
            SyncTabOverlayContext(tab);
        }
        else if (!isStartPage)
        {
            ShowTabOverlay(tab, "Starting\u2026", "Preparing the browser engine", null, null);
            SyncTabOverlayContext(tab);
        }
        UpdateTabHeader(tab);
        return tab;
    }

    private WebView2 CreateWebView()
    {
        return new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties
            {
                IsInPrivateModeEnabled = isPrivateMode
            },
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Visible = false,
            DefaultBackgroundColor = WebsiteThemePolicy.GetLoadingBackgroundColor(darkModeEnabled),
            AccessibleName = "Web page"
        };
    }

    private void EnsureTabHostLayout(BrowserTab? tab)
    {
        if (tab is null || tab.IsClosed || isClosing) return;
        if (multiViewState.IsMultiViewActive && !isDomFullScreen && multiViewState.ContainsTab(tab.Id))
        {
            ApplyMultiViewLayout();
            return;
        }
        if (pageHost.ClientSize.Width <= 0 || pageHost.ClientSize.Height <= 0) return;

        if (tab.Host.Dock != DockStyle.None)
        {
            tab.Host.Dock = DockStyle.None;
        }
        var targetBounds = pageHost.ClientRectangle;
        if (tab.Host.Bounds != targetBounds)
        {
            tab.Host.Bounds = targetBounds;
            tab.Host.PerformLayout();
        }

        var hostBounds = tab.Host.ClientRectangle;
        if (hostBounds.Width <= 0 || hostBounds.Height <= 0) return;

        if (tab.StartPageView is not null && tab.StartPageView.Visible)
        {
            if (tab.StartPageView.Bounds != hostBounds)
            {
                tab.StartPageView.Bounds = hostBounds;
            }
            tab.StartPageView.PerformLayout();
        }

        if (tab.View is not null && tab.View.Visible && tab.View.Bounds != hostBounds)
        {
            tab.View.Bounds = hostBounds;
        }

        if (tab.Overlay.Visible && tab.Overlay.Bounds != hostBounds)
        {
            tab.Overlay.Bounds = hostBounds;
        }
    }

    private WebView2 EnsureTabView(BrowserTab tab)
    {
        if (tab.View is not null) return tab.View;

        var view = CreateWebView();
        if (tab.Host.ClientSize.Width > 0 && tab.Host.ClientSize.Height > 0)
        {
            view.Bounds = tab.Host.ClientRectangle;
        }
        tab.View = view;
        KeyEventHandler keyUpHandler = (_, e) => HandleMruKeyUp(e);
        KeyEventHandler keyDownHandler = (_, e) =>
        {
            if (e.KeyCode == Keys.N && e.Control && e.Shift)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                OpenPrivateWindow();
            }
        };
        EventHandler gotFocusHandler = (_, _) => OnPaneContentFocused(tab);
        view.KeyUp += keyUpHandler;
        view.KeyDown += keyDownHandler;
        view.GotFocus += gotFocusHandler;
        tab.TrackCoreEventHandler(() =>
        {
            view.KeyUp -= keyUpHandler;
            view.KeyDown -= keyDownHandler;
            view.GotFocus -= gotFocusHandler;
        });
        tab.Host.Controls.Add(view);
        view.SendToBack();
        tab.Overlay.BringToFront();
        if (tab.StartPageView is not null) tab.StartPageView.BringToFront();
        return view;
    }

    private NativeStartPage EnsureNativeStartPage(BrowserTab tab)
    {
        if (tab.StartPageView is not null) return tab.StartPageView;

        var startPageView = new NativeStartPage { Visible = false, Dock = DockStyle.Fill };
        if (tab.Host.ClientSize.Width > 0 && tab.Host.ClientSize.Height > 0)
        {
            startPageView.Bounds = tab.Host.ClientRectangle;
        }
        startPageView.SetSearchProvider(searchProviderId);
        startPageView.SetPrivateMode(isPrivateMode);
        void QueueOmniboxFocus()
        {
            OnPaneContentFocused(tab);
            if (tab.IsClosed || activeTab != tab || isClosing) return;
            PostUi(() =>
            {
                if (!tab.IsClosed && activeTab == tab)
                {
                    RedirectStartPageSearchToAddressBar(tab);
                }
            });
        }
        startPageView.Enter += (_, _) => OnPaneContentFocused(tab);
        startPageView.SearchBar.InputControl.GotFocus += (_, _) => OnPaneContentFocused(tab);
        startPageView.SearchBar.InputControl.Enter += (_, _) => QueueOmniboxFocus();
        startPageView.SearchBar.InputControl.MouseDown += (_, _) => QueueOmniboxFocus();
        startPageView.NavigateRequested += (_, target) =>
        {
            if (tab.IsClosed) return;
            ActivateTab(tab, focusPage: false);
            RunUiTask(() => NavigateActiveAsync(target), "Navigation could not start");
        };
        startPageView.OpenInNewTabRequested += (_, target) =>
        {
            if (tab.IsClosed) return;
            RunUiTask(() => OpenNewTabAsync(target, activate: false), "Could not open link in a new tab");
        };
        startPageView.ActionRequested += (_, action) =>
        {
            if (tab.IsClosed) return;
            ActivateTab(tab, focusPage: false);
            HandleStartPageAction(action, tab.StartPageView?.ResourceModeAnchor);
        };
        startPageView.SuggestionRequested += (_, query) =>
        {
            if (tab.IsClosed || activeTab != tab) return;
            if (query.Length > 0)
            {
                RedirectStartPageSearchToAddressBar(tab, query);
                return;
            }
            HideAddressSuggestions();
        };
        startPageView.SearchKeyDown += (_, e) => StartPageSearchBarOnKeyDown(tab, e);
        startPageView.QuickLinkContextRequested += (_, e) =>
            ShowStartPageLinkContextMenu(e.Link, e.ScreenLocation);
        startPageView.ProviderRequested += (_, _) =>
        {
            ActivateTab(tab, focusPage: false);
            ShowSearchProviderMenu(startPageView.SearchAnchor);
        };
        tab.StartPageView = startPageView;
        tab.Host.Controls.Add(startPageView);
        startPageView.BringToFront();
        return startPageView;
    }

    private static void ReleaseNativeStartPage(BrowserTab tab)
    {
        var startPageView = tab.StartPageView;
        if (startPageView is null) return;

        tab.StartPageView = null;
        tab.Host.Controls.Remove(startPageView);
        startPageView.Dispose();
    }

    private void ReleaseInactiveNativeStartPagesForUltra()
    {
        if (!ultraLightEnabled) return;
        foreach (var tab in tabs.Where(item => item != activeTab && item.IsStartPage && !item.IsClosed))
        {
            ReleaseNativeStartPage(tab);
        }
    }

    private TransparentTableLayoutPanel CreateTabOverlay(
        out RoundedPanel card,
        out Label title,
        out Label hostSubtitle,
        out Label detail,
        out Button action)
    {
        var palette = ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode);
        var overlay = new TransparentTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = new Padding(24),
            AccessibleRole = AccessibleRole.Alert,
            AccessibleName = "Tab status"
        };
        overlay.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        overlay.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        overlay.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        overlay.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        overlay.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        overlay.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        card = new RoundedPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(210, 156),
            MaximumSize = new Size(480, 0),
            Padding = new Padding(28, 22, 28, 24),
            CornerRadius = 18,
            HighContrast = palette.HighContrast,
            FillColor = palette.HighContrast
                ? SystemColors.Window
                : isPrivateMode ? Color.FromArgb(30, 14, 34) : NativeUiTheme.Surface,
            BackColor = palette.HighContrast
                ? SystemColors.Window
                : isPrivateMode ? Color.FromArgb(30, 14, 34) : NativeUiTheme.Surface,
            BorderColor = palette.Border,
            FocusBorderColor = palette.Focus
        };

        var cardLayout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        TrySetBackColor(cardLayout, Color.Transparent, card.FillColor);
        cardLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        var logoHost = new Panel
        {
            Size = new Size(46, 46),
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 8),
            TabStop = false
        };
        TrySetBackColor(logoHost, Color.Transparent, card.FillColor);
        var bunnyMark = new BrandLogoControl
        {
            Dock = DockStyle.Fill,
            PrivateMode = isPrivateMode
        };
        TrySetBackColor(bunnyMark, Color.Transparent, card.FillColor);
        logoHost.Controls.Add(bunnyMark);

        title = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(410, 0),
            Margin = new Padding(0, 2, 0, 4),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = palette.HighContrast ? SystemColors.WindowText : NativeUiTheme.Text,
            Font = OverlayTitleFont
        };
        TrySetBackColor(title, Color.Transparent, card.FillColor);

        hostSubtitle = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(410, 0),
            Margin = new Padding(0, 0, 0, 6),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = palette.HighContrast ? SystemColors.WindowText : NativeUiTheme.Accent,
            Font = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 8.5f),
            Visible = false
        };
        TrySetBackColor(hostSubtitle, Color.Transparent, card.FillColor);

        detail = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(410, 0),
            Margin = new Padding(0, 0, 0, 14),
            TextAlign = ContentAlignment.TopCenter,
            ForeColor = palette.MutedText,
            Font = OverlayDetailFont
        };
        TrySetBackColor(detail, Color.Transparent, card.FillColor);

        action = new PillActionButton
        {
            Size = new Size(168, 38),
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 4, 0, 0),
            BackColor = card.FillColor,
            ForeColor = palette.HighContrast ? SystemColors.HighlightText : NativeUiTheme.AccentText,
            Font = OverlayActionFont,
            Cursor = Cursors.Hand,
            Visible = false,
            AccessibleName = "Tab recovery action"
        };
        ((PillActionButton)action).PrivateMode = isPrivateMode;
        ((PillActionButton)action).HighContrast = palette.HighContrast;

        cardLayout.Controls.Add(logoHost, 0, 0);
        cardLayout.Controls.Add(title, 0, 1);
        cardLayout.Controls.Add(hostSubtitle, 0, 2);
        cardLayout.Controls.Add(detail, 0, 3);
        cardLayout.Controls.Add(action, 0, 4);
        card.Controls.Add(cardLayout);
        overlay.Controls.Add(card, 1, 1);

        var capturedCard = card;
        var capturedTitle = title;
        var capturedHost = hostSubtitle;
        var capturedDetail = detail;
        var capturedAction = action;
        var lastCompactBucket = -1;
        void ApplyResponsiveOverlayMetrics()
        {
            var width = overlay.ClientSize.Width;
            if (width <= 0) return;
            var compact = width < 380;
            var bucket = compact ? Math.Max(180, (width / 16) * 16) : 0;
            if (bucket == lastCompactBucket) return;
            lastCompactBucket = bucket;

            var targetOuterPad = compact ? new Padding(8) : new Padding(24);
            if (overlay.Padding != targetOuterPad) overlay.Padding = targetOuterPad;

            var targetCardPad = compact ? new Padding(14, 14, 14, 16) : new Padding(28, 22, 28, 24);
            if (capturedCard.Padding != targetCardPad) capturedCard.Padding = targetCardPad;

            var targetCardMax = compact
                ? new Size(Math.Max(210, width - 16), 0)
                : new Size(480, 0);
            if (capturedCard.MaximumSize != targetCardMax) capturedCard.MaximumSize = targetCardMax;

            var maxTextWidth = compact ? Math.Max(156, width - 48) : 410;
            var textMaxSize = new Size(maxTextWidth, 0);
            if (capturedTitle.MaximumSize != textMaxSize) capturedTitle.MaximumSize = textMaxSize;
            if (capturedHost.MaximumSize != textMaxSize) capturedHost.MaximumSize = textMaxSize;
            if (capturedDetail.MaximumSize != textMaxSize) capturedDetail.MaximumSize = textMaxSize;

            var targetActionWidth = compact ? Math.Clamp(maxTextWidth, 128, 168) : 168;
            if (capturedAction.Width != targetActionWidth) capturedAction.Width = targetActionWidth;
        }
        overlay.Resize += (_, _) => ApplyResponsiveOverlayMetrics();
        return overlay;
    }

    private void SyncTabOverlayContext(BrowserTab tab)
    {
        var hostText = !tab.IsStartPage && BrowserPolicy.IsHttpUrl(tab.Url)
            ? TextSafety.FormatHostForDisplay(tab.Url, 48)
            : string.Empty;
        if (hostText.Equals("Invalid address", StringComparison.OrdinalIgnoreCase))
        {
            hostText = string.Empty;
        }
        if (!string.Equals(tab.OverlayHost.Text, hostText, StringComparison.Ordinal))
        {
            tab.OverlayHost.Text = hostText;
            tab.OverlayHost.AccessibleName = hostText;
        }
        var showHost = hostText.Length > 0;
        if (tab.OverlayHost.Visible != showHost)
        {
            tab.OverlayHost.Visible = showHost;
        }

        var palette = ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode);
        var isCrashOrError = !tab.IsDiscarded
            && (tab.ConnectionState is ConnectionState.Failed or ConnectionState.CertificateError
                || tab.RetryAction is not null);
        Color targetBorder;
        Color targetHostColor;
        if (palette.HighContrast)
        {
            targetBorder = palette.Border;
            targetHostColor = SystemColors.WindowText;
        }
        else if (isCrashOrError)
        {
            targetBorder = NativeUiTheme.Negative;
            targetHostColor = NativeUiTheme.Negative;
        }
        else if (tab.IsDiscarded)
        {
            targetBorder = isPrivateMode
                ? Color.FromArgb(116, 74, 152)
                : Color.FromArgb(124, 68, 112);
            targetHostColor = NativeUiTheme.Muted;
        }
        else
        {
            targetBorder = palette.Border;
            targetHostColor = NativeUiTheme.Accent;
        }

        if (tab.OverlayCard.BorderColor != targetBorder)
        {
            tab.OverlayCard.BorderColor = targetBorder;
            tab.OverlayCard.Invalidate();
        }
        if (tab.OverlayHost.ForeColor != targetHostColor)
        {
            tab.OverlayHost.ForeColor = targetHostColor;
        }
    }

    private void ReplaceTabView(BrowserTab tab)
    {
        ClearTabHoverStatus(tab);
        StopFindSession(tab);
        CancelPermissionsForTab(tab);
        CaptureTabPresentationState(tab);
        tab.ReaderModeActive = false;
        tab.NavigationRequestId++;
        tab.DocumentNavigationGeneration++;
        tab.InitializationGeneration++;
        tab.MotionPolicyGeneration++;
        tab.AllowedPopupBootstrapUrl = null;
        var oldView = tab.View;
        if (tab.Core is { } oldCore)
        {
            if (tab.NoMotionScriptId is not null)
            {
                try { oldCore.RemoveScriptToExecuteOnDocumentCreated(tab.NoMotionScriptId); }
                catch (Exception error) { stateStore.Log("Could not remove the no-motion page policy", error); }
                tab.NoMotionScriptId = null;
            }
            RemoveAdBlockPageScripts(tab, oldCore);
        }
        RemoveAdBlockFiltering(tab);
        AbandonPendingFrameSetups(tab);
        tab.ReleaseTrackedFrames();
        tab.DetachCoreEventHandlers();
        tab.ReleaseCleanLinkMenuItem();
        tab.ChromeStoreInstallChannel = null;
        tab.ChromeStoreBridgeScriptId = null;
        tab.PendingScopedScriptNavigationUrl = null;
        tab.LastScopedScriptRegistrationUrl = null;
        tab.AppliedAutoDarkOverride = null;
        tab.AppliedPreferredColorSchemeDark = null;
        tab.AppliedWebsiteThemeGeneration = -1;
        tab.InstalledUserScriptKeys.Clear();
        tab.ContextLinkTarget = null;
        tab.PendingFileNavigationUrl = null;
        tab.View = null;
        if (oldView is not null)
        {
            oldView.Visible = false;
            tab.Host.Controls.Remove(oldView);
            oldView.Dispose();
        }
        tab.Overlay.BringToFront();
        tab.IsSuspended = false;
        tab.IsDiscarded = false;
        tab.IsLoading = false;
        tab.IsInitializing = false;
        tab.IsAudible = false;
        tab.MicrophoneAllowedOrigins.Clear();
        tab.CameraAllowedOrigins.Clear();
        tab.PendingMediaPermissionRequests = 0;
        tab.PendingMediaPermissionOrigin = null;
        tab.PendingMediaPermissionRefreshes = 0;
        tab.ConsecutiveSuspendFailures = 0;
        tab.SupportsMemoryUsageTarget = true;
        tab.MemoryUsageTargetLevel = null;
        tab.InitializationTask = null;
        ReleaseBrowserEnvironmentIfIdle();
    }

    private void ReleaseBrowserEnvironmentIfIdle()
    {
        if (recoveringBrowser
            || openingExtensionsManager
            || installingChromeStoreExtensionIds.Count > 0
            || downloads.Any(item => !item.IsTerminal)
            || extensionProfileController is not null
            || environmentTask is { IsCompleted: false }
            || tabs.Any(item => !item.IsClosed
                && (item.View is not null || item.InitializationTask is not null)))
        {
            return;
        }

        InvalidateBrowserEnvironment();
        memoryTimer.Stop();
    }

    private void DisposeExtensionProfileController(CoreWebView2Controller? expectedController = null)
    {
        var controller = extensionProfileController;
        if (expectedController is not null && !ReferenceEquals(controller, expectedController))
        {
            try { expectedController.Close(); }
            catch { }
            return;
        }
        extensionProfileController = null;
        if (controller is null) return;
        try { controller.Close(); }
        catch { }
    }

    private static void CaptureTabPresentationState(BrowserTab tab)
    {
        try
        {
            if (tab.View is not { CoreWebView2: not null } view) return;
            tab.ZoomFactor = view.ZoomFactor;
            tab.IsMuted = tab.TabMuteOverride ?? view.CoreWebView2.IsMuted;
            tab.SavedScrollY = Math.Max(0, tab.SavedScrollY);
        }
        catch
        {
            // A failed browser process may no longer expose controller state.
        }
    }

    private void ShowStartPage(BrowserTab? tab, bool focusAddressBar = true)
    {
        if (tab is null) return;
        HideAddressSuggestions();
        tab.NavigationRequestId++;
        tab.KeepAwake = false;
        tab.MicrophoneAllowedOrigins.Clear();
        tab.CameraAllowedOrigins.Clear();
        tab.FrameOrigins.Clear();
        tab.PendingMediaPermissionRequests = 0;
        tab.PendingMediaPermissionOrigin = null;
        tab.PendingMediaPermissionRefreshes = 0;
        var wasPinned = tab.IsPinned;
        tab.IsPinned = false;
        if (wasPinned) ReorderTabForPinState(tab);
        tab.ReaderModeActive = false;
        tab.ZoomFactor = 1.0;
        tab.IsMuted = false;
        tab.TabMuteOverride = null;
        tab.SavedScrollY = 0;
        tab.PendingRestoreScrollY = 0;

        if (tab.View is not null) ReplaceTabView(tab);
        else
        {
            tab.InitializationGeneration++;
            tab.InitializationTask = null;
            tab.IsInitializing = false;
            ReleaseBrowserEnvironmentIfIdle();
            RefreshMemorySweepTimer();
        }
        tab.ZoomFactor = 1.0;
        tab.IsMuted = false;
        tab.IsStartPage = true;
        ShowNativeStartPage(tab);
        tab.IsDiscarded = false;
        tab.Url = StartPage.Url;
        tab.Title = isPrivateMode ? "Incognito" : "New tab";
        tab.StatusText = "Ready";
        tab.ConnectionState = ConnectionState.Local;
        tab.ResetBlockedStats();
        UpdateTabHeader(tab);
        if (activeTab == tab)
        {
            addressBarEditing = false;
            SyncAddressBar();
            UpdateWindowTitle();
            UpdateNavigationChrome();
            if (focusAddressBar)
            {
                PostUi(() =>
                {
                    if (activeTab == tab && !tab.IsClosed) FocusStartPageOrAddressBar();
                });
            }
        }
    }

    private NativeStartPage PrepareNativeStartPage(BrowserTab tab)
    {
        var startPageView = EnsureNativeStartPage(tab);
        tab.Overlay.Visible = false;
        if (tab.View is not null) tab.View.Visible = false;
        startPageView.SetQuickLinks(BuildStartPageLinks());
        startPageView.SetStatus(BuildStartPageStatus());
        startPageView.SetSearchProvider(searchProviderId);
        startPageView.SetPrivateMode(isPrivateMode);
        return startPageView;
    }

    private void ShowNativeStartPage(BrowserTab tab)
    {
        var startPageView = PrepareNativeStartPage(tab);
        EnsureTabHostLayout(tab);
        startPageView.Visible = true;
        startPageView.BringToFront();
    }

    private IReadOnlyList<StartPageLink> BuildStartPageLinks()
    {
        if (!isStartPageLinksDirty && cachedStartPageLinks is not null) return cachedStartPageLinks;

        const int maximumLinks = BrowserStateStore.MaximumPinnedStartPageLinks;
        var links = new List<StartPageLink>(maximumLinks);
        var seenUrls = new HashSet<string>(BrowserPolicy.UrlComparer);

        void AddLink(string? title, string? url, string category)
        {
            if (links.Count >= maximumLinks
                || string.IsNullOrWhiteSpace(url)
                || !BrowserPolicy.IsHttpUrl(url)
                || !seenUrls.Add(url))
            {
                return;
            }

            var displayTitle = string.IsNullOrWhiteSpace(title) ? HostFromUrl(url) : title.Trim();
            links.Add(new StartPageLink(displayTitle, url, category));
        }

        if (!isPrivateMode)
        {
            foreach (var pinned in state.PinnedStartPageLinks)
            {
                AddLink(pinned.Title, pinned.Url, "Pinned");
                if (links.Count >= maximumLinks) break;
            }
        }

        foreach (var bookmark in state.Bookmarks)
        {
            AddLink(bookmark.Title, bookmark.Url, "Favorite");
            if (links.Count >= maximumLinks) break;
        }

        if (links.Count < maximumLinks)
        {
            var seenRecentHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var history in state.History)
            {
                if (!Uri.TryCreate(history.Url, UriKind.Absolute, out var hostUri)) continue;
                var host = hostUri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? hostUri.Host[4..]
                    : hostUri.Host;
                if (!seenRecentHosts.Add(host)) continue;
                AddLink(history.Title, history.Url, "Recent");
                if (links.Count >= maximumLinks) break;
            }
        }

        if (links.Count < maximumLinks)
        {
            foreach (var history in state.History)
            {
                AddLink(history.Title, history.Url, "Recent");
                if (links.Count >= maximumLinks) break;
            }
        }

        cachedStartPageLinks = links;
        isStartPageLinksDirty = false;
        return links;
    }

    private void InvalidateStartPageLinks()
    {
        isStartPageLinksDirty = true;
        cachedStartPageLinks = null;
    }

    private StartPageStatus BuildStartPageStatus()
    {
        var resourceMode = ultraLightEnabled
            ? "Ultra-light mode"
            : memorySaverEnabled ? "Memory saver on" : "Memory saver off";
        var sleeping = tabs.Count(item => item.IsSuspended || item.IsDiscarded);
        var tabCount = tabs.Count == 1 ? "1 tab open" : $"{tabs.Count} tabs open";
        if (sleeping > 0) tabCount += $" \u00B7 {sleeping} resting";
        return new StartPageStatus(resourceMode, tabCount);
    }

    private void RefreshNativeStartPages(bool refreshLinks = false)
    {
        var status = BuildStartPageStatus();
        var links = refreshLinks ? BuildStartPageLinks() : null;
        foreach (var tab in tabs.Where(item => item.IsStartPage && !item.IsClosed))
        {
            if (tab.StartPageView is null) continue;
            tab.StartPageView.SetPrivateMode(isPrivateMode);
            tab.StartPageView.SetStatus(status);
            if (links is not null) tab.StartPageView.SetQuickLinks(links);
        }
    }

    private void HandleStartPageAction(StartPageAction action, Control? anchor = null)
    {
        switch (action)
        {
            case StartPageAction.CycleResourceMode:
                ShowResourceModeMenu(anchor);
                break;
            case StartPageAction.ShowTabs:
                ShowTabListMenu();
                break;
            case StartPageAction.ManageQuickLinks:
                ShowManageQuickLinksDialog();
                break;
        }
    }

    private void NavigateTabCore(BrowserTab tab, string targetUrl)
    {
        var core = tab.Core ?? throw new InvalidOperationException("The tab browser is not available.");
        SyncWebsiteThemeBeforeNavigation(tab, targetUrl);
        tab.PendingFileNavigationUrl = BrowserPolicy.IsLocalFileUrl(targetUrl) ? targetUrl : null;
        core.Navigate(targetUrl);
    }

    private async Task NavigateActiveAsync(string input)
    {
        var tab = activeTab;
        if (tab is null)
        {
            ShowTransientStatus("Open a tab before navigating");
            return;
        }
        var resolution = BrowserPolicy.ResolveAddress(input, searchProviderId);
        if (resolution.Error is not null)
        {
            ShowTransientStatus(resolution.Error);
            return;
        }

        addressBarEditing = false;
        if (resolution.IsStartPage)
        {
            ShowStartPage(tab);
            return;
        }

        var targetUrl = resolution.Url!;
        var navigationRequestId = ++tab.NavigationRequestId;
        tab.IsDiscarded = false;
        tab.IsStartPage = false;
        tab.Url = targetUrl;
        tab.KeepAwake = IsKeepAwakeHost(targetUrl);
        if (tab.Core is null && !await InitializeTabAsync(tab, requireActive: true))
        {
            PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
            return;
        }
        if (isClosing || tab.IsClosed) return;
        if (!CanContinueRequiredTabInitialization(tab))
        {
            PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
            return;
        }
        if (tab.NavigationRequestId != navigationRequestId
            || tab.IsStartPage
            || !BrowserPolicy.UrlEquals(tab.Url, targetUrl)
            || tab.Core is null) return;

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

        try
        {
            NavigateTabCore(tab, targetUrl);
        }
        catch (Exception error)
        {
            stateStore.Log("Navigation failed to start", error);
            ShowTransientStatus("Navigation could not start");
        }
    }

    private void ActivateTab(
        BrowserTab tab,
        bool focusPage = true,
        bool restoreDiscarded = true)
    {
        if (tab.IsClosed || !tabs.Contains(tab)) return;
        tab.BackgroundReductionGeneration++;
        var previousTab = activeTab != tab ? activeTab : null;
        if (previousTab is not null)
        {
            previousTab.LastActiveUtc = DateTime.UtcNow;
            StopFindSession(previousTab);
        }
        ResumeTab(tab);

        activeTab = tab;
        if (multiViewState.IsMultiViewActive && !isDomFullScreen)
        {
            var existingPane = multiViewState.IndexOfTab(tab.Id);
            if (existingPane >= 0) multiViewState.FocusPane(existingPane);
            else multiViewState.AssignTabToPane(multiViewState.FocusedPaneIndex, tab.Id);
        }
        else
        {
            multiViewState.SetSingle(tab.Id);
        }
        if (!restoringSession) mruTabs.ObserveActivation(tab);
        if (tab.IsStartPage)
        {
            ShowNativeStartPage(tab);
        }
        var preserveBackdropFrame = previousTab?.IsStartPage == true && tab.IsStartPage;
        if (preserveBackdropFrame)
        {
            // Let the destination page take a shared-frame lease before the old
            // page releases its own, avoiding a dispose/re-render cycle.
            tab.Host.Visible = true;
        }
        SyncDomFullScreenFromActiveTab();
        pageHost.SuspendLayout();
        try
        {
            if (multiViewState.IsMultiViewActive && !isDomFullScreen)
            {
                if (previousTab is not null) UpdateTabHeader(previousTab);
                UpdateTabHeader(tab);
                if (tabStrip.AutoScrollMinSize.Width > tabStrip.ClientSize.Width)
                {
                    tabStrip.ScrollControlIntoView(tab.Header);
                }
            }
            else
            {
                if (previousTab is not null)
                {
                    previousTab.Host.Visible = false;
                    if (previousTab.View is not null) previousTab.View.Visible = false;
                    UpdateTabHeader(previousTab);
                }

                if (!preserveBackdropFrame) tab.Host.Visible = true;
                if (tab.View is not null)
                {
                    tab.View.Visible = !isWindowMinimized
                        && !tab.Overlay.Visible
                        && tab.StartPageView?.Visible != true;
                }
                UpdateTabHeader(tab);
                if (tabStrip.AutoScrollMinSize.Width > tabStrip.ClientSize.Width)
                {
                    tabStrip.ScrollControlIntoView(tab.Header);
                }

                tab.Host.BringToFront();
                if (tab.Overlay.Visible)
                {
                    tab.Overlay.BringToFront();
                }
                else if (tab.StartPageView?.Visible == true)
                {
                    tab.StartPageView.BringToFront();
                }
                else
                {
                    tab.View?.BringToFront();
                }

                EnsureTabHostLayout(tab);
            }
        }
        finally
        {
            pageHost.ResumeLayout(true);
            pageHost.PerformLayout();
            if (multiViewState.IsMultiViewActive && !isDomFullScreen) ApplyMultiViewLayout();
            else EnsureTabHostLayout(tab);
        }
        tab.LastActiveUtc = DateTime.UtcNow;
        tab.ConsecutiveSuspendFailures = 0;
        addressBarEditing = false;
        SyncAddressBar();
        UpdateWindowTitle();
        UpdateNavigationChrome();
        UpdateStatus();
        UpdateChromeRowsForFullscreen();
        if (findBar.Visible && findBox.TextLength > 0 && tab.Core is not null)
        {
            RunUiTask(StartFindSession, "Find failed");
        }
        else if (findBar.Visible)
        {
            findMatchLabel.Text = string.Empty;
        }
        ApplyLiveTabMemoryTarget(tab, foreground: !isWindowMinimized);
        RefreshMemorySweepTimer();
        if (previousTab is not null && !IsTabVisibleInAnyPane(previousTab))
        {
            QueueBackgroundTabReduction(previousTab);
        }

        if (restoreDiscarded && tab.IsDiscarded && !tab.IsInitializing)
        {
            RunUiTask(() => RestoreDiscardedTabAsync(tab, focusPage), "Could not reload the unloaded tab");
            return;
        }

        if (focusPage && !tab.IsInitializing && tab.StartPageView?.Visible == true)
        {
            PostUi(() =>
            {
                if (activeTab == tab && !tab.IsClosed) FocusStartPageOrAddressBar();
            });
        }
        else if (focusPage && !tab.IsInitializing && tab.View is not null && tab.Core is not null)
        {
            PostUi(() =>
            {
                if (activeTab == tab && !tab.IsClosed && tab.View is { IsDisposed: false } view) view.Focus();
            });
        }
    }

    private async Task RestoreDiscardedTabAsync(BrowserTab tab, bool focusPage)
    {
        if (!tab.IsDiscarded) return;
        await RestoreTabContentAsync(tab, focusPage);
    }

    private async Task RestoreTabContentAsync(BrowserTab tab, bool focusPage)
    {
        if (isClosing || tab.IsClosed || tab.IsInitializing || (!tab.IsDiscarded && tab.Core is not null)) return;
        if (!CanContinueRequiredTabInitialization(tab)) return;
        if (tab.IsStartPage)
        {
            ShowStartPage(tab, focusPage);
            return;
        }

        var targetUrl = tab.Url;
        var navigationRequestId = ++tab.NavigationRequestId;
        var initialized = await InitializeTabAsync(tab, requireActive: true);
        if (!initialized || isClosing || tab.IsClosed)
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
            || !BrowserPolicy.UrlEquals(tab.Url, targetUrl)
            || tab.Core is null)
        {
            return;
        }
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
        RefreshMemorySweepTimer();
        if (focusPage && activeTab == tab)
        {
            PostUi(() =>
            {
                if (activeTab == tab && !tab.IsClosed && tab.View is { IsDisposed: false } view)
                {
                    view.Focus();
                }
            });
        }
    }

    private void MarkTabDiscarded(BrowserTab tab, string detail, bool trimProcessMemory = true)
    {
        tab.NavigationRequestId++;
        tab.IsDiscarded = true;
        tab.IsSuspended = false;
        tab.IsLoading = false;
        tab.ReaderModeActive = false;
        tab.StatusText = "Unloaded to save memory";
        ShowTabOverlay(
            tab,
            "Tab unloaded",
            $"{detail}. It will reload when selected.",
            "Reload tab",
            () => RestoreDiscardedTabAsync(tab, focusPage: true));
        SyncTabOverlayContext(tab);
        UpdateTabHeader(tab);
        if (!restoringSession)
        {
            RefreshNativeStartPages();
            if (trimProcessMemory) TrimAllProcessMemory(allowManagedGc: true);
            RefreshMemorySweepTimer();
        }
    }

    private void UnloadActiveTab()
    {
        var tab = activeTab;
        if (tab is null)
        {
            ShowTransientStatus("There is no active tab to unload");
            return;
        }
        RunUiTask(() => UnloadTabAsync(tab), "Could not unload the tab");
    }

    private async Task UnloadTabAsync(BrowserTab tab)
    {
        if (tab.IsClosed || !tabs.Contains(tab)) return;
        if (!CanUnloadTab(tab))
        {
            ShowTransientStatus("This tab is still loading or has no page to unload");
            return;
        }

        await CaptureTabScrollOffsetAsync(tab);
        if (tab.IsClosed || !tabs.Contains(tab) || !CanUnloadTab(tab)) return;

        if (activeTab == tab)
        {
            var replacement = mruTabs.Order.FirstOrDefault(item =>
                item != tab && !item.IsClosed && tabs.Contains(item));
            if (replacement is null)
            {
                replacement = tabs.FirstOrDefault(item => item != tab && item.IsStartPage && !item.IsClosed);
            }
            if (replacement is null)
            {
                replacement = await OpenNewTabAsync(StartPage.Url);
            }
            if (replacement is null || replacement == tab) return;
            ActivateTab(replacement, focusPage: false);
        }

        CaptureTabPresentationState(tab);
        ReplaceTabView(tab);
        MarkTabDiscarded(tab, "Released manually");
        ShowTransientStatus($"Unloaded {tab.Title}");
    }

    private static bool CanUnloadTab(BrowserTab tab)
    {
        return !tab.IsClosed
            && !tab.IsStartPage
            && tab.Core is not null
            && !tab.IsInitializing
            && !tab.IsLoading
            && tab.ActiveDownloads == 0
            && !tab.IsAudible
            && !IsBackgroundProtected(tab);
    }

    private void CloseTab(BrowserTab tab, bool remember = true, bool ensureReplacement = true)
    {
        var index = tabs.IndexOf(tab);
        if (index < 0) return;
        if (draggedTab == tab) CancelTabDrag();
        if (!isClosing && tab.ActiveDownloads > 0)
        {
            NativeAlertDialog.ShowInfo(
                this,
                "Download in progress",
                tab.ActiveDownloads == 1
                    ? "This tab has a download in progress. Keep it open until the download finishes or cancel the download first."
                    : $"This tab has {tab.ActiveDownloads} downloads in progress. Keep it open until they finish or cancel them first.");
            ShowDownloadsPopup();
            return;
        }
        var wasActive = activeTab == tab;

        if (remember && !tab.IsStartPage && BrowserPolicy.IsHttpUrl(tab.Url))
        {
            closedTabs.AddFirst(new ClosedTabEntry(tab.Title, tab.Url, DateTimeOffset.UtcNow));
            while (closedTabs.Count > BrowserStateStore.MaximumRecentlyClosed) closedTabs.RemoveLast();
            if (!isPrivateMode) ScheduleStateSave();
        }

        if (activeTabBeforeDrag == tab) activeTabBeforeDrag = null;
        if (wasActive)
        {
            activeTab = null;
            findMatchLabel.Text = string.Empty;
        }
        StopFindSession(tab);
        CancelPermissionsForTab(tab);
        tab.IsClosed = true;
        tabs.RemoveAt(index);
        mruTabs.Remove(tab);
        var wasMultiView = multiViewState.IsMultiViewActive;
        if (multiViewState.ContainsTab(tab.Id))
        {
            var replacement = tabs.FirstOrDefault(candidate =>
                !candidate.IsClosed && !multiViewState.ContainsTab(candidate.Id));
            multiViewState.HandleTabClosed(tab.Id, replacement?.Id);
        }
        tabStrip.Controls.Remove(tab.Header);
        pageHost.Controls.Remove(tab.Host);
        RemoveAdBlockFiltering(tab);
        AbandonPendingFrameSetups(tab);
        tab.Dispose();
        ReleaseBrowserEnvironmentIfIdle();
        RefreshNativeStartPages();
        ResizeTabHeaders();
        RefreshMemorySweepTimer();
        RunUiTask(
            async () =>
            {
                await Task.Delay(350);
                if (isClosing) return;
                TrimAllProcessMemory(allowManagedGc: true);
            },
            "Could not complete post-close memory trimming");

        if (isClosing || replacingWindow) return;
        if (tabs.Count == 0)
        {
            UpdateNavigationChrome();
            if (ensureReplacement)
            {
                RunUiTask(() => OpenNewTabAsync(StartPage.Url), "Could not open a replacement tab");
            }
            return;
        }

        if (multiViewState.IsMultiViewActive)
        {
            if (wasActive || activeTab is null)
            {
                var nextFocused = FindTabById(multiViewState.FocusedTabId) ?? tabs[Math.Min(index, tabs.Count - 1)];
                ActivateTab(nextFocused);
            }
            else
            {
                ApplyMultiViewLayout();
                RefreshLiveTabMemoryTargets();
                ScheduleStateSave();
                UpdateStatus();
            }
        }
        else
        {
            if (wasActive)
            {
                ActivateTab(tabs[Math.Min(index, tabs.Count - 1)]);
            }
            if (wasMultiView)
            {
                ApplyMultiViewLayout();
                RefreshLiveTabMemoryTargets();
                ScheduleStateSave();
            }
            if (!wasActive)
            {
                UpdateStatus();
            }
        }
    }

    private async Task RestoreClosedTabAsync()
    {
        if (closedTabs.First is null)
        {
            ShowTransientStatus("No recently closed tabs");
            return;
        }

        var closed = closedTabs.First.Value;
        await RestoreClosedEntryAsync(closed);
    }

    private void RestoreClosedEntry(ClosedTabEntry closed)
    {
        RunUiTask(
            () => RestoreClosedEntryAsync(closed),
            "Could not restore the tab");
    }

    private async Task RestoreClosedEntryAsync(ClosedTabEntry closed)
    {
        // Remove the entry before yielding so repeated clicks or shortcuts cannot
        // start duplicate restores. Put it back if creating the tab fails.
        if (!closedTabs.Remove(closed)) return;
        if (!isPrivateMode) ScheduleStateSave();

        var restored = false;
        try
        {
            restored = await OpenNewTabAsync(closed.Url) is not null;
        }
        finally
        {
            if (!restored && !closedTabs.Contains(closed))
            {
                closedTabs.AddFirst(closed);
                while (closedTabs.Count > BrowserStateStore.MaximumRecentlyClosed)
                {
                    closedTabs.RemoveLast();
                }
                if (!isPrivateMode) ScheduleStateSave();
            }
        }
    }

    private void ShowTabContextMenu(BrowserTab tab, Point screenLocation)
    {
        ClearAndDisposeMenuItems(tabMenu.Items);
        AddMenuItem(tabMenu, "Reload", "Ctrl+R", () =>
        {
            ActivateTab(tab);
            ReloadOrStop();
        });
        AddMenuItem(tabMenu, "Duplicate", string.Empty, () =>
            RunUiTask(() => OpenNewTabAsync(tab.IsStartPage ? StartPage.Url : tab.Url), "Could not duplicate the tab"));
        var pinTabItem = AddMenuItem(
            tabMenu,
            tab.IsPinned ? "Unpin tab" : "Pin tab",
            string.Empty,
            () => SetTabPinned(tab, !tab.IsPinned));
        pinTabItem.Enabled = !tab.IsStartPage;
        var pinItem = AddMenuItem(
            tabMenu,
            IsPinned(tab.Url) ? "Unpin quick link" : "Pin quick link",
            string.Empty,
            () => SetPinnedStartPageLink(
                new StartPageLink(tab.Title, tab.Url, "Pinned"),
                !IsPinned(tab.Url)));
        pinItem.Enabled = !isPrivateMode && !tab.IsStartPage && BrowserPolicy.IsHttpUrl(tab.Url);
        AddMenuItem(tabMenu, "Reopen closed tab", "Ctrl+Shift+T", () =>
            RunUiTask(RestoreClosedTabAsync, "Could not restore the tab"));
        tabMenu.Items.Add(new ToolStripSeparator());
        var muteTabItem = AddMenuItem(
            tabMenu,
            IsTabMuted(tab) ? "Unmute tab" : "Mute tab",
            tab == activeTab ? "Ctrl+M" : string.Empty,
            () => ToggleTabAudioMute(tab));
        muteTabItem.Enabled = !tab.IsStartPage || IsTabMuted(tab) || tab.IsAudible;
        var muteItem = AddMenuItem(tabMenu, IsTabMuted(tab) ? "Unmute site" : "Mute site", string.Empty, () =>
        {
            ToggleSiteMute(tab);
        });
        muteItem.Enabled = !tab.IsStartPage;
        var keepAwakeItem = AddMenuItem(
            tabMenu,
            "Keep active in background",
            string.Empty,
            () => ToggleTabKeepAwake(tab));
        keepAwakeItem.Checked = tab.KeepAwake;
        keepAwakeItem.Enabled = GetKeepAwakeHost(tab.Url) is not null;
        var pipItem = AddMenuItem(
            tabMenu,
            "Picture-in-Picture",
            string.Empty,
            () => RunUiTask(() => TogglePictureInPictureAsync(tab), "Picture-in-Picture is unavailable"));
        pipItem.Enabled = !tab.IsStartPage && tab.Core is not null;
        var unloadItem = AddMenuItem(
            tabMenu,
            "Unload tab",
            string.Empty,
            () => RunUiTask(() => UnloadTabAsync(tab), "Could not unload the tab"));
        unloadItem.Enabled = CanUnloadTab(tab);
        AddMenuItem(tabMenu, "Close other tabs", string.Empty, () =>
        {
            ActivateTab(tab);
            foreach (var other in tabs.Where(item => item != tab && !item.IsPinned).ToArray())
            {
                CloseTab(other, ensureReplacement: false);
            }
        });
        AddMenuItem(tabMenu, "Close tab", "Ctrl+W", () => CloseTab(tab));
        tabMenu.Show(screenLocation);
    }

    private void SetTabPinned(BrowserTab tab, bool pinned)
    {
        if (tab.IsClosed || !tabs.Contains(tab)) return;
        if (tab.IsStartPage && pinned)
        {
            ShowTransientStatus("Start pages cannot be persistently pinned");
            return;
        }
        if (tab.IsPinned == pinned)
        {
            UpdateTabHeader(tab);
            return;
        }

        tab.IsPinned = pinned;
        ReorderTabForPinState(tab);
        foreach (var item in tabs) UpdateTabHeader(item);
        ResizeTabHeaders();
        if (!isPrivateMode) ScheduleStateSave();
        ShowTransientStatus(pinned ? "Tab pinned" : "Tab unpinned");
    }

    private void ReorderTabForPinState(BrowserTab tab)
    {
        var oldIndex = tabs.IndexOf(tab);
        if (oldIndex < 0) return;
        tabs.RemoveAt(oldIndex);
        var pinnedCount = tabs.Count(item => item.IsPinned);
        var newIndex = pinnedCount;
        tabs.Insert(Math.Clamp(newIndex, 0, tabs.Count), tab);
        if (tabStrip.Controls.Contains(tab.Header))
        {
            tabStrip.Controls.SetChildIndex(tab.Header, newIndex);
        }
    }

    private void ShowStartPageLinkContextMenu(StartPageLink link, Point screenLocation)
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = menuRenderer
        };
        menu.Items.Add("Open", null, (_, _) =>
            RunUiTask(() => NavigateActiveAsync(link.Url), "Navigation could not start"));
        var pinned = IsPinned(link.Url);
        var pinItem = new ToolStripMenuItem(pinned ? "Unpin quick link" : "Pin quick link")
        {
            Enabled = !isPrivateMode
        };
        pinItem.Click += (_, _) => SetPinnedStartPageLink(link, !pinned);
        menu.Items.Add(pinItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(
            isPrivateMode
                ? "Private quick links are not saved"
                : "Pins appear before favorites and recent history") { Enabled = false });
        menu.Closed += (_, _) => menu.Dispose();
        NativeUiTheme.ApplyMenuWindowFrame(menu);
        menu.Show(screenLocation);
    }

    private IReadOnlyList<OpenTabCandidate> BuildOpenTabCandidates()
    {
        if (tabs.Count == 0) return Array.Empty<OpenTabCandidate>();
        var result = new List<OpenTabCandidate>(tabs.Count);
        foreach (var tab in tabs)
        {
            if (tab.IsClosed || tab.IsStartPage || !BrowserPolicy.IsSafeTopLevelUrl(tab.Url)) continue;
            result.Add(new OpenTabCandidate(tab.Title, tab.Url, tab == activeTab));
        }
        return result;
    }

    private async Task CaptureTabScrollOffsetAsync(BrowserTab tab)
    {
        if (isClosing || tab.IsClosed || tab.IsStartPage || tab.Core is not { } core) return;
        try
        {
            var raw = await core.ExecuteScriptAsync(
                "(() => { try { return Math.max(0, Math.round(window.scrollY || document.documentElement?.scrollTop || 0)); } catch (_) { return 0; } })()")
                .WaitAsync(TimeSpan.FromMilliseconds(1_500));
            if (isClosing || tab.IsClosed || !ReferenceEquals(tab.Core, core)) return;
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
                && double.IsFinite(y)
                && y >= 0)
            {
                tab.SavedScrollY = (int)Math.Min(int.MaxValue, Math.Round(y));
            }
        }
        catch
        {
            // Suspending/unloading may race with process shutdown; preserve last known offset.
        }
    }

    private static string? GetFaviconCacheKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || StartPage.IsStartPageUrl(url)) return null;
        if (!BrowserPolicy.IsHttpUrl(url)) return null;
        var host = BrowserPolicy.NormalizeExactHost(HostFromUrl(url));
        return string.IsNullOrWhiteSpace(host) ? null : host;
    }

    private static string? BuildFaviconMonogram(string? url)
    {
        var hostKey = GetFaviconCacheKey(url);
        if (hostKey is null) return null;
        var label = hostKey.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? hostKey[4..]
            : hostKey;
        foreach (var ch in label)
        {
            if (char.IsLetterOrDigit(ch))
            {
                return char.ToUpperInvariant(ch).ToString();
            }
        }
        return "W";
    }

    private Bitmap? GetCachedFaviconCopy(string? hostKey, out int version)
    {
        version = 0;
        if (isClosing || IsDisposed || hostKey is null) return null;
        if (!faviconCacheMap.TryGetValue(hostKey, out var node)) return null;
        faviconCacheOrder.Remove(node);
        faviconCacheOrder.AddLast(node);
        try
        {
            version = node.Value.Version;
            return new Bitmap(node.Value.Icon);
        }
        catch
        {
            version = 0;
            faviconCacheOrder.Remove(node);
            faviconCacheMap.Remove(hostKey);
            return null;
        }
    }

    private bool TryApplyCachedFavicon(BrowserTab tab)
    {
        if (tab.IsClosed || tab.IsStartPage) return false;
        var hostKey = GetFaviconCacheKey(tab.Url);
        if (hostKey is null || !faviconCacheMap.ContainsKey(hostKey)) return false;
        UpdateTabHeader(tab);
        return true;
    }

    private void StoreFaviconInCache(string hostKey, Bitmap favicon16)
    {
        if (isClosing || IsDisposed)
        {
            favicon16.Dispose();
            return;
        }

        if (faviconCacheMap.TryGetValue(hostKey, out var existing))
        {
            faviconCacheOrder.Remove(existing);
            faviconCacheMap.Remove(hostKey);
            try { existing.Value.Icon.Dispose(); }
            catch { }
        }

        while (faviconCacheMap.Count >= MaximumCachedFaviconHosts && faviconCacheOrder.First is { } oldest)
        {
            faviconCacheOrder.RemoveFirst();
            faviconCacheMap.Remove(oldest.Value.Host);
            try { oldest.Value.Icon.Dispose(); }
            catch { }
        }

        if (faviconVersionSequence == int.MaxValue) faviconVersionSequence = 0;
        var version = ++faviconVersionSequence;
        var node = faviconCacheOrder.AddLast((hostKey, favicon16, version));
        faviconCacheMap[hostKey] = node;
    }

    private void ClearFaviconCache()
    {
        foreach (var entry in faviconCacheOrder)
        {
            try { entry.Icon.Dispose(); }
            catch { }
        }
        faviconCacheOrder.Clear();
        faviconCacheMap.Clear();
    }

    private static Bitmap DecodeFavicon16(byte[] imageBytes)
    {
        using var rawStream = new MemoryStream(imageBytes, writable: false);
        using var decoded = Image.FromStream(rawStream, useEmbeddedColorManagement: false, validateImageData: false);
        var cachedIcon = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(cachedIcon);
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawImage(decoded, new Rectangle(0, 0, 16, 16));
            return cachedIcon;
        }
        catch
        {
            cachedIcon.Dispose();
            throw;
        }
    }

    private async Task RefreshTabFaviconAsync(
        BrowserTab tab,
        CoreWebView2 core,
        long documentGeneration)
    {
        if (isClosing || IsDisposed || tab.IsClosed || tab.IsStartPage || !ReferenceEquals(tab.Core, core))
        {
            return;
        }

        string? faviconUri;
        try { faviconUri = core.FaviconUri; }
        catch { return; }

        var hostKey = GetFaviconCacheKey(tab.Url);
        if (string.IsNullOrWhiteSpace(faviconUri) || hostKey is null)
        {
            UpdateTabHeader(tab);
            return;
        }

        try
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (isClosing
                || IsDisposed
                || tab.IsClosed
                || tab.IsStartPage
                || !ReferenceEquals(tab.Core, core)
                || tab.DocumentNavigationGeneration != documentGeneration
                || stream is null)
            {
                return;
            }

            const int maximumFaviconBytes = 512 * 1024;
            var initialCapacity = stream.CanSeek && stream.Length > 0 && stream.Length <= maximumFaviconBytes
                ? (int)stream.Length
                : 4096;
            using var buffer = new MemoryStream(initialCapacity);
            var chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                var totalRead = 0;
                while (true)
                {
                    var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length));
                    if (read == 0) break;
                    totalRead += read;
                    if (totalRead > maximumFaviconBytes) return;
                    buffer.Write(chunk, 0, read);
                }
                if (totalRead == 0) return;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
            }

            if (isClosing
                || IsDisposed
                || tab.IsClosed
                || tab.IsStartPage
                || !ReferenceEquals(tab.Core, core)
                || tab.DocumentNavigationGeneration != documentGeneration)
            {
                return;
            }

            var imageBytes = buffer.ToArray();
            var cachedIcon = await Task.Run(() => DecodeFavicon16(imageBytes));
            if (isClosing
                || IsDisposed
                || tab.IsClosed
                || tab.IsStartPage
                || !ReferenceEquals(tab.Core, core)
                || tab.DocumentNavigationGeneration != documentGeneration)
            {
                cachedIcon.Dispose();
                return;
            }
            StoreFaviconInCache(hostKey, cachedIcon);

            foreach (var openTab in tabs.Where(item =>
                         !item.IsClosed
                         && !item.IsStartPage
                         && string.Equals(GetFaviconCacheKey(item.Url), hostKey, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                UpdateTabHeader(openTab);
            }
        }
        catch
        {
            // Broken/empty site favicons fall back cleanly to the crisp tinted host monogram.
        }
    }

    private BrowserTab? activeTabBeforeDrag;
    private string? lastTabDragGhostLabel;
    private bool lastTabDragGhostHighContrast;

    private void BeginTabDrag(BrowserTab tab, Point screenLocation)
    {
        if (isClosing || tab.IsClosed || !tabs.Contains(tab)) return;

        activeTabBeforeDrag = activeTab;
        draggedTab = tab;
        tabDragDropIndex = -1;
        tabDragIndicatorIndex = -1;
        tabDragScreenLocation = screenLocation;
        tab.Header.SetDragging(true);
        UpdateTabDragGhostLabel(tab.Title);
        if (!multiViewState.IsMultiViewActive || multiViewState.ContainsTab(tab.Id))
        {
            ActivateTab(tab, focusPage: false, restoreDiscarded: false);
        }
        UpdateTabDrag(screenLocation);
    }

    private void UpdateTabDragGhostLabel(string label)
    {
        var hc = IsHighContrastActive;
        if (string.Equals(lastTabDragGhostLabel, label, StringComparison.Ordinal)
            && lastTabDragGhostHighContrast == hc)
        {
            return;
        }
        lastTabDragGhostLabel = label;
        lastTabDragGhostHighContrast = hc;
        tabDragGhost.SetState(label, hc);
    }

    private void UpdateTabDrag(Point screenLocation)
    {
        var tab = draggedTab;
        if (tab is null || tab.IsClosed || !tabs.Contains(tab))
        {
            CancelTabDrag();
            return;
        }

        tabDragScreenLocation = screenLocation;
        PositionTabDragGhost(tab, screenLocation);
        var rawIndex = GetTabDropIndex(screenLocation);
        if (rawIndex < 0)
        {
            tabDragDropIndex = -1;
            tabDragIndicatorIndex = -1;
            tabDropIndicator.Visible = false;
            return;
        }

        tabDragIndicatorIndex = rawIndex;
        tabDragDropIndex = GetFinalTabDropIndex(tab, rawIndex);
        PositionTabDropIndicator(rawIndex);
    }

    private void CompleteTabDrag(BrowserTab tab, Point screenLocation)
    {
        if (draggedTab != tab) return;

        UpdateTabDrag(screenLocation);
        var targetIndex = tabDragDropIndex;
        var previousActive = activeTabBeforeDrag;
        CancelTabDrag();
        if (targetIndex >= 0)
        {
            MoveTabToIndex(tab, targetIndex);
        }
        else
        {
            activeTabBeforeDrag = previousActive;
            try
            {
                TryDropTabOntoMultiViewPane(tab, screenLocation);
            }
            finally
            {
                activeTabBeforeDrag = null;
            }
        }
    }

    private void CancelTabDrag()
    {
        draggedTab?.Header.SetDragging(false);
        draggedTab = null;
        activeTabBeforeDrag = null;
        lastTabDragGhostLabel = null;
        tabDragDropIndex = -1;
        tabDragIndicatorIndex = -1;
        tabDropIndicator.Visible = false;
        tabDragGhost.Visible = false;
    }

    private int GetTabDropIndex(Point screenLocation)
    {
        if (tabs.Count == 0) return -1;

        var areaPoint = tabArea.PointToClient(screenLocation);
        if (!tabArea.ClientRectangle.Contains(areaPoint)
            || newTabButton.Bounds.Contains(areaPoint))
        {
            return -1;
        }

        var stripPoint = tabStrip.PointToClient(screenLocation);
        if (stripPoint.Y < 0 || stripPoint.Y > tabStrip.ClientSize.Height) return -1;

        var rawIndex = tabs.Count;
        for (var index = 0; index < tabs.Count; index++)
        {
            var header = tabs[index].Header;
            if (stripPoint.X < header.Left + (header.Width / 2))
            {
                rawIndex = index;
                break;
            }
        }

        var tab = draggedTab;
        if (tab is null) return rawIndex;

        var pinnedCount = tabs.Count(item => item.IsPinned);
        var minimumIndex = tab.IsPinned ? 0 : pinnedCount;
        var maximumIndex = tab.IsPinned ? pinnedCount : tabs.Count;
        return Math.Clamp(rawIndex, minimumIndex, maximumIndex);
    }

    private int GetFinalTabDropIndex(BrowserTab tab, int rawIndex)
    {
        var oldIndex = tabs.IndexOf(tab);
        if (oldIndex < 0) return -1;

        var pinnedCount = tabs.Count(item => item.IsPinned);
        var minimumIndex = tab.IsPinned ? 0 : pinnedCount;
        var maximumIndex = tab.IsPinned ? pinnedCount : tabs.Count;
        var constrainedIndex = Math.Clamp(rawIndex, minimumIndex, maximumIndex);
        var finalIndex = constrainedIndex > oldIndex ? constrainedIndex - 1 : constrainedIndex;
        return Math.Clamp(finalIndex, 0, tabs.Count - 1);
    }

    private bool MoveTabToIndex(BrowserTab tab, int targetIndex)
    {
        var oldIndex = tabs.IndexOf(tab);
        if (oldIndex < 0) return false;

        targetIndex = Math.Clamp(targetIndex, 0, tabs.Count - 1);
        if (oldIndex == targetIndex) return false;

        tabs.RemoveAt(oldIndex);
        tabs.Insert(targetIndex, tab);
        if (tabStrip.Controls.Contains(tab.Header))
        {
            tabStrip.Controls.SetChildIndex(tab.Header, targetIndex);
        }
        ResizeTabHeaders();
        if (!isPrivateMode) ScheduleStateSave();
        return true;
    }

    private void PositionTabDropIndicator(int rawIndex)
    {
        if (rawIndex < 0 || tabs.Count == 0)
        {
            tabDropIndicator.Visible = false;
            return;
        }

        rawIndex = Math.Clamp(rawIndex, 0, tabs.Count);
        var stripX = rawIndex == tabs.Count
            ? tabs[^1].Header.Right
            : tabs[rawIndex].Header.Left;
        var indicatorWidth = Math.Max(2, ScaleChromeLogical(2));
        var indicatorX = tabStrip.Left + stripX - (indicatorWidth / 2);
        var indicatorTop = tabStrip.Top + ScaleChromeLogical(3);
        var indicatorHeight = Math.Max(
            ScaleChromeLogical(16),
            tabStrip.ClientSize.Height - ScaleChromeLogical(6));
        var maximumX = Math.Max(0, tabArea.ClientSize.Width - indicatorWidth);
        tabDropIndicator.SetBounds(
            Math.Clamp(indicatorX, 0, maximumX),
            indicatorTop,
            indicatorWidth,
            indicatorHeight);
        tabDropIndicator.Visible = true;
        tabDropIndicator.BringToFront();
    }

    private void PositionTabDragGhost(BrowserTab tab, Point screenLocation)
    {
        var areaPoint = tabArea.PointToClient(screenLocation);
        var verticalAllowance = ScaleChromeLogical(24);
        var inTabStripBand = areaPoint.Y >= -verticalAllowance
            && areaPoint.Y <= tabArea.ClientSize.Height + verticalAllowance;
        string? dropActionLabel = null;
        if (!inTabStripBand)
        {
            dropActionLabel = ResolveMultiViewDropActionLabel(tab, screenLocation);
            if (dropActionLabel is null)
            {
                tabDragGhost.Visible = false;
                return;
            }
        }

        UpdateTabDragGhostLabel(dropActionLabel ?? tab.Title);
        var minimumGhostWidth = dropActionLabel is not null
            ? ScaleChromeLogical(176)
            : ScaleChromeLogical(72);
        var ghostWidth = Math.Min(
            Math.Max(1, tabArea.ClientSize.Width),
            Math.Max(minimumGhostWidth, tab.Header.Width));
        var ghostHeight = Math.Max(ScaleChromeLogical(24), tab.Header.Height);
        var maximumX = Math.Max(0, tabArea.ClientSize.Width - ghostWidth);
        var ghostX = Math.Clamp(areaPoint.X - (ghostWidth / 2), 0, maximumX);
        var maximumY = Math.Max(0, tabArea.ClientSize.Height - ghostHeight);
        var ghostY = Math.Clamp(tabStrip.Top + tab.Header.Top, 0, maximumY);
        tabDragGhost.SetBounds(ghostX, ghostY, ghostWidth, ghostHeight);
        if (!tabDragGhost.Visible) tabDragGhost.Visible = true;
        tabDragGhost.BringToFront();
        if (tabDropIndicator.Visible) tabDropIndicator.BringToFront();
    }

    private void UpdateTabHeader(BrowserTab tab)
    {
        tab.Header.HighContrast = IsHighContrastActive;
        tab.Header.IsPrivateMode = isPrivateMode;
        var isWebTab = !tab.IsStartPage && BrowserPolicy.IsHttpUrl(tab.Url);
        var hostKey = isWebTab ? GetFaviconCacheKey(tab.Url) : null;
        var monogram = isWebTab ? BuildFaviconMonogram(tab.Url) : null;
        if (!isWebTab)
        {
            tab.LoadedFaviconHostKey = null;
            tab.LoadedFaviconVersion = 0;
            tab.Header.SetFavicon(null, null, webTab: false);
        }
        else if (hostKey is not null && faviconCacheMap.TryGetValue(hostKey, out var cachedNode))
        {
            if (!string.Equals(tab.LoadedFaviconHostKey, hostKey, StringComparison.OrdinalIgnoreCase)
                || tab.LoadedFaviconVersion != cachedNode.Value.Version)
            {
                var copy = GetCachedFaviconCopy(hostKey, out var cacheVersion);
                if (copy is not null)
                {
                    try
                    {
                        tab.Header.SetFavicon(copy, monogram, webTab: true);
                        tab.LoadedFaviconHostKey = hostKey;
                        tab.LoadedFaviconVersion = cacheVersion;
                    }
                    catch
                    {
                        copy.Dispose();
                        throw;
                    }
                }
                else
                {
                    tab.LoadedFaviconHostKey = null;
                    tab.LoadedFaviconVersion = 0;
                    tab.Header.SetFavicon(null, monogram, webTab: true);
                }
            }
            else
            {
                tab.Header.SetFaviconMetadata(monogram, webTab: true);
            }
        }
        else
        {
            tab.LoadedFaviconHostKey = null;
            tab.LoadedFaviconVersion = 0;
            tab.Header.SetFavicon(null, monogram, webTab: true);
        }

        tab.Header.SetState(
            tab.Title,
            activeTab == tab,
            tab.IsSuspended,
            tab.IsLoading || tab.IsInitializing,
            tab.IsDiscarded,
            IsBackgroundProtected(tab),
            tab.IsAudible,
            IsTabMuted(tab),
            tab.IsPinned);
        if (multiViewState.IsMultiViewActive && multiViewState.ContainsTab(tab.Id))
        {
            RefreshMultiViewHeaders();
        }
    }

    private bool IsTabMuted(BrowserTab tab)
    {
        return tab.TabMuteOverride
            ?? (tab.IsMuted || (!isPrivateMode && SitePreferencePolicy.ShouldMuteAudio(state.AudioOutputPolicy, state.AllowedAudioHosts, state.MutedHosts, tab.Url)));
    }

    private void ResizeTabHeaders()
    {
        var newTabWidth = ScaleChromeLogical(28);
        var windowDragReserveWidth = ScaleChromeLogical(WindowDragReserveLogicalWidth);
        var availableWidth = Math.Max(
            0,
            tabArea.ClientSize.Width - newTabWidth - windowDragReserveWidth);
        var stripHeight = Math.Max(ScaleChromeLogical(24), tabArea.ClientSize.Height);
        if (tabs.Count == 0 || availableWidth <= 0)
        {
            tabStrip.SetBounds(0, 0, 0, stripHeight);
            newTabButton.SetBounds(0, 0, newTabWidth, stripHeight);
            newTabButton.BringToFront();
            tabStrip.AutoScrollMinSize = Size.Empty;
            tabStrip.AutoScrollPosition = Point.Empty;
            return;
        }
        var minimumTabWidth = ScaleChromeLogical(84);
        var contentAvailable = Math.Max(minimumTabWidth, availableWidth - tabStrip.Padding.Horizontal);
        var width = Math.Clamp(
            (contentAvailable / tabs.Count) - ScaleChromeLogical(4),
            minimumTabWidth,
            ScaleChromeLogical(220));
        var contentWidth = tabStrip.Padding.Horizontal;
        var isMaximized = WindowState == FormWindowState.Maximized;
        tabStrip.SuspendLayout();
        try
        {
            foreach (var tab in tabs)
            {
                var headerWidth = tab.IsPinned
                    ? Math.Clamp(
                        Math.Min(width, ScaleChromeLogical(124)),
                        ScaleChromeLogical(92),
                        ScaleChromeLogical(124))
                    : width;
                tab.Header.Width = headerWidth;
                tab.Header.Height = stripHeight;
                tab.Header.SetMaximized(isMaximized);
                contentWidth += headerWidth + tab.Header.Margin.Horizontal;
            }
            var stripWidth = Math.Min(contentWidth, availableWidth);
            var overflowing = contentWidth > availableWidth;
            tabStrip.AutoScrollMinSize = overflowing
                ? new Size(contentWidth, 0)
                : Size.Empty;
            if (!overflowing) tabStrip.AutoScrollPosition = Point.Empty;
            // Keep the native horizontal bar clipped just below the custom title
            // row. Wheel scrolling and active-tab reveal remain available without
            // adding a heavyweight second chrome row.
            var stripControlHeight = stripHeight
                + (overflowing ? SystemInformation.HorizontalScrollBarHeight : 0);
            tabStrip.SetBounds(0, 0, stripWidth, stripControlHeight);
            newTabButton.SetBounds(stripWidth, 0, newTabWidth, stripHeight);
            newTabButton.BringToFront();
            tabStrip.AccessibleDescription = overflowing
                ? "Open tabs. Use the mouse wheel or keyboard shortcuts to reach tabs outside the visible strip."
                : "Open tabs";
            if (overflowing && activeTab?.Header.Parent == tabStrip)
            {
                tabStrip.ScrollControlIntoView(activeTab.Header);
            }
        }
        finally
        {
            tabStrip.ResumeLayout(true);
        }
        if (draggedTab is not null)
        {
            PositionTabDragGhost(draggedTab, tabDragScreenLocation);
            if (tabDragIndicatorIndex >= 0) PositionTabDropIndicator(tabDragIndicatorIndex);
        }
    }

    private void ScrollTabStrip(int wheelDelta)
    {
        var maximum = Math.Max(0, tabStrip.AutoScrollMinSize.Width - tabStrip.ClientSize.Width);
        if (wheelDelta == 0 || maximum == 0) return;

        var current = Math.Clamp(-tabStrip.AutoScrollPosition.X, 0, maximum);
        var direction = wheelDelta > 0 ? -1 : 1;
        var next = Math.Clamp(current + direction * ScaleChromeLogical(120), 0, maximum);
        tabStrip.AutoScrollPosition = new Point(next, 0);
    }

}
