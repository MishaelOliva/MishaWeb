using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime;
using System.Text;
using System.Text.RegularExpressions;

namespace MishaWeb;

internal enum AdBlockResourceType
{
    Document,
    SubDocument,
    Script,
    Image,
    Stylesheet,
    Media,
    Font,
    XmlHttpRequest,
    Fetch,
    WebSocket,
    Ping,
    Other
}

internal readonly record struct AdBlockCompileDiagnostics(
    int UniqueLines,
    int NetworkRules,
    int CosmeticRules,
    int HideDisableRules,
    int DisabledRules,
    int UnsupportedRuleCandidates,
    int CapacityDroppedNetworkRules,
    int RegexRules,
    int IndexedRegexRules,
    int UnindexedSourceScopedRegexRules,
    int UnindexedGenericRegexRules,
    int DroppedRegexRules);

internal readonly record struct AdBlockMatchResult(
    bool Blocked,
    string Category,
    string Host,
    string RuleKind);

/// <summary>
/// A bounded ABP/uBlock-compatible network and standard cosmetic filter engine.
///
/// The rule syntax is intentionally kept in the same family as Brave's
/// adblock-rust engine. EasyList, EasyPrivacy, and curated uBO/Brave sources
/// are loaded into a last-known-good cache, while fallback rules protect the
/// first navigation and offline starts. Unsupported response-rewrite syntax is
/// rejected rather than accidentally broadened into a blocking rule.
/// </summary>
internal sealed class AdBlockEngine
{
    private enum FilterSourceCategory : byte
    {
        Unknown = 0,
        Fallback = 1,
        Ad = 2,
        Privacy = 3,
        ScriptAbuse = 4,
        Unbreak = 5
    }

    private static readonly FilterListSource[] FilterListSources = BuildFilterListSources();

