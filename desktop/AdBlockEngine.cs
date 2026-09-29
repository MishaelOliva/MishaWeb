using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
    private static readonly FilterListSource[] FilterListSources =
    [
        new("list-0.txt", new("https://easylist.to/easylist/easylist.txt")),
        new("list-1.txt", new("https://easylist.to/easylist/easyprivacy.txt")),
        new("list-2.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters.txt")),
        new("list-3.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-general.txt")),
        new("list-4.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2026.txt")),
        new("list-5.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/quick-fixes.txt")),
        new("list-6.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/privacy.txt")),
        new("list-7.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/unbreak.txt")),
        new("list-8.txt", new("https://raw.githubusercontent.com/brave/adblock-lists/master/brave-lists/brave-specific.txt")),
        new("list-9.txt", new("https://raw.githubusercontent.com/brave/adblock-lists/master/brave-lists/brave-firstparty.txt")),
        new("list-10.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2020.txt")),
        new("list-11.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2021.txt")),
        new("list-12.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2022.txt")),
        new("list-13.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2023.txt")),
        new("list-14.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2024.txt")),
        new("list-15.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters-2025.txt")),
        new("list-16.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/ubo-link-shorteners.txt")),
        new("list-17.txt", new("https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/resource-abuse.txt")),
        new("list-18.txt", new("https://raw.githubusercontent.com/brave/adblock-lists/master/brave-unbreak.txt")),
        new("list-19.txt", new("https://raw.githubusercontent.com/brave/adblock-lists/master/brave-lists/brave-firstparty-regional.txt"))
    ];

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
        baselineRuleSet = CompiledRuleSet.FromLines(
            BuiltInFallbackRules.Concat(initialRules ?? Enumerable.Empty<string>()));
        ruleSet = baselineRuleSet;
    }

    public static string DocumentScript => AdBlockDocumentScript.Source;

    public static string CreateDocumentScript(string controlChannel) =>
        AdBlockDocumentScript.Source.Replace(
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
        var requestValid = cacheEvaluation
            ? TryGetCachedRequestUri(requestUrl, out var requestUri)
            : TryGetHttpUri(requestUrl, out requestUri);
        if (!requestValid || requestUri is null) return false;
        Uri? sourceUri;
        if (cacheEvaluation) TryGetCachedSourceUri(sourceUrl, out sourceUri);
        else TryGetHttpUri(sourceUrl, out sourceUri);
        return ruleSet.ShouldBlock(requestUri, sourceUri, resourceType);
    }

    internal string GetCosmeticCss(string pageUrl, bool cacheEvaluation = true)
    {
        var valid = cacheEvaluation
            ? TryGetCachedRequestUri(pageUrl, out var pageUri)
            : TryGetHttpUri(pageUrl, out pageUri);
        if (!valid || pageUri is null) return string.Empty;
        return ruleSet.GetCosmeticCss(pageUri, cacheEvaluation);
    }

    internal Task<string> GetCosmeticCssAsync(string pageUrl, bool cacheEvaluation = true) =>
        Task.Run(() => GetCosmeticCss(pageUrl, cacheEvaluation));

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

    private async Task LoadListsAsync(
        int generation,
        CancellationTokenSource generationCancellation)
    {
        var cancellationToken = generationCancellation.Token;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cachedFiles = GetCachedListFiles();
            if (cachedFiles.Count > 0)
            {
                PublishRuleSetIfCurrent(
                    generation,
                    await CompileRuleSetAsync(cachedFiles, cancellationToken).ConfigureAwait(false));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (updateRemoteLists)
            {
                var staleSources = GetSourcesNeedingRefresh();
                var contentChanged = staleSources.Count > 0
                    && await DownloadListsAsync(staleSources, cancellationToken).ConfigureAwait(false);
                if (contentChanged)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var refreshedFiles = GetCachedListFiles();
                    if (refreshedFiles.Count > 0)
                    {
                        PublishRuleSetIfCurrent(
                            generation,
                            await CompileRuleSetAsync(refreshedFiles, cancellationToken).ConfigureAwait(false));
                    }
                }
            }
        }
        catch
        {
            // The fallback set remains active when a list update is unavailable.
        }
        finally
        {
            lock (loadSync)
            {
                if (generation == loadGeneration)
                {
                    isReady = !cancellationToken.IsCancellationRequested;
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
        lock (loadSync)
        {
            if (generation == loadGeneration) ruleSet = compiled;
        }
    }

    private List<string> GetCachedListFiles()
    {
        SweepAbandonedTemporaryFiles();
        var files = new List<string>(FilterListSources.Length);
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
                    files.Add(path);
                    aggregateBytes += file.Length;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files;
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

    private Task<CompiledRuleSet> CompileRuleSetAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken) =>
        Task.Run(() => CompiledRuleSet.FromLines(
            BuiltInFallbackRules.Concat(ReadFilterLines(files, cancellationToken)),
            cancellationToken), cancellationToken);

    private static IEnumerable<string> ReadFilterLines(
        IEnumerable<string> files,
        CancellationToken cancellationToken)
    {
        var sourceFiles = files.ToArray();
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
            foreach (var line in CompiledRuleSet.PreprocessLines(
                         ReadFilterFileLines(sourceFiles[sourceIndex], cancellationToken)))
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
                yield return trimmed;
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
            reader = new StreamReader(
                file,
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
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Uri);
            if (cachedValidators.EntityTag is { } entityTag
                && EntityTagHeaderValue.TryParse(entityTag, out var parsedEntityTag))
            {
                request.Headers.IfNoneMatch.Add(parsedEntityTag);
            }
            if (cachedValidators.LastModified is { } lastModified)
            {
                request.Headers.IfModifiedSince = lastModified;
            }

            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            downloadTimeout.CancelAfter(FilterDownloadTimeout);
            var downloadToken = downloadTimeout.Token;
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
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

    private bool CanCommitFilterFile(string targetPath, string candidatePath)
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
                if (!cached.Exists) continue;
                if (cached.Length is <= 0 or > MaximumFilterListBytes) return false;
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
        var nonEmptyLines = 0;
        var hasFilterSyntax = false;
        foreach (var line in File.ReadLines(path))
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

    private static long PackIndexKey(string value, int offset) =>
        ((long)char.ToLowerInvariant(value[offset]) << 32)
        | ((long)char.ToLowerInvariant(value[offset + 1]) << 16)
        | char.ToLowerInvariant(value[offset + 2]);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MishaWeb", "2.2.0"));
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
        foreach (var key in cache.Keys.Take(toRemove).ToArray()) cache.Remove(key);
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
        private readonly RuleIndex blocks;
        private readonly RuleIndex exceptions;
        private readonly CosmeticRuleSet cosmetics;
        private readonly List<HideDisableRule> genericBlockExceptions;

        private CompiledRuleSet(
            RuleIndex blocks,
            RuleIndex exceptions,
            CosmeticRuleSet cosmetics,
            List<HideDisableRule> genericBlockExceptions,
            AdBlockCompileDiagnostics diagnostics)
        {
            this.blocks = blocks;
            this.exceptions = exceptions;
            this.cosmetics = cosmetics;
            this.genericBlockExceptions = genericBlockExceptions;
            Diagnostics = diagnostics;
        }

        public AdBlockCompileDiagnostics Diagnostics { get; }

        public static CompiledRuleSet FromLines(
            IEnumerable<string> lines,
            CancellationToken cancellationToken = default)
        {
            var materialized = new HashSet<string>(StringComparer.Ordinal);
            var processedLineCount = 0;
            foreach (var line in PreprocessLines(lines))
            {
                if ((processedLineCount++ & 2047) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] is '!' or '[') continue;
                materialized.Add(trimmed);
                if (materialized.Count >= MaximumMaterializedRules) break;
            }

            var disabledRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            processedLineCount = 0;
            foreach (var line in materialized)
            {
                if ((processedLineCount++ & 2047) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var disabled = GetBadFilterTarget(line);
                if (disabled is not null) disabledRules.Add(GetRuleIdentity(disabled));
            }
            var blocks = new RuleIndex();
            var exceptions = new RuleIndex();
            var cosmetics = new CosmeticRuleSet();
            var genericBlockExceptions = new List<HideDisableRule>();
            var networkRuleCount = 0;
            var cosmeticRuleCount = 0;
            var hideDisableRuleCount = 0;
            var disabledRuleCount = 0;
            var unsupportedRuleCandidateCount = 0;
            var capacityDroppedNetworkRuleCount = 0;
            var regexRuleCount = 0;
            var indexedRegexRuleCount = 0;
            var unindexedGenericRegexBlockRuleCount = 0;
            var unindexedGenericRegexExceptionRuleCount = 0;
            var unindexedSourceScopedRegexBlockRuleCount = 0;
            var unindexedSourceScopedRegexExceptionRuleCount = 0;
            var droppedRegexRuleCount = 0;

            processedLineCount = 0;
            foreach (var line in EnumerateRulesWithRegexExceptionsFirst(materialized))
            {
                if ((processedLineCount++ & 2047) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // $badfilter has to be resolved before the cosmetic and
                // hide-disable parsers run. Those parsers treat everything after
                // "##" as a selector, so "example.com##.ad$badfilter" used to be
                // installed as a live rule hiding ".ad$badfilter" while the real
                // "example.com##.ad" rule stayed active.
                //
                // Building the identity key allocates a split array, a LINQ chain
                // and a StringBuilder. Most lists contain no badfilter at all, and
                // the full input is ~200k rules, so skip the whole thing when
                // there is nothing to match against.
                if (disabledRules.Count > 0
                    && (GetBadFilterTarget(line) is not null
                        || disabledRules.Contains(GetRuleIdentity(line))))
                {
                    disabledRuleCount++;
                    continue;
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
                        genericBlockExceptions.Add(hideDisable);
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
                var rule = FilterRule.Parse(line);
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
                if ((rule.Exception ? exceptions : blocks).Add(rule))
                {
                    networkRuleCount++;
                }
                else
                {
                    capacityDroppedNetworkRuleCount++;
                }
            }

            blocks.Seal();
            exceptions.Seal();
            cosmetics.Seal();
            genericBlockExceptions.TrimExcess();

            return new CompiledRuleSet(
                blocks,
                exceptions,
                cosmetics,
                genericBlockExceptions,
                new AdBlockCompileDiagnostics(
                    materialized.Count,
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

        private static IEnumerable<string> EnumerateRulesWithRegexExceptionsFirst(
            HashSet<string> materialized)
        {
            // Exception rules must consume the bounded regex budget before block
            // rules. Otherwise a list that fills the cap can silently discard a
            // later @@ rule and broaden blocking beyond the authored policy.
            foreach (var line in materialized)
            {
                if (line.StartsWith("@@", StringComparison.Ordinal)
                    && FilterRule.TryGetRegexPattern(line, out _))
                {
                    yield return line;
                }
            }
            foreach (var line in materialized)
            {
                if (!line.StartsWith("@@", StringComparison.Ordinal)
                    || !FilterRule.TryGetRegexPattern(line, out _))
                {
                    yield return line;
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
            var orTerms = expression.Split("||", StringSplitOptions.TrimEntries);
            return orTerms.Any(orTerm =>
                orTerm.Split("&&", StringSplitOptions.TrimEntries)
                    .All(EvaluateConditionAtom));
        }

        private static bool EvaluateConditionAtom(string expression)
        {
            var atom = expression.Trim().Trim('(', ')');
            var negate = false;
            while (atom.StartsWith('!'))
            {
                negate = !negate;
                atom = atom[1..].TrimStart();
            }

            var value = atom.Equals("env_chromium", StringComparison.OrdinalIgnoreCase);
            return negate ? !value : value;
        }

        private readonly record struct ConditionalFrame(
            bool ParentActive,
            bool Condition,
            bool ElseSeen);

        public bool ShouldBlock(Uri requestUri, Uri? sourceUri, AdBlockResourceType resourceType)
        {
            var suppressGenericBlocking = false;
            if (sourceUri is not null)
            {
                foreach (var rule in genericBlockExceptions)
                {
                    if (!rule.Matches(sourceUri)) continue;
                    suppressGenericBlocking = true;
                    break;
                }
            }

            var thirdParty = sourceUri is null
                || !AreSameSite(requestUri.Host, sourceUri.Host);
            var matchedBlock = false;
            var matchedImportantBlock = false;
            foreach (var rule in blocks.GetCandidates(requestUri))
            {
                if ((suppressGenericBlocking && rule.IsGeneric)
                    || !rule.Matches(requestUri, sourceUri, resourceType, thirdParty))
                {
                    continue;
                }

                matchedBlock = true;
                if (!rule.Important) continue;
                matchedImportantBlock = true;
                break;
            }
            if (!matchedBlock) return false;

            foreach (var rule in exceptions.GetCandidates(requestUri))
            {
                if (matchedImportantBlock && !rule.Important) continue;
                if (rule.Matches(requestUri, sourceUri, resourceType, thirdParty)) return false;
            }
            return true;
        }

        public string GetCosmeticCss(Uri pageUri, bool cacheEvaluation) =>
            cosmetics.GetCss(pageUri, cacheEvaluation);

        /// <summary>
        /// Builds a comparison key for a filter line: the pattern plus its
        /// options in a canonical, order-insensitive form. Filter lists routinely
        /// reorder options between a rule and its $badfilter counterpart
        /// (<c>||x^$domain=a.com,script</c> vs <c>@@||x^$script,domain=a.com</c>).
        /// Comparing raw text silently failed to disable the block in that case,
        /// so both sides are normalized here.
        /// </summary>
        internal static string GetRuleIdentity(string line)
        {
            var optionIndex = FindOptionIndex(line);
            if (optionIndex < 0) return line.Trim();
            var pattern = line.AsSpan(0, optionIndex).Trim();

            // Callers skip this entirely when no badfilter is present, so the cost
            // here is only paid by lists that actually need identity comparison. A
            // hand-rolled span sort was tried here and removed: it was measurably
            // faster but its growth path was wrong, and it silently aborted the
            // whole compile on the real 20-list catalog. Correctness wins.
            var options = line[(optionIndex + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (options.Length == 0) return pattern.ToString();
            if (options.Length == 1) return string.Concat(pattern, "|", options[0].ToLowerInvariant());

            Array.Sort(options, StringComparer.Ordinal);
            var builder = new StringBuilder(pattern.Length + (options.Length * 12) + 8);
            builder.Append(pattern);
            foreach (var option in options)
            {
                builder.Append('|').Append(option.ToLowerInvariant());
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
        [ThreadStatic] private static HashSet<long>? reusableIndexKeys;
        [ThreadStatic] private static bool reusableIndexKeysInUse;
        private readonly Dictionary<string, FilterRule> singleHostRules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<FilterRule>> multipleHostRules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, FilterRule> singleIndexedRules = [];
        private readonly Dictionary<long, List<FilterRule>> multipleIndexedRules = [];
        private readonly List<FilterRule> unindexedRules = [];
        private int indexedRuleCount;

        public bool Add(FilterRule rule)
        {
            if (rule.IndexHost is { } indexHost)
            {
                if (singleHostRules.Remove(indexHost, out var firstRule))
                {
                    multipleHostRules[indexHost] = [firstRule, rule];
                }
                else if (multipleHostRules.TryGetValue(indexHost, out var rules))
                {
                    rules.Add(rule);
                }
                else
                {
                    singleHostRules[indexHost] = rule;
                }
                // The dictionary key is now the canonical retained host. Every lookup
                // reaches this list only while walking a matching request-host suffix,
                // so individual rules do not need a duplicate host string or a second
                // suffix comparison for each request.
                rule.ReleaseIndexHost();
                return true;
            }

            if (rule.IndexKey is { } indexKey && indexedRuleCount < MaximumIndexedRules)
            {
                if (singleIndexedRules.Remove(indexKey, out var firstRule))
                {
                    multipleIndexedRules[indexKey] = [firstRule, rule];
                }
                else if (multipleIndexedRules.TryGetValue(indexKey, out var rules))
                {
                    rules.Add(rule);
                }
                else
                {
                    singleIndexedRules[indexKey] = rule;
                }
                indexedRuleCount++;
                return true;
            }
            else if (unindexedRules.Count < MaximumUnindexedRules)
            {
                unindexedRules.Add(rule);
                return true;
            }
            return false;
        }

        public void Seal()
        {
            unindexedRules.TrimExcess();
        }

        // A pattern-based struct enumerator keeps the per-request rule walk off
        // the managed heap. The candidate order is identical to the former
        // iterator: host rules, indexed rules, then bounded unindexed rules.
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
            private readonly HashSet<long> seenKeys;
            private readonly bool borrowedReusableSet;
            private List<FilterRule>? currentRules;
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
                    ? reusableIndexKeys ??= new HashSet<long>()
                    : new HashSet<long>();
                if (borrowedReusableSet) reusableIndexKeysInUse = true;
                currentRules = null;
                currentRuleIndex = -1;
                hostIndex = 0;
                targetIndex = 0;
                unindexedRuleIndex = 0;
                phase = 0;
                disposed = false;
                Current = null!;
            }

            public FilterRule Current { get; private set; }

            public bool MoveNext()
            {
                while (true)
                {
                    if (currentRules is not null
                        && ++currentRuleIndex < currentRules.Count)
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
                            if (owner.singleHostRules.TryGetValue(candidateHost, out var singleRule))
                            {
                                Current = singleRule;
                                return true;
                            }
                            if (!owner.multipleHostRules.TryGetValue(candidateHost, out currentRules)
                                || currentRules.Count == 0)
                            {
                                currentRules = null;
                                continue;
                            }
                            currentRuleIndex = -1;
                            break;
                        }
                        if (currentRules is not null) continue;
                        phase = 1;
                    }

                    if (phase == 1)
                    {
                        while (targetIndex <= target.Length - 3)
                        {
                            var key = PackIndexKey(target, targetIndex++);
                            if (!seenKeys.Add(key)) continue;
                            if (owner.singleIndexedRules.TryGetValue(key, out var singleRule))
                            {
                                Current = singleRule;
                                return true;
                            }
                            if (!owner.multipleIndexedRules.TryGetValue(key, out currentRules)
                                || currentRules.Count == 0)
                            {
                                currentRules = null;
                                continue;
                            }
                            currentRuleIndex = -1;
                            break;
                        }
                        if (currentRules is not null) continue;
                        phase = 2;
                    }

                    if (phase == 2 && unindexedRuleIndex < owner.unindexedRules.Count)
                    {
                        Current = owner.unindexedRules[unindexedRuleIndex++];
                        return true;
                    }

                    phase = 3;
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
        private readonly List<CosmeticRule> genericRules = [];
        private readonly Dictionary<string, List<CosmeticRule>> domainRules = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> styleCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> styleCacheOrder = new();
        private readonly List<HideDisableRule> genericHideExceptions = [];
        private readonly List<HideDisableRule> specificHideExceptions = [];
        private long styleCacheBytes;

        public void Add(CosmeticRule rule)
        {
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
            if (rule.DisableGenericHiding) genericHideExceptions.Add(rule);
            if (rule.DisableSpecificHiding) specificHideExceptions.Add(rule);
        }

        public void Seal()
        {
            genericRules.TrimExcess();
            genericHideExceptions.TrimExcess();
            specificHideExceptions.TrimExcess();
        }

        public string GetCss(Uri pageUri, bool cacheEvaluation)
        {
            if (!cacheEvaluation) return CompileCss(pageUri);
            var cacheKey = pageUri.GetLeftPart(UriPartial.Path) + pageUri.Query;
            if (styleCache.TryGetValue(cacheKey, out var cached)) return cached;
            var compiled = CompileCss(pageUri);
            if (!styleCache.TryAdd(cacheKey, compiled))
            {
                return styleCache.TryGetValue(cacheKey, out cached) ? cached : compiled;
            }

            Interlocked.Add(ref styleCacheBytes, GetCacheEntryBytes(cacheKey, compiled));
            styleCacheOrder.Enqueue(cacheKey);
            while ((styleCache.Count > MaximumCachedHosts
                    || Volatile.Read(ref styleCacheBytes) > MaximumCachedStyleBytes)
                && styleCacheOrder.TryDequeue(out var oldest))
            {
                if (styleCache.TryRemove(oldest, out var removed))
                {
                    Interlocked.Add(ref styleCacheBytes, -GetCacheEntryBytes(oldest, removed));
                }
            }
            return compiled;
        }

        private static long GetCacheEntryBytes(string key, string style) =>
            2L * (key.Length + style.Length);

        private string CompileCss(Uri pageUri)
        {
            var host = pageUri.Host;
            var disableGeneric = genericHideExceptions.Any(rule => rule.Matches(pageUri));
            var disableSpecific = specificHideExceptions.Any(rule => rule.Matches(pageUri));
            var exceptions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in GetCandidates(host))
            {
                if (rule.Exception && rule.Matches(host)) exceptions.Add(rule.Selector);
            }

            var css = new StringBuilder();
            var seenSelectors = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            foreach (var rule in GetCandidates(host))
            {
                if (rule.Exception
                    || !rule.Matches(host)
                    || exceptions.Contains(rule.Selector)
                    || (rule.IsGeneric && disableGeneric)
                    || (!rule.IsGeneric && disableSpecific)
                    || !seenSelectors.Add(rule.Selector))
                {
                    continue;
                }
                if (count++ >= MaximumSelectorsPerPage) break;
                var additionLength = rule.Selector.Length + 74;
                if (css.Length + additionLength > MaximumStyleBytes) break;
                css.Append(rule.Selector)
                    .Append("{display:none!important;visibility:hidden!important;min-height:0!important;}\n");
            }
            return css.ToString();
        }

        private IEnumerable<CosmeticRule> GetCandidates(string host)
        {
            // Specific selectors are more valuable than the generic tail and
            // must not be pushed past the per-page CSS safety budget.
            var labels = host.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < labels.Length; index++)
            {
                var domain = string.Join('.', labels.Skip(index));
                if (!domainRules.TryGetValue(domain, out var rules)) continue;
                foreach (var rule in rules) yield return rule;
            }
            foreach (var rule in genericRules) yield return rule;
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
        public bool DisableGenericHiding { get; }
        public bool DisableSpecificHiding { get; }
        public bool DisableGenericBlocking { get; }

        public bool Matches(Uri pageUri) =>
            matcher.Matches(pageUri, pageUri, AdBlockResourceType.Document, thirdParty: false);

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
            ":style(", ":watch-attr(", ":matches-attr(", ":matches-property("
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
            return !UnsupportedSelectorParts.Any(part => selector.Contains(part, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class FilterRule
    {
        private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(10);
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

        private FilterRule(
            string pattern,
            string? host,
            int patternHostEnd,
            bool exception,
            bool important,
            Regex? regex,
            long? regexIndexKey,
            uint? includedTypes,
            uint excludedTypes,
            string[] includedDomains,
            string[] excludedDomains,
            string[] includedTargetDomains,
            string[] excludedTargetDomains,
            bool thirdPartyOnly,
            bool firstPartyOnly,
            bool matchCase)
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
            IndexKey = regex is not null
                ? regexIndexKey
                : ExtractIndexKey(pattern);

            if (regex is not null) return;

            // Normalize anchors and globs once while compiling a list. Rebuilding
            // these short strings for every request was the largest allocation in
            // literal-rule matching on ad-heavy pages.
            var normalizedPattern = pattern;
            var hostAnchored = normalizedPattern.StartsWith("||", StringComparison.Ordinal);
            if (hostAnchored) normalizedPattern = normalizedPattern[2..];

            if (host is not null)
            {
                // normalizedPattern is pattern with the leading "||" removed, so
                // the host offset shifts by two. Slicing by host.Length instead
                // was off by the number of dots ExtractHost trimmed.
                var hostSpanEnd = Math.Min(patternHostEnd - 2, normalizedPattern.Length);
                var remainder = hostSpanEnd < 0
                    ? string.Empty
                    : normalizedPattern[hostSpanEnd..];
                if (remainder.Length == 0 || remainder == "^") return;

                var endAnchored = remainder.EndsWith('|');
                if (endAnchored) remainder = remainder[..^1];
                hostPathPattern = endAnchored ? remainder : remainder + '*';
                return;
            }

            useHostAndPathTarget = hostAnchored;
            var startAnchored = hostAnchored || normalizedPattern.StartsWith('|');
            if (normalizedPattern.StartsWith('|')) normalizedPattern = normalizedPattern[1..];
            var endAnchor = normalizedPattern.EndsWith('|');
            if (endAnchor) normalizedPattern = normalizedPattern[..^1];
            if (!startAnchored) normalizedPattern = '*' + normalizedPattern;
            if (!endAnchor) normalizedPattern += '*';
            networkPattern = normalizedPattern;
        }

        public string? IndexHost { get; private set; }
        public long? IndexKey { get; }
        public bool Exception { get; }
        public bool Important { get; }
        public bool ThirdPartyOnly { get; }
        public bool FirstPartyOnly { get; }
        public bool IsGeneric => includedDomains.Length == 0 && excludedDomains.Length == 0;
        public bool IsRegex => regex is not null;
        public bool HasRegexIndex => IsRegex && IndexKey is not null;
        public bool HasRestrictiveSourceDomainScope => includedDomains.Length > 0;

        public void ReleaseIndexHost()
        {
            hostMatchGuaranteedByIndex = true;
            IndexHost = null;
        }

        public static FilterRule? Parse(string? input)
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
                if (option is "third-party" or "3p" or "strict3p")
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
                    // $denyallow scopes the rule by the document it appears in, so
                    // it belongs with the $domain=~ exclusions and must be tested
                    // against the source host. It used to be merged into
                    // excludedTargetDomains, which Matches checks against the
                    // request host, so the rule was filtered by the wrong URL.
                    // "$denyallow=a.com" means the rule does not apply on a.com;
                    // "$denyallow=~b.com" negates that, so the rule does apply on
                    // b.com and b.com must not be excluded.
                    if (denyIncluded.Count > 0)
                    {
                        excludedDomains ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var domain in denyIncluded) excludedDomains.Add(domain);
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
            long? regexIndexKey = null;
            if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
            {
                var regexPattern = pattern[1..^1];
                if (TryExtractMandatoryRegexLiteral(regexPattern, out var literal))
                {
                    regexIndexKey = PackIndexKey(literal, 0);
                }
                try
                {
                    var options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
                    if (!matchCase) options |= RegexOptions.IgnoreCase;
                    regex = new Regex(regexPattern, options, RegexMatchTimeout);
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
                includedTypes,
                excludedTypeMask,
                ToDomainArray(includedDomains),
                ToDomainArray(excludedDomains),
                ToDomainArray(includedTargetDomains),
                ToDomainArray(excludedTargetDomains),
                thirdPartyOnly,
                firstPartyOnly,
                matchCase);
        }

        public bool Matches(
            Uri requestUri,
            Uri? sourceUri,
            AdBlockResourceType resourceType,
            bool thirdParty)
        {
            var resourceMask = 1u << (int)resourceType;
            if (includedTypes is { } included && (included & resourceMask) == 0) return false;
            if ((excludedTypes & resourceMask) != 0) return false;

            if (ThirdPartyOnly && !thirdParty) return false;
            if (FirstPartyOnly && thirdParty) return false;

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
                return hostPathPattern is null
                    || GlobMatches(hostPathPattern, requestUri.PathAndQuery, matchCase);
            }

            var target = useHostAndPathTarget
                ? requestUri.Host + requestUri.AbsolutePath + requestUri.Query
                : requestUri.AbsoluteUri;
            return GlobMatches(networkPattern!, target, matchCase);
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
        "important", "match-case", "third-party", "3p", "strict3p", "~third-party",
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
        // A terminator of '*' or '?' means the host text is a glob, not a host
        // anchor: "||cacheserve.*/promodisplay/" is not the host "cacheserve".
        // Treating it as one produced a hostPathPattern of "*/promodisplay/*",
        // which can never match a path, so the rule silently became a no-op
        // while still occupying an index slot. Return null so the rule falls
        // back to the generic glob path that matches it correctly.
        if (end >= 0 && pattern[end] is '*' or '?') return null;
        var host = (end < 0 ? pattern[2..] : pattern[2..end]).Trim('.');
        // Single-label hosts such as "localhost" or "intranet" are valid ||host
        // anchors. Rejecting them pushed the rule onto the generic "host^*"
        // path, where '.' is not a separator, so the rule matched neither the
        // host nor any of its subdomains.
        if (host.Length == 0) return null;
        var hostNameType = Uri.CheckHostName(host);
        if (hostNameType is not (UriHostNameType.Dns or UriHostNameType.Basic)) return null;
            hostEnd = end < 0 ? pattern.Length : end;
            return host;
        }

        private static long? ExtractIndexKey(string pattern)
        {
            var longestStart = -1;
            var longestLength = 0;
            var index = 0;
            while (index < pattern.Length)
            {
                if (!IsIndexLiteralCharacter(pattern[index]))
                {
                    index++;
                    continue;
                }

                var start = index++;
                while (index < pattern.Length && IsIndexLiteralCharacter(pattern[index])) index++;
                var length = index - start;
                if (length >= 3 && length > longestLength)
                {
                    longestStart = start;
                    longestLength = length;
                }
            }

            return longestStart < 0 ? null : PackIndexKey(pattern, longestStart);
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
                if (value == '|') return false;
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

        private static bool GlobMatches(string pattern, string text, bool matchCase)
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

        private static bool CharactersEqual(char left, char right, bool matchCase) =>
            matchCase ? left == right : char.ToLowerInvariant(left) == char.ToLowerInvariant(right);

        private static bool IsSeparator(char value) =>
            !char.IsLetterOrDigit(value) && value is not '_' and not '-' and not '.' and not '%';
    }

    private static int FindOptionIndex(string line)
    {
        var searchStart = line.StartsWith("@@", StringComparison.Ordinal) ? 2 : 0;
        if (searchStart < line.Length && line[searchStart] == '/')
        {
            var closingSlash = line.LastIndexOf('/');
            return closingSlash > searchStart
                && closingSlash + 1 < line.Length
                && line[closingSlash + 1] == '$'
                    ? closingSlash + 1
                    : -1;
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

    private sealed record FilterListSource(string FileName, Uri Uri);
}
