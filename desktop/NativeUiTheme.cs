using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MishaWeb;

internal readonly record struct ModernListRowVisual(
    string Title,
    string Detail,
    string BadgeGlyph,
    Color BadgeAccent = default,
    string? StatusText = null,
    Color StatusColor = default,
    string? ShortcutText = null);

/// <summary>
/// Shared palette and Liquid Glass styling helpers for native utility windows,
/// menus, tooltips, lists, and action buttons.
/// </summary>
internal static class NativeUiTheme
{
    // Shared v2 brand palette. Keeping these as process-wide Color values lets
    // every native surface share one identity without loading theme resources.
    internal static readonly Color BrandWine = Color.FromArgb(68, 7, 51);
    internal static readonly Color BrandCoral = Color.FromArgb(246, 107, 122);
    internal static readonly Color Window = Color.FromArgb(24, 11, 22);
    internal static readonly Color Chrome = Color.FromArgb(37, 15, 32);
    internal static readonly Color Toolbar = Color.FromArgb(43, 18, 38);
    internal static readonly Color Field = Color.FromArgb(23, 11, 22);
    internal static readonly Color Surface = Color.FromArgb(53, 25, 47);
    internal static readonly Color SurfaceRaised = Color.FromArgb(66, 32, 58);
    internal static readonly Color Hover = Color.FromArgb(79, 41, 68);
    internal static readonly Color Pressed = Color.FromArgb(93, 47, 77);
    internal static readonly Color Border = Color.FromArgb(150, 93, 126);
    internal static readonly Color Text = Color.FromArgb(255, 246, 249);
    internal static readonly Color SecondaryText = Color.FromArgb(237, 215, 225);
    internal static readonly Color Muted = Color.FromArgb(205, 168, 184);
    internal static readonly Color Selection = Color.FromArgb(91, 40, 74);
    internal static readonly Color Accent = Color.FromArgb(255, 120, 174);
    internal static readonly Color AccentHover = Color.FromArgb(255, 155, 195);
    internal static readonly Color AccentPressed = Color.FromArgb(223, 91, 148);
    internal static readonly Color AccentText = Color.FromArgb(43, 8, 24);
    internal static readonly Color Focus = Color.FromArgb(255, 179, 208);
    internal static readonly Color Lavender = Color.FromArgb(216, 135, 255);
    internal static readonly Color Positive = Color.FromArgb(132, 230, 191);
    internal static readonly Color Negative = Color.FromArgb(255, 157, 170);

    private static readonly Font ToolTipFont = new("Segoe UI", 9f);
    private static readonly Font RowTitleFont = new("Segoe UI Semibold", 9.5f);
    private static readonly Font RowDetailFont = new("Segoe UI", 8.5f);
    private static readonly Font RowBadgeFont = new("Segoe UI Semibold", 9f);
    private static readonly Font RowPillFont = new("Segoe UI Semibold", 8f);

    private static readonly ConditionalWeakTable<Form, object> FramedForms = new();
    private static readonly ConditionalWeakTable<Control, object> DarkScrollbarControls = new();
    private static readonly ConditionalWeakTable<ToolStripDropDown, object> FramedMenus = new();
    private static readonly ConditionalWeakTable<ToolTip, object> GlassToolTips = new();
    private static readonly ConditionalWeakTable<Button, ButtonVisualState> StyledButtons = new();
    private static readonly ConditionalWeakTable<ListBox, ModernListBoxState> StyledListBoxes = new();

    public static Color MutedText => SystemInformation.HighContrast
        ? SystemColors.GrayText
        : Muted;

    public static void Apply(Form form, params Button[] accentButtons)
    {
        ArgumentNullException.ThrowIfNull(form);
        accentButtons ??= [];
        ApplyWindowFrame(form);
        ApplyPalette();
        form.SystemColorsChanged += (_, _) => ApplyPalette();

        void ApplyPalette()
        {
            ApplyTree(form);
            foreach (var button in accentButtons)
            {
                if (button is not null && !button.IsDisposed) ApplyAccentButton(button);
            }
        }
    }

    internal static void ApplyWindowFrame(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (FramedForms.TryGetValue(form, out _))
        {
            ApplyWindowFrameCore(form);
            return;
        }

        FramedForms.Add(form, new object());
        form.HandleCreated += (_, _) => ApplyWindowFrameCore(form);
        form.SystemColorsChanged += (_, _) => ApplyWindowFrameCore(form);
        if (form.IsHandleCreated)
        {
            ApplyWindowFrameCore(form);
        }
    }

