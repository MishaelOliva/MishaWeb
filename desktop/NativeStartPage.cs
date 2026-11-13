using System.Drawing.Drawing2D;

namespace MishaWeb;

internal sealed record StartPageLink(string Title, string Url, string Category = "Recent");

internal sealed class StartPageLinkContextEventArgs(StartPageLink link, Point screenLocation) : EventArgs
{
    public StartPageLink Link { get; } = link;
    public Point ScreenLocation { get; } = screenLocation;
}

internal sealed record StartPageStatus(string ResourceMode, string Tabs)
{
    public static StartPageStatus Default { get; } = new("Memory saver", "1 tab open");
}

internal enum StartPageAction
{
    CycleResourceMode,
    ShowTabs
}

internal sealed class NativeStartPage : UserControl
{
    private const int MaximumQuickLinks = 3;
    private const int MaximumQuickLinkDisplayUrlCharacters = 512;
    private const int MaximumQuickLinkAccessibleDescriptionCharacters = 568;
    private const int MaximumQuickLinkDisplayHostCharacters = 120;

    private static readonly Color PageColor = NativeUiTheme.Window;
    private static readonly Color SurfaceColor = Color.FromArgb(180, NativeUiTheme.Surface);
    private static readonly Color SurfaceColorSecondary = Color.FromArgb(188, NativeUiTheme.Chrome);
    private static readonly Color SurfaceBorderColor = NativeUiTheme.Border;
    private static readonly Color DecorativeBorderColor = Color.FromArgb(190, NativeUiTheme.Border);
    private static readonly Color AccentColor = NativeUiTheme.Accent;
    private static readonly Color AccentColorSecondary = NativeUiTheme.Lavender;
    private static readonly Color AccentHoverColor = NativeUiTheme.AccentHover;
    private static readonly Color AccentPressedColor = NativeUiTheme.AccentPressed;
    private static readonly Color FocusColor = NativeUiTheme.Focus;
    private static readonly Color PrimaryTextColor = NativeUiTheme.Text;
    private static readonly Color SecondaryTextColor = NativeUiTheme.SecondaryText;
    private static readonly Color MutedTextColor = NativeUiTheme.Muted;
    private static readonly Color ChipColor = Color.FromArgb(255, 52, 22, 46);
    private static readonly Color ChipHoverColor = Color.FromArgb(255, 70, 30, 62);
    private static readonly Color ChipPressedColor = Color.FromArgb(255, 84, 36, 74);

    private static readonly Font BrandFont = new("Segoe UI Semibold", 27f, FontStyle.Regular);
    private static readonly Font TaglineFont = new("Segoe UI", 11f, FontStyle.Regular);
    private static readonly Font IncognitoTaglineFont = new("Segoe UI Semibold", 10f, FontStyle.Regular);
    private static readonly Font SearchFont = new("Segoe UI", 11f, FontStyle.Regular);
    private static readonly Font ActionFont = new("Segoe UI Semibold", 9.5f, FontStyle.Regular);
    private static readonly Font FeatureHeadingFont = new("Segoe UI Semibold", 9.2f, FontStyle.Regular);
    private static readonly Font FeatureDetailFont = new("Segoe UI", 8.2f, FontStyle.Regular);
    private static readonly Font LinkTitleFont = new("Segoe UI Semibold", 9.1f, FontStyle.Regular);
    private static readonly Font LinkDetailFont = new("Segoe UI", 8f, FontStyle.Regular);
    private static readonly Font LinkMonogramFont = new("Segoe UI Semibold", 10.5f, FontStyle.Regular);
    private static readonly Font LinkSiteLetterFont = new("Segoe UI", 22f, FontStyle.Bold);

    private readonly RoundedSurfacePanel card = new();
    private readonly TransparentTableLayoutPanel contentLayout = new();
    private readonly BadgeControl badge = new();
    private readonly BrandTitleLabel titleLabel = new();
    private readonly Label taglineLabel = new();
    private readonly SmartSearchBar searchBar = new();
    private readonly TransparentPanel featureHost = new();
    private readonly FeatureChip[] featureChips;
    private readonly SectionHeaderLabel quickLinksTitle = new();
    private readonly TransparentPanel quickLinksHost = new();
    private bool arranging = true;
    private bool backdropFrameLeaseHeld;
    private bool disposingBackdropFrameLease;
    private bool compactLayout;
    private StartPageStatus currentStatus = StartPageStatus.Default;
    private StartPageLink[] currentQuickLinks = [];
    private bool isPrivateModeActive;

