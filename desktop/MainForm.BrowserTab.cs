using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MishaWeb;

public sealed partial class MainForm
{
    private sealed class BrowserTab : IDisposable
    {
        private const int MaximumRecentBlockedHosts = 20;
        private static int nextBrowserTabId;
        private bool disposed;
        private readonly Dictionary<CoreWebView2Frame, FrameEventSubscription> frameSubscriptions = [];
        private readonly List<Action> coreEventUnsubscribers = [];
        private readonly List<string> recentBlockedHosts = [];

        public int Id { get; } = Interlocked.Increment(ref nextBrowserTabId);

        public BrowserTab(
            Panel host,
            TabHeader header,
            TableLayoutPanel overlay,
            RoundedPanel overlayCard,
            Label overlayTitle,
            Label overlayHost,
            Label overlayDetail,
            Button overlayAction,
            string url)
        {
            Host = host;
            Header = header;
            Overlay = overlay;
            OverlayCard = overlayCard;
            OverlayTitle = overlayTitle;
            OverlayHost = overlayHost;
            OverlayDetail = overlayDetail;
            OverlayAction = overlayAction;
            Url = url;
            Title = HostFromUrl(url);
            LastActiveUtc = DateTime.UtcNow;
        }

        public WebView2? View { get; set; }
        public CoreWebView2? Core
        {
            get
            {
                try
                {
                    return View?.CoreWebView2;
                }
                catch (Exception error) when (
                    error is InvalidOperationException
                        or COMException
                        or ObjectDisposedException)
                {
                    return null;
                }
            }
        }
        public Panel Host { get; }
        public TabHeader Header { get; }
        public NativeStartPage? StartPageView { get; set; }
        public TableLayoutPanel Overlay { get; }
        public RoundedPanel OverlayCard { get; }
        public Label OverlayTitle { get; }
        public Label OverlayHost { get; }
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
        public long BackgroundReductionGeneration { get; set; }
        public bool MicrophoneAccessGranted => MicrophoneAllowedOrigins.Count > 0;
        public bool CameraAccessGranted => CameraAllowedOrigins.Count > 0;
        public bool HasMediaCapturePermission => MicrophoneAccessGranted || CameraAccessGranted;
        public bool HasMediaCapturePermissionForOrigin(string? origin)
        {
            return origin is not null
                && (MicrophoneAllowedOrigins.Contains(origin) || CameraAllowedOrigins.Contains(origin));
        }
        public bool IsMuted { get; set; }
        public bool? TabMuteOverride { get; set; }
        public bool IsAudible { get; set; }
        public bool ReaderModeActive { get; set; }
        public int SavedScrollY { get; set; }
        public int PendingRestoreScrollY { get; set; }
        public string? LoadedFaviconHostKey { get; set; }
        public int LoadedFaviconVersion { get; set; }
        public bool SupportsMemoryUsageTarget { get; set; } = true;
        public CoreWebView2MemoryUsageTargetLevel? MemoryUsageTargetLevel { get; set; }
        public bool CanScriptClose { get; set; }
        public bool IgnoreNextExternalFailure { get; set; }
        public string ExternalNavigationStatus { get; set; } = string.Empty;
        public ConnectionState ConnectionState { get; set; } = ConnectionState.Unknown;
        public int BlockedRequestCount { get; set; }
        public int BlockedAdCount { get; private set; }
        public int BlockedTrackerCount { get; private set; }
        public int BlockedScriptCount { get; private set; }
        public int BlockedOtherCount { get; private set; }
        public IReadOnlyList<string> RecentBlockedHosts => recentBlockedHosts;
        public int ActiveDownloads { get; set; }
        public string? ExtensionOriginId { get; set; }
        public string? ChromeStoreInstallChannel { get; set; }
        public string? ChromeStoreBridgeScriptId { get; set; }
        public string? PendingScopedScriptNavigationUrl { get; set; }
        public string? LastScopedScriptRegistrationUrl { get; set; }
        public int LastScopedScriptAdBlockGeneration { get; set; }
        public bool? AppliedAutoDarkOverride { get; set; }
        public bool? AppliedPreferredColorSchemeDark { get; set; }
        public long AppliedWebsiteThemeGeneration { get; set; } = -1;
        public HashSet<string> InstalledUserScriptKeys { get; } = new(StringComparer.Ordinal);
        public string? NoMotionScriptId { get; set; }
        public string? AdBlockPolicyScriptId { get; set; }
        public string? AdBlockScriptId { get; set; }
        public string? YouTubeDocumentScriptId { get; set; }
        public string? AdBlockControlChannel { get; set; }
        public CoreWebView2ContextMenuItem? CleanLinkMenuItem { get; set; }
        public EventHandler<object>? CleanLinkMenuItemSelectedHandler { get; set; }
        public CoreWebView2ContextMenuItem? SplitViewLinkMenuItem { get; set; }
        public EventHandler<object>? SplitViewLinkMenuItemSelectedHandler { get; set; }
        public string? ContextLinkTarget { get; set; }
        public int AdBlockScriptGeneration { get; set; }
        public List<CoreWebView2Frame> AdBlockFrames { get; } = [];
        /// <summary>
        /// Document URLs known to belong to a subframe of the current document.
        /// WebView2 reports an iframe navigation with
        /// <c>RequestedSourceKind.Document</c>, the same value as the top-level
        /// page, so that flag cannot identify a subframe. These are recorded from
        /// each frame's own NavigationStarting event instead. Cleared on every
        /// top-level navigation and bounded, so it cannot grow with session length.
        /// </summary>
        public AdBlockSubframeState AdBlockSubframeUrls { get; } = new();
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
        public bool FindCaseSensitive { get; set; }
        public bool FindUsingNativeApi { get; set; }
        public Func<Task>? RetryAction { get; set; }
        public Task<bool>? InitializationTask { get; set; }

