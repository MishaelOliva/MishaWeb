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

public sealed class MainForm : Form
{
    private const int MaximumTabs = 128;
    private const int MaximumActiveDownloads = 64;
    private const int MaximumPendingExternalNavigations = 16;
    private const int MaximumPendingPermissionRequests = 16;
    private const int MaximumTrackedFramesPerTab = 256;
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
    private const int MaximumToolbarStatusWidth = 150;
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
    private static readonly Font OverlayTitleFont = new("Segoe UI Semibold", 15f);
    private static readonly Font OverlayDetailFont = new("Segoe UI", 9.5f);
    private static readonly Font OverlayActionFont = new("Segoe UI Semibold", 9f);
    private static readonly Font TabTitleFont = new("Segoe UI", 9f);
    private static readonly byte[] TransparentGif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
    private static readonly byte[] YouTubeScriptStubBytes =
        "window.adsbygoogle = window.adsbygoogle || []; window.adsbygoogle.loaded = true; window.googletag = window.googletag || { cmd: [], apiReady: true };"u8.ToArray();
    private static readonly CoreWebView2WebResourceRequestSourceKinds DocumentRequestSources =
        CoreWebView2WebResourceRequestSourceKinds.Document;
    private static readonly CoreWebView2WebResourceRequestSourceKinds WorkerRequestSources =
        CoreWebView2WebResourceRequestSourceKinds.SharedWorker
        | CoreWebView2WebResourceRequestSourceKinds.ServiceWorker;
    private static readonly string[] RequestSourceHeaderNames = ["Referer", "Origin"];
    private static readonly Font TabCloseFont = new("Segoe UI", 10.5f);

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
    private readonly Button newTabButton = new();
    private readonly Button tabListButton = new();
    private readonly ChromeButton menuButton = new();
    private readonly WindowCaptionButton minimizeButton = new();
    private readonly WindowCaptionButton maximizeButton = new();
    private readonly WindowCaptionButton closeWindowButton = new();
    private readonly Button findPreviousButton = new();
    private readonly Button findNextButton = new();
    private readonly Button closeFindButton = new();
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
    private IReadOnlyList<StartPageLink>? cachedStartPageLinks;
    private bool isStartPageLinksDirty = true;
    private DateTimeOffset nextLifecycleScanAt = DateTimeOffset.MinValue;
    private FormWindowState normalWindowState;
    private Rectangle normalBounds;
    private FormWindowState lastNonMinimizedWindowState = FormWindowState.Normal;
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
    private ExtensionsManagerForm? extensionsManager;
    private CoreWebView2Controller? extensionProfileController;
    private bool openingExtensionsManager;
    private bool downloadsButtonAutoVisible;
    private bool browserRuntimeUpdateAvailable;
    private MruSwitcherSnapshot<BrowserTab>? mruSnapshot;
    private bool openingMruSwitcher;
    private Control? focusBeforeCommandPalette;
    private bool? highContrastOverrideForTesting;

    public MainForm() : this(true)
    {
    }

    internal MainForm(
        bool startBrowserOnShown,
        BrowserState? previewState = null,
        string? startupAddress = null,
        BrowserMode mode = BrowserMode.Normal)
    {
        browserMode = mode;
        isPrivateMode = mode == BrowserMode.Private;
        stateStore = new BrowserStateStore(diagnosticsEnabled: !isPrivateMode);
        this.startupAddress = string.IsNullOrWhiteSpace(startupAddress) ? null : startupAddress;
        persistStateOnClose = startBrowserOnShown && !isPrivateMode;
        browserStartupCompleted = !startBrowserOnShown;
        state = previewState ?? (!isPrivateMode && startBrowserOnShown ? stateStore.Load() : new BrowserState());
        BrowserStateStore.NormalizeForPersistence(state);
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
        var saved = SaveStateNow();
        if (saved) browserProcessRestartStateSaved = true;
        return saved;
    }

    internal void PrepareForBrowserProcessRestart()
    {
        browserProcessRestartApproved = true;
    }

    internal void CancelPreparedBrowserProcessRestart()
    {
        browserProcessRestartApproved = false;
        browserProcessRestartStateSaved = false;
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
                || !IsHandleCreated
                || !browserStartupCompleted
                || externalNavigationDrainScheduled
                || pendingExternalNavigations.Count == 0)
            {
                return;
            }

            externalNavigationDrainScheduled = true;
        }

        try
        {
            BeginInvoke(new Action(() =>
                RunUiTask(DrainExternalNavigationsAsync, "Navigation handoff failed")));
        }
        catch (Exception error)
        {
            lock (externalNavigationSync)
            {
                externalNavigationDrainScheduled = false;
            }

            if (!isClosing && !IsDisposed)
            {
                stateStore.Log("Could not schedule navigation handoff", error);
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
                    WindowState = FormWindowState.Normal;
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyBorderlessResizableStyle(Handle);
        var palette = ResolveChromeColorPolicy(IsHighContrastActive);
        NativeMethods.TryEnableModernWindowFrame(
            Handle,
            palette.ChromeSurface,
            palette.WindowFrameBorder,
            palette.WindowFrameText);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateChromeRowsForFullscreen();
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

            if (IsPointInsideControl(maximizeButton, screenPoint))
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
        lastUserInteractionUtc = DateTime.UtcNow;
        if (isAppIdleLowPowerActive)
        {
            isAppIdleLowPowerActive = false;
            if (activeTab is not null && !isWindowMinimized)
            {
                ApplyLiveTabMemoryTarget(activeTab, foreground: true);
            }
        }
    }

    private void CheckApplicationInactivity()
    {
        if (isClosing || isWindowMinimized || activeTab is null) return;
        var idleDuration = DateTime.UtcNow - lastUserInteractionUtc;
        if (idleDuration >= TimeSpan.FromMinutes(AppIdleThresholdMinutes) && !isAppIdleLowPowerActive)
        {
            isAppIdleLowPowerActive = true;
            ApplyLiveTabMemoryTarget(activeTab, foreground: false);
            TrimAllProcessMemory();
        }
    }

    private int GetResizeHitTest(Point point)
    {
        var frameX = NativeMethods.GetResizeFrameThickness(DeviceDpi, horizontal: true);
        var frameY = NativeMethods.GetResizeFrameThickness(DeviceDpi, horizontal: false);
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
        if (clientPoint.Y < 0 || clientPoint.Y >= titleBar.Bottom) return false;
        if (IsPointInsideControl(newTabButton, screenPoint)
            || IsPointInsideControl(tabListButton, screenPoint)
            || IsPointInsideControl(minimizeButton, screenPoint)
            || IsPointInsideControl(maximizeButton, screenPoint)
            || IsPointInsideControl(closeWindowButton, screenPoint))
        {
            return false;
        }

        if (IsPointInsideControl(tabStrip, screenPoint))
        {
            // Keep the actual tab headers in the client area so their mouse
            // events can start a reorder drag. Only the unused strip space
            // should behave like a draggable window caption.
            return !tabs.Any(tab => IsPointInsideControl(tab.Header, screenPoint));
        }
        return true;
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
        info.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
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
        foreach (var button in new[] { findPreviousButton, findNextButton, closeFindButton })
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
            control.BackColor = palette.FieldSurface;
            control.ForeColor = palette.FieldText;
        }
        addressBar.BackColor = palette.FieldSurface;
        addressBar.ForeColor = palette.FieldText;
        findBox.BackColor = palette.FieldSurface;
        findBox.ForeColor = palette.FieldText;
        statusLabel.ForeColor = palette.MutedText;
        findMatchLabel.ForeColor = palette.MutedText;

        menuRenderer.HighContrast = palette.HighContrast;
        foreach (var menu in new[] { appMenu, tabMenu, tabListMenu, siteInfoMenu, addressContextMenu })
        {
            ApplyMenuPalette(menu, palette);
        }

        foreach (var tab in tabs)
        {
            tab.Header.HighContrast = palette.HighContrast;
            UpdateTabHeader(tab);
        }
        UpdateSiteInfo();
        UpdateFavoriteButton();

        if (IsHandleCreated)
        {
            NativeMethods.TryEnableModernWindowFrame(
                Handle,
                palette.ChromeSurface,
                palette.WindowFrameBorder,
                palette.WindowFrameText);
        }
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
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        BuildTabStrip();
        BuildTitleBar();
        BuildToolbar();
        BuildFindBar();
        BuildMenus();

        pageHost.Dock = DockStyle.Fill;
        pageHost.Margin = Padding.Empty;
        TrySetBackColor(pageHost, Color.Transparent, ChromeColor);
        pageHost.Resize += (_, _) => EnsureTabHostLayout(activeTab);

        rootLayout.Controls.Add(titleBar, 0, 0);
        rootLayout.Controls.Add(toolbar, 0, 1);
        rootLayout.Controls.Add(findBar, 0, 2);
        rootLayout.Controls.Add(pageHost, 0, 3);
        Controls.Add(rootLayout);
        Controls.Add(addressSuggestionPopup);
        addressSuggestionPopup.BringToFront();

        WireChromeEvents();
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
        titleBar.Padding = new Padding(6, 4, 0, 4);
        titleBar.BackColor = ChromeColor;
        titleBar.RowCount = 1;
        titleBar.ColumnCount = 7;
        titleBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
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
        appMark.Font = new Font("Segoe UI Semibold", 10f);
        appMark.PrivateMode = isPrivateMode;

        ConfigureTitleBarButton(
            newTabButton,
            "+",
            isPrivateMode ? "New incognito tab (Ctrl+T)" : "New tab (Ctrl+T)",
            isPrivateMode ? "New incognito tab" : "New tab",
            14f);
        newTabButton.Dock = DockStyle.None;
        newTabButton.Size = new Size(32, 32);
        ConfigureTitleBarButton(tabListButton, "\u2304", "All tabs", "All tabs", 11f);
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
        tabDropIndicator.Size = new Size(2, 24);
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
        toolbar.Padding = new Padding(10, 5, 10, 5);
        toolbar.BackColor = ToolbarColor;
        toolbar.RowCount = 1;
        toolbar.ColumnCount = 8;
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddToolbarColumn(40);
        AddToolbarColumn(40);
        AddToolbarColumn(40);
        AddToolbarColumn(40);
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddToolbarColumn(42);
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

        backButton.IconKind = ChromeIconKind.Back;
        forwardButton.IconKind = ChromeIconKind.Forward;
        reloadButton.IconKind = ChromeIconKind.Reload;
        homeButton.IconKind = ChromeIconKind.Home;
        menuButton.IconKind = ChromeIconKind.Menu;
        siteInfoButton.IconKind = ChromeIconKind.SiteInfo;
        favoriteButton.IconKind = ChromeIconKind.Favorite;
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
        addressBar.Font = new Font("Segoe UI", 10.5f);
        addressBar.MaxLength = BrowserPolicy.MaximumUrlLength;
        addressBar.PlaceholderText = $"Search with {BrowserPolicy.GetSearchProviderName(searchProviderId)} or enter address{(isPrivateMode ? " in Incognito" : string.Empty)}";
        addressBar.AccessibleName = "Address and search bar";
        addressBar.AccessibleDescription = "Enter a website address or search terms";
        addressContextMenu.ShowImageMargin = false;
        addressContextMenu.Renderer = menuRenderer;
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
        omniboxPanel.CornerRadius = 11;
        omniboxPanel.Padding = new Padding(1);

        var omniboxLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(3, 0, 3, 0),
            BackColor = OmniboxColor,
            ColumnCount = 4,
            RowCount = 1
        };
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
        statusLabel.TextAlign = ContentAlignment.MiddleRight;
        statusLabel.ForeColor = MutedTextColor;
        statusLabel.Font = new Font("Segoe UI", 8.25f);
        statusLabel.AutoEllipsis = true;
        statusLabel.AutoSize = true;
        statusLabel.MaximumSize = new Size(150, 0);
        statusLabel.AccessibleName = "Browser status";
        statusLabel.AccessibleRole = AccessibleRole.StatusBar;

        toolbar.Controls.Add(backButton, 0, 0);
        toolbar.Controls.Add(forwardButton, 1, 0);
        toolbar.Controls.Add(reloadButton, 2, 0);
        toolbar.Controls.Add(homeButton, 3, 0);
        toolbar.Controls.Add(omniboxPanel, 4, 0);
        toolbar.Controls.Add(statusLabel, 5, 0);
        toolbar.Controls.Add(downloadsButton, 6, 0);
        toolbar.Controls.Add(menuButton, 7, 0);
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
        findBar.RowCount = 1;
        findBar.ColumnCount = 5;
        findBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        findBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        findBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        findBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        findBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));

        findBox.Dock = DockStyle.Fill;
        findBox.Margin = new Padding(0, 0, 6, 0);
        findBox.BorderStyle = BorderStyle.FixedSingle;
        findBox.BackColor = OmniboxColor;
        findBox.ForeColor = PageTextColor;
        findBox.Font = new Font("Segoe UI", 10f);
        findBox.PlaceholderText = "Find on this page";
        findBox.AccessibleName = "Find on page";

        findMatchLabel.Dock = DockStyle.Fill;
        findMatchLabel.TextAlign = ContentAlignment.MiddleCenter;
        findMatchLabel.ForeColor = MutedTextColor;
        findMatchLabel.Font = new Font("Segoe UI", 8.5f);
        findMatchLabel.Text = string.Empty;
        findMatchLabel.AccessibleName = "Find match count";

        ConfigureToolbarButton(findPreviousButton, "\u2191", "Previous match (Shift+F3)", "Previous match", 11f);
        ConfigureToolbarButton(findNextButton, "\u2193", "Next match (F3)", "Next match", 11f);
        ConfigureToolbarButton(closeFindButton, "\u00D7", "Close find", "Close find", 13f);

        findBar.Controls.Add(findBox, 0, 0);
        findBar.Controls.Add(findMatchLabel, 1, 0);
        findBar.Controls.Add(findPreviousButton, 2, 0);
        findBar.Controls.Add(findNextButton, 3, 0);
        findBar.Controls.Add(closeFindButton, 4, 0);
        findBar.Resize += (_, _) => UpdateFindBarLayout();
        findBar.Visible = false;
    }

    private void BuildMenus()
    {
        appMenu.ShowImageMargin = false;
        appMenu.Renderer = menuRenderer;
        appMenu.BackColor = ToolbarColor;
        appMenu.ForeColor = PageTextColor;
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
        AddMenuItem(appMenu, "Saved items\u2026", string.Empty, ShowSavedItemsDialog);
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
        appMenu.Items.Add(new ToolStripSeparator());
        AddMenuItem(appMenu, "Downloads", "Ctrl+J", ShowDownloadsPopup);
        AddMenuItem(appMenu, "Find on page", "Ctrl+F", ShowFindBar);
        appMenu.Items.Add(zoomMenu);
        AddMenuItem(appMenu, "Print page\u2026", "Ctrl+P", PrintPage);
        AddMenuItem(appMenu, "Capture page as PNG\u2026", "Ctrl+Shift+S", () =>
            RunUiTask(CapturePageAsync, "Could not capture the page"));
        appMenu.Items.Add(new ToolStripSeparator());
        AddMenuItem(appMenu, "Clear browsing data\u2026", string.Empty, () => RunUiTask(ClearBrowsingDataAsync, "Could not clear browsing data"));
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
        tabListMenu.ShowImageMargin = false;
        tabListMenu.Renderer = menuRenderer;
        tabListMenu.BackColor = ToolbarColor;
        tabListMenu.ForeColor = PageTextColor;
        siteInfoMenu.ShowImageMargin = false;
        siteInfoMenu.Renderer = menuRenderer;
        siteInfoMenu.BackColor = ToolbarColor;
        siteInfoMenu.ForeColor = PageTextColor;
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
        backButton.Click += (_, _) => GoBack();
        forwardButton.Click += (_, _) => GoForward();
        reloadButton.Click += (_, _) => ReloadOrStop();
        homeButton.Click += (_, _) => ShowStartPage(activeTab);
        siteInfoButton.Click += (_, _) => ShowSiteInformation();
        favoriteButton.Click += (_, _) => ToggleFavorite();
        newTabButton.Click += (_, _) => RunUiTask(() => OpenNewTabAsync(StartPage.Url), "Could not open a tab");
        tabListButton.Click += (_, _) => ShowTabListMenu();
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
            BeginInvoke(() =>
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

        findBox.KeyDown += FindBoxOnKeyDown;
        findBox.TextChanged += (_, _) =>
        {
            if (!findBar.Visible) return;
            findDebounceTimer.Stop();
            findDebounceTimer.Start();
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
        SizeChanged += (_, _) => UpdateWindowControls();
        SizeChanged += (_, _) => PositionAddressSuggestions();
        SizeChanged += (_, _) => TrackWindowPlacementChange();
        SizeChanged += (_, _) => RunUiTask(HandleWindowStateChangeAsync, "Could not change tab power state");
        LocationChanged += (_, _) => TrackWindowPlacementChange();
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    private async Task StartBrowserAsync()
    {
        try
        {
            ShowTransientStatus("Starting browser\u2026", false);
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

            RefreshMemorySweepTimer();
        }
        catch (WebView2RuntimeNotFoundException error)
        {
            stateStore.Log("WebView2 Runtime was not found", error);
            MessageBox.Show(
                "MishaWeb needs the Microsoft Edge WebView2 Runtime. Install the Evergreen Runtime, then open MishaWeb again.",
                "WebView2 Runtime required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
        catch (Exception error)
        {
            stateStore.Log("Browser startup failed", error);
            var diagnosticNote = isPrivateMode
                ? "Private-window diagnostics were not written to disk."
                : "Details were saved to the MishaWeb diagnostics log.";
            MessageBox.Show(
                $"MishaWeb could not open its browser profile.\n\n{error.Message}\n\n{diagnosticNote}",
                "MishaWeb could not start",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
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

    private void RefreshMemorySweepTimer()
    {
        if (isClosing) return;
        var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
        if (mode == TabLifecycleMode.Off)
        {
            memoryTimer.Stop();
            return;
        }
        var canWatchPendingInitialization = tabs.Any(tab =>
            !tab.IsClosed && tab.IsInitializing);
        if (environment is null && !canWatchPendingInitialization)
        {
            memoryTimer.Stop();
            return;
        }

        var hasCandidates = tabs.Any(tab =>
            !tab.IsClosed
            && (tab.Core is not null || tab.IsInitializing)
            && !tab.IsAudible
            && !IsBackgroundProtected(tab)
            && (!IsProtectedResidentTab(tab)
                || (mode == TabLifecycleMode.Ultra
                    && !tab.IsSuspended
                    && !tab.IsLoading
                    && !tab.IsInitializing
                    && tab.ConsecutiveSuspendFailures
                        < TabLifecyclePolicy.SuspendFailureFallbackThreshold))
            && (tab != activeTab || isWindowMinimized)
            && tab.ActiveDownloads == 0);
        if (!hasCandidates)
        {
            var hasLiveWebViews = environment is not null && tabs.Any(tab => !tab.IsClosed && tab.Core is not null);
            if (!hasLiveWebViews)
            {
                memoryTimer.Stop();
                return;
            }
            if (memoryTimer.Interval != IdleMemoryMaintenanceIntervalMs)
            {
                memoryTimer.Interval = IdleMemoryMaintenanceIntervalMs;
            }
            if (!memoryTimer.Enabled) memoryTimer.Start();
            return;
        }

        nextLifecycleScanAt = DateTimeOffset.MinValue;
        var scanInterval = mode == TabLifecycleMode.Standard
            ? StandardMemoryScanIntervalMs
            : MemoryScanIntervalMs;
        if (memoryTimer.Interval != scanInterval) memoryTimer.Interval = scanInterval;
        if (!memoryTimer.Enabled) memoryTimer.Start();
    }

    private async Task<bool> RestoreOpenTabsFromStateAsync()
    {
        if (state.OpenTabs.Count == 0) return false;

        var targetActiveIndex = Math.Min(
            Math.Max(state.ActiveTabIndex.GetValueOrDefault(0), 0),
            state.OpenTabs.Count - 1);
        var maxRestoreCount = Math.Min(state.OpenTabs.Count, MaximumTabs);
        var visibleTab = activeTab;
        var restoredCount = 0;

        restoringSession = true;
        tabStrip.SuspendLayout();
        pageHost.SuspendLayout();
        try
        {
            for (var index = 0; index < maxRestoreCount; index++)
            {
                if (isClosing) return restoredCount > 0;

                var restoredTab = await OpenNewTabAsync(
                    state.OpenTabs[index],
                    activate: false,
                    isPinned: index < state.PinnedOpenTabCount);
                if (restoredTab is null) continue;

                restoredCount++;
                if (index == targetActiveIndex)
                {
                    visibleTab = restoredTab;
                }
            }
        }
        finally
        {
            restoringSession = false;
            pageHost.ResumeLayout(performLayout: true);
            tabStrip.ResumeLayout(performLayout: true);
            RefreshNativeStartPages(refreshLinks: true);
            ResizeTabHeaders();
            RefreshMemorySweepTimer();
        }

        if (tabs.Count > 0)
        {
            visibleTab ??= tabs[0];
            ActivateTab(visibleTab, focusPage: false, restoreDiscarded: false);
            mruTabs.Reset(new[] { visibleTab }.Concat(tabs.Where(item => item != visibleTab)));
            if (!visibleTab.IsStartPage && visibleTab.IsDiscarded)
            {
                await RestoreDiscardedTabAsync(visibleTab, focusPage: false);
            }
        }

        return restoredCount > 0;
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
        addressSuggestionPopup.SetSuggestions(AddressSuggestionEngine.GetSuggestions(query, state));
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

    private static CoreWebView2EnvironmentOptions CreateEnvironmentOptions()
    {
        var extraArgs = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
        var baseArgs =
            "--autoplay-policy=no-user-gesture-required "
            + "--enable-gpu-rasterization "
            + "--enable-zero-copy "
            + "--enable-features=CanvasOopRasterization,ParallelDownloading "
            + "--disable-features=BackForwardCache,SpareRendererForSitePerProcess,PeriodicBackgroundSync,AudioServiceOutOfProcess,AudioServiceSandbox "
            + "--enable-quic "
            + "--enable-hardware-overlays=single-fullscreen,single-on-top";

        var mergedArgs = string.IsNullOrWhiteSpace(extraArgs)
            ? baseArgs
            : (extraArgs.Contains("--autoplay-policy", StringComparison.OrdinalIgnoreCase)
                ? $"{baseArgs} {extraArgs}".Trim()
                : $"--autoplay-policy=no-user-gesture-required {baseArgs} {extraArgs}".Trim());

        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", mergedArgs);

        return new()
        {
            EnableTrackingPrevention = true,
            AreBrowserExtensionsEnabled = true,
            ExclusiveUserDataFolderAccess = true,
            AdditionalBrowserArguments = mergedArgs
        };
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        WebView2LoaderBootstrap.EnsureLoaded();
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MishaWeb",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);
        var options = CreateEnvironmentOptions();
        return await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
    }

    private Task<CoreWebView2Environment> GetOrCreateEnvironmentAsync()
    {
        if (environment is not null) return Task.FromResult(environment);
        if (environmentTask is null)
        {
            var generation = ++environmentGeneration;
            environmentTask = CreateEnvironmentAndRememberAsync(generation);
        }
        return environmentTask;
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAndRememberAsync(long generation)
    {
        try
        {
            var created = await CreateEnvironmentAsync();
            if (!isClosing && generation == environmentGeneration)
            {
                environment = created;
                AttachBrowserVersionAvailableHandler(created, generation);
                RefreshMemorySweepTimer();
            }
            return created;
        }
        catch
        {
            if (generation == environmentGeneration)
            {
                environment = null;
                environmentTask = null;
            }
            throw;
        }
    }

    private void InvalidateBrowserEnvironment()
    {
        DetachBrowserVersionAvailableHandler();
        var manager = extensionsManager;
        extensionsManager = null;
        try { manager?.Dispose(); }
        catch { }
        DisposeExtensionProfileController();
        environmentGeneration++;
        environment = null;
        environmentTask = null;
        memoryTimer.Stop();
    }

    private void AttachBrowserVersionAvailableHandler(
        CoreWebView2Environment created,
        long generation)
    {
        DetachBrowserVersionAvailableHandler();
        EventHandler<object> handler = (_, _) =>
            QueueBrowserRuntimeUpdateAvailable(created, generation);
        created.NewBrowserVersionAvailable += handler;
        browserVersionEventEnvironment = created;
        browserVersionAvailableHandler = handler;
    }

    private void DetachBrowserVersionAvailableHandler()
    {
        var subscribedEnvironment = browserVersionEventEnvironment;
        var handler = browserVersionAvailableHandler;
        browserVersionEventEnvironment = null;
        browserVersionAvailableHandler = null;
        if (subscribedEnvironment is null || handler is null) return;
        try { subscribedEnvironment.NewBrowserVersionAvailable -= handler; }
        catch { }
    }

    private void QueueBrowserRuntimeUpdateAvailable(
        CoreWebView2Environment sourceEnvironment,
        long generation)
    {
        void Publish()
        {
            if (isClosing
                || IsDisposed
                || generation != environmentGeneration
                || !ReferenceEquals(environment, sourceEnvironment))
            {
                return;
            }

            browserRuntimeUpdateAvailable = true;
            restartBrowserForUpdateMenuItem.Visible = true;
            ShowTransientStatus(IsBrowserEngineRestartUnsafe()
                ? "Browser engine update ready — finish calls and downloads, then restart"
                : "Browser engine update ready — restart from the menu");
        }

        try
        {
            if (!IsHandleCreated || IsDisposed) return;
            if (InvokeRequired) BeginInvoke((Action)Publish);
            else Publish();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private bool IsBrowserEngineRestartUnsafe() =>
        downloads.Any(item => !item.IsTerminal)
        || tabs.Any(tab => !tab.IsClosed && tab.HasMediaCapturePermission);

    private async Task<BrowserTab?> OpenNewTabAsync(
        string input,
        bool activate = true,
        bool waitForPopupNavigation = false,
        bool canScriptClose = false,
        bool isPinned = false,
        string? trustedExtensionId = null)
    {
        if (isClosing) return null;
        if (tabs.Count >= MaximumTabs)
        {
            ShowTransientStatus($"Tab limit reached ({MaximumTabs})");
            return null;
        }

        var resolution = ResolveNewTabInput(
            input,
            searchProviderId,
            waitForPopupNavigation,
            IsTrustedExtensionPage(input, trustedExtensionId));
        if (resolution.Error is not null)
        {
            ShowTransientStatus(resolution.Error);
            return null;
        }

        var initialUrl = resolution.IsStartPage ? StartPage.Url : resolution.Url!;
        var tab = CreateBrowserTab(initialUrl, canScriptClose);
        tab.DeferResourceFilteringUntilPopupAttached = waitForPopupNavigation;
        tab.AllowedPopupBootstrapUrl = waitForPopupNavigation
            && BrowserPolicy.IsPopupBootstrapUrl(initialUrl)
            ? initialUrl
            : null;
        tab.ExtensionOriginId = trustedExtensionId;
        tab.IsPinned = isPinned && !resolution.IsStartPage;
        tab.KeepAwake = IsKeepAwakeHost(initialUrl);
        tabs.Add(tab);
        tabStrip.Controls.Add(tab.Header);
        if (tab.IsPinned) ReorderTabForPinState(tab);
        if (!restoringSession)
        {
            RefreshNativeStartPages();
            ResizeTabHeaders();
            RefreshMemorySweepTimer();
        }

        if (activate) ActivateTab(tab, focusPage: false);

        if (resolution.IsStartPage && !waitForPopupNavigation)
        {
            if (!activate)
            {
                tab.Overlay.Visible = false;
                UpdateTabHeader(tab);
            }
            else if (activeTab == tab && IsHandleCreated)
            {
                BeginInvoke(FocusStartPageOrAddressBar);
            }
            return tab;
        }

        if (!activate && !waitForPopupNavigation)
        {
            MarkTabDiscarded(tab, "Waiting in the background");
            return tab;
        }

        var navigationRequestId = waitForPopupNavigation ? 0 : ++tab.NavigationRequestId;
        var initialized = await InitializeTabAsync(
            tab,
            requireActive: !waitForPopupNavigation);
        if (!initialized || isClosing || tab.IsClosed)
        {
            if (!waitForPopupNavigation)
            {
                PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
            }
            return tab;
        }

        if (!waitForPopupNavigation
            && !CanContinueRequiredTabInitialization(tab))
        {
            PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
        }
        else if (!waitForPopupNavigation && tab.NavigationRequestId == navigationRequestId)
        {
            NavigateTabCore(tab, resolution.Url!);
        }

        return tab;
    }

    internal static NavigationResolution ResolveNewTabInput(
        string input,
        string searchProviderId,
        bool waitForPopupNavigation,
        bool trustedExtensionPage)
    {
        // NewWindowRequested supplies an un-navigated CoreWebView2 for
        // about:blank/blob bootstrap windows. Preserve that URI instead of
        // translating it into MishaWeb's start page before attachment.
        return trustedExtensionPage
            || (waitForPopupNavigation && BrowserPolicy.IsPopupBootstrapUrl(input))
            ? NavigationResolution.Navigate(input)
            : BrowserPolicy.ResolveAddress(input, searchProviderId);
    }

    private BrowserTab CreateBrowserTab(string url, bool canScriptClose)
    {
        var isStartPage = url.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase);
        var host = new TransparentPanelHost
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Visible = false
        };
        if (pageHost.ClientSize.Width > 0 && pageHost.ClientSize.Height > 0)
        {
            host.Bounds = pageHost.ClientRectangle;
        }

        var overlay = CreateTabOverlay(out var overlayTitle, out var overlayDetail, out var overlayAction);
        TrySetBackColor(overlay, Color.Transparent, pageHost.BackColor);
        host.Controls.Add(overlay);
        overlay.BringToFront();
        if (isStartPage)
        {
            overlay.Visible = false;
        }
        pageHost.Controls.Add(host);
        TrySetBackColor(host, Color.Transparent, pageHost.BackColor);

        var header = new TabHeader(toolTip) { IsPrivateMode = isPrivateMode };
        var tab = new BrowserTab(
            host,
            header,
            overlay,
            overlayTitle,
            overlayDetail,
            overlayAction,
            url)
        {
            CanScriptClose = canScriptClose,
            IsStartPage = isStartPage,
            Title = isStartPage ? (isPrivateMode ? "Incognito" : "New tab") : HostFromUrl(url),
            StatusText = isStartPage ? "Ready" : "Starting\u2026",
            ConnectionState = isStartPage ? ConnectionState.Local : ConnectionState.Unknown
        };

        header.Activated += (_, _) => ActivateTab(tab);
        header.CloseRequested += (_, _) => CloseTab(tab);
        header.ContextRequested += (_, point) => ShowTabContextMenu(tab, point.ScreenLocation);
        header.DragStarted += (_, e) => BeginTabDrag(tab, e.ScreenLocation);
        header.DragMoved += (_, e) => UpdateTabDrag(e.ScreenLocation);
        header.DragCompleted += (_, e) => CompleteTabDrag(tab, e.ScreenLocation);
        header.DragCanceled += (_, _) => CancelTabDrag();
        header.KeyboardNavigationRequested += (_, e) =>
        {
            ActivateTab(tab, focusPage: false);
            if (e.KeyCode == Keys.Left) CycleTabs(-1, focusPage: false);
            else if (e.KeyCode == Keys.Right) CycleTabs(1, focusPage: false);
            else if (e.KeyCode == Keys.Home) ActivateTabByIndex(0, focusPage: false);
            else if (e.KeyCode == Keys.End) ActivateTabByIndex(tabs.Count - 1, focusPage: false);
            if (activeTab is not null)
            {
                activeTab.Header.Focus();
                tabStrip.ScrollControlIntoView(activeTab.Header);
            }
        };
        void TriggerTabRecovery()
        {
            if (tab.RetryAction is not null) RunUiTask(tab.RetryAction, "Recovery failed");
        }
        overlayAction.Click += (_, _) => TriggerTabRecovery();
        overlay.Click += (_, _) => { if (tab.IsDiscarded) TriggerTabRecovery(); };
        overlayTitle.Click += (_, _) => { if (tab.IsDiscarded) TriggerTabRecovery(); };
        overlayDetail.Click += (_, _) => { if (tab.IsDiscarded) TriggerTabRecovery(); };
        if (!isStartPage)
        {
            ShowTabOverlay(tab, "Starting\u2026", "Preparing the browser engine", null, null);
        }
        UpdateTabHeader(tab);
        return tab;
    }

    private WebView2 CreateWebView()
    {
        return new WebView2
        {
            CreationProperties = new CoreWebView2CreationProperties
            {
                IsInPrivateModeEnabled = isPrivateMode
            },
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Visible = false,
            DefaultBackgroundColor = WebsiteThemePolicy.GetLoadingBackgroundColor(darkModeEnabled),
            AccessibleName = "Web page"
        };
    }

    private void EnsureTabHostLayout(BrowserTab? tab)
    {
        if (tab is null || tab.IsClosed || isClosing) return;
        if (pageHost.ClientSize.Width <= 0 || pageHost.ClientSize.Height <= 0) return;

        var targetBounds = pageHost.ClientRectangle;
        if (tab.Host.Bounds != targetBounds)
        {
            tab.Host.Bounds = targetBounds;
        }

        var hostBounds = tab.Host.ClientRectangle;
        if (hostBounds.Width <= 0 || hostBounds.Height <= 0) return;

        if (tab.StartPageView is not null && tab.StartPageView.Visible)
        {
            if (tab.StartPageView.Bounds != hostBounds)
            {
                tab.StartPageView.Bounds = hostBounds;
            }
            tab.StartPageView.PerformLayout();
        }

        if (tab.View is not null && tab.View.Visible && tab.View.Bounds != hostBounds)
        {
            tab.View.Bounds = hostBounds;
        }

        if (tab.Overlay.Visible && tab.Overlay.Bounds != hostBounds)
        {
            tab.Overlay.Bounds = hostBounds;
        }
    }

    private WebView2 EnsureTabView(BrowserTab tab)
    {
        if (tab.View is not null) return tab.View;

        var view = CreateWebView();
        if (tab.Host.ClientSize.Width > 0 && tab.Host.ClientSize.Height > 0)
        {
            view.Bounds = tab.Host.ClientRectangle;
        }
        tab.View = view;
        view.KeyUp += (_, e) => HandleMruKeyUp(e);
        view.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.N && e.Control && e.Shift)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                OpenPrivateWindow();
            }
        };
        tab.Host.Controls.Add(view);
        view.SendToBack();
        tab.Overlay.BringToFront();
        if (tab.StartPageView is not null) tab.StartPageView.BringToFront();
        return view;
    }

    private NativeStartPage EnsureNativeStartPage(BrowserTab tab)
    {
        if (tab.StartPageView is not null) return tab.StartPageView;

        var startPageView = new NativeStartPage { Visible = false, Dock = DockStyle.Fill };
        if (tab.Host.ClientSize.Width > 0 && tab.Host.ClientSize.Height > 0)
        {
            startPageView.Bounds = tab.Host.ClientRectangle;
        }
        startPageView.SetSearchProvider(searchProviderId);
        startPageView.SetPrivateMode(isPrivateMode);
        void QueueOmniboxFocus()
        {
            if (tab.IsClosed || activeTab != tab || isClosing) return;
            BeginInvoke(() =>
            {
                if (!tab.IsClosed && activeTab == tab && !isClosing)
                {
                    RedirectStartPageSearchToAddressBar(tab);
                }
            });
        }
        startPageView.SearchBar.InputControl.Enter += (_, _) => QueueOmniboxFocus();
        startPageView.SearchBar.InputControl.MouseDown += (_, _) => QueueOmniboxFocus();
        startPageView.NavigateRequested += (_, target) =>
        {
            if (tab.IsClosed) return;
            ActivateTab(tab, focusPage: false);
            RunUiTask(() => NavigateActiveAsync(target), "Navigation could not start");
        };
        startPageView.ActionRequested += (_, action) =>
        {
            if (tab.IsClosed) return;
            ActivateTab(tab, focusPage: false);
            HandleStartPageAction(action, tab.StartPageView?.ResourceModeAnchor);
        };
        startPageView.SuggestionRequested += (_, query) =>
        {
            if (tab.IsClosed || activeTab != tab) return;
            if (query.Length > 0)
            {
                RedirectStartPageSearchToAddressBar(tab, query);
                return;
            }
            HideAddressSuggestions();
        };
        startPageView.SearchKeyDown += (_, e) => StartPageSearchBarOnKeyDown(tab, e);
        startPageView.QuickLinkContextRequested += (_, e) =>
            ShowStartPageLinkContextMenu(e.Link, e.ScreenLocation);
        startPageView.ProviderRequested += (_, _) =>
        {
            ActivateTab(tab, focusPage: false);
            ShowSearchProviderMenu(startPageView.SearchAnchor);
        };
        tab.StartPageView = startPageView;
        tab.Host.Controls.Add(startPageView);
        startPageView.BringToFront();
        return startPageView;
    }

    private static void ReleaseNativeStartPage(BrowserTab tab)
    {
        var startPageView = tab.StartPageView;
        if (startPageView is null) return;

        tab.StartPageView = null;
        tab.Host.Controls.Remove(startPageView);
        startPageView.Dispose();
    }

    private void ReleaseInactiveNativeStartPagesForUltra()
    {
        if (!ultraLightEnabled) return;
        foreach (var tab in tabs.Where(item => item != activeTab && item.IsStartPage && !item.IsClosed))
        {
            ReleaseNativeStartPage(tab);
        }
    }

    private static TransparentTableLayoutPanel CreateTabOverlay(
        out Label title,
        out Label detail,
        out Button action)
    {
        var overlay = new TransparentTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = new Padding(20),
            AccessibleRole = AccessibleRole.Alert,
            AccessibleName = "Tab status"
        };
        overlay.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        overlay.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        overlay.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        overlay.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        overlay.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        overlay.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        overlay.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        overlay.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        title = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = PageTextColor,
            Font = OverlayTitleFont,
            AutoEllipsis = true
        };
        detail = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopCenter,
            ForeColor = MutedTextColor,
            Font = OverlayDetailFont,
            AutoEllipsis = true
        };
        action = new Button
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(90, 4, 90, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = NativeUiTheme.Accent,
            ForeColor = NativeUiTheme.AccentText,
            Font = OverlayActionFont,
            Cursor = Cursors.Hand,
            Visible = false,
            AccessibleName = "Tab recovery action"
        };
        action.FlatAppearance.BorderSize = 0;
        action.FlatAppearance.MouseOverBackColor = NativeUiTheme.AccentHover;
        action.FlatAppearance.MouseDownBackColor = NativeUiTheme.AccentPressed;
        overlay.Controls.Add(title, 1, 1);
        overlay.Controls.Add(detail, 1, 2);
        overlay.Controls.Add(action, 1, 3);
        return overlay;
    }

    private Task<bool> InitializeTabAsync(
        BrowserTab tab,
        bool requireActive = false)
    {
        if (isClosing || tab.IsClosed) return Task.FromResult(false);
        if (requireActive && !CanContinueRequiredTabInitialization(tab))
        {
            return Task.FromResult(false);
        }
        if (tab.InitializationTask is not null) return tab.InitializationTask;
        if (tab.Core is not null) return Task.FromResult(true);

        tab.IsInitializing = true;
        tab.StatusText = "Starting\u2026";
        ShowTabOverlay(tab, "Starting\u2026", "Preparing the browser engine", null, null);
        UpdateTabHeader(tab);
        UpdateStatus();
        RefreshMemorySweepTimer();

        var initializationGeneration = tab.InitializationGeneration;
        var initialization = InitializeTabAfterEnvironmentAsync(
            tab,
            requireActive,
            initializationGeneration);
        tab.InitializationTask = initialization;
        return ClearInitializationWhenCompleteAsync(tab, initialization);
    }

    private async Task<bool> InitializeTabAfterEnvironmentAsync(
        BrowserTab tab,
        bool requireActive,
        long initializationGeneration)
    {
        var initializingEnvironment = await GetOrCreateEnvironmentAsync();
        if (!IsTabInitializationCurrent(tab, initializationGeneration, requireActive)
            || environment != initializingEnvironment)
        {
            return false;
        }
        var initializingView = EnsureTabView(tab);
        return await InitializeTabCoreAsync(
            tab,
            initializingView,
            initializingEnvironment,
            requireActive,
            initializationGeneration);
    }

    private bool IsTabInitializationCurrent(
        BrowserTab tab,
        long initializationGeneration,
        bool requireActive)
    {
        return !isClosing
            && !tab.IsClosed
            && !IsDisposed
            && !tab.IsStartPage
            && tab.InitializationGeneration == initializationGeneration
            && (!requireActive || CanContinueRequiredTabInitialization(tab));
    }

    private bool CanContinueRequiredTabInitialization(BrowserTab tab)
    {
        return ShouldContinueRequiredTabInitialization(
            isActive: activeTab == tab,
            isWindowMinimized,
            isBackgroundProtected: IsBackgroundProtected(tab),
            protectedResidentRank: GetProtectedResidentRank(tab));
    }

    internal static bool ShouldContinueRequiredTabInitialization(
        bool isActive,
        bool isWindowMinimized,
        bool isBackgroundProtected,
        int protectedResidentRank)
    {
        return isActive && !isWindowMinimized
            || isBackgroundProtected
            || protectedResidentRank is >= 0 and < TabLifecyclePolicy.ProtectedResidentTabCount;
    }

    private async Task<bool> ClearInitializationWhenCompleteAsync(
        BrowserTab tab,
        Task<bool> initialization)
    {
        try
        {
            return await initialization;
        }
        finally
        {
            if (ReferenceEquals(tab.InitializationTask, initialization))
            {
                tab.InitializationTask = null;
                if (tab.Core is null) tab.IsInitializing = false;
            }
            ReleaseBrowserEnvironmentIfIdle();
            if (activeTab == tab && tab.IsDiscarded && !tab.IsClosed && !isClosing)
            {
                RunUiTask(() => RestoreDiscardedTabAsync(tab, focusPage: true), "Could not restore the active tab");
            }
        }
    }

    private async Task<bool> InitializeTabCoreAsync(
        BrowserTab tab,
        WebView2 initializingView,
        CoreWebView2Environment initializingEnvironment,
        bool requireActive,
        long initializationGeneration)
    {
        try
        {
            var controllerOptions = initializingEnvironment.CreateCoreWebView2ControllerOptions();
            var profileOptions = GetControllerProfileSnapshotForTesting(isPrivateMode);
            controllerOptions.IsInPrivateModeEnabled = profileOptions.IsInPrivateModeEnabled;
            if (profileOptions.ProfileName is not null) controllerOptions.ProfileName = profileOptions.ProfileName;
            await initializingView.EnsureCoreWebView2Async(initializingEnvironment, controllerOptions);
            if (!IsTabInitializationCurrent(tab, initializationGeneration, requireActive)
                || tab.View != initializingView
                || environment != initializingEnvironment)
            {
                ReleaseAbandonedInitialization(tab, initializingView, requireActive);
                return false;
            }

            await ConfigureWebView(tab);
            if (!IsTabInitializationCurrent(tab, initializationGeneration, requireActive)
                || tab.View != initializingView
                || environment != initializingEnvironment)
            {
                ReleaseAbandonedInitialization(tab, initializingView, requireActive);
                return false;
            }
            tab.IsInitializing = false;
            tab.IsDiscarded = false;
            tab.StatusText = "Ready";
            tab.Overlay.Visible = false;
            if (tab.StartPageView is not null) tab.StartPageView.Visible = false;
            initializingView.Visible = activeTab == tab && !isWindowMinimized;
            UpdateTabHeader(tab);
            UpdateNavigationChrome();
            return true;
        }
        catch (Exception error)
        {
            if (isClosing || tab.IsClosed || tab.View != initializingView) return false;
            tab.StatusText = "Tab failed";
            stateStore.Log("Tab initialization failed", error);
            ReplaceTabView(tab);
            ShowTabOverlay(
                tab,
                "This tab could not start",
                error.Message,
                "Try again",
                () => RecreateTabAsync(tab));
            UpdateTabHeader(tab);
            UpdateNavigationChrome();
            return false;
        }
    }

    private void PreserveCanceledInitializationAsDiscarded(
        BrowserTab tab,
        long expectedNavigationRequestId)
    {
        if (isClosing
            || tab.IsClosed
            || tab.NavigationRequestId != expectedNavigationRequestId
            || tab.IsStartPage
            || IsBackgroundProtected(tab)
            || (!tab.IsDiscarded && tab.RetryAction is not null)
            || (tab.IsDiscarded && tab.StatusText == "Unloaded to save memory"))
        {
            return;
        }

        if (tab.View is not null) ReplaceTabView(tab);
        MarkTabDiscarded(tab, "Waiting in the background");
        if (activeTab == tab && !isClosing && !tab.IsClosed)
        {
            RunUiTask(() => RestoreDiscardedTabAsync(tab, focusPage: true), "Could not restore the active tab");
        }
    }

    private void ReleaseAbandonedInitialization(
        BrowserTab tab,
        WebView2 initializingView,
        bool retainDiscardedShell)
    {
        if (tab.IsClosed || tab.View != initializingView) return;

        var hasRecoveryAction = !tab.IsDiscarded && tab.RetryAction is not null;
        ReplaceTabView(tab);
        if (retainDiscardedShell
            && !hasRecoveryAction
            && !tab.IsStartPage
            && !tab.IsClosed)
        {
            MarkTabDiscarded(tab, "Waiting in the background");
            if (activeTab == tab && !isClosing && !tab.IsClosed)
            {
                RunUiTask(() => RestoreDiscardedTabAsync(tab, focusPage: true), "Could not restore the active tab");
            }
        }
    }

    private async Task ConfigureWebView(BrowserTab tab)
    {
        var view = tab.View ?? throw new InvalidOperationException("The tab view was not created.");
        var core = view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.IsPinchZoomEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = true;
        core.Settings.IsPasswordAutosaveEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = true;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        bool IsCurrentCore() => !tab.IsClosed && ReferenceEquals(tab.Core, core);
        ApplySitePreferences(tab, persist: false);
        core.IsMuted = tab.IsMuted;
        tab.IsAudible = core.IsDocumentPlayingAudio;
        tab.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        QueueManagedExtensionReconciliation(core.Profile);
        applyingZoomPreference = true;
        try { view.ZoomFactor = tab.ZoomFactor; }
        finally { applyingZoomPreference = false; }
        TrackCoreEvent<object>(
            tab,
            handler => view.ZoomFactorChanged += handler,
            handler => view.ZoomFactorChanged -= handler,
            (_, _) =>
            {
                if (!IsCurrentCore()) return;
                tab.ZoomFactor = Math.Clamp(view.ZoomFactor, BrowserStateStore.MinimumSiteZoom, BrowserStateStore.MaximumSiteZoom);
                if (!applyingZoomPreference) SaveSiteZoomPreference(tab);
            });
        // None avoids WebView2's built-in tracking prevention from partitioning
        // IndexedDB, WebCrypto, and WebSockets across communication domains
        // (such as Messenger / Facebook). The native shield continues to block
        // its audited network and cosmetic rules independently.
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None;
        await ApplyWebsiteThemeAsync(tab);
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                BrowserPerformance.SiteResponsivenessDocumentScript);
        }
        catch (Exception error)
        {
            stateStore.Log("Could not install the site responsiveness script", error);
        }

        if (adBlockEnabled && !tab.DeferResourceFilteringUntilPopupAttached)
        {
            await InstallAdBlockFilteringAsync(tab);
        }

        if (reduceWebsiteMotionEnabled) await ApplyMotionPolicyAsync(tab);

        if (adBlockEnabled)
        {
            _ = adBlocker.LoadAsync();
            try { await InstallAdBlockPageScriptsAsync(tab); }
            catch (Exception error) { stateStore.Log("Could not install the adblock page shield", error); }
        }

        foreach (var script in isPrivateMode ? Array.Empty<string>() : browserExtensions.LoadScripts())
        {
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(script); }
            catch (Exception error) { stateStore.Log("Could not load a browser extension", error); }
        }

        if (!isPrivateMode)
        {
            try
            {
                tab.ChromeStoreInstallChannel = Guid.NewGuid().ToString("N");
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    BrowserExtensions.CreateChromeWebStoreInstallBridge(tab.ChromeStoreInstallChannel));
                TrackCoreEvent<CoreWebView2WebMessageReceivedEventArgs>(
                    tab,
                    handler => core.WebMessageReceived += handler,
                    handler => core.WebMessageReceived -= handler,
                    (_, e) =>
                    {
                        if (IsCurrentCore()) OnChromeStoreWebMessage(tab, e);
                    });
            }
            catch (Exception error)
            {
                tab.ChromeStoreInstallChannel = null;
                stateStore.Log("Could not install the Chrome Web Store button bridge", error);
            }
        }

        TrackCoreEvent<CoreWebView2NavigationStartingEventArgs>(
            tab,
            handler => core.NavigationStarting += handler,
            handler => core.NavigationStarting -= handler,
            (_, e) => { if (IsCurrentCore()) OnNavigationStarting(tab, e); });
        TrackCoreEvent<CoreWebView2DOMContentLoadedEventArgs>(
            tab,
            handler => core.DOMContentLoaded += handler,
            handler => core.DOMContentLoaded -= handler,
            (_, _) => { if (IsCurrentCore()) OnDomContentLoaded(tab); });
        TrackCoreEvent<CoreWebView2FrameCreatedEventArgs>(
            tab,
            handler => core.FrameCreated += handler,
            handler => core.FrameCreated -= handler,
            (_, e) => { if (IsCurrentCore()) OnFrameCreated(tab, e.Frame); });
        TrackCoreEvent<CoreWebView2NavigationCompletedEventArgs>(
            tab,
            handler => core.NavigationCompleted += handler,
            handler => core.NavigationCompleted -= handler,
            (_, e) =>
            {
                if (!IsCurrentCore()) return;
                var isSuccess = e.IsSuccess;
                var webErrorStatus = e.WebErrorStatus;
                RunUiTask(
                    () => OnNavigationCompletedAsync(tab, core, isSuccess, webErrorStatus),
                    "Could not finish page navigation");
            });
        TrackCoreEvent<CoreWebView2SourceChangedEventArgs>(
            tab,
            handler => core.SourceChanged += handler,
            handler => core.SourceChanged -= handler,
            (_, e) => { if (IsCurrentCore()) OnSourceChanged(tab, e); });
        TrackCoreEvent<object>(
            tab,
            handler => core.DocumentTitleChanged += handler,
            handler => core.DocumentTitleChanged -= handler,
            (_, _) => { if (IsCurrentCore()) OnDocumentTitleChanged(tab); });
        TrackCoreEvent<object>(
            tab,
            handler => core.ContainsFullScreenElementChanged += handler,
            handler => core.ContainsFullScreenElementChanged -= handler,
            (_, _) =>
            {
                if (IsCurrentCore()) OnContainsFullScreenElementChanged(tab, core);
            });
        TrackCoreEvent<object>(
            tab,
            handler => core.HistoryChanged += handler,
            handler => core.HistoryChanged -= handler,
            (_, _) =>
            {
                if (IsCurrentCore() && activeTab == tab) UpdateNavigationChrome();
            });
        TrackCoreEvent<CoreWebView2NewWindowRequestedEventArgs>(
            tab,
            handler => core.NewWindowRequested += handler,
            handler => core.NewWindowRequested -= handler,
            (_, e) => { if (IsCurrentCore()) OnNewWindowRequested(tab, e); });
        TrackCoreEvent<CoreWebView2PermissionRequestedEventArgs>(
            tab,
            handler => core.PermissionRequested += handler,
            handler => core.PermissionRequested -= handler,
            (_, e) => { if (IsCurrentCore()) OnPermissionRequested(tab, e); });
        TrackCoreEvent<CoreWebView2ContextMenuRequestedEventArgs>(
            tab,
            handler => core.ContextMenuRequested += handler,
            handler => core.ContextMenuRequested -= handler,
            (_, e) => { if (IsCurrentCore()) OnContextMenuRequested(tab, e); });
        TrackCoreEvent<object>(
            tab,
            handler => core.WindowCloseRequested += handler,
            handler => core.WindowCloseRequested -= handler,
            (_, _) =>
            {
                if (!IsCurrentCore()) return;
                if (tab.CanScriptClose) CloseTab(tab);
                else ShowTransientStatus("This page cannot close a user-created tab");
            });
        TrackCoreEvent<CoreWebView2ProcessFailedEventArgs>(
            tab,
            handler => core.ProcessFailed += handler,
            handler => core.ProcessFailed -= handler,
            (_, e) =>
            {
                if (!IsCurrentCore() || isClosing || !IsHandleCreated) return;
                CoreWebView2ProcessFailedKind failedKind;
                try { failedKind = e.ProcessFailedKind; }
                catch (Exception error) when (error is InvalidOperationException or COMException)
                {
                    stateStore.Log("Could not inspect the failed browser process", error);
                    return;
                }

                // WebView2 may still be unwinding ProcessFailed. Replacing and
                // disposing its controller inside that callback is re-entrant
                // native teardown and can raise STATUS_BREAKPOINT. Snapshot the
                // enum only, then recover on the next UI turn.
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsCurrentCore()) OnProcessFailed(tab, failedKind);
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        TrackCoreEvent<CoreWebView2DownloadStartingEventArgs>(
            tab,
            handler => core.DownloadStarting += handler,
            handler => core.DownloadStarting -= handler,
            (_, e) => { if (IsCurrentCore()) OnDownloadStarting(tab, e); });
        TrackCoreEvent<object>(
            tab,
            handler => core.StatusBarTextChanged += handler,
            handler => core.StatusBarTextChanged -= handler,
            (_, _) =>
            {
                if (!IsCurrentCore()) return;
                try
                {
                    // StatusBarTextChanged can fire for every link crossed by the
                    // pointer. Keep only the newest bounded raw value here and do
                    // URI parsing/sanitization once when the 100 ms UI timer drains.
                    var pendingStatus = TextSafety.Truncate(
                        core.StatusBarText ?? string.Empty,
                        MaximumPendingHoverStatusCharacters);
                    if (pendingStatus.Equals(tab.LastHoverStatusRaw, StringComparison.Ordinal)) return;
                    tab.LastHoverStatusRaw = pendingStatus;
                    tab.PendingHoverStatus = pendingStatus;
                    tab.HasPendingHoverStatus = true;
                }
                catch (Exception error) when (error is InvalidOperationException or COMException)
                {
                    tab.LastHoverStatusRaw = string.Empty;
                    tab.PendingHoverStatus = null;
                    tab.HasPendingHoverStatus = false;
                    tab.HoverStatus = string.Empty;
                    stateStore.Log("Could not read the page link status", error);
                }
                if (activeTab == tab && !statusUiTimer.Enabled) statusUiTimer.Start();
            });
        TrackCoreEvent<object>(
            tab,
            handler => core.IsDocumentPlayingAudioChanged += handler,
            handler => core.IsDocumentPlayingAudioChanged -= handler,
            (_, _) =>
            {
                if (!IsCurrentCore()) return;
                tab.IsAudible = core.IsDocumentPlayingAudio;
                UpdateTabHeader(tab);
                if (tab.IsAudible) tab.ConsecutiveSuspendFailures = 0;
                ApplyLiveTabMemoryTarget(tab, tab == activeTab && !isWindowMinimized);
                RefreshMemorySweepTimer();
                if (!tab.IsAudible && (tab != activeTab || isWindowMinimized))
                {
                    QueueBackgroundTabReduction(tab);
                }
            });
        TrackCoreEvent<CoreWebView2LaunchingExternalUriSchemeEventArgs>(
            tab,
            handler => core.LaunchingExternalUriScheme += handler,
            handler => core.LaunchingExternalUriScheme -= handler,
            (_, e) =>
            {
                if (IsCurrentCore()) OnLaunchingExternalUriScheme(tab, e);
            });
        ApplyLiveTabMemoryTarget(tab, tab == activeTab && !isWindowMinimized);
    }

    private static void TrackCoreEvent<TEventArgs>(
        BrowserTab tab,
        Action<EventHandler<TEventArgs>> subscribe,
        Action<EventHandler<TEventArgs>> unsubscribe,
        EventHandler<TEventArgs> handler)
    {
        subscribe(handler);
        tab.TrackCoreEventHandler(() => unsubscribe(handler));
    }

    private async Task InstallAdBlockFilteringAsync(BrowserTab tab)
    {
        var core = tab.Core;
        if (!adBlockEnabled
            || core is null
            || tab.ResourceFilterInstalled
            || tab.DeferResourceFilteringUntilPopupAttached)
        {
            return;
        }

        core.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All,
            DocumentRequestSources);
        EventHandler<CoreWebView2WebResourceRequestedEventArgs> handler = (_, e) =>
        {
            if (!tab.IsClosed && ReferenceEquals(tab.Core, core)) OnWebResourceRequested(tab, e);
        };
        tab.ResourceRequestHandler = handler;
        core.WebResourceRequested += handler;
        tab.ResourceFilterInstalled = true;
        TryAssignWorkerAdBlockFilter(tab);
        adBlocker.AcquireConsumer();
        _ = adBlocker.LoadAsync();
        await Task.CompletedTask;
    }

    private async Task InstallAdBlockPageScriptsAsync(BrowserTab tab)
    {
        var core = tab.Core;
        if (!adBlockEnabled || core is null || tab.IsClosed) return;
        RemoveAdBlockPageScripts(tab, core);
        var generation = tab.AdBlockScriptGeneration;

        var exceptions = state.AdBlockExceptionHosts
            .Select(BrowserPolicy.NormalizeExactHost)
            .Where(host => host is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var exceptionJson = JsonSerializer.Serialize(exceptions);
        var policyScript =
            "(() => { const hosts = new Set("
            + exceptionJson
            + "); let topHost=location.hostname.toLowerCase();"
            + "try { topHost=top.location.hostname.toLowerCase(); } catch (_) {"
            + "try { topHost=new URL(document.referrer).hostname.toLowerCase(); } catch (_) {} }"
            + "if (hosts.has(location.hostname.toLowerCase()) || hosts.has(topHost)) "
            + "window.__mishaAdBlockEnabled = false; })();";
        var policyId = await core.AddScriptToExecuteOnDocumentCreatedAsync(policyScript);
        if (!adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || tab.AdBlockScriptGeneration != generation)
        {
            TryRemoveAdBlockScript(core, policyId, "Could not remove a stale adblock site policy");
            return;
        }
        tab.AdBlockPolicyScriptId = policyId;

        var controlChannel = tab.AdBlockControlChannel ??=
            "__misha_adblock_control_" + Guid.NewGuid().ToString("N");
        var scriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(
            AdBlockEngine.CreateDocumentScript(controlChannel));
        if (!adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || tab.AdBlockScriptGeneration != generation)
        {
            TryRemoveAdBlockScript(core, scriptId, "Could not remove a stale adblock page shield");
            if (tab.AdBlockPolicyScriptId == policyId)
            {
                TryRemoveAdBlockScript(core, policyId, "Could not remove a stale adblock site policy");
                tab.AdBlockPolicyScriptId = null;
            }
            return;
        }
        tab.AdBlockScriptId = scriptId;
    }

    private void RemoveAdBlockPageScripts(BrowserTab tab, CoreWebView2 core)
    {
        tab.AdBlockScriptGeneration++;
        if (tab.AdBlockPolicyScriptId is not null)
        {
            TryRemoveAdBlockScript(core, tab.AdBlockPolicyScriptId, "Could not remove the adblock site policy");
            tab.AdBlockPolicyScriptId = null;
        }
        if (tab.AdBlockScriptId is not null)
        {
            TryRemoveAdBlockScript(core, tab.AdBlockScriptId, "Could not remove the adblock page shield");
            tab.AdBlockScriptId = null;
        }
    }

    private void TryRemoveAdBlockScript(CoreWebView2 core, string scriptId, string logMessage)
    {
        try { core.RemoveScriptToExecuteOnDocumentCreated(scriptId); }
        catch (Exception error) { stateStore.Log(logMessage, error); }
    }

    private void RemoveAdBlockFiltering(BrowserTab tab)
    {
        if (!tab.ResourceFilterInstalled) return;
        var core = tab.Core;
        if (core is not null)
        {
            if (tab.WorkerResourceFilterInstalled)
            {
                try
                {
                    core.RemoveWebResourceRequestedFilter(
                        "*",
                        CoreWebView2WebResourceContext.All,
                        WorkerRequestSources);
                }
                catch (Exception error) { stateStore.Log("Could not remove the worker adblock filter", error); }
            }
            if (tab.ResourceRequestHandler is not null)
            {
                try { core.WebResourceRequested -= tab.ResourceRequestHandler; }
                catch (Exception error) { stateStore.Log("Could not detach the adblock resource handler", error); }
            }
            try
            {
                core.RemoveWebResourceRequestedFilter(
                    "*",
                    CoreWebView2WebResourceContext.All,
                    DocumentRequestSources);
            }
            catch (Exception error) { stateStore.Log("Could not remove the adblock resource filter", error); }
        }
        if (workerAdBlockFilterOwner == tab) workerAdBlockFilterOwner = null;
        tab.WorkerResourceFilterInstalled = false;
        tab.ResourceRequestHandler = null;
        tab.ResourceFilterInstalled = false;
        adBlocker.ReleaseConsumer();
        if (adBlockEnabled)
        {
            var replacement = tabs.FirstOrDefault(item =>
                item != tab && item.ResourceFilterInstalled && !item.IsClosed && item.Core is not null);
            if (replacement is not null) TryAssignWorkerAdBlockFilter(replacement);
        }
    }

    private void TryAssignWorkerAdBlockFilter(BrowserTab tab)
    {
        if (workerAdBlockFilterOwner is { IsClosed: false, WorkerResourceFilterInstalled: true, Core: not null })
        {
            return;
        }
        if (tab.Core is null || tab.IsClosed || !tab.ResourceFilterInstalled) return;

        try
        {
            tab.Core.AddWebResourceRequestedFilter(
                "*",
                CoreWebView2WebResourceContext.All,
                WorkerRequestSources);
            tab.WorkerResourceFilterInstalled = true;
            workerAdBlockFilterOwner = tab;
        }
        catch (Exception error)
        {
            stateStore.Log("Could not install the worker adblock filter", error);
        }
    }

    private void OnContainsFullScreenElementChanged(BrowserTab tab, CoreWebView2 core)
    {
        if (isClosing || !IsHandleCreated) return;
        if (activeTab != tab) return;

        if (InvokeRequired) BeginInvoke((Action)(() => SetDomFullScreen(core.ContainsFullScreenElement)));
        else SetDomFullScreen(core.ContainsFullScreenElement);
    }

    private void SyncDomFullScreenFromActiveTab()
    {
        if (isClosing || activeTab?.Core is null)
        {
            if (isDomFullScreen)
            {
                isDomFullScreen = false;
                UpdateChromeRowsForFullscreen();
            }

            return;
        }

        SetDomFullScreen(activeTab.Core.ContainsFullScreenElement);
    }

    private void SetDomFullScreen(bool value)
    {
        if (isDomFullScreen == value) return;
        if (value)
        {
            findBarWasVisibleBeforeDomFullScreen = findBar.Visible;
            if (!isFullScreen)
            {
                normalWindowStateBeforeDomFullScreen = WindowState;
                normalBoundsBeforeDomFullScreen = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                hasDomFullScreenWindowState = true;
                WindowState = FormWindowState.Normal;
                Padding = Padding.Empty;
                Bounds = Screen.FromControl(this).Bounds;
            }
        }
        else if (hasDomFullScreenWindowState && !isFullScreen)
        {
            Bounds = normalBoundsBeforeDomFullScreen;
            WindowState = normalWindowStateBeforeDomFullScreen;
            hasDomFullScreenWindowState = false;
        }

        isDomFullScreen = value;
        if (!value) findBar.Visible = findBarWasVisibleBeforeDomFullScreen;
        UpdateChromeRowsForFullscreen();
    }

    private void ExitDomFullScreen()
    {
        var tab = activeTab;
        var core = tab?.Core;
        if (tab is null || core is null)
        {
            SetDomFullScreen(false);
            return;
        }
        RunUiTask(
            async () =>
            {
                try
                {
                    await core.ExecuteScriptAsync(
                        "if(document.fullscreenElement&&document.exitFullscreen)document.exitFullscreen();");
                }
                finally
                {
                    if (!tab.IsClosed && ReferenceEquals(tab.Core, core)) SetDomFullScreen(false);
                }
            },
            "Could not leave page fullscreen");
    }

    private void UpdateChromeRowsForFullscreen()
    {
        var isChromeHidden = isFullScreen || isDomFullScreen;
        titleBar.Visible = !isChromeHidden;
        toolbar.Visible = !isChromeHidden;
        rootLayout.RowStyles[0].Height = isChromeHidden ? 0 : ScaleChromeLogical(40);
        rootLayout.RowStyles[1].Height = isChromeHidden ? 0 : ScaleChromeLogical(46);
        if (isChromeHidden) HideAddressSuggestions();
        rootLayout.PerformLayout();

        if (isChromeHidden)
        {
            findBar.Visible = false;
            rootLayout.RowStyles[2].Height = 0;
            UpdateWindowControls();
            return;
        }

        var shouldShowFindBar = findBar.Visible;
        findBar.Visible = shouldShowFindBar;
        rootLayout.RowStyles[2].Height = shouldShowFindBar ? ScaleChromeLogical(40) : 0;
        UpdateFindBarLayout();
        UpdateWindowControls();
    }

    private void OnNavigationStarting(BrowserTab tab, CoreWebView2NavigationStartingEventArgs e)
    {
        var pendingFileNavigation = tab.PendingFileNavigationUrl;
        tab.PendingFileNavigationUrl = null;
        var isLocalFileNavigation = BrowserPolicy.TryNormalizeLocalFileUrl(e.Uri, out var normalizedFileUrl);
        var isAuthorizedLocalFileNavigation = isLocalFileNavigation
            && ((pendingFileNavigation is not null
                    && BrowserPolicy.UrlEquals(pendingFileNavigation, normalizedFileUrl))
                || BrowserPolicy.IsLocalFileUrl(tab.Url));
        var targetHost = HostFromUrl(e.Uri);
        var sourceHost = HostFromUrl(tab.Url);
        if (adBlockEnabled
            && !IsExceptionHost(targetHost)
            && !IsExceptionHost(sourceHost)
            && adBlocker.ShouldBlockNavigation(
                e.Uri,
                tab.Url,
                applyRedirectHostHeuristics: e.IsRedirected || !e.IsUserInitiated,
                cacheEvaluation: !isPrivateMode))
        {
            e.Cancel = true;
            tab.BlockedRequestCount++;
            tab.IsLoading = false;
            tab.StatusText = "Blocked an ad redirect";
            if (tab.BlockedRequestCount == 1) ShowTransientStatus("Shield blocked an ad redirect");
            UpdateNavigationChrome();
            UpdateStatus();
            return;
        }

        var trustedExtensionNavigation = IsTrustedExtensionPage(e.Uri, tab.ExtensionOriginId);
        var allowedPopupBootstrap = tab.AllowedPopupBootstrapUrl;
        // This is a one-navigation capability, not a reusable scheme exception.
        // A newly attached CoreWebView2 may already be at about:blank without
        // raising NavigationStarting, so consume it even when the next URI does
        // not match. Otherwise a later page could replay the stale allowance.
        tab.AllowedPopupBootstrapUrl = null;
        var authorizedPopupBootstrap = allowedPopupBootstrap is not null
            && BrowserPolicy.UrlEquals(allowedPopupBootstrap, e.Uri);
        var isAllowedExternalNavigation = IsAllowedExternalNavigation(e.Uri, e.IsUserInitiated);
        if (!authorizedPopupBootstrap
            && !BrowserPolicy.IsSafeTopLevelUrl(
                e.Uri,
                allowFileScheme: isAuthorizedLocalFileNavigation)
            && !trustedExtensionNavigation
            && !isAllowedExternalNavigation)
        {
            e.Cancel = true;
            tab.IsLoading = false;
            tab.StatusText = "Blocked unsafe navigation";
            ShowTransientStatus("Blocked an unsupported or unsafe link");
            UpdateNavigationChrome();
            return;
        }
        if (!authorizedPopupBootstrap
            && isAllowedExternalNavigation
            && !BrowserPolicy.IsSafeTopLevelUrl(
                e.Uri,
                allowFileScheme: isAuthorizedLocalFileNavigation)
            && !trustedExtensionNavigation)
        {
            // LaunchingExternalUriScheme owns confirmation and status. The
            // current document remains loaded, so do not revoke its state.
            return;
        }
        if (StartPage.IsStartPageUrl(e.Uri))
        {
            e.Cancel = true;
            var navigationCore = tab.Core;
            BeginInvoke(() =>
            {
                if (!tab.IsClosed && ReferenceEquals(tab.Core, navigationCore)) ShowStartPage(tab);
            });
            return;
        }

        ClearTabHoverStatus(tab);
        StopFindSession(tab);
        tab.ReaderModeActive = false;
        tab.ContextLinkTarget = null;
        if (!trustedExtensionNavigation) tab.ExtensionOriginId = null;
        try
        {
            if (tab.Core is { } navigationCore)
            {
                navigationCore.Settings.IsWebMessageEnabled =
                    ShouldEnableChromeStoreInstallBridge(e.Uri, isPrivateMode);
            }
        }
        catch (InvalidOperationException)
        {
            // A browser process failure can dispose the core during navigation teardown.
        }

        CancelPermissionsForTab(tab);
        ClearTabMediaPermissions(tab);
        // A top-level navigation replaces the entire child-frame tree. Detach
        // every frame event sink now instead of waiting for COM Destroyed
        // callbacks, which are not guaranteed after a renderer/process exit.
        // Otherwise repeated navigations can leave old frame wrappers and
        // their closures rooted for the rest of the browser session.
        AbandonPendingFrameSetups(tab);
        tab.ReleaseTrackedFrames();
        tab.DocumentNavigationGeneration++;
        tab.IsLoading = true;
        tab.ConsecutiveSuspendFailures = 0;
        tab.StatusText = "Loading\u2026";
        tab.BlockedRequestCount = 0;
        tab.IsStartPage = false;
        ReleaseNativeStartPage(tab);
        if (activeTab == tab && !isWindowMinimized && !tab.Overlay.Visible)
        {
            if (tab.View is not null) tab.View.Visible = true;
        }
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            tab.Url = e.Uri;
            tab.ConnectionState = ConnectionState.Loading;
        }
        else if (isAuthorizedLocalFileNavigation)
        {
            tab.Url = normalizedFileUrl;
            tab.KeepAwake = false;
            tab.ConnectionState = ConnectionState.Loading;
        }
        else if (trustedExtensionNavigation)
        {
            tab.Url = e.Uri;
            tab.ConnectionState = ConnectionState.Local;
        }
        if (activeTab == tab)
        {
            SyncAddressBar();
            UpdateNavigationChrome();
            UpdateStatus();
        }
        UpdateTabHeader(tab);
    }

    private void OnWebResourceRequested(
        BrowserTab tab,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!adBlockEnabled || isClosing || tab.IsClosed) return;

        var sourceUrl = GetAdBlockSourceUrl(
            tab,
            e.Request.Headers,
            e.RequestedSourceKind,
            out var requestSourceAttributed);
        var topLevelUrl = tab.Core?.Source ?? tab.Url;
        var documentSource = (e.RequestedSourceKind & DocumentRequestSources) != 0;
        var workerSource = (e.RequestedSourceKind & WorkerRequestSources) != 0;
        if (adBlockExceptionHosts.Count > 0
            && (IsExceptionHost(HostFromUrl(e.Request.Uri))
                || (documentSource && IsExceptionHost(HostFromUrl(topLevelUrl)))
                || IsExceptionHost(HostFromUrl(sourceUrl)))) return;
        var resourceType = MapResourceType(
            e.ResourceContext,
            e.ResourceContext == CoreWebView2WebResourceContext.Document
                ? GetRequestHeader(e.Request.Headers, "Sec-Fetch-Dest")
                : null);
        // A media stream is not an ad simply because it comes from one of
        // YouTube's rotating googlevideo hosts. Filter-list false positives
        // here produce the exact black/error player which a reload may hide.
        // Keep the narrow player bootstrap and videoplayback transport alive;
        // ad response pruning and player-side skip logic still handle ads.
        if (ShouldBypassYouTubePlaybackRequest(topLevelUrl, sourceUrl, e.Request.Uri, resourceType)) return;
        if (ShouldBypassCommunicationRequest(topLevelUrl, sourceUrl, e.Request.Uri, resourceType, workerSource)) return;
        if (resourceType is AdBlockResourceType.WebSocket or AdBlockResourceType.Media)
        {
            var topLevelOrigin = NormalizePermissionOrigin(topLevelUrl);
            var requestSourceOrigin = requestSourceAttributed
                ? NormalizePermissionOrigin(sourceUrl)
                : null;
            var sourceMatchesGrantedOrigin = tab.HasMediaCapturePermissionForOrigin(requestSourceOrigin);
            var topLevelMatchesGrantedOrigin = tab.HasMediaCapturePermissionForOrigin(topLevelOrigin);
            var hasDistinctRequestSource = requestSourceAttributed
                && requestSourceOrigin is not null
                && !requestSourceOrigin.Equals(topLevelOrigin, StringComparison.OrdinalIgnoreCase);
            var compatibilityPageUrl = workerSource || hasDistinctRequestSource
                ? sourceUrl
                : topLevelUrl;
            var compatibilityOrigin = NormalizePermissionOrigin(compatibilityPageUrl);
            var mediaPermissionGranted = workerSource
                ? requestSourceAttributed
                    && compatibilityOrigin is not null && tabs.Any(item =>
                    !item.IsClosed
                    && item.HasMediaCapturePermissionForOrigin(compatibilityOrigin))
                : requestSourceAttributed
                    && (hasDistinctRequestSource
                        ? sourceMatchesGrantedOrigin
                        : topLevelMatchesGrantedOrigin || sourceMatchesGrantedOrigin);
            if (CommunicationCompatibilityPolicy.ShouldBypassShield(
                    compatibilityPageUrl,
                    e.Request.Uri,
                    resourceType,
                    mediaPermissionGranted)) return;
        }
        if (!adBlocker.ShouldBlock(
                e.Request.Uri,
                sourceUrl,
                resourceType,
                cacheEvaluation: !isPrivateMode)) return;

        e.Response = CreateBlockedResourceResponse(resourceType, e.Request.Uri, topLevelUrl, e.Request.Headers);
        tab.BlockedRequestCount++;
        if (tab.BlockedRequestCount == 1) ShowTransientStatus("Shield blocked ad and tracker requests");
        else if (activeTab == tab && !statusUiTimer.Enabled) statusUiTimer.Start();
    }

    private string GetAdBlockSourceUrl(
        BrowserTab tab,
        CoreWebView2HttpRequestHeaders headers,
        CoreWebView2WebResourceRequestSourceKinds sourceKind,
        out bool sourceWasAttributed)
    {
        sourceWasAttributed = false;
        foreach (var name in RequestSourceHeaderNames)
        {
            try
            {
                if (!headers.Contains(name)) continue;
                var value = headers.GetHeader(name);
                if (BrowserPolicy.IsHttpUrl(value))
                {
                    sourceWasAttributed = true;
                    return value;
                }
            }
            catch (ArgumentException) { }
            catch (COMException) { }
        }
        if ((sourceKind & WorkerRequestSources) != 0)
        {
            // Worker events are environment-wide and may be raised on a tab
            // unrelated to the worker. Unknown is safer than pretending the
            // request target is its own first-party source.
            return string.Empty;
        }
        return tab.Core?.Source ?? tab.Url;
    }

    private static string? GetRequestHeader(
        CoreWebView2HttpRequestHeaders headers,
        string name)
    {
        try
        {
            if (!headers.Contains(name)) return null;
            return headers.GetHeader(name);
        }
        catch (ArgumentException) { return null; }
        catch (COMException) { return null; }
    }

    private static AdBlockResourceType MapResourceType(
        CoreWebView2WebResourceContext webViewContext,
        string? fetchDestination = null)
    {
        return webViewContext switch
        {
            CoreWebView2WebResourceContext.Document when fetchDestination is not null
                && (fetchDestination.Equals("iframe", StringComparison.OrdinalIgnoreCase)
                    || fetchDestination.Equals("frame", StringComparison.OrdinalIgnoreCase)) =>
                AdBlockResourceType.SubDocument,
            CoreWebView2WebResourceContext.Document => AdBlockResourceType.Document,
            CoreWebView2WebResourceContext.Stylesheet => AdBlockResourceType.Stylesheet,
            CoreWebView2WebResourceContext.Image => AdBlockResourceType.Image,
            CoreWebView2WebResourceContext.Media => AdBlockResourceType.Media,
            CoreWebView2WebResourceContext.Font => AdBlockResourceType.Font,
            CoreWebView2WebResourceContext.Script => AdBlockResourceType.Script,
            CoreWebView2WebResourceContext.XmlHttpRequest => AdBlockResourceType.XmlHttpRequest,
            CoreWebView2WebResourceContext.Fetch => AdBlockResourceType.Fetch,
            CoreWebView2WebResourceContext.Websocket => AdBlockResourceType.WebSocket,
            CoreWebView2WebResourceContext.Ping or CoreWebView2WebResourceContext.CspViolationReport => AdBlockResourceType.Ping,
            CoreWebView2WebResourceContext.EventSource => AdBlockResourceType.XmlHttpRequest,
            CoreWebView2WebResourceContext.TextTrack => AdBlockResourceType.Media,
            _ => AdBlockResourceType.Other
        };
    }

    internal static bool ShouldBypassUnknownWorkerRequest(
        bool workerSource,
        string sourceUrl,
        IEnumerable<string> openPageUrls,
        IEnumerable<string> exceptionHosts)
    {
        // An unattributed worker cannot safely inherit a shield exception from
        // some unrelated open tab. Known source/request origins are evaluated
        // by the normal exact-host checks in OnWebResourceRequested.
        _ = workerSource;
        _ = sourceUrl;
        _ = openPageUrls;
        _ = exceptionHosts;
        return false;
    }

    internal static bool ShouldBypassYouTubePlaybackRequest(
        string? topLevelUrl,
        string? sourceUrl,
        string? requestUrl,
        AdBlockResourceType resourceType)
    {
        if (string.IsNullOrWhiteSpace(requestUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request))
        {
            return false;
        }

        // 1. Any request to *.googlevideo.com is Google's video/audio streaming CDN.
        // It NEVER serves standalone display ads or third-party trackers.
        // Blocking it at the network level causes 0:00/0:00 black-screen player stalls.
        var isGooglevideoHost = request.Host.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase)
            || request.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);
        if (isGooglevideoHost)
        {
            return true;
        }

        // 2. YouTube player API and video info endpoints must be bypassed regardless
        // of whether topLevelUrl or sourceUrl represents the YouTube page.
        var isYouTubeRequest = IsYouTubeHost(request.Host);
        if (!isYouTubeRequest)
        {
            return false;
        }

        var path = request.AbsolutePath;
        var isPlayerEndpoint = path.Equals("/youtubei/v1/player", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/get_watch", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/next", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/browse", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/search", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/reel/reel_item_watch", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/reel/reel_watch_sequence", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/youtubei/v1/att/get", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/get_video_info", StringComparison.OrdinalIgnoreCase);

        if (isPlayerEndpoint)
        {
            return resourceType is AdBlockResourceType.XmlHttpRequest
                or AdBlockResourceType.Fetch
                or AdBlockResourceType.Other;
        }

        var topLevelIsYouTube = !string.IsNullOrWhiteSpace(topLevelUrl)
            && Uri.TryCreate(topLevelUrl, UriKind.Absolute, out var topUri)
            && IsYouTubeHost(topUri.Host);
        var sourceIsYouTube = !string.IsNullOrWhiteSpace(sourceUrl)
            && Uri.TryCreate(sourceUrl, UriKind.Absolute, out var srcUri)
            && IsYouTubeHost(srcUri.Host);

        if (topLevelIsYouTube || sourceIsYouTube)
        {
            if (resourceType is AdBlockResourceType.Media)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ShouldBypassCommunicationRequest(
        string? topLevelUrl,
        string? sourceUrl,
        string? requestUrl,
        AdBlockResourceType resourceType,
        bool workerSource)
    {
        if (resourceType is not (AdBlockResourceType.WebSocket
                or AdBlockResourceType.Media
                or AdBlockResourceType.XmlHttpRequest
                or AdBlockResourceType.Fetch
                or AdBlockResourceType.Script
                or AdBlockResourceType.Ping
                or AdBlockResourceType.Other))
        {
            return false;
        }

        var targetUrl = !string.IsNullOrEmpty(topLevelUrl) && !topLevelUrl.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            ? topLevelUrl
            : sourceUrl;

        if (CommunicationCompatibilityPolicy.ShouldBypassCommunicationRequest(targetUrl, requestUrl, resourceType))
        {
            return true;
        }

        if (workerSource && Uri.TryCreate(requestUrl, UriKind.Absolute, out var reqUri))
        {
            if (CommunicationCompatibilityPolicy.IsProviderTransportHost(reqUri.IdnHost))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsYouTubeHost(string host) =>
        host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);

    private CoreWebView2WebResourceResponse? CreateBlockedResourceResponse(
        AdBlockResourceType resourceType,
        string? requestUrl = null,
        string? topLevelUrl = null,
        CoreWebView2HttpRequestHeaders? requestHeaders = null)
    {
        if (environment is null) return null;
        var isYouTubeContext = (topLevelUrl is not null && topLevelUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            || (requestUrl is not null && (requestUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
                || requestUrl.Contains("doubleclick.net", StringComparison.OrdinalIgnoreCase)
                || requestUrl.Contains("googleads", StringComparison.OrdinalIgnoreCase)
                || requestUrl.Contains("googlesyndication", StringComparison.OrdinalIgnoreCase)));

        if (isYouTubeContext)
        {
            var origin = "https://www.youtube.com";
            if (requestHeaders is not null && requestHeaders.Contains("Origin"))
            {
                try
                {
                    var headerOrigin = requestHeaders.GetHeader("Origin");
                    if (!string.IsNullOrWhiteSpace(headerOrigin) && headerOrigin != "null")
                    {
                        origin = headerOrigin;
                    }
                }
                catch { }
            }
            else if (!string.IsNullOrEmpty(topLevelUrl) && Uri.TryCreate(topLevelUrl, UriKind.Absolute, out var topUri))
            {
                origin = topUri.GetLeftPart(UriPartial.Authority);
            }

            if (requestUrl is not null && requestUrl.Contains("/pagead/id", StringComparison.OrdinalIgnoreCase))
            {
                var idPayload = ")]}'\n\n{\"id\":\"ANyPxKrAzkV5cLEVtGXqf11mX0EFDh00ASxA-CsrWnAIiEOXKju9lnsjfHFdvqf7wl5Er6SrJEF7\",\"type\":4}"u8.ToArray();
                return environment.CreateWebResourceResponse(
                    new MemoryStream(idPayload, writable: false),
                    200,
                    "OK",
                    $"Content-Type: application/json; charset=ISO-8859-1\r\nAccess-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Credentials: true\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: *\r\nVary: Origin");
            }

            if (resourceType is AdBlockResourceType.XmlHttpRequest or AdBlockResourceType.Fetch)
            {
                return environment.CreateWebResourceResponse(
                    new MemoryStream("{}"u8.ToArray(), writable: false),
                    200,
                    "OK",
                    $"Content-Type: application/json; charset=utf-8\r\nAccess-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Credentials: true\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: *\r\nVary: Origin");
            }

            if (resourceType is AdBlockResourceType.Script)
            {
                return environment.CreateWebResourceResponse(
                    new MemoryStream(YouTubeScriptStubBytes, writable: false),
                    200,
                    "OK",
                    "Content-Type: application/javascript; charset=utf-8\r\nAccess-Control-Allow-Origin: *");
            }

            if (resourceType is AdBlockResourceType.Ping or AdBlockResourceType.Other)
            {
                return environment.CreateWebResourceResponse(
                    Stream.Null,
                    204,
                    "No Content",
                    $"Access-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Credentials: true\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: *\r\nVary: Origin");
            }
        }

        return resourceType switch
        {
            AdBlockResourceType.Script => environment.CreateWebResourceResponse(
                Stream.Null, 200, "Blocked by MishaWeb", "Content-Type: application/javascript"),
            AdBlockResourceType.Stylesheet => environment.CreateWebResourceResponse(
                Stream.Null, 200, "Blocked by MishaWeb", "Content-Type: text/css"),
            AdBlockResourceType.Image => environment.CreateWebResourceResponse(
                new MemoryStream(TransparentGif, writable: false),
                200,
                "Blocked by MishaWeb",
                "Content-Type: image/gif"),
            AdBlockResourceType.Font or AdBlockResourceType.Media
                or AdBlockResourceType.XmlHttpRequest or AdBlockResourceType.Fetch
                or AdBlockResourceType.Ping => environment.CreateWebResourceResponse(
                    Stream.Null, 204, "Blocked by MishaWeb", string.Empty),
            _ => environment.CreateWebResourceResponse(
                Stream.Null, 403, "Blocked by MishaWeb", "Content-Type: text/plain")
        };
    }

    private void OnDomContentLoaded(BrowserTab tab)
    {
        RunUiTask(
            () => ApplyAdBlockCosmeticsAsync(tab),
            "Could not apply cosmetic ad filters");
    }

    private void OnFrameCreated(BrowserTab tab, CoreWebView2Frame frame)
    {
        var originatingCore = tab.Core;
        if (originatingCore is null
            || tab.AdBlockFrames.Count >= MaximumTrackedFramesPerTab)
        {
            return;
        }
        var frameDocumentGeneration = tab.DocumentNavigationGeneration;
        bool IsCurrentFrame() => originatingCore is not null
            && !tab.IsClosed
            && ReferenceEquals(tab.Core, originatingCore)
            && frameDocumentGeneration == tab.DocumentNavigationGeneration
            && tab.AdBlockFrames.Contains(frame);
        var frameUrl = string.Empty;
        var frameNavigationGeneration = 0L;
        EventHandler<object> destroyedHandler = (_, _) =>
        {
            // WebView2 reports IsDestroyed=true while this callback is running.
            // Calling any remove_* method on that frame can raise a native
            // STATUS_BREAKPOINT instead of returning a catchable COM error.
            // The destroyed frame releases its native event registrations; only
            // forget our managed bookkeeping and delegate owner here.
            AbandonPendingFrameSetup(frame);
            tab.ReleaseDestroyedFrame(frame);
        };
        EventHandler<CoreWebView2NavigationStartingEventArgs> navigationStartingHandler = (_, e) =>
        {
            if (!IsCurrentFrame()) return;
            frameNavigationGeneration++;
            AbandonPendingFrameSetup(frame);
            tab.FrameOrigins.Remove(frame);
            frameUrl = e.Uri;
        };
        EventHandler<CoreWebView2DOMContentLoadedEventArgs> domContentLoadedHandler = (_, _) =>
        {
            var loadedFrameUrl = frameUrl;
            var loadedFrameNavigationGeneration = frameNavigationGeneration;
            var loadedFrameOrigin = NormalizePermissionOrigin(loadedFrameUrl);
            bool IsCurrentLoadedFrame() => IsCurrentFrame()
                && loadedFrameNavigationGeneration == frameNavigationGeneration
                && BrowserPolicy.UrlEquals(frameUrl, loadedFrameUrl);
            if (!IsCurrentLoadedFrame())
            {
                return;
            }
            if (loadedFrameOrigin is not null)
            {
                tab.FrameOrigins[frame] = loadedFrameOrigin;
            }
            QueueFrameSetup(
                tab,
                frame,
                IsCurrentLoadedFrame,
                async () =>
                {
                    if (!IsCurrentLoadedFrame()) return;
                    await RefreshTabMediaPermissionOriginAsync(
                        tab,
                        loadedFrameUrl,
                        frameDocumentGeneration,
                        IsCurrentLoadedFrame);
                    if (IsCurrentLoadedFrame())
                    {
                        await ApplyAdBlockCosmeticsAsync(
                            tab,
                            frame,
                            loadedFrameUrl,
                            originatingCore,
                            frameDocumentGeneration,
                            IsCurrentLoadedFrame);
                    }
                });
        };
        EventHandler<CoreWebView2FrameCreatedEventArgs> frameCreatedHandler = (_, e) =>
        {
            if (IsCurrentFrame()) OnFrameCreated(tab, e.Frame);
        };

        var subscription = new FrameEventSubscription(
            frame,
            destroyedHandler,
            navigationStartingHandler,
            domContentLoadedHandler,
            frameCreatedHandler);
        if (!tab.TryTrackFrame(frame, subscription)) return;
        try
        {
            subscription.Attach();
        }
        catch (Exception error) when (
            error is ObjectDisposedException or InvalidOperationException or COMException)
        {
            tab.ReleaseTrackedFrame(frame);
            stateStore.Log("Could not track a page frame", error);
        }
    }

    private void QueueFrameSetup(
        BrowserTab tab,
        CoreWebView2Frame frame,
        Func<bool> isCurrent,
        Func<Task> execute)
    {
        // WebView2 and WinForms callbacks run on the window's UI apartment.
        // Keep both scheduling and execution there so frame COM wrappers never
        // cross threads. The global bound prevents frame-heavy pages from
        // retaining an unbounded chain of closures while permission work waits.
        Debug.Assert(!InvokeRequired);
        if (frameSetupQueueStopping || isClosing || !isCurrent()) return;

        tab.FrameSetupInProgress.Add(frame);
        if (pendingFrameSetupByFrame.TryGetValue(frame, out var existing))
        {
            // A frame can navigate again while its previous setup is queued or
            // active. Preserve one latest follow-up instead of running stale URL
            // work or allowing duplicate queue entries to accumulate.
            existing.Replace(isCurrent, execute);
            return;
        }

        if (pendingFrameSetups.Count >= MaximumPendingFrameSetups)
        {
            AbandonOldestPendingFrameSetup();
        }

        var work = new FrameSetupWork(tab, frame, isCurrent, execute);
        pendingFrameSetups.Enqueue(work);
        pendingFrameSetupByFrame.Add(frame, work);
        DrainFrameSetupQueue();
    }

    private void DrainFrameSetupQueue()
    {
        Debug.Assert(!InvokeRequired);
        if (drainingFrameSetupQueue || frameSetupQueueStopping || isClosing) return;

        drainingFrameSetupQueue = true;
        try
        {
            while (activeFrameSetupWorkers < MaximumConcurrentFrameSetups
                && pendingFrameSetups.Count > 0)
            {
                FrameSetupWork? next = null;
                var candidatesToInspect = pendingFrameSetups.Count;
                while (candidatesToInspect-- > 0)
                {
                    var candidate = pendingFrameSetups.Dequeue();
                    if (!pendingFrameSetupByFrame.TryGetValue(candidate.Frame, out var current)
                        || !ReferenceEquals(candidate, current))
                    {
                        continue;
                    }
                    if (activeFrameSetups.Contains(candidate.Frame))
                    {
                        pendingFrameSetups.Enqueue(candidate);
                        continue;
                    }

                    pendingFrameSetupByFrame.Remove(candidate.Frame);
                    if (!candidate.IsCurrent())
                    {
                        ReleaseFrameSetupTracking(candidate);
                        continue;
                    }

                    next = candidate;
                    break;
                }

                if (next is null) break;
                activeFrameSetups.Add(next.Frame);
                activeFrameSetupWorkers++;
                RunUiTask(
                    () => ExecuteFrameSetupAsync(next),
                    "Could not finish frame setup");
            }
        }
        finally
        {
            drainingFrameSetupQueue = false;
        }
    }

    private async Task ExecuteFrameSetupAsync(FrameSetupWork work)
    {
        try
        {
            if (!frameSetupQueueStopping && !isClosing && work.IsCurrent())
            {
                await work.Execute();
            }
        }
        finally
        {
            activeFrameSetups.Remove(work.Frame);
            activeFrameSetupWorkers = Math.Max(0, activeFrameSetupWorkers - 1);
            ReleaseFrameSetupTracking(work);
            DrainFrameSetupQueue();
        }
    }

    private void AbandonPendingFrameSetup(CoreWebView2Frame frame)
    {
        Debug.Assert(!InvokeRequired);
        if (!pendingFrameSetupByFrame.Remove(frame, out var abandoned)) return;

        var queuedCount = pendingFrameSetups.Count;
        while (queuedCount-- > 0)
        {
            var candidate = pendingFrameSetups.Dequeue();
            if (!ReferenceEquals(candidate, abandoned)) pendingFrameSetups.Enqueue(candidate);
        }
        ReleaseFrameSetupTracking(abandoned);
    }

    private void AbandonPendingFrameSetups(BrowserTab tab)
    {
        Debug.Assert(!InvokeRequired);
        var queuedCount = pendingFrameSetups.Count;
        while (queuedCount-- > 0)
        {
            var candidate = pendingFrameSetups.Dequeue();
            if (!ReferenceEquals(candidate.Tab, tab))
            {
                pendingFrameSetups.Enqueue(candidate);
                continue;
            }

            if (pendingFrameSetupByFrame.TryGetValue(candidate.Frame, out var current)
                && ReferenceEquals(candidate, current))
            {
                pendingFrameSetupByFrame.Remove(candidate.Frame);
                ReleaseFrameSetupTracking(candidate);
            }
        }
    }

    private void AbandonOldestPendingFrameSetup()
    {
        Debug.Assert(!InvokeRequired);
        while (pendingFrameSetups.Count > 0)
        {
            var abandoned = pendingFrameSetups.Dequeue();
            if (!pendingFrameSetupByFrame.TryGetValue(abandoned.Frame, out var current)
                || !ReferenceEquals(abandoned, current))
            {
                continue;
            }

            pendingFrameSetupByFrame.Remove(abandoned.Frame);
            ReleaseFrameSetupTracking(abandoned);
            return;
        }
    }

    private void StopFrameSetupQueue()
    {
        Debug.Assert(!InvokeRequired);
        frameSetupQueueStopping = true;
        while (pendingFrameSetups.Count > 0)
        {
            var abandoned = pendingFrameSetups.Dequeue();
            if (pendingFrameSetupByFrame.TryGetValue(abandoned.Frame, out var current)
                && ReferenceEquals(abandoned, current))
            {
                pendingFrameSetupByFrame.Remove(abandoned.Frame);
                ReleaseFrameSetupTracking(abandoned);
            }
        }
        pendingFrameSetupByFrame.Clear();
    }

    private void ReleaseFrameSetupTracking(FrameSetupWork work)
    {
        if (!activeFrameSetups.Contains(work.Frame)
            && !pendingFrameSetupByFrame.ContainsKey(work.Frame))
        {
            work.Tab.FrameSetupInProgress.Remove(work.Frame);
        }
    }

    private async Task ApplyAdBlockCosmeticsAsync(
        BrowserTab tab,
        CoreWebView2Frame frame,
        string frameUrl,
        CoreWebView2 originatingCore,
        long documentGeneration,
        Func<bool> isCurrentFrame)
    {
        var core = tab.Core;
        if (core is null
            || !ReferenceEquals(core, originatingCore)
            || documentGeneration != tab.DocumentNavigationGeneration
            || tab.IsClosed
            || isClosing
            || !isCurrentFrame()
            || !adBlockEnabled
            || !BrowserPolicy.IsHttpUrl(frameUrl)
            || IsExceptionHost(HostFromUrl(core.Source))
            || IsExceptionHost(HostFromUrl(frameUrl))) return;

        await adBlocker.LoadAsync();
        if (!adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || documentGeneration != tab.DocumentNavigationGeneration
            || !isCurrentFrame()
            || IsExceptionHost(HostFromUrl(core.Source))) return;

        var css = await adBlocker.GetCosmeticCssAsync(frameUrl, cacheEvaluation: !isPrivateMode);
        if (css.Length == 0
            || !adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || documentGeneration != tab.DocumentNavigationGeneration
            || !isCurrentFrame()
            || IsExceptionHost(HostFromUrl(core.Source))) return;
        await frame.ExecuteScriptAsync(CreateCosmeticStyleScript(css, frameUrl));
    }

    private async Task ApplyAdBlockCosmeticsAsync(BrowserTab tab)
    {
        var core = tab.Core;
        if (core is null || tab.IsClosed || isClosing) return;
        var pageUrl = core.Source;
        var exception = !adBlockEnabled || IsExceptionHost(HostFromUrl(pageUrl));
        if (exception)
        {
            await DisableAdBlockForCurrentDocumentAsync(tab);
            return;
        }

        await adBlocker.LoadAsync();
        if (!adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || !BrowserPolicy.UrlEquals(core.Source, pageUrl)
            || IsExceptionHost(HostFromUrl(core.Source)))
        {
            return;
        }

        var css = await adBlocker.GetCosmeticCssAsync(pageUrl, cacheEvaluation: !isPrivateMode);
        if (css.Length == 0
            || !adBlockEnabled
            || tab.IsClosed
            || tab.Core != core
            || IsExceptionHost(HostFromUrl(core.Source))) return;
        await core.ExecuteScriptAsync(CreateCosmeticStyleScript(css, pageUrl));
    }

    private static string CreateCosmeticStyleScript(string css, string pageUrl)
    {
        var serializedCss = JsonSerializer.Serialize(css);
        var serializedPageUrl = JsonSerializer.Serialize(pageUrl);
        return "(() => {"
            + "if (window.__mishaAdBlockEnabled === false || !document.documentElement) return;"
            + $"if (new URL(location.href).href !== new URL({serializedPageUrl}).href) return;"
            + "const id='__misha_adblock_remote_style';"
            + "let style=document.getElementById(id);"
            + "if (!style) { style=document.createElement('style'); style.id=id; document.documentElement.appendChild(style); }"
            + $"style.textContent={serializedCss};"
            + "})();";
    }

    private async Task OnNavigationCompletedAsync(
        BrowserTab tab,
        CoreWebView2 core,
        bool isSuccess,
        CoreWebView2WebErrorStatus webErrorStatus)
    {
        if (isClosing || tab.IsClosed || !ReferenceEquals(tab.Core, core)) return;
        var navigationGeneration = tab.DocumentNavigationGeneration;
        tab.IsLoading = false;
        if (tab.IgnoreNextExternalFailure
            && webErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted)
        {
            tab.IgnoreNextExternalFailure = false;
            tab.StatusText = tab.ExternalNavigationStatus;
            tab.ExternalNavigationStatus = string.Empty;
        }
        else if (isSuccess)
        {
            tab.StatusText = "Ready";
            if (!tab.IsStartPage)
            {
                string source;
                try { source = core.Source; }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                UpdateTabUrlFromSource(tab, source);
                ApplySitePreferences(tab, persist: false);
                AddHistory(tab);
                await RefreshTabMediaPermissionStateAsync(tab, navigationGeneration);
                if (isClosing
                    || tab.IsClosed
                    || !ReferenceEquals(tab.Core, core)
                    || tab.DocumentNavigationGeneration != navigationGeneration)
                {
                    return;
                }
            }
            tab.ConnectionState = tab.IsStartPage
                ? ConnectionState.Local
                : BrowserPolicy.IsLocalFileUrl(tab.Url)
                    ? ConnectionState.LocalFile
                    : IsTrustedExtensionPage(tab.Url, tab.ExtensionOriginId)
                        ? ConnectionState.Local
                        : Uri.TryCreate(tab.Url, UriKind.Absolute, out var successfulUri)
                            && successfulUri.Scheme == Uri.UriSchemeHttps
                                ? ConnectionState.Secure
                                : ConnectionState.Insecure;
        }
        else
        {
            tab.StatusText = FriendlyNavigationError(webErrorStatus);
            tab.ConnectionState = IsCertificateError(webErrorStatus)
                ? ConnectionState.CertificateError
                : ConnectionState.Failed;
        }

        if (activeTab == tab)
        {
            SyncAddressBar();
            UpdateNavigationChrome();
            UpdateStatus();
        }
        UpdateTabHeader(tab);
        if ((activeTab != tab || isWindowMinimized) && !IsBackgroundProtected(tab))
        {
            QueueBackgroundTabReduction(tab);
        }
    }

    private void OnSourceChanged(BrowserTab tab, CoreWebView2SourceChangedEventArgs e)
    {
        if (tab.IsStartPage) return;
        var source = tab.Core!.Source;
        var keepAwakeChanged = UpdateTabUrlFromSource(tab, source);
        ApplySitePreferences(tab, persist: false);
        if (!e.IsNewDocument) AddHistory(tab);
        if (keepAwakeChanged) UpdateTabHeader(tab);
        if (activeTab == tab && !addressBarEditing)
        {
            SyncAddressBar();
            UpdateSiteInfo();
        }
    }

    private void OnDocumentTitleChanged(BrowserTab tab)
    {
        var title = tab.IsStartPage ? (isPrivateMode ? "Incognito" : "New tab") : tab.Core!.DocumentTitle;
        var sanitizedTitle = BrowserStateStore.SanitizeTitle(title);
        tab.Title = sanitizedTitle.Length == 0 ? HostFromUrl(tab.Url) : sanitizedTitle;
        UpdateTabHeader(tab);
        UpdateHistoryTitle(tab);
        if (activeTab == tab) UpdateWindowTitle();
    }

    private void OnNewWindowRequested(
        BrowserTab sourceTab,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        CoreWebView2Deferral deferral;
        try { deferral = e.GetDeferral(); }
        catch (Exception error)
        {
            try { e.Handled = true; }
            catch { }
            stateStore.Log("Could not defer popup creation", error);
            if (!isClosing) ShowTransientStatus("Blocked a popup that could not be opened safely");
            return;
        }
        RunUiTask(
            () => OnNewWindowRequestedAsync(sourceTab, e, deferral),
            "Could not open the popup");
    }

    private async Task OnNewWindowRequestedAsync(
        BrowserTab sourceTab,
        CoreWebView2NewWindowRequestedEventArgs e,
        CoreWebView2Deferral deferral)
    {
        BrowserTab? popup = null;
        var popupAttached = false;
        var sourceCore = sourceTab.Core;
        var sourceGeneration = sourceTab.InitializationGeneration;
        string? sourceDocument = null;
        try
        {
            sourceDocument = sourceCore?.Source;
            var popupSource = e.OriginalSourceFrameInfo.Source;
            var trustedCallPopup = CommunicationCompatibilityPolicy.IsTrustedPopup(
                popupSource,
                e.Uri);
            var trustedExtensionId = IsTrustedExtensionPage(
                    popupSource,
                    sourceTab.ExtensionOriginId)
                && IsTrustedExtensionPage(e.Uri, sourceTab.ExtensionOriginId)
                ? sourceTab.ExtensionOriginId
                : null;
            if (isClosing
                || (!BrowserPolicy.IsSafeTopLevelUrl(e.Uri)
                    && !BrowserPolicy.IsPopupBootstrapUrl(e.Uri)
                    && trustedExtensionId is null))
            {
                e.Handled = true;
                if (!e.IsUserInitiated) ShowTransientStatus("Blocked a scripted popup");
                return;
            }

            if (adBlockEnabled
                && !trustedCallPopup
                && trustedExtensionId is null
                && !IsExceptionHost(HostFromUrl(sourceTab.Url))
                && !IsExceptionHost(HostFromUrl(e.Uri))
                && adBlocker.ShouldBlockPopup(
                    e.Uri,
                    sourceTab.Url,
                    cacheEvaluation: !isPrivateMode))
            {
                e.Handled = true;
                sourceTab.BlockedRequestCount++;
                if (sourceTab.BlockedRequestCount == 1)
                {
                    ShowTransientStatus("Shield blocked a cross-site popup");
                }
                if (activeTab == sourceTab) UpdateStatus();
                return;
            }

            if (!e.IsUserInitiated && !trustedCallPopup && trustedExtensionId is null)
            {
                e.Handled = true;
                ShowTransientStatus("Blocked a scripted popup");
                return;
            }

            popup = await OpenNewTabAsync(
                e.Uri,
                activate: true,
                waitForPopupNavigation: true,
                canScriptClose: true,
                trustedExtensionId: trustedExtensionId);
            if (popup?.Core is null)
            {
                e.Handled = true;
                if (popup is not null && !popup.IsClosed)
                {
                    CloseTab(popup, remember: false, ensureReplacement: false);
                }
                popup = null;
                return;
            }

            if (isClosing
                || sourceTab.IsClosed
                || sourceTab.Core != sourceCore
                || sourceTab.InitializationGeneration != sourceGeneration
                || !string.Equals(sourceCore?.Source, sourceDocument, StringComparison.Ordinal))
            {
                e.Handled = true;
                CloseTab(popup, remember: false, ensureReplacement: false);
                popup = null;
                return;
            }

            e.NewWindow = popup.Core;
            e.Handled = true;
            popupAttached = true;
            popup.DeferResourceFilteringUntilPopupAttached = false;
            if (adBlockEnabled)
            {
                // WebView2 requires resource filters for popup contents to be
                // added only after the CoreWebView2 has been assigned here.
                await InstallAdBlockFilteringAsync(popup);
            }
            popup.Url = e.Uri;
            popup.IsStartPage = false;
            popup.StatusText = "Loading\u2026";
            UpdateTabHeader(popup);
        }
        catch (Exception error)
        {
            try { e.Handled = true; }
            catch { }
            if (popupAttached)
            {
                // Do not complete the WebView2 deferral with a partially
                // configured popup. Once NewWindow has been assigned, a later
                // filter/setup failure must still fail closed.
                try { e.NewWindow = null; }
                catch { }
                popupAttached = false;
            }
            if (popup is not null && !popup.IsClosed)
            {
                CloseTab(popup, remember: false, ensureReplacement: false);
            }
            stateStore.Log("Popup creation failed", error);
            ShowTransientStatus("Could not open the popup");
        }
        finally
        {
            try { deferral.Complete(); }
            catch (Exception error) { stateStore.Log("Could not complete popup creation", error); }
        }
    }

    private void OnProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedKind failedKind)
    {
        if (isClosing || tab.IsClosed) return;
        if (isDomFullScreen
            && (activeTab == tab
                || failedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited))
        {
            SetDomFullScreen(false);
        }
        StopFindSession(tab);
        tab.ReaderModeActive = false;

        switch (failedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                FailActiveDownloadsAfterBrowserProcessExit();
                InvalidateBrowserEnvironment();
                foreach (var item in tabs)
                {
                    ReplaceTabView(item);
                    item.StatusText = "Browser process stopped";
                    item.ConnectionState = ConnectionState.Failed;
                    ShowTabOverlay(
                        item,
                        "The browser process stopped",
                        "Your tabs are preserved and can be restored.",
                        "Restart browser",
                        RestartBrowserAsync);
                }
                UpdateNavigationChrome();
                UpdateStatus();
                break;

            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                ReplaceTabView(tab);
                tab.StatusText = "Page crashed";
                tab.ConnectionState = ConnectionState.Failed;
                ShowTabOverlay(
                    tab,
                    "This page stopped working",
                    "Reload the tab to create a fresh page process.",
                    "Reload tab",
                    () => RecreateTabAsync(tab));
                UpdateTabHeader(tab);
                UpdateStatus();
                break;

            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                tab.StatusText = "Page not responding";
                if (activeTab == tab) ShowTransientStatus("This page is not responding");
                break;
        }
    }

    private int FailActiveDownloadsAfterBrowserProcessExit()
    {
        var interruptedCount = 0;
        foreach (var download in downloads.Where(item => !item.IsTerminal).ToArray())
        {
            // Once the browser process is gone these COM operations cannot
            // make further progress or reliably raise StateChanged. Detach
            // first, then leave an honest terminal record for the downloads UI.
            DetachDownloadHandlers(download);
            download.IsTerminal = true;
            download.State = "Interrupted";
            download.CanResume = false;
            download.EstimatedEndTimeText = string.Empty;
            download.InterruptReason = "Browser process stopped";
            interruptedCount++;
        }

        foreach (var item in tabs)
        {
            item.ActiveDownloads = 0;
        }

        if (interruptedCount == 0) return 0;

        TrimDownloadRecords();
        BeginDownloadGracePeriod();
        RefreshDownloadsUiNow();
        return interruptedCount;
    }

    private static long SafeDownloadBytes(ulong? value)
    {
        var bytes = value.GetValueOrDefault();
        return bytes > long.MaxValue ? long.MaxValue : (long)bytes;
    }

    private static long SafeDownloadBytes(long? value)
    {
        return Math.Max(0, value.GetValueOrDefault());
    }

    private void OnChromeStoreWebMessage(
        BrowserTab tab,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        var channel = tab.ChromeStoreInstallChannel;
        var currentUrl = tab.Core?.Source ?? tab.Url;
        var extensionId = BrowserExtensions.ParseChromeWebStoreListingExtensionId(currentUrl);
        if (channel is null
            || extensionId is null
            || !BrowserExtensions.IsChromeWebStoreOrigin(e.Source))
        {
            return;
        }

        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        var expectedMessage = string.Concat(channel, ":", extensionId);
        if (!message.Equals(expectedMessage, StringComparison.Ordinal)) return;
        QueueChromeStoreExtensionInstall(tab, extensionId);
    }

    private bool TryHandleChromeStoreExtensionDownload(
        BrowserTab tab,
        CoreWebView2DownloadStartingEventArgs e)
    {
        var pageUrl = tab.Core?.Source ?? tab.Url;
        var extensionId = BrowserExtensions.MatchChromeWebStoreInstallRequest(
            pageUrl,
            e.DownloadOperation.Uri);
        if (extensionId is null) return false;

        e.Cancel = true;
        e.Handled = true;
        try { e.DownloadOperation.Cancel(); }
        catch { }
        QueueChromeStoreExtensionInstall(tab, extensionId);
        return true;
    }

    private void QueueChromeStoreExtensionInstall(BrowserTab tab, string extensionId)
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Extensions are unavailable in private windows");
            return;
        }
        if (isClosing
            || closeWhenExtensionOperationsFinish
            || tab.IsClosed
            || tab.Core is null)
        {
            return;
        }
        if (!installingChromeStoreExtensionIds.Add(extensionId))
        {
            ShowTransientStatus("That extension is already being installed");
            return;
        }

        var profile = tab.Core.Profile;
        RunUiTask(
            () => InstallChromeStoreExtensionFromPageAsync(profile, extensionId),
            "Could not install the Chrome Web Store extension");
    }

    private async Task InstallChromeStoreExtensionFromPageAsync(
        CoreWebView2Profile profile,
        string extensionId)
    {
        try
        {
            var cancellationToken = chromeStoreInstallCancellation.Token;
            cancellationToken.ThrowIfCancellationRequested();
            var installedExtensions = await profile.GetBrowserExtensionsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (installedExtensions.Any(extension =>
                extension.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase)))
            {
                ShowTransientStatus("That extension is already installed; use Extensions… to manage it");
                return;
            }

            ShowTransientStatus("Preparing extension…", false);
            await InstallPreparedExtensionAsync(
                profile,
                this,
                cancellationToken,
                () => browserExtensions.DownloadFromChromeWebStoreAsync(
                    extensionId,
                    environment?.BrowserVersionString ?? string.Empty,
                    cancellationToken));
            if (extensionsManager is { IsDisposed: false } manager)
            {
                await manager.RefreshAfterExternalChangeAsync();
            }
            ShowTransientStatus("Extension installed");
        }
        catch (OperationCanceledException)
        {
            if (!isClosing) ShowTransientStatus("Extension installation canceled");
        }
        catch (Exception error)
        {
            if (isClosing) return;
            var friendlyError = CreateFriendlyExtensionError(error);
            stateStore.Log("Chrome Web Store button installation failed", friendlyError);
            ShowTransientStatus("Extension installation failed");
            MessageBox.Show(
                this,
                friendlyError.Message,
                "Extension installation failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            installingChromeStoreExtensionIds.Remove(extensionId);
            ReleaseBrowserEnvironmentIfIdle();
        }
    }

    private void OnDownloadStarting(BrowserTab tab, CoreWebView2DownloadStartingEventArgs e)
    {
        if (TryHandleChromeStoreExtensionDownload(tab, e)) return;
        if (downloads.Count(item => !item.IsTerminal) >= MaximumActiveDownloads)
        {
            e.Cancel = true;
            e.Handled = true;
            ShowTransientStatus("Too many active downloads; finish or cancel one first");
            return;
        }

        // MishaWeb owns the download surface and lifecycle from this point.
        // Suppress WebView2's separate default dialog so each download appears
        // exactly once and remains governed by the browser's close safeguards.
        e.Handled = true;
        var operation = e.DownloadOperation;
        var entry = new DownloadEntry(
            SanitizeDownloadDisplayName(Path.GetFileName(e.ResultFilePath)),
            e.ResultFilePath,
            operation.Uri,
            DateTime.Now,
            "Downloading");
        entry.Operation = operation;
        entry.BytesReceived = SafeDownloadBytes(operation.BytesReceived);
        entry.TotalBytesToReceive = SafeDownloadBytes(operation.TotalBytesToReceive);
        entry.CancelAction = operation.Cancel;
        entry.PauseAction = operation.Pause;
        entry.ResumeAction = operation.Resume;
        entry.CanResume = operation.CanResume;
        entry.EstimatedEndTimeText = FormatEstimatedEndTime(operation.EstimatedEndTime);
        entry.InterruptReason = MapDownloadInterruptReason(operation.InterruptReason);
        downloads.Insert(0, entry);
        TrimDownloadRecords();
        downloadsButtonAutoVisible = true;
        downloadGraceTimer.Stop();
        UpdateResponsiveToolbar();
        tab.ActiveDownloads++;
        tab.ConsecutiveSuspendFailures = 0;
        ApplyLiveTabMemoryTarget(tab, tab == activeTab && !isWindowMinimized);
        UpdateStatus();

        var weakTab = new WeakReference<BrowserTab>(tab);
        EventHandler<object>? stateChanged = null;
        stateChanged = (_, _) =>
        {
            entry.BytesReceived = SafeDownloadBytes(operation.BytesReceived);
            entry.TotalBytesToReceive = SafeDownloadBytes(operation.TotalBytesToReceive);
            entry.FilePath = operation.ResultFilePath;
            entry.FileName = SanitizeDownloadDisplayName(
                Path.GetFileName(operation.ResultFilePath));
            var isUserPaused = operation.State == CoreWebView2DownloadState.Interrupted
                && operation.InterruptReason.ToString() is "UserCanceled" or "UserPaused"
                && operation.CanResume;
            entry.State = operation.State switch
            {
                CoreWebView2DownloadState.Completed => "Complete",
                CoreWebView2DownloadState.Interrupted when isUserPaused => "Paused",
                CoreWebView2DownloadState.Interrupted => "Interrupted",
                _ => "Downloading"
            };
            entry.CanResume = operation.CanResume;
            entry.EstimatedEndTimeText = FormatEstimatedEndTime(operation.EstimatedEndTime);
            entry.InterruptReason = isUserPaused ? "Paused by user" : MapDownloadInterruptReason(operation.InterruptReason);

            var isTerminal = operation.State == CoreWebView2DownloadState.Completed
                || (operation.State == CoreWebView2DownloadState.Interrupted && !isUserPaused);
            if (!entry.IsTerminal && isTerminal)
            {
                entry.IsTerminal = true;
                entry.CancelAction = null;
                entry.PauseAction = null;
                entry.ResumeAction = null;
                DetachDownloadHandlers(entry);
                if (weakTab.TryGetTarget(out var downloadTab) && !downloadTab.IsClosed)
                {
                    downloadTab.ActiveDownloads = Math.Max(0, downloadTab.ActiveDownloads - 1);
                    ApplyLiveTabMemoryTarget(
                        downloadTab,
                        downloadTab == activeTab && !isWindowMinimized);
                    if (downloadTab != activeTab || isWindowMinimized)
                    {
                        QueueBackgroundTabReduction(downloadTab);
                    }
                }
                if (operation.State == CoreWebView2DownloadState.Completed)
                {
                    ShowTransientStatus($"Downloaded {entry.FileName}");
                }
                else
                {
                    ShowTransientStatus($"Download interrupted: {entry.FileName}");
                }
                TrimDownloadRecords();
                if (tabs.All(item => item.ActiveDownloads == 0)) BeginDownloadGracePeriod();
                ReleaseBrowserEnvironmentIfIdle();
            }
            RefreshDownloadsUi();
        };
        entry.StateChangedHandler = stateChanged;
        operation.StateChanged += stateChanged;
        EventHandler<object> bytesChanged = (_, _) =>
        {
            entry.BytesReceived = SafeDownloadBytes(operation.BytesReceived);
            entry.TotalBytesToReceive = SafeDownloadBytes(operation.TotalBytesToReceive);
            RefreshDownloadsUi();
        };
        entry.BytesReceivedChangedHandler = bytesChanged;
        operation.BytesReceivedChanged += bytesChanged;
        EventHandler<object> estimatedEndTimeChanged = (_, _) =>
        {
            entry.EstimatedEndTimeText = FormatEstimatedEndTime(operation.EstimatedEndTime);
            RefreshDownloadsUi();
        };
        entry.EstimatedEndTimeChangedHandler = estimatedEndTimeChanged;
        operation.EstimatedEndTimeChanged += estimatedEndTimeChanged;
        RefreshDownloadsUi();
    }

    private void OnLaunchingExternalUriScheme(BrowserTab tab, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        string requestedUri;
        string? initiatingOrigin;
        bool isUserInitiated;
        var documentGeneration = tab.DocumentNavigationGeneration;
        try
        {
            // Default to deny before dereferencing any apartment-bound event
            // properties. If the browser process disconnects during the prompt,
            // this request must not silently fall through to the OS handler.
            e.Cancel = true;
            isUserInitiated = e.IsUserInitiated;
            requestedUri = e.Uri;
            initiatingOrigin = e.InitiatingOrigin;
        }
        catch (Exception error)
        {
            stateStore.Log("Could not inspect an external-app request", error);
            if (!isClosing) ShowTransientStatus("Blocked an external-app request that could not be verified");
            return;
        }

        if (!isUserInitiated)
        {
            ShowTransientStatus("Blocked an external-app request without a user gesture");
            return;
        }

        var scheme = Uri.TryCreate(requestedUri, UriKind.Absolute, out var uri) ? uri.Scheme : "external";
        var originValue = NormalizePermissionOrigin(initiatingOrigin)
            ?? NormalizePermissionOrigin(tab.Url)
            ?? HostFromUrl(tab.Url);
        var origin = SanitizeExtensionPromptText(originValue, 160);
        var answer = MessageBox.Show(
            this,
            $"Allow {origin} to open the '{scheme}' app?",
            "Open an external app?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        var allowed = answer == DialogResult.Yes;
        if (isClosing
            || tab.IsClosed
            || tab.DocumentNavigationGeneration != documentGeneration)
        {
            return;
        }
        try
        {
            e.Cancel = !allowed;
        }
        catch (Exception error)
        {
            stateStore.Log("Could not complete an external-app request", error);
            if (!isClosing) ShowTransientStatus("The external-app request was canceled");
            return;
        }
        tab.IgnoreNextExternalFailure = true;
        tab.ExternalNavigationStatus = allowed ? "Opened external app" : "External app canceled";
    }

    private async Task RecreateTabAsync(BrowserTab tab)
    {
        if (isClosing || tab.IsClosed) return;
        if (environment is null)
        {
            await RestartBrowserAsync();
            return;
        }

        var targetUrl = tab.Url;
        var showStartPage = tab.IsStartPage || targetUrl.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase);
        ReplaceTabView(tab);
        if (showStartPage)
        {
            ShowStartPage(tab, false);
            return;
        }
        var navigationRequestId = ++tab.NavigationRequestId;
        if (!await InitializeTabAsync(tab, requireActive: true))
        {
            PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
            return;
        }
        if (!CanContinueRequiredTabInitialization(tab))
        {
            PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
            return;
        }
        if (tab.NavigationRequestId != navigationRequestId
            || tab.IsStartPage
            || !BrowserPolicy.UrlEquals(tab.Url, targetUrl)) return;
        NavigateTabCore(tab, targetUrl);
    }

    private Task RestartBrowserForUpdateAsync()
    {
        if (!browserRuntimeUpdateAvailable) return Task.CompletedTask;
        var result = Program.RequestBrowserProcessRestart(this);
        switch (result)
        {
            case Program.BrowserRestartRequestResult.Started:
                break;
            case Program.BrowserRestartRequestResult.Canceled:
                ShowTransientStatus("Browser update restart canceled");
                break;
            case Program.BrowserRestartRequestResult.StateChanged:
                ShowTransientStatus("Browser windows changed — review them and try the restart again");
                break;
            case Program.BrowserRestartRequestResult.ExtensionOperationInProgress:
                ShowTransientStatus("Finish or cancel the extension operation before restarting MishaWeb");
                extensionsManager?.Activate();
                break;
            case Program.BrowserRestartRequestResult.SessionSaveFailed:
                ShowTransientStatus("Restart canceled because the current session could not be saved");
                MessageBox.Show(
                    this,
                    "MishaWeb could not safely save your open tabs, so the browser update restart was canceled and every window was left open.",
                    "Session could not be saved",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;
            case Program.BrowserRestartRequestResult.LaunchFailed:
                ShowTransientStatus("Could not start the updated browser process");
                MessageBox.Show(
                    this,
                    "MishaWeb could not start its replacement process. Your windows were left open and the update restart was canceled.",
                    "Could not restart MishaWeb",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                break;
            default:
                ShowTransientStatus("The browser update restart is unavailable");
                break;
        }
        return Task.CompletedTask;
    }

    private async Task RestartBrowserAsync()
    {
        if (isClosing || recoveringBrowser) return;
        recoveringBrowser = true;
        ShowTransientStatus("Restarting browser\u2026", false);

        try
        {
            foreach (var tab in tabs) ReplaceTabView(tab);
            InvalidateBrowserEnvironment();
            await GetOrCreateEnvironmentAsync();
            if (isClosing) return;

            var tabToRestore = activeTab ?? tabs.FirstOrDefault();
            foreach (var tab in tabs.Where(tab => tab != tabToRestore).ToArray())
            {
                if (tab.IsStartPage || tab.Url.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase))
                {
                    ShowStartPage(tab, false);
                }
                else
                {
                    MarkTabDiscarded(tab, "Waiting after browser recovery");
                }
            }

            if (tabToRestore is not null && !tabToRestore.IsClosed)
            {
                if (tabToRestore.IsStartPage
                    || tabToRestore.Url.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase))
                {
                    ShowStartPage(tabToRestore, false);
                }
                else
                {
                    var targetUrl = tabToRestore.Url;
                    var navigationRequestId = ++tabToRestore.NavigationRequestId;
                    var initialized = await InitializeTabAsync(tabToRestore, requireActive: true);
                    if (!initialized)
                    {
                        PreserveCanceledInitializationAsDiscarded(tabToRestore, navigationRequestId);
                    }
                    else if (!CanContinueRequiredTabInitialization(tabToRestore))
                    {
                        PreserveCanceledInitializationAsDiscarded(tabToRestore, navigationRequestId);
                    }
                    else if (tabToRestore.NavigationRequestId == navigationRequestId
                        && BrowserPolicy.UrlEquals(tabToRestore.Url, targetUrl))
                    {
                        NavigateTabCore(tabToRestore, targetUrl);
                    }
                }
            }

            if (tabs.Count == 0)
            {
                await OpenNewTabAsync(StartPage.Url);
            }
            else if (tabToRestore is not null && activeTab == tabToRestore && !tabToRestore.IsClosed)
            {
                ActivateTab(tabToRestore, focusPage: false);
            }
            browserRuntimeUpdateAvailable = false;
            restartBrowserForUpdateMenuItem.Visible = false;
            ShowTransientStatus("Browser restored");
        }
        catch (Exception error)
        {
            InvalidateBrowserEnvironment();
            stateStore.Log("Browser recovery failed", error);
            foreach (var tab in tabs)
            {
                ShowTabOverlay(
                    tab,
                    "Browser recovery failed",
                    error.Message,
                    "Try again",
                    RestartBrowserAsync);
            }
            ShowTransientStatus("Browser recovery failed");
        }
        finally
        {
            recoveringBrowser = false;
            ReleaseBrowserEnvironmentIfIdle();
            RefreshMemorySweepTimer();
        }
    }

    private void ReplaceTabView(BrowserTab tab)
    {
        ClearTabHoverStatus(tab);
        StopFindSession(tab);
        CancelPermissionsForTab(tab);
        CaptureTabPresentationState(tab);
        tab.ReaderModeActive = false;
        tab.NavigationRequestId++;
        tab.DocumentNavigationGeneration++;
        tab.InitializationGeneration++;
        tab.MotionPolicyGeneration++;
        tab.AllowedPopupBootstrapUrl = null;
        var oldView = tab.View;
        if (tab.Core is { } oldCore)
        {
            if (tab.NoMotionScriptId is not null)
            {
                try { oldCore.RemoveScriptToExecuteOnDocumentCreated(tab.NoMotionScriptId); }
                catch (Exception error) { stateStore.Log("Could not remove the no-motion page policy", error); }
                tab.NoMotionScriptId = null;
            }
            RemoveAdBlockPageScripts(tab, oldCore);
        }
        RemoveAdBlockFiltering(tab);
        AbandonPendingFrameSetups(tab);
        tab.ReleaseTrackedFrames();
        tab.DetachCoreEventHandlers();
        tab.ReleaseCleanLinkMenuItem();
        tab.ChromeStoreInstallChannel = null;
        tab.ContextLinkTarget = null;
        tab.PendingFileNavigationUrl = null;
        tab.View = null;
        if (oldView is not null)
        {
            oldView.Visible = false;
            tab.Host.Controls.Remove(oldView);
            oldView.Dispose();
        }
        tab.Overlay.BringToFront();
        tab.IsSuspended = false;
        tab.IsDiscarded = false;
        tab.IsLoading = false;
        tab.IsInitializing = false;
        tab.IsAudible = false;
        tab.MicrophoneAllowedOrigins.Clear();
        tab.CameraAllowedOrigins.Clear();
        tab.PendingMediaPermissionRequests = 0;
        tab.PendingMediaPermissionOrigin = null;
        tab.PendingMediaPermissionRefreshes = 0;
        tab.ConsecutiveSuspendFailures = 0;
        tab.SupportsMemoryUsageTarget = true;
        tab.MemoryUsageTargetLevel = null;
        tab.InitializationTask = null;
        ReleaseBrowserEnvironmentIfIdle();
    }

    private void ReleaseBrowserEnvironmentIfIdle()
    {
        if (recoveringBrowser
            || openingExtensionsManager
            || installingChromeStoreExtensionIds.Count > 0
            || downloads.Any(item => !item.IsTerminal)
            || extensionProfileController is not null
            || environmentTask is { IsCompleted: false }
            || tabs.Any(item => !item.IsClosed
                && (item.View is not null || item.InitializationTask is not null)))
        {
            return;
        }

        InvalidateBrowserEnvironment();
        memoryTimer.Stop();
    }

    private void DisposeExtensionProfileController(CoreWebView2Controller? expectedController = null)
    {
        var controller = extensionProfileController;
        if (expectedController is not null && !ReferenceEquals(controller, expectedController))
        {
            try { expectedController.Close(); }
            catch { }
            return;
        }
        extensionProfileController = null;
        if (controller is null) return;
        try { controller.Close(); }
        catch { }
    }

    private static void CaptureTabPresentationState(BrowserTab tab)
    {
        try
        {
            if (tab.View is not { CoreWebView2: not null } view) return;
            tab.ZoomFactor = view.ZoomFactor;
            tab.IsMuted = view.CoreWebView2.IsMuted;
        }
        catch
        {
            // A failed browser process may no longer expose controller state.
        }
    }

    private void ShowStartPage(BrowserTab? tab, bool focusAddressBar = true)
    {
        if (tab is null) return;
        HideAddressSuggestions();
        tab.NavigationRequestId++;
        tab.KeepAwake = false;
        tab.MicrophoneAllowedOrigins.Clear();
        tab.CameraAllowedOrigins.Clear();
        tab.FrameOrigins.Clear();
        tab.PendingMediaPermissionRequests = 0;
        tab.PendingMediaPermissionOrigin = null;
        tab.PendingMediaPermissionRefreshes = 0;
        var wasPinned = tab.IsPinned;
        tab.IsPinned = false;
        if (wasPinned) ReorderTabForPinState(tab);
        tab.ReaderModeActive = false;
        tab.ZoomFactor = 1.0;
        tab.IsMuted = false;

        if (tab.View is not null) ReplaceTabView(tab);
        else
        {
            tab.InitializationGeneration++;
            tab.InitializationTask = null;
            tab.IsInitializing = false;
            ReleaseBrowserEnvironmentIfIdle();
            RefreshMemorySweepTimer();
        }
        tab.ZoomFactor = 1.0;
        tab.IsMuted = false;
        ShowNativeStartPage(tab);
        tab.IsStartPage = true;
        tab.IsDiscarded = false;
        tab.Url = StartPage.Url;
        tab.Title = isPrivateMode ? "Incognito" : "New tab";
        tab.StatusText = "Ready";
        tab.ConnectionState = ConnectionState.Local;
        tab.BlockedRequestCount = 0;
        UpdateTabHeader(tab);
        if (activeTab == tab)
        {
            addressBarEditing = false;
            SyncAddressBar();
            UpdateWindowTitle();
            UpdateNavigationChrome();
            if (focusAddressBar) BeginInvoke(FocusStartPageOrAddressBar);
        }
    }

    private void ShowNativeStartPage(BrowserTab tab)
    {
        var startPageView = EnsureNativeStartPage(tab);
        tab.Overlay.Visible = false;
        if (tab.View is not null) tab.View.Visible = false;
        startPageView.SetQuickLinks(BuildStartPageLinks());
        startPageView.SetStatus(BuildStartPageStatus());
        startPageView.SetSearchProvider(searchProviderId);
        startPageView.SetPrivateMode(isPrivateMode);
        EnsureTabHostLayout(tab);
        startPageView.Visible = true;
        startPageView.BringToFront();
    }

    private IReadOnlyList<StartPageLink> BuildStartPageLinks()
    {
        if (!isStartPageLinksDirty && cachedStartPageLinks is not null) return cachedStartPageLinks;

        const int maximumLinks = BrowserStateStore.MaximumPinnedStartPageLinks;
        var links = new List<StartPageLink>(maximumLinks);
        var seenUrls = new HashSet<string>(BrowserPolicy.UrlComparer);

        void AddLink(string? title, string? url, string category)
        {
            if (links.Count >= maximumLinks
                || string.IsNullOrWhiteSpace(url)
                || !BrowserPolicy.IsHttpUrl(url)
                || !seenUrls.Add(url))
            {
                return;
            }

            var displayTitle = string.IsNullOrWhiteSpace(title) ? HostFromUrl(url) : title.Trim();
            links.Add(new StartPageLink(displayTitle, url, category));
        }

        if (!isPrivateMode)
        {
            foreach (var pinned in state.PinnedStartPageLinks)
            {
                AddLink(pinned.Title, pinned.Url, "Pinned");
                if (links.Count >= maximumLinks) break;
            }
        }

        foreach (var bookmark in state.Bookmarks)
        {
            AddLink(bookmark.Title, bookmark.Url, "Favorite");
            if (links.Count >= maximumLinks) break;
        }

        if (links.Count < maximumLinks)
        {
            foreach (var history in state.History)
            {
                AddLink(history.Title, history.Url, "Recent");
                if (links.Count >= maximumLinks) break;
            }
        }

        cachedStartPageLinks = links;
        isStartPageLinksDirty = false;
        return links;
    }

    private void InvalidateStartPageLinks()
    {
        isStartPageLinksDirty = true;
        cachedStartPageLinks = null;
    }

    private StartPageStatus BuildStartPageStatus()
    {
        var resourceMode = ultraLightEnabled
            ? "Ultra-light mode"
            : memorySaverEnabled ? "Memory saver on" : "Memory saver off";
        var sleeping = tabs.Count(item => item.IsSuspended || item.IsDiscarded);
        var tabCount = tabs.Count == 1 ? "1 tab open" : $"{tabs.Count} tabs open";
        if (sleeping > 0) tabCount += $" \u00B7 {sleeping} resting";
        return new StartPageStatus(resourceMode, tabCount);
    }

    private void RefreshNativeStartPages(bool refreshLinks = false)
    {
        var status = BuildStartPageStatus();
        var links = refreshLinks ? BuildStartPageLinks() : null;
        foreach (var tab in tabs.Where(item => item.IsStartPage && !item.IsClosed))
        {
            if (tab.StartPageView is null) continue;
            tab.StartPageView.SetPrivateMode(isPrivateMode);
            tab.StartPageView.SetStatus(status);
            if (links is not null) tab.StartPageView.SetQuickLinks(links);
        }
    }

    private void HandleStartPageAction(StartPageAction action, Control? anchor = null)
    {
        switch (action)
        {
            case StartPageAction.CycleResourceMode:
                ShowResourceModeMenu(anchor);
                break;
            case StartPageAction.ShowTabs:
                ShowTabListMenu();
                break;
        }
    }

    private static void NavigateTabCore(BrowserTab tab, string targetUrl)
    {
        var core = tab.Core ?? throw new InvalidOperationException("The tab browser is not available.");
        tab.PendingFileNavigationUrl = BrowserPolicy.IsLocalFileUrl(targetUrl) ? targetUrl : null;
        core.Navigate(targetUrl);
    }

    private async Task NavigateActiveAsync(string input)
    {
        var tab = activeTab;
        if (tab is null)
        {
            ShowTransientStatus("Open a tab before navigating");
            return;
        }
        var resolution = BrowserPolicy.ResolveAddress(input, searchProviderId);
        if (resolution.Error is not null)
        {
            ShowTransientStatus(resolution.Error);
            return;
        }

        addressBarEditing = false;
        if (resolution.IsStartPage)
        {
            ShowStartPage(tab);
            return;
        }

        var targetUrl = resolution.Url!;
        var navigationRequestId = ++tab.NavigationRequestId;
        tab.IsDiscarded = false;
        tab.IsStartPage = false;
        tab.Url = targetUrl;
        tab.KeepAwake = IsKeepAwakeHost(targetUrl);
        if (tab.Core is null && !await InitializeTabAsync(tab, requireActive: true))
        {
            PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
            return;
        }
        if (isClosing || tab.IsClosed) return;
        if (!CanContinueRequiredTabInitialization(tab))
        {
            PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
            return;
        }
        if (tab.NavigationRequestId != navigationRequestId
            || tab.IsStartPage
            || !BrowserPolicy.UrlEquals(tab.Url, targetUrl)
            || tab.Core is null) return;

        try
        {
            NavigateTabCore(tab, targetUrl);
        }
        catch (Exception error)
        {
            stateStore.Log("Navigation failed to start", error);
            ShowTransientStatus("Navigation could not start");
        }
    }

    private void ActivateTab(
        BrowserTab tab,
        bool focusPage = true,
        bool restoreDiscarded = true)
    {
        if (tab.IsClosed || !tabs.Contains(tab)) return;
        var previousTab = activeTab != tab ? activeTab : null;
        if (previousTab is not null)
        {
            previousTab.LastActiveUtc = DateTime.UtcNow;
            StopFindSession(previousTab);
        }
        ResumeTab(tab);

        activeTab = tab;
        if (!restoringSession) mruTabs.ObserveActivation(tab);
        if (tab.IsStartPage)
        {
            ShowNativeStartPage(tab);
        }
        var preserveBackdropFrame = previousTab?.IsStartPage == true && tab.IsStartPage;
        if (preserveBackdropFrame)
        {
            // Let the destination page take a shared-frame lease before the old
            // page releases its own, avoiding a dispose/re-render cycle.
            tab.Host.Visible = true;
        }
        SyncDomFullScreenFromActiveTab();
        pageHost.SuspendLayout();
        try
        {
            if (previousTab is not null)
            {
                previousTab.Host.Visible = false;
                if (previousTab.View is not null) previousTab.View.Visible = false;
                UpdateTabHeader(previousTab);
            }

            if (!preserveBackdropFrame) tab.Host.Visible = true;
            if (tab.View is not null)
            {
                tab.View.Visible = !isWindowMinimized
                    && !tab.Overlay.Visible
                    && tab.StartPageView?.Visible != true;
            }
            UpdateTabHeader(tab);
            if (tabStrip.AutoScrollMinSize.Width > tabStrip.ClientSize.Width)
            {
                tabStrip.ScrollControlIntoView(tab.Header);
            }

            tab.Host.BringToFront();
            if (tab.Overlay.Visible)
            {
                tab.Overlay.BringToFront();
            }
            else if (tab.StartPageView?.Visible == true)
            {
                tab.StartPageView.BringToFront();
            }
            else
            {
                tab.View?.BringToFront();
            }

            EnsureTabHostLayout(tab);
        }
        finally
        {
            pageHost.ResumeLayout(true);
            pageHost.PerformLayout();
            EnsureTabHostLayout(tab);
        }
        tab.LastActiveUtc = DateTime.UtcNow;
        tab.ConsecutiveSuspendFailures = 0;
        addressBarEditing = false;
        SyncAddressBar();
        UpdateWindowTitle();
        UpdateNavigationChrome();
        UpdateStatus();
        UpdateChromeRowsForFullscreen();
        if (findBar.Visible && findBox.TextLength > 0 && tab.Core is not null)
        {
            RunUiTask(StartFindSession, "Find failed");
        }
        else if (findBar.Visible)
        {
            findMatchLabel.Text = string.Empty;
        }
        ApplyLiveTabMemoryTarget(tab, foreground: !isWindowMinimized);
        RefreshMemorySweepTimer();
        if (previousTab is not null) QueueBackgroundTabReduction(previousTab);

        if (restoreDiscarded && tab.IsDiscarded && !tab.IsInitializing)
        {
            RunUiTask(() => RestoreDiscardedTabAsync(tab, focusPage), "Could not reload the unloaded tab");
            return;
        }

        if (focusPage && !tab.IsInitializing && tab.StartPageView?.Visible == true)
        {
            BeginInvoke(() =>
            {
                if (activeTab == tab && !tab.IsClosed) FocusStartPageOrAddressBar();
            });
        }
        else if (focusPage && !tab.IsInitializing && tab.View is not null && tab.Core is not null)
        {
            BeginInvoke(() =>
            {
                if (activeTab == tab && !tab.IsClosed) tab.View?.Focus();
            });
        }
    }

    private async Task RestoreDiscardedTabAsync(BrowserTab tab, bool focusPage)
    {
        if (isClosing || tab.IsClosed || !tab.IsDiscarded) return;
        if (!CanContinueRequiredTabInitialization(tab)) return;
        if (tab.IsStartPage)
        {
            ShowStartPage(tab, focusPage);
            return;
        }

        var targetUrl = tab.Url;
        var navigationRequestId = ++tab.NavigationRequestId;
        var initialized = await InitializeTabAsync(tab, requireActive: true);
        if (!initialized || isClosing || tab.IsClosed)
        {
            PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
            return;
        }
        if (!CanContinueRequiredTabInitialization(tab))
        {
            PreserveCanceledInitializationAsDiscarded(tab, navigationRequestId);
            return;
        }
        if (tab.NavigationRequestId != navigationRequestId
            || tab.IsStartPage
            || !BrowserPolicy.UrlEquals(tab.Url, targetUrl)
            || tab.Core is null)
        {
            return;
        }
        NavigateTabCore(tab, targetUrl);
        RefreshMemorySweepTimer();
        if (focusPage && activeTab == tab) BeginInvoke(() => tab.View?.Focus());
    }

    private void MarkTabDiscarded(BrowserTab tab, string detail)
    {
        tab.NavigationRequestId++;
        tab.IsDiscarded = true;
        tab.IsSuspended = false;
        tab.IsLoading = false;
        tab.ReaderModeActive = false;
        tab.StatusText = "Unloaded to save memory";
        ShowTabOverlay(
            tab,
            "Tab unloaded",
            $"{detail}. It will reload when selected.",
            "Reload tab",
            () => RestoreDiscardedTabAsync(tab, focusPage: true));
        UpdateTabHeader(tab);
        if (!restoringSession)
        {
            RefreshNativeStartPages();
            TrimAllProcessMemory();
            RefreshMemorySweepTimer();
        }
    }

    private void UnloadActiveTab()
    {
        var tab = activeTab;
        if (tab is null)
        {
            ShowTransientStatus("There is no active tab to unload");
            return;
        }
        RunUiTask(() => UnloadTabAsync(tab), "Could not unload the tab");
    }

    private async Task UnloadTabAsync(BrowserTab tab)
    {
        if (tab.IsClosed || !tabs.Contains(tab)) return;
        if (!CanUnloadTab(tab))
        {
            ShowTransientStatus("This tab is still loading or has no page to unload");
            return;
        }

        if (activeTab == tab)
        {
            var replacement = mruTabs.Order.FirstOrDefault(item =>
                item != tab && !item.IsClosed && tabs.Contains(item));
            if (replacement is null)
            {
                replacement = tabs.FirstOrDefault(item => item != tab && item.IsStartPage && !item.IsClosed);
            }
            if (replacement is null)
            {
                replacement = await OpenNewTabAsync(StartPage.Url);
            }
            if (replacement is null || replacement == tab) return;
            ActivateTab(replacement, focusPage: false);
        }

        CaptureTabPresentationState(tab);
        ReplaceTabView(tab);
        MarkTabDiscarded(tab, "Released manually");
        ShowTransientStatus($"Unloaded {tab.Title}");
    }

    private static bool CanUnloadTab(BrowserTab tab)
    {
        return !tab.IsClosed
            && !tab.IsStartPage
            && tab.Core is not null
            && !tab.IsInitializing
            && !tab.IsLoading
            && tab.ActiveDownloads == 0
            && !tab.IsAudible
            && !IsBackgroundProtected(tab);
    }

    private void CloseTab(BrowserTab tab, bool remember = true, bool ensureReplacement = true)
    {
        var index = tabs.IndexOf(tab);
        if (index < 0) return;
        if (draggedTab == tab) CancelTabDrag();
        if (!isClosing && tab.ActiveDownloads > 0)
        {
            MessageBox.Show(
                this,
                tab.ActiveDownloads == 1
                    ? "This tab has a download in progress. Keep it open until the download finishes or cancel the download first."
                    : $"This tab has {tab.ActiveDownloads} downloads in progress. Keep it open until they finish or cancel them first.",
                "Download in progress",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            ShowDownloadsPopup();
            return;
        }
        var wasActive = activeTab == tab;

        if (remember && !tab.IsStartPage && BrowserPolicy.IsHttpUrl(tab.Url))
        {
            closedTabs.AddFirst(new ClosedTabEntry(tab.Title, tab.Url, DateTimeOffset.UtcNow));
            while (closedTabs.Count > BrowserStateStore.MaximumRecentlyClosed) closedTabs.RemoveLast();
            if (!isPrivateMode) ScheduleStateSave();
        }

        if (wasActive)
        {
            activeTab = null;
            findMatchLabel.Text = string.Empty;
        }
        StopFindSession(tab);
        CancelPermissionsForTab(tab);
        tab.IsClosed = true;
        tabs.RemoveAt(index);
        mruTabs.Remove(tab);
        tabStrip.Controls.Remove(tab.Header);
        pageHost.Controls.Remove(tab.Host);
        RemoveAdBlockFiltering(tab);
        AbandonPendingFrameSetups(tab);
        tab.Dispose();
        ReleaseBrowserEnvironmentIfIdle();
        RefreshNativeStartPages();
        ResizeTabHeaders();
        TrimAllProcessMemory();
        RefreshMemorySweepTimer();
        RunUiTask(
            async () =>
            {
                await Task.Delay(350);
                if (isClosing) return;
                TrimAllProcessMemory();
            },
            "Could not complete post-close memory trimming");

        if (isClosing || replacingWindow) return;
        if (tabs.Count == 0)
        {
            UpdateNavigationChrome();
            if (ensureReplacement)
            {
                RunUiTask(() => OpenNewTabAsync(StartPage.Url), "Could not open a replacement tab");
            }
            return;
        }

        if (wasActive) ActivateTab(tabs[Math.Min(index, tabs.Count - 1)]);
        else UpdateStatus();
    }

    private async Task RestoreClosedTabAsync()
    {
        if (closedTabs.First is null)
        {
            ShowTransientStatus("No recently closed tabs");
            return;
        }

        var closed = closedTabs.First.Value;
        await RestoreClosedEntryAsync(closed);
    }

    private void RestoreClosedEntry(ClosedTabEntry closed)
    {
        RunUiTask(
            () => RestoreClosedEntryAsync(closed),
            "Could not restore the tab");
    }

    private async Task RestoreClosedEntryAsync(ClosedTabEntry closed)
    {
        // Remove the entry before yielding so repeated clicks or shortcuts cannot
        // start duplicate restores. Put it back if creating the tab fails.
        if (!closedTabs.Remove(closed)) return;
        if (!isPrivateMode) ScheduleStateSave();

        var restored = false;
        try
        {
            restored = await OpenNewTabAsync(closed.Url) is not null;
        }
        finally
        {
            if (!restored && !closedTabs.Contains(closed))
            {
                closedTabs.AddFirst(closed);
                while (closedTabs.Count > BrowserStateStore.MaximumRecentlyClosed)
                {
                    closedTabs.RemoveLast();
                }
                if (!isPrivateMode) ScheduleStateSave();
            }
        }
    }

    private void QueueBackgroundTabReduction(BrowserTab tab)
    {
        var requestedMode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
        if (isClosing
            || restoringSession
            || tab.IsClosed
            || requestedMode == TabLifecycleMode.Off)
        {
            return;
        }

        if (requestedMode == TabLifecycleMode.Ultra && tab.IsStartPage)
        {
            ReleaseNativeStartPage(tab);
            return;
        }

        ApplyLiveTabMemoryTarget(tab, foreground: false);
        RefreshMemorySweepTimer();
        RunUiTask(
            async () =>
            {
                await Task.Delay(2500);
                if (isClosing || tab.IsClosed || (tab == activeTab && !isWindowMinimized)) return;
                CompactBackgroundTabMemory(tab);

                var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
                if (mode != TabLifecycleMode.Ultra) return;
                await Task.Delay(2500);
                if (isClosing || tab.IsClosed || (tab == activeTab && !isWindowMinimized)) return;
                var currentMode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
                if (currentMode != TabLifecycleMode.Ultra) return;
                var suspended = await TrySuspendTabAsync(
                    tab,
                    includeActiveTab: isWindowMinimized && tab == activeTab,
                    expectedMode: currentMode);
                if (!suspended) TryDiscardTabAfterFailedSuspend(tab);
            },
            "Could not rest a background tab");
    }

    private void CompactBackgroundTabMemory(BrowserTab tab)
    {
        if (isClosing
            || tab.IsClosed
            || tab.IsLoading
            || tab.IsInitializing
            || IsBackgroundProtected(tab)
            || (tab == activeTab && !isWindowMinimized))
        {
            return;
        }

        if (!IsTabAudible(tab) && tab.ActiveDownloads == 0 && tab.Core is { } core)
        {
            try
            {
                _ = core.ExecuteScriptAsync("try { window.gc && window.gc(); } catch (_) {}");
            }
            catch { }
        }
        SystemResourceInfo.TrimCurrentProcessWorkingSet();
    }

    private bool IsOrdinaryResidentCandidate(BrowserTab tab)
    {
        return !tab.IsClosed
            && !tab.IsStartPage
            && tabs.Contains(tab)
            && (tab.Core is not null || tab.IsInitializing);
    }

    private int GetProtectedResidentRank(BrowserTab target)
    {
        var rank = 0;
        if (activeTab is { } active && IsOrdinaryResidentCandidate(active))
        {
            if (ReferenceEquals(active, target)) return rank;
            rank++;
        }

        foreach (var candidate in mruTabs.Order)
        {
            if (ReferenceEquals(candidate, activeTab)
                || !IsOrdinaryResidentCandidate(candidate))
            {
                continue;
            }
            if (ReferenceEquals(candidate, target))
            {
                return rank < TabLifecyclePolicy.ProtectedResidentTabCount ? rank : -1;
            }
            rank++;
            if (rank >= TabLifecyclePolicy.ProtectedResidentTabCount) break;
        }
        return -1;
    }

    private bool IsProtectedResidentTab(BrowserTab tab)
    {
        return GetProtectedResidentRank(tab) >= 0;
    }

    private void ApplyLiveTabMemoryTarget(BrowserTab tab, bool foreground)
    {
        if (!tab.SupportsMemoryUsageTarget
            || tab.IsSuspended
            || tab.Core is not { } core)
        {
            return;
        }

        var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
        var requiresBackgroundExecution = IsBackgroundProtected(tab)
            || tab.ActiveDownloads > 0
            || tab.IsLoading
            || tab.IsInitializing
            || IsTabAudible(tab);
        var retainedSuspendFailures = IsProtectedResidentTab(tab)
            ? tab.ConsecutiveSuspendFailures
            : 0;
        var target = TabLifecyclePolicy.ShouldUseLowMemoryTarget(
                mode,
                foreground,
                requiresBackgroundExecution,
                retainedSuspendFailures)
            ? CoreWebView2MemoryUsageTargetLevel.Low
            : CoreWebView2MemoryUsageTargetLevel.Normal;
        SetTabMemoryTarget(tab, target);
    }

    private void RefreshLiveTabMemoryTargets()
    {
        foreach (var tab in tabs.Where(item => !item.IsClosed))
        {
            ApplyLiveTabMemoryTarget(tab, tab == activeTab && !isWindowMinimized);
        }
    }

    private void SetTabMemoryTarget(
        BrowserTab tab,
        CoreWebView2MemoryUsageTargetLevel target)
    {
        if (!tab.SupportsMemoryUsageTarget
            || tab.IsSuspended
            || tab.Core is not { } core
            || tab.MemoryUsageTargetLevel == target)
        {
            return;
        }

        try
        {
            core.MemoryUsageTargetLevel = target;
            tab.MemoryUsageTargetLevel = target;
        }
        catch (Exception error)
        {
            tab.SupportsMemoryUsageTarget = false;
            stateStore.Log("Could not set the live tab memory target", error);
        }
    }

    private bool TryDiscardTabAfterFailedSuspend(BrowserTab tab)
    {
        var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
        // A duplicate reduction request can observe IsSuspending and receive a
        // false result while the first TrySuspendAsync COM call is still in
        // flight. Never dispose that WebView until its suspension continuation
        // has finished validating the tab/core generation.
        if (mode != TabLifecycleMode.Ultra
            || tab.IsClosed
            || tab.IsSuspending)
        {
            return false;
        }

        var nowUtc = DateTime.UtcNow;
        var snapshot = new TabLifecycleSnapshot(
            Id: 0,
            tab.LastActiveUtc,
            IsActive: tab == activeTab && !isWindowMinimized,
            tab.IsClosed,
            tab.IsLoading,
            tab.IsInitializing,
            tab.IsAudible,
            HasActiveDownload: tab.ActiveDownloads > 0,
            HasCore: tab.Core is not null,
            tab.IsSuspended,
            IsBackgroundProtected(tab),
            ProtectedResidentRank: GetProtectedResidentRank(tab));
        var residentCoreCount = tabs.Count(item => !item.IsClosed && item.Core is not null);
        if (!TabLifecyclePolicy.ShouldDiscardAfterFailedSuspend(
                snapshot,
                mode,
                nowUtc,
                systemMemory.MemoryLoadPercent,
                systemMemory.IsValid,
                systemMemory.TotalPhysicalBytes,
                systemMemory.LogicalProcessorCount,
                residentCoreCount,
                tab.ConsecutiveSuspendFailures))
        {
            return false;
        }

        if (TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled) != TabLifecycleMode.Ultra)
        {
            return false;
        }
        ReplaceTabView(tab);
        MarkTabDiscarded(tab, "Ultra-light mode released a page that could not sleep");
        return true;
    }

    private async Task SleepInactiveTabsAsync()
    {
        var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
        if (isClosing
            || mode == TabLifecycleMode.Off
            || memorySweepRunning
            || (environment is null && !tabs.Any(tab => !tab.IsClosed && tab.IsInitializing)))
        {
            return;
        }
        CheckApplicationInactivity();
        if (DateTimeOffset.UtcNow < nextLifecycleScanAt) return;
        if (!tabs.Any(item =>
            (item.Core is not null || item.IsInitializing)
            && !item.IsClosed
            && !item.IsAudible
            && !IsBackgroundProtected(item)
            && (!IsProtectedResidentTab(item)
                || (mode == TabLifecycleMode.Ultra
                    && !item.IsSuspended
                    && !item.IsLoading
                    && !item.IsInitializing
                    && item.ConsecutiveSuspendFailures
                        < TabLifecyclePolicy.SuspendFailureFallbackThreshold))
            && (item != activeTab || isWindowMinimized)
            && item.ActiveDownloads == 0
            && !item.IsDiscarded))
        {
            TrimAllProcessMemory();
            RefreshMemorySweepTimer();
            nextLifecycleScanAt = DateTimeOffset.MinValue;
            return;
        }

        memorySweepRunning = true;
        nextLifecycleScanAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(MemoryScanCooldownSeconds);

        try
        {
            systemMemory = SystemResourceInfo.CaptureMemory();
            var nowUtc = DateTime.UtcNow;
            var snapshotTabs = tabs.ToArray();
            var snapshots = snapshotTabs
                .Select((tab, index) => new TabLifecycleSnapshot(
                    index,
                    tab.LastActiveUtc,
                    tab == activeTab && !isWindowMinimized,
                    tab.IsClosed,
                    tab.IsLoading,
                    tab.IsInitializing,
                    IsTabAudible(tab),
                    tab.ActiveDownloads > 0,
                    tab.Core is not null,
                    tab.IsSuspended,
                    IsBackgroundProtected(tab),
                    GetProtectedResidentRank(tab)))
                .ToArray();
            var decisions = TabLifecyclePolicy.PlanSweep(
                snapshots,
                mode,
                nowUtc,
                systemMemory.MemoryLoadPercent,
                systemMemory.IsValid,
                systemMemory.TotalPhysicalBytes,
                systemMemory.LogicalProcessorCount,
                mode == TabLifecycleMode.Standard
                    ? Math.Min(MaximumStandardUnloadsPerSweep, snapshotTabs.Length)
                    : snapshotTabs.Length);

            foreach (var decision in decisions)
            {
                if (decision.Id < 0 || decision.Id >= snapshotTabs.Length) continue;
                var tab = snapshotTabs[decision.Id];
                if (isClosing || tab.IsClosed) return;
                var currentMode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
                if (currentMode != mode) break;
                try
                {
                    if (decision.Action == TabLifecycleAction.Suspend)
                    {
                        var suspended = await TrySuspendTabAsync(
                            tab,
                            includeActiveTab: isWindowMinimized && tab == activeTab,
                            expectedMode: mode);
                        if (!suspended) TryDiscardTabAfterFailedSuspend(tab);
                    }
                    else if (decision.Action == TabLifecycleAction.Discard
                        && currentMode is TabLifecycleMode.Standard or TabLifecycleMode.Ultra
                        && (currentMode == TabLifecycleMode.Standard
                            || tab.IsSuspended
                            || tab.IsLoading
                            || tab.IsInitializing)
                        && !tab.IsDiscarded
                        && (tab != activeTab || isWindowMinimized)
                        && !tab.IsAudible
                        && !IsBackgroundProtected(tab)
                        && !IsProtectedResidentTab(tab)
                        && tab.ActiveDownloads == 0)
                    {
                        ReplaceTabView(tab);
                        MarkTabDiscarded(
                            tab,
                            currentMode == TabLifecycleMode.Standard
                                ? "Memory saver released an older inactive page"
                                : "Ultra-light mode released its page process");
                    }
                }
                catch (Exception error)
                {
                    stateStore.Log("Could not reduce an inactive tab", error);
                }
            }
            UpdateStatus();
            TrimAllProcessMemory();
        }
        finally
        {
            memorySweepRunning = false;
        }
    }

    private void TrimAllProcessMemory()
    {
        if (isClosing) return;
        try
        {
            foreach (var tab in tabs)
            {
                if (!tab.IsClosed && tab.Core is { } core)
                {
                    try
                    {
                        _ = core.ExecuteScriptAsync("try { window.gc && window.gc(); } catch (_) {}");
                    }
                    catch
                    {
                        // Ignore COM or lifecycle errors
                    }
                }
            }

            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            SystemResourceInfo.TrimCurrentProcessWorkingSet();
            if (environment is not null)
            {
                try
                {
                    var processInfos = environment.GetProcessInfos();
                    if (processInfos is not null)
                    {
                        foreach (var processInfo in processInfos)
                        {
                            SystemResourceInfo.TrimProcessWorkingSet(processInfo.ProcessId);
                            if (processInfo.Kind == CoreWebView2ProcessKind.Renderer)
                            {
                                try
                                {
                                    using var proc = Process.GetProcessById(processInfo.ProcessId);
                                    if (proc.PrivateMemorySize64 > MaxInactiveRendererPrivateBytes)
                                    {
                                        foreach (var tab in tabs)
                                        {
                                            if (!tab.IsClosed && tab.Core is { } core)
                                            {
                                                _ = core.ExecuteScriptAsync("try { window.gc && window.gc({type:'major',execution:'sync'}); } catch (_) {}");
                                            }
                                        }
                                    }
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                }
                catch (Exception error)
                {
                    stateStore.Log("Could not trim WebView2 child process memory", error);
                }
            }
        }
        catch (Exception error)
        {
            stateStore.Log("Could not trim process memory", error);
        }
    }

    private async Task<bool> TrySuspendTabAsync(
        BrowserTab tab,
        bool includeActiveTab = false,
        TabLifecycleMode? expectedMode = null)
    {
        if (isClosing
            || tab.IsClosed
            || tab.IsSuspended
            || tab.IsSuspending
            || tab.IsDiscarded
            || tab.IsLoading
            || tab.IsInitializing
            || IsBackgroundProtected(tab)
            || (IsProtectedResidentTab(tab)
                && tab.ConsecutiveSuspendFailures
                    >= TabLifecyclePolicy.SuspendFailureFallbackThreshold)
            || tab.ActiveDownloads > 0
            || (!includeActiveTab && tab == activeTab)
            || tab.View is null
            || tab.View.IsDisposed
            || tab.View.Disposing
            || tab.Core is null
            || IsTabAudible(tab))
        {
            return false;
        }

        var suspendingView = tab.View;
        var core = tab.Core;
        // A page can leave the protected MRU set after using the Low target as
        // its failed-suspend fallback. Restore Normal before trying the separate
        // Ultra suspension strategy again.
        SetTabMemoryTarget(tab, CoreWebView2MemoryUsageTargetLevel.Normal);
        tab.IsSuspending = true;
        try
        {
            if (suspendingView.IsDisposed || suspendingView.Disposing) return false;
            try { suspendingView.Visible = false; }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) when (
                suspendingView.IsDisposed || suspendingView.Disposing)
            {
                return false;
            }
            bool suspended;
            try
            {
                suspended = await core.TrySuspendAsync();
            }
            catch (Exception error)
            {
                if (tab.View == suspendingView
                    && !suspendingView.IsDisposed
                    && !suspendingView.Disposing
                    && !isClosing
                    && !tab.IsClosed)
                {
                    var modeAfterFailure = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
                    var lifecycleChanged = modeAfterFailure == TabLifecycleMode.Off
                        || (expectedMode is not null && modeAfterFailure != expectedMode)
                        || IsBackgroundProtected(tab)
                        || tab.IsLoading
                        || tab.IsInitializing
                        || tab.IsAudible
                        || tab.ActiveDownloads > 0
                        || (activeTab == tab && !isWindowMinimized);
                    if (lifecycleChanged)
                    {
                        tab.ConsecutiveSuspendFailures = 0;
                        if (activeTab == tab && !isWindowMinimized)
                        {
                            suspendingView.Visible = !tab.Overlay.Visible
                                && tab.StartPageView?.Visible != true;
                        }
                        ApplyLiveTabMemoryTarget(tab, activeTab == tab && !isWindowMinimized);
                    }
                    else
                    {
                        if (error is InvalidOperationException)
                        {
                            RecoverInvalidTabCore(tab, suspendingView);
                            return false;
                        }
                        tab.ConsecutiveSuspendFailures++;
                        ApplyLiveTabMemoryTarget(tab, activeTab == tab && !isWindowMinimized);
                    }
                    stateStore.Log("Could not suspend a background tab", error);
                }
                return false;
            }
            if (tab.View != suspendingView
                || suspendingView.IsDisposed
                || suspendingView.Disposing) return false;
            tab.IsSuspended = suspended;
            if (isClosing || tab.IsClosed) return false;
            var currentMode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
            if (currentMode == TabLifecycleMode.Off
                || (expectedMode is not null && currentMode != expectedMode)
                || IsBackgroundProtected(tab)
                || tab.IsLoading
                || tab.IsInitializing
                || tab.IsAudible
                || tab.ActiveDownloads > 0
                || (activeTab == tab && !isWindowMinimized))
            {
                tab.ConsecutiveSuspendFailures = 0;
                ResumeTab(tab);
                if (activeTab == tab && !isWindowMinimized && tab.View is not null)
                {
                    tab.View.Visible = !tab.Overlay.Visible;
                }
                ApplyLiveTabMemoryTarget(tab, activeTab == tab && !isWindowMinimized);
                return false;
            }
            if (!suspended)
            {
                tab.ConsecutiveSuspendFailures++;
                ApplyLiveTabMemoryTarget(tab, activeTab == tab && !isWindowMinimized);
                return false;
            }

            tab.ConsecutiveSuspendFailures = 0;
            tab.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            if (tab.IsSuspended)
            {
                UpdateTabHeader(tab);
                RefreshNativeStartPages();
            }
            return tab.IsSuspended;
        }
        finally
        {
            tab.IsSuspending = false;
        }
    }

    private void RecoverInvalidTabCore(BrowserTab tab, WebView2 expectedView)
    {
        if (isClosing
            || tab.IsClosed
            || tab.View != expectedView)
        {
            return;
        }

        ReplaceTabView(tab);
        tab.ConnectionState = ConnectionState.Failed;
        if (tab == activeTab && !isWindowMinimized)
        {
            tab.StatusText = "Page process stopped";
            ShowTabOverlay(
                tab,
                "This page stopped working",
                "The expired page process was released safely.",
                "Reload tab",
                () => RecreateTabAsync(tab));
        }
        else
        {
            MarkTabDiscarded(tab, "Released after its page process stopped");
        }
        UpdateTabHeader(tab);
        UpdateStatus();
    }

    private bool IsTabAudible(BrowserTab tab)
    {
        return tab.Core is not null && tab.IsAudible;
    }

    private static bool IsBackgroundProtected(BrowserTab tab)
    {
        var effectiveUrl = tab.Core?.Source ?? tab.Url;
        return tab.PendingMediaPermissionRefreshes > 0
            || tab.PendingMediaPermissionRequests > 0
            || CommunicationCompatibilityPolicy.IsCallSite(effectiveUrl)
            || IsYouTubeWatchUrl(effectiveUrl)
            || CommunicationCompatibilityPolicy.ShouldProtectBackgroundTab(
                tab.KeepAwake,
                tab.MicrophoneAccessGranted,
                tab.CameraAccessGranted);
    }

    private static bool IsYouTubeWatchUrl(string? url)
    {
        return !string.IsNullOrEmpty(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && IsYouTubeHost(uri.Host)
            && (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.StartsWith("/shorts", StringComparison.OrdinalIgnoreCase));
    }

    private void ResumeTab(BrowserTab tab)
    {
        var expectedView = tab.View;
        var core = tab.Core;
        if (!tab.IsSuspended || core is null || expectedView is null) return;
        try
        {
            core.Resume();
            tab.IsSuspended = false;
            SetTabMemoryTarget(tab, CoreWebView2MemoryUsageTargetLevel.Normal);
            RunUiTask(
                () => ApplyWebsiteThemeAsync(tab),
                "Could not refresh the website color preference after waking a tab");
            UpdateTabHeader(tab);
            RefreshNativeStartPages();
        }
        catch (Exception error)
        {
            stateStore.Log("Could not resume a tab", error);
            RecoverInvalidTabCore(tab, expectedView);
        }
    }

    private async Task HandleWindowStateChangeAsync()
    {
        var minimized = WindowState == FormWindowState.Minimized;
        if (minimized == isWindowMinimized) return;
        isWindowMinimized = minimized;
        var transitionGeneration = ++windowStateGeneration;

        if (minimized)
        {
            var minimizingTab = activeTab;
            if (minimizingTab?.View is not null) minimizingTab.View.Visible = false;
            if (minimizingTab?.StartPageView is not null)
            {
                minimizingTab.StartPageView.Visible = false;
            }
            if (minimizingTab is not null)
            {
                minimizingTab.LastActiveUtc = DateTime.UtcNow;
                minimizingTab.ConsecutiveSuspendFailures = 0;
                ApplyLiveTabMemoryTarget(minimizingTab, foreground: false);
            }
            if (TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled) == TabLifecycleMode.Ultra
                && minimizingTab is not null)
            {
                var mode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
                var suspended = await TrySuspendTabAsync(
                    minimizingTab,
                    includeActiveTab: true,
                    expectedMode: mode);
                if (!suspended) TryDiscardTabAfterFailedSuspend(minimizingTab);
            }
            TrimAllProcessMemory();
            RefreshMemorySweepTimer();
            return;
        }

        UpdateWindowControls();
        if (activeTab is not null)
        {
            var restoringTab = activeTab;
            restoringTab.Host.Visible = true;
            if (restoringTab.IsStartPage)
            {
                ShowNativeStartPage(restoringTab);
            }
            else if (restoringTab.IsDiscarded)
            {
                await RestoreDiscardedTabAsync(restoringTab, focusPage: false);
            }
            else
            {
                ResumeTab(restoringTab);
                if (restoringTab.View is not null)
                {
                    restoringTab.View.Visible = !restoringTab.Overlay.Visible
                        && restoringTab.StartPageView?.Visible != true;
                }
            }
            if (transitionGeneration != windowStateGeneration
                || isWindowMinimized
                || activeTab != restoringTab)
            {
                ApplyLiveTabMemoryTarget(restoringTab, foreground: false);
                RefreshMemorySweepTimer();
                return;
            }
            ApplyLiveTabMemoryTarget(restoringTab, foreground: true);
        }

        RefreshMemorySweepTimer();
    }

    private void SetResourceMode(TabLifecycleMode mode)
    {
        var normalizedMode = mode switch
        {
            TabLifecycleMode.Off => TabLifecycleMode.Off,
            TabLifecycleMode.Standard => TabLifecycleMode.Standard,
            TabLifecycleMode.Ultra => TabLifecycleMode.Ultra,
            _ => TabLifecycleMode.Standard
        };

        memorySaverEnabled = normalizedMode != TabLifecycleMode.Off;
        ultraLightEnabled = normalizedMode == TabLifecycleMode.Ultra;
        ResetSuspendFailureCounts();
        if (normalizedMode != TabLifecycleMode.Ultra)
        {
            foreach (var tab in tabs.Where(item => item.IsSuspended)) ResumeTab(tab);
        }
        RefreshLiveTabMemoryTargets();
        ReleaseInactiveNativeStartPagesForUltra();
        RefreshMemorySweepTimer();
        SavePreferences();
        RefreshNativeStartPages();
        ShowTransientStatus(normalizedMode switch
        {
            TabLifecycleMode.Ultra => "Ultra-light selected — 3 recent sites won't reload",
            TabLifecycleMode.Standard => "Memory saver selected — 3 recent sites stay ready",
            _ => "Resource saver off"
        });
    }

    private void ShowResourceModeMenu(Control? anchor = null)
    {
        var target = anchor ?? menuButton;
        resourceModeMenu.DropDown.Show(
            target,
            new Point(target.Width, target.Height),
            ToolStripDropDownDirection.BelowLeft);
    }

    private void ToggleMemorySaver()
    {
        memorySaverEnabled = !memorySaverEnabled;
        ResetSuspendFailureCounts();
        if (!memorySaverEnabled)
        {
            ultraLightEnabled = false;
            foreach (var tab in tabs.Where(item => item.IsSuspended)) ResumeTab(tab);
        }
        RefreshLiveTabMemoryTargets();
        RefreshMemorySweepTimer();
        SavePreferences();
        RefreshNativeStartPages();
        ShowTransientStatus(memorySaverEnabled ? "Memory saver on" : "Memory saver off");
    }

    private void ToggleUltraLightMode()
    {
        ultraLightEnabled = !ultraLightEnabled;
        if (ultraLightEnabled) memorySaverEnabled = true;
        else
        {
            foreach (var tab in tabs.Where(item => item.IsSuspended)) ResumeTab(tab);
        }
        ResetSuspendFailureCounts();
        RefreshLiveTabMemoryTargets();
        ReleaseInactiveNativeStartPagesForUltra();
        RefreshMemorySweepTimer();
        SavePreferences();
        RefreshNativeStartPages();
        ShowTransientStatus(ultraLightEnabled
            ? "Ultra-light mode on \u2014 very old inactive tabs may reload"
            : "Ultra-light mode off");
    }

    private void CycleResourceMode()
    {
        if (ultraLightEnabled)
        {
            ultraLightEnabled = false;
            memorySaverEnabled = false;
            foreach (var tab in tabs.Where(item => item.IsSuspended)) ResumeTab(tab);
        }
        else if (memorySaverEnabled)
        {
            ultraLightEnabled = true;
        }
        else
        {
            memorySaverEnabled = true;
        }

        ResetSuspendFailureCounts();
        RefreshLiveTabMemoryTargets();
        ReleaseInactiveNativeStartPagesForUltra();
        RefreshMemorySweepTimer();
        SavePreferences();
        RefreshNativeStartPages();
        var mode = ultraLightEnabled
            ? "Ultra-light mode"
            : memorySaverEnabled ? "Memory saver" : "Resource saver off";
        ShowTransientStatus($"{mode} selected");
    }

    private void ResetSuspendFailureCounts()
    {
        foreach (var tab in tabs) tab.ConsecutiveSuspendFailures = 0;
    }

    private void ToggleDarkMode()
    {
        darkModeEnabled = !darkModeEnabled;
        var themeGeneration = Interlocked.Increment(ref websiteThemeGeneration);
        foreach (var tab in tabs.Where(item =>
                     !item.IsClosed
                     && !item.IsSuspended
                     && item.Core is not null).ToArray())
        {
            ApplyWebsiteThemeLoadingBackground(tab, darkModeEnabled);
            RunUiTask(
                () => ApplyWebsiteThemeAsync(tab, themeGeneration),
                "Could not change the website color preference");
        }

        foreach (var tab in tabs.Where(item => item.IsStartPage && item.Core is not null))
        {
            ShowStartPage(tab, false);
        }
        SavePreferences();
        ShowTransientStatus(darkModeEnabled ? "Website dark mode on" : "Website light mode restored");
    }

    private async Task ApplyWebsiteThemeAsync(BrowserTab tab, long? requestedGeneration = null)
    {
        var generation = requestedGeneration ?? Interlocked.Read(ref websiteThemeGeneration);

        while (!isClosing && !tab.IsClosed)
        {
            var core = tab.Core;
            if (core is null) return;

            var useDarkTheme = darkModeEnabled;
            if (generation != Interlocked.Read(ref websiteThemeGeneration))
            {
                generation = Interlocked.Read(ref websiteThemeGeneration);
                continue;
            }

            ApplyWebsiteThemeLoadingBackground(tab, useDarkTheme);
            try
            {
                WebsiteThemePolicy.ApplyPreferredColorScheme(core, useDarkTheme);
                await WebsiteThemePolicy.ApplyAutoDarkModeOverrideAsync(core, useDarkTheme);
            }
            catch (Exception error)
            {
                // The profile preference still works when this optional Chromium capability
                // is unavailable. Theme enhancement failures must never break navigation.
                stateStore.Log("Could not apply the automatic website color policy", error);
            }

            if (ReferenceEquals(tab.Core, core)
                && generation == Interlocked.Read(ref websiteThemeGeneration)
                && useDarkTheme == darkModeEnabled)
            {
                return;
            }

            generation = Interlocked.Read(ref websiteThemeGeneration);
        }
    }

    private static void ApplyWebsiteThemeLoadingBackground(BrowserTab tab, bool useDarkTheme)
    {
        if (tab.View is not null)
        {
            tab.View.DefaultBackgroundColor = WebsiteThemePolicy.GetLoadingBackgroundColor(useDarkTheme);
        }
    }

    private void ToggleAdBlocker()
    {
        adBlockEnabled = !adBlockEnabled;
        if (adBlockEnabled)
        {
            foreach (var tab in tabs.Where(item => item.Core is not null))
            {
                tab.AdBlockControlChannel = null;
                RunUiTask(
                    async () =>
                    {
                        await InstallAdBlockFilteringAsync(tab);
                        if (tab.Core is not null)
                        {
                            await InstallAdBlockPageScriptsAsync(tab);
                            if (adBlockEnabled && tab.Core is not null) tab.Core.Reload();
                        }
                    },
                    "Could not enable the site shield");
            }
        }
        else
        {
            foreach (var tab in tabs.Where(item => item.Core is not null))
            {
                RemoveAdBlockFiltering(tab);
                RemoveAdBlockPageScripts(tab, tab.Core!);
                RunUiTask(
                    () => DisableAdBlockForCurrentDocumentAsync(tab),
                    "Could not disable the current page shield");
            }
        }
        SavePreferences();
        ShowTransientStatus(adBlockEnabled ? "Ad and tracker blocker on" : "Ad and tracker blocker off");
        UpdateStatus();
    }

    private void ToggleFavorite()
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Favorites are unavailable in a private window");
            return;
        }
        var tab = activeTab;
        if (tab is null || tab.IsStartPage || !BrowserPolicy.IsHttpUrl(tab.Url))
        {
            ShowTransientStatus("Open a website before adding a favorite");
            return;
        }

        var existing = state.Bookmarks.FindIndex(item =>
            BrowserPolicy.UrlEquals(item.Url, tab.Url));
        if (existing >= 0)
        {
            state.Bookmarks.RemoveAt(existing);
            ShowTransientStatus("Removed from favorites");
        }
        else
        {
            state.Bookmarks.Insert(0, new BookmarkEntry(tab.Title, tab.Url));
            if (state.Bookmarks.Count > 200) state.Bookmarks.RemoveRange(200, state.Bookmarks.Count - 200);
            ShowTransientStatus("Added to favorites");
        }

        InvalidateStartPageLinks();
        ScheduleStateSave();
        UpdateFavoriteButton();
        RefreshNativeStartPages(refreshLinks: true);
    }

    private void AddHistory(BrowserTab tab)
    {
        if (isPrivateMode) return;
        if (tab.IsStartPage || !BrowserPolicy.IsHttpUrl(tab.Url)) return;
        state.History.RemoveAll(item => BrowserPolicy.UrlEquals(item.Url, tab.Url));
        state.History.Insert(0, new HistoryEntry(tab.Title, tab.Url, DateTime.UtcNow));
        if (state.History.Count > 300) state.History.RemoveRange(300, state.History.Count - 300);
        InvalidateStartPageLinks();
        ScheduleStateSave();
    }

    private void UpdateHistoryTitle(BrowserTab tab)
    {
        if (isPrivateMode) return;
        if (tab.IsStartPage || !BrowserPolicy.IsHttpUrl(tab.Url)) return;
        var index = state.History.FindIndex(item => BrowserPolicy.UrlEquals(item.Url, tab.Url));
        if (index < 0 || state.History[index].Title.Equals(tab.Title, StringComparison.Ordinal)) return;

        var entry = state.History[index];
        state.History[index] = entry with { Title = tab.Title };
        InvalidateStartPageLinks();
        ScheduleStateSave();
    }

    private async Task ClearBrowsingDataAsync()
    {
        var answer = MessageBox.Show(
            "Clear cookies, cache, saved passwords, autofill data, permissions, and browsing history? Favorites are kept.",
            "Clear browsing data",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        ShowTransientStatus("Clearing browsing data\u2026", false);
        CancelAllPendingPermissionRequests();
        foreach (var tab in tabs) ClearTabMediaPermissions(tab);

        Exception? profileClearError = null;
        CoreWebView2Controller? temporaryController = null;
        try
        {
            var core = activeTab?.Core
                ?? tabs.Select(item => item.Core).FirstOrDefault(item => item is not null);
            if (core is null)
            {
                var browserEnvironment = await GetOrCreateEnvironmentAsync();
                if (isClosing
                    || environment != browserEnvironment
                    || !IsHandleCreated)
                {
                    throw new InvalidOperationException("The browser profile is not available.");
                }

                var controllerOptions = browserEnvironment.CreateCoreWebView2ControllerOptions();
                var profileOptions = GetControllerProfileSnapshotForTesting(isPrivateMode);
                controllerOptions.IsInPrivateModeEnabled = profileOptions.IsInPrivateModeEnabled;
                if (profileOptions.ProfileName is not null)
                {
                    controllerOptions.ProfileName = profileOptions.ProfileName;
                }
                temporaryController = await browserEnvironment.CreateCoreWebView2ControllerAsync(
                    Handle,
                    controllerOptions);
                temporaryController.IsVisible = false;
                temporaryController.Bounds = Rectangle.Empty;
                core = temporaryController.CoreWebView2;
            }

            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        }
        catch (Exception error)
        {
            profileClearError = error;
            stateStore.Log("Could not completely clear the WebView2 profile", error);
        }
        finally
        {
            try { temporaryController?.Close(); }
            catch { }
            ReleaseBrowserEnvironmentIfIdle();
        }

        state.History.Clear();
        downloads.RemoveAll(item => item.IsTerminal);
        closedTabs.Clear();
        state.RecentlyClosed.Clear();
        state.DismissedSuggestionUrls.Clear();
        state.SuggestionUsage.Clear();
        state.SiteZoom.Clear();
        state.MutedHosts.Clear();
        state.KeepAwakeHosts.Clear();
        state.AdBlockExceptionHosts.Clear();
        adBlockExceptionHosts.Clear();
        foreach (var tab in tabs)
        {
            tab.ZoomFactor = 1.0;
            tab.IsMuted = false;
            tab.KeepAwake = false;
            if (tab.View is not null)
            {
                try { tab.View.ZoomFactor = 1.0; }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
            UpdateTabHeader(tab);
        }
        permissionManager?.Hide();
        InvalidateStartPageLinks();
        RefreshNativeStartPages(refreshLinks: true);
        RefreshDownloadsUi();
        UpdateNavigationChrome();

        var stateSaved = true;
        if (persistStateOnClose)
        {
            stateSaveTimer.Stop();
            PopulateStateFromPreferences();
            stateSaved = stateStore.Save(state);
        }

        if (profileClearError is null && stateSaved)
        {
            ShowTransientStatus("Browsing data cleared");
            return;
        }

        ShowTransientStatus("Some browsing data could not be cleared");
        var detail = profileClearError is not null && !stateSaved
            ? "The browser profile and the local state file could not be fully cleared."
            : profileClearError is not null
                ? "The local MishaWeb history was cleared, but the browser profile could not be fully cleared."
                : "The browser profile was cleared, but the local state file could not be updated.";
        MessageBox.Show(
            this,
            detail,
            "Browsing data partially cleared",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void CancelAllPendingPermissionRequests()
    {
        permissionPrompt?.CloseRequest();
        if (activePermissionRequest is not null)
        {
            var request = activePermissionRequest;
            activePermissionRequest = null;
            CompletePermissionRequest(request, PermissionPromptDecision.Cancel);
        }
        while (permissionQueue.Count > 0)
        {
            CompletePermissionRequest(
                permissionQueue.Dequeue(),
                PermissionPromptDecision.Cancel);
        }
    }

    private async Task FindOnPageAsync(bool backwards)
    {
        var core = activeTab?.Core;
        var query = findBox.Text;
        if (core is null)
        {
            ShowTransientStatus("Find is unavailable until a website is open");
            return;
        }
        if (string.IsNullOrEmpty(query))
        {
            StopFindSession(activeTab);
            findMatchLabel.Text = string.Empty;
            ShowTransientStatus("Type a word to find on this page");
            return;
        }

        var tab = activeTab!;
        if (tab.FindUsingNativeApi && tab.FindSession is not null
            && tab.FindQuery.Equals(query, StringComparison.Ordinal))
        {
            if (backwards) tab.FindSession.FindPrevious();
            else tab.FindSession.FindNext();
            UpdateFindMatchLabel(tab);
            return;
        }

        await StartFindSession(backwards);
    }

    private void ShowFindBar()
    {
        if (environment is null || activeTab?.Core is null)
        {
            ShowTransientStatus("Find is unavailable until a website is open");
            return;
        }
        findBar.Visible = true;
        rootLayout.RowStyles[2].Height = ScaleChromeLogical(40);
        UpdateFindBarLayout();
        findBox.Focus();
        findBox.SelectAll();
        findDebounceTimer.Stop();
        if (findBox.TextLength > 0) RunUiTask(StartFindSession, "Find failed");
    }

    private void HideFindBar()
    {
        findDebounceTimer.Stop();
        StopFindSession(activeTab);
        findBar.Visible = false;
        rootLayout.RowStyles[2].Height = 0;
        findMatchLabel.Text = string.Empty;
        activeTab?.View?.Focus();
    }

    private Task StartFindSession() => StartFindSession(backwards: false);

    private async Task StartFindSession(bool backwards)
    {
        var tab = activeTab;
        var core = tab?.Core;
        var query = findBox.Text;
        if (tab is null || core is null || string.IsNullOrEmpty(query))
        {
            StopFindSession(tab);
            findMatchLabel.Text = string.Empty;
            return;
        }

        var documentGeneration = tab.DocumentNavigationGeneration;
        StopFindSession(tab);
        var requestGeneration = tab.FindRequestGeneration;
        CoreWebView2Find? find = null;
        EventHandler<object>? matchCountChanged = null;
        EventHandler<object>? activeMatchChanged = null;
        try
        {
            find = core.Find;
            matchCountChanged = (_, _) =>
            {
                if (IsFindContinuationCurrent(
                        tab,
                        core,
                        documentGeneration,
                        requestGeneration,
                        find,
                        matchCountChanged,
                        activeMatchChanged,
                        query))
                {
                    UpdateFindMatchLabel(tab);
                }
            };
            activeMatchChanged = (_, _) =>
            {
                if (IsFindContinuationCurrent(
                        tab,
                        core,
                        documentGeneration,
                        requestGeneration,
                        find,
                        matchCountChanged,
                        activeMatchChanged,
                        query))
                {
                    UpdateFindMatchLabel(tab);
                }
            };
            tab.FindSession = find;
            tab.FindQuery = query;
            tab.FindUsingNativeApi = true;
            tab.FindMatchCountChangedHandler = matchCountChanged;
            tab.FindActiveMatchIndexChangedHandler = activeMatchChanged;
            find.MatchCountChanged += matchCountChanged;
            find.ActiveMatchIndexChanged += activeMatchChanged;
            var options = (environment ?? throw new InvalidOperationException("Browser environment is not ready"))
                .CreateFindOptions();
            options.FindTerm = query;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;
            await find.StartAsync(options);
            if (!IsFindContinuationCurrent(
                    tab,
                    core,
                    documentGeneration,
                    requestGeneration,
                    find,
                    matchCountChanged,
                    activeMatchChanged,
                    query))
            {
                ReleaseFindSession(tab, find, matchCountChanged, activeMatchChanged);
                return;
            }
            if (backwards) find.FindPrevious();
            UpdateFindMatchLabel(tab);
        }
        catch (Exception error) when (IsUnsupportedFindException(error))
        {
            var requestIsCurrent = find is null
                ? IsLegacyFindContinuationCurrent(
                    tab,
                    core,
                    documentGeneration,
                    requestGeneration,
                    query)
                : IsFindContinuationCurrent(
                    tab,
                    core,
                    documentGeneration,
                    requestGeneration,
                    find,
                    matchCountChanged,
                    activeMatchChanged,
                    query);
            if (find is not null)
            {
                ReleaseFindSession(tab, find, matchCountChanged, activeMatchChanged);
            }
            if (!requestIsCurrent) return;
            tab.FindUsingNativeApi = false;
            await RunLegacyFindAsync(
                tab,
                core,
                documentGeneration,
                requestGeneration,
                query,
                backwards);
        }
        catch
        {
            if (find is not null)
            {
                ReleaseFindSession(tab, find, matchCountChanged, activeMatchChanged);
            }
            throw;
        }
    }

    private async Task RunLegacyFindAsync(
        BrowserTab tab,
        CoreWebView2 core,
        long documentGeneration,
        long requestGeneration,
        string query,
        bool backwards)
    {
        if (!IsLegacyFindContinuationCurrent(
                tab,
                core,
                documentGeneration,
                requestGeneration,
                query)) return;
        var encoded = JsonSerializer.Serialize(query);
        var script = $"window.find({encoded}, false, {(backwards ? "true" : "false")}, true, false, true, false)";
        var result = await core.ExecuteScriptAsync(script);
        if (!IsLegacyFindContinuationCurrent(
                tab,
                core,
                documentGeneration,
                requestGeneration,
                query)) return;
        findMatchLabel.Text = result.Equals("true", StringComparison.OrdinalIgnoreCase)
            ? "Match found"
            : "No match";
    }

    private void StopFindSession(BrowserTab? tab)
    {
        if (tab is null) return;
        tab.FindRequestGeneration++;
        var find = tab.FindSession;
        if (find is null)
        {
            tab.FindQuery = string.Empty;
            tab.FindUsingNativeApi = false;
            if (activeTab == tab) findMatchLabel.Text = string.Empty;
            return;
        }
        ReleaseFindSession(
            tab,
            find,
            tab.FindMatchCountChangedHandler,
            tab.FindActiveMatchIndexChangedHandler);
    }

    private void ReleaseFindSession(
        BrowserTab tab,
        CoreWebView2Find find,
        EventHandler<object>? matchCountChanged,
        EventHandler<object>? activeMatchChanged)
    {
        var ownsCurrentSession = IsFindSessionOwner(
            ReferenceEquals(tab.FindSession, find),
            ReferenceEquals(tab.FindMatchCountChangedHandler, matchCountChanged),
            ReferenceEquals(tab.FindActiveMatchIndexChangedHandler, activeMatchChanged));
        try
        {
            if (matchCountChanged is not null)
            {
                find.MatchCountChanged -= matchCountChanged;
            }
            if (activeMatchChanged is not null)
            {
                find.ActiveMatchIndexChanged -= activeMatchChanged;
            }
            // CoreWebView2.Find can return the same per-core object to
            // overlapping requests. A stale completion may remove only its
            // own handlers; stopping the shared object would cancel the newer
            // request that now owns it.
            if (ownsCurrentSession) find.Stop();
        }
        catch (Exception error)
        {
            stateStore.Log("Could not stop page find", error);
        }
        if (!ownsCurrentSession) return;
        tab.FindSession = null;
        tab.FindMatchCountChangedHandler = null;
        tab.FindActiveMatchIndexChangedHandler = null;
        tab.FindQuery = string.Empty;
        tab.FindUsingNativeApi = false;
        if (activeTab == tab) findMatchLabel.Text = string.Empty;
    }

    private void UpdateFindMatchLabel(BrowserTab tab)
    {
        if (tab.FindSession is null || activeTab != tab) return;
        try
        {
            var active = tab.FindSession.ActiveMatchIndex;
            var total = tab.FindSession.MatchCount;
            findMatchLabel.Text = total > 0 && active > 0
                ? $"{active} / {total}"
                : total == 0 ? "No matches" : $"\u2014 / {total}";
        }
        catch (Exception error) when (IsUnsupportedFindException(error))
        {
            StopFindSession(tab);
        }
    }

    private bool IsFindContinuationCurrent(
        BrowserTab tab,
        CoreWebView2 core,
        long documentGeneration,
        long requestGeneration,
        CoreWebView2Find find,
        EventHandler<object>? matchCountChanged,
        EventHandler<object>? activeMatchChanged,
        string query) =>
        IsFindContinuationEligible(
            isClosing,
            tab.IsClosed,
            ReferenceEquals(activeTab, tab),
            ReferenceEquals(tab.Core, core),
            tab.DocumentNavigationGeneration == documentGeneration,
            tab.FindRequestGeneration == requestGeneration
                && IsFindSessionOwner(
                    ReferenceEquals(tab.FindSession, find),
                    ReferenceEquals(tab.FindMatchCountChangedHandler, matchCountChanged),
                    ReferenceEquals(tab.FindActiveMatchIndexChangedHandler, activeMatchChanged)),
            findBar.Visible,
            findBox.Text.Equals(query, StringComparison.Ordinal));

    private bool IsLegacyFindContinuationCurrent(
        BrowserTab tab,
        CoreWebView2 core,
        long documentGeneration,
        long requestGeneration,
        string query) =>
        IsFindContinuationEligible(
            isClosing,
            tab.IsClosed,
            ReferenceEquals(activeTab, tab),
            ReferenceEquals(tab.Core, core),
            tab.DocumentNavigationGeneration == documentGeneration,
            tab.FindRequestGeneration == requestGeneration
                && tab.FindSession is null,
            findBar.Visible,
            findBox.Text.Equals(query, StringComparison.Ordinal));

    internal static bool IsFindSessionOwner(
        bool sessionIsCurrent,
        bool matchHandlerIsCurrent,
        bool activeHandlerIsCurrent) =>
        sessionIsCurrent
        && matchHandlerIsCurrent
        && activeHandlerIsCurrent;

    internal static bool IsFindContinuationEligible(
        bool isClosing,
        bool tabIsClosed,
        bool tabIsActive,
        bool coreIsCurrent,
        bool documentIsCurrent,
        bool sessionIsCurrent,
        bool findBarIsVisible,
        bool queryIsCurrent) =>
        !isClosing
        && !tabIsClosed
        && tabIsActive
        && coreIsCurrent
        && documentIsCurrent
        && sessionIsCurrent
        && findBarIsVisible
        && queryIsCurrent;

    private static bool IsUnsupportedFindException(Exception error)
    {
        return error is NotSupportedException
            or InvalidOperationException
            or System.Runtime.InteropServices.COMException;
    }

    private void ChangeZoom(double delta, bool reset = false)
    {
        if (environment is null) return;
        try
        {
            var tab = activeTab;
            var view = tab?.View;
            if (tab is null || view?.CoreWebView2 is null) return;
            var next = reset ? 1.0 : Math.Clamp(view.ZoomFactor + delta, 0.25, 3.0);
            tab.ZoomFactor = next;
            applyingZoomPreference = true;
            try { view.ZoomFactor = next; }
            finally { applyingZoomPreference = false; }
            SaveSiteZoomPreference(tab);
            ApplyZoomPreferenceToMatchingTabs(tab.Url, next);
            ShowTransientStatus($"Zoom {tab.ZoomFactor:P0}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not change page zoom", error);
            ShowTransientStatus("Zoom is unavailable while the browser restarts");
        }
    }

    private void ApplySitePreferences(BrowserTab tab, bool persist)
    {
        if (tab.IsClosed) return;
        if (tab.IsStartPage || !BrowserPolicy.IsHttpUrl(tab.Url))
        {
            tab.ZoomFactor = 1.0;
            tab.IsMuted = false;
        }
        else
        {
            tab.ZoomFactor = isPrivateMode
                ? tab.ZoomFactor
                : SitePreferencePolicy.GetZoom(state.SiteZoom, tab.Url);
            tab.IsMuted = isPrivateMode
                ? tab.IsMuted
                : SitePreferencePolicy.ShouldMuteAudio(state.AudioOutputPolicy, state.AllowedAudioHosts, state.MutedHosts, tab.Url);
        }

        if (tab.View is { CoreWebView2: not null } view)
        {
            try
            {
                applyingZoomPreference = true;
                view.ZoomFactor = Math.Clamp(tab.ZoomFactor, BrowserStateStore.MinimumSiteZoom, BrowserStateStore.MaximumSiteZoom);
                view.CoreWebView2.IsMuted = tab.IsMuted;
            }
            catch (Exception error)
            {
                stateStore.Log("Could not apply site preferences", error);
            }
            finally
            {
                applyingZoomPreference = false;
            }
        }

        if (persist && !isPrivateMode) ScheduleStateSave();
        UpdateTabHeader(tab);
    }

    private void ApplyZoomPreferenceToMatchingTabs(string url, double factor)
    {
        var host = SitePreferencePolicy.GetHost(url);
        if (host is null) return;
        foreach (var tab in tabs.Where(item =>
                     !item.IsClosed
                     && item.Core is not null
                     && SitePreferencePolicy.GetHost(item.Url)?.Equals(host, StringComparison.OrdinalIgnoreCase) == true))
        {
            tab.ZoomFactor = factor;
            if (tab.View is not { CoreWebView2: not null } view) continue;
            try
            {
                applyingZoomPreference = true;
                view.ZoomFactor = factor;
            }
            catch (Exception error)
            {
                stateStore.Log("Could not apply exact-host zoom", error);
            }
            finally
            {
                applyingZoomPreference = false;
            }
        }
    }

    private void SaveSiteZoomPreference(BrowserTab tab)
    {
        if (isPrivateMode || tab.IsStartPage) return;
        var host = SitePreferencePolicy.GetHost(tab.Url);
        if (host is null) return;
        state.SiteZoom.RemoveAll(item => BrowserPolicy.IsExactHost(item.Host, host));
        if (Math.Abs(tab.ZoomFactor - 1.0) >= 0.000_001)
        {
            state.SiteZoom.Insert(0, new SiteZoomEntry(
                host,
                Math.Clamp(tab.ZoomFactor, BrowserStateStore.MinimumSiteZoom, BrowserStateStore.MaximumSiteZoom)));
            if (state.SiteZoom.Count > BrowserStateStore.MaximumSiteZoomEntries)
            {
                state.SiteZoom.RemoveRange(
                    BrowserStateStore.MaximumSiteZoomEntries,
                    state.SiteZoom.Count - BrowserStateStore.MaximumSiteZoomEntries);
            }
        }
        ScheduleStateSave();
    }

    private void ToggleActiveSiteMute()
    {
        var tab = activeTab;
        if (tab is null || tab.IsStartPage)
        {
            ShowTransientStatus("Open a website before changing site audio");
            return;
        }
        ToggleSiteMute(tab);
    }

    private void ToggleSiteMute(BrowserTab tab)
    {
        if (isPrivateMode)
        {
            tab.IsMuted = !tab.IsMuted;
            if (tab.Core is not null) tab.Core.IsMuted = tab.IsMuted;
            UpdateTabHeader(tab);
            return;
        }

        var host = SitePreferencePolicy.GetHost(tab.Url);
        if (host is null) return;

        bool muted;
        if (state.AudioOutputPolicy == AudioOutputPolicy.SpecificSitesOnly)
        {
            var currentlyAllowed = state.AllowedAudioHosts.Any(item => BrowserPolicy.IsExactHost(item, host));
            state.AllowedAudioHosts.RemoveAll(item => BrowserPolicy.IsExactHost(item, host));
            if (!currentlyAllowed)
            {
                state.AllowedAudioHosts.Insert(0, host);
                if (state.AllowedAudioHosts.Count > BrowserStateStore.MaximumAudioHosts)
                {
                    state.AllowedAudioHosts.RemoveRange(
                        BrowserStateStore.MaximumAudioHosts,
                        state.AllowedAudioHosts.Count - BrowserStateStore.MaximumAudioHosts);
                }
                muted = false;
            }
            else
            {
                muted = true;
            }
        }
        else if (state.AudioOutputPolicy == AudioOutputPolicy.MuteAll)
        {
            ShowTransientStatus("Web audio is muted globally in Voice & Audio settings");
            return;
        }
        else
        {
            muted = !SitePreferencePolicy.IsMuted(state.MutedHosts, host);
            state.MutedHosts.RemoveAll(item => BrowserPolicy.IsExactHost(item, host));
            if (muted) state.MutedHosts.Insert(0, host);
            if (state.MutedHosts.Count > BrowserStateStore.MaximumMutedHosts)
            {
                state.MutedHosts.RemoveRange(
                    BrowserStateStore.MaximumMutedHosts,
                    state.MutedHosts.Count - BrowserStateStore.MaximumMutedHosts);
            }
        }

        foreach (var liveTab in tabs.Where(item =>
                     !item.IsClosed
                     && SitePreferencePolicy.GetHost(item.Url)?.Equals(host, StringComparison.OrdinalIgnoreCase) == true))
        {
            liveTab.IsMuted = muted;
            if (liveTab.Core is not null) liveTab.Core.IsMuted = muted;
            UpdateTabHeader(liveTab);
        }
        ScheduleStateSave();
        ShowTransientStatus(muted ? $"Muted {host}" : $"Unmuted {host}");
    }

    private void PrintPage()
    {
        var tab = activeTab;
        if (tab is null || tab.IsStartPage || tab.Core is null)
        {
            ShowTransientStatus("Open a website before printing");
            return;
        }

        try
        {
            tab.Core.ShowPrintUI(CoreWebView2PrintDialogKind.System);
        }
        catch (Exception error)
        {
            stateStore.Log("Could not open the print dialog", error);
            ShowTransientStatus("Printing is unavailable for this page");
        }
    }

    private async Task CapturePageAsync()
    {
        var tab = activeTab;
        if (tab is null)
        {
            ShowTransientStatus("No page is available to capture");
            return;
        }

        var picturesFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "png",
            Filter = "PNG image (*.png)|*.png",
            InitialDirectory = Directory.Exists(picturesFolder) ? picturesFolder : null,
            FileName = $"{MakeSafeFileName(tab.Title)}-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            OverwritePrompt = true,
            Title = "Capture page as PNG"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            await using var stream = new FileStream(
                dialog.FileName,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                useAsync: true);
            if (tab.IsStartPage)
            {
                var startPageView = EnsureNativeStartPage(tab);
                using var image = new Bitmap(
                    Math.Max(1, startPageView.ClientSize.Width),
                    Math.Max(1, startPageView.ClientSize.Height));
                startPageView.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            }
            else if (tab.Core is not null)
            {
                await tab.Core.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png,
                    stream);
            }
            else
            {
                ShowTransientStatus("Wait for the page to finish starting");
                return;
            }

            ShowTransientStatus($"Saved {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not capture the page", error);
            ShowTransientStatus("Could not save the page capture");
        }
    }

    internal static string MakeSafeFileName(string value)
    {
        var title = string.IsNullOrWhiteSpace(value)
            ? "MishaWeb"
            : TextSafety.RemoveUnpairedSurrogates(value.Trim());
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var characters = title
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray();
        var safe = new string(characters).Trim(' ', '.', '-');
        if (safe.Length == 0) safe = "MishaWeb";
        return TextSafety.Truncate(safe, 64).TrimEnd();
    }

    private void GoBack()
    {
        try
        {
            if (activeTab?.Core?.CanGoBack == true) activeTab.Core.GoBack();
        }
        catch (Exception error)
        {
            stateStore.Log("Back navigation failed", error);
        }
    }

    private void GoForward()
    {
        try
        {
            if (activeTab?.Core?.CanGoForward == true) activeTab.Core.GoForward();
        }
        catch (Exception error)
        {
            stateStore.Log("Forward navigation failed", error);
        }
    }

    private void ReloadOrStop()
    {
        try
        {
            var tab = activeTab;
            if (tab?.Core is null) return;
            if (tab.IsLoading) tab.Core.Stop();
            else if (tab.IsStartPage) ShowStartPage(tab, false);
            else tab.Core.Reload();
        }
        catch (Exception error)
        {
            stateStore.Log("Reload or stop failed", error);
        }
    }

    private void ShowSiteInformation()
    {
        var tab = activeTab;
        if (tab is null) return;
        siteInfoMenu.Items.Clear();
        if (tab.ConnectionState == ConnectionState.Local
            || !Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri))
        {
            siteInfoMenu.Items.Add(new ToolStripMenuItem(
                isPrivateMode
                    ? "Private start page \u2014 no normal saved data"
                    : "Offline start page \u2014 no request until you navigate") { Enabled = false });
            siteInfoMenu.Show(siteInfoButton, new Point(0, siteInfoButton.Height), ToolStripDropDownDirection.BelowLeft);
            return;
        }

        var connection = tab.ConnectionState switch
        {
            ConnectionState.LocalFile => "Local file on this device",
            ConnectionState.Secure => "Encrypted HTTPS",
            ConnectionState.Insecure => "Unencrypted HTTP",
            ConnectionState.CertificateError => "Certificate error \u2014 not trusted",
            ConnectionState.Loading => "Still being verified",
            ConnectionState.Failed => "Navigation did not complete",
            _ => "Unknown connection"
        };
        var locationLabel = uri.IsFile
            ? TextSafety.TruncateWithEllipsis(uri.LocalPath, 80)
            : uri.UserInfo.Length > 0
                ? $"{uri.IdnHost} — credentials in URL hidden"
                : uri.IdnHost;
        siteInfoMenu.Items.Add(new ToolStripMenuItem(locationLabel) { Enabled = false });
        siteInfoMenu.Items.Add(new ToolStripMenuItem($"Connection: {connection}") { Enabled = false });
        siteInfoMenu.Items.Add(new ToolStripMenuItem($"Blocked requests: {tab.BlockedRequestCount}") { Enabled = false });
        siteInfoMenu.Items.Add(new ToolStripSeparator());

        var currentZoom = Math.Round(tab.ZoomFactor * 100);
        var zoomOut = new ToolStripMenuItem("Zoom out");
        zoomOut.Click += (_, _) => ChangeZoom(-0.10);
        var zoomValue = new ToolStripMenuItem($"Zoom: {currentZoom:0}%") { Enabled = false };
        var zoomIn = new ToolStripMenuItem("Zoom in");
        zoomIn.Click += (_, _) => ChangeZoom(0.10);
        var resetZoom = new ToolStripMenuItem("Reset zoom to 100%");
        resetZoom.Click += (_, _) => ChangeZoom(0, reset: true);
        siteInfoMenu.Items.Add(zoomOut);
        siteInfoMenu.Items.Add(zoomValue);
        siteInfoMenu.Items.Add(zoomIn);
        siteInfoMenu.Items.Add(resetZoom);

        if (uri.IsFile)
        {
            var localReaderItem = new ToolStripMenuItem(
                tab.ReaderModeActive ? "Exit reader mode" : "Enter reader mode")
            {
                Enabled = tab.Core is not null
            };
            localReaderItem.Click += (_, _) => RunUiTask(
                ToggleReaderModeAsync,
                "Reader mode unavailable");
            siteInfoMenu.Items.Add(localReaderItem);
            siteInfoMenu.Show(
                siteInfoButton,
                new Point(0, siteInfoButton.Height),
                ToolStripDropDownDirection.BelowLeft);
            return;
        }

        var muteItem = new ToolStripMenuItem(IsTabMuted(tab) ? "Unmute site" : "Mute site");
        muteItem.Click += (_, _) => ToggleSiteMute(tab);
        siteInfoMenu.Items.Add(muteItem);
        var cleanLink = new ToolStripMenuItem("Copy clean page link");
        cleanLink.Click += (_, _) => CopyCleanPageLink();
        siteInfoMenu.Items.Add(cleanLink);
        var readerItem = new ToolStripMenuItem(tab.ReaderModeActive ? "Exit reader mode" : "Enter reader mode");
        readerItem.Enabled = tab.Core is not null;
        readerItem.Click += (_, _) => RunUiTask(ToggleReaderModeAsync, "Reader mode unavailable");
        siteInfoMenu.Items.Add(readerItem);
        siteInfoMenu.Items.Add(new ToolStripSeparator());
        var microphonePermission = new ToolStripMenuItem("Microphone: checking…") { Enabled = false };
        var cameraPermission = new ToolStripMenuItem("Camera: checking…") { Enabled = false };
        siteInfoMenu.Items.Add(microphonePermission);
        siteInfoMenu.Items.Add(cameraPermission);
        if (tab.Core is not null)
        {
            RunUiTask(
                () => PopulateMediaPermissionMenusAsync(
                    tab,
                    uri,
                    microphonePermission,
                    cameraPermission),
                "Could not read camera and microphone permissions");
        }
        var windowsPrivacy = new ToolStripMenuItem("Windows camera and microphone settings");
        var microphoneSettings = new ToolStripMenuItem("Microphone privacy settings");
        microphoneSettings.Click += (_, _) => OpenWindowsPrivacySettings("microphone");
        var cameraSettings = new ToolStripMenuItem("Camera privacy settings");
        cameraSettings.Click += (_, _) => OpenWindowsPrivacySettings("webcam");
        windowsPrivacy.DropDownItems.Add(microphoneSettings);
        windowsPrivacy.DropDownItems.Add(cameraSettings);
        siteInfoMenu.Items.Add(windowsPrivacy);
        var permissions = new ToolStripMenuItem("Review saved permissions");
        permissions.Click += (_, _) => ShowPermissionManager();
        siteInfoMenu.Items.Add(permissions);
        var voiceAudio = new ToolStripMenuItem("Voice and audio settings\u2026");
        voiceAudio.Click += (_, _) => ShowVoiceAudioSettingsDialog();
        siteInfoMenu.Items.Add(voiceAudio);
        var resetPreferences = new ToolStripMenuItem("Reset site preferences");
        resetPreferences.Click += (_, _) => ResetSitePreferences(uri);
        siteInfoMenu.Items.Add(resetPreferences);
        siteInfoMenu.Items.Add(new ToolStripSeparator());

        var shieldDisabled = IsExceptionHost(uri.Host);
        var shieldItem = new ToolStripMenuItem(
            shieldDisabled ? "Enable shield for this site" : "Disable shield for this site")
        {
            ToolTipText = "Uses this exact host only; a reload is required"
        };
        shieldItem.Click += (_, _) => ToggleSiteShield(uri.Host);
        siteInfoMenu.Items.Add(shieldItem);

        var resetPermissions = new ToolStripMenuItem("Reset saved permissions for this site")
        {
            ToolTipText = "Current scheme, host, and port only"
        };
        resetPermissions.Click += (_, _) => RunUiTask(
            () => ResetSitePermissionsAsync(uri),
            "Could not reset saved permissions");
        siteInfoMenu.Items.Add(resetPermissions);
        siteInfoMenu.Show(siteInfoButton, new Point(0, siteInfoButton.Height), ToolStripDropDownDirection.BelowLeft);
    }

    private bool IsExceptionHost(string host)
    {
        if (adBlockExceptionHosts.Count == 0) return false;
        var normalized = BrowserPolicy.NormalizeExactHost(host);
        return normalized is not null && adBlockExceptionHosts.Contains(normalized);
    }

    private void ToggleSiteShield(string host)
    {
        var normalized = BrowserPolicy.NormalizeExactHost(host);
        if (normalized is null) return;
        if (adBlockExceptionHosts.Remove(normalized))
        {
            var existing = state.AdBlockExceptionHosts.FindIndex(item =>
                item.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) state.AdBlockExceptionHosts.RemoveAt(existing);
            foreach (var tab in tabs.Where(item =>
                item.Core is not null
                && BrowserPolicy.IsExactHost(HostFromUrl(item.Core.Source), normalized)))
            {
                tab.AdBlockControlChannel = null;
            }
            ShowTransientStatus($"Shield restored for {normalized}; reload required");
        }
        else
        {
            if (state.AdBlockExceptionHosts.Count >= BrowserStateStore.MaximumAdBlockExceptionHosts)
            {
                ShowTransientStatus("You can save up to 200 site shield exceptions");
                return;
            }
            state.AdBlockExceptionHosts.Add(normalized);
            adBlockExceptionHosts.Add(normalized);
            foreach (var tab in tabs.Where(item =>
                item.Core is not null
                && BrowserPolicy.IsExactHost(HostFromUrl(item.Core.Source), normalized)))
            {
                RunUiTask(
                    () => DisableAdBlockForCurrentDocumentAsync(tab),
                    "Could not disable the current page shield");
            }
            ShowTransientStatus($"Shield disabled for {normalized}");
        }
        RefreshAdBlockPagePolicies();
        if (!isPrivateMode) ScheduleStateSave();
    }

    private void ToggleActiveSiteShield()
    {
        var host = activeTab is null ? null : SitePreferencePolicy.GetHost(activeTab.Url);
        if (host is null)
        {
            ShowTransientStatus("Open a website before changing the site shield");
            return;
        }
        ToggleSiteShield(host);
    }

    private void ToggleActiveKeepAwake()
    {
        var tab = activeTab;
        if (tab is null || tab.IsStartPage)
        {
            ShowTransientStatus("Open a website before protecting it");
            return;
        }
        tab.KeepAwake = !tab.KeepAwake;
        SyncKeepAwakeHostState(tab);
        if (tab.KeepAwake)
        {
            ResumeTab(tab);
            if (tab.IsDiscarded)
            {
                RunUiTask(
                    () => RestoreDiscardedTabAsync(tab, focusPage: false),
                    "Could not reload the protected tab");
            }
        }
        else if (tab != activeTab || isWindowMinimized)
        {
            QueueBackgroundTabReduction(tab);
        }
        ApplyLiveTabMemoryTarget(tab, tab == activeTab && !isWindowMinimized);
        RefreshMemorySweepTimer();
        UpdateTabHeader(tab);
        ShowTransientStatus(tab.KeepAwake ? "Tab kept active" : "Tab can sleep again");
    }

    private void ResetSitePreferences(Uri uri)
    {
        var host = SitePreferencePolicy.GetHost(uri.Host);
        if (host is null) return;
        state.SiteZoom.RemoveAll(item => BrowserPolicy.IsExactHost(item.Host, host));
        state.MutedHosts.RemoveAll(item => BrowserPolicy.IsExactHost(item, host));
        foreach (var tab in tabs.Where(item =>
                     !item.IsClosed
                     && SitePreferencePolicy.GetHost(item.Url)?.Equals(host, StringComparison.OrdinalIgnoreCase) == true))
        {
            tab.ZoomFactor = 1.0;
            tab.IsMuted = false;
            if (tab.View is { CoreWebView2: not null } view)
            {
                try
                {
                    applyingZoomPreference = true;
                    view.ZoomFactor = 1.0;
                    view.CoreWebView2.IsMuted = false;
                }
                catch (Exception error)
                {
                    stateStore.Log("Could not reset site preferences", error);
                }
                finally
                {
                    applyingZoomPreference = false;
                }
            }
            UpdateTabHeader(tab);
        }
        if (!isPrivateMode) ScheduleStateSave();
        ShowTransientStatus($"Site preferences reset for {host}");
    }

    private void RefreshAdBlockPagePolicies()
    {
        if (!adBlockEnabled) return;
        foreach (var tab in tabs.Where(item => item.Core is not null && !item.IsClosed))
        {
            RunUiTask(
                () => InstallAdBlockPageScriptsAsync(tab),
                "Could not update the site shield policy");
        }
    }

    private static string GetDisableAdBlockPageScript(BrowserTab tab)
    {
        var dispatch = tab.AdBlockControlChannel is null
            ? string.Empty
            : "window.dispatchEvent(new Event("
                + JsonSerializer.Serialize(tab.AdBlockControlChannel)
                + "));";
        return dispatch
            + "document.getElementById('__misha_adblock_style')?.remove();"
            + "document.getElementById('__misha_adblock_remote_style')?.remove();";
    }

    private async Task DisableAdBlockForCurrentDocumentAsync(BrowserTab tab)
    {
        var script = GetDisableAdBlockPageScript(tab);
        if (tab.Core is not null)
        {
            try { await tab.Core.ExecuteScriptAsync(script); }
            catch (Exception error) { stateStore.Log("Could not disable the top-level page shield", error); }
        }
        foreach (var frame in tab.AdBlockFrames.ToArray())
        {
            try { await frame.ExecuteScriptAsync(script); }
            catch (Exception error) { stateStore.Log("Could not disable a frame page shield", error); }
        }
    }

    private async Task PopulateMediaPermissionMenusAsync(
        BrowserTab tab,
        Uri uri,
        ToolStripMenuItem microphoneMenu,
        ToolStripMenuItem cameraMenu)
    {
        var core = tab.Core;
        var origin = NormalizePermissionOrigin(uri.GetLeftPart(UriPartial.Authority));
        if (core is null || origin is null) return;

        var microphoneState = CoreWebView2PermissionState.Default;
        var cameraState = CoreWebView2PermissionState.Default;
        var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
        for (var index = 0; index < settings.Count; index++)
        {
            var setting = settings[index];
            if (!setting.PermissionOrigin.Equals(origin, StringComparison.OrdinalIgnoreCase)) continue;
            if (setting.PermissionKind == CoreWebView2PermissionKind.Microphone)
            {
                microphoneState = setting.PermissionState;
            }
            else if (setting.PermissionKind == CoreWebView2PermissionKind.Camera)
            {
                cameraState = setting.PermissionState;
            }
        }

        if (isClosing
            || tab.IsClosed
            || tab.Core != core
            || activeTab != tab
            || microphoneMenu.Owner != siteInfoMenu
            || cameraMenu.Owner != siteInfoMenu)
        {
            return;
        }

        ConfigureMediaPermissionMenu(
            tab,
            origin,
            microphoneMenu,
            CoreWebView2PermissionKind.Microphone,
            microphoneState,
            tab.MicrophoneAllowedOrigins.Contains(origin));
        ConfigureMediaPermissionMenu(
            tab,
            origin,
            cameraMenu,
            CoreWebView2PermissionKind.Camera,
            cameraState,
            tab.CameraAllowedOrigins.Contains(origin));
    }

    private void ConfigureMediaPermissionMenu(
        BrowserTab tab,
        string origin,
        ToolStripMenuItem menu,
        CoreWebView2PermissionKind kind,
        CoreWebView2PermissionState state,
        bool temporarilyAllowed)
    {
        var name = kind == CoreWebView2PermissionKind.Microphone ? "Microphone" : "Camera";
        var stateLabel = state switch
        {
            CoreWebView2PermissionState.Allow => "Allow",
            CoreWebView2PermissionState.Deny => "Block",
            _ when temporarilyAllowed => "Allow once",
            _ => "Ask"
        };
        menu.Text = $"{name}: {stateLabel}";
        menu.Enabled = true;
        menu.DropDownItems.Clear();

        AddSitePermissionChoice(menu, tab, origin, kind, "Allow", CoreWebView2PermissionState.Allow,
            state == CoreWebView2PermissionState.Allow);
        AddSitePermissionChoice(menu, tab, origin, kind, "Ask every time", CoreWebView2PermissionState.Default,
            state == CoreWebView2PermissionState.Default && !temporarilyAllowed);
        AddSitePermissionChoice(menu, tab, origin, kind, "Block", CoreWebView2PermissionState.Deny,
            state == CoreWebView2PermissionState.Deny);
    }

    private void AddSitePermissionChoice(
        ToolStripMenuItem parent,
        BrowserTab tab,
        string origin,
        CoreWebView2PermissionKind kind,
        string label,
        CoreWebView2PermissionState state,
        bool isChecked)
    {
        var item = new ToolStripMenuItem(label) { Checked = isChecked };
        item.Click += (_, _) =>
        {
            siteInfoMenu.Close();
            RunUiTask(
                () => SetSiteMediaPermissionAsync(tab, origin, kind, state),
                $"Could not update {GetPermissionDisplayName(kind)} access");
        };
        parent.DropDownItems.Add(item);
    }

    private async Task SetSiteMediaPermissionAsync(
        BrowserTab tab,
        string origin,
        CoreWebView2PermissionKind kind,
        CoreWebView2PermissionState state)
    {
        var core = tab.Core;
        if (core is null
            || tab.IsClosed
            || !origin.Equals(NormalizePermissionOrigin(core.Source), StringComparison.OrdinalIgnoreCase))
        {
            ShowTransientStatus("The page changed before its permissions could be updated");
            return;
        }

        await core.Profile.SetPermissionStateAsync(kind, origin, state);
        // The profile mutation is shared by every live tab, even if the tab
        // that opened the menu navigated while the asynchronous write ran.
        UpdateLiveTabsForMediaPermission(origin, kind, state);
        if (tab.Core != core
            || tab.IsClosed
            || !origin.Equals(NormalizePermissionOrigin(core.Source), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var permission = GetPermissionDisplayName(kind);
        var message = state switch
        {
            CoreWebView2PermissionState.Allow => $"{permission} allowed for {origin}; retry the call",
            CoreWebView2PermissionState.Deny => $"{permission} blocked for {origin}",
            _ => $"{permission} will ask again for {origin}"
        };
        ShowTransientStatus(message);
    }

    private void OpenWindowsPrivacySettings(string page)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"ms-settings:privacy-{page}") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            stateStore.Log("Could not open Windows privacy settings", error);
            ShowTransientStatus("Open Windows Settings > Privacy & security to check device access");
        }
    }

    private async Task ResetSitePermissionsAsync(Uri uri)
    {
        var core = activeTab?.Core;
        if (core is null)
        {
            ShowTransientStatus("Site permissions are unavailable while the browser is stopped");
            return;
        }

        var origin = uri.GetLeftPart(UriPartial.Authority);
        try
        {
            var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
            for (var index = 0; index < settings.Count; index++)
            {
                var setting = settings[index];
                if (setting.PermissionOrigin.Equals(origin, StringComparison.OrdinalIgnoreCase))
                {
                    await core.Profile.SetPermissionStateAsync(
                        setting.PermissionKind,
                        setting.PermissionOrigin,
                        CoreWebView2PermissionState.Default);
                }
            }
            foreach (var tab in tabs.Where(item =>
                         !item.IsClosed && TabContainsMediaOrigin(item, origin)))
            {
                ApplyTabMediaPermissions(tab, origin, microphoneAllowed: false, cameraAllowed: false);
            }
            ShowTransientStatus($"Saved permissions reset for {origin}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not reset saved permissions", error);
            ShowTransientStatus("Saved permissions could not be reset for this site");
        }
    }

    private void OnContextMenuRequested(
        BrowserTab tab,
        CoreWebView2ContextMenuRequestedEventArgs args)
    {
        tab.ContextLinkTarget = null;
        try
        {
            var target = args.ContextMenuTarget;
            if (!target.HasLinkUri) return;
            var link = target.LinkUri;
            if (string.IsNullOrWhiteSpace(link) || !BrowserPolicy.IsHttpUrl(link)) return;
            tab.ContextLinkTarget = link;
            var item = tab.CleanLinkMenuItem;
            if (item is null)
            {
                var menuEnvironment = environment;
                if (menuEnvironment is null) return;
                item = menuEnvironment.CreateContextMenuItem(
                    "Copy clean link",
                    null,
                    CoreWebView2ContextMenuItemKind.Command);
                EventHandler<object> selectedHandler = (_, _) => CopyContextLink(tab);
                item.CustomItemSelected += selectedHandler;
                tab.CleanLinkMenuItem = item;
                tab.CleanLinkMenuItemSelectedHandler = selectedHandler;
            }
            item.IsEnabled = true;
            args.MenuItems.Add(item);
        }
        catch (Exception error)
        {
            stateStore.Log("Could not add the clean-link context menu item", error);
        }
    }

    private void CopyContextLink(BrowserTab tab)
    {
        var target = tab.ContextLinkTarget;
        if (tab.IsClosed
            || tab.Core is null
            || string.IsNullOrWhiteSpace(target)
            || !BrowserPolicy.IsHttpUrl(target))
        {
            ShowTransientStatus("The link is no longer available");
            return;
        }
        try
        {
            Clipboard.SetText(CleanLinkPolicy.Clean(target));
            ShowTransientStatus("Clean link copied");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not copy the context link", error);
            ShowTransientStatus("Could not copy the link");
        }
    }

    private async Task RefreshTabMediaPermissionStateAsync(
        BrowserTab tab,
        long expectedDocumentGeneration)
    {
        var core = tab.Core;
        var origin = NormalizePermissionOrigin(core?.Source ?? tab.Url);
        if (core is null || origin is null || tab.IsClosed) return;

        await RefreshTabMediaPermissionOriginAsync(
            tab,
            origin,
            expectedDocumentGeneration,
            () => tab.Core == core
                && origin.Equals(
                    NormalizePermissionOrigin(core.Source),
                    StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshTabMediaPermissionOriginAsync(
        BrowserTab tab,
        string? originOrUrl,
        long expectedDocumentGeneration,
        Func<bool> isCurrent)
    {
        var core = tab.Core;
        var origin = NormalizePermissionOrigin(originOrUrl);
        if (core is null
            || origin is null
            || tab.IsClosed
            || tab.DocumentNavigationGeneration != expectedDocumentGeneration)
        {
            return;
        }

        tab.PendingMediaPermissionRefreshes++;
        try
        {
            var microphoneAllowed = false;
            var cameraAllowed = false;
            try
            {
                var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
                for (var index = 0; index < settings.Count; index++)
                {
                    var setting = settings[index];
                    if (setting.PermissionState != CoreWebView2PermissionState.Allow
                        || !setting.PermissionOrigin.Equals(origin, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (setting.PermissionKind == CoreWebView2PermissionKind.Microphone)
                    {
                        microphoneAllowed = true;
                    }
                    else if (setting.PermissionKind == CoreWebView2PermissionKind.Camera)
                    {
                        cameraAllowed = true;
                    }
                }
            }
            catch (Exception error)
            {
                stateStore.Log("Could not refresh camera and microphone permissions", error);
                return;
            }

            if (tab.IsClosed
                || tab.Core != core
                || tab.DocumentNavigationGeneration != expectedDocumentGeneration
                || !isCurrent())
            {
                return;
            }
            ApplyTabMediaPermissions(tab, origin, microphoneAllowed, cameraAllowed);
        }
        finally
        {
            if (tab.DocumentNavigationGeneration == expectedDocumentGeneration)
            {
                tab.PendingMediaPermissionRefreshes = Math.Max(
                    0,
                    tab.PendingMediaPermissionRefreshes - 1);
            }
        }
    }

    private void UpdateTabMediaPermissionState(
        BrowserTab tab,
        string origin,
        CoreWebView2PermissionKind kind,
        CoreWebView2PermissionState permissionState)
    {
        if (kind is not (CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera)) return;
        var microphoneAllowed = tab.MicrophoneAllowedOrigins.Contains(origin);
        var cameraAllowed = tab.CameraAllowedOrigins.Contains(origin);
        var allowed = permissionState == CoreWebView2PermissionState.Allow;
        if (kind == CoreWebView2PermissionKind.Microphone) microphoneAllowed = allowed;
        else cameraAllowed = allowed;
        ApplyTabMediaPermissions(tab, origin, microphoneAllowed, cameraAllowed);
    }

    private void UpdateLiveTabsForMediaPermission(
        string origin,
        CoreWebView2PermissionKind kind,
        CoreWebView2PermissionState permissionState)
    {
        foreach (var tab in tabs.Where(item =>
                     !item.IsClosed && TabContainsMediaOrigin(item, origin)))
        {
            UpdateTabMediaPermissionState(tab, origin, kind, permissionState);
        }
    }

    private static bool TabContainsMediaOrigin(BrowserTab tab, string origin)
    {
        return origin.Equals(
                NormalizePermissionOrigin(tab.Core?.Source ?? tab.Url),
                StringComparison.OrdinalIgnoreCase)
            || tab.MicrophoneAllowedOrigins.Contains(origin)
            || tab.CameraAllowedOrigins.Contains(origin)
            || tab.FrameOrigins.Values.Any(value =>
                value.Equals(origin, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshLiveTabMediaPermissionsAsync()
    {
        foreach (var tab in tabs.Where(item => !item.IsClosed && item.Core is not null).ToArray())
        {
            var core = tab.Core!;
            var documentGeneration = tab.DocumentNavigationGeneration;
            var origins = new HashSet<string>(tab.MicrophoneAllowedOrigins, StringComparer.OrdinalIgnoreCase);
            origins.UnionWith(tab.CameraAllowedOrigins);
            origins.UnionWith(tab.FrameOrigins.Values);
            var topLevelOrigin = NormalizePermissionOrigin(core.Source);
            if (topLevelOrigin is not null) origins.Add(topLevelOrigin);
            foreach (var origin in origins)
            {
                await RefreshTabMediaPermissionOriginAsync(
                    tab,
                    origin,
                    documentGeneration,
                    () => !tab.IsClosed && tab.Core == core);
            }
        }
    }

    private void ApplyTabMediaPermissions(
        BrowserTab tab,
        string origin,
        bool microphoneAllowed,
        bool cameraAllowed)
    {
        var changed = SetOriginPermission(tab.MicrophoneAllowedOrigins, origin, microphoneAllowed)
            | SetOriginPermission(tab.CameraAllowedOrigins, origin, cameraAllowed);
        if (!changed) return;

        OnTabMediaPermissionStateChanged(tab);
    }

    private static bool SetOriginPermission(HashSet<string> origins, string origin, bool allowed)
    {
        return allowed ? origins.Add(origin) : origins.Remove(origin);
    }

    private void OnTabMediaPermissionStateChanged(BrowserTab tab)
    {
        if (tab.HasMediaCapturePermission)
        {
            tab.ConsecutiveSuspendFailures = 0;
            ResumeTab(tab);
        }
        ApplyLiveTabMemoryTarget(tab, tab == activeTab && !isWindowMinimized);
        RefreshMemorySweepTimer();
        UpdateTabHeader(tab);
    }

    private void ClearTabMediaPermissions(BrowserTab tab)
    {
        tab.PendingMediaPermissionRequests = 0;
        tab.PendingMediaPermissionOrigin = null;
        tab.PendingMediaPermissionRefreshes = 0;
        var changed = tab.MicrophoneAllowedOrigins.Count > 0 || tab.CameraAllowedOrigins.Count > 0;
        tab.MicrophoneAllowedOrigins.Clear();
        tab.CameraAllowedOrigins.Clear();
        tab.FrameOrigins.Clear();
        if (changed) OnTabMediaPermissionStateChanged(tab);
    }

    private static bool IsMediaCapturePermission(CoreWebView2PermissionKind kind)
    {
        return kind is CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera;
    }

    private void OnPermissionRequested(
        BrowserTab tab,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        CoreWebView2Deferral? deferral = null;
        var mediaRequestRegistered = false;
        try
        {
            deferral = args.GetDeferral();
            args.Handled = true;
            var origin = NormalizePermissionOrigin(args.Uri);
            if (origin is null || args.PermissionKind == CoreWebView2PermissionKind.UnknownPermission)
            {
                args.State = CoreWebView2PermissionState.Deny;
                args.SavesInProfile = false;
                deferral.Complete();
                return;
            }

            if (args.PermissionKind == CoreWebView2PermissionKind.Autoplay)
            {
                args.State = CoreWebView2PermissionState.Allow;
                args.SavesInProfile = true;
                deferral.Complete();
                return;
            }

            if (args.PermissionKind == CoreWebView2PermissionKind.Microphone)
            {
                if (!SitePreferencePolicy.CanRequestMicrophone(
                    state.AudioInputPolicy,
                    state.AllowedMicrophoneHosts,
                    state.BlockedMicrophoneHosts,
                    origin,
                    out var autoDecision))
                {
                    args.State = CoreWebView2PermissionState.Deny;
                    args.SavesInProfile = false;
                    deferral.Complete();
                    ShowTransientStatus(state.AudioInputPolicy == AudioInputPolicy.BlockAll
                        ? "Microphone access blocked by Voice & Audio settings"
                        : $"Microphone access blocked for {origin} by Voice & Audio settings");
                    return;
                }
                if (autoDecision == true)
                {
                    args.State = CoreWebView2PermissionState.Allow;
                    args.SavesInProfile = !isPrivateMode;
                    UpdateTabMediaPermissionState(tab, origin, args.PermissionKind, CoreWebView2PermissionState.Allow);
                    deferral.Complete();
                    return;
                }
            }

            if (IsMediaCapturePermission(args.PermissionKind))
            {
                if (tab.PendingMediaPermissionRequests == 0)
                {
                    tab.PendingMediaPermissionOrigin = origin;
                }
                else if (!origin.Equals(
                    tab.PendingMediaPermissionOrigin,
                    StringComparison.OrdinalIgnoreCase))
                {
                    tab.PendingMediaPermissionOrigin = null;
                }
                tab.PendingMediaPermissionRequests++;
                mediaRequestRegistered = true;
            }

            if (permissionQueue.Count + (activePermissionRequest is null ? 0 : 1)
                >= MaximumPendingPermissionRequests)
            {
                if (mediaRequestRegistered)
                {
                    tab.PendingMediaPermissionRequests = Math.Max(0, tab.PendingMediaPermissionRequests - 1);
                    if (tab.PendingMediaPermissionRequests == 0) tab.PendingMediaPermissionOrigin = null;
                    mediaRequestRegistered = false;
                }
                args.State = CoreWebView2PermissionState.Deny;
                args.SavesInProfile = false;
                deferral.Complete();
                deferral = null;
                ShowTransientStatus("Blocked excessive website permission prompts");
                return;
            }

            permissionQueue.Enqueue(new PermissionRequest(
                tab,
                tab.Core!,
                tab.DocumentNavigationGeneration,
                args,
                deferral,
                origin,
                GetPermissionDisplayName(args.PermissionKind),
                args.PermissionKind,
                args.IsUserInitiated));
            deferral = null;
            ShowNextPermissionRequest();
        }
        catch (Exception error)
        {
            if (mediaRequestRegistered)
            {
                tab.PendingMediaPermissionRequests = Math.Max(0, tab.PendingMediaPermissionRequests - 1);
                if (tab.PendingMediaPermissionRequests == 0) tab.PendingMediaPermissionOrigin = null;
            }
            stateStore.Log("Could not queue a website permission request", error);
            try
            {
                args.Handled = true;
                args.State = CoreWebView2PermissionState.Deny;
                args.SavesInProfile = false;
            }
            catch { }
            try { deferral?.Complete(); }
            catch { }
        }
    }

    private void ShowNextPermissionRequest()
    {
        if (isClosing || activePermissionRequest is not null) return;
        while (permissionQueue.Count > 0)
        {
            var request = permissionQueue.Dequeue();
            if (request.Tab.IsClosed
                || request.Tab.Core is null
                || !ReferenceEquals(request.Core, request.Tab.Core)
                || request.DocumentGeneration != request.Tab.DocumentNavigationGeneration)
            {
                CompletePermissionRequest(request, PermissionPromptDecision.Cancel);
                continue;
            }
            activePermissionRequest = request;
            permissionPrompt ??= CreatePermissionPrompt();
            permissionPrompt.ShowRequest(
                this,
                request.Origin,
                request.Permission,
                request.IsUserInitiated,
                allowAlways: !isPrivateMode);
            return;
        }
    }

    private PermissionPromptForm CreatePermissionPrompt()
    {
        var prompt = new PermissionPromptForm();
        prompt.DecisionRequested += (_, decision) =>
        {
            var request = activePermissionRequest;
            activePermissionRequest = null;
            if (request is not null) CompletePermissionRequest(request, decision);
            ShowNextPermissionRequest();
        };
        return prompt;
    }

    private void CompletePermissionRequest(
        PermissionRequest request,
        PermissionPromptDecision decision)
    {
        if (!request.TryBeginCompletion()) return;
        try
        {
            var requestIsCurrent = !request.Tab.IsClosed
                && ReferenceEquals(request.Core, request.Tab.Core)
                && request.DocumentGeneration == request.Tab.DocumentNavigationGeneration;
            var effectiveDecision = !requestIsCurrent
                ? PermissionPromptDecision.Cancel
                : isPrivateMode && decision == PermissionPromptDecision.AllowAlways
                ? PermissionPromptDecision.AllowOnce
                : decision;
            var permissionState = effectiveDecision switch
            {
                PermissionPromptDecision.AllowOnce or PermissionPromptDecision.AllowAlways
                    => CoreWebView2PermissionState.Allow,
                _ => CoreWebView2PermissionState.Deny
            };
            var savesInProfile = !isPrivateMode
                && effectiveDecision is PermissionPromptDecision.AllowAlways or PermissionPromptDecision.Block;
            request.Args.State = permissionState;
            request.Args.SavesInProfile = savesInProfile;
            UpdateTabMediaPermissionState(
                request.Tab,
                request.Origin,
                request.PermissionKind,
                permissionState);
            if (savesInProfile)
            {
                UpdateLiveTabsForMediaPermission(
                    request.Origin,
                    request.PermissionKind,
                    permissionState);
            }
        }
        catch (Exception error)
        {
            stateStore.Log("Could not apply a website permission decision", error);
        }
        finally
        {
            if (IsMediaCapturePermission(request.PermissionKind))
            {
                request.Tab.PendingMediaPermissionRequests = Math.Max(
                    0,
                    request.Tab.PendingMediaPermissionRequests - 1);
                if (request.Tab.PendingMediaPermissionRequests == 0)
                {
                    request.Tab.PendingMediaPermissionOrigin = null;
                }
            }
            try { request.Deferral.Complete(); }
            catch (Exception error) { stateStore.Log("Could not complete a website permission request", error); }
        }
    }

    private void CancelPermissionsForTab(BrowserTab tab)
    {
        if (activePermissionRequest?.Tab == tab)
        {
            permissionPrompt?.CloseRequest();
            var request = activePermissionRequest;
            activePermissionRequest = null;
            if (request is not null) CompletePermissionRequest(request, PermissionPromptDecision.Cancel);
        }

        foreach (var request in permissionQueue.Where(item => item.Tab == tab).ToArray())
        {
            CompletePermissionRequest(request, PermissionPromptDecision.Cancel);
        }
        var remaining = permissionQueue.Where(item => item.Tab != tab).ToArray();
        permissionQueue.Clear();
        foreach (var request in remaining) permissionQueue.Enqueue(request);
        if (!replacingWindow) ShowNextPermissionRequest();
    }

    private void ShowVoiceAudioSettingsDialog()
    {
        var activeHost = activeTab is not null && !activeTab.IsStartPage
            ? SitePreferencePolicy.GetHost(activeTab.Url)
            : null;

        using var dialog = new VoiceAudioSettingsDialog(
            state,
            activeHost,
            result =>
            {
                state.AudioOutputPolicy = result.AudioOutputPolicy;
                state.AllowedAudioHosts = result.AllowedAudioHosts.ToList();
                state.MutedHosts = result.MutedHosts.ToList();
                state.AudioInputPolicy = result.AudioInputPolicy;
                state.AllowedMicrophoneHosts = result.AllowedMicrophoneHosts.ToList();
                state.BlockedMicrophoneHosts = result.BlockedMicrophoneHosts.ToList();

                ApplyAudioOutputPolicyToAllTabs();
                ApplyAudioInputPolicyToAllTabs();
                ScheduleStateSave();

                ShowTransientStatus("Voice and audio settings updated");
            });

        dialog.ShowDialog(this);
    }

    private void ApplyAudioOutputPolicyToAllTabs()
    {
        foreach (var liveTab in tabs.Where(item => !item.IsClosed && !item.IsStartPage))
        {
            var shouldMute = isPrivateMode
                ? liveTab.IsMuted
                : SitePreferencePolicy.ShouldMuteAudio(state.AudioOutputPolicy, state.AllowedAudioHosts, state.MutedHosts, liveTab.Url);
            liveTab.IsMuted = shouldMute;
            if (liveTab.Core is not null) liveTab.Core.IsMuted = shouldMute;
            UpdateTabHeader(liveTab);
        }
    }

    private void ApplyAudioInputPolicyToAllTabs()
    {
        foreach (var liveTab in tabs.Where(item => !item.IsClosed && !item.IsStartPage))
        {
            var host = SitePreferencePolicy.GetHost(liveTab.Url);
            if (state.AudioInputPolicy == AudioInputPolicy.BlockAll)
            {
                ClearTabMediaPermissions(liveTab);
            }
            else if (state.AudioInputPolicy == AudioInputPolicy.SpecificSitesOnly)
            {
                if (host is null || !state.AllowedMicrophoneHosts.Any(h => BrowserPolicy.IsExactHost(h, host)))
                {
                    var changed = liveTab.MicrophoneAllowedOrigins.Count > 0;
                    liveTab.MicrophoneAllowedOrigins.Clear();
                    if (changed) OnTabMediaPermissionStateChanged(liveTab);
                }
            }
        }
    }

    private void ShowPermissionManager()
    {
        var core = activeTab?.Core ?? tabs.Select(item => item.Core).FirstOrDefault(item => item is not null);
        if (core is null)
        {
            ShowTransientStatus("Permission management is unavailable until a website is open");
            return;
        }
        permissionManager ??= CreatePermissionManager();
        RunUiTask(
            () => LoadPermissionManagerAsync(core),
            "Could not open the permission manager");
    }

    private PermissionManagerForm CreatePermissionManager()
    {
        var manager = new PermissionManagerForm();
        manager.ActionRequested += (_, action) => RunUiTask(
            () => HandlePermissionManagerActionAsync(action),
            "Could not update saved permissions");
        return manager;
    }

    private async Task LoadPermissionManagerAsync(CoreWebView2 core)
    {
        var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
        var rows = new List<PermissionSettingRow>(settings.Count);
        for (var index = 0; index < settings.Count; index++)
        {
            var setting = settings[index];
            rows.Add(new PermissionSettingRow(
                setting.PermissionOrigin,
                GetPermissionDisplayName(setting.PermissionKind),
                setting.PermissionState == CoreWebView2PermissionState.Allow ? "Allow" : "Block",
                setting));
        }
        permissionManager!.Open(this, rows);
    }

    private async Task HandlePermissionManagerActionAsync(PermissionManagerAction action)
    {
        var core = activeTab?.Core ?? tabs.Select(item => item.Core).FirstOrDefault(item => item is not null);
        var selected = permissionManager?.SelectedRow;
        if (core is null) return;
        if (action == PermissionManagerAction.ResetAll)
        {
            var answer = MessageBox.Show(
                "Reset every saved website permission?",
                "Reset all permissions?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            var settings = await core.Profile.GetNonDefaultPermissionSettingsAsync();
            for (var index = 0; index < settings.Count; index++)
            {
                var setting = settings[index];
                await core.Profile.SetPermissionStateAsync(
                    setting.PermissionKind,
                    setting.PermissionOrigin,
                    CoreWebView2PermissionState.Default);
            }
        }
        else if (selected?.Token is CoreWebView2PermissionSetting setting)
        {
            await core.Profile.SetPermissionStateAsync(
                setting.PermissionKind,
                setting.PermissionOrigin,
                CoreWebView2PermissionState.Default);
        }
        await RefreshLiveTabMediaPermissionsAsync();
        await LoadPermissionManagerAsync(core);
        ShowTransientStatus("Saved permissions updated");
    }

    private static string? NormalizePermissionOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        var host = uri.HostNameType == UriHostNameType.IPv6
            ? $"[{uri.IdnHost}]"
            : uri.IdnHost;
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme.ToLowerInvariant()}://{host}{port}";
    }

    private static string GetPermissionDisplayName(CoreWebView2PermissionKind kind)
    {
        return kind switch
        {
            CoreWebView2PermissionKind.Microphone => "microphone",
            CoreWebView2PermissionKind.Camera => "camera",
            CoreWebView2PermissionKind.Geolocation => "location",
            CoreWebView2PermissionKind.Notifications => "notifications",
            CoreWebView2PermissionKind.ClipboardRead => "clipboard read",
            CoreWebView2PermissionKind.MultipleAutomaticDownloads => "multiple downloads",
            CoreWebView2PermissionKind.FileReadWrite => "file access",
            CoreWebView2PermissionKind.Autoplay => "autoplay",
            CoreWebView2PermissionKind.LocalFonts => "local fonts",
            CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "MIDI devices",
            CoreWebView2PermissionKind.WindowManagement => "window management",
            CoreWebView2PermissionKind.PersistentStorage => "persistent storage",
            _ => kind.ToString()
        };
    }

    private void ShowAbout()
    {
        var runtimeVersion = environment?.BrowserVersionString ?? "not running";
        var resourceMode = ultraLightEnabled ? "Ultra-light" : memorySaverEnabled ? "Memory saver" : "Off";
        MessageBox.Show(
            $"MishaWeb {Application.ProductVersion}\n\nA resource-light native Windows browser powered by WebView2.\n\nWebView2 Runtime: {runtimeVersion}\nTabs: {tabs.Count}\nResource mode: {resourceMode}\n\nKeyboard help: Ctrl+L, Ctrl+T, Ctrl+W, Ctrl+Shift+T, Ctrl+Tab, Ctrl+F, Ctrl+D, Ctrl+P, Ctrl+Shift+S, F11.",
            "About MishaWeb",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ShowTabContextMenu(BrowserTab tab, Point screenLocation)
    {
        tabMenu.Items.Clear();
        AddMenuItem(tabMenu, "Reload", "Ctrl+R", () =>
        {
            ActivateTab(tab);
            ReloadOrStop();
        });
        AddMenuItem(tabMenu, "Duplicate", string.Empty, () =>
            RunUiTask(() => OpenNewTabAsync(tab.IsStartPage ? StartPage.Url : tab.Url), "Could not duplicate the tab"));
        var pinTabItem = AddMenuItem(
            tabMenu,
            tab.IsPinned ? "Unpin tab" : "Pin tab",
            string.Empty,
            () => SetTabPinned(tab, !tab.IsPinned));
        pinTabItem.Enabled = !tab.IsStartPage;
        var pinItem = AddMenuItem(
            tabMenu,
            IsPinned(tab.Url) ? "Unpin quick link" : "Pin quick link",
            string.Empty,
            () => SetPinnedStartPageLink(
                new StartPageLink(tab.Title, tab.Url, "Pinned"),
                !IsPinned(tab.Url)));
        pinItem.Enabled = !isPrivateMode && !tab.IsStartPage && BrowserPolicy.IsHttpUrl(tab.Url);
        AddMenuItem(tabMenu, "Reopen closed tab", "Ctrl+Shift+T", () =>
            RunUiTask(RestoreClosedTabAsync, "Could not restore the tab"));
        tabMenu.Items.Add(new ToolStripSeparator());
        var muteItem = AddMenuItem(tabMenu, IsTabMuted(tab) ? "Unmute site" : "Mute site", string.Empty, () =>
        {
            ToggleSiteMute(tab);
        });
        muteItem.Enabled = !tab.IsStartPage;
        var keepAwakeItem = AddMenuItem(
            tabMenu,
            "Keep active in background",
            string.Empty,
            () =>
            {
                var newKeepAwake = !tab.KeepAwake;
                tab.KeepAwake = newKeepAwake;
                SyncKeepAwakeHostState(tab);
                if (newKeepAwake)
                {
                    tab.ConsecutiveSuspendFailures = 0;
                    ResumeTab(tab);
                    if (tab.IsDiscarded)
                    {
                        RunUiTask(
                            () => RestoreDiscardedTabAsync(tab, focusPage: false),
                            "Could not reload the protected tab");
                    }
                }
                else if (tab != activeTab || isWindowMinimized)
                {
                    QueueBackgroundTabReduction(tab);
                }
                ApplyLiveTabMemoryTarget(tab, tab == activeTab && !isWindowMinimized);
                RefreshMemorySweepTimer();
                UpdateTabHeader(tab);
                ShowTransientStatus(tab.KeepAwake
                    ? "This tab will stay active in the background"
                    : "This tab can sleep again");
            });
        keepAwakeItem.Checked = tab.KeepAwake;
        keepAwakeItem.Enabled = GetKeepAwakeHost(tab.Url) is not null;
        var unloadItem = AddMenuItem(
            tabMenu,
            "Unload tab",
            string.Empty,
            () => RunUiTask(() => UnloadTabAsync(tab), "Could not unload the tab"));
        unloadItem.Enabled = CanUnloadTab(tab);
        AddMenuItem(tabMenu, "Close other tabs", string.Empty, () =>
        {
            foreach (var other in tabs.Where(item => item != tab && !item.IsPinned).ToArray())
            {
                CloseTab(other, ensureReplacement: false);
            }
            ActivateTab(tab);
        });
        AddMenuItem(tabMenu, "Close tab", "Ctrl+W", () => CloseTab(tab));
        tabMenu.Show(screenLocation);
    }

    private void SetTabPinned(BrowserTab tab, bool pinned)
    {
        if (tab.IsClosed || !tabs.Contains(tab)) return;
        if (tab.IsStartPage && pinned)
        {
            ShowTransientStatus("Start pages cannot be persistently pinned");
            return;
        }
        if (tab.IsPinned == pinned)
        {
            UpdateTabHeader(tab);
            return;
        }

        tab.IsPinned = pinned;
        ReorderTabForPinState(tab);
        foreach (var item in tabs) UpdateTabHeader(item);
        ResizeTabHeaders();
        if (!isPrivateMode) ScheduleStateSave();
        ShowTransientStatus(pinned ? "Tab pinned" : "Tab unpinned");
    }

    private void ReorderTabForPinState(BrowserTab tab)
    {
        var oldIndex = tabs.IndexOf(tab);
        if (oldIndex < 0) return;
        tabs.RemoveAt(oldIndex);
        var pinnedCount = tabs.Count(item => item.IsPinned);
        var newIndex = pinnedCount;
        tabs.Insert(Math.Clamp(newIndex, 0, tabs.Count), tab);
        if (tabStrip.Controls.Contains(tab.Header))
        {
            tabStrip.Controls.SetChildIndex(tab.Header, newIndex);
        }
    }

    private void ShowStartPageLinkContextMenu(StartPageLink link, Point screenLocation)
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = menuRenderer
        };
        menu.Items.Add("Open", null, (_, _) =>
            RunUiTask(() => NavigateActiveAsync(link.Url), "Navigation could not start"));
        var pinned = IsPinned(link.Url);
        var pinItem = new ToolStripMenuItem(pinned ? "Unpin quick link" : "Pin quick link")
        {
            Enabled = !isPrivateMode
        };
        pinItem.Click += (_, _) => SetPinnedStartPageLink(link, !pinned);
        menu.Items.Add(pinItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(
            isPrivateMode
                ? "Private quick links are not saved"
                : "Pins appear before favorites and recent history") { Enabled = false });
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(screenLocation);
    }

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
            query => CommandRankingEngine.Rank(BuildCommandCandidates(query), query),
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

        var showAllActions = !string.IsNullOrWhiteSpace(query);
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
            }
            yield return Action("Toggle reader mode", "Show a clean reading view for the current page", "reader-mode", "F9");
            yield return Action("Copy clean page link", "Copy the current page URL without tracking parameters", "copy-clean-link");
            yield return Action("Open permission manager", "Review saved website permissions", "permissions");
            yield return Action("Toggle resource mode", "Cycle memory saver and ultra-light modes", "resource-mode");
            yield return Action("Toggle shield", "Enable or disable the exact-host page shield", "shield");
            yield return Action("Keep active in background", "Protect the current site from lifecycle release", "keep-awake");
            yield return Action("Mute site", "Remember mute for the current exact host", "mute-site");
            yield return Action("Voice and audio settings", "Configure sound output and microphone input policies", "voice-audio-settings");
            yield return Action("Fullscreen", "Toggle browser fullscreen", "fullscreen", "F11");
            yield return Action("Toggle website theme", "Change the global website color mode", "theme");
        }

        var mruRanks = mruTabs.Order
            .Select((tab, index) => (tab, index))
            .ToDictionary(item => item.tab, item => item.index);
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

        if (!isPrivateMode)
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

            for (var index = 0; index < closedTabs.Count; index++)
            {
                var closed = closedTabs.ElementAt(index);
                yield return new CommandCandidate(
                    closed.Title,
                    FormatBrowserChromeUrlForDisplay(closed.Url),
                    CommandSource.RecentlyClosed,
                    closed.Url,
                    SourceIndex: index,
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
        var details = new List<string>(5);
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
            case "reader-mode":
                RunUiTask(ToggleReaderModeAsync, "Reader mode unavailable");
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
            case "mute-site":
                ToggleActiveSiteMute();
                break;
            case "voice-audio-settings":
                ShowVoiceAudioSettingsDialog();
                break;
            case "fullscreen":
                ToggleFullScreen();
                break;
            case "theme":
                ToggleDarkMode();
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

    private void BeginMruSwitch(int direction)
    {
        if (activeTab is null || tabs.Count < 2) return;
        if (mruSnapshot is null)
        {
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
            openingMruSwitcher = true;
            try
            {
                mruSwitcher.Open(this, labels);
            }
            finally
            {
                openingMruSwitcher = false;
            }
        }
        if (direction < 0) mruSwitcher?.MoveSelection(-1);
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

    private void ShowSessionManager()
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Sessions are unavailable in a private window");
            return;
        }

        sessionManager ??= CreateSessionManager();
        sessionManager.Open(this, state.NamedSessions);
    }

    private SessionManagerForm CreateSessionManager()
    {
        var manager = new SessionManagerForm();
        manager.ActionRequested += (_, request) => HandleSessionManagerRequest(request);
        return manager;
    }

    private void HandleSessionManagerRequest(SessionManagerRequest request)
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Sessions are unavailable in a private window");
            return;
        }

        switch (request.Action)
        {
            case SessionManagerAction.NewFromCurrent:
                SaveCurrentWindowAsSession();
                break;
            case SessionManagerAction.Open when request.Session is not null:
                OpenSavedSession(request.Session, replace: false);
                break;
            case SessionManagerAction.Replace when request.Session is not null:
                OpenSavedSession(request.Session, replace: true);
                break;
            case SessionManagerAction.Update when request.Session is not null:
                UpdateSavedSession(request.Session);
                break;
            case SessionManagerAction.Rename when request.Session is not null:
                RenameSavedSession(request.Session);
                break;
            case SessionManagerAction.Delete when request.Session is not null:
                DeleteSavedSession(request.Session);
                break;
        }
    }

    private void SaveCurrentWindowAsSession()
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Sessions are unavailable in a private window");
            return;
        }
        if (!TextPromptDialog.TryShow(this, "Save session", "Session name:", "", out var name)) return;
        SaveCurrentWindowAsSession(name, allowOverwrite: false);
    }

    private void SaveCurrentWindowAsSession(string name, bool allowOverwrite)
    {
        var normalizedName = BrowserStateStore.NormalizeSessionName(name);
        if (normalizedName is null)
        {
            ShowTransientStatus("Enter a nonblank session name up to 80 characters");
            return;
        }

        var session = CreateSessionFromCurrent(normalizedName);
        if (session is null)
        {
            ShowTransientStatus("There are no safe tabs to save");
            return;
        }

        var existingIndex = state.NamedSessions.FindIndex(item =>
            item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0 && !allowOverwrite)
        {
            var answer = MessageBox.Show(
                $"Replace the existing '{state.NamedSessions[existingIndex].Name}' session?",
                "Replace saved session?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }

        if (existingIndex >= 0) state.NamedSessions.RemoveAt(existingIndex);
        else if (state.NamedSessions.Count >= BrowserStateStore.MaximumNamedSessions)
        {
            ShowTransientStatus("You can save up to 20 named sessions");
            return;
        }
        state.NamedSessions.Insert(0, session);
        BrowserStateStore.NormalizeForPersistence(state);
        ScheduleStateSave();
        sessionManager?.SetSessions(state.NamedSessions);
        ShowTransientStatus($"Session saved: {session.Name}");
    }

    private SavedSessionEntry? CreateSessionFromCurrent(string name)
    {
        var captured = tabs
            .Where(tab => !tab.IsClosed && (tab.IsStartPage || BrowserPolicy.IsHttpUrl(tab.Url)))
            .Take(BrowserStateStore.MaximumSessionTabs)
            .Select(tab => new SavedSessionTab(
                tab.Url,
                tab.IsPinned && !tab.IsStartPage))
            .ToList();
        if (captured.Count == 0) return null;

        var activeIndex = activeTab is null
            ? null
            : tabs
                .Where(tab => !tab.IsClosed && (tab.IsStartPage || BrowserPolicy.IsHttpUrl(tab.Url)))
                .Take(BrowserStateStore.MaximumSessionTabs)
                .ToList()
                .FindIndex(tab => tab == activeTab) is var index && index >= 0
                    ? index
                    : (int?)null;
        return new SavedSessionEntry(name, DateTimeOffset.UtcNow, captured, activeIndex);
    }

    private void OpenSavedSession(SavedSessionEntry session, bool replace)
    {
        if (isPrivateMode) return;
        var normalized = state.NamedSessions.FirstOrDefault(item =>
            item.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        if (normalized is null) return;

        var capacity = replace ? MaximumTabs : MaximumTabs - tabs.Count;
        if (capacity <= 0)
        {
            ShowTransientStatus("There is no room for another session");
            return;
        }

        if (replace)
        {
            var activeDownloadCount = downloads.Count(item => !item.IsTerminal);
            if (activeDownloadCount > 0)
            {
                ShowTransientStatus("Finish or cancel downloads before replacing this window");
                ShowDownloadsPopup();
                return;
            }
            var answer = MessageBox.Show(
                $"Replace the current {tabs.Count} tabs with '{normalized.Name}'? Current tabs will not be added to Recently closed.",
                "Replace current window?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            replacingWindow = true;
            try
            {
                foreach (var tab in tabs.ToArray())
                {
                    CloseTab(tab, remember: false, ensureReplacement: false);
                }
            }
            finally
            {
                replacingWindow = false;
            }
        }

        RunUiTask(
            () => AppendSessionTabsAsync(normalized, capacity),
            "Could not open the saved session");
    }

    private async Task AppendSessionTabsAsync(SavedSessionEntry session, int capacity)
    {
        var selected = new List<BrowserTab>();
        var validTabs = session.Tabs
            .Where(tab => tab is not null && BrowserPolicy.IsSafeTopLevelUrl(tab.Url))
            .Take(capacity)
            .ToList();
        for (var index = 0; index < validTabs.Count; index++)
        {
            if (isClosing) return;
            var saved = validTabs[index];
            var tab = await OpenNewTabAsync(
                saved.Url,
                activate: false,
                isPinned: saved.IsPinned);
            if (tab is not null) selected.Add(tab);
        }

        if (selected.Count == 0)
        {
            await OpenNewTabAsync(StartPage.Url);
            ShowTransientStatus("The saved session had no valid tabs");
            return;
        }

        var target = session.ActiveTabIndex is int active
            && active >= 0
            && active < selected.Count
                ? selected[active]
                : selected[0];
        ActivateTab(target, focusPage: false);
        var skipped = Math.Max(0, session.Tabs.Count - selected.Count);
        ShowTransientStatus(skipped == 0
            ? $"Opened session: {session.Name}"
            : $"Opened session: {session.Name}; skipped {skipped} tab(s) at the limit");
    }

    private void UpdateSavedSession(SavedSessionEntry session)
    {
        var index = state.NamedSessions.FindIndex(item =>
            item.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var updated = CreateSessionFromCurrent(state.NamedSessions[index].Name);
        if (updated is null) return;
        state.NamedSessions[index] = updated;
        BrowserStateStore.NormalizeForPersistence(state);
        ScheduleStateSave();
        sessionManager?.SetSessions(state.NamedSessions);
        ShowTransientStatus($"Session updated: {updated.Name}");
    }

    private void RenameSavedSession(SavedSessionEntry session)
    {
        if (!TextPromptDialog.TryShow(this, "Rename session", "New name:", session.Name, out var name)) return;
        var normalizedName = BrowserStateStore.NormalizeSessionName(name);
        if (normalizedName is null)
        {
            ShowTransientStatus("Enter a nonblank session name up to 80 characters");
            return;
        }

        var sourceIndex = state.NamedSessions.FindIndex(item =>
            item.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0) return;
        var existingIndex = state.NamedSessions.FindIndex(item =>
            item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)
            && !item.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            var answer = MessageBox.Show(
                $"Replace the existing '{state.NamedSessions[existingIndex].Name}' session?",
                "Replace saved session?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            state.NamedSessions.RemoveAt(existingIndex);
            if (existingIndex < sourceIndex) sourceIndex--;
        }

        state.NamedSessions[sourceIndex] = state.NamedSessions[sourceIndex] with
        {
            Name = normalizedName,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        BrowserStateStore.NormalizeForPersistence(state);
        ScheduleStateSave();
        sessionManager?.SetSessions(state.NamedSessions);
    }

    private void DeleteSavedSession(SavedSessionEntry session)
    {
        var answer = MessageBox.Show(
            $"Delete the '{session.Name}' session?",
            "Delete saved session?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        state.NamedSessions.RemoveAll(item =>
            item.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        ScheduleStateSave();
        sessionManager?.SetSessions(state.NamedSessions);
        ShowTransientStatus("Session deleted");
    }

    private void SetPinnedStartPageLink(StartPageLink link, bool pin)
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Quick links are unavailable in a private window");
            return;
        }

        var existing = state.PinnedStartPageLinks.FindIndex(item => BrowserPolicy.UrlEquals(item.Url, link.Url));
        if (pin)
        {
            if (existing >= 0)
            {
                ShowTransientStatus("This page is already pinned");
                return;
            }
            if (state.PinnedStartPageLinks.Count >= BrowserStateStore.MaximumPinnedStartPageLinks)
            {
                ShowTransientStatus("You can pin up to three quick links");
                return;
            }
            state.PinnedStartPageLinks.Insert(0, new PinnedStartPageLink(link.Title, link.Url));
            ShowTransientStatus("Quick link pinned");
        }
        else if (existing >= 0)
        {
            state.PinnedStartPageLinks.RemoveAt(existing);
            ShowTransientStatus("Quick link unpinned");
        }
        else
        {
            return;
        }

        BrowserStateStore.NormalizeForPersistence(state);
        InvalidateStartPageLinks();
        RefreshNativeStartPages(refreshLinks: true);
        ScheduleStateSave();
    }

    private void ShowTabListMenu()
    {
        tabListMenu.Items.Clear();
        var searchBox = new ToolStripTextBox
        {
            AutoSize = false,
            Width = Math.Max(220, tabListButton.Width * 6),
            ToolTipText = "Filter tab titles and addresses",
            AccessibleName = "Filter open tabs"
        };
        var separator = new ToolStripSeparator();
        tabListMenu.Items.Add(searchBox);
        tabListMenu.Items.Add(separator);

        void Populate(string query)
        {
            while (tabListMenu.Items.Count > 2) tabListMenu.Items.RemoveAt(2);
            var matchingTabs = query.Length == 0
                ? tabs.ToArray()
                : CommandRankingEngine.Rank(
                        tabs.Select((tab, index) => new CommandCandidate(
                            tab.Title,
                            BuildTabDetail(tab),
                            CommandSource.OpenTab,
                            tab.IsStartPage ? null : tab.Url,
                            MruRank: mruTabs.Order.ToList().IndexOf(tab),
                            SourceIndex: index,
                            Target: tab)),
                        query,
                        MaximumTabs)
                    .Select(row => row.Candidate.Target)
                    .OfType<BrowserTab>()
                    .Where(tab => !tab.IsClosed)
                    .ToArray();
            if (matchingTabs.Length == 0)
            {
                tabListMenu.Items.Add(new ToolStripMenuItem(
                    query.Length == 0 ? "No open tabs" : "No tabs match this filter") { Enabled = false });
                return;
            }

            foreach (var tab in matchingTabs)
            {
                var item = new ToolStripMenuItem(TrimMenuText(tab.Title, 42))
                {
                    Checked = tab == activeTab,
                    ToolTipText = tab.IsStartPage ? (isPrivateMode ? "Incognito" : "New tab") : BuildTabDetail(tab),
                    AccessibleName = $"{tab.Title} tab"
                };
                item.Click += (_, _) => ActivateTab(tab);
                tabListMenu.Items.Add(item);
            }
        }

        searchBox.TextChanged += (_, _) => Populate(searchBox.Text.Trim());
        Populate(string.Empty);
        tabListMenu.Show(tabListButton, new Point(tabListButton.Width, tabListButton.Height), ToolStripDropDownDirection.BelowLeft);
        searchBox.Focus();
    }

    private void PopulateAppMenu()
    {
        extensionsMenuItem.Enabled = !isPrivateMode;
        extensionsMenuItem.ToolTipText = isPrivateMode
            ? "Extensions are unavailable in private windows"
            : "Install and manage browser extensions";
        extensionsMenuItem.DropDownItems.Clear();
        var manageItem = new ToolStripMenuItem("Manage extensions…")
        {
            ToolTipText = "Open the extensions manager"
        };
        manageItem.Click += (_, _) => RunUiTask(
            ShowExtensionsManagerAsync,
            "Could not open the extensions manager");
        extensionsMenuItem.DropDownItems.Add(manageItem);

        if (!isPrivateMode)
        {
            var managed = browserExtensions.GetManagedExtensions();
            if (managed.Count > 0)
            {
                extensionsMenuItem.DropDownItems.Add(new ToolStripSeparator());
                foreach (var ext in managed.Values.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    Image? icon = null;
                    if (ext.IconPath is not null)
                    {
                        var iconFullPath = Path.Combine(ext.FolderPath, ext.IconPath.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(iconFullPath))
                        {
                            try { icon = Image.FromFile(iconFullPath); } catch { }
                        }
                    }
                    var extItem = new ToolStripMenuItem(TrimMenuText(ext.Name, 42), icon);
                    var hasPopup = ext.PopupPath is not null || ext.LaunchPath is not null;
                    var hasOptions = ext.OptionsPath is not null;
                    if (hasPopup && hasOptions)
                    {
                        var popupSub = new ToolStripMenuItem("Open popup", null, (_, _) =>
                        {
                            var target = ext.PopupPath ?? ext.LaunchPath!;
                            RunUiTask(() => OpenNewTabAsync($"chrome-extension://{ext.Id}/{target}", trustedExtensionId: ext.Id), "Could not open extension popup");
                        });
                        var optionsSub = new ToolStripMenuItem("Options", null, (_, _) =>
                        {
                            RunUiTask(() => OpenNewTabAsync($"chrome-extension://{ext.Id}/{ext.OptionsPath}", trustedExtensionId: ext.Id), "Could not open extension options");
                        });
                        extItem.DropDownItems.Add(popupSub);
                        extItem.DropDownItems.Add(optionsSub);
                        extItem.Click += (_, _) =>
                        {
                            var target = ext.PopupPath ?? ext.LaunchPath!;
                            RunUiTask(() => OpenNewTabAsync($"chrome-extension://{ext.Id}/{target}", trustedExtensionId: ext.Id), "Could not open extension");
                        };
                    }
                    else if (hasPopup)
                    {
                        extItem.Click += (_, _) =>
                        {
                            var target = ext.PopupPath ?? ext.LaunchPath!;
                            RunUiTask(() => OpenNewTabAsync($"chrome-extension://{ext.Id}/{target}", trustedExtensionId: ext.Id), "Could not open extension");
                        };
                    }
                    else if (hasOptions)
                    {
                        extItem.Click += (_, _) =>
                        {
                            RunUiTask(() => OpenNewTabAsync($"chrome-extension://{ext.Id}/{ext.OptionsPath}", trustedExtensionId: ext.Id), "Could not open extension options");
                        };
                    }
                    extensionsMenuItem.DropDownItems.Add(extItem);
                }
            }
        }
        PopulateSearchProviderMenu();
        favoritesMenu.DropDownItems.Clear();
        if (state.Bookmarks.Count == 0)
        {
            favoritesMenu.DropDownItems.Add(new ToolStripMenuItem("No favorites yet") { Enabled = false });
        }
        else
        {
            foreach (var bookmark in state.Bookmarks.Take(20))
            {
                var item = new ToolStripMenuItem(TrimMenuText(bookmark.Title))
                {
                    ToolTipText = FormatBrowserChromeUrlForDisplay(bookmark.Url)
                };
                item.Click += (_, _) => RunUiTask(() => NavigateActiveAsync(bookmark.Url), "Navigation could not start");
                favoritesMenu.DropDownItems.Add(item);
            }
        }
        favoritesMenu.DropDownItems.Add(new ToolStripSeparator());
        favoritesMenu.DropDownItems.Add("Add or remove this page", null, (_, _) => ToggleFavorite());

        historyMenu.DropDownItems.Clear();
        if (state.History.Count == 0)
        {
            historyMenu.DropDownItems.Add(new ToolStripMenuItem("No browsing history") { Enabled = false });
        }
        else
        {
            foreach (var history in state.History.Take(20))
            {
                var item = new ToolStripMenuItem(TrimMenuText(history.Title))
                {
                    ToolTipText = FormatBrowserChromeUrlForDisplay(history.Url)
                };
                item.Click += (_, _) => RunUiTask(() => NavigateActiveAsync(history.Url), "Navigation could not start");
                historyMenu.DropDownItems.Add(item);
            }
            historyMenu.DropDownItems.Add(new ToolStripSeparator());
            historyMenu.DropDownItems.Add("Clear history list", null, (_, _) =>
            {
                state.History.Clear();
                InvalidateStartPageLinks();
                RefreshNativeStartPages(refreshLinks: true);
                ScheduleStateSave();
                ShowTransientStatus("History list cleared");
            });
        }

        downloadsMenu.DropDownItems.Clear();
        if (downloads.Count == 0)
        {
            downloadsMenu.DropDownItems.Add(new ToolStripMenuItem("No downloads this session") { Enabled = false });
        }
        else
        {
            var visibleDownloads = downloads
                .Where(download => !download.IsTerminal)
                .Concat(downloads.Where(download => download.IsTerminal).Take(12));
            foreach (var download in visibleDownloads)
            {
                var item = new ToolStripMenuItem($"{download.State} {FormatDownloadProgress(download.BytesReceived, download.TotalBytesToReceive, download.IsTerminal)}: {TrimMenuText(download.FileName, 36)}")
                {
                    ToolTipText = SanitizeDownloadDisplayPath(download.FilePath)
                };
                if (download.State.Equals("Complete", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(download.FilePath))
                {
                    item.DropDownItems.Add("Open file", null, (_, _) => OpenDownloadedFile(download));
                }
                item.DropDownItems.Add("Show in folder", null, (_, _) => ShowDownloadedFile(download));
                if (!download.IsTerminal && download.CancelAction is not null)
                {
                    item.DropDownItems.Add("Cancel", null, (_, _) => CancelDownload(download));
                }
                item.DropDownItems.Add("Copy source address", null, (_, _) => CopyDownloadAddress(download));
                if (download.IsTerminal)
                {
                    item.DropDownItems.Add(new ToolStripSeparator());
                    item.DropDownItems.Add("Remove from list", null, (_, _) =>
                    {
                        downloads.Remove(download);
                        ShowTransientStatus("Download removed from this session's list");
                    });
                }
                downloadsMenu.DropDownItems.Add(item);
            }
        }
        downloadsMenu.DropDownItems.Add(new ToolStripSeparator());
        downloadsMenu.DropDownItems.Add("Open Downloads folder", null, (_, _) => OpenDownloadsFolder());

        zoomMenu.DropDownItems.Clear();
        zoomMenu.DropDownItems.Add("Zoom out", null, (_, _) => ChangeZoom(-0.10));
        zoomMenu.DropDownItems.Add("Reset to 100%", null, (_, _) => ChangeZoom(0, true));
        zoomMenu.DropDownItems.Add("Zoom in", null, (_, _) => ChangeZoom(0.10));

        adBlockMenuItem.Checked = adBlockEnabled;
        adBlockMenuItem.Text = adBlockEnabled ? "Ad and tracker blocker: on" : "Ad and tracker blocker: off";
        var resourceMode = TabLifecyclePolicy.ResolveMode(memorySaverEnabled, ultraLightEnabled);
        resourceOffMenuItem.Checked = resourceMode == TabLifecycleMode.Off;
        memorySaverMenuItem.Checked = resourceMode == TabLifecycleMode.Standard;
        ultraLightMenuItem.Checked = resourceMode == TabLifecycleMode.Ultra;
        resourceModeMenu.Text = resourceMode switch
        {
            TabLifecycleMode.Ultra => "Resource mode: Ultra-light",
            TabLifecycleMode.Standard => "Resource mode: Memory saver",
            _ => "Resource mode: Off"
        };
        resourceOffMenuItem.Text = "Off";
        memorySaverMenuItem.Text = "Memory saver";
        ultraLightMenuItem.Text = "Ultra-light";
        reduceMotionMenuItem.Checked = reduceWebsiteMotionEnabled;
        reduceMotionMenuItem.Text = reduceWebsiteMotionEnabled
            ? "Reduce website motion: on"
            : "Reduce website motion: off";
        siteThemeMenuItem.Checked = darkModeEnabled;
        siteThemeMenuItem.Text = darkModeEnabled ? "Website theme: dark" : "Website theme: light";
        fullScreenMenuItem.Checked = isFullScreen;
        fullScreenMenuItem.Text = isFullScreen ? "Exit fullscreen" : "Fullscreen";
        fullScreenMenuItem.ShortcutKeyDisplayString = "F11";

        favoritesMenu.Enabled = true;
        historyMenu.Enabled = true;
        downloadsMenu.Enabled = true;
    }

    private void PopulateSearchProviderMenu()
    {
        searchProviderMenu.DropDownItems.Clear();
        foreach (var provider in BrowserPolicy.AvailableSearchProviders)
        {
            var item = new ToolStripMenuItem(provider.DisplayName)
            {
                Checked = provider.Id.Equals(searchProviderId, StringComparison.OrdinalIgnoreCase),
                ToolTipText = $"Use {provider.DisplayName} for searches"
            };
            item.Click += (_, _) => SetSearchProvider(provider.Id);
            searchProviderMenu.DropDownItems.Add(item);
        }
        searchProviderMenu.Text = $"Search engine: {BrowserPolicy.GetSearchProviderName(searchProviderId)}";
    }

    private void ShowSearchProviderMenu(Control anchor)
    {
        PopulateSearchProviderMenu();
        searchProviderMenu.DropDown.Show(
            anchor,
            new Point(0, anchor.Height + 4),
            ToolStripDropDownDirection.BelowRight);
    }

    private void SetSearchProvider(string providerId)
    {
        searchProviderId = BrowserPolicy.NormalizeSearchProviderId(providerId);
        state.SearchProviderId = searchProviderId;
        addressBar.PlaceholderText = $"Search with {BrowserPolicy.GetSearchProviderName(searchProviderId)} or enter address{(isPrivateMode ? " in Incognito" : string.Empty)}";
        foreach (var tab in tabs.Where(item => item.StartPageView is not null && !item.IsClosed))
        {
            tab.StartPageView!.SetSearchProvider(searchProviderId);
        }
        if (addressSuggestionPopup.Visible)
        {
            var query = smartSearchBarEditing && activeSmartSearchBar is not null
                ? activeSmartSearchBar.Text
                : addressBar.Text;
            addressSuggestionPopup.SetSuggestions(AddressSuggestionEngine.GetSuggestions(query, state));
            PositionAddressSuggestions();
        }
        if (!isPrivateMode) ScheduleStateSave();
        ShowTransientStatus($"Search engine: {BrowserPolicy.GetSearchProviderName(searchProviderId)}");
    }

    private void ShowSavedItemsDialog()
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Saved items are unavailable in a private window");
            return;
        }

        using var dialog = new SavedItemsDialog(BuildSavedItemRows);
        dialog.ActionRequested += (_, action) =>
        {
            HandleSavedItemAction(action);
            dialog.RefreshItems();
        };
        dialog.ShowDialog(this);
    }

    private IReadOnlyList<SavedItemRow> BuildSavedItemRows(SavedItemsView view)
    {
        if (isPrivateMode) return Array.Empty<SavedItemRow>();
        return view switch
        {
            SavedItemsView.Favorites => state.Bookmarks
                .Select(item => new SavedItemRow(
                    string.IsNullOrWhiteSpace(item.Title) ? HostFromUrl(item.Url) : item.Title,
                    item.Url,
                    IsPinned(item.Url),
                    "Favorite"))
                .ToArray(),
            SavedItemsView.History => state.History
                .Select(item => new SavedItemRow(
                    string.IsNullOrWhiteSpace(item.Title) ? HostFromUrl(item.Url) : item.Title,
                    item.Url,
                    IsPinned(item.Url),
                    $"Visited {item.VisitedUtc.ToLocalTime():g}"))
                .ToArray(),
            SavedItemsView.QuickLinks => state.PinnedStartPageLinks
                .Select(item => new SavedItemRow(
                    string.IsNullOrWhiteSpace(item.Title) ? HostFromUrl(item.Url) : item.Title,
                    item.Url,
                    true,
                    "Quick link"))
                .ToArray(),
            SavedItemsView.RecentlyClosed => closedTabs
                .Select(item => new SavedItemRow(
                    string.IsNullOrWhiteSpace(item.Title) ? HostFromUrl(item.Url) : item.Title,
                    item.Url,
                    false,
                    $"Closed {item.ClosedUtc.ToLocalTime():g}",
                    item))
                .ToArray(),
            _ => Array.Empty<SavedItemRow>()
        };
    }

    private bool IsPinned(string url)
    {
        return state.PinnedStartPageLinks.Any(item => BrowserPolicy.UrlEquals(item.Url, url));
    }

    internal static bool RemoveSavedItem(BrowserState state, SavedItemsView view, string url)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return view switch
        {
            SavedItemsView.Favorites =>
                state.Bookmarks.RemoveAll(item => BrowserPolicy.UrlEquals(item.Url, url)) > 0,
            SavedItemsView.History =>
                state.History.RemoveAll(item => BrowserPolicy.UrlEquals(item.Url, url)) > 0,
            SavedItemsView.QuickLinks =>
                state.PinnedStartPageLinks.RemoveAll(item => BrowserPolicy.UrlEquals(item.Url, url)) > 0,
            _ => false
        };
    }

    internal static bool RenameSavedItem(
        BrowserState state,
        SavedItemsView view,
        string url,
        string title)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        switch (view)
        {
            case SavedItemsView.Favorites:
                for (var index = 0; index < state.Bookmarks.Count; index++)
                {
                    if (!BrowserPolicy.UrlEquals(state.Bookmarks[index].Url, url)) continue;
                    state.Bookmarks[index] = state.Bookmarks[index] with { Title = title };
                    return true;
                }
                break;
            case SavedItemsView.History:
                for (var index = 0; index < state.History.Count; index++)
                {
                    if (!BrowserPolicy.UrlEquals(state.History[index].Url, url)) continue;
                    state.History[index] = state.History[index] with { Title = title };
                    return true;
                }
                break;
            case SavedItemsView.QuickLinks:
                for (var index = 0; index < state.PinnedStartPageLinks.Count; index++)
                {
                    if (!BrowserPolicy.UrlEquals(state.PinnedStartPageLinks[index].Url, url)) continue;
                    state.PinnedStartPageLinks[index] = state.PinnedStartPageLinks[index] with { Title = title };
                    return true;
                }
                break;
        }

        return false;
    }

    private void HandleSavedItemAction(SavedItemAction action)
    {
        var url = action.Item.Url;
        switch (action.Kind)
        {
            case SavedItemActionKind.Open:
                if (action.View == SavedItemsView.RecentlyClosed
                    && action.Item.Token is ClosedTabEntry closedEntry)
                {
                    RestoreClosedEntry(closedEntry);
                    return;
                }
                RunUiTask(() => NavigateActiveAsync(url), "Navigation could not start");
                return;
            case SavedItemActionKind.Remove:
                if (action.View == SavedItemsView.RecentlyClosed)
                {
                    if (action.Item.Token is ClosedTabEntry closed)
                    {
                        closedTabs.Remove(closed);
                    }
                    else
                    {
                        var matching = closedTabs.FirstOrDefault(item =>
                            BrowserPolicy.UrlEquals(item.Url, url));
                        if (matching is not null) closedTabs.Remove(matching);
                    }
                    if (!isPrivateMode) ScheduleStateSave();
                    ShowTransientStatus("Recently closed item removed");
                    return;
                }
                RemoveSavedItem(state, action.View, url);
                ShowTransientStatus(action.View switch
                {
                    SavedItemsView.Favorites => "Favorite removed",
                    SavedItemsView.History => "History item removed",
                    SavedItemsView.QuickLinks => "Quick link removed",
                    _ => "Saved item removed"
                });
                break;
            case SavedItemActionKind.Rename:
                var title = BrowserStateStore.SanitizeTitle(action.NewTitle);
                if (title.Length == 0)
                {
                    ShowTransientStatus("Enter a title before renaming");
                    return;
                }
                RenameSavedItem(state, action.View, url, title);
                ShowTransientStatus("Saved item renamed");
                break;
            case SavedItemActionKind.TogglePin:
                var pinnedIndex = state.PinnedStartPageLinks.FindIndex(item => BrowserPolicy.UrlEquals(item.Url, url));
                if (pinnedIndex >= 0)
                {
                    state.PinnedStartPageLinks.RemoveAt(pinnedIndex);
                    ShowTransientStatus("Quick link unpinned");
                }
                else
                {
                    if (state.PinnedStartPageLinks.Count >= BrowserStateStore.MaximumPinnedStartPageLinks)
                    {
                        ShowTransientStatus("You can pin up to three quick links");
                        return;
                    }
                    state.PinnedStartPageLinks.Insert(0, new PinnedStartPageLink(action.Item.Title, url));
                    ShowTransientStatus("Quick link pinned");
                }
                break;
            case SavedItemActionKind.MoveUp:
            case SavedItemActionKind.MoveDown:
                var currentIndex = state.PinnedStartPageLinks.FindIndex(item => BrowserPolicy.UrlEquals(item.Url, url));
                var nextIndex = action.Kind == SavedItemActionKind.MoveUp ? currentIndex - 1 : currentIndex + 1;
                if (currentIndex >= 0 && nextIndex >= 0 && nextIndex < state.PinnedStartPageLinks.Count)
                {
                    (state.PinnedStartPageLinks[currentIndex], state.PinnedStartPageLinks[nextIndex]) =
                        (state.PinnedStartPageLinks[nextIndex], state.PinnedStartPageLinks[currentIndex]);
                    ShowTransientStatus("Quick link order updated");
                }
                break;
        }

        BrowserStateStore.NormalizeForPersistence(state);
        InvalidateStartPageLinks();
        RefreshNativeStartPages(refreshLinks: true);
        if (!isPrivateMode) ScheduleStateSave();
    }

    private async Task ShowExtensionsManagerAsync()
    {
        if (isPrivateMode)
        {
            ShowTransientStatus("Extensions are unavailable in private windows");
            return;
        }
        if (extensionsManager is { IsDisposed: false })
        {
            extensionsManager.PrefillStoreAddress(GetCurrentChromeStoreAddress());
            if (!extensionsManager.Visible) extensionsManager.Show(this);
            extensionsManager.Activate();
            return;
        }
        if (openingExtensionsManager)
        {
            ShowTransientStatus("Opening extensions…");
            return;
        }

        openingExtensionsManager = true;
        CoreWebView2Controller? controller = null;
        ExtensionsManagerForm? manager = null;
        try
        {
            var browserEnvironment = await GetOrCreateEnvironmentAsync();
            if (isClosing || environment != browserEnvironment) return;
            var controllerOptions = browserEnvironment.CreateCoreWebView2ControllerOptions();
            controllerOptions.IsInPrivateModeEnabled = false;
            controller = await browserEnvironment.CreateCoreWebView2ControllerAsync(Handle, controllerOptions);
            if (isClosing || environment != browserEnvironment)
            {
                controller.Close();
                controller = null;
                return;
            }
            controller.IsVisible = false;
            controller.Bounds = Rectangle.Empty;
            extensionProfileController = controller;

            manager = new ExtensionsManagerForm(
                BuildExtensionManagerRowsAsync,
                InstallChromeStoreExtensionAsync,
                InstallExtensionPackageAsync,
                InstallUnpackedExtensionAsync,
                UpdateChromeStoreExtensionAsync,
                HandleExtensionManagerActionAsync,
                OpenManagedExtensionsFolder,
                OpenExtensionsFolder);
            extensionsManager = manager;
            manager.PrefillStoreAddress(GetCurrentChromeStoreAddress());
            manager.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(extensionsManager, manager)) extensionsManager = null;
                DisposeExtensionProfileController(controller);
                ReleaseBrowserEnvironmentIfIdle();
            };
            manager.Show(this);
        }
        catch
        {
            if (ReferenceEquals(extensionsManager, manager)) extensionsManager = null;
            try { manager?.Dispose(); }
            catch { }
            if (controller is not null) DisposeExtensionProfileController(controller);
            throw;
        }
        finally
        {
            openingExtensionsManager = false;
        }
    }

    private void QueueManagedExtensionReconciliation(CoreWebView2Profile profile)
    {
        if (isPrivateMode || managedExtensionReconciliationStarted || isClosing) return;
        managedExtensionReconciliationStarted = true;
        _ = ReconcileManagedExtensionsAfterStartupAsync(profile);
    }

    private async Task ReconcileManagedExtensionsAfterStartupAsync(CoreWebView2Profile profile)
    {
        try
        {
            var installed = await profile.GetBrowserExtensionsAsync();
            if (isClosing) return;
            var installedIds = installed.Select(extension => extension.Id).ToArray();
            await Task.Run(() => browserExtensions.ReconcileManagedExtensions(installedIds));
        }
        catch (Exception error)
        {
            if (isClosing) return;
            managedExtensionReconciliationStarted = false;
            stateStore.Log("Could not reconcile managed extension packages", error);
        }
    }

    private async Task<IReadOnlyList<ExtensionManagerRow>> BuildExtensionManagerRowsAsync()
    {
        var profile = GetExtensionManagerProfile();
        var installed = await profile.GetBrowserExtensionsAsync();
        var installedIds = installed.Select(extension => extension.Id).ToArray();
        var managedExtensions = await Task.Run(() =>
        {
            browserExtensions.ReconcileManagedExtensions(installedIds);
            return browserExtensions.GetManagedExtensions();
        });
        return installed
            .Select(extension =>
            {
                managedExtensions.TryGetValue(extension.Id, out var managed);
                var fullIconPath = managed?.FolderPath is not null && managed.IconPath is not null
                    ? Path.Combine(managed.FolderPath, managed.IconPath.Replace('/', Path.DirectorySeparatorChar))
                    : null;
                return new ExtensionManagerRow(
                    extension.Id,
                    SanitizeExtensionDisplayName(extension.Name),
                    SanitizeExtensionPromptText(managed?.Version ?? "Unknown", 80),
                    extension.IsEnabled,
                    managed?.LaunchPath,
                    managed?.StoreId is not null
                        && managed.StoreId.Equals(extension.Id, StringComparison.OrdinalIgnoreCase),
                    extension,
                    managed?.PopupPath,
                    managed?.OptionsPath,
                    managed?.Description,
                    fullIconPath,
                    managed?.StoreId,
                    managed?.FolderPath,
                    managed?.RequestedCapabilities);
            })
            .ToArray();
    }

    private Task InstallChromeStoreExtensionAsync(string value, CancellationToken cancellationToken) =>
        InstallPreparedExtensionAsync(
            GetExtensionManagerProfile(),
            (IWin32Window?)extensionsManager ?? this,
            cancellationToken,
            () => browserExtensions.DownloadFromChromeWebStoreAsync(
                value,
                environment?.BrowserVersionString ?? string.Empty,
                cancellationToken));

    private Task InstallExtensionPackageAsync(string path, CancellationToken cancellationToken) =>
        InstallPreparedExtensionAsync(
            GetExtensionManagerProfile(),
            (IWin32Window?)extensionsManager ?? this,
            cancellationToken,
            () => browserExtensions.ImportPackageAsync(path, cancellationToken));

    private Task InstallUnpackedExtensionAsync(string path, CancellationToken cancellationToken) =>
        InstallPreparedExtensionAsync(
            GetExtensionManagerProfile(),
            (IWin32Window?)extensionsManager ?? this,
            cancellationToken,
            () => browserExtensions.ImportUnpackedAsync(path, cancellationToken));

    private async Task<string> UpdateChromeStoreExtensionAsync(
        ExtensionManagerRow row,
        CancellationToken cancellationToken)
    {
        if (isPrivateMode)
        {
            throw new InvalidOperationException("Extensions are unavailable in private windows.");
        }
        if (row.Token is not CoreWebView2BrowserExtension installedExtension
            || !installedExtension.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected extension is no longer available.");
        }
        var eligibilityError = GetExtensionUpdateEligibilityError(
            isPrivateMode: false,
            isManagedStoreExtension: row.CanUpdate,
            isEnabled: installedExtension.IsEnabled);
        if (eligibilityError is not null)
        {
            throw new InvalidOperationException(eligibilityError);
        }

        using var extensionMutation = BeginExtensionMutation();
        var profile = GetExtensionManagerProfile();
        var update = await browserExtensions.CheckForChromeWebStoreUpdateAsync(
            row.Id,
            environment?.BrowserVersionString ?? string.Empty,
            cancellationToken);
        if (update is null)
        {
            return $"{SanitizeExtensionDisplayName(row.Name)} is already up to date";
        }

        var owner = (IWin32Window?)extensionsManager ?? this;
        var prepared = update.Prepared;
        CoreWebView2BrowserExtension? replacement = null;
        var committed = false;
        var restored = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ConfirmPreparedExtensionUpdate(update, owner))
            {
                throw new OperationCanceledException("Extension update canceled.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            // WebView2 keeps only the later package when the same extension ID
            // is added. The old managed folder remains untouched until the new
            // package is live, identity-checked, and recorded.
            await Task.Run(() => browserExtensions.MarkInstallationStarted(prepared));
            replacement = await profile.AddBrowserExtensionAsync(prepared.FolderPath);
            if (!replacement.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase))
            {
                try { await replacement.RemoveAsync(); }
                catch (Exception cleanupError)
                {
                    stateStore.Log("Could not remove a mismatched extension update", cleanupError);
                }
                replacement = null;
                throw new InvalidDataException("The installed update ID did not match the selected extension.");
            }
            await Task.Run(() => browserExtensions.RememberInstalled(row.Id, prepared));
            committed = true;
            return $"Updated {SanitizeExtensionDisplayName(prepared.Name)} to {SanitizeExtensionPromptText(prepared.Version, 80)}";
        }
        catch (Exception error)
        {
            if (replacement is not null && !committed)
            {
                try
                {
                    var previous = await profile.AddBrowserExtensionAsync(update.Current.FolderPath);
                    if (!previous.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The restored extension ID did not match.");
                    }
                    restored = true;
                }
                catch (Exception rollbackError)
                {
                    stateStore.Log("Could not roll back the extension update", rollbackError);
                }
            }

            if (!committed && (replacement is null || restored))
            {
                await Task.Run(() => browserExtensions.DiscardPrepared(prepared));
            }
            else if (!committed)
            {
                stateStore.Log(
                    "Preserved an uncommitted extension update package after rollback failed",
                    error);
            }
            throw CreateFriendlyExtensionError(error);
        }
    }

    internal static string? GetExtensionUpdateEligibilityError(
        bool isPrivateMode,
        bool isManagedStoreExtension,
        bool isEnabled)
    {
        if (isPrivateMode) return "Extensions are unavailable in private windows.";
        if (!isManagedStoreExtension)
        {
            return "Only MishaWeb-managed Chrome Web Store extensions can be updated.";
        }
        return isEnabled
            ? null
            : "Enable this extension before updating it. WebView2 activates a replacement as soon as it is installed.";
    }

    private async Task InstallPreparedExtensionAsync(
        CoreWebView2Profile profile,
        IWin32Window confirmationOwner,
        CancellationToken cancellationToken,
        Func<Task<PreparedBrowserExtension>> prepare)
    {
        using var extensionMutation = BeginExtensionMutation();
        PreparedBrowserExtension? prepared = null;
        CoreWebView2BrowserExtension installed;
        try
        {
            prepared = await prepare();
            cancellationToken.ThrowIfCancellationRequested();
            if (!ConfirmPreparedExtensionInstall(prepared, confirmationOwner))
            {
                throw new OperationCanceledException("Extension installation canceled.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => browserExtensions.MarkInstallationStarted(prepared));
            installed = await profile.AddBrowserExtensionAsync(prepared.FolderPath);
        }
        catch (Exception error)
        {
            if (prepared is not null)
            {
                await Task.Run(() => browserExtensions.DiscardPrepared(prepared));
            }
            throw CreateFriendlyExtensionError(error);
        }

        try
        {
            // WebView2 extension objects are apartment-threaded. Read the COM
            // property on the UI thread before moving file work off-thread.
            var installedId = installed.Id;
            await Task.Run(() => browserExtensions.RememberInstalled(installedId, prepared));
        }
        catch (Exception error)
        {
            stateStore.Log("Could not record the installed extension package", error);
            var removed = false;
            try
            {
                await installed.RemoveAsync();
                removed = true;
            }
            catch (Exception cleanupError)
            {
                stateStore.Log("Could not roll back an unrecorded extension", cleanupError);
            }
            if (removed)
            {
                await Task.Run(() => browserExtensions.DiscardPrepared(prepared));
            }
            else
            {
                stateStore.Log(
                    "Preserved an unrecorded extension package because WebView2 may still use it",
                    error);
            }
            throw CreateFriendlyExtensionError(
                new IOException(
                    removed
                        ? "The extension could not be saved and was rolled back."
                        : "The extension could not be saved. Its package was retained so the browser can recover it safely.",
                    error));
        }
    }

    private bool ConfirmPreparedExtensionUpdate(
        PreparedBrowserExtensionUpdate update,
        IWin32Window owner)
    {
        const int maximumVisibleCapabilities = 18;
        var prepared = update.Prepared;
        var safeName = SanitizeExtensionPromptText(prepared.Name, 120);
        var safeCurrentVersion = SanitizeExtensionPromptText(update.Current.Version, 80);
        var safeNewVersion = SanitizeExtensionPromptText(prepared.Version, 80);
        if (safeName.Length == 0) safeName = "Unnamed extension";
        if (safeCurrentVersion.Length == 0) safeCurrentVersion = "unknown";
        if (safeNewVersion.Length == 0) safeNewVersion = "unknown";

        var requested = SelectExtensionCapabilitiesForPrompt(
            prepared.RequestedCapabilities,
            maximumVisibleCapabilities);
        var requestedText = requested.Count == 0
            ? "No permissions or site access are declared in its manifest."
            : string.Join(Environment.NewLine, requested.Select(value => "• " + value));
        var added = SelectExtensionCapabilitiesForPrompt(
            update.AddedCapabilities,
            maximumVisibleCapabilities);
        var addedText = added.Count == 0
            ? "No newly declared access."
            : string.Join(Environment.NewLine, added.Select(value => "• " + value));
        var message = $"Update {safeName} from {safeCurrentVersion} to {safeNewVersion}?{Environment.NewLine}"
            + $"Chrome Web Store ID: {prepared.StoreId}{Environment.NewLine}{Environment.NewLine}"
            + $"Newly declared access:{Environment.NewLine}{addedText}{Environment.NewLine}{Environment.NewLine}"
            + $"All access requested by the update:{Environment.NewLine}{requestedText}"
            + $"{Environment.NewLine}{Environment.NewLine}The update activates only if you approve it.";
        return MessageBox.Show(
            owner,
            message,
            "Update extension",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private bool ConfirmPreparedExtensionInstall(
        PreparedBrowserExtension prepared,
        IWin32Window owner)
    {
        const int maximumVisibleCapabilities = 18;
        var safeName = SanitizeExtensionPromptText(prepared.Name, 120);
        var safeVersion = SanitizeExtensionPromptText(prepared.Version, 80);
        if (safeName.Length == 0) safeName = "Unnamed extension";
        if (safeVersion.Length == 0) safeVersion = "unknown version";
        var visibleCapabilities = SelectExtensionCapabilitiesForPrompt(
            prepared.RequestedCapabilities,
            maximumVisibleCapabilities);
        var access = visibleCapabilities.Count == 0
            ? "No permissions or site access are declared in its manifest."
            : string.Join(Environment.NewLine, visibleCapabilities.Select(value => "• " + value));
        var source = prepared.StoreId is null
            ? "Local package"
            : $"Chrome Web Store ID: {prepared.StoreId}";
        var message = $"Install {safeName} {safeVersion}?{Environment.NewLine}"
            + $"{source}{Environment.NewLine}{Environment.NewLine}Requested access:{Environment.NewLine}{access}"
            + $"{Environment.NewLine}{Environment.NewLine}Only install extensions you trust.";
        return MessageBox.Show(
            owner,
            message,
            "Install extension",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    internal static List<string> SelectExtensionCapabilitiesForPrompt(
        IReadOnlyList<string> capabilities,
        int maximumOrdinaryCapabilities)
    {
        const int maximumScannedCapabilities = 4_096;
        const int maximumVisibleHighRiskCapabilities = 12;
        var sanitized = capabilities
            .Take(maximumScannedCapabilities)
            .Select(value => SanitizeExtensionPromptText(value, 180))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => (Value: value, Risk: GetExtensionCapabilityRisk(value)))
            .ToArray();
        var highRisk = sanitized
            .Where(item => item.Risk > 0)
            .OrderByDescending(item => item.Risk)
            .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ordinary = sanitized
            .Where(item => item.Risk == 0)
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var visible = highRisk
            .Take(maximumVisibleHighRiskCapabilities)
            .Select(item => DescribeExtensionCapability(item.Value, item.Risk))
            .ToList();
        if (highRisk.Length > maximumVisibleHighRiskCapabilities)
        {
            visible.Add(
                $"HIGH RISK — {highRisk.Length - maximumVisibleHighRiskCapabilities} additional sensitive access declaration(s) are not expanded");
        }
        var ordinaryLimit = Math.Max(0, maximumOrdinaryCapabilities);
        visible.AddRange(ordinary.Take(ordinaryLimit).Select(item => item.Value));
        if (ordinary.Length > ordinaryLimit)
        {
            visible.Add($"…and {ordinary.Length - ordinaryLimit} additional lower-risk declaration(s)");
        }
        if (capabilities.Count > maximumScannedCapabilities)
        {
            visible.Add(
                $"HIGH RISK — {capabilities.Count - maximumScannedCapabilities} additional manifest declaration(s) require manual review");
        }
        return visible;
    }

    private static int GetExtensionCapabilityRisk(string capability)
    {
        var value = capability.ToLowerInvariant();
        var permission = GetDeclaredExtensionPermission(value);
        if (value.Contains("<all_urls>", StringComparison.Ordinal)
            || value.Contains("*://*/*", StringComparison.Ordinal)
            || permission is "debugger"
                or "desktopcapture"
                or "nativemessaging"
                or "pagecapture"
                or "proxy"
                or "tabcapture"
                or "webauthenticationproxy")
        {
            return 3;
        }
        if (permission is "accessibilityfeatures.modify"
                or "accessibilityfeatures.read"
                or "bookmarks"
                or "browsingdata"
                or "clipboardread"
                or "clipboardwrite"
                or "contentsettings"
                or "cookies"
                or "declarativenetrequest"
                or "declarativenetrequestfeedback"
                or "declarativenetrequestwithhostaccess"
                or "favicon"
                or "geolocation"
                or "history"
                or "identity.email"
                or "management"
                or "notifications"
                or "privacy"
                or "readinglist"
                or "system.storage"
                or "tabs"
                or "tabgroups"
                or "topsites"
                or "ttsengine"
                or "webnavigation"
            || permission?.StartsWith("downloads", StringComparison.Ordinal) == true
            || permission?.StartsWith("webrequest", StringComparison.Ordinal) == true
            || value.StartsWith("site access:", StringComparison.Ordinal)
            || value.StartsWith("runs on:", StringComparison.Ordinal)
            || value.StartsWith("accepts connections from:", StringComparison.Ordinal))
        {
            return 2;
        }
        return 0;
    }

    private static string DescribeExtensionCapability(string capability, int risk)
    {
        if (risk < 2) return capability;
        var lower = capability.ToLowerInvariant();
        var permission = GetDeclaredExtensionPermission(lower);
        var consequence = lower.Contains("<all_urls>", StringComparison.Ordinal)
            || lower.Contains("*://*/*", StringComparison.Ordinal)
                ? "can read and change data on all websites"
            : permission == "nativemessaging"
                ? "can communicate with applications on this computer"
            : permission == "debugger"
                ? "can inspect and control browser tabs"
            : permission == "proxy"
                ? "can route and change browser network traffic"
            : permission is "desktopcapture" or "pagecapture" or "tabcapture"
                ? "can capture screen or webpage contents"
            : permission == "webauthenticationproxy"
                ? "can intercept browser authentication requests"
            : permission == "management"
                ? "can manage installed browser extensions"
            : permission is "privacy" or "contentsettings" or "accessibilityfeatures.modify"
                ? "can change sensitive browser settings"
            : permission == "accessibilityfeatures.read"
                ? "can read accessibility settings"
            : permission is "tabs" or "history" or "webnavigation" or "topsites"
                ? "can read browsing activity"
            : permission == "favicon"
                ? "can read icons of websites you visit"
            : permission is "bookmarks" or "readinglist"
                ? "can read or change saved browsing items"
            : permission is "clipboardread" or "clipboardwrite"
                ? "can read or change clipboard contents"
            : permission is "geolocation" or "identity.email"
                ? "can access identity or physical-location data"
            : permission == "browsingdata"
                ? "can remove browser data"
            : permission == "notifications"
                ? "can display browser notifications"
            : permission == "tabgroups"
                ? "can view and manage tab groups"
            : permission == "ttsengine"
                ? "can read text spoken by synthesized speech"
            : permission == "system.storage"
                ? "can identify and eject storage devices"
            : permission?.StartsWith("downloads", StringComparison.Ordinal) == true
                ? "can manage downloaded files"
            : permission?.StartsWith("webrequest", StringComparison.Ordinal) == true
                || permission?.StartsWith("declarativenetrequest", StringComparison.Ordinal) == true
                ? "can observe, block, or change network requests"
            : lower.StartsWith("site access:", StringComparison.Ordinal)
                || lower.StartsWith("runs on:", StringComparison.Ordinal)
                ? "can read and change data on the named website"
            : lower.StartsWith("accepts connections from:", StringComparison.Ordinal)
                ? "can receive connections from named websites or extensions"
            : "can access sensitive browser or website data";
        return $"HIGH RISK — {consequence}: {capability}";
    }

    private static string? GetDeclaredExtensionPermission(string capability)
    {
        var separator = capability.IndexOf(':');
        if (separator <= 0) return null;
        var label = capability[..separator].Trim();
        if (label is not ("permission" or "optional permission")) return null;
        var permission = capability[(separator + 1)..].Trim();
        return permission.Length == 0 ? null : permission;
    }

    internal static string SanitizeExtensionPromptText(string value, int maximumLength)
        => TextSafety.SanitizeSingleLine(value, maximumLength);

    internal static string SanitizeDownloadDisplayName(string? fileName)
    {
        var sanitized = SanitizeExtensionPromptText(
            fileName ?? string.Empty,
            MaximumDownloadDisplayNameCharacters);
        return sanitized.Length == 0 ? "download" : sanitized;
    }

    internal static string SanitizeDownloadDisplayPath(string? filePath) =>
        SanitizeExtensionPromptText(
            filePath ?? string.Empty,
            MaximumDownloadDisplayPathCharacters);

    internal static string SanitizeExtensionDisplayName(string? name)
    {
        var sanitized = SanitizeExtensionPromptText(
            name ?? string.Empty,
            MaximumExtensionDisplayNameCharacters);
        return sanitized.Length == 0 ? "Unnamed extension" : sanitized;
    }

    private async Task HandleExtensionManagerActionAsync(
        ExtensionManagerAction action,
        ExtensionManagerRow row)
    {
        if (row.Token is not CoreWebView2BrowserExtension extension)
        {
            throw new InvalidOperationException("The selected extension is no longer available.");
        }

        using var extensionMutation = (action == ExtensionManagerAction.Open
            || action == ExtensionManagerAction.OpenPopup
            || action == ExtensionManagerAction.OpenOptions)
            ? null
            : BeginExtensionMutation();
        try
        {
            switch (action)
            {
                case ExtensionManagerAction.Open:
                case ExtensionManagerAction.OpenPopup:
                    var popupTarget = (action == ExtensionManagerAction.OpenPopup ? row.PopupPath : null)
                        ?? row.LaunchPath
                        ?? row.OptionsPath;
                    if (popupTarget is null) return;
                    var popupUrl = $"chrome-extension://{row.Id}/{popupTarget}";
                    if (await OpenNewTabAsync(popupUrl, trustedExtensionId: row.Id) is null)
                    {
                        throw new InvalidOperationException("The extension page could not be opened.");
                    }
                    break;
                case ExtensionManagerAction.OpenOptions:
                    var optionsTarget = row.OptionsPath ?? row.LaunchPath;
                    if (optionsTarget is null) return;
                    var optionsUrl = $"chrome-extension://{row.Id}/{optionsTarget}";
                    if (await OpenNewTabAsync(optionsUrl, trustedExtensionId: row.Id) is null)
                    {
                        throw new InvalidOperationException("The extension options page could not be opened.");
                    }
                    break;
                case ExtensionManagerAction.ToggleEnabled:
                    await extension.EnableAsync(!row.IsEnabled);
                    break;
                case ExtensionManagerAction.Remove:
                    await extension.RemoveAsync();
                    await Task.Run(() => browserExtensions.ForgetInstalled(row.Id));
                    break;
            }
        }
        catch (Exception error)
        {
            throw CreateFriendlyExtensionError(error);
        }
    }

    private IDisposable BeginExtensionMutation()
    {
        Interlocked.Increment(ref activeExtensionMutationCount);
        return new ExtensionMutationScope(this);
    }

    private void EndExtensionMutation()
    {
        var remaining = Interlocked.Decrement(ref activeExtensionMutationCount);
        if (remaining == 0)
        {
            QueueDeferredCloseAfterExtensionMutation();
            return;
        }
        if (remaining > 0) return;
        Interlocked.Exchange(ref activeExtensionMutationCount, 0);
        stateStore.Log(
            "Extension operation accounting was unbalanced",
            new InvalidOperationException("The active extension operation count became negative."));
    }

    private void QueueDeferredCloseAfterExtensionMutation()
    {
        if (!closeWhenExtensionOperationsFinish
            || isClosing
            || IsDisposed
            || !IsHandleCreated)
        {
            return;
        }
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (closeWhenExtensionOperationsFinish
                    && !isClosing
                    && !IsDisposed
                    && Volatile.Read(ref activeExtensionMutationCount) == 0)
                {
                    Close();
                }
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void ResetChromeStoreInstallCancellationAfterCanceledClose()
    {
        if (isClosing
            || closeWhenExtensionOperationsFinish
            || Volatile.Read(ref activeExtensionMutationCount) > 0
            || !chromeStoreInstallCancellation.IsCancellationRequested)
        {
            return;
        }
        var canceled = chromeStoreInstallCancellation;
        chromeStoreInstallCancellation = new CancellationTokenSource();
        canceled.Dispose();
    }

    private sealed class ExtensionMutationScope : IDisposable
    {
        private MainForm? owner;

        internal ExtensionMutationScope(MainForm owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.EndExtensionMutation();
        }
    }

    private CoreWebView2Profile GetExtensionManagerProfile()
    {
        if (isPrivateMode)
        {
            throw new InvalidOperationException("Extensions are unavailable in private windows.");
        }
        return extensionProfileController?.CoreWebView2?.Profile
            ?? throw new InvalidOperationException("The extensions manager is no longer connected to the browser profile.");
    }

    private string? GetCurrentChromeStoreAddress()
    {
        var url = activeTab?.Url;
        if (BrowserExtensions.ParseChromeWebStoreExtensionId(url) is not null) return url;
        return state.History
            .Select(entry => entry.Url)
            .FirstOrDefault(historyUrl =>
                BrowserExtensions.ParseChromeWebStoreExtensionId(historyUrl) is not null);
    }

    private void OpenManagedExtensionsFolder()
    {
        OpenFolder(browserExtensions.ManagedFolderPath, "managed extensions folder");
    }

    private void OpenExtensionsFolder()
    {
        OpenFolder(browserExtensions.FolderPath, "user scripts folder");
    }

    private void OpenFolder(string path, string displayName)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            stateStore.Log($"Could not open the {displayName}", error);
            ShowTransientStatus($"Could not open the {displayName}");
        }
    }

    private static Exception CreateFriendlyExtensionError(Exception error)
    {
        if (error is InvalidDataException
            or FileNotFoundException
            or DirectoryNotFoundException
            or OperationCanceledException)
        {
            return error;
        }
        return error.HResult switch
        {
            unchecked((int)0x80070032) => new InvalidOperationException(
                "Browser extension support is unavailable in this WebView2 Runtime.", error),
            unchecked((int)0x80070002) => new InvalidOperationException(
                "The extension does not contain a valid manifest.json file.", error),
            unchecked((int)0x80070005) => new InvalidOperationException(
                "The extension files could not be accessed. Check their permissions and names.", error),
            _ when error is HttpRequestException => new InvalidOperationException(
                "The extension could not be downloaded. Check your connection and try again.", error),
            _ => new InvalidOperationException(
                string.IsNullOrWhiteSpace(error.Message)
                    ? "The browser extension could not be installed."
                    : error.Message,
                error)
        };
    }

    private void ShowDownloadsPopup()
    {
        downloadsPopup ??= new DownloadsPopupForm(BuildDownloadPopupRows, HandleDownloadPopupAction);
        downloadsPopup.Open(this, downloadsButtonAutoVisible ? downloadsButton : menuButton);
    }

    private IReadOnlyList<DownloadPopupRow> BuildDownloadPopupRows()
    {
        return downloads
            .OrderByDescending(item => item.StartedAt)
            .Select(download => new DownloadPopupRow(
                download.FileName,
                download.State,
                BuildDownloadProgress(download),
                string.IsNullOrWhiteSpace(download.InterruptReason)
                    ? download.EstimatedEndTimeText
                    : download.InterruptReason,
                CanPauseDownload(download),
                CanResumeDownload(download),
                !download.IsTerminal && download.CancelAction is not null,
                download.IsTerminal && File.Exists(download.FilePath),
                !string.IsNullOrWhiteSpace(download.FilePath),
                download))
            .ToArray();
    }

    private void HandleDownloadPopupAction(DownloadPopupAction action, object token)
    {
        if (token is not DownloadEntry download || !downloads.Contains(download)) return;
        switch (action)
        {
            case DownloadPopupAction.Pause:
                PauseDownload(download);
                break;
            case DownloadPopupAction.Resume:
                ResumeDownload(download);
                break;
            case DownloadPopupAction.Cancel:
                CancelDownload(download);
                break;
            case DownloadPopupAction.Open:
                OpenDownloadedFile(download);
                break;
            case DownloadPopupAction.ShowInFolder:
                ShowDownloadedFile(download);
                break;
            case DownloadPopupAction.CopyAddress:
                CopyDownloadAddress(download);
                break;
            case DownloadPopupAction.Remove when download.IsTerminal:
                downloads.Remove(download);
                RefreshDownloadsUi();
                ShowTransientStatus("Download removed from this session's list");
                break;
        }
    }

    private static bool CanPauseDownload(DownloadEntry download)
    {
        return !download.IsTerminal && download.PauseAction is not null
            && download.State.Equals("Downloading", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanResumeDownload(DownloadEntry download)
    {
        return !download.IsTerminal && download.ResumeAction is not null && download.CanResume;
    }

    private void PauseDownload(DownloadEntry download)
    {
        try
        {
            download.PauseAction?.Invoke();
            ShowTransientStatus($"Pausing {download.FileName}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not pause a download", error);
            ShowTransientStatus("Could not pause the download");
        }
    }

    private void ResumeDownload(DownloadEntry download)
    {
        try
        {
            download.ResumeAction?.Invoke();
            ShowTransientStatus($"Resuming {download.FileName}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not resume a download", error);
            ShowTransientStatus("Could not resume the download");
        }
    }

    private void RefreshDownloadsUi()
    {
        if (isClosing) return;
        if (!downloadUiTimer.Enabled) downloadUiTimer.Start();
    }

    private void RefreshDownloadsUiNow()
    {
        downloadUiTimer.Stop();
        if (downloadsPopup?.Visible == true) downloadsPopup.RefreshRows();
        UpdateStatus();
    }

    private void BeginDownloadGracePeriod()
    {
        if (isClosing) return;
        downloadsButtonAutoVisible = true;
        downloadGraceTimer.Stop();
        downloadGraceTimer.Start();
        UpdateResponsiveToolbar();
    }

    private void TrimDownloadRecords()
    {
        var active = downloads.Where(item => !item.IsTerminal).ToArray();
        var terminal = downloads
            .Where(item => item.IsTerminal)
            .OrderByDescending(item => item.StartedAt)
            .Take(50)
            .ToArray();
        var kept = active
            .Concat(terminal)
            .OrderByDescending(item => item.StartedAt)
            .ToArray();
        downloads.Clear();
        downloads.AddRange(kept);
    }

    private static void DetachDownloadHandlers(DownloadEntry download)
    {
        var operation = download.Operation;
        if (operation is not null)
        {
            try
            {
                if (download.StateChangedHandler is not null)
                {
                    operation.StateChanged -= download.StateChangedHandler;
                }
                if (download.BytesReceivedChangedHandler is not null)
                {
                    operation.BytesReceivedChanged -= download.BytesReceivedChangedHandler;
                }
                if (download.EstimatedEndTimeChangedHandler is not null)
                {
                    operation.EstimatedEndTimeChanged -= download.EstimatedEndTimeChangedHandler;
                }
            }
            catch (InvalidOperationException)
            {
                // The owning WebView process may already be gone.
            }
            catch (COMException)
            {
                // The owning WebView process may already be gone.
            }
        }
        download.Operation = null;
        download.StateChangedHandler = null;
        download.BytesReceivedChangedHandler = null;
        download.EstimatedEndTimeChangedHandler = null;
        download.CancelAction = null;
        download.PauseAction = null;
        download.ResumeAction = null;
    }

    private static string BuildDownloadProgress(DownloadEntry download)
    {
        if (download.TotalBytesToReceive > 0)
        {
            var percent = Math.Clamp(
                (double)download.BytesReceived / download.TotalBytesToReceive * 100d,
                0d,
                100d);
            return $"{FormatBytes(download.BytesReceived)} / {FormatBytes(download.TotalBytesToReceive)} ({percent:0}%)";
        }
        return download.BytesReceived > 0 ? $"{FormatBytes(download.BytesReceived)} received" : "Waiting for size";
    }

    private static string FormatEstimatedEndTime(object? value)
    {
        if (value is null) return string.Empty;
        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("1/1/0001", StringComparison.Ordinal)) return string.Empty;
        return DateTimeOffset.TryParse(text, out var parsed)
            ? $"ETA {parsed.ToLocalTime():t}"
            : string.Empty;
    }

    private static string MapDownloadInterruptReason(CoreWebView2DownloadInterruptReason reason)
    {
        return reason.ToString() switch
        {
            "UserCanceled" => "Canceled by user",
            "UserPaused" => "Paused by user",
            "NetworkInvalidRequest" or "NetworkFailed" or "NetworkTimeout" => "Network error",
            "NetworkServerDown" or "ServerFailed" or "ServerBadContent" => "Server error",
            "FileFailed" or "FileAccessDenied" or "FileNoSpace" => "Could not write the file",
            "FileNameTooLong" => "File name is too long",
            "FileSecurityCheckFailed" => "File security check failed",
            "DownloadProcessCrashed" => "Download process stopped",
            _ => reason == default ? "Download interrupted" : reason.ToString()
        };
    }

    private static void OpenDownloadsFolder()
    {
        var downloadsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        Directory.CreateDirectory(downloadsFolder);
        Process.Start(new ProcessStartInfo(downloadsFolder) { UseShellExecute = true });
    }

    private void OpenDownloadedFile(DownloadEntry download)
    {
        try
        {
            if (!File.Exists(download.FilePath))
            {
                ShowTransientStatus("The downloaded file is no longer at its saved location");
                return;
            }
            Process.Start(new ProcessStartInfo(download.FilePath) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            stateStore.Log("Could not open a downloaded file", error);
            ShowTransientStatus("Could not open the downloaded file");
        }
    }

    private void ShowDownloadedFile(DownloadEntry download)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(download.FilePath) && File.Exists(download.FilePath))
            {
                var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                startInfo.ArgumentList.Add($"/select,{download.FilePath}");
                Process.Start(startInfo);
                return;
            }

            var folder = Path.GetDirectoryName(download.FilePath);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                return;
            }
            OpenDownloadsFolder();
        }
        catch (Exception error)
        {
            stateStore.Log("Could not show a downloaded file", error);
            ShowTransientStatus("Could not open the download location");
        }
    }

    private void CancelDownload(DownloadEntry download)
    {
        try
        {
            download.CancelAction?.Invoke();
            download.CancelAction = null;
            ShowTransientStatus($"Canceling {download.FileName}");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not cancel a download", error);
            ShowTransientStatus("Could not cancel the download");
        }
    }

    internal static string FormatDownloadProgress(long bytesReceived, long totalBytesToReceive, bool isTerminal = false)
    {
        if (isTerminal) return string.Empty;
        if (totalBytesToReceive > 0)
        {
            var percent = Math.Clamp(
                (double)bytesReceived / totalBytesToReceive * 100d,
                0d,
                100d);
            return $"{percent:0}%";
        }

        return bytesReceived > 0
            ? $"{FormatBytes(bytesReceived)} received"
            : "";
    }

    private static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unitIndex = 0;
        var display = (double)value;
        while (display >= 1024 && unitIndex < units.Length - 1)
        {
            display /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value} B" : $"{display:0.0} {units[unitIndex]}";
    }

    private void CopyDownloadAddress(DownloadEntry download)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(download.SourceUrl)) return;
            Clipboard.SetText(download.SourceUrl);
            ShowTransientStatus("Download address copied");
        }
        catch (Exception error)
        {
            stateStore.Log("Could not copy a download address", error);
            ShowTransientStatus("Could not copy the download address");
        }
    }

    internal void OpenPrivateWindow()
    {
        if (isClosing) return;
        var privateWindow = new MainForm(true, null, null, BrowserMode.Private)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(Location.X + 28, Location.Y + 28)
        };
        Program.RegisterTopLevelWindow(privateWindow);
        privateWindow.Show();
        ShowTransientStatus("Incognito window opened");
    }

    private void ToggleReducedWebsiteMotion()
    {
        reduceWebsiteMotionEnabled = !reduceWebsiteMotionEnabled;
        state.ReduceWebsiteMotionEnabled = reduceWebsiteMotionEnabled;
        foreach (var tab in tabs.Where(item => item.Core is not null))
        {
            RunUiTask(
                () => ApplyMotionPolicyAsync(tab),
                "Could not update website motion settings");
        }
        if (!isPrivateMode) SavePreferences();
        ShowTransientStatus(reduceWebsiteMotionEnabled
            ? "Reduced website motion on"
            : "Reduced website motion off; reload may restore already-canceled animations");
    }

    private async Task ApplyMotionPolicyAsync(BrowserTab tab)
    {
        var core = tab.Core;
        if (core is null) return;
        var requestGeneration = ++tab.MotionPolicyGeneration;
        var requestedReducedMotion = reduceWebsiteMotionEnabled;
        bool IsCurrent() => IsMotionPolicyContinuationEligible(
            isClosing,
            tab.IsClosed,
            ReferenceEquals(tab.Core, core),
            tab.MotionPolicyGeneration == requestGeneration,
            reduceWebsiteMotionEnabled == requestedReducedMotion);

        if (tab.NoMotionScriptId is not null)
        {
            var existingScriptId = tab.NoMotionScriptId;
            try
            {
                core.RemoveScriptToExecuteOnDocumentCreated(existingScriptId);
            }
            catch (Exception error)
            {
                stateStore.Log("Could not remove the no-motion page policy", error);
            }
            if (IsCurrent()
                && string.Equals(tab.NoMotionScriptId, existingScriptId, StringComparison.Ordinal))
            {
                tab.NoMotionScriptId = null;
            }
        }

        if (!IsCurrent()) return;
        if (requestedReducedMotion)
        {
            string? installedScriptId = null;
            try
            {
                installedScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    BrowserPerformance.NoMotionDocumentScript);
                if (!IsCurrent())
                {
                    try { core.RemoveScriptToExecuteOnDocumentCreated(installedScriptId); }
                    catch (Exception error)
                    {
                        stateStore.Log("Could not remove a stale no-motion page policy", error);
                    }
                    return;
                }
                tab.NoMotionScriptId = installedScriptId;
            }
            catch (Exception error)
            {
                if (IsCurrent())
                {
                    stateStore.Log("Could not install the no-motion page policy", error);
                }
            }
        }
        else
        {
            try { await core.ExecuteScriptAsync(BrowserPerformance.RestoreMotionDocumentScript); }
            catch (Exception error) { stateStore.Log("Could not restore website motion", error); }
        }
    }

    internal static bool IsMotionPolicyContinuationEligible(
        bool isClosing,
        bool tabIsClosed,
        bool coreIsCurrent,
        bool generationIsCurrent,
        bool settingIsCurrent) =>
        !isClosing
        && !tabIsClosed
        && coreIsCurrent
        && generationIsCurrent
        && settingIsCurrent;

    private void OnSystemPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not UserPreferenceCategory.Color and not UserPreferenceCategory.General) return;
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)ApplyChromeColorPolicy); }
            catch (InvalidOperationException) { }
            return;
        }
        ApplyChromeColorPolicy();
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

    private async Task ToggleReaderModeAsync()
    {
        var tab = activeTab;
        var core = tab?.Core;
        if (tab is null || tab.IsStartPage || core is null)
        {
            ShowTransientStatus("Reader mode is available on an open web page");
            return;
        }
        var documentGeneration = tab.DocumentNavigationGeneration;
        bool IsCurrent() => IsReaderModeContinuationEligible(
            isClosing,
            tab.IsClosed,
            ReferenceEquals(activeTab, tab),
            ReferenceEquals(tab.Core, core),
            tab.DocumentNavigationGeneration == documentGeneration);

        try
        {
            var result = await core.ExecuteScriptAsync(ReaderModeScript.Toggle);
            if (!IsCurrent()) return;
            var enabled = result.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (!enabled && !tab.ReaderModeActive)
            {
                ShowTransientStatus("Reader mode is unavailable for this page");
                return;
            }
            tab.ReaderModeActive = !tab.ReaderModeActive;
            StopFindSession(tab);
            if (findBar.Visible && findBox.TextLength > 0)
            {
                try { await StartFindSession(); }
                catch (Exception findError)
                {
                    if (IsCurrent()) stateStore.Log("Could not refresh find after reader mode", findError);
                }
            }
            if (!IsCurrent()) return;
            UpdateSiteInfo();
            ShowTransientStatus(tab.ReaderModeActive ? "Reader mode on" : "Reader mode off");
        }
        catch (Exception error)
        {
            if (!IsCurrent()) return;
            tab.ReaderModeActive = false;
            stateStore.Log("Could not toggle reader mode", error);
            ShowTransientStatus("Reader mode is unavailable for this page");
        }
    }

    internal static bool IsReaderModeContinuationEligible(
        bool isClosing,
        bool tabIsClosed,
        bool tabIsActive,
        bool coreIsCurrent,
        bool documentIsCurrent) =>
        !isClosing
        && !tabIsClosed
        && tabIsActive
        && coreIsCurrent
        && documentIsCurrent;

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
        var localSuggestions = AddressSuggestionEngine.GetSuggestions(query, state);
        addressSuggestionPopup.SetSuggestions(localSuggestions);
        PositionAddressSuggestions(suggestionAnchor);

        liveSuggestionCts?.Cancel();
        liveSuggestionCts?.Dispose();
        liveSuggestionCts = null;

        var targetAnchor = suggestionAnchor;
        var cts = new CancellationTokenSource();
        liveSuggestionCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                var liveQueries = await SearchSuggestionService.Instance.GetSearchSuggestionsAsync(query, cts.Token).ConfigureAwait(false);
                if (cts.Token.IsCancellationRequested || isClosing) return;

                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(() =>
                {
                    if (cts.Token.IsCancellationRequested || isClosing || IsDisposed) return;
                    if (lastAddressSuggestionInput != query) return;
                    var currentlyActive = (addressBarEditing && addressBar.Focused)
                        || (smartSearchBarEditing && activeSmartSearchBar is not null && activeSmartSearchBar.InputControl.Focused);
                    if (!currentlyActive) return;

                    var merged = AddressSuggestionEngine.MergeWithLiveSearch(localSuggestions, liveQueries, query, state);
                    addressSuggestionPopup.SetSuggestions(merged);
                    PositionAddressSuggestions(targetAnchor);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                stateStore.Log("Live search suggestions error", error);
            }
        });
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

        addressSuggestionPopup.ClearSuggestions();
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
        if (suggestion.IsSearch) return;

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
        addressSuggestionPopup.SetSuggestions(AddressSuggestionEngine.GetSuggestions(query, state));
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

    private static string? GetKeepAwakeHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !BrowserPolicy.IsHttpUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return BrowserPolicy.NormalizeExactHost(uri.Host);
    }

    private bool UpdateTabUrlFromSource(BrowserTab tab, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;

        if (BrowserPolicy.TryNormalizeLocalFileUrl(source, out var localFileUrl))
        {
            var keepAwakeChanged = tab.KeepAwake;
            tab.Url = localFileUrl;
            tab.KeepAwake = false;
            if (keepAwakeChanged)
            {
                ApplyLiveTabMemoryTarget(tab, tab == activeTab && !isWindowMinimized);
                RefreshMemorySweepTimer();
            }
            return keepAwakeChanged;
        }
        if (!BrowserPolicy.IsHttpUrl(source)) return false;

        tab.Url = source;
        var keepAwake = IsKeepAwakeHost(source);
        if (tab.KeepAwake == keepAwake) return false;

        tab.KeepAwake = keepAwake;
        if (keepAwake) tab.ConsecutiveSuspendFailures = 0;
        ApplyLiveTabMemoryTarget(tab, tab == activeTab && !isWindowMinimized);
        RefreshMemorySweepTimer();
        return true;
    }

    private bool IsKeepAwakeHost(string? url)
    {
        var host = GetKeepAwakeHost(url);
        if (host is null) return false;
        return state.KeepAwakeHosts.Any(item => BrowserPolicy.IsExactHost(item, host));
    }

    private void SyncKeepAwakeHostState(BrowserTab tab)
    {
        var host = GetKeepAwakeHost(tab.Url);
        if (host is null) return;

        if (tab.KeepAwake)
        {
            state.KeepAwakeHosts.RemoveAll(item => BrowserPolicy.IsExactHost(item, host));
            state.KeepAwakeHosts.Insert(0, host);
            if (state.KeepAwakeHosts.Count > BrowserStateStore.MaximumKeepAwakeHosts)
            {
                state.KeepAwakeHosts.RemoveRange(
                    BrowserStateStore.MaximumKeepAwakeHosts,
                    state.KeepAwakeHosts.Count - BrowserStateStore.MaximumKeepAwakeHosts);
            }
        }
        else
        {
            state.KeepAwakeHosts.RemoveAll(item => BrowserPolicy.IsExactHost(item, host));
        }

        if (!isPrivateMode) ScheduleStateSave();
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
        if (keyData is (Keys.Control | Keys.R) or Keys.F5)
        {
            ReloadOrStop();
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
            ToggleFullScreen();
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
        if (e.KeyCode == Keys.Control && mruSnapshot is not null)
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
        addressBar.Text = activeTab is null || activeTab.IsStartPage
            ? string.Empty
            : FormatAddressForDisplay(activeTab.Url);
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
        var palette = ResolveChromeColorPolicy(IsHighContrastActive);
        var muted = palette.HighContrast ? palette.FieldText : MutedTextColor;
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
        var palette = ResolveChromeColorPolicy(IsHighContrastActive);
        var tab = activeTab;
        var isFavorite = tab is not null && state.Bookmarks.Any(item =>
            BrowserPolicy.UrlEquals(item.Url, tab.Url));
        favoriteButton.GlyphState = isFavorite ? "filled" : "outline";
        favoriteButton.ForeColor = palette.HighContrast
            ? palette.FieldText
            : isFavorite ? AccentColor : MutedTextColor;
        favoriteButton.AccessibleName = isFavorite ? "Remove from favorites" : "Add to favorites";
    }

    private void BeginTabDrag(BrowserTab tab, Point screenLocation)
    {
        if (isClosing || tab.IsClosed || !tabs.Contains(tab)) return;

        draggedTab = tab;
        tabDragDropIndex = -1;
        tabDragIndicatorIndex = -1;
        tabDragScreenLocation = screenLocation;
        tab.Header.SetDragging(true);
        tabDragGhost.SetState(tab.Title, IsHighContrastActive);
        ActivateTab(tab, focusPage: false, restoreDiscarded: false);
        UpdateTabDrag(screenLocation);
    }

    private void UpdateTabDrag(Point screenLocation)
    {
        var tab = draggedTab;
        if (tab is null || tab.IsClosed || !tabs.Contains(tab))
        {
            CancelTabDrag();
            return;
        }

        tabDragScreenLocation = screenLocation;
        PositionTabDragGhost(tab, screenLocation);
        var rawIndex = GetTabDropIndex(screenLocation);
        if (rawIndex < 0)
        {
            tabDragDropIndex = -1;
            tabDragIndicatorIndex = -1;
            tabDropIndicator.Visible = false;
            return;
        }

        tabDragIndicatorIndex = rawIndex;
        tabDragDropIndex = GetFinalTabDropIndex(tab, rawIndex);
        PositionTabDropIndicator(rawIndex);
    }

    private void CompleteTabDrag(BrowserTab tab, Point screenLocation)
    {
        if (draggedTab != tab) return;

        UpdateTabDrag(screenLocation);
        var targetIndex = tabDragDropIndex;
        CancelTabDrag();
        if (targetIndex >= 0) MoveTabToIndex(tab, targetIndex);
    }

    private void CancelTabDrag()
    {
        draggedTab?.Header.SetDragging(false);
        draggedTab = null;
        tabDragDropIndex = -1;
        tabDragIndicatorIndex = -1;
        tabDropIndicator.Visible = false;
        tabDragGhost.Visible = false;
    }

    private int GetTabDropIndex(Point screenLocation)
    {
        if (tabs.Count == 0) return -1;

        var areaPoint = tabArea.PointToClient(screenLocation);
        if (!tabArea.ClientRectangle.Contains(areaPoint)
            || newTabButton.Bounds.Contains(areaPoint))
        {
            return -1;
        }

        var stripPoint = tabStrip.PointToClient(screenLocation);
        if (stripPoint.Y < 0 || stripPoint.Y > tabStrip.ClientSize.Height) return -1;

        var rawIndex = tabs.Count;
        for (var index = 0; index < tabs.Count; index++)
        {
            var header = tabs[index].Header;
            if (stripPoint.X < header.Left + (header.Width / 2))
            {
                rawIndex = index;
                break;
            }
        }

        var tab = draggedTab;
        if (tab is null) return rawIndex;

        var pinnedCount = tabs.Count(item => item.IsPinned);
        var minimumIndex = tab.IsPinned ? 0 : pinnedCount;
        var maximumIndex = tab.IsPinned ? pinnedCount : tabs.Count;
        return Math.Clamp(rawIndex, minimumIndex, maximumIndex);
    }

    private int GetFinalTabDropIndex(BrowserTab tab, int rawIndex)
    {
        var oldIndex = tabs.IndexOf(tab);
        if (oldIndex < 0) return -1;

        var pinnedCount = tabs.Count(item => item.IsPinned);
        var minimumIndex = tab.IsPinned ? 0 : pinnedCount;
        var maximumIndex = tab.IsPinned ? pinnedCount : tabs.Count;
        var constrainedIndex = Math.Clamp(rawIndex, minimumIndex, maximumIndex);
        var finalIndex = constrainedIndex > oldIndex ? constrainedIndex - 1 : constrainedIndex;
        return Math.Clamp(finalIndex, 0, tabs.Count - 1);
    }

    private bool MoveTabToIndex(BrowserTab tab, int targetIndex)
    {
        var oldIndex = tabs.IndexOf(tab);
        if (oldIndex < 0) return false;

        targetIndex = Math.Clamp(targetIndex, 0, tabs.Count - 1);
        if (oldIndex == targetIndex) return false;

        tabs.RemoveAt(oldIndex);
        tabs.Insert(targetIndex, tab);
        if (tabStrip.Controls.Contains(tab.Header))
        {
            tabStrip.Controls.SetChildIndex(tab.Header, targetIndex);
        }
        ResizeTabHeaders();
        if (!isPrivateMode) ScheduleStateSave();
        return true;
    }

    private void PositionTabDropIndicator(int rawIndex)
    {
        if (rawIndex < 0 || tabs.Count == 0)
        {
            tabDropIndicator.Visible = false;
            return;
        }

        rawIndex = Math.Clamp(rawIndex, 0, tabs.Count);
        var stripX = rawIndex == tabs.Count
            ? tabs[^1].Header.Right
            : tabs[rawIndex].Header.Left;
        var indicatorWidth = Math.Max(2, ScaleChromeLogical(2));
        var indicatorX = tabStrip.Left + stripX - (indicatorWidth / 2);
        var indicatorTop = tabStrip.Top + ScaleChromeLogical(3);
        var indicatorHeight = Math.Max(
            ScaleChromeLogical(16),
            tabStrip.ClientSize.Height - ScaleChromeLogical(6));
        var maximumX = Math.Max(0, tabArea.ClientSize.Width - indicatorWidth);
        tabDropIndicator.SetBounds(
            Math.Clamp(indicatorX, 0, maximumX),
            indicatorTop,
            indicatorWidth,
            indicatorHeight);
        tabDropIndicator.Visible = true;
        tabDropIndicator.BringToFront();
    }

    private void PositionTabDragGhost(BrowserTab tab, Point screenLocation)
    {
        var areaPoint = tabArea.PointToClient(screenLocation);
        var verticalAllowance = ScaleChromeLogical(24);
        if (areaPoint.Y < -verticalAllowance
            || areaPoint.Y > tabArea.ClientSize.Height + verticalAllowance)
        {
            tabDragGhost.Visible = false;
            return;
        }

        var ghostWidth = Math.Max(ScaleChromeLogical(72), tab.Header.Width);
        var ghostHeight = Math.Max(ScaleChromeLogical(28), tab.Header.Height);
        var maximumX = Math.Max(0, tabArea.ClientSize.Width - ghostWidth);
        var ghostX = Math.Clamp(areaPoint.X - (ghostWidth / 2), 0, maximumX);
        var maximumY = Math.Max(0, tabArea.ClientSize.Height - ghostHeight);
        var ghostY = Math.Clamp(tabStrip.Top + tab.Header.Top, 0, maximumY);
        tabDragGhost.SetBounds(ghostX, ghostY, ghostWidth, ghostHeight);
        tabDragGhost.Visible = true;
        tabDragGhost.BringToFront();
        if (tabDropIndicator.Visible) tabDropIndicator.BringToFront();
    }

    private void UpdateTabHeader(BrowserTab tab)
    {
        tab.Header.HighContrast = IsHighContrastActive;
        tab.Header.IsPrivateMode = isPrivateMode;
        tab.Header.SetState(
            tab.Title,
            activeTab == tab,
            tab.IsSuspended,
            tab.IsLoading || tab.IsInitializing,
            tab.IsDiscarded,
            IsBackgroundProtected(tab),
            tab.IsAudible,
            IsTabMuted(tab),
            tab.IsPinned);
    }

    private bool IsTabMuted(BrowserTab tab)
    {
        return tab.IsMuted || (!isPrivateMode && SitePreferencePolicy.ShouldMuteAudio(state.AudioOutputPolicy, state.AllowedAudioHosts, state.MutedHosts, tab.Url));
    }

    private void ResizeTabHeaders()
    {
        var newTabWidth = ScaleChromeLogical(32);
        var windowDragReserveWidth = ScaleChromeLogical(WindowDragReserveLogicalWidth);
        var availableWidth = Math.Max(
            0,
            tabArea.ClientSize.Width - newTabWidth - windowDragReserveWidth);
        var stripHeight = Math.Max(ScaleChromeLogical(28), tabArea.ClientSize.Height);
        if (tabs.Count == 0 || availableWidth <= 0)
        {
            tabStrip.SetBounds(0, 0, 0, stripHeight);
            newTabButton.SetBounds(0, 0, newTabWidth, stripHeight);
            newTabButton.BringToFront();
            tabStrip.AutoScrollMinSize = Size.Empty;
            tabStrip.AutoScrollPosition = Point.Empty;
            return;
        }
        var minimumTabWidth = ScaleChromeLogical(84);
        var contentAvailable = Math.Max(minimumTabWidth, availableWidth - tabStrip.Padding.Horizontal);
        var width = Math.Clamp(
            (contentAvailable / tabs.Count) - ScaleChromeLogical(4),
            minimumTabWidth,
            ScaleChromeLogical(220));
        var contentWidth = tabStrip.Padding.Horizontal;
        foreach (var tab in tabs)
        {
            var headerWidth = tab.IsPinned
                ? Math.Clamp(
                    Math.Min(width, ScaleChromeLogical(124)),
                    ScaleChromeLogical(92),
                    ScaleChromeLogical(124))
                : width;
            tab.Header.Width = headerWidth;
            contentWidth += headerWidth + tab.Header.Margin.Horizontal;
        }
        var stripWidth = Math.Min(contentWidth, availableWidth);
        var overflowing = contentWidth > availableWidth;
        tabStrip.AutoScrollMinSize = overflowing
            ? new Size(contentWidth, 0)
            : Size.Empty;
        if (!overflowing) tabStrip.AutoScrollPosition = Point.Empty;
        // Keep the native horizontal bar clipped just below the custom title
        // row. Wheel scrolling and active-tab reveal remain available without
        // adding a heavyweight second chrome row.
        var stripControlHeight = stripHeight
            + (overflowing ? SystemInformation.HorizontalScrollBarHeight : 0);
        tabStrip.SetBounds(0, 0, stripWidth, stripControlHeight);
        newTabButton.SetBounds(stripWidth, 0, newTabWidth, stripHeight);
        newTabButton.BringToFront();
        tabStrip.AccessibleDescription = overflowing
            ? "Open tabs. Use the mouse wheel or keyboard shortcuts to reach tabs outside the visible strip."
            : "Open tabs";
        if (overflowing && activeTab?.Header.Parent == tabStrip)
        {
            tabStrip.ScrollControlIntoView(activeTab.Header);
        }
        if (draggedTab is not null)
        {
            PositionTabDragGhost(draggedTab, tabDragScreenLocation);
            if (tabDragIndicatorIndex >= 0) PositionTabDropIndicator(tabDragIndicatorIndex);
        }
    }

    private void ScrollTabStrip(int wheelDelta)
    {
        var maximum = Math.Max(0, tabStrip.AutoScrollMinSize.Width - tabStrip.ClientSize.Width);
        if (wheelDelta == 0 || maximum == 0) return;

        var current = Math.Clamp(-tabStrip.AutoScrollPosition.X, 0, maximum);
        var direction = wheelDelta > 0 ? -1 : 1;
        var next = Math.Clamp(current + direction * ScaleChromeLogical(120), 0, maximum);
        tabStrip.AutoScrollPosition = new Point(next, 0);
    }

    private void UpdateResponsiveToolbar()
    {
        if (toolbar.ColumnStyles.Count < 8) return;
        var width = toolbar.ClientSize.Width;
        SetToolbarColumnVisibility(3, width >= ScaleToolbarLogical(560), ScaleToolbarLogical(36));
        SetToolbarColumnVisibility(6, downloadsButtonAutoVisible, ScaleToolbarLogical(38));
        // GetControlFromPosition can omit a control that started hidden; keep
        // the download action explicitly synchronized with its reserved cell.
        downloadsButton.Visible = downloadsButtonAutoVisible;

        var fixedWidth = toolbar.Padding.Horizontal;
        foreach (var column in new[] { 0, 1, 2, 3, 6, 7 })
        {
            fixedWidth += (int)Math.Ceiling(toolbar.ColumnStyles[column].Width);
        }

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
        if (findBar.ColumnStyles.Count < 5) return;
        var width = Math.Max(1, findBar.ClientSize.Width);
        var buttonWidth = ScaleChromeLogical(36);
        var matchWidth = ScaleChromeLogical(width >= ScaleChromeLogical(640) ? 110 : 72);
        var rightPadding = ScaleChromeLogical(10);
        var preferredLeft = width >= ScaleChromeLogical(900)
            ? ScaleChromeLogical(244)
            : width >= ScaleChromeLogical(700) ? ScaleChromeLogical(120) : ScaleChromeLogical(12);
        var minimumInput = ScaleChromeLogical(120);
        var fixedControls = matchWidth + (buttonWidth * 3) + rightPadding;
        var leftPadding = Math.Min(
            preferredLeft,
            Math.Max(ScaleChromeLogical(4), width - fixedControls - minimumInput));
        findBar.Padding = new Padding(
            leftPadding,
            ScaleChromeLogical(5),
            rightPadding,
            ScaleChromeLogical(5));
        findBar.ColumnStyles[1].Width = matchWidth;
        for (var index = 2; index <= 4; index++) findBar.ColumnStyles[index].Width = buttonWidth;
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
        var isMaximized = WindowState == FormWindowState.Maximized;
        maximizeButton.Glyph = isMaximized ? CaptionGlyph.Restore : CaptionGlyph.Maximize;
        maximizeButton.Invalidate();
        maximizeButton.AccessibleName = isMaximized ? "Restore down" : "Maximize";
        maximizeButton.AccessibleDescription = maximizeButton.AccessibleName;
        toolTip.SetToolTip(maximizeButton, maximizeButton.AccessibleName);
        Padding = isMaximized || isFullScreen || isDomFullScreen ? Padding.Empty : new Padding(1);
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
        var parts = new List<string>(4);
        var tab = activeTab;
        if (tab?.HasPendingHoverStatus == true)
        {
            var pendingStatus = tab.PendingHoverStatus ?? string.Empty;
            tab.PendingHoverStatus = null;
            tab.HasPendingHoverStatus = false;
            tab.HoverStatus = FormatHoverStatusForDisplay(pendingStatus);
        }
        if (!string.IsNullOrWhiteSpace(transientStatus)) parts.Add(transientStatus);
        else if (!string.IsNullOrWhiteSpace(tab?.HoverStatus)) parts.Add(tab.HoverStatus);
        else if (tab is not null) parts.Add(tab.StatusText);
        else parts.Add(environment is null ? "Starting\u2026" : "Ready");

        if (tab?.BlockedRequestCount > 0) parts.Add($"{tab.BlockedRequestCount} blocked");
        var activeDownloads = tabs.Sum(item => item.ActiveDownloads);
        if (activeDownloads > 0) parts.Add($"{activeDownloads} downloading");
        var sleeping = tabs.Count(item => item.IsSuspended);
        if (sleeping > 0) parts.Add($"{sleeping} sleeping");
        var unloaded = tabs.Count(item => item.IsDiscarded);
        if (unloaded > 0) parts.Add($"{unloaded} unloaded");

        var text = string.Join(" \u00B7 ", parts);
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
        if (activeTab == tab && actionText is not null && IsHandleCreated)
        {
            BeginInvoke(() =>
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
            normalWindowState = WindowState;
            normalBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
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
        ScheduleStateSave();
    }

    internal static void ApplyResourceModeDefaults(BrowserState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ResourceModeDefaultsVersion is not null) return;

        // One-time migration: standard Memory Saver is now the default.
        // Once saved, later manual resource-mode choices remain untouched.
        state.MemorySaverEnabled = true;
        state.UltraLightModeEnabled = false;
        state.ResourceModeDefaultsVersion = 1;
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
        if (!isPrivateMode) state.WindowPlacement = CaptureCurrentWindowPlacement();

        var activeIndex = activeTab is null ? -1 : restorableTabs.IndexOf(activeTab);
        state.ActiveTabIndex = activeIndex >= 0 ? activeIndex : null;
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
        try { System.IO.File.WriteAllText("form_closing.log", $"{e.CloseReason}\n{Environment.StackTrace}"); } catch { }
        if (isClosing) return;

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
            var answer = MessageBox.Show(
                this,
                activeDownloadCount == 1
                    ? "One download is still in progress. Exiting now will cancel it."
                    : $"{activeDownloadCount} downloads are still in progress. Exiting now will cancel them.",
                "Exit and cancel downloads?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                ShowDownloadsPopup();
                ResetChromeStoreInstallCancellationAfterCanceledClose();
                return;
            }
        }

        var sessionSaved = !persistStateOnClose
            || (browserProcessRestartApproved && browserProcessRestartStateSaved)
            || SaveStateNow();
        if (!sessionSaved
            && e.CloseReason is CloseReason.UserClosing or CloseReason.ApplicationExitCall)
        {
            var answer = MessageBox.Show(
                this,
                "MishaWeb could not save your current tabs and window settings. Exiting anyway may restore an older session the next time you open MishaWeb.",
                "Exit without saving this session?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                ScheduleStateSave();
                ShowTransientStatus("Exit canceled — MishaWeb will retry saving your session");
                ResetChromeStoreInstallCancellationAfterCanceledClose();
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
        if (isPrivateMode)
        {
            downloads.Clear();
            try
            {
                var core = tabs.Select(item => item.Core).FirstOrDefault(item => item is not null);
                if (core is not null)
                {
                    _ = core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                }
            }
            catch { }
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
            commandPalette?.Dispose();
            mruSwitcher?.Dispose();
            sessionManager?.Dispose();
            permissionPrompt?.Dispose();
            permissionManager?.Dispose();
            downloadsPopup?.Dispose();
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
        button.Font = new Font("Segoe UI Semibold", fontSize);
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
        button.Font = new Font("Segoe UI", fontSize);
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

    private sealed class FirstClickSelectTextBox : TextBox
    {
        private const int WmLeftButtonDown = 0x0201;
        private const int WmLeftButtonUp = 0x0202;
        private bool selectAllOnLeftButtonUp;

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmLeftButtonDown)
            {
                selectAllOnLeftButtonUp = !Focused;
            }

            base.WndProc(ref message);

            if (message.Msg == WmLeftButtonUp)
            {
                var shouldSelectAll = selectAllOnLeftButtonUp;
                selectAllOnLeftButtonUp = false;
                if (shouldSelectAll && Focused && TextLength > 0) SelectAll();
            }
        }

        protected override void OnLostFocus(EventArgs e)
        {
            selectAllOnLeftButtonUp = false;
            base.OnLostFocus(e);
        }
    }

    private sealed class ChromeMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly DarkMenuColorTable colorTable;

        public ChromeMenuRenderer() : this(new DarkMenuColorTable())
        {
        }

        private ChromeMenuRenderer(DarkMenuColorTable colorTable) : base(colorTable)
        {
            this.colorTable = colorTable;
            RoundedEdges = false;
        }

        public bool HighContrast
        {
            get => colorTable.HighContrast;
            set => colorTable.HighContrast = value;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (HighContrast)
            {
                e.TextColor = !e.Item.Enabled
                    ? SystemColors.GrayText
                    : e.Item.Selected ? SystemColors.HighlightText : SystemColors.MenuText;
            }
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkMenuColorTable : ProfessionalColorTable
    {
        public bool HighContrast { get; set; }

        public override Color ToolStripDropDownBackground => HighContrast ? SystemColors.Menu : ToolbarColor;
        public override Color ImageMarginGradientBegin => HighContrast ? SystemColors.Menu : ToolbarColor;
        public override Color ImageMarginGradientMiddle => HighContrast ? SystemColors.Menu : ToolbarColor;
        public override Color ImageMarginGradientEnd => HighContrast ? SystemColors.Menu : ToolbarColor;
        public override Color MenuBorder => HighContrast ? SystemColors.WindowText : NativeUiTheme.Border;
        public override Color MenuItemBorder => HighContrast ? SystemColors.Highlight : NativeUiTheme.Focus;
        public override Color MenuItemSelected => HighContrast ? SystemColors.Highlight : NativeUiTheme.Selection;
        public override Color MenuItemSelectedGradientBegin => HighContrast ? SystemColors.Highlight : NativeUiTheme.Selection;
        public override Color MenuItemSelectedGradientEnd => HighContrast ? SystemColors.Highlight : NativeUiTheme.Selection;
        public override Color SeparatorDark => HighContrast ? SystemColors.WindowText : NativeUiTheme.Border;
        public override Color SeparatorLight => HighContrast ? SystemColors.WindowText : NativeUiTheme.Border;
    }

    private enum ChromeIconKind
    {
        None,
        Back,
        Forward,
        Reload,
        Home,
        SiteInfo,
        Favorite,
        Downloads,
        Menu
    }

    private sealed class ChromeButton : Button
    {
        private static readonly Color DisabledTextColor = Color.FromArgb(139, 105, 121);
        private static readonly Color HoverSurfaceColor = NativeUiTheme.Hover;
        private static readonly Color PressedSurfaceColor = NativeUiTheme.Pressed;
        private static readonly Color FocusStrokeColor = NativeUiTheme.Focus;
        private bool isHovered;
        private bool isPressed;
        private ChromeIconKind iconKind;
        private string glyphState = string.Empty;

        public ChromeButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.Opaque
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            UseMnemonic = false;
        }

        public ChromeIconKind IconKind
        {
            get => iconKind;
            set
            {
                if (iconKind == value) return;
                iconKind = value;
                if (iconKind != ChromeIconKind.None && Text.Length > 0) Text = string.Empty;
                Invalidate();
            }
        }

        public string GlyphState
        {
            get => glyphState;
            set
            {
                var next = value ?? string.Empty;
                if (glyphState.Equals(next, StringComparison.Ordinal)) return;
                glyphState = next;
                Invalidate();
            }
        }
        public int CornerRadius { get; set; } = 10;
        public bool HighContrast { get; set; }

        protected override void OnTextChanged(EventArgs e)
        {
            if (iconKind != ChromeIconKind.None && Text.Length > 0)
            {
                Text = string.Empty;
                return;
            }
            base.OnTextChanged(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            isHovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            isHovered = false;
            isPressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Enabled && e.Button == MouseButtons.Left) isPressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            isPressed = false;
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
            isPressed = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            isPressed = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // The hover/idle layers below are translucent. Clear to the opaque
            // owning surface first so repeated paints cannot accumulate stale
            // glyph or legacy Button.Text pixels in the backing buffer.
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var surfaceBounds = new RectangleF(
                0.5f,
                0.5f,
                Math.Max(1, Width - 1f),
                Math.Max(1, Height - 1f));
            using var surfacePath = CreateRoundedPath(surfaceBounds, Scale(CornerRadius));
            var highlighted = HighContrast && Enabled && (isPressed || isHovered);
            var surfaceColor = !Enabled
                ? Color.Transparent
                : highlighted ? SystemColors.Highlight
                : isPressed ? PressedSurfaceColor
                : isHovered ? HoverSurfaceColor
                : HighContrast ? Color.Transparent : Color.FromArgb(18, 255, 255, 255);
            if (surfaceColor.A > 0)
            {
                using var surface = new SolidBrush(surfaceColor);
                e.Graphics.FillPath(surface, surfacePath);
            }

            if (Enabled && (isHovered || isPressed || (Focused && ShowFocusCues)))
            {
                using var border = new Pen(
                    HighContrast
                        ? Focused && ShowFocusCues ? SystemColors.Highlight : SystemColors.WindowText
                        : Focused && ShowFocusCues ? FocusStrokeColor : Color.FromArgb(96, NativeUiTheme.Focus),
                    Focused && ShowFocusCues ? Scale(HighContrast ? 2f : 1.3f) : Scale(0.8f));
                e.Graphics.DrawPath(border, surfacePath);
            }

            var scale = DeviceDpi / 96f;
            var glyphColor = HighContrast
                ? !Enabled ? SystemColors.GrayText : highlighted ? SystemColors.HighlightText : ForeColor
                : Enabled ? ForeColor : DisabledTextColor;
            var glyphSize = Math.Min(21f * scale, Math.Max(12f, Math.Min(Width, Height) - (8f * scale)));
            var glyphBounds = new RectangleF(
                (Width - glyphSize) / 2f,
                (Height - glyphSize) / 2f,
                glyphSize,
                glyphSize);
            DrawIcon(e.Graphics, IconKind, GlyphState, glyphBounds, glyphColor, scale);
        }

        private void DrawIcon(
            Graphics graphics,
            ChromeIconKind kind,
            string text,
            RectangleF bounds,
            Color color,
            float scale)
        {
            if (kind == ChromeIconKind.None)
            {
                TextRenderer.DrawText(
                    graphics,
                    text,
                    Font,
                    ClientRectangle,
                    color,
                    TextFormatFlags.HorizontalCenter
                    | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding
                    | TextFormatFlags.NoPrefix
                    | TextFormatFlags.SingleLine);
                return;
            }

            var penWidth = Math.Max(1.35f, 1.7f * scale);
            using var pen = new Pen(color, penWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var center = new PointF(
                bounds.Left + (bounds.Width / 2f),
                bounds.Top + (bounds.Height / 2f));

            switch (kind)
            {
                case ChromeIconKind.Back:
                    DrawNavigationArrow(graphics, pen, bounds, forward: false);
                    break;
                case ChromeIconKind.Forward:
                    DrawNavigationArrow(graphics, pen, bounds, forward: true);
                    break;
                case ChromeIconKind.Reload:
                    {
                        if (text.Equals("stop", StringComparison.Ordinal))
                        {
                            using var stopPath = CreateRoundedPath(
                                new RectangleF(
                                    center.X - (bounds.Width * 0.27f),
                                    center.Y - (bounds.Height * 0.27f),
                                    bounds.Width * 0.54f,
                                    bounds.Height * 0.54f),
                                2.5f * scale);
                            using var stopFill = new SolidBrush(color);
                            graphics.FillPath(stopFill, stopPath);
                        }
                        else
                        {
                            var arcBounds = RectangleF.Inflate(bounds, -bounds.Width * 0.17f, -bounds.Height * 0.17f);
                            graphics.DrawArc(pen, arcBounds, -48, 292);
                            var tip = new PointF(
                                center.X + (bounds.Width * 0.34f),
                                center.Y - (bounds.Height * 0.28f));
                            graphics.DrawLines(
                                pen,
                                [
                                    new PointF(tip.X - (bounds.Width * 0.2f), tip.Y - (bounds.Height * 0.02f)),
                                    tip,
                                    new PointF(tip.X - (bounds.Width * 0.02f), tip.Y + (bounds.Height * 0.2f))
                                ]);
                        }
                    }
                    break;
                case ChromeIconKind.Home:
                    var roof = new PointF(
                        center.X,
                        bounds.Top + (bounds.Height * 0.1f));
                    graphics.DrawLines(
                        pen,
                        [
                            new PointF(bounds.Left + (bounds.Width * 0.13f), center.Y - (bounds.Height * 0.02f)),
                            roof,
                            new PointF(bounds.Right - (bounds.Width * 0.13f), center.Y - (bounds.Height * 0.02f))
                        ]);
                    using (var house = CreateRoundedPath(
                        new RectangleF(
                            bounds.Left + (bounds.Width * 0.22f),
                            center.Y - (bounds.Height * 0.02f),
                            bounds.Width * 0.56f,
                            bounds.Height * 0.5f),
                        2f * scale))
                    {
                        graphics.DrawPath(pen, house);
                    }
                    graphics.DrawLine(
                        pen,
                        center.X,
                        bounds.Bottom - (bounds.Height * 0.02f),
                        center.X,
                        bounds.Bottom - (bounds.Height * 0.25f));
                    break;
                case ChromeIconKind.SiteInfo:
                    DrawSiteInfo(graphics, pen, text, bounds, color, scale);
                    break;
                case ChromeIconKind.Favorite:
                    {
                        var star = CreateStarPoints(center, bounds.Width * 0.44f, bounds.Width * 0.2f);
                        if (text.Equals("filled", StringComparison.Ordinal))
                        {
                            using var starFill = new SolidBrush(color);
                            graphics.FillPolygon(starFill, star);
                        }
                        else
                        {
                            graphics.DrawPolygon(pen, star);
                        }
                    }
                    break;
                case ChromeIconKind.Downloads:
                    graphics.DrawLine(pen, center.X, bounds.Top + (bounds.Height * 0.12f), center.X, bounds.Bottom - (bounds.Height * 0.2f));
                    graphics.DrawLines(
                        pen,
                        [
                            new PointF(center.X - (bounds.Width * 0.2f), center.Y + (bounds.Height * 0.12f)),
                            new PointF(center.X, bounds.Bottom - (bounds.Height * 0.2f)),
                            new PointF(center.X + (bounds.Width * 0.2f), center.Y + (bounds.Height * 0.12f))
                        ]);
                    graphics.DrawLine(pen, bounds.Left + (bounds.Width * 0.15f), bounds.Bottom - (bounds.Height * 0.08f), bounds.Right - (bounds.Width * 0.15f), bounds.Bottom - (bounds.Height * 0.08f));
                    break;
                case ChromeIconKind.Menu:
                    {
                        using var dot = new SolidBrush(color);
                        var dotSize = Math.Max(2f, 2.6f * scale);
                        for (var index = -1; index <= 1; index++)
                        {
                            graphics.FillEllipse(
                                dot,
                                center.X - (dotSize / 2f),
                                center.Y + (index * bounds.Height * 0.25f) - (dotSize / 2f),
                                dotSize,
                                dotSize);
                        }
                    }
                    break;
            }
        }

        private static void DrawNavigationArrow(Graphics graphics, Pen pen, RectangleF bounds, bool forward)
        {
            var centerY = bounds.Top + (bounds.Height / 2f);
            var left = bounds.Left + (bounds.Width * 0.16f);
            var right = bounds.Right - (bounds.Width * 0.16f);
            var headX = forward ? right : left;
            var lineStart = forward ? left : right;
            var lineEnd = forward ? right - (bounds.Width * 0.02f) : left + (bounds.Width * 0.02f);
            graphics.DrawLine(pen, lineStart, centerY, lineEnd, centerY);
            var wing = bounds.Width * 0.24f;
            graphics.DrawLines(
                pen,
                [
                    new PointF(headX, centerY),
                    new PointF(headX + (forward ? -wing : wing), centerY - (bounds.Height * 0.24f)),
                    new PointF(headX, centerY),
                    new PointF(headX + (forward ? -wing : wing), centerY + (bounds.Height * 0.24f))
                ]);
        }

        private static void DrawSiteInfo(
            Graphics graphics,
            Pen pen,
            string text,
            RectangleF bounds,
            Color color,
            float scale)
        {
            var center = new PointF(
                bounds.Left + (bounds.Width / 2f),
                bounds.Top + (bounds.Height / 2f));
            if (text == "warning")
            {
                graphics.DrawLines(
                    pen,
                    [
                        new PointF(center.X, bounds.Top + (bounds.Height * 0.08f)),
                        new PointF(bounds.Right - (bounds.Width * 0.08f), bounds.Bottom - (bounds.Height * 0.1f)),
                        new PointF(bounds.Left + (bounds.Width * 0.08f), bounds.Bottom - (bounds.Height * 0.1f)),
                        new PointF(center.X, bounds.Top + (bounds.Height * 0.08f))
                    ]);
                graphics.DrawLine(pen, center.X, center.Y - (bounds.Height * 0.18f), center.X, center.Y + (bounds.Height * 0.12f));
                using var dot = new SolidBrush(color);
                graphics.FillEllipse(dot, center.X - (1.2f * scale), center.Y + (bounds.Height * 0.22f), 2.4f * scale, 2.4f * scale);
            }
            else if (text == "loading")
            {
                using var dot = new SolidBrush(color);
                var dotSize = Math.Max(2f, 2.5f * scale);
                for (var index = -1; index <= 1; index++)
                {
                    graphics.FillEllipse(dot, center.X + (index * bounds.Width * 0.24f) - (dotSize / 2f), center.Y - (dotSize / 2f), dotSize, dotSize);
                }
            }
            else if (text == "unknown")
            {
                graphics.DrawEllipse(pen, bounds);
                using var questionFont = new Font("Segoe UI Semibold", 9f * scale);
                TextRenderer.DrawText(
                    graphics,
                    "?",
                    questionFont,
                    Rectangle.Round(bounds),
                    color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
            {
                using var ring = new Pen(color, Math.Max(1f, 1.3f * scale));
                graphics.DrawEllipse(ring, bounds);
                using var fill = new SolidBrush(color);
                graphics.FillEllipse(fill, center.X - (2.2f * scale), center.Y - (2.2f * scale), 4.4f * scale, 4.4f * scale);
            }
        }

        private static PointF[] CreateStarPoints(PointF center, float outerRadius, float innerRadius)
        {
            var points = new PointF[10];
            for (var index = 0; index < points.Length; index++)
            {
                var radius = index % 2 == 0 ? outerRadius : innerRadius;
                var angle = (-MathF.PI / 2f) + (index * MathF.PI / 5f);
                points[index] = new PointF(
                    center.X + (MathF.Cos(angle) * radius),
                    center.Y + (MathF.Sin(angle) * radius));
            }
            return points;
        }

        private new float Scale(float value)
        {
            return Math.Max(0.5f, value * DeviceDpi / 96f);
        }

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            var safeRadius = Math.Max(1f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));
            var diameter = safeRadius * 2f;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private enum CaptionGlyph
    {
        Minimize,
        Maximize,
        Restore,
        Close
    }

    private sealed class WindowCaptionButton : Button
    {
        private bool isHovered;
        private bool isPressed;

        public CaptionGlyph Glyph { get; set; }
        public bool HighContrast { get; set; }

        public void SetNonClientState(bool hovered, bool pressed)
        {
            if (isHovered == hovered && isPressed == pressed) return;
            isHovered = hovered;
            isPressed = pressed;
            Invalidate();
        }

        public WindowCaptionButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            isHovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            isHovered = false;
            isPressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) isPressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            isPressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var highlighted = HighContrast && (isPressed || isHovered);
            var background = highlighted
                ? SystemColors.Highlight
                : isPressed ? FlatAppearance.MouseDownBackColor
                : isHovered ? FlatAppearance.MouseOverBackColor
                : BackColor;
            var glyphColor = highlighted ? SystemColors.HighlightText : ForeColor;
            using var brush = new SolidBrush(background);
            e.Graphics.FillRectangle(brush, ClientRectangle);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = DeviceDpi / 96f;
            var glyphSize = Math.Max(9f, 10f * scale);
            var centerX = ClientRectangle.Left + (ClientRectangle.Width / 2f);
            var centerY = ClientRectangle.Top + (ClientRectangle.Height / 2f);
            var left = centerX - (glyphSize / 2f);
            var top = centerY - (glyphSize / 2f);
            using var pen = new Pen(glyphColor, Math.Max(1f, 1.2f * scale))
            {
                StartCap = LineCap.Square,
                EndCap = LineCap.Square
            };

            switch (Glyph)
            {
                case CaptionGlyph.Minimize:
                    e.Graphics.DrawLine(pen, left, centerY + (glyphSize * 0.32f), left + glyphSize, centerY + (glyphSize * 0.32f));
                    break;
                case CaptionGlyph.Maximize:
                    e.Graphics.DrawRectangle(pen, left, top, glyphSize, glyphSize * 0.82f);
                    break;
                case CaptionGlyph.Restore:
                    var offset = Math.Max(2f, 2f * scale);
                    e.Graphics.DrawRectangle(pen, left + offset, top, glyphSize - offset, glyphSize - offset);
                    e.Graphics.DrawRectangle(pen, left, top + offset, glyphSize - offset, glyphSize - offset);
                    break;
                case CaptionGlyph.Close:
                    e.Graphics.DrawLine(pen, left, top, left + glyphSize, top + glyphSize);
                    e.Graphics.DrawLine(pen, left + glyphSize, top, left, top + glyphSize);
                    break;
            }

            if (Focused && ShowFocusCues)
            {
                var focusBounds = Rectangle.Inflate(ClientRectangle, -4, -4);
                ControlPaint.DrawFocusRectangle(
                    e.Graphics,
                    focusBounds,
                    HighContrast ? SystemColors.Highlight : glyphColor,
                    background);
            }
        }
    }

    private sealed class RoundedPanel : Panel
    {
        private bool isFocused;

        public RoundedPanel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            BackColor = OmniboxColor;
        }

        public Color FillColor { get; set; } = OmniboxColor;
        public Color BorderColor { get; set; } = NativeUiTheme.Border;
        public Color FocusBorderColor { get; set; } = OmniboxFocusColor;
        public int CornerRadius { get; set; } = 10;
        public bool HighContrast { get; set; }

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
            if (Width <= 1 || Height <= 1) return;
            using var path = CreateRoundedPath(new Rectangle(0, 0, Width, Height), CornerRadius);
            var previousRegion = Region;
            Region = new Region(path);
            previousRegion?.Dispose();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
            using var fill = new SolidBrush(HighContrast ? SystemColors.Window : FillColor);
            e.Graphics.FillPath(fill, path);
            using var border = new Pen(
                HighContrast ? IsFocused ? SystemColors.Highlight : SystemColors.WindowText : IsFocused ? FocusBorderColor : BorderColor,
                IsFocused ? HighContrast ? 2f : 1.5f : 1f);
            e.Graphics.DrawPath(border, path);
        }

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

    private sealed class TransparentPanelHost : Panel
    {
        public TransparentPanelHost()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor,
                true);
            TrySetBackColor(this, Color.Transparent, SystemColors.Control);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
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
            TrySetBackColor(this, Color.Transparent, SystemColors.Control);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }
    }

    private sealed class AnnouncingStatusLabel : Label
    {
        public void Announce(string message)
        {
            AccessibleName = $"Browser status: {message}";
            AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
            AccessibilityObject.RaiseLiveRegionChanged();
        }
    }

    private sealed class FrameSetupWork(
        BrowserTab tab,
        CoreWebView2Frame frame,
        Func<bool> isCurrent,
        Func<Task> execute)
    {
        public BrowserTab Tab { get; } = tab;
        public CoreWebView2Frame Frame { get; } = frame;
        public Func<bool> IsCurrent { get; private set; } = isCurrent;
        public Func<Task> Execute { get; private set; } = execute;

        public void Replace(Func<bool> replacementIsCurrent, Func<Task> replacementExecute)
        {
            IsCurrent = replacementIsCurrent;
            Execute = replacementExecute;
        }
    }

    internal sealed class FrameEventSubscription : IDisposable
    {
        private readonly CoreWebView2Frame frame;
        private readonly EventHandler<object> destroyedHandler;
        private readonly EventHandler<CoreWebView2NavigationStartingEventArgs> navigationStartingHandler;
        private readonly EventHandler<CoreWebView2DOMContentLoadedEventArgs> domContentLoadedHandler;
        private readonly EventHandler<CoreWebView2FrameCreatedEventArgs> frameCreatedHandler;
        private int attachedCount;

        public FrameEventSubscription(
            CoreWebView2Frame frame,
            EventHandler<object> destroyedHandler,
            EventHandler<CoreWebView2NavigationStartingEventArgs> navigationStartingHandler,
            EventHandler<CoreWebView2DOMContentLoadedEventArgs> domContentLoadedHandler,
            EventHandler<CoreWebView2FrameCreatedEventArgs> frameCreatedHandler)
        {
            this.frame = frame;
            this.destroyedHandler = destroyedHandler;
            this.navigationStartingHandler = navigationStartingHandler;
            this.domContentLoadedHandler = domContentLoadedHandler;
            this.frameCreatedHandler = frameCreatedHandler;
        }

        public void Attach()
        {
            frame.Destroyed += destroyedHandler;
            attachedCount = 1;
            frame.NavigationStarting += navigationStartingHandler;
            attachedCount = 2;
            frame.DOMContentLoaded += domContentLoadedHandler;
            attachedCount = 3;
            frame.FrameCreated += frameCreatedHandler;
            attachedCount = 4;
        }

        public void Dispose()
        {
            var count = Interlocked.Exchange(ref attachedCount, 0);
            if (count >= 4) TryDetach(() => frame.FrameCreated -= frameCreatedHandler);
            if (count >= 3) TryDetach(() => frame.DOMContentLoaded -= domContentLoadedHandler);
            if (count >= 2) TryDetach(() => frame.NavigationStarting -= navigationStartingHandler);
            if (count >= 1) TryDetach(() => frame.Destroyed -= destroyedHandler);
        }

        public void AbandonDestroyedFrame()
        {
            // Never call a CoreWebView2Frame member from its Destroyed event.
            // Dropping this subscription's managed owner is sufficient because
            // the native frame is already tearing down all registrations.
            Interlocked.Exchange(ref attachedCount, 0);
        }

        private static void TryDetach(Action detach)
        {
            try { detach(); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (COMException) { }
        }
    }

    private sealed class BrowserTab : IDisposable
    {
        private bool disposed;
        private readonly Dictionary<CoreWebView2Frame, FrameEventSubscription> frameSubscriptions = [];
        private readonly List<Action> coreEventUnsubscribers = [];

        public BrowserTab(
            Panel host,
            TabHeader header,
            TableLayoutPanel overlay,
            Label overlayTitle,
            Label overlayDetail,
            Button overlayAction,
            string url)
        {
            Host = host;
            Header = header;
            Overlay = overlay;
            OverlayTitle = overlayTitle;
            OverlayDetail = overlayDetail;
            OverlayAction = overlayAction;
            Url = url;
            Title = HostFromUrl(url);
            LastActiveUtc = DateTime.UtcNow;
        }

        public WebView2? View { get; set; }
        public CoreWebView2? Core => View?.CoreWebView2;
        public Panel Host { get; }
        public TabHeader Header { get; }
        public NativeStartPage? StartPageView { get; set; }
        public TableLayoutPanel Overlay { get; }
        public Label OverlayTitle { get; }
        public Label OverlayDetail { get; }
        public Button OverlayAction { get; }
        public string Url { get; set; }
        public string Title { get; set; }
        public string StatusText { get; set; } = "Starting\u2026";
        public string HoverStatus { get; set; } = string.Empty;
        public string LastHoverStatusRaw { get; set; } = string.Empty;
        public string? PendingHoverStatus { get; set; }
        public bool HasPendingHoverStatus { get; set; }
        public DateTime LastActiveUtc { get; set; }
        public bool IsStartPage { get; set; }
        public bool IsPinned { get; set; }
        public bool IsSuspended { get; set; }
        public bool IsDiscarded { get; set; }
        public bool IsLoading { get; set; }
        public bool IsInitializing { get; set; }
        public bool IsSuspending { get; set; }
        public bool IsClosed { get; set; }
        public bool KeepAwake { get; set; }
        public HashSet<string> MicrophoneAllowedOrigins { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CameraAllowedOrigins { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int PendingMediaPermissionRequests { get; set; }
        public string? PendingMediaPermissionOrigin { get; set; }
        public int PendingMediaPermissionRefreshes { get; set; }
        public long DocumentNavigationGeneration { get; set; }
        public bool MicrophoneAccessGranted => MicrophoneAllowedOrigins.Count > 0;
        public bool CameraAccessGranted => CameraAllowedOrigins.Count > 0;
        public bool HasMediaCapturePermission => MicrophoneAccessGranted || CameraAccessGranted;
        public bool HasMediaCapturePermissionForOrigin(string? origin)
        {
            return origin is not null
                && (MicrophoneAllowedOrigins.Contains(origin) || CameraAllowedOrigins.Contains(origin));
        }
        public bool IsMuted { get; set; }
        public bool IsAudible { get; set; }
        public bool ReaderModeActive { get; set; }
        public bool SupportsMemoryUsageTarget { get; set; } = true;
        public CoreWebView2MemoryUsageTargetLevel? MemoryUsageTargetLevel { get; set; }
        public bool CanScriptClose { get; set; }
        public bool IgnoreNextExternalFailure { get; set; }
        public string ExternalNavigationStatus { get; set; } = string.Empty;
        public ConnectionState ConnectionState { get; set; } = ConnectionState.Unknown;
        public int BlockedRequestCount { get; set; }
        public int ActiveDownloads { get; set; }
        public string? ExtensionOriginId { get; set; }
        public string? ChromeStoreInstallChannel { get; set; }
        public string? NoMotionScriptId { get; set; }
        public string? AdBlockPolicyScriptId { get; set; }
        public string? AdBlockScriptId { get; set; }
        public string? AdBlockControlChannel { get; set; }
        public CoreWebView2ContextMenuItem? CleanLinkMenuItem { get; set; }
        public EventHandler<object>? CleanLinkMenuItemSelectedHandler { get; set; }
        public string? ContextLinkTarget { get; set; }
        public int AdBlockScriptGeneration { get; set; }
        public List<CoreWebView2Frame> AdBlockFrames { get; } = [];
        public Dictionary<CoreWebView2Frame, string> FrameOrigins { get; } = [];
        public HashSet<CoreWebView2Frame> FrameSetupInProgress { get; } = [];
        public bool ResourceFilterInstalled { get; set; }
        public bool DeferResourceFilteringUntilPopupAttached { get; set; }
        public string? AllowedPopupBootstrapUrl { get; set; }
        public bool WorkerResourceFilterInstalled { get; set; }
        public EventHandler<CoreWebView2WebResourceRequestedEventArgs>? ResourceRequestHandler { get; set; }
        public int ConsecutiveSuspendFailures { get; set; }
        public long NavigationRequestId { get; set; }
        public long InitializationGeneration { get; set; }
        public long MotionPolicyGeneration { get; set; }
        public string? PendingFileNavigationUrl { get; set; }
        public double ZoomFactor { get; set; } = 1.0;
        public CoreWebView2Find? FindSession { get; set; }
        public EventHandler<object>? FindMatchCountChangedHandler { get; set; }
        public EventHandler<object>? FindActiveMatchIndexChangedHandler { get; set; }
        public long FindRequestGeneration { get; set; }
        public string FindQuery { get; set; } = string.Empty;
        public bool FindUsingNativeApi { get; set; }
        public Func<Task>? RetryAction { get; set; }
        public Task<bool>? InitializationTask { get; set; }

        internal bool TryTrackFrame(CoreWebView2Frame frame, FrameEventSubscription subscription)
        {
            if (disposed || frameSubscriptions.ContainsKey(frame))
            {
                subscription.Dispose();
                return false;
            }

            frameSubscriptions.Add(frame, subscription);
            AdBlockFrames.Add(frame);
            return true;
        }

        internal void ReleaseTrackedFrame(CoreWebView2Frame frame)
        {
            if (frameSubscriptions.Remove(frame, out var subscription))
            {
                subscription.Dispose();
            }
            ReleaseFrameBookkeeping(frame);
        }

        internal void ReleaseDestroyedFrame(CoreWebView2Frame frame)
        {
            if (frameSubscriptions.Remove(frame, out var subscription))
            {
                subscription.AbandonDestroyedFrame();
            }
            ReleaseFrameBookkeeping(frame);
        }

        private void ReleaseFrameBookkeeping(CoreWebView2Frame frame)
        {
            AdBlockFrames.Remove(frame);
            FrameOrigins.Remove(frame);
            FrameSetupInProgress.Remove(frame);
        }

        internal void ReleaseTrackedFrames()
        {
            foreach (var subscription in frameSubscriptions.Values.ToArray())
            {
                subscription.Dispose();
            }
            frameSubscriptions.Clear();
            AdBlockFrames.Clear();
            FrameOrigins.Clear();
            FrameSetupInProgress.Clear();
        }

        internal void TrackCoreEventHandler(Action unsubscribe)
        {
            if (disposed)
            {
                TryUnsubscribe(unsubscribe);
                return;
            }
            coreEventUnsubscribers.Add(unsubscribe);
        }

        internal void DetachCoreEventHandlers()
        {
            foreach (var unsubscribe in coreEventUnsubscribers.ToArray())
            {
                TryUnsubscribe(unsubscribe);
            }
            coreEventUnsubscribers.Clear();
        }

        internal void ReleaseCleanLinkMenuItem()
        {
            var item = CleanLinkMenuItem;
            var handler = CleanLinkMenuItemSelectedHandler;
            CleanLinkMenuItem = null;
            CleanLinkMenuItemSelectedHandler = null;
            if (item is null || handler is null) return;
            TryUnsubscribe(() => item.CustomItemSelected -= handler);
        }

        private static void TryUnsubscribe(Action unsubscribe)
        {
            try { unsubscribe(); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (COMException) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ClearTabHoverStatus(this);

            DetachCoreEventHandlers();
            ReleaseTrackedFrames();
            ReleaseCleanLinkMenuItem();

            // Break references to WebView2 COM wrappers before disposing the
            // WinForms shell. In-flight generation-guarded tasks may still hold
            // this BrowserTab briefly, but they must not retain an old renderer,
            // frame tree, or controller through it.
            var view = View;
            View = null;
            StartPageView = null;
            MicrophoneAllowedOrigins.Clear();
            CameraAllowedOrigins.Clear();
            FindSession = null;
            FindMatchCountChangedHandler = null;
            FindActiveMatchIndexChangedHandler = null;
            ResourceRequestHandler = null;
            RetryAction = null;
            InitializationTask = null;
            NoMotionScriptId = null;
            AdBlockPolicyScriptId = null;
            AdBlockScriptId = null;
            AdBlockControlChannel = null;
            ChromeStoreInstallChannel = null;
            ContextLinkTarget = null;

            if (view is not null)
            {
                Host.Controls.Remove(view);
                view.Dispose();
            }
            Host.Dispose();
            Header.Dispose();
        }
    }

    private sealed class TabDragGhost : Control
    {
        private string title = "New tab";
        private bool highContrast;

        public TabDragGhost()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
            Cursor = Cursors.SizeWE;
            Enabled = false;
            TabStop = false;
            Visible = false;
        }

        public bool HighContrast
        {
            get => highContrast;
            set
            {
                if (highContrast == value) return;
                highContrast = value;
                Invalidate();
            }
        }

        public void SetState(string tabTitle, bool useHighContrast)
        {
            title = string.IsNullOrWhiteSpace(tabTitle) ? "New tab" : tabTitle;
            highContrast = useHighContrast;
            AccessibleName = $"Dragging {title} tab";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 8 || Height < 8) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var shadowBounds = new RectangleF(3, 3, Width - 6, Height - 5);
            using (var shadowPath = CreateRoundedPath(shadowBounds, 9f))
            using (var shadowBrush = new SolidBrush(Color.FromArgb(76, Color.Black)))
            {
                e.Graphics.FillPath(shadowBrush, shadowPath);
            }

            var tabBounds = new RectangleF(1, 1, Width - 5, Height - 5);
            using (var tabPath = CreateRoundedPath(tabBounds, 9f))
            using (var fillBrush = new SolidBrush(highContrast
                ? Color.FromArgb(232, SystemColors.Highlight)
                : Color.FromArgb(224, NativeUiTheme.SurfaceRaised)))
            using (var borderPen = new Pen(
                highContrast ? SystemColors.HighlightText : AccentColor,
                highContrast ? 2f : 1.5f))
            {
                e.Graphics.FillPath(fillBrush, tabPath);
                e.Graphics.DrawPath(borderPen, tabPath);
            }

            var foreground = highContrast ? SystemColors.HighlightText : PageTextColor;
            var textBounds = new Rectangle(12, 1, Math.Max(1, Width - 42), Math.Max(1, Height - 6));
            TextRenderer.DrawText(
                e.Graphics,
                title,
                TabTitleFont,
                textBounds,
                foreground,
                TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPadding
                    | TextFormatFlags.SingleLine
                    | TextFormatFlags.VerticalCenter);

            using var closePen = new Pen(foreground, 1.4f);
            var closeCenterX = Width - 20;
            var closeCenterY = (Height - 3) / 2;
            e.Graphics.DrawLine(closePen, closeCenterX - 3, closeCenterY - 3, closeCenterX + 3, closeCenterY + 3);
            e.Graphics.DrawLine(closePen, closeCenterX + 3, closeCenterY - 3, closeCenterX - 3, closeCenterY + 3);

            using var accentPen = new Pen(highContrast ? SystemColors.HighlightText : AccentColor, 2f);
            e.Graphics.DrawLine(accentPen, 10, Height - 4, Math.Max(10, Width - 12), Height - 4);
        }

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class TabHeader : UserControl
    {
        private readonly Label titleLabel = new();
        private readonly Button closeButton = new();
        private readonly ToolTip toolTip;
        private bool isSelected;
        private bool isHovered;
        private bool isSuspended;
        private bool isLoading;
        private bool isDiscarded;
        private bool keepAwake;
        private bool isAudible;
        private bool isMuted;
        private bool isPinned;
        private bool isDragging;
        private bool pointerTracking;
        private bool dragStarted;
        private Point pointerDownScreenLocation;
        private string currentTitle = "New tab";

        public bool IsPrivateMode { get; set; }

        public TabHeader(ToolTip toolTip)
        {
            this.toolTip = toolTip;
            Height = 32;
            Width = 190;
            Margin = new Padding(0, 0, 4, 0);
            Padding = new Padding(10, 0, 0, 0);
            BackColor = TabIdleColor;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PageTab;

            titleLabel.Dock = DockStyle.Fill;
            titleLabel.Margin = Padding.Empty;
            titleLabel.TextAlign = ContentAlignment.MiddleLeft;
            titleLabel.AutoEllipsis = true;
            titleLabel.Font = TabTitleFont;
            titleLabel.ForeColor = MutedTextColor;
            MainForm.TrySetBackColor(titleLabel, Color.Transparent, TabIdleColor);
            titleLabel.Cursor = Cursors.Hand;

            closeButton.Dock = DockStyle.Right;
            closeButton.Width = 29;
            closeButton.Margin = Padding.Empty;
            closeButton.Padding = Padding.Empty;
            closeButton.Text = "\u00D7";
            closeButton.FlatStyle = FlatStyle.Flat;
            closeButton.FlatAppearance.BorderSize = 0;
            closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(167, 47, 85);
            closeButton.BackColor = TabIdleColor;
            closeButton.ForeColor = MutedTextColor;
            closeButton.Font = TabCloseFont;
            closeButton.Cursor = Cursors.Hand;
            closeButton.TabStop = true;
            closeButton.AccessibleName = "Close tab";

            Controls.Add(titleLabel);
            Controls.Add(closeButton);

            MouseEnter += (_, _) => SetHovered(true);
            MouseLeave += (_, _) => SetHovered(false);
            titleLabel.MouseEnter += (_, _) => SetHovered(true);
            titleLabel.MouseLeave += (_, _) => SetHovered(false);
            closeButton.MouseEnter += (_, _) => SetHovered(true);
            closeButton.MouseLeave += (_, _) => SetHovered(false);

            closeButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
            MouseDown += OnPointerDown;
            titleLabel.MouseDown += OnPointerDown;
            MouseMove += OnPointerMove;
            titleLabel.MouseMove += OnPointerMove;
            MouseUp += OnMouseUp;
            titleLabel.MouseUp += OnMouseUp;
            MouseCaptureChanged += OnMouseCaptureChanged;
            KeyDown += (_, e) =>
            {
                if (e.KeyCode is Keys.Enter or Keys.Space) Activated?.Invoke(this, EventArgs.Empty);
                else if (e.KeyCode == Keys.Delete || (e.Control && e.KeyCode == Keys.W))
                {
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (e.KeyCode is Keys.Left or Keys.Right or Keys.Home or Keys.End)
                {
                    KeyboardNavigationRequested?.Invoke(this, e);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F10 && e.Shift)
                {
                    ContextRequested?.Invoke(
                        this,
                        new TabContextEventArgs(PointToScreen(new Point(Width / 2, Height / 2))));
                    e.Handled = true;
                }
            };
        }

        public event EventHandler? Activated;
        public event EventHandler? CloseRequested;
        public event EventHandler<TabContextEventArgs>? ContextRequested;
        public event EventHandler<TabDragEventArgs>? DragStarted;
        public event EventHandler<TabDragEventArgs>? DragMoved;
        public event EventHandler<TabDragEventArgs>? DragCompleted;
        public event EventHandler? DragCanceled;
        public event EventHandler<KeyEventArgs>? KeyboardNavigationRequested;
        public bool HighContrast { get; set; }

        public void SetDragging(bool dragging)
        {
            if (isDragging == dragging) return;
            isDragging = dragging;
            Cursor = dragging ? Cursors.SizeWE : Cursors.Hand;
            titleLabel.Cursor = dragging ? Cursors.SizeWE : Cursors.Hand;
            ApplyVisualState();
        }

        public void SetState(
            string title,
            bool selected,
            bool suspended,
            bool loading,
            bool discarded,
            bool protectedInBackground,
            bool audible,
            bool muted,
            bool pinned)
        {
            currentTitle = title;
            isSelected = selected;
            isSuspended = suspended;
            isLoading = loading;
            isDiscarded = discarded;
            keepAwake = protectedInBackground;
            isAudible = audible;
            isMuted = muted;
            isPinned = pinned;
            ApplyVisualState();
            AccessibleName = $"{title} tab";
            var stateDescription = selected
                ? "Selected tab"
                : discarded ? "Unloaded tab" : suspended ? "Sleeping tab" : loading ? "Loading tab" : "Tab";
            if (protectedInBackground) stateDescription += ", kept active in background";
            if (audible && !muted) stateDescription += ", playing audio";
            if (muted) stateDescription += ", muted";
            if (pinned) stateDescription += ", pinned";
            AccessibleDescription = stateDescription;
            var tooltip = stateDescription == "Tab" || stateDescription == "Selected tab"
                ? title
                : $"{title}\n{stateDescription}";
            toolTip.SetToolTip(this, tooltip);
            toolTip.SetToolTip(titleLabel, tooltip);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Width <= 1 || Height <= 1) return;
            using var path = new GraphicsPath();
            const int radius = 9;
            const int diameter = radius * 2;
            var bounds = new Rectangle(0, 0, Width, Height);
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            var previousRegion = Region;
            Region = new Region(path);
            previousRegion?.Dispose();
            ApplyVisualState();
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new TabHeaderAccessibleObject(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                toolTip.SetToolTip(this, string.Empty);
                toolTip.SetToolTip(titleLabel, string.Empty);
            }
            base.Dispose(disposing);
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            Invalidate();
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var effectiveAccent = HighContrast
                ? SystemColors.HighlightText
                : IsPrivateMode ? NativeUiTheme.Lavender : AccentColor;
            if (isSelected)
            {
                using var accent = new Pen(effectiveAccent, 2f);
                e.Graphics.DrawLine(accent, 10, Height - 2, Math.Max(10, Width - 10), Height - 2);
            }
            if (isDragging)
            {
                using var dragOutline = new Pen(effectiveAccent, 1f)
                {
                    DashStyle = DashStyle.Dash
                };
                e.Graphics.DrawRectangle(dragOutline, 1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            }

            var hasAudioState = isAudible || isMuted;
            if (hasAudioState)
            {
                var audioColor = HighContrast
                    ? isSelected ? SystemColors.HighlightText : SystemColors.WindowText
                    : isMuted ? Color.FromArgb(244, 198, 132) : PageTextColor;
                DrawAudioGlyph(
                    e.Graphics,
                    new RectangleF(6f, Math.Max(1f, (Height - 16f) / 2f), 16f, 16f),
                    audioColor,
                    isMuted);
            }
            else if (isLoading || isDiscarded || isSuspended || keepAwake)
            {
                var stateColor = HighContrast
                    ? isSelected ? SystemColors.HighlightText : SystemColors.WindowText
                    : isLoading
                        ? AccentColor
                        : isDiscarded ? NegativeColor : keepAwake ? PositiveColor : MutedTextColor;
                using var stateBrush = new SolidBrush(stateColor);
                e.Graphics.FillEllipse(stateBrush, 8, Math.Max(1, (Height - 6) / 2), 6, 6);
            }
            if (Focused)
            {
                ControlPaint.DrawFocusRectangle(
                    e.Graphics,
                    ClientRectangle,
                    HighContrast && isSelected ? SystemColors.HighlightText : ForeColor,
                    BackColor);
            }
        }

        private static void DrawAudioGlyph(
            Graphics graphics,
            RectangleF bounds,
            Color color,
            bool muted)
        {
            var centerY = bounds.Top + (bounds.Height / 2f);
            using var pen = new Pen(color, 1.55f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var speaker = new[]
            {
                new PointF(bounds.Left + 1f, centerY - 2f),
                new PointF(bounds.Left + 4f, centerY - 2f),
                new PointF(bounds.Left + 7f, centerY - 5f),
                new PointF(bounds.Left + 7f, centerY + 5f),
                new PointF(bounds.Left + 4f, centerY + 2f),
                new PointF(bounds.Left + 1f, centerY + 2f)
            };
            graphics.DrawPolygon(pen, speaker);

            if (muted)
            {
                graphics.DrawLine(pen, bounds.Left + 10f, centerY - 3f, bounds.Right - 1f, centerY + 3f);
                graphics.DrawLine(pen, bounds.Right - 1f, centerY - 3f, bounds.Left + 10f, centerY + 3f);
                return;
            }

            graphics.DrawArc(
                pen,
                bounds.Left + 5.5f,
                centerY - 4f,
                6.5f,
                8f,
                -55f,
                110f);
            graphics.DrawArc(
                pen,
                bounds.Left + 6.5f,
                centerY - 6f,
                8.5f,
                12f,
                -55f,
                110f);
        }

        private void OnPointerDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || pointerTracking) return;

            var source = sender as Control ?? this;
            pointerDownScreenLocation = source.PointToScreen(e.Location);
            pointerTracking = true;
            dragStarted = false;
            Capture = true;
        }

        private void OnPointerMove(object? sender, MouseEventArgs e)
        {
            if (!pointerTracking) return;

            var source = sender as Control ?? this;
            var screenLocation = source.PointToScreen(e.Location);
            if (!dragStarted && IsBeyondDragThreshold(screenLocation))
            {
                dragStarted = true;
                DragStarted?.Invoke(this, new TabDragEventArgs(screenLocation));
            }
            if (dragStarted)
            {
                DragMoved?.Invoke(this, new TabDragEventArgs(screenLocation));
            }
        }

        private bool IsBeyondDragThreshold(Point screenLocation)
        {
            var dragSize = SystemInformation.DragSize;
            var dragBounds = new Rectangle(
                pointerDownScreenLocation.X - (dragSize.Width / 2),
                pointerDownScreenLocation.Y - (dragSize.Height / 2),
                dragSize.Width,
                dragSize.Height);
            return !dragBounds.Contains(screenLocation);
        }

        private void OnMouseCaptureChanged(object? sender, EventArgs e)
        {
            if (!pointerTracking) return;

            var wasDragging = dragStarted;
            pointerTracking = false;
            dragStarted = false;
            if (wasDragging) DragCanceled?.Invoke(this, EventArgs.Empty);
        }

        private void OnMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && pointerTracking)
            {
                var source = sender as Control ?? this;
                var screenLocation = source.PointToScreen(e.Location);
                var wasDragging = dragStarted;
                pointerTracking = false;
                dragStarted = false;
                Capture = false;
                if (wasDragging)
                {
                    DragCompleted?.Invoke(this, new TabDragEventArgs(screenLocation));
                }
                else
                {
                    Activated?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (e.Button == MouseButtons.Middle) CloseRequested?.Invoke(this, EventArgs.Empty);
            else if (e.Button == MouseButtons.Right)
            {
                var point = sender is Control control ? control.PointToScreen(e.Location) : PointToScreen(e.Location);
                ContextRequested?.Invoke(this, new TabContextEventArgs(point));
            }
        }

        private void SetHovered(bool hovered)
        {
            if (isHovered == hovered) return;
            isHovered = hovered;
            ApplyVisualState();
        }

        private void ApplyVisualState()
        {
            var highlighted = HighContrast && (isSelected || isHovered);
            var activeBackground = IsPrivateMode ? Color.FromArgb(48, 20, 56) : TabActiveColor;
            var idleBackground = IsPrivateMode ? Color.FromArgb(24, 11, 25) : TabIdleColor;
            var hoverBackground = IsPrivateMode ? Color.FromArgb(38, 16, 44) : NativeUiTheme.Surface;
            var background = HighContrast
                ? highlighted ? SystemColors.Highlight : SystemColors.Control
                : isDragging ? NativeUiTheme.Pressed
                : isSelected ? activeBackground
                : isHovered ? hoverBackground : idleBackground;
            var foreground = HighContrast
                ? highlighted ? SystemColors.HighlightText : SystemColors.ControlText
                : isDragging ? MutedTextColor
                : isSelected ? PageTextColor : MutedTextColor;
            titleLabel.Text = currentTitle;
            titleLabel.ForeColor = HighContrast
                ? foreground
                : isSuspended ? Color.FromArgb(181, 138, 156) : foreground;
            Padding = new Padding(isAudible || isMuted ? 25 : isLoading || isDiscarded || isSuspended || keepAwake ? 20 : 10, 0, 0, 0);
            closeButton.BackColor = background;
            closeButton.ForeColor = foreground;
            closeButton.FlatAppearance.MouseOverBackColor = HighContrast
                ? SystemColors.Highlight
                : Color.FromArgb(167, 47, 85);
            closeButton.Visible = !isDragging && !isPinned && (isSelected || isHovered || Width >= 170);
            BackColor = background;
            Invalidate();
        }

        private sealed class TabHeaderAccessibleObject(TabHeader owner) : ControlAccessibleObject(owner)
        {
            public override AccessibleRole Role => AccessibleRole.PageTab;

            public override AccessibleStates State => base.State
                | AccessibleStates.Selectable
                | (owner.isSelected ? AccessibleStates.Selected : AccessibleStates.None);
        }
    }

    private sealed class TabContextEventArgs(Point screenLocation) : EventArgs
    {
        public Point ScreenLocation { get; } = screenLocation;
    }

    private sealed class TabDragEventArgs(Point screenLocation) : EventArgs
    {
        public Point ScreenLocation { get; } = screenLocation;
    }

    private sealed class PermissionRequest(
        BrowserTab tab,
        CoreWebView2 core,
        long documentGeneration,
        CoreWebView2PermissionRequestedEventArgs args,
        CoreWebView2Deferral deferral,
        string origin,
        string permission,
        CoreWebView2PermissionKind permissionKind,
        bool isUserInitiated)
    {
        public BrowserTab Tab { get; } = tab;
        public CoreWebView2 Core { get; } = core;
        public long DocumentGeneration { get; } = documentGeneration;
        public CoreWebView2PermissionRequestedEventArgs Args { get; } = args;
        public CoreWebView2Deferral Deferral { get; } = deferral;
        public string Origin { get; } = origin;
        public string Permission { get; } = permission;
        public CoreWebView2PermissionKind PermissionKind { get; } = permissionKind;
        public bool IsUserInitiated { get; } = isUserInitiated;
        private int completionStarted;

        public bool TryBeginCompletion() => Interlocked.Exchange(ref completionStarted, 1) == 0;
    }

    private sealed class DownloadEntry(
        string fileName,
        string filePath,
        string sourceUrl,
        DateTime startedAt,
        string state)
    {
        public string FileName { get; set; } = fileName;
        public string FilePath { get; set; } = filePath;
        public string SourceUrl { get; } = sourceUrl;
        public DateTime StartedAt { get; } = startedAt;
        public string State { get; set; } = state;
        public long BytesReceived { get; set; }
        public long TotalBytesToReceive { get; set; }
        public bool IsTerminal { get; set; }
        public Action? CancelAction { get; set; }
        public Action? PauseAction { get; set; }
        public Action? ResumeAction { get; set; }
        public bool CanResume { get; set; }
        public string EstimatedEndTimeText { get; set; } = string.Empty;
        public string InterruptReason { get; set; } = string.Empty;
        public CoreWebView2DownloadOperation? Operation { get; set; }
        public EventHandler<object>? StateChangedHandler { get; set; }
        public EventHandler<object>? BytesReceivedChangedHandler { get; set; }
        public EventHandler<object>? EstimatedEndTimeChangedHandler { get; set; }
    }

    private static class NativeMethods
    {
        public const int WsCaption = 0x00C00000;
        public const int WsThickFrame = 0x00040000;
        public const int WsSysMenu = 0x00080000;
        public const int WsMinimizeBox = 0x00020000;
        public const int WsMaximizeBox = 0x00010000;
        public const int WsExDlgModalFrame = 0x00000001;
        public const int WsExWindowEdge = 0x00000100;
        public const int WsExClientEdge = 0x00000200;
        public const int WmGetMinMaxInfo = 0x0024;
        public const int WmNcMouseMove = 0x00A0;
        public const int WmNcLeftButtonDown = 0x00A1;
        public const int WmNcLeftButtonUp = 0x00A2;
        public const int WmNcCalcSize = 0x0083;
        public const int WmNcHitTest = 0x0084;
        public const int WmNcActivate = 0x0086;
        public const int WmMouseMove = 0x0200;
        public const int WmLeftButtonUp = 0x0202;
        public const int WmCaptureChanged = 0x0215;
        public const int WmNcMouseLeave = 0x02A2;
        public const int HtClient = 1;
        public const int HtCaption = 2;
        public const int HtLeft = 10;
        public const int HtRight = 11;
        public const int HtTop = 12;
        public const int HtTopLeft = 13;
        public const int HtTopRight = 14;
        public const int HtBottom = 15;
        public const int HtBottomLeft = 16;
        public const int HtBottomRight = 17;
        public const int HtMaxButton = 9;
        public const int MonitorDefaultToNearest = 2;

        private const int SmCxSizeFrame = 32;
        private const int SmCySizeFrame = 33;
        private const int SmCxPaddedBorder = 92;
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;
        private const int TmeLeave = 0x00000002;
        private const int TmeNonClient = 0x00000010;

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int index, uint dpi);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCapture(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessage(
            IntPtr window,
            int message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "TrackMouseEvent")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TrackMouseEventNative(ref TrackMouseEventInfo eventInfo);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public static Point PointFromLParam(IntPtr value)
        {
            var packed = value.ToInt64();
            return new Point(unchecked((short)(packed & 0xFFFF)), unchecked((short)((packed >> 16) & 0xFFFF)));
        }

        public static int GetResizeFrameThickness(int dpi, bool horizontal)
        {
            try
            {
                var frameMetric = horizontal ? SmCxSizeFrame : SmCySizeFrame;
                return Math.Max(4, GetSystemMetricsForDpi(frameMetric, (uint)dpi)
                    + GetSystemMetricsForDpi(SmCxPaddedBorder, (uint)dpi));
            }
            catch (EntryPointNotFoundException)
            {
                var frameMetric = horizontal ? SmCxSizeFrame : SmCySizeFrame;
                return Math.Max(4, GetSystemMetrics(frameMetric) + GetSystemMetrics(SmCxPaddedBorder));
            }
        }

        public static void CaptureMouse(IntPtr window)
        {
            _ = SetCapture(window);
        }

        public static void ReleaseMouseCapture()
        {
            _ = ReleaseCapture();
        }

        public static void BeginWindowMove(IntPtr window)
        {
            _ = ReleaseCapture();
            _ = SendMessage(window, WmNcLeftButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }

        public static void TrackNonClientMouseLeave(IntPtr window)
        {
            var eventInfo = new TrackMouseEventInfo
            {
                Size = Marshal.SizeOf<TrackMouseEventInfo>(),
                Flags = TmeLeave | TmeNonClient,
                TrackWindow = window
            };
            _ = TrackMouseEventNative(ref eventInfo);
        }

        public static void ApplyBorderlessResizableStyle(IntPtr window)
        {
            var style = GetWindowLongPtr(window, GwlStyle).ToInt64();
            style &= ~WsCaption;
            style |= WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox;
            _ = SetWindowLongPtr(window, GwlStyle, (IntPtr)style);

            var extendedStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
            extendedStyle &= ~(WsExClientEdge | WsExWindowEdge | WsExDlgModalFrame);
            _ = SetWindowLongPtr(window, GwlExStyle, (IntPtr)extendedStyle);
            _ = SetWindowPos(
                window,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }

        public static void TryEnableModernWindowFrame(
            IntPtr window,
            Color captionColor,
            Color borderColor,
            Color textColor)
        {
            try
            {
                var darkMode = 1;
                _ = DwmSetWindowAttribute(window, 20, ref darkMode, sizeof(int));
                var roundedCorners = 2;
                _ = DwmSetWindowAttribute(window, 33, ref roundedCorners, sizeof(int));
                var border = ToColorRef(borderColor);
                _ = DwmSetWindowAttribute(window, 34, ref border, sizeof(int));
                var caption = ToColorRef(captionColor);
                _ = DwmSetWindowAttribute(window, 35, ref caption, sizeof(int));
                var text = ToColorRef(textColor);
                _ = DwmSetWindowAttribute(window, 36, ref text, sizeof(int));
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            {
                // DWM is unavailable only on unsupported legacy Windows configurations.
            }
        }

        private static int ToColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrackMouseEventInfo
        {
            public int Size;
            public int Flags;
            public IntPtr TrackWindow;
            public int HoverTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MonitorInfo
        {
            public int Size;
            public NativeRect MonitorArea;
            public NativeRect WorkArea;
            public int Flags;
        }
    }

    private enum ConnectionState
    {
        Unknown,
        Loading,
        Local,
        LocalFile,
        Secure,
        Insecure,
        CertificateError,
        Failed
    }

    private sealed class IncognitoBadgeControl : Control
    {
        private readonly ToolTip toolTip;
        private bool isHovered;

        public IncognitoBadgeControl(ToolTip toolTip)
        {
            this.toolTip = toolTip;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);
            TabStop = false;
            Size = new Size(82, 26);
            Margin = new Padding(2, 3, 2, 3);
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.StaticText;
            AccessibleName = "Incognito browsing";
            AccessibleDescription = "No browsing history, cookies, or site data will be saved.";
            toolTip.SetToolTip(this, "Incognito mode: browsing history, cookies, and site data are deleted when all incognito windows close.");
            MouseEnter += (_, _) => { isHovered = true; Invalidate(); };
            MouseLeave += (_, _) => { isHovered = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = CreateRoundedRectanglePath(bounds, 7);
            var backColor = isHovered ? Color.FromArgb(56, 24, 66) : Color.FromArgb(42, 18, 50);
            using var brush = new SolidBrush(backColor);
            using var borderPen = new Pen(isHovered ? NativeUiTheme.Lavender : Color.FromArgb(120, 60, 148), 1f);
            g.FillPath(brush, path);
            g.DrawPath(borderPen, path);

            // Draw stealth glasses icon
            using var lensPen = new Pen(NativeUiTheme.Lavender, 1.4f);
            var midY = Height / 2;
            g.DrawEllipse(lensPen, 8, midY - 4, 7, 7);
            g.DrawEllipse(lensPen, 17, midY - 4, 7, 7);
            g.DrawLine(lensPen, 14, midY - 2, 18, midY - 2);

            // Draw "Incognito" text
            using var font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Regular);
            var textBounds = new Rectangle(27, 0, Width - 29, Height);
            TextRenderer.DrawText(
                g,
                "Incognito",
                font,
                textBounds,
                NativeUiTheme.Lavender,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }

        private static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;
            var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}

internal sealed class IncognitoShortcutMessageFilter : IMessageFilter
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var key = (Keys)(int)m.WParam;
            if (key == Keys.N && (Control.ModifierKeys & (Keys.Control | Keys.Shift)) == (Keys.Control | Keys.Shift))
            {
                if (Form.ActiveForm is MainForm activeWindow && !activeWindow.IsDisposed)
                {
                    activeWindow.OpenPrivateWindow();
                    return true;
                }
                if (Application.OpenForms.OfType<MainForm>().FirstOrDefault(w => !w.IsDisposed) is { } anyWindow)
                {
                    anyWindow.OpenPrivateWindow();
                    return true;
                }
            }
        }
        return false;
    }
}
