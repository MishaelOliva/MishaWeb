using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MishaWeb;

internal readonly record struct ChromeColorPolicy(
    bool HighContrast,
    Color Frame,
    Color ChromeSurface,
    Color ToolbarSurface,
    Color PageSurface,
    Color FieldSurface,
    Color ChromeText,
    Color FieldText,
    Color MutedText,
    Color Border,
    Color Focus,
    Color Selection,
    Color SelectionText,
    Color HoverSurface,
    Color PressedSurface,
    Color TitleHoverSurface,
    Color TitlePressedSurface,
    Color MenuSurface,
    Color MenuText,
    Color WindowFrameBorder,
    Color WindowFrameText);

internal readonly record struct ChromeContrastSnapshot(
    Color ChromeSurface,
    Color ToolbarSurface,
    Color FieldSurface,
    Color FieldText,
    Color Focus,
    Color MenuSurface,
    Color MenuText,
    bool ToolbarButtonUsesHighContrast,
    bool CaptionButtonUsesHighContrast,
    bool OmniboxUsesHighContrast,
    bool TabUsesHighContrast,
    bool MenuRendererUsesHighContrast);

internal readonly record struct ToolbarLayoutSnapshot(
    int ClientWidth,
    int ToolbarWidth,
    int MinimumOmniboxWidth,
    Rectangle Back,
    Rectangle Forward,
    Rectangle Reload,
    Rectangle Home,
    Rectangle Omnibox,
    Rectangle Status,
    Rectangle Downloads,
    Rectangle Menu,
    bool HomeVisible,
    bool StatusVisible,
    bool DownloadsVisible,
    string StatusText,
    string StatusAccessibleName,
    bool VectorButtonTextIsEmpty);

internal readonly record struct BrowserEnvironmentOptionsSnapshot(
    bool EnableTrackingPrevention,
    bool AreBrowserExtensionsEnabled,
    bool ExclusiveUserDataFolderAccess,
    string? AdditionalBrowserArguments = null);

internal readonly record struct BrowserControllerProfileSnapshot(
    bool IsInPrivateModeEnabled,
    string? ProfileName);

