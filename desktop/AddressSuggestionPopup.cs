using System.Drawing.Drawing2D;

namespace MishaWeb;

internal sealed class AddressSuggestionPopup : Control
{
    private static readonly Color SurfaceColor = NativeUiTheme.Chrome;
    private static readonly Color SelectedColor = NativeUiTheme.Selection;
    private static readonly Color BorderColor = NativeUiTheme.Border;
    private static readonly Color PrimaryTextColor = NativeUiTheme.Text;
    private static readonly Color SecondaryTextColor = NativeUiTheme.Muted;
    private static readonly Color AccentColor = NativeUiTheme.Accent;
    private static readonly Font PrimaryFont = new("Segoe UI Semibold", 9.5f);
    private static readonly Font SecondaryFont = new("Segoe UI", 8.25f);
    private static readonly Font PillFont = new("Segoe UI Semibold", 8f);

    private IReadOnlyList<AddressSuggestion> allSuggestions = Array.Empty<AddressSuggestion>();
    private IReadOnlyList<AddressSuggestion> suggestions = Array.Empty<AddressSuggestion>();
    private int selectedIndex = -1;
    private int hoverIndex = -1;
    private int removeHoverIndex = -1;
    private int pressedIndex = -1;
    private bool pressedRemove;

    public AddressSuggestionPopup()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : NativeUiTheme.Window;
        Visible = false;
        TabStop = false;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "Address suggestions";
        AccessibleDescription = "Address, bookmark, history, and search suggestions";
    }

    public event Action<AddressSuggestion>? SuggestionAccepted;
    public event Action<AddressSuggestion>? SuggestionRemoved;

    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            var next = suggestions.Count == 0 ? -1 : Math.Clamp(value, -1, suggestions.Count - 1);
            if (selectedIndex == next) return;
            selectedIndex = next;
            AccessibilityNotifyClients(AccessibleEvents.Selection, selectedIndex);
            Invalidate();
        }
    }

    public int PreferredPopupHeight => suggestions.Count * ScaledRowHeight + 2;

    public int RowHeight => ScaledRowHeight;

    private int ScaledRowHeight => Math.Max(44, (int)Math.Round(48 * DeviceDpi / 96f));

    public void SetSuggestions(IReadOnlyList<AddressSuggestion> items)
    {
        allSuggestions = items ?? Array.Empty<AddressSuggestion>();
        suggestions = allSuggestions;
        selectedIndex = -1;
        hoverIndex = -1;
        removeHoverIndex = -1;
        pressedIndex = -1;
        pressedRemove = false;
        RefreshPresentation();
    }

    public void ClearSuggestions()
    {
        allSuggestions = Array.Empty<AddressSuggestion>();
        suggestions = Array.Empty<AddressSuggestion>();
        selectedIndex = -1;
        hoverIndex = -1;
        removeHoverIndex = -1;
        pressedIndex = -1;
        pressedRemove = false;
        Visible = false;
        Invalidate();
    }

    public void LimitVisibleRows(int maximumRows)
    {
        if (maximumRows <= 0)
        {
            ClearSuggestions();
            return;
        }
        if (allSuggestions.Count <= maximumRows)
        {
            ApplyVisibleSuggestions(allSuggestions);
            return;
        }

        var limited = new AddressSuggestion[maximumRows];
        for (var index = 0; index < maximumRows; index++)
        {
            limited[index] = allSuggestions[index] with { KeyboardIndex = index };
        }
        if (maximumRows > 1 && allSuggestions[^1].IsSearch)
        {
            limited[^1] = allSuggestions[^1] with { KeyboardIndex = maximumRows - 1 };
        }

        ApplyVisibleSuggestions(limited);
    }

    private void ApplyVisibleSuggestions(IReadOnlyList<AddressSuggestion> nextSuggestions)
    {
        var selected = selectedIndex >= 0 && selectedIndex < suggestions.Count
            ? suggestions[selectedIndex]
            : (AddressSuggestion?)null;
        var hovered = hoverIndex >= 0 && hoverIndex < suggestions.Count
            ? suggestions[hoverIndex]
            : (AddressSuggestion?)null;
        var previousSelectedIndex = selectedIndex;
        suggestions = nextSuggestions;
        selectedIndex = FindSuggestionIndex(selected);
        hoverIndex = FindSuggestionIndex(hovered);
        removeHoverIndex = -1;
        pressedIndex = -1;
        pressedRemove = false;
        if (selectedIndex != previousSelectedIndex)
        {
            AccessibilityNotifyClients(AccessibleEvents.Selection, selectedIndex);
        }
        RefreshPresentation();
    }

    private int FindSuggestionIndex(AddressSuggestion? target)
    {
        if (target is not { } candidate) return -1;
        for (var index = 0; index < suggestions.Count; index++)
        {
            var item = suggestions[index];
            if (item.Source == candidate.Source
                && item.Match == candidate.Match
                && string.Equals(item.AcceptText, candidate.AcceptText, StringComparison.Ordinal)
                && string.Equals(item.NavigationTarget, candidate.NavigationTarget, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private void RefreshPresentation()
    {
        AccessibleDescription = suggestions.Count == 0
            ? "No suggestions"
            : $"{suggestions.Count} suggestions available. Press Enter to use your typed input or use Up and Down to choose a suggestion.";
        Height = PreferredPopupHeight;
        Visible = suggestions.Count > 0;
        Invalidate();
    }

    public void MoveSelection(int direction)
    {
        if (suggestions.Count == 0) return;
        if (selectedIndex < 0)
        {
            SelectedIndex = direction < 0 ? suggestions.Count - 1 : 0;
            return;
        }

        SelectedIndex = (selectedIndex + direction + suggestions.Count) % suggestions.Count;
    }

    public bool TryGetSelected(out AddressSuggestion suggestion)
    {
        if (selectedIndex >= 0 && selectedIndex < suggestions.Count)
        {
            suggestion = suggestions[selectedIndex];
            return true;
        }

        suggestion = default;
        return false;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Region is not null)
        {
            var previous = Region;
            Region = null;
            previous.Dispose();
        }
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : NativeUiTheme.Window;
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        if (suggestions.Count > 0) Height = PreferredPopupHeight;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (suggestions.Count == 0) return;
        var index = HitTestRow(e.Location);
        var nextHover = index >= 0 && index < suggestions.Count ? index : -1;
        if (hoverIndex != nextHover)
        {
            hoverIndex = nextHover;
            Invalidate();
        }
        var nextRemoveHover = index >= 0
            && index < suggestions.Count
            && CanRemoveSuggestion(suggestions[index])
            && GetRemoveButtonBounds(index).Contains(e.Location)
                ? index
                : -1;
        if (removeHoverIndex != nextRemoveHover)
        {
            removeHoverIndex = nextRemoveHover;
            Cursor = removeHoverIndex >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hoverIndex = -1;
        removeHoverIndex = -1;
        pressedIndex = -1;
        pressedRemove = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var index = HitTestRow(e.Location);
        if (index < 0 || index >= suggestions.Count) return;
        SelectedIndex = index;
        pressedIndex = index;
        pressedRemove = CanRemoveSuggestion(suggestions[index]) && GetRemoveButtonBounds(index).Contains(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var index = HitTestRow(e.Location);
        var activate = pressedIndex >= 0
            && index == pressedIndex
            && index < suggestions.Count;
        var remove = activate
            && pressedRemove
            && CanRemoveSuggestion(suggestions[index])
            && GetRemoveButtonBounds(index).Contains(e.Location);
        if (activate)
        {
            if (remove) SuggestionRemoved?.Invoke(suggestions[index]);
            else SuggestionAccepted?.Invoke(suggestions[index]);
        }
        pressedIndex = -1;
        pressedRemove = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;

        var g = e.Graphics;
        var highContrast = SystemInformation.HighContrast;
        g.Clear(highContrast ? SystemColors.Window : NativeUiTheme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var radius = Scale(12);
        var containerRect = new RectangleF(0.5f, 0.5f, Math.Max(2f, Width - 1f), Math.Max(2f, Height - 1f));
        using var outline = NativeUiTheme.CreateRoundedPath(containerRect, radius);

        if (highContrast)
        {
            using var hcFill = new SolidBrush(SystemColors.Window);
            g.FillPath(hcFill, outline);
        }
        else
        {
            using var glassFill = new LinearGradientBrush(
                containerRect,
                Color.FromArgb(255, 52, 22, 47),
                Color.FromArgb(255, 29, 12, 26),
                LinearGradientMode.Vertical);
            g.FillPath(glassFill, outline);
        }

        var clipState = g.Save();
        try
        {
            g.SetClip(outline, CombineMode.Intersect);
            if (!highContrast)
            {
                var topSheenRect = new RectangleF(containerRect.X, containerRect.Y, containerRect.Width, Scale(16));
                using var topSheen = new LinearGradientBrush(
                    topSheenRect,
                    Color.FromArgb(44, 255, 240, 250),
                    Color.FromArgb(0, SurfaceColor),
                    LinearGradientMode.Vertical);
                g.FillRectangle(topSheen, topSheenRect);

                var bottomGlowRect = new RectangleF(containerRect.X, containerRect.Bottom - Scale(12), containerRect.Width, Scale(12));
                using var bottomGlow = new LinearGradientBrush(
                    bottomGlowRect,
                    Color.FromArgb(0, SurfaceColor),
                    Color.FromArgb(28, AccentColor),
                    LinearGradientMode.Vertical);
                g.FillRectangle(bottomGlow, bottomGlowRect);
            }

            var rowHeight = ScaledRowHeight;
            for (var index = 0; index < suggestions.Count; index++)
            {
                var row = new Rectangle(1, 1 + (index * rowHeight), Math.Max(0, Width - 2), rowHeight);
                var isSelected = index == selectedIndex;
                var isHovered = index == hoverIndex;
                var highlighted = isSelected || isHovered;
                if (highlighted)
                {
                    DrawRowHighlight(g, row, suggestions[index], isSelected, highContrast);
                }

                DrawSuggestion(g, suggestions[index], row);
                if (CanRemoveSuggestion(suggestions[index])) DrawRemoveButton(g, index);

                var nextHighlighted = (index + 1) == selectedIndex || (index + 1) == hoverIndex;
                if (index < suggestions.Count - 1 && !highlighted && !nextHighlighted)
                {
                    using var separator = new Pen(
                        highContrast
                            ? SystemColors.WindowText
                            : Color.FromArgb(46, BorderColor));
                    g.DrawLine(separator, Scale(55), row.Bottom - 1, Width - Scale(14), row.Bottom - 1);
                }
            }
        }
        finally
        {
            g.Restore(clipState);
        }

        if (highContrast)
        {
            using var border = new Pen(SystemColors.WindowText, 1f);
            g.DrawPath(border, outline);
        }
        else
        {
            using var rimBrush = new LinearGradientBrush(
                containerRect,
                Color.FromArgb(185, 255, 224, 246),
                Color.FromArgb(120, AccentColor),
                LinearGradientMode.Vertical);
            using var border = new Pen(rimBrush, 1.1f);
            g.DrawPath(border, outline);

            var innerRect = containerRect;
            innerRect.Inflate(-1f, -1f);
            if (innerRect.Width > 6f && innerRect.Height > 6f)
            {
                using var innerPath = NativeUiTheme.CreateRoundedPath(innerRect, Math.Max(2f, radius - 1f));
                using var innerRimBrush = new LinearGradientBrush(
                    innerRect,
                    Color.FromArgb(52, 255, 255, 255),
                    Color.FromArgb(14, NativeUiTheme.Focus),
                    LinearGradientMode.Vertical);
                using var innerRimPen = new Pen(innerRimBrush, 1f);
                g.DrawPath(innerRimPen, innerPath);
            }
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance()
    {
        return new SuggestionPopupAccessibleObject(this);
    }

    private void ActivateSuggestion(int index)
    {
        if (index < 0 || index >= suggestions.Count) return;
        SuggestionAccepted?.Invoke(suggestions[index]);
    }

    private sealed class SuggestionPopupAccessibleObject(AddressSuggestionPopup owner)
        : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.List;

        public override int GetChildCount() => owner.suggestions.Count;

        public override AccessibleObject? GetChild(int index)
        {
            return index >= 0 && index < owner.suggestions.Count
                ? new SuggestionRowAccessibleObject(owner, owner.suggestions[index])
                : null;
        }
    }

    private sealed class SuggestionRowAccessibleObject(
        AddressSuggestionPopup owner,
        AddressSuggestion snapshot) : AccessibleObject
    {
        private int CurrentIndex => owner.FindSuggestionIndex(snapshot);

        public override string Name
        {
            get
            {
                var index = CurrentIndex;
                return index >= 0 ? owner.suggestions[index].Title : snapshot.Title;
            }
        }

        public override string Description
        {
            get
            {
                var index = CurrentIndex;
                return index >= 0 ? owner.suggestions[index].Detail : snapshot.Detail;
            }
        }

        public override AccessibleRole Role => AccessibleRole.ListItem;
        public override AccessibleStates State
        {
            get
            {
                var index = CurrentIndex;
                if (index < 0) return AccessibleStates.Unavailable | AccessibleStates.Offscreen;
                return AccessibleStates.Selectable
                    | (owner.selectedIndex == index ? AccessibleStates.Selected : AccessibleStates.None);
            }
        }

        public override string DefaultAction => snapshot.IsSearch ? "Search" : "Open";

        public override Rectangle Bounds
        {
            get
            {
                var index = CurrentIndex;
                if (index < 0) return Rectangle.Empty;
                var row = new Rectangle(0, 1 + (index * owner.ScaledRowHeight), owner.Width, owner.ScaledRowHeight);
                return owner.RectangleToScreen(row);
            }
        }

        public override void DoDefaultAction()
        {
            owner.ActivateSuggestion(CurrentIndex);
        }
    }

    private static bool CanRemoveSuggestion(AddressSuggestion suggestion) =>
        suggestion.Source is AddressSuggestionSource.Bookmark or AddressSuggestionSource.History;

    private void DrawRowHighlight(
        Graphics graphics,
        Rectangle row,
        AddressSuggestion suggestion,
        bool isSelected,
        bool highContrast)
    {
        var pillBounds = new RectangleF(
            row.Left + Scale(6),
            row.Top + Scale(3),
            Math.Max(4f, row.Width - Scale(12)),
            Math.Max(4f, row.Height - Scale(6)));
        var radius = Scale(8);
        using var pillPath = NativeUiTheme.CreateRoundedPath(pillBounds, radius);

        if (highContrast)
        {
            using var hcBrush = new SolidBrush(isSelected ? SystemColors.Highlight : SystemColors.Control);
            graphics.FillPath(hcBrush, pillPath);
            if (isSelected)
            {
                using var hcPen = new Pen(SystemColors.HighlightText, 1f);
                graphics.DrawPath(hcPen, pillPath);
            }
            return;
        }

        if (isSelected)
        {
            using var pillFill = new LinearGradientBrush(
                pillBounds,
                NativeUiTheme.SurfaceRaised,
                SelectedColor,
                LinearGradientMode.Vertical);
            graphics.FillPath(pillFill, pillPath);

            var sheenRect = new RectangleF(
                pillBounds.X + 1f,
                pillBounds.Y + 1f,
                Math.Max(2f, pillBounds.Width - 2f),
                Math.Max(3f, pillBounds.Height * 0.36f));
            using var sheen = new LinearGradientBrush(
                sheenRect,
                Color.FromArgb(38, 255, 240, 250),
                Color.FromArgb(0, NativeUiTheme.SurfaceRaised),
                LinearGradientMode.Vertical);
            graphics.FillRectangle(sheen, sheenRect);

            using var rim = new Pen(Color.FromArgb(190, NativeUiTheme.Focus), 1f);
            graphics.DrawPath(rim, pillPath);

            var cueWidth = Math.Max(2.5f, Scale(3));
            var cueColor = suggestion.Source == AddressSuggestionSource.OpenTab
                ? NativeUiTheme.Positive
                : AccentColor;
            var cueRect = new RectangleF(
                pillBounds.X + Scale(3),
                pillBounds.Y + Scale(7),
                cueWidth,
                Math.Max(4f, pillBounds.Height - Scale(14)));
            using var cuePath = NativeUiTheme.CreateRoundedPath(cueRect, cueWidth / 2f);
            using var cueBrush = new SolidBrush(cueColor);
            graphics.FillPath(cueBrush, cuePath);
            return;
        }

        using var hoverFill = new SolidBrush(Color.FromArgb(150, NativeUiTheme.Hover));
        graphics.FillPath(hoverFill, pillPath);
        using var hoverRim = new Pen(Color.FromArgb(95, BorderColor), 1f);
        graphics.DrawPath(hoverRim, pillPath);
    }

    private void DrawSuggestion(Graphics graphics, AddressSuggestion suggestion, Rectangle row)
    {
        var iconSize = Scale(28);
        var iconBounds = new Rectangle(Scale(17), row.Top + ((row.Height - iconSize) / 2), iconSize, iconSize);
        var iconBoundsF = new RectangleF(iconBounds.X, iconBounds.Y, iconBounds.Width, iconBounds.Height);
        var index = (row.Top - 1) / ScaledRowHeight;
        var selected = index == selectedIndex || index == hoverIndex;
        var highContrast = SystemInformation.HighContrast;
        var iconAccent = suggestion.Source switch
        {
            AddressSuggestionSource.OpenTab => NativeUiTheme.Positive,
            AddressSuggestionSource.Bookmark => NativeUiTheme.BrandCoral,
            AddressSuggestionSource.History => NativeUiTheme.Lavender,
            _ => AccentColor
        };

        using (var iconPath = NativeUiTheme.CreateRoundedPath(iconBoundsF, Scale(8)))
        {
            using var iconBackground = new SolidBrush(
                highContrast
                    ? (selected ? SystemColors.Highlight : SystemColors.Window)
                    : Color.FromArgb(selected ? 68 : 42, iconAccent));
            graphics.FillPath(iconBackground, iconPath);

            using var iconBorder = new Pen(
                highContrast
                    ? (selected ? SystemColors.HighlightText : SystemColors.WindowText)
                    : Color.FromArgb(selected ? 190 : 120, iconAccent),
                1f);
            graphics.DrawPath(iconBorder, iconPath);
        }

        var glyphColor = highContrast
            ? (selected ? SystemColors.HighlightText : SystemColors.WindowText)
            : (selected ? PrimaryTextColor : iconAccent);
        DrawSuggestionGlyph(graphics, suggestion, iconBoundsF, glyphColor);

        var pillText = suggestion.Source == AddressSuggestionSource.OpenTab
            ? "Switch to tab"
            : suggestion.IsTopMatch ? "Top match" : null;
        var badgeWidth = pillText is null
            ? 0
            : suggestion.Source == AddressSuggestionSource.OpenTab ? Scale(92) : Scale(74);
        var rightMargin = Scale(42);
        var textLeft = iconBounds.Right + Scale(10);
        var textWidth = Math.Max(10, row.Right - textLeft - rightMargin - badgeWidth);
        var titleBounds = new Rectangle(textLeft, row.Top + Scale(7), textWidth, Scale(18));
        var detailBounds = new Rectangle(textLeft, titleBounds.Bottom, textWidth, Scale(16));
        TextRenderer.DrawText(
            graphics,
            suggestion.Title,
            PrimaryFont,
            titleBounds,
            highContrast && selected
                ? SystemColors.HighlightText
                : highContrast
                    ? SystemColors.WindowText
                    : suggestion.IsSearch && !selected ? AccentColor : PrimaryTextColor,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(
            graphics,
            suggestion.Detail,
            SecondaryFont,
            detailBounds,
            highContrast && selected
                ? SystemColors.HighlightText
                : highContrast ? SystemColors.WindowText : SecondaryTextColor,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        if (pillText is not null)
        {
            var pillAccent = suggestion.Source == AddressSuggestionSource.OpenTab
                ? NativeUiTheme.Positive
                : AccentColor;
            var badgeBounds = new Rectangle(
                row.Right - rightMargin - badgeWidth,
                row.Top + ((row.Height - Scale(20)) / 2),
                badgeWidth - Scale(6),
                Scale(20));
            var badgeBoundsF = new RectangleF(badgeBounds.X, badgeBounds.Y, badgeBounds.Width, badgeBounds.Height);
            using var badgePath = NativeUiTheme.CreateRoundedPath(badgeBoundsF, badgeBoundsF.Height / 2f);
            if (!highContrast)
            {
                using var badgeBrush = new SolidBrush(Color.FromArgb(40, pillAccent));
                graphics.FillPath(badgeBrush, badgePath);
                using var badgeRim = new Pen(Color.FromArgb(145, pillAccent), 1f);
                graphics.DrawPath(badgeRim, badgePath);
            }
            else if (selected)
            {
                using var hcRim = new Pen(SystemColors.HighlightText, 1f);
                graphics.DrawPath(hcRim, badgePath);
            }

            TextRenderer.DrawText(
                graphics,
                pillText,
                PillFont,
                badgeBounds,
                highContrast
                    ? (selected ? SystemColors.HighlightText : SystemColors.WindowText)
                    : pillAccent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    private void DrawSuggestionGlyph(
        Graphics graphics,
        AddressSuggestion suggestion,
        RectangleF iconBounds,
        Color color)
    {
        var pad = Math.Max(4f, iconBounds.Width * 0.25f);
        var area = RectangleF.Inflate(iconBounds, -pad, -pad);
        var strokeWidth = Math.Max(1.35f, Scale(14) / 10f);

        switch (suggestion.Source)
        {
            case AddressSuggestionSource.Bookmark:
                DrawBookmarkStar(graphics, area, color);
                break;
            case AddressSuggestionSource.History when suggestion.IsSearchHistory:
                DrawHistoryArrow(graphics, area, color, strokeWidth);
                break;
            case AddressSuggestionSource.History:
                DrawClockIcon(graphics, area, color, strokeWidth);
                break;
            case AddressSuggestionSource.OpenTab:
                DrawOpenTabIcon(graphics, area, color, strokeWidth);
                break;
            default:
                DrawSearchMagnifier(graphics, area, color, strokeWidth);
                break;
        }
    }

    private static void DrawBookmarkStar(Graphics graphics, RectangleF area, Color color)
    {
        var cx = area.X + (area.Width / 2f);
        var cy = area.Y + (area.Height / 2f);
        var outerR = Math.Min(area.Width, area.Height) * 0.52f;
        var innerR = outerR * 0.44f;
        var points = new PointF[10];
        for (var i = 0; i < 10; i++)
        {
            var angle = (-MathF.PI / 2f) + (i * MathF.PI / 5f);
            var r = (i % 2 == 0) ? outerR : innerR;
            points[i] = new PointF(cx + (r * MathF.Cos(angle)), cy + (r * MathF.Sin(angle)));
        }

        using var brush = new SolidBrush(color);
        graphics.FillPolygon(brush, points);
    }

    private static void DrawHistoryArrow(Graphics graphics, RectangleF area, Color color, float strokeWidth)
    {
        using var pen = new Pen(color, strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        var ring = RectangleF.Inflate(area, -0.8f, -0.8f);
        graphics.DrawArc(pen, ring, 35f, 265f);

        var tipX = ring.X + (ring.Width * 0.20f);
        var tipY = ring.Y + (ring.Height * 0.25f);
        var barb = Math.Max(2.4f, area.Width * 0.24f);
        graphics.DrawLine(pen, tipX, tipY, tipX + barb, tipY);
        graphics.DrawLine(pen, tipX, tipY, tipX, tipY - barb);
    }

    private static void DrawClockIcon(Graphics graphics, RectangleF area, Color color, float strokeWidth)
    {
        using var pen = new Pen(color, strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var ring = RectangleF.Inflate(area, -0.8f, -0.8f);
        graphics.DrawEllipse(pen, ring);
        var cx = ring.X + (ring.Width / 2f);
        var cy = ring.Y + (ring.Height / 2f);
        graphics.DrawLine(pen, cx, cy, cx, ring.Top + (ring.Height * 0.26f));
        graphics.DrawLine(pen, cx, cy, ring.Right - (ring.Width * 0.26f), cy + (ring.Height * 0.08f));
    }

    private static void DrawOpenTabIcon(Graphics graphics, RectangleF area, Color color, float strokeWidth)
    {
        using var pen = new Pen(color, strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        var card = new RectangleF(area.X + 0.5f, area.Y + (area.Height * 0.14f), area.Width * 0.72f, area.Height * 0.74f);
        using var cardPath = NativeUiTheme.CreateRoundedPath(card, 2.2f);
        graphics.DrawPath(pen, cardPath);

        var midY = area.Y + (area.Height * 0.52f);
        var arrowStart = area.X + (area.Width * 0.42f);
        var arrowEnd = area.Right - 0.5f;
        var barb = Math.Max(2.2f, area.Width * 0.22f);
        graphics.DrawLine(pen, arrowStart, midY, arrowEnd, midY);
        graphics.DrawLine(pen, arrowEnd - barb, midY - barb, arrowEnd, midY);
        graphics.DrawLine(pen, arrowEnd - barb, midY + barb, arrowEnd, midY);
    }

    private static void DrawSearchMagnifier(Graphics graphics, RectangleF area, Color color, float strokeWidth)
    {
        using var pen = new Pen(color, strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var lensSize = Math.Min(area.Width, area.Height) * 0.64f;
        var lens = new RectangleF(area.X + 0.5f, area.Y + 0.5f, lensSize, lensSize);
        graphics.DrawEllipse(pen, lens);
        var handleStart = new PointF(lens.Right - (lensSize * 0.16f), lens.Bottom - (lensSize * 0.16f));
        var handleEnd = new PointF(area.Right - 0.6f, area.Bottom - 0.6f);
        graphics.DrawLine(pen, handleStart, handleEnd);
    }

    private void DrawRemoveButton(Graphics graphics, int index)
    {
        var bounds = GetRemoveButtonBounds(index);
        var selected = index == selectedIndex;
        var highContrast = SystemInformation.HighContrast;
        if (index == removeHoverIndex)
        {
            using var hoverBrush = new SolidBrush(
                highContrast ? SystemColors.Highlight : NativeUiTheme.Hover);
            graphics.FillEllipse(hoverBrush, bounds);
            if (!highContrast)
            {
                using var hoverRim = new Pen(Color.FromArgb(140, BorderColor), 1f);
                graphics.DrawEllipse(hoverRim, bounds);
            }
        }

        var color = highContrast
            ? (selected || index == removeHoverIndex ? SystemColors.HighlightText : SystemColors.WindowText)
            : (index == removeHoverIndex ? PrimaryTextColor : SecondaryTextColor);
        var inset = Scale(8);
        var stroke = Math.Max(1.35f, Scale(13) / 10f);
        using var pen = new Pen(color, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(pen, bounds.Left + inset, bounds.Top + inset, bounds.Right - inset, bounds.Bottom - inset);
        graphics.DrawLine(pen, bounds.Right - inset, bounds.Top + inset, bounds.Left + inset, bounds.Bottom - inset);
    }

    private Rectangle GetRemoveButtonBounds(int index)
    {
        var size = Scale(26);
        var rowTop = 1 + (index * ScaledRowHeight);
        var left = Math.Max(Scale(42), Width - Scale(12) - size);
        return new Rectangle(left, rowTop + ((ScaledRowHeight - size) / 2), size, size);
    }

    private int HitTestRow(Point location)
    {
        if (location.X < 1 || location.X >= Width - 1 || location.Y < 1) return -1;
        var index = (location.Y - 1) / ScaledRowHeight;
        return index >= 0
            && index < suggestions.Count
            && location.Y < 1 + (suggestions.Count * ScaledRowHeight)
                ? index
                : -1;
    }

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
}