        internal void RecordBlockedAttempt(AdBlockMatchResult evaluation)
        {
            if (!evaluation.Blocked) return;
            RecordBlockedHost(evaluation.Host, evaluation.Category);
        }

        internal void RecordBlockedHost(string? host, string category)
        {
            BlockedRequestCount++;
            if (string.Equals(category, "Ad", StringComparison.OrdinalIgnoreCase))
            {
                BlockedAdCount++;
            }
            else if (string.Equals(category, "Tracker", StringComparison.OrdinalIgnoreCase))
            {
                BlockedTrackerCount++;
            }
            else if (string.Equals(category, "Script", StringComparison.OrdinalIgnoreCase))
            {
                BlockedScriptCount++;
            }
            else
            {
                BlockedOtherCount++;
            }

            if (string.IsNullOrWhiteSpace(host)) return;
            var normalized = host.Trim();
            for (var index = 0; index < recentBlockedHosts.Count; index++)
            {
                if (string.Equals(recentBlockedHosts[index], normalized, StringComparison.OrdinalIgnoreCase))
                {
                    if (index == 0) return;
                    recentBlockedHosts.RemoveAt(index);
                    recentBlockedHosts.Insert(0, normalized);
                    return;
                }
            }

            if (recentBlockedHosts.Count >= MaximumRecentBlockedHosts)
            {
                recentBlockedHosts.RemoveAt(recentBlockedHosts.Count - 1);
            }
            recentBlockedHosts.Insert(0, normalized);
        }

        internal void ResetBlockedStats()
        {
            BlockedRequestCount = 0;
            BlockedAdCount = 0;
            BlockedTrackerCount = 0;
            BlockedScriptCount = 0;
            BlockedOtherCount = 0;
            recentBlockedHosts.Clear();
        }

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
            if (item is not null && handler is not null)
            {
                TryUnsubscribe(() => item.CustomItemSelected -= handler);
            }

            var splitItem = SplitViewLinkMenuItem;
            var splitHandler = SplitViewLinkMenuItemSelectedHandler;
            SplitViewLinkMenuItem = null;
            SplitViewLinkMenuItemSelectedHandler = null;
            if (splitItem is not null && splitHandler is not null)
            {
                TryUnsubscribe(() => splitItem.CustomItemSelected -= splitHandler);
            }
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
            YouTubeDocumentScriptId = null;
            AdBlockControlChannel = null;
            ChromeStoreInstallChannel = null;
            ChromeStoreBridgeScriptId = null;
            PendingScopedScriptNavigationUrl = null;
            LastScopedScriptRegistrationUrl = null;
            AppliedAutoDarkOverride = null;
            AppliedPreferredColorSchemeDark = null;
            AppliedWebsiteThemeGeneration = -1;
            InstalledUserScriptKeys.Clear();
            ContextLinkTarget = null;
            LoadedFaviconHostKey = null;
            recentBlockedHosts.Clear();

            if (view is not null)
            {
                Host.Controls.Remove(view);
                view.Dispose();
            }
            Host.Dispose();
            Header.Dispose();
        }
    }

}