    public NativeStartPage()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);

        Dock = DockStyle.Fill;
        BackColor = PageColor;
        ForeColor = PrimaryTextColor;
        Font = TaglineFont;
        TabStop = false;
        AccessibleName = "MishaWeb start page";
        AccessibleDescription = "A lightweight page for starting a search or opening a quick link";
        AccessibleRole = AccessibleRole.Pane;

        TrySetTransparentBackColor(card);
        // Keep the composition floating directly over the night garden. A translucent
        // WinForms child surface can flatten against black on some display paths, so
        // the outer card deliberately paints no fill or frame; the smaller feature,
        // search, and quick-link surfaces retain their own readable glass treatment.
        card.FillColor = Color.Transparent;
        card.FillColorSecondary = Color.Transparent;
        card.BorderColor = Color.Transparent;
        card.HighlightColor = Color.Transparent;
        card.AutoScroll = true;
        card.Margin = Padding.Empty;
        card.TabStop = false;
        card.AccessibleName = "Start page card";
        card.AccessibleRole = AccessibleRole.Pane;

        contentLayout.Dock = DockStyle.None;
        contentLayout.Margin = Padding.Empty;
        TrySetTransparentBackColor(contentLayout);
        contentLayout.ColumnCount = 1;
        contentLayout.RowCount = 8;
        contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < contentLayout.RowCount; index++)
        {
            contentLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        }
        contentLayout.TabStop = false;

        badge.Anchor = AnchorStyles.None;
        badge.Margin = Padding.Empty;
        badge.TabStop = false;

        titleLabel.Dock = DockStyle.Fill;
        titleLabel.Margin = Padding.Empty;
        TrySetTransparentBackColor(titleLabel);
        titleLabel.ForeColor = PrimaryTextColor;
        titleLabel.Font = BrandFont;
        titleLabel.Text = "MishaWeb";
        titleLabel.TextAlign = ContentAlignment.MiddleCenter;
        titleLabel.AutoEllipsis = true;
        titleLabel.TabStop = false;
        titleLabel.AccessibleName = "MishaWeb";
        titleLabel.AccessibleRole = AccessibleRole.StaticText;

        taglineLabel.Dock = DockStyle.Fill;
        taglineLabel.Margin = Padding.Empty;
        TrySetTransparentBackColor(taglineLabel);
        taglineLabel.ForeColor = SecondaryTextColor;
        taglineLabel.Font = TaglineFont;
        taglineLabel.Text = string.Empty;
        taglineLabel.TextAlign = ContentAlignment.TopCenter;
        taglineLabel.AutoEllipsis = true;
        taglineLabel.TabStop = false;
        taglineLabel.AccessibleRole = AccessibleRole.StaticText;
        taglineLabel.Visible = false;

        searchBar.Visible = true;
        searchBar.SearchKeyDown += (_, e) => SearchKeyDown?.Invoke(this, e);
        searchBar.InputChanged += (_, _) => SuggestionRequested?.Invoke(this, searchBar.Text);
        searchBar.ProviderRequested += (_, _) => ProviderRequested?.Invoke(this, EventArgs.Empty);
        searchBar.SubmitRequested += (_, _) =>
        {
            var input = searchBar.Text.Trim();
            if (input.Length > 0) NavigateRequested?.Invoke(this, input);
        };

        featureHost.Dock = DockStyle.Fill;
        featureHost.Margin = Padding.Empty;
        featureHost.TabStop = false;
        featureHost.AccessibleName = "Browser features";
        featureHost.AccessibleRole = AccessibleRole.Grouping;
        featureHost.Resize += (_, _) => ArrangeFeatureChips();

        featureChips =
        [
            new FeatureChip(StartPageAction.CycleResourceMode, "Lean browsing", "Memory saver"),
            new FeatureChip(StartPageAction.ShowTabs, "Tab workspace", "1 tab open")
        ];
        foreach (var chip in featureChips)
        {
            chip.Click += (_, _) => ActionRequested?.Invoke(this, chip.Action);
            chip.TabStop = false;
            chip.Visible = false;
        }
        featureHost.Controls.AddRange(featureChips);
        featureHost.Enabled = false;

        quickLinksTitle.Dock = DockStyle.Fill;
        quickLinksTitle.Margin = Padding.Empty;
        TrySetTransparentBackColor(quickLinksTitle);
        quickLinksTitle.ForeColor = SecondaryTextColor;
        quickLinksTitle.Font = ActionFont;
        quickLinksTitle.Text = "Quick links";
        quickLinksTitle.TextAlign = ContentAlignment.MiddleLeft;
        quickLinksTitle.TabStop = false;
        quickLinksTitle.AccessibleRole = AccessibleRole.StaticText;

        quickLinksHost.Dock = DockStyle.Fill;
        quickLinksHost.Margin = Padding.Empty;
        quickLinksHost.TabStop = false;
        quickLinksHost.AccessibleName = "Quick links";
        quickLinksHost.AccessibleRole = AccessibleRole.Grouping;
        quickLinksHost.Resize += (_, _) => ArrangeQuickLinks();

        contentLayout.Controls.Add(badge, 0, 0);
        contentLayout.Controls.Add(titleLabel, 0, 1);
        contentLayout.Controls.Add(taglineLabel, 0, 2);
        contentLayout.Controls.Add(searchBar, 0, 3);
        contentLayout.Controls.Add(featureHost, 0, 5);
        contentLayout.Controls.Add(quickLinksTitle, 0, 6);
        contentLayout.Controls.Add(quickLinksHost, 0, 7);
        card.Controls.Add(contentLayout);
        Controls.Add(card);

        ApplyDpiMetrics();
        ApplySystemColorPalette();
        arranging = false;
        PerformLayout();
    }

    public event EventHandler<string>? NavigateRequested;
    public event EventHandler<StartPageAction>? ActionRequested;
    public event EventHandler<string>? SuggestionRequested;
    public event EventHandler<KeyEventArgs>? SearchKeyDown;
    public event EventHandler? ProviderRequested;
    public event EventHandler<StartPageLinkContextEventArgs>? QuickLinkContextRequested;
    public SmartSearchBar SearchBar => searchBar;
    public Control SearchAnchor => searchBar.SuggestionAnchor;
    public Control ResourceModeAnchor => featureChips[0];

    public string SearchText
    {
        get => searchBar.Text;
        set => searchBar.Text = value;
    }

    public SmartSearchClassification SearchClassification => searchBar.Classification;

    public void SetSearchProvider(string providerId)
    {
        searchBar.ProviderId = providerId;
    }

    public void SetPrivateMode(bool enabled)
    {
        isPrivateModeActive = enabled;
        badge.IsPrivateMode = enabled;
        if (enabled)
        {
            titleLabel.Text = "Incognito";
            titleLabel.ForeColor = NativeUiTheme.Lavender;
            titleLabel.IsPrivateMode = true;
            taglineLabel.Text = "You're browsing in Incognito mode \u2014 history and cookies are not saved";
            taglineLabel.ForeColor = Color.FromArgb(226, 185, 248);
            taglineLabel.Font = IncognitoTaglineFont;
            taglineLabel.Visible = true;
            taglineLabel.AccessibleDescription = "Incognito browsing is isolated from MishaWeb saved items";
            badge.AccessibleName = "Incognito browsing";
            quickLinksTitle.Visible = false;
            quickLinksHost.Visible = false;
        }
        else
        {
            titleLabel.Text = "MishaWeb";
            titleLabel.ForeColor = PrimaryTextColor;
            titleLabel.IsPrivateMode = false;
            taglineLabel.Text = string.Empty;
            taglineLabel.ForeColor = SecondaryTextColor;
            taglineLabel.Font = TaglineFont;
            taglineLabel.Visible = false;
            taglineLabel.AccessibleDescription = string.Empty;
            badge.AccessibleName = "MishaWeb logo";
            quickLinksTitle.Visible = true;
            quickLinksHost.Visible = currentQuickLinks.Length > 0;
        }
        AccessibleDescription = enabled
            ? "Incognito MishaWeb start page. Search or open a site; normal saved items are not available."
            : "A lightweight page for starting a search or opening a quick link";
        if (!arranging) PerformLayout();
        Invalidate(true);
    }

    public void SetStatus(StartPageStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status == currentStatus) return;

        currentStatus = status;
        featureChips[0].SetText(status.ResourceMode);
        featureChips[1].SetText(status.Tabs);
    }

    public void SetQuickLinks(IEnumerable<StartPageLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        var normalizedLinks = new List<StartPageLink>(MaximumQuickLinks);
        foreach (var link in links)
        {
            if (normalizedLinks.Count >= MaximumQuickLinks) break;
            if (link is null
                || string.IsNullOrWhiteSpace(link.Title)
                || string.IsNullOrWhiteSpace(link.Url))
            {
                continue;
            }

            normalizedLinks.Add(new StartPageLink(
                link.Title.Trim(),
                link.Url.Trim(),
                string.IsNullOrWhiteSpace(link.Category) ? "Recent" : link.Category.Trim()));
        }

        var nextLinks = normalizedLinks.ToArray();
        if (QuickLinksMatch(currentQuickLinks, nextLinks)) return;

        quickLinksHost.SuspendLayout();
        try
        {
            while (quickLinksHost.Controls.Count > 0)
            {
                var control = quickLinksHost.Controls[0];
                quickLinksHost.Controls.RemoveAt(0);
                control.Dispose();
            }

            for (var index = 0; index < nextLinks.Length; index++)
            {
                quickLinksHost.Controls.Add(CreateQuickLinkButton(nextLinks[index], index));
            }

            currentQuickLinks = nextLinks;
        }
        finally
        {
            quickLinksHost.ResumeLayout(false);
        }

        PerformLayout();
        ArrangeQuickLinks();
        Invalidate();
    }

    private static bool QuickLinksMatch(
        IReadOnlyList<StartPageLink> current,
        IReadOnlyList<StartPageLink> next)
    {
        if (current.Count != next.Count) return false;
        for (var index = 0; index < current.Count; index++)
        {
            if (current[index] != next[index]) return false;
        }

        return true;
    }

    public void FocusSearch()
    {
        searchBar.FocusInput();
    }

    internal bool ActivateQuickLinkForTesting(int index)
    {
        if (index < 0 || index >= quickLinksHost.Controls.Count
            || quickLinksHost.Controls[index] is not QuickLinkButton button)
        {
            return false;
        }

        button.PerformClickForTesting();
        return true;
    }

    internal bool ActivateActionForTesting(StartPageAction action)
    {
        var chip = featureChips.FirstOrDefault(item => item.Action == action);
        if (chip is null) return false;
        chip.PerformClickForTesting();
        return true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateBackdropFrameLease();
        ApplyDpiMetrics();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateBackdropFrameLease();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Control.Dispose can raise VisibleChanged before IsDisposed flips to true.
            // Guard that re-entrant path so it cannot acquire a fresh cache lease.
            disposingBackdropFrameLease = true;
            if (backdropFrameLeaseHeld)
            {
                backdropFrameLeaseHeld = false;
                StartPageArtwork.ReleaseBackdropFrame();
            }
        }
        base.Dispose(disposing);
    }

    private void UpdateBackdropFrameLease()
    {
        var shouldHoldLease = !disposingBackdropFrameLease
            && IsHandleCreated
            && Visible
            && !IsDisposed;
        if (shouldHoldLease == backdropFrameLeaseHeld) return;

        backdropFrameLeaseHeld = shouldHoldLease;
        if (shouldHoldLease) StartPageArtwork.AcquireBackdropFrame();
        else StartPageArtwork.ReleaseBackdropFrame();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        StartPageArtwork.InvalidateBackdropFrame();
        ApplyDpiMetrics();
        PerformLayout();
        Invalidate(true);
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        StartPageArtwork.InvalidateBackdropFrame();
        ApplySystemColorPalette();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (arranging) return;

        arranging = true;
        try
        {
            var outerMargin = ScaleLogical(18);
            var availableWidth = Math.Max(1, ClientSize.Width - (outerMargin * 2));
            var availableHeight = Math.Max(1, ClientSize.Height - (outerMargin * 2));
            var hasQuickLinks = quickLinksHost.Controls.Count > 0;
            var cardWidth = Math.Min(ScaleLogical(680), availableWidth);
            compactLayout = availableHeight < ScaleLogical(hasQuickLinks ? 545 : 365)
                || cardWidth < ScaleLogical(620);

            ConfigureContentRows(compactLayout, hasQuickLinks, cardWidth);

            var desiredHeight = CalculateCardHeight();
            var cardHeight = Math.Min(desiredHeight, availableHeight);
            card.Bounds = new Rectangle(
                Math.Max(0, (ClientSize.Width - cardWidth) / 2),
                Math.Max(0, (ClientSize.Height - cardHeight) / 2),
                cardWidth,
                cardHeight);
            var needsScroll = desiredHeight > card.ClientSize.Height;
            var contentWidth = Math.Max(
                1,
                card.ClientSize.Width - (needsScroll ? SystemInformation.VerticalScrollBarWidth : 0));
            card.AutoScrollMinSize = new Size(contentWidth, desiredHeight);
            contentLayout.Bounds = new Rectangle(0, 0, contentWidth, desiredHeight);
            card.PerformLayout();
            ArrangeFeatureChips();
            ArrangeQuickLinks();
        }
        finally
        {
            arranging = false;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        var clip = Rectangle.Intersect(e.ClipRectangle, ClientRectangle);
        if (clip.IsEmpty) return;

        StartPageArtwork.DrawBackdropSlice(e.Graphics, ClientSize, clip, clip);
    }

    internal static bool PaintChildBackdropSlice(Control control, PaintEventArgs e)
    {
        return PaintBackdropSlice(control, e);
    }

    private static bool PaintBackdropSlice(Control control, PaintEventArgs e)
    {
        var offset = Point.Empty;
        Control? current = control;
        while (current is not null && current is not NativeStartPage)
        {
            offset.Offset(current.Left, current.Top);
            current = current.Parent;
        }

        if (current is not NativeStartPage startPage) return false;

        var rootSlice = new Rectangle(
            offset.X + e.ClipRectangle.X,
            offset.Y + e.ClipRectangle.Y,
            e.ClipRectangle.Width,
            e.ClipRectangle.Height);
        var clippedRootSlice = Rectangle.Intersect(startPage.ClientRectangle, rootSlice);
        if (clippedRootSlice.IsEmpty) return true;

        var localDestination = new Rectangle(
            e.ClipRectangle.X + clippedRootSlice.X - rootSlice.X,
            e.ClipRectangle.Y + clippedRootSlice.Y - rootSlice.Y,
            clippedRootSlice.Width,
            clippedRootSlice.Height);
        StartPageArtwork.DrawBackdropSlice(
            e.Graphics,
            startPage.ClientSize,
            clippedRootSlice,
            localDestination);
        return true;
    }

    private QuickLinkButton CreateQuickLinkButton(StartPageLink link, int index)
    {
        var button = new QuickLinkButton(link)
        {
            Tag = link,
            Cursor = Cursors.Hand,
            TabIndex = index + 2,
            AccessibleName = $"Open {link.Title}",
            AccessibleDescription = FormatQuickLinkAccessibleDescription(link),
            CornerRadius = ScaleLogical(14)
        };
        button.Click += QuickLinkClicked;
        button.ContextRequested += (_, args) => QuickLinkContextRequested?.Invoke(this, args);
        return button;
    }

    internal static string FormatQuickLinkAccessibleDescription(StartPageLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var category = TextSafety.SanitizeSingleLine(link.Category, 40);
        if (category.Length == 0) category = "Quick link";
        var displayUrl = TextSafety.FormatUrlForDisplay(
            link.Url,
            MaximumQuickLinkDisplayUrlCharacters);
        return TextSafety.TruncateWithEllipsis(
            $"{category}. Navigate to {displayUrl}",
            MaximumQuickLinkAccessibleDescriptionCharacters);
    }

    internal static string FormatQuickLinkHostForDisplay(string? url) =>
        TextSafety.FormatHostForDisplay(url, MaximumQuickLinkDisplayHostCharacters);

    private void QuickLinkClicked(object? sender, EventArgs e)
    {
        if (sender is QuickLinkButton { Tag: StartPageLink link })
        {
            NavigateRequested?.Invoke(this, link.Url);
        }
    }

    private void ApplyDpiMetrics()
    {
        contentLayout.Padding = new Padding(
            ScaleLogical(10),
            ScaleLogical(2),
            ScaleLogical(10),
            ScaleLogical(2));

        card.CornerRadius = ScaleLogical(24);
        var badgeSide = ScaleLogical(compactLayout ? 52 : 60);
        badge.Size = new Size(badgeSide, badgeSide);
        searchBar.Margin = Padding.Empty;
        searchBar.MinimumSize = new Size(ScaleLogical(260), ScaleLogical(48));

        foreach (var chip in featureChips)
        {
            chip.CornerRadius = ScaleLogical(14);
        }

        foreach (Control control in quickLinksHost.Controls)
        {
            if (control is QuickLinkButton button) button.CornerRadius = ScaleLogical(14);
        }

        PerformLayout();
    }

    private void ApplySystemColorPalette()
    {
        var highContrast = SystemInformation.HighContrast;
        BackColor = highContrast ? SystemColors.Window : PageColor;
        ForeColor = highContrast ? SystemColors.WindowText : PrimaryTextColor;
        titleLabel.ForeColor = highContrast
            ? SystemColors.WindowText
            : isPrivateModeActive ? NativeUiTheme.Lavender : PrimaryTextColor;
        taglineLabel.ForeColor = highContrast
            ? SystemColors.WindowText
            : isPrivateModeActive ? Color.FromArgb(226, 185, 248) : SecondaryTextColor;
        quickLinksTitle.ForeColor = highContrast ? SystemColors.WindowText : FocusColor;
        card.Invalidate(true);
        foreach (var chip in featureChips) chip.Invalidate();
        quickLinksHost.Invalidate(true);
        Invalidate(true);
    }

    private void ConfigureContentRows(bool compact, bool hasQuickLinks, int cardWidth)
    {
        contentLayout.Padding = new Padding(
            ScaleLogical(compact ? 7 : 10),
            ScaleLogical(2),
            ScaleLogical(compact ? 7 : 10),
            ScaleLogical(2));

        var badgeSide = ScaleLogical(compact ? 52 : 60);
        if (badge.Size.Width != badgeSide || badge.Size.Height != badgeSide)
        {
            badge.Size = new Size(badgeSide, badgeSide);
        }

        SetRowHeight(0, compact ? 58 : 68);
        SetRowHeight(1, isPrivateModeActive ? (compact ? 48 : 58) : (compact ? 44 : 52));
        SetRowHeight(2, isPrivateModeActive ? (compact ? 28 : 34) : (compact ? 6 : 10));
        SetRowHeight(3, compact ? 58 : 66);
        SetRowHeight(4, compact ? 8 : 12);
        var contentWidth = Math.Max(1, cardWidth - contentLayout.Padding.Horizontal);
        contentLayout.RowStyles[5].Height = 0;

        if (isPrivateModeActive)
        {
            SetRowHeight(6, 0);
            contentLayout.RowStyles[7].Height = 0;
            quickLinksTitle.Visible = false;
            quickLinksHost.Visible = false;
        }
        else
        {
            SetRowHeight(6, compact ? 26 : 32);
            if (hasQuickLinks)
            {
                var columns = DetermineQuickLinkColumns(quickLinksHost.Controls.Count, contentWidth);
                var rows = Math.Max(1, (int)Math.Ceiling(quickLinksHost.Controls.Count / (double)columns));
                var quickLinkHeight = ScaleLogical(compact ? 56 : 62);
                var gap = ScaleLogical(compact ? 6 : 8);
                contentLayout.RowStyles[7].Height = (rows * quickLinkHeight) + ((rows - 1) * gap);
            }
            else
            {
                contentLayout.RowStyles[7].Height = 0;
            }

            quickLinksTitle.Text = hasQuickLinks
                ? "Quick links"
                : "Quick links \u00B7 Add favorites to see them here";
            quickLinksTitle.AccessibleName = quickLinksTitle.Text;
            quickLinksTitle.Visible = true;
            quickLinksHost.Visible = hasQuickLinks;
        }

        featureHost.Visible = false;
    }

    private void SetRowHeight(int index, int logicalHeight)
    {
        contentLayout.RowStyles[index].Height = logicalHeight == 0 ? 0 : ScaleLogical(logicalHeight);
    }

    private int CalculateCardHeight()
    {
        var rowsHeight = 0f;
        foreach (RowStyle style in contentLayout.RowStyles)
        {
            rowsHeight += style.Height;
        }
        return Math.Max(
            ScaleLogical(180),
            (int)Math.Ceiling(rowsHeight) + contentLayout.Padding.Vertical);
    }

    private void ArrangeFeatureChips()
    {
        if (featureHost.ClientSize.Width <= 0 || featureHost.ClientSize.Height <= 0) return;

        var gap = ScaleLogical(compactLayout ? 8 : 12);
        var columns = DetermineFeatureColumns(featureHost.ClientSize.Width);
        var rows = (int)Math.Ceiling(featureChips.Length / (double)columns);
        var width = Math.Max(1, (featureHost.ClientSize.Width - (gap * (columns - 1))) / columns);
        var height = Math.Min(
            ScaleLogical(compactLayout ? 58 : 70),
            Math.Max(1, (featureHost.ClientSize.Height - (gap * (rows - 1))) / rows));
        for (var index = 0; index < featureChips.Length; index++)
        {
            var column = index % columns;
            var row = index / columns;
            featureChips[index].Bounds = new Rectangle(
                column * (width + gap),
                row * (height + gap),
                width,
                height);
        }
    }

    private int DetermineFeatureColumns(int availableWidth)
    {
        if (availableWidth < ScaleLogical(430)) return 1;
        if (availableWidth < ScaleLogical(660)) return 2;
        return featureChips.Length;
    }

    private void ArrangeQuickLinks()
    {
        var count = quickLinksHost.Controls.Count;
        if (count == 0 || quickLinksHost.ClientSize.Width <= 0 || quickLinksHost.ClientSize.Height <= 0) return;

        var gap = ScaleLogical(compactLayout ? 7 : 9);
        var columns = DetermineQuickLinkColumns(count, quickLinksHost.ClientSize.Width);
        var width = Math.Max(1, (quickLinksHost.ClientSize.Width - (gap * (columns - 1))) / columns);
        var rows = (int)Math.Ceiling(count / (double)columns);
        var height = Math.Min(
            ScaleLogical(compactLayout ? 58 : 68),
            Math.Max(1, (quickLinksHost.ClientSize.Height - (gap * (rows - 1))) / rows));

        for (var index = 0; index < count; index++)
        {
            var column = index % columns;
            var row = index / columns;
            quickLinksHost.Controls[index].Bounds = new Rectangle(
                column * (width + gap),
                row * (height + gap),
                width,
                height);
        }
    }

    private int DetermineQuickLinkColumns(int count, int availableWidth)
    {
        if (count <= 1) return Math.Max(1, count);
        if (availableWidth < ScaleLogical(390)) return 1;
        if (availableWidth < ScaleLogical(600)) return Math.Min(2, count);
        return count <= 4 ? count : Math.Min(3, count);
    }

    private int ScaleLogical(int logicalValue)
    {
        return Math.Max(1, (logicalValue * DeviceDpi + 48) / 96);
    }

    private static void TrySetTransparentBackColor(Control control)
    {
        try
        {
            control.BackColor = Color.Transparent;
        }
        catch (ArgumentException)
        {
            // Some controls do not allow transparent backgrounds without explicit styles.
            // Fall back to default inherited behavior.
        }
    }

    private static Color ShiftColor(Color color, int amount)
    {
        return Color.FromArgb(
            color.A,
            Math.Clamp(color.R + amount, 0, 255),
            Math.Clamp(color.G + amount, 0, 255),
            Math.Clamp(color.B + amount, 0, 255));
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

    private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 1 || bounds.Height <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var safeRadius = Math.Max(1f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
        var diameter = safeRadius * 2f;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void PaintLiquidGlassCard(
        Graphics graphics,
        RectangleF bounds,
        float cornerRadius,
        bool hovered,
        bool pressed,
        bool focused)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        using var path = CreateRoundedPath(bounds, cornerRadius);

        if (SystemInformation.HighContrast)
        {
            var highContrastSelection = pressed || hovered || focused;
            using var hcFill = new SolidBrush(highContrastSelection ? SystemColors.Highlight : SystemColors.Window);
            using var hcBorder = new Pen(SystemColors.WindowText, focused ? 2f : 1f);
            graphics.FillPath(hcFill, path);
            graphics.DrawPath(hcBorder, path);
            return;
        }

        var topAlpha = pressed ? 196 : hovered ? 166 : 132;
        var bottomAlpha = pressed ? 210 : hovered ? 178 : 148;
        var topTint = Color.FromArgb(topAlpha, hovered ? 92 : 76, hovered ? 38 : 31, hovered ? 82 : 70);
        var bottomTint = Color.FromArgb(bottomAlpha, 42, 16, 44);

        var state = graphics.Save();
        try
        {
            graphics.SetClip(path, CombineMode.Intersect);
            using var fill = new LinearGradientBrush(bounds, topTint, bottomTint, LinearGradientMode.ForwardDiagonal);
            graphics.FillPath(fill, path);

            var specHeight = Math.Max(8f, bounds.Height * 0.48f);
            var specRect = new RectangleF(
                bounds.X - (bounds.Width * 0.12f),
                bounds.Y - (specHeight * 0.24f),
                bounds.Width * 1.24f,
                specHeight * 1.18f);
            using var specPath = new GraphicsPath();
            specPath.AddEllipse(specRect);
            using var specBrush = new LinearGradientBrush(
                new RectangleF(bounds.X, bounds.Y, bounds.Width, specHeight + 1f),
                Color.FromArgb(hovered ? 78 : 54, 255, 255, 255),
                Color.FromArgb(0, 255, 232, 246),
                LinearGradientMode.Vertical);
            graphics.FillPath(specBrush, specPath);

            var causticHeight = Math.Max(6f, bounds.Height * 0.30f);
            var causticRect = new RectangleF(bounds.X, bounds.Bottom - causticHeight, bounds.Width, causticHeight + 1f);
            using var causticBrush = new LinearGradientBrush(
                causticRect,
                Color.FromArgb(0, AccentColor),
                Color.FromArgb(hovered ? 66 : 42, FocusColor),
                LinearGradientMode.Vertical);
            graphics.FillRectangle(causticBrush, causticRect);
        }
        finally
        {
            graphics.Restore(state);
        }

        using var outerRimBrush = new LinearGradientBrush(
            bounds,
            Color.FromArgb(focused ? 235 : hovered ? 218 : 178, 255, 230, 246),
            Color.FromArgb(focused ? 195 : hovered ? 148 : 92, AccentColor),
            LinearGradientMode.Vertical);
        using var outerRimPen = new Pen(outerRimBrush, focused || hovered ? 1.45f : 1.1f);
        graphics.DrawPath(outerRimPen, path);

        var innerBounds = bounds;
        innerBounds.Inflate(-1.1f, -1.1f);
        if (innerBounds.Width > 6f && innerBounds.Height > 6f)
        {
            using var innerPath = CreateRoundedPath(innerBounds, Math.Max(2f, cornerRadius - 1.1f));
            using var innerRimBrush = new LinearGradientBrush(
                innerBounds,
                Color.FromArgb(hovered ? 105 : 76, 255, 255, 255),
                Color.FromArgb(hovered ? 42 : 20, FocusColor),
                LinearGradientMode.Vertical);
            using var innerRimPen = new Pen(innerRimBrush, 1f);
            graphics.DrawPath(innerRimPen, innerPath);
        }
    }

    private sealed class RoundedSurfacePanel : Panel
    {
        private bool isFocused;
        private int cornerRadius = 16;
        private Size lastRegionSize;
        private int lastRegionRadius;

        public RoundedSurfacePanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
        }

        public Color FillColor { get; set; } = SurfaceColor;
        public Color FillColorSecondary { get; set; } = SurfaceColorSecondary;
        public Color BorderColor { get; set; } = SurfaceBorderColor;
        public Color FocusBorderColor { get; set; } = FocusColor;
        public Color HighlightColor { get; set; } = Color.FromArgb(82, NativeUiTheme.Focus);
        public int CornerRadius
        {
            get => cornerRadius;
            set
            {
                var next = Math.Max(1, value);
                if (cornerRadius == next) return;
                cornerRadius = next;
                UpdateClipRegion();
                Invalidate();
            }
        }

        public bool IsFocused
        {
            get => isFocused;
            set
            {
                if (isFocused == value) return;
                isFocused = value;
                Invalidate();
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            UpdateClipRegion();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var effectiveBorderColor = IsFocused ? FocusBorderColor : BorderColor;
            if (FillColor.A == 0
                && FillColorSecondary.A == 0
                && effectiveBorderColor.A == 0
                && HighlightColor.A == 0)
            {
                if (!PaintBackdropSlice(this, e)) base.OnPaintBackground(e);
                return;
            }

            base.OnPaintBackground(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = CreateRoundedPath(
                new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f)),
                CornerRadius);
            if (SystemInformation.HighContrast)
            {
                using var systemFill = new SolidBrush(SystemColors.Window);
                using var systemBorder = new Pen(IsFocused ? SystemColors.Highlight : SystemColors.WindowText, IsFocused ? 2f : 1f);
                e.Graphics.FillPath(systemFill, path);
                e.Graphics.DrawPath(systemBorder, path);
                return;
            }
            using var fill = new LinearGradientBrush(
                ClientRectangle,
                FillColor,
                FillColorSecondary,
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(effectiveBorderColor, IsFocused ? 1.6f : 1f);
            using var highlight = new Pen(HighlightColor, 1f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
            e.Graphics.DrawPath(highlight, path);
            if (Width >= Math.Max(1, (560 * DeviceDpi + 48) / 96))
            {
                StartPageArtwork.DrawBunnyPeek(e.Graphics, ClientRectangle);
            }
        }

        private void UpdateClipRegion()
        {
            if (Width <= 1 || Height <= 1) return;

            var currentSize = new Size(Width, Height);
            if (lastRegionSize == currentSize && lastRegionRadius == CornerRadius && Region is not null)
            {
                return;
            }

            lastRegionSize = currentSize;
            lastRegionRadius = CornerRadius;
            using var path = CreateRoundedPath(new RectangleF(0, 0, Width, Height), CornerRadius);
            var previousRegion = Region;
            Region = new Region(path);
            previousRegion?.Dispose();
        }
    }

    private sealed class TransparentTableLayoutPanel : TableLayoutPanel
    {
        public TransparentTableLayoutPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor,
                true);
            NativeStartPage.TrySetTransparentBackColor(this);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!PaintBackdropSlice(this, e)) base.OnPaintBackground(e);
        }
    }

    private sealed class TransparentPanel : Panel
    {
        public TransparentPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor,
                true);
            NativeStartPage.TrySetTransparentBackColor(this);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!PaintBackdropSlice(this, e)) base.OnPaintBackground(e);
        }
    }

    private sealed class RoundedButton : Button
    {
        private bool hovered;
        private bool pressed;

        public RoundedButton()
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

        public Color FillColor { get; set; } = AccentColor;
        public Color FillColorSecondary { get; set; } = AccentColorSecondary;
        public Color HoverColor { get; set; } = AccentHoverColor;
        public Color PressedColor { get; set; } = AccentPressedColor;
        public Color BorderColor { get; set; } = AccentColor;
        public Color FocusColor { get; set; } = NativeStartPage.FocusColor;
        public int CornerRadius { get; set; } = 10;

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
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            pressed = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var background = !Enabled
                ? ControlPaint.Dark(FillColor, 0.15f)
                : pressed ? PressedColor : hovered ? HoverColor : FillColor;
            using var path = CreateRoundedPath(
                new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f)),
                CornerRadius);
            var secondary = !Enabled || pressed || hovered ? background : FillColorSecondary;
            using var fill = new LinearGradientBrush(
                ClientRectangle,
                background,
                secondary,
                LinearGradientMode.Horizontal);
            using var border = new Pen(BorderColor);
            pevent.Graphics.FillPath(fill, path);
            pevent.Graphics.DrawPath(border, path);

            var textColor = Enabled ? ForeColor : ControlPaint.Dark(ForeColor);
            TextRenderer.DrawText(
                pevent.Graphics,
                Text,
                Font,
                Rectangle.Inflate(ClientRectangle, -8, -2),
                textColor,
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine);

            if (Focused && ShowFocusCues)
            {
                var focusBounds = Rectangle.Inflate(ClientRectangle, -5, -5);
                ControlPaint.DrawFocusRectangle(pevent.Graphics, focusBounds, FocusColor, background);
            }
        }
    }

    private sealed class QuickLinkButton : Control
    {
        private readonly StartPageLink link;
        private bool hovered;
        private bool pressed;

        public QuickLinkButton(StartPageLink link)
        {
            this.link = link;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
            NativeStartPage.TrySetTransparentBackColor(this);
            ForeColor = PrimaryTextColor;
            TabStop = true;
            AccessibleRole = AccessibleRole.Link;
        }

        public int CornerRadius { get; set; } = 11;
        public event EventHandler<StartPageLinkContextEventArgs>? ContextRequested;

        public void PerformClickForTesting()
        {
            OnClick(EventArgs.Empty);
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
            Focus();
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            if (e.Button == MouseButtons.Right)
            {
                ContextRequested?.Invoke(
                    this,
                    new StartPageLinkContextEventArgs(
                        link,
                        PointToScreen(e.Location)));
            }
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

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.F10 && e.Shift)
            {
                ContextRequested?.Invoke(
                    this,
                    new StartPageLinkContextEventArgs(
                        link,
                        PointToScreen(new Point(Width / 2, Height / 2))));
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!PaintBackdropSlice(this, e)) base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var highContrastSelection = SystemInformation.HighContrast && (pressed || hovered || Focused);
            var cardBounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
            PaintLiquidGlassCard(e.Graphics, cardBounds, CornerRadius, hovered, pressed, Focused);

            var scale = Math.Max(1f, DeviceDpi / 96f);
            var dense = Height < ScalePixels(64);
            var badgeSize = Math.Max(28, (int)Math.Round((dense ? 31 : 37) * scale));
            var badgeLeft = Math.Max(8, (int)Math.Round((dense ? 9 : 12) * scale));
            var badgeTop = Math.Max(1, (Height - badgeSize) / 2);
            var badgeBounds = new Rectangle(badgeLeft, badgeTop, badgeSize, badgeSize);
            if (!SystemInformation.HighContrast)
            {
                var haloBounds = RectangleF.Inflate(badgeBounds, 3.5f * scale, 3.5f * scale);
                using var halo = new SolidBrush(Color.FromArgb(hovered ? 48 : 24, AccentColor));
                e.Graphics.FillEllipse(halo, haloBounds);
            }
            var iconKind = GetSiteIconKind(link.Url);
            DrawSiteIcon(
                e.Graphics,
                badgeBounds,
                iconKind,
                GetMonogram(link.Title, link.Url),
                scale);

            var textLeft = badgeBounds.Right + Math.Max(7, (int)Math.Round((dense ? 8 : 10) * scale));
            var showChevron = Width >= ScalePixels(185);
            var textRightPadding = Math.Max(8, (int)Math.Round((showChevron ? 34 : 10) * scale));
            var textWidth = Math.Max(1, Width - textLeft - textRightPadding);
            var titleBounds = new Rectangle(textLeft, Math.Max(2, Height / 2 - ScalePixels(18)), textWidth, ScalePixels(18));
            var detailBounds = new Rectangle(textLeft, Height / 2 + ScalePixels(1), textWidth, ScalePixels(17));
            TextRenderer.DrawText(
                e.Graphics,
                link.Title,
                LinkTitleFont,
                titleBounds,
                SystemInformation.HighContrast
                    ? highContrastSelection ? SystemColors.HighlightText : SystemColors.WindowText
                    : PrimaryTextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(
                e.Graphics,
                $"{link.Category} \u00B7 {GetDisplayHost(link.Url)}",
                LinkDetailFont,
                detailBounds,
                SystemInformation.HighContrast
                    ? highContrastSelection ? SystemColors.HighlightText : SystemColors.WindowText
                    : SecondaryTextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (showChevron)
            {
                var centerX = Width - Math.Max(14f, 17f * scale);
                var centerY = Height / 2f;
                if (!SystemInformation.HighContrast && (hovered || Focused))
                {
                    var pillSize = 18f * scale;
                    var pillBounds = new RectangleF(
                        centerX - (pillSize / 2f),
                        centerY - (pillSize / 2f),
                        pillSize,
                        pillSize);
                    using var pillBrush = new SolidBrush(Color.FromArgb(44, AccentColor));
                    e.Graphics.FillEllipse(pillBrush, pillBounds);
                }
                using var arrowPen = new Pen(
                    SystemInformation.HighContrast
                        ? highContrastSelection ? SystemColors.HighlightText : SystemColors.WindowText
                        : Color.FromArgb(hovered ? 255 : 225, NativeUiTheme.Focus),
                    Math.Max(1.2f, 1.4f * scale))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };
                e.Graphics.DrawLines(
                    arrowPen,
                    [
                        new PointF(centerX - (2f * scale), centerY - (3.8f * scale)),
                        new PointF(centerX + (2f * scale), centerY),
                        new PointF(centerX - (2f * scale), centerY + (3.8f * scale))
                    ]);
            }
        }

        private int ScalePixels(int value)
        {
            return Math.Max(1, (value * DeviceDpi + 48) / 96);
        }

        private enum SiteIconKind
        {
            Generic,
            YouTube,
            Messenger,
            Facebook,
            Google,
            Instagram,
            Discord,
            Spotify,
            Twitch,
            X,
            Anthropic
        }

        private static void DrawSiteIcon(
            Graphics graphics,
            Rectangle bounds,
            SiteIconKind kind,
            string monogram,
            float scale)
        {
            if (SystemInformation.HighContrast)
            {
                using var highContrastPath = CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
                using var highContrastFill = new SolidBrush(SystemColors.Highlight);
                using var highContrastBorder = new Pen(SystemColors.WindowText, Math.Max(1f, scale));
                graphics.FillPath(highContrastFill, highContrastPath);
                graphics.DrawPath(highContrastBorder, highContrastPath);
                TextRenderer.DrawText(
                    graphics,
                    monogram,
                    LinkMonogramFont,
                    bounds,
                    SystemColors.HighlightText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }

            switch (kind)
            {
                case SiteIconKind.YouTube:
                    DrawYouTubeIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.Messenger:
                    DrawMessengerIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.Facebook:
                    DrawFacebookIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.Google:
                    DrawGoogleIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.Instagram:
                    DrawInstagramIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.Discord:
                    DrawDiscordIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.Spotify:
                    DrawSpotifyIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.Twitch:
                    DrawTwitchIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.X:
                    DrawXIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.Anthropic:
                    DrawAnthropicIcon(graphics, bounds, scale);
                    return;
            }

            using var path = CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
            using var fill = new LinearGradientBrush(
                bounds,
                SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(235, NativeUiTheme.Accent),
                SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(240, NativeUiTheme.Lavender),
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(
                SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(180, NativeUiTheme.Focus),
                Math.Max(1f, scale));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
            TextRenderer.DrawText(
                graphics,
                monogram,
                LinkMonogramFont,
                bounds,
                SystemInformation.HighContrast ? SystemColors.HighlightText : NativeUiTheme.AccentText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private static void DrawYouTubeIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var path = CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
            using var fill = new LinearGradientBrush(
                bounds,
                Color.FromArgb(249, 78, 97),
                Color.FromArgb(192, 30, 66),
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(Color.FromArgb(160, 255, 188, 196), Math.Max(1f, scale));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);

            var centerX = bounds.Left + (bounds.Width / 2f);
            var centerY = bounds.Top + (bounds.Height / 2f);
            var playWidth = bounds.Width * 0.34f;
            var playHeight = bounds.Height * 0.42f;
            using var play = new SolidBrush(Color.White);
            graphics.FillPolygon(
                play,
                [
                    new PointF(centerX - (playWidth * 0.34f), centerY - (playHeight / 2f)),
                    new PointF(centerX + (playWidth * 0.66f), centerY),
                    new PointF(centerX - (playWidth * 0.34f), centerY + (playHeight / 2f))
                ]);
        }

        private static void DrawMessengerIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var fill = new LinearGradientBrush(
                bounds,
                Color.FromArgb(65, 202, 255),
                Color.FromArgb(120, 68, 224),
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(Color.FromArgb(180, 190, 226, 255), Math.Max(1f, scale));
            graphics.FillEllipse(fill, bounds);
            graphics.DrawEllipse(border, bounds);

            var left = bounds.Left + (bounds.Width * 0.26f);
            var right = bounds.Right - (bounds.Width * 0.23f);
            var top = bounds.Top + (bounds.Height * 0.2f);
            var bottom = bounds.Bottom - (bounds.Height * 0.18f);
            using var lightning = new SolidBrush(Color.White);
            graphics.FillPolygon(
                lightning,
                [
                    new PointF(left + (bounds.Width * 0.2f), top + (bounds.Height * 0.45f)),
                    new PointF(left + (bounds.Width * 0.52f), top),
                    new PointF(left + (bounds.Width * 0.48f), top + (bounds.Height * 0.34f)),
                    new PointF(right, top + (bounds.Height * 0.28f)),
                    new PointF(left + (bounds.Width * 0.46f), bottom),
                    new PointF(left + (bounds.Width * 0.5f), top + (bounds.Height * 0.6f)),
                    new PointF(left, top + (bounds.Height * 0.67f))
                ]);
        }

        private static void DrawFacebookIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var fill = new SolidBrush(Color.FromArgb(55, 117, 232));
            using var border = new Pen(Color.FromArgb(170, 172, 216, 255), Math.Max(1f, scale));
            graphics.FillEllipse(fill, bounds);
            graphics.DrawEllipse(border, bounds);
            TextRenderer.DrawText(
                graphics,
                "f",
                LinkSiteLetterFont,
                bounds,
                Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private static void DrawGoogleIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            var ringBounds = RectangleF.Inflate(bounds, -bounds.Width * 0.18f, -bounds.Height * 0.18f);
            using var basePen = new Pen(Color.FromArgb(242, 245, 248), Math.Max(4f, 6f * scale));
            graphics.DrawArc(basePen, ringBounds, 0, 360);
            using var blue = new Pen(Color.FromArgb(66, 133, 244), Math.Max(4f, 6f * scale));
            using var red = new Pen(Color.FromArgb(234, 67, 53), Math.Max(4f, 6f * scale));
            using var yellow = new Pen(Color.FromArgb(251, 188, 5), Math.Max(4f, 6f * scale));
            using var green = new Pen(Color.FromArgb(52, 168, 83), Math.Max(4f, 6f * scale));
            graphics.DrawArc(blue, ringBounds, -42, 128);
            graphics.DrawArc(red, ringBounds, 86, 90);
            graphics.DrawArc(yellow, ringBounds, 176, 66);
            graphics.DrawArc(green, ringBounds, 242, 76);
            using var crossbar = new Pen(Color.FromArgb(66, 133, 244), Math.Max(4f, 6f * scale));
            var centerY = bounds.Top + (bounds.Height / 2f);
            graphics.DrawLine(crossbar, bounds.Left + (bounds.Width * 0.5f), centerY, bounds.Right - (bounds.Width * 0.17f), centerY);
        }

        private static void DrawInstagramIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var path = CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
            using var fill = new LinearGradientBrush(
                bounds,
                Color.FromArgb(249, 196, 74),
                Color.FromArgb(181, 53, 160),
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(Color.FromArgb(150, 255, 222, 239), Math.Max(1f, scale));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);

            var cameraBounds = RectangleF.Inflate(bounds, -bounds.Width * 0.24f, -bounds.Height * 0.24f);
            using var camera = new Pen(Color.White, Math.Max(1.7f, 2.3f * scale));
            using var cameraPath = CreateRoundedPath(cameraBounds, 5f * scale);
            graphics.DrawPath(camera, cameraPath);
            var lens = new RectangleF(
                bounds.Left + (bounds.Width * 0.39f),
                bounds.Top + (bounds.Height * 0.39f),
                bounds.Width * 0.22f,
                bounds.Height * 0.22f);
            graphics.DrawEllipse(camera, lens);
            using var dot = new SolidBrush(Color.White);
            graphics.FillEllipse(dot, bounds.Right - (bounds.Width * 0.33f), bounds.Top + (bounds.Height * 0.28f), 3f * scale, 3f * scale);
        }

        private static void DrawDiscordIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var path = CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
            using var fill = new SolidBrush(Color.FromArgb(88, 101, 242));
            using var border = new Pen(Color.FromArgb(160, 185, 193, 255), Math.Max(1f, scale));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
            using var face = new SolidBrush(Color.White);
            var faceBounds = RectangleF.Inflate(bounds, -bounds.Width * 0.24f, -bounds.Height * 0.3f);
            graphics.FillEllipse(face, faceBounds);
            using var eyes = new SolidBrush(Color.FromArgb(88, 101, 242));
            graphics.FillEllipse(eyes, faceBounds.Left + (faceBounds.Width * 0.24f), faceBounds.Top + (faceBounds.Height * 0.4f), 3f * scale, 4f * scale);
            graphics.FillEllipse(eyes, faceBounds.Right - (faceBounds.Width * 0.33f), faceBounds.Top + (faceBounds.Height * 0.4f), 3f * scale, 4f * scale);
        }

        private static void DrawSpotifyIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var fill = new SolidBrush(Color.FromArgb(30, 215, 96));
            using var border = new Pen(Color.FromArgb(165, 191, 255, 210), Math.Max(1f, scale));
            graphics.FillEllipse(fill, bounds);
            graphics.DrawEllipse(border, bounds);
            using var wave = new Pen(Color.FromArgb(20, 43, 31), Math.Max(1.7f, 2.2f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            var left = bounds.Left + (bounds.Width * 0.23f);
            var right = bounds.Right - (bounds.Width * 0.2f);
            var center = bounds.Top + (bounds.Height / 2f);
            graphics.DrawArc(wave, new RectangleF(left, center - (bounds.Height * 0.2f), right - left, bounds.Height * 0.24f), 198, 144);
            graphics.DrawArc(wave, new RectangleF(left + (bounds.Width * 0.05f), center - (bounds.Height * 0.02f), right - left - (bounds.Width * 0.1f), bounds.Height * 0.2f), 198, 144);
            graphics.DrawArc(wave, new RectangleF(left + (bounds.Width * 0.1f), center + (bounds.Height * 0.14f), right - left - (bounds.Width * 0.2f), bounds.Height * 0.16f), 198, 144);
        }

        private static void DrawTwitchIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var path = CreateRoundedPath(bounds, Math.Max(8f, 10f * scale));
            using var fill = new SolidBrush(Color.FromArgb(145, 70, 218));
            using var border = new Pen(Color.FromArgb(165, 224, 195, 255), Math.Max(1f, scale));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
            using var chatPen = new Pen(Color.White, Math.Max(1.6f, 2f * scale));
            var chat = new RectangleF(
                bounds.Left + (bounds.Width * 0.23f),
                bounds.Top + (bounds.Height * 0.27f),
                bounds.Width * 0.54f,
                bounds.Height * 0.42f);
            graphics.DrawRectangle(chatPen, chat.X, chat.Y, chat.Width, chat.Height);
            graphics.DrawLine(chatPen, chat.Left + (chat.Width * 0.24f), chat.Bottom, chat.Left + (chat.Width * 0.08f), chat.Bottom + (bounds.Height * 0.13f));
            graphics.DrawLine(chatPen, chat.Left + (chat.Width * 0.4f), chat.Top + (chat.Height * 0.25f), chat.Left + (chat.Width * 0.4f), chat.Bottom - (chat.Height * 0.2f));
            graphics.DrawLine(chatPen, chat.Left + (chat.Width * 0.64f), chat.Top + (chat.Height * 0.25f), chat.Left + (chat.Width * 0.64f), chat.Bottom - (chat.Height * 0.2f));
        }

        private static void DrawXIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var fill = new SolidBrush(Color.FromArgb(16, 17, 20));
            using var border = new Pen(Color.FromArgb(165, 222, 226, 232), Math.Max(1f, scale));
            graphics.FillEllipse(fill, bounds);
            graphics.DrawEllipse(border, bounds);
            using var cross = new Pen(Color.White, Math.Max(2f, 2.4f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawLine(cross, bounds.Left + (bounds.Width * 0.3f), bounds.Top + (bounds.Height * 0.28f), bounds.Right - (bounds.Width * 0.27f), bounds.Bottom - (bounds.Height * 0.28f));
            graphics.DrawLine(cross, bounds.Right - (bounds.Width * 0.32f), bounds.Top + (bounds.Height * 0.28f), bounds.Left + (bounds.Width * 0.27f), bounds.Bottom - (bounds.Height * 0.28f));
        }

        private static void DrawAnthropicIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var path = CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
            using var fill = new LinearGradientBrush(
                bounds,
                Color.FromArgb(241, 174, 95),
                Color.FromArgb(205, 103, 55),
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(Color.FromArgb(165, 255, 222, 170), Math.Max(1f, scale));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
            using var mark = new Pen(Color.FromArgb(255, 61, 40, 30), Math.Max(2f, 2.5f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var centerX = bounds.Left + (bounds.Width / 2f);
            var top = bounds.Top + (bounds.Height * 0.26f);
            var bottom = bounds.Bottom - (bounds.Height * 0.24f);
            graphics.DrawLine(mark, centerX, top, bounds.Left + (bounds.Width * 0.29f), bottom);
            graphics.DrawLine(mark, centerX, top, bounds.Right - (bounds.Width * 0.29f), bottom);
            graphics.DrawLine(mark, bounds.Left + (bounds.Width * 0.39f), bounds.Top + (bounds.Height * 0.63f), bounds.Right - (bounds.Width * 0.39f), bounds.Top + (bounds.Height * 0.63f));
        }

        private static SiteIconKind GetSiteIconKind(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return SiteIconKind.Generic;
            var host = uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? uri.IdnHost[4..]
                : uri.IdnHost;
            if (IsHostOrSubdomain(host, "youtube.com") || IsHostOrSubdomain(host, "youtu.be")) return SiteIconKind.YouTube;
            if (IsHostOrSubdomain(host, "messenger.com")) return SiteIconKind.Messenger;
            if (IsHostOrSubdomain(host, "facebook.com")) return SiteIconKind.Facebook;
            if (IsHostOrSubdomain(host, "google.com")) return SiteIconKind.Google;
            if (IsHostOrSubdomain(host, "instagram.com")) return SiteIconKind.Instagram;
            if (IsHostOrSubdomain(host, "discord.com") || IsHostOrSubdomain(host, "discordapp.com")) return SiteIconKind.Discord;
            if (IsHostOrSubdomain(host, "spotify.com")) return SiteIconKind.Spotify;
            if (IsHostOrSubdomain(host, "twitch.tv")) return SiteIconKind.Twitch;
            if (IsHostOrSubdomain(host, "x.com") || IsHostOrSubdomain(host, "twitter.com")) return SiteIconKind.X;
            if (IsHostOrSubdomain(host, "anthropic.com")) return SiteIconKind.Anthropic;
            return SiteIconKind.Generic;
        }

        private static bool IsHostOrSubdomain(string host, string root)
        {
            return host.Equals(root, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetMonogram(string title, string url)
        {
            var source = string.IsNullOrWhiteSpace(title) ? GetDisplayHost(url) : title.Trim();
            foreach (var character in source)
            {
                if (char.IsLetter(character)) return char.ToUpperInvariant(character).ToString();
            }
            foreach (var character in source)
            {
                if (char.IsDigit(character)) return character.ToString();
            }
            return "\u2022";
        }

        private static string GetDisplayHost(string url)
            => FormatQuickLinkHostForDisplay(url);
    }

    private sealed class FeatureChip : Control
    {
        private readonly string heading;
        private string detail;
        private bool hovered;
        private bool pressed;

        public FeatureChip(StartPageAction action, string heading, string detail)
        {
            Action = action;
            this.heading = heading;
            this.detail = detail;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
            Text = detail;
            Font = FeatureDetailFont;
            ForeColor = SecondaryTextColor;
            NativeStartPage.TrySetTransparentBackColor(this);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleName = detail;
            AccessibleDescription = $"{heading}. {detail}";
            AccessibleRole = AccessibleRole.PushButton;
        }

        public StartPageAction Action { get; }
        public int CornerRadius { get; set; } = 10;

        public void SetText(string text)
        {
            if (string.Equals(detail, text, StringComparison.Ordinal)) return;
            detail = text;
            Text = text;
            AccessibleName = text;
            AccessibleDescription = $"{heading}. {text}";
            Invalidate();
        }

        public void PerformClickForTesting()
        {
            OnClick(EventArgs.Empty);
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
            Focus();
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

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!PaintBackdropSlice(this, e)) base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var highContrastSelection = SystemInformation.HighContrast && (pressed || hovered || Focused);
            var cardBounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
            PaintLiquidGlassCard(e.Graphics, cardBounds, CornerRadius, hovered, pressed, Focused);

            var scale = Math.Max(1f, DeviceDpi / 96f);
            var dense = Height < ScalePixels(64);
            var iconSize = Math.Max(30, (int)Math.Round((dense ? 36 : 44) * scale));
            var iconLeft = Math.Max(8, (int)Math.Round((dense ? 9 : 12) * scale));
            var iconBounds = new Rectangle(iconLeft, Math.Max(1, (Height - iconSize) / 2), iconSize, iconSize);
            StartPageArtwork.DrawFeatureIcon(
                e.Graphics,
                iconBounds,
                Action,
                hovered || Focused);

            var textLeft = iconBounds.Right + Math.Max(7, (int)Math.Round((dense ? 8 : 11) * scale));
            var textWidth = Math.Max(1, Width - textLeft - iconLeft);
            var headingBounds = new Rectangle(textLeft, Math.Max(2, Height / 2 - ScalePixels(18)), textWidth, ScalePixels(18));
            var detailBounds = new Rectangle(textLeft, Height / 2 + ScalePixels(1), textWidth, ScalePixels(17));
            TextRenderer.DrawText(
                e.Graphics,
                heading,
                FeatureHeadingFont,
                headingBounds,
                SystemInformation.HighContrast
                    ? highContrastSelection ? SystemColors.HighlightText : SystemColors.WindowText
                    : PrimaryTextColor,
                TextFormatFlags.Left
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(
                e.Graphics,
                detail,
                FeatureDetailFont,
                detailBounds,
                SystemInformation.HighContrast
                    ? highContrastSelection ? SystemColors.HighlightText : SystemColors.WindowText
                    : MutedTextColor,
                TextFormatFlags.Left
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine);
        }

        private int ScalePixels(int value)
        {
            return Math.Max(1, (value * DeviceDpi + 48) / 96);
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
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
            NativeStartPage.TrySetTransparentBackColor(this);
            TabStop = false;
            AccessibleName = "Search";
            AccessibleRole = AccessibleRole.Graphic;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = Math.Max(1f, DeviceDpi / 96f);
            var diameter = 12f * scale;
            var left = (Width - diameter - (4f * scale)) / 2f;
            var top = (Height - diameter - (2f * scale)) / 2f;
            using var pen = new Pen(MutedTextColor, Math.Max(1.4f, 1.6f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawEllipse(pen, left, top, diameter, diameter);
            e.Graphics.DrawLine(
                pen,
                left + diameter - (1f * scale),
                top + diameter - (1f * scale),
                left + diameter + (4f * scale),
                top + diameter + (4f * scale));
        }
    }

    private sealed class BrandTitleLabel : Label
    {
        public bool IsPrivateMode { get; set; }

        public BrandTitleLabel()
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
            if (!PaintBackdropSlice(this, e)) base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var text = Text ?? string.Empty;
            if (text.Length == 0) return;

            var flags = TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine;

            if (!SystemInformation.HighContrast && !IsPrivateMode && ClientSize.Width > 40 && ClientSize.Height > 16)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var scale = Math.Max(1f, DeviceDpi / 96f);
                var measured = TextRenderer.MeasureText(
                    e.Graphics,
                    text,
                    Font,
                    ClientSize,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                var glowWidth = Math.Min(ClientSize.Width, Math.Max(80f, measured.Width + (38f * scale)));
                var glowHeight = Math.Max(16f, ClientSize.Height * 0.78f);
                var glowRect = new RectangleF(
                    (ClientSize.Width - glowWidth) / 2f,
                    (ClientSize.Height - glowHeight) / 2f,
                    glowWidth,
                    glowHeight);
                using var glowPath = new GraphicsPath();
                glowPath.AddEllipse(glowRect);
                using var glowBrush = new PathGradientBrush(glowPath)
                {
                    CenterColor = Color.FromArgb(46, AccentColor),
                    SurroundColors = [Color.FromArgb(0, AccentColor)]
                };
                e.Graphics.FillPath(glowBrush, glowPath);

                var sparkleRadius = 4f * scale;
                var halfTextWidth = measured.Width / 2f;
                var centerX = ClientSize.Width / 2f;
                var centerY = ClientSize.Height / 2f;
                var leftSparkleX = centerX - halfTextWidth - (14f * scale);
                var rightSparkleX = centerX + halfTextWidth + (14f * scale);
                if (leftSparkleX > sparkleRadius + 4f && rightSparkleX < ClientSize.Width - sparkleRadius - 4f)
                {
                    DrawFourPointSparkle(e.Graphics, new PointF(leftSparkleX, centerY - (2f * scale)), sparkleRadius, Color.FromArgb(185, FocusColor));
                    DrawFourPointSparkle(e.Graphics, new PointF(rightSparkleX, centerY + (1f * scale)), sparkleRadius * 0.85f, Color.FromArgb(165, AccentColorSecondary));
                }
            }

            TextRenderer.DrawText(
                e.Graphics,
                text,
                Font,
                ClientRectangle,
                SystemInformation.HighContrast ? SystemColors.WindowText : ForeColor,
                flags);
        }

        private static void DrawFourPointSparkle(Graphics graphics, PointF center, float radius, Color color)
        {
            using var pen = new Pen(color, Math.Max(1f, radius * 0.34f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawLine(pen, center.X, center.Y - radius, center.X, center.Y + radius);
            graphics.DrawLine(pen, center.X - radius, center.Y, center.X + radius, center.Y);
        }
    }

    private sealed class SectionHeaderLabel : Label
    {
        public SectionHeaderLabel()
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
            if (!PaintBackdropSlice(this, e)) base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var text = Text ?? string.Empty;
            if (text.Length == 0) return;

            var scale = Math.Max(1f, DeviceDpi / 96f);
            var textLeft = 0;
            if (!SystemInformation.HighContrast && ClientSize.Width > 24 && ClientSize.Height > 10)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var dotSize = 6f * scale;
                var centerY = ClientSize.Height / 2f;
                var dotCenterX = (dotSize / 2f) + (2f * scale);
                var haloRect = new RectangleF(
                    dotCenterX - (5.5f * scale),
                    centerY - (5.5f * scale),
                    11f * scale,
                    11f * scale);
                using var haloBrush = new SolidBrush(Color.FromArgb(52, AccentColor));
                e.Graphics.FillEllipse(haloBrush, haloRect);
                using var dotBrush = new SolidBrush(AccentColor);
                e.Graphics.FillEllipse(
                    dotBrush,
                    dotCenterX - (dotSize / 2f),
                    centerY - (dotSize / 2f),
                    dotSize,
                    dotSize);
                textLeft = (int)Math.Ceiling(dotCenterX + (dotSize / 2f) + (7f * scale));
            }

            var textBounds = new Rectangle(
                textLeft,
                0,
                Math.Max(1, ClientSize.Width - textLeft),
                ClientSize.Height);
            TextRenderer.DrawText(
                e.Graphics,
                text,
                Font,
                textBounds,
                SystemInformation.HighContrast ? SystemColors.WindowText : ForeColor,
                TextFormatFlags.Left
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine);
        }
    }

    private sealed class BadgeControl : Control
    {
        public bool IsPrivateMode { get; set; }

        public BadgeControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
            NativeStartPage.TrySetTransparentBackColor(this);
            TabStop = false;
            AccessibleName = "MishaWeb logo";
            AccessibleRole = AccessibleRole.Graphic;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!PaintBackdropSlice(this, e)) base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!SystemInformation.HighContrast && ClientSize.Width > 8 && ClientSize.Height > 8)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var glowPath = new GraphicsPath();
                glowPath.AddEllipse(new RectangleF(0f, 0f, ClientSize.Width, ClientSize.Height));
                using var glowBrush = new PathGradientBrush(glowPath)
                {
                    CenterColor = Color.FromArgb(72, IsPrivateMode ? AccentColorSecondary : AccentColor),
                    SurroundColors = [Color.FromArgb(0, AccentColor)]
                };
                e.Graphics.FillPath(glowBrush, glowPath);
            }

            var inset = Math.Max(2, Math.Min(ClientRectangle.Width, ClientRectangle.Height) / 14);
            var markBounds = Rectangle.Inflate(ClientRectangle, -inset, -inset);
            StartPageArtwork.DrawBrandMark(e.Graphics, markBounds);
            if (!IsPrivateMode) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var badgeSize = Math.Max(10, Math.Min(markBounds.Width, markBounds.Height) / 3);
            var badgeBounds = new Rectangle(
                markBounds.Right - badgeSize - 1,
                markBounds.Bottom - badgeSize - 1,
                badgeSize,
                badgeSize);
            using var fill = new SolidBrush(NativeUiTheme.Lavender);
            using var border = new Pen(Color.FromArgb(120, 60, 148), 1f);
            e.Graphics.FillEllipse(fill, badgeBounds);
            e.Graphics.DrawEllipse(border, badgeBounds);
            TextRenderer.DrawText(
                e.Graphics,
                "I",
                Font,
                badgeBounds,
                NativeUiTheme.AccentText,
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding
                | TextFormatFlags.SingleLine);
        }
    }
}