    internal static void ApplyDarkScrollbars(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (DarkScrollbarControls.TryGetValue(control, out _))
        {
            ApplyDarkScrollbarsCore(control);
            return;
        }

        DarkScrollbarControls.Add(control, new object());
        control.HandleCreated += (_, _) => ApplyDarkScrollbarsCore(control);
        control.SystemColorsChanged += (_, _) => ApplyDarkScrollbarsCore(control);
        if (control.IsHandleCreated)
        {
            ApplyDarkScrollbarsCore(control);
        }
    }

    internal static void ApplyMenuWindowFrame(ToolStripDropDown menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (FramedMenus.TryGetValue(menu, out _))
        {
            ApplyMenuWindowFrameCore(menu);
            return;
        }

        FramedMenus.Add(menu, new object());
        menu.HandleCreated += (_, _) => ApplyMenuWindowFrameCore(menu);
        menu.Opening += (_, _) => ApplyMenuWindowFrameCore(menu);
        menu.SystemColorsChanged += (_, _) => ApplyMenuWindowFrameCore(menu);
        if (menu.IsHandleCreated)
        {
            ApplyMenuWindowFrameCore(menu);
        }
    }

    internal static void ConfigureGlassToolTip(ToolTip tip)
    {
        ArgumentNullException.ThrowIfNull(tip);
        tip.OwnerDraw = true;
        if (!SystemInformation.HighContrast)
        {
            tip.BackColor = Chrome;
            tip.ForeColor = Text;
        }

        if (GlassToolTips.TryGetValue(tip, out _)) return;
        GlassToolTips.Add(tip, new object());
        tip.Popup += (_, e) => OnGlassToolTipPopup(tip, e);
        tip.Draw += (_, e) => OnGlassToolTipDraw(e);
    }

    internal static void ApplySecondaryButton(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        ApplyButton(button, SystemInformation.HighContrast);
    }