public sealed partial class MainForm : Form
{
    private const int MaximumTabs = 128;
    private const int MaximumActiveDownloads = 64;
    private const int MaximumPendingExternalNavigations = 16;
    private const int MaximumPendingPermissionRequests = 16;
    private const int MaximumTrackedFramesPerTab = 256;
    // Cap on remembered subframe document URLs per tab. Bounded, cleared on
    // overflow, and reset whenever the top-level document changes, so a URL seen
    // as a subframe under one page cannot leak its classification into the next.
    private const int MaximumTrackedSubframeUrlsPerTab = 128;
    private const int MaximumConcurrentFrameSetups = 2;
    private const int MaximumPendingFrameSetups = 64;
    private const int MaximumHoverStatusCharacters = 2_048;
    private const int MaximumPendingHoverStatusCharacters = TextSafety.MaximumUrlInputCharactersToParse;
    private const int MaximumBrowserChromeUrlCharacters = 512;
    private const int MaximumDownloadDisplayNameCharacters = 255;
    private const int MaximumDownloadDisplayPathCharacters = 1_024;
    private const int MaximumExtensionDisplayNameCharacters = 120;
    private const int MemoryScanIntervalMs = 15_000;
    private const int StandardMemoryScanIntervalMs = 60_000;
    private const int IdleMemoryMaintenanceIntervalMs = 20_000;
    private const int MaximumStandardUnloadsPerSweep = 4;
    private const int MemoryScanCooldownSeconds = 1;
    private const int AddressSuggestionDebounceMs = 120;
    private const int MinimumToolbarOmniboxWidth = 240;
    // Wide enough for the longest status the toolbar shows in practice, measured
    // at about 175 px for "Ready - 13 blocked - 1 downloading". At 150 the
    // ellipsis cut the download count off entirely. UpdateResponsiveToolbar
    // protects MinimumToolbarOmniboxWidth before showing the column, so a wider
    // status gives up its own space rather than pushing the omnibox below its
    // minimum.
    private const int MaximumToolbarStatusWidth = 210;
    private const int WindowDragReserveLogicalWidth = 48;
    private static readonly Color ChromeColor = NativeUiTheme.Chrome;
    private static readonly Color ToolbarColor = NativeUiTheme.Toolbar;
    private static readonly Color TabIdleColor = NativeUiTheme.Chrome;
    private static readonly Color TabActiveColor = NativeUiTheme.SurfaceRaised;
    private static readonly Color OmniboxColor = NativeUiTheme.Field;
    private static readonly Color OmniboxFocusColor = NativeUiTheme.Focus;
    private static readonly Color MutedTextColor = NativeUiTheme.Muted;
    private static readonly Color PageColor = NativeUiTheme.Window;
    private static readonly Color DarkPageColor = NativeUiTheme.Window;
    private static readonly Color PageTextColor = NativeUiTheme.Text;
    private static readonly Color PageMutedTextColor = NativeUiTheme.Muted;
    private static readonly Color AccentColor = NativeUiTheme.Accent;
    private static readonly Color PositiveColor = NativeUiTheme.Positive;
    private static readonly Color NegativeColor = NativeUiTheme.Negative;
    private static readonly Font OverlayTitleFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 15f);
    private static readonly Font OverlayDetailFont = NativeUiTheme.GetSharedFont("Segoe UI", 9.5f);
    private static readonly Font OverlayActionFont = NativeUiTheme.GetSharedFont("Segoe UI Semibold", 9f);
    private static readonly Font TabTitleFont = NativeUiTheme.GetSharedFont("Segoe UI", 9f);
    private static readonly byte[] TransparentGif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
    private static readonly byte[] YouTubeScriptStubBytes =
        "window.adsbygoogle = window.adsbygoogle || []; window.adsbygoogle.loaded = true; window.googletag = window.googletag || { cmd: [], apiReady: true };"u8.ToArray();
    // Stub bodies handed back for every blocked request. Hoisted to statics so an
    // ad-heavy page does not allocate a fresh array per blocked subresource.
    private static readonly byte[] YouTubePageAdStubBytes =
        ")]}'\n\n{\"id\":\"ANyPxKrAzkV5cLEVtGXqf11mX0EFDh00ASxA-CsrWnAIiEOXKju9lnsjfHFdvqf7wl5Er6SrJEF7\",\"type\":4}"u8.ToArray();
    private static readonly byte[] EmptyJsonObjectBytes = "{}"u8.ToArray();
    private static readonly CoreWebView2WebResourceRequestSourceKinds DocumentRequestSources =
        CoreWebView2WebResourceRequestSourceKinds.Document;
    private static readonly CoreWebView2WebResourceRequestSourceKinds WorkerRequestSources =
        CoreWebView2WebResourceRequestSourceKinds.SharedWorker
        | CoreWebView2WebResourceRequestSourceKinds.ServiceWorker;
    private static readonly string[] RequestSourceHeaderNames = ["Referer", "Origin"];

    private readonly TableLayoutPanel rootLayout = new();
    private readonly TableLayoutPanel titleBar = new();
    private readonly TableLayoutPanel toolbar = new();
    private readonly BrandLogoControl appMark = new();
    private readonly Panel tabArea = new();
    private readonly FlowLayoutPanel tabStrip = new();
    private readonly Panel tabDropIndicator = new();
    private readonly TabDragGhost tabDragGhost = new();
    private readonly RoundedPanel omniboxPanel = new();
    private readonly AddressSuggestionPopup addressSuggestionPopup = new();
    private readonly RoundedPanel findOmniboxPanel = new();
    private readonly TableLayoutPanel findBar = new();
    private readonly TransparentPanelHost pageHost = new();
    private readonly FirstClickSelectTextBox addressBar = new();
    private readonly TextBox findBox = new();
    private readonly Label findMatchLabel = new();
    private readonly ChromeButton backButton = new();
    private readonly ChromeButton forwardButton = new();
    private readonly ChromeButton reloadButton = new();
    private readonly ChromeButton homeButton = new();
    private readonly ChromeButton siteInfoButton = new();
    private readonly ChromeButton favoriteButton = new();
    private readonly ChromeButton downloadsButton = new();
    private readonly MultiViewToolbarButton multiViewButton = new();
    private readonly ChromeButton newTabButton = new();
    private readonly ChromeButton tabListButton = new();
    private readonly ChromeButton menuButton = new();
    private readonly ContextMenuStrip multiViewMenu = new();
    private readonly ContextMenuStrip paneSwitchMenu = new();
    private readonly PaneHeaderBar[] paneHeaders = new PaneHeaderBar[4];
    private readonly MultiViewSplitter[] multiViewSplitters = new MultiViewSplitter[2];
    private readonly MultiViewState multiViewState = new();
    private readonly ToolStripMenuItem multiViewAppMenuItem = new("Multiple views");
    private MultiViewComputedLayout? lastMultiViewLayout;
    private readonly WindowCaptionButton minimizeButton = new();
    private readonly WindowCaptionButton maximizeButton = new();
    private readonly WindowCaptionButton closeWindowButton = new();
    private readonly ChromeButton findCaseSensitiveButton = new();
    private readonly ChromeButton findPreviousButton = new();
    private readonly ChromeButton findNextButton = new();
    private readonly ChromeButton closeFindButton = new();
    private readonly AnnouncingStatusLabel statusLabel = new();
    private readonly ToolTip toolTip = new() { AutoPopDelay = 7000, InitialDelay = 450, ReshowDelay = 100 };
    private readonly IncognitoBadgeControl incognitoBadge;
    private readonly ContextMenuStrip appMenu = new();
    private readonly ContextMenuStrip tabMenu = new();
    private readonly ContextMenuStrip tabListMenu = new();
    private readonly ContextMenuStrip siteInfoMenu = new();
    private readonly ContextMenuStrip addressContextMenu = new();
    private readonly ChromeMenuRenderer menuRenderer = new();
    private readonly ToolStripMenuItem favoritesMenu = new("Favorites");
    private readonly ToolStripMenuItem historyMenu = new("History");
    private readonly ToolStripMenuItem downloadsMenu = new("Downloads");
    private readonly ToolStripMenuItem extensionsMenuItem = new("Extensions…");
    private readonly ToolStripMenuItem adBlockMenuItem = new("Ad and tracker blocker");
    private readonly ToolStripMenuItem searchProviderMenu = new("Search engine");
    private readonly ToolStripMenuItem resourceModeMenu = new("Resource mode");
    private readonly ToolStripMenuItem resourceOffMenuItem = new("Off");
    private readonly ToolStripMenuItem reduceMotionMenuItem = new("Reduce website motion");
    private readonly BrowserExtensions browserExtensions = new();
    private readonly HashSet<string> installingChromeStoreExtensionIds = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource chromeStoreInstallCancellation = new();
    private readonly AdBlockEngine adBlocker = AdBlockEngine.Shared;
    private readonly Queue<FrameSetupWork> pendingFrameSetups = [];
    private readonly Dictionary<CoreWebView2Frame, FrameSetupWork> pendingFrameSetupByFrame =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<CoreWebView2Frame> activeFrameSetups =
        new(ReferenceEqualityComparer.Instance);
    private readonly ToolStripMenuItem zoomMenu = new("Zoom");
    private readonly ToolStripMenuItem memorySaverMenuItem = new("Memory saver");
    private readonly ToolStripMenuItem ultraLightMenuItem = new("Ultra-light mode");
    private readonly ToolStripMenuItem siteThemeMenuItem = new("Dark website theme");
    private readonly ToolStripMenuItem fullScreenMenuItem = new("Fullscreen");
    private readonly ToolStripMenuItem restartBrowserForUpdateMenuItem = new("Restart browser engine to update")
    {
        Visible = false
    };
    private readonly System.Windows.Forms.Timer memoryTimer = new() { Interval = MemoryScanIntervalMs };
    private readonly System.Windows.Forms.Timer stateSaveTimer = new() { Interval = 1_500 };
    private readonly System.Windows.Forms.Timer addressSuggestionTimer = new() { Interval = AddressSuggestionDebounceMs };
    private readonly System.Windows.Forms.Timer findDebounceTimer = new() { Interval = 35 };
    private readonly System.Windows.Forms.Timer statusUiTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer downloadUiTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer downloadGraceTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer transientStatusTimer = new() { Interval = 4_000 };
    private readonly List<BrowserTab> tabs = [];
    private readonly LinkedList<ClosedTabEntry> closedTabs = [];
    private readonly List<DownloadEntry> downloads = [];
    private readonly MruTabModel<BrowserTab> mruTabs = new();
    private readonly Queue<PermissionRequest> permissionQueue = [];
    private readonly BrowserStateStore stateStore;
    private readonly BrowserState state;
    private readonly HashSet<string> adBlockExceptionHosts;
    private readonly object externalNavigationSync = new();
    private readonly Queue<string?> pendingExternalNavigations = [];
    private readonly bool persistStateOnClose;
    private readonly BrowserMode browserMode;
    private readonly bool isPrivateMode;
    private SystemMemorySnapshot systemMemory;

    private CoreWebView2Environment? environment;
    private Task<CoreWebView2Environment>? environmentTask;
    private CoreWebView2Environment? browserVersionEventEnvironment;
    private EventHandler<object>? browserVersionAvailableHandler;
    private long environmentGeneration;
    private long windowStateGeneration;
    private long websiteThemeGeneration;
    private BrowserTab? activeTab;
    private BrowserTab? draggedTab;
    private int tabDragDropIndex = -1;
    private int tabDragIndicatorIndex = -1;
    private Point tabDragScreenLocation;
    private BrowserTab? workerAdBlockFilterOwner;
    private bool memorySaverEnabled;
    private bool ultraLightEnabled;
    private bool darkModeEnabled;
    private bool adBlockEnabled;
    private bool reduceWebsiteMotionEnabled;
    private string searchProviderId = BrowserPolicy.DefaultSearchProviderId;
    private bool addressBarEditing;
    private bool smartSearchBarEditing;
    private bool memorySweepRunning;
    private DateTime lastUserInteractionUtc = DateTime.UtcNow;
    private bool isAppIdleLowPowerActive;
    private const int AppIdleThresholdMinutes = 5;
    private const long MaxInactiveRendererPrivateBytes = 96L * 1024 * 1024;
    private bool drainingFrameSetupQueue;
    private bool frameSetupQueueStopping;
    private int activeFrameSetupWorkers;
    private bool restoringSession;
    private bool replacingWindow;
    private bool recoveringBrowser;
    private bool isClosing;
    private bool privateCleanupCompleted;
    private bool privateCleanupInProgress;
    private int processTrimInFlight;
    private DateTimeOffset lastManagedGcAtUtc = DateTimeOffset.MinValue;
    private bool browserProcessRestartApproved;
    private bool browserProcessRestartStateSaved;
    private int activeExtensionMutationCount;
    private bool closeWhenExtensionOperationsFinish;
    private bool managedExtensionReconciliationStarted;
    private bool browserStartupCompleted;
    private bool externalNavigationDrainScheduled;
    private bool isFullScreen;
    private bool isDomFullScreen;
    private FormWindowState normalWindowStateBeforeDomFullScreen;
    private Rectangle normalBoundsBeforeDomFullScreen;
    private bool hasDomFullScreenWindowState;
    private bool isWindowMinimized;
    private bool maximizeNonClientPressed;
    private bool findBarWasVisibleBeforeFullScreen;
    private bool findBarWasVisibleBeforeDomFullScreen;
    private bool applyingZoomPreference;
    private string pendingSuggestionInput = string.Empty;
    private string lastAddressSuggestionInput = string.Empty;
    private bool isAutocompletingAddressBar;
    private bool suppressAddressBarAutocomplete;
    private CancellationTokenSource? liveSuggestionCts;
    private Control? suggestionAnchor;
    private readonly string? startupAddress;
    private readonly string? webViewUserDataFolderOverride;
    private IReadOnlyList<StartPageLink>? cachedStartPageLinks;
    private bool isStartPageLinksDirty = true;
    private DateTimeOffset nextLifecycleScanAt = DateTimeOffset.MinValue;
    private FormWindowState normalWindowState;
    private Rectangle normalBounds;
    private FormWindowState lastNonMinimizedWindowState = FormWindowState.Normal;
    private bool isInSizeMove;
    private bool pendingPlacementSaveAfterSizeMove;
    private (FormWindowState State, bool FullScreen, bool DomFullScreen)? lastAppliedWindowControlState;
    private string transientStatus = string.Empty;
    private string lastAnnouncedStatus = string.Empty;
    private string? lastRenderedStatusText;
    private SmartSearchBar? activeSmartSearchBar;
    private CommandPaletteForm? commandPalette;
    private MruSwitcherForm? mruSwitcher;
    private SessionManagerForm? sessionManager;
    private PermissionPromptForm? permissionPrompt;
    private PermissionManagerForm? permissionManager;
    private PermissionRequest? activePermissionRequest;
    private DownloadsPopupForm? downloadsPopup;
    private DownloadNotificationForm? downloadNotification;
    private DownloadEntry? notificationDownloadEntry;
    private ExtensionsManagerForm? extensionsManager;
    private CoreWebView2Controller? extensionProfileController;
    private bool openingExtensionsManager;
    private bool downloadsButtonAutoVisible;
    private bool browserRuntimeUpdateAvailable;
    private MruSwitcherSnapshot<BrowserTab>? mruSnapshot;
    private bool openingMruSwitcher;
    private const int MaximumCachedFaviconHosts = 64;
    private readonly Dictionary<string, LinkedListNode<(string Host, Bitmap Icon)>> faviconCacheMap =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Host, Bitmap Icon)> faviconCacheOrder = new();
    private long faviconCacheVersion = 1;
    private bool findMatchCase;
    private long findOpenGeneration;
    private long findBoxEditGeneration;
    private bool suppressFindEditTracking;
    private readonly bool priorUncleanShutdown;
    private bool markCleanShutdownOnSave;
    private Control? focusBeforeCommandPalette;
    private bool? highContrastOverrideForTesting;

    public MainForm() : this(true)
    {
    }

    internal MainForm(
        bool startBrowserOnShown,
        BrowserState? previewState = null,
        string? startupAddress = null,
        BrowserMode mode = BrowserMode.Normal,
        string? webViewUserDataFolderOverride = null)
    {
        browserMode = mode;
        isPrivateMode = mode == BrowserMode.Private;
        stateStore = new BrowserStateStore(diagnosticsEnabled: !isPrivateMode);
        this.startupAddress = string.IsNullOrWhiteSpace(startupAddress) ? null : startupAddress;
        this.webViewUserDataFolderOverride = string.IsNullOrWhiteSpace(webViewUserDataFolderOverride)
            ? null
            : Path.GetFullPath(webViewUserDataFolderOverride);
        persistStateOnClose = startBrowserOnShown && !isPrivateMode;
        browserStartupCompleted = !startBrowserOnShown;
        state = previewState ?? (!isPrivateMode && startBrowserOnShown ? stateStore.Load() : new BrowserState());
        BrowserStateStore.NormalizeForPersistence(state);
        priorUncleanShutdown = !isPrivateMode
            && startBrowserOnShown
            && previewState is null
            && !state.CleanShutdown
            && state.OpenTabs.Any(BrowserPolicy.IsHttpUrl);
        if (!isPrivateMode && startBrowserOnShown && previewState is null && state.RecentDownloads.Count > 0)
        {
            HydrateRecentDownloadsFromState();
        }
        adBlockExceptionHosts = state.AdBlockExceptionHosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var closed in state.RecentlyClosed)
        {
            closedTabs.AddLast(closed);
        }
        systemMemory = SystemResourceInfo.CaptureMemory();
        searchProviderId = BrowserPolicy.NormalizeSearchProviderId(state.SearchProviderId);
        reduceWebsiteMotionEnabled = state.ReduceWebsiteMotionEnabled;
        ApplyResourceModeDefaults(state);
        memorySaverEnabled = state.MemorySaverEnabled;
        ultraLightEnabled = state.UltraLightModeEnabled ?? false;
        if (ultraLightEnabled) memorySaverEnabled = true;
        darkModeEnabled = state.DarkModeEnabled;
        adBlockEnabled = state.AdBlockEnabled;

        incognitoBadge = new IncognitoBadgeControl(toolTip);
        incognitoBadge.Click += (_, _) => ShowTransientStatus("Incognito mode: browsing history and data will not be saved");

        Text = isPrivateMode ? "Incognito \u2014 MishaWeb" : "MishaWeb";
        StartPosition = FormStartPosition.Manual;
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        MinimumSize = new Size(
            Math.Min(480, Math.Max(360, workingArea.Width - 20)),
            Math.Min(420, Math.Max(320, workingArea.Height - 20)));
        var defaultSize = new Size(
            Math.Max(MinimumSize.Width, Math.Min(1396, workingArea.Width - 40)),
            Math.Max(MinimumSize.Height, Math.Min(879, workingArea.Height - 40)));
        Bounds = new Rectangle(
            workingArea.Left + Math.Max(0, (workingArea.Width - defaultSize.Width) / 2),
            workingArea.Top + Math.Max(0, (workingArea.Height - defaultSize.Height) / 2),
            defaultSize.Width,
            defaultSize.Height);
        var savedWindowPlacement = isPrivateMode ? null : state.WindowPlacement;
        if (savedWindowPlacement is not null)
        {
            Bounds = ResolveRestoredWindowBounds(
                savedWindowPlacement,
                Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray(),
                MinimumSize);
        }
        normalBounds = Bounds;
        normalWindowState = FormWindowState.Normal;
        BackColor = NativeUiTheme.BrandWine;
        Padding = new Padding(1);
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? SystemIcons.Application;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        DoubleBuffered = true;

        BuildChrome();
        if (savedWindowPlacement is not null)
        {
            // AutoScale can resize a borderless form while its controls are created.
            // Reapply the persisted physical bounds before restoring its presentation.
            Bounds = normalBounds;
        }
        ApplySavedWindowPresentation(savedWindowPlacement);
        if (startBrowserOnShown)
        {
            if (adBlockEnabled)
            {
                _ = adBlocker.LoadAsync();
            }
            if (this.startupAddress is not null || state.OpenTabs.Any(BrowserPolicy.IsHttpUrl))
            {
                _ = GetOrCreateEnvironmentAsync();
            }
            Load += (_, _) => RunUiTask(StartBrowserAsync, "MishaWeb could not start");
        }
        HandleCreated += (_, _) => ScheduleExternalNavigationDrain();
        FormClosing += OnFormClosing;
        Deactivate += (_, _) =>
        {
            if (!openingMruSwitcher && mruSnapshot is not null && mruSwitcher?.Visible == true)
            {
                CancelMruSwitch();
            }
        };
    }

    private void ApplySavedWindowPresentation(SavedWindowPlacement? placement)
    {
        if (placement is null) return;

        switch (placement.Presentation)
        {
            case SavedWindowPresentation.Maximized:
                lastNonMinimizedWindowState = FormWindowState.Maximized;
                WindowState = FormWindowState.Maximized;
                break;
            case SavedWindowPresentation.FullScreen:
                normalWindowState = placement.RestoreMaximizedAfterFullScreen
                    ? FormWindowState.Maximized
                    : FormWindowState.Normal;
                lastNonMinimizedWindowState = normalWindowState;
                isFullScreen = true;
                findBarWasVisibleBeforeFullScreen = false;
                WindowState = FormWindowState.Normal;
                Padding = Padding.Empty;
                Bounds = Screen.FromRectangle(normalBounds).Bounds;
                UpdateChromeRowsForFullscreen();
                break;
            default:
                lastNonMinimizedWindowState = FormWindowState.Normal;
                WindowState = FormWindowState.Normal;
                break;
        }
    }

    internal static Rectangle ResolveRestoredWindowBounds(
        SavedWindowPlacement placement,
        IReadOnlyList<Rectangle> workingAreas,
        Size minimumSize)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(workingAreas);

        var requested = new Rectangle(
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height);
        var availableAreas = workingAreas
            .Where(area => area.Width > 0 && area.Height > 0)
            .ToArray();
        if (availableAreas.Length == 0) return requested;

        static long IntersectionArea(Rectangle first, Rectangle second)
        {
            var intersection = Rectangle.Intersect(first, second);
            return intersection.IsEmpty ? 0 : (long)intersection.Width * intersection.Height;
        }

        static double CenterDistanceSquared(Rectangle first, Rectangle second)
        {
            var deltaX = ((double)first.Left + first.Right) - ((double)second.Left + second.Right);
            var deltaY = ((double)first.Top + first.Bottom) - ((double)second.Top + second.Bottom);
            return (deltaX * deltaX) + (deltaY * deltaY);
        }

        var targetArea = availableAreas
            .OrderByDescending(area => IntersectionArea(requested, area))
            .ThenBy(area => CenterDistanceSquared(requested, area))
            .First();
        var minimumWidth = Math.Min(Math.Max(1, minimumSize.Width), targetArea.Width);
        var minimumHeight = Math.Min(Math.Max(1, minimumSize.Height), targetArea.Height);
        var width = Math.Clamp(requested.Width, minimumWidth, targetArea.Width);
        var height = Math.Clamp(requested.Height, minimumHeight, targetArea.Height);
        var x = Math.Clamp(requested.X, targetArea.Left, targetArea.Right - width);
        var y = Math.Clamp(requested.Y, targetArea.Top, targetArea.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    internal bool ReceiveExternalNavigation(string? address)
    {
        if (!string.IsNullOrWhiteSpace(address)
            && BrowserPolicy.ResolveAddress(address).Error is not null)
        {
            return false;
        }

        lock (externalNavigationSync)
        {
            if (isClosing || IsDisposed) return false;
            if (pendingExternalNavigations.Any(item =>
                    string.Equals(item, address, StringComparison.Ordinal)))
            {
                return true;
            }
            if (pendingExternalNavigations.Count >= MaximumPendingExternalNavigations)
            {
                return false;
            }
            pendingExternalNavigations.Enqueue(address);
        }

        ScheduleExternalNavigationDrain();
        return true;
    }

    internal bool HasPendingExternalNavigationForTesting(string? address)
    {
        lock (externalNavigationSync)
        {
            return pendingExternalNavigations.Any(item =>
                string.Equals(item, address, StringComparison.Ordinal));
        }
    }

    internal bool IsPrivateBrowserWindow => isPrivateMode;
    internal bool IsClosingForApplicationContext => Volatile.Read(ref isClosing);

    internal Program.BrowserRestartWindowState CaptureBrowserRestartWindowState() =>
        new(
            isPrivateMode,
            downloads.Count(item => !item.IsTerminal),
            tabs.Count(tab => !tab.IsClosed && tab.HasMediaCapturePermission),
            Math.Max(0, Volatile.Read(ref activeExtensionMutationCount)),
            CountNonRestorableTabsForProcessRestart(
                tabs.Where(tab => !tab.IsClosed).Select(tab => tab.Url),
                isPrivateMode));

    internal static int CountNonRestorableTabsForProcessRestart(
        IEnumerable<string> tabAddresses,
        bool isPrivateMode)
    {
        ArgumentNullException.ThrowIfNull(tabAddresses);
        if (isPrivateMode) return 0;
        return tabAddresses.Count(address =>
            !address.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase)
            && !BrowserPolicy.IsHttpUrl(address));
    }

    internal bool TryPersistSessionForBrowserProcessRestart()
    {
        if (isPrivateMode || !persistStateOnClose) return true;
        stateSaveTimer.Stop();
        markCleanShutdownOnSave = true;
        var saved = SaveStateNow();
        if (saved)
        {
            browserProcessRestartStateSaved = true;
        }
        else
        {
            markCleanShutdownOnSave = false;
        }
        return saved;
    }

    internal void TryEmergencySaveState()
    {
        if (isPrivateMode || !persistStateOnClose || IsDisposed) return;
        try
        {
            stateSaveTimer.Stop();
            PopulateStateFromPreferences();
            _ = stateStore.Save(state);
        }
        catch
        {
            // Crash-path state capture is strictly best-effort.
        }
    }

    internal void PrepareForBrowserProcessRestart()
    {
        browserProcessRestartApproved = true;
    }

    internal void CancelPreparedBrowserProcessRestart()
    {
        browserProcessRestartApproved = false;
        browserProcessRestartStateSaved = false;
        markCleanShutdownOnSave = false;
    }

    internal IDisposable BeginExtensionMutationForTesting() => BeginExtensionMutation();

    internal bool RequestCloseDuringExtensionMutationForTesting()
    {
        var args = new FormClosingEventArgs(CloseReason.UserClosing, cancel: false);
        OnFormClosing(this, args);
        return args.Cancel;
    }

    internal bool IsChromeStoreInstallCancellationRequestedForTesting =>
        chromeStoreInstallCancellation.IsCancellationRequested;

    internal bool IsCloseDeferredForExtensionMutationForTesting =>
        closeWhenExtensionOperationsFinish;

    private void ScheduleExternalNavigationDrain()
    {
        lock (externalNavigationSync)
        {
            if (isClosing
                || IsDisposed
                || Disposing
                || !IsHandleCreated
                || !browserStartupCompleted
                || externalNavigationDrainScheduled
                || pendingExternalNavigations.Count == 0)
            {
                return;
            }

            externalNavigationDrainScheduled = true;
        }

        if (!PostUi(() =>
            RunUiTask(DrainExternalNavigationsAsync, "Navigation handoff failed")))
        {
            lock (externalNavigationSync)
            {
                externalNavigationDrainScheduled = false;
            }
        }
    }

    private async Task DrainExternalNavigationsAsync()
    {
        try
        {
            while (true)
            {
                string? address;
                lock (externalNavigationSync)
                {
                    if (isClosing || pendingExternalNavigations.Count == 0) return;
                    address = pendingExternalNavigations.Dequeue();
                }

                if (WindowState == FormWindowState.Minimized)
                {
                    WindowState = lastNonMinimizedWindowState == FormWindowState.Maximized
                        ? FormWindowState.Maximized
                        : FormWindowState.Normal;
                }
                if (!Visible) Show();
                Activate();
                BringToFront();

                if (string.IsNullOrWhiteSpace(address)) continue;

                await OpenExternalAddressAsync(address!);
            }
        }
        finally
        {
            lock (externalNavigationSync)
            {
                externalNavigationDrainScheduled = false;
                if (isClosing) pendingExternalNavigations.Clear();
            }

            ScheduleExternalNavigationDrain();
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style &= ~NativeMethods.WsCaption;
            parameters.Style |= NativeMethods.WsThickFrame
                | NativeMethods.WsSysMenu
                | NativeMethods.WsMinimizeBox
                | NativeMethods.WsMaximizeBox;
            parameters.ExStyle &= ~(NativeMethods.WsExClientEdge
                | NativeMethods.WsExWindowEdge
                | NativeMethods.WsExDlgModalFrame);
            return parameters;
        }
    }

    internal BrowserMode Mode => browserMode;
    internal bool IsPrivateMode => isPrivateMode;

    private async Task StartBrowserAsync()
    {
        try
        {
            ShowTransientStatus("Starting browser\u2026", false);
            if (priorUncleanShutdown)
            {
                var webTabCount = state.OpenTabs.Count(BrowserPolicy.IsHttpUrl);
                var restorePrevious = NativeAlertDialog.Confirm(
                    this,
                    "Restore previous tabs?",
                    webTabCount == 1
                        ? "MishaWeb did not close cleanly last time. Restore your open tab from the previous session?"
                        : $"MishaWeb did not close cleanly last time. Restore your {webTabCount} open tabs from the previous session?",
                    "Restore tabs",
                    "Start fresh");
                if (!restorePrevious)
                {
                    foreach (var url in state.OpenTabs.Where(BrowserPolicy.IsHttpUrl).Reverse())
                    {
                        closedTabs.AddFirst(new ClosedTabEntry(HostFromUrl(url), url, DateTimeOffset.UtcNow));
                    }
                    while (closedTabs.Count > BrowserStateStore.MaximumRecentlyClosed) closedTabs.RemoveLast();
                    state.OpenTabs.Clear();
                    state.PinnedOpenTabCount = 0;
                    state.ActiveTabIndex = null;
                }
            }

            var restoredTabs = await RestoreOpenTabsFromStateAsync();
            if (isClosing) return;
            if (!restoredTabs)
            {
                var firstTab = await OpenNewTabAsync(StartPage.Url);
                if (isClosing) return;
                ShowTransientStatus(firstTab is null ? "The first tab needs attention" : "Ready");
            }
            else
            {
                ShowTransientStatus("Ready");
            }

            if (startupAddress is not null)
            {
                await OpenExternalAddressAsync(startupAddress);
                if (isClosing) return;
            }

            if (!isPrivateMode && persistStateOnClose)
            {
                markCleanShutdownOnSave = false;
                PopulateStateFromPreferences();
                state.CleanShutdown = false;
                _ = stateStore.QueueSave(state);
            }

            RefreshMemorySweepTimer();
        }
        catch (WebView2RuntimeNotFoundException error)
        {
            stateStore.Log("WebView2 Runtime was not found", error);
            NativeAlertDialog.ShowInfo(
                this,
                "WebView2 Runtime required",
                "MishaWeb needs the Microsoft Edge WebView2 Runtime. Install the Evergreen Runtime, then open MishaWeb again.");
            Close();
        }
        catch (Exception error)
        {
            stateStore.Log("Browser startup failed", error);
            var diagnosticNote = isPrivateMode
                ? "Private-window diagnostics were not written to disk."
                : "Details were saved to the MishaWeb diagnostics log.";
            NativeAlertDialog.ShowInfo(
                this,
                "MishaWeb could not start",
                $"MishaWeb could not open its browser profile.\n\n{error.Message}\n\n{diagnosticNote}");
            Close();
        }
        finally
        {
            lock (externalNavigationSync)
            {
                browserStartupCompleted = true;
            }
            ScheduleExternalNavigationDrain();
        }
    }

    private Task OpenExternalAddressAsync(string address)
    {
        return activeTab is { IsStartPage: true }
            ? NavigateActiveAsync(address)
            : OpenNewTabAsync(address);
    }

    private void TrackWindowPlacementChange()
    {
        if (!Visible || isClosing) return;

        if (!isFullScreen && !isDomFullScreen)
        {
            if (WindowState == FormWindowState.Normal)
            {
                if (IsUsableWindowBounds(Bounds)) normalBounds = Bounds;
                lastNonMinimizedWindowState = FormWindowState.Normal;
            }
            else if (WindowState == FormWindowState.Maximized)
            {
                if (IsUsableWindowBounds(RestoreBounds)) normalBounds = RestoreBounds;
                lastNonMinimizedWindowState = FormWindowState.Maximized;
            }
        }

        if (isInSizeMove)
        {
            pendingPlacementSaveAfterSizeMove = true;
            return;
        }
        ScheduleStateSave();
    }

    private SavedWindowPlacement CaptureCurrentWindowPlacement()
    {
        Rectangle bounds;
        SavedWindowPresentation presentation;
        var restoreMaximizedAfterFullScreen = false;

        if (isFullScreen)
        {
            bounds = normalBounds;
            presentation = SavedWindowPresentation.FullScreen;
            restoreMaximizedAfterFullScreen = normalWindowState == FormWindowState.Maximized;
        }
        else if (isDomFullScreen && hasDomFullScreenWindowState)
        {
            bounds = normalBoundsBeforeDomFullScreen;
            presentation = normalWindowStateBeforeDomFullScreen == FormWindowState.Maximized
                ? SavedWindowPresentation.Maximized
                : SavedWindowPresentation.Normal;
        }
        else
        {
            var effectiveState = WindowState == FormWindowState.Minimized
                ? lastNonMinimizedWindowState
                : WindowState;
            presentation = effectiveState == FormWindowState.Maximized
                ? SavedWindowPresentation.Maximized
                : SavedWindowPresentation.Normal;
            bounds = WindowState == FormWindowState.Normal ? Bounds : normalBounds;
        }

        if (!IsUsableWindowBounds(bounds)) bounds = normalBounds;
        if (!IsUsableWindowBounds(bounds) && state.WindowPlacement is { } previous)
        {
            bounds = new Rectangle(previous.X, previous.Y, previous.Width, previous.Height);
        }
        if (!IsUsableWindowBounds(bounds)) bounds = Bounds;

        return new SavedWindowPlacement(
            bounds.X,
            bounds.Y,
            Math.Max(320, bounds.Width),
            Math.Max(240, bounds.Height),
            presentation,
            restoreMaximizedAfterFullScreen);
    }

    private static bool IsUsableWindowBounds(Rectangle bounds) =>
        bounds.Width >= 320 && bounds.Height >= 240;

    internal SavedWindowPlacement CaptureWindowPlacementForTesting() =>
        CaptureCurrentWindowPlacement();

    private void PopulateStateFromPreferences()
    {
        state.SearchProviderId = searchProviderId;
        state.ReduceWebsiteMotionEnabled = reduceWebsiteMotionEnabled;
        state.MemorySaverEnabled = memorySaverEnabled;
        state.UltraLightModeEnabled = ultraLightEnabled;
        state.ResourceModeDefaultsVersion = 1;
        state.DarkModeEnabled = darkModeEnabled;
        state.AdBlockEnabled = adBlockEnabled;
        var restorableTabs = tabs
            .Where(tab => tab.IsStartPage || BrowserPolicy.IsHttpUrl(tab.Url))
            .Take(MaximumTabs)
            .ToList();
        state.OpenTabs = restorableTabs.Select(tab => tab.Url).ToList();
        state.PinnedOpenTabCount = restorableTabs
            .TakeWhile(tab => tab.IsPinned && !tab.IsStartPage)
            .Count();
        state.RecentlyClosed = isPrivateMode ? [] : closedTabs.ToList();
        if (!isPrivateMode)
        {
            state.WindowPlacement = CaptureCurrentWindowPlacement();
            SyncRecentDownloadsToState(scheduleSave: false);
            state.CleanShutdown = markCleanShutdownOnSave;
        }

        var activeIndex = activeTab is null ? -1 : restorableTabs.IndexOf(activeTab);
        state.ActiveTabIndex = activeIndex >= 0 ? activeIndex : null;
        if (!isPrivateMode && multiViewState.IsMultiViewActive)
        {
            var paneIndices = new List<int>(multiViewState.PaneCount);
            foreach (var paneTabId in multiViewState.GetVisibleTabIds())
            {
                var paneTab = FindTabById(paneTabId);
                var idx = paneTab is null ? -1 : restorableTabs.IndexOf(paneTab);
                if (idx >= 0 && !paneIndices.Contains(idx)) paneIndices.Add(idx);
            }
            state.MultiViewLayout = paneIndices.Count >= 2
                ? new SavedMultiViewLayout(
                    multiViewState.Preset.ToString(),
                    paneIndices.ToArray(),
                    Math.Clamp(multiViewState.FocusedPaneIndex, 0, paneIndices.Count - 1),
                    multiViewState.HorizontalSplitRatio,
                    multiViewState.VerticalSplitRatio)
                : null;
        }
        else
        {
            state.MultiViewLayout = null;
        }
    }

    private void SavePreferences()
    {
        PopulateStateFromPreferences();
        ScheduleStateSave();
    }

    private void ScheduleStateSave()
    {
        if (isClosing || !persistStateOnClose) return;
        stateSaveTimer.Stop();
        stateSaveTimer.Start();
    }

    private void QueueStateSaveNow()
    {
        stateSaveTimer.Stop();
        if (isClosing || !persistStateOnClose) return;

        PopulateStateFromPreferences();
        _ = stateStore.QueueSave(state);
    }

    private bool SaveStateNow()
    {
        stateSaveTimer.Stop();
        if (!persistStateOnClose) return true;
        PopulateStateFromPreferences();
        return stateStore.Save(state);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (isClosing) return;
        if (privateCleanupInProgress
            && e.CloseReason is CloseReason.UserClosing or CloseReason.ApplicationExitCall)
        {
            e.Cancel = true;
            return;
        }

        if (Volatile.Read(ref activeExtensionMutationCount) > 0)
        {
            e.Cancel = true;
            closeWhenExtensionOperationsFinish = true;
            try { chromeStoreInstallCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            ShowTransientStatus(
                "Waiting for the extension transaction to finish; MishaWeb will close automatically");
            if (extensionsManager is { IsDisposed: false, Visible: true }) extensionsManager.Activate();
            return;
        }
        closeWhenExtensionOperationsFinish = false;

        var activeDownloadCount = downloads.Count(item => !item.IsTerminal);
        if (!browserProcessRestartApproved
            && activeDownloadCount > 0
            && e.CloseReason is CloseReason.UserClosing or CloseReason.ApplicationExitCall)
        {
            var confirmCancelDownloads = NativeAlertDialog.Confirm(
                this,
                "Exit and cancel downloads?",
                activeDownloadCount == 1
                    ? "One download is still in progress. Exiting now will cancel it."
                    : $"{activeDownloadCount} downloads are still in progress. Exiting now will cancel them.",
                "Exit and cancel",
                "Keep downloading");
            if (!confirmCancelDownloads)
            {
                e.Cancel = true;
                ShowDownloadsPopup();
                ResetChromeStoreInstallCancellationAfterCanceledClose();
                return;
            }
        }

        if (!isPrivateMode) markCleanShutdownOnSave = true;
        var sessionSaved = !persistStateOnClose
            || (browserProcessRestartApproved && browserProcessRestartStateSaved)
            || SaveStateNow();
        if (!sessionSaved
            && e.CloseReason is CloseReason.UserClosing or CloseReason.ApplicationExitCall)
        {
            var confirmExitWithoutSave = NativeAlertDialog.Confirm(
                this,
                "Exit without saving this session?",
                "MishaWeb could not save your current tabs and window settings. Exiting anyway may restore an older session the next time you open MishaWeb.",
                "Exit anyway",
                "Stay open");
            if (!confirmExitWithoutSave)
            {
                markCleanShutdownOnSave = false;
                e.Cancel = true;
                ScheduleStateSave();
                ShowTransientStatus("Exit canceled \u2014 MishaWeb will retry saving your session");
                ResetChromeStoreInstallCancellationAfterCanceledClose();
                return;
            }
        }

        if (isPrivateMode
            && !privateCleanupCompleted
            && !browserProcessRestartApproved
            && e.CloseReason is CloseReason.UserClosing or CloseReason.ApplicationExitCall)
        {
            var privateCore = tabs.Select(item => item.Core).FirstOrDefault(item => item is not null);
            if (privateCore is not null)
            {
                foreach (var download in downloads) DetachDownloadHandlers(download);
                downloads.Clear();
                memoryTimer.Stop();
                stateSaveTimer.Stop();
                addressSuggestionTimer.Stop();
                findDebounceTimer.Stop();
                downloadNotification?.Dismiss();
                e.Cancel = true;
                privateCleanupInProgress = true;
                Hide();
                RunUiTask(
                    async () =>
                    {
                        try
                        {
                            await privateCore.Profile
                                .ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile)
                                .WaitAsync(TimeSpan.FromMilliseconds(750));
                        }
                        catch
                        {
                        }
                        finally
                        {
                            privateCleanupCompleted = true;
                            privateCleanupInProgress = false;
                            if (!isClosing && !IsDisposed && !Disposing) Close();
                        }
                    },
                    "Could not clear private browsing data");
                return;
            }
        }

        chromeStoreInstallCancellation.Cancel();
        lock (externalNavigationSync)
        {
            isClosing = true;
            pendingExternalNavigations.Clear();
        }
        CancelAllPendingPermissionRequests();
        DetachBrowserVersionAvailableHandler();
        foreach (var download in downloads) DetachDownloadHandlers(download);
        memoryTimer.Stop();
        stateSaveTimer.Stop();
        transientStatusTimer.Stop();
        addressSuggestionTimer.Stop();
        findDebounceTimer.Stop();
        statusUiTimer.Stop();
        downloadUiTimer.Stop();
        downloadGraceTimer.Stop();
        downloadNotification?.Dismiss();
        if (isPrivateMode)
        {
            downloads.Clear();
            var privateCore = tabs.Select(item => item.Core).FirstOrDefault(item => item is not null);
            if (privateCore is not null && !privateCleanupCompleted)
            {
                try { _ = privateCore.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile); }
                catch { }
            }
        }
        DisposeAllTabs();
    }

    private void DisposeAllTabs()
    {
        CancelTabDrag();
        StopFrameSetupQueue();
        activeTab = null;
        workerAdBlockFilterOwner = null;
        foreach (var tab in tabs) tab.IsClosed = true;
        foreach (var tab in tabs.ToArray())
        {
            try { RemoveAdBlockFiltering(tab); }
            catch (Exception error) { stateStore.Log("Could not release a closing tab filter", error); }
            tab.ReleaseTrackedFrames();
            tab.ChromeStoreInstallChannel = null;
            tab.Dispose();
        }
        tabs.Clear();
        InvalidateBrowserEnvironment();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Dispose can be called directly (for example, if a startup handoff
            // window fails before FormClosing). Mirror the runtime teardown here
            // so permission deferrals, download COM operations, and navigation
            // queues cannot keep an otherwise-disposed window alive.
            lock (externalNavigationSync)
            {
                isClosing = true;
                pendingExternalNavigations.Clear();
                externalNavigationDrainScheduled = false;
            }
            try { chromeStoreInstallCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            CancelAllPendingPermissionRequests();
            DetachBrowserVersionAvailableHandler();
            foreach (var download in downloads) DetachDownloadHandlers(download);
            downloads.Clear();
            installingChromeStoreExtensionIds.Clear();
            addressSuggestionPopup.ClearSuggestions();
            cachedStartPageLinks = null;
            AddressSuggestionEngine.ReleaseCache(state);
            StopFrameSetupQueue();
            if (tabs.Count > 0) DisposeAllTabs();
            else InvalidateBrowserEnvironment();
            ClearFaviconCache();
            SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
            memoryTimer.Dispose();
            stateSaveTimer.Dispose();
            transientStatusTimer.Dispose();
            addressSuggestionTimer.Dispose();
            liveSuggestionCts?.Cancel();
            liveSuggestionCts?.Dispose();
            liveSuggestionCts = null;
            findDebounceTimer.Dispose();
            statusUiTimer.Dispose();
            downloadUiTimer.Dispose();
            downloadGraceTimer.Dispose();
            appMenu.Dispose();
            tabMenu.Dispose();
            tabListMenu.Dispose();
            siteInfoMenu.Dispose();
            addressContextMenu.Dispose();
            multiViewMenu.Dispose();
            paneSwitchMenu.Dispose();
            commandPalette?.Dispose();
            mruSwitcher?.Dispose();
            sessionManager?.Dispose();
            permissionPrompt?.Dispose();
            permissionManager?.Dispose();
            downloadsPopup?.Dispose();
            downloadNotification?.Dispose();
            notificationDownloadEntry = null;
            extensionsManager?.Dispose();
            DisposeExtensionProfileController();
            chromeStoreInstallCancellation.Dispose();
            stateStore.Dispose();
        }
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing) toolTip.Dispose();
        }
    }

    private bool PostUi(Action action)
    {
        if (isClosing || IsDisposed || Disposing || !IsHandleCreated) return false;
        try
        {
            BeginInvoke(() =>
            {
                if (isClosing || IsDisposed || Disposing) return;
                action();
            });
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async void RunUiTask(Func<Task> action, string failureMessage)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            // Async event adapters can finish after an external Dispose even
            // when FormClosing did not run. Avoid turning the original failure
            // into a second ObjectDisposedException while reporting it.
            if (isClosing || IsDisposed || Disposing) return;
            stateStore.Log(failureMessage, error);
            ShowTransientStatus(failureMessage);
        }
    }

    private static string FriendlyNavigationError(CoreWebView2WebErrorStatus status)
    {
        return status switch
        {
            CoreWebView2WebErrorStatus.HostNameNotResolved => "Site not found",
            CoreWebView2WebErrorStatus.Disconnected => "No network connection",
            CoreWebView2WebErrorStatus.Timeout => "Connection timed out",
            CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
                or CoreWebView2WebErrorStatus.CertificateExpired
                or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors
                or CoreWebView2WebErrorStatus.CertificateRevoked
                or CoreWebView2WebErrorStatus.CertificateIsInvalid => "Certificate error",
            CoreWebView2WebErrorStatus.OperationCanceled => "Navigation canceled",
            _ => "Navigation failed"
        };
    }

    private static bool IsCertificateError(CoreWebView2WebErrorStatus status)
    {
        return status is CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
            or CoreWebView2WebErrorStatus.CertificateExpired
            or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors
            or CoreWebView2WebErrorStatus.CertificateRevoked
            or CoreWebView2WebErrorStatus.CertificateIsInvalid;
    }

    internal static bool IsAllowedExternalNavigation(string value, bool userInitiated)
    {
        if (!userInitiated
            || value.Length > BrowserPolicy.MaximumUrlLength
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Only OS-registered application protocols belong on this path. These
        // schemes are handled inside Chromium/WebView2 (or represent network
        // transports), so treating them as external could let a replacement
        // document retain the previous HTTPS address in MishaWeb's omnibox.
        return uri.Scheme.ToLowerInvariant() is not (
            "about"
            or "blob"
            or "chrome"
            or "chrome-distiller"
            or "chrome-error"
            or "chrome-extension"
            or "chrome-native"
            or "chrome-search"
            or "chrome-untrusted"
            or "cid"
            or "content"
            or "data"
            or "devtools"
            or "edge"
            or "file"
            or "filesystem"
            or "ftp"
            or "http"
            or "https"
            or "isolated-app"
            or "javascript"
            or "ms-browser-extension"
            or "urn"
            or "view-source"
            or "vbscript"
            or "webview"
            or "ws"
            or "wss");
    }

    private static bool IsTrustedExtensionPage(string value, string? extensionId)
    {
        return BrowserExtensions.ParseChromeWebStoreExtensionId(extensionId) is { } normalizedId
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals("chrome-extension", StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals(normalizedId, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static string HostFromUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.IdnHost
            : "New tab";
    }

    private static string TrimMenuText(string text, int maximumLength = 46)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Untitled";
        return TextSafety.TruncateWithEllipsis(
            TextSafety.RemoveUnpairedSurrogates(text),
            maximumLength);
    }

    private static void ApplyToolbarButtonStyle(
        Button button,
        string text,
        string tooltip,
        string accessibleName,
        float fontSize)
    {
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(2, 0, 2, 0);
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = NativeUiTheme.Hover;
        button.FlatAppearance.MouseDownBackColor = NativeUiTheme.Pressed;
        button.BackColor = ToolbarColor;
        button.ForeColor = PageTextColor;
        button.Font = NativeUiTheme.GetSharedFont("Segoe UI Semibold", fontSize);
        button.Cursor = Cursors.Hand;
        button.TabStop = true;
        button.AccessibleName = accessibleName;
        button.AccessibleDescription = tooltip;
    }

    private void ConfigureTitleBarButton(
        Button button,
        string text,
        string tooltip,
        string accessibleName,
        float fontSize)
    {
        button.Dock = DockStyle.Fill;
        button.Margin = Padding.Empty;
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = NativeUiTheme.Surface;
        button.FlatAppearance.MouseDownBackColor = NativeUiTheme.SurfaceRaised;
        button.BackColor = ChromeColor;
        button.ForeColor = PageTextColor;
        button.Font = NativeUiTheme.GetSharedFont("Segoe UI", fontSize);
        button.Cursor = Cursors.Hand;
        button.TabStop = true;
        button.AccessibleRole = AccessibleRole.PushButton;
        button.AccessibleName = accessibleName;
        button.AccessibleDescription = tooltip;
        toolTip.SetToolTip(button, tooltip);
    }

    private void ConfigureOmniboxButton(
        Button button,
        string text,
        string tooltip,
        string accessibleName,
        float fontSize)
    {
        ConfigureToolbarButton(button, text, tooltip, accessibleName, fontSize);
        button.Margin = Padding.Empty;
        button.BackColor = OmniboxColor;
        button.FlatAppearance.MouseOverBackColor = NativeUiTheme.Hover;
        button.FlatAppearance.MouseDownBackColor = NativeUiTheme.Pressed;
    }

    private void AddToolbarColumn(float width)
    {
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
    }

    private void ConfigureToolbarButton(Button button, string text, string tooltip, string accessibleName, float fontSize)
    {
        ApplyToolbarButtonStyle(button, text, tooltip, accessibleName, fontSize);
        toolTip.SetToolTip(button, tooltip);
    }

    private static ToolStripMenuItem AddMenuItem(
        ToolStripDropDown menu,
        string text,
        string shortcut,
        Action action)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

}
