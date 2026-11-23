using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

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
    ShowTabs,
    ManageQuickLinks
}

internal sealed class NativeStartPage : UserControl
{
    private const int MaximumQuickLinks = 6;
    private const int MaximumQuickLinkDisplayUrlCharacters = 512;
    private const int MaximumQuickLinkAccessibleDescriptionCharacters = 568;
    private const int MaximumQuickLinkDisplayHostCharacters = 120;
    private const int WmPrint = 0x0317;
    private const int WmPrintClient = 0x0318;

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

    private static readonly Font BrandFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 27f);
    private static readonly Font TaglineFont = NativeUiTheme.GetSharedFont("Segoe UI", 11f);
    private static readonly Font IncognitoTaglineFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 10f);
    private static readonly Font ActionFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 9.5f);
    private static readonly Font FeatureHeadingFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 9.2f);
    private static readonly Font FeatureDetailFont = NativeUiTheme.GetSharedFont("Segoe UI", 8.2f);
    private static readonly Font LinkTitleFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 9.1f);
    private static readonly Font LinkDetailFont = NativeUiTheme.GetSharedFont("Segoe UI", 8f);
    private static readonly Font ManageButtonFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 8.2f);
    private static readonly Font LinkMonogramFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 10.5f);
    private static readonly Font LinkSiteLetterFont = NativeUiTheme.GetSharedFont("Segoe UI", 22f, FontStyle.Bold);

    private readonly RoundedSurfacePanel card = new();
    private readonly TransparentTableLayoutPanel contentLayout = new();
    private readonly BadgeControl badge = new();
    private readonly BrandTitleLabel titleLabel = new();
    private readonly Label taglineLabel = new();
    private readonly SmartSearchBar searchBar = new();
    private readonly TransparentPanel featureHost = new();
    private readonly FeatureChip[] featureChips;
    private readonly TransparentPanel quickLinksHeader = new();
    private readonly SectionHeaderLabel quickLinksTitle = new();
    private readonly ManageQuickLinksButton manageQuickLinksButton = new();
    private readonly TransparentPanel quickLinksHost = new();
    private readonly ToolTip quickLinkToolTip = new();
    private bool arranging = true;
    private bool backdropFrameLeaseHeld;
    private bool disposingBackdropFrameLease;
    private bool compactLayout;
    private StartPageStatus currentStatus = StartPageStatus.Default;
    private StartPageLink[] currentQuickLinks = [];
    private bool isPrivateModeActive;
    private int measuredManageWidthDpi;
    private int measuredManageTextWidth;

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
        featureHost.Resize += (_, _) => { if (!arranging) ArrangeFeatureChips(); };

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

        NativeUiTheme.ConfigureGlassToolTip(quickLinkToolTip);

        quickLinksHeader.Dock = DockStyle.Fill;
        quickLinksHeader.Margin = Padding.Empty;
        quickLinksHeader.TabStop = false;
        quickLinksHeader.AccessibleName = "Quick links header";
        quickLinksHeader.AccessibleRole = AccessibleRole.Grouping;
        quickLinksHeader.Resize += (_, _) => { if (!arranging) ArrangeQuickLinksHeader(); };

        quickLinksTitle.Dock = DockStyle.None;
        quickLinksTitle.Margin = Padding.Empty;
        TrySetTransparentBackColor(quickLinksTitle);
        quickLinksTitle.ForeColor = SecondaryTextColor;
        quickLinksTitle.Font = ActionFont;
        quickLinksTitle.Text = "Quick links";
        quickLinksTitle.TextAlign = ContentAlignment.MiddleLeft;
        quickLinksTitle.TabStop = false;
        quickLinksTitle.AccessibleRole = AccessibleRole.StaticText;

        manageQuickLinksButton.Click += (_, _) => ActionRequested?.Invoke(this, StartPageAction.ManageQuickLinks);
        quickLinkToolTip.SetToolTip(manageQuickLinksButton, "Add, edit, or reorder Start Page quick links");

        quickLinksHeader.Controls.Add(quickLinksTitle);
        quickLinksHeader.Controls.Add(manageQuickLinksButton);

        quickLinksHost.Dock = DockStyle.Fill;
        quickLinksHost.Margin = Padding.Empty;
        quickLinksHost.TabStop = false;
        quickLinksHost.AccessibleName = "Quick links";
        quickLinksHost.AccessibleRole = AccessibleRole.Grouping;
        quickLinksHost.Resize += (_, _) => { if (!arranging) ArrangeQuickLinks(); };

        contentLayout.Controls.Add(badge, 0, 0);
        contentLayout.Controls.Add(titleLabel, 0, 1);
        contentLayout.Controls.Add(taglineLabel, 0, 2);
        contentLayout.Controls.Add(searchBar, 0, 3);
        contentLayout.Controls.Add(featureHost, 0, 5);
        contentLayout.Controls.Add(quickLinksHeader, 0, 6);
        contentLayout.Controls.Add(quickLinksHost, 0, 7);
        card.Controls.Add(contentLayout);
        Controls.Add(card);

        ApplyDpiMetrics();
        ApplySystemColorPalette();
        arranging = false;
        PerformLayout();
    }

    public event EventHandler<string>? NavigateRequested;
    public event EventHandler<string>? OpenInNewTabRequested;
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
            quickLinksHeader.Visible = false;
            quickLinksTitle.Visible = false;
            manageQuickLinksButton.Visible = false;
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
            quickLinksHeader.Visible = true;
            quickLinksTitle.Visible = true;
            manageQuickLinksButton.Visible = true;
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
        if (!arranging && featureHost.Visible)
        {
            ArrangeFeatureChips();
        }
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

        var countChanged = quickLinksHost.Controls.Count != nextLinks.Length;
        quickLinksHost.SuspendLayout();
        try
        {
            while (quickLinksHost.Controls.Count > nextLinks.Length)
            {
                var lastIndex = quickLinksHost.Controls.Count - 1;
                var control = quickLinksHost.Controls[lastIndex];
                quickLinksHost.Controls.RemoveAt(lastIndex);
                quickLinkToolTip.SetToolTip(control, string.Empty);
                control.Dispose();
            }

            for (var index = 0; index < nextLinks.Length; index++)
            {
                if (index < quickLinksHost.Controls.Count
                    && quickLinksHost.Controls[index] is QuickLinkButton existing)
                {
                    ConfigureQuickLinkButton(existing, nextLinks[index], index);
                }
                else
                {
                    quickLinksHost.Controls.Add(CreateQuickLinkButton(nextLinks[index], index));
                }
            }

            currentQuickLinks = nextLinks;
        }
        finally
        {
            quickLinksHost.ResumeLayout(false);
        }

        if (countChanged)
        {
            PerformLayout();
        }
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
        if (action == StartPageAction.ManageQuickLinks)
        {
            manageQuickLinksButton.PerformClickForTesting();
            return true;
        }

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
            quickLinkToolTip.Dispose();
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
        Invalidate(true);
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        StartPageArtwork.InvalidateBackdropFrame();
        ApplySystemColorPalette();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is WmPrint or WmPrintClient)
        {
            using var lease = StartPageArtwork.AcquireBackdropFrameLease();
            base.WndProc(ref m);
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (arranging) return;

        arranging = true;
        try
        {
            var microLayout = ClientSize.Width < ScaleLogical(420) || ClientSize.Height < ScaleLogical(320);
            var outerMargin = ScaleLogical(microLayout ? 8 : 18);
            var availableWidth = Math.Max(1, ClientSize.Width - (outerMargin * 2));
            var availableHeight = Math.Max(1, ClientSize.Height - (outerMargin * 2));
            var hasQuickLinks = quickLinksHost.Controls.Count > 0;
            var cardWidth = Math.Min(ScaleLogical(680), availableWidth);
            compactLayout = availableHeight < ScaleLogical(hasQuickLinks ? 545 : 365)
                || cardWidth < ScaleLogical(620);

            searchBar.SetCompactMinimumSize(microLayout);
            ConfigureContentRows(compactLayout, microLayout, hasQuickLinks, cardWidth, availableHeight);

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
            if (featureHost.Visible) ArrangeFeatureChips();
            ArrangeQuickLinksHeader();
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
            Cursor = Cursors.Hand
        };
        button.Click += QuickLinkClicked;
        button.OpenInNewTabRequested += QuickLinkOpenInNewTab;
        button.ContextRequested += (_, args) => QuickLinkContextRequested?.Invoke(this, args);
        ConfigureQuickLinkButton(button, link, index);
        return button;
    }

    private void ConfigureQuickLinkButton(QuickLinkButton button, StartPageLink link, int index)
    {
        button.UpdateLink(link);
        button.Tag = link;
        button.TabIndex = index + 2;
        button.AccessibleName = $"Open {link.Title}";
        button.AccessibleDescription = FormatQuickLinkAccessibleDescription(link);
        button.CornerRadius = ScaleLogical(14);
        var tooltipTitle = TextSafety.SanitizeSingleLine(link.Title, 96);
        if (tooltipTitle.Length == 0) tooltipTitle = "Quick link";
        var tooltipUrl = TextSafety.FormatUrlForDisplay(link.Url, 160);
        quickLinkToolTip.SetToolTip(
            button,
            tooltipUrl.Length > 0 ? $"{tooltipTitle} \u2014 {tooltipUrl}" : tooltipTitle);
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
            if ((ModifierKeys & Keys.Control) != 0)
            {
                QuickLinkOpenInNewTab(sender, link.Url);
                return;
            }
            NavigateRequested?.Invoke(this, link.Url);
        }
    }

    private void QuickLinkOpenInNewTab(object? sender, string url)
    {
        if (OpenInNewTabRequested is not null)
        {
            OpenInNewTabRequested.Invoke(this, url);
        }
        else
        {
            NavigateRequested?.Invoke(this, url);
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
        var micro = ClientSize.Width < ScaleLogical(420) || ClientSize.Height < ScaleLogical(320);
        var veryTightHeight = micro && ClientSize.Height < ScaleLogical(250);
        var badgeSide = veryTightHeight ? ScaleLogical(32) : ScaleLogical(micro ? 38 : compactLayout ? 52 : 60);
        badge.Size = new Size(badgeSide, badgeSide);
        searchBar.Margin = Padding.Empty;
        searchBar.SetCompactMinimumSize(micro);

        foreach (var chip in featureChips)
        {
            chip.CornerRadius = ScaleLogical(14);
        }

        foreach (Control control in quickLinksHost.Controls)
        {
            if (control is QuickLinkButton button) button.CornerRadius = ScaleLogical(14);
        }

        if (arranging) return;
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
        Invalidate(true);
    }

    private void ConfigureContentRows(bool compact, bool micro, bool hasQuickLinks, int cardWidth, int availableHeight)
    {
        contentLayout.Padding = new Padding(
            ScaleLogical(micro ? 4 : compact ? 7 : 10),
            ScaleLogical(2),
            ScaleLogical(micro ? 4 : compact ? 7 : 10),
            ScaleLogical(2));

        var showMicroBadge = !micro || availableHeight >= ScaleLogical(250);
        var showMicroTitle = !micro || availableHeight >= ScaleLogical(195);
        badge.Visible = showMicroBadge;
        titleLabel.Visible = showMicroTitle;

        var badgeSide = ScaleLogical(micro ? 38 : compact ? 52 : 60);
        if (badge.Size.Width != badgeSide || badge.Size.Height != badgeSide)
        {
            badge.Size = new Size(badgeSide, badgeSide);
        }

        SetRowHeight(0, showMicroBadge ? (micro ? 42 : compact ? 58 : 68) : 0);
        SetRowHeight(1, showMicroTitle
            ? (micro ? 36 : isPrivateModeActive ? (compact ? 48 : 58) : (compact ? 44 : 52))
            : 0);
        SetRowHeight(2, micro ? 0 : isPrivateModeActive ? (compact ? 28 : 34) : (compact ? 6 : 10));
        SetRowHeight(3, micro ? 48 : compact ? 58 : 66);
        SetRowHeight(4, micro ? 4 : compact ? 8 : 12);
        var contentWidth = Math.Max(1, cardWidth - contentLayout.Padding.Horizontal);
        var showFeatureBar = !micro && availableHeight >= ScaleLogical(420);
        SetRowHeight(5, showFeatureBar ? 32 : 0);

        if (isPrivateModeActive)
        {
            SetRowHeight(6, 0);
            contentLayout.RowStyles[7].Height = 0;
            quickLinksHeader.Visible = false;
            quickLinksTitle.Visible = false;
            manageQuickLinksButton.Visible = false;
            quickLinksHost.Visible = false;
        }
        else
        {
            SetRowHeight(6, micro ? 24 : compact ? 28 : 34);
            if (hasQuickLinks)
            {
                var columns = DetermineQuickLinkColumns(quickLinksHost.Controls.Count, contentWidth);
                var rows = Math.Max(1, (int)Math.Ceiling(quickLinksHost.Controls.Count / (double)columns));
                var quickLinkHeight = ScaleLogical(micro ? 48 : compact ? 56 : 62);
                var gap = ScaleLogical(micro ? 5 : compact ? 6 : 8);
                contentLayout.RowStyles[7].Height = (rows * quickLinkHeight) + ((rows - 1) * gap);
            }
            else
            {
                contentLayout.RowStyles[7].Height = 0;
            }

            quickLinksTitle.Text = hasQuickLinks || micro
                ? "Quick links"
                : "Quick links \u00B7 Add favorites to see them here";
            quickLinksTitle.AccessibleName = quickLinksTitle.Text;
            quickLinksHeader.Visible = true;
            quickLinksTitle.Visible = true;
            manageQuickLinksButton.Visible = true;
            quickLinksHost.Visible = hasQuickLinks;
        }

        featureHost.Visible = showFeatureBar;
        featureHost.Enabled = showFeatureBar;
        foreach (var chip in featureChips)
        {
            chip.Visible = showFeatureBar;
            chip.TabStop = showFeatureBar;
        }
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
        if (!featureHost.Visible
            || featureHost.ClientSize.Width <= 0
            || featureHost.ClientSize.Height <= 0
            || featureChips.Length == 0)
        {
            return;
        }

        var availableWidth = featureHost.ClientSize.Width;
        var availableHeight = featureHost.ClientSize.Height;
        var gap = ScaleLogical(compactLayout ? 8 : 10);
        var pillHeight = Math.Min(availableHeight, ScaleLogical(28));
        var pillTop = Math.Max(0, (availableHeight - pillHeight) / 2);
        var maxPillWidth = Math.Max(
            1,
            (availableWidth - (gap * Math.Max(0, featureChips.Length - 1))) / featureChips.Length);

        var widths = new int[featureChips.Length];
        var totalWidth = gap * Math.Max(0, featureChips.Length - 1);
        for (var index = 0; index < featureChips.Length; index++)
        {
            var preferred = featureChips[index].MeasurePillWidth(pillHeight);
            widths[index] = Math.Clamp(preferred, Math.Min(maxPillWidth, ScaleLogical(96)), maxPillWidth);
            totalWidth += widths[index];
        }

        var x = Math.Max(0, (availableWidth - totalWidth) / 2);
        var radius = Math.Max(6, pillHeight / 2);
        for (var index = 0; index < featureChips.Length; index++)
        {
            var chip = featureChips[index];
            chip.CornerRadius = radius;
            chip.Bounds = new Rectangle(x, pillTop, widths[index], pillHeight);
            x += widths[index] + gap;
        }
    }

    private void ArrangeQuickLinksHeader()
    {
        if (quickLinksHeader.ClientSize.Width <= 0 || quickLinksHeader.ClientSize.Height <= 0) return;

        var headerWidth = quickLinksHeader.ClientSize.Width;
        var headerHeight = quickLinksHeader.ClientSize.Height;
        var gap = ScaleLogical(8);
        var dpi = Math.Max(96, DeviceDpi);
        var scale = dpi / 96f;
        if (measuredManageWidthDpi != dpi)
        {
            Size measuredText;
            if (quickLinksHeader.IsHandleCreated)
            {
                using var measureGraphics = quickLinksHeader.CreateGraphics();
                measuredText = TextRenderer.MeasureText(
                    measureGraphics,
                    "Manage",
                    ManageButtonFont,
                    Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                measuredManageWidthDpi = dpi;
            }
            else
            {
                measuredText = TextRenderer.MeasureText(
                    "Manage",
                    ManageButtonFont,
                    Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
            measuredManageTextWidth = measuredText.Width;
        }
        var chromePadding = (int)Math.Ceiling((9f + 8.5f + 5f + 9f) * scale);
        var pillWidth = Math.Min(
            Math.Max(1, headerWidth / 2),
            Math.Max(ScaleLogical(74), measuredManageTextWidth + chromePadding));
        var pillHeight = Math.Max(
            Math.Min(headerHeight, ScaleLogical(20)),
            Math.Min(headerHeight - Math.Max(2, ScaleLogical(4)), ScaleLogical(compactLayout ? 22 : 25)));

        manageQuickLinksButton.Bounds = new Rectangle(
            Math.Max(0, headerWidth - pillWidth),
            Math.Max(0, (headerHeight - pillHeight) / 2),
            pillWidth,
            pillHeight);
        quickLinksTitle.Bounds = new Rectangle(
            0,
            0,
            Math.Max(1, manageQuickLinksButton.Left - gap),
            headerHeight);
    }

    private void ArrangeQuickLinks()
    {
        var count = quickLinksHost.Controls.Count;
        if (count == 0 || quickLinksHost.ClientSize.Width <= 0 || quickLinksHost.ClientSize.Height <= 0) return;

        var micro = ClientSize.Width < ScaleLogical(420) || ClientSize.Height < ScaleLogical(320);
        var gap = ScaleLogical(micro ? 5 : compactLayout ? 6 : 8);
        var columns = DetermineQuickLinkColumns(count, quickLinksHost.ClientSize.Width);
        var width = Math.Max(1, (quickLinksHost.ClientSize.Width - (gap * (columns - 1))) / columns);
        var rows = (int)Math.Ceiling(count / (double)columns);
        var height = Math.Min(
            ScaleLogical(micro ? 48 : compactLayout ? 56 : 62),
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
        if (availableWidth < ScaleLogical(560)) return Math.Min(2, count);
        if (count == 4) return 2;
        return Math.Min(3, count);
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
        using var path = NativeUiTheme.CreateRoundedPath(bounds, cornerRadius);

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
            using var innerPath = NativeUiTheme.CreateRoundedPath(innerBounds, Math.Max(2f, cornerRadius - 1.1f));
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
                if (Controls.Count == 1
                    && Controls[0].Visible
                    && Controls[0].Bounds.Contains(e.ClipRectangle))
                {
                    return;
                }
                if (!PaintBackdropSlice(this, e)) base.OnPaintBackground(e);
                return;
            }

            base.OnPaintBackground(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = NativeUiTheme.CreateRoundedPath(
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var previousRegion = Region;
                Region = null;
                previousRegion?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void UpdateClipRegion()
        {
            if (Width <= 1 || Height <= 1) return;

            var effectiveBorderAlpha = IsFocused ? FocusBorderColor.A : BorderColor.A;
            if (FillColor.A == 0
                && FillColorSecondary.A == 0
                && effectiveBorderAlpha == 0
                && HighlightColor.A == 0)
            {
                if (Region is not null)
                {
                    var previousEmpty = Region;
                    Region = null;
                    previousEmpty.Dispose();
                }
                lastRegionSize = Size.Empty;
                lastRegionRadius = 0;
                return;
            }

            var currentSize = new Size(Width, Height);
            if (lastRegionSize == currentSize && lastRegionRadius == CornerRadius && Region is not null)
            {
                return;
            }

            lastRegionSize = currentSize;
            lastRegionRadius = CornerRadius;
            using var path = NativeUiTheme.CreateRoundedPath(new RectangleF(0, 0, Width, Height), CornerRadius);
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
            for (var index = 0; index < Controls.Count; index++)
            {
                var child = Controls[index];
                if (child.Visible && child.Bounds.Contains(e.ClipRectangle))
                {
                    return;
                }
            }
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
            for (var index = 0; index < Controls.Count; index++)
            {
                var child = Controls[index];
                if (child.Visible && child.Bounds.Contains(e.ClipRectangle))
                {
                    return;
                }
            }
            if (!PaintBackdropSlice(this, e)) base.OnPaintBackground(e);
        }
    }

    private sealed class QuickLinkButton : Control
    {
        private const int MaximumCachedSiteIcons = 48;
        private static readonly object SiteIconCacheSync = new();
        private static readonly Dictionary<SiteIconCacheKey, Bitmap> SiteIconCache = new();
        private static readonly LinkedList<SiteIconCacheKey> SiteIconLru = new();
        private static readonly (Color Top, Color Bottom, Color Rim)[] GenericJewelPalettes =
        [
            (Color.FromArgb(238, 224, 78, 152), Color.FromArgb(242, 134, 62, 178), Color.FromArgb(185, 255, 198, 232)),
            (Color.FromArgb(238, 156, 92, 232), Color.FromArgb(242, 92, 52, 176), Color.FromArgb(185, 222, 196, 255)),
            (Color.FromArgb(238, 42, 168, 212), Color.FromArgb(242, 40, 98, 200), Color.FromArgb(185, 184, 234, 255)),
            (Color.FromArgb(238, 36, 182, 122), Color.FromArgb(242, 20, 122, 108), Color.FromArgb(185, 178, 245, 216)),
            (Color.FromArgb(238, 240, 136, 76), Color.FromArgb(242, 206, 68, 112), Color.FromArgb(185, 255, 214, 186))
        ];

        private StartPageLink link;
        private SiteIconKind iconKind;
        private string monogram;
        private int hostHashBucket;
        private string detailText;
        private bool hovered;
        private bool pressed;
        private bool middlePressed;

        public QuickLinkButton(StartPageLink link)
        {
            this.link = link;
            iconKind = GetSiteIconKind(link.Url);
            monogram = GetMonogram(link.Title, link.Url);
            var displayHost = GetDisplayHost(link.Url);
            hostHashBucket = ComputeHostHashBucket(displayHost);
            detailText = $"{link.Category} \u00B7 {displayHost}";
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
        public event EventHandler<string>? OpenInNewTabRequested;

        public void UpdateLink(StartPageLink nextLink)
        {
            if (link == nextLink) return;
            link = nextLink;
            iconKind = GetSiteIconKind(nextLink.Url);
            monogram = GetMonogram(nextLink.Title, nextLink.Url);
            var displayHost = GetDisplayHost(nextLink.Url);
            hostHashBucket = ComputeHostHashBucket(displayHost);
            detailText = $"{nextLink.Category} \u00B7 {displayHost}";
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
            middlePressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) pressed = true;
            else if (e.Button == MouseButtons.Middle) middlePressed = true;
            Focus();
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var wasMiddlePressed = middlePressed;
            pressed = false;
            middlePressed = false;
            Invalidate();
            if (e.Button == MouseButtons.Middle && wasMiddlePressed && ClientRectangle.Contains(e.Location))
            {
                OpenInNewTabRequested?.Invoke(this, link.Url);
            }
            else if (e.Button == MouseButtons.Right)
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
            middlePressed = false;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                if (e.Control) OpenInNewTabRequested?.Invoke(this, link.Url);
                else OnClick(EventArgs.Empty);
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
            DrawCachedSiteIcon(
                e.Graphics,
                badgeBounds,
                iconKind,
                monogram,
                hostHashBucket,
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
                detailText,
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

        private readonly record struct SiteIconCacheKey(
            SiteIconKind Kind,
            string Monogram,
            int HostHashBucket,
            int BadgeSize,
            bool HighContrast);

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
            Anthropic,
            GitHub,
            ChatGpt,
            Reddit,
            Wikipedia,
            LinkedIn,
            Netflix
        }

        private static void DrawCachedSiteIcon(
            Graphics graphics,
            Rectangle bounds,
            SiteIconKind kind,
            string monogram,
            int hostHashBucket,
            float scale)
        {
            var badgeSize = Math.Max(1, Math.Min(bounds.Width, bounds.Height));
            var highContrast = SystemInformation.HighContrast;
            var key = new SiteIconCacheKey(
                kind,
                kind == SiteIconKind.Generic || highContrast ? monogram : string.Empty,
                kind == SiteIconKind.Generic && !highContrast ? hostHashBucket : 0,
                badgeSize,
                highContrast);

            lock (SiteIconCacheSync)
            {
                if (!SiteIconCache.TryGetValue(key, out var cached))
                {
                    cached = RenderSiteIconBitmap(key, scale, graphics.DpiX, graphics.DpiY);
                    if (SiteIconCache.Count >= MaximumCachedSiteIcons && SiteIconLru.First is { } oldest)
                    {
                        SiteIconLru.RemoveFirst();
                        if (SiteIconCache.Remove(oldest.Value, out var evicted))
                        {
                            evicted.Dispose();
                        }
                    }
                    SiteIconCache[key] = cached;
                    SiteIconLru.AddLast(key);
                }
                else if (Math.Abs(cached.HorizontalResolution - graphics.DpiX) > 0.5f
                    || Math.Abs(cached.VerticalResolution - graphics.DpiY) > 0.5f)
                {
                    cached.SetResolution(graphics.DpiX, graphics.DpiY);
                }

                graphics.DrawImageUnscaled(cached, bounds.X, bounds.Y);
            }
        }

        private static Bitmap RenderSiteIconBitmap(
            SiteIconCacheKey key,
            float scale,
            float dpiX,
            float dpiY)
        {
            var bitmap = new Bitmap(key.BadgeSize, key.BadgeSize, PixelFormat.Format32bppPArgb);
            try
            {
                bitmap.SetResolution(Math.Max(48f, dpiX), Math.Max(48f, dpiY));
                using var g = Graphics.FromImage(bitmap);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);
                var localBounds = new Rectangle(0, 0, key.BadgeSize - 1, key.BadgeSize - 1);
                DrawSiteIcon(g, localBounds, key.Kind, key.Monogram, key.HostHashBucket, scale);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static int ComputeHostHashBucket(string host)
        {
            if (string.IsNullOrEmpty(host)) return 0;
            unchecked
            {
                uint hash = 2166136261u;
                for (var i = 0; i < host.Length; i++)
                {
                    hash ^= char.ToLowerInvariant(host[i]);
                    hash *= 16777619u;
                }
                return (int)(hash % (uint)GenericJewelPalettes.Length);
            }
        }

        private static void DrawSiteIcon(
            Graphics graphics,
            Rectangle bounds,
            SiteIconKind kind,
            string monogram,
            int hostHashBucket,
            float scale)
        {
            if (SystemInformation.HighContrast)
            {
                using var highContrastPath = NativeUiTheme.CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
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
                case SiteIconKind.GitHub:
                    DrawGitHubIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.ChatGpt:
                    DrawChatGptIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.Reddit:
                    DrawRedditIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.Wikipedia:
                    DrawWikipediaIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.LinkedIn:
                    DrawLinkedInIcon(graphics, bounds, scale);
                    return;
                case SiteIconKind.Netflix:
                    DrawNetflixIcon(graphics, bounds, scale);
                    return;
            }

            var jewel = GenericJewelPalettes[Math.Clamp(hostHashBucket, 0, GenericJewelPalettes.Length - 1)];
            using var path = NativeUiTheme.CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
            using var fill = new LinearGradientBrush(
                bounds,
                jewel.Top,
                jewel.Bottom,
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(jewel.Rim, Math.Max(1f, scale));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
            TextRenderer.DrawText(
                graphics,
                monogram,
                LinkMonogramFont,
                bounds,
                NativeUiTheme.AccentText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private static void DrawYouTubeIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var path = NativeUiTheme.CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
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
            using var path = NativeUiTheme.CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
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
            using var cameraPath = NativeUiTheme.CreateRoundedPath(cameraBounds, 5f * scale);
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
            using var path = NativeUiTheme.CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
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
            using var path = NativeUiTheme.CreateRoundedPath(bounds, Math.Max(8f, 10f * scale));
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
            using var path = NativeUiTheme.CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
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

        private static void DrawGitHubIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var path = NativeUiTheme.CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
            using var fill = new LinearGradientBrush(
                bounds,
                Color.FromArgb(46, 52, 62),
                Color.FromArgb(20, 24, 31),
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(Color.FromArgb(170, 200, 210, 224), Math.Max(1f, scale));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);

            using var white = new SolidBrush(Color.White);
            var w = bounds.Width;
            var h = bounds.Height;
            var head = new RectangleF(bounds.Left + (w * 0.24f), bounds.Top + (h * 0.27f), w * 0.52f, h * 0.42f);
            graphics.FillEllipse(white, head);
            graphics.FillPolygon(
                white,
                [
                    new PointF(bounds.Left + (w * 0.28f), bounds.Top + (h * 0.38f)),
                    new PointF(bounds.Left + (w * 0.30f), bounds.Top + (h * 0.19f)),
                    new PointF(bounds.Left + (w * 0.43f), bounds.Top + (h * 0.30f))
                ]);
            graphics.FillPolygon(
                white,
                [
                    new PointF(bounds.Right - (w * 0.28f), bounds.Top + (h * 0.38f)),
                    new PointF(bounds.Right - (w * 0.30f), bounds.Top + (h * 0.19f)),
                    new PointF(bounds.Right - (w * 0.43f), bounds.Top + (h * 0.30f))
                ]);
            var shoulders = new RectangleF(bounds.Left + (w * 0.33f), bounds.Top + (h * 0.60f), w * 0.34f, h * 0.26f);
            using var shoulderPath = NativeUiTheme.CreateRoundedPath(shoulders, w * 0.12f);
            graphics.FillPath(white, shoulderPath);
            using var tail = new Pen(Color.White, Math.Max(1.6f, 2f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawArc(tail, bounds.Left + (w * 0.16f), bounds.Top + (h * 0.57f), w * 0.24f, h * 0.20f, 30f, 130f);
        }

        private static void DrawChatGptIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var path = NativeUiTheme.CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
            using var fill = new LinearGradientBrush(
                bounds,
                Color.FromArgb(25, 195, 125),
                Color.FromArgb(14, 132, 88),
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(Color.FromArgb(170, 180, 255, 224), Math.Max(1f, scale));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);

            var cx = bounds.Left + (bounds.Width / 2f);
            var cy = bounds.Top + (bounds.Height / 2f);
            var orbit = bounds.Width * 0.17f;
            var petalR = bounds.Width * 0.165f;
            using var pen = new Pen(Color.White, Math.Max(1.4f, 1.8f * scale));
            for (var i = 0; i < 6; i++)
            {
                var angle = (i * MathF.PI / 3f) - (MathF.PI / 6f);
                var px = cx + (MathF.Cos(angle) * orbit);
                var py = cy + (MathF.Sin(angle) * orbit);
                graphics.DrawEllipse(pen, px - petalR, py - petalR, petalR * 2f, petalR * 2f);
            }
        }

        private static void DrawRedditIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var fill = new LinearGradientBrush(
                bounds,
                Color.FromArgb(255, 87, 34),
                Color.FromArgb(217, 58, 0),
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(Color.FromArgb(170, 255, 206, 188), Math.Max(1f, scale));
            graphics.FillEllipse(fill, bounds);
            graphics.DrawEllipse(border, bounds);

            var w = bounds.Width;
            var h = bounds.Height;
            using var white = new SolidBrush(Color.White);
            using var whitePen = new Pen(Color.White, Math.Max(1.4f, 1.7f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            // Antenna
            graphics.DrawLines(
                whitePen,
                [
                    new PointF(bounds.Left + (w * 0.50f), bounds.Top + (h * 0.39f)),
                    new PointF(bounds.Left + (w * 0.57f), bounds.Top + (h * 0.21f)),
                    new PointF(bounds.Left + (w * 0.72f), bounds.Top + (h * 0.25f))
                ]);
            var tipSize = w * 0.12f;
            graphics.FillEllipse(white, bounds.Left + (w * 0.70f), bounds.Top + (h * 0.20f), tipSize, tipSize);
            // Ears
            var earSize = w * 0.16f;
            graphics.FillEllipse(white, bounds.Left + (w * 0.17f), bounds.Top + (h * 0.45f), earSize, earSize);
            graphics.FillEllipse(white, bounds.Right - (w * 0.17f) - earSize, bounds.Top + (h * 0.45f), earSize, earSize);
            // Head
            var face = new RectangleF(bounds.Left + (w * 0.23f), bounds.Top + (h * 0.39f), w * 0.54f, h * 0.37f);
            graphics.FillEllipse(white, face);
            // Eyes
            using var eyeBrush = new SolidBrush(Color.FromArgb(232, 68, 12));
            var eyeSize = Math.Max(2.2f, w * 0.105f);
            graphics.FillEllipse(eyeBrush, face.Left + (face.Width * 0.23f), face.Top + (face.Height * 0.34f), eyeSize, eyeSize);
            graphics.FillEllipse(eyeBrush, face.Right - (face.Width * 0.23f) - eyeSize, face.Top + (face.Height * 0.34f), eyeSize, eyeSize);
        }

        private static void DrawWikipediaIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var fill = new LinearGradientBrush(
                bounds,
                Color.FromArgb(245, 247, 250),
                Color.FromArgb(203, 211, 223),
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(Color.FromArgb(180, 255, 255, 255), Math.Max(1f, scale));
            graphics.FillEllipse(fill, bounds);
            graphics.DrawEllipse(border, bounds);

            var w = bounds.Width;
            var h = bounds.Height;
            using var pen = new Pen(Color.FromArgb(32, 36, 44), Math.Max(1.6f, 2f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var topY = bounds.Top + (h * 0.31f);
            var botY = bounds.Top + (h * 0.71f);
            graphics.DrawLines(
                pen,
                [
                    new PointF(bounds.Left + (w * 0.21f), topY),
                    new PointF(bounds.Left + (w * 0.35f), botY),
                    new PointF(bounds.Left + (w * 0.51f), topY + (h * 0.04f))
                ]);
            graphics.DrawLines(
                pen,
                [
                    new PointF(bounds.Left + (w * 0.49f), topY + (h * 0.04f)),
                    new PointF(bounds.Left + (w * 0.65f), botY),
                    new PointF(bounds.Left + (w * 0.79f), topY)
                ]);
        }

        private static void DrawLinkedInIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var path = NativeUiTheme.CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
            using var fill = new LinearGradientBrush(
                bounds,
                Color.FromArgb(14, 118, 212),
                Color.FromArgb(0, 65, 130),
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(Color.FromArgb(165, 180, 224, 255), Math.Max(1f, scale));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);

            var w = bounds.Width;
            var h = bounds.Height;
            var stroke = Math.Max(2.2f, 2.7f * scale);
            using var whiteBrush = new SolidBrush(Color.White);
            using var whitePen = new Pen(Color.White, stroke)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            // "i" dot and stem
            var dotSize = Math.Max(2.8f, w * 0.115f);
            var iX = bounds.Left + (w * 0.30f);
            graphics.FillEllipse(whiteBrush, iX - (dotSize / 2f), bounds.Top + (h * 0.25f), dotSize, dotSize);
            graphics.DrawLine(whitePen, iX, bounds.Top + (h * 0.44f), iX, bounds.Top + (h * 0.73f));
            // "n" stem and arch
            var nLeft = bounds.Left + (w * 0.47f);
            var nRight = bounds.Left + (w * 0.72f);
            var nTop = bounds.Top + (h * 0.44f);
            var nBot = bounds.Top + (h * 0.73f);
            graphics.DrawLine(whitePen, nLeft, nTop, nLeft, nBot);
            graphics.DrawArc(whitePen, nLeft, nTop - (h * 0.02f), nRight - nLeft, h * 0.24f, 180f, 180f);
            graphics.DrawLine(whitePen, nRight, nTop + (h * 0.10f), nRight, nBot);
        }

        private static void DrawNetflixIcon(Graphics graphics, Rectangle bounds, float scale)
        {
            using var path = NativeUiTheme.CreateRoundedPath(bounds, Math.Max(8f, 11f * scale));
            using var fill = new LinearGradientBrush(
                bounds,
                Color.FromArgb(26, 26, 32),
                Color.FromArgb(10, 10, 14),
                LinearGradientMode.ForwardDiagonal);
            using var border = new Pen(Color.FromArgb(165, 240, 110, 120), Math.Max(1f, scale));
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);

            var w = bounds.Width;
            var h = bounds.Height;
            var barW = Math.Max(3.2f, w * 0.145f);
            var leftX = bounds.Left + (w * 0.26f);
            var rightX = bounds.Right - (w * 0.26f) - barW;
            var topY = bounds.Top + (h * 0.20f);
            var botY = bounds.Bottom - (h * 0.20f);
            using var darkRed = new SolidBrush(Color.FromArgb(178, 7, 16));
            using var brightRed = new SolidBrush(Color.FromArgb(229, 9, 20));
            graphics.FillRectangle(darkRed, leftX, topY, barW, botY - topY);
            graphics.FillRectangle(darkRed, rightX, topY, barW, botY - topY);
            graphics.FillPolygon(
                brightRed,
                [
                    new PointF(leftX, topY),
                    new PointF(leftX + barW, topY),
                    new PointF(rightX + barW, botY),
                    new PointF(rightX, botY)
                ]);
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
            if (IsHostOrSubdomain(host, "anthropic.com") || IsHostOrSubdomain(host, "claude.ai")) return SiteIconKind.Anthropic;
            if (IsHostOrSubdomain(host, "github.com")) return SiteIconKind.GitHub;
            if (IsHostOrSubdomain(host, "chatgpt.com") || IsHostOrSubdomain(host, "openai.com")) return SiteIconKind.ChatGpt;
            if (IsHostOrSubdomain(host, "reddit.com")) return SiteIconKind.Reddit;
            if (IsHostOrSubdomain(host, "wikipedia.org")) return SiteIconKind.Wikipedia;
            if (IsHostOrSubdomain(host, "linkedin.com")) return SiteIconKind.LinkedIn;
            if (IsHostOrSubdomain(host, "netflix.com")) return SiteIconKind.Netflix;
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

        public int MeasurePillWidth(int pillHeight)
        {
            var scale = Math.Max(1f, DeviceDpi / 96f);
            var iconSize = Math.Max(14, Math.Min(pillHeight - ScalePixels(8), (int)Math.Round(18f * scale)));
            var padX = Math.Max(8, (int)Math.Round(11f * scale));
            var gap = Math.Max(5, (int)Math.Round(6f * scale));
            var measured = TextRenderer.MeasureText(
                detail,
                FeatureDetailFont,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return (padX * 2) + iconSize + gap + measured.Width + ScalePixels(4);
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
            var pillMode = Height <= ScalePixels(42);
            var radius = pillMode ? Math.Max(6f, (Height - 1f) / 2f) : CornerRadius;
            PaintLiquidGlassCard(e.Graphics, cardBounds, radius, hovered, pressed, Focused);

            var scale = Math.Max(1f, DeviceDpi / 96f);
            if (pillMode)
            {
                var iconSize = Math.Max(14, Math.Min(Height - ScalePixels(8), (int)Math.Round(18f * scale)));
                var padLeft = Math.Max(8, (int)Math.Round(10f * scale));
                var iconBounds = new Rectangle(padLeft, Math.Max(1, (Height - iconSize) / 2), iconSize, iconSize);
                StartPageArtwork.DrawFeatureIcon(
                    e.Graphics,
                    iconBounds,
                    Action,
                    hovered || Focused);

                var textLeft = iconBounds.Right + Math.Max(5, (int)Math.Round(6f * scale));
                var textBounds = new Rectangle(
                    textLeft,
                    0,
                    Math.Max(1, Width - textLeft - padLeft),
                    Height);
                TextRenderer.DrawText(
                    e.Graphics,
                    detail,
                    FeatureDetailFont,
                    textBounds,
                    SystemInformation.HighContrast
                        ? highContrastSelection ? SystemColors.HighlightText : SystemColors.WindowText
                        : hovered || Focused ? PrimaryTextColor : SecondaryTextColor,
                    TextFormatFlags.Left
                    | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPadding
                    | TextFormatFlags.NoPrefix
                    | TextFormatFlags.SingleLine);
                return;
            }

            var dense = Height < ScalePixels(64);
            var fullIconSize = Math.Max(30, (int)Math.Round((dense ? 36 : 44) * scale));
            var iconLeft = Math.Max(8, (int)Math.Round((dense ? 9 : 12) * scale));
            var fullIconBounds = new Rectangle(iconLeft, Math.Max(1, (Height - fullIconSize) / 2), fullIconSize, fullIconSize);
            StartPageArtwork.DrawFeatureIcon(
                e.Graphics,
                fullIconBounds,
                Action,
                hovered || Focused);

            var fullTextLeft = fullIconBounds.Right + Math.Max(7, (int)Math.Round((dense ? 8 : 11) * scale));
            var textWidth = Math.Max(1, Width - fullTextLeft - iconLeft);
            var headingBounds = new Rectangle(fullTextLeft, Math.Max(2, Height / 2 - ScalePixels(18)), textWidth, ScalePixels(18));
            var detailBounds = new Rectangle(fullTextLeft, Height / 2 + ScalePixels(1), textWidth, ScalePixels(17));
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

    private sealed class BrandTitleLabel : Label
    {
        private string? cachedMeasureText;
        private int cachedMeasureDpi;
        private Size cachedMeasureSize;

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
                var dpi = Math.Max(96, DeviceDpi);
                var scale = dpi / 96f;
                Size measured;
                if (cachedMeasureDpi == dpi && string.Equals(cachedMeasureText, text, StringComparison.Ordinal))
                {
                    measured = cachedMeasureSize;
                }
                else
                {
                    measured = TextRenderer.MeasureText(
                        e.Graphics,
                        text,
                        Font,
                        ClientSize,
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    cachedMeasureText = text;
                    cachedMeasureDpi = dpi;
                    cachedMeasureSize = measured;
                }
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

    private sealed class ManageQuickLinksButton : Control
    {
        private bool hovered;
        private bool pressed;

        public ManageQuickLinksButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
            NativeStartPage.TrySetTransparentBackColor(this);
            ForeColor = SecondaryTextColor;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleName = "Manage quick links";
            AccessibleDescription = "Add, edit, or reorder quick links";
            AccessibleRole = AccessibleRole.PushButton;
            Text = "Manage";
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
            var cardBounds = new RectangleF(0.5f, 0.5f, Math.Max(1f, Width - 1f), Math.Max(1f, Height - 1f));
            var radius = Math.Max(6f, (Height - 1f) / 2f);
            PaintLiquidGlassCard(e.Graphics, cardBounds, radius, hovered, pressed, Focused);

            var scale = Math.Max(1f, DeviceDpi / 96f);
            var textColor = SystemInformation.HighContrast
                ? highContrastSelection ? SystemColors.HighlightText : SystemColors.WindowText
                : hovered || Focused ? PrimaryTextColor : SecondaryTextColor;

            var iconSize = Math.Max(7f, 8.5f * scale);
            var iconLeft = Math.Max(7f, 9f * scale);
            var iconTop = (Height - iconSize) / 2f;
            var tile = Math.Max(2f, (iconSize - (2f * scale)) / 2f);
            var step = tile + (2f * scale);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var iconBrush = new SolidBrush(
                SystemInformation.HighContrast ? textColor : hovered || Focused ? AccentColor : FocusColor);
            for (var row = 0; row < 2; row++)
            {
                for (var col = 0; col < 2; col++)
                {
                    var rect = new RectangleF(iconLeft + (col * step), iconTop + (row * step), tile, tile);
                    using var tilePath = NativeUiTheme.CreateRoundedPath(rect, Math.Max(0.8f, 1.1f * scale));
                    e.Graphics.FillPath(iconBrush, tilePath);
                }
            }

            var textLeft = (int)Math.Ceiling(iconLeft + iconSize + (5f * scale));
            var textBounds = new Rectangle(
                textLeft,
                0,
                Math.Max(1, Width - textLeft - Math.Max(5, (int)Math.Round(7f * scale))),
                Height);
            TextRenderer.DrawText(
                e.Graphics,
                "Manage",
                ManageButtonFont,
                textBounds,
                textColor,
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine);
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

            var badgeWidth = Math.Max(22f, markBounds.Width * 0.50f);
            var badgeHeight = Math.Max(14f, markBounds.Height * 0.30f);
            var badgeBounds = new RectangleF(
                markBounds.Right - badgeWidth,
                markBounds.Bottom - badgeHeight,
                badgeWidth,
                badgeHeight);
            StartPageArtwork.DrawIncognitoBadge(e.Graphics, badgeBounds);
        }
    }
}
