using System.Drawing.Drawing2D;
using System.Reflection;
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

    private static readonly object SharedFontSync = new();
    private static readonly Dictionary<(string Family, int QuarterPointSize, FontStyle Style), Font> SharedFonts = new();
    private static readonly Dictionary<(string Family, int QuarterPointSize, int Dpi, FontStyle Style), Font> SharedDpiFonts = new();
    private static readonly object SharedGdiSync = new();
    private static readonly Dictionary<int, SolidBrush> SharedBrushes = new();
    private static readonly Dictionary<(int Argb, int WidthTenths), Pen> SharedPens = new();
    private static readonly object BadgeWidthSync = new();
    private static readonly Dictionary<(string Text, int Dpi, int MaxLogicalWidth), int> CachedBadgeWidths = new();
    private const int MaximumCachedBadgeWidths = 256;
    private static readonly PropertyInfo? DoubleBufferedProperty =
        typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly ConditionalWeakTable<Form, AppliedFormThemeState> AppliedForms = new();
    private static readonly ConditionalWeakTable<Form, WindowFrameState> FramedForms = new();
    private static readonly ConditionalWeakTable<Control, object> DarkScrollbarControls = new();
    private static readonly ConditionalWeakTable<ToolStripDropDown, MenuFrameState> FramedMenus = new();
    private static readonly ConditionalWeakTable<ToolTip, object> GlassToolTips = new();
    private static readonly ConditionalWeakTable<Button, ButtonVisualState> StyledButtons = new();
    private static readonly ConditionalWeakTable<ListBox, ModernListBoxState> StyledListBoxes = new();

    public static Color MutedText => SystemInformation.HighContrast
        ? SystemColors.GrayText
        : Muted;

    internal static Font GetSharedFont(string familyName, float emSize, FontStyle style = FontStyle.Regular)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);
        var quarterPoints = Math.Max(4, (int)MathF.Round(emSize * 4f));
        var normalizedSize = quarterPoints / 4f;
        var key = (familyName, quarterPoints, style);
        lock (SharedFontSync)
        {
            if (SharedFonts.TryGetValue(key, out var existing)) return existing;
            var created = new Font(familyName, normalizedSize, style);
            SharedFonts[key] = created;
            return created;
        }
    }

    internal static Font GetSharedFontForDpi(
        string familyName,
        float logicalPointSize,
        int dpi,
        FontStyle style = FontStyle.Regular)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);
        var effectiveDpi = Math.Max(96, dpi);
        var quarterPoints = Math.Max(4, (int)MathF.Round(logicalPointSize * 4f));
        var key = (familyName, quarterPoints, effectiveDpi, style);
        lock (SharedFontSync)
        {
            if (SharedDpiFonts.TryGetValue(key, out var existing)) return existing;
            var pixelSize = Math.Max(4f, (quarterPoints / 4f) * (effectiveDpi / 72f));
            var created = new Font(familyName, pixelSize, style, GraphicsUnit.Pixel);
            SharedDpiFonts[key] = created;
            return created;
        }
    }

    internal static SolidBrush GetSharedBrush(Color color)
    {
        var argb = color.ToArgb();
        lock (SharedGdiSync)
        {
            if (SharedBrushes.TryGetValue(argb, out var existing)) return existing;
            var created = new SolidBrush(color);
            SharedBrushes[argb] = created;
            return created;
        }
    }

    internal static Pen GetSharedPen(Color color, float width = 1f)
    {
        var widthTenths = Math.Max(1, (int)MathF.Round(width * 10f));
        var key = (color.ToArgb(), widthTenths);
        lock (SharedGdiSync)
        {
            if (SharedPens.TryGetValue(key, out var existing)) return existing;
            var created = new Pen(color, widthTenths / 10f);
            SharedPens[key] = created;
            return created;
        }
    }

    internal static Color BlendColor(Color from, Color to, float amount)
    {
        var t = Math.Clamp(amount, 0f, 1f);
        var a = (int)Math.Round(from.A + ((to.A - from.A) * t));
        var r = (int)Math.Round(from.R + ((to.R - from.R) * t));
        var g = (int)Math.Round(from.G + ((to.G - from.G) * t));
        var b = (int)Math.Round(from.B + ((to.B - from.B) * t));
        return Color.FromArgb(
            Math.Clamp(a, 0, 255),
            Math.Clamp(r, 0, 255),
            Math.Clamp(g, 0, 255),
            Math.Clamp(b, 0, 255));
    }

    private static int GetCachedPillTextWidth(string text, Font pillFont, int dpi, int maxLogicalWidth)
    {
        var effectiveDpi = Math.Max(96, dpi);
        var key = (text, effectiveDpi, maxLogicalWidth);
        lock (BadgeWidthSync)
        {
            if (CachedBadgeWidths.TryGetValue(key, out var cached)) return cached;
        }

        var scale = effectiveDpi / 96f;
        var maxPx = Math.Max(1, (int)Math.Round(maxLogicalWidth * scale));
        var maxHeightPx = Math.Max(1, (int)Math.Round(22 * scale));
        var measured = TextRenderer.MeasureText(
            text,
            pillFont,
            new Size(maxPx, maxHeightPx),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;

        lock (BadgeWidthSync)
        {
            if (CachedBadgeWidths.Count >= MaximumCachedBadgeWidths)
            {
                CachedBadgeWidths.Clear();
            }
            CachedBadgeWidths[key] = measured;
        }
        return measured;
    }

    internal static void EnableDoubleBuffering(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        try
        {
            DoubleBufferedProperty?.SetValue(control, true);
        }
        catch
        {
            // Some specialized native wrappers ignore reflection-based double buffering.
        }
    }

    public static void Apply(Form form, params Button[] accentButtons)
    {
        ArgumentNullException.ThrowIfNull(form);
        accentButtons ??= [];
        ApplyWindowFrame(form);

        var appliedState = AppliedForms.GetOrCreateValue(form);
        appliedState.AccentButtons = accentButtons;
        ApplyFormPalette(form, appliedState);

        if (appliedState.EventsWired) return;
        appliedState.EventsWired = true;
        form.SystemColorsChanged += (_, _) =>
        {
            if (!form.IsDisposed) ApplyFormPalette(form, appliedState);
        };
    }

    private static void ApplyFormPalette(Form form, AppliedFormThemeState appliedState)
    {
        ApplyTree(form);
        foreach (var button in appliedState.AccentButtons)
        {
            if (button is not null && !button.IsDisposed) ApplyAccentButton(button);
        }
    }

    internal static void ApplyWindowFrame(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        ApplyWindowFrame(form, captionColor: null, borderColor: null, textColor: null);
    }

    internal static void ApplyWindowFrame(
        Form form,
        Color? captionColor,
        Color? borderColor,
        Color? textColor)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (FramedForms.TryGetValue(form, out var state))
        {
            if (captionColor.HasValue) state.CustomCaptionColor = captionColor.Value;
            if (borderColor.HasValue) state.CustomBorderColor = borderColor.Value;
            if (textColor.HasValue) state.CustomTextColor = textColor.Value;
            ApplyWindowFrameCore(form, state);
            return;
        }

        state = new WindowFrameState
        {
            CustomCaptionColor = captionColor,
            CustomBorderColor = borderColor,
            CustomTextColor = textColor
        };
        FramedForms.Add(form, state);
        form.HandleCreated += (_, _) => ApplyWindowFrameCore(form, state);
        form.SystemColorsChanged += (_, _) => ApplyWindowFrameCore(form, state);
        if (form.IsHandleCreated)
        {
            ApplyWindowFrameCore(form, state);
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

    internal static void ApplyMenuWindowFrame(ToolStripDropDown menu, Color? borderColor = null)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (FramedMenus.TryGetValue(menu, out var state))
        {
            if (borderColor.HasValue) state.CustomBorderColor = borderColor.Value;
            ApplyMenuWindowFrameCore(menu, state);
            return;
        }

        state = new MenuFrameState
        {
            CustomBorderColor = borderColor
        };
        FramedMenus.Add(menu, state);
        menu.HandleCreated += (_, _) => ApplyMenuWindowFrameCore(menu, state);
        menu.Opening += (_, _) => ApplyMenuWindowFrameCore(menu, state);
        menu.SystemColorsChanged += (_, _) => ApplyMenuWindowFrameCore(menu, state);
        if (menu.IsHandleCreated)
        {
            ApplyMenuWindowFrameCore(menu, state);
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
        EnableDoubleBuffering(listBox);
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

        var effectiveDpi = Math.Max(96, dpi);
        var scale = effectiveDpi / 96f;
        int Scaled(int value) => Math.Max(1, (int)Math.Round(value * scale));

        var titleFont = GetSharedFontForDpi("Segoe UI Semibold", 9.5f, effectiveDpi);
        var detailFont = GetSharedFontForDpi("Segoe UI", 8.5f, effectiveDpi);
        var badgeFont = GetSharedFontForDpi("Segoe UI Semibold", 9f, effectiveDpi);
        var pillFont = GetSharedFontForDpi("Segoe UI Semibold", 8f, effectiveDpi);

        var highContrast = SystemInformation.HighContrast;
        var selected = (state & DrawItemState.Selected) != 0;
        var focused = (state & DrawItemState.Focus) != 0 && (state & DrawItemState.NoFocusRect) == 0;
        var backdropColor = highContrast ? SystemColors.Window : Field;
        graphics.FillRectangle(GetSharedBrush(backdropColor), bounds);

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
                graphics.FillPath(GetSharedBrush(selected ? SystemColors.Highlight : SystemColors.Control), hcPath);
                if (selected)
                {
                    graphics.DrawPath(GetSharedPen(SystemColors.HighlightText, 1f), hcPath);
                }
            }
            if (focused)
            {
                ControlPaint.DrawFocusRectangle(
                    graphics,
                    Rectangle.Round(pillBounds),
                    selected ? SystemColors.HighlightText : SystemColors.WindowText,
                    selected ? SystemColors.Highlight : (isHovered ? SystemColors.Control : SystemColors.Window));
            }
        }
        else if (selected || isHovered || focused)
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

                var clipState = graphics.Save();
                try
                {
                    graphics.SetClip(pillPath, CombineMode.Intersect);
                    var sheenRect = new RectangleF(
                        pillBounds.X,
                        pillBounds.Y,
                        Math.Max(2f, pillBounds.Width),
                        Math.Max(3f, pillBounds.Height * 0.36f));
                    using var sheen = new LinearGradientBrush(
                        sheenRect,
                        Color.FromArgb(36, 255, 240, 250),
                        Color.FromArgb(0, SurfaceRaised),
                        LinearGradientMode.Vertical);
                    graphics.FillRectangle(sheen, sheenRect);

                    var causticHeight = Math.Max(3f, pillBounds.Height * 0.28f);
                    var causticRect = new RectangleF(
                        pillBounds.X,
                        pillBounds.Bottom - causticHeight,
                        Math.Max(2f, pillBounds.Width),
                        causticHeight + 1f);
                    using var caustic = new LinearGradientBrush(
                        causticRect,
                        Color.FromArgb(0, Accent),
                        Color.FromArgb(36, Accent),
                        LinearGradientMode.Vertical);
                    graphics.FillRectangle(caustic, causticRect);
                }
                finally
                {
                    graphics.Restore(clipState);
                }

                graphics.DrawPath(GetSharedPen(Color.FromArgb(focused ? 245 : 190, Focus), focused ? 1.5f : 1f), pillPath);

                var cueWidth = Math.Max(2.5f, Scaled(3));
                var cueRect = new RectangleF(
                    pillBounds.X + Scaled(3),
                    pillBounds.Y + Scaled(7),
                    cueWidth,
                    Math.Max(4f, pillBounds.Height - Scaled(14)));
                using var cuePath = CreateRoundedPath(cueRect, cueWidth / 2f);
                graphics.FillPath(GetSharedBrush(Accent), cuePath);
            }
            else if (isHovered)
            {
                graphics.FillPath(GetSharedBrush(Color.FromArgb(155, Hover)), pillPath);
                graphics.DrawPath(
                    GetSharedPen(
                        focused ? Color.FromArgb(200, Focus) : Color.FromArgb(95, Border),
                        focused ? 1.35f : 1f),
                    pillPath);
            }
            else
            {
                graphics.DrawPath(GetSharedPen(Color.FromArgb(195, Focus), 1.35f), pillPath);
            }
        }
        else
        {
            graphics.DrawLine(
                GetSharedPen(Color.FromArgb(46, Border), 1f),
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
            graphics.FillPath(GetSharedBrush(badgeBg), badgePath);
            graphics.DrawPath(GetSharedPen(badgeBorder, 1f), badgePath);
            TextRenderer.DrawText(
                graphics,
                row.BadgeGlyph,
                badgeFont,
                badgeRect,
                badgeText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        var rightEdge = bounds.Right - Scaled(14);
        if (!string.IsNullOrWhiteSpace(row.ShortcutText))
        {
            var shortcutTextWidth = GetCachedPillTextWidth(row.ShortcutText, pillFont, effectiveDpi, 140);
            var pillWidth = Math.Min(Scaled(130), shortcutTextWidth + Scaled(12));
            var pillHeight = Scaled(20);
            var shortcutRect = new Rectangle(
                rightEdge - pillWidth,
                bounds.Y + ((bounds.Height - pillHeight) / 2),
                pillWidth,
                pillHeight);
            var shortcutRectF = new RectangleF(shortcutRect.X, shortcutRect.Y, shortcutRect.Width, shortcutRect.Height);
            using var shortcutPath = CreateRoundedPath(shortcutRectF, Scaled(6));
            graphics.FillPath(GetSharedBrush(highContrast ? SystemColors.Control : Surface), shortcutPath);
            graphics.DrawPath(GetSharedPen(highContrast ? SystemColors.WindowText : Border, 1f), shortcutPath);
            TextRenderer.DrawText(
                graphics,
                row.ShortcutText,
                pillFont,
                shortcutRect,
                highContrast ? SystemColors.ControlText : SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            rightEdge = shortcutRect.Left - Scaled(6);
        }

        if (!string.IsNullOrWhiteSpace(row.StatusText))
        {
            var statusAccent = row.StatusColor.IsEmpty ? Accent : row.StatusColor;
            var statusTextWidth = GetCachedPillTextWidth(row.StatusText, pillFont, effectiveDpi, 130);
            var pillWidth = Math.Min(Scaled(124), statusTextWidth + Scaled(14));
            var pillHeight = Scaled(20);
            var statusRect = new Rectangle(
                rightEdge - pillWidth,
                bounds.Y + ((bounds.Height - pillHeight) / 2),
                pillWidth,
                pillHeight);
            var statusRectF = new RectangleF(statusRect.X, statusRect.Y, statusRect.Width, statusRect.Height);
            using var statusPath = CreateRoundedPath(statusRectF, pillHeight / 2f);
            graphics.FillPath(
                GetSharedBrush(highContrast
                    ? (selected ? SystemColors.Highlight : SystemColors.Control)
                    : Color.FromArgb(40, statusAccent)),
                statusPath);
            graphics.DrawPath(
                GetSharedPen(
                    highContrast
                        ? (selected ? SystemColors.HighlightText : SystemColors.WindowText)
                        : Color.FromArgb(145, statusAccent),
                    1f),
                statusPath);
            TextRenderer.DrawText(
                graphics,
                row.StatusText,
                pillFont,
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
                titleFont,
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
                titleFont,
                titleRect,
                titleColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(
                graphics,
                row.Detail,
                detailFont,
                detailRect,
                detailColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    internal static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 1f || bounds.Height <= 1f)
        {
            path.AddRectangle(bounds);
            return path;
        }

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
        EnableDoubleBuffering(button);
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
        button.GotFocus += (_, _) => button.Invalidate();
        button.LostFocus += (_, _) =>
        {
            state.IsPressed = false;
            button.Invalidate();
        };
        button.EnabledChanged += (_, _) =>
        {
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
        var font = GetSharedFontForDpi("Segoe UI", 9f, dpi);

        var measured = TextRenderer.MeasureText(
            text,
            font,
            new Size(maxWidth, 0),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);

        e.ToolTipSize = new Size(
            Math.Max(padX * 2 + 12, measured.Width + (padX * 2)),
            Math.Max((int)Math.Round(26 * scale), measured.Height + (padY * 2)));
    }

    private static void OnGlassToolTipDraw(DrawToolTipEventArgs e)
    {
        if (e.Bounds.Width <= 2 || e.Bounds.Height <= 2) return;

        var dpi = Math.Max(96, e.AssociatedControl?.DeviceDpi ?? 96);
        var font = GetSharedFontForDpi("Segoe UI", 9f, dpi);

        if (SystemInformation.HighContrast)
        {
            e.DrawBackground();
            e.DrawBorder();
            var hcRect = Rectangle.Inflate(e.Bounds, -8, -4);
            TextRenderer.DrawText(
                e.Graphics,
                e.ToolTipText,
                font,
                hcRect,
                SystemColors.InfoText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        var isPrivate = e.AssociatedControl?.FindForm() is MainForm { IsPrivateWindow: true };
        var scale = dpi / 96f;
        var g = e.Graphics;
        g.FillRectangle(GetSharedBrush(isPrivate ? Color.FromArgb(24, 11, 25) : Window), e.Bounds);

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
            isPrivate ? Color.FromArgb(255, 52, 22, 68) : Color.FromArgb(255, 58, 25, 52),
            isPrivate ? Color.FromArgb(255, 28, 12, 36) : Color.FromArgb(255, 31, 13, 28),
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
                isPrivate ? Color.FromArgb(44, 244, 228, 255) : Color.FromArgb(42, 255, 240, 250),
                Color.FromArgb(0, isPrivate ? Color.FromArgb(38, 16, 48) : Chrome),
                LinearGradientMode.Vertical);
            g.FillRectangle(sheen, sheenRect);
        }
        finally
        {
            g.Restore(clipState);
        }

        using (var rimBrush = new LinearGradientBrush(
            cardRect,
            isPrivate ? Color.FromArgb(210, 236, 198, 255) : Color.FromArgb(200, 255, 224, 246),
            isPrivate ? Color.FromArgb(165, Lavender) : Color.FromArgb(140, Focus),
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
            font,
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

    private static void ApplyWindowFrameCore(Form form, WindowFrameState state)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        var handle = form.Handle;
        var highContrast = SystemInformation.HighContrast;
        var defaultColor = unchecked((int)0xFFFFFFFF);
        var borderRef = highContrast ? defaultColor : ToColorRef(state.CustomBorderColor ?? Border);
        var captionRef = highContrast ? defaultColor : ToColorRef(state.CustomCaptionColor ?? Chrome);
        var textRef = highContrast ? defaultColor : ToColorRef(state.CustomTextColor ?? Text);

        if (state.HasApplied
            && state.AppliedHandle == handle
            && state.AppliedHighContrast == highContrast
            && state.AppliedBorderColorRef == borderRef
            && state.AppliedCaptionColorRef == captionRef
            && state.AppliedTextColorRef == textRef)
        {
            return;
        }

        try
        {
            if (highContrast)
            {
                var darkOff = 0;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 20, ref darkOff, sizeof(int));
                var cornerDefault = 0;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 33, ref cornerDefault, sizeof(int));
                var hcColor = defaultColor;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 34, ref hcColor, sizeof(int));
                _ = NativeMethods.DwmSetWindowAttribute(handle, 35, ref hcColor, sizeof(int));
                _ = NativeMethods.DwmSetWindowAttribute(handle, 36, ref hcColor, sizeof(int));
            }
            else
            {
                var darkMode = 1;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
                var roundedCorners = 2;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 33, ref roundedCorners, sizeof(int));
                var border = borderRef;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
                var caption = captionRef;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
                var text = textRef;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
            }

            state.AppliedHandle = handle;
            state.AppliedHighContrast = highContrast;
            state.AppliedBorderColorRef = borderRef;
            state.AppliedCaptionColorRef = captionRef;
            state.AppliedTextColorRef = textRef;
            state.HasApplied = true;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // DWM is unavailable only on unsupported legacy Windows configurations.
        }
    }

    private static void ApplyMenuWindowFrameCore(ToolStripDropDown menu, MenuFrameState state)
    {
        if (menu.IsDisposed || !menu.IsHandleCreated) return;
        var handle = menu.Handle;
        var highContrast = SystemInformation.HighContrast;
        var defaultColor = unchecked((int)0xFFFFFFFF);
        var borderRef = highContrast ? defaultColor : ToColorRef(state.CustomBorderColor ?? Border);

        if (state.HasApplied
            && state.AppliedHandle == handle
            && state.AppliedHighContrast == highContrast
            && state.AppliedBorderColorRef == borderRef)
        {
            return;
        }

        try
        {
            if (highContrast)
            {
                var darkOff = 0;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 20, ref darkOff, sizeof(int));
                var cornerDefault = 0;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 33, ref cornerDefault, sizeof(int));
                var hcColor = defaultColor;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 34, ref hcColor, sizeof(int));
            }
            else
            {
                var darkMode = 1;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
                var roundedCorners = 2;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 33, ref roundedCorners, sizeof(int));
                var border = borderRef;
                _ = NativeMethods.DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            }

            state.AppliedHandle = handle;
            state.AppliedHighContrast = highContrast;
            state.AppliedBorderColorRef = borderRef;
            state.HasApplied = true;
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

    private sealed class WindowFrameState
    {
        public Color? CustomCaptionColor { get; set; }
        public Color? CustomBorderColor { get; set; }
        public Color? CustomTextColor { get; set; }
        public IntPtr AppliedHandle { get; set; }
        public bool AppliedHighContrast { get; set; }
        public int AppliedBorderColorRef { get; set; }
        public int AppliedCaptionColorRef { get; set; }
        public int AppliedTextColorRef { get; set; }
        public bool HasApplied { get; set; }
    }

    private sealed class MenuFrameState
    {
        public Color? CustomBorderColor { get; set; }
        public IntPtr AppliedHandle { get; set; }
        public bool AppliedHighContrast { get; set; }
        public int AppliedBorderColorRef { get; set; }
        public bool HasApplied { get; set; }
    }

    private sealed class AppliedFormThemeState
    {
        public bool EventsWired { get; set; }
        public Button[] AccentButtons { get; set; } = [];
    }

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

    internal static class UiTransitionAnimator
    {
        internal const int DefaultDurationMs = 110;

        [ThreadStatic]
        private static System.Windows.Forms.Timer? threadTimer;

        [ThreadStatic]
        private static List<FloatTransition>? activeTransitions;

        private sealed class FloatTransition
        {
            public required Control Owner { get; init; }
            public required string Key { get; init; }
            public float StartValue { get; init; }
            public float TargetValue { get; init; }
            public float CurrentValue { get; set; }
            public long StartTick { get; init; }
            public int DurationMs { get; init; }
            public required Action<float> Apply { get; init; }
        }

        internal static void AnimateFloat(
            Control owner,
            string key,
            float fromValue,
            float targetValue,
            Action<float> apply,
            int durationMs = DefaultDurationMs)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(apply);

            if (SystemInformation.HighContrast
                || !SystemInformation.UIEffectsEnabled
                || durationMs <= 0
                || owner.IsDisposed
                || !owner.IsHandleCreated
                || !owner.Visible
                || MathF.Abs(targetValue - fromValue) < 0.001f)
            {
                RemoveExisting(owner, key);
                if (!owner.IsDisposed) apply(targetValue);
                return;
            }

            var list = activeTransitions ??= new List<FloatTransition>(8);
            var startValue = fromValue;
            for (var index = list.Count - 1; index >= 0; index--)
            {
                var item = list[index];
                if (ReferenceEquals(item.Owner, owner) && string.Equals(item.Key, key, StringComparison.Ordinal))
                {
                    startValue = item.CurrentValue;
                    list.RemoveAt(index);
                    break;
                }
            }

            if (MathF.Abs(targetValue - startValue) < 0.001f)
            {
                apply(targetValue);
                StopTimerIfIdle();
                return;
            }

            list.Add(new FloatTransition
            {
                Owner = owner,
                Key = key,
                StartValue = startValue,
                TargetValue = targetValue,
                CurrentValue = startValue,
                StartTick = Environment.TickCount64,
                DurationMs = durationMs,
                Apply = apply
            });

            EnsureTimerRunning();
        }

        internal static void Cancel(Control owner)
        {
            if (activeTransitions is not { Count: > 0 } list) return;
            for (var index = list.Count - 1; index >= 0; index--)
            {
                if (ReferenceEquals(list[index].Owner, owner))
                {
                    list.RemoveAt(index);
                }
            }
            StopTimerIfIdle();
        }

        private static void RemoveExisting(Control owner, string key)
        {
            if (activeTransitions is not { Count: > 0 } list) return;
            for (var index = list.Count - 1; index >= 0; index--)
            {
                var item = list[index];
                if (ReferenceEquals(item.Owner, owner) && string.Equals(item.Key, key, StringComparison.Ordinal))
                {
                    list.RemoveAt(index);
                }
            }
            StopTimerIfIdle();
        }

        private static void EnsureTimerRunning()
        {
            if (threadTimer is null)
            {
                threadTimer = new System.Windows.Forms.Timer { Interval = 16 };
                threadTimer.Tick += OnTimerTick;
            }
            if (!threadTimer.Enabled)
            {
                threadTimer.Start();
            }
        }

        private static void StopTimerIfIdle()
        {
            if (activeTransitions is { Count: > 0 }) return;
            if (threadTimer is not null)
            {
                threadTimer.Stop();
                threadTimer.Tick -= OnTimerTick;
                threadTimer.Dispose();
                threadTimer = null;
            }
        }

        private static void OnTimerTick(object? sender, EventArgs e)
        {
            var list = activeTransitions;
            if (list is null || list.Count == 0)
            {
                StopTimerIfIdle();
                return;
            }

            var nowTick = Environment.TickCount64;
            var immediateSnap = SystemInformation.HighContrast || !SystemInformation.UIEffectsEnabled;
            for (var index = list.Count - 1; index >= 0; index--)
            {
                var transition = list[index];
                if (transition.Owner.IsDisposed)
                {
                    list.RemoveAt(index);
                    continue;
                }

                if (immediateSnap || !transition.Owner.IsHandleCreated || !transition.Owner.Visible)
                {
                    transition.CurrentValue = transition.TargetValue;
                    list.RemoveAt(index);
                    transition.Apply(transition.TargetValue);
                    continue;
                }

                var elapsed = nowTick - transition.StartTick;
                var progress = Math.Clamp((float)elapsed / transition.DurationMs, 0f, 1f);
                var eased = 1f - MathF.Pow(1f - progress, 3f);
                var nextValue = progress >= 1f
                    ? transition.TargetValue
                    : transition.StartValue + ((transition.TargetValue - transition.StartValue) * eased);
                transition.CurrentValue = nextValue;
                if (progress >= 1f)
                {
                    list.RemoveAt(index);
                }
                transition.Apply(nextValue);
            }

            StopTimerIfIdle();
        }
    }

    private static class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);
    }
}
