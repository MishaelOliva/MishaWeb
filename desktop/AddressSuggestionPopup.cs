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
    private static readonly Font IconFont = new("Segoe UI Symbol", 10f);

    private IReadOnlyList<AddressSuggestion> allSuggestions = Array.Empty<AddressSuggestion>();
    private IReadOnlyList<AddressSuggestion> suggestions = Array.Empty<AddressSuggestion>();
    private int selectedIndex = -1;
    private int hoverIndex = -1;
    private int removeHoverIndex = -1;
    private int pressedIndex = -1;
    private bool pressedRemove;
    private Size lastRegionSize;
    private int lastRegionRadius;

    public AddressSuggestionPopup()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : SurfaceColor;
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
        if (Width <= 1 || Height <= 1) return;
        var currentSize = new Size(Width, Height);
        var radius = Scale(10);
        if (lastRegionSize == currentSize && lastRegionRadius == radius && Region is not null) return;
        lastRegionSize = currentSize;
        lastRegionRadius = radius;
        using var path = CreateRoundedPath(new Rectangle(0, 0, Width, Height), radius);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : SurfaceColor;
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
            && !suggestions[index].IsSearch
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
        pressedRemove = !suggestions[index].IsSearch && GetRemoveButtonBounds(index).Contains(e.Location);
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
            && GetRemoveButtonBounds(index).Contains(e.Location);
        if (activate)
        {
            if (remove) SuggestionRemoved?.Invoke(suggestions[index]);
            else if (!suggestions[index].IsSearch) SuggestionAccepted?.Invoke(suggestions[index]);
            else SuggestionAccepted?.Invoke(suggestions[index]);
        }
        pressedIndex = -1;
        pressedRemove = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(SystemInformation.HighContrast ? SystemColors.Window : SurfaceColor);

        var rowHeight = ScaledRowHeight;
        for (var index = 0; index < suggestions.Count; index++)
        {
            var row = new Rectangle(1, 1 + (index * rowHeight), Math.Max(0, Width - 2), rowHeight);
            var highlighted = index == selectedIndex || index == hoverIndex;
            if (highlighted)
            {
                using var selectedBrush = new SolidBrush(
                    SystemInformation.HighContrast ? SystemColors.Highlight : SelectedColor);
                e.Graphics.FillRectangle(selectedBrush, row);
                if (!SystemInformation.HighContrast && index == selectedIndex)
                {
                    using var selectionCue = new SolidBrush(AccentColor);
                    e.Graphics.FillRectangle(
                        selectionCue,
                        row.Left,
                        row.Top + Scale(4),
                        Math.Max(2, Scale(3)),
                        Math.Max(1, row.Height - Scale(8)));
                }
            }

            DrawSuggestion(e.Graphics, suggestions[index], row);
            if (!suggestions[index].IsSearch) DrawRemoveButton(e.Graphics, index);
            if (index < suggestions.Count - 1)
            {
                using var separator = new Pen(
                    SystemInformation.HighContrast
                        ? highlighted ? SystemColors.HighlightText : SystemColors.WindowText
                        : Color.FromArgb(128, NativeUiTheme.Border));
                e.Graphics.DrawLine(separator, Scale(42), row.Bottom - 1, Width - Scale(12), row.Bottom - 1);
            }
        }

        using var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : BorderColor);
        using var outline = CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Scale(10));
        e.Graphics.DrawPath(border, outline);
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

    private void DrawSuggestion(Graphics graphics, AddressSuggestion suggestion, Rectangle row)
    {
        var iconBounds = new Rectangle(Scale(12), row.Top + ((row.Height - Scale(26)) / 2), Scale(26), Scale(26));
        var index = (row.Top - 1) / ScaledRowHeight;
        var selected = index == selectedIndex || index == hoverIndex;
        using var iconBackground = new SolidBrush(
            SystemInformation.HighContrast
                ? selected ? SystemColors.Highlight : SystemColors.Window
                : NativeUiTheme.SurfaceRaised);
        graphics.FillEllipse(iconBackground, iconBounds);
        var icon = suggestion.Source switch
        {
            AddressSuggestionSource.Bookmark => "\u2605",
            AddressSuggestionSource.History when suggestion.IsSearchHistory => "\u21BB",
            AddressSuggestionSource.History => "\u29C9",
            _ => "\u2315"
        };
        TextRenderer.DrawText(
            graphics,
            icon,
            IconFont,
            iconBounds,
            SystemInformation.HighContrast
                ? selected ? SystemColors.HighlightText : SystemColors.WindowText
                : AccentColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var textLeft = iconBounds.Right + Scale(10);
        var badgeWidth = suggestion.IsTopMatch ? Scale(74) : 0;
        var textWidth = Math.Max(10, row.Right - textLeft - Scale(42) - badgeWidth);
        var titleBounds = new Rectangle(textLeft, row.Top + Scale(7), textWidth, Scale(18));
        var detailBounds = new Rectangle(textLeft, titleBounds.Bottom, textWidth, Scale(16));
        TextRenderer.DrawText(
            graphics,
            suggestion.Title,
            PrimaryFont,
            titleBounds,
            SystemInformation.HighContrast && selected
                ? SystemColors.HighlightText
                : SystemInformation.HighContrast
                    ? SystemColors.WindowText
                    : suggestion.IsSearch && !selected ? AccentColor : PrimaryTextColor,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(
            graphics,
            suggestion.Detail,
            SecondaryFont,
            detailBounds,
            SystemInformation.HighContrast && selected ? SystemColors.HighlightText : SystemInformation.HighContrast ? SystemColors.WindowText : SecondaryTextColor,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        if (suggestion.IsTopMatch)
        {
            var badgeBounds = new Rectangle(
                row.Right - Scale(40) - badgeWidth,
                row.Top + ((row.Height - Scale(18)) / 2),
                badgeWidth - Scale(6),
                Scale(18));
            using var badgeBrush = new SolidBrush(
                SystemInformation.HighContrast
                    ? selected ? SystemColors.Highlight : SystemColors.Window
                    : Color.FromArgb(32, AccentColor));
            using var badgePath = CreateRoundedPath(badgeBounds, Scale(4));
            if (!SystemInformation.HighContrast)
            {
                graphics.FillPath(badgeBrush, badgePath);
            }
            TextRenderer.DrawText(
                graphics,
                "Top match",
                SecondaryFont,
                badgeBounds,
                SystemInformation.HighContrast
                    ? selected ? SystemColors.HighlightText : SystemColors.WindowText
                    : AccentColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private void DrawRemoveButton(Graphics graphics, int index)
    {
        var bounds = GetRemoveButtonBounds(index);
        var selected = index == selectedIndex;
        if (index == removeHoverIndex)
        {
            using var hoverBrush = new SolidBrush(
                SystemInformation.HighContrast ? SystemColors.Highlight : NativeUiTheme.Hover);
            graphics.FillEllipse(hoverBrush, bounds);
        }

        TextRenderer.DrawText(
            graphics,
            "\u00D7",
            IconFont,
            bounds,
            SystemInformation.HighContrast
                ? selected || index == removeHoverIndex
                    ? SystemColors.HighlightText
                    : SystemColors.WindowText
                : index == removeHoverIndex ? PrimaryTextColor : SecondaryTextColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var diameter = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
