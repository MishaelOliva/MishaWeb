using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MishaWeb;

internal enum SmartSearchClassification
{
    Empty,
    Search,
    Address,
    Invalid
}

internal readonly record struct SmartSearchPresentation(
    SmartSearchClassification Classification,
    string Label,
    string ActionText,
    string Error)
{
    public bool CanSubmit => Classification is SmartSearchClassification.Search
        or SmartSearchClassification.Address;
}

/// <summary>
/// A renderer-free command bar used by the native start page. It deliberately
/// exposes only local input and keyboard events; suggestions remain owned by
/// the browser window so the same popup can be anchored to the omnibox.
/// </summary>
internal sealed class SmartSearchBar : UserControl
{
    private const int CompactWidthLogical = 500;
    private const int WideHintWidthLogical = 650;
    private const int HintColumnWidthLogical = 72;
    private const int ProviderColumnWidthLogical = 124;
    private const int ClearColumnWidthLogical = 26;
    private const int ActionWidthLogical = 108;
    private const int CompactActionWidthLogical = 48;

    // Custom WinForms controls must paint opaque pixels on every frame. Alpha-filled
    // child buffers accumulate stale glyphs during interactive resizing on some GPUs.
    private static readonly Color SurfaceColor = Color.FromArgb(255, 52, 21, 51);
    private static readonly Color SurfaceColorSecondary = SurfaceColor;
    private static readonly Color InputSurfaceColor = SurfaceColor;
    private static readonly Color BorderColor = Color.FromArgb(205, 198, 108, 158);
    private static readonly Color FocusColor = NativeUiTheme.Focus;
    private static readonly Color PrimaryTextColor = NativeUiTheme.Text;
    private static readonly Color SecondaryTextColor = NativeUiTheme.SecondaryText;
    private static readonly Color MutedTextColor = NativeUiTheme.Muted;
    private static readonly Color AccentColor = NativeUiTheme.Accent;
    private static readonly Color AccentHoverColor = NativeUiTheme.AccentHover;
    private static readonly Color AccentPressedColor = NativeUiTheme.AccentPressed;
    private static readonly Color PillColor = BlendOpaque(Color.FromArgb(86, 36, 76), SurfaceColor, 242);
    private static readonly Color PillHoverColor = BlendOpaque(Color.FromArgb(108, 46, 95), SurfaceColor, 248);
    private static readonly Color PillPressedColor = BlendOpaque(Color.FromArgb(124, 52, 108), SurfaceColor, 252);
    private static readonly Color IdleActionTextColor = Color.FromArgb(255, 242, 212, 228);
    private static readonly Font InputFont = NativeUiTheme.GetSharedFont("Segoe UI", 11f);
    private static readonly Font LabelFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 8f);
    private static readonly Font ProviderFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 8.25f);
    private static readonly Font ActionFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 9f);
    private static readonly Font HintFont = NativeUiTheme.GetSharedFont("Segoe UI", 7.6f);

    private readonly GlassSurface surface = new();
    private readonly DoubleBufferedTableLayoutPanel layout = new();
    private readonly SearchGlyph icon = new();
    private readonly TextBox input = new();
    private readonly GlassModeLabel modeLabel = new();
    private readonly RoundedHintLabel hintLabel = new();
    private readonly ProviderPillButton providerButton = new();
    private readonly ClearSearchButton clearButton = new();
    private readonly ArrowActionButton actionButton = new();
    private readonly ToolTip toolTip = new();
    private string providerId = BrowserPolicy.DefaultSearchProviderId;
    private SmartSearchPresentation presentation;
    private bool hasPresentedState;
    private string lastToolTipText = string.Empty;
    private bool compactMinimumSize;
    private int lastAppliedCompactState = -1;

    public SmartSearchBar()
    {
        BuildControls();
    }

    private void BuildControls()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        MinimumSize = new Size(260, 48);
        TabStop = false;
        AccessibleRole = AccessibleRole.Grouping;
        AccessibleName = "Smart search command bar";
        AccessibleDescription = "Search the web or open a website address";

        surface.Dock = DockStyle.Fill;
        surface.Margin = Padding.Empty;
        surface.Padding = new Padding(2);
        surface.AccessibleRole = AccessibleRole.Grouping;
        surface.AccessibleName = "Smart search command bar surface";
        surface.FocusBorderColor = FocusColor;
        surface.Enter += (_, _) => UpdateFocusState();
        surface.Leave += (_, _) => UpdateFocusState();

        layout.Dock = DockStyle.Fill;
        layout.Margin = Padding.Empty;
        layout.Padding = new Padding(7, 5, 7, 5);
        TrySetTransparentBackColor(layout);
        layout.ColumnCount = 7;
        layout.RowCount = 2;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 45));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));

        icon.Dock = DockStyle.Fill;
        icon.Margin = Padding.Empty;
        icon.AccessibleName = "Search input";
        icon.AccessibleRole = AccessibleRole.Graphic;

        modeLabel.Dock = DockStyle.Fill;
        modeLabel.Margin = new Padding(6, 0, 0, 0);
        modeLabel.Font = LabelFont;
        modeLabel.ForeColor = FocusColor;
        modeLabel.TextAlign = ContentAlignment.MiddleLeft;
        modeLabel.AutoEllipsis = true;
        modeLabel.TabStop = false;
        TrySetTransparentBackColor(modeLabel);
        modeLabel.AccessibleRole = AccessibleRole.StaticText;

        hintLabel.AutoSize = true;
        hintLabel.Margin = new Padding(0, 1, 6, 1);
        hintLabel.Font = HintFont;
        hintLabel.ForeColor = SecondaryTextColor;
        hintLabel.Text = "CTRL + K";
        hintLabel.TextAlign = ContentAlignment.MiddleCenter;
        hintLabel.Padding = new Padding(6, 2, 6, 2);
        TrySetTransparentBackColor(hintLabel);
        hintLabel.AccessibleName = "Keyboard shortcut Ctrl plus K";
        hintLabel.AccessibleRole = AccessibleRole.StaticText;

        providerButton.Dock = DockStyle.Fill;
        providerButton.Margin = new Padding(0, 0, 6, 0);
        providerButton.MinimumSize = new Size(118, 0);
        providerButton.Font = ProviderFont;
        providerButton.ForeColor = SecondaryTextColor;
        ConfigureProviderButton();
        providerButton.Click += (_, _) => ProviderRequested?.Invoke(this, EventArgs.Empty);

        clearButton.Dock = DockStyle.Fill;
        clearButton.Margin = new Padding(0, 1, 4, 2);
        clearButton.Visible = false;
        clearButton.Click += (_, _) => ClearInput();
        toolTip.SetToolTip(clearButton, "Clear search");

        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(6, 1, 4, 3);
        input.AutoSize = false;
        input.BorderStyle = BorderStyle.None;
        TrySetBackColor(input, InputSurfaceColor);
        input.ForeColor = PrimaryTextColor;
        input.Font = InputFont;
        input.PlaceholderText = "Search the web or enter an address";
        input.MaxLength = BrowserPolicy.MaximumUrlLength;
        input.TabIndex = 0;
        input.AccessibleName = "Search or open a website";
        input.AccessibleDescription = "Type search terms or a website address. Press Enter to continue.";
        input.TextChanged += (_, _) =>
        {
            UpdatePresentation();
            InputChanged?.Invoke(this, EventArgs.Empty);
        };
        input.Enter += (_, _) => UpdateFocusState();
        input.Leave += (_, _) => UpdateFocusState();
        input.KeyDown += OnInputKeyDown;

        actionButton.Dock = DockStyle.Fill;
        actionButton.Margin = new Padding(1, 1, 2, 1);
        actionButton.Font = ActionFont;
        actionButton.ForeColor = NativeUiTheme.AccentText;
        actionButton.FillColor = AccentColor;
        actionButton.HoverColor = AccentHoverColor;
        actionButton.PressedColor = AccentPressedColor;
        actionButton.AccessibleRole = AccessibleRole.PushButton;
        toolTip.SetToolTip(actionButton, "Run this search or address");
        actionButton.Click += (_, _) => SubmitRequested?.Invoke(this, EventArgs.Empty);
        actionButton.Enter += (_, _) => UpdateFocusState();
        actionButton.Leave += (_, _) => UpdateFocusState();

        layout.Controls.Add(icon, 0, 0);
        layout.SetRowSpan(icon, 2);
        layout.Controls.Add(modeLabel, 1, 0);
        layout.Controls.Add(hintLabel, 3, 0);
        layout.Controls.Add(providerButton, 4, 0);
        layout.Controls.Add(input, 1, 1);
        layout.SetColumnSpan(input, 4);
        layout.Controls.Add(clearButton, 5, 1);
        layout.Controls.Add(actionButton, 6, 0);
        layout.SetRowSpan(actionButton, 2);
        surface.Controls.Add(layout);
        Controls.Add(surface);

        NativeUiTheme.ConfigureGlassToolTip(toolTip);
        ApplyDpiMetrics();
        UpdatePresentation();
        ApplyResponsiveLayout();
        ApplySystemColorPalette();
    }

    public event EventHandler? InputChanged;
    public event EventHandler<KeyEventArgs>? SearchKeyDown;
    public event EventHandler? SubmitRequested;
    public event EventHandler? ProviderRequested;

    public Control SuggestionAnchor => this;

    public new string Text
    {
        get => input.Text;
        set
        {
            if (input.Text == (value ?? string.Empty)) return;
            input.Text = value ?? string.Empty;
            input.SelectionStart = input.TextLength;
        }
    }

    public TextBox InputControl => input;

    public string ProviderId
    {
        get => providerId;
        set
        {
            var normalized = BrowserPolicy.NormalizeSearchProviderId(value);
            if (providerId == normalized) return;
            providerId = normalized;
            ConfigureProviderButton();
            UpdatePresentation();
        }
    }

    public SmartSearchClassification Classification => presentation.Classification;
    public string DynamicLabel => presentation.Label;
    public string DynamicAction => presentation.ActionText;
    public string InlineError => presentation.Error;
    public bool CanSubmit => presentation.CanSubmit;

    public void FocusInput(bool selectAll = true)
    {
        if (!input.CanFocus) return;
        input.Focus();
        if (selectAll) input.SelectAll();
    }

    public void ClearInput()
    {
        input.Clear();
        FocusInput(false);
    }

    internal bool SubmitForTesting()
    {
        if (!CanSubmit) return false;
        SubmitRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDpiMetrics();
        ApplyResponsiveLayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyResponsiveLayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpiMetrics();
        ApplyResponsiveLayout();
        Invalidate(true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!NativeStartPage.PaintChildBackdropSlice(this, e)) base.OnPaintBackground(e);
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        ApplySystemColorPalette();
    }

    private void ApplySystemColorPalette()
    {
        input.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : PrimaryTextColor;
        TrySetBackColor(input, SystemInformation.HighContrast ? SystemColors.Window : InputSurfaceColor, SystemColors.Window);
        modeLabel.ForeColor = SystemInformation.HighContrast
            ? SystemColors.WindowText
            : presentation.Classification == SmartSearchClassification.Invalid
                ? NativeUiTheme.Negative
                : FocusColor;
        hintLabel.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : SecondaryTextColor;
        surface.InvalidateInteriorCache();
        Invalidate(true);
    }

    private static void TrySetTransparentBackColor(Control control)
    {
        try
        {
            control.BackColor = Color.Transparent;
        }
        catch (ArgumentException)
        {
            // Control does not allow transparent backgrounds; keep the default color.
        }
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
            if (color.A < 255)
            {
                try
                {
                    control.BackColor = Color.FromArgb(255, color.R, color.G, color.B);
                    return;
                }
                catch (ArgumentException)
                {
                    // Fallback to the provided opaque default.
                }
            }
            else if (fallback is null)
            {
                return;
            }
        }

        if (fallback is null) return;

        try
        {
            control.BackColor = fallback.Value;
        }
        catch (ArgumentException)
        {
            // Give up silently; back color is best-effort for startup paths.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        SearchKeyDown?.Invoke(this, e);
        if (e.Handled || e.SuppressKeyPress) return;

        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (CanSubmit) SubmitRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (e.KeyCode == Keys.Escape && input.TextLength > 0)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ClearInput();
        }
    }

    private void ConfigureProviderButton()
    {
        providerButton.Text = BrowserPolicy.GetSearchProviderName(providerId);
        providerButton.AccessibleName = $"Search engine: {BrowserPolicy.GetSearchProviderName(providerId)}";
        providerButton.AccessibleDescription = "Choose the search engine used for search suggestions and searches";
        toolTip.SetToolTip(providerButton, "Choose search engine");
    }

    internal void SetCompactMinimumSize(bool enabled)
    {
        if (compactMinimumSize == enabled) return;
        compactMinimumSize = enabled;
        ApplyDpiMetrics();
    }

    private void UpdatePresentation()
    {
        SmartSearchPresentation nextPresentation;
        var value = input.Text.Trim();
        if (value.Length == 0)
        {
            nextPresentation = new SmartSearchPresentation(
                SmartSearchClassification.Empty,
                "Search or open a site",
                "Go",
                string.Empty);
        }
        else
        {
            var resolution = BrowserPolicy.ResolveAddress(value, providerId);
            if (resolution.Error is not null)
            {
                nextPresentation = new SmartSearchPresentation(
                    SmartSearchClassification.Invalid,
                    resolution.Error,
                    "Go",
                    resolution.Error);
            }
            else if (IsResolvedAddressNavigation(resolution, value, providerId))
            {
                nextPresentation = new SmartSearchPresentation(
                    SmartSearchClassification.Address,
                    "Open website",
                    "Open",
                    string.Empty);
            }
            else
            {
                nextPresentation = new SmartSearchPresentation(
                    SmartSearchClassification.Search,
                    $"Search with {BrowserPolicy.GetSearchProviderName(providerId)}",
                    "Search",
                    string.Empty);
            }
        }

        if (!hasPresentedState || nextPresentation != presentation)
        {
            presentation = nextPresentation;
            hasPresentedState = true;

            modeLabel.Text = presentation.Label;
            modeLabel.AccessibleName = presentation.Label;
            modeLabel.ForeColor = SystemInformation.HighContrast
                ? SystemColors.WindowText
                : presentation.Classification == SmartSearchClassification.Invalid
                    ? NativeUiTheme.Negative
                    : FocusColor;
            input.AccessibleDescription = presentation.Error.Length == 0
                ? "Type search terms or a website address. Press Enter to continue."
                : $"{presentation.Error}. Correct the value and try again.";
            actionButton.Text = presentation.ActionText;
            actionButton.Enabled = presentation.CanSubmit;
            actionButton.AccessibleName = presentation.Classification switch
            {
                SmartSearchClassification.Search => "Search",
                SmartSearchClassification.Address => "Open website",
                SmartSearchClassification.Invalid => "Invalid address",
                _ => "Go"
            };
            actionButton.AccessibleDescription = presentation.Error.Length == 0
                ? $"{presentation.ActionText} the current command"
                : presentation.Error;

            var nextToolTip = presentation.Error.Length == 0 ? presentation.Label : presentation.Error;
            if (!string.Equals(lastToolTipText, nextToolTip, StringComparison.Ordinal))
            {
                lastToolTipText = nextToolTip;
                toolTip.SetToolTip(this, nextToolTip);
            }
        }

        if (ApplyResponsiveVisibility())
        {
            surface.Invalidate(false);
        }
        surface.IsFocused = input.Focused || actionButton.Focused || providerButton.Focused;
    }

    private static bool IsResolvedAddressNavigation(
        NavigationResolution resolution,
        string value,
        string providerId)
    {
        if (resolution.IsStartPage || string.IsNullOrEmpty(resolution.Url)) return false;
        if (!resolution.Url.Contains("/search?q=", StringComparison.OrdinalIgnoreCase)) return true;
        return !string.Equals(
            resolution.Url,
            BrowserPolicy.CreateSearchUrl(value, providerId),
            StringComparison.Ordinal);
    }

    private void UpdateFocusState()
    {
        surface.IsFocused = input.Focused || actionButton.Focused || providerButton.Focused;
    }

    private void ApplyResponsiveLayout()
    {
        if (layout.ColumnStyles.Count < 7) return;
        if (ApplyResponsiveVisibility())
        {
            surface.Invalidate(false);
        }
    }

    private bool ApplyResponsiveVisibility()
    {
        if (layout.ColumnStyles.Count < 7) return false;

        var compact = Width > 0 && Width < Scale(CompactWidthLogical);
        var micro = compactMinimumSize && Width > 0 && Width < Scale(260);
        var showHint = !compact
            && Width >= Scale(WideHintWidthLogical)
            && input.TextLength == 0;
        var showClear = input.TextLength > 0;
        var nextState = (compact ? 1 : 0) | (micro ? 2 : 0) | (showHint ? 4 : 0) | (showClear ? 8 : 0);
        if (nextState == lastAppliedCompactState) return false;
        lastAppliedCompactState = nextState;

        layout.SuspendLayout();
        try
        {
            modeLabel.Visible = !compact;
            hintLabel.Visible = showHint;
            providerButton.Visible = !compact;
            clearButton.Visible = showClear;
            layout.ColumnStyles[0].SizeType = SizeType.Absolute;
            layout.ColumnStyles[0].Width = Scale(micro ? 32 : 45);
            layout.ColumnStyles[2].SizeType = SizeType.Absolute;
            layout.ColumnStyles[2].Width = Scale(micro ? 2 : 8);
            layout.ColumnStyles[3].SizeType = SizeType.Absolute;
            layout.ColumnStyles[3].Width = showHint ? Scale(HintColumnWidthLogical) : 0;
            layout.ColumnStyles[4].SizeType = SizeType.Absolute;
            layout.ColumnStyles[4].Width = compact ? 0 : Scale(ProviderColumnWidthLogical);
            layout.ColumnStyles[5].SizeType = SizeType.Absolute;
            layout.ColumnStyles[5].Width = showClear ? Scale(ClearColumnWidthLogical) : 0;
            layout.ColumnStyles[6].SizeType = SizeType.Absolute;
            layout.ColumnStyles[6].Width = Scale(compact ? CompactActionWidthLogical : ActionWidthLogical);
            actionButton.Compact = compact;
        }
        finally
        {
            layout.ResumeLayout(true);
        }
        return true;
    }

    private void ApplyDpiMetrics()
    {
        MinimumSize = new Size(
            Scale(compactMinimumSize ? 180 : 260),
            Scale(compactMinimumSize ? 42 : 48));
        lastAppliedCompactState = -1;
        surface.Padding = new Padding(Scale(2));
        surface.CornerRadius = Scale(20);
        layout.Padding = new Padding(Scale(7), Scale(5), Scale(7), Scale(5));
        layout.RowStyles[0].Height = Scale(21);
        layout.ColumnStyles[0].SizeType = SizeType.Absolute;
        layout.ColumnStyles[0].Width = Scale(45);
        layout.ColumnStyles[2].SizeType = SizeType.Absolute;
        layout.ColumnStyles[2].Width = Scale(8);

        modeLabel.Margin = new Padding(Scale(6), 0, 0, 0);
        hintLabel.Margin = new Padding(0, Scale(1), Scale(6), Scale(1));
        hintLabel.Padding = new Padding(Scale(6), Scale(2), Scale(6), Scale(2));
        providerButton.Margin = new Padding(0, 0, Scale(6), 0);
        providerButton.MinimumSize = new Size(Scale(118), 0);
        providerButton.CornerRadius = Scale(10);
        clearButton.Margin = new Padding(0, Scale(1), Scale(4), Scale(2));
        input.Margin = new Padding(Scale(6), Scale(1), Scale(4), Scale(2));
        actionButton.Margin = new Padding(Scale(2), Scale(1), Scale(2), Scale(1));
        actionButton.CornerRadius = Scale(14);
    }

    private int Scale(int logicalValue)
    {
        return Math.Max(1, (logicalValue * DeviceDpi + 48) / 96);
    }

    private static Color FlattenColor(Color color, Color background)
    {
        return color.A == 255 ? color : BlendOpaque(color, background, color.A);
    }

    private static Color BlendOpaque(Color foreground, Color background, int foregroundAlpha)
    {
        var alpha = Math.Clamp(foregroundAlpha, 0, 255);
        var inverse = 255 - alpha;
        return Color.FromArgb(
            255,
            ((foreground.R * alpha) + (background.R * inverse) + 127) / 255,
            ((foreground.G * alpha) + (background.G * inverse) + 127) / 255,
            ((foreground.B * alpha) + (background.B * inverse) + 127) / 255);
    }

    private static void PaintLiquidGlassSlice(Control child, PaintEventArgs e)
    {
        if (e.ClipRectangle.IsEmpty) return;

        if (SystemInformation.HighContrast)
        {
            using var bg = new SolidBrush(SystemColors.Window);
            e.Graphics.FillRectangle(bg, e.ClipRectangle);
            return;
        }

        if (!NativeStartPage.PaintChildBackdropSlice(child, e))
        {
            using var fallbackBg = new SolidBrush(NativeUiTheme.Window);
            e.Graphics.FillRectangle(fallbackBg, e.ClipRectangle);
        }

        GlassSurface? hostSurface = null;
        var offsetX = 0;
        var offsetY = 0;
        Control? current = child;
        while (current is not null && current is not GlassSurface)
        {
            offsetX += current.Left;
            offsetY += current.Top;
            current = current.Parent;
        }

        if (current is GlassSurface found)
        {
            hostSurface = found;
        }

        if (hostSurface is null)
        {
            using var fallbackSurface = new SolidBrush(SurfaceColor);
            e.Graphics.FillRectangle(fallbackSurface, e.ClipRectangle);
            return;
        }

        var childInHost = new Rectangle(
            offsetX + e.ClipRectangle.X,
            offsetY + e.ClipRectangle.Y,
            e.ClipRectangle.Width,
            e.ClipRectangle.Height);
        hostSurface.PaintGlassInteriorSlice(e.Graphics, childInHost, e.ClipRectangle);
    }

    private sealed class DoubleBufferedTableLayoutPanel : TableLayoutPanel
    {
        public DoubleBufferedTableLayoutPanel()
        {
            DoubleBuffered = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw,
                true);
        }
    }

    private sealed class GlassModeLabel : Label
    {
        public GlassModeLabel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintLiquidGlassSlice(this, e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var text = Text ?? string.Empty;
            if (text.Length == 0) return;

            TextRenderer.DrawText(
                e.Graphics,
                text,
                Font,
                ClientRectangle,
                SystemInformation.HighContrast ? SystemColors.WindowText : ForeColor,
                TextFormatFlags.Left
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine);
        }
    }

    private sealed class ClearSearchButton : Control
    {
        private bool hovered;
        private bool pressed;

        public ClearSearchButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
            TrySetTransparentBackColor(this);
            Cursor = Cursors.Hand;
            TabStop = false;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = "Clear search";
            AccessibleDescription = "Clear the search input";
        }

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

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Painted inside double-buffered OnPaint.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintLiquidGlassSlice(this, e);
            if (Width <= 4 || Height <= 4) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = Math.Max(1f, DeviceDpi / 96f);
            var side = Math.Min(Math.Min(Width, Height) - (2f * scale), 20f * scale);
            if (side < 8f) side = Math.Min(Width, Height);
            var circle = new RectangleF(
                (Width - side) / 2f,
                (Height - side) / 2f,
                side,
                side);

            var highContrast = SystemInformation.HighContrast;
            if (hovered || pressed)
            {
                using var bg = new SolidBrush(
                    highContrast
                        ? SystemColors.Highlight
                        : Color.FromArgb(pressed ? 160 : 96, NativeUiTheme.Negative));
                g.FillEllipse(bg, circle);
            }

            var glyphColor = highContrast
                ? (hovered || pressed ? SystemColors.HighlightText : SystemColors.WindowText)
                : (hovered || pressed ? PrimaryTextColor : SecondaryTextColor);
            var half = Math.Max(3f, side * 0.22f);
            var cx = circle.X + (circle.Width / 2f);
            var cy = circle.Y + (circle.Height / 2f);
            using var pen = new Pen(glyphColor, Math.Max(1.25f, 1.45f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(pen, cx - half, cy - half, cx + half, cy + half);
            g.DrawLine(pen, cx + half, cy - half, cx - half, cy + half);
        }
    }

    private sealed class GlassSurface : Panel
    {
        private bool isFocused;
        private Bitmap? interiorCache;
        private Size interiorCacheSize;
        private bool interiorCacheFocused;
        private bool interiorCacheHighContrast;

        public GlassSurface()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
            CornerRadius = 20;
        }

        public int CornerRadius { get; set; }
        public Color FocusBorderColor { get; set; } = FocusColor;
        public bool IsFocused
        {
            get => isFocused;
            set
            {
                if (isFocused == value) return;
                isFocused = value;
                InvalidateInteriorCache();
                Invalidate(true);
            }
        }

        public void InvalidateInteriorCache()
        {
            interiorCache?.Dispose();
            interiorCache = null;
            interiorCacheSize = Size.Empty;
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            if (interiorCache is not null && interiorCacheSize != Size)
            {
                InvalidateInteriorCache();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                InvalidateInteriorCache();
            }
            base.Dispose(disposing);
        }

        private Bitmap? EnsureInteriorCache()
        {
            if (Width <= 1 || Height <= 1) return null;
            var highContrast = SystemInformation.HighContrast;
            if (interiorCache is not null
                && interiorCacheSize == Size
                && interiorCacheFocused == isFocused
                && interiorCacheHighContrast == highContrast)
            {
                return interiorCache;
            }

            InvalidateInteriorCache();
            var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            try
            {
                bitmap.SetResolution(96f, 96f);
                using var g = Graphics.FromImage(bitmap);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.Clear(Color.Transparent);
                var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
                using var tintBrush = new LinearGradientBrush(
                    bounds,
                    Color.FromArgb(186, 72, 30, 68),
                    Color.FromArgb(232, 62, 24, 58),
                    LinearGradientMode.Vertical);
                tintBrush.InterpolationColors = new ColorBlend
                {
                    Colors = new[]
                    {
                        Color.FromArgb(182, 74, 32, 70),
                        SurfaceColor,
                        SurfaceColor,
                        Color.FromArgb(228, 72, 26, 66)
                    },
                    Positions = new[] { 0f, 0.40f, 0.87f, 1f }
                };
                g.FillRectangle(tintBrush, 0, 0, Width, Height);

                var specHeight = Math.Max(8f, bounds.Height * 0.41f);
                var specRect = new RectangleF(
                    bounds.X - (bounds.Width * 0.08f),
                    bounds.Y - (specHeight * 0.25f),
                    bounds.Width * 1.16f,
                    specHeight * 1.15f);
                using var specPath = new GraphicsPath();
                specPath.AddEllipse(specRect);
                using var specBrush = new LinearGradientBrush(
                    new RectangleF(bounds.X, bounds.Y, bounds.Width, specHeight + 1f),
                    Color.FromArgb(isFocused ? 54 : 38, 255, 244, 252),
                    Color.FromArgb(0, 255, 224, 244),
                    LinearGradientMode.Vertical);
                g.FillPath(specBrush, specPath);

                interiorCache = bitmap;
                interiorCacheSize = Size;
                interiorCacheFocused = isFocused;
                interiorCacheHighContrast = highContrast;
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        public void PaintGlassInteriorSlice(Graphics graphics, Rectangle sourceInHost, Rectangle destination)
        {
            var cached = EnsureInteriorCache();
            if (cached is null) return;

            var clampedSource = Rectangle.Intersect(new Rectangle(0, 0, cached.Width, cached.Height), sourceInHost);
            if (clampedSource.IsEmpty) return;

            var destRect = new Rectangle(
                destination.X + (clampedSource.X - sourceInHost.X),
                destination.Y + (clampedSource.Y - sourceInHost.Y),
                clampedSource.Width,
                clampedSource.Height);
            var previousInterpolation = graphics.InterpolationMode;
            var previousPixelOffset = graphics.PixelOffsetMode;
            try
            {
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.None;
                graphics.DrawImage(cached, destRect, clampedSource, GraphicsUnit.Pixel);
            }
            finally
            {
                graphics.InterpolationMode = previousInterpolation;
                graphics.PixelOffsetMode = previousPixelOffset;
            }
        }

        public void PaintGlassInterior(Graphics graphics)
        {
            var cached = EnsureInteriorCache();
            if (cached is null) return;

            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
            using var path = NativeUiTheme.CreateRoundedPath(bounds, CornerRadius);
            var state = graphics.Save();
            try
            {
                graphics.SetClip(path, CombineMode.Intersect);
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.None;
                graphics.DrawImage(
                    cached,
                    new Rectangle(0, 0, Width, Height),
                    new Rectangle(0, 0, cached.Width, cached.Height),
                    GraphicsUnit.Pixel);
            }
            finally
            {
                graphics.Restore(state);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!NativeStartPage.PaintChildBackdropSlice(this, e))
            {
                using var bg = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window : NativeUiTheme.Window);
                e.Graphics.FillRectangle(bg, ClientRectangle);
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
            using var path = NativeUiTheme.CreateRoundedPath(bounds, CornerRadius);
            if (SystemInformation.HighContrast)
            {
                using var fill = new SolidBrush(SystemColors.Window);
                using var border = new Pen(SystemColors.WindowText, isFocused ? 2f : 1f);
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
            else
            {
                PaintGlassInterior(e.Graphics);

                using var outerRimBrush = new LinearGradientBrush(
                    bounds,
                    Color.FromArgb(isFocused ? 225 : 178, 255, 232, 246),
                    Color.FromArgb(isFocused ? 185 : 118, NativeUiTheme.Accent),
                    LinearGradientMode.Vertical);
                using var border = new Pen(outerRimBrush, isFocused ? 1.8f : 1.15f);
                e.Graphics.DrawPath(border, path);

                var innerBounds = bounds;
                innerBounds.Inflate(-1.1f, -1.1f);
                if (innerBounds.Width > 8f && innerBounds.Height > 8f)
                {
                    using var innerPath = NativeUiTheme.CreateRoundedPath(innerBounds, Math.Max(2f, CornerRadius - 1.1f));
                    using var innerRimBrush = new LinearGradientBrush(
                        innerBounds,
                        Color.FromArgb(isFocused ? 105 : 74, 255, 255, 255),
                        Color.FromArgb(isFocused ? 46 : 22, NativeUiTheme.Focus),
                        LinearGradientMode.Vertical);
                    using var innerBorder = new Pen(innerRimBrush, 1f);
                    e.Graphics.DrawPath(innerBorder, innerPath);
                }
            }
        }
    }

    private sealed class SearchGlyph : Control
    {
        public SearchGlyph()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            BackColor = FlattenColor(SurfaceColor, NativeUiTheme.Chrome);
            TabStop = false;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // All painting happens inside double-buffered OnPaint.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintLiquidGlassSlice(this, e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = Math.Max(1f, DeviceDpi / 96f);
            var tile = new RectangleF(
                Math.Max(1f, (Width - (34f * scale)) / 2f),
                Math.Max(1f, (Height - (34f * scale)) / 2f),
                34f * scale,
                34f * scale);
            if (SystemInformation.HighContrast)
            {
                using var hcBrush = new SolidBrush(SystemColors.Highlight);
                using var hcBorder = new Pen(SystemColors.WindowText, 1f);
                e.Graphics.FillEllipse(hcBrush, tile);
                e.Graphics.DrawEllipse(hcBorder, tile);
            }
            else
            {
                var halo = tile;
                halo.Inflate(4f * scale, 4f * scale);
                using var haloBrush = new SolidBrush(Color.FromArgb(44, NativeUiTheme.Accent));
                e.Graphics.FillEllipse(haloBrush, halo);
                using var tileBrush = new LinearGradientBrush(
                    tile,
                    NativeUiTheme.Accent,
                    NativeUiTheme.Lavender,
                    LinearGradientMode.ForwardDiagonal);
                e.Graphics.FillEllipse(tileBrush, tile);
                var specTile = new RectangleF(
                    tile.X + (tile.Width * 0.14f),
                    tile.Y + (1.5f * scale),
                    tile.Width * 0.72f,
                    tile.Height * 0.42f);
                using var specBrush = new LinearGradientBrush(
                    specTile,
                    Color.FromArgb(115, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255),
                    LinearGradientMode.Vertical);
                e.Graphics.FillEllipse(specBrush, specTile);

                var ringRect = tile;
                ringRect.Inflate(-0.75f * scale, -0.75f * scale);
                using var innerRing = new Pen(Color.FromArgb(148, 255, 255, 255), Math.Max(1f, 1.1f * scale));
                e.Graphics.DrawEllipse(innerRing, ringRect);
            }

            var totalGlyphSize = 14.5f * scale;
            var lensDiameter = 10f * scale;
            var left = tile.Left + ((tile.Width - totalGlyphSize) / 2f);
            var top = tile.Top + ((tile.Height - totalGlyphSize) / 2f);
            var radius = lensDiameter / 2f;
            var centerX = left + radius;
            var centerY = top + radius;
            var diag = radius * 0.7071f;
            using var pen = new Pen(
                SystemInformation.HighContrast ? SystemColors.HighlightText : NativeUiTheme.Text,
                Math.Max(1.4f, 1.75f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawEllipse(pen, left, top, lensDiameter, lensDiameter);
            e.Graphics.DrawLine(
                pen,
                centerX + diag,
                centerY + diag,
                left + totalGlyphSize,
                top + totalGlyphSize);
        }
    }

    private sealed class RoundedHintLabel : Label
    {
        public RoundedHintLabel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintLiquidGlassSlice(this, e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1f, Width - 1f), Math.Max(1f, Height - 1f));
            var radius = Math.Min(bounds.Height / 2f, 6f * Math.Max(1f, DeviceDpi / 96f));
            using var path = NativeUiTheme.CreateRoundedPath(bounds, radius);
            if (SystemInformation.HighContrast)
            {
                using var fill = new SolidBrush(SystemColors.Window);
                using var border = new Pen(SystemColors.WindowText, 1f);
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
            else
            {
                using var fill = new LinearGradientBrush(
                    bounds,
                    Color.FromArgb(62, 255, 224, 244),
                    Color.FromArgb(24, NativeUiTheme.Accent),
                    LinearGradientMode.Vertical);
                using var borderBrush = new LinearGradientBrush(
                    bounds,
                    Color.FromArgb(148, 255, 228, 245),
                    Color.FromArgb(52, NativeUiTheme.Accent),
                    LinearGradientMode.Vertical);
                using var border = new Pen(borderBrush, 1f);
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                ClientRectangle,
                SystemInformation.HighContrast ? SystemColors.WindowText : ForeColor,
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine);
        }
    }

    private sealed class ProviderPillButton : Button
    {
        private bool hovered;
        private bool pressed;

        public ProviderPillButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            UseMnemonic = false;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
        }

        public int CornerRadius { get; set; } = 10;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // All painting happens inside double-buffered OnPaint.
        }

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

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            pressed = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            pressed = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintLiquidGlassSlice(this, e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
            using var path = NativeUiTheme.CreateRoundedPath(bounds, CornerRadius);
            if (SystemInformation.HighContrast)
            {
                using var fill = new SolidBrush(Enabled ? SystemColors.Highlight : SystemColors.Control);
                using var border = new Pen(SystemColors.WindowText, Focused ? 1.6f : 1f);
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
            else
            {
                var topAlpha = !Enabled ? 46 : pressed ? 176 : hovered ? 142 : 96;
                var bottomAlpha = !Enabled ? 28 : pressed ? 132 : hovered ? 104 : 68;
                using var fill = new LinearGradientBrush(
                    bounds,
                    Color.FromArgb(topAlpha, 128, 56, 108),
                    Color.FromArgb(bottomAlpha, 72, 26, 66),
                    LinearGradientMode.Vertical);
                e.Graphics.FillPath(fill, path);

                var state = e.Graphics.Save();
                try
                {
                    e.Graphics.SetClip(path, CombineMode.Intersect);
                    var specRect = new RectangleF(bounds.X, bounds.Y, bounds.Width, Math.Max(4f, bounds.Height * 0.48f));
                    using var specBrush = new LinearGradientBrush(
                        specRect,
                        Color.FromArgb(hovered ? 78 : 54, 255, 255, 255),
                        Color.FromArgb(0, 255, 232, 246),
                        LinearGradientMode.Vertical);
                    e.Graphics.FillRectangle(specBrush, specRect);
                }
                finally
                {
                    e.Graphics.Restore(state);
                }

                using var borderBrush = new LinearGradientBrush(
                    bounds,
                    Color.FromArgb(Focused ? 235 : hovered ? 210 : 175, 255, 224, 245),
                    Color.FromArgb(Focused ? 185 : 96, NativeUiTheme.Accent),
                    LinearGradientMode.Vertical);
                using var border = new Pen(borderBrush, Focused ? 1.5f : 1f);
                e.Graphics.DrawPath(border, path);
            }

            var scale = Math.Max(1f, DeviceDpi / 96f);
            var iconCenter = new PointF(14f * scale, Height / 2f);
            using var iconPen = new Pen(
                SystemInformation.HighContrast
                    ? Enabled ? SystemColors.HighlightText : SystemColors.GrayText
                    : Enabled ? FocusColor : MutedTextColor,
                Math.Max(1.1f, 1.35f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            var lensRadius = 4.2f * scale;
            var lensDiameter = lensRadius * 2f;
            e.Graphics.DrawEllipse(
                iconPen,
                iconCenter.X - lensRadius,
                iconCenter.Y - lensRadius - (0.6f * scale),
                lensDiameter,
                lensDiameter);
            e.Graphics.DrawLine(
                iconPen,
                iconCenter.X + (lensRadius * 0.65f),
                iconCenter.Y + (lensRadius * 0.55f),
                iconCenter.X + (lensRadius * 1.35f),
                iconCenter.Y + (lensRadius * 1.25f));

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                new Rectangle(
                    (int)(26f * scale),
                    0,
                    Math.Max(1, Width - (int)(46f * scale)),
                    Height),
                SystemInformation.HighContrast
                    ? Enabled ? SystemColors.HighlightText : SystemColors.GrayText
                    : Enabled ? ForeColor : MutedTextColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            var chevronX = Width - (13f * scale);
            var chevronY = Height / 2f;
            using var chevron = new Pen(
                SystemInformation.HighContrast
                    ? Enabled ? SystemColors.HighlightText : SystemColors.GrayText
                    : Enabled ? SecondaryTextColor : MutedTextColor,
                Math.Max(1.1f, 1.3f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            e.Graphics.DrawLines(chevron,
            [
                new PointF(chevronX - (2.4f * scale), chevronY - (1.5f * scale)),
                new PointF(chevronX, chevronY + (1.6f * scale)),
                new PointF(chevronX + (2.4f * scale), chevronY - (1.5f * scale))
            ]);
        }
    }

    private sealed class ArrowActionButton : Button
    {
        private bool hovered;
        private bool pressed;

        public ArrowActionButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            UseMnemonic = false;
            Cursor = Cursors.Hand;
        }

        public Color FillColor { get; set; } = AccentColor;
        public Color HoverColor { get; set; } = AccentHoverColor;
        public Color PressedColor { get; set; } = AccentPressedColor;
        public int CornerRadius { get; set; } = 12;
        public bool Compact { get; set; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // All painting happens inside double-buffered OnPaint.
        }

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

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            pressed = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            pressed = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintLiquidGlassSlice(this, e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
            using var path = NativeUiTheme.CreateRoundedPath(bounds, CornerRadius);
            var color = SystemInformation.HighContrast
                ? SystemColors.Highlight
                : pressed ? PressedColor : hovered ? HoverColor : FillColor;
            if (SystemInformation.HighContrast)
            {
                using var hcFill = new SolidBrush(Enabled ? color : SystemColors.Control);
                using var hcBorder = new Pen(SystemColors.WindowText, Focused ? 1.8f : 1f);
                e.Graphics.FillPath(hcFill, path);
                e.Graphics.DrawPath(hcBorder, path);
            }
            else
            {
                var topColor = Enabled
                    ? Color.FromArgb(pressed ? 236 : hovered ? 228 : 210, color)
                    : Color.FromArgb(124, 152, 74, 126);
                var bottomColor = Enabled
                    ? Color.FromArgb(pressed ? 224 : hovered ? 210 : 188, NativeUiTheme.Lavender)
                    : Color.FromArgb(104, 116, 62, 118);
                using var fill = new LinearGradientBrush(
                    bounds,
                    topColor,
                    bottomColor,
                    LinearGradientMode.ForwardDiagonal);
                e.Graphics.FillPath(fill, path);

                var state = e.Graphics.Save();
                try
                {
                    e.Graphics.SetClip(path, CombineMode.Intersect);
                    var specHeight = Math.Max(6f, bounds.Height * 0.48f);
                    var specRect = new RectangleF(
                        bounds.X - (bounds.Width * 0.08f),
                        bounds.Y - (specHeight * 0.25f),
                        bounds.Width * 1.16f,
                        specHeight * 1.15f);
                    using var specPath = new GraphicsPath();
                    specPath.AddEllipse(specRect);
                    using var specBrush = new LinearGradientBrush(
                        new RectangleF(bounds.X, bounds.Y, bounds.Width, specHeight + 1f),
                        Color.FromArgb(Enabled ? 96 : 64, 255, 255, 255),
                        Color.FromArgb(0, 255, 232, 248),
                        LinearGradientMode.Vertical);
                    e.Graphics.FillPath(specBrush, specPath);
                }
                finally
                {
                    e.Graphics.Restore(state);
                }

                using var borderBrush = new LinearGradientBrush(
                    bounds,
                    Color.FromArgb(Focused ? 240 : Enabled ? 215 : 168, 255, 232, 246),
                    Color.FromArgb(Focused ? 200 : Enabled ? 148 : 88, NativeUiTheme.Accent),
                    LinearGradientMode.Vertical);
                using var border = new Pen(borderBrush, Focused ? 1.8f : 1.1f);
                e.Graphics.DrawPath(border, path);
            }

            var scale = Math.Max(1f, DeviceDpi / 96f);
            var contentColor = SystemInformation.HighContrast
                ? Enabled ? SystemColors.HighlightText : SystemColors.GrayText
                : Enabled ? ForeColor : IdleActionTextColor;
            var centerY = Height / 2f;
            float arrowCenterX;
            if (!Compact && !string.IsNullOrEmpty(Text))
            {
                var textSize = TextRenderer.MeasureText(
                    e.Graphics,
                    Text,
                    Font,
                    ClientSize,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                var gap = 6f * scale;
                var chevronWidth = 4f * scale;
                var groupWidth = Math.Min(Math.Max(1f, Width - (12f * scale)), textSize.Width + gap + chevronWidth);
                var groupLeft = Math.Max(6f * scale, (Width - groupWidth) / 2f);
                var textWidth = Math.Max(1, (int)Math.Ceiling(groupWidth - gap - chevronWidth));
                TextRenderer.DrawText(
                    e.Graphics,
                    Text,
                    Font,
                    new Rectangle((int)Math.Floor(groupLeft), 0, textWidth, Height),
                    contentColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                arrowCenterX = groupLeft + textWidth + gap + (chevronWidth / 2f);
            }
            else
            {
                arrowCenterX = Width / 2f;
            }

            using var arrow = new Pen(contentColor, Math.Max(1.2f, 1.4f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            e.Graphics.DrawLines(arrow,
            [
                new PointF(arrowCenterX - (2f * scale), centerY - (3.2f * scale)),
                new PointF(arrowCenterX + (1.5f * scale), centerY),
                new PointF(arrowCenterX - (2f * scale), centerY + (3.2f * scale))
            ]);
        }
    }
}