    private static FilterListSource[] BuildFilterListSources()
    {
        var sources = new List<FilterListSource>(24)
        {
            new("list-0.txt", new("https://easylist.to/easylist/easylist.txt"), FilterSourceCategory.Ad),
            new("list-1.txt", new("https://easylist.to/easylist/easyprivacy.txt"), FilterSourceCategory.Privacy),
            new("list-2.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters.txt"), FilterSourceCategory.Ad),
            new("list-3.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-general.txt"), FilterSourceCategory.Ad),
            new("list-4.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2026.txt"), FilterSourceCategory.Ad),
            new("list-5.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/quick-fixes.txt"), FilterSourceCategory.Ad),
            new("list-6.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/privacy.txt"), FilterSourceCategory.Privacy),
            new("list-7.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/unbreak.txt"), FilterSourceCategory.Unbreak),
            new("list-8.txt", new("https://raw.githubusercontent.com/brave/adblock-lists/master/brave-lists/brave-specific.txt"), FilterSourceCategory.Ad),
            new("list-9.txt", new("https://raw.githubusercontent.com/brave/adblock-lists/master/brave-lists/brave-firstparty.txt"), FilterSourceCategory.Ad),
            new("list-10.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2020.txt"), FilterSourceCategory.Ad),
            new("list-11.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2021.txt"), FilterSourceCategory.Ad),
            new("list-12.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2022.txt"), FilterSourceCategory.Ad),
            new("list-13.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2023.txt"), FilterSourceCategory.Ad),
            new("list-14.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2024.txt"), FilterSourceCategory.Ad),
            new("list-15.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2025.txt"), FilterSourceCategory.Ad),
            new("list-16.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/ubo-link-shorteners.txt"), FilterSourceCategory.Privacy),
            new("list-17.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/resource-abuse.txt"), FilterSourceCategory.ScriptAbuse),
            new("list-18.txt", new("https://raw.githubusercontent.com/brave/adblock-lists/master/brave-unbreak.txt"), FilterSourceCategory.Unbreak),
            new("list-19.txt", new("https://raw.githubusercontent.com/brave/adblock-lists/master/brave-lists/brave-firstparty-regional.txt"), FilterSourceCategory.Ad)
        };

        var currentYear = DateTime.UtcNow.Year;
        for (var year = 2027; year <= currentYear && year <= 2035; year++)
        {
            var nextIndex = sources.Count;
            sources.Add(new(
                $"list-{nextIndex}.txt",
                new($"https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-{year}.txt"),
                FilterSourceCategory.Ad));
        }

        return sources.ToArray();
    }

    private static readonly HttpClient HttpClient = CreateHttpClient();
    // WebView2 can report the same request and source strings through several
    // policy hooks. Small thread-local caches avoid reparsing them without
    // introducing cross-thread locks or allowing unbounded URL retention.
    private const int MaximumCachedRequestUrisPerThread = 64;
    // The source URL is re-derived per policy hook and varies with the active
    // tab, so a 16-entry cache was exhausted by a handful of requests and threw
    // the whole dictionary away on the next miss.
    private const int MaximumCachedSourceUrisPerThread = 64;
    private const int MaximumCachedHostSuffixSetsPerThread = 64;
    private const int MaximumCachedSiteKeysPerThread = 64;
    [ThreadStatic] private static Dictionary<string, Uri?>? cachedRequestUris;
    [ThreadStatic] private static Dictionary<string, Uri?>? cachedSourceUris;
    [ThreadStatic] private static Dictionary<string, string[]>? cachedHostSuffixSets;
    [ThreadStatic] private static Dictionary<string, string>? cachedSiteKeys;
    private static readonly TimeSpan FilterCacheLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan FilterRefreshRetryDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RuleUnloadGracePeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FilterDownloadTimeout = TimeSpan.FromSeconds(12);
    private const int MaximumFilterListBytes = 16 * 1024 * 1024;
    private const long MaximumAggregateFilterBytes = 48L * 1024 * 1024;
    private const int MaximumInputFilterLines = 500_000;
    private const int MaximumInputLinesPerSource = 100_000;
    private const int MinimumReservedLinesPerLaterSource = 5_000;
    private const int MaximumFilterLineCharacters = 64 * 1024;
    private const int MaximumHttpCacheMetadataBytes = 4 * 1024;
    private const int MaximumEntityTagCharacters = 1024;
    private const int MaximumConcurrentFilterDownloads = 3;
    private static readonly string[] FallbackRules =
    [
        "||adform.net^",
        "||adnxs.com^",
        "||adsafeprotected.com^",
        "||adservice.google.com^",
        "||googleads.g.doubleclick.net^",
        "||static.doubleclick.net^",
        "||s0.2mdn.net^",
        "||adsrvr.org^",
        "||amazon-adsystem.com^",
        "||doubleclick.net^",
        "||googleadservices.com^",
        "||googlesyndication.com^",
        "||moatads.com^",
        "||smartadserver.com^",
        "||criteo.com^",
        "||criteo.net^",
        "||openx.net^",
        "||rubiconproject.com^",
        "||pubmatic.com^",
        "||taboola.com^",
        "||outbrain.com^",
        "||media.net^",
        "||exoclick.com^",
        "||trafficjunky.net^",
        "||propellerads.com^",
        "||mgid.com^",
        "||quantserve.com^",
        "||scorecardresearch.com^",
        "||zedo.com^",
        "||adroll.com^",
        "||adskeeper.com^",
        "||adskeeper.co.uk^",
        "||adsterra.com^",
        "||hilltopads.net^",
        "||popads.net^",
        "||popcash.net^",
        "||adcash.com^",
        "||clickadu.com^",
        "||clickaine.com^",
        "||onclickmax.com^",
        "||onclicktop.com^",
        "||traffichunt.com^",
        "||juicyads.com^",
        "||adfoc.us^",
        "||ad-maven.com^",
        "||admaven.com^",
        "||pushground.com^",
        "||notifadz.com^",
        "||adnium.com^",
        "||adnami.io^",
        "||ad-delivery.net^",
        "||adkernel.com^",
        "||adop.cc^",
        "||adf.ly^",
        "||go2cloud.org^",
        "||linkshrink.net^",
        "||shorte.st^",
        "||ouo.io^",
        "||ouo.press^",
        "||linkvertise.com^",
        "||bc.vc^",
        "||cuty.io^",
        "||luugy.com^",
        "||bet88.ph^",
        "||winzir.ph^",
        "||lucky88.com^",
        "||1xbet.com^",
        "||188bet.com^",
        "||bet365.com^",
        "||betway.com^",
        "||stake.com^",
        "||casino.org^",
        "||banner^",
        "||popunder^",
        "||pop-up^",
        "||popup^",
        "||adserver^",
        "||advertising^",
        "||advertisement^",
        "||adsbygoogle.com^",
        "||pagead2.googlesyndication.com^",

        // First-party YouTube endpoints must be available before remote lists
        // finish loading. Do not block googlevideo playback or the player API.
        "||youtube.com/pagead/",
        "||youtube.com/youtubei/v1/player/ad_break",
        "||youtube.com/api/stats/ads?",
        "||youtube.com/pcs/activeview?",
        "||www.youtube.com/get_midroll_",
        "||m.youtube.com/get_midroll_",
        "||youtube.com/get_video_info?*=adunit&"
    ];

    // Built-in safety-net rules protect subresources and ad frames, but they
    // must not turn a typed top-level visit into strict blocking. Remote lists
    // and caller-supplied rules retain their authored document semantics.
    private static readonly string[] BuiltInFallbackRules =
        FallbackRules.Select(ExcludeTopLevelDocument).ToArray();

    // Keep popup/navigation heuristics deliberately narrower than network
    // filtering. Ad networks and gambling sites can still be legitimate typed
    // destinations; these entries are dedicated redirect/shortener surfaces.
    private static readonly string[] KnownRedirectHosts =
    [
        "adf.ly", "adfoc.us", "bc.vc", "cuty.io", "go2cloud.org",
        "linkshrink.net", "linkvertise.com", "luugy.com", "ouo.io",
        "ouo.press", "popads.net", "popcash.net", "shorte.st"
    ];

    public static AdBlockEngine Shared { get; } = new();

    private readonly string cacheFolder;
    private readonly bool updateRemoteLists;
    private readonly object loadSync = new();
    private readonly object cacheWriteSync = new();
    private readonly CompiledRuleSet baselineRuleSet;
    private volatile CompiledRuleSet ruleSet;
    private int liveConsumers;
    private int loadGeneration;
    private Task? loadTask;
    private CancellationTokenSource? loadCancellation;
    private System.Threading.Timer? unloadTimer;
    private DateTime lastRefreshCheckUtc;

    public AdBlockEngine(
        string? cacheFolder = null,
        IEnumerable<string>? initialRules = null,
        bool updateRemoteLists = true)
    {
        this.cacheFolder = cacheFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MishaWeb",
            "Filters");
        this.updateRemoteLists = updateRemoteLists;
        var taggedBaseline = BuiltInFallbackRules
            .Select(static line => (line, FilterSourceCategory.Fallback));
        var taggedInitial = CompiledRuleSet
            .PreprocessLines(initialRules ?? Enumerable.Empty<string>())
            .Select(static line => (line, FilterSourceCategory.Unknown));
        baselineRuleSet = CompiledRuleSet.FromTaggedLines(taggedBaseline.Concat(taggedInitial));
        ruleSet = baselineRuleSet;
    }

    public static string DocumentScript => AdBlockDocumentScript.Source;

    public static string CreateDocumentScript(string controlChannel) =>
        AdBlockDocumentScript.Source.Replace(
            "__MISHA_ADBLOCK_CONTROL_CHANNEL__",
            controlChannel,
            StringComparison.Ordinal);

    internal static string CreateUniversalBootstrapScript(string controlChannel) =>
        AdBlockDocumentScript.UniversalBootstrapSource.Replace(
            "__MISHA_ADBLOCK_CONTROL_CHANNEL__",
            controlChannel,
            StringComparison.Ordinal);

    internal static string CreateYouTubeDocumentScript() =>
        AdBlockDocumentScript.YouTubeRuntimeSource;

    internal static string CreateYouTubeDocumentScript(string controlChannel) =>
        string.IsNullOrEmpty(controlChannel)
            ? AdBlockDocumentScript.YouTubeRuntimeSource
            : AdBlockDocumentScript.YouTubeRuntimeSource.Replace(
                "__MISHA_ADBLOCK_CONTROL_CHANNEL__",
                controlChannel,
                StringComparison.Ordinal);

    private volatile bool isReady;

    public bool IsReady => isReady;

    internal AdBlockCompileDiagnostics LastCompileDiagnostics => ruleSet.Diagnostics;

    internal static int ConfiguredFilterSourceCountForTesting => FilterListSources.Length;

    internal static bool TryGetRegexPatternForTesting(string line, out string pattern) =>
        FilterRule.TryGetRegexPattern(line, out pattern);

    internal static string GetRuleIdentityForTesting(string line) =>
        CompiledRuleSet.GetRuleIdentity(line);

    internal static IReadOnlyList<string> SplitFilterOptionsForTesting(string optionText) =>
        FilterRule.SplitFilterOptions(optionText);

    internal static string? GetBadFilterTargetForTesting(string line) =>
        CompiledRuleSet.GetBadFilterTarget(line);

    internal static bool IsRecognizedBareOptionForTesting(string token) =>
        FilterRule.IsRecognizedBareOption(token);

    internal static IReadOnlyCollection<string> ValuelessOptionFlagsForTesting =>
        FilterRule.ValuelessOptionFlags;



    internal static bool TryExtractMandatoryRegexLiteralForTesting(
        string pattern,
        out string literal) =>
        FilterRule.TryExtractMandatoryRegexLiteral(pattern, out literal);

    internal static bool IsSupportedRegexRuleForTesting(string line) =>
        FilterRule.Parse(line)?.IsRegex == true;

    public void AcquireConsumer()
    {
        Interlocked.Increment(ref liveConsumers);
        lock (loadSync)
        {
            unloadTimer?.Dispose();
            unloadTimer = null;
        }
    }

    public void ReleaseConsumer()
    {
        while (true)
        {
            var current = Volatile.Read(ref liveConsumers);
            if (current <= 0) return;
            if (Interlocked.CompareExchange(ref liveConsumers, current - 1, current) != current) continue;
            if (current == 1) ScheduleRuleUnload();
            return;
        }
    }

    private void ScheduleRuleUnload()
    {
        lock (loadSync)
        {
            if (Volatile.Read(ref liveConsumers) != 0) return;
            unloadTimer?.Dispose();
            unloadTimer = new System.Threading.Timer(
                _ => UnloadRulesIfIdle(),
                null,
                RuleUnloadGracePeriod,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void UnloadRulesIfIdle()
    {
        CancellationTokenSource? cancellation;
        lock (loadSync)
        {
            if (Volatile.Read(ref liveConsumers) != 0) return;
            unloadTimer?.Dispose();
            unloadTimer = null;
            loadGeneration++;
            cancellation = loadCancellation;
            loadCancellation = null;
            loadTask = null;
            ruleSet = baselineRuleSet;
            isReady = false;
        }
        CancelAndDispose(cancellation);
    }

    public Task LoadAsync()
    {
        CancellationTokenSource? superseded = null;
        Task task;
        lock (loadSync)
        {
            unloadTimer?.Dispose();
            unloadTimer = null;
            var nowUtc = DateTime.UtcNow;
            var shouldRefresh = false;
            if (loadTask is { IsCompleted: true }
                && updateRemoteLists
                && nowUtc - lastRefreshCheckUtc >= FilterRefreshRetryDelay)
            {
                lastRefreshCheckUtc = nowUtc;
                shouldRefresh = CacheNeedsRefresh();
            }
            if (loadTask is null || shouldRefresh)
            {
                lastRefreshCheckUtc = nowUtc;
                superseded = loadCancellation;
                var cancellation = new CancellationTokenSource();
                loadCancellation = cancellation;
                loadTask = LoadListsAsync(++loadGeneration, cancellation);
            }
            task = loadTask;
        }
        CancelAndDispose(superseded);
        return task;
    }

    /// <summary>
    /// Cancels and disposes a superseded generation. Must be called outside
    /// <c>loadSync</c>: <see cref="CancellationTokenSource.Cancel"/> runs its
    /// registered callbacks inline, so calling it under the lock let task
    /// continuations resume on the locked thread, and let the loader's own
    /// <c>finally</c> dispose a source that was still mid-cancel.
    /// </summary>
    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        cancellation.Dispose();
    }

    public bool ShouldBlock(
        string requestUrl,
        string sourceUrl,
        AdBlockResourceType resourceType,
        bool cacheEvaluation = true)
    {
        if (!TryResolveEvaluationUris(
                requestUrl,
                sourceUrl,
                cacheEvaluation,
                out var requestUri,
                out var sourceUri)
            || requestUri is null)
        {
            return false;
        }
        return ruleSet.ShouldBlock(requestUri, sourceUri, resourceType);
    }

    public AdBlockMatchResult EvaluateRequest(
        string url,
        string? sourceUrl,
        string resourceType) =>
        EvaluateRequest(url, sourceUrl, ParseResourceType(resourceType));

    internal AdBlockMatchResult EvaluateRequest(
        string url,
        string? sourceUrl,
        AdBlockResourceType resourceType,
        bool cacheEvaluation = true)
    {
        if (!TryResolveEvaluationUris(
                url,
                sourceUrl,
                cacheEvaluation,
                out var requestUri,
                out var sourceUri)
            || requestUri is null)
        {
            return new AdBlockMatchResult(false, "None", string.Empty, "None");
        }
        return ruleSet.Evaluate(requestUri, sourceUri, resourceType);
    }

    private static bool TryResolveEvaluationUris(
        string? requestUrl,
        string? sourceUrl,
        bool cacheEvaluation,
        out Uri? requestUri,
        out Uri? sourceUri)
    {
        var requestValid = cacheEvaluation
            ? TryGetCachedRequestUri(requestUrl, out requestUri)
            : TryGetHttpUri(requestUrl, out requestUri);
        if (!requestValid || requestUri is null)
        {
            sourceUri = null;
            return false;
        }
        if (cacheEvaluation) TryGetCachedSourceUri(sourceUrl, out sourceUri);
        else TryGetHttpUri(sourceUrl, out sourceUri);
        return true;
    }

    internal string GetCosmeticCss(string pageUrl, bool cacheEvaluation = true)
    {
        var valid = cacheEvaluation
            ? TryGetCachedRequestUri(pageUrl, out var pageUri)
            : TryGetHttpUri(pageUrl, out pageUri);
        if (!valid || pageUri is null) return string.Empty;
        return ruleSet.GetCosmeticCss(pageUri, cacheEvaluation);
    }

    internal Task<string> GetCosmeticCssAsync(string pageUrl, bool cacheEvaluation = true)
    {
        if (cacheEvaluation
            && TryGetCachedRequestUri(pageUrl, out var cachedPageUri)
            && cachedPageUri is not null
            && ruleSet.TryGetCachedCosmeticCss(cachedPageUri, out var cachedCss))
        {
            return Task.FromResult(cachedCss);
        }
        return Task.Run(() => GetCosmeticCss(pageUrl, cacheEvaluation));
    }

    public bool ShouldBlockNavigation(
        string targetUrl,
        string sourceUrl,
        bool applyRedirectHostHeuristics = true,
        bool cacheEvaluation = true)
    {
        var targetValid = cacheEvaluation
            ? TryGetCachedRequestUri(targetUrl, out var targetUri)
            : TryGetHttpUri(targetUrl, out targetUri);
        if (!targetValid || targetUri is null) return false;
        Uri? sourceUri;
        if (cacheEvaluation) TryGetCachedSourceUri(sourceUrl, out sourceUri);
        else TryGetHttpUri(sourceUrl, out sourceUri);

        if (ruleSet.ShouldBlock(targetUri, sourceUri, AdBlockResourceType.Document)) return true;
        return applyRedirectHostHeuristics && IsKnownRedirectHost(targetUri.Host);
    }

    public bool ShouldBlockPopup(
        string targetUrl,
        string sourceUrl,
        bool cacheEvaluation = true)
    {
        // The WebView event separately rejects non-user-initiated popups. Keep
        // user-initiated OAuth, payment, and target=_blank flows working while
        // still applying network rules and known redirect heuristics.
        if (targetUrl.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryGetHttpUri(targetUrl, out _)) return true;
        return ShouldBlockNavigation(targetUrl, sourceUrl, cacheEvaluation: cacheEvaluation);
    }

    /// <summary>
    /// Name-based resource classification. <see cref="MainForm"/> owns the enum
    /// overload that the live <c>WebResourceRequested</c> path uses; this mirror
    /// exists for string-context callers and tests, so it must stay in step with
    /// that switch rather than drifting.
    /// </summary>
    internal static AdBlockResourceType MapResourceType(
        string? webViewContext,
        string? fetchDestination = null)
    {
        return webViewContext switch
        {
            "Document" when fetchDestination is not null
                && (fetchDestination.Equals("iframe", StringComparison.OrdinalIgnoreCase)
                    || fetchDestination.Equals("frame", StringComparison.OrdinalIgnoreCase)) =>
                AdBlockResourceType.SubDocument,
            "Document" => AdBlockResourceType.Document,
            "Stylesheet" => AdBlockResourceType.Stylesheet,
            "Image" or "Favicon" => AdBlockResourceType.Image,
            "Media" => AdBlockResourceType.Media,
            "Font" => AdBlockResourceType.Font,
            "Script" => AdBlockResourceType.Script,
            "XmlHttpRequest" => AdBlockResourceType.XmlHttpRequest,
            "Fetch" => AdBlockResourceType.Fetch,
            "Websocket" or "WebSocket" => AdBlockResourceType.WebSocket,
            "Ping" or "CspViolationReport" => AdBlockResourceType.Ping,
            "EventSource" => AdBlockResourceType.XmlHttpRequest,
            "TextTrack" => AdBlockResourceType.Media,
            _ => AdBlockResourceType.Other
        };
    }

    private static readonly SearchValues<string> TrackerSignalValues = SearchValues.Create(
    [
        "analytics", "telemetry", "beacon", "tracker", "tracking",
        "pixel", "metrics", "scorecardresearch", "quantserve"
    ], StringComparison.OrdinalIgnoreCase);

    private static readonly SearchValues<string> AdSignalValues = SearchValues.Create(
    [
        "doubleclick", "googlesyndication", "googleadservices", "pagead",
        "adservice", "adsystem", "adsense", "adserver", "adroll", "criteo",
        "taboola", "outbrain", "popads", "popcash", "adsterra", "adnxs",
        "adform", "pubmatic", "rubiconproject", "openx", "moatads",
        "banner", "popup", "popunder", "advert", "adunit", "://ads.",
        ".ads.", "/ads/", "/ads.", "/ads?", "/ad/", "&ads="
    ], StringComparison.OrdinalIgnoreCase);

    private static AdBlockResourceType ParseResourceType(string? resourceType)
    {
        if (string.IsNullOrWhiteSpace(resourceType)) return AdBlockResourceType.Other;
        var trimmed = resourceType.Trim();
        var mapped = MapResourceType(trimmed);
        if (mapped != AdBlockResourceType.Other) return mapped;
        return trimmed.ToLowerInvariant() switch
        {
            "document" => AdBlockResourceType.Document,
            "subdocument" or "sub_frame" or "iframe" or "frame" => AdBlockResourceType.SubDocument,
            "stylesheet" or "css" => AdBlockResourceType.Stylesheet,
            "image" or "img" or "favicon" => AdBlockResourceType.Image,
            "media" or "texttrack" => AdBlockResourceType.Media,
            "font" => AdBlockResourceType.Font,
            "script" => AdBlockResourceType.Script,
            "xmlhttprequest" or "xhr" or "eventsource" => AdBlockResourceType.XmlHttpRequest,
            "fetch" => AdBlockResourceType.Fetch,
            "websocket" => AdBlockResourceType.WebSocket,
            "ping" or "beacon" or "cspviolationreport" => AdBlockResourceType.Ping,
            _ => AdBlockResourceType.Other
        };
    }

    private static string ClassifyBlockCategory(
        FilterRule matchedRule,
        Uri requestUri,
        AdBlockResourceType resourceType)
    {
        return matchedRule.SourceCategory switch
        {
            FilterSourceCategory.Privacy =>
                resourceType == AdBlockResourceType.Ping || HasTrackerSignal(requestUri)
                    ? "Tracker"
                    : "Privacy",
            FilterSourceCategory.ScriptAbuse => "Script",
            FilterSourceCategory.Ad => "Ad",
            _ => ClassifyByRuleAndRequest(matchedRule, requestUri, resourceType)
        };
    }

    private static string ClassifyByRuleAndRequest(
        FilterRule matchedRule,
        Uri requestUri,
        AdBlockResourceType resourceType)
    {
        if (HasAdSignal(requestUri)) return "Ad";
        if (resourceType == AdBlockResourceType.Ping || HasTrackerSignal(requestUri))
        {
            return "Tracker";
        }
        if (resourceType == AdBlockResourceType.Script) return "Script";
        if (matchedRule.SourceCategory == FilterSourceCategory.Fallback) return "Ad";
        return "Network";
    }

    private static bool HasTrackerSignal(Uri requestUri) =>
        requestUri.AbsoluteUri.AsSpan().ContainsAny(TrackerSignalValues);

    private static bool HasAdSignal(Uri requestUri) =>
        requestUri.AbsoluteUri.AsSpan().ContainsAny(AdSignalValues);

    private async Task LoadListsAsync(
        int generation,
        CancellationTokenSource generationCancellation)
    {
        var cancellationToken = generationCancellation.Token;
        var compiledCleanly = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cachedSources = GetCachedListSources();
            var compiledFromCache = false;
            if (cachedSources.Count > 0)
            {
                PublishRuleSetIfCurrent(
                    generation,
                    await CompileRuleSetAsync(cachedSources, cancellationToken).ConfigureAwait(false));
                compiledFromCache = true;
            }
            else
            {
                await Task.Run(PublicSuffixRules.Warmup, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var didRemoteUpdate = false;
            if (updateRemoteLists)
            {
                var staleSources = GetSourcesNeedingRefresh();
                var contentChanged = staleSources.Count > 0
                    && await DownloadListsAsync(staleSources, cancellationToken).ConfigureAwait(false);
                if (contentChanged)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var refreshedSources = GetCachedListSources();
                    if (refreshedSources.Count > 0)
                    {
                        PublishRuleSetIfCurrent(
                            generation,
                            await CompileRuleSetAsync(refreshedSources, cancellationToken).ConfigureAwait(false));
                        didRemoteUpdate = true;
                    }
                }
            }
            compiledCleanly = compiledFromCache || didRemoteUpdate || !updateRemoteLists || cachedSources.Count == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Debug.WriteLine($"MishaWeb AdBlock list load failed: {error}");
        }
        finally
        {
            lock (loadSync)
            {
                if (generation == loadGeneration)
                {
                    isReady = compiledCleanly && !cancellationToken.IsCancellationRequested;
                    if (ReferenceEquals(loadCancellation, generationCancellation))
                    {
                        loadCancellation = null;
                    }
                }
            }
            // A cancelled source can still be unwinding its callback list on the
            // thread that called Cancel(), and this method may be running inline
            // on that same stack. The replacing caller owns disposal in that
            // case; only a source that finished on its own is disposed here.
            if (!cancellationToken.IsCancellationRequested) generationCancellation.Dispose();
        }
    }

    private void PublishRuleSetIfCurrent(int generation, CompiledRuleSet compiled)
    {
        CompiledRuleSet? superseded = null;
        lock (loadSync)
        {
            if (generation == loadGeneration)
            {
                superseded = ruleSet;
                ruleSet = compiled;
            }
        }
        if (superseded is not null
            && !ReferenceEquals(superseded, baselineRuleSet)
            && !ReferenceEquals(superseded, compiled))
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        }
    }

    private List<string> GetCachedListFiles() =>
        GetCachedListSources().Select(static source => source.Path).ToList();

    private List<(string Path, FilterSourceCategory Category)> GetCachedListSources()
    {
        SweepAbandonedTemporaryFiles();
        var sources = new List<(string Path, FilterSourceCategory Category)>(FilterListSources.Length);
        long aggregateBytes = 0;
        foreach (var source in FilterListSources)
        {
            try
            {
                var path = Path.Combine(cacheFolder, source.FileName);
                var file = new FileInfo(path);
                if (file.Exists
                    && file.Length is > 0 and <= MaximumFilterListBytes
                    && aggregateBytes + file.Length <= MaximumAggregateFilterBytes)
                {
                    sources.Add((path, source.Category));
                    aggregateBytes += file.Length;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return sources;
    }

    /// <summary>
    /// Removes download leftovers from a previous process. List payloads and
    /// cache metadata are staged under GUID-suffixed ".tmp" names, which the
    /// in-process cleanup only handles for its own files; a hard kill or power
    /// loss leaves them behind and they accumulate across restarts.
    /// </summary>
    private void SweepAbandonedTemporaryFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - FilterCacheLifetime;
            foreach (var path in Directory.EnumerateFiles(cacheFolder, "*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal IReadOnlyList<string> GetCachedFilterFilesForTesting() =>
        GetCachedListFiles().Select(Path.GetFileName).OfType<string>().ToArray();

    private const string CompiledSnapshotFileName = "compiled-rules-v1.bin";
    private const uint CompiledSnapshotMagic = 0x4241574Du; // "MWAB"
    private const int CompiledSnapshotVersion = 1;
    private const int FilterParserFingerprintVersion = 2;

    private Task<CompiledRuleSet> CompileRuleSetAsync(
        IReadOnlyCollection<(string Path, FilterSourceCategory Category)> sources,
        CancellationToken cancellationToken) =>
        Task.Run(() => CompileRuleSetWithSnapshotCache(sources, cancellationToken), cancellationToken);

    private CompiledRuleSet CompileRuleSetWithSnapshotCache(
        IReadOnlyCollection<(string Path, FilterSourceCategory Category)> sources,
        CancellationToken cancellationToken)
    {
        PublicSuffixRules.Warmup();
        var fingerprint = ComputeSourcesFingerprint(sources);
        if (fingerprint != 0
            && TryLoadCompiledSnapshot(fingerprint, cancellationToken, out var cachedRuleSet)
            && cachedRuleSet is not null)
        {
            return cachedRuleSet;
        }

        var compiled = CompiledRuleSet.FromTaggedLines(
            BuiltInFallbackRules
                .Select(static line => (line, FilterSourceCategory.Fallback))
                .Concat(ReadFilterLines(sources, cancellationToken)),
            out var snapshotEntries,
            out var materializedCount,
            out var disabledRuleCount,
            cancellationToken,
            captureSnapshotEntries: fingerprint != 0);

        if (fingerprint != 0 && snapshotEntries is not null && !cancellationToken.IsCancellationRequested)
        {
            TryWriteCompiledSnapshot(fingerprint, materializedCount, disabledRuleCount, snapshotEntries);
        }

        return compiled;
    }

    private static ulong ComputeSourcesFingerprint(
        IReadOnlyCollection<(string Path, FilterSourceCategory Category)> sources)
    {
        if (sources.Count == 0) return 0;

        var hash = 14695981039346656037UL;
        MixUInt64(ref hash, (ulong)FilterParserFingerprintVersion);
        MixUInt64(ref hash, (ulong)BuiltInFallbackRules.Length);
        foreach (var fallbackRule in BuiltInFallbackRules)
        {
            MixUInt64(ref hash, (ulong)fallbackRule.Length);
            foreach (var ch in fallbackRule)
            {
                MixUInt64(ref hash, ch);
            }
        }
        MixUInt64(ref hash, (ulong)sources.Count);
        foreach (var source in sources)
        {
            try
            {
                var info = new FileInfo(source.Path);
                if (!info.Exists || info.Length <= 0) return 0;
                foreach (var ch in info.Name)
                {
                    MixUInt64(ref hash, char.ToLowerInvariant(ch));
                }
                MixUInt64(ref hash, (ulong)source.Category);
                MixUInt64(ref hash, (ulong)info.Length);
                MixUInt64(ref hash, (ulong)info.LastWriteTimeUtc.Ticks);
            }
            catch
            {
                return 0;
            }
        }
        return hash == 0 ? 1UL : hash;

        static void MixUInt64(ref ulong state, ulong value)
        {
            state ^= value;
            state *= 1099511628211UL;
        }
    }

    private bool TryLoadCompiledSnapshot(
        ulong expectedFingerprint,
        CancellationToken cancellationToken,
        out CompiledRuleSet? compiled)
    {
        compiled = null;
        try
        {
            var snapshotPath = Path.Combine(cacheFolder, CompiledSnapshotFileName);
            var info = new FileInfo(snapshotPath);
            if (!info.Exists || info.Length <= 28 || info.Length > MaximumAggregateFilterBytes)
            {
                return false;
            }

            using var stream = new FileStream(
                snapshotPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadUInt32() != CompiledSnapshotMagic
                || reader.ReadInt32() != CompiledSnapshotVersion
                || reader.ReadUInt64() != expectedFingerprint)
            {
                return false;
            }

            var materializedCount = reader.ReadInt32();
            var disabledRuleCount = reader.ReadInt32();
            var entryCount = reader.ReadInt32();
            if (materializedCount < 0
                || materializedCount > MaximumInputFilterLines
                || disabledRuleCount < 0
                || disabledRuleCount > materializedCount
                || entryCount < 0
                || entryCount > materializedCount)
            {
                return false;
            }

            var entries = new (string Line, FilterSourceCategory Category)[entryCount];
            for (var i = 0; i < entryCount; i++)
            {
                if ((i & 2047) == 0) cancellationToken.ThrowIfCancellationRequested();
                var categoryByte = reader.ReadByte();
                var line = reader.ReadString();
                if (line.Length == 0 || line.Length > MaximumFilterLineCharacters)
                {
                    return false;
                }
                entries[i] = (line, (FilterSourceCategory)categoryByte);
            }

            compiled = CompiledRuleSet.BuildFromActiveOrderedEntries(
                entries,
                materializedCount,
                disabledRuleCount,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void TryWriteCompiledSnapshot(
        ulong fingerprint,
        int materializedCount,
        int disabledRuleCount,
        IReadOnlyList<(string Line, FilterSourceCategory Category)> entries)
    {
        try
        {
            Directory.CreateDirectory(cacheFolder);
            var snapshotPath = Path.Combine(cacheFolder, CompiledSnapshotFileName);
            var tempPath = snapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
                {
                    writer.Write(CompiledSnapshotMagic);
                    writer.Write(CompiledSnapshotVersion);
                    writer.Write(fingerprint);
                    writer.Write(materializedCount);
                    writer.Write(disabledRuleCount);
                    writer.Write(entries.Count);
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var entry = entries[i];
                        writer.Write((byte)entry.Category);
                        writer.Write(entry.Line);
                    }
                    writer.Flush();
                }

                lock (cacheWriteSync)
                {
                    File.Move(tempPath, snapshotPath, overwrite: true);
                }
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { }
            }
        }
        catch
        {
            // Snapshot persistence is an opportunistic cold-start accelerator.
        }
    }

    private static IEnumerable<(string Line, FilterSourceCategory Category)> ReadFilterLines(
        IEnumerable<(string Path, FilterSourceCategory Category)> sources,
        CancellationToken cancellationToken)
    {
        var sourceFiles = sources.ToArray();
        var remainingLines = MaximumInputFilterLines;
        for (var sourceIndex = 0; sourceIndex < sourceFiles.Length; sourceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var laterSources = sourceFiles.Length - sourceIndex - 1;
            var reservedForLater = Math.Min(
                remainingLines,
                laterSources * MinimumReservedLinesPerLaterSource);
            var sourceBudget = Math.Min(
                MaximumInputLinesPerSource,
                Math.Max(0, remainingLines - reservedForLater));
            var acceptedFromSource = 0;
            var source = sourceFiles[sourceIndex];
            foreach (var line in CompiledRuleSet.PreprocessLines(
                         ReadFilterFileLines(source.Path, cancellationToken)))
            {
                if ((acceptedFromSource & 2047) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var trimmed = line.Trim();
                if (trimmed.Length == 0
                    || trimmed[0] is '!' or '['
                    || trimmed.Length > MaximumFilterLineCharacters)
                {
                    continue;
                }
                if (remainingLines <= 0) yield break;
                if (acceptedFromSource >= sourceBudget) break;
                acceptedFromSource++;
                remainingLines--;
                yield return (trimmed, source.Category);
            }
        }
    }

    private static IEnumerable<string> ReadFilterFileLines(
        string file,
        CancellationToken cancellationToken)
    {
        StreamReader? reader;
        try
        {
            var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024);
            reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 64 * 1024);
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        using (reader)
        {
            var buffer = ArrayPool<char>.Shared.Rent(16 * 1024);
            var line = new StringBuilder(512);
            var oversized = false;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read;
                    try { read = reader.Read(buffer, 0, buffer.Length); }
                    catch (IOException) { break; }
                    catch (UnauthorizedAccessException) { break; }
                    if (read == 0) break;

                    for (var index = 0; index < read; index++)
                    {
                        var character = buffer[index];
                        if (character == '\r') continue;
                        if (character == '\n')
                        {
                            if (!oversized) yield return line.ToString();
                            line.Clear();
                            oversized = false;
                            continue;
                        }
                        if (oversized) continue;
                        if (line.Length >= MaximumFilterLineCharacters)
                        {
                            line.Clear();
                            oversized = true;
                            continue;
                        }
                        line.Append(character);
                    }
                }
                if (!oversized && line.Length > 0) yield return line.ToString();
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
    }

    private bool CacheNeedsRefresh()
    {
        return GetSourcesNeedingRefresh().Count > 0;
    }

    private List<FilterListSource> GetSourcesNeedingRefresh()
    {
        var staleSources = new List<FilterListSource>();
        var nowUtc = DateTime.UtcNow;
        foreach (var source in FilterListSources)
        {
            try
            {
                var path = Path.Combine(cacheFolder, source.FileName);
                if (!File.Exists(path)
                    || nowUtc - File.GetLastWriteTimeUtc(path) > FilterCacheLifetime)
                {
                    staleSources.Add(source);
                }
            }
            catch (IOException) { staleSources.Add(source); }
            catch (UnauthorizedAccessException) { staleSources.Add(source); }
        }
        return staleSources;
    }

    internal IReadOnlyList<string> GetStaleFilterFilesForTesting() =>
        GetSourcesNeedingRefresh().Select(source => source.FileName).ToArray();

    private async Task<bool> DownloadListsAsync(
        IReadOnlyCollection<FilterListSource> sources,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheFolder);
        using var limiter = new SemaphoreSlim(MaximumConcurrentFilterDownloads);
        var downloads = sources.Select(async source =>
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await DownloadListAsync(source, cancellationToken).ConfigureAwait(false); }
            finally { limiter.Release(); }
        }).ToArray();
        var results = await Task.WhenAll(downloads).ConfigureAwait(false);
        return results.Any(success => success);
    }

    private async Task<bool> DownloadListAsync(
        FilterListSource source,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(cacheFolder, source.FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var hasUsableCachedCopy = HasUsableCachedCopy(path);
            var cachedValidators = hasUsableCachedCopy
                ? ReadHttpCacheValidators(path)
                : default;
            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            downloadTimeout.CancelAfter(FilterDownloadTimeout);
            var downloadToken = downloadTimeout.Token;
            using var response = await SendWithHttpsRedirectsAsync(
                source.Uri,
                cachedValidators,
                downloadToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (!hasUsableCachedCopy || !HasUsableCachedCopy(path))
                {
                    throw new InvalidDataException("A conditional filter response had no usable cached copy.");
                }

                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                TryWriteHttpCacheValidators(
                    path,
                    MergeHttpCacheValidators(cachedValidators, GetHttpCacheValidators(response)));
                return false;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumFilterListBytes)
            {
                throw new InvalidDataException("Filter list exceeded the size limit.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false);
            await using (var output = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 32 * 1024,
                useAsync: true))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
                try
                {
                    var totalBytes = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(
                            buffer.AsMemory(0, 32 * 1024),
                            downloadToken).ConfigureAwait(false);
                        if (read == 0) break;
                        totalBytes += read;
                        if (totalBytes > MaximumFilterListBytes)
                        {
                            throw new InvalidDataException("Filter list exceeded the size limit.");
                        }
                        await output.WriteAsync(
                            buffer.AsMemory(0, read),
                            downloadToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (!LooksLikeFilterList(temporary))
            {
                throw new InvalidDataException("Downloaded content was not a filter list.");
            }

            if (File.Exists(path)
                && await FilesEqualAsync(path, temporary).ConfigureAwait(false))
            {
                File.Delete(temporary);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                TryWriteHttpCacheValidators(path, GetHttpCacheValidators(response));
                return false;
            }

            lock (cacheWriteSync)
            {
                if (!CanCommitFilterFile(path, temporary))
                {
                    throw new InvalidDataException("Filter cache exceeded the aggregate size limit.");
                }
                File.Move(temporary, path, true);
            }
            TryWriteHttpCacheValidators(path, GetHttpCacheValidators(response));
            return true;
        }
        catch
        {
            // Keep the last known-good copy when an individual source is unavailable.
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    private static async Task<HttpResponseMessage> SendWithHttpsRedirectsAsync(
        Uri initialUri,
        FilterHttpCacheValidators cachedValidators,
        CancellationToken cancellationToken)
    {
        const int maxRedirects = 5;
        var currentUri = initialUri;
        for (var redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
        {
            if (!currentUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Filter list URL must use HTTPS.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            if (redirectCount == 0)
            {
                if (cachedValidators.EntityTag is { } entityTag
                    && EntityTagHeaderValue.TryParse(entityTag, out var parsedEntityTag))
                {
                    request.Headers.IfNoneMatch.Add(parsedEntityTag);
                }
                if (cachedValidators.LastModified is { } lastModified)
                {
                    request.Headers.IfModifiedSince = lastModified;
                }
            }

            var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;
            if (statusCode is not (301 or 302 or 303 or 307 or 308))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new InvalidDataException("Filter list redirect did not include a Location header.");
            }

            var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (!nextUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Filter list redirect must use HTTPS.");
            }
            currentUri = nextUri;
        }

        throw new InvalidDataException("Filter list exceeded the maximum redirect limit.");
    }

    internal bool CanCommitFilterFile(string targetPath, string candidatePath)
    {
        try
        {
            var candidate = new FileInfo(candidatePath);
            if (!candidate.Exists || candidate.Length is <= 0 or > MaximumFilterListBytes)
            {
                return false;
            }

            long aggregateBytes = candidate.Length;
            foreach (var configuredSource in FilterListSources)
            {
                var configuredPath = Path.Combine(cacheFolder, configuredSource.FileName);
                if (configuredPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase)) continue;
                var cached = new FileInfo(configuredPath);
                if (!cached.Exists || cached.Length is <= 0 or > MaximumFilterListBytes) continue;
                aggregateBytes += cached.Length;
                if (aggregateBytes > MaximumAggregateFilterBytes) return false;
            }
            return aggregateBytes <= MaximumAggregateFilterBytes;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool HasUsableCachedCopy(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length is > 0 and <= MaximumFilterListBytes;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static FilterHttpCacheValidators GetHttpCacheValidators(HttpResponseMessage response)
    {
        var entityTag = response.Headers.ETag?.ToString();
        if (entityTag is { Length: > MaximumEntityTagCharacters }) entityTag = null;
        return new FilterHttpCacheValidators(entityTag, response.Content.Headers.LastModified);
    }

    private static FilterHttpCacheValidators MergeHttpCacheValidators(
        FilterHttpCacheValidators cached,
        FilterHttpCacheValidators response) =>
        new(response.EntityTag ?? cached.EntityTag, response.LastModified ?? cached.LastModified);

    private static FilterHttpCacheValidators ReadHttpCacheValidators(string filterPath)
    {
        var metadataPath = GetHttpCacheMetadataPath(filterPath);
        try
        {
            var file = new FileInfo(metadataPath);
            if (!file.Exists || file.Length is <= 0 or > MaximumHttpCacheMetadataBytes)
            {
                return default;
            }

            var lines = File.ReadAllLines(metadataPath, Encoding.UTF8);
            if (lines.Length != 3 || !lines[0].Equals("v1", StringComparison.Ordinal))
            {
                return default;
            }

            string? entityTag = null;
            if (lines[1].Length > 0)
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(lines[1]));
                if (decoded.Length <= MaximumEntityTagCharacters
                    && EntityTagHeaderValue.TryParse(decoded, out _))
                {
                    entityTag = decoded;
                }
            }

            DateTimeOffset? lastModified = null;
            if (long.TryParse(
                    lines[2],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var unixSeconds))
            {
                lastModified = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            return new FilterHttpCacheValidators(entityTag, lastModified);
        }
        catch (FormatException) { return default; }
        catch (ArgumentOutOfRangeException) { return default; }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }

    private static void TryWriteHttpCacheValidators(
        string filterPath,
        FilterHttpCacheValidators validators)
    {
        var metadataPath = GetHttpCacheMetadataPath(filterPath);
        // A per-list fixed name collided whenever two generations of the engine
        // refreshed the same list at once: one task's finally block deleted the
        // other's in-flight temp file. Match the GUID scheme used for list
        // payloads.
        var temporary = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (validators.EntityTag is null && validators.LastModified is null)
            {
                File.Delete(metadataPath);
                return;
            }

            var entityTag = validators.EntityTag;
            if (entityTag is { Length: > MaximumEntityTagCharacters }
                || (entityTag is not null && !EntityTagHeaderValue.TryParse(entityTag, out _)))
            {
                entityTag = null;
            }
            var encodedEntityTag = entityTag is null
                ? string.Empty
                : Convert.ToBase64String(Encoding.UTF8.GetBytes(entityTag));
            var modifiedSeconds = validators.LastModified?.ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            File.WriteAllLines(
                temporary,
                ["v1", encodedEntityTag, modifiedSeconds],
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, metadataPath, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string GetHttpCacheMetadataPath(string filterPath) => filterPath + ".http-cache";

    private static async Task<bool> FilesEqualAsync(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (leftInfo.Length != rightInfo.Length) return false;

        await using var left = new FileStream(
            leftPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 32 * 1024,
            useAsync: true);
        await using var right = new FileStream(
            rightPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 32 * 1024,
            useAsync: true);
        var leftBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        var rightBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (true)
            {
                var leftRead = await left.ReadAsync(leftBuffer.AsMemory(0, 32 * 1024)).ConfigureAwait(false);
                var rightRead = await right.ReadAsync(rightBuffer.AsMemory(0, 32 * 1024)).ConfigureAwait(false);
                if (leftRead != rightRead) return false;
                if (leftRead == 0) return true;
                if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                {
                    return false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
            ArrayPool<byte>.Shared.Return(rightBuffer);
        }
    }

    private static bool LooksLikeFilterList(string path)
    {
        const int maxSampleBytes = 16 * 1024;
        const int maxSampleNonEmptyLines = 64;
        var buffer = ArrayPool<byte>.Shared.Rent(maxSampleBytes);
        try
        {
            int bytesRead;
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: maxSampleBytes))
            {
                bytesRead = 0;
                while (bytesRead < maxSampleBytes)
                {
                    var read = stream.Read(buffer, bytesRead, maxSampleBytes - bytesRead);
                    if (read == 0) break;
                    bytesRead += read;
                }
            }
            if (bytesRead == 0) return false;

            using var memory = new MemoryStream(buffer, 0, bytesRead, writable: false);
            using var reader = new StreamReader(memory, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var nonEmptyLines = 0;
            var hasFilterSyntax = false;
            while (nonEmptyLines < maxSampleNonEmptyLines && reader.ReadLine() is { } line)
            {
                if (line.Contains("<html", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("<!doctype", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                if (string.IsNullOrWhiteSpace(line)) continue;
                nonEmptyLines++;
                hasFilterSyntax |= line.StartsWith('!')
                    || line.StartsWith("||", StringComparison.Ordinal)
                    || line.Contains("##", StringComparison.Ordinal);
            }
            return nonEmptyLines >= 5 && hasFilterSyntax;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static uint PackIndexKey4(ReadOnlySpan<char> value, int offset) =>
        ((uint)ToLowerAsciiByte(value[offset]) << 24)
        | ((uint)ToLowerAsciiByte(value[offset + 1]) << 16)
        | ((uint)ToLowerAsciiByte(value[offset + 2]) << 8)
        | ToLowerAsciiByte(value[offset + 3]);

    private static uint PackIndexKey3(ReadOnlySpan<char> value, int offset) =>
        ((uint)ToLowerAsciiByte(value[offset]) << 16)
        | ((uint)ToLowerAsciiByte(value[offset + 1]) << 8)
        | ToLowerAsciiByte(value[offset + 2]);

    private static byte ToLowerAsciiByte(char value) =>
        value is >= 'A' and <= 'Z' ? (byte)(value + ('a' - 'A')) : (byte)value;

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseCookies = false,
            AllowAutoRedirect = false
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        var version = typeof(AdBlockEngine).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MishaWeb", version));
        return client;
    }

    private static bool TryGetHttpUri(string? value, out Uri? uri)
    {
        uri = null;
        if (value is null
            || !Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate is null
            || (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static bool TryGetCachedSourceUri(string? value, out Uri? uri)
    {
        return TryGetCachedHttpUri(
            value,
            ref cachedSourceUris,
            MaximumCachedSourceUrisPerThread,
            out uri);
    }

    private static bool TryGetCachedRequestUri(string? value, out Uri? uri)
    {
        return TryGetCachedHttpUri(
            value,
            ref cachedRequestUris,
            MaximumCachedRequestUrisPerThread,
            out uri);
    }

    private static bool TryGetCachedHttpUri(
        string? value,
        ref Dictionary<string, Uri?>? cache,
        int maximumEntries,
        out Uri? uri)
    {
        var cacheKey = value ?? string.Empty;
        if (cacheKey.Length > BrowserPolicy.MaximumUrlLength)
        {
            return TryGetHttpUri(value, out uri);
        }
        cache ??= new Dictionary<string, Uri?>(
            maximumEntries,
            StringComparer.Ordinal);
        if (cache.TryGetValue(cacheKey, out uri))
        {
            return uri is not null;
        }

        var valid = TryGetHttpUri(value, out uri);
        TrimCache(cache, maximumEntries);
        cache[cacheKey] = uri;
        return valid;
    }

    /// <summary>
    /// Drops a fraction of a bounded per-thread cache rather than clearing it
    /// wholesale. Which entries go is deliberately unspecified; only the bound
    /// matters for correctness, and retaining most of the cache stops hot
    /// entries from being reparsed on every WebView2 policy hook.
    /// </summary>
    private static void TrimCache<TValue>(Dictionary<string, TValue> cache, int maximumEntries)
    {
        if (cache.Count < maximumEntries) return;
        var toRemove = Math.Max(1, maximumEntries / 4);
        var keys = ArrayPool<string>.Shared.Rent(toRemove);
        var collected = 0;
        try
        {
            foreach (var key in cache.Keys)
            {
                keys[collected++] = key;
                if (collected == toRemove) break;
            }
            for (var index = 0; index < collected; index++)
            {
                cache.Remove(keys[index]);
            }
        }
        finally
        {
            Array.Clear(keys, 0, collected);
            ArrayPool<string>.Shared.Return(keys);
        }
    }

    private static bool AreSameSite(string leftHost, string rightHost)
    {
        return GetSiteKey(leftHost).Equals(
            GetSiteKey(rightHost),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HostMatches(string requestHost, string ruleHost)
    {
        var requestStart = 0;
        var requestEnd = requestHost.Length;
        while (requestEnd > requestStart && requestHost[requestEnd - 1] == '.') requestEnd--;

        var ruleStart = 0;
        var ruleEnd = ruleHost.Length;
        while (ruleStart < ruleEnd && ruleHost[ruleStart] == '.') ruleStart++;
        while (ruleEnd > ruleStart && ruleHost[ruleEnd - 1] == '.') ruleEnd--;

        var request = requestHost.AsSpan(requestStart, requestEnd - requestStart);
        var rule = ruleHost.AsSpan(ruleStart, ruleEnd - ruleStart);
        if (request.Equals(rule, StringComparison.OrdinalIgnoreCase)) return true;
        return request.Length > rule.Length
            && request[request.Length - rule.Length - 1] == '.'
            && request[^rule.Length..].Equals(rule, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] GetCachedHostSuffixes(string host)
    {
        var cache = cachedHostSuffixSets ??= new Dictionary<string, string[]>(
            MaximumCachedHostSuffixSetsPerThread,
            StringComparer.OrdinalIgnoreCase);
        if (cache.TryGetValue(host, out var cached)) return cached;

        var hostEnd = host.Length;
        while (hostEnd > 0 && host[hostEnd - 1] == '.') hostEnd--;
        var suffixCount = 1;
        for (var index = 0; index < hostEnd; index++)
        {
            if (host[index] == '.') suffixCount++;
        }

        var suffixes = new string[suffixCount];
        var suffixIndex = 0;
        var hostStart = 0;
        while (hostStart < hostEnd)
        {
            suffixes[suffixIndex++] = hostStart == 0 && hostEnd == host.Length
                ? host
                : host[hostStart..hostEnd];
            var nextDot = host.IndexOf('.', hostStart, hostEnd - hostStart);
            if (nextDot < 0) break;
            hostStart = nextDot + 1;
        }

        if (suffixIndex != suffixes.Length) Array.Resize(ref suffixes, suffixIndex);
        TrimCache(cache, MaximumCachedHostSuffixSetsPerThread);
        cache[host] = suffixes;
        return suffixes;
    }

    private static bool MatchesAnyHost(string host, string[] domains)
    {
        foreach (var domain in domains)
        {
            if (HostMatches(host, domain)) return true;
        }
        return false;
    }

    private static string GetSiteKey(string host)
    {
        var cache = cachedSiteKeys ??= new Dictionary<string, string>(
            MaximumCachedSiteKeysPerThread,
            StringComparer.OrdinalIgnoreCase);
        if (cache.TryGetValue(host, out var siteKey)) return siteKey;
        siteKey = PublicSuffixRules.GetSiteKey(host);
        TrimCache(cache, MaximumCachedSiteKeysPerThread);
        cache[host] = siteKey;
        return siteKey;
    }

    private static string ExcludeTopLevelDocument(string rule)
    {
        var options = IsWholeHostFallbackRule(rule)
            ? "third-party,~document"
            : "~document";
        return FindOptionIndex(rule) >= 0
            ? rule + ',' + options
            : rule + '$' + options;
    }

    private static bool IsWholeHostFallbackRule(string rule)
    {
        if (!rule.StartsWith("||", StringComparison.Ordinal)) return false;
        var end = rule.IndexOfAny(['/','^','*','|','?','$'], 2);
        return end < 0 || rule[end] is '^' or '|';
    }

    private static bool IsKnownRedirectHost(string host)
    {
        foreach (var redirectHost in KnownRedirectHosts)
        {
            if (HostMatches(host, redirectHost)) return true;
        }
        return false;
    }

    private sealed class CompiledRuleSet
    {
        private const int MaximumMaterializedRules = 400_000;
        private const int MaximumCosmeticRules = 100_000;
        private const int MaximumHideDisableRules = 20_000;
        private const int MaximumRegexRules = 512;
        private const int MaximumUnindexedGenericRegexBlockRules = 64;
        private const int MaximumUnindexedGenericRegexExceptionRules = 128;
        private const int MaximumUnindexedSourceScopedRegexBlockRules = 256;
        private const int MaximumUnindexedSourceScopedRegexExceptionRules = 256;
        private const int MaximumRegexPatternCharacters = 2_048;
        [ThreadStatic] private static (CompiledRuleSet? Owner, Dictionary<string, bool>? Cache) genericBlockCache;
        private readonly RuleIndex blocks;
        private readonly RuleIndex importantBlocks;
        private readonly RuleIndex exceptions;
        private readonly CosmeticRuleSet cosmetics;
        private readonly RuleIndex genericBlockIndex;

        private CompiledRuleSet(
            RuleIndex blocks,
            RuleIndex importantBlocks,
            RuleIndex exceptions,
            CosmeticRuleSet cosmetics,
            RuleIndex genericBlockIndex,
            AdBlockCompileDiagnostics diagnostics)
        {
            this.blocks = blocks;
            this.importantBlocks = importantBlocks;
            this.exceptions = exceptions;
            this.cosmetics = cosmetics;
            this.genericBlockIndex = genericBlockIndex;
            Diagnostics = diagnostics;
        }

        public AdBlockCompileDiagnostics Diagnostics { get; }

        public static CompiledRuleSet FromLines(
            IEnumerable<string> lines,
            CancellationToken cancellationToken = default) =>
            FromLines(lines, preprocess: true, cancellationToken);

        public static CompiledRuleSet FromLines(
            IEnumerable<string> lines,
            bool preprocess,
            CancellationToken cancellationToken = default) =>
            FromTaggedLines(
                (preprocess ? PreprocessLines(lines) : lines)
                    .Select(static line => (line, FilterSourceCategory.Unknown)),
                cancellationToken);

        public static CompiledRuleSet FromTaggedLines(
            IEnumerable<(string Line, FilterSourceCategory Category)> lines,
            CancellationToken cancellationToken = default) =>
            FromTaggedLines(
                lines,
                out _,
                out _,
                out _,
                cancellationToken,
                captureSnapshotEntries: false);

        public static CompiledRuleSet FromTaggedLines(
            IEnumerable<(string Line, FilterSourceCategory Category)> lines,
            out List<(string Line, FilterSourceCategory Category)>? snapshotEntries,
            out int materializedCount,
            out int disabledRuleCount,
            CancellationToken cancellationToken = default,
            bool captureSnapshotEntries = false)
        {
            var materialized = new Dictionary<string, FilterSourceCategory>(StringComparer.Ordinal);
            var processedLineCount = 0;
            foreach (var entry in lines)
            {
                if ((processedLineCount++ & 2047) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var trimmed = entry.Line.Trim();
                if (trimmed.Length == 0 || trimmed[0] is '!' or '[') continue;
                if (materialized.TryGetValue(trimmed, out var existingCategory))
                {
                    if (existingCategory is FilterSourceCategory.Unknown or FilterSourceCategory.Fallback
                        && entry.Category is not (FilterSourceCategory.Unknown or FilterSourceCategory.Fallback))
                    {
                        materialized[trimmed] = entry.Category;
                    }
                    continue;
                }
                materialized.Add(trimmed, entry.Category);
                if (materialized.Count >= MaximumMaterializedRules) break;
            }

            var disabledRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var disabledRawPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            processedLineCount = 0;
            foreach (var line in materialized.Keys)
            {
                if ((processedLineCount++ & 2047) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var disabled = GetBadFilterTarget(line);
                if (disabled is not null)
                {
                    disabledRules.Add(GetRuleIdentity(disabled));
                    disabledRawPatterns.Add(GetRawPatternPrefix(disabled));
                }
            }

            disabledRuleCount = 0;
            var activeOrdered = new List<(string Line, FilterSourceCategory Category)>(materialized.Count);
            processedLineCount = 0;
            foreach (var entry in EnumerateRulesWithRegexExceptionsFirst(materialized))
            {
                var line = entry.Key;
                if ((processedLineCount++ & 2047) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // $badfilter has to be resolved before the cosmetic and
                // hide-disable parsers run. Pre-filtering by raw pattern skips
                // option splitting and sorting for rules that cannot match.
                if (disabledRules.Count > 0
                    && disabledRawPatterns.Contains(GetRawPatternPrefix(line))
                    && (GetBadFilterTarget(line) is not null
                        || disabledRules.Contains(GetRuleIdentity(line))))
                {
                    disabledRuleCount++;
                    continue;
                }

                activeOrdered.Add((line, entry.Value));
            }

            materializedCount = materialized.Count;
            snapshotEntries = captureSnapshotEntries ? activeOrdered : null;
            return BuildFromActiveOrderedEntries(
                activeOrdered,
                materializedCount,
                disabledRuleCount,
                cancellationToken);
        }

        internal static CompiledRuleSet BuildFromActiveOrderedEntries(
            IReadOnlyList<(string Line, FilterSourceCategory Category)> activeOrderedEntries,
            int materializedCount,
            int disabledRuleCount,
            CancellationToken cancellationToken = default)
        {
            var blocks = new RuleIndex();
            var importantBlocks = new RuleIndex();
            var exceptions = new RuleIndex();
            var cosmetics = new CosmeticRuleSet();
            var genericBlockIndex = new RuleIndex();
            var networkRuleCount = 0;
            var cosmeticRuleCount = 0;
            var hideDisableRuleCount = 0;
            var unsupportedRuleCandidateCount = 0;
            var capacityDroppedNetworkRuleCount = 0;
            var regexRuleCount = 0;
            var indexedRegexRuleCount = 0;
            var unindexedGenericRegexBlockRuleCount = 0;
            var unindexedGenericRegexExceptionRuleCount = 0;
            var unindexedSourceScopedRegexBlockRuleCount = 0;
            var unindexedSourceScopedRegexExceptionRuleCount = 0;
            var droppedRegexRuleCount = 0;

            for (var index = 0; index < activeOrderedEntries.Count; index++)
            {
                var entry = activeOrderedEntries[index];
                var line = entry.Line;
                if ((index & 2047) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var hideDisable = HideDisableRule.Parse(line);
                if (hideDisable is not null)
                {
                    if (hideDisableRuleCount >= MaximumHideDisableRules)
                    {
                        unsupportedRuleCandidateCount++;
                        continue;
                    }
                    hideDisableRuleCount++;
                    cosmetics.AddDisable(hideDisable);
                    if (hideDisable.DisableGenericBlocking)
                    {
                        var genericMatcher = hideDisable.DisableGenericHiding || hideDisable.DisableSpecificHiding
                            ? hideDisable.CreateSeparateMatcher(line) ?? hideDisable.Matcher
                            : hideDisable.Matcher;
                        genericBlockIndex.Add(genericMatcher);
                    }
                    continue;
                }

                var cosmetic = CosmeticRule.Parse(line);
                if (cosmetic is not null)
                {
                    if (cosmeticRuleCount >= MaximumCosmeticRules)
                    {
                        unsupportedRuleCandidateCount++;
                        continue;
                    }
                    cosmeticRuleCount++;
                    cosmetics.Add(cosmetic);
                    continue;
                }

                var regexCandidate = FilterRule.TryGetRegexPattern(line, out var regexPattern);
                if (regexCandidate
                    && (regexPattern.Length > MaximumRegexPatternCharacters
                        || regexRuleCount >= MaximumRegexRules))
                {
                    droppedRegexRuleCount++;
                    continue;
                }
                var rule = FilterRule.Parse(line, entry.Category);
                if (rule is null)
                {
                    if (line.Length > 0 && line[0] is not '!' and not '[')
                    {
                        unsupportedRuleCandidateCount++;
                    }
                    continue;
                }
                if (rule.IsRegex)
                {
                    if (!rule.HasRegexIndex
                        && rule.HasRestrictiveSourceDomainScope
                        && (rule.Exception
                            ? unindexedSourceScopedRegexExceptionRuleCount
                                >= MaximumUnindexedSourceScopedRegexExceptionRules
                            : unindexedSourceScopedRegexBlockRuleCount
                                >= MaximumUnindexedSourceScopedRegexBlockRules))
                    {
                        droppedRegexRuleCount++;
                        continue;
                    }
                    if (!rule.HasRegexIndex
                        && !rule.HasRestrictiveSourceDomainScope
                        && (rule.Exception
                            ? unindexedGenericRegexExceptionRuleCount
                                >= MaximumUnindexedGenericRegexExceptionRules
                            : unindexedGenericRegexBlockRuleCount
                                >= MaximumUnindexedGenericRegexBlockRules))
                    {
                        droppedRegexRuleCount++;
                        continue;
                    }
                    regexRuleCount++;
                    if (rule.HasRegexIndex) indexedRegexRuleCount++;
                    else if (rule.HasRestrictiveSourceDomainScope)
                    {
                        if (rule.Exception) unindexedSourceScopedRegexExceptionRuleCount++;
                        else unindexedSourceScopedRegexBlockRuleCount++;
                    }
                    else
                    {
                        if (rule.Exception) unindexedGenericRegexExceptionRuleCount++;
                        else unindexedGenericRegexBlockRuleCount++;
                    }
                }
                var targetIndex = rule.Exception
                    ? exceptions
                    : (rule.Important ? importantBlocks : blocks);
                if (targetIndex.Add(rule))
                {
                    networkRuleCount++;
                }
                else
                {
                    capacityDroppedNetworkRuleCount++;
                }
            }

            blocks.Seal();
            importantBlocks.Seal();
            exceptions.Seal();
            cosmetics.Seal();
            genericBlockIndex.Seal();

            return new CompiledRuleSet(
                blocks,
                importantBlocks,
                exceptions,
                cosmetics,
                genericBlockIndex,
                new AdBlockCompileDiagnostics(
                    materializedCount,
                    networkRuleCount,
                    cosmeticRuleCount,
                    hideDisableRuleCount,
                    disabledRuleCount,
                    unsupportedRuleCandidateCount,
                    capacityDroppedNetworkRuleCount,
                    regexRuleCount,
                    indexedRegexRuleCount,
                    unindexedSourceScopedRegexBlockRuleCount
                        + unindexedSourceScopedRegexExceptionRuleCount,
                    unindexedGenericRegexBlockRuleCount
                        + unindexedGenericRegexExceptionRuleCount,
                    droppedRegexRuleCount));
        }

        private static string GetRawPatternPrefix(string line)
        {
            var optionIndex = FindOptionIndex(line);
            return optionIndex < 0 ? line.Trim() : line[..optionIndex].Trim();
        }

        private static IEnumerable<KeyValuePair<string, FilterSourceCategory>> EnumerateRulesWithRegexExceptionsFirst(
            Dictionary<string, FilterSourceCategory> materialized)
        {
            // Exception rules must consume the bounded regex budget before block
            // rules. Otherwise a list that fills the cap can silently discard a
            // later @@ rule and broaden blocking beyond the authored policy.
            foreach (var entry in materialized)
            {
                if (entry.Key.StartsWith("@@", StringComparison.Ordinal)
                    && FilterRule.TryGetRegexPattern(entry.Key, out _))
                {
                    yield return entry;
                }
            }
            foreach (var entry in materialized)
            {
                if (!entry.Key.StartsWith("@@", StringComparison.Ordinal)
                    || !FilterRule.TryGetRegexPattern(entry.Key, out _))
                {
                    yield return entry;
                }
            }
        }

        internal static IEnumerable<string> PreprocessLines(IEnumerable<string> lines)
        {
            var frames = new Stack<ConditionalFrame>();
            var active = true;
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.StartsWith("!#if ", StringComparison.Ordinal))
                {
                    var condition = EvaluateCondition(line[5..]);
                    frames.Push(new ConditionalFrame(active, condition, false));
                    active = active && condition;
                    continue;
                }
                if (line.Equals("!#else", StringComparison.Ordinal))
                {
                    if (frames.Count == 0) continue;
                    var frame = frames.Pop();
                    if (frame.ElseSeen)
                    {
                        frames.Push(frame);
                        active = false;
                    }
                    else
                    {
                        frames.Push(frame with { ElseSeen = true });
                        active = frame.ParentActive && !frame.Condition;
                    }
                    continue;
                }
                if (line.Equals("!#endif", StringComparison.Ordinal))
                {
                    if (frames.Count > 0) active = frames.Pop().ParentActive;
                    continue;
                }
                if (line.StartsWith("!#include ", StringComparison.Ordinal)) continue;
                if (active) yield return rawLine;
            }
        }

        private static bool EvaluateCondition(string expression)
        {
            var parser = new ConditionParser(expression.AsSpan());
            if (!parser.TryParseOr(out var result)) return false;
            parser.SkipWhitespace();
            return parser.AtEnd && result;
        }

        private ref struct ConditionParser
        {
            private readonly ReadOnlySpan<char> text;
            private int position;

            public ConditionParser(ReadOnlySpan<char> text)
            {
                this.text = text;
                position = 0;
            }

            public readonly bool AtEnd => position >= text.Length;

            public void SkipWhitespace()
            {
                while (position < text.Length && char.IsWhiteSpace(text[position]))
                {
                    position++;
                }
            }

            public bool TryParseOr(out bool value)
            {
                if (!TryParseAnd(out value)) return false;
                while (true)
                {
                    SkipWhitespace();
                    if (position + 1 < text.Length && text[position] == '|' && text[position + 1] == '|')
                    {
                        position += 2;
                        if (!TryParseAnd(out var rhs)) return false;
                        value |= rhs;
                    }
                    else
                    {
                        return true;
                    }
                }
            }

            private bool TryParseAnd(out bool value)
            {
                if (!TryParseUnary(out value)) return false;
                while (true)
                {
                    SkipWhitespace();
                    if (position + 1 < text.Length && text[position] == '&' && text[position + 1] == '&')
                    {
                        position += 2;
                        if (!TryParseUnary(out var rhs)) return false;
                        value &= rhs;
                    }
                    else
                    {
                        return true;
                    }
                }
            }

            private bool TryParseUnary(out bool value)
            {
                SkipWhitespace();
                if (position < text.Length && text[position] == '!')
                {
                    position++;
                    if (!TryParseUnary(out var inner))
                    {
                        value = false;
                        return false;
                    }
                    value = !inner;
                    return true;
                }
                return TryParsePrimary(out value);
            }

            private bool TryParsePrimary(out bool value)
            {
                value = false;
                SkipWhitespace();
                if (position >= text.Length) return false;

                if (text[position] == '(')
                {
                    position++;
                    if (!TryParseOr(out value)) return false;
                    SkipWhitespace();
                    if (position >= text.Length || text[position] != ')') return false;
                    position++;
                    return true;
                }

                var start = position;
                while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '_' or '-'))
                {
                    position++;
                }
                if (position == start) return false;

                var identifier = text[start..position];
                value = identifier.Equals("env_chromium", StringComparison.OrdinalIgnoreCase);
                return true;
            }
        }

        private readonly record struct ConditionalFrame(
            bool ParentActive,
            bool Condition,
            bool ElseSeen);

        public bool ShouldBlock(Uri requestUri, Uri? sourceUri, AdBlockResourceType resourceType) =>
            TryEvaluate(requestUri, sourceUri, resourceType, out _, out _);

        public AdBlockMatchResult Evaluate(
            Uri requestUri,
            Uri? sourceUri,
            AdBlockResourceType resourceType)
        {
            var blocked = TryEvaluate(
                requestUri,
                sourceUri,
                resourceType,
                out var matchedRule,
                out var matchedException);
            if (!blocked || matchedRule is null)
            {
                return new AdBlockMatchResult(
                    Blocked: false,
                    Category: "None",
                    Host: requestUri.IdnHost,
                    RuleKind: matchedException ? "Exception" : "None");
            }

            var ruleKind = matchedRule.IsRegex
                ? "Regex"
                : matchedRule.IsHostAnchored
                    ? "HostAnchored"
                    : "Pattern";
            return new AdBlockMatchResult(
                Blocked: true,
                Category: ClassifyBlockCategory(matchedRule, requestUri, resourceType),
                Host: requestUri.IdnHost,
                RuleKind: ruleKind);
        }

        private bool TryEvaluate(
            Uri requestUri,
            Uri? sourceUri,
            AdBlockResourceType resourceType,
            out FilterRule? matchedRule,
            out bool matchedException)
        {
            matchedRule = null;
            matchedException = false;
            var suppressGenericBlocking = IsGenericBlockingSuppressed(sourceUri);
            var thirdPartyState = -1;
            string? pathAndQuery = null;

            var matchedImportantBlock = false;
            if (!importantBlocks.IsEmpty)
            {
                foreach (var rule in importantBlocks.GetCandidates(requestUri))
                {
                    if ((suppressGenericBlocking && rule.IsGeneric)
                        || !rule.Matches(requestUri, sourceUri, resourceType, ref thirdPartyState, ref pathAndQuery))
                    {
                        continue;
                    }

                    matchedRule = rule;
                    matchedImportantBlock = true;
                    break;
                }
            }

            if (matchedRule is null && !blocks.IsEmpty)
            {
                foreach (var rule in blocks.GetCandidates(requestUri))
                {
                    if ((suppressGenericBlocking && rule.IsGeneric)
                        || !rule.Matches(requestUri, sourceUri, resourceType, ref thirdPartyState, ref pathAndQuery))
                    {
                        continue;
                    }

                    matchedRule = rule;
                    break;
                }
            }
            if (matchedRule is null) return false;

            if (!exceptions.IsEmpty)
            {
                foreach (var rule in exceptions.GetCandidates(requestUri))
                {
                    if (matchedImportantBlock && !rule.Important) continue;
                    if (rule.Matches(requestUri, sourceUri, resourceType, ref thirdPartyState, ref pathAndQuery))
                    {
                        matchedRule = rule;
                        matchedException = true;
                        return false;
                    }
                }
            }
            return true;
        }

        private bool IsGenericBlockingSuppressed(Uri? sourceUri)
        {
            if (sourceUri is null || genericBlockIndex.IsEmpty) return false;
            var entry = genericBlockCache;
            if (!ReferenceEquals(entry.Owner, this) || entry.Cache is null)
            {
                entry = (this, new Dictionary<string, bool>(MaximumCachedSourceUrisPerThread, StringComparer.Ordinal));
                genericBlockCache = entry;
            }
            var sourceKey = sourceUri.AbsoluteUri;
            if (entry.Cache.TryGetValue(sourceKey, out var cached)) return cached;

            var suppressed = false;
            var pageThirdPartyState = 0;
            foreach (var matcher in genericBlockIndex.GetCandidates(sourceUri))
            {
                if (matcher.Matches(sourceUri, sourceUri, AdBlockResourceType.Document, ref pageThirdPartyState))
                {
                    suppressed = true;
                    break;
                }
            }
            TrimCache(entry.Cache, MaximumCachedSourceUrisPerThread);
            entry.Cache[sourceKey] = suppressed;
            return suppressed;
        }

        public string GetCosmeticCss(Uri pageUri, bool cacheEvaluation) =>
            cosmetics.GetCss(pageUri, cacheEvaluation);

        public bool TryGetCachedCosmeticCss(Uri pageUri, out string css) =>
            cosmetics.TryGetCachedCss(pageUri, out css);

        /// <summary>
        /// Builds a comparison key for a filter line: the pattern plus its
        /// options in a canonical, order-insensitive form. Filter lists routinely
        /// reorder options between a rule and its $badfilter counterpart
        /// (<c>||x^$domain=a.com,script</c> vs <c>@@||x^$script,domain=a.com</c>).
        /// Lowercasing every option before sorting ensures case variations sort
        /// consistently too.
        /// </summary>
        internal static string GetRuleIdentity(string line)
        {
            var optionIndex = FindOptionIndex(line);
            if (optionIndex < 0) return line.Trim();
            var pattern = line.AsSpan(0, optionIndex).Trim();

            var options = line[(optionIndex + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (options.Length == 0) return pattern.ToString();
            if (options.Length == 1) return string.Concat(pattern, "|", options[0].ToLowerInvariant());

            for (var index = 0; index < options.Length; index++)
            {
                options[index] = options[index].ToLowerInvariant();
            }
            Array.Sort(options, StringComparer.Ordinal);
            var builder = new StringBuilder(pattern.Length + (options.Length * 12) + 8);
            builder.Append(pattern);
            foreach (var option in options)
            {
                builder.Append('|').Append(option);
            }
            return builder.ToString();
        }

        internal static string? GetBadFilterTarget(string line)
        {
            if (line.IndexOf("badfilter", StringComparison.OrdinalIgnoreCase) < 0) return null;
            var optionIndex = FindOptionIndex(line);
            if (optionIndex < 0) return null;
            var options = line.AsSpan(optionIndex + 1);
            if (!ContainsBadFilterOption(options)) return null;

            var target = new StringBuilder(line.Length);
            target.Append(line.AsSpan(0, optionIndex));
            var wroteOption = false;
            var start = 0;
            while (start <= options.Length)
            {
                var relativeEnd = options[start..].IndexOf(',');
                var end = relativeEnd < 0 ? options.Length : start + relativeEnd;
                var option = options[start..end].Trim();
                if (option.Length > 0
                    && !option.Equals("badfilter", StringComparison.OrdinalIgnoreCase))
                {
                    target.Append(wroteOption ? ',' : '$');
                    target.Append(option);
                    wroteOption = true;
                }
                if (relativeEnd < 0) break;
                start = end + 1;
            }
            return target.ToString();
        }

        private static bool ContainsBadFilterOption(ReadOnlySpan<char> options)
        {
            var start = 0;
            while (start <= options.Length)
            {
                var relativeEnd = options[start..].IndexOf(',');
                var end = relativeEnd < 0 ? options.Length : start + relativeEnd;
                if (options[start..end].Trim().Equals(
                        "badfilter",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (relativeEnd < 0) return false;
                start = end + 1;
            }
            return false;
        }
    }

    private sealed class RuleIndex
    {
        private const int MaximumUnindexedRules = 4_000;
        private const int MaximumIndexedRules = 100_000;
        private const int MaximumRetainedIndexKeys = 4_096;
        [ThreadStatic] private static HashSet<uint>? reusableIndexKeys;
        [ThreadStatic] private static bool reusableIndexKeysInUse;
        private readonly Dictionary<string, object> hostRules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, object> indexed4Rules = [];
        private readonly Dictionary<uint, object> indexed3Rules = [];
        private readonly List<FilterRule> unindexedRules = [];
        private FilterRule[] sealedUnindexedRules = Array.Empty<FilterRule>();
        private int indexedRuleCount;
        private bool isEmpty = true;

        public bool IsEmpty => isEmpty;

        public bool Add(FilterRule rule)
        {
            if (rule.IndexHost is { } indexHost)
            {
                AddToBucket(hostRules, indexHost, rule);
                // The dictionary key is now the canonical retained host. Every lookup
                // reaches this list only while walking a matching request-host suffix,
                // so individual rules do not need a duplicate host string or a second
                // suffix comparison for each request.
                rule.ReleaseIndexHost();
                isEmpty = false;
                return true;
            }

            if (rule.IndexKey is { } indexKey && indexedRuleCount < MaximumIndexedRules)
            {
                var table = rule.IsFourByteIndex ? indexed4Rules : indexed3Rules;
                AddToBucket(table, indexKey, rule);
                indexedRuleCount++;
                isEmpty = false;
                return true;
            }
            else if (unindexedRules.Count < MaximumUnindexedRules)
            {
                unindexedRules.Add(rule);
                isEmpty = false;
                return true;
            }
            return false;
        }

        private static void AddToBucket<TKey>(Dictionary<TKey, object> table, TKey key, FilterRule rule)
            where TKey : notnull
        {
            if (!table.TryGetValue(key, out var existing))
            {
                table[key] = rule;
                return;
            }

            if (existing is List<FilterRule> list)
            {
                list.Add(rule);
                return;
            }

            table[key] = new List<FilterRule>(4) { (FilterRule)existing, rule };
        }

        public void Seal()
        {
            FreezeBuckets(hostRules);
            FreezeBuckets(indexed4Rules);
            FreezeBuckets(indexed3Rules);
            sealedUnindexedRules = unindexedRules.Count == 0
                ? Array.Empty<FilterRule>()
                : unindexedRules.ToArray();
            unindexedRules.Clear();
            unindexedRules.TrimExcess();
        }

        private static void FreezeBuckets<TKey>(Dictionary<TKey, object> table)
            where TKey : notnull
        {
            if (table.Count == 0) return;
            List<KeyValuePair<TKey, FilterRule[]>>? updates = null;
            foreach (var pair in table)
            {
                if (pair.Value is List<FilterRule> list)
                {
                    updates ??= new List<KeyValuePair<TKey, FilterRule[]>>();
                    updates.Add(new KeyValuePair<TKey, FilterRule[]>(pair.Key, list.ToArray()));
                }
            }
            if (updates is null) return;
            foreach (var update in updates)
            {
                table[update.Key] = update.Value;
            }
        }

        // A pattern-based struct enumerator keeps the per-request rule walk off
        // the managed heap. The candidate order is host rules, 4-byte indexed
        // rules, optional 3-byte indexed rules, then bounded unindexed rules.
        public CandidateEnumerable GetCandidates(Uri uri) => new(this, uri);

        internal readonly struct CandidateEnumerable
        {
            private readonly RuleIndex owner;
            private readonly Uri uri;

            public CandidateEnumerable(RuleIndex owner, Uri uri)
            {
                this.owner = owner;
                this.uri = uri;
            }

            public CandidateEnumerator GetEnumerator() => new(owner, uri);
        }

        internal struct CandidateEnumerator : IDisposable
        {
            private readonly RuleIndex owner;
            private readonly string[] hostSuffixes;
            private readonly string target;
            private readonly HashSet<uint> seenKeys;
            private readonly bool borrowedReusableSet;
            private FilterRule[]? currentRules;
            private int currentRuleIndex;
            private int hostIndex;
            private int targetIndex;
            private int unindexedRuleIndex;
            private int phase;
            private bool disposed;

            public CandidateEnumerator(RuleIndex owner, Uri uri)
            {
                this.owner = owner;
                hostSuffixes = GetCachedHostSuffixes(uri.Host);
                target = uri.AbsoluteUri;
                borrowedReusableSet = !reusableIndexKeysInUse;
                seenKeys = borrowedReusableSet
                    ? reusableIndexKeys ??= new HashSet<uint>()
                    : new HashSet<uint>();
                if (borrowedReusableSet) reusableIndexKeysInUse = true;
                currentRules = null;
                currentRuleIndex = -1;
                hostIndex = 0;
                targetIndex = 0;
                unindexedRuleIndex = 0;
                phase = owner.hostRules.Count > 0 ? 0 : 1;
                disposed = false;
                Current = null!;
            }

            public FilterRule Current { get; private set; }

            public bool MoveNext()
            {
                while (true)
                {
                    if (currentRules is not null
                        && ++currentRuleIndex < currentRules.Length)
                    {
                        Current = currentRules[currentRuleIndex];
                        return true;
                    }
                    currentRules = null;

                    if (phase == 0)
                    {
                        while (hostIndex < hostSuffixes.Length)
                        {
                            var candidateHost = hostSuffixes[hostIndex++];
                            if (!owner.hostRules.TryGetValue(candidateHost, out var entry)) continue;
                            if (entry is FilterRule singleRule)
                            {
                                Current = singleRule;
                                return true;
                            }
                            if (entry is FilterRule[] ruleArray && ruleArray.Length > 0)
                            {
                                currentRules = ruleArray;
                                currentRuleIndex = 0;
                                Current = ruleArray[0];
                                return true;
                            }
                        }
                        phase = 1;
                    }

                    if (phase == 1)
                    {
                        if (owner.indexed4Rules.Count > 0)
                        {
                            while (targetIndex <= target.Length - 4)
                            {
                                var c0 = target[targetIndex];
                                var c1 = target[targetIndex + 1];
                                var c2 = target[targetIndex + 2];
                                var c3 = target[targetIndex + 3];
                                targetIndex++;
                                if ((c0 | c1 | c2 | c3) > 0x7F) continue;
                                var key = ((uint)ToLowerAsciiByte(c0) << 24)
                                    | ((uint)ToLowerAsciiByte(c1) << 16)
                                    | ((uint)ToLowerAsciiByte(c2) << 8)
                                    | ToLowerAsciiByte(c3);
                                if (!owner.indexed4Rules.TryGetValue(key, out var entry)) continue;
                                if (!seenKeys.Add(key)) continue;
                                if (entry is FilterRule singleRule)
                                {
                                    Current = singleRule;
                                    return true;
                                }
                                if (entry is FilterRule[] ruleArray && ruleArray.Length > 0)
                                {
                                    currentRules = ruleArray;
                                    currentRuleIndex = 0;
                                    Current = ruleArray[0];
                                    return true;
                                }
                            }
                        }
                        targetIndex = 0;
                        phase = owner.indexed3Rules.Count > 0 ? 2 : 3;
                    }

                    if (phase == 2)
                    {
                        while (targetIndex <= target.Length - 3)
                        {
                            var c0 = target[targetIndex];
                            var c1 = target[targetIndex + 1];
                            var c2 = target[targetIndex + 2];
                            targetIndex++;
                            if ((c0 | c1 | c2) > 0x7F) continue;
                            var key = ((uint)ToLowerAsciiByte(c0) << 16)
                                | ((uint)ToLowerAsciiByte(c1) << 8)
                                | ToLowerAsciiByte(c2);
                            if (!owner.indexed3Rules.TryGetValue(key, out var entry)) continue;
                            if (!seenKeys.Add(key)) continue;
                            if (entry is FilterRule singleRule)
                            {
                                Current = singleRule;
                                return true;
                            }
                            if (entry is FilterRule[] ruleArray && ruleArray.Length > 0)
                            {
                                currentRules = ruleArray;
                                currentRuleIndex = 0;
                                Current = ruleArray[0];
                                return true;
                            }
                        }
                        phase = 3;
                    }

                    var unindexed = owner.sealedUnindexedRules;
                    if (phase == 3 && unindexedRuleIndex < unindexed.Length)
                    {
                        Current = unindexed[unindexedRuleIndex++];
                        return true;
                    }

                    phase = 4;
                    return false;
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                seenKeys.Clear();
                if (borrowedReusableSet)
                {
                    if (seenKeys.EnsureCapacity(0) > MaximumRetainedIndexKeys)
                    {
                        reusableIndexKeys = null;
                    }
                    reusableIndexKeysInUse = false;
                }
            }
        }
    }

    private sealed class CosmeticRuleSet
    {
        private const int MaximumStyleBytes = 512 * 1024;
        private const int MaximumSelectorsPerPage = 12_000;
        private const int MaximumCachedHosts = 64;
        private const long MaximumCachedStyleBytes = 4 * 1024 * 1024;
        private const int SelectorBatchSize = 256;
        private const string HideDeclarationSuffix =
            "{display:none!important;visibility:hidden!important;min-height:0!important;}\n";

        private readonly record struct StyleCacheKey(string Host, bool DisableGeneric, bool DisableSpecific);
        private readonly record struct GenericCssChunk(string Css, int SelectorCount);

        private readonly List<CosmeticRule> genericRules = [];
        private readonly List<CosmeticRule> conditionalGenericRules = [];
        private readonly Dictionary<string, List<CosmeticRule>> domainRules = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> exceptedSelectors = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<StyleCacheKey, string> styleCache = new();
        private readonly ConcurrentQueue<StyleCacheKey> styleCacheOrder = new();
        private readonly RuleIndex hideDisableIndex = new();
        private GenericCssChunk[] precompiledGenericChunks = Array.Empty<GenericCssChunk>();
        private string precompiledGenericCss = string.Empty;
        private int precompiledGenericSelectorCount;
        private long styleCacheBytes;

        public void Add(CosmeticRule rule)
        {
            if (rule.Exception) exceptedSelectors.Add(rule.Selector);
            if (rule.IncludedDomains.Length == 0)
            {
                genericRules.Add(rule);
                return;
            }

            foreach (var domain in rule.IncludedDomains)
            {
                if (!domainRules.TryGetValue(domain, out var rules))
                {
                    rules = new List<CosmeticRule>(1);
                    domainRules[domain] = rules;
                }
                rules.Add(rule);
            }
        }

        public void AddDisable(HideDisableRule rule)
        {
            if (!rule.DisableGenericHiding && !rule.DisableSpecificHiding) return;
            rule.Matcher.HideDisableFlags = (byte)((rule.DisableGenericHiding ? 1 : 0)
                | (rule.DisableSpecificHiding ? 2 : 0));
            hideDisableIndex.Add(rule.Matcher);
        }

        public void Seal()
        {
            foreach (var pair in domainRules) pair.Value.TrimExcess();
            hideDisableIndex.Seal();

            var seenGenericSelectors = new HashSet<string>(StringComparer.Ordinal);
            var chunks = new List<GenericCssChunk>();
            var allGenericBuilder = new StringBuilder();
            var batch = new List<string>(SelectorBatchSize);
            var batchTextLength = 0;
            var totalSelectors = 0;

            void FlushBatch()
            {
                if (batch.Count == 0) return;
                var estimatedCssLength = batchTextLength + batch.Count + HideDeclarationSuffix.Length;
                if (allGenericBuilder.Length + estimatedCssLength > MaximumStyleBytes)
                {
                    batch.Clear();
                    batchTextLength = 0;
                    return;
                }

                var chunkBuilder = new StringBuilder(estimatedCssLength);
                for (var index = 0; index < batch.Count; index++)
                {
                    if (index > 0) chunkBuilder.Append(',');
                    chunkBuilder.Append(batch[index]);
                }
                chunkBuilder.Append(HideDeclarationSuffix);
                var chunkCss = chunkBuilder.ToString();
                chunks.Add(new GenericCssChunk(chunkCss, batch.Count));
                allGenericBuilder.Append(chunkCss);
                totalSelectors += batch.Count;
                batch.Clear();
                batchTextLength = 0;
            }

            foreach (var rule in genericRules)
            {
                if (rule.Exception
                    || rule.HasExcludedDomains
                    || exceptedSelectors.Contains(rule.Selector))
                {
                    conditionalGenericRules.Add(rule);
                    continue;
                }
                if (totalSelectors + batch.Count >= MaximumSelectorsPerPage) continue;
                if (!seenGenericSelectors.Add(rule.Selector)) continue;

                var projectedBatchBytes = batchTextLength + batch.Count + rule.Selector.Length + HideDeclarationSuffix.Length;
                if (allGenericBuilder.Length + projectedBatchBytes > MaximumStyleBytes)
                {
                    FlushBatch();
                    break;
                }

                batch.Add(rule.Selector);
                batchTextLength += rule.Selector.Length;
                if (batch.Count >= SelectorBatchSize) FlushBatch();
            }

            FlushBatch();
            genericRules.Clear();
            genericRules.TrimExcess();
            conditionalGenericRules.TrimExcess();
            precompiledGenericChunks = chunks.ToArray();
            precompiledGenericCss = allGenericBuilder.ToString();
            precompiledGenericSelectorCount = totalSelectors;
        }

        public bool TryGetCachedCss(Uri pageUri, out string css)
        {
            ResolveHideDisableFlags(pageUri, out var disableGeneric, out var disableSpecific);
            if (disableGeneric && disableSpecific)
            {
                css = string.Empty;
                return true;
            }

            var cacheKey = new StyleCacheKey(pageUri.Host, disableGeneric, disableSpecific);
            if (styleCache.TryGetValue(cacheKey, out var cached))
            {
                css = cached;
                return true;
            }

            css = string.Empty;
            return false;
        }

        public string GetCss(Uri pageUri, bool cacheEvaluation)
        {
            ResolveHideDisableFlags(pageUri, out var disableGeneric, out var disableSpecific);
            if (disableGeneric && disableSpecific) return string.Empty;

            var host = pageUri.Host;
            if (!cacheEvaluation) return CompileCss(host, disableGeneric, disableSpecific);

            var cacheKey = new StyleCacheKey(host, disableGeneric, disableSpecific);
            if (styleCache.TryGetValue(cacheKey, out var cached)) return cached;

            var compiled = CompileCss(host, disableGeneric, disableSpecific);
            if (!styleCache.TryAdd(cacheKey, compiled))
            {
                return styleCache.TryGetValue(cacheKey, out cached) ? cached : compiled;
            }

            Interlocked.Add(ref styleCacheBytes, GetCacheEntryBytes(cacheKey.Host, compiled));
            styleCacheOrder.Enqueue(cacheKey);
            while ((styleCache.Count > MaximumCachedHosts
                    || Volatile.Read(ref styleCacheBytes) > MaximumCachedStyleBytes)
                && styleCacheOrder.TryDequeue(out var oldest))
            {
                if (styleCache.TryRemove(oldest, out var removed))
                {
                    Interlocked.Add(ref styleCacheBytes, -GetCacheEntryBytes(oldest.Host, removed));
                }
            }
            return compiled;
        }

        private void ResolveHideDisableFlags(Uri pageUri, out bool disableGeneric, out bool disableSpecific)
        {
            disableGeneric = false;
            disableSpecific = false;
            if (hideDisableIndex.IsEmpty) return;

            var thirdPartyState = 0;
            foreach (var matcher in hideDisableIndex.GetCandidates(pageUri))
            {
                if (!matcher.Matches(pageUri, pageUri, AdBlockResourceType.Document, ref thirdPartyState))
                {
                    continue;
                }
                var flags = matcher.HideDisableFlags;
                if ((flags & 1) != 0) disableGeneric = true;
                if ((flags & 2) != 0) disableSpecific = true;
                if (disableGeneric && disableSpecific) return;
            }
        }

        private long GetCacheEntryBytes(string host, string style) =>
            ReferenceEquals(style, precompiledGenericCss)
                ? 2L * host.Length
                : 2L * (host.Length + style.Length);

        private string CompileCss(string host, bool disableGeneric, bool disableSpecific)
        {
            HashSet<string>? exceptions = null;
            foreach (var rule in GetDynamicCandidates(host, disableGeneric, disableSpecific))
            {
                if (rule.Exception && rule.Matches(host))
                {
                    exceptions ??= new HashSet<string>(StringComparer.Ordinal);
                    exceptions.Add(rule.Selector);
                }
            }

            var dynamicCss = new StringBuilder();
            var seenSelectors = new HashSet<string>(StringComparer.Ordinal);
            var batch = new List<string>(SelectorBatchSize);
            var count = 0;

            void FlushDynamicBatch()
            {
                if (batch.Count == 0) return;
                var separatorBytes = batch.Count - 1;
                var rawSelectorBytes = 0;
                foreach (var item in batch) rawSelectorBytes += item.Length;
                if (dynamicCss.Length + rawSelectorBytes + separatorBytes + HideDeclarationSuffix.Length > MaximumStyleBytes)
                {
                    batch.Clear();
                    return;
                }
                for (var index = 0; index < batch.Count; index++)
                {
                    if (index > 0) dynamicCss.Append(',');
                    dynamicCss.Append(batch[index]);
                }
                dynamicCss.Append(HideDeclarationSuffix);
                batch.Clear();
            }

            foreach (var rule in GetDynamicCandidates(host, disableGeneric, disableSpecific))
            {
                if (rule.Exception
                    || !rule.Matches(host)
                    || (exceptions is not null && exceptions.Contains(rule.Selector))
                    || !seenSelectors.Add(rule.Selector))
                {
                    continue;
                }
                if (count >= MaximumSelectorsPerPage) break;
                count++;
                batch.Add(rule.Selector);
                if (batch.Count >= SelectorBatchSize)
                {
                    var prevLen = dynamicCss.Length;
                    FlushDynamicBatch();
                    if (dynamicCss.Length == prevLen) break;
                }
            }
            FlushDynamicBatch();

            if (disableGeneric || precompiledGenericCss.Length == 0)
            {
                return dynamicCss.ToString();
            }

            if (dynamicCss.Length == 0
                && precompiledGenericSelectorCount <= MaximumSelectorsPerPage
                && precompiledGenericCss.Length <= MaximumStyleBytes)
            {
                return precompiledGenericCss;
            }

            if (count + precompiledGenericSelectorCount <= MaximumSelectorsPerPage
                && dynamicCss.Length + precompiledGenericCss.Length <= MaximumStyleBytes)
            {
                dynamicCss.Append(precompiledGenericCss);
                return dynamicCss.ToString();
            }

            // Combined host-specific + unconditional generic rules exceed one of
            // the page caps; append as many full 256-selector batches as fit.
            foreach (var chunk in precompiledGenericChunks)
            {
                if (count + chunk.SelectorCount > MaximumSelectorsPerPage) break;
                if (dynamicCss.Length + chunk.Css.Length > MaximumStyleBytes) break;
                dynamicCss.Append(chunk.Css);
                count += chunk.SelectorCount;
            }
            return dynamicCss.ToString();
        }

        private IEnumerable<CosmeticRule> GetDynamicCandidates(string host, bool disableGeneric, bool disableSpecific)
        {
            // Specific selectors are more valuable than the generic tail and
            // must not be pushed past the per-page CSS safety budget.
            if (!disableSpecific && !string.IsNullOrEmpty(host) && domainRules.Count > 0)
            {
                var suffixes = GetCachedHostSuffixes(host);
                foreach (var domain in suffixes)
                {
                    if (!domainRules.TryGetValue(domain, out var rules)) continue;
                    foreach (var rule in rules) yield return rule;
                }
            }
            if (!disableGeneric)
            {
                foreach (var rule in conditionalGenericRules) yield return rule;
            }
        }
    }

    private sealed class HideDisableRule
    {
        private HideDisableRule(
            FilterRule matcher,
            bool disableGenericHiding,
            bool disableSpecificHiding,
            bool disableGenericBlocking)
        {
            this.matcher = matcher;
            DisableGenericHiding = disableGenericHiding;
            DisableSpecificHiding = disableSpecificHiding;
            DisableGenericBlocking = disableGenericBlocking;
        }

        private readonly FilterRule matcher;
        public FilterRule Matcher => matcher;
        public bool DisableGenericHiding { get; }
        public bool DisableSpecificHiding { get; }
        public bool DisableGenericBlocking { get; }

        public FilterRule? CreateSeparateMatcher(string line) =>
            Parse(line)?.Matcher;

        public static HideDisableRule? Parse(string line)
        {
            if (!line.StartsWith("@@", StringComparison.Ordinal)) return null;
            var optionIndex = FindOptionIndex(line);
            if (optionIndex < 0) return null;
            var options = line[(optionIndex + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var disableAllHiding = options.Any(option =>
                option.Equals("elemhide", StringComparison.OrdinalIgnoreCase));
            var disableGenericHiding = disableAllHiding || options.Any(option =>
                option.Equals("generichide", StringComparison.OrdinalIgnoreCase));
            var disableSpecificHiding = disableAllHiding || options.Any(option =>
                option.Equals("specifichide", StringComparison.OrdinalIgnoreCase));
            var disableGenericBlocking = options.Any(option =>
                option.Equals("genericblock", StringComparison.OrdinalIgnoreCase));
            if (!disableGenericHiding && !disableSpecificHiding && !disableGenericBlocking) return null;

            var remainingOptions = options.Where(option =>
                !option.Equals("elemhide", StringComparison.OrdinalIgnoreCase)
                && !option.Equals("generichide", StringComparison.OrdinalIgnoreCase)
                && !option.Equals("specifichide", StringComparison.OrdinalIgnoreCase)
                && !option.Equals("genericblock", StringComparison.OrdinalIgnoreCase));
            var normalized = line[..optionIndex];
            var remainingText = string.Join(',', remainingOptions);
            if (remainingText.Length > 0) normalized += '$' + remainingText;
            var matcher = FilterRule.Parse(normalized);
            return matcher is null
                ? null
                : new HideDisableRule(
                    matcher,
                    disableGenericHiding,
                    disableSpecificHiding,
                    disableGenericBlocking);
        }
    }

    private sealed class CosmeticRule
    {
        private static readonly string[] UnsupportedSelectorParts =
        [
            ":has-text(", ":-abp-", ":matches-css(", ":matches-css-before(",
            ":matches-css-after(", ":xpath(", ":upward(", ":remove(",
            ":style(", ":watch-attr(", ":matches-attr(", ":matches-property(",
            ":matches-path(", ":min-text-length(", ":remove-attr(",
            ":remove-class(", ":matches-media(", ":others(", ":contains("
        ];

        private CosmeticRule(
            string selector,
            bool exception,
            string[] includedDomains,
            string[] excludedDomains)
        {
            Selector = selector;
            Exception = exception;
            IncludedDomains = includedDomains;
            this.excludedDomains = excludedDomains;
        }

        private readonly string[] excludedDomains;
        public string Selector { get; }
        public bool Exception { get; }
        public string[] IncludedDomains { get; }
        public bool HasExcludedDomains => excludedDomains.Length > 0;
        public bool IsGeneric => IncludedDomains.Length == 0;

        public static CosmeticRule? Parse(string line)
        {
            var exceptionIndex = line.IndexOf("#@#", StringComparison.Ordinal);
            var hideIndex = line.IndexOf("##", StringComparison.Ordinal);
            var exception = exceptionIndex >= 0;
            var delimiterIndex = exception ? exceptionIndex : hideIndex;
            var delimiterLength = exception ? 3 : 2;
            if (delimiterIndex < 0) return null;

            var selector = line[(delimiterIndex + delimiterLength)..].Trim();
            if (!IsSafeCssSelector(selector)) return null;
            var includedDomains = Array.Empty<string>();
            var excludedDomains = Array.Empty<string>();
            var domainText = line[..delimiterIndex].Trim();
            if (domainText.Length > 0)
            {
                var includedDomainSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var excludedDomainSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!ParseDomainList(domainText, ',', includedDomainSet, excludedDomainSet))
                {
                    return null;
                }
                includedDomains = includedDomainSet.ToArray();
                excludedDomains = excludedDomainSet.ToArray();
            }
            return new CosmeticRule(selector, exception, includedDomains, excludedDomains);
        }

        public bool Matches(string host)
        {
            if (MatchesAnyHost(host, excludedDomains)) return false;
            return IncludedDomains.Length == 0
                || MatchesAnyHost(host, IncludedDomains);
        }

        private static bool IsSafeCssSelector(string selector)
        {
            if (selector.Length is 0 or > 2_048
                || selector[0] == '^'
                || selector[0] == ','
                || selector[^1] == ','
                || selector.Contains('$')
                || selector.StartsWith("+js(", StringComparison.OrdinalIgnoreCase)
                || selector.Contains('{')
                || selector.Contains('}')
                || selector.Contains(';')
                || selector.Contains('@')
                || selector.Contains("/*", StringComparison.Ordinal)
                || selector.Contains("*/", StringComparison.Ordinal)
                || selector.Contains("url(", StringComparison.OrdinalIgnoreCase)
                || selector.Any(char.IsControl))
            {
                return false;
            }
            if (UnsupportedSelectorParts.Any(part => selector.Contains(part, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var parenDepth = 0;
            var bracketDepth = 0;
            var quote = '\0';
            for (var index = 0; index < selector.Length; index++)
            {
                var ch = selector[index];
                if (quote != '\0')
                {
                    if (ch == '\\')
                    {
                        if (index + 1 >= selector.Length) return false;
                        index++;
                    }
                    else if (ch == quote)
                    {
                        quote = '\0';
                    }
                    continue;
                }

                if (ch == '\\')
                {
                    if (index + 1 >= selector.Length) return false;
                    index++;
                    continue;
                }
                if (ch is '\'' or '"')
                {
                    quote = ch;
                }
                else if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')')
                {
                    if (--parenDepth < 0) return false;
                }
                else if (ch == '[')
                {
                    bracketDepth++;
                }
                else if (ch == ']')
                {
                    if (--bracketDepth < 0) return false;
                }
            }

            return quote == '\0' && parenDepth == 0 && bracketDepth == 0;
        }
    }

    private sealed class FilterRule
    {
        private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(25);
        private readonly Regex? regex;
        private readonly uint? includedTypes;
        private readonly uint excludedTypes;
        private readonly string[] includedDomains;
        private readonly string[] excludedDomains;
        private readonly string[] includedTargetDomains;
        private readonly string[] excludedTargetDomains;
        private readonly bool matchCase;
        private readonly bool hostAnchoredRule;
        private bool hostMatchGuaranteedByIndex;
        private readonly string? hostPathPattern;
        private readonly string? networkPattern;
        private readonly bool useHostAndPathTarget;
        private readonly FilterSourceCategory sourceCategory;

        private FilterRule(
            string pattern,
            string? host,
            int patternHostEnd,
            bool exception,
            bool important,
            Regex? regex,
            uint? regexIndexKey,
            bool regexIsFourByteIndex,
            uint? includedTypes,
            uint excludedTypes,
            string[] includedDomains,
            string[] excludedDomains,
            string[] includedTargetDomains,
            string[] excludedTargetDomains,
            bool thirdPartyOnly,
            bool firstPartyOnly,
            bool matchCase,
            FilterSourceCategory sourceCategory)
        {
            IndexHost = host;
            hostAnchoredRule = host is not null;
            Exception = exception;
            Important = important;
            this.regex = regex;
            this.includedTypes = includedTypes;
            this.excludedTypes = excludedTypes;
            this.includedDomains = includedDomains;
            this.excludedDomains = excludedDomains;
            this.includedTargetDomains = includedTargetDomains;
            this.excludedTargetDomains = excludedTargetDomains;
            ThirdPartyOnly = thirdPartyOnly;
            FirstPartyOnly = firstPartyOnly;
            this.matchCase = matchCase;
            this.sourceCategory = sourceCategory;
            if (regex is not null)
            {
                IndexKey = regexIndexKey;
                IsFourByteIndex = regexIsFourByteIndex;
                return;
            }

            IndexKey = ExtractIndexKey(pattern, out var isFourByte);
            IsFourByteIndex = isFourByte;

            // Normalize anchors and globs once while compiling a list. Rebuilding
            // these short strings or lowercasing them per character on every request
            // wastes hot-path cycles.
            var normalizedPattern = pattern;
            var hostAnchored = normalizedPattern.StartsWith("||", StringComparison.Ordinal);
            if (hostAnchored) normalizedPattern = normalizedPattern[2..];

            if (host is not null)
            {
                var hostSpanEnd = Math.Min(patternHostEnd - 2, normalizedPattern.Length);
                var remainder = hostSpanEnd < 0
                    ? string.Empty
                    : normalizedPattern[hostSpanEnd..];
                if (remainder.Length == 0 || remainder == "^") return;

                var endAnchored = remainder.EndsWith('|');
                if (endAnchored) remainder = remainder[..^1];
                var compiledRemainder = endAnchored ? remainder : remainder + '*';
                hostPathPattern = matchCase ? compiledRemainder : compiledRemainder.ToLowerInvariant();
                return;
            }

            useHostAndPathTarget = hostAnchored;
            var startAnchored = hostAnchored || normalizedPattern.StartsWith('|');
            if (normalizedPattern.StartsWith('|')) normalizedPattern = normalizedPattern[1..];
            var endAnchor = normalizedPattern.EndsWith('|');
            if (endAnchor) normalizedPattern = normalizedPattern[..^1];
            if (!startAnchored) normalizedPattern = '*' + normalizedPattern;
            if (!endAnchor) normalizedPattern += '*';
            networkPattern = matchCase ? normalizedPattern : normalizedPattern.ToLowerInvariant();
        }

        public string? IndexHost { get; private set; }
        public uint? IndexKey { get; }
        public bool IsFourByteIndex { get; }
        public bool Exception { get; }
        public bool Important { get; }
        public bool ThirdPartyOnly { get; }
        public bool FirstPartyOnly { get; }
        public byte HideDisableFlags { get; set; }
        public bool IsGeneric => includedDomains.Length == 0 && excludedDomains.Length == 0;
        public bool IsRegex => regex is not null;
        public bool IsHostAnchored => hostAnchoredRule;
        public FilterSourceCategory SourceCategory => sourceCategory;
        public bool HasRegexIndex => IsRegex && IndexKey is not null;
        public bool HasRestrictiveSourceDomainScope => includedDomains.Length > 0;

        public void ReleaseIndexHost()
        {
            hostMatchGuaranteedByIndex = true;
            IndexHost = null;
        }

        public static FilterRule? Parse(
            string? input,
            FilterSourceCategory sourceCategory = FilterSourceCategory.Unknown)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var line = input.Trim();
            if (line.StartsWith('!')
                || line.StartsWith('[')
                || line.Contains("##", StringComparison.Ordinal)
                || line.Contains("#@#", StringComparison.Ordinal)
                || line.Contains("#?#", StringComparison.Ordinal)
                || line.Contains("#$#", StringComparison.Ordinal)
                || line.Contains("#%#", StringComparison.Ordinal))
            {
                return null;
            }

            var exception = line.StartsWith("@@", StringComparison.Ordinal);
            if (exception) line = line[2..];

            var optionIndex = FindOptionIndex(line);
            var pattern = optionIndex < 0 ? line : line[..optionIndex];
            var optionText = optionIndex < 0 ? string.Empty : line[(optionIndex + 1)..];
            if (pattern.Length == 0) return null;

            var includedTypeMask = 0u;
            var excludedTypeMask = 0u;
            HashSet<string>? includedDomains = null;
            HashSet<string>? excludedDomains = null;
            HashSet<string>? includedTargetDomains = null;
            HashSet<string>? excludedTargetDomains = null;
            var thirdPartyOnly = false;
            var firstPartyOnly = false;
            var important = false;
            var matchCase = false;

            foreach (var rawOption in SplitFilterOptions(optionText))
            {
                var option = rawOption.ToLowerInvariant();
                if (option is "badfilter" or "generichide" or "specifichide" or "elemhide" or "genericblock")
                {
                    return null;
                }
                if (option is "third-party" or "3p" or "strict3p" or "~first-party")
                {
                    thirdPartyOnly = true;
                    continue;
                }
                if (option is "~third-party" or "1p" or "first-party" or "strict1p")
                {
                    firstPartyOnly = true;
                    continue;
                }
                if (option == "important")
                {
                    important = true;
                    continue;
                }
                if (option == "match-case")
                {
                    matchCase = true;
                    continue;
                }
                if (option == "~match-case") continue;
                if (option.StartsWith("domain=", StringComparison.Ordinal))
                {
                    if (!ParseDomainList(
                            rawOption[7..],
                            '|',
                            ref includedDomains,
                            ref excludedDomains))
                    {
                        return null;
                    }
                    continue;
                }
                if (option.StartsWith("from=", StringComparison.Ordinal))
                {
                    if (!ParseDomainList(
                            rawOption[5..],
                            '|',
                            ref includedDomains,
                            ref excludedDomains))
                    {
                        return null;
                    }
                    continue;
                }
                if (option.StartsWith("to=", StringComparison.Ordinal))
                {
                    if (!ParseDomainList(
                            rawOption[3..],
                            '|',
                            ref includedTargetDomains,
                            ref excludedTargetDomains))
                    {
                        return null;
                    }
                    continue;
                }
                if (option.StartsWith("denyallow=", StringComparison.Ordinal))
                {
                    var denyIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var denyExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    // SplitFilterOptions has already joined every continuation of
                    // this value with '|', so no comma can survive to here.
                    //
                    // This engine compares hosts by suffix and has no CIDR matching,
                    // so a range entry cannot be honoured. Dropping just that entry
                    // keeps the rule, where letting it reach ParseDomainList failed
                    // the whole parse and deleted the rule by error.
                    var denyAllowValue = rawOption[10..]
                        .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(entry => entry.IndexOf('/') < 0)
                        .ToArray();
                    if (!ParseDomainList(string.Join('|', denyAllowValue), '|', denyIncluded, denyExcluded))
                    {
                        return null;
                    }
                    // $denyallow excludes specific request target hosts from a broader
                    // block rule (matching uBO/AdGuard $denyallow semantics).
                    // "$denyallow=a.com" means requests to a.com are not blocked;
                    // "$denyallow=~b.com" keeps b.com out of the exclusion set.
                    if (denyIncluded.Count > 0)
                    {
                        excludedTargetDomains ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var domain in denyIncluded) excludedTargetDomains.Add(domain);
                    }
                    continue;
                }
                // Response-rewrite directives. This engine cancels requests; it
                // never rewrites responses. Treating a rewrite as a block is the
                // one thing that must never happen, because a pixel swap
                // becomes a broken image and a noop script becomes a thrown
                // exception. Drop the rule and count it as unsupported instead.
                if (option.StartsWith("redirect-rule=", StringComparison.Ordinal)
                    || option.StartsWith("redirect=", StringComparison.Ordinal)
                    || option.StartsWith("priority=", StringComparison.Ordinal)
                    || option is "empty" or "mp4" or "all")
                {
                    return null;
                }

                var excluded = option.StartsWith('~');
                var name = excluded ? option[1..] : option;
                if (excluded)
                {
                    if (!AddMappedTypes(name, ref excludedTypeMask)) return null;
                }
                else if (!AddMappedTypes(name, ref includedTypeMask))
                {
                    return null;
                }
            }

            uint? includedTypes = includedTypeMask == 0 ? null : includedTypeMask;
            var host = ExtractHost(pattern, out var patternHostEnd);
            Regex? regex = null;
            uint? regexIndexKey = null;
            var regexIsFourByte = false;
            if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
            {
                var regexPattern = pattern[1..^1];
                if (TryExtractMandatoryRegexLiteral(regexPattern, out var literal))
                {
                    if (literal.Length >= 4)
                    {
                        var bestOffset = SelectBestFourCharOffset(literal.AsSpan());
                        regexIndexKey = PackIndexKey4(literal.AsSpan(), bestOffset);
                        regexIsFourByte = true;
                    }
                    else
                    {
                        regexIndexKey = PackIndexKey3(literal.AsSpan(), 0);
                    }
                }
                var options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
                if (!matchCase) options |= RegexOptions.IgnoreCase;
                try
                {
                    regex = new Regex(regexPattern, options | RegexOptions.NonBacktracking, RegexMatchTimeout);
                }
                catch (NotSupportedException)
                {
                    try
                    {
                        regex = new Regex(regexPattern, options, RegexMatchTimeout);
                    }
                    catch
                    {
                        return null;
                    }
                }
                catch
                {
                    return null;
                }
            }

            return new FilterRule(
                pattern,
                host,
                patternHostEnd,
                exception,
                important,
                regex,
                regexIndexKey,
                regexIsFourByte,
                includedTypes,
                excludedTypeMask,
                ToDomainArray(includedDomains),
                ToDomainArray(excludedDomains),
                ToDomainArray(includedTargetDomains),
                ToDomainArray(excludedTargetDomains),
                thirdPartyOnly,
                firstPartyOnly,
                matchCase,
                sourceCategory);
        }

        public bool Matches(
            Uri requestUri,
            Uri? sourceUri,
            AdBlockResourceType resourceType,
            ref int thirdPartyState)
        {
            string? pathAndQuery = null;
            return Matches(requestUri, sourceUri, resourceType, ref thirdPartyState, ref pathAndQuery);
        }

        public bool Matches(
            Uri requestUri,
            Uri? sourceUri,
            AdBlockResourceType resourceType,
            ref int thirdPartyState,
            ref string? pathAndQuery)
        {
            var resourceMask = 1u << (int)resourceType;
            if (includedTypes is { } included && (included & resourceMask) == 0) return false;
            if ((excludedTypes & resourceMask) != 0) return false;

            if (ThirdPartyOnly || FirstPartyOnly)
            {
                if (thirdPartyState < 0)
                {
                    thirdPartyState = sourceUri is null
                        || !AreSameSite(requestUri.Host, sourceUri.Host)
                        ? 1
                        : 0;
                }
                var thirdParty = thirdPartyState == 1;
                if (ThirdPartyOnly && !thirdParty) return false;
                if (FirstPartyOnly && thirdParty) return false;
            }

            if (includedDomains.Length > 0
                && (sourceUri is null || !MatchesAnyHost(sourceUri.Host, includedDomains)))
            {
                return false;
            }
            if (sourceUri is not null && MatchesAnyHost(sourceUri.Host, excludedDomains)) return false;
            if (includedTargetDomains.Length > 0
                && !MatchesAnyHost(requestUri.Host, includedTargetDomains))
            {
                return false;
            }
            if (MatchesAnyHost(requestUri.Host, excludedTargetDomains)) return false;

            if (IsRegex)
            {
                try { return regex!.IsMatch(requestUri.AbsoluteUri); }
                catch (RegexMatchTimeoutException) { return false; }
            }

            if (hostAnchoredRule)
            {
                if (!hostMatchGuaranteedByIndex
                    && (IndexHost is null || !HostMatches(requestUri.Host, IndexHost)))
                {
                    return false;
                }
                if (hostPathPattern is null) return true;
                pathAndQuery ??= requestUri.PathAndQuery;
                return GlobMatches(hostPathPattern.AsSpan(), pathAndQuery.AsSpan(), matchCase);
            }

            if (useHostAndPathTarget)
            {
                ReadOnlySpan<char> hostAndPath;
                int hostEnd;
                if (requestUri.IsDefaultPort && requestUri.UserInfo.Length == 0 && requestUri.Fragment.Length == 0)
                {
                    var absoluteUri = requestUri.AbsoluteUri;
                    var schemeSeparator = absoluteUri.IndexOf("://", StringComparison.Ordinal);
                    if (schemeSeparator >= 0)
                    {
                        hostAndPath = absoluteUri.AsSpan(schemeSeparator + 3);
                        hostEnd = hostAndPath.IndexOf('/');
                        if (hostEnd < 0) hostEnd = hostAndPath.Length;
                    }
                    else
                    {
                        var host = requestUri.Host;
                        pathAndQuery ??= requestUri.PathAndQuery;
                        hostAndPath = string.Concat(host, pathAndQuery).AsSpan();
                        hostEnd = host.Length;
                    }
                }
                else
                {
                    var host = requestUri.Host;
                    pathAndQuery ??= requestUri.PathAndQuery;
                    hostAndPath = string.Concat(host, pathAndQuery).AsSpan();
                    hostEnd = host.Length;
                }

                var patternSpan = networkPattern!.AsSpan();
                if (GlobMatches(patternSpan, hostAndPath, matchCase)) return true;
                for (var index = 0; index < hostEnd; index++)
                {
                    if (hostAndPath[index] == '.'
                        && index + 1 < hostEnd
                        && GlobMatches(patternSpan, hostAndPath[(index + 1)..], matchCase))
                    {
                        return true;
                    }
                }
                return false;
            }

            return GlobMatches(networkPattern!.AsSpan(), requestUri.AbsoluteUri.AsSpan(), matchCase);
        }

        private static bool AddMappedTypes(string option, ref uint output)
        {
            switch (option)
            {
                case "document":
                case "doc":
                case "popup":
                case "popunder":
                case "main_frame":
                    output |= ResourceMask(AdBlockResourceType.Document);
                    return true;
                case "subdocument":
                case "sub_frame":
                case "frame":
                    output |= ResourceMask(AdBlockResourceType.SubDocument);
                    return true;
                case "script": output |= ResourceMask(AdBlockResourceType.Script); return true;
                case "image":
                case "image-set": output |= ResourceMask(AdBlockResourceType.Image); return true;
                case "stylesheet":
                case "css": output |= ResourceMask(AdBlockResourceType.Stylesheet); return true;
                case "media":
                case "texttrack":
                    output |= ResourceMask(AdBlockResourceType.Media);
                    return true;
                case "eventsource":
                    // Must agree with how MapResourceType files a live request:
                    // an EventSource request is classified XmlHttpRequest, so
                    // mapping this name to Media would compile a rule that can
                    // never match.
                    output |= ResourceMask(AdBlockResourceType.XmlHttpRequest);
                    return true;
                case "font": output |= ResourceMask(AdBlockResourceType.Font); return true;
                case "xmlhttprequest":
                case "xhr":
                    output |= ResourceMask(AdBlockResourceType.XmlHttpRequest)
                        | ResourceMask(AdBlockResourceType.Fetch);
                    return true;
                case "fetch": output |= ResourceMask(AdBlockResourceType.Fetch); return true;
                case "websocket": output |= ResourceMask(AdBlockResourceType.WebSocket); return true;
                case "ping":
                case "beacon": output |= ResourceMask(AdBlockResourceType.Ping); return true;
                case "object":
                case "object-subrequest":
                case "other": output |= ResourceMask(AdBlockResourceType.Other); return true;
                default: return false;
            }
        }

        private static uint ResourceMask(AdBlockResourceType resourceType) => 1u << (int)resourceType;

        private static string[] ToDomainArray(HashSet<string>? domains) =>
            domains is { Count: > 0 } ? domains.ToArray() : Array.Empty<string>();

        internal static bool TryGetRegexPattern(string input, out string regexPattern)
        {
            regexPattern = string.Empty;
            if (string.IsNullOrWhiteSpace(input)) return false;
            var line = input.Trim();
            if (line.StartsWith("@@", StringComparison.Ordinal)) line = line[2..];
            var optionIndex = FindOptionIndex(line);
            var pattern = optionIndex < 0 ? line : line[..optionIndex];
            if (pattern.Length <= 2 || pattern[0] != '/' || pattern[^1] != '/') return false;
            regexPattern = pattern[1..^1];
            return true;
        }

    /// <summary>
    /// Splits a rule's option text on commas, except inside a $denyallow value.
    /// uBO accepts both "|" and "," there, but a plain comma split truncates the
    /// value and leaves its tail looking like a separate option, which then fails
    /// option mapping and silently deletes the whole rule. Keeping the value
    /// intact lets ParseDomainList handle both separators.
    /// </summary>
    internal static List<string> SplitFilterOptions(string optionText)
    {
        var options = new List<string>(4);
        // $denyallow is the one option whose value legitimately contains commas.
        // Its value absorbs the following tokens until a token that is itself a
        // "name=" option appears, so a trailing $third-party or type mask is not
        // swallowed and does not become a denyallow domain.
        var tokens = optionText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith("denyallow=", StringComparison.OrdinalIgnoreCase))
            {
                options.Add(token);
                continue;
            }
            // Absorb the following tokens as value continuations. They belong to
            // this list until a token that is itself a "name=" option appears, so a
            // trailing $third-party or type mask is not swallowed. Advance the
            // cursor past what was consumed so it is not also emitted on its own.
            var value = new StringBuilder(token[10..]);
            var consumed = index;
            for (var next = index + 1; next < tokens.Length; next++)
            {
                if (!IsDenyAllowValueContinuation(tokens[next])) break;
                value.Append('|').Append(tokens[next]);
                consumed = next;
            }
            options.Add("denyallow=" + value);
            index = consumed;
        }
        return options;
    }

    /// <summary>
    /// Valueless flags the option loop matches by literal comparison, as opposed
    /// to the resource types it maps through AddMappedTypes. A $denyallow value
    /// continues until one of these or a mapped resource type appears.
    /// </summary>
    internal static readonly HashSet<string> ValuelessOptionFlags =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "important", "match-case", "~match-case", "third-party", "3p", "strict3p", "~third-party",
        "1p", "first-party", "strict1p", "~first-party", "all", "empty", "mp4",
        "generichide", "specifichide", "elemhide", "genericblock", "badfilter",
        "redirect", "redirect-rule", "priority"
    };

    /// <summary>
    /// True when AddMappedTypes accepts this name, matched case-insensitively to
    /// match how the option loop lower-cases options before parsing. Delegating
    /// to the parser is what keeps this boundary from drifting away from it: a
    /// name accepted here but rejected there would truncate a $denyallow value,
    /// and the leftover token would then fail option mapping and delete the rule.
    /// </summary>
    private static bool IsSupportedResourceTypeName(string name)
    {
        var probe = 0u;
        return AddMappedTypes(name.ToLowerInvariant(), ref probe);
    }

    /// <summary>
    /// True when the option loop would handle this token, which is what makes it
    /// the end of a $denyallow value. This asks the parser rather than consulting
    /// a hand-maintained list of names, so a token the loop accepts always
    /// terminates a value and a token it does not accept never does. A previous
    /// hand-written mirror had drifted to list options the loop rejects, which
    /// meant a value ending in one of them was cut short and the leftover token
    /// failed option mapping and deleted the whole rule.
    /// </summary>
    internal static bool IsRecognizedBareOption(string token)
    {
        if (token.Length == 0) return false;

        // Anything carrying a value is a named option, handled before the
        // resource-type mapping, so it terminates a value.
        if (token.IndexOf('=') >= 0) return true;

        if (ValuelessOptionFlags.Contains(token)) return true;
        if (IsSupportedResourceTypeName(token)) return true;

        // A "~" prefix negates a type, so "~script" is a token the option loop
        // handles. Keeping the boundary symmetric avoids a value ending in a
        // negated type being absorbed and parsed as a domain.
        return token.StartsWith('~') && IsSupportedResourceTypeName(token[1..]);
    }

    /// <summary>
    /// True when a token continues a $denyallow value rather than starting a new
    /// option. Anything the parser does not recognize continues the value, which
    /// keeps single-label hosts, CIDR blocks and plain domains in the list
    /// instead of letting them fall through as separate options and delete the
    /// rule by parse failure.
    /// </summary>
    private static bool IsDenyAllowValueContinuation(string token) =>
        token.Length > 0 && !IsRecognizedBareOption(token);

    /// <summary>
    /// Extracts the host of a "||host" rule. <paramref name="hostEnd"/> is the
    /// offset just past the raw host text in <paramref name="pattern"/>, which
    /// keeps any dot padding, so callers can slice the path remainder without
    /// re-deriving the length. Slicing with <c>host.Length</c> instead is off by
    /// the number of trimmed dots and produced garbage patterns such as
    /// "m/banner" for "||.ads.example.com/banner".
    /// </summary>
    private static string? ExtractHost(string pattern, out int hostEnd)
    {
        hostEnd = 0;
        if (!pattern.StartsWith("||", StringComparison.Ordinal)) return null;
        var end = pattern.IndexOfAny(['/', '^', '*', '|', '?', '$'], 2);
        // Require a true host-end boundary ('/', '^', or '|'). A missing boundary
        // or a segment ending in '.' (e.g., "||ads." or "||analytics.") is a
        // prefix/glob pattern, not a complete host, so it must fall back to
        // useHostAndPathTarget rather than turning "ads." into TLD "ads".
        if (end < 0 || pattern[end] is not ('/' or '^' or '|')) return null;
        var rawHost = pattern.AsSpan(2, end - 2);
        if (rawHost.EndsWith('.')) return null;
        var host = rawHost.Trim('.').ToString();
        // Single-label hosts such as "localhost" or "intranet" are valid ||host
        // anchors. Rejecting them pushed the rule onto the generic "host^*"
        // path, where '.' is not a separator, so the rule matched neither the
        // host nor any of its subdomains.
        if (host.Length == 0) return null;
        var hostNameType = Uri.CheckHostName(host);
        if (hostNameType is not (UriHostNameType.Dns or UriHostNameType.Basic)) return null;
        hostEnd = end;
        return host;
    }

        private static uint? ExtractIndexKey(string pattern, out bool isFourByte)
        {
            var longestStart = -1;
            var longestLength = 0;
            var bestThreeStart = -1;
            var bestFourStart = -1;
            var bestFourScore = int.MinValue;
            var index = 0;
            var schemePrefixLength = 0;
            if (pattern.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                schemePrefixLength = 5; // "https"
            }
            else if (pattern.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                schemePrefixLength = 4; // "http"
            }

            while (index < pattern.Length)
            {
                if (!IsIndexLiteralCharacter(pattern[index]))
                {
                    index++;
                    continue;
                }

                var start = index++;
                while (index < pattern.Length && IsIndexLiteralCharacter(pattern[index])) index++;
                if (start == 0 && schemePrefixLength > 0 && index - start == schemePrefixLength)
                {
                    continue;
                }
                var length = index - start;
                if (length >= 4)
                {
                    var runSpan = pattern.AsSpan(start, length);
                    var offset = SelectBestFourCharOffset(runSpan);
                    var score = (length * 16) + ScoreFourCharToken(runSpan.Slice(offset, 4));
                    if (score > bestFourScore)
                    {
                        bestFourScore = score;
                        bestFourStart = start + offset;
                    }
                }
                if (length >= 3 && length > longestLength)
                {
                    longestStart = start;
                    longestLength = length;
                    bestThreeStart = start;
                }
            }

            if (bestFourStart >= 0)
            {
                isFourByte = true;
                return PackIndexKey4(pattern.AsSpan(), bestFourStart);
            }

            if (bestThreeStart < 0 && schemePrefixLength >= 4)
            {
                isFourByte = true;
                return PackIndexKey4(pattern.AsSpan(), 0);
            }

            isFourByte = false;
            if (bestThreeStart < 0 && schemePrefixLength >= 3)
            {
                return PackIndexKey3(pattern.AsSpan(), 0);
            }

            return longestStart < 0 ? null : PackIndexKey3(pattern.AsSpan(), bestThreeStart);
        }

        private static int SelectBestFourCharOffset(ReadOnlySpan<char> literal)
        {
            if (literal.Length <= 4) return 0;
            var bestOffset = 0;
            var bestScore = int.MinValue;
            for (var offset = 0; offset <= literal.Length - 4; offset++)
            {
                var score = ScoreFourCharToken(literal.Slice(offset, 4));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOffset = offset;
                }
            }
            return bestOffset;
        }

        private static int ScoreFourCharToken(ReadOnlySpan<char> window)
        {
            var score = 0;
            var alphaNumCount = 0;
            foreach (var ch in window)
            {
                if (char.IsLetterOrDigit(ch)) alphaNumCount++;
            }
            score += alphaNumCount * 4;
            if (window.Equals("www.", StringComparison.OrdinalIgnoreCase)
                || window.Equals(".com", StringComparison.OrdinalIgnoreCase)
                || window.Equals(".net", StringComparison.OrdinalIgnoreCase)
                || window.Equals(".org", StringComparison.OrdinalIgnoreCase)
                || window.Equals("http", StringComparison.OrdinalIgnoreCase)
                || window.Equals("html", StringComparison.OrdinalIgnoreCase)
                || window.Equals("ttps", StringComparison.OrdinalIgnoreCase)
                || window.Equals(".php", StringComparison.OrdinalIgnoreCase)
                || window.Equals("cdn.", StringComparison.OrdinalIgnoreCase)
                || window.Equals("ads.", StringComparison.OrdinalIgnoreCase))
            {
                score -= 100;
            }
            return score;
        }

        internal static bool TryExtractMandatoryRegexLiteral(
            string pattern,
            out string literal)
        {
            literal = string.Empty;
            var current = new StringBuilder();
            var best = string.Empty;
            var groupDepth = 0;
            var inCharacterClass = false;
            var lastTopLevelAtomWasLiteral = false;
            var lastTokenWasQuantifier = false;

            void CommitCurrent()
            {
                if (current.Length > best.Length) best = current.ToString();
            }

            void BreakCurrent()
            {
                CommitCurrent();
                current.Clear();
                lastTopLevelAtomWasLiteral = false;
                lastTokenWasQuantifier = false;
            }

            for (var index = 0; index < pattern.Length; index++)
            {
                var value = pattern[index];
                if (inCharacterClass)
                {
                    if (value == '\\' && index + 1 < pattern.Length) index++;
                    else if (value == ']') inCharacterClass = false;
                    continue;
                }

                if (value == '[')
                {
                    if (groupDepth == 0) BreakCurrent();
                    inCharacterClass = true;
                    continue;
                }
                if (value == '(')
                {
                    if (groupDepth == 0) BreakCurrent();
                    groupDepth++;
                    continue;
                }
                if (value == ')')
                {
                    if (groupDepth > 0) groupDepth--;
                    if (groupDepth == 0) BreakCurrent();
                    continue;
                }
                if (groupDepth > 0)
                {
                    if (value == '\\' && index + 1 < pattern.Length) index++;
                    continue;
                }
                if (value == '|') return false;

                if (value == '\\')
                {
                    if (index + 1 >= pattern.Length) return false;
                    var escapeType = pattern[index + 1];
                    if (char.IsDigit(escapeType)
                        || escapeType is 'c' or 'p' or 'P' or 'k')
                    {
                        // Backreferences and escapes with a syntactic payload can
                        // contain letters that never occur in the matched text.
                        return false;
                    }
                    if (escapeType is 'd' or 'D' or 's' or 'S' or 'w' or 'W'
                        or 'b' or 'B' or 'A' or 'Z' or 'z' or 'G'
                        or 'a' or 'e' or 'f' or 'n' or 'r' or 't' or 'v')
                    {
                        index++;
                        BreakCurrent();
                        continue;
                    }
                    if (!TryReadRegexLiteralEscape(pattern, ref index, out var escaped)
                        || !IsIndexLiteralCharacter(escaped))
                    {
                        BreakCurrent();
                        continue;
                    }
                    current.Append(escaped);
                    lastTopLevelAtomWasLiteral = true;
                    lastTokenWasQuantifier = false;
                    continue;
                }

                if (value is '?' or '*')
                {
                    if (value == '?' && lastTokenWasQuantifier)
                    {
                        lastTokenWasQuantifier = false;
                        lastTopLevelAtomWasLiteral = false;
                        continue;
                    }
                    if (lastTopLevelAtomWasLiteral && current.Length > 0) current.Length--;
                    BreakCurrent();
                    lastTokenWasQuantifier = true;
                    continue;
                }
                if (value == '+')
                {
                    BreakCurrent();
                    lastTokenWasQuantifier = true;
                    continue;
                }
                if (value == '{')
                {
                    var closingBrace = pattern.IndexOf('}', index + 1);
                    if (closingBrace < 0)
                    {
                        BreakCurrent();
                        continue;
                    }
                    var quantifier = pattern.AsSpan(index + 1, closingBrace - index - 1);
                    var comma = quantifier.IndexOf(',');
                    var minimumText = comma < 0 ? quantifier : quantifier[..comma];
                    if (!int.TryParse(minimumText, NumberStyles.None, CultureInfo.InvariantCulture, out var minimum))
                    {
                        BreakCurrent();
                        index = closingBrace;
                        continue;
                    }
                    if (minimum == 0 && lastTopLevelAtomWasLiteral && current.Length > 0)
                    {
                        current.Length--;
                    }
                    BreakCurrent();
                    lastTokenWasQuantifier = true;
                    index = closingBrace;
                    continue;
                }

                if (!IsIndexLiteralCharacter(value) || value == '.')
                {
                    BreakCurrent();
                    continue;
                }
                current.Append(value);
                lastTopLevelAtomWasLiteral = true;
                lastTokenWasQuantifier = false;
            }

            CommitCurrent();
            if (best.Length < 3) return false;
            literal = best;
            return true;
        }

        private static bool TryReadRegexLiteralEscape(
            string pattern,
            ref int index,
            out char literal)
        {
            literal = default;
            if (++index >= pattern.Length) return false;
            var escaped = pattern[index];
            if (escaped == 'x' && index + 2 < pattern.Length
                && int.TryParse(
                    pattern.AsSpan(index + 1, 2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var hexByte))
            {
                index += 2;
                literal = (char)hexByte;
                return true;
            }
            if (escaped == 'u' && index + 4 < pattern.Length
                && int.TryParse(
                    pattern.AsSpan(index + 1, 4),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var hexCharacter))
            {
                index += 4;
                literal = (char)hexCharacter;
                return true;
            }
            if (char.IsLetterOrDigit(escaped)) return false;
            literal = escaped;
            return true;
        }

        private static bool IsIndexLiteralCharacter(char value) =>
            value is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '%' or '.' or '-';

        private static bool GlobMatches(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text, bool matchCase)
        {
            var patternIndex = 0;
            var textIndex = 0;
            var starIndex = -1;
            var starTextIndex = -1;

            while (textIndex < text.Length)
            {
                if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    starIndex = patternIndex++;
                    starTextIndex = textIndex;
                    continue;
                }

                if (patternIndex < pattern.Length && pattern[patternIndex] == '^')
                {
                    if (IsSeparator(text[textIndex]))
                    {
                        patternIndex++;
                        textIndex++;
                        continue;
                    }
                }
                else if (patternIndex < pattern.Length
                    && pattern[patternIndex] != '|'
                    && CharactersEqual(pattern[patternIndex], text[textIndex], matchCase))
                {
                    patternIndex++;
                    textIndex++;
                    continue;
                }

                if (starIndex >= 0)
                {
                    patternIndex = starIndex + 1;
                    textIndex = ++starTextIndex;
                    continue;
                }
                return false;
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] is '*' or '^') patternIndex++;
            return patternIndex == pattern.Length;
        }

        private static bool CharactersEqual(char compiledPatternChar, char textChar, bool matchCase) =>
            matchCase
                ? compiledPatternChar == textChar
                : compiledPatternChar == ToLowerFast(textChar);

        private static char ToLowerFast(char value) =>
            value is >= 'A' and <= 'Z'
                ? (char)(value + ('a' - 'A'))
                : value < 128
                    ? value
                    : char.ToLowerInvariant(value);

        private static bool IsSeparator(char value) =>
            !char.IsLetterOrDigit(value) && value is not '_' and not '-' and not '.' and not '%';
    }

    private static int FindOptionIndex(string line)
    {
        var searchStart = line.StartsWith("@@", StringComparison.Ordinal) ? 2 : 0;
        if (searchStart < line.Length && line[searchStart] == '/')
        {
            if (line.Length > searchStart + 2 && line[^1] == '/')
            {
                return -1;
            }

            var regexOptionSeparator = line.LastIndexOf("/$", StringComparison.Ordinal);
            if (regexOptionSeparator > searchStart)
            {
                return regexOptionSeparator + 1;
            }

            var dollarIndex = line.LastIndexOf('$');
            return dollarIndex >= searchStart ? dollarIndex : -1;
        }
        return line.IndexOf('$', searchStart);
    }

    private static bool ParseDomainList(
        string input,
        char separator,
        ref HashSet<string>? included,
        ref HashSet<string>? excluded)
    {
        included ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        excluded ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ParseDomainList(input, separator, included, excluded);
    }

    private static bool ParseDomainList(
        string input,
        char separator,
        HashSet<string> included,
        HashSet<string> excluded)
    {
        foreach (var rawDomain in input.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var isExcluded = rawDomain.StartsWith('~');
            var domain = (isExcluded ? rawDomain[1..] : rawDomain).TrimStart('.').TrimEnd('.');
            if (domain == "*") continue;
            if (domain.Length == 0
                || domain.Contains('*')
                || (Uri.CheckHostName(domain) == UriHostNameType.Unknown && domain != "localhost"))
            {
                return false;
            }
            (isExcluded ? excluded : included).Add(domain);
        }
        return true;
    }

    private readonly record struct FilterHttpCacheValidators(
        string? EntityTag,
        DateTimeOffset? LastModified);

    private sealed record FilterListSource(string FileName, Uri Uri, FilterSourceCategory Category);
}
