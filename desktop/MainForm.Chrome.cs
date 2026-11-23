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
    private int cachedResizeFrameDpi;
    private int cachedResizeFrameX;
    private int cachedResizeFrameY;
    private long lastUserInteractionTick = Environment.TickCount64;
    private TableLayoutPanel? omniboxLayout;
    private (string Primary, bool IsHoverPrimary, int Blocked, int ActiveDownloads, int Sleeping, int Unloaded) lastStatusTuple;
    private bool hasStatusTuple;
    private bool syncingAddressBar;

    internal bool IsPrivateWindow => isPrivateMode;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyBorderlessResizableStyle(Handle);
        var palette = ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode);
        NativeUiTheme.ApplyWindowFrame(
            this,
            palette.ChromeSurface,
            palette.WindowFrameBorder,
            palette.WindowFrameText);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyChromeDpiMetrics();
        UpdateChromeRowsForFullscreen();
        UpdateResponsiveToolbar();
        UpdateFindBarLayout();
        ResizeTabHeaders();
        if (addressSuggestionPopup.Visible)
        {
            PositionAddressSuggestions();
        }
        omniboxPanel.Invalidate();
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        ApplyChromeColorPolicy();
    }

    protected override void WndProc(ref Message message)
    {
        TrackPotentialUserInteraction(message.Msg);
        if (message.Msg == NativeMethods.WmNcActivate && WindowState != FormWindowState.Minimized)
        {
            // Keep DefWindowProc's activation bookkeeping without letting it paint
            // a native inactive caption over the app-owned title bar.
            message.LParam = new IntPtr(-1);
        }

        if (message.Msg == NativeMethods.WmEnterSizeMove)
        {
            isInSizeMove = true;
            pendingPlacementSaveAfterSizeMove = false;
        }
        else if (message.Msg == NativeMethods.WmExitSizeMove)
        {
            isInSizeMove = false;
            if (pendingPlacementSaveAfterSizeMove)
            {
                pendingPlacementSaveAfterSizeMove = false;
                TrackWindowPlacementChange();
                ScheduleStateSave();
            }
        }

        if (message.Msg == NativeMethods.WmNcCalcSize && message.WParam != IntPtr.Zero)
        {
            message.Result = IntPtr.Zero;
            return;
        }

        if (message.Msg == NativeMethods.WmGetMinMaxInfo)
        {
            base.WndProc(ref message);
            if (!isFullScreen) UpdateMaximizedBounds(message.LParam);
            return;
        }

        if (message.Msg == NativeMethods.WmNcMouseMove)
        {
            var hovered = message.WParam.ToInt64() == NativeMethods.HtMaxButton;
            SetMaximizeNonClientState(hovered, maximizeNonClientPressed && hovered);
            if (hovered) NativeMethods.TrackNonClientMouseLeave(Handle);
        }
        else if (message.Msg == NativeMethods.WmNcMouseLeave && !maximizeNonClientPressed)
        {
            SetMaximizeNonClientState(false, false);
        }

        if (message.Msg == NativeMethods.WmNcLeftButtonDown
            && message.WParam.ToInt64() == NativeMethods.HtMaxButton)
        {
            maximizeNonClientPressed = true;
            SetMaximizeNonClientState(true, true);
            NativeMethods.CaptureMouse(Handle);
            message.Result = IntPtr.Zero;
            return;
        }

        if (maximizeNonClientPressed && message.Msg == NativeMethods.WmMouseMove)
        {
            var screenPoint = PointToScreen(NativeMethods.PointFromLParam(message.LParam));
            var hovered = IsPointInsideControl(maximizeButton, screenPoint);
            SetMaximizeNonClientState(hovered, hovered);
            message.Result = IntPtr.Zero;
            return;
        }

        if (maximizeNonClientPressed
            && message.Msg is NativeMethods.WmLeftButtonUp or NativeMethods.WmNcLeftButtonUp)
        {
            var point = NativeMethods.PointFromLParam(message.LParam);
            var screenPoint = message.Msg == NativeMethods.WmLeftButtonUp ? PointToScreen(point) : point;
            var shouldToggle = IsPointInsideControl(maximizeButton, screenPoint);
            NativeMethods.ReleaseMouseCapture();
            maximizeNonClientPressed = false;
            SetMaximizeNonClientState(false, false);
            if (shouldToggle) ToggleMaximizeRestore();
            message.Result = IntPtr.Zero;
            return;
        }

        if (message.Msg == NativeMethods.WmCaptureChanged && maximizeNonClientPressed)
        {
            maximizeNonClientPressed = false;
            SetMaximizeNonClientState(false, false);
        }

        if (message.Msg == NativeMethods.WmNcHitTest)
        {
            if (isFullScreen || isDomFullScreen)
            {
                message.Result = (IntPtr)NativeMethods.HtClient;
                return;
            }

            var screenPoint = NativeMethods.PointFromLParam(message.LParam);
            var clientPoint = PointToClient(screenPoint);
            if (WindowState == FormWindowState.Normal)
            {
                var resizeHit = GetResizeHitTest(clientPoint);
                if (resizeHit != NativeMethods.HtClient)
                {
                    message.Result = (IntPtr)resizeHit;
                    return;
                }
            }

            if (IsClientPointInsideControl(maximizeButton, clientPoint))
            {
                message.Result = (IntPtr)NativeMethods.HtMaxButton;
                return;
            }

            if (IsCaptionPoint(clientPoint, screenPoint))
            {
                message.Result = (IntPtr)NativeMethods.HtCaption;
                return;
            }

            message.Result = (IntPtr)NativeMethods.HtClient;
            return;
        }

        base.WndProc(ref message);
    }

    private void TrackPotentialUserInteraction(int msg)
    {
        if (msg is NativeMethods.WmMouseMove or NativeMethods.WmNcMouseMove
            or NativeMethods.WmNcLeftButtonDown or NativeMethods.WmNcLeftButtonUp
            or NativeMethods.WmLeftButtonUp
            or 0x0201 /* WM_LBUTTONDOWN */ or 0x0204 /* WM_RBUTTONDOWN */ or 0x0205 /* WM_RBUTTONUP */
            or 0x020A /* WM_MOUSEWHEEL */
            or 0x0100 /* WM_KEYDOWN */ or 0x0104 /* WM_SYSKEYDOWN */
            or 0x0006 /* WM_ACTIVATE */)
        {
            NoteUserInteraction();
        }
    }

    private void NoteUserInteraction()
    {
        var nowTick = Environment.TickCount64;
        if (isAppIdleLowPowerActive)
        {
            lastUserInteractionTick = nowTick;
            lastUserInteractionUtc = DateTime.UtcNow;
            isAppIdleLowPowerActive = false;
            if (!isWindowMinimized)
            {
                RefreshLiveTabMemoryTargets();
            }
            return;
        }

        if (nowTick - lastUserInteractionTick < 1_000) return;
        lastUserInteractionTick = nowTick;
        lastUserInteractionUtc = DateTime.UtcNow;
    }

    private void CheckApplicationInactivity()
    {
        if (isClosing || isWindowMinimized || activeTab is null) return;
        var threshold = TimeSpan.FromMinutes(AppIdleThresholdMinutes);
        if (IsHandleCreated
            && NativeMethods.TryGetLastInputIdleTime(out var osIdleDuration)
            && osIdleDuration < threshold
            && NativeMethods.IsForegroundWindowOwnedOrChild(Handle))
        {
            var observedInteractionUtc = DateTime.UtcNow - osIdleDuration;
            if (observedInteractionUtc > lastUserInteractionUtc)
            {
                lastUserInteractionUtc = observedInteractionUtc;
                lastUserInteractionTick = Environment.TickCount64;
            }
            if (isAppIdleLowPowerActive)
            {
                isAppIdleLowPowerActive = false;
                RefreshLiveTabMemoryTargets();
            }
            return;
        }
        var idleDuration = DateTime.UtcNow - lastUserInteractionUtc;
        if (idleDuration >= threshold && !isAppIdleLowPowerActive)
        {
            isAppIdleLowPowerActive = true;
            RefreshLiveTabMemoryTargets();
            for (var index = 0; index < tabs.Count; index++)
            {
                var tab = tabs[index];
                if (!tab.IsClosed)
                {
                    ApplyLiveTabMemoryTarget(tab, foreground: false);
                }
            }
            TrimAllProcessMemory();
        }
    }

    private int GetResizeHitTest(Point point)
    {
        var dpi = DeviceDpi;
        if (cachedResizeFrameDpi != dpi)
        {
            cachedResizeFrameX = NativeMethods.GetResizeFrameThickness(dpi, horizontal: true);
            cachedResizeFrameY = NativeMethods.GetResizeFrameThickness(dpi, horizontal: false);
            cachedResizeFrameDpi = dpi;
        }
        var frameX = cachedResizeFrameX;
        var frameY = cachedResizeFrameY;
        var left = point.X >= 0 && point.X < frameX;
        var right = point.X <= ClientSize.Width && point.X >= ClientSize.Width - frameX;
        var top = point.Y >= 0 && point.Y < frameY;
        var bottom = point.Y <= ClientSize.Height && point.Y >= ClientSize.Height - frameY;

        if (top && left) return NativeMethods.HtTopLeft;
        if (top && right) return NativeMethods.HtTopRight;
        if (bottom && left) return NativeMethods.HtBottomLeft;
        if (bottom && right) return NativeMethods.HtBottomRight;
        if (left) return NativeMethods.HtLeft;
        if (right) return NativeMethods.HtRight;
        if (top) return NativeMethods.HtTop;
        if (bottom) return NativeMethods.HtBottom;
        return NativeMethods.HtClient;
    }

    private bool IsCaptionPoint(Point clientPoint, Point screenPoint)
    {
        _ = screenPoint;
        if (!TryGetControlOriginInFormClient(titleBar, out _, out var titleBarTop)) return false;
        if (clientPoint.Y < 0 || clientPoint.Y >= titleBarTop + titleBar.Height) return false;
        if (IsClientPointInsideControl(newTabButton, clientPoint)
            || IsClientPointInsideControl(tabListButton, clientPoint)
            || (incognitoBadge is not null && IsClientPointInsideControl(incognitoBadge, clientPoint))
            || IsClientPointInsideControl(minimizeButton, clientPoint)
            || IsClientPointInsideControl(maximizeButton, clientPoint)
            || IsClientPointInsideControl(closeWindowButton, clientPoint))
        {
            return false;
        }

        if (TryGetControlOriginInFormClient(tabStrip, out var tabStripLeft, out var tabStripTop))
        {
            var localX = clientPoint.X - tabStripLeft;
            var localY = clientPoint.Y - tabStripTop;
            if (localX >= 0 && localX < tabStrip.ClientSize.Width
                && localY >= 0 && localY < tabStrip.ClientSize.Height)
            {
                // Keep the actual tab headers in the client area so their mouse
                // events can start a reorder drag. Only the unused strip space
                // should behave like a draggable window caption.
                for (var index = 0; index < tabs.Count; index++)
                {
                    var header = tabs[index].Header;
                    if (!header.Visible) continue;
                    if (localX >= header.Left && localX < header.Right
                        && localY >= header.Top && localY < header.Bottom)
                    {
                        return false;
                    }
                }
                return true;
            }
        }
        return true;
    }

    private bool TryGetControlOriginInFormClient(Control? control, out int originX, out int originY)
    {
        originX = 0;
        originY = 0;
        if (control is null || !control.Visible) return false;

        Control? current = control;
        while (current is not null && !ReferenceEquals(current, this))
        {
            if (!current.Visible) return false;
            originX += current.Left;
            originY += current.Top;
            current = current.Parent;
        }
        return ReferenceEquals(current, this);
    }

    private bool IsClientPointInsideControl(Control? control, Point formClientPoint)
    {
        if (control is null || !TryGetControlOriginInFormClient(control, out var originX, out var originY))
        {
            return false;
        }
        var localX = formClientPoint.X - originX;
        var localY = formClientPoint.Y - originY;
        return localX >= 0 && localX < control.Width
            && localY >= 0 && localY < control.Height;
    }

    private void BeginWindowDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || isClosing || isFullScreen || isDomFullScreen) return;

        var source = sender as Control ?? this;
        var screenPoint = source.PointToScreen(e.Location);
        if (!IsCaptionPoint(PointToClient(screenPoint), screenPoint)) return;

        if (e.Clicks > 1)
        {
            ToggleMaximizeRestore();
            return;
        }

        NativeMethods.BeginWindowMove(Handle);
    }

    private static bool IsPointInsideControl(Control control, Point screenPoint)
    {
        return control.Visible && control.RectangleToScreen(control.ClientRectangle).Contains(screenPoint);
    }

    private void SetMaximizeNonClientState(bool hovered, bool pressed)
    {
        maximizeButton.SetNonClientState(hovered, pressed);
    }

    private void UpdateMaximizedBounds(IntPtr minMaxInfoPointer)
    {
        var monitor = NativeMethods.MonitorFromWindow(Handle, NativeMethods.MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var monitorInfo = new NativeMethods.MonitorInfo();
        monitorInfo.Size = Marshal.SizeOf<NativeMethods.MonitorInfo>();
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo)) return;

        var info = Marshal.PtrToStructure<NativeMethods.MinMaxInfo>(minMaxInfoPointer);
        info.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        info.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        info.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        var maxHeight = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        if (monitorInfo.WorkArea.Left == monitorInfo.MonitorArea.Left
            && monitorInfo.WorkArea.Top == monitorInfo.MonitorArea.Top
            && monitorInfo.WorkArea.Right == monitorInfo.MonitorArea.Right
            && monitorInfo.WorkArea.Bottom == monitorInfo.MonitorArea.Bottom)
        {
            // Leave a 1px strip when WorkArea fills the monitor so an auto-hide taskbar can still appear.
            maxHeight = Math.Max(1, maxHeight - 1);
        }
        info.MaxSize.Y = maxHeight;
        Marshal.StructureToPtr(info, minMaxInfoPointer, false);
    }

    private bool IsHighContrastActive => highContrastOverrideForTesting
        ?? SystemInformation.HighContrast;

    internal static ChromeColorPolicy ResolveChromeColorPolicyForTesting(bool highContrast)
    {
        return ResolveChromeColorPolicy(highContrast, isPrivateMode: false);
    }

    internal static ChromeColorPolicy ResolveChromeColorPolicyForTesting(bool highContrast, bool isPrivateMode)
    {
        return ResolveChromeColorPolicy(highContrast, isPrivateMode);
    }

    internal void ApplyHighContrastChromeForTesting(bool enabled)
    {
        highContrastOverrideForTesting = enabled;
        ApplyChromeColorPolicy();
    }

    internal ChromeContrastSnapshot GetChromeContrastSnapshotForTesting()
    {
        return new ChromeContrastSnapshot(
            rootLayout.BackColor,
            toolbar.BackColor,
            addressBar.BackColor,
            addressBar.ForeColor,
            omniboxPanel.FocusBorderColor,
            appMenu.BackColor,
            appMenu.ForeColor,
            backButton.HighContrast,
            minimizeButton.HighContrast,
            omniboxPanel.HighContrast,
            tabs.FirstOrDefault()?.Header.HighContrast ?? IsHighContrastActive,
            menuRenderer.HighContrast);
    }

    private static ChromeColorPolicy ResolveChromeColorPolicy(bool highContrast, bool isPrivateMode = false)
    {
        if (highContrast)
        {
            return new ChromeColorPolicy(
                true,
                SystemColors.WindowFrame,
                SystemColors.Control,
                SystemColors.Control,
                SystemColors.Window,
                SystemColors.Window,
                SystemColors.ControlText,
                SystemColors.WindowText,
                SystemColors.GrayText,
                SystemColors.WindowText,
                SystemColors.Highlight,
                SystemColors.Highlight,
                SystemColors.HighlightText,
                SystemColors.Highlight,
                SystemColors.Highlight,
                SystemColors.Highlight,
                SystemColors.Highlight,
                SystemColors.Menu,
                SystemColors.MenuText,
                SystemColors.WindowText,
                SystemColors.WindowText);
        }

        if (isPrivateMode)
        {
            return new ChromeColorPolicy(
                false,
                Color.FromArgb(34, 10, 32),
                Color.FromArgb(24, 11, 25),
                Color.FromArgb(30, 13, 33),
                Color.FromArgb(18, 9, 20),
                Color.FromArgb(20, 10, 22),
                NativeUiTheme.Text,
                NativeUiTheme.Text,
                Color.FromArgb(185, 155, 195),
                Color.FromArgb(98, 55, 112),
                NativeUiTheme.Lavender,
                Color.FromArgb(64, 25, 78),
                NativeUiTheme.Text,
                Color.FromArgb(50, 22, 58),
                Color.FromArgb(62, 28, 72),
                Color.FromArgb(38, 16, 44),
                Color.FromArgb(48, 20, 56),
                Color.FromArgb(30, 13, 33),
                NativeUiTheme.Text,
                Color.FromArgb(100, 48, 118),
                NativeUiTheme.Text);
        }

        return new ChromeColorPolicy(
            false,
            NativeUiTheme.BrandWine,
            ChromeColor,
            ToolbarColor,
            PageColor,
            OmniboxColor,
            PageTextColor,
            PageTextColor,
            MutedTextColor,
            NativeUiTheme.Border,
            OmniboxFocusColor,
            NativeUiTheme.Selection,
            PageTextColor,
            NativeUiTheme.Hover,
            NativeUiTheme.Pressed,
            NativeUiTheme.Surface,
            NativeUiTheme.SurfaceRaised,
            ToolbarColor,
            PageTextColor,
            Color.FromArgb(142, 72, 111),
            NativeUiTheme.Text);
    }

    private void ApplyChromeColorPolicy()
    {
        if (IsDisposed) return;

        var palette = ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode);
        BackColor = palette.Frame;
        rootLayout.BackColor = palette.ChromeSurface;
        titleBar.BackColor = palette.ChromeSurface;
        tabArea.BackColor = palette.ChromeSurface;
        tabStrip.BackColor = palette.ChromeSurface;
        tabDropIndicator.BackColor = palette.HighContrast ? palette.Focus : (isPrivateMode ? NativeUiTheme.Lavender : AccentColor);
        tabDragGhost.HighContrast = palette.HighContrast;
        toolbar.BackColor = palette.ToolbarSurface;
        findBar.BackColor = palette.ToolbarSurface;
        TrySetBackColor(pageHost, Color.Transparent, palette.ChromeSurface);

        appMark.BackColor = palette.ChromeSurface;
        appMark.ForeColor = palette.HighContrast ? palette.ChromeText : (isPrivateMode ? NativeUiTheme.Lavender : AccentColor);
        ApplyButtonPalette(newTabButton, palette.ChromeSurface, palette.ChromeText, palette, titleButton: true);
        ApplyButtonPalette(tabListButton, palette.ChromeSurface, palette.ChromeText, palette, titleButton: true);
        ApplyButtonPalette(minimizeButton, palette.ChromeSurface, palette.ChromeText, palette, titleButton: true);
        ApplyButtonPalette(maximizeButton, palette.ChromeSurface, palette.ChromeText, palette, titleButton: true);
        ApplyButtonPalette(closeWindowButton, palette.ChromeSurface, palette.ChromeText, palette, titleButton: true);
        closeWindowButton.FlatAppearance.MouseOverBackColor = palette.HighContrast
            ? palette.HoverSurface
            : Color.FromArgb(167, 47, 85);
        closeWindowButton.FlatAppearance.MouseDownBackColor = palette.HighContrast
            ? palette.PressedSurface
            : Color.FromArgb(132, 32, 63);

        foreach (var button in new[] { backButton, forwardButton, reloadButton, homeButton, downloadsButton, menuButton })
        {
            ApplyButtonPalette(button, palette.ToolbarSurface, palette.ChromeText, palette);
        }
        foreach (var button in new[] { siteInfoButton, favoriteButton })
        {
            ApplyButtonPalette(button, palette.FieldSurface, palette.FieldText, palette);
        }
        foreach (var button in new ChromeButton[] { findCaseSensitiveButton, findPreviousButton, findNextButton, closeFindButton })
        {
            ApplyButtonPalette(button, palette.ToolbarSurface, palette.ChromeText, palette);
        }

        omniboxPanel.HighContrast = palette.HighContrast;
        omniboxPanel.BackColor = palette.FieldSurface;
        omniboxPanel.FillColor = palette.FieldSurface;
        omniboxPanel.BorderColor = palette.Border;
        omniboxPanel.FocusBorderColor = palette.Focus;
        foreach (Control control in omniboxPanel.Controls)
        {
            TrySetBackColor(control, Color.Transparent, palette.FieldSurface);
            control.ForeColor = palette.FieldText;
        }
        findOmniboxPanel.HighContrast = palette.HighContrast;
        findOmniboxPanel.BackColor = palette.FieldSurface;
        findOmniboxPanel.FillColor = palette.FieldSurface;
        findOmniboxPanel.BorderColor = palette.Border;
        findOmniboxPanel.FocusBorderColor = palette.Focus;
        addressBar.BackColor = palette.FieldSurface;
        addressBar.ForeColor = palette.FieldText;
        findBox.BackColor = palette.FieldSurface;
        findBox.ForeColor = palette.FieldText;
        statusLabel.ForeColor = palette.MutedText;
        UpdateFindMatchVisualState();

        menuRenderer.HighContrast = palette.HighContrast;
        multiViewButton.Palette = palette;
        foreach (var menu in new[] { appMenu, tabMenu, tabListMenu, siteInfoMenu, addressContextMenu, multiViewMenu, paneSwitchMenu })
        {
            ApplyMenuPalette(menu, palette);
        }
        RefreshMultiViewHeaders();

        foreach (var tab in tabs)
        {
            tab.Header.HighContrast = palette.HighContrast;
            UpdateTabHeader(tab);
        }
        UpdateSiteInfo();
        UpdateFavoriteButton();

        NativeUiTheme.ApplyWindowFrame(
            this,
            palette.ChromeSurface,
            palette.WindowFrameBorder,
            palette.WindowFrameText);
        Invalidate(true);
    }

    private static void ApplyButtonPalette(
        Button button,
        Color background,
        Color foreground,
        ChromeColorPolicy palette,
        bool titleButton = false)
    {
        button.BackColor = background;
        button.ForeColor = foreground;
        button.FlatAppearance.MouseOverBackColor = titleButton
            ? palette.TitleHoverSurface
            : palette.HoverSurface;
        button.FlatAppearance.MouseDownBackColor = titleButton
            ? palette.TitlePressedSurface
            : palette.PressedSurface;
        if (button is ChromeButton chromeButton) chromeButton.HighContrast = palette.HighContrast;
        if (button is WindowCaptionButton captionButton) captionButton.HighContrast = palette.HighContrast;
        button.Invalidate();
    }

    private static void ApplyMenuPalette(ToolStripDropDown menu, ChromeColorPolicy palette)
    {
        if (menu.IsDisposed) return;
        NativeUiTheme.ApplyMenuWindowFrame(menu, palette.WindowFrameBorder);
        menu.BackColor = palette.MenuSurface;
        menu.ForeColor = palette.MenuText;
        foreach (ToolStripItem item in menu.Items)
        {
            item.BackColor = palette.MenuSurface;
            item.ForeColor = palette.MenuText;
            if (item is ToolStripMenuItem menuItem && menuItem.HasDropDownItems)
            {
                ApplyMenuPalette(menuItem.DropDown, palette);
            }
        }
        menu.Invalidate();
    }

    private static void TrySetBackColor(Control control, Color color, Color? fallback = null)
    {
        try
        {
            control.BackColor = color;
            return;
        }
        catch (ArgumentException)
        {
            // Keep startup stable when a control does not support transparent
            // backgrounds; inherit from parent when possible, falling back to
            // the supplied fallback and finally a guaranteed-opaque color so
            // this can never throw during startup.
        }

        if (color.A < 255 && control.Parent is not null)
        {
            try
            {
                control.BackColor = control.Parent.BackColor;
                return;
            }
            catch (ArgumentException)
            {
                // Parent's color was not supported either; fall through.
            }
        }

        if (fallback is not null)
        {
            try
            {
                control.BackColor = fallback.Value;
                return;
            }
            catch (ArgumentException)
            {
                // Fallback was not supported either; fall through to a safe default.
            }
        }

        try
        {
            control.BackColor = SystemColors.Control;
        }
        catch (ArgumentException)
        {
            // Give up silently; leaving BackColor unset beats crashing startup.
        }
    }

    private void BuildChrome()
    {
        SuspendLayout();

        rootLayout.Dock = DockStyle.Fill;
        rootLayout.BackColor = ChromeColor;
        rootLayout.ColumnCount = 1;
        rootLayout.RowCount = 4;
        rootLayout.Margin = Padding.Empty;
        rootLayout.Padding = Padding.Empty;
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        BuildTabStrip();
        BuildTitleBar();
        BuildToolbar();
        BuildFindBar();
        BuildMenus();
        NativeUiTheme.ConfigureGlassToolTip(toolTip);
        NativeUiTheme.ApplyDarkScrollbars(tabStrip);

        pageHost.Dock = DockStyle.Fill;
        pageHost.Margin = Padding.Empty;
        TrySetBackColor(pageHost, Color.Transparent, ChromeColor);
        BuildMultiViewSurface();
        pageHost.Resize += (_, _) =>
        {
            if (multiViewState.IsMultiViewActive && !isDomFullScreen) ApplyMultiViewLayout();
            else EnsureTabHostLayout(activeTab);
        };

        rootLayout.Controls.Add(titleBar, 0, 0);
        rootLayout.Controls.Add(toolbar, 0, 1);
        rootLayout.Controls.Add(findBar, 0, 2);
        rootLayout.Controls.Add(pageHost, 0, 3);
        Controls.Add(rootLayout);
        Controls.Add(addressSuggestionPopup);
        addressSuggestionPopup.BringToFront();

        WireChromeEvents();
        ApplyChromeDpiMetrics();
        UpdateNavigationChrome();
        UpdateResponsiveToolbar();
        UpdateWindowControls();
        ApplyChromeColorPolicy();
        ResumeLayout(true);
    }

    private void BuildTitleBar()
    {
        titleBar.Dock = DockStyle.Fill;
        titleBar.Margin = Padding.Empty;
        titleBar.Padding = new Padding(6, 3, 0, 3);
        titleBar.BackColor = ChromeColor;
        titleBar.RowCount = 1;
        titleBar.ColumnCount = 7;
        titleBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, isPrivateMode ? 86 : 4));
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        titleBar.AccessibleName = "Window title and tabs";
        titleBar.AccessibleRole = AccessibleRole.TitleBar;

        appMark.Dock = DockStyle.Fill;
        appMark.Margin = new Padding(2, 0, 5, 0);
        appMark.BackColor = ChromeColor;
        appMark.ForeColor = AccentColor;
        appMark.Font = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 10f);
        appMark.PrivateMode = isPrivateMode;

        ConfigureTitleBarButton(
            newTabButton,
            string.Empty,
            isPrivateMode ? "New incognito tab (Ctrl+T)" : "New tab (Ctrl+T)",
            isPrivateMode ? "New incognito tab" : "New tab",
            14f);
        newTabButton.IconKind = ChromeIconKind.NewTab;
        newTabButton.CornerRadius = 8;
        newTabButton.Dock = DockStyle.None;
        newTabButton.Size = new Size(28, 27);
        ConfigureTitleBarButton(tabListButton, string.Empty, "All tabs", "All tabs", 11f);
        tabListButton.IconKind = ChromeIconKind.TabList;
        tabListButton.CornerRadius = 8;
        ConfigureTitleBarButton(minimizeButton, string.Empty, "Minimize", "Minimize", 10f);
        minimizeButton.Glyph = CaptionGlyph.Minimize;
        ConfigureTitleBarButton(maximizeButton, string.Empty, "Maximize", "Maximize", 11f);
        maximizeButton.Glyph = CaptionGlyph.Maximize;
        ConfigureTitleBarButton(closeWindowButton, string.Empty, "Close", "Close", 13f);
        closeWindowButton.Glyph = CaptionGlyph.Close;
        minimizeButton.Cursor = Cursors.Default;
        maximizeButton.Cursor = Cursors.Default;
        closeWindowButton.Cursor = Cursors.Default;
        closeWindowButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(167, 47, 85);
        closeWindowButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(132, 32, 63);

        tabArea.Dock = DockStyle.Fill;
        tabArea.Margin = Padding.Empty;
        tabArea.BackColor = ChromeColor;
        tabArea.AccessibleName = "Tabs and window drag area";
        tabArea.AccessibleDescription = "Drag the empty space after the New tab button to move the window";
        toolTip.SetToolTip(tabArea, "Drag empty space to move the window");
        tabDropIndicator.Margin = Padding.Empty;
        tabDropIndicator.Size = new Size(2, 20);
        tabDropIndicator.BackColor = AccentColor;
        tabDropIndicator.Enabled = false;
        tabDropIndicator.Visible = false;
        tabArea.Controls.Add(tabStrip);
        tabArea.Controls.Add(newTabButton);
        tabArea.Controls.Add(tabDragGhost);
        tabArea.Controls.Add(tabDropIndicator);

        titleBar.Controls.Add(appMark, 0, 0);
        titleBar.Controls.Add(tabArea, 1, 0);
        titleBar.Controls.Add(tabListButton, 2, 0);
        if (isPrivateMode)
        {
            titleBar.Controls.Add(incognitoBadge, 3, 0);
        }
        titleBar.Controls.Add(minimizeButton, 4, 0);
        titleBar.Controls.Add(maximizeButton, 5, 0);
        titleBar.Controls.Add(closeWindowButton, 6, 0);
    }

    private void BuildToolbar()
    {
        toolbar.Dock = DockStyle.Fill;
        toolbar.Margin = Padding.Empty;
        toolbar.Padding = new Padding(10, 5, 10, 6);
        toolbar.BackColor = ToolbarColor;
        toolbar.Paint += (_, e) =>
        {
            if (IsHighContrastActive || toolbar.Width <= 2 || toolbar.Height <= 2) return;
            e.Graphics.DrawLine(
                NativeUiTheme.GetSharedPen(Color.FromArgb(32, 255, 232, 248), 1f),
                0,
                0,
                toolbar.Width - 1,
                0);
            e.Graphics.DrawLine(
                NativeUiTheme.GetSharedPen(Color.FromArgb(62, 255, 224, 245), 1f),
                0,
                toolbar.Height - 1,
                toolbar.Width - 1,
                toolbar.Height - 1);
        };
        toolbar.RowCount = 1;
        toolbar.ColumnCount = 9;
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddToolbarColumn(40);
        AddToolbarColumn(40);
        AddToolbarColumn(40);
        AddToolbarColumn(40);
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddToolbarColumn(42);
        AddToolbarColumn(144);
        AddToolbarColumn(42);

        ConfigureToolbarButton(backButton, string.Empty, "Back (Alt+Left)", "Back", 15f);
        ConfigureToolbarButton(forwardButton, string.Empty, "Forward (Alt+Right)", "Forward", 15f);
        ConfigureToolbarButton(reloadButton, string.Empty, "Reload (Ctrl+R)", "Reload", 14f);
        ConfigureToolbarButton(homeButton, string.Empty, "New-tab home", "Home", 13f);
        ConfigureToolbarButton(menuButton, string.Empty, "MishaWeb menu", "Browser menu", 16f);
        ConfigureOmniboxButton(siteInfoButton, string.Empty, "Site information", "Site information", 10f);
        ConfigureOmniboxButton(favoriteButton, string.Empty, "Add this page to favorites (Ctrl+D)", "Add to favorites", 15f);

        ConfigureToolbarButton(downloadsButton, string.Empty, "Downloads", "Downloads", 12f);
        downloadsButton.Visible = false;

        multiViewButton.Dock = DockStyle.Fill;
        multiViewButton.Margin = new Padding(2, 2, 2, 2);
        multiViewButton.TabStop = false;
        toolTip.SetToolTip(multiViewButton, "Multiple views (Ctrl+Shift+\\) \u2014 Click to add view, right-click for layouts");

        backButton.IconKind = ChromeIconKind.Back;
        forwardButton.IconKind = ChromeIconKind.Forward;
        reloadButton.IconKind = ChromeIconKind.Reload;
        homeButton.IconKind = ChromeIconKind.Home;
        menuButton.IconKind = ChromeIconKind.Menu;
        siteInfoButton.IconKind = ChromeIconKind.SiteInfo;
        siteInfoButton.IsOmniboxEmbedded = true;
        favoriteButton.IconKind = ChromeIconKind.Favorite;
        favoriteButton.IsOmniboxEmbedded = true;
        downloadsButton.IconKind = ChromeIconKind.Downloads;
        reloadButton.GlyphState = "reload";
        siteInfoButton.GlyphState = "unknown";
        favoriteButton.GlyphState = "outline";

        addressBar.Dock = DockStyle.Fill;
        addressBar.Margin = new Padding(2, 7, 2, 5);
        addressBar.AutoSize = false;
        addressBar.BorderStyle = BorderStyle.None;
        addressBar.BackColor = OmniboxColor;
        addressBar.ForeColor = PageTextColor;
        addressBar.Font = NativeUiTheme.GetSharedFont("Segoe UI", 10.5f);
        addressBar.MaxLength = BrowserPolicy.MaximumUrlLength;
        addressBar.PlaceholderText = $"Search with {BrowserPolicy.GetSearchProviderName(searchProviderId)} or enter address{(isPrivateMode ? " in Incognito" : string.Empty)}";
        addressBar.AccessibleName = "Address and search bar";
        addressBar.AccessibleDescription = "Enter a website address or search terms";
        addressContextMenu.ShowImageMargin = false;
        addressContextMenu.Renderer = menuRenderer;
        NativeUiTheme.ApplyMenuWindowFrame(addressContextMenu);
        var undoAddressEdit = new ToolStripMenuItem("Undo");
        undoAddressEdit.Click += (_, _) => addressBar.Undo();
        var cutAddressText = new ToolStripMenuItem("Cut");
        cutAddressText.Click += (_, _) => addressBar.Cut();
        var copyAddressText = new ToolStripMenuItem("Copy");
        copyAddressText.Click += (_, _) => addressBar.Copy();
        var pasteAddressText = new ToolStripMenuItem("Paste");
        pasteAddressText.Click += (_, _) => addressBar.Paste();
        var deleteAddressText = new ToolStripMenuItem("Delete");
        deleteAddressText.Click += (_, _) => addressBar.SelectedText = string.Empty;
        var selectAllAddressText = new ToolStripMenuItem("Select all");
        selectAllAddressText.Click += (_, _) => addressBar.SelectAll();
        var pasteAndGo = new ToolStripMenuItem("Paste and go")
        {
            AccessibleName = "Paste and go"
        };
        pasteAndGo.Click += (_, _) => PasteAndGo();
        addressContextMenu.Opening += (_, _) =>
        {
            undoAddressEdit.Enabled = addressBar.CanUndo;
            cutAddressText.Enabled = addressBar.SelectionLength > 0;
            copyAddressText.Enabled = addressBar.SelectionLength > 0;
            deleteAddressText.Enabled = addressBar.SelectionLength > 0;
            pasteAddressText.Enabled = CanReadPasteAndGoText();
            pasteAndGo.Enabled = pasteAddressText.Enabled;
            selectAllAddressText.Enabled = addressBar.TextLength > 0
                && addressBar.SelectionLength != addressBar.TextLength;
        };
        addressContextMenu.Items.AddRange(
        [
            undoAddressEdit,
            new ToolStripSeparator(),
            cutAddressText,
            copyAddressText,
            pasteAddressText,
            deleteAddressText,
            new ToolStripSeparator(),
            selectAllAddressText,
            new ToolStripSeparator(),
            pasteAndGo
        ]);
        addressBar.ContextMenuStrip = addressContextMenu;

        omniboxPanel.Dock = DockStyle.Fill;
        omniboxPanel.Margin = new Padding(9, 1, 9, 1);
        omniboxPanel.FillColor = OmniboxColor;
        omniboxPanel.BorderColor = NativeUiTheme.Border;
        omniboxPanel.FocusBorderColor = OmniboxFocusColor;
        omniboxPanel.CornerRadius = 15;
        omniboxPanel.Padding = new Padding(2);

        omniboxLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(3, 0, 3, 0),
            ColumnCount = 3,
            RowCount = 1
        };
        TrySetBackColor(omniboxLayout, Color.Transparent, OmniboxColor);
        omniboxLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        omniboxLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
        omniboxLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        omniboxLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
        omniboxLayout.Controls.Add(siteInfoButton, 0, 0);
        omniboxLayout.Controls.Add(addressBar, 1, 0);
        omniboxLayout.Controls.Add(favoriteButton, 2, 0);
        omniboxPanel.Controls.Add(omniboxLayout);

        statusLabel.Dock = DockStyle.Fill;
        statusLabel.Margin = new Padding(3, 0, 5, 0);
        statusLabel.Text = "Starting\u2026";
        statusLabel.ForeColor = MutedTextColor;
        statusLabel.Font = NativeUiTheme.GetSharedFont("Segoe UI", 8.25f);
        // AnnouncingStatusLabel paints itself: right-aligned, single line,
        // vertically centred, truncated with an ellipsis. It therefore ignores
        // TextAlign, AutoEllipsis and MaximumSize, and none of those are set, so
        // the paint stays the single source of truth for how the text is drawn.
        // The toolbar column already bounds the width, so no size cap is needed;
        // setting one would cap the control rather than the text, which put the
        // status out of line with the surrounding buttons.
        statusLabel.AccessibleName = "Browser status";
        statusLabel.AccessibleRole = AccessibleRole.StatusBar;

        toolbar.Controls.Add(backButton, 0, 0);
        toolbar.Controls.Add(forwardButton, 1, 0);
        toolbar.Controls.Add(reloadButton, 2, 0);
        toolbar.Controls.Add(homeButton, 3, 0);
        toolbar.Controls.Add(omniboxPanel, 4, 0);
        toolbar.Controls.Add(statusLabel, 5, 0);
        toolbar.Controls.Add(downloadsButton, 6, 0);
        toolbar.Controls.Add(multiViewButton, 7, 0);
        toolbar.Controls.Add(menuButton, 8, 0);
    }

    private void BuildTabStrip()
    {
        tabStrip.Dock = DockStyle.None;
        tabStrip.Margin = Padding.Empty;
        tabStrip.BackColor = ChromeColor;
        tabStrip.FlowDirection = FlowDirection.LeftToRight;
        tabStrip.WrapContents = false;
        tabStrip.AutoScroll = true;
        tabStrip.Padding = new Padding(2, 0, 2, 0);
        tabStrip.AccessibleName = "Open tabs";
        tabStrip.AccessibleRole = AccessibleRole.PageTabList;
    }

    private void BuildFindBar()
    {
        findBar.Dock = DockStyle.Fill;
        findBar.Margin = Padding.Empty;
        findBar.Padding = new Padding(12, 5, 10, 5);
        findBar.BackColor = ToolbarColor;
        findBar.Paint += (_, e) =>
        {
            if (IsHighContrastActive || findBar.Width <= 2 || findBar.Height <= 2) return;
            e.Graphics.DrawLine(
                NativeUiTheme.GetSharedPen(Color.FromArgb(62, 255, 224, 245), 1f),
                0,
                findBar.Height - 1,
                findBar.Width - 1,
                findBar.Height - 1);
        };
        findBar.RowCount = 1;
        findBar.ColumnCount = 6;
        findBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        findBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        findBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        findBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        findBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        findBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));

        findOmniboxPanel.Dock = DockStyle.Fill;
        findOmniboxPanel.Margin = new Padding(0, 1, 8, 1);
        findOmniboxPanel.FillColor = OmniboxColor;
        findOmniboxPanel.BorderColor = NativeUiTheme.Border;
        findOmniboxPanel.FocusBorderColor = OmniboxFocusColor;
        findOmniboxPanel.CornerRadius = 14;
        findOmniboxPanel.Padding = new Padding(10, 1, 10, 1);

        findBox.Dock = DockStyle.Fill;
        findBox.Margin = new Padding(2, 5, 2, 4);
        findBox.AutoSize = false;
        findBox.BorderStyle = BorderStyle.None;
        findBox.BackColor = OmniboxColor;
        findBox.ForeColor = PageTextColor;
        findBox.Font = NativeUiTheme.GetSharedFont("Segoe UI", 10f);
        findBox.PlaceholderText = "Find on this page";
        findBox.AccessibleName = "Find on page";
        findOmniboxPanel.Controls.Add(findBox);

        findMatchLabel.Dock = DockStyle.Fill;
        findMatchLabel.TextAlign = ContentAlignment.MiddleCenter;
        findMatchLabel.ForeColor = MutedTextColor;
        findMatchLabel.Font = NativeUiTheme.GetSharedFont("Segoe UI", 8.5f);
        findMatchLabel.Text = string.Empty;
        findMatchLabel.AccessibleName = "Find match count";
        findMatchLabel.TextChanged += (_, _) => UpdateFindMatchVisualState();

        ConfigureToolbarButton(findCaseSensitiveButton, string.Empty, "Match case", "Match case", 10f);
        ConfigureToolbarButton(findPreviousButton, string.Empty, "Previous match (\u2191, Shift+F3)", "Previous match", 11f);
        ConfigureToolbarButton(findNextButton, string.Empty, "Next match (\u2193, F3)", "Next match", 11f);
        ConfigureToolbarButton(closeFindButton, string.Empty, "Close find (Esc)", "Close find", 13f);
        findCaseSensitiveButton.IconKind = ChromeIconKind.MatchCase;
        findCaseSensitiveButton.CornerRadius = 9;
        findCaseSensitiveButton.AccessibleDescription = "Case-insensitive search";
        findPreviousButton.IconKind = ChromeIconKind.FindPrevious;
        findPreviousButton.CornerRadius = 9;
        findNextButton.IconKind = ChromeIconKind.FindNext;
        findNextButton.CornerRadius = 9;
        closeFindButton.IconKind = ChromeIconKind.FindClose;
        closeFindButton.CornerRadius = 9;

        findBar.Controls.Add(findOmniboxPanel, 0, 0);
        findBar.Controls.Add(findMatchLabel, 1, 0);
        findBar.Controls.Add(findCaseSensitiveButton, 2, 0);
        findBar.Controls.Add(findPreviousButton, 3, 0);
        findBar.Controls.Add(findNextButton, 4, 0);
        findBar.Controls.Add(closeFindButton, 5, 0);
        findBar.Resize += (_, _) => UpdateFindBarLayout();
        findBar.Visible = false;
    }

    private void BuildMenus()
    {
        appMenu.ShowImageMargin = false;
        appMenu.Renderer = menuRenderer;
        appMenu.BackColor = ToolbarColor;
        appMenu.ForeColor = PageTextColor;
        NativeUiTheme.ApplyMenuWindowFrame(appMenu);
        appMenu.Opening += (_, _) => PopulateAppMenu();

        AddMenuItem(appMenu, "Command palette", "Ctrl+Shift+P", ShowCommandPalette);
        AddMenuItem(appMenu, "New tab", "Ctrl+T", () => RunUiTask(() => OpenNewTabAsync(StartPage.Url), "Could not open a tab"));
        AddMenuItem(appMenu, "New incognito window", "Ctrl+Shift+N", OpenPrivateWindow);
        AddMenuItem(appMenu, "Reopen closed tab", "Ctrl+Shift+T", () => RunUiTask(RestoreClosedTabAsync, "Could not restore the tab"));
        appMenu.Items.Add(new ToolStripSeparator());
        appMenu.Items.Add(favoritesMenu);
        appMenu.Items.Add(historyMenu);
        appMenu.Items.Add(downloadsMenu);
        appMenu.Items.Add(searchProviderMenu);
        AddMenuItem(appMenu, "Saved items\u2026", "Ctrl+Shift+O", ShowSavedItemsDialog);
        AddMenuItem(appMenu, "Saved sessions\u2026", string.Empty, ShowSessionManager);
        AddMenuItem(appMenu, "Voice and audio settings\u2026", string.Empty, ShowVoiceAudioSettingsDialog);
        extensionsMenuItem.Click += (_, _) => RunUiTask(
            ShowExtensionsManagerAsync,
            "Could not open the extensions manager");
        appMenu.Items.Add(extensionsMenuItem);
        appMenu.Items.Add(new ToolStripSeparator());
        adBlockMenuItem.Click += (_, _) => ToggleAdBlocker();
        appMenu.Items.Add(adBlockMenuItem);
        BuildResourceModeMenu();
        appMenu.Items.Add(resourceModeMenu);
        reduceMotionMenuItem.Click += (_, _) => ToggleReducedWebsiteMotion();
        appMenu.Items.Add(reduceMotionMenuItem);
        siteThemeMenuItem.Click += (_, _) => ToggleDarkMode();
        fullScreenMenuItem.Click += (_, _) => ToggleFullScreen();
        appMenu.Items.Add(siteThemeMenuItem);
        appMenu.Items.Add(fullScreenMenuItem);
        AddMenuItem(multiViewAppMenuItem.DropDown, "Single view (1 pane)", string.Empty, () =>
            RunUiTask(() => SetMultiViewPresetAsync(MultiViewPreset.Single), "Could not switch view layout"));
        AddMenuItem(multiViewAppMenuItem.DropDown, "Split view (2 panes)", "Ctrl+Shift+\\", () =>
            RunUiTask(() => SetMultiViewPresetAsync(MultiViewPreset.Split), "Could not switch view layout"));
        AddMenuItem(multiViewAppMenuItem.DropDown, "Tri view (3 panes)", string.Empty, () =>
            RunUiTask(() => SetMultiViewPresetAsync(MultiViewPreset.Tri), "Could not switch view layout"));
        AddMenuItem(multiViewAppMenuItem.DropDown, "Quad view (4 panes)", string.Empty, () =>
            RunUiTask(() => SetMultiViewPresetAsync(MultiViewPreset.Quad), "Could not switch view layout"));
        appMenu.Items.Add(multiViewAppMenuItem);
        AddMenuItem(appMenu, "Picture-in-Picture", string.Empty, () =>
            RunUiTask(() => TogglePictureInPictureAsync(), "Picture-in-Picture is unavailable"));
        appMenu.Items.Add(new ToolStripSeparator());
        AddMenuItem(appMenu, "Downloads", "Ctrl+J", ShowDownloadsPopup);
        AddMenuItem(appMenu, "Find on page", "Ctrl+F", ShowFindBar);
        appMenu.Items.Add(zoomMenu);
        AddMenuItem(appMenu, "Print page\u2026", "Ctrl+P", PrintPage);
        AddMenuItem(appMenu, "Capture page as PNG\u2026", "Ctrl+Shift+S", () =>
            RunUiTask(CapturePageAsync, "Could not capture the page"));
        appMenu.Items.Add(new ToolStripSeparator());
        AddMenuItem(appMenu, "Clear browsing data\u2026", "Ctrl+Shift+Del", () => RunUiTask(ClearBrowsingDataAsync, "Could not clear browsing data"));
        restartBrowserForUpdateMenuItem.Click += (_, _) => RunUiTask(
            RestartBrowserForUpdateAsync,
            "Could not restart the browser engine");
        appMenu.Items.Add(restartBrowserForUpdateMenuItem);
        AddMenuItem(appMenu, "About MishaWeb", string.Empty, ShowAbout);
        AddMenuItem(appMenu, "Exit", string.Empty, Close);

        tabMenu.ShowImageMargin = false;
        tabMenu.Renderer = menuRenderer;
        tabMenu.BackColor = ToolbarColor;
        tabMenu.ForeColor = PageTextColor;
        NativeUiTheme.ApplyMenuWindowFrame(tabMenu);
        tabListMenu.ShowImageMargin = false;
        tabListMenu.Renderer = menuRenderer;
        tabListMenu.BackColor = ToolbarColor;
        tabListMenu.ForeColor = PageTextColor;
        NativeUiTheme.ApplyMenuWindowFrame(tabListMenu);
        siteInfoMenu.ShowImageMargin = false;
        siteInfoMenu.Renderer = menuRenderer;
        siteInfoMenu.BackColor = ToolbarColor;
        siteInfoMenu.ForeColor = PageTextColor;
        NativeUiTheme.ApplyMenuWindowFrame(siteInfoMenu);
        foreach (var dropDown in new ToolStripDropDown[]
        {
            favoritesMenu.DropDown,
            historyMenu.DropDown,
            downloadsMenu.DropDown,
            searchProviderMenu.DropDown,
            resourceModeMenu.DropDown,
            zoomMenu.DropDown,
            extensionsMenuItem.DropDown
        })
        {
            NativeUiTheme.ApplyMenuWindowFrame(dropDown);
        }
    }

    private void BuildResourceModeMenu()
    {
        resourceModeMenu.DropDownItems.Clear();
        memorySaverMenuItem.ToolTipText =
            "Keeps your three most recently used sites ready; releases older inactive pages.";
        memorySaverMenuItem.AccessibleDescription = memorySaverMenuItem.ToolTipText;
        ultraLightMenuItem.ToolTipText =
            "May sleep recent sites without refreshing them; unloads only older eligible pages.";
        ultraLightMenuItem.AccessibleDescription = ultraLightMenuItem.ToolTipText;
        resourceOffMenuItem.Click += (_, _) => SetResourceMode(TabLifecycleMode.Off);
        memorySaverMenuItem.Click += (_, _) => SetResourceMode(TabLifecycleMode.Standard);
        ultraLightMenuItem.Click += (_, _) => SetResourceMode(TabLifecycleMode.Ultra);
        resourceModeMenu.DropDownItems.Add(resourceOffMenuItem);
        resourceModeMenu.DropDownItems.Add(memorySaverMenuItem);
        resourceModeMenu.DropDownItems.Add(ultraLightMenuItem);
    }

    private void WireChromeEvents()
    {
        backButton.Click += (_, _) =>
        {
            if (ModifierKeys.HasFlag(Keys.Shift))
            {
                RunUiTask(() => ShowNavigationHistoryMenuAsync(backButton, forward: false), "History unavailable");
                return;
            }
            GoBack();
        };
        backButton.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && backButton.Enabled)
            {
                RunUiTask(() => ShowNavigationHistoryMenuAsync(backButton, forward: false), "History unavailable");
            }
        };
        forwardButton.Click += (_, _) =>
        {
            if (ModifierKeys.HasFlag(Keys.Shift))
            {
                RunUiTask(() => ShowNavigationHistoryMenuAsync(forwardButton, forward: true), "History unavailable");
                return;
            }
            GoForward();
        };
        forwardButton.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && forwardButton.Enabled)
            {
                RunUiTask(() => ShowNavigationHistoryMenuAsync(forwardButton, forward: true), "History unavailable");
            }
        };
        reloadButton.Click += (_, _) => ReloadOrStop();
        homeButton.Click += (_, _) => ShowStartPage(activeTab);
        siteInfoButton.Click += (_, _) => ShowSiteInformation();
        if (incognitoBadge is not null) incognitoBadge.Click += (_, _) => ShowSiteInformation();
        favoriteButton.Click += (_, _) => ToggleFavorite();
        newTabButton.Click += (_, _) => RunUiTask(() => OpenNewTabAsync(StartPage.Url), "Could not open a tab");
        tabListButton.Click += (_, _) => ShowTabListMenu();
        multiViewButton.Click += (_, _) => HandleMultiViewButtonClick();
        multiViewButton.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) ShowMultiViewMenu(multiViewButton);
        };
        menuButton.Click += (_, _) => appMenu.Show(menuButton, new Point(menuButton.Width, menuButton.Height), ToolStripDropDownDirection.BelowLeft);
        downloadsButton.Click += (_, _) => ShowDownloadsPopup();
        minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        maximizeButton.Click += (_, _) => ToggleMaximizeRestore();
        closeWindowButton.Click += (_, _) => Close();
        titleBar.MouseDown += BeginWindowDrag;
        appMark.MouseDown += BeginWindowDrag;
        tabArea.MouseDown += BeginWindowDrag;
        tabStrip.MouseDown += BeginWindowDrag;
        tabStrip.MouseWheel += (_, e) => ScrollTabStrip(e.Delta);

        addressBar.Enter += (_, _) =>
        {
            addressBarEditing = true;
            smartSearchBarEditing = false;
            activeSmartSearchBar = null;
            suggestionAnchor = omniboxPanel;
            omniboxPanel.IsFocused = true;
            if (activeTab is { IsStartPage: false }) addressBar.Text = activeTab.Url;
            addressBar.SelectAll();
            QueueAddressSuggestions();
        };
        addressBar.Leave += (_, _) =>
        {
            PostUi(() =>
            {
                if (addressBar.Focused) return;
                addressBarEditing = false;
                omniboxPanel.IsFocused = false;
                addressSuggestionTimer.Stop();
                HideAddressSuggestions();
                SyncAddressBar();
            });
        };
        addressBar.TextChanged += (_, _) =>
        {
            if (isAutocompletingAddressBar) return;
            if (!syncingAddressBar && addressBar.TextLength > 0)
            {
                PrewarmEnvironmentOnUserInput();
            }
            if (suppressAddressBarAutocomplete)
            {
                suppressAddressBarAutocomplete = false;
            }
            else
            {
                TryApplyAddressBarAutocomplete();
            }
            QueueAddressSuggestions();
        };
        addressBar.KeyDown += AddressBarOnKeyDown;
        addressSuggestionPopup.SuggestionAccepted += AcceptAddressSuggestion;
        addressSuggestionPopup.SuggestionRemoved += RemoveAddressSuggestion;
        addressSuggestionTimer.Tick += (_, _) =>
        {
            addressSuggestionTimer.Stop();
            UpdateAddressSuggestions();
        };

        findBox.Enter += (_, _) => findOmniboxPanel.IsFocused = true;
        findBox.Leave += (_, _) => findOmniboxPanel.IsFocused = false;
        findBox.KeyDown += FindBoxOnKeyDown;
        findBox.TextChanged += (_, _) =>
        {
            if (!suppressFindEditTracking) findBoxEditGeneration++;
            if (!findBar.Visible) return;
            findDebounceTimer.Stop();
            findDebounceTimer.Start();
        };
        findCaseSensitiveButton.Click += (_, _) =>
        {
            findMatchCase = !findMatchCase;
            findCaseSensitiveButton.IsActiveToggle = findMatchCase;
            findCaseSensitiveButton.AccessibleDescription = findMatchCase
                ? "Case-sensitive search enabled"
                : "Case-insensitive search";
            if (findBox.TextLength > 0) RunUiTask(StartFindSession, "Find failed");
        };
        findPreviousButton.Click += (_, _) => RunUiTask(() => FindOnPageAsync(true), "Find failed");
        findNextButton.Click += (_, _) => RunUiTask(() => FindOnPageAsync(false), "Find failed");
        closeFindButton.Click += (_, _) => HideFindBar();
        findDebounceTimer.Tick += (_, _) =>
        {
            findDebounceTimer.Stop();
            if (findBar.Visible) RunUiTask(StartFindSession, "Find failed");
        };
        downloadGraceTimer.Tick += (_, _) =>
        {
            downloadGraceTimer.Stop();
            if (downloads.Sum(item => item.IsTerminal ? 0 : 1) == 0)
            {
                downloadsButtonAutoVisible = false;
                UpdateResponsiveToolbar();
            }
        };
        downloadUiTimer.Tick += (_, _) => RefreshDownloadsUiNow();
        statusUiTimer.Tick += (_, _) =>
        {
            statusUiTimer.Stop();
            UpdateStatus();
        };

        memoryTimer.Tick += (_, _) =>
        {
            memoryTimer.Stop();
            RunUiTask(
                async () =>
                {
                    await SleepInactiveTabsAsync();
                    RefreshMemorySweepTimer();
                },
                "Memory saver check failed");
        };
        stateSaveTimer.Tick += (_, _) => QueueStateSaveNow();
        transientStatusTimer.Tick += (_, _) =>
        {
            transientStatusTimer.Stop();
            transientStatus = string.Empty;
            UpdateStatus();
        };
        tabArea.Resize += (_, _) => ResizeTabHeaders();
        toolbar.Resize += (_, _) =>
        {
            UpdateResponsiveToolbar();
            PositionAddressSuggestions();
        };
        SizeChanged += (_, _) =>
        {
            UpdateWindowControls();
            PositionAddressSuggestions();
            if (WindowState == FormWindowState.Minimized) downloadNotification?.Dismiss();
            else RepositionDownloadNotification();
            TrackWindowPlacementChange();
            if ((WindowState == FormWindowState.Minimized) != isWindowMinimized)
            {
                RunUiTask(HandleWindowStateChangeAsync, "Could not change tab power state");
            }
        };
        LocationChanged += (_, _) =>
        {
            RepositionDownloadNotification();
            TrackWindowPlacementChange();
        };
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    internal void PrepareChromePreviewForTesting()
    {
        if (tabs.Count > 0) return;

        var tab = CreateBrowserTab(StartPage.Url, canScriptClose: false);
        tabs.Add(tab);
        tabStrip.Controls.Add(tab.Header);
        ActivateTab(tab, focusPage: false);
        ResizeTabHeaders();
        transientStatus = string.Empty;
        UpdateStatus();
    }

    internal void PrepareLoadedChromePreviewForTesting()
    {
        if (tabs.Count > 0) return;

        var previewTabs = new[]
        {
            ("Messenger", "https://www.messenger.com/t/example"),
            ("YouTube", "https://www.youtube.com/watch?v=preview"),
            ("Google Drive", "https://drive.google.com/drive/my-drive"),
            ("MishaWeb docs", "https://learn.microsoft.com/mishaweb")
        };
        for (var index = 0; index < previewTabs.Length; index++)
        {
            var preview = previewTabs[index];
            var tab = CreateBrowserTab(preview.Item2, canScriptClose: false);
            tab.Title = preview.Item1;
            tab.StatusText = "Ready";
            tab.ConnectionState = ConnectionState.Secure;
            tab.BlockedRequestCount = index == 0 ? 13 : 0;
            tab.ActiveDownloads = index == 0 ? 1 : 0;
            tab.Overlay.Visible = false;
            tabs.Add(tab);
            tabStrip.Controls.Add(tab.Header);
        }

        downloadsButtonAutoVisible = true;
        transientStatus = string.Empty;
        ActivateTab(tabs[0], focusPage: false, restoreDiscarded: false);
        backButton.Enabled = true;
        forwardButton.Enabled = true;
        reloadButton.Enabled = true;
        reloadButton.GlyphState = "reload";
        ResizeTabHeaders();
        UpdateStatus();
        UpdateResponsiveToolbar();
    }

    internal ToolbarLayoutSnapshot GetToolbarLayoutSnapshotForTesting()
    {
        PerformLayout();
        toolbar.PerformLayout();
        var iconButtons = new[]
        {
            backButton,
            forwardButton,
            reloadButton,
            homeButton,
            siteInfoButton,
            favoriteButton,
            downloadsButton,
            menuButton
        };
        return new ToolbarLayoutSnapshot(
            ClientSize.Width,
            toolbar.ClientSize.Width,
            ScaleToolbarLogical(MinimumToolbarOmniboxWidth),
            backButton.Bounds,
            forwardButton.Bounds,
            reloadButton.Bounds,
            homeButton.Visible ? homeButton.Bounds : Rectangle.Empty,
            omniboxPanel.Bounds,
            statusLabel.Visible ? statusLabel.Bounds : Rectangle.Empty,
            downloadsButton.Visible ? downloadsButton.Bounds : Rectangle.Empty,
            menuButton.Bounds,
            homeButton.Visible,
            statusLabel.Visible,
            downloadsButton.Visible,
            statusLabel.Text,
            statusLabel.AccessibleName ?? string.Empty,
            iconButtons.All(button => button.Text.Length == 0));
    }

    internal bool VectorToolbarTextIsSuppressedForTesting()
    {
        var cases = new[]
        {
            (Button: reloadButton, State: "stop"),
            (Button: siteInfoButton, State: "warning"),
            (Button: siteInfoButton, State: "loading"),
            (Button: siteInfoButton, State: "secure"),
            (Button: favoriteButton, State: "filled"),
            (Button: favoriteButton, State: "outline"),
            (Button: menuButton, State: string.Empty)
        };
        var originals = cases
            .Select(item => (item.Button, item.Button.GlyphState))
            .ToArray();
        var valid = true;
        foreach (var item in cases)
        {
            item.Button.GlyphState = item.State;
            item.Button.Text = "legacy glyph";
            valid &= item.Button.Text.Length == 0;
        }
        foreach (var original in originals)
        {
            original.Button.GlyphState = original.GlyphState;
        }
        return valid;
    }

    internal bool VectorToolbarPaintIsIdempotentForTesting()
    {
        var originalState = favoriteButton.GlyphState;
        var originalEnabled = favoriteButton.Enabled;
        try
        {
            favoriteButton.Enabled = true;
            favoriteButton.GlyphState = "outline";
            favoriteButton.CreateControl();
            var size = favoriteButton.ClientSize;
            if (size.Width <= 0 || size.Height <= 0) return false;

            using var expected = new Bitmap(size.Width, size.Height);
            using var repeated = new Bitmap(size.Width, size.Height);
            using (var expectedBackground = Graphics.FromImage(expected))
            using (var repeatedBackground = Graphics.FromImage(repeated))
            {
                expectedBackground.Clear(Color.Lime);
                repeatedBackground.Clear(Color.Lime);
            }
            favoriteButton.DrawToBitmap(expected, new Rectangle(Point.Empty, size));
            favoriteButton.GlyphState = "filled";
            favoriteButton.DrawToBitmap(repeated, new Rectangle(Point.Empty, size));
            favoriteButton.GlyphState = "outline";
            for (var iteration = 0; iteration < 64; iteration++)
            {
                favoriteButton.DrawToBitmap(repeated, new Rectangle(Point.Empty, size));
            }
            return BitmapsMatch(expected, repeated);
        }
        finally
        {
            favoriteButton.GlyphState = originalState;
            favoriteButton.Enabled = originalEnabled;
        }
    }

    private static bool BitmapsMatch(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        for (var y = 0; y < first.Height; y++)
        {
            for (var x = 0; x < first.Width; x++)
            {
                if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) return false;
            }
        }
        return true;
    }

    internal void PrepareSuggestionPreviewForTesting(string query)
    {
        PrepareChromePreviewForTesting();
        addressBarEditing = true;
        omniboxPanel.IsFocused = true;
        addressBar.Text = query;
        addressBar.SelectionStart = addressBar.TextLength;
        addressSuggestionPopup.SetSuggestions(
            AddressSuggestionEngine.GetSuggestions(query, state, BuildOpenTabCandidates()));
        PositionAddressSuggestions();
        addressSuggestionPopup.SelectedIndex = 0;
    }

    internal bool UsesRendererFreeStartPageForTesting =>
        environment is null
        && tabs.Count == 1
        && tabs[0].IsStartPage
        && tabs[0].View is null;

    internal void DrawSuggestionPreviewForTesting(Bitmap target)
    {
        if (addressSuggestionPopup.Visible)
        {
            addressSuggestionPopup.DrawToBitmap(target, addressSuggestionPopup.Bounds);
        }
    }

    internal static BrowserEnvironmentOptionsSnapshot GetEnvironmentOptionsSnapshotForTesting()
    {
        var options = CreateEnvironmentOptions();
        return new(
            EnableTrackingPrevention: options.EnableTrackingPrevention,
            AreBrowserExtensionsEnabled: options.AreBrowserExtensionsEnabled,
            ExclusiveUserDataFolderAccess: options.ExclusiveUserDataFolderAccess,
            AdditionalBrowserArguments: options.AdditionalBrowserArguments);
    }

    internal static BrowserControllerProfileSnapshot GetControllerProfileSnapshotForTesting(bool isPrivateMode) =>
        new(
            IsInPrivateModeEnabled: isPrivateMode,
            ProfileName: isPrivateMode ? "MishaWebPrivate" : null);

    internal static bool ShouldEnableChromeStoreInstallBridge(string? url, bool isPrivateMode) =>
        !isPrivateMode
        && BrowserExtensions.IsChromeWebStoreOrigin(url);

    private void ShowCommandPalette()
    {
        if (isClosing) return;
        commandPalette ??= CreateCommandPalette();
        focusBeforeCommandPalette = FindFocusedControl();
        commandPalette.Open(this);
    }

    private CommandPaletteForm CreateCommandPalette()
    {
        var palette = new CommandPaletteForm(
            query =>
            {
                var (_, effectiveQuery) = ParseScopedCommandQuery(query);
                return CommandRankingEngine.Rank(BuildCommandCandidates(query), effectiveQuery);
            },
            ExecutePaletteCommand);
        palette.Cancelled += (_, _) => RestoreFocusAfterCommandPalette();
        palette.CommandActivated += (_, candidate) =>
        {
            ExecutePaletteCommand(candidate);
            if (ShouldRestoreFocusAfterPalette(candidate)) RestoreFocusAfterCommandPalette();
            else focusBeforeCommandPalette = null;
        };
        return palette;
    }

    private static (CommandSource? Scope, string Query) ParseScopedCommandQuery(string? rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery)) return (null, string.Empty);
        var trimmed = rawQuery.TrimStart();
        if (trimmed.Length == 0) return (null, string.Empty);
        return trimmed[0] switch
        {
            '>' => (CommandSource.BrowserCommand, trimmed[1..].TrimStart()),
            '@' => (CommandSource.OpenTab, trimmed[1..].TrimStart()),
            '#' => (CommandSource.Favorite, trimmed[1..].TrimStart()),
            _ => (null, rawQuery)
        };
    }

    private static bool ShouldRestoreFocusAfterPalette(CommandCandidate candidate)
    {
        if (candidate.Target is BrowserTab or SavedSessionEntry or ClosedTabEntry) return false;
        return candidate.Target as string is not (
            "new-tab"
            or "private-window"
            or "close-tab"
            or "reopen-tab"
            or "duplicate-tab"
            or "session-manager"
            or "downloads"
            or "permissions"
            or "voice-audio-settings"
            or "saved-items"
            or "history"
            or "manage-quick-links"
            or "find");
    }

    private IEnumerable<CommandCandidate> BuildCommandCandidates(string? query)
    {
        static CommandCandidate Action(
            string title,
            string detail,
            string id,
            string shortcut = "") =>
            new(title, detail, CommandSource.BrowserCommand, Shortcut: shortcut, Target: id);

        var (scope, effectiveQuery) = ParseScopedCommandQuery(query);
        if (scope is null or CommandSource.BrowserCommand)
        {
            var showAllActions = scope == CommandSource.BrowserCommand || !string.IsNullOrWhiteSpace(effectiveQuery);
            yield return Action("New tab", "Open a blank start page", "new-tab", "Ctrl+T");
            yield return Action("New incognito window", "Open a temporary incognito window", "private-window", "Ctrl+Shift+N");
            yield return Action("Close tab", "Close the active tab", "close-tab", "Ctrl+W");
            yield return Action("Reopen tab", "Restore the newest recently closed tab", "reopen-tab", "Ctrl+Shift+T");
            yield return Action("Find on page", "Search text in the current page", "find", "Ctrl+F");
            yield return Action("Open downloads", "Review downloads from this runtime session", "downloads", "Ctrl+J");
            if (showAllActions)
            {
                yield return Action("Duplicate tab", "Open another tab at the current address", "duplicate-tab");
                yield return Action(
                    activeTab?.IsPinned == true ? "Unpin tab" : "Pin tab",
                    "Move the active tab in or out of the pinned section",
                    "toggle-pin-tab");
                yield return Action("Unload tab", "Sleep tab; release its WebView while keeping the tab", "unload-tab");
                if (!isPrivateMode)
                {
                    yield return Action("Save current window as a session", "Capture the current tabs as a named session", "save-session");
                    yield return Action("Open session manager", "Open and manage saved sessions", "session-manager");
                    yield return Action("Saved items", "Browse favorites, history, quick links, and closed tabs", "saved-items", "Ctrl+Shift+O");
                    yield return Action("History", "Open browsing history", "history", "Ctrl+H");
                    yield return Action("Manage quick links", "Edit or reorder pinned start page shortcuts", "manage-quick-links");
                }
                yield return Action("Toggle reader mode", "Show a clean reading view for the current page", "reader-mode", "F9");
                yield return Action("Picture-in-Picture", "Pop out the current page's video player", "pip");
                yield return Action("Copy clean page link", "Copy the current page URL without tracking parameters", "copy-clean-link");
                yield return Action("Open permission manager", "Review saved website permissions", "permissions");
                yield return Action("Toggle resource mode", "Cycle memory saver and ultra-light modes", "resource-mode");
                yield return Action("Toggle shield", "Enable or disable the exact-host page shield", "shield");
                yield return Action("Keep active in background", "Protect the current site from lifecycle release", "keep-awake");
                yield return Action(
                    activeTab is not null && IsTabMuted(activeTab) ? "Unmute tab" : "Mute tab",
                    "Mute or unmute audio for the active tab only",
                    "mute-tab",
                    "Ctrl+M");
                yield return Action("Mute site", "Remember mute for the current exact host", "mute-site");
                yield return Action("Voice and audio settings", "Configure sound output and microphone input policies", "voice-audio-settings");
                yield return Action("Fullscreen", "Toggle browser fullscreen", "fullscreen", "F11");
                yield return Action("Add split view", "Open a new side-by-side Multiple Views pane", "add-multi-view", "Ctrl+Shift+\\");
                yield return Action("Split view (2 panes)", "Show 2 panes side-by-side", "split-view");
                yield return Action("Tri view (3 panes)", "Show 3 panes in a 1+2 split grid", "tri-view");
                yield return Action("Quad view", "Show 4 panes side-by-side in a grid", "quad-view");
                yield return Action("Swap panes", "Swap the focused pane with the next pane", "swap-panes", "Ctrl+Alt+S");
                yield return Action(
                    multiViewState.IsFocusedPaneMaximized ? "Restore pane grid" : "Maximize focused pane",
                    "Temporarily expand or restore the focused Multi-View pane",
                    "toggle-maximize-pane",
                    "Ctrl+Alt+Z");
                yield return Action("Exit multiple views", "Return to a single active pane", "exit-multi-view", "Ctrl+Shift+0");
                yield return Action("Toggle website theme", "Change the global website color mode", "theme");
                yield return Action("Clear browsing data", "Clear history, cookies, cache, and saved permissions", "clear-browsing-data", "Ctrl+Shift+Del");
            }
        }

        if (scope is null or CommandSource.OpenTab)
        {
            var mruRanks = new Dictionary<BrowserTab, int>(tabs.Count);
            var mruRankIndex = 0;
            foreach (var orderedTab in mruTabs.Order)
            {
                mruRanks.TryAdd(orderedTab, mruRankIndex++);
            }
            for (var index = 0; index < tabs.Count; index++)
            {
                var tab = tabs[index];
                if (tab.IsClosed) continue;
                yield return new CommandCandidate(
                    tab.Title,
                    BuildTabDetail(tab),
                    CommandSource.OpenTab,
                    tab.IsStartPage ? null : tab.Url,
                    MruRank: mruRanks.TryGetValue(tab, out var rank) ? rank : int.MaxValue,
                    SourceIndex: index,
                    Target: tab);
            }
        }

        if (scope is null or CommandSource.Favorite)
        {
            for (var index = 0; index < state.Bookmarks.Count; index++)
            {
                var bookmark = state.Bookmarks[index];
                yield return new CommandCandidate(
                    bookmark.Title,
                    FormatBrowserChromeUrlForDisplay(bookmark.Url),
                    CommandSource.Favorite,
                    bookmark.Url,
                    SourceIndex: index,
                    Target: bookmark.Url);
            }
        }

        if (!isPrivateMode && scope is null)
        {
            for (var index = 0; index < state.NamedSessions.Count; index++)
            {
                var session = state.NamedSessions[index];
                yield return new CommandCandidate(
                    session.Name,
                    $"{session.Tabs.Count} tabs",
                    CommandSource.Session,
                    SourceIndex: index,
                    Target: session);
            }

            var closedIndex = 0;
            foreach (var closed in closedTabs)
            {
                yield return new CommandCandidate(
                    closed.Title,
                    FormatBrowserChromeUrlForDisplay(closed.Url),
                    CommandSource.RecentlyClosed,
                    closed.Url,
                    SourceIndex: closedIndex++,
                    Target: closed);
            }

            for (var index = 0; index < state.History.Count; index++)
            {
                var history = state.History[index];
                yield return new CommandCandidate(
                    history.Title,
                    FormatBrowserChromeUrlForDisplay(history.Url),
                    CommandSource.History,
                    history.Url,
                    SourceIndex: index,
                    Target: history.Url);
            }
        }
    }

    private string BuildTabDetail(BrowserTab tab)
    {
        var details = new List<string>(6);
        if (multiViewState.IsMultiViewActive && multiViewState.IndexOfTab(tab.Id) is >= 0 and var paneIndex)
        {
            details.Add($"pane {paneIndex + 1}");
        }
        if (tab.IsPinned) details.Add("pinned");
        if (tab.IsDiscarded) details.Add("unloaded");
        else if (tab.IsSuspended) details.Add("sleeping");
        if (tab.IsAudible) details.Add("audible");
        if (tab.ActiveDownloads > 0) details.Add("downloading");
        var displayUrl = FormatBrowserChromeUrlForDisplay(tab.Url);
        var detail = details.Count == 0
            ? displayUrl
            : string.Join(", ", details) + " \u2014 " + displayUrl;
        return TextSafety.TruncateWithEllipsis(detail, MaximumBrowserChromeUrlCharacters);
    }

    private void ExecutePaletteCommand(CommandCandidate candidate)
    {
        if (candidate.Target is BrowserTab tab)
        {
            if (!tab.IsClosed && tabs.Contains(tab)) ActivateTab(tab);
            return;
        }
        if (candidate.Target is SavedSessionEntry session)
        {
            OpenSavedSession(session, replace: false);
            return;
        }
        if (candidate.Target is ClosedTabEntry closed)
        {
            RestoreClosedEntry(closed);
            return;
        }
        if (candidate.Target is string url && candidate.Source is CommandSource.Favorite or CommandSource.History)
        {
            var stillAvailable = candidate.Source == CommandSource.Favorite
                ? state.Bookmarks.Any(item => BrowserPolicy.UrlEquals(item.Url, url))
                : state.History.Any(item => BrowserPolicy.UrlEquals(item.Url, url));
            if (!stillAvailable || !BrowserPolicy.IsHttpUrl(url))
            {
                ShowTransientStatus("That saved address is no longer available");
                return;
            }
            RunUiTask(() => NavigateActiveAsync(url), "Navigation could not start");
            return;
        }

        switch (candidate.Target as string)
        {
            case "new-tab":
                RunUiTask(() => OpenNewTabAsync(StartPage.Url), "Could not open a tab");
                break;
            case "private-window":
                OpenPrivateWindow();
                break;
            case "close-tab":
                if (activeTab is not null) CloseTab(activeTab);
                break;
            case "reopen-tab":
                RunUiTask(RestoreClosedTabAsync, "Could not restore the tab");
                break;
            case "duplicate-tab":
                if (activeTab is not null)
                {
                    RunUiTask(
                        () => OpenNewTabAsync(activeTab.IsStartPage ? StartPage.Url : activeTab.Url),
                        "Could not duplicate the tab");
                }
                break;
            case "toggle-pin-tab":
                if (activeTab is not null) SetTabPinned(activeTab, !activeTab.IsPinned);
                break;
            case "unload-tab":
                UnloadActiveTab();
                break;
            case "save-session":
                SaveCurrentWindowAsSession();
                break;
            case "session-manager":
                ShowSessionManager();
                break;
            case "saved-items":
                ShowSavedItemsDialog();
                break;
            case "history":
                ShowSavedItemsDialog(SavedItemsView.History);
                break;
            case "manage-quick-links":
                ShowManageQuickLinksDialog();
                break;
            case "reader-mode":
                RunUiTask(ToggleReaderModeAsync, "Reader mode unavailable");
                break;
            case "pip":
                RunUiTask(() => TogglePictureInPictureAsync(), "Picture-in-Picture is unavailable");
                break;
            case "copy-clean-link":
                CopyCleanPageLink();
                break;
            case "downloads":
                ShowDownloadsPopup();
                break;
            case "permissions":
                ShowPermissionManager();
                break;
            case "find":
                ShowFindBar();
                break;
            case "resource-mode":
                CycleResourceMode();
                break;
            case "shield":
                ToggleActiveSiteShield();
                break;
            case "keep-awake":
                ToggleActiveKeepAwake();
                break;
            case "mute-tab":
                ToggleActiveTabAudioMute();
                break;
            case "mute-site":
                ToggleActiveSiteMute();
                break;
            case "voice-audio-settings":
                ShowVoiceAudioSettingsDialog();
                break;
            case "fullscreen":
                ToggleFullScreen();
                break;
            case "add-multi-view":
                RunUiTask(AddMultiViewPaneAsync, "Could not add a new view");
                break;
            case "split-view":
                RunUiTask(() => SetMultiViewPresetAsync(MultiViewPreset.Split), "Could not switch to Split view");
                break;
            case "tri-view":
                RunUiTask(() => SetMultiViewPresetAsync(MultiViewPreset.Tri), "Could not switch to Tri view");
                break;
            case "quad-view":
                RunUiTask(() => SetMultiViewPresetAsync(MultiViewPreset.Quad), "Could not switch to Quad view");
                break;
            case "swap-panes":
                SwapFocusedMultiViewPane();
                break;
            case "toggle-maximize-pane":
                ToggleMultiViewPaneMaximized(multiViewState.FocusedPaneIndex);
                break;
            case "exit-multi-view":
                RunUiTask(() => SetMultiViewPresetAsync(MultiViewPreset.Single), "Could not exit multiple views");
                break;
            case "theme":
                ToggleDarkMode();
                break;
            case "clear-browsing-data":
                RunUiTask(ClearBrowsingDataAsync, "Could not clear browsing data");
                break;
        }
    }

    private void RestoreFocusAfterCommandPalette()
    {
        var target = focusBeforeCommandPalette;
        focusBeforeCommandPalette = null;
        if (target is not null && !target.IsDisposed && target.CanFocus)
        {
            target.Focus();
        }
        else if (activeTab?.View is not null && activeTab.Core is not null)
        {
            activeTab.View.Focus();
        }
    }

    private Control? FindFocusedControl()
    {
        Control? current = ActiveControl;
        while (current is ContainerControl container && container.ActiveControl is not null)
        {
            current = container.ActiveControl;
        }
        return current;
    }

    internal void BeginMruSwitch(int direction)
    {
        if (activeTab is null || tabs.Count < 2) return;
        if (mruSnapshot is not null && mruSwitcher?.Visible == true)
        {
            mruSwitcher.MoveSelection(direction);
            return;
        }

        if (mruTabs.Order.Count < tabs.Count)
        {
            var existing = mruTabs.Order.Where(item => !item.IsClosed && tabs.Contains(item)).ToList();
            var missing = tabs.Where(item => !item.IsClosed && !existing.Contains(item)).ToList();
            if (missing.Count > 0) mruTabs.Reset(existing.Concat(missing));
        }

        mruSnapshot = mruTabs.Begin(activeTab);
        if (mruSnapshot.Items.Count == 0)
        {
            mruSnapshot = null;
            return;
        }
        mruSwitcher ??= CreateMruSwitcher();
        var labels = mruSnapshot.Items
            .Take(8)
            .Select(item => $"{item.Title} \u2014 {BuildTabDetail(item)}")
            .ToArray();
        var initialIndex = direction < 0 ? Math.Max(0, labels.Length - 1) : 0;
        openingMruSwitcher = true;
        try
        {
            mruSwitcher.Open(this, labels, initialIndex, commitOnCtrlRelease: true);
        }
        finally
        {
            openingMruSwitcher = false;
        }
    }

    internal bool TryCommitActiveMruSwitch()
    {
        if (mruSnapshot is null || mruSwitcher is not { Visible: true }) return false;
        mruSwitcher.CommitSelection();
        return true;
    }

    private MruSwitcherForm CreateMruSwitcher()
    {
        var switcher = new MruSwitcherForm();
        switcher.Cancelled += (_, _) => mruSnapshot = null;
        switcher.Committed += (_, _) => CommitMruSwitch();
        return switcher;
    }

    private void CommitMruSwitch()
    {
        if (mruSnapshot is null) return;
        var selectedIndex = mruSwitcher?.SelectedIndex ?? -1;
        var selected = selectedIndex >= 0 && selectedIndex < mruSnapshot.Items.Count
            ? mruSnapshot.Items[selectedIndex]
            : null;
        mruSnapshot = null;
        if (selected is not null && !selected.IsClosed && tabs.Contains(selected)) ActivateTab(selected);
    }

    private void CancelMruSwitch()
    {
        mruSnapshot = null;
        mruSwitcher?.CancelSelection();
    }

    private void StartPageSearchBarOnKeyDown(BrowserTab tab, KeyEventArgs e)
    {
        if (tab.IsClosed || activeTab != tab || tab.StartPageView is null) return;
        var bar = tab.StartPageView.SearchBar;
        activeSmartSearchBar = bar;
        smartSearchBarEditing = bar.InputControl.Focused;
        suggestionAnchor = tab.StartPageView.SearchAnchor;

        if ((e.KeyCode is Keys.Down or Keys.Up) && addressSuggestionPopup.Visible)
        {
            addressSuggestionPopup.MoveSelection(e.KeyCode == Keys.Down ? 1 : -1);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Delete && e.Shift && addressSuggestionPopup.TryGetSelected(out var removable))
        {
            RemoveAddressSuggestion(removable);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Enter)
        {
            SubmitAddressInput(bar.Text, bar.CanSubmit, bar.InlineError);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Escape && addressSuggestionPopup.Visible)
        {
            HideAddressSuggestions();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void AddressBarOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Back)
        {
            if (addressBar.SelectionLength > 0 && addressBar.SelectionStart + addressBar.SelectionLength == addressBar.TextLength)
            {
                var typedLength = addressBar.SelectionStart;
                suppressAddressBarAutocomplete = true;
                if (typedLength > 0)
                {
                    isAutocompletingAddressBar = true;
                    try
                    {
                        addressBar.Text = addressBar.Text[..(typedLength - 1)];
                        addressBar.SelectionStart = addressBar.TextLength;
                        addressBar.SelectionLength = 0;
                    }
                    finally
                    {
                        isAutocompletingAddressBar = false;
                    }
                    e.SuppressKeyPress = true;
                    QueueAddressSuggestions();
                    return;
                }
            }
            suppressAddressBarAutocomplete = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            if (addressBar.SelectionLength > 0 && addressBar.SelectionStart + addressBar.SelectionLength == addressBar.TextLength)
            {
                isAutocompletingAddressBar = true;
                try
                {
                    addressBar.Text = addressBar.Text[..addressBar.SelectionStart];
                    addressBar.SelectionStart = addressBar.TextLength;
                    addressBar.SelectionLength = 0;
                }
                finally
                {
                    isAutocompletingAddressBar = false;
                }
                suppressAddressBarAutocomplete = true;
                e.SuppressKeyPress = true;
                return;
            }
            suppressAddressBarAutocomplete = true;
        }
        else if (e.KeyCode is Keys.Right or Keys.Tab)
        {
            if (addressBar.SelectionLength > 0 && addressBar.SelectionStart + addressBar.SelectionLength == addressBar.TextLength)
            {
                addressBar.SelectionStart = addressBar.TextLength;
                addressBar.SelectionLength = 0;
                e.SuppressKeyPress = true;
                return;
            }
        }

        if ((e.KeyCode is Keys.Down or Keys.Up) && addressSuggestionPopup.Visible)
        {
            addressSuggestionPopup.MoveSelection(e.KeyCode == Keys.Down ? 1 : -1);
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Delete && e.Shift && addressSuggestionPopup.TryGetSelected(out var removable))
        {
            RemoveAddressSuggestion(removable);
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Enter)
        {
            SubmitAddressInput(addressBar.Text);
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.Escape)
        {
            if (addressSuggestionPopup.Visible)
            {
                HideAddressSuggestions();
                e.SuppressKeyPress = true;
                return;
            }
            addressBarEditing = false;
            SyncAddressBar();
            activeTab?.View?.Focus();
            e.SuppressKeyPress = true;
            return;
        }

        addressBarEditing = true;
    }

    private void PasteAndGo()
    {
        if (!TryReadPasteAndGoText(out var pasted))
        {
            ShowTransientStatus("Clipboard text is empty, unsafe, or too long");
            return;
        }

        addressBar.Text = pasted.Trim();
        addressBar.SelectionStart = addressBar.TextLength;
        addressBarEditing = false;
        HideAddressSuggestions();
        RunUiTask(() => NavigateActiveAsync(addressBar.Text), "Navigation could not start");
    }

    private bool CanReadPasteAndGoText()
    {
        return TryReadPasteAndGoText(out _);
    }

    private bool TryReadPasteAndGoText(out string pasted)
    {
        pasted = string.Empty;
        try
        {
            pasted = Clipboard.ContainsText(TextDataFormat.UnicodeText)
                ? Clipboard.GetText(TextDataFormat.UnicodeText)
                : string.Empty;
        }
        catch (Exception error)
        {
            stateStore.Log("Could not read clipboard for paste and go", error);
            return false;
        }

        return !string.IsNullOrWhiteSpace(pasted)
            && pasted.Length <= BrowserPolicy.MaximumUrlLength
            && !pasted.Any(char.IsControl);
    }

    private void CopyCleanPageLink()
    {
        var tab = activeTab;
        var source = tab?.Core?.Source ?? tab?.Url;
        if (tab is null || tab.IsStartPage || !BrowserPolicy.IsHttpUrl(source ?? string.Empty))
        {
            ShowTransientStatus("Open an HTTP or HTTPS page before copying its link");
            return;
        }

        var cleaned = CleanLinkPolicy.Clean(source);
        try
        {
            Clipboard.SetText(cleaned);
            ShowTransientStatus(
                string.Equals(cleaned, source, StringComparison.Ordinal)
                    ? "No removable tracking parameters found"
                    : "Clean page link copied");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not copy clean page link", error);
            ShowTransientStatus("Could not copy the page link");
        }
    }

    private void TryApplyAddressBarAutocomplete()
    {
        if (!addressBarEditing || !addressBar.Focused || addressBar.TextLength == 0) return;
        if (addressBar.SelectionStart != addressBar.TextLength) return;

        var typed = addressBar.Text;
        if (typed.Length > 40 || typed.Contains(' ') || typed.Contains('/') || typed.Contains(':'))
        {
            return;
        }

        var topMatch = AddressSuggestionEngine.GetTopMatchHost(typed, state);
        if (!string.IsNullOrEmpty(topMatch)
            && topMatch.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
            && topMatch.Length > typed.Length)
        {
            isAutocompletingAddressBar = true;
            try
            {
                var typedLength = typed.Length;
                addressBar.Text = topMatch;
                addressBar.SelectionStart = typedLength;
                addressBar.SelectionLength = topMatch.Length - typedLength;
            }
            finally
            {
                isAutocompletingAddressBar = false;
            }
        }
    }

    private void UpdateAddressSuggestions()
    {
        var addressSuggestionsActive = addressBarEditing && addressBar.Focused;
        var smartSuggestionsActive = smartSearchBarEditing
            && activeSmartSearchBar is not null
            && activeSmartSearchBar.InputControl.Focused;
        if (lastAddressSuggestionInput == pendingSuggestionInput
            || isClosing
            || (!addressSuggestionsActive && !smartSuggestionsActive))
        {
            HideAddressSuggestions();
            return;
        }

        var query = pendingSuggestionInput;
        if (string.IsNullOrWhiteSpace(query))
        {
            HideAddressSuggestions();
            return;
        }

        lastAddressSuggestionInput = query;
        var localSuggestions = AddressSuggestionEngine.GetSuggestions(query, state, BuildOpenTabCandidates());

        liveSuggestionCts?.Cancel();
        liveSuggestionCts?.Dispose();
        liveSuggestionCts = null;

        if (!isPrivateMode
            && state.EnableLiveSearchSuggestions
            && !BrowserPolicy.IsRecognizedAddressInput(query))
        {
            if (SearchSuggestionService.Instance.TryGetCachedSuggestions(
                    query,
                    searchProviderId,
                    out var cachedLiveQueries))
            {
                var merged = AddressSuggestionEngine.MergeWithLiveSearch(localSuggestions, cachedLiveQueries, query, state);
                addressSuggestionPopup.SetSuggestions(merged);
                PositionAddressSuggestions(suggestionAnchor);
                return;
            }

            addressSuggestionPopup.SetSuggestions(localSuggestions);
            PositionAddressSuggestions(suggestionAnchor);

            var targetAnchor = suggestionAnchor;
            var cts = new CancellationTokenSource();
            liveSuggestionCts = cts;
            _ = FetchLiveSearchSuggestionsAsync(query, localSuggestions, targetAnchor, cts.Token);
            return;
        }

        addressSuggestionPopup.SetSuggestions(localSuggestions);
        PositionAddressSuggestions(suggestionAnchor);
    }

    private async Task FetchLiveSearchSuggestionsAsync(
        string query,
        IReadOnlyList<AddressSuggestion> localSuggestions,
        Control? targetAnchor,
        CancellationToken cancellationToken)
    {
        try
        {
            var liveQueries = await SearchSuggestionService.Instance.GetSearchSuggestionsAsync(
                query,
                searchProviderId,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested || isClosing || IsDisposed) return;
            if (lastAddressSuggestionInput != query) return;
            if (targetAnchor is not null && !ReferenceEquals(suggestionAnchor, targetAnchor)) return;

            var currentlyActive = (addressBarEditing && addressBar.Focused)
                || (smartSearchBarEditing && activeSmartSearchBar is not null && activeSmartSearchBar.InputControl.Focused);
            if (!currentlyActive) return;

            var merged = AddressSuggestionEngine.MergeWithLiveSearch(localSuggestions, liveQueries, query, state);
            addressSuggestionPopup.SetSuggestions(merged, preserveSelection: true);
            PositionAddressSuggestions(targetAnchor);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            stateStore.Log("Live search suggestions error", error);
        }
    }

    private void QueueAddressSuggestions(string? input = null, Control? anchor = null)
    {
        var addressSuggestionsActive = addressBarEditing && addressBar.Focused;
        var smartSuggestionsActive = smartSearchBarEditing
            && activeSmartSearchBar is not null
            && activeSmartSearchBar.InputControl.Focused;
        if (isClosing || (!addressSuggestionsActive && !smartSuggestionsActive))
        {
            addressSuggestionTimer.Stop();
            HideAddressSuggestions();
            return;
        }

        suggestionAnchor = anchor ?? suggestionAnchor ?? omniboxPanel;
        var nextInput = input ?? (addressSuggestionsActive ? addressBar.Text : activeSmartSearchBar?.Text ?? string.Empty);
        if (nextInput == pendingSuggestionInput) return;

        pendingSuggestionInput = nextInput;
        if (string.IsNullOrWhiteSpace(pendingSuggestionInput))
        {
            lastAddressSuggestionInput = string.Empty;
            addressSuggestionTimer.Stop();
            HideAddressSuggestions();
            return;
        }

        addressSuggestionTimer.Stop();
        findDebounceTimer.Stop();
        addressSuggestionTimer.Start();
    }

    private void PositionAddressSuggestions(Control? anchor = null)
    {
        var target = anchor ?? suggestionAnchor ?? omniboxPanel;
        if (!addressSuggestionPopup.Visible || !target.IsHandleCreated) return;
        var origin = PointToClient(target.PointToScreen(new Point(0, target.Height + 4)));
        var availableHeight = Math.Max(0, ClientSize.Height - origin.Y - 8);
        if (availableHeight < addressSuggestionPopup.RowHeight + 2)
        {
            HideAddressSuggestions();
            return;
        }

        var visibleRows = Math.Max(1, (availableHeight - 2) / addressSuggestionPopup.RowHeight);
        addressSuggestionPopup.LimitVisibleRows(visibleRows);

        addressSuggestionPopup.SetBounds(
            origin.X,
            origin.Y,
            target.Width,
            addressSuggestionPopup.PreferredPopupHeight);
        addressSuggestionPopup.BringToFront();
    }

    private void AcceptAddressSuggestion(AddressSuggestion suggestion)
    {
        addressBarEditing = false;
        smartSearchBarEditing = false;
        HideAddressSuggestions();
        if (suggestion.Source == AddressSuggestionSource.OpenTab)
        {
            var matchingTab = tabs.FirstOrDefault(item =>
                    !item.IsClosed
                    && !item.IsStartPage
                    && item != activeTab
                    && BrowserPolicy.UrlEquals(item.Url, suggestion.NavigationTarget))
                ?? tabs.FirstOrDefault(item =>
                    !item.IsClosed
                    && !item.IsStartPage
                    && BrowserPolicy.UrlEquals(item.Url, suggestion.NavigationTarget));
            if (matchingTab is not null)
            {
                ActivateTab(matchingTab, focusPage: true);
                ShowTransientStatus($"Switched to {matchingTab.Title}");
                return;
            }
        }
        if (activeSmartSearchBar is not null && activeSmartSearchBar.InputControl.Focused)
        {
            activeSmartSearchBar.Text = suggestion.AcceptText;
        }
        else
        {
            addressBar.Text = suggestion.AcceptText;
            addressBar.SelectionStart = addressBar.TextLength;
        }
        if (!suggestion.IsSearch)
        {
            RecordSuggestionUsage(suggestion.NavigationTarget);
        }
        RunUiTask(
            () => NavigateActiveAsync(suggestion.NavigationTarget),
            "Navigation could not start");
    }

    private void SubmitAddressInput(string input, bool canSubmit = true, string? inlineError = null)
    {
        if (addressSuggestionPopup.TryGetSelected(out var suggestion))
        {
            AcceptAddressSuggestion(suggestion);
            return;
        }

        if (!canSubmit)
        {
            if (!string.IsNullOrWhiteSpace(inlineError)) ShowTransientStatus(inlineError);
            return;
        }

        HideAddressSuggestions();
        RunUiTask(() => NavigateActiveAsync(input), "Navigation could not start");
    }

    private void RemoveAddressSuggestion(AddressSuggestion suggestion)
    {
        if (suggestion.IsSearch || suggestion.Source == AddressSuggestionSource.OpenTab) return;

        if (isPrivateMode)
        {
            ShowTransientStatus("Private suggestions are not saved");
            return;
        }

        state.DismissedSuggestionUrls.RemoveAll(url => BrowserPolicy.UrlEquals(url, suggestion.NavigationTarget));
        state.DismissedSuggestionUrls.Insert(0, suggestion.NavigationTarget);
        state.SuggestionUsage.RemoveAll(item => BrowserPolicy.UrlEquals(item.Url, suggestion.NavigationTarget));
        if (state.DismissedSuggestionUrls.Count > BrowserStateStore.MaximumDismissedSuggestions)
        {
            state.DismissedSuggestionUrls.RemoveRange(
                BrowserStateStore.MaximumDismissedSuggestions,
                state.DismissedSuggestionUrls.Count - BrowserStateStore.MaximumDismissedSuggestions);
        }

        ScheduleStateSave();
        var query = activeSmartSearchBar is not null && activeSmartSearchBar.InputControl.Focused
            ? activeSmartSearchBar.Text
            : addressBar.Text;
        addressSuggestionPopup.SetSuggestions(
            AddressSuggestionEngine.GetSuggestions(query, state, BuildOpenTabCandidates()));
        PositionAddressSuggestions();
        ShowTransientStatus("Suggestion removed");
    }

    private void RecordSuggestionUsage(string? navigationTarget)
    {
        if (isPrivateMode || string.IsNullOrWhiteSpace(navigationTarget) || !BrowserPolicy.IsHttpUrl(navigationTarget))
        {
            return;
        }

        var acceptedAt = DateTimeOffset.UtcNow;
        var existingIndex = state.SuggestionUsage.FindIndex(item =>
            BrowserPolicy.UrlEquals(item.Url, navigationTarget));

        var acceptedCount = 1;
        if (existingIndex >= 0)
        {
            var currentCount = state.SuggestionUsage[existingIndex].AcceptedCount;
            acceptedCount = currentCount == int.MaxValue ? int.MaxValue : currentCount + 1;
            state.SuggestionUsage.RemoveAt(existingIndex);
        }

        state.SuggestionUsage.Insert(0, new AddressSuggestionUsage(navigationTarget, acceptedCount, acceptedAt));
        if (state.SuggestionUsage.Count > BrowserStateStore.MaximumSuggestionUsageEntries)
        {
            state.SuggestionUsage.RemoveRange(
                BrowserStateStore.MaximumSuggestionUsageEntries,
                state.SuggestionUsage.Count - BrowserStateStore.MaximumSuggestionUsageEntries);
        }

        ScheduleStateSave();
    }

    private void HideAddressSuggestions()
    {
        liveSuggestionCts?.Cancel();
        liveSuggestionCts?.Dispose();
        liveSuggestionCts = null;
        pendingSuggestionInput = string.Empty;
        lastAddressSuggestionInput = string.Empty;
        addressSuggestionPopup.ClearSuggestions();
    }

    private void FindBoxOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.F3)
        {
            RunUiTask(() => FindOnPageAsync(e.Shift), "Find failed");
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            HideFindBar();
            e.SuppressKeyPress = true;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.P))
        {
            ShowCommandPalette();
            return true;
        }
        if (keyData == (Keys.Control | Keys.K))
        {
            FocusAddressBar();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.N))
        {
            OpenPrivateWindow();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.A))
        {
            ShowTabListMenu();
            return true;
        }
        if (keyData is (Keys.Control | Keys.L) or Keys.F6)
        {
            FocusAddressBar();
            return true;
        }
        if (keyData == (Keys.Control | Keys.T))
        {
            RunUiTask(() => OpenNewTabAsync(StartPage.Url), "Could not open a tab");
            return true;
        }
        if (keyData == (Keys.Control | Keys.W))
        {
            if (activeTab is not null) CloseTab(activeTab);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.T))
        {
            RunUiTask(RestoreClosedTabAsync, "Could not restore the tab");
            return true;
        }
        if (keyData == (Keys.Control | Keys.Tab))
        {
            BeginMruSwitch(1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Tab))
        {
            BeginMruSwitch(-1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.PageDown))
        {
            CycleTabs(1);
            return true;
        }
        if (keyData == (Keys.Control | Keys.PageUp))
        {
            CycleTabs(-1);
            return true;
        }
        if (keyData is (Keys.Alt | Keys.Left))
        {
            GoBack();
            return true;
        }
        if (keyData is (Keys.Alt | Keys.Right))
        {
            GoForward();
            return true;
        }
        if (keyData is (Keys.Alt | Keys.Home))
        {
            ShowStartPage(activeTab);
            return true;
        }
        if (keyData is (Keys.Control | Keys.Shift | Keys.R) or (Keys.Control | Keys.F5) or (Keys.Control | Keys.Shift | Keys.F5))
        {
            HardReloadActiveTab();
            return true;
        }
        if (keyData is (Keys.Control | Keys.R) or Keys.F5)
        {
            ReloadOrStop();
            return true;
        }
        if (keyData == (Keys.Control | Keys.H))
        {
            ShowSavedItemsDialog(SavedItemsView.History);
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.O))
        {
            ShowSavedItemsDialog();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Delete))
        {
            RunUiTask(ClearBrowsingDataAsync, "Could not clear browsing data");
            return true;
        }
        if (keyData == (Keys.Control | Keys.M))
        {
            ToggleActiveTabAudioMute();
            return true;
        }
        if (keyData is (Keys.Control | Keys.G) or (Keys.Control | Keys.Shift | Keys.G))
        {
            if (!findBar.Visible || findBox.TextLength == 0)
            {
                ShowFindBar();
                if (findBox.TextLength == 0) ShowTransientStatus("Type a word to find on this page");
            }
            else
            {
                RunUiTask(() => FindOnPageAsync(keyData.HasFlag(Keys.Shift)), "Find failed");
            }
            return true;
        }
        if (keyData == (Keys.Shift | Keys.G)
            && findBar.Visible
            && !findBox.Focused
            && FindFocusedControl() is { } findBarFocus
            && findBar.Contains(findBarFocus))
        {
            if (findBox.TextLength > 0)
            {
                RunUiTask(() => FindOnPageAsync(true), "Find failed");
            }
            return true;
        }
        if (keyData == (Keys.Control | Keys.P))
        {
            PrintPage();
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.S))
        {
            RunUiTask(CapturePageAsync, "Could not capture the page");
            return true;
        }
        if (keyData == (Keys.Control | Keys.F))
        {
            ShowFindBar();
            return true;
        }
        if (keyData == (Keys.Control | Keys.J))
        {
            ShowDownloadsPopup();
            return true;
        }
        if (keyData == Keys.F9)
        {
            RunUiTask(ToggleReaderModeAsync, "Reader mode unavailable");
            return true;
        }
        if (keyData is Keys.F3 or (Keys.Shift | Keys.F3))
        {
            if (!findBar.Visible || findBox.TextLength == 0)
            {
                ShowFindBar();
                if (findBox.TextLength == 0) ShowTransientStatus("Type a word to find on this page");
            }
            else
            {
                RunUiTask(() => FindOnPageAsync(keyData.HasFlag(Keys.Shift)), "Find failed");
            }
            return true;
        }
        if (keyData == (Keys.Control | Keys.D))
        {
            ToggleFavorite();
            return true;
        }
        if (keyData is (Keys.Control | Keys.Oemplus) or (Keys.Control | Keys.Add) or (Keys.Control | Keys.Shift | Keys.Oemplus))
        {
            ChangeZoom(0.10);
            return true;
        }
        if (keyData is (Keys.Control | Keys.OemMinus) or (Keys.Control | Keys.Subtract))
        {
            ChangeZoom(-0.10);
            return true;
        }
        if (keyData is (Keys.Control | Keys.D0) or (Keys.Control | Keys.NumPad0))
        {
            ChangeZoom(0, true);
            return true;
        }
        if (keyData == Keys.F11)
        {
            if (isDomFullScreen && !isFullScreen)
            {
                ExitDomFullScreen();
            }
            else
            {
                ToggleFullScreen();
            }
            return true;
        }
        if (keyData == Keys.Escape)
        {
            if (commandPalette?.Visible == true)
            {
                commandPalette.CloseWithoutActivation();
                return true;
            }
            if (mruSnapshot is not null)
            {
                CancelMruSwitch();
                return true;
            }
            if (addressSuggestionPopup.Visible)
            {
                HideAddressSuggestions();
                return true;
            }
            if (permissionPrompt?.Visible == true && activePermissionRequest is not null)
            {
                permissionPrompt.CancelRequest();
            }
            else if (isDomFullScreen) ExitDomFullScreen();
            else if (findBar.Visible) HideFindBar();
            else if (activeTab?.ReaderModeActive == true)
            {
                RunUiTask(ToggleReaderModeAsync, "Reader mode unavailable");
            }
            else if (isFullScreen) ToggleFullScreen();
            else if (activeTab?.IsLoading == true) ReloadOrStop();
            else
            {
                addressBarEditing = false;
                SyncAddressBar();
                activeTab?.View?.Focus();
            }
            return true;
        }

        if (keyData == (Keys.Control | Keys.Shift | Keys.OemPipe))
        {
            RunUiTask(AddMultiViewPaneAsync, "Could not add a new view");
            return true;
        }
        if (keyData == (Keys.Control | Keys.Shift | Keys.D0))
        {
            RunUiTask(() => SetMultiViewPresetAsync(MultiViewPreset.Single), "Could not exit multiple views");
            return true;
        }
        if (keyData.HasFlag(Keys.Control) && keyData.HasFlag(Keys.Alt) && !keyData.HasFlag(Keys.Shift))
        {
            var paneKey = keyData & Keys.KeyCode;
            if (paneKey is >= Keys.D1 and <= Keys.D4)
            {
                var targetPane = paneKey - Keys.D1;
                if (multiViewState.IsMultiViewActive && targetPane < multiViewState.PaneCount)
                {
                    FocusMultiViewPane(targetPane, focusContent: true);
                }
                return true;
            }
            if (paneKey == Keys.Z)
            {
                ToggleMultiViewPaneMaximized(multiViewState.FocusedPaneIndex);
                return true;
            }
            if (paneKey == Keys.S)
            {
                SwapFocusedMultiViewPane();
                return true;
            }
        }

        if (keyData.HasFlag(Keys.Control) && !keyData.HasFlag(Keys.Alt) && !keyData.HasFlag(Keys.Shift))
        {
            var keyCode = keyData & Keys.KeyCode;
            if (keyCode >= Keys.D1 && keyCode <= Keys.D8)
            {
                ActivateTabByIndex(keyCode - Keys.D1);
                return true;
            }
            if (keyCode == Keys.D9)
            {
                ActivateTabByIndex(tabs.Count - 1);
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        HandleMruKeyUp(e);
    }

    private void HandleMruKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey && mruSnapshot is not null)
        {
            mruSwitcher?.CommitSelection();
        }
    }

    private void CycleTabs(int direction, bool focusPage = true)
    {
        if (tabs.Count < 2 || activeTab is null) return;
        var index = tabs.IndexOf(activeTab);
        ActivateTab(tabs[(index + direction + tabs.Count) % tabs.Count], focusPage);
    }

    private void ActivateTabByIndex(int index, bool focusPage = true)
    {
        if (index >= 0 && index < tabs.Count) ActivateTab(tabs[index], focusPage);
    }

    private void FocusStartPageOrAddressBar()
    {
        FocusAddressBar();
    }

    private void RedirectStartPageSearchToAddressBar(BrowserTab tab, string? pendingText = null)
    {
        if (tab.IsClosed || activeTab != tab || tab.StartPageView is null || isClosing) return;

        var startPage = tab.StartPageView;
        var text = pendingText ?? startPage.SearchText;
        if (startPage.SearchText.Length > 0) startPage.SearchText = string.Empty;
        if (text.Length > 0)
        {
            addressBar.Text = text;
            addressBar.SelectionStart = addressBar.TextLength;
            addressBar.SelectionLength = 0;
        }
        FocusAddressBar(selectAll: text.Length == 0 && addressBar.TextLength == 0);
    }

    private void FocusAddressBar(bool selectAll = true)
    {
        if (!addressBar.CanFocus) return;
        smartSearchBarEditing = false;
        activeSmartSearchBar = null;
        suggestionAnchor = omniboxPanel;
        addressBarEditing = true;
        addressBar.Focus();
        if (selectAll)
        {
            addressBar.SelectAll();
        }
        else
        {
            addressBar.SelectionStart = addressBar.TextLength;
            addressBar.SelectionLength = 0;
        }
        QueueAddressSuggestions();
    }

    private void SyncAddressBar()
    {
        if (addressBarEditing) return;
        HideAddressSuggestions();
        syncingAddressBar = true;
        try
        {
            addressBar.Text = activeTab is null || activeTab.IsStartPage
                ? string.Empty
                : FormatAddressForDisplay(activeTab.Url);
        }
        finally
        {
            syncingAddressBar = false;
        }
    }

    internal static string FormatAddressForDisplay(string? address)
        => TextSafety.FormatUrlForDisplay(address, BrowserPolicy.MaximumUrlLength);

    internal static string FormatHoverStatusForDisplay(string? value)
        => FormatUrlForBrowserChrome(value, MaximumHoverStatusCharacters);

    internal static (string HoverStatus, string LastRaw, string? Pending, bool HasPending)
        ClearHoverStatusSnapshot(
            string? hoverStatus,
            string? lastRaw,
            string? pending,
            bool hasPending) =>
        (string.Empty, string.Empty, null, false);

    private static void ClearTabHoverStatus(BrowserTab tab)
    {
        var cleared = ClearHoverStatusSnapshot(
            tab.HoverStatus,
            tab.LastHoverStatusRaw,
            tab.PendingHoverStatus,
            tab.HasPendingHoverStatus);
        tab.HoverStatus = cleared.HoverStatus;
        tab.LastHoverStatusRaw = cleared.LastRaw;
        tab.PendingHoverStatus = cleared.Pending;
        tab.HasPendingHoverStatus = cleared.HasPending;
    }

    internal static string FormatBrowserChromeUrlForDisplay(string? value)
        => FormatUrlForBrowserChrome(value, MaximumBrowserChromeUrlCharacters);

    private static string FormatUrlForBrowserChrome(string? value, int maximumCharacters)
        => TextSafety.FormatUrlForDisplay(value, maximumCharacters);

    private void UpdateNavigationChrome()
    {
        try
        {
            var core = activeTab?.Core;
            backButton.Enabled = core?.CanGoBack == true;
            forwardButton.Enabled = core?.CanGoForward == true;
            reloadButton.Enabled = core is not null;
            reloadButton.GlyphState = activeTab?.IsLoading == true ? "stop" : "reload";
            reloadButton.AccessibleName = activeTab?.IsLoading == true ? "Stop loading" : "Reload";
        }
        catch
        {
            backButton.Enabled = false;
            forwardButton.Enabled = false;
            reloadButton.Enabled = false;
        }
        UpdateSiteInfo();
        UpdateFavoriteButton();
    }

    private void UpdateSiteInfo()
    {
        var palette = ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode);
        var muted = palette.HighContrast ? palette.FieldText : palette.MutedText;
        var positive = palette.HighContrast ? palette.FieldText : PositiveColor;
        var negative = palette.HighContrast ? palette.FieldText : NegativeColor;
        var tab = activeTab;
        if (tab is null)
        {
            siteInfoButton.GlyphState = "unknown";
            siteInfoButton.ForeColor = muted;
            toolTip.SetToolTip(siteInfoButton, "No active page");
            siteInfoButton.AccessibleName = "Site information: No active page";
            siteInfoButton.AccessibleDescription = "No active page";
            return;
        }

        var host = Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        switch (tab.ConnectionState)
        {
            case ConnectionState.Local:
                siteInfoButton.GlyphState = "local";
                siteInfoButton.ForeColor = positive;
                toolTip.SetToolTip(siteInfoButton, "Offline MishaWeb start page");
                break;
            case ConnectionState.LocalFile:
                siteInfoButton.GlyphState = "local";
                siteInfoButton.ForeColor = positive;
                toolTip.SetToolTip(siteInfoButton, "Local file on this device");
                break;
            case ConnectionState.Secure:
                siteInfoButton.GlyphState = "secure";
                siteInfoButton.ForeColor = positive;
                toolTip.SetToolTip(siteInfoButton, $"Encrypted connection to {host}");
                break;
            case ConnectionState.Insecure:
                siteInfoButton.GlyphState = "warning";
                siteInfoButton.ForeColor = negative;
                toolTip.SetToolTip(siteInfoButton, "Connection is not encrypted");
                break;
            case ConnectionState.CertificateError:
                siteInfoButton.GlyphState = "warning";
                siteInfoButton.ForeColor = negative;
                toolTip.SetToolTip(siteInfoButton, "Certificate error \u2014 connection not trusted");
                break;
            case ConnectionState.Loading:
                siteInfoButton.GlyphState = "loading";
                siteInfoButton.ForeColor = muted;
                toolTip.SetToolTip(siteInfoButton, "Verifying connection");
                break;
            default:
                siteInfoButton.GlyphState = "unknown";
                siteInfoButton.ForeColor = muted;
                toolTip.SetToolTip(siteInfoButton, "Connection status unavailable");
                break;
        }
        var description = toolTip.GetToolTip(siteInfoButton);
        siteInfoButton.AccessibleName = $"Site information: {description}";
        siteInfoButton.AccessibleDescription = description;
    }

    private void UpdateFavoriteButton()
    {
        var palette = ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode);
        var tab = activeTab;
        var isFavorite = tab is not null && state.Bookmarks.Any(item =>
            BrowserPolicy.UrlEquals(item.Url, tab.Url));
        favoriteButton.GlyphState = isFavorite ? "filled" : "outline";
        favoriteButton.ForeColor = palette.HighContrast
            ? palette.FieldText
            : isFavorite
                ? (isPrivateMode ? NativeUiTheme.Lavender : AccentColor)
                : palette.MutedText;
        favoriteButton.AccessibleName = isFavorite ? "Remove from favorites" : "Add to favorites";
    }

    private void ApplyChromeDpiMetrics()
    {
        if (titleBar.ColumnStyles.Count >= 7)
        {
            titleBar.SuspendLayout();
            try
            {
                titleBar.ColumnStyles[0].Width = ScaleChromeLogical(34);
                titleBar.ColumnStyles[2].Width = ScaleChromeLogical(28);
                titleBar.ColumnStyles[3].Width = ScaleChromeLogical(isPrivateMode ? 86 : 4);
                titleBar.ColumnStyles[4].Width = ScaleChromeLogical(46);
                titleBar.ColumnStyles[5].Width = ScaleChromeLogical(46);
                titleBar.ColumnStyles[6].Width = ScaleChromeLogical(46);
            }
            finally
            {
                titleBar.ResumeLayout(false);
            }
        }

        var nextNewTabSize = new Size(ScaleChromeLogical(28), ScaleChromeLogical(27));
        if (newTabButton.Size != nextNewTabSize)
        {
            newTabButton.Size = nextNewTabSize;
        }
        if (incognitoBadge is not null)
        {
            var nextBadgeSize = new Size(ScaleChromeLogical(82), ScaleChromeLogical(26));
            if (incognitoBadge.Size != nextBadgeSize)
            {
                incognitoBadge.Size = nextBadgeSize;
            }
        }
        if (omniboxLayout is { ColumnStyles.Count: >= 3 })
        {
            var embeddedWidth = ScaleToolbarLogical(32);
            omniboxLayout.ColumnStyles[0].Width = embeddedWidth;
            omniboxLayout.ColumnStyles[2].Width = embeddedWidth;
        }
    }

    private void UpdateFindMatchVisualState()
    {
        var palette = ResolveChromeColorPolicy(IsHighContrastActive, isPrivateMode);
        var isNoMatch = findMatchLabel.Text.StartsWith("No match", StringComparison.OrdinalIgnoreCase);
        findMatchLabel.ForeColor = palette.HighContrast
            ? palette.MutedText
            : isNoMatch ? NativeUiTheme.Negative : palette.MutedText;
        findOmniboxPanel.FocusBorderColor = palette.HighContrast
            ? palette.Focus
            : isNoMatch ? NativeUiTheme.Negative : palette.Focus;
    }

    private void UpdateResponsiveToolbar()
    {
        if (toolbar.ColumnStyles.Count < 9) return;
        toolbar.SuspendLayout();
        try
        {
            var navColumnWidth = ScaleToolbarLogical(40);
            toolbar.ColumnStyles[0].Width = navColumnWidth;
            toolbar.ColumnStyles[1].Width = navColumnWidth;
            toolbar.ColumnStyles[2].Width = navColumnWidth;
            toolbar.ColumnStyles[8].Width = ScaleToolbarLogical(42);
            if (omniboxLayout is { ColumnStyles.Count: >= 3 })
            {
                var embeddedWidth = ScaleToolbarLogical(32);
                omniboxLayout.ColumnStyles[0].Width = embeddedWidth;
                omniboxLayout.ColumnStyles[2].Width = embeddedWidth;
            }

            var width = toolbar.ClientSize.Width;
            SetToolbarColumnVisibility(3, width >= ScaleToolbarLogical(560), ScaleToolbarLogical(36));
            SetToolbarColumnVisibility(6, downloadsButtonAutoVisible, ScaleToolbarLogical(38));
            // GetControlFromPosition can omit a control that started hidden; keep
            // the download action explicitly synchronized with its reserved cell.
            downloadsButton.Visible = downloadsButtonAutoVisible;

            var showMultiView = width >= ScaleToolbarLogical(540);
            var compactMultiView = width < ScaleToolbarLogical(840);
            multiViewButton.Compact = compactMultiView;
            SetToolbarColumnVisibility(7, showMultiView, ScaleToolbarLogical(compactMultiView ? 38 : 144));
            multiViewButton.Visible = showMultiView;

            var fixedWidth = toolbar.Padding.Horizontal
                + (int)Math.Ceiling(toolbar.ColumnStyles[0].Width)
                + (int)Math.Ceiling(toolbar.ColumnStyles[1].Width)
                + (int)Math.Ceiling(toolbar.ColumnStyles[2].Width)
                + (int)Math.Ceiling(toolbar.ColumnStyles[3].Width)
                + (int)Math.Ceiling(toolbar.ColumnStyles[6].Width)
                + (int)Math.Ceiling(toolbar.ColumnStyles[7].Width)
                + (int)Math.Ceiling(toolbar.ColumnStyles[8].Width);

            // Keep hover/status geometry stable. Text is ellipsized inside one
            // fixed column, so frequent link changes never trigger measurement or
            // toolbar column churn.
            var preferredStatusWidth = ScaleToolbarLogical(MaximumToolbarStatusWidth);
            var statusVisible = statusLabel.Text.Length > 0
                && width - fixedWidth - preferredStatusWidth >= ScaleToolbarLogical(MinimumToolbarOmniboxWidth) + omniboxPanel.Margin.Horizontal;
            var statusColumn = toolbar.ColumnStyles[5];
            statusColumn.SizeType = SizeType.Absolute;
            statusColumn.Width = statusVisible ? preferredStatusWidth : 0;
            statusLabel.Visible = statusVisible;
        }
        finally
        {
            toolbar.ResumeLayout(true);
        }
    }

    private int ScaleToolbarLogical(int logicalPixels)
    {
        return Math.Max(1, (logicalPixels * toolbar.DeviceDpi + 48) / 96);
    }

    private int ScaleChromeLogical(int logicalPixels)
    {
        return logicalPixels == 0
            ? 0
            : Math.Max(1, (logicalPixels * DeviceDpi + 48) / 96);
    }

    private void UpdateFindBarLayout()
    {
        if (findBar.ColumnStyles.Count < 6) return;
        findBar.SuspendLayout();
        try
        {
            var width = Math.Max(1, findBar.ClientSize.Width);
            var buttonWidth = ScaleChromeLogical(36);
            var matchWidth = ScaleChromeLogical(width >= ScaleChromeLogical(640) ? 110 : 72);
            var rightPadding = ScaleChromeLogical(10);
            var preferredLeft = width >= ScaleChromeLogical(900)
                ? ScaleChromeLogical(244)
                : width >= ScaleChromeLogical(700) ? ScaleChromeLogical(120) : ScaleChromeLogical(12);
            var minimumInput = ScaleChromeLogical(120);
            var fixedControls = matchWidth + (buttonWidth * 4) + rightPadding;
            var leftPadding = Math.Min(
                preferredLeft,
                Math.Max(ScaleChromeLogical(4), width - fixedControls - minimumInput));
            findBar.Padding = new Padding(
                leftPadding,
                ScaleChromeLogical(5),
                rightPadding,
                ScaleChromeLogical(5));
            findBar.ColumnStyles[1].Width = matchWidth;
            for (var index = 2; index <= 5; index++) findBar.ColumnStyles[index].Width = buttonWidth;
        }
        finally
        {
            findBar.ResumeLayout(true);
        }
    }

    private void SetToolbarColumnVisibility(int column, bool visible, float expandedWidth)
    {
        toolbar.ColumnStyles[column].Width = visible ? expandedWidth : 0;
        var control = toolbar.GetControlFromPosition(column, 0);
        if (control is not null) control.Visible = visible;
    }

    private void UpdateWindowTitle()
    {
        var suffix = isPrivateMode ? "Incognito \u2014 MishaWeb" : "MishaWeb";
        var isStartOrNew = activeTab is null
            || activeTab.Title.Equals("New tab", StringComparison.OrdinalIgnoreCase)
            || activeTab.Title.Equals("Incognito", StringComparison.OrdinalIgnoreCase);
        Text = isStartOrNew || activeTab is null
            ? suffix
            : $"{activeTab.Title} \u2014 {suffix}";
    }

    private void UpdateWindowControls()
    {
        var currentState = (WindowState, isFullScreen, isDomFullScreen);
        if (lastAppliedWindowControlState == currentState) return;
        lastAppliedWindowControlState = currentState;

        var isMaximized = WindowState == FormWindowState.Maximized;
        maximizeButton.Glyph = isMaximized ? CaptionGlyph.Restore : CaptionGlyph.Maximize;
        maximizeButton.Invalidate();
        maximizeButton.AccessibleName = isMaximized ? "Restore down" : "Maximize";
        maximizeButton.AccessibleDescription = maximizeButton.AccessibleName;
        toolTip.SetToolTip(maximizeButton, maximizeButton.AccessibleName);
        Padding = isMaximized || isFullScreen || isDomFullScreen ? Padding.Empty : new Padding(1);
        var nextTitleBarPadding = isMaximized ? new Padding(6, 0, 0, 3) : new Padding(6, 3, 0, 3);
        if (titleBar.Padding != nextTitleBarPadding)
        {
            titleBar.Padding = nextTitleBarPadding;
            ResizeTabHeaders();
        }
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
        UpdateWindowControls();
    }

    private void UpdateStatus()
    {
        if (isClosing) return;
        var tab = activeTab;
        if (tab?.HasPendingHoverStatus == true)
        {
            var pendingStatus = tab.PendingHoverStatus ?? string.Empty;
            tab.PendingHoverStatus = null;
            tab.HasPendingHoverStatus = false;
            tab.HoverStatus = FormatHoverStatusForDisplay(pendingStatus);
        }

        var isHoverPrimary = false;
        string primary;
        if (!string.IsNullOrWhiteSpace(transientStatus))
        {
            primary = transientStatus;
        }
        else if (!string.IsNullOrWhiteSpace(tab?.HoverStatus))
        {
            primary = tab.HoverStatus;
            isHoverPrimary = true;
        }
        else if (tab is not null)
        {
            primary = tab.StatusText;
        }
        else
        {
            primary = environment is null ? "Starting\u2026" : "Ready";
        }

        var blocked = tab?.BlockedRequestCount ?? 0;
        var activeDownloads = 0;
        var sleeping = 0;
        var unloaded = 0;
        for (var index = 0; index < tabs.Count; index++)
        {
            var item = tabs[index];
            activeDownloads += item.ActiveDownloads;
            if (item.IsSuspended) sleeping++;
            if (item.IsDiscarded) unloaded++;
        }

        var currentTuple = (primary, isHoverPrimary, blocked, activeDownloads, sleeping, unloaded);
        if (!hasStatusTuple || currentTuple != lastStatusTuple)
        {
            hasStatusTuple = true;
            lastStatusTuple = currentTuple;

            string text;
            if (blocked <= 0 && activeDownloads <= 0 && sleeping <= 0 && unloaded <= 0)
            {
                text = primary;
            }
            else
            {
                var parts = new List<string>(5) { primary };
                if (blocked > 0) parts.Add($"{blocked} blocked");
                if (activeDownloads > 0) parts.Add($"{activeDownloads} downloading");
                if (sleeping > 0) parts.Add($"{sleeping} sleeping");
                if (unloaded > 0) parts.Add($"{unloaded} unloaded");
                text = string.Join(" \u00B7 ", parts);
            }

            var displayText = text.Equals("Ready", StringComparison.Ordinal) ? string.Empty : text;
            if (!text.Equals(lastRenderedStatusText, StringComparison.Ordinal))
            {
                var statusVisibilityChanged = StatusTextVisibilityChanged(statusLabel.Text, displayText);
                lastRenderedStatusText = text;
                statusLabel.Text = displayText;
                statusLabel.AccessibleName = $"Browser status: {text}";
                toolTip.SetToolTip(statusLabel, text);
                if (statusVisibilityChanged) UpdateResponsiveToolbar();
            }
        }

        var semanticStatus = !string.IsNullOrWhiteSpace(transientStatus)
            ? transientStatus
            : string.IsNullOrWhiteSpace(tab?.HoverStatus)
                ? tab?.StatusText ?? (environment is null ? "Starting…" : "Ready")
                : string.Empty;
        if (semanticStatus.Length > 0
            && !semanticStatus.Equals(lastAnnouncedStatus, StringComparison.Ordinal))
        {
            lastAnnouncedStatus = semanticStatus;
            statusLabel.Announce(semanticStatus);
        }
    }

    internal static bool StatusTextVisibilityChanged(string? previous, string? current) =>
        string.IsNullOrEmpty(previous) != string.IsNullOrEmpty(current);

    private void ShowTransientStatus(string message, bool autoClear = true)
    {
        if (isClosing) return;
        transientStatus = message;
        transientStatusTimer.Stop();
        if (autoClear) transientStatusTimer.Start();
        UpdateStatus();
    }

    private void ShowTabOverlay(
        BrowserTab tab,
        string title,
        string detail,
        string? actionText,
        Func<Task>? retryAction)
    {
        tab.OverlayTitle.Text = title;
        tab.OverlayDetail.Text = detail;
        tab.OverlayTitle.AccessibleName = title;
        tab.OverlayDetail.AccessibleName = detail;
        tab.Overlay.AccessibleName = $"{title}. {detail}";
        tab.RetryAction = retryAction;
        tab.OverlayAction.Visible = actionText is not null;
        tab.OverlayAction.Text = actionText ?? string.Empty;
        tab.OverlayAction.AccessibleName = actionText ?? "Tab recovery action";
        if (tab.StartPageView is not null) tab.StartPageView.Visible = false;
        tab.Overlay.Visible = true;
        if (tab.View is not null) tab.View.Visible = false;
        tab.Overlay.BringToFront();
        if (activeTab == tab && actionText is not null)
        {
            PostUi(() =>
            {
                if (!tab.IsClosed && activeTab == tab && tab.OverlayAction.Visible)
                {
                    tab.OverlayAction.Focus();
                }
            });
        }
    }

    private void ToggleFullScreen()
    {
        HideAddressSuggestions();
        isFullScreen = !isFullScreen;
        if (isFullScreen)
        {
            if (hasDomFullScreenWindowState)
            {
                normalWindowState = normalWindowStateBeforeDomFullScreen;
                normalBounds = normalBoundsBeforeDomFullScreen;
                hasDomFullScreenWindowState = false;
            }
            else
            {
                normalWindowState = WindowState;
                normalBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            }
            findBarWasVisibleBeforeFullScreen = findBar.Visible;
            WindowState = FormWindowState.Normal;
            Padding = Padding.Empty;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            Bounds = normalBounds;
            WindowState = normalWindowState;
            findBar.Visible = findBarWasVisibleBeforeFullScreen;
            ResizeTabHeaders();
            UpdateResponsiveToolbar();
            UpdateWindowControls();
        }

        UpdateChromeRowsForFullscreen();
        UpdateWindowControls();
        ScheduleStateSave();
    }

}
