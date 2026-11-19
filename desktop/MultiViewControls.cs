using System.Drawing.Drawing2D;

namespace MishaWeb;

internal sealed class MultiViewToolbarButton : Button
{
    private static readonly Font LabelFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 8.5f);
    private static readonly Font BadgeFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 7.25f);

    private bool hovered;
    private bool pressed;
    private bool compact;
    private int activeViewCount = 1;
    private ChromeColorPolicy palette = new(
        false,
        NativeUiTheme.Chrome,
        NativeUiTheme.Chrome,
        NativeUiTheme.Toolbar,
        NativeUiTheme.Window,
        NativeUiTheme.Field,
        NativeUiTheme.Text,
        NativeUiTheme.Text,
        NativeUiTheme.Muted,
        NativeUiTheme.Border,
        NativeUiTheme.Focus,
        NativeUiTheme.Selection,
        NativeUiTheme.Text,
        NativeUiTheme.Hover,
        NativeUiTheme.Pressed,
        NativeUiTheme.Hover,
        NativeUiTheme.Pressed,
        NativeUiTheme.Surface,
        NativeUiTheme.Text,
        NativeUiTheme.Border,
        NativeUiTheme.Text);

    public MultiViewToolbarButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        AccessibleName = "Multiple views";
        AccessibleDescription = "Add side-by-side split, tri, or quad view panes";
        AccessibleRole = AccessibleRole.PushButton;
    }

    public bool Compact
    {
        get => compact;
        set
        {
            if (compact == value) return;
            compact = value;
            Invalidate();
        }
    }

    public int ActiveViewCount
    {
        get => activeViewCount;
        set
        {
            var clamped = Math.Clamp(value, 1, 4);
            if (activeViewCount == clamped) return;
            activeViewCount = clamped;
            Invalidate();
        }
    }

    public ChromeColorPolicy Palette
    {
        get => palette;
        set
        {
            if (palette == value) return;
            palette = value;
            Invalidate();
        }
    }

    protected override bool ShowFocusCues => true;

    protected override void OnMouseEnter(EventArgs e)
    {
        hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = false;
        pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left) pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        pressed = false;
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var graphics = pevent.Graphics;
        graphics.Clear(palette.ToolbarSurface);
        if (ClientSize.Width <= 4 || ClientSize.Height <= 4) return;

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = Math.Max(1f, DeviceDpi / 96f);
        var active = activeViewCount > 1;
        var fill = palette.HighContrast
            ? (pressed || hovered ? palette.Selection : active ? palette.Selection : palette.ToolbarSurface)
            : pressed
                ? palette.PressedSurface
                : hovered
                    ? palette.HoverSurface
                    : active
                        ? Color.FromArgb(88, 34, 72)
                        : palette.ToolbarSurface;

        var pillRect = new RectangleF(1f, 2f, Math.Max(2f, ClientSize.Width - 2f), Math.Max(2f, ClientSize.Height - 4f));
        var radius = Math.Min(pillRect.Height / 2f, 9f * scale);
        using var pillPath = NativeUiTheme.CreateRoundedPath(pillRect, radius);
        using (var bgBrush = new SolidBrush(fill))
        {
            graphics.FillPath(bgBrush, pillPath);
        }

        if (active || hovered || Focused || palette.HighContrast)
        {
            var borderColor = palette.HighContrast
                ? palette.ChromeText
                : Focused ? palette.Focus : active ? NativeUiTheme.Accent : palette.Border;
            using var borderPen = new Pen(borderColor, Focused ? 1.5f : 1f);
            graphics.DrawPath(borderPen, pillPath);
        }

        var foreground = palette.HighContrast && (pressed || hovered || active)
            ? palette.SelectionText
            : palette.ChromeText;

        var iconSide = Math.Max(12f, 13.5f * scale);
        var showLabel = !compact && ClientSize.Width >= (int)Math.Ceiling(90 * scale);
        var leftPad = showLabel ? 8f * scale : (ClientSize.Width - iconSide) / 2f;
        var iconRect = new RectangleF(
            leftPad,
            (ClientSize.Height - iconSide) / 2f,
            iconSide,
            iconSide);

        DrawGridGlyph(graphics, iconRect, activeViewCount, foreground, scale);

        if (showLabel)
        {
            var badgeReserve = active ? (int)Math.Ceiling(20 * scale) : (int)Math.Ceiling(6 * scale);
            var textX = (int)Math.Ceiling(iconRect.Right + (5 * scale));
            var textRect = new Rectangle(
                textX,
                0,
                Math.Max(1, ClientSize.Width - textX - badgeReserve),
                ClientSize.Height);
            TextRenderer.DrawText(
                graphics,
                "Multiple views",
                LabelFont,
                textRect,
                foreground,
                TextFormatFlags.Left
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine);
        }

        if (active)
        {
            var badgeSize = Math.Max(12f, 13f * scale);
            var badgeRect = showLabel
                ? new RectangleF(ClientSize.Width - badgeSize - (6f * scale), (ClientSize.Height - badgeSize) / 2f, badgeSize, badgeSize)
                : new RectangleF(iconRect.Right - (badgeSize * 0.45f), iconRect.Top - (badgeSize * 0.25f), badgeSize, badgeSize);
            using var badgeBrush = new SolidBrush(palette.HighContrast ? palette.SelectionText : NativeUiTheme.Accent);
            graphics.FillEllipse(badgeBrush, badgeRect);
            TextRenderer.DrawText(
                graphics,
                activeViewCount.ToString(),
                BadgeFont,
                Rectangle.Round(badgeRect),
                palette.HighContrast ? palette.ChromeSurface : NativeUiTheme.AccentText,
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding
                | TextFormatFlags.SingleLine);
        }
    }

    private static void DrawGridGlyph(Graphics graphics, RectangleF bounds, int viewCount, Color color, float scale)
    {
        using var pen = new Pen(color, Math.Max(1.1f, 1.25f * scale));
        using var outer = NativeUiTheme.CreateRoundedPath(bounds, 2.5f * scale);
        graphics.DrawPath(pen, outer);

        var midX = bounds.Left + (bounds.Width / 2f);
        var midY = bounds.Top + (bounds.Height / 2f);
        graphics.DrawLine(pen, midX, bounds.Top + 1f, midX, bounds.Bottom - 1f);
        if (viewCount == 3)
        {
            graphics.DrawLine(pen, midX, midY, bounds.Right - 1f, midY);
        }
        else if (viewCount >= 4)
        {
            graphics.DrawLine(pen, bounds.Left + 1f, midY, bounds.Right - 1f, midY);
        }
    }
}