    internal static void ConfigureModernListBox(
        ListBox listBox,
        int logicalItemHeight,
        Func<int, DrawItemState, ModernListRowVisual> rowSelector)
    {
        ArgumentNullException.ThrowIfNull(listBox);
        ArgumentNullException.ThrowIfNull(rowSelector);

        listBox.DrawMode = DrawMode.OwnerDrawFixed;
        listBox.IntegralHeight = false;
        listBox.HorizontalScrollbar = false;
        ApplyDarkScrollbars(listBox);

        var state = StyledListBoxes.GetOrCreateValue(listBox);
        state.LogicalItemHeight = Math.Max(28, logicalItemHeight);
        state.RowSelector = rowSelector;
        SyncListItemHeight(listBox, state.LogicalItemHeight);

        if (state.EventsWired) return;
        state.EventsWired = true;
        listBox.HandleCreated += (_, _) => SyncListItemHeight(listBox, state.LogicalItemHeight);
        listBox.DpiChangedAfterParent += (_, _) => SyncListItemHeight(listBox, state.LogicalItemHeight);
        listBox.MouseMove += (_, e) =>
        {
            var nextHover = listBox.IndexFromPoint(e.Location);
            if (nextHover < 0 || nextHover >= listBox.Items.Count) nextHover = -1;
            if (state.HoverIndex == nextHover) return;
            var previousHover = state.HoverIndex;
            state.HoverIndex = nextHover;
            InvalidateListItem(listBox, previousHover);
            InvalidateListItem(listBox, nextHover);
        };
        listBox.MouseLeave += (_, _) =>
        {
            if (state.HoverIndex < 0) return;
            var previousHover = state.HoverIndex;
            state.HoverIndex = -1;
            InvalidateListItem(listBox, previousHover);
        };
        listBox.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= listBox.Items.Count || state.RowSelector is null) return;
            var visual = state.RowSelector(e.Index, e.State);
            var dpi = Math.Max(96, listBox.DeviceDpi);
            DrawModernListRow(
                e.Graphics,
                e.Bounds,
                e.State,
                visual,
                dpi,
                isHovered: e.Index == state.HoverIndex);
        };
    }

    internal static void DrawModernListRow(
        Graphics graphics,
        Rectangle bounds,
        DrawItemState state,
        ModernListRowVisual row,
        int dpi = 96,
        bool isHovered = false)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        if (bounds.Width <= 2 || bounds.Height <= 2) return;

        var scale = Math.Max(96, dpi) / 96f;
        int Scaled(int value) => Math.Max(1, (int)Math.Round(value * scale));

        var highContrast = SystemInformation.HighContrast;
        var selected = (state & DrawItemState.Selected) != 0;
        var backdropColor = highContrast ? SystemColors.Window : Field;
        using (var backdrop = new SolidBrush(backdropColor))
        {
            graphics.FillRectangle(backdrop, bounds);
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var pillBounds = new RectangleF(
            bounds.X + Scaled(5),
            bounds.Y + Scaled(2),
            Math.Max(4f, bounds.Width - Scaled(10)),
            Math.Max(4f, bounds.Height - Scaled(4)));
        var radius = Math.Min(Scaled(9), pillBounds.Height * 0.4f);

        if (highContrast)
        {
            if (selected || isHovered)
            {
                using var hcPath = CreateRoundedPath(pillBounds, radius);
                using var hcBrush = new SolidBrush(selected ? SystemColors.Highlight : SystemColors.Control);
                graphics.FillPath(hcBrush, hcPath);
                if (selected)
                {
                    using var hcPen = new Pen(SystemColors.HighlightText, 1f);
                    graphics.DrawPath(hcPen, hcPath);
                }
            }
        }
        else if (selected || isHovered)
        {
            using var pillPath = CreateRoundedPath(pillBounds, radius);
            if (selected)
            {
                using var fill = new LinearGradientBrush(
                    pillBounds,
                    SurfaceRaised,
                    Selection,
                    LinearGradientMode.Vertical);
                graphics.FillPath(fill, pillPath);

                var sheenRect = new RectangleF(pillBounds.X + 1f, pillBounds.Y + 1f, Math.Max(2f, pillBounds.Width - 2f), Math.Max(3f, pillBounds.Height * 0.35f));
                using var sheen = new LinearGradientBrush(
                    sheenRect,
                    Color.FromArgb(36, 255, 240, 250),
                    Color.FromArgb(0, SurfaceRaised),
                    LinearGradientMode.Vertical);
                graphics.FillRectangle(sheen, sheenRect);

                using var rim = new Pen(Color.FromArgb(190, Focus), 1f);
                graphics.DrawPath(rim, pillPath);

                var cueWidth = Math.Max(2.5f, Scaled(3));
                var cueRect = new RectangleF(
                    pillBounds.X + Scaled(3),
                    pillBounds.Y + Scaled(7),
                    cueWidth,
                    Math.Max(4f, pillBounds.Height - Scaled(14)));
                using var cuePath = CreateRoundedPath(cueRect, cueWidth / 2f);
                using var cueBrush = new SolidBrush(Accent);
                graphics.FillPath(cueBrush, cuePath);
            }
            else
            {
                using var hoverFill = new SolidBrush(Color.FromArgb(155, Hover));
                graphics.FillPath(hoverFill, pillPath);
                using var hoverRim = new Pen(Color.FromArgb(95, Border), 1f);
                graphics.DrawPath(hoverRim, pillPath);
            }
        }
        else
        {
            using var dividerPen = new Pen(Color.FromArgb(46, Border), 1f);
            graphics.DrawLine(
                dividerPen,
                bounds.X + Scaled(46),
                bounds.Bottom - 1,
                bounds.Right - Scaled(12),
                bounds.Bottom - 1);
        }

        var badgeAccent = row.BadgeAccent.IsEmpty ? Accent : row.BadgeAccent;
        var badgeSize = Math.Min(Scaled(28), Math.Max(18, bounds.Height - Scaled(14)));
        var badgeRect = new Rectangle(
            bounds.X + Scaled(12),
            bounds.Y + ((bounds.Height - badgeSize) / 2),
            badgeSize,
            badgeSize);
        if (!string.IsNullOrEmpty(row.BadgeGlyph))
        {
            var badgeBg = highContrast
                ? (selected ? SystemColors.Highlight : SystemColors.Control)
                : Color.FromArgb(selected ? 68 : 42, badgeAccent);
            var badgeBorder = highContrast
                ? (selected ? SystemColors.HighlightText : SystemColors.WindowText)
                : Color.FromArgb(selected ? 185 : 115, badgeAccent);
            var badgeText = highContrast
                ? (selected ? SystemColors.HighlightText : SystemColors.WindowText)
                : (selected ? Text : badgeAccent);
            var badgeRectF = new RectangleF(badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height);
            using var badgePath = CreateRoundedPath(badgeRectF, Math.Max(4f, badgeSize * 0.28f));
            using (var badgeFill = new SolidBrush(badgeBg))
            {
                graphics.FillPath(badgeFill, badgePath);
            }
            using (var badgeRim = new Pen(badgeBorder, 1f))
            {
                graphics.DrawPath(badgeRim, badgePath);
            }
            TextRenderer.DrawText(
                graphics,
                row.BadgeGlyph,
                RowBadgeFont,
                badgeRect,
                badgeText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        var rightEdge = bounds.Right - Scaled(14);
        if (!string.IsNullOrWhiteSpace(row.ShortcutText))
        {
            var shortcutSize = TextRenderer.MeasureText(
                graphics,
                row.ShortcutText,
                RowPillFont,
                new Size(Scaled(140), Scaled(22)),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            var pillWidth = Math.Min(Scaled(130), shortcutSize.Width + Scaled(12));
            var pillHeight = Scaled(20);
            var shortcutRect = new Rectangle(
                rightEdge - pillWidth,
                bounds.Y + ((bounds.Height - pillHeight) / 2),
                pillWidth,
                pillHeight);
            var shortcutRectF = new RectangleF(shortcutRect.X, shortcutRect.Y, shortcutRect.Width, shortcutRect.Height);
            using var shortcutPath = CreateRoundedPath(shortcutRectF, Scaled(6));
            using var shortcutBg = new SolidBrush(highContrast ? SystemColors.Control : Surface);
            using var shortcutPen = new Pen(highContrast ? SystemColors.WindowText : Border, 1f);
            graphics.FillPath(shortcutBg, shortcutPath);
            graphics.DrawPath(shortcutPen, shortcutPath);
            TextRenderer.DrawText(
                graphics,
                row.ShortcutText,
                RowPillFont,
                shortcutRect,
                highContrast ? SystemColors.ControlText : SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            rightEdge = shortcutRect.Left - Scaled(6);
        }

        if (!string.IsNullOrWhiteSpace(row.StatusText))
        {
            var statusAccent = row.StatusColor.IsEmpty ? Accent : row.StatusColor;
            var statusSize = TextRenderer.MeasureText(
                graphics,
                row.StatusText,
                RowPillFont,
                new Size(Scaled(130), Scaled(22)),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            var pillWidth = Math.Min(Scaled(124), statusSize.Width + Scaled(14));
            var pillHeight = Scaled(20);
            var statusRect = new Rectangle(
                rightEdge - pillWidth,
                bounds.Y + ((bounds.Height - pillHeight) / 2),
                pillWidth,
                pillHeight);
            var statusRectF = new RectangleF(statusRect.X, statusRect.Y, statusRect.Width, statusRect.Height);
            using var statusPath = CreateRoundedPath(statusRectF, pillHeight / 2f);
            using var statusBg = new SolidBrush(highContrast
                ? (selected ? SystemColors.Highlight : SystemColors.Control)
                : Color.FromArgb(40, statusAccent));
            using var statusPen = new Pen(highContrast
                ? (selected ? SystemColors.HighlightText : SystemColors.WindowText)
                : Color.FromArgb(145, statusAccent), 1f);
            graphics.FillPath(statusBg, statusPath);
            graphics.DrawPath(statusPen, statusPath);
            TextRenderer.DrawText(
                graphics,
                row.StatusText,
                RowPillFont,
                statusRect,
                highContrast
                    ? (selected ? SystemColors.HighlightText : SystemColors.ControlText)
                    : statusAccent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            rightEdge = statusRect.Left - Scaled(8);
        }

        var textLeft = string.IsNullOrEmpty(row.BadgeGlyph) ? bounds.X + Scaled(12) : badgeRect.Right + Scaled(10);
        var textWidth = Math.Max(20, rightEdge - textLeft);
        var titleColor = highContrast
            ? (selected ? SystemColors.HighlightText : SystemColors.WindowText)
            : Text;
        var detailColor = highContrast
            ? (selected ? SystemColors.HighlightText : SystemColors.GrayText)
            : (selected ? SecondaryText : Muted);

        if (string.IsNullOrWhiteSpace(row.Detail))
        {
            var titleRect = new Rectangle(textLeft, bounds.Y + Scaled(4), textWidth, Math.Max(14, bounds.Height - Scaled(8)));
            TextRenderer.DrawText(
                graphics,
                row.Title,
                RowTitleFont,
                titleRect,
                titleColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        else
        {
            var titleHeight = Math.Max(16, (bounds.Height - Scaled(10)) / 2);
            var topPad = Math.Max(4, (bounds.Height - (titleHeight + Scaled(16))) / 2);
            var titleRect = new Rectangle(textLeft, bounds.Y + topPad, textWidth, titleHeight);
            var detailRect = new Rectangle(textLeft, titleRect.Bottom, textWidth, Scaled(17));
            TextRenderer.DrawText(
                graphics,
                row.Title,
                RowTitleFont,
                titleRect,
                titleColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(
                graphics,
                row.Detail,
                RowDetailFont,
                detailRect,
                detailColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    internal static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var maxRadius = Math.Min(bounds.Width, bounds.Height) / 2f;
        var effectiveRadius = Math.Clamp(radius, 0.5f, Math.Max(0.5f, maxRadius));
        var diameter = effectiveRadius * 2f;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270f, 90f);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    private static void ApplyTree(Control control)
    {
        var highContrast = SystemInformation.HighContrast;
        var window = highContrast ? SystemColors.Window : Window;
        var field = highContrast ? SystemColors.Window : Field;
        var foreground = highContrast ? SystemColors.WindowText : Text;
        control.ForeColor = foreground;
        try
        {
            control.BackColor = control switch
            {
                TextBoxBase or ListBox or ComboBox => field,
                Button => highContrast ? SystemColors.Control : Surface,
                Label => Color.Transparent,
                _ => window
            };
        }
        catch (ArgumentException)
        {
            control.BackColor = highContrast ? SystemColors.Window : window;
        }

        switch (control)
        {
            case Button button:
                ApplyButton(button, highContrast);
                break;
            case TextBoxBase textBox:
                textBox.BorderStyle = BorderStyle.FixedSingle;
                ApplyDarkScrollbars(textBox);
                break;
            case ListBox listBox:
                listBox.BorderStyle = BorderStyle.FixedSingle;
                ApplyDarkScrollbars(listBox);
                break;
            case ComboBox comboBox:
                comboBox.FlatStyle = highContrast ? FlatStyle.System : FlatStyle.Flat;
                ApplyDarkScrollbars(comboBox);
                break;
            case ScrollableControl scrollable when scrollable is not Form && scrollable.AutoScroll:
                ApplyDarkScrollbars(scrollable);
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyTree(child);
        }
    }

    private static void ApplyButton(Button button, bool highContrast)
    {
        if (highContrast)
        {
            button.FlatStyle = FlatStyle.System;
            button.UseVisualStyleBackColor = true;
            button.ForeColor = SystemColors.ControlText;
            if (StyledButtons.TryGetValue(button, out var hcState))
            {
                hcState.IsAccent = false;
            }
            return;
        }

        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Surface;
        button.ForeColor = Text;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Hover;
        button.FlatAppearance.MouseDownBackColor = Pressed;
        EnsureGlassButtonPaint(button, isAccent: false);
    }

    internal static void ApplyAccentButton(Button button)
    {
        if (SystemInformation.HighContrast)
        {
            button.FlatStyle = FlatStyle.System;
            button.UseVisualStyleBackColor = true;
            button.ForeColor = SystemColors.ControlText;
            if (StyledButtons.TryGetValue(button, out var hcState))
            {
                hcState.IsAccent = true;
            }
            return;
        }

        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Accent;
        button.ForeColor = AccentText;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Focus;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        button.FlatAppearance.MouseDownBackColor = AccentPressed;
        EnsureGlassButtonPaint(button, isAccent: true);
    }

    private static void EnsureGlassButtonPaint(Button button, bool isAccent)
    {
        var state = StyledButtons.GetOrCreateValue(button);
        state.IsAccent = isAccent;
        if (state.EventsWired)
        {
            button.Invalidate();
            return;
        }

        state.EventsWired = true;
        button.MouseEnter += (_, _) =>
        {
            state.IsHovered = true;
            button.Invalidate();
        };
        button.MouseLeave += (_, _) =>
        {
            state.IsHovered = false;
            state.IsPressed = false;
            button.Invalidate();
        };
        button.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            state.IsPressed = true;
            button.Invalidate();
        };
        button.MouseUp += (_, _) =>
        {
            if (!state.IsPressed) return;
            state.IsPressed = false;
            button.Invalidate();
        };
        button.Paint += OnGlassButtonPaint;
        button.Invalidate();
    }

    private static void OnGlassButtonPaint(object? sender, PaintEventArgs e)
    {
        if (sender is not Button button || button.IsDisposed || SystemInformation.HighContrast) return;
        if (!StyledButtons.TryGetValue(button, out var state)) return;

        var width = button.ClientSize.Width;
        var height = button.ClientSize.Height;
        if (width <= 2 || height <= 2) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var dpi = Math.Max(96, button.DeviceDpi);
        var scale = dpi / 96f;
        var parentBack = ResolveOpaqueParentColor(button, Window);

        if (button.FlatAppearance.BorderSize == 0)
        {
            using (var clearBrush = new SolidBrush(parentBack))
            {
                g.FillRectangle(clearBrush, button.ClientRectangle);
            }
            if (state.IsHovered || state.IsPressed)
            {
                var pillRect = new RectangleF(1f, 1f, width - 2f, height - 2f);
                using var pillPath = CreateRoundedPath(pillRect, Math.Min(width, height) / 2f);
                using var hoverFill = new SolidBrush(state.IsPressed ? Pressed : Hover);
                g.FillPath(hoverFill, pillPath);
            }
            TextRenderer.DrawText(
                g,
                button.Text,
                button.Font,
                button.ClientRectangle,
                button.Enabled ? (state.IsHovered ? Text : button.ForeColor) : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            return;
        }

        using (var clearBrush = new SolidBrush(parentBack))
        {
            g.FillRectangle(clearBrush, button.ClientRectangle);
        }

        var radius = Math.Clamp(7f * scale, 3f, Math.Max(3f, (height - 2f) * 0.44f));
        var rect = new RectangleF(0.5f, 0.5f, width - 1f, height - 1f);
        using var path = CreateRoundedPath(rect, radius);

        Color topColor;
        Color bottomColor;
        Color borderColor;
        Color textColor;
        if (!button.Enabled)
        {
            topColor = Color.FromArgb(46, 22, 41);
            bottomColor = Color.FromArgb(35, 16, 31);
            borderColor = Color.FromArgb(95, Border);
            textColor = Color.FromArgb(148, 122, 134);
        }
        else if (state.IsAccent)
        {
            var baseAccent = state.IsPressed ? AccentPressed : state.IsHovered ? AccentHover : Accent;
            topColor = Blend(baseAccent, Color.White, state.IsPressed ? 0.04f : 0.14f);
            bottomColor = Blend(baseAccent, Chrome, state.IsPressed ? 0.18f : 0.06f);
            borderColor = button.Focused ? Text : Focus;
            textColor = AccentText;
        }
        else
        {
            topColor = state.IsPressed ? Pressed : state.IsHovered ? Hover : SurfaceRaised;
            bottomColor = state.IsPressed ? Hover : state.IsHovered ? Surface : Chrome;
            borderColor = button.Focused || ReferenceEquals(button.FindForm()?.AcceptButton, button) ? Focus : Border;
            textColor = Text;
        }

        using (var fill = new LinearGradientBrush(rect, topColor, bottomColor, LinearGradientMode.Vertical))
        {
            g.FillPath(fill, path);
        }

        if (button.Enabled)
        {
            var clipState = g.Save();
            try
            {
                g.SetClip(path, CombineMode.Intersect);
                var sheenHeight = Math.Max(3f, rect.Height * 0.42f);
                var sheenRect = new RectangleF(rect.X, rect.Y, rect.Width, sheenHeight);
                using var sheenBrush = new LinearGradientBrush(
                    sheenRect,
                    Color.FromArgb(state.IsAccent ? 72 : 34, 255, 240, 250),
                    Color.FromArgb(0, topColor),
                    LinearGradientMode.Vertical);
                g.FillRectangle(sheenBrush, sheenRect);
            }
            finally
            {
                g.Restore(clipState);
            }
        }

        using (var borderPen = new Pen(borderColor, button.Focused ? 1.4f : 1f))
        {
            g.DrawPath(borderPen, path);
        }

        if (button.Focused && button.Enabled && rect.Width > 8f && rect.Height > 8f)
        {
            var innerRect = rect;
            innerRect.Inflate(-2f, -2f);
            using var innerPath = CreateRoundedPath(innerRect, Math.Max(2f, radius - 2f));
            using var focusPen = new Pen(Color.FromArgb(state.IsAccent ? 180 : 120, state.IsAccent ? AccentText : Focus), 1f);
            g.DrawPath(focusPen, innerPath);
        }

        TextRenderer.DrawText(
            g,
            button.Text,
            button.Font,
            button.ClientRectangle,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    private static void OnGlassToolTipPopup(ToolTip tip, PopupEventArgs e)
    {
        if (e.AssociatedControl is not { } control) return;
        var text = tip.GetToolTip(control);
        if (string.IsNullOrEmpty(text)) return;

        var dpi = Math.Max(96, control.DeviceDpi);
        var scale = dpi / 96f;
        var padX = Math.Max(8, (int)Math.Round(10 * scale));
        var padY = Math.Max(5, (int)Math.Round(6 * scale));
        var maxWidth = Math.Max(220, (int)Math.Round(380 * scale));

        Size measured;
        if (control.IsHandleCreated)
        {
            using var g = control.CreateGraphics();
            measured = TextRenderer.MeasureText(
                g,
                text,
                ToolTipFont,
                new Size(maxWidth, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        }
        else
        {
            measured = TextRenderer.MeasureText(
                text,
                ToolTipFont,
                new Size(maxWidth, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        }

        e.ToolTipSize = new Size(
            Math.Max(padX * 2 + 12, measured.Width + (padX * 2)),
            Math.Max((int)Math.Round(26 * scale), measured.Height + (padY * 2)));
    }

    private static void OnGlassToolTipDraw(DrawToolTipEventArgs e)
    {
        if (e.Bounds.Width <= 2 || e.Bounds.Height <= 2) return;

        if (SystemInformation.HighContrast)
        {
            e.DrawBackground();
            e.DrawBorder();
            var hcRect = Rectangle.Inflate(e.Bounds, -8, -4);
            TextRenderer.DrawText(
                e.Graphics,
                e.ToolTipText,
                ToolTipFont,
                hcRect,
                SystemColors.InfoText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        var dpi = Math.Max(96, e.AssociatedControl?.DeviceDpi ?? 96);
        var scale = dpi / 96f;
        var g = e.Graphics;
        using (var backdrop = new SolidBrush(Window))
        {
            g.FillRectangle(backdrop, e.Bounds);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var cardRect = new RectangleF(
            e.Bounds.X + 0.5f,
            e.Bounds.Y + 0.5f,
            Math.Max(2f, e.Bounds.Width - 1f),
            Math.Max(2f, e.Bounds.Height - 1f));
        var radius = Math.Min(8f * scale, Math.Min(cardRect.Width, cardRect.Height) * 0.42f);
        using var cardPath = CreateRoundedPath(cardRect, radius);

        using (var fill = new LinearGradientBrush(
            cardRect,
            Color.FromArgb(255, 58, 25, 52),
            Color.FromArgb(255, 31, 13, 28),
            LinearGradientMode.Vertical))
        {
            g.FillPath(fill, cardPath);
        }

        var clipState = g.Save();
        try
        {
            g.SetClip(cardPath, CombineMode.Intersect);
            var sheenRect = new RectangleF(cardRect.X, cardRect.Y, cardRect.Width, Math.Max(4f, cardRect.Height * 0.42f));
            using var sheen = new LinearGradientBrush(
                sheenRect,
                Color.FromArgb(42, 255, 240, 250),
                Color.FromArgb(0, Chrome),
                LinearGradientMode.Vertical);
            g.FillRectangle(sheen, sheenRect);
        }
        finally
        {
            g.Restore(clipState);
        }

        using (var rimBrush = new LinearGradientBrush(
            cardRect,
            Color.FromArgb(200, 255, 224, 246),
            Color.FromArgb(140, Focus),
            LinearGradientMode.Vertical))
        using (var rimPen = new Pen(rimBrush, 1f))
        {
            g.DrawPath(rimPen, cardPath);
        }

        var padX = Math.Max(8, (int)Math.Round(10 * scale));
        var padY = Math.Max(4, (int)Math.Round(5 * scale));
        var textRect = new Rectangle(
            e.Bounds.X + padX,
            e.Bounds.Y + padY,
            Math.Max(4, e.Bounds.Width - (padX * 2)),
            Math.Max(4, e.Bounds.Height - (padY * 2)));
        TextRenderer.DrawText(
            g,
            e.ToolTipText,
            ToolTipFont,
            textRect,
            Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private static void SyncListItemHeight(ListBox listBox, int logicalItemHeight)
    {
        var dpi = Math.Max(96, listBox.DeviceDpi);
        var scaled = Math.Clamp((int)Math.Round(logicalItemHeight * (dpi / 96f)), 24, 140);
        if (listBox.ItemHeight != scaled)
        {
            listBox.ItemHeight = scaled;
        }
    }

    private static void InvalidateListItem(ListBox listBox, int index)
    {
        if (index < 0 || index >= listBox.Items.Count || listBox.IsDisposed) return;
        listBox.Invalidate(listBox.GetItemRectangle(index));
    }

    private static Color ResolveOpaqueParentColor(Control control, Color fallback)
    {
        var current = control.Parent;
        while (current is not null)
        {
            var candidate = current.BackColor;
            if (candidate != Color.Transparent && candidate.A == 255)
            {
                return candidate;
            }
            current = current.Parent;
        }
        return fallback;
    }

    private static Color Blend(Color baseColor, Color overlayColor, float amount)
    {
        var t = Math.Clamp(amount, 0f, 1f);
        var r = (int)Math.Round(baseColor.R + ((overlayColor.R - baseColor.R) * t));
        var g = (int)Math.Round(baseColor.G + ((overlayColor.G - baseColor.G) * t));
        var b = (int)Math.Round(baseColor.B + ((overlayColor.B - baseColor.B) * t));
        return Color.FromArgb(255, Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
    }

    private static void ApplyWindowFrameCore(Form form)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        try
        {
            if (SystemInformation.HighContrast)
            {
                var darkOff = 0;
                _ = NativeMethods.DwmSetWindowAttribute(form.Handle, 20, ref darkOff, sizeof(int));
                var cornerDefault = 0;
                _ = NativeMethods.DwmSetWindowAttribute(form.Handle, 33, ref cornerDefault, sizeof(int));
                var defaultColor = unchecked((int)0xFFFFFFFF);
                _ = NativeMethods.DwmSetWindowAttribute(form.Handle, 34, ref defaultColor, sizeof(int));
                _ = NativeMethods.DwmSetWindowAttribute(form.Handle, 35, ref defaultColor, sizeof(int));
                _ = NativeMethods.DwmSetWindowAttribute(form.Handle, 36, ref defaultColor, sizeof(int));
                return;
            }

            var darkMode = 1;
            _ = NativeMethods.DwmSetWindowAttribute(form.Handle, 20, ref darkMode, sizeof(int));
            var roundedCorners = 2;
            _ = NativeMethods.DwmSetWindowAttribute(form.Handle, 33, ref roundedCorners, sizeof(int));
            var border = ToColorRef(Border);
            _ = NativeMethods.DwmSetWindowAttribute(form.Handle, 34, ref border, sizeof(int));
            var caption = ToColorRef(Chrome);
            _ = NativeMethods.DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));
            var text = ToColorRef(Text);
            _ = NativeMethods.DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(int));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // DWM is unavailable only on unsupported legacy Windows configurations.
        }
    }

    private static void ApplyMenuWindowFrameCore(ToolStripDropDown menu)
    {
        if (menu.IsDisposed || !menu.IsHandleCreated) return;
        try
        {
            if (SystemInformation.HighContrast)
            {
                var darkOff = 0;
                _ = NativeMethods.DwmSetWindowAttribute(menu.Handle, 20, ref darkOff, sizeof(int));
                var cornerDefault = 0;
                _ = NativeMethods.DwmSetWindowAttribute(menu.Handle, 33, ref cornerDefault, sizeof(int));
                var defaultColor = unchecked((int)0xFFFFFFFF);
                _ = NativeMethods.DwmSetWindowAttribute(menu.Handle, 34, ref defaultColor, sizeof(int));
                return;
            }

            var darkMode = 1;
            _ = NativeMethods.DwmSetWindowAttribute(menu.Handle, 20, ref darkMode, sizeof(int));
            var roundedCorners = 2;
            _ = NativeMethods.DwmSetWindowAttribute(menu.Handle, 33, ref roundedCorners, sizeof(int));
            var border = ToColorRef(Border);
            _ = NativeMethods.DwmSetWindowAttribute(menu.Handle, 34, ref border, sizeof(int));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // DWM is unavailable only on unsupported legacy Windows configurations.
        }
    }

    private static void ApplyDarkScrollbarsCore(Control control)
    {
        if (control.IsDisposed || !control.IsHandleCreated) return;
        try
        {
            _ = NativeMethods.SetWindowTheme(
                control.Handle,
                SystemInformation.HighContrast ? null : "DarkMode_Explorer",
                null);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // uxtheme.dll is unavailable only on unsupported configurations.
        }
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private sealed class ButtonVisualState
    {
        public bool IsAccent { get; set; }
        public bool IsHovered { get; set; }
        public bool IsPressed { get; set; }
        public bool EventsWired { get; set; }
    }

    private sealed class ModernListBoxState
    {
        public int LogicalItemHeight { get; set; } = 48;
        public int HoverIndex { get; set; } = -1;
        public bool EventsWired { get; set; }
        public Func<int, DrawItemState, ModernListRowVisual>? RowSelector { get; set; }
    }

    private static class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);
    }
}