internal sealed class PaneHeaderBar : Control
{
    private static readonly Font TitleFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 8.25f);
    private static readonly Font IndexFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 7.25f);

    private string paneTitle = "New tab";
    private bool isFocusedPane;
    private bool isPrivateMode;
    private bool highContrast;
    private bool isAudible;
    private bool isMuted;
    private bool closeHovered;
    private bool switchHovered;
    private bool audioHovered;
    private Rectangle closeBounds;
    private Rectangle switchBounds;
    private Rectangle audioBounds;

    public PaneHeaderBar(int paneIndex)
    {
        PaneIndex = paneIndex;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.StandardClick
            | ControlStyles.StandardDoubleClick
            | ControlStyles.UserPaint,
            true);
        TabStop = true;
        AccessibleRole = AccessibleRole.TitleBar;
        AccessibleName = $"View pane {paneIndex + 1}";
        AccessibleDescription = "Activate pane, switch tab, toggle audio, or close pane";
    }

    public int PaneIndex { get; }
    public event EventHandler? PaneActivated;
    public event EventHandler? CloseRequested;
    public event EventHandler<Point>? TabSwitchRequested;
    public event EventHandler? AudioToggleRequested;
    public event EventHandler? MaximizeToggleRequested;

    public bool IsFocusedPane
    {
        get => isFocusedPane;
        set
        {
            if (isFocusedPane == value) return;
            isFocusedPane = value;
            Invalidate();
        }
    }

    public void UpdateMetadata(
        string title,
        bool focused,
        bool privateMode,
        bool highContrastActive,
        bool audible = false,
        bool muted = false)
    {
        var normalized = TextSafety.SanitizeSingleLine(title, 96);
        if (normalized.Length == 0) normalized = "New tab";
        if (string.Equals(paneTitle, normalized, StringComparison.Ordinal)
            && isFocusedPane == focused
            && isPrivateMode == privateMode
            && highContrast == highContrastActive
            && isAudible == audible
            && isMuted == muted)
        {
            return;
        }
        var audioStateChanged = (isAudible || isMuted) != (audible || muted);
        paneTitle = normalized;
        isFocusedPane = focused;
        isPrivateMode = privateMode;
        highContrast = highContrastActive;
        isAudible = audible;
        isMuted = muted;
        if (audioStateChanged) RecalculateButtonBounds();
        AccessibleName = $"Pane {PaneIndex + 1}: {paneTitle}";
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecalculateButtonBounds();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        RecalculateButtonBounds();
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            PaneActivated?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.W && e.Control && !closeBounds.IsEmpty)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.M && e.Control && !audioBounds.IsEmpty)
        {
            AudioToggleRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Down && e.Alt && !switchBounds.IsEmpty)
        {
            TabSwitchRequested?.Invoke(this, PointToScreen(new Point(switchBounds.Left, Height)));
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.F10 && e.Shift && !switchBounds.IsEmpty)
        {
            TabSwitchRequested?.Invoke(this, PointToScreen(new Point(switchBounds.Left, Height)));
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var nextClose = closeBounds.Contains(e.Location);
        var nextSwitch = !nextClose && switchBounds.Contains(e.Location);
        var nextAudio = !nextClose && !nextSwitch && !audioBounds.IsEmpty && audioBounds.Contains(e.Location);
        if (nextClose != closeHovered || nextSwitch != switchHovered || nextAudio != audioHovered)
        {
            closeHovered = nextClose;
            switchHovered = nextSwitch;
            audioHovered = nextAudio;
            Cursor = nextClose || nextSwitch || nextAudio ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (closeHovered || switchHovered || audioHovered)
        {
            closeHovered = false;
            switchHovered = false;
            audioHovered = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (closeBounds.Contains(e.Location))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!audioBounds.IsEmpty && audioBounds.Contains(e.Location))
        {
            AudioToggleRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        PaneActivated?.Invoke(this, EventArgs.Empty);
        if (switchBounds.Contains(e.Location))
        {
            TabSwitchRequested?.Invoke(this, PointToScreen(new Point(switchBounds.Left, Height)));
        }
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        var local = PointToClient(Cursor.Position);
        if (closeBounds.Contains(local)
            || switchBounds.Contains(local)
            || (!audioBounds.IsEmpty && audioBounds.Contains(local)))
        {
            return;
        }
        MaximizeToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 2 || Height <= 2) return;
        if (closeBounds.IsEmpty) RecalculateButtonBounds();

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = Math.Max(1f, DeviceDpi / 96f);

        var bgColor = highContrast
            ? (isFocusedPane ? SystemColors.Highlight : SystemColors.Control)
            : isFocusedPane
                ? (isPrivateMode ? Color.FromArgb(46, 28, 60) : Color.FromArgb(48, 22, 46))
                : (isPrivateMode ? Color.FromArgb(26, 16, 36) : Color.FromArgb(30, 14, 30));
        var textColor = highContrast
            ? (isFocusedPane ? SystemColors.HighlightText : SystemColors.ControlText)
            : isFocusedPane ? NativeUiTheme.Text : NativeUiTheme.Muted;

        using (var bg = new SolidBrush(bgColor))
        {
            graphics.FillRectangle(bg, ClientRectangle);
        }

        if (isFocusedPane)
        {
            var barColor = highContrast ? SystemColors.HighlightText : NativeUiTheme.Accent;
            using var accentBrush = new SolidBrush(barColor);
            graphics.FillRectangle(accentBrush, 0, 0, Width, Math.Max(2, (int)Math.Round(2f * scale)));
        }

        using (var bottomLine = new Pen(highContrast ? SystemColors.WindowText : NativeUiTheme.Border, 1f))
        {
            graphics.DrawLine(bottomLine, 0, Height - 1, Width - 1, Height - 1);
        }

        var badgeSize = Math.Max(14f, 15f * scale);
        var badgeRect = new RectangleF(6f * scale, (Height - badgeSize) / 2f, badgeSize, badgeSize);
        var badgeFill = highContrast
            ? SystemColors.WindowText
            : isFocusedPane ? NativeUiTheme.Accent : Color.FromArgb(72, 34, 66);
        using (var badgeBrush = new SolidBrush(badgeFill))
        {
            graphics.FillEllipse(badgeBrush, badgeRect);
        }
        TextRenderer.DrawText(
            graphics,
            (PaneIndex + 1).ToString(),
            IndexFont,
            Rectangle.Round(badgeRect),
            highContrast ? SystemColors.Window : (isFocusedPane ? NativeUiTheme.AccentText : NativeUiTheme.Text),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        var titleLeft = (int)Math.Ceiling(badgeRect.Right + (6f * scale));
        var rightLimit = !audioBounds.IsEmpty
            ? audioBounds.Left
            : (switchBounds.Width > 0 ? switchBounds.Left : (closeBounds.Width > 0 ? closeBounds.Left : Width));
        var titleRight = Math.Max(titleLeft + 1, rightLimit - (int)Math.Ceiling(4f * scale));
        var titleRect = new Rectangle(titleLeft, 0, Math.Max(1, titleRight - titleLeft), Height);
        TextRenderer.DrawText(
            graphics,
            paneTitle,
            TitleFont,
            titleRect,
            textColor,
            TextFormatFlags.Left
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPadding
            | TextFormatFlags.NoPrefix
            | TextFormatFlags.SingleLine);

        if (!audioBounds.IsEmpty)
        {
            if (audioHovered)
            {
                using var hoverBrush = new SolidBrush(highContrast ? SystemColors.Highlight : NativeUiTheme.Hover);
                graphics.FillRectangle(hoverBrush, audioBounds);
            }
            var audioColor = highContrast
                ? textColor
                : isMuted ? NativeUiTheme.Muted : NativeUiTheme.Accent;
            using var audioPen = new Pen(audioColor, Math.Max(1.2f, 1.3f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var cx = audioBounds.Left + (audioBounds.Width / 2f);
            var cy = audioBounds.Top + (audioBounds.Height / 2f);
            var s = 2.8f * scale;
            graphics.DrawPolygon(audioPen,
            [
                new PointF(cx - s, cy - (s * 0.45f)),
                new PointF(cx - (s * 0.35f), cy - (s * 0.45f)),
                new PointF(cx + (s * 0.35f), cy - s),
                new PointF(cx + (s * 0.35f), cy + s),
                new PointF(cx - (s * 0.35f), cy + (s * 0.45f)),
                new PointF(cx - s, cy + (s * 0.45f))
            ]);
            if (isMuted)
            {
                graphics.DrawLine(audioPen, cx - s, cy - s, cx + s, cy + s);
            }
            else
            {
                graphics.DrawArc(audioPen, cx - (s * 0.2f), cy - (s * 0.85f), s * 1.35f, s * 1.7f, -45, 90);
            }
        }

        if (switchBounds.Width > 0)
        {
            if (switchHovered)
            {
                using var hoverBrush = new SolidBrush(highContrast ? SystemColors.Highlight : NativeUiTheme.Hover);
                graphics.FillRectangle(hoverBrush, switchBounds);
            }
            using var arrowPen = new Pen(textColor, Math.Max(1.2f, 1.3f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var cx = switchBounds.Left + (switchBounds.Width / 2f);
            var cy = switchBounds.Top + (switchBounds.Height / 2f);
            var d = 3f * scale;
            graphics.DrawLines(arrowPen,
            [
                new PointF(cx - d, cy - (d * 0.4f)),
                new PointF(cx, cy + (d * 0.6f)),
                new PointF(cx + d, cy - (d * 0.4f))
            ]);
        }

        if (closeBounds.Width > 0)
        {
            if (closeHovered)
            {
                using var hoverBrush = new SolidBrush(highContrast ? SystemColors.Highlight : Color.FromArgb(148, 36, 74));
                graphics.FillRectangle(hoverBrush, closeBounds);
            }
            using var closePen = new Pen(
                closeHovered && !highContrast ? Color.White : textColor,
                Math.Max(1.2f, 1.35f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            var cx = closeBounds.Left + (closeBounds.Width / 2f);
            var cy = closeBounds.Top + (closeBounds.Height / 2f);
            var half = 3.5f * scale;
            graphics.DrawLine(closePen, cx - half, cy - half, cx + half, cy + half);
            graphics.DrawLine(closePen, cx + half, cy - half, cx - half, cy + half);
        }

        if (Focused && ShowFocusCues)
        {
            var focusRect = Rectangle.Inflate(ClientRectangle, -1, -1);
            if (focusRect.Width > 2 && focusRect.Height > 2)
            {
                ControlPaint.DrawFocusRectangle(
                    graphics,
                    focusRect,
                    highContrast ? textColor : NativeUiTheme.Focus,
                    bgColor);
            }
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance()
    {
        return new PaneHeaderAccessibleObject(this);
    }

    private void RecalculateButtonBounds()
    {
        var scale = Math.Max(1f, DeviceDpi / 96f);
        var buttonSize = Math.Max(18, Math.Min(Height - 2, (int)Math.Round(22 * scale)));
        var gap = Math.Max(2, (int)Math.Round(2 * scale));
        var rightMargin = Math.Max(3, (int)Math.Round(3 * scale));
        var y = Math.Max(1, (Height - buttonSize) / 2);
        var closeRight = Math.Max(0, Width - rightMargin);
        closeBounds = Width >= buttonSize * 2 + (int)Math.Round(24 * scale)
            ? new Rectangle(closeRight - buttonSize, y, buttonSize, buttonSize)
            : Rectangle.Empty;
        switchBounds = Width >= buttonSize * 3 + (int)Math.Round(32 * scale)
            ? new Rectangle(closeBounds.Left - buttonSize - gap, y, buttonSize, buttonSize)
            : Rectangle.Empty;
        audioBounds = (isAudible || isMuted) && Width >= buttonSize * 4 + (int)Math.Round(40 * scale) && !switchBounds.IsEmpty
            ? new Rectangle(switchBounds.Left - buttonSize - gap, y, buttonSize, buttonSize)
            : Rectangle.Empty;
    }

    private enum HeaderActionKind
    {
        Audio,
        SwitchTab,
        Close
    }

    private List<HeaderActionKind> GetVisibleActions()
    {
        var actions = new List<HeaderActionKind>(3);
        if (!audioBounds.IsEmpty) actions.Add(HeaderActionKind.Audio);
        if (!switchBounds.IsEmpty) actions.Add(HeaderActionKind.SwitchTab);
        if (!closeBounds.IsEmpty) actions.Add(HeaderActionKind.Close);
        return actions;
    }

    private Rectangle GetActionBounds(HeaderActionKind kind) => kind switch
    {
        HeaderActionKind.Audio => audioBounds,
        HeaderActionKind.SwitchTab => switchBounds,
        HeaderActionKind.Close => closeBounds,
        _ => Rectangle.Empty
    };

    private void InvokeAction(HeaderActionKind kind)
    {
        switch (kind)
        {
            case HeaderActionKind.Audio:
                AudioToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case HeaderActionKind.SwitchTab:
                PaneActivated?.Invoke(this, EventArgs.Empty);
                TabSwitchRequested?.Invoke(this, PointToScreen(new Point(switchBounds.Left, Height)));
                break;
            case HeaderActionKind.Close:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private sealed class PaneHeaderAccessibleObject(PaneHeaderBar owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.TitleBar;

        public override AccessibleStates State => base.State
            | AccessibleStates.Selectable
            | (owner.isFocusedPane ? AccessibleStates.Selected : AccessibleStates.None);

        public override string DefaultAction => "Activate pane";

        public override void DoDefaultAction()
        {
            if (!owner.IsDisposed) owner.PaneActivated?.Invoke(owner, EventArgs.Empty);
        }

        public override int GetChildCount() => owner.IsDisposed ? 0 : owner.GetVisibleActions().Count;

        public override AccessibleObject? GetChild(int index)
        {
            if (owner.IsDisposed) return null;
            var actions = owner.GetVisibleActions();
            return index >= 0 && index < actions.Count
                ? new PaneHeaderActionAccessibleObject(this, owner, actions[index])
                : null;
        }

        public override AccessibleObject? HitTest(int x, int y)
        {
            if (owner.IsDisposed || !owner.IsHandleCreated) return null;
            var client = owner.PointToClient(new Point(x, y));
            foreach (var action in owner.GetVisibleActions())
            {
                if (owner.GetActionBounds(action).Contains(client))
                {
                    return new PaneHeaderActionAccessibleObject(this, owner, action);
                }
            }
            return base.HitTest(x, y);
        }
    }

    private sealed class PaneHeaderActionAccessibleObject(
        PaneHeaderAccessibleObject parent,
        PaneHeaderBar owner,
        HeaderActionKind kind) : AccessibleObject
    {
        public override AccessibleObject Parent => parent;

        public override AccessibleRole Role => AccessibleRole.PushButton;

        public override string Name => kind switch
        {
            HeaderActionKind.Audio => owner.isMuted ? $"Unmute pane {owner.PaneIndex + 1}" : $"Mute pane {owner.PaneIndex + 1}",
            HeaderActionKind.SwitchTab => $"Switch tab in pane {owner.PaneIndex + 1}",
            HeaderActionKind.Close => $"Close pane {owner.PaneIndex + 1}",
            _ => string.Empty
        };

        public override string DefaultAction => kind switch
        {
            HeaderActionKind.Audio => owner.isMuted ? "Unmute" : "Mute",
            HeaderActionKind.SwitchTab => "Switch tab",
            HeaderActionKind.Close => "Close",
            _ => "Press"
        };

        public override AccessibleStates State
        {
            get
            {
                if (owner.IsDisposed || !owner.IsHandleCreated || owner.GetActionBounds(kind).IsEmpty)
                {
                    return AccessibleStates.Unavailable | AccessibleStates.Offscreen;
                }
                return AccessibleStates.Focusable;
            }
        }

        public override Rectangle Bounds
        {
            get
            {
                if (owner.IsDisposed || !owner.IsHandleCreated) return Rectangle.Empty;
                var local = owner.GetActionBounds(kind);
                return local.IsEmpty ? Rectangle.Empty : owner.RectangleToScreen(local);
            }
        }

        public override void DoDefaultAction()
        {
            if (!owner.IsDisposed) owner.InvokeAction(kind);
        }
    }
}

internal sealed class MultiViewSplitter : Control
{
    private const long DragCoalesceIntervalMs = 16;
    private readonly bool isVertical;
    private bool hovered;
    private bool dragging;
    private Rectangle dragRegionBounds;
    private long lastDragNotificationTick;
    private float? pendingDragRatio;

    public MultiViewSplitter(bool isVertical)
    {
        this.isVertical = isVertical;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
        TabStop = false;
        Cursor = isVertical ? Cursors.SizeWE : Cursors.SizeNS;
        AccessibleRole = AccessibleRole.Separator;
        AccessibleName = isVertical ? "Horizontal view splitter" : "Vertical view splitter";
    }

    public event Action<float>? RatioDragged;
    public event Action? DragCompleted;
    public event Action? ResetRequested;

    public void UpdateDragRegion(Rectangle region) => dragRegionBounds = region;

    protected override void OnMouseEnter(EventArgs e)
    {
        hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            dragging = true;
            lastDragNotificationTick = 0;
            pendingDragRatio = null;
            Capture = true;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging || Parent is null) return;

        var point = Parent.PointToClient(Cursor.Position);
        var region = dragRegionBounds.Width > 0 && dragRegionBounds.Height > 0
            ? dragRegionBounds
            : Parent.ClientRectangle;

        float ratio;
        if (isVertical)
        {
            ratio = (point.X - region.X) / (float)Math.Max(1, region.Width);
        }
        else
        {
            ratio = (point.Y - region.Y) / (float)Math.Max(1, region.Height);
        }

        var now = Environment.TickCount64;
        if (lastDragNotificationTick == 0 || now - lastDragNotificationTick >= DragCoalesceIntervalMs)
        {
            lastDragNotificationTick = now;
            pendingDragRatio = null;
            RatioDragged?.Invoke(ratio);
        }
        else
        {
            pendingDragRatio = ratio;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        CompleteDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (dragging && !Capture)
        {
            CompleteDrag();
        }
    }

    private void CompleteDrag()
    {
        if (!dragging) return;
        dragging = false;
        if (Capture) Capture = false;
        if (pendingDragRatio is { } finalRatio)
        {
            pendingDragRatio = null;
            RatioDragged?.Invoke(finalRatio);
        }
        DragCompleted?.Invoke();
        Invalidate();
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        ResetRequested?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var color = SystemInformation.HighContrast
            ? (dragging || hovered ? SystemColors.Highlight : SystemColors.WindowFrame)
            : (dragging || hovered ? NativeUiTheme.Accent : NativeUiTheme.Border);
        using var brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }
}
