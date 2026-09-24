using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MishaWeb;

if (args.Length == 2
    && args[0].Equals("--audit-adblock-regex", StringComparison.Ordinal))
{
    return AdBlockRegexAudit.Run(args[1]);
}

if (args.Length is 1 or 2
    && args[0].Equals("--check-webview-runtime", StringComparison.Ordinal))
{
    var probeFolder = args.Length == 2
        ? Path.GetFullPath(args[1])
        : Path.Combine(Path.GetTempPath(), "MishaWeb-WebView2-Probe-" + Environment.ProcessId);
    try
    {
        string? browserVersion = null;
        Exception? probeError = null;
        var probeThread = new Thread(() =>
        {
            try
            {
                browserVersion = WebView2LoaderBootstrap.ProbeRuntimeAsync(probeFolder)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception error)
            {
                probeError = error;
            }
        });
        probeThread.SetApartmentState(ApartmentState.STA);
        probeThread.Start();
        probeThread.Join();
        if (probeError is not null) throw probeError;
        Console.WriteLine(
            $"ready=True; browserVersion={browserVersion}; "
            + $"userDataFolder={probeFolder}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(
            $"ready=False; error={error.GetType().Name}: {error.Message}; "
            + $"userDataFolder={probeFolder}");
        return 1;
    }
}

if (args.Length == 1
    && args[0].Equals("--check-media-capture-runtime", StringComparison.Ordinal))
{
    var probeFolder = Path.Combine(
        Path.GetTempPath(),
        "MishaWeb-Media-Capture-Probe-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
    try
    {
        var result = RunMediaCaptureRuntimeProbe(probeFolder);
        Console.WriteLine(
            $"ready={result.Ready}; secureContext={result.SecureContext}; "
            + $"microphonePermission={result.MicrophonePermission}; cameraPermission={result.CameraPermission}; "
            + $"permissionOriginsMatch={result.PermissionOriginsMatch}; "
            + $"audioTracks={result.AudioTracks}; videoTracks={result.VideoTracks}; "
            + $"audioLive={result.AudioLive}; videoLive={result.VideoLive}");
        if (!string.IsNullOrWhiteSpace(result.PageError))
        {
            Console.Error.WriteLine($"pageError={result.PageError}");
        }
        return result.Ready ? 0 : 1;
    }
    catch (Exception error)
    {
        var rootError = error.GetBaseException();
        Console.Error.WriteLine($"ready=False; error={rootError.GetType().Name}: {rootError.Message}");
        return 1;
    }
    finally
    {
        DeleteMediaCaptureProbeFolder(probeFolder);
    }
}

if (args.Length == 1
    && args[0].Equals("--check-website-theme-runtime", StringComparison.Ordinal))
{
    var probeFolder = Path.Combine(
        Path.GetTempPath(),
        "MishaWeb-Website-Theme-Probe-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
    try
    {
        var result = RunWebsiteThemeRuntimeProbe(probeFolder);
        Console.WriteLine(
            $"ready={result.Ready}; darkMedia={result.DarkMedia}; "
            + $"darkLuminance={result.DarkLuminance:F3}; dynamicLuminance={result.DynamicLuminance:F3}; "
            + $"frameLuminance={result.FrameLuminance:F3}; mediaDelta={result.MediaDelta:F1}; "
            + $"lightMedia={result.LightMedia}; lightLuminance={result.LightLuminance:F3}; "
            + $"sameDocument={result.SameDocument}");
        return result.Ready ? 0 : 1;
    }
    catch (Exception error)
    {
        var cause = error.GetBaseException();
        Console.Error.WriteLine($"ready=False; error={cause.GetType().Name}: {cause.Message}");
        return 1;
    }
    finally
    {
        DeleteWebsiteThemeProbeFolder(probeFolder);
    }
}

if (args.Length == 1
    && args[0].Equals("--check-browser-extensions-runtime", StringComparison.Ordinal))
{
    var probeFolder = Path.Combine(
        Path.GetTempPath(),
        "MishaWeb-Extension-Runtime-Probe-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
    try
    {
        var result = RunBrowserExtensionsRuntimeProbe(probeFolder);
        Console.WriteLine(
            $"ready={result.Ready}; installed={result.Installed}; disabled={result.Disabled}; "
            + $"enabled={result.Enabled}; persisted={result.Persisted}; "
            + $"privateIsolated={result.PrivateIsolated}; removed={result.Removed}; "
            + $"extensionId={result.ExtensionId}");
        return result.Ready ? 0 : 1;
    }
    catch (Exception error)
    {
        var cause = error.GetBaseException();
        Console.Error.WriteLine($"ready=False; error={cause.GetType().Name}: {cause.Message}");
        return 1;
    }
    finally
    {
        DeleteBrowserExtensionsProbeFolder(probeFolder);
    }
}

if (args.Length == 1
    && args[0].Equals("--check-chrome-store-bridge-runtime", StringComparison.Ordinal))
{
    var probeFolder = Path.Combine(
        Path.GetTempPath(),
        "MishaWeb-Chrome-Store-Bridge-Probe-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
    try
    {
        var result = RunChromeStoreBridgeRuntimeProbe(probeFolder);
        Console.WriteLine(
            $"ready={result.Ready}; initialCategory={result.InitialCategory}; "
            + $"listingReached={result.ListingReached}; fallbackVisible={result.FallbackVisible}; "
            + $"trustedClickMessage={result.TrustedClickMessage}; message={result.Message}");
        return result.Ready ? 0 : 1;
    }
    catch (Exception error)
    {
        var cause = error.GetBaseException();
        Console.Error.WriteLine($"ready=False; error={cause.GetType().Name}: {cause.Message}");
        return 1;
    }
    finally
    {
        DeleteChromeStoreBridgeProbeFolder(probeFolder);
    }
}

if (args.Length == 2
    && args[0].Equals("--check-chrome-store-package", StringComparison.Ordinal))
{
    var probeFolder = Path.Combine(
        Path.GetTempPath(),
        "MishaWeb-Extension-Download-Probe-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
    try
    {
        var result = RunChromeStoreExtensionRuntimeProbe(probeFolder, args[1]);
        Console.WriteLine(
            $"ready={result.Ready}; installed={result.Installed}; removed={result.Removed}; "
            + $"name={result.Name}; version={result.Version}; storeId={result.StoreId}; "
            + $"launchPath={result.LaunchPath}; runtime={result.RuntimeVersion}");
        return result.Ready ? 0 : 1;
    }
    catch (Exception error)
    {
        var cause = error.GetBaseException();
        Console.Error.WriteLine($"ready=False; error={cause.GetType().Name}: {cause.Message}");
        return 1;
    }
    finally
    {
        DeleteExtensionDownloadProbeFolder(probeFolder);
    }
}

if (args.Length == 1 && args[0].Equals("--check-live-filters", StringComparison.Ordinal))
{
    var liveEngine = new AdBlockEngine();
    liveEngine.AcquireConsumer();
    try
    {
        await liveEngine.LoadAsync();
        var liveCacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MishaWeb",
            "Filters");
        var liveFileCount = Directory.Exists(liveCacheFolder)
            ? Directory.EnumerateFiles(liveCacheFolder, "list-*.txt").Count()
            : 0;
        const string liveWatchUrl = "https://www.youtube.com/watch?v=live-filter-check";
        var functionalYouTubeRequestsAllowed = new[]
        {
            ("https://www.youtube.com/youtubei/v1/player?prettyPrint=false", AdBlockResourceType.Fetch),
            ("https://www.youtube.com/youtubei/v1/next?prettyPrint=false", AdBlockResourceType.Fetch),
            ("https://www.youtube.com/youtubei/v1/browse?prettyPrint=false", AdBlockResourceType.Fetch),
            ("https://www.youtube.com/youtubei/v1/guide?prettyPrint=false", AdBlockResourceType.XmlHttpRequest),
            ("https://www.youtube.com/youtubei/v1/comment/get_comments?prettyPrint=false", AdBlockResourceType.Fetch),
            ("https://www.youtube.com/comment_service_ajax?action_get_comments=1", AdBlockResourceType.XmlHttpRequest),
            ("https://www.youtube.com/s/player/current/player_ias.vflset/en_US/base.js", AdBlockResourceType.Script),
            ("https://rr1---sn-npoe7n7z.googlevideo.com/videoplayback?id=content&itag=18&mime=video%2Fmp4&range=0-1048575", AdBlockResourceType.Media)
        }.All(request => !liveEngine.ShouldBlock(request.Item1, liveWatchUrl, request.Item2));
        var youtubeAdRequestsBlocked = new[]
        {
            ("https://www.youtube.com/pagead/adview?ai=pre-roll", AdBlockResourceType.XmlHttpRequest),
            ("https://www.youtube.com/youtubei/v1/player/ad_break?key=test", AdBlockResourceType.Fetch),
            ("https://www.youtube.com/api/stats/ads?ver=2", AdBlockResourceType.Fetch)
        }.All(request => liveEngine.ShouldBlock(request.Item1, liveWatchUrl, request.Item2));
        var diagnostics = liveEngine.LastCompileDiagnostics;
        Console.WriteLine(
            $"ready={liveEngine.IsReady}; cachedLists={liveFileCount}; "
            + $"youtubeFunctionalAllowed={functionalYouTubeRequestsAllowed}; "
            + $"youtubeAdsBlocked={youtubeAdRequestsBlocked}; "
            + $"networkRules={diagnostics.NetworkRules}; cosmeticRules={diagnostics.CosmeticRules}; "
            + $"unsupported={diagnostics.UnsupportedRuleCandidates}; "
            + $"capacityDropped={diagnostics.CapacityDroppedNetworkRules}");
        Environment.ExitCode = liveEngine.IsReady
            && liveFileCount >= AdBlockEngine.ConfiguredFilterSourceCountForTesting
            && functionalYouTubeRequestsAllowed
            && youtubeAdRequestsBlocked
            && diagnostics.CapacityDroppedNetworkRules == 0
            ? 0
            : 1;
    }
    finally
    {
        liveEngine.ReleaseConsumer();
    }
    return Environment.ExitCode;
}

if ((args.Length == 1
        && (args[0].Equals("--benchmark-adblock-live", StringComparison.Ordinal)
            || args[0].Equals("--benchmark-adblock-live-requests", StringComparison.Ordinal)))
    || (args.Length == 2
        && (args[0].Equals("--benchmark-adblock-folder", StringComparison.Ordinal)
            || args[0].Equals("--benchmark-adblock-folder-requests", StringComparison.Ordinal))))
{
    var runRequestBenchmark = args[0].EndsWith("-requests", StringComparison.Ordinal);
    var cacheFolder = args.Length == 2 ? args[1] : null;
    var liveEngine = new AdBlockEngine(
        cacheFolder: cacheFolder,
        updateRemoteLists: false);
    liveEngine.AcquireConsumer();
    try
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var retainedBefore = GC.GetTotalMemory(forceFullCollection: false);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await liveEngine.LoadAsync();
        timer.Stop();
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var retainedAfter = GC.GetTotalMemory(forceFullCollection: true);
        var diagnostics = liveEngine.LastCompileDiagnostics;
        Console.WriteLine(
            $"compileMs={timer.Elapsed.TotalMilliseconds:F2}; "
            + $"allocatedMiB={allocatedBytes / (1024d * 1024d):F2}; "
            + $"retainedDeltaMiB={(retainedAfter - retainedBefore) / (1024d * 1024d):F2}; "
            + $"uniqueLines={diagnostics.UniqueLines}; networkRules={diagnostics.NetworkRules}; "
            + $"cosmeticRules={diagnostics.CosmeticRules}; unsupported={diagnostics.UnsupportedRuleCandidates}; "
            + $"capacityDropped={diagnostics.CapacityDroppedNetworkRules}; "
            + $"regexRules={diagnostics.RegexRules}; indexedRegex={diagnostics.IndexedRegexRules}; "
            + $"sourceScopedUnindexedRegex={diagnostics.UnindexedSourceScopedRegexRules}; "
            + $"genericUnindexedRegex={diagnostics.UnindexedGenericRegexRules}; "
            + $"droppedRegex={diagnostics.DroppedRegexRules}");
        if (runRequestBenchmark)
        {
            const string sourceUrl = "https://publisher.benchmark.invalid/watch";
            var requests = new[]
            {
                ("https://cdn.benchmark.invalid/app.js", AdBlockResourceType.Script),
                ("https://images.benchmark.invalid/hero.webp", AdBlockResourceType.Image),
                ("https://api.benchmark.invalid/v1/feed?cursor=next", AdBlockResourceType.Fetch),
                ("https://media.benchmark.invalid/video/segment-42.m4s", AdBlockResourceType.Media),
                ("https://fonts.benchmark.invalid/family.woff2", AdBlockResourceType.Font),
                ("https://analytics.benchmark.invalid/pixel.gif", AdBlockResourceType.Image),
                ("https://www.youtube.com/youtubei/v1/next?prettyPrint=false", AdBlockResourceType.Fetch),
                ("https://www.youtube.com/pagead/adview?ai=benchmark", AdBlockResourceType.XmlHttpRequest)
            };
            var coldDecisionMicroseconds = new double[requests.Length];
            for (var requestIndex = 0; requestIndex < requests.Length; requestIndex++)
            {
                var request = requests[requestIndex];
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                _ = liveEngine.ShouldBlock(request.Item1, sourceUrl, request.Item2);
                coldDecisionMicroseconds[requestIndex] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var requestAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var requestTimer = System.Diagnostics.Stopwatch.StartNew();
            var blocked = 0;
            const int sweeps = 100;
            var decisionMicroseconds = new double[sweeps * requests.Length];
            var decisionIndex = 0;
            for (var sweep = 0; sweep < sweeps; sweep++)
            {
                foreach (var request in requests)
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (liveEngine.ShouldBlock(request.Item1, sourceUrl, request.Item2)) blocked++;
                    decisionMicroseconds[decisionIndex++] = System.Diagnostics.Stopwatch
                        .GetElapsedTime(started)
                        .TotalMicroseconds;
                }
            }
            requestTimer.Stop();
            var requestAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - requestAllocatedBefore;
            var decisions = sweeps * requests.Length;
            Array.Sort(decisionMicroseconds);
            var requestRetainedAfter = GC.GetTotalMemory(forceFullCollection: true);
            Console.WriteLine(
                $"requestDecisions={decisions}; requestElapsedMs={requestTimer.Elapsed.TotalMilliseconds:F2}; "
                + $"microsecondsPerDecision={requestTimer.Elapsed.TotalMicroseconds / decisions:F2}; "
                + $"p50Microseconds={decisionMicroseconds[decisions / 2]:F2}; "
                + $"p95Microseconds={decisionMicroseconds[(int)(decisions * 0.95)]:F2}; "
                + $"coldMaxMicroseconds={coldDecisionMicroseconds.Max():F2}; "
                + $"requestAllocatedBytes={requestAllocatedBytes}; bytesPerDecision={(double)requestAllocatedBytes / decisions:F2}; "
                + $"retainedAfterRequestsMiB={(requestRetainedAfter - retainedBefore) / (1024d * 1024d):F2}; "
                + $"blocked={blocked}");
        }
        return diagnostics.CapacityDroppedNetworkRules == 0 ? 0 : 1;
    }
    finally
    {
        liveEngine.ReleaseConsumer();
    }
}

if (args.Length == 1 && args[0].Equals("--benchmark-lifecycle", StringComparison.Ordinal))
{
    const int iterations = 20_000;
    var benchmarkNowUtc = new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);
    var benchmarkSnapshots = Enumerable.Range(0, 32)
        .Select(index => new TabLifecycleSnapshot(
            index,
            benchmarkNowUtc - TimeSpan.FromSeconds(180 - index),
            IsActive: index == 31,
            IsClosed: false,
            IsLoading: index is 3 or 11,
            IsInitializing: false,
            IsAudible: index == 29,
            HasActiveDownload: index == 28,
            HasCore: true,
            IsSuspended: index % 2 == 0,
            KeepAwake: index == 30))
        .ToArray();

    var checksum = 0;
    for (var warmup = 0; warmup < 2_000; warmup++)
    {
        checksum += TabLifecyclePolicy.PlanSweep(
            benchmarkSnapshots,
            TabLifecycleMode.Ultra,
            benchmarkNowUtc,
            memoryLoadPercent: 82,
            memorySnapshotIsValid: true,
            totalPhysicalBytes: 8UL * 1024 * 1024 * 1024,
            logicalProcessorCount: 4,
            maximumActions: 8).Count;
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var timer = System.Diagnostics.Stopwatch.StartNew();
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        checksum += TabLifecyclePolicy.PlanSweep(
            benchmarkSnapshots,
            TabLifecycleMode.Ultra,
            benchmarkNowUtc,
            memoryLoadPercent: 82,
            memorySnapshotIsValid: true,
            totalPhysicalBytes: 8UL * 1024 * 1024 * 1024,
            logicalProcessorCount: 4,
            maximumActions: 8).Count;
    }
    timer.Stop();
    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    Console.WriteLine(
        $"iterations={iterations}; elapsedMs={timer.Elapsed.TotalMilliseconds:F2}; "
        + $"allocatedBytes={allocatedBytes}; bytesPerSweep={(double)allocatedBytes / iterations:F2}; "
        + $"checksum={checksum}");
    return 0;
}

if (args.Length is 1 or 2 && args[0].Equals("--benchmark-suggestions", StringComparison.Ordinal))
{
    const int iterations = 20_000;
    var benchmarkNow = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    var benchmarkState = new BrowserState
    {
        Bookmarks = Enumerable.Range(0, 200)
            .Select(index => new BookmarkEntry(
                $"Pink benchmark page {index}",
                $"https://bookmark-{index}.benchmark.invalid/pink/{index}"))
            .ToList(),
        History = Enumerable.Range(0, 300)
            .Select(index => new HistoryEntry(
                $"Pink benchmark history {index}",
                $"https://history-{index}.benchmark.invalid/pink/{index}",
                benchmarkNow.UtcDateTime.AddMinutes(-index)))
            .ToList(),
        DismissedSuggestionUrls = Enumerable.Range(0, 24)
            .Select(index => $"https://history-{index}.benchmark.invalid/pink/{index}")
            .ToList(),
        SuggestionUsage = Enumerable.Range(0, 32)
            .Select(index => new AddressSuggestionUsage(
                $"https://bookmark-{index}.benchmark.invalid/pink/{index}",
                index + 1,
                benchmarkNow.AddMinutes(-index)))
            .ToList()
    };

    var checksum = 0;
    for (var warmup = 0; warmup < 2_000; warmup++)
    {
        var suggestions = AddressSuggestionEngine.GetSuggestions("pink", benchmarkState);
        checksum = unchecked((checksum * 31) + suggestions.Count + suggestions[0].Title.Length);
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var timer = System.Diagnostics.Stopwatch.StartNew();
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        var suggestions = AddressSuggestionEngine.GetSuggestions("pink", benchmarkState);
        checksum = unchecked((checksum * 31) + suggestions.Count + suggestions[0].Title.Length);
    }
    timer.Stop();
    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    var benchmarkSummary =
        $"queries={iterations}; elapsedMs={timer.Elapsed.TotalMilliseconds:F2}; "
        + $"allocatedBytes={allocatedBytes}; bytesPerQuery={(double)allocatedBytes / iterations:F2}; "
        + $"checksum={checksum}";
    Console.WriteLine(benchmarkSummary);
    if (args.Length == 2) File.WriteAllText(args[1], benchmarkSummary);
    return 0;
}

if (args.Length == 1 && args[0].Equals("--benchmark-adblock", StringComparison.Ordinal))
{
    const int iterations = 100_000;
    var benchmarkEngine = new AdBlockEngine(
        initialRules:
        [
            "||ads.benchmark.invalid^$third-party",
            "@@||ads.benchmark.invalid/allowed.js$script",
            "tracker-pixel$third-party,image",
            "||api.benchmark.invalid/sponsor^$xhr",
            "||important.benchmark.invalid^$script,important",
            "@@||important.benchmark.invalid/allowed.js$script,important"
        ],
        updateRemoteLists: false);
    var benchmarkRequests = new (string Url, string Source, AdBlockResourceType Type, bool Expected)[]
    {
        ("https://ads.benchmark.invalid/banner.js", "https://publisher.example/article", AdBlockResourceType.Script, true),
        ("https://ads.benchmark.invalid/allowed.js", "https://publisher.example/article", AdBlockResourceType.Script, false),
        ("https://ads.benchmark.invalid/first-party.js", "https://ads.benchmark.invalid/home", AdBlockResourceType.Script, false),
        ("https://cdn.example/assets/tracker-pixel.png", "https://publisher.example/article", AdBlockResourceType.Image, true),
        ("https://api.benchmark.invalid/sponsor?id=7", "https://publisher.example/article", AdBlockResourceType.Fetch, true),
        ("https://api.benchmark.invalid/content?id=7", "https://publisher.example/article", AdBlockResourceType.Fetch, false),
        ("https://important.benchmark.invalid/allowed.js", "https://publisher.example/article", AdBlockResourceType.Script, false),
        ("https://important.benchmark.invalid/blocked.js", "https://publisher.example/article", AdBlockResourceType.Script, true)
    };

    var checksum = 0;
    for (var warmup = 0; warmup < 5_000; warmup++)
    {
        var request = benchmarkRequests[warmup % benchmarkRequests.Length];
        checksum = unchecked((checksum * 31)
            + (benchmarkEngine.ShouldBlock(request.Url, request.Source, request.Type) ? 1 : 0));
    }

    if (benchmarkRequests.Any(request =>
            benchmarkEngine.ShouldBlock(request.Url, request.Source, request.Type) != request.Expected))
    {
        Console.Error.WriteLine("adblock benchmark fixture produced an unexpected decision");
        return 1;
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var timer = System.Diagnostics.Stopwatch.StartNew();
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        foreach (var request in benchmarkRequests)
        {
            checksum = unchecked((checksum * 31)
                + (benchmarkEngine.ShouldBlock(request.Url, request.Source, request.Type) ? 1 : 0));
        }
    }
    timer.Stop();
    var decisions = iterations * benchmarkRequests.Length;
    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    Console.WriteLine(
        $"decisions={decisions}; elapsedMs={timer.Elapsed.TotalMilliseconds:F2}; "
        + $"allocatedBytes={allocatedBytes}; bytesPerDecision={(double)allocatedBytes / decisions:F2}; "
        + $"checksum={checksum}");
    return 0;
}

if (args.Length == 1 && args[0].Equals("--benchmark-adblock-mixed", StringComparison.Ordinal))
{
    const int uniqueRequestCount = 4_096;
    const int sweeps = 50;
    var benchmarkEngine = new AdBlockEngine(
        initialRules:
        [
            "||ads.benchmark.invalid^$third-party",
            "@@||ads.benchmark.invalid/allowed.js$script",
            "tracker-pixel$third-party,image",
            "||api.benchmark.invalid/sponsor^$xhr",
            "||important.benchmark.invalid^$script,important",
            "@@||important.benchmark.invalid/allowed.js$script,important"
        ],
        updateRemoteLists: false);
    const string sourceUrl = "https://publisher.example/article";
    var requests = new (string Url, AdBlockResourceType Type, bool Expected)[uniqueRequestCount];
    for (var index = 0; index < requests.Length; index++)
    {
        requests[index] = (index % 8) switch
        {
            0 => ($"https://ads.benchmark.invalid/banner.js?request={index}", AdBlockResourceType.Script, true),
            1 => ($"https://ads.benchmark.invalid/allowed.js?request={index}", AdBlockResourceType.Script, false),
            2 => ($"https://cdn-{index}.example/assets/tracker-pixel.png", AdBlockResourceType.Image, true),
            3 => ($"https://api.benchmark.invalid/sponsor?id={index}", AdBlockResourceType.Fetch, true),
            4 => ($"https://api.benchmark.invalid/content?id={index}", AdBlockResourceType.Fetch, false),
            5 => ($"https://important.benchmark.invalid/allowed.js?request={index}", AdBlockResourceType.Script, false),
            6 => ($"https://important.benchmark.invalid/blocked.js?request={index}", AdBlockResourceType.Script, true),
            _ => ($"https://static-{index}.example/content.js", AdBlockResourceType.Script, false)
        };
    }

    var checksum = 0;
    foreach (var request in requests)
    {
        var blocked = benchmarkEngine.ShouldBlock(request.Url, sourceUrl, request.Type);
        if (blocked != request.Expected)
        {
            Console.Error.WriteLine($"mixed adblock fixture mismatch: {request.Url}");
            return 1;
        }
        checksum = unchecked((checksum * 31) + (blocked ? 1 : 0));
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var timer = System.Diagnostics.Stopwatch.StartNew();
    for (var sweep = 0; sweep < sweeps; sweep++)
    {
        foreach (var request in requests)
        {
            checksum = unchecked((checksum * 31)
                + (benchmarkEngine.ShouldBlock(request.Url, sourceUrl, request.Type) ? 1 : 0));
        }
    }
    timer.Stop();
    var decisions = sweeps * uniqueRequestCount;
    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    Console.WriteLine(
        $"uniqueRequests={uniqueRequestCount}; decisions={decisions}; "
        + $"elapsedMs={timer.Elapsed.TotalMilliseconds:F2}; allocatedBytes={allocatedBytes}; "
        + $"bytesPerDecision={(double)allocatedBytes / decisions:F2}; checksum={checksum}");
    return 0;
}

if (args.Length == 2
    && args[0].Equals("--check-start-page-layout", StringComparison.Ordinal))
{
    var dpiMode = args[1] switch
    {
        "unaware" => HighDpiMode.DpiUnaware,
        "per-monitor-v2" => HighDpiMode.PerMonitorV2,
        _ => throw new ArgumentOutOfRangeException(nameof(args), "Unknown DPI layout probe mode.")
    };
    Application.SetHighDpiMode(dpiMode);
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    var result = VerifyNativeStartPageResponsiveLayoutInCurrentProcess();
    Console.WriteLine($"valid={result.Valid}; hierarchy={result.Hierarchy}; dpi={result.Dpi}");
    return result.Valid && result.Hierarchy ? 0 : 1;
}

var renderChrome = args.Length == 2
    && args[0].Equals("--render-chrome", StringComparison.Ordinal);
var renderLoadedChrome = args.Length == 2
    && args[0].Equals("--render-loaded-chrome", StringComparison.Ordinal);
var renderSuggestions = args.Length == 3
    && args[0].Equals("--render-suggestions", StringComparison.Ordinal);
if (renderChrome || renderLoadedChrome || renderSuggestions)
{
    Exception? renderError = null;
    var renderThread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var previewState = new BrowserState
            {
                History =
                [
                    new HistoryEntry("GitHub", "https://github.com/", DateTime.UtcNow),
                    new HistoryEntry("Microsoft Learn", "https://learn.microsoft.com/", DateTime.UtcNow.AddMinutes(-1)),
                    new HistoryEntry("Example news", "https://news.example/", DateTime.UtcNow.AddMinutes(-2)),
                    new HistoryEntry("Web mail", "https://mail.example/", DateTime.UtcNow.AddMinutes(-3)),
                    new HistoryEntry("Music", "https://music.example/", DateTime.UtcNow.AddMinutes(-4)),
                    new HistoryEntry("Maps", "https://maps.example/", DateTime.UtcNow.AddMinutes(-5))
                ]
            };
            using var form = new MainForm(startBrowserOnShown: false, previewState)
            {
                Size = renderLoadedChrome ? new Size(1917, 420) : new Size(1280, 720),
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false
            };
            form.Show();
            if (renderLoadedChrome) form.PrepareLoadedChromePreviewForTesting();
            else form.PrepareChromePreviewForTesting();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
            if (renderSuggestions)
            {
                form.PrepareSuggestionPreviewForTesting(args[2]);
            }
            form.PerformLayout();
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            if (renderSuggestions) form.DrawSuggestionPreviewForTesting(image);
            form.Close();
            var outputFolder = Path.GetDirectoryName(args[1]);
            if (!string.IsNullOrWhiteSpace(outputFolder)) Directory.CreateDirectory(outputFolder);
            image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception error)
        {
            renderError = error;
        }
    });
    renderThread.SetApartmentState(ApartmentState.STA);
    renderThread.Start();
    renderThread.Join();
    if (renderError is not null)
    {
        throw new InvalidOperationException("Browser chrome rendering failed.", renderError);
    }
    Console.WriteLine($"Rendered browser chrome to {args[1]}");
    return 0;
}

var failures = new List<string>();
var checkCount = 0;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

var encodedStartupUrl = "https://example.com/a%2Fb?token=x%26y";
Check(
    "startup handoff preserves encoded paths and signed query values",
    MishaWeb.Program.ResolveStartupAddress([encodedStartupUrl]) == encodedStartupUrl);

var restartStartupArguments = MishaWeb.Program.ParseStartupArguments(
    ["--restart-after-pid", "4242", encodedStartupUrl]);
var malformedRestartStartupArguments = MishaWeb.Program.ParseStartupArguments(
    ["--restart-after-pid", "not-a-pid", encodedStartupUrl]);
Check(
    "internal restart PID tokens are parsed before startup and never become navigation input",
    restartStartupArguments.RestartAfterProcessId == 4242
        && restartStartupArguments.NavigationArguments.SequenceEqual([encodedStartupUrl])
        && MishaWeb.Program.ResolveStartupAddress(
            ["--restart-after-pid", "4242", encodedStartupUrl]) == encodedStartupUrl
        && malformedRestartStartupArguments.RestartAfterProcessId is null
        && malformedRestartStartupArguments.NavigationArguments.SequenceEqual([encodedStartupUrl])
        && MishaWeb.Program.WaitForRestartPredecessor(int.MaxValue, TimeSpan.Zero)
        && !MishaWeb.Program.WaitForRestartPredecessor(Environment.ProcessId, TimeSpan.Zero));

var restartExecutablePath = Path.GetFullPath("MishaWeb.exe");
var restartStartInfo = MishaWeb.Program.BrowserApplicationContext.CreateRestartProcessStartInfo(
    restartExecutablePath,
    4242);
Check(
    "restart successor uses the exact executable and structured arguments without a shell",
    restartStartInfo.FileName == restartExecutablePath
        && !restartStartInfo.UseShellExecute
        && restartStartInfo.ArgumentList.SequenceEqual(["--restart-after-pid", "4242"])
        && string.IsNullOrEmpty(restartStartInfo.Arguments));

var aggregateRestartGuard = MishaWeb.Program.BrowserApplicationContext.AggregateBrowserRestartGuard(
    [
        new MishaWeb.Program.BrowserRestartWindowState(false, 2, 0, 0, 2),
        new MishaWeb.Program.BrowserRestartWindowState(true, 1, 2, 0, 0)
    ]);
Check(
    "process restart guard aggregates work and non-restorable tabs across all windows",
    aggregateRestartGuard.WindowCount == 2
        && aggregateRestartGuard.PrivateWindowCount == 1
        && aggregateRestartGuard.ActiveDownloadCount == 3
        && aggregateRestartGuard.ActiveMediaCaptureCount == 2
        && aggregateRestartGuard.ActiveExtensionMutationCount == 0
        && aggregateRestartGuard.NonRestorableTabCount == 2
        && aggregateRestartGuard.HasActiveWork
        && aggregateRestartGuard.RequiresConfirmation);
Check(
    "one normal window warns before dropping local-file or extension tabs on restart",
    MainForm.CountNonRestorableTabsForProcessRestart(
        [
            "file:///C:/Users/Mei/Documents/example.html",
            "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/index.html",
            "https://example.com/",
            StartPage.Url
        ],
        isPrivateMode: false) == 2
        && MishaWeb.Program.BrowserApplicationContext.AggregateBrowserRestartGuard(
            [new MishaWeb.Program.BrowserRestartWindowState(false, 0, 0, 0, 2)])
            .RequiresConfirmation);

Exception? restartUiError = null;
var restartCoordinationValid = false;
var finalWindowExitValid = false;
var extensionAndSaveGuardsValid = false;
var extensionCloseDeferralValid = false;
var privateOnlyUrlHandoffValid = false;
var privateOnlyEmptyHandoffValid = false;
var restartUiThread = new Thread(() =>
{
    try
    {
        using (var normalRestartWindow = new MainForm(startBrowserOnShown: false))
        using (var privateRestartWindow = new MainForm(
                   startBrowserOnShown: false,
                   previewState: null,
                   startupAddress: null,
                   mode: BrowserMode.Private))
        {
            _ = normalRestartWindow.Handle;
            _ = privateRestartWindow.Handle;
            var confirmationCount = 0;
            var launchCount = 0;
            MishaWeb.Program.BrowserRestartGuard promptedRestartGuard = default;
            using var restartContext = new MishaWeb.Program.BrowserApplicationContext(
                (_, guard) =>
                {
                    promptedRestartGuard = guard;
                    confirmationCount++;
                    return confirmationCount > 1;
                },
                predecessorProcessId =>
                {
                    launchCount++;
                    return predecessorProcessId == Environment.ProcessId;
                },
                exitThread: () => { });
            var normalRegistered = restartContext.Register(normalRestartWindow);
            var privateRegistered = restartContext.Register(privateRestartWindow);
            var canceledRestart = restartContext.RequestBrowserProcessRestart(normalRestartWindow);
            var windowsAfterCanceledRestart = restartContext.WindowCount;
            var exitsAfterCanceledRestart = restartContext.ExitThreadRequestCountForTesting;
            var windowsSeenAfterFirstClose = -1;
            var exitsSeenAfterFirstClose = -1;
            privateRestartWindow.FormClosed += (_, _) =>
            {
                windowsSeenAfterFirstClose = restartContext.WindowCount;
                exitsSeenAfterFirstClose = restartContext.ExitThreadRequestCountForTesting;
            };
            var startedRestart = restartContext.RequestBrowserProcessRestart(normalRestartWindow);
            restartCoordinationValid = normalRegistered
                && privateRegistered
                && canceledRestart == MishaWeb.Program.BrowserRestartRequestResult.Canceled
                && windowsAfterCanceledRestart == 2
                && exitsAfterCanceledRestart == 0
                && confirmationCount == 2
                && promptedRestartGuard.WindowCount == 2
                && promptedRestartGuard.PrivateWindowCount == 1
                && launchCount == 1
                && startedRestart == MishaWeb.Program.BrowserRestartRequestResult.Started
                && normalRestartWindow.IsDisposed
                && privateRestartWindow.IsDisposed;
            finalWindowExitValid = windowsSeenAfterFirstClose == 1
                && exitsSeenAfterFirstClose == 0
                && restartContext.WindowCount == 0
                && restartContext.ExitThreadRequestCountForTesting == 1;
        }

        using (var guardedWindow = new MainForm(startBrowserOnShown: false))
        {
            _ = guardedWindow.Handle;
            var persistenceAttempts = 0;
            var guardedLaunches = 0;
            using var guardedContext = new MishaWeb.Program.BrowserApplicationContext(
                (_, _) => true,
                _ =>
                {
                    guardedLaunches++;
                    return true;
                },
                exitThread: () => { },
                persistRestartSession: _ =>
                {
                    persistenceAttempts++;
                    return false;
                });
            var registered = guardedContext.Register(guardedWindow);
            var mutation = guardedWindow.BeginExtensionMutationForTesting();
            var activeMutationState = guardedWindow.CaptureBrowserRestartWindowState();
            var busyResult = guardedContext.RequestBrowserProcessRestart(guardedWindow);
            mutation.Dispose();
            mutation.Dispose();
            var releasedMutationState = guardedWindow.CaptureBrowserRestartWindowState();
            var saveFailureResult = guardedContext.RequestBrowserProcessRestart(guardedWindow);
            extensionAndSaveGuardsValid = registered
                && activeMutationState.ActiveExtensionMutationCount == 1
                && busyResult == MishaWeb.Program.BrowserRestartRequestResult.ExtensionOperationInProgress
                && persistenceAttempts == 1
                && guardedLaunches == 0
                && releasedMutationState.ActiveExtensionMutationCount == 0
                && saveFailureResult == MishaWeb.Program.BrowserRestartRequestResult.SessionSaveFailed
                && guardedContext.WindowCount == 1
                && !guardedWindow.IsDisposed;
            guardedWindow.Close();
        }

        using (var closeGuardWindow = new MainForm(startBrowserOnShown: false))
        {
            var mutation = closeGuardWindow.BeginExtensionMutationForTesting();
            var closeWasDeferred = closeGuardWindow.RequestCloseDuringExtensionMutationForTesting();
            extensionCloseDeferralValid = closeWasDeferred
                && closeGuardWindow.IsChromeStoreInstallCancellationRequestedForTesting
                && closeGuardWindow.IsCloseDeferredForExtensionMutationForTesting
                && closeGuardWindow.CaptureBrowserRestartWindowState().ActiveExtensionMutationCount == 1;
            mutation.Dispose();
        }

        bool VerifyPrivateOnlyHandoff(string? address)
        {
            using var privateWindow = new MainForm(
                startBrowserOnShown: false,
                previewState: null,
                startupAddress: null,
                mode: BrowserMode.Private);
            _ = privateWindow.Handle;
            MainForm? createdNormal = null;
            using var handoffContext = new MishaWeb.Program.BrowserApplicationContext(
                exitThread: () => { },
                createNormalWindow: () =>
                {
                    createdNormal = new MainForm(startBrowserOnShown: false);
                    return createdNormal;
                },
                showNormalWindow: _ => { });
            if (!handoffContext.Register(privateWindow)) return false;
            var handoffTask = Task.Run(() => handoffContext.TryReceiveExternalNavigation(address));
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!handoffTask.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(3))
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            var accepted = handoffTask.IsCompletedSuccessfully && handoffTask.Result;
            var valid = accepted
                && createdNormal is { IsDisposed: false }
                && !createdNormal.IsPrivateBrowserWindow
                && createdNormal.HasPendingExternalNavigationForTesting(address)
                && handoffContext.WindowCount == 2;
            createdNormal?.Close();
            privateWindow.Close();
            return valid;
        }

        privateOnlyUrlHandoffValid = VerifyPrivateOnlyHandoff(encodedStartupUrl);
        privateOnlyEmptyHandoffValid = VerifyPrivateOnlyHandoff(null);
    }
    catch (Exception error)
    {
        restartUiError = error;
    }
})
{
    IsBackground = true,
    Name = "MishaWeb restart smoke"
};
restartUiThread.SetApartmentState(ApartmentState.STA);
restartUiThread.Start();
var restartUiCompleted = restartUiThread.Join(TimeSpan.FromSeconds(20));
Check(
    "two-window update restart confirms, launches once, and closes the full registry",
    restartUiCompleted && restartUiError is null && restartCoordinationValid);
Check(
    "application context requests exit only after its final registered window closes",
    restartUiCompleted && restartUiError is null && finalWindowExitValid);
Check(
    "extension mutations and failed session persistence hard-block process restart",
    restartUiCompleted && restartUiError is null && extensionAndSaveGuardsValid);
Check(
    "closing during an extension transaction cancels direct preparation and defers exit until the transaction finishes",
    restartUiCompleted && restartUiError is null && extensionCloseDeferralValid);
Check(
    "private-only URL handoff opens a normal window and acknowledges its queued address",
    restartUiCompleted && restartUiError is null && privateOnlyUrlHandoffValid);
Check(
    "private-only no-URL launch opens a normal window instead of entering private context",
    restartUiCompleted && restartUiError is null && privateOnlyEmptyHandoffValid);

const string testUserSid = "S-1-5-21-123456789-234567890-345678901-1001";
var firstInstanceIdentity = MishaWeb.Program.CreateSingleInstanceIdentity(testUserSid);
var repeatedInstanceIdentity = MishaWeb.Program.CreateSingleInstanceIdentity(testUserSid);
var otherUserIdentity = MishaWeb.Program.CreateSingleInstanceIdentity(
    "S-1-5-21-123456789-234567890-345678901-1002");
Check(
    "single-instance names are stable, user-scoped, cross-session, and do not expose the SID",
    firstInstanceIdentity == repeatedInstanceIdentity
        && firstInstanceIdentity != otherUserIdentity
        && firstInstanceIdentity.MutexName.StartsWith("Global\\", StringComparison.Ordinal)
        && !firstInstanceIdentity.MutexName.Contains(testUserSid, StringComparison.Ordinal)
        && !firstInstanceIdentity.PipeName.Contains(testUserSid, StringComparison.Ordinal)
        && firstInstanceIdentity.PipeName.Length
            == "MishaWeb-BrowserInstancePipe-".Length + 32);

var startupHandoffRouter = new MishaWeb.Program.StartupHandoffRouter(capacity: 2);
var firstStartupHandoffAccepted = startupHandoffRouter.TryAccept(encodedStartupUrl);
var duplicateStartupHandoffAccepted = startupHandoffRouter.TryAccept(encodedStartupUrl);
var secondStartupUrl = "https://example.net/queued";
var secondStartupHandoffAccepted = startupHandoffRouter.TryAccept(secondStartupUrl);
var fullStartupQueueRejected = !startupHandoffRouter.TryAccept("https://example.org/overflow");
var deliveredStartupHandoffs = new List<string?>();
var startupRouterAttached = startupHandoffRouter.Attach(address =>
{
    deliveredStartupHandoffs.Add(address);
    return true;
});
var liveHandoffAccepted = startupHandoffRouter.TryAccept(null);
Check(
    "pre-form navigation handoffs are bounded, deduplicated, ordered, and drain to the live form",
    firstStartupHandoffAccepted
        && duplicateStartupHandoffAccepted
        && secondStartupHandoffAccepted
        && fullStartupQueueRejected
        && startupRouterAttached
        && liveHandoffAccepted
        && startupHandoffRouter.PendingCount == 0
        && deliveredStartupHandoffs.SequenceEqual(
            new string?[] { encodedStartupUrl, secondStartupUrl, null }));

var rejectingHandoffRouter = new MishaWeb.Program.StartupHandoffRouter(capacity: 1);
var rejectingRouterAttached = rejectingHandoffRouter.Attach(_ => false);
Check(
    "live handoff acknowledgement reports receiver rejection",
    rejectingRouterAttached
        && !rejectingHandoffRouter.TryAccept("https://example.com/rejected"));

var acceptancePipeName = "MishaWeb-Smoke-Accept-" + Guid.NewGuid().ToString("N");
string? acceptedPipeAddress = null;
using (var acceptanceServer = new MishaWeb.Program.SingleInstanceServer(
           acceptancePipeName,
           address =>
           {
               acceptedPipeAddress = address;
               return true;
           }))
{
    acceptanceServer.Start();
    var pipeAccepted = await MishaWeb.Program.TrySendNavigationToPipeAsync(
        acceptancePipeName,
        encodedStartupUrl,
        TimeSpan.FromSeconds(3));
    Check(
        "CurrentUserOnly IPC acknowledges only after the receiver accepts the page",
        pipeAccepted && acceptedPipeAddress == encodedStartupUrl);
}

var delayedPipeName = "MishaWeb-Smoke-Delayed-" + Guid.NewGuid().ToString("N");
using (var delayedServer = new MishaWeb.Program.SingleInstanceServer(delayedPipeName, _ => true))
{
    var delayedHandoff = MishaWeb.Program.TrySendNavigationToPipeAsync(
        delayedPipeName,
        encodedStartupUrl,
        TimeSpan.FromSeconds(4));
    await Task.Delay(900);
    delayedServer.Start();
    Check(
        "IPC client retries while the first browser is still creating its pipe server",
        await delayedHandoff);
}

var rejectionPipeName = "MishaWeb-Smoke-Reject-" + Guid.NewGuid().ToString("N");
using (var rejectionServer = new MishaWeb.Program.SingleInstanceServer(
           rejectionPipeName,
           _ => false))
{
    rejectionServer.Start();
    var pipeRejected = !await MishaWeb.Program.TrySendNavigationToPipeAsync(
        rejectionPipeName,
        encodedStartupUrl,
        TimeSpan.FromSeconds(3));
    Check("IPC returns a negative acknowledgement when its queue rejects a page", pipeRejected);
}

var extensionOptions = MainForm.GetEnvironmentOptionsSnapshotForTesting();
Check(
    "browser environment enables native extensions, tracking prevention, and exclusive profile access",
    extensionOptions.AreBrowserExtensionsEnabled
        && extensionOptions.EnableTrackingPrevention
        && extensionOptions.ExclusiveUserDataFolderAccess);
Check(
    "browser environment options configure memory-saving Chromium arguments",
    extensionOptions.AdditionalBrowserArguments is not null
        && extensionOptions.AdditionalBrowserArguments.Contains("--enable-gpu-rasterization", StringComparison.Ordinal)
        && extensionOptions.AdditionalBrowserArguments.Contains("--enable-zero-copy", StringComparison.Ordinal)
        && extensionOptions.AdditionalBrowserArguments.Contains("--enable-features=CanvasOopRasterization,ParallelDownloading", StringComparison.Ordinal)
        && extensionOptions.AdditionalBrowserArguments.Contains("--disable-features=BackForwardCache,SpareRendererForSitePerProcess,PeriodicBackgroundSync", StringComparison.Ordinal)
        && extensionOptions.AdditionalBrowserArguments.Contains("--enable-quic", StringComparison.Ordinal)
        && extensionOptions.AdditionalBrowserArguments.Contains("--enable-hardware-overlays=single-fullscreen,single-on-top", StringComparison.Ordinal));
var normalProfileOptions = MainForm.GetControllerProfileSnapshotForTesting(isPrivateMode: false);
var privateProfileOptions = MainForm.GetControllerProfileSnapshotForTesting(isPrivateMode: true);
Check(
    "private tabs use a dedicated InPrivate profile",
    !normalProfileOptions.IsInPrivateModeEnabled
        && normalProfileOptions.ProfileName is null
        && privateProfileOptions.IsInPrivateModeEnabled
        && privateProfileOptions.ProfileName == "MishaWebPrivate");
Check(
    "managed extension updates are unavailable in private mode and fail closed while disabled",
    MainForm.GetExtensionUpdateEligibilityError(
        isPrivateMode: true,
        isManagedStoreExtension: true,
        isEnabled: true)?.Contains("private", StringComparison.OrdinalIgnoreCase) == true
        && MainForm.GetExtensionUpdateEligibilityError(
            isPrivateMode: false,
            isManagedStoreExtension: false,
            isEnabled: true)?.Contains("managed", StringComparison.OrdinalIgnoreCase) == true
        && MainForm.GetExtensionUpdateEligibilityError(
            isPrivateMode: false,
            isManagedStoreExtension: true,
            isEnabled: false)?.Contains("Enable", StringComparison.Ordinal) == true
        && MainForm.GetExtensionUpdateEligibilityError(
            isPrivateMode: false,
            isManagedStoreExtension: true,
            isEnabled: true) is null);
const string testStoreExtensionId = "bcjindcccaagfpapjjmafapmmgkkhgoa";
Check(
    "Chrome Web Store listing URLs yield their extension ID",
    BrowserExtensions.ParseChromeWebStoreExtensionId(
        $"https://chromewebstore.google.com/detail/json-formatter/{testStoreExtensionId}")
        == testStoreExtensionId
        && BrowserExtensions.ParseChromeWebStoreExtensionId(testStoreExtensionId.ToUpperInvariant())
            == testStoreExtensionId);
Check(
    "lookalike and malformed extension addresses are rejected",
    BrowserExtensions.ParseChromeWebStoreExtensionId(
        $"https://chromewebstore.google.com.evil.example/detail/{testStoreExtensionId}") is null
        && BrowserExtensions.ParseChromeWebStoreExtensionId(
            $"https://attacker.example@chromewebstore.google.com/detail/{testStoreExtensionId}") is null
        && BrowserExtensions.ParseChromeWebStoreExtensionId(
            $"https://chromewebstore.google.com:444/detail/{testStoreExtensionId}") is null
        && BrowserExtensions.ParseChromeWebStoreExtensionId("abcdefghijklmnopqrstuvwxzy012345") is null);
Check(
    "only exact HTTPS Chrome Web Store origins can request native installation",
    BrowserExtensions.IsChromeWebStoreOrigin("https://chromewebstore.google.com/")
        && BrowserExtensions.IsChromeWebStoreOrigin("https://chrome.google.com/webstore/")
        && !BrowserExtensions.IsChromeWebStoreOrigin("http://chromewebstore.google.com/")
        && !BrowserExtensions.IsChromeWebStoreOrigin("https://chromewebstore.google.com.evil.example/"));
Check(
    "native Store installation is limited to root extension detail pages",
    BrowserExtensions.ParseChromeWebStoreListingExtensionId(
        $"https://chromewebstore.google.com/detail/json-formatter/{testStoreExtensionId}")
        == testStoreExtensionId
        && BrowserExtensions.ParseChromeWebStoreListingExtensionId(
            $"https://chromewebstore.google.com/detail/json-formatter/{testStoreExtensionId}/reviews") is null
        && BrowserExtensions.ParseChromeWebStoreListingExtensionId(
            $"https://chromewebstore.google.com/detail/{testStoreExtensionId}/report") is null);
Check(
    "Web messaging is limited to normal exact Store origins and disabled in private mode",
    MainForm.ShouldEnableChromeStoreInstallBridge(
        $"https://chromewebstore.google.com/detail/json-formatter/{testStoreExtensionId}",
        isPrivateMode: false)
        && MainForm.ShouldEnableChromeStoreInstallBridge(
            "https://chromewebstore.google.com/category/extensions",
            isPrivateMode: false)
        && !MainForm.ShouldEnableChromeStoreInstallBridge(
            $"https://chromewebstore.google.com/detail/json-formatter/{testStoreExtensionId}",
            isPrivateMode: true)
        && !MainForm.ShouldEnableChromeStoreInstallBridge(
            "https://chromewebstore.google.com.evil.example/detail/fake/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            isPrivateMode: false));
var extensionDownloadUri = BrowserExtensions.BuildChromeWebStoreDownloadUri(
    testStoreExtensionId,
    "150.0.4078.105 beta");
Check(
    "Chrome Web Store downloads use the official update service and runtime major",
    extensionDownloadUri.Host == "clients2.google.com"
        && extensionDownloadUri.Query.Contains("prodversion=150.0.0.0", StringComparison.Ordinal)
        && Uri.UnescapeDataString(extensionDownloadUri.Query).Contains(
            $"id={testStoreExtensionId}&installsource=ondemand&uc",
            StringComparison.Ordinal));
const string otherStoreExtensionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
Check(
    "Web Store download interception requires the official service and matching listing ID",
    BrowserExtensions.ParseChromeWebStoreDownloadExtensionId(extensionDownloadUri.AbsoluteUri)
        == testStoreExtensionId
        && BrowserExtensions.MatchChromeWebStoreInstallRequest(
            $"https://chromewebstore.google.com/detail/json-formatter/{testStoreExtensionId}",
            extensionDownloadUri.AbsoluteUri) == testStoreExtensionId
        && BrowserExtensions.MatchChromeWebStoreInstallRequest(
            $"https://chromewebstore.google.com/detail/other/{otherStoreExtensionId}",
            extensionDownloadUri.AbsoluteUri) is null
        && BrowserExtensions.ParseChromeWebStoreDownloadExtensionId(
            extensionDownloadUri.AbsoluteUri.Replace(
                "clients2.google.com",
                "clients2.google.com.evil.example",
                StringComparison.Ordinal)) is null);
var chromeStoreInstallBridge = BrowserExtensions.CreateChromeWebStoreInstallBridge(
    "extension-install-channel");
Check(
    "Web Store button bridge is valid JavaScript and requires a trusted official install click",
    CanParseJavaScript(chromeStoreInstallBridge)
        && chromeStoreInstallBridge.Contains("event.isTrusted", StringComparison.Ordinal)
        && chromeStoreInstallBridge.Contains("177592", StringComparison.Ordinal)
        && chromeStoreInstallBridge.Contains("Install in MishaWeb", StringComparison.Ordinal)
        && chromeStoreInstallBridge.Contains("MutationObserver", StringComparison.Ordinal)
        && chromeStoreInstallBridge.Contains("listingId", StringComparison.Ordinal)
        && chromeStoreInstallBridge.Contains("channel+':'+id", StringComparison.Ordinal)
        && chromeStoreInstallBridge.Contains("extension-install-channel", StringComparison.Ordinal));

var extensionTestRoot = Path.Combine(
    Path.GetTempPath(),
    "MishaWeb-Extension-Smoke-" + Guid.NewGuid().ToString("N"));
try
{
    var extensionStore = Path.Combine(extensionTestRoot, "store");
    var unpackedSource = Path.Combine(extensionTestRoot, "unpacked");
    Directory.CreateDirectory(unpackedSource);
    WriteTestExtensionManifest(unpackedSource, "Fixture extension", "1.2.3", "popup/index.html");
    Directory.CreateDirectory(Path.Combine(unpackedSource, "popup"));
    File.WriteAllText(Path.Combine(unpackedSource, "popup", "index.html"), "<!doctype html><title>Fixture</title>");
    File.WriteAllText(Path.Combine(unpackedSource, "content.js"), "globalThis.__mishaExtensionFixture = true;");
    File.WriteAllText(Path.Combine(unpackedSource, "misha-extension.json"), "package-owned-content");

    var extensionService = new BrowserExtensions(extensionStore);
    var preparedUnpacked = await extensionService.ImportUnpackedAsync(unpackedSource);
    Check(
        "unpacked extensions are copied into immutable app-owned storage",
        preparedUnpacked.IsManaged
            && Directory.Exists(preparedUnpacked.FolderPath)
            && !Path.GetFullPath(preparedUnpacked.FolderPath).Equals(
                Path.GetFullPath(unpackedSource),
                StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(preparedUnpacked.FolderPath, "manifest.json"))
            && preparedUnpacked.LaunchPath == "popup/index.html");
    Check(
        "extension ownership metadata stays outside the installed extension root",
        File.ReadAllText(Path.Combine(preparedUnpacked.FolderPath, "misha-extension.json"))
            == "package-owned-content"
            && File.Exists(preparedUnpacked.FolderPath + ".misha-managed"));
    Check(
        "extension permission and host declarations are available for confirmation",
        preparedUnpacked.RequestedCapabilities.Contains("Permission: tabs")
            && preparedUnpacked.RequestedCapabilities.Contains("Site access: https://api.extension-fixture.test/*")
            && preparedUnpacked.RequestedCapabilities.Contains("Optional permission: downloads")
            && preparedUnpacked.RequestedCapabilities.Contains("Runs on: https://extension-fixture.test/*"));

    var overlappingSourceRejected = false;
    try { _ = await extensionService.ImportUnpackedAsync(extensionStore); }
    catch (InvalidDataException) { overlappingSourceRejected = true; }
    Check(
        "unpacked extension sources cannot overlap managed storage",
        overlappingSourceRejected);

    var oversizedCapabilitySource = Path.Combine(extensionTestRoot, "oversized-capability");
    Directory.CreateDirectory(oversizedCapabilitySource);
    File.WriteAllText(
        Path.Combine(oversizedCapabilitySource, "manifest.json"),
        JsonSerializer.Serialize(new
        {
            manifest_version = 3,
            name = "Oversized capability fixture",
            version = "1.0.0",
            permissions = new[] { new string('x', 4_097) }
        }));
    var oversizedCapabilityRejected = false;
    try { _ = await extensionService.ImportUnpackedAsync(oversizedCapabilitySource); }
    catch (InvalidDataException) { oversizedCapabilityRejected = true; }
    Check(
        "extension capability strings are rejected before they can inflate prompts or the registry",
        oversizedCapabilityRejected);

    const string managedRuntimeId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    extensionService.RememberInstalled(managedRuntimeId, preparedUnpacked);
    var reloadedExtensionService = new BrowserExtensions(extensionStore);
    var reloadedRecord = reloadedExtensionService.GetManagedExtension(managedRuntimeId);
    Check(
        "managed extension metadata survives a service restart",
        reloadedRecord?.FolderPath == preparedUnpacked.FolderPath
            && reloadedRecord.LaunchPath == "popup/index.html");
    reloadedExtensionService.ForgetInstalled(managedRuntimeId);
    Check(
        "removing a managed extension deletes only its owned package",
        !Directory.Exists(preparedUnpacked.FolderPath)
            && !File.Exists(preparedUnpacked.FolderPath + ".misha-managed")
            && Directory.Exists(unpackedSource)
            && reloadedExtensionService.GetManagedExtension(managedRuntimeId) is null);

    var mutationLoadsPreserveRegistryBytes = true;
    var invalidRegistryPayloads = new[]
    {
        Encoding.UTF8.GetBytes("[{not-valid-json]"),
        new byte[2 * 1024 * 1024 + 1]
    };
    for (var payloadIndex = 0; payloadIndex < invalidRegistryPayloads.Length; payloadIndex++)
    {
        var guardedStore = Path.Combine(extensionTestRoot, $"guarded-registry-{payloadIndex}");
        var guardedService = new BrowserExtensions(guardedStore);
        var guardedPrepared = await guardedService.ImportUnpackedAsync(unpackedSource);
        var guardedRegistryPath = Path.Combine(guardedStore, "managed-extensions.json");
        File.WriteAllBytes(guardedRegistryPath, invalidRegistryPayloads[payloadIndex]);
        var registryBytesBefore = File.ReadAllBytes(guardedRegistryPath);
        var markerPath = guardedPrepared.FolderPath + ".misha-managed";
        var markerBytesBefore = File.ReadAllBytes(markerPath);
        var mutationRejected = false;
        try
        {
            guardedService.RememberInstalled(
                new string((char)('c' + payloadIndex), 32),
                guardedPrepared);
        }
        catch (InvalidDataException) { mutationRejected = true; }
        mutationLoadsPreserveRegistryBytes &= mutationRejected
            && File.ReadAllBytes(guardedRegistryPath).SequenceEqual(registryBytesBefore)
            && File.ReadAllBytes(markerPath).SequenceEqual(markerBytesBefore);
        guardedService.DiscardPrepared(guardedPrepared);
    }
    Check(
        "corrupt and oversized extension registries fail closed without replacing registry or package-state bytes",
        mutationLoadsPreserveRegistryBytes);

    var unsafePathStore = Path.Combine(extensionTestRoot, "unsafe-path-registry");
    var unsafePathService = new BrowserExtensions(unsafePathStore);
    var unsafePathPrepared = await unsafePathService.ImportUnpackedAsync(unpackedSource);
    var unsafePathRegistry = Path.Combine(unsafePathStore, "managed-extensions.json");
    File.WriteAllText(
        unsafePathRegistry,
        JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                folderPath = Path.Combine(extensionTestRoot, "outside-managed-packages"),
                name = "Unsafe path fixture",
                version = "1.0.0",
                storeId = (string?)null,
                launchPath = (string?)null,
                requestedCapabilities = Array.Empty<string>()
            }
        }));
    var unsafeRegistryBytesBefore = File.ReadAllBytes(unsafePathRegistry);
    var unsafeMarkerBytesBefore = File.ReadAllBytes(
        unsafePathPrepared.FolderPath + ".misha-managed");
    var unsafePathMutationRejected = false;
    try
    {
        unsafePathService.RememberInstalled(
            "iiiiiiiiiiiiiiiiiiiiiiiiiiiiiiii",
            unsafePathPrepared);
    }
    catch (InvalidDataException) { unsafePathMutationRejected = true; }
    Check(
        "a syntactically valid registry path outside managed storage fails closed",
        unsafePathMutationRejected
            && File.ReadAllBytes(unsafePathRegistry).SequenceEqual(unsafeRegistryBytesBefore)
            && File.ReadAllBytes(unsafePathPrepared.FolderPath + ".misha-managed")
                .SequenceEqual(unsafeMarkerBytesBefore));
    unsafePathService.DiscardPrepared(unsafePathPrepared);

    var registryLimitStore = Path.Combine(extensionTestRoot, "registry-limit-store");
    var registryLimitSource = Path.Combine(extensionTestRoot, "registry-limit-source");
    Directory.CreateDirectory(registryLimitSource);
    var boundedLargeCapabilities = Enumerable.Range(0, 120)
        .Select(index => $"{index:D3}-" + new string('x', 4_000))
        .ToArray();
    File.WriteAllText(
        Path.Combine(registryLimitSource, "manifest.json"),
        JsonSerializer.Serialize(new
        {
            manifest_version = 3,
            name = "Registry size fixture",
            version = "1.0.0",
            permissions = boundedLargeCapabilities
        }));
    var registryLimitService = new BrowserExtensions(registryLimitStore);
    var registryLimitPrepared = new List<PreparedBrowserExtension>();
    var registryLimitRejected = false;
    var registryBytesStayedAuthoritative = false;
    var committedRegistryRecords = 0;
    for (var index = 0; index < 8 && !registryLimitRejected; index++)
    {
        var prepared = await registryLimitService.ImportUnpackedAsync(registryLimitSource);
        registryLimitPrepared.Add(prepared);
        var registryPath = Path.Combine(registryLimitStore, "managed-extensions.json");
        var bytesBefore = File.Exists(registryPath)
            ? File.ReadAllBytes(registryPath)
            : Array.Empty<byte>();
        try
        {
            registryLimitService.RememberInstalled(new string((char)('a' + index), 32), prepared);
            committedRegistryRecords++;
        }
        catch (InvalidDataException)
        {
            registryLimitRejected = true;
            registryBytesStayedAuthoritative = File.Exists(registryPath)
                && File.ReadAllBytes(registryPath).SequenceEqual(bytesBefore);
        }
    }
    Check(
        "serialized extension registry growth is bounded before atomic replacement",
        registryLimitRejected
            && registryBytesStayedAuthoritative
            && committedRegistryRecords >= 3
            && registryLimitService.GetManagedExtensions().Count == committedRegistryRecords);
    foreach (var prepared in registryLimitPrepared) registryLimitService.DiscardPrepared(prepared);

    var lifecycleStore = Path.Combine(extensionTestRoot, "package-lifecycle-store");
    var lifecycleService = new BrowserExtensions(lifecycleStore);
    var stalePrepared = await lifecycleService.ImportUnpackedAsync(unpackedSource);
    var stalePreparedMarker = stalePrepared.FolderPath + ".misha-managed";
    File.SetLastWriteTimeUtc(stalePreparedMarker, DateTime.UtcNow - TimeSpan.FromHours(7));
    new BrowserExtensions(lifecycleStore).ReconcileManagedExtensions(Array.Empty<string>());
    var stalePreparedRemoved = !Directory.Exists(stalePrepared.FolderPath)
        && !File.Exists(stalePreparedMarker);
    var installingPackage = await lifecycleService.ImportUnpackedAsync(unpackedSource);
    lifecycleService.MarkInstallationStarted(installingPackage);
    File.SetLastWriteTimeUtc(
        installingPackage.FolderPath + ".misha-managed",
        DateTime.UtcNow - TimeSpan.FromHours(7));
    var cleanupTrigger = await lifecycleService.ImportUnpackedAsync(unpackedSource);
    var installingPackagePreserved = Directory.Exists(installingPackage.FolderPath);
    lifecycleService.DiscardPrepared(cleanupTrigger);
    const string lifecycleRuntimeId = "ffffffffffffffffffffffffffffffff";
    lifecycleService.RememberInstalled(lifecycleRuntimeId, installingPackage);
    File.SetLastWriteTimeUtc(
        installingPackage.FolderPath + ".misha-managed",
        DateTime.UtcNow - TimeSpan.FromHours(7));
    cleanupTrigger = await lifecycleService.ImportUnpackedAsync(unpackedSource);
    var installedPackagePreserved = Directory.Exists(installingPackage.FolderPath);
    lifecycleService.DiscardPrepared(cleanupTrigger);
    lifecycleService.ForgetInstalled(lifecycleRuntimeId);

    var legacyPackage = await lifecycleService.ImportUnpackedAsync(unpackedSource);
    File.WriteAllText(
        legacyPackage.FolderPath + ".misha-managed",
        "MishaWeb managed extension package\n");
    File.SetLastWriteTimeUtc(
        legacyPackage.FolderPath + ".misha-managed",
        DateTime.UtcNow - TimeSpan.FromHours(7));
    cleanupTrigger = await lifecycleService.ImportUnpackedAsync(unpackedSource);
    var legacyPackagePreserved = Directory.Exists(legacyPackage.FolderPath);
    lifecycleService.DiscardPrepared(cleanupTrigger);
    lifecycleService.DiscardPrepared(legacyPackage);
    Check(
        "stale prepared packages are reclaimed while installing, installed, and legacy packages stay fail-safe",
        stalePreparedRemoved
            && installingPackagePreserved
            && installedPackagePreserved
            && legacyPackagePreserved);

    var activeOrphanStore = Path.Combine(extensionTestRoot, "active-installed-orphan");
    var activeOrphanService = new BrowserExtensions(activeOrphanStore);
    var activeOrphan = await activeOrphanService.ImportUnpackedAsync(unpackedSource);
    const string activeOrphanRuntimeId = "gggggggggggggggggggggggggggggggg";
    activeOrphanService.MarkInstallationStarted(activeOrphan);
    activeOrphanService.RememberInstalled(activeOrphanRuntimeId, activeOrphan);
    File.Delete(Path.Combine(activeOrphanStore, "managed-extensions.json"));
    var activeOrphanRecovery = new BrowserExtensions(activeOrphanStore);
    activeOrphanRecovery.ReconcileManagedExtensions([activeOrphanRuntimeId]);
    var activeOrphanRecord = activeOrphanRecovery.GetManagedExtension(activeOrphanRuntimeId);
    Check(
        "an installed package orphaned after WebView activation is adopted when its runtime ID is still live",
        activeOrphanRecord?.FolderPath == activeOrphan.FolderPath
            && Directory.Exists(activeOrphan.FolderPath));
    activeOrphanRecovery.ForgetInstalled(activeOrphanRuntimeId);

    var inactiveOrphanStore = Path.Combine(extensionTestRoot, "inactive-installed-orphan");
    var inactiveOrphanService = new BrowserExtensions(inactiveOrphanStore);
    var inactiveOrphan = await inactiveOrphanService.ImportUnpackedAsync(unpackedSource);
    const string inactiveOrphanRuntimeId = "hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh";
    inactiveOrphanService.MarkInstallationStarted(inactiveOrphan);
    inactiveOrphanService.RememberInstalled(inactiveOrphanRuntimeId, inactiveOrphan);
    File.Delete(Path.Combine(inactiveOrphanStore, "managed-extensions.json"));
    File.SetLastWriteTimeUtc(
        inactiveOrphan.FolderPath + ".misha-managed",
        DateTime.UtcNow - TimeSpan.FromHours(7));
    var inactiveOrphanRecovery = new BrowserExtensions(inactiveOrphanStore);
    inactiveOrphanRecovery.ReconcileManagedExtensions(Array.Empty<string>());
    Check(
        "an installed package orphan is reclaimed only after its runtime ID is absent and the marker is stale",
        !Directory.Exists(inactiveOrphan.FolderPath)
            && !File.Exists(inactiveOrphan.FolderPath + ".misha-managed"));

    var legacyMarkerStore = Path.Combine(extensionTestRoot, "legacy-marker-recovery");
    const string legacyRuntimeId = "flpnibonbhdmnpgflnbemgghghhblmpm";
    var legacyPackageFolder = Path.Combine(legacyMarkerStore, "Packages", legacyRuntimeId, "2.0.6-24aeec83");
    Directory.CreateDirectory(legacyPackageFolder);
    File.WriteAllText(
        Path.Combine(legacyPackageFolder, "manifest.json"),
        """
        {
          "manifest_version": 3,
          "name": "Ghostify Test",
          "version": "2.0.6",
          "description": "Control your visibility",
          "action": { "default_popup": "popup.html" },
          "options_ui": { "page": "options.html" }
        }
        """);
    File.WriteAllText(legacyPackageFolder + ".misha-managed", "MishaWeb managed extension package\n");
    var legacyRecovery = new BrowserExtensions(legacyMarkerStore);
    legacyRecovery.ReconcileManagedExtensions([legacyRuntimeId]);
    var legacyRecord = legacyRecovery.GetManagedExtension(legacyRuntimeId);
    Check(
        "legacy marker package is successfully adopted with format version 1 and manifest pathways",
        legacyRecord is not null
            && legacyRecord.Id == legacyRuntimeId
            && legacyRecord.StoreId == legacyRuntimeId
            && legacyRecord.Version == "2.0.6"
            && legacyRecord.PopupPath == "popup.html"
            && legacyRecord.OptionsPath == "options.html"
            && legacyRecord.Description == "Control your visibility");

    var zipBytes = CreateTestExtensionZip("CRX fixture", "2.0.0", "options.html");
    var publicKey = Encoding.ASCII.GetBytes("misha-web-extension-test-public-key");
    var crx2Bytes = CreateCrx2Package(publicKey, zipBytes);
    var crx2Header = BrowserExtensions.ReadCrxHeaderForTesting(crx2Bytes);
    Check(
        "CRX2 package headers locate the embedded ZIP archive",
        crx2Header.Version == 2 && crx2Header.ZipOffset == 16 + publicKey.Length + 8);
    byte[] signedCrx2Bytes;
    byte[] signedCrx2PublicKey;
    using (var signingKey = System.Security.Cryptography.RSA.Create(2048))
    {
        signedCrx2PublicKey = signingKey.ExportSubjectPublicKeyInfo();
        signedCrx2Bytes = CreateSignedCrx2Package(signingKey, zipBytes);
    }
    var crx2Path = Path.Combine(extensionTestRoot, "fixture.crx");
    File.WriteAllBytes(crx2Path, signedCrx2Bytes);
    var preparedCrx2 = await extensionService.ImportPackageAsync(crx2Path);
    using (var importedManifest = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(preparedCrx2.FolderPath, "manifest.json"))))
    {
        Check(
            "CRX imports preserve their identity key and launch surface",
            preparedCrx2.StoreId is null
                && preparedCrx2.LaunchPath == "options.html"
                && importedManifest.RootElement.TryGetProperty("key", out var manifestKey)
                && manifestKey.GetString() == Convert.ToBase64String(signedCrx2PublicKey));
    }
    extensionService.DiscardPrepared(preparedCrx2);

    var crx3Bytes = CreateCrx3Package(publicKey, zipBytes);
    var crx3Header = BrowserExtensions.ReadCrxHeaderForTesting(crx3Bytes);
    Check(
        "CRX3 protobuf headers locate the embedded ZIP archive",
        crx3Header.Version == 3 && crx3Header.ZipOffset > 12);
    byte[] signedCrx3;
    byte[] tamperedCrx3;
    using (var signingKey = System.Security.Cryptography.RSA.Create(2048))
    {
        signedCrx3 = CreateSignedCrx3Package(signingKey, zipBytes);
        tamperedCrx3 = signedCrx3.ToArray();
        tamperedCrx3[^1] ^= 0x01;
        Check(
            "CRX3 signatures authenticate the archive payload",
            BrowserExtensions.VerifyCrxPackageForTesting(signedCrx3)
                && !BrowserExtensions.VerifyCrxPackageForTesting(tamperedCrx3));
    }
    var crx3Path = Path.Combine(extensionTestRoot, "fixture-v3.crx");
    File.WriteAllBytes(crx3Path, signedCrx3);
    var preparedCrx3 = await extensionService.ImportPackageAsync(crx3Path);
    Check(
        "CRX3 packages import through an offset-safe ZIP stream",
        preparedCrx3.StoreId is null
            && File.Exists(Path.Combine(preparedCrx3.FolderPath, "content.js")));
    extensionService.DiscardPrepared(preparedCrx3);

    var tamperedCrx3Path = Path.Combine(extensionTestRoot, "fixture-v3-tampered.crx");
    File.WriteAllBytes(tamperedCrx3Path, tamperedCrx3);
    var tamperedPackageRejected = false;
    try { _ = await extensionService.ImportPackageAsync(tamperedCrx3Path); }
    catch (InvalidDataException) { tamperedPackageRejected = true; }
    Check("local CRX imports reject tampered signed packages", tamperedPackageRejected);

    var traversalArchivePath = Path.Combine(extensionTestRoot, "traversal.zip");
    File.WriteAllBytes(traversalArchivePath, CreateTraversalZip());
    var traversalRejected = false;
    try { _ = await extensionService.ImportPackageAsync(traversalArchivePath); }
    catch (InvalidDataException) { traversalRejected = true; }
    Check(
        "extension archives cannot escape managed storage",
        traversalRejected && !File.Exists(Path.Combine(extensionTestRoot, "escaped.txt")));

    var missingManifestSource = Path.Combine(extensionTestRoot, "missing-manifest");
    Directory.CreateDirectory(missingManifestSource);
    File.WriteAllText(Path.Combine(missingManifestSource, "script.js"), "void 0;");
    var missingManifestRejected = false;
    try { _ = await extensionService.ImportUnpackedAsync(missingManifestSource); }
    catch (InvalidDataException) { missingManifestRejected = true; }
    Check("unpacked extensions require a valid manifest", missingManifestRejected);

    var invalidCrxRejected = false;
    try
    {
        _ = BrowserExtensions.ReadCrxHeaderForTesting(
            [.. "Cr24"u8.ToArray(), 9, 0, 0, 0, 0, 0, 0, 0]);
    }
    catch (InvalidDataException) { invalidCrxRejected = true; }
    Check("unsupported CRX versions are rejected", invalidCrxRejected);

    var updateCurrent = new ManagedBrowserExtension(
        testStoreExtensionId,
        Path.Combine(extensionStore, "current-update-fixture"),
        "Update fixture",
        "1.2.3",
        testStoreExtensionId,
        "options.html",
        ["Permission: tabs"]);
    var sameVersionUpdate = new PreparedBrowserExtension(
        Path.Combine(extensionStore, "same-update-fixture"),
        "Update fixture",
        "1.2.3.0",
        testStoreExtensionId,
        "options.html",
        ["Permission: tabs"],
        IsManaged: true);
    var noUpdateEvaluation = BrowserExtensions.EvaluateExtensionUpdateForTesting(
        updateCurrent,
        sameVersionUpdate);
    Check(
        "managed extension update checks treat equivalent dotted versions as current",
        !noUpdateEvaluation.IsUpdate
            && BrowserExtensions.CompareDottedVersionsForTesting("1.10.0", "1.9.99") > 0
            && BrowserExtensions.CompareDottedVersionsForTesting("2", "2.0.0.0") == 0);

    var expandedUpdate = sameVersionUpdate with
    {
        Version = "1.2.4",
        RequestedCapabilities = ["Permission: tabs", "Site access: <all_urls>"]
    };
    var expandedEvaluation = BrowserExtensions.EvaluateExtensionUpdateForTesting(
        updateCurrent,
        expandedUpdate);
    Check(
        "managed extension updates detect newly requested capabilities before activation",
        expandedEvaluation.IsUpdate
            && expandedEvaluation.AddedCapabilities.SequenceEqual(
                ["Site access: <all_urls>"],
                StringComparer.OrdinalIgnoreCase));

    var mismatchedUpdateRejected = false;
    try
    {
        _ = BrowserExtensions.EvaluateExtensionUpdateForTesting(
            updateCurrent,
            expandedUpdate with { StoreId = otherStoreExtensionId });
    }
    catch (InvalidDataException) { mismatchedUpdateRejected = true; }
    Check(
        "managed extension updates reject a package whose signed identity does not match the installed Store ID",
        mismatchedUpdateRejected && tamperedPackageRejected);

    var giantCapabilityUpdate = expandedUpdate with
    {
        RequestedCapabilities = Enumerable.Range(0, 6_000)
            .Select(index => $"Permission: bounded-{index}")
            .ToArray()
    };
    var boundedUpdateEvaluation = BrowserExtensions.EvaluateExtensionUpdateForTesting(
        updateCurrent,
        giantCapabilityUpdate);
    var invalidVersionRejected = false;
    try { _ = BrowserExtensions.CompareDottedVersionsForTesting("1.2.3.4.5", "1.2.3"); }
    catch (InvalidDataException) { invalidVersionRejected = true; }
    Check(
        "extension update version and capability work stays strictly bounded",
        boundedUpdateEvaluation.AddedCapabilities.Count == 4_096
            && invalidVersionRejected);

    var rollbackSourceV1 = Path.Combine(extensionTestRoot, "rollback-v1");
    var rollbackSourceV2 = Path.Combine(extensionTestRoot, "rollback-v2");
    Directory.CreateDirectory(rollbackSourceV1);
    Directory.CreateDirectory(rollbackSourceV2);
    WriteTestExtensionManifest(rollbackSourceV1, "Rollback fixture", "3.0.0", "options.html");
    WriteTestExtensionManifest(rollbackSourceV2, "Rollback fixture", "3.1.0", "options.html");
    var rollbackCurrentPrepared = (await extensionService.ImportUnpackedAsync(rollbackSourceV1)) with
    {
        StoreId = testStoreExtensionId
    };
    extensionService.RememberInstalled(testStoreExtensionId, rollbackCurrentPrepared);
    var rollbackCandidate = (await extensionService.ImportUnpackedAsync(rollbackSourceV2)) with
    {
        StoreId = testStoreExtensionId
    };
    var rollbackCurrentRecord = extensionService.GetManagedExtension(testStoreExtensionId)!;
    var rollbackEvaluation = BrowserExtensions.EvaluateExtensionUpdateForTesting(
        rollbackCurrentRecord,
        rollbackCandidate);
    var oldPackagePreservedBeforeCommit = rollbackEvaluation.IsUpdate
        && Directory.Exists(rollbackCurrentPrepared.FolderPath)
        && Directory.Exists(rollbackCandidate.FolderPath)
        && extensionService.GetManagedExtension(testStoreExtensionId)?.FolderPath
            == rollbackCurrentPrepared.FolderPath;
    extensionService.DiscardPrepared(rollbackCandidate);
    Check(
        "canceling or rolling back an extension update preserves the installed package and removes only the candidate",
        oldPackagePreservedBeforeCommit
            && Directory.Exists(rollbackCurrentPrepared.FolderPath)
            && !Directory.Exists(rollbackCandidate.FolderPath)
            && extensionService.GetManagedExtension(testStoreExtensionId)?.FolderPath
                == rollbackCurrentPrepared.FolderPath);
    extensionService.ForgetInstalled(testStoreExtensionId);

    var userScriptOne = Path.Combine(extensionStore, "01-first.js");
    var userScriptTwo = Path.Combine(extensionStore, "02-second.js");
    var unscopedUserScript = Path.Combine(extensionStore, "03-unscoped.js");
    Directory.CreateDirectory(extensionStore);
    File.WriteAllText(userScriptOne, "// @match https://first.example/*\nglobalThis.first = true;");
    File.WriteAllText(userScriptTwo, "// @match https://*.second.example/*\n// @exclude https://private.second.example/*\nglobalThis.second = true;");
    File.WriteAllText(unscopedUserScript, "globalThis.unscoped = true;");
    var scopedUserScripts = extensionService.LoadScripts();
    Check(
        "legacy JavaScript user scripts remain ordered, separate, and origin-scoped",
        scopedUserScripts.Count == 2
            && scopedUserScripts[0].Contains("globalThis.first = true", StringComparison.Ordinal)
            && scopedUserScripts[0].Contains("first.example", StringComparison.Ordinal)
            && scopedUserScripts[1].Contains("globalThis.second = true", StringComparison.Ordinal)
            && scopedUserScripts[1].Contains("private.second.example", StringComparison.Ordinal)
            && scopedUserScripts.All(script =>
                script.Contains("u.protocol!=='http:'", StringComparison.Ordinal)
                && script.Contains("chromewebstore.google.com", StringComparison.Ordinal)));
    Check(
        "legacy JavaScript without explicit match metadata is disabled",
        scopedUserScripts.All(script => !script.Contains("globalThis.unscoped", StringComparison.Ordinal)));
}
finally
{
    try
    {
        if (Directory.Exists(extensionTestRoot)) Directory.Delete(extensionTestRoot, recursive: true);
    }
    catch { }
}

var promptCapabilities = Enumerable.Range(0, 24)
    .Select(index => $"Permission: harmless-{index:00}")
    .Append("Site access: <all_urls>")
    .Append("Permission: nativeMessaging")
    .ToArray();
var visiblePromptCapabilities = MainForm.SelectExtensionCapabilitiesForPrompt(
    promptCapabilities,
    maximumOrdinaryCapabilities: 18);
Check(
    "extension consent always surfaces high-risk declarations before capped ordinary entries",
    visiblePromptCapabilities.Count == 21
        && visiblePromptCapabilities[0].StartsWith("HIGH RISK", StringComparison.Ordinal)
        && visiblePromptCapabilities[1].StartsWith("HIGH RISK", StringComparison.Ordinal)
        && visiblePromptCapabilities.Any(value =>
            value.Contains("<all_urls>", StringComparison.OrdinalIgnoreCase))
        && visiblePromptCapabilities.Any(value =>
            value.Contains("nativeMessaging", StringComparison.OrdinalIgnoreCase))
        && visiblePromptCapabilities[^1]
            == "…and 6 additional lower-risk declaration(s)");
var officialWarningCapabilities = new[]
{
    "Permission: proxy",
    "Permission: desktopCapture",
    "Permission: management",
    "Permission: privacy",
    "Permission: tabs",
    "Permission: webNavigation",
    "Permission: clipboardWrite",
    "Permission: bookmarks",
    "Permission: favicon",
    "Permission: geolocation",
    "Permission: declarativeNetRequest",
    "Permission: harmless"
};
var visibleOfficialWarnings = MainForm.SelectExtensionCapabilitiesForPrompt(
    officialWarningCapabilities,
    maximumOrdinaryCapabilities: 1);
var harmlessWarningIndex = visibleOfficialWarnings.FindIndex(value =>
    value.Equals("Permission: harmless", StringComparison.Ordinal));
Check(
    "extension consent highlights current Chrome warning-bearing permissions before ordinary access",
    harmlessWarningIndex == visibleOfficialWarnings.Count - 1
        && visibleOfficialWarnings.Take(harmlessWarningIndex).All(value =>
            value.StartsWith("HIGH RISK", StringComparison.Ordinal))
        && visibleOfficialWarnings.Any(value =>
            value.Contains("browser network traffic", StringComparison.Ordinal))
        && visibleOfficialWarnings.Any(value =>
            value.Contains("capture screen", StringComparison.Ordinal))
        && visibleOfficialWarnings.Any(value =>
            value.Contains("browsing activity", StringComparison.Ordinal))
        && visibleOfficialWarnings.Any(value =>
            value.Contains("clipboard", StringComparison.Ordinal))
        && visibleOfficialWarnings.Any(value =>
            value.Contains("icons of websites", StringComparison.Ordinal)));
var overflowWarningCapabilities = new[]
{
    "Permission: proxy",
    "Permission: debugger",
    "Permission: nativeMessaging",
    "Permission: desktopCapture",
    "Permission: pageCapture",
    "Permission: tabCapture",
    "Permission: webAuthenticationProxy",
    "Permission: management",
    "Permission: privacy",
    "Permission: contentSettings",
    "Permission: tabs",
    "Permission: webNavigation",
    "Permission: history",
    "Permission: clipboardRead",
    "Permission: bookmarks"
};
var boundedOverflowWarnings = MainForm.SelectExtensionCapabilitiesForPrompt(
    overflowWarningCapabilities,
    maximumOrdinaryCapabilities: 0);
Check(
    "extension consent safely aggregates excessive sensitive declarations",
    boundedOverflowWarnings.Count == 13
        && boundedOverflowWarnings.Take(12).All(value =>
            value.StartsWith("HIGH RISK", StringComparison.Ordinal))
        && boundedOverflowWarnings[^1].Contains(
            "3 additional sensitive access declaration(s)",
            StringComparison.Ordinal));
var giantPromptCapabilities = Enumerable.Range(0, 5_000)
    .Select(index => index switch
    {
        4_093 => "Permission: proxy",
        4_094 => "Site access: https://sensitive-tail.example/*",
        4_095 => "Permission: nativeMessaging",
        _ => $"Permission: harmless-{index:0000}"
    })
    .ToArray();
var boundedGiantPrompt = MainForm.SelectExtensionCapabilitiesForPrompt(
    giantPromptCapabilities,
    maximumOrdinaryCapabilities: 18);
Check(
    "extension consent stays bounded while surfacing sensitive declarations at the scan tail",
    boundedGiantPrompt.Count == 23
        && boundedGiantPrompt[0].StartsWith("HIGH RISK", StringComparison.Ordinal)
        && boundedGiantPrompt[1].StartsWith("HIGH RISK", StringComparison.Ordinal)
        && boundedGiantPrompt[2].StartsWith("HIGH RISK", StringComparison.Ordinal)
        && boundedGiantPrompt.Any(value =>
            value.Contains("sensitive-tail.example", StringComparison.OrdinalIgnoreCase))
        && boundedGiantPrompt.Any(value =>
            value.Contains("nativeMessaging", StringComparison.OrdinalIgnoreCase))
        && boundedGiantPrompt.Any(value =>
            value.Contains("browser network traffic", StringComparison.Ordinal))
        && boundedGiantPrompt.Any(value =>
            value.Contains("904 additional manifest declaration(s)", StringComparison.Ordinal)));
var sanitizedPrompt = MainForm.SanitizeExtensionPromptText(
    "safe\u202Etxt\u2066name",
    100);
Check(
    "extension consent strips bidi and other invisible format controls",
    !sanitizedPrompt.Contains('\u202E') && !sanitizedPrompt.Contains('\u2066'));

Check("blank opens start page", BrowserPolicy.ResolveAddress("  ").IsStartPage);
Check("about:blank opens start page", BrowserPolicy.ResolveAddress("about:blank").IsStartPage);
Check(
    "WebView popup bootstrap URLs remain un-navigated pages until NewWindow attachment",
    MainForm.ResolveNewTabInput(
        "about:blank",
        BrowserPolicy.DefaultSearchProviderId,
        waitForPopupNavigation: true,
        trustedExtensionPage: false) is { IsStartPage: false, Url: "about:blank", Error: null }
    && MainForm.ResolveNewTabInput(
        "blob:https://www.messenger.com/call-window",
        BrowserPolicy.DefaultSearchProviderId,
        waitForPopupNavigation: true,
        trustedExtensionPage: false) is
    {
        IsStartPage: false,
        Url: "blob:https://www.messenger.com/call-window",
        Error: null
    }
    && MainForm.ResolveNewTabInput(
        "about:blank",
        BrowserPolicy.DefaultSearchProviderId,
        waitForPopupNavigation: false,
        trustedExtensionPage: false).IsStartPage
    && MainForm.ResolveNewTabInput(
        "javascript:alert(1)",
        BrowserPolicy.DefaultSearchProviderId,
        waitForPopupNavigation: true,
        trustedExtensionPage: false).Error is not null);
Check("app-owned URL opens start page", BrowserPolicy.ResolveAddress(StartPage.Url).IsStartPage);
CheckUrl("bare domain gets HTTPS", "example.com", "https://example.com/");
CheckUrl("www domain gets HTTPS", "www.example.com/docs", "https://www.example.com/docs");
CheckUrl("localhost gets HTTP", "localhost:5173/app", "http://localhost:5173/app");
CheckUrl("localhost with a trailing dot gets HTTP", "localhost.:5173/app", "http://localhost.:5173/app");
CheckUrl("reserved localhost subdomains get HTTP", "app.localhost:5173/app", "http://app.localhost:5173/app");
CheckUrl("IPv4 gets HTTP", "127.0.0.1:8080", "http://127.0.0.1:8080/");
Check(
    "search text uses Google",
    BrowserPolicy.ResolveAddress("fast browser").Url == "https://www.google.com/search?q=fast%20browser");
Check(
    "Google is the only configured search provider",
    BrowserPolicy.SearchProviderName == "Google"
        && BrowserPolicy.AvailableSearchProviders.Count == 1
        && BrowserPolicy.AvailableSearchProviders[0].Id == BrowserPolicy.DefaultSearchProviderId);
Check(
    "legacy provider choices fall back to Google",
    BrowserPolicy.NormalizeSearchProviderId("bing") == BrowserPolicy.DefaultSearchProviderId
        && BrowserPolicy.ResolveAddress("fast browser", "duckduckgo").Url
            == "https://www.google.com/search?q=fast%20browser");
Check("unsafe scheme is rejected", BrowserPolicy.ResolveAddress("javascript:alert(1)").Error is not null);
Check(
    "oversized addresses are rejected before navigation or persistence",
    BrowserPolicy.ResolveAddress("https://example.com/" + new string('x', BrowserPolicy.MaximumUrlLength)).Error is not null);
Check(
    "Unicode searches that encode beyond the navigation limit are rejected coherently",
    BrowserPolicy.ResolveAddress(new string('界', 512)).Error is not null);
Check(
    "exact IPv6 and internationalized hosts normalize to canonical comparison keys",
    BrowserPolicy.NormalizeExactHost("[2001:0db8:0:0:0:0:0:1]") == "2001:db8::1"
        && BrowserPolicy.NormalizeExactHost("BÜCHER.example") == "xn--bcher-kva.example");
Check(
    "UNC paths and network file URLs are rejected without becoming browser navigations",
    BrowserPolicy.ResolveAddress(@"\\server\share\page.html").Error is not null
        && BrowserPolicy.ResolveAddress("file://server/share/page.html").Error is not null
        && !BrowserPolicy.IsSafeTopLevelUrl("file://server/share/page.html", allowFileScheme: true));

var scopedSavedItemsState = new BrowserState
{
    Bookmarks = [new BookmarkEntry("Favorite title", "https://same.example/")],
    History = [new HistoryEntry("History title", "https://same.example/", DateTime.UtcNow)],
    PinnedStartPageLinks = [new PinnedStartPageLink("Quick-link title", "https://same.example/")]
};
Check(
    "saved-item removal affects only the selected collection",
    MainForm.RemoveSavedItem(scopedSavedItemsState, SavedItemsView.Favorites, "https://same.example/")
        && scopedSavedItemsState.Bookmarks.Count == 0
        && scopedSavedItemsState.History.Count == 1
        && scopedSavedItemsState.PinnedStartPageLinks.Count == 1);
Check(
    "saved-item rename affects only the selected collection",
    MainForm.RenameSavedItem(scopedSavedItemsState, SavedItemsView.History, "https://same.example/", "Renamed history")
        && scopedSavedItemsState.History[0].Title == "Renamed history"
        && scopedSavedItemsState.PinnedStartPageLinks[0].Title == "Quick-link title");

var rankedCommands = CommandRankingEngine.RankCandidates(
[
    new CommandCandidate("Open tab", "https://open.example/", CommandSource.OpenTab, "https://open.example/", MruRank: 0),
    new CommandCandidate("Open favorite", "https://favorite.example/", CommandSource.Favorite, "https://favorite.example/"),
    new CommandCandidate("Open", "browser command", CommandSource.BrowserCommand, Target: "open"),
    new CommandCandidate("Unsafe", "javascript:alert(1)", CommandSource.History, "javascript:alert(1)"),
    new CommandCandidate("Subsequence candidate", "detail", CommandSource.History, "https://history.example/")
],
"open");
Check(
    "command ranking prefers exact and browser-command sources",
    rankedCommands.Count >= 3
        && rankedCommands[0].Title == "Open"
        && rankedCommands.Any(item => item.Title == "Open tab")
        && rankedCommands.All(item => item.Title != "Unsafe"));
Check(
    "command ranking recognizes cheap subsequences",
    CommandRankingEngine.RankCandidates(
        [new CommandCandidate("Subsequence candidate", "detail", CommandSource.History, "https://history.example/")],
        "sqce").Count == 1);
Check(
    "command ranking caps palette rows",
    CommandRankingEngine.RankCandidates(
        Enumerable.Range(0, 20).Select(index => new CommandCandidate(
            $"Command {index}", "detail", CommandSource.BrowserCommand, Target: $"command-{index}")),
        string.Empty,
        maximumRows: 10).Count == 10);

Check(
    "clean links remove tracking parameters while preserving order and fragments",
    CleanLinkPolicy.Clean(
        "https://example.com/path?ref=x&utm_source=news&ID=7&utm_source=duplicate#section")
        == "https://example.com/path?ref=x&ID=7#section");
Check(
    "clean links decode removable parameter names without changing retained encoding",
    CleanLinkPolicy.Clean("https://example.com/?%75%74%6D_medium=email&source=a%2Bb")
        == "https://example.com/?source=a%2Bb");
Check(
    "clean links remove modern ad-network click identifiers conservatively",
    CleanLinkPolicy.Clean(
        "https://example.com/product?id=7&ttclid=tiktok&twclid=x&srsltid=shopping&epik=pin")
        == "https://example.com/product?id=7");
Check(
    "clean links preserve signed URLs",
    CleanLinkPolicy.Clean("https://example.com/file?utm_source=x&Signature=keep")
        == "https://example.com/file?utm_source=x&Signature=keep");
Check(
    "clean links preserve malformed input",
    CleanLinkPolicy.Clean("https://example.com/?utm_source=%ZZ")
        == "https://example.com/?utm_source=%ZZ");
Check(
    "clean links reject non-http input",
    CleanLinkPolicy.Clean("javascript:alert(1)?utm_source=x")
        == "javascript:alert(1)?utm_source=x");

var featureState = new BrowserState
{
    OpenTabs =
    [
        "https://pinned-one.example/",
        "javascript:alert(1)",
        "https://pinned-two.example/",
        "https://duplicate.example/",
        "https://duplicate.example/"
    ],
    PinnedOpenTabCount = 3,
    ActiveTabIndex = 4,
    NamedSessions =
    [
        new SavedSessionEntry(
            " Work ",
            DateTimeOffset.UtcNow.AddMinutes(-10),
            [
                new SavedSessionTab("https://old.example/", false),
                new SavedSessionTab("https://new.example/", true),
                new SavedSessionTab(StartPage.Url, true),
                new SavedSessionTab("javascript:alert(1)", true)
            ],
            1),
        new SavedSessionEntry(
            "work",
            DateTimeOffset.UtcNow,
            [new SavedSessionTab("https://latest.example/", false)],
            0),
        new SavedSessionEntry("   ", DateTimeOffset.UtcNow, [new SavedSessionTab("https://ignored.example/", false)], 0)
    ],
    RecentlyClosed =
    [
        new ClosedTabEntry("  New\tTitle\u0001 ", "https://newest.example/", DateTimeOffset.UtcNow),
        new ClosedTabEntry("Unsafe", "javascript:alert(1)", DateTimeOffset.UtcNow.AddMinutes(-1))
    ],
    SiteZoom =
    [
        new SiteZoomEntry("EXAMPLE.com", 9),
        new SiteZoomEntry("example.com", 1),
        new SiteZoomEntry("other.example", 0.1)
    ],
    MutedHosts = ["EXAMPLE.com", "example.com", "bad host"],
    AllowedAudioHosts = ["SOUND.example.com", "sound.example.com", "bad host"],
    AllowedMicrophoneHosts = ["MIC.example.com", "mic.example.com"],
    BlockedMicrophoneHosts = ["BLOCKED.example.com", "blocked.example.com"]
};
BrowserStateStore.NormalizeForPersistence(featureState);
Check(
    "open-tab normalization removes unsafe URLs and remaps the active index",
    featureState.OpenTabs.SequenceEqual(
        ["https://pinned-one.example/", "https://pinned-two.example/", "https://duplicate.example/", "https://duplicate.example/"])
        && featureState.PinnedOpenTabCount == 2
        && featureState.ActiveTabIndex == 3);
Check(
    "session normalization deduplicates names and preserves the newest session",
    featureState.NamedSessions.Count == 1
        && featureState.NamedSessions[0].Name == "work"
        && featureState.NamedSessions[0].Tabs[0].Url == "https://latest.example/");
Check(
    "site preference normalization clamps zoom and exact-host mute entries",
    featureState.SiteZoom.Count == 1
        && featureState.SiteZoom[0].Host == "other.example"
        && featureState.SiteZoom[0].ZoomFactor == BrowserStateStore.MinimumSiteZoom
        && featureState.MutedHosts.SequenceEqual(["example.com"]));
Check(
    "voice and audio settings normalization normalizes and deduplicates host lists",
    featureState.AudioOutputPolicy == AudioOutputPolicy.AllowAll
        && featureState.AudioInputPolicy == AudioInputPolicy.AskEveryTime
        && featureState.AllowedAudioHosts.SequenceEqual(["sound.example.com"])
        && featureState.AllowedMicrophoneHosts.SequenceEqual(["mic.example.com"])
        && featureState.BlockedMicrophoneHosts.SequenceEqual(["blocked.example.com"]));
Check(
    "recently closed normalization sanitizes titles and filters unsafe URLs",
    featureState.RecentlyClosed.Count == 1
        && featureState.RecentlyClosed[0].Title == "New Title"
        && featureState.RecentlyClosed[0].Url == "https://newest.example/");
Check(
    "site preferences match exact hosts across HTTP and HTTPS",
    SitePreferencePolicy.GetHost("https://EXAMPLE.com/path") == "example.com"
        && SitePreferencePolicy.GetZoom(featureState.SiteZoom, "http://other.example/page")
            == BrowserStateStore.MinimumSiteZoom
        && SitePreferencePolicy.IsMuted(featureState.MutedHosts, "https://example.com/page"));
Check(
    "ShouldMuteAudio respects AllowAll, SpecificSitesOnly, and MuteAll policies",
    !SitePreferencePolicy.ShouldMuteAudio(AudioOutputPolicy.AllowAll, ["sound.example.com"], ["muted.example.com"], "https://sound.example.com/play")
        && SitePreferencePolicy.ShouldMuteAudio(AudioOutputPolicy.AllowAll, ["sound.example.com"], ["muted.example.com"], "https://muted.example.com/play")
        && !SitePreferencePolicy.ShouldMuteAudio(AudioOutputPolicy.SpecificSitesOnly, ["sound.example.com"], [], "https://sound.example.com/play")
        && SitePreferencePolicy.ShouldMuteAudio(AudioOutputPolicy.SpecificSitesOnly, ["sound.example.com"], [], "https://other.example.com/play")
        && SitePreferencePolicy.ShouldMuteAudio(AudioOutputPolicy.MuteAll, ["sound.example.com"], [], "https://sound.example.com/play"));

bool? micDecision;
Check(
    "CanRequestMicrophone respects BlockAll, SpecificSitesOnly, and AskEveryTime policies",
    !SitePreferencePolicy.CanRequestMicrophone(AudioInputPolicy.BlockAll, ["mic.example.com"], [], "https://mic.example.com/", out micDecision)
        && micDecision == false
        && SitePreferencePolicy.CanRequestMicrophone(AudioInputPolicy.SpecificSitesOnly, ["mic.example.com"], [], "https://mic.example.com/", out micDecision)
        && micDecision == true
        && !SitePreferencePolicy.CanRequestMicrophone(AudioInputPolicy.SpecificSitesOnly, ["mic.example.com"], [], "https://unlisted.example.com/", out micDecision)
        && micDecision == false
        && SitePreferencePolicy.CanRequestMicrophone(AudioInputPolicy.AskEveryTime, ["mic.example.com"], ["blocked.example.com"], "https://mic.example.com/", out micDecision)
        && micDecision == true
        && !SitePreferencePolicy.CanRequestMicrophone(AudioInputPolicy.AskEveryTime, ["mic.example.com"], ["blocked.example.com"], "https://blocked.example.com/", out micDecision)
        && micDecision == false
        && SitePreferencePolicy.CanRequestMicrophone(AudioInputPolicy.AskEveryTime, ["mic.example.com"], ["blocked.example.com"], "https://unlisted.example.com/", out micDecision)
        && micDecision == null);

var voiceAudioDialogVerified = false;
var voiceAudioThread = new Thread(() =>
{
    using var voiceAudioDialog = new VoiceAudioSettingsDialog(
        new BrowserState
        {
            AudioOutputPolicy = AudioOutputPolicy.SpecificSitesOnly,
            AllowedAudioHosts = ["allowed.example.com"],
            AudioInputPolicy = AudioInputPolicy.SpecificSitesOnly,
            AllowedMicrophoneHosts = ["mic.example.com"]
        },
        "current.example.com",
        _ => { });
    voiceAudioDialogVerified = voiceAudioDialog.Text == "Voice and audio settings"
        && voiceAudioDialog.Controls.Count > 0;
});
voiceAudioThread.SetApartmentState(ApartmentState.STA);
voiceAudioThread.Start();
voiceAudioThread.Join();
Check("VoiceAudioSettingsDialog instantiates and exposes controls cleanly", voiceAudioDialogVerified);

var mruModel = new MruTabModel<string>();
mruModel.ObserveActivation("a");
mruModel.ObserveActivation("b");
mruModel.ObserveActivation("a");
var mruSnapshot = mruModel.Begin("a");
Check(
    "MRU switching captures the prior tab without mutating order",
    mruSnapshot.Items.SequenceEqual(["b"])
        && mruSnapshot.Selected == "b"
        && mruModel.Order.SequenceEqual(["a", "b"]));
mruModel.Commit(mruSnapshot);
Check("MRU commit promotes the selected tab", mruModel.Order.SequenceEqual(["b", "a"]));

Check("native feature surfaces construct without a browser environment", CanConstructFeatureSurfaces());

Check(
    "communication compatibility recognizes supported HTTPS call sites and their subdomains",
    CommunicationCompatibilityPolicy.IsCallSite("https://www.messenger.com/t/123")
        && CommunicationCompatibilityPolicy.IsCallSite("https://web.facebook.com/messages/t/123")
        && CommunicationCompatibilityPolicy.IsCallSite("https://canary.discord.com/channels/1/2")
        && CommunicationCompatibilityPolicy.IsCallSite("https://app.zoom.us/wc/123/start"));
Check(
    "communication compatibility rejects insecure, lookalike, and non-web call-site URLs",
    !CommunicationCompatibilityPolicy.IsCallSite("http://discord.com/channels/1/2")
        && !CommunicationCompatibilityPolicy.IsCallSite("https://discord.com.evil.example/channels/1/2")
        && !CommunicationCompatibilityPolicy.IsCallSite("javascript:alert(1)")
        && !CommunicationCompatibilityPolicy.IsCallSite(null));
Check(
    "communication shield bypass is limited to WebSocket and media traffic on call sites",
    CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://www.messenger.com/t/123",
        "wss://edge-chat.facebook.com/chat",
        AdBlockResourceType.WebSocket,
        mediaAccessGranted: false)
    && CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://discord.com/channels/1/2",
        "https://cdn.discord.media/call/audio.webm",
        AdBlockResourceType.Media,
        mediaAccessGranted: false)
    && !CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://app.zoom.us/wc/123/start",
        "https://app.zoom.us/api/call",
        AdBlockResourceType.Fetch,
        mediaAccessGranted: false)
    && !CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://app.zoom.us/wc/123/start",
        "https://source.zoom.us/app.js",
        AdBlockResourceType.Script,
        mediaAccessGranted: false));
Check(
    "communication shield bypass never expands to unrelated pages or third-party transports",
    !CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://example.com/meeting",
        "wss://gateway.discord.gg/",
        AdBlockResourceType.WebSocket,
        mediaAccessGranted: false)
    && !CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://zoom.us.attacker.example/meeting",
        "https://source.zoom.us/call.webm",
        AdBlockResourceType.Media,
        mediaAccessGranted: false)
    && !CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://app.zoom.us/wc/123/start",
        "wss://tracker.example/socket",
        AdBlockResourceType.WebSocket,
        mediaAccessGranted: false));
Check(
    "granted media access supports same-origin secure WebSocket and media traffic on any HTTPS call site",
    CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://meet.example/room",
        "wss://meet.example/session",
        AdBlockResourceType.WebSocket,
        mediaAccessGranted: true)
    && CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://meet.example/room",
        "https://meet.example/audio.webm",
        AdBlockResourceType.Media,
        mediaAccessGranted: true)
    && !CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://meet.example/room",
        "wss://realtime.call-vendor.example/session",
        AdBlockResourceType.WebSocket,
        mediaAccessGranted: true)
    && !CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://meet.example/room",
        "wss://meet.example:8443/session",
        AdBlockResourceType.WebSocket,
        mediaAccessGranted: true)
    && !CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://meet.example/room",
        "wss://meet.example/session",
        AdBlockResourceType.WebSocket,
        mediaAccessGranted: false)
    && !CommunicationCompatibilityPolicy.ShouldBypassShield(
        "http://meet.example/room",
        "wss://meet.example/session",
        AdBlockResourceType.WebSocket,
        mediaAccessGranted: true)
    && !CommunicationCompatibilityPolicy.ShouldBypassShield(
        "https://meet.example/room",
        "ws://meet.example/session",
        AdBlockResourceType.WebSocket,
        mediaAccessGranted: true));
Check(
    "microphone or camera access protects a call tab from background suspension",
    CommunicationCompatibilityPolicy.ShouldProtectBackgroundTab(false, true, false)
        && CommunicationCompatibilityPolicy.ShouldProtectBackgroundTab(false, false, true)
        && CommunicationCompatibilityPolicy.ShouldProtectBackgroundTab(true, false, false)
        && !CommunicationCompatibilityPolicy.ShouldProtectBackgroundTab(false, false, false));
Check(
    "communication popup trust preserves same-provider aliases, blobs, and bootstrap windows",
    CommunicationCompatibilityPolicy.IsTrustedPopup(
        "https://www.messenger.com/t/123",
        "https://web.facebook.com/call/123")
    && CommunicationCompatibilityPolicy.IsTrustedPopup(
        "https://discord.com/channels/1/2",
        "blob:https://canary.discordapp.com/call-id")
    && CommunicationCompatibilityPolicy.IsTrustedPopup(
        "https://app.zoom.us/wc/123/start",
        "https://us02web.zoomgov.com/j/123")
    && CommunicationCompatibilityPolicy.IsTrustedPopup(
        "https://discord.com/channels/1/2",
        "about:blank"));
Check(
    "communication popup trust rejects cross-provider, insecure, and spoofed handoffs",
    !CommunicationCompatibilityPolicy.IsTrustedPopup(
        "https://discord.com/channels/1/2",
        "https://zoom.us/j/123")
    && !CommunicationCompatibilityPolicy.IsTrustedPopup(
        "https://app.zoom.us/wc/123/start",
        "http://zoom.us/j/123")
    && !CommunicationCompatibilityPolicy.IsTrustedPopup(
        "https://www.messenger.com/t/123",
        "https://facebook.com.attacker.example/call/123")
    && !CommunicationCompatibilityPolicy.IsTrustedPopup(
        "https://example.com/meeting",
        "about:blank")
    && !CommunicationCompatibilityPolicy.IsTrustedPopup(
        "https://meet.example/room",
        "https://different.example/call-window")
    && !CommunicationCompatibilityPolicy.IsTrustedPopup(
        "https://meet.example/room",
        "https://meet.example/call-window")
    && !CommunicationCompatibilityPolicy.IsTrustedPopup(
        "http://meet.example/room",
        "about:blank"));

var adBlockTestEngine = new AdBlockEngine(initialRules:
[
    "||ads.example^",
    "@@||ads.example/allowed.js",
    "||tracking.example^$third-party"
]);
Check(
    "adblock fallback blocks a known ad host",
    adBlockTestEngine.ShouldBlock(
        "https://cdn.ads.example/banner.js",
        "https://publisher.example/watch",
        AdBlockResourceType.Script));
Check(
    "adblock exceptions allow an explicitly permitted resource",
    !adBlockTestEngine.ShouldBlock(
        "https://cdn.ads.example/allowed.js",
        "https://publisher.example/watch",
        AdBlockResourceType.Script));
Check(
    "third-party rules do not block first-party resources",
    !adBlockTestEngine.ShouldBlock(
        "https://www.tracking.example/pixel",
        "https://tracking.example/page",
        AdBlockResourceType.Image));
Check(
    "requests with an unknown worker origin are treated conservatively as third-party",
    adBlockTestEngine.ShouldBlock(
        "https://www.tracking.example/pixel",
        string.Empty,
        AdBlockResourceType.Image));
Check(
    "unattributed worker requests never inherit another open tab's shield exception",
    !MainForm.ShouldBypassUnknownWorkerRequest(
        workerSource: true,
        sourceUrl: string.Empty,
        openPageUrls: ["https://allowed-worker.example/app"],
        exceptionHosts: ["allowed-worker.example"]));
Check(
    "known worker origins and unrelated pages keep normal filtering",
    !MainForm.ShouldBypassUnknownWorkerRequest(
        workerSource: true,
        sourceUrl: "https://known-worker.example/",
        openPageUrls: ["https://allowed-worker.example/app"],
        exceptionHosts: ["allowed-worker.example"])
        && !MainForm.ShouldBypassUnknownWorkerRequest(
            workerSource: true,
            sourceUrl: string.Empty,
            openPageUrls: ["https://other.example/app"],
            exceptionHosts: ["allowed-worker.example"]));

var privateSuffixPartyEngine = new AdBlockEngine(initialRules:
[
    "||beta.github.io^$third-party"
]);
Check(
    "private public-suffix tenants are classified as separate parties",
    privateSuffixPartyEngine.ShouldBlock(
        "https://beta.github.io/ad.js",
        "https://alpha.github.io/page",
        AdBlockResourceType.Script));
Check(
    "subdomains of one private-suffix tenant remain first-party",
    !privateSuffixPartyEngine.ShouldBlock(
        "https://cdn.beta.github.io/ad.js",
        "https://www.beta.github.io/page",
        AdBlockResourceType.Script));

var countrySuffixPartyEngine = new AdBlockEngine(initialRules:
[
    "||b.id.au^$third-party"
]);
Check(
    "authoritative public-suffix boundaries separate id.au registrants",
    countrySuffixPartyEngine.ShouldBlock(
        "https://b.id.au/ad.js",
        "https://a.id.au/page",
        AdBlockResourceType.Script));

var hostPathRuleEngine = new AdBlockEngine(initialRules:
[
    "||path-filter.invalid/pagead/",
    "||path-filter.invalid/exact-ad|"
]);
Check(
    "host-anchored adblock paths implicitly match longer suffixes and queries",
    hostPathRuleEngine.ShouldBlock(
        "https://path-filter.invalid/pagead/adview?slot=pre-roll",
        "https://publisher.example/watch",
        AdBlockResourceType.XmlHttpRequest));
Check(
    "host-anchored adblock paths preserve path boundaries",
    !hostPathRuleEngine.ShouldBlock(
        "https://path-filter.invalid/pageaddress/content.js",
        "https://publisher.example/watch",
        AdBlockResourceType.Script));
Check(
    "end-anchored adblock paths match the exact URL",
    hostPathRuleEngine.ShouldBlock(
        "https://path-filter.invalid/exact-ad",
        "https://publisher.example/watch",
        AdBlockResourceType.Script));
Check(
    "end-anchored adblock paths reject a query suffix",
    !hostPathRuleEngine.ShouldBlock(
        "https://path-filter.invalid/exact-ad?slot=1",
        "https://publisher.example/watch",
        AdBlockResourceType.Script));

var regexRuleEngine = new AdBlockEngine(initialRules:
[
    @"/^https:\/\/regex-filter\.invalid\/ads\/\d+\.js$/"
]);
Check(
    "interpreted regex adblock rules still match their target",
    regexRuleEngine.ShouldBlock(
        "https://regex-filter.invalid/ads/123.js",
        "https://publisher.example/watch",
        AdBlockResourceType.Script));
Check(
    "interpreted regex adblock rules keep their boundaries",
    !regexRuleEngine.ShouldBlock(
        "https://regex-filter.invalid/assets/123.css",
        "https://publisher.example/watch",
        AdBlockResourceType.Stylesheet));

var regexIndexSoundnessCases = new[]
{
    ("optional atoms keep a mandatory outside literal", "tracke?r", "tracker", true),
    ("lookaround contents are ignored while outside literals stay indexable", "(?=ads)tracker", "adstracker", true),
    ("alternation is conservatively left unindexed", "tracker|beacon", "tracker", false),
    ("simple character-class escapes break but do not poison later literals", @"\d+tracker", "42tracker", true),
    ("control escapes with payload are conservatively left unindexed", @"\cAfoo", "\u0001foo", false),
    ("Unicode category escapes with payload are conservatively left unindexed", @"\p{Lu}foo", "Afoo", false),
    ("named backreferences with payload are conservatively left unindexed", @"\k<name>bar", "barbar", false),
    ("numeric backreferences are conservatively left unindexed", @"(a)\1bar", "aabar", false),
    ("plus-quantified atoms do not join surrounding literals", "foo+bar", "foooobar", true),
    ("bounded repeated atoms do not join surrounding literals", "foo{2,}bar", "fooooobar", true),
    ("lazy repeated atoms remain separated", "foo+?bar", "foooobar", true)
};
foreach (var testCase in regexIndexSoundnessCases)
{
    var extracted = AdBlockEngine.TryExtractMandatoryRegexLiteralForTesting(
        testCase.Item2,
        out var mandatoryLiteral);
    Check(
        testCase.Item1,
        extracted == testCase.Item4
            && (!extracted
                || (mandatoryLiteral.Length >= 3
                    && !mandatoryLiteral.Equals("foobar", StringComparison.OrdinalIgnoreCase)
                    && testCase.Item3.Contains(mandatoryLiteral, StringComparison.OrdinalIgnoreCase))));
}

var representativeRegexEngine = new AdBlockEngine(initialRules:
[
    @"/^https:\/\/mewcdn\.online\/[a-f0-9]{32}\.js\?/$script",
    @"/^https:\/\/[a-z0-9-]{7,}\.[a-z]{3,6}\/assets\/script$/$script,3p,match-case,to=~edu|~gov",
    @"/^https:\/\/optional\.invalid\/track(?:er)?\.js$/$script",
    @"/^https:\/\/(?=ads\.)ads\.lookaround\.invalid\/pixel\d+$/$image",
    @"/^https:\/\/(?:ads|beacon)\.alternate\.invalid\/pixel$/$image"
]);
Check(
    "representative current hexadecimal regex rules keep blocking",
    representativeRegexEngine.ShouldBlock(
        "https://mewcdn.online/0123456789abcdef0123456789abcdef.js?",
        "https://publisher.example/watch",
        AdBlockResourceType.Script));
Check(
    "representative current scoped asset regex rules keep blocking",
    representativeRegexEngine.ShouldBlock(
        "https://tracker-host.online/assets/script",
        "https://publisher.example/watch",
        AdBlockResourceType.Script));
Check(
    "optional-group regex rules keep blocking",
    representativeRegexEngine.ShouldBlock(
        "https://optional.invalid/tracker.js",
        "https://publisher.example/watch",
        AdBlockResourceType.Script));
Check(
    "lookaround regex rules are supported by the bounded interpreter",
    representativeRegexEngine.ShouldBlock(
        "https://ads.lookaround.invalid/pixel42",
        "https://publisher.example/watch",
        AdBlockResourceType.Image));
Check(
    "unindexed alternation regex rules retain their authored matches",
    representativeRegexEngine.ShouldBlock(
        "https://beacon.alternate.invalid/pixel",
        "https://publisher.example/watch",
        AdBlockResourceType.Image));

var pathologicalRegexEngine = new AdBlockEngine(initialRules:
[
    @"/^https:\/\/timeout\.invalid\/(?:a|aa)+$/$script"
]);
var pathologicalRegexTimer = System.Diagnostics.Stopwatch.StartNew();
var pathologicalRegexBlocked = pathologicalRegexEngine.ShouldBlock(
    "https://timeout.invalid/" + new string('a', 3_800) + "!",
    "https://publisher.example/watch",
    AdBlockResourceType.Script);
pathologicalRegexTimer.Stop();
Check(
    "pathological interpreted regexes fail open within the aggregate safety budget",
    !pathologicalRegexBlocked && pathologicalRegexTimer.Elapsed < TimeSpan.FromSeconds(2));

var oversizedRegexEngine = new AdBlockEngine(initialRules:
[
    "/" + new string('a', 2_049) + "/"
]);
Check(
    "oversized regex rules are rejected by the compile cap",
    oversizedRegexEngine.LastCompileDiagnostics.RegexRules == 0
        && oversizedRegexEngine.LastCompileDiagnostics.DroppedRegexRules == 1);

var regexCapRules = Enumerable.Range(0, 520)
    .Select(index => $@"/^https:\/\/cap-{index:D3}\.invalid\/ad$/")
    .Append(@"@@/^https:\/\/cap-000\.invalid\/ad$/")
    .ToArray();
var regexCapEngine = new AdBlockEngine(initialRules: regexCapRules);
Check(
    "regex exception rules consume the bounded quota before blocking rules",
    regexCapEngine.LastCompileDiagnostics.RegexRules == 512
        && regexCapEngine.LastCompileDiagnostics.DroppedRegexRules == 9
        && !regexCapEngine.ShouldBlock(
            "https://cap-000.invalid/ad",
            "https://publisher.example/watch",
            AdBlockResourceType.Script));

var genericUnindexedRegexRules = Enumerable.Range(0, 70)
    .Select(index => @"/^[a-z]{3}\u" + (0x0100 + index).ToString("X4", CultureInfo.InvariantCulture) + "$/")
    .ToArray();
var genericUnindexedRegexEngine = new AdBlockEngine(initialRules: genericUnindexedRegexRules);
Check(
    "generic unindexed regex rules retain a separate bounded quota",
    genericUnindexedRegexEngine.LastCompileDiagnostics.RegexRules == 64
        && genericUnindexedRegexEngine.LastCompileDiagnostics.UnindexedGenericRegexRules == 64
        && genericUnindexedRegexEngine.LastCompileDiagnostics.DroppedRegexRules == 6);

var sourceScopedUnindexedRegexRules = Enumerable.Range(0, 270)
    .Select(index => @"/^[a-z]{3}\u"
        + (0x0200 + index).ToString("X4", CultureInfo.InvariantCulture)
        + $"$/$script,domain=site-{index}.invalid")
    .ToArray();
var sourceScopedUnindexedRegexEngine = new AdBlockEngine(initialRules: sourceScopedUnindexedRegexRules);
Check(
    "source-scoped unindexed regex rules use their own bounded quota",
    sourceScopedUnindexedRegexEngine.LastCompileDiagnostics.RegexRules == 256
        && sourceScopedUnindexedRegexEngine.LastCompileDiagnostics.UnindexedSourceScopedRegexRules == 256
        && sourceScopedUnindexedRegexEngine.LastCompileDiagnostics.DroppedRegexRules == 14);

var youtubeWatchUrl = "https://www.youtube.com/watch?v=video123";
var youtubeFallbackEngine = new AdBlockEngine(initialRules: []);
Check(
    "offline fallback blocks YouTube page-ad requests before lists load",
    youtubeFallbackEngine.ShouldBlock(
        "https://www.youtube.com/pagead/adview?ai=pre-roll",
        youtubeWatchUrl,
        AdBlockResourceType.XmlHttpRequest));
Check(
    "offline fallback blocks YouTube player ad-break requests before lists load",
    youtubeFallbackEngine.ShouldBlock(
        "https://www.youtube.com/youtubei/v1/player/ad_break?key=test",
        youtubeWatchUrl,
        AdBlockResourceType.Fetch));
Check(
    "offline fallback blocks YouTube mid-roll metadata before lists load",
    youtubeFallbackEngine.ShouldBlock(
        "https://www.youtube.com/get_midroll_info?video_id=video123",
        youtubeWatchUrl,
        AdBlockResourceType.XmlHttpRequest));
Check(
    "offline fallback blocks YouTube ad telemetry before lists load",
    youtubeFallbackEngine.ShouldBlock(
        "https://www.youtube.com/api/stats/ads?ver=2",
        youtubeWatchUrl,
        AdBlockResourceType.Fetch));
Check(
    "YouTube fallback allows the watch document",
    !youtubeFallbackEngine.ShouldBlock(
        youtubeWatchUrl,
        youtubeWatchUrl,
        AdBlockResourceType.Document));
Check(
    "YouTube fallback allows top-level watch navigation",
    !youtubeFallbackEngine.ShouldBlockNavigation(
        youtubeWatchUrl,
        "https://www.google.com/search?q=video"));
Check(
    "fallback host rules block embedded resources without strict-blocking typed destinations",
    youtubeFallbackEngine.ShouldBlock(
        "https://stake.com/promo/frame",
        "https://publisher.example/watch",
        AdBlockResourceType.SubDocument)
    && youtubeFallbackEngine.ShouldBlock(
        "https://stake.com/assets/app.js",
        "https://publisher.example/watch",
        AdBlockResourceType.Script)
    && !youtubeFallbackEngine.ShouldBlock(
        "https://stake.com/assets/app.js",
        "https://stake.com/",
        AdBlockResourceType.Script)
    && !youtubeFallbackEngine.ShouldBlock(
        "https://stake.com/",
        "https://publisher.example/watch",
        AdBlockResourceType.Document)
    && !youtubeFallbackEngine.ShouldBlockNavigation(
        "https://stake.com/",
        "https://publisher.example/watch")
    && !youtubeFallbackEngine.ShouldBlockNavigation(
        "https://casino.org/",
        "https://publisher.example/watch"));
Check(
    "YouTube fallback allows the normal player bootstrap request",
    !youtubeFallbackEngine.ShouldBlock(
        "https://www.youtube.com/youtubei/v1/player?key=test",
        youtubeWatchUrl,
        AdBlockResourceType.Fetch));
var functionalYouTubeRequests = new[]
{
    ("next Fetch", "https://www.youtube.com/youtubei/v1/next?prettyPrint=false", AdBlockResourceType.Fetch),
    ("next XHR", "https://www.youtube.com/youtubei/v1/next?prettyPrint=false", AdBlockResourceType.XmlHttpRequest),
    ("browse", "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false", AdBlockResourceType.Fetch),
    ("guide", "https://www.youtube.com/youtubei/v1/guide?prettyPrint=false", AdBlockResourceType.XmlHttpRequest),
    ("comments", "https://www.youtube.com/youtubei/v1/comment/get_comments?prettyPrint=false", AdBlockResourceType.Fetch),
    ("legacy comments", "https://www.youtube.com/comment_service_ajax?action_get_comments=1", AdBlockResourceType.XmlHttpRequest),
    ("player script", "https://www.youtube.com/s/player/current/player_ias.vflset/en_US/base.js", AdBlockResourceType.Script)
};
foreach (var (name, url, resourceType) in functionalYouTubeRequests)
{
    Check(
        $"YouTube fallback allows the functional {name} request",
        !youtubeFallbackEngine.ShouldBlock(url, youtubeWatchUrl, resourceType));
}
Check(
    "YouTube fallback allows ordinary video playback media",
    !youtubeFallbackEngine.ShouldBlock(
        "https://rr1---sn-npoe7n7z.googlevideo.com/videoplayback?id=content&mime=video%2Fmp4&range=0-1023",
        youtubeWatchUrl,
        AdBlockResourceType.Media));
Check(
    "YouTube fallback allows ordinary thumbnail images",
    !youtubeFallbackEngine.ShouldBlock(
        "https://i.ytimg.com/vi/video123/hqdefault.jpg",
        youtubeWatchUrl,
        AdBlockResourceType.Image));

var xhrRuleEngine = new AdBlockEngine(initialRules:
[
    "||xhr-filter.invalid/api^$xmlhttprequest"
]);
Check(
    "xmlhttprequest rules block XMLHttpRequest resources",
    xhrRuleEngine.ShouldBlock(
        "https://xhr-filter.invalid/api?slot=1",
        youtubeWatchUrl,
        AdBlockResourceType.XmlHttpRequest));
Check(
    "xmlhttprequest rules also block Fetch resources",
    xhrRuleEngine.ShouldBlock(
        "https://xhr-filter.invalid/api?slot=1",
        youtubeWatchUrl,
        AdBlockResourceType.Fetch));
Check(
    "xmlhttprequest rules do not block documents",
    !xhrRuleEngine.ShouldBlock(
        "https://xhr-filter.invalid/api?slot=1",
        youtubeWatchUrl,
        AdBlockResourceType.Document));
Check(
    "xmlhttprequest rules do not block media",
    !xhrRuleEngine.ShouldBlock(
        "https://xhr-filter.invalid/api?slot=1",
        youtubeWatchUrl,
        AdBlockResourceType.Media));

var xhrAliasRuleEngine = new AdBlockEngine(initialRules:
[
    "||xhr-alias.invalid/api^$xhr"
]);
Check(
    "xhr aliases retain their request-type constraint",
    xhrAliasRuleEngine.ShouldBlock(
        "https://xhr-alias.invalid/api?slot=1",
        youtubeWatchUrl,
        AdBlockResourceType.Fetch)
    && !xhrAliasRuleEngine.ShouldBlock(
        "https://xhr-alias.invalid/api?slot=1",
        youtubeWatchUrl,
        AdBlockResourceType.Script));

var modifierSafetyEngine = new AdBlockEngine(initialRules:
[
    "||disabled-rule.invalid^",
    "||disabled-rule.invalid^$badfilter",
    "||response-rewrite.invalid^$replace=/advert/content/",
    "||redirect-only.invalid^$redirect-rule=noopjs",
    "||important-rule.invalid^$script,important",
    "@@||important-rule.invalid^$script"
]);
Check(
    "badfilter rules disable their matching network rule",
    !modifierSafetyEngine.ShouldBlock(
        "https://disabled-rule.invalid/ad.js",
        youtubeWatchUrl,
        AdBlockResourceType.Script));
Check(
    "unsupported response rewrites are skipped instead of broadened",
    !modifierSafetyEngine.ShouldBlock(
        "https://response-rewrite.invalid/ad.js",
        youtubeWatchUrl,
        AdBlockResourceType.Script));
Check(
    "redirect-rule options do not become standalone blocks",
    !modifierSafetyEngine.ShouldBlock(
        "https://redirect-only.invalid/ad.js",
        youtubeWatchUrl,
        AdBlockResourceType.Script));
Check(
    "important blocking rules override ordinary exceptions",
    modifierSafetyEngine.ShouldBlock(
        "https://important-rule.invalid/ad.js",
        youtubeWatchUrl,
        AdBlockResourceType.Script));

var importantExceptionEngine = new AdBlockEngine(initialRules:
[
    "||important-exception.invalid^$script,important",
    "@@||important-exception.invalid/allowed.js$script,important"
]);
Check(
    "important exceptions still override important blocking rules",
    !importantExceptionEngine.ShouldBlock(
        "https://important-exception.invalid/allowed.js",
        youtubeWatchUrl,
        AdBlockResourceType.Script)
    && importantExceptionEngine.ShouldBlock(
        "https://important-exception.invalid/blocked.js",
        youtubeWatchUrl,
        AdBlockResourceType.Script));

var popunderModifierEngine = new AdBlockEngine(initialRules:
[
    "||popunder-filter.invalid^$popunder"
]);
Check(
    "popunder request modifiers participate in popup navigation blocking",
    popunderModifierEngine.ShouldBlockNavigation(
        "https://popunder-filter.invalid/landing",
        "https://publisher.example/watch"));

var domainRuleEngine = new AdBlockEngine(initialRules:
[
    "||domain-filter.invalid/ad.js$script,domain=youtube.com|~music.youtube.com"
]);
Check(
    "domain-scoped rules include matching YouTube pages",
    domainRuleEngine.ShouldBlock(
        "https://domain-filter.invalid/ad.js",
        youtubeWatchUrl,
        AdBlockResourceType.Script));
Check(
    "domain-scoped rules honor excluded YouTube subdomains",
    !domainRuleEngine.ShouldBlock(
        "https://domain-filter.invalid/ad.js",
        "https://music.youtube.com/watch?v=video123",
        AdBlockResourceType.Script));
Check(
    "domain-scoped rules do not leak onto unrelated pages",
    !domainRuleEngine.ShouldBlock(
        "https://domain-filter.invalid/ad.js",
        "https://publisher.example/watch",
        AdBlockResourceType.Script));

var expectedResourceTypeMappings = new (string? Context, AdBlockResourceType Expected)[]
{
    ("Document", AdBlockResourceType.Document),
    ("Script", AdBlockResourceType.Script),
    ("Image", AdBlockResourceType.Image),
    ("Favicon", AdBlockResourceType.Image),
    ("Stylesheet", AdBlockResourceType.Stylesheet),
    ("Media", AdBlockResourceType.Media),
    ("Font", AdBlockResourceType.Font),
    ("XmlHttpRequest", AdBlockResourceType.XmlHttpRequest),
    ("Fetch", AdBlockResourceType.Fetch),
    ("EventSource", AdBlockResourceType.XmlHttpRequest),
    ("TextTrack", AdBlockResourceType.Media),
    ("Websocket", AdBlockResourceType.WebSocket),
    ("Ping", AdBlockResourceType.Ping),
    ("CspViolationReport", AdBlockResourceType.Ping),
    ("Unknown", AdBlockResourceType.Other),
    (null, AdBlockResourceType.Other)
};
foreach (var (context, expected) in expectedResourceTypeMappings)
{
    Check(
        $"WebView2 {context ?? "null"} resources map to {expected}",
        AdBlockEngine.MapResourceType(context) == expected);
}
Check(
    "WebView2 iframe document requests map to subdocuments",
    AdBlockEngine.MapResourceType("Document", "iframe") == AdBlockResourceType.SubDocument);

var conditionalRuleEngine = new AdBlockEngine(initialRules:
[
    "!#if env_firefox",
    "||firefox-only.invalid^",
    "!#else",
    "||chromium-fallback.invalid^",
    "!#endif",
    "!#if env_chromium",
    "||chromium-only.invalid^",
    "!#endif"
]);
Check(
    "filter preprocessor excludes rules for other browser engines",
    !conditionalRuleEngine.ShouldBlock(
        "https://firefox-only.invalid/ad.js",
        youtubeWatchUrl,
        AdBlockResourceType.Script));
Check(
    "filter preprocessor keeps Chromium branches",
    conditionalRuleEngine.ShouldBlock(
        "https://chromium-only.invalid/ad.js",
        youtubeWatchUrl,
        AdBlockResourceType.Script));
Check(
    "filter preprocessor selects else branches",
    conditionalRuleEngine.ShouldBlock(
        "https://chromium-fallback.invalid/ad.js",
        youtubeWatchUrl,
        AdBlockResourceType.Script));

var sourceIsolationRoot = Path.Combine(
    Path.GetTempPath(),
    "MishaWeb-Filter-Source-Isolation-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(sourceIsolationRoot);
    File.WriteAllLines(Path.Combine(sourceIsolationRoot, "list-0.txt"),
    [
        "!#if env_firefox",
        "||suppressed-source.invalid^"
    ]);
    File.WriteAllLines(Path.Combine(sourceIsolationRoot, "list-1.txt"),
    [
        "||independent-source.invalid^"
    ]);
    var sourceIsolationEngine = new AdBlockEngine(
        sourceIsolationRoot,
        updateRemoteLists: false);
    await sourceIsolationEngine.LoadAsync();
    Check(
        "unterminated filter conditionals cannot suppress a later source",
        sourceIsolationEngine.ShouldBlock(
            "https://independent-source.invalid/ad.js",
            youtubeWatchUrl,
            AdBlockResourceType.Script));
}
finally
{
    try { Directory.Delete(sourceIsolationRoot, recursive: true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

var cosmeticRuleEngine = new AdBlockEngine(initialRules:
[
    "##.smoke-generic-ad",
    "youtube.com#@#.smoke-generic-ad",
    "youtube.com##.smoke-youtube-ad",
    "youtube.com##.smoke-excepted-ad",
    "youtube.com#@#.smoke-excepted-ad",
    "youtube.com##+js(set, smokeAdValue, undefined)"
]);
var youtubeCosmeticCss = cosmeticRuleEngine.GetCosmeticCss(youtubeWatchUrl);
var otherSiteCosmeticCss = cosmeticRuleEngine.GetCosmeticCss("https://publisher.example/watch");
Check(
    "standard cosmetic rules compile for their matching site",
    youtubeCosmeticCss.Contains(".smoke-youtube-ad", StringComparison.Ordinal));
Check(
    "site cosmetic exceptions remove matching specific and generic selectors",
    !youtubeCosmeticCss.Contains(".smoke-excepted-ad", StringComparison.Ordinal)
        && !youtubeCosmeticCss.Contains(".smoke-generic-ad", StringComparison.Ordinal));
Check(
    "generic cosmetic rules remain active on non-excepted sites",
    otherSiteCosmeticCss.Contains(".smoke-generic-ad", StringComparison.Ordinal));
Check(
    "site-specific cosmetic rules do not leak onto other sites",
    !otherSiteCosmeticCss.Contains(".smoke-youtube-ad", StringComparison.Ordinal));
Check(
    "remote scriptlet syntax is never emitted as cosmetic CSS",
    !youtubeCosmeticCss.Contains("+js", StringComparison.Ordinal));
Check(
    "googlevideo requests always bypass adblocking regardless of top-level or path",
    MainForm.ShouldBypassYouTubePlaybackRequest(
        topLevelUrl: "about:blank",
        sourceUrl: null,
        requestUrl: "https://rr10---sn-bavcx-hoald.googlevideo.com/videoplayback?expire=1789883506&ei=test",
        resourceType: AdBlockResourceType.Fetch)
    && MainForm.ShouldBypassYouTubePlaybackRequest(
        topLevelUrl: null,
        sourceUrl: "https://www.youtube.com/watch?v=0mNykxUtSGE",
        requestUrl: "https://rr1---sn-aigzrnld.googlevideo.com/initplayback?source=youtube",
        resourceType: AdBlockResourceType.Media)
    && MainForm.ShouldBypassYouTubePlaybackRequest(
        topLevelUrl: "https://example.com/embed",
        sourceUrl: null,
        requestUrl: "https://redirector.googlevideo.com/videometa",
        resourceType: AdBlockResourceType.XmlHttpRequest));
Check(
    "youtube player and watch endpoints bypass adblocking",
    MainForm.ShouldBypassYouTubePlaybackRequest(
        topLevelUrl: "https://www.youtube.com/watch?v=0mNykxUtSGE",
        sourceUrl: null,
        requestUrl: "https://www.youtube.com/youtubei/v1/player?key=test",
        resourceType: AdBlockResourceType.Fetch)
    && MainForm.ShouldBypassYouTubePlaybackRequest(
        topLevelUrl: "about:blank",
        sourceUrl: "https://www.youtube.com/watch?v=0mNykxUtSGE",
        requestUrl: "https://www.youtube.com/youtubei/v1/reel/reel_item_watch",
        resourceType: AdBlockResourceType.XmlHttpRequest)
    && !MainForm.ShouldBypassYouTubePlaybackRequest(
        topLevelUrl: "https://www.youtube.com/watch?v=0mNykxUtSGE",
        sourceUrl: null,
        requestUrl: "https://www.youtube.com/pagead/adview",
        resourceType: AdBlockResourceType.Fetch));

var cosmeticInjectionEngine = new AdBlockEngine(initialRules:
[
    "victim.example##@import url(https://attacker.invalid/x.css);/*",
    "victim.example##.safe-selector"
]);
var cosmeticInjectionCss = cosmeticInjectionEngine.GetCosmeticCss("https://victim.example/");
Check(
    "remote cosmetic lists cannot inject at-rules, URLs, comments, or declarations",
    cosmeticInjectionCss.Contains(".safe-selector", StringComparison.Ordinal)
        && !cosmeticInjectionCss.Contains("@import", StringComparison.OrdinalIgnoreCase)
        && !cosmeticInjectionCss.Contains("attacker.invalid", StringComparison.OrdinalIgnoreCase));

var cosmeticBudgetRules = Enumerable.Range(0, 12_100)
    .Select(index => $"##.smoke-generic-{index}")
    .Append("youtube.com##.smoke-site-priority");
var cosmeticBudgetEngine = new AdBlockEngine(initialRules: cosmeticBudgetRules);
Check(
    "site-specific cosmetics stay ahead of the generic CSS budget",
    cosmeticBudgetEngine.GetCosmeticCss(youtubeWatchUrl)
        .Contains(".smoke-site-priority", StringComparison.Ordinal));

var cosmeticAllowlistEngine = new AdBlockEngine(initialRules:
[
    "##.smoke-generic-hide",
    "accounts.example##.smoke-specific-hide",
    "@@||accounts.example^$generichide",
    "shop.example##.smoke-shop-specific",
    "@@||shop.example^$specifichide",
    "quiet.example##.smoke-quiet-specific",
    "@@||quiet.example^$elemhide"
]);
var accountsCosmeticCss = cosmeticAllowlistEngine.GetCosmeticCss("https://accounts.example/login");
var shopCosmeticCss = cosmeticAllowlistEngine.GetCosmeticCss("https://shop.example/checkout");
var quietCosmeticCss = cosmeticAllowlistEngine.GetCosmeticCss("https://quiet.example/");
Check(
    "generichide exceptions preserve specific cosmetics while suppressing generic ones",
    !accountsCosmeticCss.Contains(".smoke-generic-hide", StringComparison.Ordinal)
        && accountsCosmeticCss.Contains(".smoke-specific-hide", StringComparison.Ordinal));
Check(
    "specifichide exceptions preserve generic cosmetics while suppressing specific ones",
    shopCosmeticCss.Contains(".smoke-generic-hide", StringComparison.Ordinal)
        && !shopCosmeticCss.Contains(".smoke-shop-specific", StringComparison.Ordinal));
Check(
    "elemhide exceptions suppress both generic and specific cosmetics",
    !quietCosmeticCss.Contains(".smoke-generic-hide", StringComparison.Ordinal)
        && !quietCosmeticCss.Contains(".smoke-quiet-specific", StringComparison.Ordinal));

var pathCosmeticAllowlistEngine = new AdBlockEngine(initialRules:
[
    "##.smoke-path-generic",
    "@@||search.example/results?$generichide"
]);
Check(
    "path-scoped generichide applies only to its matching page",
    !pathCosmeticAllowlistEngine.GetCosmeticCss("https://search.example/results?q=adblock")
        .Contains(".smoke-path-generic", StringComparison.Ordinal)
        && pathCosmeticAllowlistEngine.GetCosmeticCss("https://search.example/home")
            .Contains(".smoke-path-generic", StringComparison.Ordinal));

var genericBlockAllowlistEngine = new AdBlockEngine(initialRules:
[
    "||generic-ad.invalid^",
    "||scoped-ad.invalid^$domain=trusted.example",
    "@@||trusted.example/checkout?$genericblock"
]);
Check(
    "path-scoped genericblock suppresses generic rules on its matching page",
    !genericBlockAllowlistEngine.ShouldBlock(
        "https://generic-ad.invalid/ad.js",
        "https://trusted.example/checkout?step=pay",
        AdBlockResourceType.Script));
Check(
    "path-scoped genericblock does not widen to the rest of the site",
    genericBlockAllowlistEngine.ShouldBlock(
        "https://generic-ad.invalid/ad.js",
        "https://trusted.example/home",
        AdBlockResourceType.Script));
Check(
    "genericblock exceptions retain site-scoped network rules",
    genericBlockAllowlistEngine.ShouldBlock(
        "https://scoped-ad.invalid/ad.js",
        "https://trusted.example/checkout?step=pay",
        AdBlockResourceType.Script));

var adBlockCacheFolder = Path.Combine(
    Path.GetTempPath(),
    "MishaWeb-AdBlock-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(adBlockCacheFolder);
var cachedRuleEngine = new AdBlockEngine(
    cacheFolder: adBlockCacheFolder,
    updateRemoteLists: false);
var cachedRuleConsumerHeld = false;
try
{
    var configuredFilterSourceCount = AdBlockEngine.ConfiguredFilterSourceCountForTesting;
    for (var filterIndex = 0; filterIndex < configuredFilterSourceCount; filterIndex++)
    {
        File.WriteAllText(
            Path.Combine(adBlockCacheFolder, $"list-{filterIndex}.txt"),
            "! smoke cache placeholder");
    }
    File.WriteAllText(
        Path.Combine(adBlockCacheFolder, "list-0.txt"),
        "||cached-filter.invalid^");
    File.WriteAllText(
        Path.Combine(adBlockCacheFolder, "rogue.txt"),
        "||rogue-filter.invalid^");
    File.WriteAllText(Path.Combine(adBlockCacheFolder, "list-1.txt"), string.Empty);
    var selectedCachedFiles = cachedRuleEngine.GetCachedFilterFilesForTesting();
    Check(
        "adblock cache loads only non-empty files owned by configured sources",
        selectedCachedFiles.Count == configuredFilterSourceCount - 1
            && !selectedCachedFiles.Contains("list-1.txt", StringComparer.OrdinalIgnoreCase)
            && !selectedCachedFiles.Contains("rogue.txt", StringComparer.OrdinalIgnoreCase));
    File.SetLastWriteTimeUtc(
        Path.Combine(adBlockCacheFolder, "list-7.txt"),
        DateTime.UtcNow - TimeSpan.FromHours(9));
    Check(
        "adblock refresh selects only stale or missing filter sources",
        cachedRuleEngine.GetStaleFilterFilesForTesting().SequenceEqual(["list-7.txt"]));
    cachedRuleEngine.AcquireConsumer();
    cachedRuleConsumerHeld = true;
    cachedRuleEngine.LoadAsync().GetAwaiter().GetResult();
    Check(
        "fresh cached adblock rules load for the first consumer",
        cachedRuleEngine.ShouldBlock(
            "https://cached-filter.invalid/ad.js",
            youtubeWatchUrl,
            AdBlockResourceType.Script));
    Check(
        "unowned cache files cannot inject filter rules",
        !cachedRuleEngine.ShouldBlock(
            "https://rogue-filter.invalid/ad.js",
            youtubeWatchUrl,
            AdBlockResourceType.Script));

    cachedRuleEngine.ReleaseConsumer();
    cachedRuleConsumerHeld = false;
    cachedRuleEngine.AcquireConsumer();
    cachedRuleConsumerHeld = true;
    cachedRuleEngine.LoadAsync().GetAwaiter().GetResult();
    Check(
        "cached adblock rules survive releasing and reacquiring the final consumer",
        cachedRuleEngine.ShouldBlock(
            "https://cached-filter.invalid/ad.js",
            youtubeWatchUrl,
            AdBlockResourceType.Script));
}
finally
{
    if (cachedRuleConsumerHeld) cachedRuleEngine.ReleaseConsumer();
    Directory.Delete(adBlockCacheFolder, recursive: true);
}

Check(
    "popup guard blocks list-matched cross-site ad targets",
    adBlockTestEngine.ShouldBlockPopup(
        "https://cdn.ads.example/landing",
        "https://miruro.bz/watch/episode"));
Check(
    "popup guard allows clean user-initiated cross-site flows",
    !adBlockTestEngine.ShouldBlockPopup(
        "https://partner.example/offer",
        "https://news.example/article"));
Check(
    "popup guard allows user-initiated about-blank flows for OAuth and payments",
    !adBlockTestEngine.ShouldBlockPopup(
        "about:blank",
        "https://news.example/article"));
Check(
    "navigation guard blocks the observed ad redirect host",
    adBlockTestEngine.ShouldBlockNavigation(
        "https://luugy.com/?zoneid=11199717",
        "https://miruro.bz/watch/episode"));
Check(
    "navigation guard recognizes subdomains of explicit redirect infrastructure",
    adBlockTestEngine.ShouldBlockNavigation(
        "https://track.luugy.com/?zoneid=11199717",
        "https://miruro.bz/watch/episode"));
Check(
    "explicit navigation can bypass redirect heuristics without bypassing authored document rules",
    !adBlockTestEngine.ShouldBlockNavigation(
        "https://luugy.com/?zoneid=11199717",
        "https://miruro.bz/watch/episode",
        applyRedirectHostHeuristics: false)
    && adBlockTestEngine.ShouldBlockNavigation(
        "https://cdn.ads.example/landing",
        "https://miruro.bz/watch/episode",
        applyRedirectHostHeuristics: false));

var localFile = Path.GetTempFileName();
try
{
    Check(
        "local file paths are opened as file URLs",
        BrowserPolicy.ResolveAddress(localFile).Url == new Uri(localFile).AbsoluteUri);
    var fileUrlWithState = new Uri(localFile).AbsoluteUri + "?mode=dark#section";
    Check(
        "local file URL normalization preserves query and fragment state",
        BrowserPolicy.TryNormalizeLocalFileUrl(fileUrlWithState, out var normalizedFileUrlWithState)
            && normalizedFileUrlWithState.EndsWith("?mode=dark#section", StringComparison.Ordinal));
}
finally
{
    File.Delete(localFile);
}
var tempFolder = Path.Combine(Path.GetTempPath(), "MishaWeb-Smoke-" + Guid.NewGuid());
Directory.CreateDirectory(tempFolder);
try
{
    var indexFile = Path.Combine(tempFolder, "index.html");
    File.WriteAllText(indexFile, "<html>ok</html>");
    var folderNavigation = BrowserPolicy.ResolveAddress(tempFolder);
    Check(
        "folder path resolves to index.html",
        folderNavigation.Error is null
            && folderNavigation.Url?.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase) == true);
}
finally
{
    Directory.Delete(tempFolder, true);
}

Check("HTTPS navigation is safe", BrowserPolicy.IsSafeTopLevelUrl("https://example.com/"));
Check(
    "blob navigation is reserved for one-shot popup bootstrap handling",
    !BrowserPolicy.IsSafeTopLevelUrl("blob:https://example.com/id")
        && BrowserPolicy.IsPopupBootstrapUrl("blob:https://example.com/id"));
Check(
    "malformed or identifier-free blob bootstrap URLs are rejected",
    !BrowserPolicy.IsPopupBootstrapUrl("blob:https://")
        && !BrowserPolicy.IsPopupBootstrapUrl("blob:https://example.com/")
        && !BrowserPolicy.IsPopupBootstrapUrl("blob:javascript:alert(1)"));
Check("file navigation is blocked", !BrowserPolicy.IsSafeTopLevelUrl("file:///C:/secret.txt"));
Check(
    "browser-internal schemes cannot masquerade as external application links",
    !MainForm.IsAllowedExternalNavigation("about:blank", userInitiated: true)
        && !MainForm.IsAllowedExternalNavigation("blob:https://example.com/id", userInitiated: true)
        && !MainForm.IsAllowedExternalNavigation("data:text/html,spoof", userInitiated: true)
        && !MainForm.IsAllowedExternalNavigation("filesystem:https://example.com/temporary/page", userInitiated: true)
        && !MainForm.IsAllowedExternalNavigation("view-source:https://example.com/", userInitiated: true)
        && !MainForm.IsAllowedExternalNavigation("chrome-extension://abcdefghijklmnopabcdefghijklmnop/page.html", userInitiated: true)
        && !MainForm.IsAllowedExternalNavigation("chrome-untrusted://new-tab-page/", userInitiated: true)
        && !MainForm.IsAllowedExternalNavigation("isolated-app://example/", userInitiated: true)
        && !MainForm.IsAllowedExternalNavigation("urn:uuid:12345678-1234-1234-1234-123456789abc", userInitiated: true)
        && !MainForm.IsAllowedExternalNavigation("devtools://devtools/bundled/inspector.html", userInitiated: true));
Check(
    "external application links require a user gesture and keep registered custom protocols",
    MainForm.IsAllowedExternalNavigation("mailto:user@example.com", userInitiated: true)
        && MainForm.IsAllowedExternalNavigation("tel:+15551234567", userInitiated: true)
        && MainForm.IsAllowedExternalNavigation("misha-helper:open/item", userInitiated: true)
        && !MainForm.IsAllowedExternalNavigation("misha-helper:open/item", userInitiated: false));
var safeHoverAddress = MainForm.FormatHoverStatusForDisplay(
    "https://user:secret@BÜCHER.example/path\u202Etxt?next=1");
var oversizedHoverAddress = MainForm.FormatHoverStatusForDisplay(
    "misha-helper:" + new string('x', BrowserPolicy.MaximumUrlLength));
Check(
    "hover targets cannot inject credentials, bidi controls, or oversized browser chrome text",
    safeHoverAddress.StartsWith("⚠ credentials hidden · https://xn--bcher-kva.example/", StringComparison.Ordinal)
        && !safeHoverAddress.Contains("user", StringComparison.Ordinal)
        && !safeHoverAddress.Contains("secret", StringComparison.Ordinal)
        && !safeHoverAddress.Contains('\u202E')
        && oversizedHoverAddress.Length <= 2_048
        && oversizedHoverAddress == "Address too long to display");
var clearedHoverStatus = MainForm.ClearHoverStatusSnapshot(
    "old page display",
    "old page raw",
    "old page pending",
    hasPending: true);
Check(
    "accepted navigation can atomically clear formatted and deferred hover state",
    clearedHoverStatus.HoverStatus.Length == 0
        && clearedHoverStatus.LastRaw.Length == 0
        && clearedHoverStatus.Pending is null
        && !clearedHoverStatus.HasPending);
var malformedIdnDisplay = TextSafety.FormatUrlForDisplay(
    "https://user:secret@\uD800.example/path",
    512);
var malformedIpv6Display = TextSafety.FormatUrlForDisplay(
    "https://user:secret@[:::1/path",
    512);
var malformedPortDisplay = TextSafety.FormatUrlForDisplay(
    "https://user:secret@example.com:999999/path",
    512);
var malformedHostDisplay = TextSafety.FormatHostForDisplay(
    "https://user:secret@[:::1/path",
    120);
var multiMegabyteDisplayInput = new string('x', 1_100_000);
var boundedSingleLine = TextSafety.SanitizeSingleLine(multiMegabyteDisplayInput, 64);
Check(
    "malformed URL displays contain parser failures, redact userinfo, and bound adversarial input work",
    new[] { malformedIdnDisplay, malformedIpv6Display, malformedPortDisplay }
        .All(value => value.Length <= 512
            && !value.Contains("user", StringComparison.Ordinal)
            && !value.Contains("secret", StringComparison.Ordinal))
        && malformedHostDisplay == "Invalid address"
        && boundedSingleLine.Length <= 64
        && boundedSingleLine.EndsWith('…')
        && TextSafety.FormatUrlForDisplay(multiMegabyteDisplayInput, 512)
            == "Address too long to display");
Check(
    "hover text changes relayout the toolbar only when status visibility changes",
    !MainForm.StatusTextVisibilityChanged("first link", "second link")
        && !MainForm.StatusTextVisibilityChanged(string.Empty, string.Empty)
        && MainForm.StatusTextVisibilityChanged(string.Empty, "link")
        && MainForm.StatusTextVisibilityChanged("link", string.Empty));
var safeBrowserChromeDetail = MainForm.FormatBrowserChromeUrlForDisplay(
    "https://user:secret@B\u00DCCHER.example/path\u202Etxt?next=1");
var oversizedBrowserChromeDetail = MainForm.FormatBrowserChromeUrlForDisplay(
    "https://example.com/" + new string('x', 700) + "\U0001F600");
Check(
    "tab, favorite, and history URL details hide credentials, canonicalize IDNs, and stay bounded",
    safeBrowserChromeDetail.StartsWith(
        "⚠ credentials hidden · https://xn--bcher-kva.example/",
        StringComparison.Ordinal)
        && !safeBrowserChromeDetail.Contains("user", StringComparison.Ordinal)
        && !safeBrowserChromeDetail.Contains("secret", StringComparison.Ordinal)
        && !safeBrowserChromeDetail.Contains('\u202E')
        && oversizedBrowserChromeDetail.Length <= 512
        && oversizedBrowserChromeDetail.StartsWith("https://example.com/", StringComparison.Ordinal)
        && HasOnlyPairedSurrogates(oversizedBrowserChromeDetail));
var rawSharedDisplayUrl = "https://user:secret@www.B\u00DCCHER.example/"
    + new string('x', 700)
    + "\u202Etxt";
var quickLinkDescription = NativeStartPage.FormatQuickLinkAccessibleDescription(
    new StartPageLink("IDN", rawSharedDisplayUrl, "Pinned"));
var quickLinkHost = NativeStartPage.FormatQuickLinkHostForDisplay(rawSharedDisplayUrl);
var savedDisplayRow = new SavedItemRow(
    "Saved IDN",
    rawSharedDisplayUrl,
    IsPinned: true,
    "Favorite");
var savedDisplayText = SavedItemsDialog.FormatRowForDisplay(savedDisplayRow);
var displaySuggestionState = new BrowserState
{
    Bookmarks = [new BookmarkEntry("Display spoof fixture", rawSharedDisplayUrl)]
};
var displaySuggestion = AddressSuggestionEngine.GetSuggestions(
        "Display spoof fixture",
        displaySuggestionState,
        2)
    .First(item => !item.IsSearch);
var blankTitleDisplaySuggestion = AddressSuggestionEngine.GetSuggestions(
        "secret",
        new BrowserState { Bookmarks = [new BookmarkEntry(string.Empty, rawSharedDisplayUrl)] },
        2)
    .First(item => !item.IsSearch);
Check(
    "shared URL display safety covers start-page accessibility, saved rows, and suggestions without changing targets",
    quickLinkDescription.Contains(
        "https://www.xn--bcher-kva.example/",
        StringComparison.Ordinal)
        && quickLinkDescription.Length <= 568
        && !quickLinkDescription.Contains("user", StringComparison.Ordinal)
        && !quickLinkDescription.Contains("secret", StringComparison.Ordinal)
        && !quickLinkDescription.Contains('\u202E')
        && quickLinkHost == "xn--bcher-kva.example"
        && savedDisplayText.Contains(
            "https://www.xn--bcher-kva.example/",
            StringComparison.Ordinal)
        && savedDisplayText.Length <= 960
        && !savedDisplayText.Contains("secret", StringComparison.Ordinal)
        && savedDisplayRow.Url == rawSharedDisplayUrl
        && displaySuggestion.Detail.StartsWith(
            "⚠ credentials hidden · https://www.xn--bcher-kva.example/",
            StringComparison.Ordinal)
        && displaySuggestion.Detail.Length <= 512
        && !displaySuggestion.Detail.Contains('\u202E')
        && displaySuggestion.AcceptText == rawSharedDisplayUrl
        && displaySuggestion.NavigationTarget == rawSharedDisplayUrl
        && blankTitleDisplaySuggestion.Title.StartsWith(
            "⚠ credentials hidden · https://www.xn--bcher-kva.example/",
            StringComparison.Ordinal)
        && !blankTitleDisplaySuggestion.Title.Contains("secret", StringComparison.Ordinal)
        && !blankTitleDisplaySuggestion.Title.Contains('\u202E')
        && blankTitleDisplaySuggestion.AcceptText == rawSharedDisplayUrl
        && blankTitleDisplaySuggestion.NavigationTarget == rawSharedDisplayUrl);
var sanitizedDownloadName = MainForm.SanitizeDownloadDisplayName(
    "photo\u202Egnp.exe");
var oversizedDownloadName = MainForm.SanitizeDownloadDisplayName(
    new string('n', 400) + "😀");
var sanitizedDownloadPath = MainForm.SanitizeDownloadDisplayPath(
    "C:\\Downloads\\report\u202Efdp.exe");
var oversizedDownloadPath = MainForm.SanitizeDownloadDisplayPath(
    "C:\\Downloads\\" + new string('p', 2_000));
Check(
    "download display names and paths strip bidi reversal and remain bounded",
    sanitizedDownloadName == "photo gnp.exe"
        && !sanitizedDownloadName.Contains('\u202E')
        && oversizedDownloadName.Length <= 255
        && !char.IsHighSurrogate(oversizedDownloadName[^1])
        && !char.IsLowSurrogate(oversizedDownloadName[^1])
        && sanitizedDownloadPath == "C:\\Downloads\\report fdp.exe"
        && !sanitizedDownloadPath.Contains('\u202E')
        && oversizedDownloadPath.Length <= 1_024);
var sanitizedExtensionDisplayName = MainForm.SanitizeExtensionDisplayName(
    "Password helper\u202Etxt.exe\r\nEnabled");
var oversizedExtensionDisplayName = MainForm.SanitizeExtensionDisplayName(
    new string('e', 300) + "😀");
Check(
    "installed extension names cannot inject bidi, controls, or oversized native list text",
    sanitizedExtensionDisplayName == "Password helper txt.exe  Enabled"
        && !sanitizedExtensionDisplayName.Contains('\u202E')
        && !sanitizedExtensionDisplayName.Contains('\r')
        && !sanitizedExtensionDisplayName.Contains('\n')
        && oversizedExtensionDisplayName.Length <= 120
        && !char.IsHighSurrogate(oversizedExtensionDisplayName[^1])
        && !char.IsLowSurrogate(oversizedExtensionDisplayName[^1]));
Check(
    "URL hosts compare case-insensitively",
    BrowserPolicy.UrlEquals("https://EXAMPLE.com/Path", "https://example.COM/Path"));
Check(
    "URL paths remain case-sensitive",
    !BrowserPolicy.UrlEquals("https://example.com/Path", "https://example.com/path"));
var backdropFrameCache = VerifyBackdropFrameCacheLifecycle();
Check("visible start page shares one 24-bit backdrop frame", backdropFrameCache.Active24Bit);
Check("hidden start page releases the shared backdrop frame", backdropFrameCache.Released);
Check("offscreen backdrop rendering remains uncached", OffscreenBackdropRenderingStaysUncached());
var bunnyLogo = VerifyBunnyLogoAssetsAndRendering();
Check("generated bunny logo master is a bounded 512px PNG", bunnyLogo.Master);
Check("runtime bunny logo is an optimized 256px PNG", bunnyLogo.Runtime);
Check("runtime bunny logo is embedded under the stable brand resource name", bunnyLogo.Embedded);
Check("runtime bunny logo is lazily decoded once and shared process-wide", bunnyLogo.SingleDecode);
Check("bunny logo remains legible at 16, 24, 32, and 64 pixels", bunnyLogo.SmallRender);
Check("start-page badge renders the embedded bunny logo without errors", bunnyLogo.BadgeRender);
Check("multi-size bunny application icon contains the required Windows sizes", bunnyLogo.Icon);
Check("project wiring uses the v2 bunny PNG and ICO assets", bunnyLogo.Wiring);
var backdropAfterLogoProbe = StartPageArtwork.GetBackdropFrameStateForTesting();
Check(
    "start-page logo probe releases its backdrop lease",
    backdropAfterLogoProbe.Leases == 0 && !backdropAfterLogoProbe.Allocated);
Check("native start page constructs and lays out", CanConstructNativeStartPage());
Check("native start page adapts layout and indicators for incognito mode", CanConfigureNativeStartPageForIncognitoMode());
var backdropAfterConstructionProbe = StartPageArtwork.GetBackdropFrameStateForTesting();
Check(
    $"standalone start-page disposal releases its backdrop lease ({backdropAfterConstructionProbe})",
    backdropAfterConstructionProbe.Leases == 0 && !backdropAfterConstructionProbe.Allocated);
Check("bunny night-garden backdrop is embedded, valid, and size-bounded", HasEmbeddedStartPageBackdrop());
var responsiveStartPage = VerifyNativeStartPageResponsiveLayout();
Check(
    $"start page stays contained through DPI-aware shrink-expand layouts ({responsiveStartPage.ObservedDpis})",
    responsiveStartPage.Valid);
Check("start page retains its complete logo-title-search-status-links hierarchy", responsiveStartPage.Hierarchy);
var smartSearchLayout = VerifySmartSearchLayoutRegression();
Check(
    $"smart search stays separated across wide-compact-wide and empty-active transitions at {smartSearchLayout.Dpi} DPI",
    smartSearchLayout.Valid);
var startPageSearchRouting = VerifyStartPageSearchRoutesToOmnibox();
Check("new tabs focus the top address bar instead of the middle search field", startPageSearchRouting.Focus);
Check("text entered through the middle search field transfers to the top bar", startPageSearchRouting.TextTransfer);
Check("smart search disabled fills are flattened to opaque colors", SmartSearchDisabledPaintIsOpaque());
Check("smart search resize surfaces paint only opaque pixels", SmartSearchPaintSurfacesAreOpaque());
Check("start-page interactive cards paint opaque resize-stable surfaces", StartPageInteractiveSurfacesAreOpaque());
Check("start page outer composition leaves the night-garden artwork visible", StartPageOuterCardIsTransparent());
Check("smart search icon has no decorative artwork underneath it", SmartSearchIconHasNoArtworkOverlay());
Check("new-tab-only browser creates no WebView or browser environment", CanPrepareRendererFreeBrowser());
var tabDragRegression = VerifyTabDragReordering();
Check("dragging a tab header changes the visible tab order", tabDragRegression.Reordered);
Check("tab headers stay interactive while unused title-bar space moves the window", tabDragRegression.HitTesting);
Check("tab drag shows a ghost preview only while movement is active", tabDragRegression.GhostFeedback);
var tabAudioIndicator = VerifyTabAudioIndicator();
Check("audible tabs render a speaker indicator beside the title", tabAudioIndicator.AudibleGlyph);
Check("muted tabs render a distinct slashed-speaker indicator", tabAudioIndicator.MutedGlyph);
Check("browser audio-state changes refresh the matching tab header", tabAudioIndicator.RefreshWired);
Check("blank title-bar surfaces start the native window move gesture", WindowCaptionDragUsesNativeMove());
var reservedWindowDragSpace = VerifyReservedWindowDragSpace();
Check(
    $"the title bar retains a dedicated window-drag area with crowded tabs ({reservedWindowDragSpace.Width}px)",
    reservedWindowDragSpace.Valid);
Check(
    "crowded tab headers remain reachable through bounded horizontal overflow",
    reservedWindowDragSpace.Scrollable);
var savedWindowPlacement = VerifySavedWindowPlacement();
Check("a valid right-side window position and size restore exactly", savedWindowPlacement.RightSideRestored);
Check("a saved window on a disconnected monitor is moved fully onscreen", savedWindowPlacement.OffscreenRecovered);
Check("maximized and F11 fullscreen window states restore", savedWindowPlacement.PresentationRestored);
Check("resource modes remain nested under one three-choice submenu", ResourceModeMenuRemainsNested());
var normalChromePalette = MainForm.ResolveChromeColorPolicyForTesting(highContrast: false);
Check(
    "normal chrome retains a pink berry identity",
    IsRoseHue(NativeUiTheme.Accent)
    && IsRoseHue(normalChromePalette.Focus)
    && normalChromePalette.Frame == NativeUiTheme.BrandWine
    && normalChromePalette.Selection == NativeUiTheme.Selection
    && NativeUiTheme.Accent != Color.FromArgb(101, 211, 246)
    && normalChromePalette.Focus != Color.FromArgb(127, 219, 252));
Check(
    "pink palette primary text contrast meets WCAG AA",
    ContrastRatio(NativeUiTheme.Text, NativeUiTheme.Window) >= 4.5);
Check(
    "pink palette muted text contrast meets WCAG AA",
    ContrastRatio(NativeUiTheme.Muted, NativeUiTheme.Surface) >= 4.5);
Check(
    "pink action label contrast meets WCAG AA",
    ContrastRatio(NativeUiTheme.AccentText, NativeUiTheme.Accent) >= 4.5);
Check(
    "pink focus indicator contrast meets non-text guidance",
    ContrastRatio(NativeUiTheme.Focus, NativeUiTheme.Field) >= 3.0);
Check(
    "pink control border contrast meets non-text guidance",
    ContrastRatio(NativeUiTheme.Border, NativeUiTheme.Surface) >= 3.0);
Check(
    "pink accent is distinct from browser chrome",
    ContrastRatio(NativeUiTheme.Accent, NativeUiTheme.Chrome) >= 3.0);
var loadedToolbarRegression = VerifyLoadedToolbarRegression();
Check("loaded toolbar keeps controls separated from 480 to 1917 logical pixels", loadedToolbarRegression.Layout);
Check("vector toolbar buttons suppress native text in every state", loadedToolbarRegression.Text);
Check("vector toolbar repainting is pixel-idempotent", loadedToolbarRegression.Paint);
Check("repeated address-bar clicks preserve caret editing", loadedToolbarRegression.AddressBarPreservesCaret);
var addressBarClickSelection = VerifyAddressBarFirstClickSelection();
Check("the first address-bar click selects the complete URL", addressBarClickSelection.FirstClick);
Check("a later address-bar click still permits precise caret placement", addressBarClickSelection.LaterClick);
Check("main chrome applies a renderable system high-contrast palette", CanRenderHighContrastChrome());
var chromeThemeRoundTrip = VerifyChromeThemeRoundTrip();
Check("dark pink chrome restores exactly after a high-contrast round trip", chromeThemeRoundTrip.Restored);
Check("dark, light-loading, and high-contrast surfaces remain visually distinct", chromeThemeRoundTrip.Distinct);
Check("normal and high-contrast chrome both render without blank surfaces", chromeThemeRoundTrip.Rendered);
Check("front-end source and UI strings contain no mojibake markers", FrontendSourcesAreEncodingClean());
Check("capture filenames remove Windows-reserved characters", MainForm.MakeSafeFileName("Misha: page?*") == "Misha- page");
var unicodeCaptureName = MainForm.MakeSafeFileName(new string('x', 63) + "😀");
Check(
    "capture filenames never split a Unicode surrogate pair",
    unicodeCaptureName.Length <= 64 && HasOnlyPairedSurrogates(unicodeCaptureName));
Check("suggestion popup keeps only visible keyboard rows", CanLimitSuggestionPopupRows());
var productAssembly = typeof(BrowserState).Assembly;
var informationalVersion = System.Reflection.CustomAttributeExtensions
    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(productAssembly)
    ?.InformationalVersion;
Check(
    "release assemblies carry the 2.2.0 product version",
    productAssembly.GetName().Version == new Version(2, 2, 0, 0)
        && informationalVersion == "2.2.0");
Check(
    "new sessions default to standard memory saver",
    new BrowserState().MemorySaverEnabled && new BrowserState().UltraLightModeEnabled == false);
var legacyResourceState = new BrowserState
{
    MemorySaverEnabled = false,
    UltraLightModeEnabled = true,
    ResourceModeDefaultsVersion = null
};
MainForm.ApplyResourceModeDefaults(legacyResourceState);
Check(
    "legacy profiles migrate once to standard memory saver",
    legacyResourceState.MemorySaverEnabled
        && legacyResourceState.UltraLightModeEnabled == false
        && legacyResourceState.ResourceModeDefaultsVersion == 1);
var manualResourceState = new BrowserState
{
    MemorySaverEnabled = false,
    UltraLightModeEnabled = false,
    ResourceModeDefaultsVersion = 1
};
MainForm.ApplyResourceModeDefaults(manualResourceState);
Check(
    "manual resource-mode choices survive after migration",
    !manualResourceState.MemorySaverEnabled && manualResourceState.UltraLightModeEnabled == false);
Check("start page stores and displays at most three quick links", BrowserStateStore.MaximumPinnedStartPageLinks == 3);
Check("new sessions preserve normal website motion", !new BrowserState().ReduceWebsiteMotionEnabled);
var websiteThemePolicyType = productAssembly.GetType("MishaWeb.WebsiteThemePolicy");
var preferredSchemeMethod = websiteThemePolicyType?.GetMethod(
    "GetPreferredColorScheme",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
var autoDarkParametersMethod = websiteThemePolicyType?.GetMethod(
    "GetAutoDarkModeParameters",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
var loadingBackgroundMethod = websiteThemePolicyType?.GetMethod(
    "GetLoadingBackgroundColor",
    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
var autoDarkCommand = websiteThemePolicyType?.GetField(
        "AutoDarkModeCommand",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
    ?.GetRawConstantValue() as string;
var enableAutoDarkParameters = autoDarkParametersMethod?.Invoke(null, [true]) as string;
var clearAutoDarkParameters = autoDarkParametersMethod?.Invoke(null, [false]) as string;
Check(
    "website theme policy maps the saved setting to explicit profile schemes",
    preferredSchemeMethod?.Invoke(null, [true])?.ToString() == "Dark"
        && preferredSchemeMethod.Invoke(null, [false])?.ToString() == "Light");
Check(
    "website theme policy uses Chromium's reversible auto-dark command",
    autoDarkCommand == "Emulation.setAutoDarkModeOverride"
        && IsAutoDarkParameterObject(enableAutoDarkParameters, expectedEnabled: true)
        && IsClearAutoDarkParameterObject(clearAutoDarkParameters));
Check(
    "website theme loading surfaces switch cleanly between dark and light",
    loadingBackgroundMethod?.Invoke(null, [true]) is Color darkLoadingColor
        && loadingBackgroundMethod.Invoke(null, [false]) is Color lightLoadingColor
        && darkLoadingColor == NativeUiTheme.Window
        && lightLoadingColor == Color.White);
var mainFormThemeSource = ReadRepositorySource("desktop", "MainForm.cs");
var nativeStartPageSecuritySource = ReadRepositorySource("desktop", "NativeStartPage.cs");
var nativeFeatureSurfacesSource = ReadRepositorySource("desktop", "NativeFeatureSurfaces.cs");
var savedItemsSecuritySource = ReadRepositorySource("desktop", "SavedItemsDialog.cs");
var addressSuggestionSecuritySource = ReadRepositorySource("desktop", "AddressSuggestionEngine.cs");
var extensionUpdateTransactionSource = ExtractSourceSection(
    mainFormThemeSource,
    "private async Task<string> UpdateChromeStoreExtensionAsync(",
    "internal static string? GetExtensionUpdateEligibilityError(");
var extensionInstallTransactionSource = ExtractSourceSection(
    mainFormThemeSource,
    "private async Task InstallPreparedExtensionAsync(",
    "private bool ConfirmPreparedExtensionUpdate(");
var extensionMutationReleaseSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void EndExtensionMutation()",
    "private sealed class ExtensionMutationScope");
var updateInstallBoundary = extensionUpdateTransactionSource.IndexOf(
    "MarkInstallationStarted(prepared)",
    StringComparison.Ordinal);
var freshInstallBoundary = extensionInstallTransactionSource.IndexOf(
    "MarkInstallationStarted(prepared)",
    StringComparison.Ordinal);
Check(
    "extension activation enters durable installing state before WebView Add and cannot be canceled mid-commit",
    updateInstallBoundary >= 0
        && updateInstallBoundary < extensionUpdateTransactionSource.IndexOf(
            "profile.AddBrowserExtensionAsync(prepared.FolderPath)",
            StringComparison.Ordinal)
        && !extensionUpdateTransactionSource[updateInstallBoundary..].Contains(
            "ThrowIfCancellationRequested",
            StringComparison.Ordinal)
        && freshInstallBoundary >= 0
        && freshInstallBoundary < extensionInstallTransactionSource.IndexOf(
            "profile.AddBrowserExtensionAsync(prepared.FolderPath)",
            StringComparison.Ordinal)
        && !extensionInstallTransactionSource[freshInstallBoundary..].Contains(
            "ThrowIfCancellationRequested",
            StringComparison.Ordinal));
Check(
    "the final extension mutation release schedules a previously requested deferred close",
    extensionMutationReleaseSource.Contains("QueueDeferredCloseAfterExtensionMutation()", StringComparison.Ordinal)
        && extensionMutationReleaseSource.Contains("remaining == 0", StringComparison.Ordinal));
var themeApplySource = ExtractSourceSection(
    mainFormThemeSource,
    "private async Task ApplyWebsiteThemeAsync",
    "private void ToggleAdBlocker");
var activateTabSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void ActivateTab(",
    "private async Task RestoreDiscardedTabAsync");
var resumeTabSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void ResumeTab",
    "private async Task HandleWindowStateChangeAsync");
var windowStateSource = ExtractSourceSection(
    mainFormThemeSource,
    "private async Task HandleWindowStateChangeAsync",
    "private void SetResourceMode");
var suspendTabSource = ExtractSourceSection(
    mainFormThemeSource,
    "private async Task<bool> TrySuspendTabAsync",
    "private bool IsTabAudible");
var failedSuspendDiscardSource = ExtractSourceSection(
    mainFormThemeSource,
    "private bool TryDiscardTabAfterFailedSuspend",
    "private async Task SleepInactiveTabsAsync");
var motionPolicySource = ExtractSourceSection(
    mainFormThemeSource,
    "private async Task ApplyMotionPolicyAsync",
    "private void OnSystemPreferenceChanged");
var readerModeSource = ExtractSourceSection(
    mainFormThemeSource,
    "private async Task ToggleReaderModeAsync",
    "internal static bool IsReaderModeContinuationEligible");
var runUiTaskSource = ExtractSourceSection(
    mainFormThemeSource,
    "private async void RunUiTask",
    "private static string FriendlyNavigationError");
var refreshMemorySweepSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void RefreshMemorySweepTimer",
    "private async Task<bool> RestoreOpenTabsFromStateAsync");
var resourceModeSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void SetResourceMode",
    "private void ShowResourceModeMenu");
var toggleUltraLightSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void ToggleUltraLightMode",
    "private void CycleResourceMode");
var browserTabDisposeSource = ExtractSourceSection(
    mainFormThemeSource,
    "public void Dispose()",
    "private sealed class TabDragGhost");
var popupDeferralSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void OnNewWindowRequested(",
    "private async Task OnNewWindowRequestedAsync");
var popupSetupSource = ExtractSourceSection(
    mainFormThemeSource,
    "private async Task OnNewWindowRequestedAsync",
    "private void OnProcessFailed(");
var processFailedSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void OnProcessFailed(",
    "private void FailActiveDownloadsAfterBrowserProcessExit");
var permissionRequestSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void OnPermissionRequested(",
    "private void ShowNextPermissionRequest");
var externalSchemeSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void OnLaunchingExternalUriScheme(",
    "private async Task RecreateTabAsync");
var downloadStartingSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void OnDownloadStarting(",
    "private void OnLaunchingExternalUriScheme(");
var downloadOperationIndex = downloadStartingSource.IndexOf(
    "var operation = e.DownloadOperation;",
    StringComparison.Ordinal);
var normalDownloadHandledIndex = downloadOperationIndex >= 0
    ? downloadStartingSource.LastIndexOf(
        "e.Handled = true;",
        downloadOperationIndex,
        StringComparison.Ordinal)
    : -1;
var normalDownloadBranchStart = downloadOperationIndex >= 0
    ? downloadStartingSource.LastIndexOf("return;", downloadOperationIndex, StringComparison.Ordinal)
    : -1;
var contextMenuSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void OnContextMenuRequested(",
    "private void CopyContextLink");
var configureWebViewSource = ExtractSourceSection(
    mainFormThemeSource,
    "private async Task ConfigureWebView(",
    "private async Task InstallAdBlockFilteringAsync");
var frameSetupSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void OnFrameCreated(",
    "private async Task ApplyAdBlockCosmeticsAsync(");
var updateStatusSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void UpdateStatus()",
    "private void ShowTransientStatus");
var navigationStartingSecuritySource = ExtractSourceSection(
    mainFormThemeSource,
    "private void OnNavigationStarting(",
    "private void OnWebResourceRequested");
var webResourceRequestedSecuritySource = ExtractSourceSection(
    mainFormThemeSource,
    "private void OnWebResourceRequested(",
    "private string GetAdBlockSourceUrl");
var replaceTabViewSecuritySource = ExtractSourceSection(
    mainFormThemeSource,
    "private void ReplaceTabView(",
    "private void ReleaseBrowserEnvironmentIfIdle");
var closeTabSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void CloseTab(",
    "private async Task RestoreClosedTabAsync");
var mainFormDisposeSource = ExtractSourceSection(
    mainFormThemeSource,
    "protected override void Dispose(bool disposing)",
    "private async void RunUiTask");
var popupCatchIndex = popupDeferralSource.IndexOf("catch (Exception error)", StringComparison.Ordinal);
var permissionCatchIndex = permissionRequestSource.IndexOf("catch (Exception error)", StringComparison.Ordinal);
Check(
    "popup deferral failures are explicitly handled instead of falling through to WebView defaults",
    popupCatchIndex >= 0
        && popupDeferralSource.IndexOf("e.Handled = true;", popupCatchIndex, StringComparison.Ordinal)
            > popupCatchIndex);
Check(
    "popup setup failures detach and close partially assigned WebView windows",
    popupSetupSource.Contains("if (popupAttached)", StringComparison.Ordinal)
        && popupSetupSource.Contains("e.NewWindow = null;", StringComparison.Ordinal)
        && popupSetupSource.Contains(
            "CloseTab(popup, remember: false, ensureReplacement: false);",
            StringComparison.Ordinal));
Check(
    "a crashed fullscreen page restores native browser chrome",
    processFailedSource.Contains("if (isDomFullScreen", StringComparison.Ordinal)
        && processFailedSource.IndexOf("SetDomFullScreen(false);", StringComparison.Ordinal)
            < processFailedSource.IndexOf("switch (failedKind)", StringComparison.Ordinal));
Check(
    "browser-process recovery is posted after the WebView2 callback unwinds",
    configureWebViewSource.Contains("failedKind = e.ProcessFailedKind;", StringComparison.Ordinal)
        && configureWebViewSource.Contains("BeginInvoke(new Action(() =>", StringComparison.Ordinal)
        && configureWebViewSource.Contains(
            "OnProcessFailed(tab, failedKind);",
            StringComparison.Ordinal)
        && processFailedSource.Contains("switch (failedKind)", StringComparison.Ordinal));
Check(
    "permission deferral failures remain handled and denied",
    permissionCatchIndex >= 0
        && permissionRequestSource.IndexOf("args.Handled = true;", permissionCatchIndex, StringComparison.Ordinal)
            > permissionCatchIndex
        && permissionRequestSource.IndexOf("CoreWebView2PermissionState.Deny", permissionCatchIndex, StringComparison.Ordinal)
            > permissionCatchIndex);
Check(
    "external-app callbacks default-deny and contain apartment disconnects across user prompts",
    externalSchemeSource.IndexOf("e.Cancel = true;", StringComparison.Ordinal)
            < externalSchemeSource.IndexOf("isUserInitiated = e.IsUserInitiated;", StringComparison.Ordinal)
        && externalSchemeSource.Contains("requestedUri = e.Uri;", StringComparison.Ordinal)
        && externalSchemeSource.Contains("initiatingOrigin = e.InitiatingOrigin;", StringComparison.Ordinal)
        && externalSchemeSource.Contains("e.Cancel = !allowed;", StringComparison.Ordinal)
        && externalSchemeSource.Contains("tab.DocumentNavigationGeneration != documentGeneration", StringComparison.Ordinal));
Check(
    "native downloads suppress WebView2's duplicate default download dialog",
    normalDownloadHandledIndex >= 0
        && normalDownloadHandledIndex > normalDownloadBranchStart);
Check(
    "context-menu event arguments are dereferenced only inside callback containment",
    contextMenuSource.IndexOf("try", StringComparison.Ordinal)
            < contextMenuSource.IndexOf("args.ContextMenuTarget", StringComparison.Ordinal)
        && contextMenuSource.Contains("tab.ContextLinkTarget = null;", StringComparison.Ordinal));
Check(
    "status-link callbacks contain browser-process teardown and defer bounded display formatting",
    configureWebViewSource.Contains("core.StatusBarText ?? string.Empty", StringComparison.Ordinal)
        && configureWebViewSource.Contains("MaximumPendingHoverStatusCharacters", StringComparison.Ordinal)
        && configureWebViewSource.Contains("tab.PendingHoverStatus = pendingStatus;", StringComparison.Ordinal)
        && !configureWebViewSource.Contains("FormatHoverStatusForDisplay(core.StatusBarText)", StringComparison.Ordinal)
        && updateStatusSource.Contains("FormatHoverStatusForDisplay(pendingStatus)", StringComparison.Ordinal)
        && configureWebViewSource.Contains("error is InvalidOperationException or COMException", StringComparison.Ordinal)
        && configureWebViewSource.Contains("tab.HoverStatus = string.Empty;", StringComparison.Ordinal));
Check(
    "hover updates use stable status geometry without repeated text measurement or relayout",
    !mainFormThemeSource.Contains("statusLabel.GetPreferredSize", StringComparison.Ordinal)
        && mainFormThemeSource.Contains(
            "var preferredStatusWidth = ScaleToolbarLogical(MaximumToolbarStatusWidth);",
            StringComparison.Ordinal)
        && updateStatusSource.Contains(
            "if (statusVisibilityChanged) UpdateResponsiveToolbar();",
            StringComparison.Ordinal));
Check(
    "accepted navigation, core replacement, and tab disposal clear every hover cache layer",
    navigationStartingSecuritySource.Contains("ClearTabHoverStatus(tab);", StringComparison.Ordinal)
        && replaceTabViewSecuritySource.Contains("ClearTabHoverStatus(tab);", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("ClearTabHoverStatus(this);", StringComparison.Ordinal));
Check(
    "native extension rows and download path tooltips use display-only sanitized text",
    mainFormThemeSource.Contains("SanitizeExtensionDisplayName(extension.Name)", StringComparison.Ordinal)
        && mainFormThemeSource.Contains(
            "ToolTipText = SanitizeDownloadDisplayPath(download.FilePath)",
            StringComparison.Ordinal));
Check(
    "tab, favorite, recently closed, and history browser-chrome URL displays use the safe formatter",
    mainFormThemeSource.Contains(
        "var displayUrl = FormatBrowserChromeUrlForDisplay(tab.Url);",
        StringComparison.Ordinal)
        && mainFormThemeSource.Contains(
            "FormatBrowserChromeUrlForDisplay(bookmark.Url)",
            StringComparison.Ordinal)
        && mainFormThemeSource.Contains(
            "FormatBrowserChromeUrlForDisplay(closed.Url)",
            StringComparison.Ordinal)
        && mainFormThemeSource.Contains(
            "FormatBrowserChromeUrlForDisplay(history.Url)",
            StringComparison.Ordinal)
        && mainFormThemeSource.Contains(
            "ToolTipText = FormatBrowserChromeUrlForDisplay(bookmark.Url)",
            StringComparison.Ordinal)
        && mainFormThemeSource.Contains(
            "ToolTipText = FormatBrowserChromeUrlForDisplay(history.Url)",
            StringComparison.Ordinal)
        && !mainFormThemeSource.Contains("ToolTipText = bookmark.Url", StringComparison.Ordinal)
        && !mainFormThemeSource.Contains("ToolTipText = history.Url", StringComparison.Ordinal));
Check(
    "start-page, saved-items, and suggestion display surfaces use the shared canonical URL formatter",
    nativeStartPageSecuritySource.Contains(
        "AccessibleDescription = FormatQuickLinkAccessibleDescription(link)",
        StringComparison.Ordinal)
        && nativeStartPageSecuritySource.Contains(
            "TextSafety.FormatHostForDisplay(url, MaximumQuickLinkDisplayHostCharacters)",
            StringComparison.Ordinal)
        && savedItemsSecuritySource.Contains(
            "itemList.Items.Add(FormatRowForDisplay(item));",
            StringComparison.Ordinal)
        && savedItemsSecuritySource.Contains(
            "TextSafety.FormatUrlForDisplay(item.Url, MaximumDisplayUrlCharacters)",
            StringComparison.Ordinal)
        && addressSuggestionSecuritySource.Contains(
            "TextSafety.FormatUrlForDisplay(candidate.Url, MaximumUrlCharactersToDisplay)",
            StringComparison.Ordinal)
        && addressSuggestionSecuritySource.Contains(
            "TextSafety.FormatUrlForDisplay(url, MaximumTitleCharactersToMatch)",
            StringComparison.Ordinal));
Check(
    "website theme toggle updates awake cores and defers sleeping tabs until resume",
    mainFormThemeSource.Contains("Interlocked.Increment(ref websiteThemeGeneration)", StringComparison.Ordinal)
        && mainFormThemeSource.Contains(
            "&& !item.IsSuspended",
            StringComparison.Ordinal)
        && activateTabSource.Contains("ResumeTab(tab);", StringComparison.Ordinal)
        && !activateTabSource.Contains("tab.Core.Resume();", StringComparison.Ordinal)
        && resumeTabSource.Contains("ApplyWebsiteThemeAsync(tab)", StringComparison.Ordinal));
Check(
    "minimizing releases and restoring reacquires the native start-page frame",
    windowStateSource.Contains("minimizingTab.StartPageView.Visible = false;", StringComparison.Ordinal)
        && windowStateSource.Contains("ShowNativeStartPage(restoringTab);", StringComparison.Ordinal));
Check(
    "background suspension ignores WebViews disposed during an asynchronous sleep request",
    suspendTabSource.Contains("tab.View.IsDisposed", StringComparison.Ordinal)
        && suspendTabSource.Contains("suspendingView.IsDisposed", StringComparison.Ordinal)
        && suspendTabSource.Contains("suspendingView.Disposing", StringComparison.Ordinal)
        && suspendTabSource.Contains("catch (ObjectDisposedException)", StringComparison.Ordinal));
Check(
    "protected tabs stop repeated failed suspension work and fall back to a low target",
    suspendTabSource.Contains("SuspendFailureFallbackThreshold", StringComparison.Ordinal)
        && suspendTabSource.Contains("ApplyLiveTabMemoryTarget(tab", StringComparison.Ordinal)
        && refreshMemorySweepSource.Contains("ConsecutiveSuspendFailures", StringComparison.Ordinal));
Check(
    "failed-suspend fallback never disposes a WebView during an in-flight suspension",
    failedSuspendDiscardSource.Contains("tab.IsSuspending", StringComparison.Ordinal)
        && failedSuspendDiscardSource.Contains("tab.IsClosed", StringComparison.Ordinal));
var browserCrashDownloadRecovery = VerifyBrowserProcessDownloadCleanup();
Check(
    "browser-process download cleanup is terminal and idempotent",
    browserCrashDownloadRecovery.FirstInterrupted == 1
        && browserCrashDownloadRecovery.SecondInterrupted == 0
        && browserCrashDownloadRecovery.Terminal
        && browserCrashDownloadRecovery.HonestReason);
Check(
    "browser-process download cleanup releases close, restart, and tab lifecycle guards",
    browserCrashDownloadRecovery.GuardWasActive
        && browserCrashDownloadRecovery.NoActiveRecords
        && browserCrashDownloadRecovery.TabCountersCleared
        && browserCrashDownloadRecovery.RestartGuardReleased);
Check(
    "find continuation accepts only the current live request",
    MainForm.IsFindContinuationEligible(
        isClosing: false,
        tabIsClosed: false,
        tabIsActive: true,
        coreIsCurrent: true,
        documentIsCurrent: true,
        sessionIsCurrent: true,
        findBarIsVisible: true,
        queryIsCurrent: true));
Check(
    "older find A cannot overwrite or stop newer find B",
    !MainForm.IsFindContinuationEligible(
        isClosing: false,
        tabIsClosed: false,
        tabIsActive: true,
        coreIsCurrent: true,
        documentIsCurrent: true,
        sessionIsCurrent: false,
        findBarIsVisible: true,
        queryIsCurrent: true));
Check(
    "reused WebView2 Find objects require the current request handlers",
    MainForm.IsFindSessionOwner(
        sessionIsCurrent: true,
        matchHandlerIsCurrent: true,
        activeHandlerIsCurrent: true)
        && !MainForm.IsFindSessionOwner(
            sessionIsCurrent: true,
            matchHandlerIsCurrent: false,
            activeHandlerIsCurrent: false));
Check(
    "find continuation rejects navigation and core replacement",
    !MainForm.IsFindContinuationEligible(
        isClosing: false,
        tabIsClosed: false,
        tabIsActive: true,
        coreIsCurrent: false,
        documentIsCurrent: true,
        sessionIsCurrent: true,
        findBarIsVisible: true,
        queryIsCurrent: true)
        && !MainForm.IsFindContinuationEligible(
            isClosing: false,
            tabIsClosed: false,
            tabIsActive: true,
            coreIsCurrent: true,
            documentIsCurrent: false,
            sessionIsCurrent: true,
            findBarIsVisible: true,
            queryIsCurrent: true));
Check(
    "find continuation rejects a closed tab",
    !MainForm.IsFindContinuationEligible(
        isClosing: false,
        tabIsClosed: true,
        tabIsActive: true,
        coreIsCurrent: true,
        documentIsCurrent: true,
        sessionIsCurrent: true,
        findBarIsVisible: true,
        queryIsCurrent: true));
Check(
    "motion policy accepts only its current tab-core request",
    MainForm.IsMotionPolicyContinuationEligible(
        isClosing: false,
        tabIsClosed: false,
        coreIsCurrent: true,
        generationIsCurrent: true,
        settingIsCurrent: true));
Check(
    "out-of-order motion ON cannot overwrite newer OFF",
    !MainForm.IsMotionPolicyContinuationEligible(
        isClosing: false,
        tabIsClosed: false,
        coreIsCurrent: true,
        generationIsCurrent: false,
        settingIsCurrent: false)
        && motionPolicySource.Contains(
            "core.RemoveScriptToExecuteOnDocumentCreated(installedScriptId)",
            StringComparison.Ordinal));
Check(
    "motion policy completion rejects a replacement core",
    !MainForm.IsMotionPolicyContinuationEligible(
        isClosing: false,
        tabIsClosed: false,
        coreIsCurrent: false,
        generationIsCurrent: true,
        settingIsCurrent: true));
Check(
    "reader-mode completion accepts only its current active document",
    MainForm.IsReaderModeContinuationEligible(
        isClosing: false,
        tabIsClosed: false,
        tabIsActive: true,
        coreIsCurrent: true,
        documentIsCurrent: true)
        && readerModeSource.Contains("if (!IsCurrent()) return;", StringComparison.Ordinal));
Check(
    "reader-mode completion rejects switch, navigation, replacement, and close",
    !MainForm.IsReaderModeContinuationEligible(false, false, false, true, true)
        && !MainForm.IsReaderModeContinuationEligible(false, false, true, true, false)
        && !MainForm.IsReaderModeContinuationEligible(false, false, true, false, true)
        && !MainForm.IsReaderModeContinuationEligible(false, true, true, true, true));
Check(
    "async UI adapters do not report through a disposed form",
    runUiTaskSource.Contains("IsDisposed", StringComparison.Ordinal)
        && runUiTaskSource.Contains("Disposing", StringComparison.Ordinal));
Check(
    "standard memory saver runs bounded unload sweeps while suspension remains ultra-only",
    refreshMemorySweepSource.Contains("mode == TabLifecycleMode.Off", StringComparison.Ordinal)
        && mainFormThemeSource.Contains(
            "currentMode is TabLifecycleMode.Standard or TabLifecycleMode.Ultra",
            StringComparison.Ordinal)
        && mainFormThemeSource.Contains("MaximumStandardUnloadsPerSweep", StringComparison.Ordinal)
        && windowStateSource.Contains("== TabLifecycleMode.Ultra", StringComparison.Ordinal));
Check(
    "automatic unload rechecks the three-tab resident guarantee at execution time",
    mainFormThemeSource.Contains("ProtectedResidentRank: GetProtectedResidentRank(tab)", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("&& !IsProtectedResidentTab(tab)", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("&& !tab.IsStartPage", StringComparison.Ordinal));
Check(
    "rapid switching does not cancel initialization of any protected resident tab",
    Enumerable.Range(0, TabLifecyclePolicy.ProtectedResidentTabCount).All(rank =>
        MainForm.ShouldContinueRequiredTabInitialization(
            isActive: false,
            isWindowMinimized: false,
            isBackgroundProtected: false,
            protectedResidentRank: rank))
        && !MainForm.ShouldContinueRequiredTabInitialization(
            isActive: false,
            isWindowMinimized: false,
            isBackgroundProtected: false,
            protectedResidentRank: TabLifecyclePolicy.ProtectedResidentTabCount)
        && CountOccurrences(
            mainFormThemeSource,
            "CanContinueRequiredTabInitialization(tab)") >= 6);
Check(
    "leaving ultra-light resumes sleeping tabs before applying live targets",
    resourceModeSource.Contains("normalizedMode != TabLifecycleMode.Ultra", StringComparison.Ordinal)
        && toggleUltraLightSource.Contains("item.IsSuspended", StringComparison.Ordinal)
        && toggleUltraLightSource.Contains("ResumeTab(tab)", StringComparison.Ordinal));
Check(
    "tab disposal releases WebView and frame COM references before shell teardown",
    browserTabDisposeSource.Contains("View = null;", StringComparison.Ordinal)
        && browserTabDisposeSource.Contains("AdBlockFrames.Clear();", StringComparison.Ordinal)
        && browserTabDisposeSource.Contains("FrameOrigins.Clear();", StringComparison.Ordinal)
        && browserTabDisposeSource.Contains("InitializationTask = null;", StringComparison.Ordinal)
        && browserTabDisposeSource.Contains("Host.Controls.Remove(view);", StringComparison.Ordinal));
Check(
    "core event sinks are detached before a replaced WebView is disposed",
    configureWebViewSource.Contains("TrackCoreEvent<", StringComparison.Ordinal)
        && configureWebViewSource.Contains("-= handler", StringComparison.Ordinal)
        && replaceTabViewSecuritySource.Contains("DetachCoreEventHandlers();", StringComparison.Ordinal)
        && replaceTabViewSecuritySource.Contains("ReleaseCleanLinkMenuItem();", StringComparison.Ordinal)
        && browserTabDisposeSource.Contains("DetachCoreEventHandlers();", StringComparison.Ordinal));
Check(
    "frame event sinks are detached on top-level navigation and tab teardown",
    navigationStartingSecuritySource.Contains("ReleaseTrackedFrames();", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("sealed class FrameEventSubscription", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("subscription.Attach();", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("catch (ObjectDisposedException) { }", StringComparison.Ordinal)
        && browserTabDisposeSource.Contains("ReleaseTrackedFrames();", StringComparison.Ordinal));
Check(
    "overlapping child-frame setup is queued with two workers instead of silently dropped",
    mainFormThemeSource.Contains("private const int MaximumConcurrentFrameSetups = 2;", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("private const int MaximumPendingFrameSetups = 64;", StringComparison.Ordinal)
        && frameSetupSource.Contains("pendingFrameSetups.Enqueue(work);", StringComparison.Ordinal)
        && frameSetupSource.Contains(
            "activeFrameSetupWorkers < MaximumConcurrentFrameSetups",
            StringComparison.Ordinal)
        && !frameSetupSource.Contains("WaitAsync(0)", StringComparison.Ordinal));
Check(
    "child-frame setup coalesces to the latest frame navigation generation",
    frameSetupSource.Contains("frameNavigationGeneration++", StringComparison.Ordinal)
        && frameSetupSource.Contains(
            "loadedFrameNavigationGeneration == frameNavigationGeneration",
            StringComparison.Ordinal)
        && frameSetupSource.Contains("existing.Replace(isCurrent, execute);", StringComparison.Ordinal)
        && frameSetupSource.Contains("activeFrameSetups.Contains(candidate.Frame)", StringComparison.Ordinal));
Check(
    "the bounded frame queue releases bookkeeping for evicted and abandoned work",
    frameSetupSource.Contains("AbandonOldestPendingFrameSetup();", StringComparison.Ordinal)
        && frameSetupSource.Contains("pendingFrameSetupByFrame.Remove", StringComparison.Ordinal)
        && frameSetupSource.Contains(
            "work.Tab.FrameSetupInProgress.Remove(work.Frame);",
            StringComparison.Ordinal));
Check(
    "frame setup closures are released on navigation, replacement, close, and teardown",
    navigationStartingSecuritySource.Contains("AbandonPendingFrameSetups(tab);", StringComparison.Ordinal)
        && replaceTabViewSecuritySource.Contains("AbandonPendingFrameSetups(tab);", StringComparison.Ordinal)
        && closeTabSource.Contains("AbandonPendingFrameSetups(tab);", StringComparison.Ordinal)
        && mainFormDisposeSource.Contains("StopFrameSetupQueue();", StringComparison.Ordinal));
Check(
    "a destroyed WebView2 frame is forgotten without calling native remove handlers",
    mainFormThemeSource.Contains("tab.ReleaseDestroyedFrame(frame);", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("subscription.AbandonDestroyedFrame();", StringComparison.Ordinal)
        && mainFormThemeSource.Contains(
            "Never call a CoreWebView2Frame member from its Destroyed event.",
            StringComparison.Ordinal));
Check(
    "an exact-host shield exception also permits its target document request",
    webResourceRequestedSecuritySource.Contains(
        "IsExceptionHost(HostFromUrl(e.Request.Uri))",
        StringComparison.Ordinal));
Check(
    "direct form disposal releases runtime queues and COM-backed records",
    mainFormDisposeSource.Contains("pendingExternalNavigations.Clear();", StringComparison.Ordinal)
        && mainFormDisposeSource.Contains("CancelAllPendingPermissionRequests();", StringComparison.Ordinal)
        && mainFormDisposeSource.Contains("DetachDownloadHandlers(download);", StringComparison.Ordinal)
        && mainFormDisposeSource.Contains("downloads.Clear();", StringComparison.Ordinal)
        && mainFormDisposeSource.Contains("InvalidateBrowserEnvironment();", StringComparison.Ordinal));
Check(
    "hidden reusable native surfaces release row tokens immediately",
    nativeFeatureSurfacesSource.Contains("Palette rows can contain a BrowserTab target", StringComparison.Ordinal)
        && nativeFeatureSurfacesSource.Contains("Permission rows carry WebView2 permission-setting wrappers", StringComparison.Ordinal)
        && CountOccurrences(nativeFeatureSurfacesSource, "protected override void OnVisibleChanged(EventArgs e)") >= 4
        && CountOccurrences(nativeFeatureSurfacesSource, "rows = [];") >= 3);
Check(
    "address suggestion indexing cannot root a closed window state",
    addressSuggestionSecuritySource.Contains("WeakReference<BrowserState>", StringComparison.Ordinal)
        && addressSuggestionSecuritySource.Contains("internal static void ReleaseCache", StringComparison.Ordinal)
        && mainFormDisposeSource.Contains("AddressSuggestionEngine.ReleaseCache(state);", StringComparison.Ordinal));
Check(
    "website theme updates converge on the newest setting without reloads",
    themeApplySource.Contains("generation != Interlocked.Read(ref websiteThemeGeneration)", StringComparison.Ordinal)
        && themeApplySource.Contains("useDarkTheme == darkModeEnabled", StringComparison.Ordinal)
        && themeApplySource.Contains("ApplyAutoDarkModeOverrideAsync", StringComparison.Ordinal)
        && !themeApplySource.Contains("Reload(", StringComparison.Ordinal)
        && !themeApplySource.Contains("AddScriptToExecuteOnDocumentCreatedAsync", StringComparison.Ordinal));
Check(
    "opt-in reduced motion compresses CSS motion while retaining lifecycle events",
    BrowserPerformance.NoMotionDocumentScript.Contains("animation-duration: 0.001ms", StringComparison.Ordinal)
        && BrowserPerformance.NoMotionDocumentScript.Contains("transition-duration: 0.001ms", StringComparison.Ordinal)
        && BrowserPerformance.NoMotionDocumentScript.Contains("scroll-behavior: auto", StringComparison.Ordinal)
        && !BrowserPerformance.NoMotionDocumentScript.Contains("Element.prototype.animate", StringComparison.Ordinal)
        && !BrowserPerformance.NoMotionDocumentScript.Contains("document.getAnimations", StringComparison.Ordinal));
Check(
    "reduced-motion install and restore scripts remain valid JavaScript",
    CanParseJavaScript(
        BrowserPerformance.NoMotionDocumentScript
        + Environment.NewLine
        + BrowserPerformance.RestoreMotionDocumentScript));
Check(
    "site responsiveness script is valid JavaScript and covers passive scroll, async decode, and hover preconnect",
    CanParseJavaScript(BrowserPerformance.SiteResponsivenessDocumentScript)
        && BrowserPerformance.SiteResponsivenessDocumentScript.Contains("__mishaSiteResponsivenessInstalled", StringComparison.Ordinal)
        && BrowserPerformance.SiteResponsivenessDocumentScript.Contains("addEventListener", StringComparison.Ordinal)
        && BrowserPerformance.SiteResponsivenessDocumentScript.Contains("passive: true", StringComparison.Ordinal)
        && BrowserPerformance.SiteResponsivenessDocumentScript.Contains("decoding = 'async'", StringComparison.Ordinal)
        && BrowserPerformance.SiteResponsivenessDocumentScript.Contains("font-display: swap", StringComparison.Ordinal)
        && BrowserPerformance.SiteResponsivenessDocumentScript.Contains("dns-prefetch", StringComparison.Ordinal));
Check(
    "browser environment options configure GPU rasterization, zero-copy, and network acceleration",
    mainFormThemeSource.Contains("--enable-gpu-rasterization", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("--enable-zero-copy", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("CanvasOopRasterization", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("ParallelDownloading", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("BackForwardCache", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("--enable-quic", StringComparison.Ordinal)
        && mainFormThemeSource.Contains("SiteResponsivenessDocumentScript", StringComparison.Ordinal));
var adBlockDocumentScript = AdBlockEngine.DocumentScript;
Check(
    "YouTube document shield covers desktop, mobile, and player ad surfaces",
    adBlockDocumentScript.Contains("ytd-ad-slot-renderer", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("#masthead-ad", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("ytd-search-pyv-renderer", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("ytm-companion-ad-renderer", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains(".video-ads", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains(".ytp-ad-module", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("ytp-ad-skip-button", StringComparison.Ordinal));
Check(
    "YouTube document shield prunes player ad metadata",
    adBlockDocumentScript.Contains("adPlacements", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("playerAds", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("adSlots", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("adBreakHeartbeatParams", StringComparison.Ordinal));
Check(
    "YouTube document shield covers fetch and XMLHttpRequest player responses",
    adBlockDocumentScript.Contains("fetch", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("XMLHttpRequest", StringComparison.Ordinal));
Check(
    "YouTube document shield scopes deep response patching to player payload endpoints",
    adBlockDocumentScript.Contains("shouldSanitizePlayerPayload", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("youtubei\\/v1\\/(?:player|get_watch)", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("get_video_info|playlist|watch", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("return shouldSanitizePlayerPayload(args[0])", StringComparison.Ordinal));
Check(
    "YouTube document shield leaves global DOM and JSON primitives untouched",
    !adBlockDocumentScript.Contains("Node.prototype.appendChild =", StringComparison.Ordinal)
        && !adBlockDocumentScript.Contains("JSON.parse = new Proxy", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("HTMLIFrameElement.prototype", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("contentWindowGetter", StringComparison.Ordinal));
Check(
    "YouTube document shield bounds document-wide observation to player bootstrap",
    !adBlockDocumentScript.Contains("rootObserver.observe(document.documentElement", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("playerBootstrapObserver", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("stopPlayerBootstrapObservation", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("attributeFilter: ['class']", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("10_000", StringComparison.Ordinal));
Check(
    "YouTube metadata sanitization is idempotent per response object",
    adBlockDocumentScript.Contains("const sanitizedObjects = new WeakSet()", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("sanitizedObjects.has(value)", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("sanitizedObjects.add(value)", StringComparison.Ordinal));
Check(
    "YouTube document shield recognizes server-side ad placement markers",
    adBlockDocumentScript.Contains("serverContract", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("SSAP, AD", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("getStatsForNerds", StringComparison.Ordinal));
Check(
    "YouTube document shield keeps the first-party player ad container compatible",
    !adBlockDocumentScript.Contains("'#player-ads'", StringComparison.Ordinal));
Check(
    "YouTube document shield keeps broad fallback cosmetics off the application shell",
    adBlockDocumentScript.Contains("const selectors = isYouTube ? [] : [", StringComparison.Ordinal));
Check(
    "YouTube document shield does not replace inherited renderer methods",
    !adBlockDocumentScript.Contains("Object.defineProperty(Object.prototype", StringComparison.Ordinal)
        && !adBlockDocumentScript.Contains("hasAllowedInstreamAd", StringComparison.Ordinal)
        && !adBlockDocumentScript.Contains("adBlocksFound", StringComparison.Ordinal));
Check(
    "YouTube server-contract recovery is bounded and endpoint-scoped",
    adBlockDocumentScript.Contains("recoveryMarkers = ['channel', 'lactmilli']", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("/youtubei\\/v1\\/player", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("status?.status === 'UNPLAYABLE'", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("WEB_PAGE_TYPE_UNKNOWN", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("recoveryAttempt >= recoveryMarkers.length", StringComparison.Ordinal));
Check(
    "YouTube server-contract recovery covers inline ad-block enforcement",
    adBlockDocumentScript.Contains("enforcementMessageViewModel", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("openAdAllowlistInstructionCommand", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("client.clientScreen = 'CHANNEL'", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("yt-enforcement-message-view-model", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("ytp-transparent", StringComparison.Ordinal));
Check(
    "YouTube exact enforcement is masked before first paint and fails open",
    adBlockDocumentScript.Contains("data-misha-youtube-recovery", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("exactAdBlockEnforcement", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("openAdAllowlistInstructionCommand", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("window.setTimeout(clearRecoveryMask, 15_000)", StringComparison.Ordinal)
        && !adBlockDocumentScript.Contains(
            "ytd-enforcement-message-view-model{display:none",
            StringComparison.Ordinal));
Check(
    "YouTube recovery advances on new player responses without a four-second stall",
    adBlockDocumentScript.Contains("seenRecoveryResponses", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("queuePlayerRecovery", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("queueUrgentCleanup", StringComparison.Ordinal)
        && !adBlockDocumentScript.Contains("recoveryLastAttempt", StringComparison.Ordinal)
        && !adBlockDocumentScript.Contains("Date.now() - recoveryLastAttempt", StringComparison.Ordinal));
Check(
    "YouTube recovery starts at document root creation instead of DOMContentLoaded",
    adBlockDocumentScript.Contains("cleanupBootstrapObserver.observe(document", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("styleBootstrapObserver.observe(document", StringComparison.Ordinal));
Check(
    "YouTube delayed network-machine enforcement is disabled without broad prototype hooks",
    adBlockDocumentScript.Contains("all_web_enable_network_machine", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("all_web_network_machine_raw_request", StringComparison.Ordinal)
        && !adBlockDocumentScript.Contains("Promise.prototype.then", StringComparison.Ordinal)
        && !adBlockDocumentScript.Contains("Map.prototype.has", StringComparison.Ordinal));
Check(
    "document shield does not replace the browser popup API",
    !adBlockDocumentScript.Contains("window.open =", StringComparison.Ordinal));
Check(
    "page scripts cannot turn off the document shield flag",
    adBlockDocumentScript.Contains(
        "Object.defineProperty(window, '__mishaAdBlockEnabled'",
        StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("set: () => {}", StringComparison.Ordinal));
var generatedAdBlockScript = AdBlockEngine.CreateDocumentScript("__smoke_control_channel");
Check(
    "document shield control channels are unique-install placeholders",
    generatedAdBlockScript.Contains("__smoke_control_channel", StringComparison.Ordinal)
        && !generatedAdBlockScript.Contains(
            "__MISHA_ADBLOCK_CONTROL_CHANNEL__",
            StringComparison.Ordinal));
Check(
    "YouTube document shield preserves watch data while pruning representative ads at runtime",
    CanExecuteAdBlockDocumentScript(generatedAdBlockScript));
var youtubeTransportRecovery = ExecuteYouTubeTransportRecoveryFixture(generatedAdBlockScript);
Check(
    "YouTube transport recovery requires the complete buffering-error signal",
    youtubeTransportRecovery.GetValueOrDefault("strictSignal"));
Check(
    "YouTube transport recovery preserves the requested playback position",
    youtubeTransportRecovery.GetValueOrDefault("startPreserved"));
Check(
    "YouTube transport recovery tries channel then lactmilli",
    youtubeTransportRecovery.GetValueOrDefault("markerSequence"));
Check(
    "YouTube transport recovery suppresses rapid duplicate retries",
    youtubeTransportRecovery.GetValueOrDefault("duplicateSuppressed"));
Check(
    "YouTube transport recovery remains armed during the second-attempt grace window",
    youtubeTransportRecovery.GetValueOrDefault("secondAttemptHold"));
Check(
    "YouTube transport recovery does not treat static ready media as progress",
    youtubeTransportRecovery.GetValueOrDefault("staticReadyHeld"));
Check(
    "YouTube transport recovery is capped at two attempts and fails open",
    youtubeTransportRecovery.GetValueOrDefault("cappedFailOpen"));
Check(
    "YouTube transport recovery clears its temporary state after real media progress",
    youtubeTransportRecovery.GetValueOrDefault("progressClears"));
Check(
    "YouTube transport recovery resumes a later stall from current playback",
    youtubeTransportRecovery.GetValueOrDefault("laterStallResumesCurrent"));
Check(
    "YouTube transport recovery respects a later seek back to the beginning",
    youtubeTransportRecovery.GetValueOrDefault("laterStallResumesSeekedStart"));
Check(
    "YouTube transport recovery clears after a strictly playable media state",
    youtubeTransportRecovery.GetValueOrDefault("playableStateClears"));
Check(
    "YouTube transport recovery ignores unavailable errors without exact retry-later text",
    youtubeTransportRecovery.GetValueOrDefault("exactErrorOnly"));
Check(
    "YouTube transport recovery ignores live, captcha, and non-buffering players",
    youtubeTransportRecovery.GetValueOrDefault("falsePositivesIgnored"));
Check(
    "YouTube transport recovery rejects missing and invalid video identities",
    youtubeTransportRecovery.GetValueOrDefault("invalidVideoIdsIgnored"));
Check(
    "YouTube transport recovery rejects a stale response for another video",
    youtubeTransportRecovery.GetValueOrDefault("mismatchedVideoIgnored"));
Check(
    "YouTube server recovery stays fail-open after its retry budget",
    youtubeTransportRecovery.GetValueOrDefault("serverBudgetFailsOpen"));
var documentScriptSyntaxPath = Path.Combine(
    Path.GetTempPath(),
    "MishaWeb-AdBlock-" + Guid.NewGuid().ToString("N") + ".js");
try
{
    File.WriteAllText(documentScriptSyntaxPath, generatedAdBlockScript);
    var syntaxCheckStart = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "node",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true
    };
    syntaxCheckStart.ArgumentList.Add("--check");
    syntaxCheckStart.ArgumentList.Add(documentScriptSyntaxPath);
    using var syntaxCheck = System.Diagnostics.Process.Start(syntaxCheckStart);
    var syntaxCompleted = syntaxCheck is not null && syntaxCheck.WaitForExit(5_000);
    if (syntaxCheck is { HasExited: false }) syntaxCheck.Kill(entireProcessTree: true);
    Check(
        "generated document shield is valid JavaScript",
        syntaxCompleted && syntaxCheck?.ExitCode == 0);
}
finally
{
    File.Delete(documentScriptSyntaxPath);
}
var adBlockUsesMutationObserver = adBlockDocumentScript.Contains("MutationObserver", StringComparison.Ordinal);
Check(
    "ad blocker gates mutation observation to YouTube pages",
    !adBlockUsesMutationObserver
        || (adBlockDocumentScript.Contains("youtube.com", StringComparison.Ordinal)
            && new[] { "location.hostname", "location.host", "isYouTube" }
                .Any(marker => adBlockDocumentScript.Contains(marker, StringComparison.Ordinal))));
Check(
    "YouTube cleanup is activity-aware and sparse while pages are hidden",
    adBlockDocumentScript.Contains("? 30_000", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("fastRecoveryPoll ? 250", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("window.setTimeout", StringComparison.Ordinal)
        && adBlockDocumentScript.Contains("playerObserver?.observe", StringComparison.Ordinal)
        && !adBlockDocumentScript.Contains("window.setInterval", StringComparison.Ordinal));

var largeSession = new BrowserState
{
    OpenTabs =
    [
        "javascript:alert(1)",
        "https://duplicate.example/",
        "https://duplicate.example/",
        .. Enumerable.Range(0, 130).Select(index => $"https://tab{index}.example/")
    ],
    ActiveTabIndex = 2
};
BrowserStateStore.NormalizeOpenTabs(largeSession);
Check(
    "large sessions keep duplicate tabs, remap the active tab, and cap lightweight shells",
    largeSession.OpenTabs.Count == 128
        && largeSession.OpenTabs[0] == "https://duplicate.example/"
        && largeSession.OpenTabs[1] == "https://duplicate.example/"
        && largeSession.ActiveTabIndex == 1);

Check(
    "session persistence uses bounded URL and settings sizes",
    BrowserStateStore.MaximumUrlLength == 4_096
        && BrowserStateStore.MaximumSettingsBytes == 16L * 1_024 * 1_024);

var oversizedPersistenceUrl = "https://oversized.example/"
    + new string('x', BrowserStateStore.MaximumUrlLength);
var persistenceState = new BrowserState
{
    OpenTabs =
    [
        null!,
        "javascript:alert(1)",
        "  https://kept.example/path  ",
        oversizedPersistenceUrl
    ],
    ActiveTabIndex = 2,
    Bookmarks =
    [
        null!,
        new BookmarkEntry(
            $"  Favorite\r\n{new string('F', BrowserStateStore.MaximumTitleLength + 100)}  ",
            "  https://favorite.example/path  "),
        new BookmarkEntry("duplicate", "https://favorite.example/path"),
        new BookmarkEntry("unsafe", "javascript:alert(1)"),
        new BookmarkEntry("oversized", oversizedPersistenceUrl)
    ],
    History =
    [
        null!,
        new HistoryEntry("Older history", "https://history.example/path", DateTime.UnixEpoch),
        new HistoryEntry("  Newer\r\nhistory  ", "  https://history.example/path  ", DateTime.UnixEpoch.AddDays(1)),
        new HistoryEntry("unsafe", "file:///C:/secret.txt", DateTime.UtcNow)
    ],
    DismissedSuggestionUrls =
    [
        null!,
        "  https://dismissed.example/path  ",
        "https://dismissed.example/path",
        "javascript:alert(1)",
        oversizedPersistenceUrl
    ],
    SuggestionUsage =
    [
        null!,
        new AddressSuggestionUsage("https://usage.example/path", 0, default),
        new AddressSuggestionUsage(
            "https://usage.example/path",
            2,
            DateTimeOffset.UnixEpoch.AddDays(2)),
        new AddressSuggestionUsage("javascript:alert(1)", 50, DateTimeOffset.UtcNow)
    ],
    KeepAwakeHosts =
    [
        null!,
        "EXAMPLE.com",
        "example.com",
        "bad host"
    ]
};
BrowserStateStore.NormalizeForPersistence(persistenceState);
Check(
    "persistence filters null, unsafe, and oversized tab entries while remapping the active tab",
    persistenceState.OpenTabs is ["https://kept.example/path"]
        && persistenceState.ActiveTabIndex == 0);
Check(
    "persistence sanitizes titles and trims, deduplicates, and caps bookmark URLs",
    persistenceState.Bookmarks.Count == 1
        && persistenceState.Bookmarks[0].Url == "https://favorite.example/path"
        && persistenceState.Bookmarks[0].Title.Length == BrowserStateStore.MaximumTitleLength
        && persistenceState.Bookmarks[0].Title.All(character => !char.IsControl(character)));
var unicodeBoundaryTitle = BrowserStateStore.SanitizeTitle(
    new string('x', BrowserStateStore.MaximumTitleLength - 2) + "😀" + "\uD800");
Check(
    "title sanitization preserves paired Unicode without splitting surrogate pairs",
    unicodeBoundaryTitle.Length == BrowserStateStore.MaximumTitleLength
        && HasOnlyPairedSurrogates(unicodeBoundaryTitle)
        && System.Text.Json.JsonSerializer.Serialize(unicodeBoundaryTitle).Length > 0);
Check(
    "persistence filters null history entries and keeps the newest sanitized duplicate",
    persistenceState.History is [{ Title: "Newer history", Url: "https://history.example/path" }]);
Check(
    "persistence sanitizes and deduplicates dismissed suggestions",
    persistenceState.DismissedSuggestionUrls is ["https://dismissed.example/path"]);
Check(
    "persistence merges valid suggestion usage without inventing recent activity",
    persistenceState.SuggestionUsage.Count == 1
        && persistenceState.SuggestionUsage[0].Url == "https://usage.example/path"
        && persistenceState.SuggestionUsage[0].AcceptedCount == 3
        && persistenceState.SuggestionUsage[0].LastAcceptedUtc == DateTimeOffset.UnixEpoch.AddDays(2));
Check(
    "persistence normalizes and deduplicates keep-awake hosts",
    persistenceState.KeepAwakeHosts.Count == 1
        && BrowserPolicy.IsExactHost(persistenceState.KeepAwakeHosts[0], "example.com"));

var cappedPersistenceState = new BrowserState
{
    Bookmarks = Enumerable.Range(0, 205)
        .Select(index => new BookmarkEntry($"Bookmark {index}", $"https://bookmark{index}.example/"))
        .ToList(),
    History = Enumerable.Range(0, 305)
        .Select(index => new HistoryEntry(
            $"History {index}",
            $"https://history{index}.example/",
            DateTime.UnixEpoch.AddMinutes(index)))
        .ToList(),
    DismissedSuggestionUrls = Enumerable.Range(0, 305)
        .Select(index => $"https://dismissed{index}.example/")
        .ToList(),
    SuggestionUsage = Enumerable.Range(0, 305)
        .Select(index => new AddressSuggestionUsage(
            $"https://usage{index}.example/",
            index + 1,
            DateTimeOffset.UnixEpoch.AddMinutes(index)))
        .ToList(),
    KeepAwakeHosts = Enumerable.Range(0, 205)
        .Select(index => $"keep-awake-{index}.example")
        .ToList()
};
BrowserStateStore.NormalizeForPersistence(cappedPersistenceState);
Check(
    "persistence caps all bounded user-state collections",
    cappedPersistenceState.Bookmarks.Count == 200
        && cappedPersistenceState.History.Count == 300
        && cappedPersistenceState.DismissedSuggestionUrls.Count == BrowserStateStore.MaximumDismissedSuggestions
        && cappedPersistenceState.SuggestionUsage.Count == BrowserStateStore.MaximumSuggestionUsageEntries
        && cappedPersistenceState.KeepAwakeHosts.Count == BrowserStateStore.MaximumKeepAwakeHosts);

var roundTripFolder = Path.Combine(Path.GetTempPath(), "MishaWeb.Tests." + Guid.NewGuid().ToString("N"));
var persistenceRoundTripSucceeded = false;
var backupRecoverySucceeded = false;
var unchangedPersistenceSkipped = false;
try
{
    var roundTripStore = new BrowserStateStore(roundTripFolder);
    var roundTripState = new BrowserState
    {
        OpenTabs = ["https://duplicate.example/", "https://duplicate.example/"],
        ActiveTabIndex = 1,
        Bookmarks = [new BookmarkEntry("Favorite", "https://favorite.example/")],
        History = [new HistoryEntry("History", "https://history.example/", DateTime.UtcNow)],
        SuggestionUsage =
        [
            new AddressSuggestionUsage(
                "https://favorite.example/",
                4,
                DateTimeOffset.UnixEpoch.AddDays(4))
        ],
        KeepAwakeHosts = ["favorite.example"],
        WindowPlacement = new SavedWindowPlacement(
            2880,
            120,
            900,
            800,
            SavedWindowPresentation.FullScreen,
            RestoreMaximizedAfterFullScreen: true)
    };
    roundTripStore.Save(roundTripState);
    var roundTrippedState = roundTripStore.Load();
    var settingsFile = new FileInfo(Path.Combine(roundTripFolder, "settings.json"));
    persistenceRoundTripSucceeded = roundTrippedState.OpenTabs.Count == 2
        && roundTrippedState.ActiveTabIndex == 1
        && roundTrippedState.Bookmarks is [{ Url: "https://favorite.example/" }]
        && roundTrippedState.History is [{ Url: "https://history.example/" }]
        && roundTrippedState.SuggestionUsage is [{ AcceptedCount: 4 }]
        && roundTrippedState.KeepAwakeHosts is ["favorite.example"]
        && roundTrippedState.WindowPlacement == roundTripState.WindowPlacement
        && settingsFile.Exists
        && settingsFile.Length <= BrowserStateStore.MaximumSettingsBytes;

    roundTripStore.Save(roundTripState);
    var backupFile = new FileInfo(settingsFile.FullName + ".bak");
    var primaryWriteTime = settingsFile.LastWriteTimeUtc;
    var backupWriteTime = backupFile.LastWriteTimeUtc;
    roundTripStore.Save(roundTripState);
    settingsFile.Refresh();
    backupFile.Refresh();
    unchangedPersistenceSkipped = settingsFile.LastWriteTimeUtc == primaryWriteTime
        && backupFile.LastWriteTimeUtc == backupWriteTime;

    roundTripStore.Save(new BrowserState
    {
        Bookmarks = [new BookmarkEntry("Replacement", "https://replacement.example/")]
    });
    File.WriteAllText(settingsFile.FullName, "{corrupt-json");
    var recoveredState = roundTripStore.Load();
    var restoredPrimaryState = roundTripStore.Load();
    backupRecoverySucceeded = recoveredState.Bookmarks is [{ Url: "https://replacement.example/" }]
        && recoveredState.SuggestionUsage.Count == 0
        && recoveredState.KeepAwakeHosts.Count == 0
        && restoredPrimaryState.Bookmarks is [{ Url: "https://replacement.example/" }]
        && File.Exists(settingsFile.FullName + ".bak");
}
catch
{
    persistenceRoundTripSucceeded = false;
    backupRecoverySucceeded = false;
}
finally
{
    if (Directory.Exists(roundTripFolder)) Directory.Delete(roundTripFolder, recursive: true);
}
Check("normalized settings survive an atomic file round trip", persistenceRoundTripSucceeded);
Check("unchanged settings skip redundant primary and backup writes", unchangedPersistenceSkipped);
Check("a mirrored settings backup recovers without resurrecting older private data", backupRecoverySucceeded);

var backgroundSaveFolder = Path.Combine(
    Path.GetTempPath(),
    "MishaWeb.BackgroundSave.Tests." + Guid.NewGuid().ToString("N"));
var backgroundSaveRunsOffCaller = false;
var backgroundSaveCoalesced = false;
var disposeDrainedLatestSnapshot = false;
BrowserStateStore? backgroundSaveStore = null;
using var firstBackgroundWriteEntered = new ManualResetEventSlim(false);
using var releaseFirstBackgroundWrite = new ManualResetEventSlim(false);
try
{
    var callerThreadId = Environment.CurrentManagedThreadId;
    var backgroundWriteCount = 0;
    var backgroundWriterThreadId = callerThreadId;
    backgroundSaveStore = new BrowserStateStore(
        backgroundSaveFolder,
        diagnosticsEnabled: false,
        beforeBackgroundWriteForTesting: () =>
        {
            backgroundWriterThreadId = Environment.CurrentManagedThreadId;
            if (Interlocked.Increment(ref backgroundWriteCount) == 1)
            {
                firstBackgroundWriteEntered.Set();
                _ = releaseFirstBackgroundWrite.Wait(TimeSpan.FromSeconds(5));
            }
        });

    var firstQueued = backgroundSaveStore.QueueSave(new BrowserState
    {
        OpenTabs = ["https://first-background-save.example/"]
    });
    var firstWriterStarted = firstBackgroundWriteEntered.Wait(TimeSpan.FromSeconds(5));
    var secondQueued = backgroundSaveStore.QueueSave(new BrowserState
    {
        OpenTabs = ["https://superseded-background-save.example/"]
    });
    var finalQueued = backgroundSaveStore.QueueSave(new BrowserState
    {
        OpenTabs = ["https://latest-background-save.example/"]
    });

    releaseFirstBackgroundWrite.Set();
    var coalescedWriterRan = SpinWait.SpinUntil(
        () => Volatile.Read(ref backgroundWriteCount) == 2,
        TimeSpan.FromSeconds(5));
    backgroundSaveStore.Dispose();
    using var backgroundSaveReader = new BrowserStateStore(
        backgroundSaveFolder,
        diagnosticsEnabled: false);
    var drainedState = backgroundSaveReader.Load();

    backgroundSaveRunsOffCaller = firstQueued
        && firstWriterStarted
        && backgroundWriterThreadId != callerThreadId;
    backgroundSaveCoalesced = secondQueued
        && finalQueued
        && coalescedWriterRan
        && Volatile.Read(ref backgroundWriteCount) == 2;
    disposeDrainedLatestSnapshot = drainedState.OpenTabs is
        ["https://latest-background-save.example/"];
}
catch
{
    backgroundSaveRunsOffCaller = false;
    backgroundSaveCoalesced = false;
    disposeDrainedLatestSnapshot = false;
}
finally
{
    releaseFirstBackgroundWrite.Set();
    backgroundSaveStore?.Dispose();
    if (Directory.Exists(backgroundSaveFolder))
    {
        Directory.Delete(backgroundSaveFolder, recursive: true);
    }
}
Check("debounced settings writes leave the caller thread", backgroundSaveRunsOffCaller);
Check("slow settings storage keeps only one pending snapshot", backgroundSaveCoalesced);
Check("disposing the settings store drains the newest snapshot", disposeDrainedLatestSnapshot);

var synchronousSaveFolder = Path.Combine(
    Path.GetTempPath(),
    "MishaWeb.SynchronousSave.Tests." + Guid.NewGuid().ToString("N"));
var synchronousSaveStayedAuthoritative = false;
BrowserStateStore? synchronousSaveStore = null;
using var delayedBackgroundWriteEntered = new ManualResetEventSlim(false);
try
{
    synchronousSaveStore = new BrowserStateStore(
        synchronousSaveFolder,
        diagnosticsEnabled: false,
        beforeBackgroundWriteForTesting: () =>
        {
            delayedBackgroundWriteEntered.Set();
            Thread.Sleep(100);
        });
    var olderQueued = synchronousSaveStore.QueueSave(new BrowserState
    {
        OpenTabs = ["https://older-debounced-save.example/"]
    });
    var olderWriteClaimed = delayedBackgroundWriteEntered.Wait(TimeSpan.FromSeconds(5));
    var finalSaved = synchronousSaveStore.Save(new BrowserState
    {
        OpenTabs = ["https://durable-final-save.example/"]
    });
    synchronousSaveStore.Dispose();

    using var synchronousSaveReader = new BrowserStateStore(
        synchronousSaveFolder,
        diagnosticsEnabled: false);
    var finalState = synchronousSaveReader.Load();
    synchronousSaveStayedAuthoritative = olderQueued
        && olderWriteClaimed
        && finalSaved
        && finalState.OpenTabs is ["https://durable-final-save.example/"];
}
catch
{
    synchronousSaveStayedAuthoritative = false;
}
finally
{
    synchronousSaveStore?.Dispose();
    if (Directory.Exists(synchronousSaveFolder))
    {
        Directory.Delete(synchronousSaveFolder, recursive: true);
    }
}
Check(
    "a durable final save cannot be overwritten by an older debounced write",
    synchronousSaveStayedAuthoritative);

var stateSaveTimerSource = ExtractSourceSection(
    mainFormThemeSource,
    "stateSaveTimer.Tick +=",
    "transientStatusTimer.Tick +=");
var queuedStateSaveSource = ExtractSourceSection(
    mainFormThemeSource,
    "private void QueueStateSaveNow()",
    "private bool SaveStateNow()");
var synchronousStateSaveSource = ExtractSourceSection(
    mainFormThemeSource,
    "private bool SaveStateNow()",
    "private void OnFormClosing(");
Check(
    "the WinForms debounce timer queues an immutable state snapshot",
    stateSaveTimerSource.Contains("QueueStateSaveNow()", StringComparison.Ordinal)
        && queuedStateSaveSource.Contains("stateStore.QueueSave(state)", StringComparison.Ordinal)
        && !queuedStateSaveSource.Contains("stateStore.Save(state)", StringComparison.Ordinal));
Check(
    "final-close and browser-restart saves remain synchronous and durable",
    synchronousStateSaveSource.Contains("stateStore.Save(state)", StringComparison.Ordinal));

var invalidWindowPlacementState = new BrowserState
{
    WindowPlacement = new SavedWindowPlacement(0, 0, 100, 100)
};
BrowserStateStore.NormalizeForPersistence(invalidWindowPlacementState);
Check("invalid saved window geometry is discarded", invalidWindowPlacementState.WindowPlacement is null);

var unknownWindowPresentationState = new BrowserState
{
    WindowPlacement = new SavedWindowPlacement(
        120,
        80,
        960,
        720,
        (SavedWindowPresentation)999,
        RestoreMaximizedAfterFullScreen: true)
};
BrowserStateStore.NormalizeForPersistence(unknownWindowPresentationState);
Check(
    "unknown saved window states safely fall back to normal",
    unknownWindowPresentationState.WindowPlacement is
    {
        Presentation: SavedWindowPresentation.Normal,
        RestoreMaximizedAfterFullScreen: false
    });

var suggestionState = new BrowserState
{
    Bookmarks =
    [
        new BookmarkEntry("git", "https://exact.example/"),
        new BookmarkEntry("GitHub", "https://github.com/"),
        new BookmarkEntry("Digital Garden", "https://garden.example/"),
        new BookmarkEntry("YouTube", "https://www.youtube.com/")
    ],
    History =
    [
        new HistoryEntry("Git cheat sheet", "https://history.example/git", DateTime.UnixEpoch)
    ]
};

var rankedSuggestions = AddressSuggestionEngine.GetSuggestions("git", suggestionState);
Check(
    "suggestions rank exact, prefix, and fuzzy local matches before search",
    rankedSuggestions.Count == 5
        && rankedSuggestions[0].Title == "git"
        && rankedSuggestions[0].Match == AddressSuggestionMatch.Exact
        && rankedSuggestions[1].Title == "GitHub"
        && rankedSuggestions[1].Match == AddressSuggestionMatch.Prefix
        && rankedSuggestions[2].Title == "Git cheat sheet"
        && rankedSuggestions[2].Match == AddressSuggestionMatch.Prefix
        && rankedSuggestions[3].Title == "Digital Garden"
        && rankedSuggestions[3].Match == AddressSuggestionMatch.Fuzzy
        && rankedSuggestions[4].IsSearch);
Check(
    "suggestions identify bookmark and history sources",
    rankedSuggestions[0].Source == AddressSuggestionSource.Bookmark
        && rankedSuggestions[2].Source == AddressSuggestionSource.History
        && rankedSuggestions[^1].Source == AddressSuggestionSource.Search);

var learnedSuggestionState = new BrowserState
{
    Bookmarks =
    [
        new BookmarkEntry("learn", "https://exact-learning.example/"),
        new BookmarkEntry("Learning old", "https://old-learning.example/"),
        new BookmarkEntry("Learning recent", "https://recent-learning.example/")
    ],
    SuggestionUsage =
    [
        new AddressSuggestionUsage(
            "https://old-learning.example/",
            2,
            DateTimeOffset.UnixEpoch.AddDays(1)),
        new AddressSuggestionUsage(
            "https://recent-learning.example/",
            2,
            DateTimeOffset.UnixEpoch.AddDays(2))
    ]
};
var learnedSuggestions = AddressSuggestionEngine.GetSuggestions("learn", learnedSuggestionState);
Check(
    "learned ranking preserves exact matches and uses recency to break frequency ties",
    learnedSuggestions.Count == 4
        && learnedSuggestions[0].NavigationTarget == "https://exact-learning.example/"
        && learnedSuggestions[1].NavigationTarget == "https://recent-learning.example/"
        && learnedSuggestions[2].NavigationTarget == "https://old-learning.example/"
        && learnedSuggestions[3].IsSearch);

var fuzzySuggestions = AddressSuggestionEngine.GetSuggestions("ytb", suggestionState);
Check(
    "cheap subsequence matching provides fuzzy suggestions",
    fuzzySuggestions.Count == 2
        && fuzzySuggestions[0].Title == "YouTube"
        && fuzzySuggestions[0].Match == AddressSuggestionMatch.Fuzzy);

var hostSuggestions = AddressSuggestionEngine.GetSuggestions("youtube.com", suggestionState);
Check(
    "URL matching ignores common scheme, www, and trailing slash decorations",
    hostSuggestions.Count == 2
        && hostSuggestions[0].Title == "YouTube"
        && hostSuggestions[0].Match == AddressSuggestionMatch.Exact);

var incompleteInputSuggestionState = new BrowserState
{
    History =
    [
        new HistoryEntry("YouTube", "https://www.youtube.com/", DateTime.UtcNow),
        new HistoryEntry("A page titled YouTube", "https://video.example/", DateTime.UtcNow)
    ]
};
var incompleteInputSuggestions = AddressSuggestionEngine.GetSuggestions(
    "you",
    incompleteInputSuggestionState);
Check(
    "incomplete input stays a Google query while matching sites remain explicit suggestions",
    incompleteInputSuggestions.Any(item =>
        !item.IsSearch && item.NavigationTarget == "https://www.youtube.com/")
        && incompleteInputSuggestions[^1] is
        {
            IsSearch: true,
            AcceptText: "you",
            NavigationTarget: "https://www.google.com/search?q=you"
        }
        && BrowserPolicy.ResolveAddress("you").Url == "https://www.google.com/search?q=you");

var duplicateState = new BrowserState
{
    Bookmarks =
    [
        new BookmarkEntry("A needle archive", "https://EXAMPLE.com/Path")
    ],
    History =
    [
        new HistoryEntry("needle", "https://example.COM/Path", DateTime.UnixEpoch)
    ]
};
var duplicateSuggestions = AddressSuggestionEngine.GetSuggestions("needle", duplicateState);
Check(
    "duplicate URLs collapse to their strongest matching entry",
    duplicateSuggestions.Count == 2
        && duplicateSuggestions[0].Source == AddressSuggestionSource.History
        && duplicateSuggestions[0].Match == AddressSuggestionMatch.Exact);

var mutableSuggestionState = new BrowserState
{
    History =
    [
        new HistoryEntry("Cache alpha", "https://alpha-cache.example/", DateTime.UnixEpoch),
        new HistoryEntry("Cache beta", "https://beta-cache.example/", DateTime.UnixEpoch)
    ]
};
_ = AddressSuggestionEngine.GetSuggestions("cache", mutableSuggestionState);
mutableSuggestionState.History.Insert(
    0,
    new HistoryEntry("cache", "https://exact-cache.example/", DateTime.UnixEpoch));
var suggestionsAfterHistoryMutation = AddressSuggestionEngine.GetSuggestions("cache", mutableSuggestionState);
Check(
    "the lightweight suggestion index notices history mutations",
    suggestionsAfterHistoryMutation[0].NavigationTarget == "https://exact-cache.example/");
mutableSuggestionState.DismissedSuggestionUrls.Add("https://exact-cache.example/");
var suggestionsAfterDismissal = AddressSuggestionEngine.GetSuggestions("cache", mutableSuggestionState);
Check(
    "the lightweight suggestion index notices dismissal mutations",
    suggestionsAfterDismissal.All(item => item.NavigationTarget != "https://exact-cache.example/"));
mutableSuggestionState.SuggestionUsage.Add(new AddressSuggestionUsage(
    "https://beta-cache.example/",
    20,
    DateTimeOffset.UnixEpoch.AddDays(1)));
var suggestionsAfterUsageMutation = AddressSuggestionEngine.GetSuggestions("cache", mutableSuggestionState);
Check(
    "the lightweight suggestion index notices ranking mutations",
    suggestionsAfterUsageMutation[0].NavigationTarget == "https://beta-cache.example/");
mutableSuggestionState.History[1] = new HistoryEntry(
    "cache",
    "https://replacement-cache.example/",
    DateTime.UnixEpoch);
var suggestionsAfterSameCountReplacement = AddressSuggestionEngine.GetSuggestions(
    "cache",
    mutableSuggestionState);
Check(
    "the lightweight suggestion index notices same-count entry replacements",
    suggestionsAfterSameCountReplacement[0].NavigationTarget
        == "https://replacement-cache.example/");

var encodedSearch = AddressSuggestionEngine.GetSuggestions("pink cats & tea", new BrowserState());
Check(
    "Google search suggestions are produced locally with escaped targets",
    encodedSearch.Count == 1
        && encodedSearch[0].IsSearch
        && encodedSearch[0].AcceptText == "pink cats & tea"
        && encodedSearch[0].NavigationTarget
            == "https://www.google.com/search?q=pink%20cats%20%26%20tea");
var legacyProviderSearch = AddressSuggestionEngine.GetSuggestions(
    "pink cats",
    new BrowserState { SearchProviderId = "bing" });
Check(
    "saved legacy providers cannot redirect suggestion searches away from Google",
    legacyProviderSearch is
    [
    {
        IsSearch: true,
        Detail: "Google search",
        NavigationTarget: "https://www.google.com/search?q=pink%20cats"
    }
    ]);

var manySuggestionsState = new BrowserState
{
    Bookmarks = Enumerable.Range(0, 12)
        .Select(index => new BookmarkEntry($"Pink place {index}", $"https://pink{index}.example/"))
        .ToList()
};
var cappedSuggestions = AddressSuggestionEngine.GetSuggestions("pink", manySuggestionsState, 999);
Check(
    "suggestions enforce a hard result cap and reserve the search row",
    cappedSuggestions.Count == AddressSuggestionEngine.MaximumResultLimit
        && cappedSuggestions[^1].IsSearch
        && cappedSuggestions.Count(item => !item.IsSearch)
            == AddressSuggestionEngine.MaximumResultLimit - 1);
Check(
    "keyboard selection indexes are contiguous and zero based",
    cappedSuggestions.Select((item, index) => item.KeyboardIndex == index).All(matches => matches));

var requestedThree = AddressSuggestionEngine.GetSuggestions("pink", manySuggestionsState, 3);
Check(
    "the caller result cap includes the Google row",
    requestedThree.Count == 3
        && requestedThree[0].KeyboardIndex == 0
        && requestedThree[1].KeyboardIndex == 1
        && requestedThree[2].KeyboardIndex == 2
        && requestedThree[2].IsSearch);
Check(
    "a one-row cap still returns the search action",
    AddressSuggestionEngine.GetSuggestions("pink", manySuggestionsState, 1) is [
        { IsSearch: true, KeyboardIndex: 0 }
    ]);
Check(
    "blank input and a zero row cap do not create suggestions",
    AddressSuggestionEngine.GetSuggestions("   ", suggestionState).Count == 0
        && AddressSuggestionEngine.GetSuggestions("pink", suggestionState, 0).Count == 0);
var unicodeSuggestion = AddressSuggestionEngine.GetSuggestions(
    new string('x', 78) + "😀tail",
    new BrowserState());
Check(
    "suggestion display truncation never splits a Unicode surrogate pair",
    unicodeSuggestion.Count == 1
        && HasOnlyPairedSurrogates(unicodeSuggestion[0].Title));
Check(
    "encoded searches that exceed the navigation cap are omitted",
    AddressSuggestionEngine.GetSuggestions(new string('é', 2_000), new BrowserState()).Count == 0);

var unsafeSuggestionState = new BrowserState
{
    History =
    [
        new HistoryEntry("pink script", "javascript:alert(1)", DateTime.UnixEpoch)
    ]
};
Check(
    "unsafe state entries are never emitted as navigation suggestions",
    AddressSuggestionEngine.GetSuggestions("pink", unsafeSuggestionState) is [
        { IsSearch: true }
    ]);

var removableSuggestionState = new BrowserState
{
    History =
    [
        new HistoryEntry("burger at DuckDuckGo", "https://duckduckgo.com/?q=burger&ia=web", DateTime.UnixEpoch),
        new HistoryEntry("Burger recipes", "https://recipes.example/burger", DateTime.UnixEpoch)
    ],
    DismissedSuggestionUrls = ["https://recipes.example/burger"]
};
var removableSuggestions = AddressSuggestionEngine.GetSuggestions("burger", removableSuggestionState);
Check(
    "legacy DuckDuckGo searches and dismissed URLs stay out of suggestions",
    removableSuggestions is [{ IsSearch: true }]);
Check(
    "ordinary DuckDuckGo pages are not mistaken for legacy search URLs",
    !BrowserPolicy.IsLegacyDuckDuckGoSearchUrl("https://duckduckgo.com/about")
        && BrowserPolicy.IsLegacyDuckDuckGoSearchUrl("https://duckduckgo.com/?q=burger&ia=web"));

var chromeGradeState = new BrowserState
{
    Bookmarks =
    [
        new BookmarkEntry("YouTube", "https://www.youtube.com/"),
        new BookmarkEntry("GitHub", "https://github.com/")
    ],
    History =
    [
        new HistoryEntry(
            "Your application for the Junior Digital Developer role was not selected. - mishael.oliva2002@gmail.com - Gmail",
            "https://mail.google.com/mail/u/0/#inbox/FMfcgzQhWLNrQMkkbBfvRXwrVZXWdhpl",
            DateTime.UtcNow),
        new HistoryEntry(
            "gdrive - Google Search",
            "https://www.google.com/search?q=gdrive",
            DateTime.UtcNow),
        new HistoryEntry(
            "yt to mp3 320kbps - Google Search",
            "https://www.google.com/search?q=yt%20to%20mp3%20320kbps",
            DateTime.UtcNow)
    ]
};

var ySuggestions = AddressSuggestionEngine.GetSuggestions("y", chromeGradeState);
Check(
    "host-first ranking elevates YouTube as Top Match over long email subjects",
    ySuggestions.Count >= 3
        && ySuggestions[0].Title == "YouTube"
        && ySuggestions[0].NavigationTarget == "https://www.youtube.com/"
        && ySuggestions[0].IsTopMatch
        && ySuggestions[0].Match == AddressSuggestionMatch.Prefix
        && ySuggestions[1].Title == "yt to mp3 320kbps"
        && ySuggestions[1].IsSearchHistory
        && ySuggestions[1].Detail == "Google search"
        && ySuggestions[^1].IsSearch);

var gSuggestions = AddressSuggestionEngine.GetSuggestions("g", chromeGradeState);
Check(
    "search query extraction cleans Google search history to clean search suggestions",
    gSuggestions.Count >= 3
        && gSuggestions[0].Title == "GitHub"
        && gSuggestions[0].IsTopMatch
        && gSuggestions[1].Title == "gdrive"
        && gSuggestions[1].Detail == "Google search"
        && gSuggestions[1].IsSearchHistory
        && gSuggestions[1].AcceptText == "gdrive");

Check(
    "GetTopMatchHost returns domain for omnibox inline autocomplete",
    AddressSuggestionEngine.GetTopMatchHost("y", chromeGradeState) == "youtube.com"
        && AddressSuggestionEngine.GetTopMatchHost("git", chromeGradeState) == "github.com");

var mergedLive = AddressSuggestionEngine.MergeWithLiveSearch(
    ySuggestions,
    ["youtube music", "yahoo", "yelp"],
    "y",
    chromeGradeState,
    6);
Check(
    "MergeWithLiveSearch seamlessly integrates top match, live query suggestions, and search row",
    mergedLive.Count == 6
        && mergedLive[0].Title == "YouTube"
        && mergedLive[0].IsTopMatch
        && mergedLive[1].Title == "youtube music"
        && mergedLive[1].IsSearch
        && mergedLive[2].Title == "yahoo"
        && mergedLive[3].Title == "yelp"
        && mergedLive[^1].IsSearch
        && mergedLive.Select((s, i) => s.KeyboardIndex == i).All(v => v));

var nowUtc = new DateTime(2026, 7, 12, 12, 0, 0, DateTimeKind.Utc);
const ulong OneGibibyte = 1024UL * 1024 * 1024;
const int ModernLogicalProcessorCount = 8;

var invalidMemorySnapshot = new SystemMemorySnapshot(
    MemoryLoadPercent: 0,
    TotalPhysicalBytes: 0,
    AvailablePhysicalBytes: 0,
    LogicalProcessorCount: 2,
    IsValid: false);
Check(
    "invalid memory snapshots remain distinguishable from a real zero-percent load",
    !invalidMemorySnapshot.IsValid && invalidMemorySnapshot.MemoryLoadPercent == 0);
Check(
    "logical processor classification survives an invalid memory snapshot",
    invalidMemorySnapshot.LogicalProcessorCount == 2 && invalidMemorySnapshot.IsLowSpecMachine);
Check(
    "current process working set trimming returns true on supported Windows hosts",
    SystemResourceInfo.TrimCurrentProcessWorkingSet());
Check(
    "process working set trimming succeeds for the current process id",
    SystemResourceInfo.TrimProcessWorkingSet(Environment.ProcessId));
Check(
    "process working set trimming rejects invalid or non-existent process ids safely",
    !SystemResourceInfo.TrimProcessWorkingSet(-1)
        && !SystemResourceInfo.TrimProcessWorkingSet(0));

Check(
    "ultra resident budget is one on a four-GiB machine",
    TabLifecyclePolicy.ResolveUltraResidentCoreBudget(4 * OneGibibyte, 8) == 1);
Check(
    "ultra resident budget leaves the smallest memory tier one byte above four GiB",
    TabLifecyclePolicy.ResolveUltraResidentCoreBudget((4 * OneGibibyte) + 1, 8) == 2);
Check(
    "ultra resident budget is one on a two-processor machine",
    TabLifecyclePolicy.ResolveUltraResidentCoreBudget(16 * OneGibibyte, 2) == 1);
Check(
    "ultra resident budget leaves the smallest CPU tier above two processors",
    TabLifecyclePolicy.ResolveUltraResidentCoreBudget(16 * OneGibibyte, 3) == 2);
Check(
    "ultra resident budget is two on an eight-GiB machine",
    TabLifecyclePolicy.ResolveUltraResidentCoreBudget(8 * OneGibibyte, 8) == 2);
Check(
    "ultra resident budget leaves the middle memory tier one byte above eight GiB",
    TabLifecyclePolicy.ResolveUltraResidentCoreBudget((8 * OneGibibyte) + 1, 8) == 3);
Check(
    "ultra resident budget is two on a four-processor machine",
    TabLifecyclePolicy.ResolveUltraResidentCoreBudget(16 * OneGibibyte, 4) == 2);
Check(
    "ultra resident budget leaves the middle CPU tier above four processors",
    TabLifecyclePolicy.ResolveUltraResidentCoreBudget(16 * OneGibibyte, 5) == 3);
Check(
    "ultra resident budget is three on a larger modern machine",
    TabLifecyclePolicy.ResolveUltraResidentCoreBudget(16 * OneGibibyte, 8) == 3);

var failedSuspendTab = Snapshot(90, TimeSpan.FromSeconds(15));
Check(
    "resident overflow discards a safe tab after three failed suspensions",
    TabLifecyclePolicy.ShouldDiscardAfterFailedSuspend(
        failedSuspendTab,
        TabLifecycleMode.Ultra,
        nowUtc,
        memoryLoadPercent: 40,
        memorySnapshotIsValid: true,
        totalPhysicalBytes: 8 * OneGibibyte,
        logicalProcessorCount: 4,
        residentCoreCount: 3,
        consecutiveFailures: 3));
Check(
    "failed-suspend fallback waits for three attempts",
    !TabLifecyclePolicy.ShouldDiscardAfterFailedSuspend(
        failedSuspendTab,
        TabLifecycleMode.Ultra,
        nowUtc,
        memoryLoadPercent: 80,
        memorySnapshotIsValid: true,
        totalPhysicalBytes: 8 * OneGibibyte,
        logicalProcessorCount: 4,
        residentCoreCount: 3,
        consecutiveFailures: 2));
Check(
    "normal machines use a thirty-second failed-suspend fallback",
    TabLifecyclePolicy.ShouldDiscardAfterFailedSuspend(
        Snapshot(91, TimeSpan.FromSeconds(30)),
        TabLifecycleMode.Ultra,
        nowUtc,
        memoryLoadPercent: 40,
        memorySnapshotIsValid: true,
        totalPhysicalBytes: 16 * OneGibibyte,
        logicalProcessorCount: ModernLogicalProcessorCount,
        residentCoreCount: 3,
        consecutiveFailures: 3));
Check(
    "failed-suspend fallback keeps protected tabs live",
    !TabLifecyclePolicy.ShouldDiscardAfterFailedSuspend(
        failedSuspendTab with { KeepAwake = true },
        TabLifecycleMode.Ultra,
        nowUtc,
        memoryLoadPercent: 100,
        memorySnapshotIsValid: true,
        totalPhysicalBytes: 2 * OneGibibyte,
        logicalProcessorCount: 2,
        residentCoreCount: 20,
        consecutiveFailures: 20));
Check(
    "failed-suspend fallback never unloads any of the three MRU resident tabs",
    Enumerable.Range(0, TabLifecyclePolicy.ProtectedResidentTabCount).All(rank =>
        !TabLifecyclePolicy.ShouldDiscardAfterFailedSuspend(
            failedSuspendTab with { ProtectedResidentRank = rank },
            TabLifecycleMode.Ultra,
            nowUtc,
            memoryLoadPercent: 100,
            memorySnapshotIsValid: true,
            totalPhysicalBytes: 2 * OneGibibyte,
            logicalProcessorCount: 2,
            residentCoreCount: 20,
            consecutiveFailures: 20)));
Check(
    "the protected resident rank is exactly three ordinary tabs",
    TabLifecyclePolicy.ProtectedResidentTabCount == 3
        && !TabLifecyclePolicy.IsProtectedResident(failedSuspendTab)
        && TabLifecyclePolicy.IsProtectedResident(failedSuspendTab with { ProtectedResidentRank = 0 })
        && TabLifecyclePolicy.IsProtectedResident(failedSuspendTab with { ProtectedResidentRank = 2 })
        && !TabLifecyclePolicy.IsProtectedResident(failedSuspendTab with { ProtectedResidentRank = 3 }));

Check(
    "resource policy is off when both resource modes are disabled",
    TabLifecyclePolicy.ResolveMode(false, false) == TabLifecycleMode.Off);
Check(
    "memory saver selects the standard resource policy",
    TabLifecyclePolicy.ResolveMode(true, false) == TabLifecycleMode.Standard);
Check(
    "ultra-light mode selects the ultra resource policy",
    TabLifecyclePolicy.ResolveMode(false, true) == TabLifecycleMode.Ultra);
Check(
    "ultra-light mode takes precedence over standard memory saver",
    TabLifecyclePolicy.ResolveMode(true, true) == TabLifecycleMode.Ultra);
Check(
    "resource-off mode keeps live background pages at the normal memory target",
    !TabLifecyclePolicy.ShouldUseLowMemoryTarget(
        TabLifecycleMode.Off,
        isForeground: false,
        requiresBackgroundExecution: true,
        consecutiveSuspendFailures: 3));
Check(
    "foreground pages always keep the normal memory target",
    !TabLifecyclePolicy.ShouldUseLowMemoryTarget(
        TabLifecycleMode.Ultra,
        isForeground: true,
        requiresBackgroundExecution: true,
        consecutiveSuspendFailures: 3));
Check(
    "ordinary ultra-light pages stay normal while waiting for suspension",
    !TabLifecyclePolicy.ShouldUseLowMemoryTarget(
        TabLifecycleMode.Ultra,
        isForeground: false,
        requiresBackgroundExecution: false,
        consecutiveSuspendFailures: 0));
Check(
    "standard memory saver puts every inactive page on the low memory target",
    TabLifecyclePolicy.ShouldUseLowMemoryTarget(
        TabLifecycleMode.Standard,
        isForeground: false,
        requiresBackgroundExecution: false,
        consecutiveSuspendFailures: 0));
Check(
    "protected ultra-light pages that cannot suspend use the low memory target",
    TabLifecyclePolicy.ShouldUseLowMemoryTarget(
        TabLifecycleMode.Ultra,
        isForeground: false,
        requiresBackgroundExecution: true,
        consecutiveSuspendFailures: 0));
Check(
    "ordinary ultra-light pages stay on the suspend strategy instead of mixing memory APIs",
    !TabLifecyclePolicy.ShouldUseLowMemoryTarget(
        TabLifecycleMode.Ultra,
        isForeground: false,
        requiresBackgroundExecution: false,
        consecutiveSuspendFailures: 1));
Check(
    "repeatedly unsuspendable ultra-light pages fall back to the low memory target",
    TabLifecyclePolicy.ShouldUseLowMemoryTarget(
        TabLifecycleMode.Ultra,
        isForeground: false,
        requiresBackgroundExecution: false,
        consecutiveSuspendFailures: TabLifecyclePolicy.SuspendFailureFallbackThreshold));

CheckNoDecision(
    "standard mode keeps an older live tab through its full fifteen-minute grace",
    Snapshot(1, TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1)),
    TabLifecycleMode.Standard,
    50,
    8 * OneGibibyte);
CheckSingleDecision(
    "standard mode directly unloads an older live tab after fifteen minutes",
    Snapshot(1, TimeSpan.FromMinutes(15)),
    TabLifecycleMode.Standard,
    50,
    8 * OneGibibyte,
    TabLifecycleAction.Discard);
CheckNoDecision(
    "standard pressure unloading waits for two full inactive minutes",
    Snapshot(1, TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1)),
    TabLifecycleMode.Standard,
    75,
    8 * OneGibibyte);
CheckSingleDecision(
    "verified pressure lets standard mode unload an older tab after two minutes",
    Snapshot(1, TimeSpan.FromMinutes(2)),
    TabLifecycleMode.Standard,
    75,
    8 * OneGibibyte,
    TabLifecycleAction.Discard);
CheckNoDecision(
    "invalid pressure data cannot shorten the standard unload grace",
    Snapshot(1, TimeSpan.FromMinutes(2)),
    TabLifecycleMode.Standard,
    75,
    8 * OneGibibyte,
    memorySnapshotIsValid: false);
Check(
    "standard mode never unloads any of the three protected resident tabs",
    Enumerable.Range(0, TabLifecyclePolicy.ProtectedResidentTabCount).All(rank =>
        PlanSweep(
            [Snapshot(130 + rank, TimeSpan.FromHours(1)) with { ProtectedResidentRank = rank }],
            TabLifecycleMode.Standard,
            memoryLoadPercent: 100,
            memorySnapshotIsValid: true,
            totalPhysicalBytes: 2 * OneGibibyte,
            logicalProcessorCount: 2,
            maximumActions: 1).Count == 0));
CheckSingleDecision(
    "the fourth MRU tab follows the bounded standard unload policy",
    Snapshot(133, TimeSpan.FromMinutes(2)) with { ProtectedResidentRank = 3 },
    TabLifecycleMode.Standard,
    75,
    2 * OneGibibyte,
    TabLifecycleAction.Discard,
    logicalProcessorCount: 2);
CheckSingleDecision(
    "ultra mode unloads a stalled background load after two minutes",
    Snapshot(92, TimeSpan.FromMinutes(2)) with { IsLoading = true },
    TabLifecycleMode.Ultra,
    50,
    8 * OneGibibyte,
    TabLifecycleAction.Discard,
    logicalProcessorCount: ModernLogicalProcessorCount);
CheckNoDecision(
    "stalled background loads keep their full two-minute grace period",
    Snapshot(92, TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1)) with { IsLoading = true },
    TabLifecycleMode.Ultra,
    50,
    8 * OneGibibyte,
    logicalProcessorCount: ModernLogicalProcessorCount);
CheckNoDecision(
    "standard mode gives a pressured background load five minutes to finish",
    Snapshot(92, TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1)) with { IsLoading = true },
    TabLifecycleMode.Standard,
    100,
    2 * OneGibibyte,
    logicalProcessorCount: 2);
CheckSingleDecision(
    "standard mode eventually unloads a stalled background load under pressure",
    Snapshot(92, TimeSpan.FromMinutes(5)) with { IsLoading = true },
    TabLifecycleMode.Standard,
    100,
    2 * OneGibibyte,
    TabLifecycleAction.Discard,
    logicalProcessorCount: 2);
CheckNoDecision(
    "protected background loads remain live in ultra mode",
    Snapshot(92, TimeSpan.FromMinutes(20)) with { IsLoading = true, KeepAwake = true },
    TabLifecycleMode.Ultra,
    100,
    2 * OneGibibyte,
    logicalProcessorCount: 2);
var loadingBudgetDecisions = PlanSweep(
    Enumerable.Range(0, 4)
        .Select(index => Snapshot(120 + index, TimeSpan.FromSeconds(5)) with { IsLoading = true })
        .ToArray(),
    TabLifecycleMode.Ultra,
    memoryLoadPercent: 40,
    memorySnapshotIsValid: true,
    totalPhysicalBytes: 16 * OneGibibyte,
    maximumActions: 10,
    logicalProcessorCount: ModernLogicalProcessorCount);
Check(
    "resident overflow unloads excess background loads after five seconds",
    loadingBudgetDecisions is [{ Id: 120, Action: TabLifecycleAction.Discard }]);
CheckSingleDecision(
    "ultra mode cancels stalled initialization under memory pressure",
    Snapshot(124, TimeSpan.FromSeconds(15)) with { IsInitializing = true, HasCore = false },
    TabLifecycleMode.Ultra,
    75,
    16 * OneGibibyte,
    TabLifecycleAction.Discard,
    logicalProcessorCount: ModernLogicalProcessorCount);
CheckSingleDecision(
    "ultra mode suspends at the five-second boundary",
    Snapshot(1, TimeSpan.FromSeconds(5)),
    TabLifecycleMode.Ultra,
    50,
    8 * OneGibibyte,
    TabLifecycleAction.Suspend);
CheckSingleDecision(
    "ultra mode may sleep an inactive MRU resident tab without unloading it",
    Snapshot(140, TimeSpan.FromSeconds(5)) with { ProtectedResidentRank = 2 },
    TabLifecycleMode.Ultra,
    100,
    2 * OneGibibyte,
    TabLifecycleAction.Suspend,
    logicalProcessorCount: 2);
CheckNoDecision(
    "a sleeping MRU resident tab is never discarded even under extreme pressure",
    Snapshot(140, TimeSpan.FromHours(1)) with
    {
        IsSuspended = true,
        ProtectedResidentRank = 2
    },
    TabLifecycleMode.Ultra,
    100,
    2 * OneGibibyte,
    logicalProcessorCount: 2);
CheckNoDecision(
    "a stalled MRU resident load is never discarded",
    Snapshot(141, TimeSpan.FromHours(1)) with
    {
        IsLoading = true,
        ProtectedResidentRank = 1
    },
    TabLifecycleMode.Ultra,
    100,
    2 * OneGibibyte,
    logicalProcessorCount: 2);
CheckNoDecision(
    "ultra mode does not suspend one tick before five seconds",
    Snapshot(1, TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1)),
    TabLifecycleMode.Ultra,
    50,
    8 * OneGibibyte);

var suspendedForPressureBoundary = Snapshot(2, TimeSpan.FromSeconds(15)) with { IsSuspended = true };
CheckSingleDecision(
    "ultra mode discards a suspended tab at 75 percent memory load after 15 seconds",
    suspendedForPressureBoundary,
    TabLifecycleMode.Ultra,
    75,
    16 * OneGibibyte,
    TabLifecycleAction.Discard,
    logicalProcessorCount: ModernLogicalProcessorCount);
CheckNoDecision(
    "pressure discard waits for the full 15-second boundary",
    Snapshot(2, TimeSpan.FromSeconds(15) - TimeSpan.FromTicks(1)) with { IsSuspended = true },
    TabLifecycleMode.Ultra,
    75,
    16 * OneGibibyte,
    logicalProcessorCount: ModernLogicalProcessorCount);
CheckNoDecision(
    "74 percent memory load does not trigger pressure discard",
    suspendedForPressureBoundary,
    TabLifecycleMode.Ultra,
    74,
    16 * OneGibibyte,
    logicalProcessorCount: ModernLogicalProcessorCount);
CheckNoDecision(
    "an explicitly invalid memory snapshot does not trigger pressure discard",
    suspendedForPressureBoundary,
    TabLifecycleMode.Ultra,
    75,
    16 * OneGibibyte,
    logicalProcessorCount: ModernLogicalProcessorCount,
    memorySnapshotIsValid: false);
Check(
    "the compatibility sweep does not treat an all-zero capture failure as valid pressure data",
    TabLifecyclePolicy.PlanSweep(
        [suspendedForPressureBoundary],
        TabLifecycleMode.Ultra,
        nowUtc,
        75,
        0,
        1).Count == 0);
CheckNoDecision(
    "an out-of-range memory load does not trigger pressure discard",
    suspendedForPressureBoundary,
    TabLifecycleMode.Ultra,
    101,
    16 * OneGibibyte,
    logicalProcessorCount: ModernLogicalProcessorCount);
CheckSingleDecision(
    "memory pressure suspends rather than directly discarding a live tab",
    Snapshot(2, TimeSpan.FromSeconds(15)),
    TabLifecycleMode.Ultra,
    75,
    16 * OneGibibyte,
    TabLifecycleAction.Suspend,
    logicalProcessorCount: ModernLogicalProcessorCount);
CheckSingleDecision(
    "standard mode directly unloads a stale suspended tab without invoking suspension",
    Snapshot(2, TimeSpan.FromMinutes(20)) with { IsSuspended = true },
    TabLifecycleMode.Standard,
    100,
    2 * OneGibibyte,
    TabLifecycleAction.Discard,
    logicalProcessorCount: 2);

var suspendedForSmallMachineBoundary = Snapshot(3, TimeSpan.FromSeconds(30)) with { IsSuspended = true };
CheckSingleDecision(
    "ultra mode discards after 30 seconds on a four-GiB machine",
    suspendedForSmallMachineBoundary,
    TabLifecycleMode.Ultra,
    40,
    4 * OneGibibyte,
    TabLifecycleAction.Discard,
    logicalProcessorCount: ModernLogicalProcessorCount);
CheckNoDecision(
    "small-machine discard waits for the full 30-second boundary",
    Snapshot(3, TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1)) with { IsSuspended = true },
    TabLifecycleMode.Ultra,
    40,
    4 * OneGibibyte,
    logicalProcessorCount: ModernLogicalProcessorCount);
CheckSingleDecision(
    "ultra mode discards after 30 seconds on a two-processor machine",
    suspendedForSmallMachineBoundary,
    TabLifecycleMode.Ultra,
    40,
    16 * OneGibibyte,
    TabLifecycleAction.Discard,
    logicalProcessorCount: 2);
CheckNoDecision(
    "a machine above four GiB with more than two processors is not in the smallest tier",
    suspendedForSmallMachineBoundary,
    TabLifecycleMode.Ultra,
    40,
    (4 * OneGibibyte) + 1,
    logicalProcessorCount: ModernLogicalProcessorCount);
CheckNoDecision(
    "unknown physical memory with a modern processor count is not treated as tiny",
    suspendedForSmallMachineBoundary,
    TabLifecycleMode.Ultra,
    40,
    0,
    logicalProcessorCount: ModernLogicalProcessorCount,
    memorySnapshotIsValid: false);
CheckSingleDecision(
    "ultra mode suspends before it discards an unsuspended tiny-machine tab",
    Snapshot(3, TimeSpan.FromSeconds(30)),
    TabLifecycleMode.Ultra,
    40,
    4 * OneGibibyte,
    TabLifecycleAction.Suspend,
    logicalProcessorCount: ModernLogicalProcessorCount);

var suspendedForTwoMinutes = Snapshot(4, TimeSpan.FromMinutes(2)) with { IsSuspended = true };
CheckSingleDecision(
    "ultra mode unconditionally discards a suspended tab after two minutes",
    suspendedForTwoMinutes,
    TabLifecycleMode.Ultra,
    10,
    16 * OneGibibyte,
    TabLifecycleAction.Discard,
    logicalProcessorCount: ModernLogicalProcessorCount);
CheckNoDecision(
    "unconditional discard waits for the full two-minute boundary",
    Snapshot(4, TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1)) with { IsSuspended = true },
    TabLifecycleMode.Ultra,
    10,
    16 * OneGibibyte,
    logicalProcessorCount: ModernLogicalProcessorCount);

var tinyBudgetTabs = new[]
{
    Snapshot(101, TimeSpan.FromSeconds(7)) with { IsActive = true },
    Snapshot(102, TimeSpan.FromSeconds(6)) with { IsSuspended = true },
    Snapshot(103, TimeSpan.FromSeconds(5)) with { IsSuspended = true }
};
var tinyBudgetDecisions = PlanSweep(
    tinyBudgetTabs,
    TabLifecycleMode.Ultra,
    memoryLoadPercent: 10,
    memorySnapshotIsValid: true,
    totalPhysicalBytes: 4 * OneGibibyte,
    logicalProcessorCount: ModernLogicalProcessorCount,
    maximumActions: 10);
Check(
    "resident overflow discards only eligible suspended tabs and leaves the active tab protected",
    tinyBudgetDecisions.Count == 2
        && tinyBudgetDecisions.All(item => item.Action == TabLifecycleAction.Discard)
        && tinyBudgetDecisions[0].Id == 102
        && tinyBudgetDecisions[1].Id == 103);

var protectedResidentBudgetTabs = new[]
{
    Snapshot(150, TimeSpan.FromMinutes(10)) with { IsSuspended = true, ProtectedResidentRank = 0 },
    Snapshot(151, TimeSpan.FromMinutes(10)) with { IsSuspended = true, ProtectedResidentRank = 1 },
    Snapshot(152, TimeSpan.FromMinutes(10)) with { IsSuspended = true, ProtectedResidentRank = 2 },
    Snapshot(153, TimeSpan.FromMinutes(10)) with { IsSuspended = true, ProtectedResidentRank = 3 }
};
Check(
    "tiny-machine resident budgets can unload only pages outside the MRU three",
    PlanSweep(
        protectedResidentBudgetTabs,
        TabLifecycleMode.Ultra,
        memoryLoadPercent: 100,
        memorySnapshotIsValid: true,
        totalPhysicalBytes: 2 * OneGibibyte,
        logicalProcessorCount: 2,
        maximumActions: 10) is [{ Id: 153, Action: TabLifecycleAction.Discard }]);

var budgetBoundaryTabs = new[]
{
    Snapshot(111, TimeSpan.FromSeconds(5)) with { IsActive = true },
    Snapshot(112, TimeSpan.FromSeconds(5)) with { IsSuspended = true }
};
Check(
    "resident budget discards at the five-second boundary",
    PlanSweep(
        budgetBoundaryTabs,
        TabLifecycleMode.Ultra,
        10,
        true,
        4 * OneGibibyte,
        ModernLogicalProcessorCount,
        10) is [{ Id: 112, Action: TabLifecycleAction.Discard }]);
Check(
    "resident budget waits until the five-second boundary",
    PlanSweep(
        [
            budgetBoundaryTabs[0],
            budgetBoundaryTabs[1] with
            {
                LastActiveUtc = nowUtc - TimeSpan.FromSeconds(5) + TimeSpan.FromTicks(1)
            }
        ],
        TabLifecycleMode.Ultra,
        10,
        true,
        4 * OneGibibyte,
        ModernLogicalProcessorCount,
        10).Count == 0);

var eligibleUltraTab = Snapshot(10, TimeSpan.FromMinutes(3));
var guardedSnapshots = new (string Name, TabLifecycleSnapshot Snapshot)[]
{
    ("active tabs are protected", eligibleUltraTab with { IsActive = true }),
    ("closed tabs are ignored", eligibleUltraTab with { IsClosed = true }),
    ("recent loading tabs keep their pressure grace", Snapshot(10, TimeSpan.FromSeconds(15) - TimeSpan.FromTicks(1)) with { IsLoading = true }),
    ("recent initializing tabs keep their pressure grace", Snapshot(10, TimeSpan.FromSeconds(15) - TimeSpan.FromTicks(1)) with { IsInitializing = true, HasCore = false }),
    ("audible tabs are protected", eligibleUltraTab with { IsAudible = true }),
    ("downloading tabs are protected", eligibleUltraTab with { HasActiveDownload = true }),
    ("tabs without a live core are ignored", eligibleUltraTab with { HasCore = false }),
    ("keep-awake tabs are protected", eligibleUltraTab with { KeepAwake = true }),
    ("negative tab identifiers are rejected", eligibleUltraTab with { Id = -1 }),
    ("future activity timestamps are rejected", eligibleUltraTab with { LastActiveUtc = nowUtc.AddTicks(1) }),
    ("default activity timestamps are rejected", eligibleUltraTab with { LastActiveUtc = default }),
    ("non-UTC activity timestamps are rejected", eligibleUltraTab with
    {
                LastActiveUtc = DateTime.SpecifyKind(nowUtc - TimeSpan.FromMinutes(3), DateTimeKind.Unspecified)
    })
};
foreach (var guarded in guardedSnapshots)
{
    CheckNoDecision(guarded.Name, guarded.Snapshot, TabLifecycleMode.Ultra, 90, 8 * OneGibibyte);
}

CheckNoDecision(
    "off mode plans no lifecycle work",
    eligibleUltraTab,
    TabLifecycleMode.Off,
    100,
    2 * OneGibibyte);
Check(
    "a non-UTC sweep timestamp is rejected",
    TabLifecyclePolicy.PlanSweep(
        [eligibleUltraTab],
        TabLifecycleMode.Ultra,
        DateTime.SpecifyKind(nowUtc, DateTimeKind.Unspecified),
        90,
        8 * OneGibibyte,
        2).Count == 0);
Check(
    "an unknown lifecycle mode is rejected",
    TabLifecyclePolicy.PlanSweep(
        [eligibleUltraTab],
        (TabLifecycleMode)999,
        nowUtc,
        90,
        8 * OneGibibyte,
        2).Count == 0);

var ordered = TabLifecyclePolicy.PlanSweep(
    [
        Snapshot(30, TimeSpan.FromSeconds(6)),
        Snapshot(20, TimeSpan.FromSeconds(8)),
        Snapshot(10, TimeSpan.FromSeconds(7))
    ],
    TabLifecycleMode.Ultra,
    nowUtc,
    50,
    8 * OneGibibyte,
    2);
Check(
    "sweep chooses the oldest eligible tabs first",
    ordered.Count == 2 && ordered[0].Id == 20 && ordered[1].Id == 10);

var discardBeforeSuspend = PlanSweep(
    [
        Snapshot(40, TimeSpan.FromMinutes(1)),
        Snapshot(41, TimeSpan.FromSeconds(15)) with { IsSuspended = true }
    ],
    TabLifecycleMode.Ultra,
    memoryLoadPercent: 75,
    memorySnapshotIsValid: true,
    totalPhysicalBytes: 16 * OneGibibyte,
    logicalProcessorCount: ModernLogicalProcessorCount,
    maximumActions: 1);
Check(
    "discard decisions take priority over an older suspend decision",
    discardBeforeSuspend is [{ Id: 41, Action: TabLifecycleAction.Discard }]);

var skippedGuard = TabLifecyclePolicy.PlanSweep(
    [
        Snapshot(1, TimeSpan.FromSeconds(10)) with { KeepAwake = true },
        Snapshot(2, TimeSpan.FromSeconds(8)),
        Snapshot(3, TimeSpan.FromSeconds(7))
    ],
    TabLifecycleMode.Ultra,
    nowUtc,
    50,
    8 * OneGibibyte,
    2);
Check(
    "an ineligible old tab does not consume the action batch",
    skippedGuard.Count == 2 && skippedGuard[0].Id == 2 && skippedGuard[1].Id == 3);

var tiedTimestamp = nowUtc - TimeSpan.FromSeconds(6);
var stableTies = TabLifecyclePolicy.PlanSweep(
    [
        Snapshot(7, TimeSpan.Zero) with { LastActiveUtc = tiedTimestamp },
        Snapshot(4, TimeSpan.Zero) with { LastActiveUtc = tiedTimestamp },
        Snapshot(9, TimeSpan.Zero) with { LastActiveUtc = tiedTimestamp }
    ],
    TabLifecycleMode.Ultra,
    nowUtc,
    50,
    8 * OneGibibyte,
    2);
Check(
    "equal activity timestamps preserve input order",
    stableTies.Count == 2 && stableTies[0].Id == 7 && stableTies[1].Id == 4);

var oneAction = TabLifecyclePolicy.PlanSweep(
    [Snapshot(1, TimeSpan.FromSeconds(7)), Snapshot(2, TimeSpan.FromSeconds(6))],
    TabLifecycleMode.Ultra,
    nowUtc,
    50,
    8 * OneGibibyte,
    1);
Check("sweep honors the maximum action count", oneAction.Count == 1 && oneAction[0].Id == 1);

var manyResidentTabs = Enumerable.Range(0, 10)
    .Select(index => Snapshot(200 + index, TimeSpan.FromSeconds(20 - index)) with
    {
        IsSuspended = true
    })
    .ToList();
var convergenceOrder = new List<int>();
var convergenceSweeps = 0;
while (convergenceSweeps < 10)
{
    var convergenceDecisions = PlanSweep(
        manyResidentTabs,
        TabLifecycleMode.Ultra,
        memoryLoadPercent: 10,
        memorySnapshotIsValid: true,
        totalPhysicalBytes: 8 * OneGibibyte,
        logicalProcessorCount: ModernLogicalProcessorCount,
        maximumActions: 3);
    if (convergenceDecisions.Count == 0) break;

    convergenceSweeps++;
    foreach (var decision in convergenceDecisions)
    {
        convergenceOrder.Add(decision.Id);
        var index = manyResidentTabs.FindIndex(item => item.Id == decision.Id);
        if (index >= 0)
        {
            manyResidentTabs[index] = manyResidentTabs[index] with
            {
                HasCore = false,
                IsSuspended = false
            };
        }
    }
}
Check(
    "batched ultra sweeps converge many resident tabs to the two-core budget",
    convergenceSweeps == 3
        && manyResidentTabs.Count(item => item.HasCore) == 2
        && convergenceOrder.SequenceEqual(Enumerable.Range(200, 8)));
Check(
    "a zero-sized action batch plans no work",
    TabLifecyclePolicy.PlanSweep(
        [eligibleUltraTab],
        TabLifecycleMode.Ultra,
        nowUtc,
        100,
        2 * OneGibibyte,
        0).Count == 0);
Check(
    "a negative action batch plans no work",
    TabLifecyclePolicy.PlanSweep(
        [eligibleUltraTab],
        TabLifecycleMode.Ultra,
        nowUtc,
        100,
        2 * OneGibibyte,
        -1).Count == 0);

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} smoke test(s) failed:");
    foreach (var failure in failures) Console.Error.WriteLine($"- {failure}");
    return 1;
}

Console.WriteLine($"MishaWeb smoke tests passed ({checkCount} checks).");
return 0;

TabLifecycleSnapshot Snapshot(int id, TimeSpan inactiveFor)
{
    return new TabLifecycleSnapshot(
        id,
        nowUtc - inactiveFor,
        IsActive: false,
        IsClosed: false,
        IsLoading: false,
        IsInitializing: false,
        IsAudible: false,
        HasActiveDownload: false,
        HasCore: true,
        IsSuspended: false,
        KeepAwake: false);
}

void CheckSingleDecision(
    string name,
    TabLifecycleSnapshot snapshot,
    TabLifecycleMode mode,
    uint memoryLoadPercent,
    ulong totalPhysicalBytes,
    TabLifecycleAction expectedAction,
    int logicalProcessorCount = ModernLogicalProcessorCount,
    bool memorySnapshotIsValid = true)
{
    var decisions = PlanSweep(
        [snapshot],
        mode,
        memoryLoadPercent,
        memorySnapshotIsValid,
        totalPhysicalBytes,
        logicalProcessorCount,
        1);
    Check(
        name,
        decisions.Count == 1
            && decisions[0].Id == snapshot.Id
            && decisions[0].Action == expectedAction);
}

void CheckNoDecision(
    string name,
    TabLifecycleSnapshot snapshot,
    TabLifecycleMode mode,
    uint memoryLoadPercent,
    ulong totalPhysicalBytes,
    int logicalProcessorCount = ModernLogicalProcessorCount,
    bool memorySnapshotIsValid = true)
{
    Check(
        name,
        PlanSweep(
            [snapshot],
            mode,
            memoryLoadPercent,
            memorySnapshotIsValid,
            totalPhysicalBytes,
            logicalProcessorCount,
            1).Count == 0);
}

IReadOnlyList<TabLifecycleDecision> PlanSweep(
    IReadOnlyList<TabLifecycleSnapshot> snapshots,
    TabLifecycleMode mode,
    uint memoryLoadPercent,
    bool memorySnapshotIsValid,
    ulong totalPhysicalBytes,
    int logicalProcessorCount,
    int maximumActions)
{
    return TabLifecyclePolicy.PlanSweep(
        snapshots,
        mode,
        nowUtc,
        memoryLoadPercent,
        memorySnapshotIsValid,
        totalPhysicalBytes,
        logicalProcessorCount,
        maximumActions);
}

void CheckUrl(string name, string input, string expected)
{
    var resolution = BrowserPolicy.ResolveAddress(input);
    Check(name, resolution.Error is null && !resolution.IsStartPage && resolution.Url == expected);
}

bool CanConstructNativeStartPage()
{
    Exception? error = null;
    var completedLayout = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var page = new NativeStartPage { Size = new Size(960, 560) };
            string? navigationTarget = null;
            StartPageAction? requestedAction = null;
            page.NavigateRequested += (_, target) => navigationTarget = target;
            page.ActionRequested += (_, action) => requestedAction = action;
            page.SetQuickLinks(
            [
                new StartPageLink("Example", "https://example.com/"),
                new StartPageLink("Search", "https://www.google.com/", "Favorite"),
                new StartPageLink("Docs", "https://learn.microsoft.com/"),
                new StartPageLink("News", "https://news.example/"),
                new StartPageLink("Mail", "https://mail.example/"),
                new StartPageLink("Music", "https://music.example/"),
                new StartPageLink("Maps", "https://maps.example/"),
                new StartPageLink("Extra", "https://extra.example/")
            ]);
            page.SetStatus(new StartPageStatus("Ultra-light mode", "6 tabs · 2 resting"));
            page.CreateControl();
            page.PerformLayout();
            using var renderedPage = new Bitmap(page.Width, page.Height);
            page.DrawToBitmap(renderedPage, page.ClientRectangle);
            var descendants = GetDescendants(page).ToArray();
            var activatedLink = page.ActivateQuickLinkForTesting(0);
            var activatedAction = page.ActivateActionForTesting(StartPageAction.ShowTabs);
            completedLayout = page.Controls.Count > 0
                && descendants.Count(control => control.AccessibleRole == AccessibleRole.Link) == 3
                && descendants.Any(control => control.AccessibleName == "Ultra-light mode")
                && descendants.Any(control => control.AccessibleName == "6 tabs · 2 resting")
                && activatedLink
                && navigationTarget == "https://example.com/"
                && activatedAction
                && requestedAction == StartPageAction.ShowTabs;
        }
        catch (Exception caught)
        {
            error = caught;
        }
    })
    { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return error is null && completedLayout;
}

bool CanConfigureNativeStartPageForIncognitoMode()
{
    Exception? error = null;
    var verified = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var page = new NativeStartPage { Size = new Size(960, 560) };
            page.SetPrivateMode(true);
            page.CreateControl();
            page.PerformLayout();
            var descendants = GetDescendants(page).ToArray();
            var titleLabel = descendants.OfType<Label>().FirstOrDefault(c => c.Text == "Incognito");
            var taglineLabel = descendants.OfType<Label>().FirstOrDefault(c => c.Text.Contains("Incognito mode", StringComparison.OrdinalIgnoreCase));
            var quickLinksVisible = descendants.Any(c => c.AccessibleName == "Quick links" && c.Visible);

            var titleIsIncognito = titleLabel is not null && titleLabel.ForeColor == NativeUiTheme.Lavender;
            var taglineIsVisible = taglineLabel is not null && taglineLabel.Visible;
            var quickLinksHidden = !quickLinksVisible;

            page.SetPrivateMode(false);
            page.PerformLayout();
            var revertedTitleOk = descendants.OfType<Label>().Any(c => c.Text == "MishaWeb");

            verified = titleIsIncognito
                && taglineIsVisible
                && quickLinksHidden
                && revertedTitleOk;
        }
        catch (Exception caught)
        {
            error = caught;
        }
    })
    { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return error is null && verified;
}

(bool Active24Bit, bool Released) VerifyBackdropFrameCacheLifecycle()
{
    Exception? error = null;
    var active24Bit = false;
    var released = false;
    var diagnostic = string.Empty;
    var thread = new Thread(() =>
    {
        try
        {
            var baseline = StartPageArtwork.GetBackdropFrameStateForTesting();
            using var host = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false,
                ClientSize = new Size(960, 560)
            };
            using var page = new NativeStartPage { Dock = DockStyle.Fill };
            host.Controls.Add(page);
            host.Show();
            host.PerformLayout();
            page.PerformLayout();
            Application.DoEvents();

            using (var renderedPage = new Bitmap(page.Width, page.Height))
            {
                page.DrawToBitmap(renderedPage, page.ClientRectangle);
            }

            var active = StartPageArtwork.GetBackdropFrameStateForTesting();
            active24Bit = baseline.Leases == 0
                && !baseline.Allocated
                && active.Leases == 1
                && active.Allocated
                && active.PixelFormat == System.Drawing.Imaging.PixelFormat.Format24bppRgb;

            page.Visible = false;
            Application.DoEvents();
            var hidden = StartPageArtwork.GetBackdropFrameStateForTesting();
            released = hidden.Leases == 0 && !hidden.Allocated;
            diagnostic = $"baseline={baseline}; active={active}; hidden={hidden}";
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (error is not null) Console.Error.WriteLine($"Backdrop-frame lifecycle probe failed: {error}");
    else if (!active24Bit || !released) Console.Error.WriteLine($"Backdrop-frame lifecycle probe: {diagnostic}");
    return error is null ? (active24Bit, released) : (false, false);
}

bool OffscreenBackdropRenderingStaysUncached()
{
    var before = StartPageArtwork.GetBackdropFrameStateForTesting();
    using var rendered = new Bitmap(
        320,
        180,
        System.Drawing.Imaging.PixelFormat.Format24bppRgb);
    using (var graphics = Graphics.FromImage(rendered))
    {
        StartPageArtwork.DrawBackdropSlice(
            graphics,
            new Size(960, 540),
            new Rectangle(240, 180, 320, 180),
            new Rectangle(0, 0, 320, 180));
    }
    var after = StartPageArtwork.GetBackdropFrameStateForTesting();
    return before.Leases == 0
        && !before.Allocated
        && after.Leases == 0
        && !after.Allocated
        && SurfaceHasVisualDetail(rendered);
}

bool HasEmbeddedStartPageBackdrop()
{
    try
    {
        using var stream = typeof(StartPageArtwork).Assembly.GetManifestResourceStream(
            StartPageArtwork.BackdropResourceName);
        if (stream is null || stream.Length is < 64_000 or > 400_000) return false;

        using var image = Image.FromStream(
            stream,
            useEmbeddedColorManagement: false,
            validateImageData: true);
        return image.Width >= 1_600
            && image.Height >= 900
            && image.Width / (double)image.Height is > 1.76 and < 1.79;
    }
    catch
    {
        return false;
    }
}

(bool Master, bool Runtime, bool Embedded, bool SingleDecode, bool SmallRender, bool BadgeRender, bool Icon, bool Wiring)
    VerifyBunnyLogoAssetsAndRendering()
{
    const string masterFileName = "mishaweb-bunny-logo-v2.png";
    const string runtimeFileName = "mishaweb-bunny-logo-runtime.png";
    const string iconFileName = "mishaweb-bunny-logo-v2.ico";
    const string resourceName = "MishaWeb.Brand.BunnyLogo.png";

    var masterPath = FindRepositoryFile("desktop", "Assets", "Brand", masterFileName);
    var runtimePath = FindRepositoryFile("desktop", "Assets", "Brand", runtimeFileName);
    var iconPath = FindRepositoryFile("desktop", "Assets", "Brand", iconFileName);
    var masterValid = IsExpectedPng(masterPath, expectedPixels: 512, maximumBytes: 500_000);
    var runtimeValid = IsExpectedPng(runtimePath, expectedPixels: 256, maximumBytes: 250_000);
    var embeddedValid = false;
    try
    {
        var assembly = typeof(StartPageArtwork).Assembly;
        var names = assembly.GetManifestResourceNames();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not null
            && names.Count(name => name.Equals(resourceName, StringComparison.Ordinal)) == 1
            && stream.Length is > 10_000 and <= 250_000)
        {
            using var image = Image.FromStream(
                stream,
                useEmbeddedColorManagement: false,
                validateImageData: true);
            embeddedValid = image.RawFormat.Guid == System.Drawing.Imaging.ImageFormat.Png.Guid
                && image.Width == 256
                && image.Height == 256;
        }
    }
    catch
    {
        embeddedValid = false;
    }

    var projectSource = ReadRepositorySource("desktop", "MishaWeb.csproj")
        .Replace('\\', '/');
    var artworkSource = ReadRepositorySource("desktop", "StartPageArtwork.cs");
    var brandArtworkSource = ReadRepositorySource("desktop", "BrandArtwork.cs");
    var wiringValid = projectSource.Contains(
            $"<ApplicationIcon>Assets/Brand/{iconFileName}</ApplicationIcon>",
            StringComparison.Ordinal)
        && projectSource.Contains(
            $"EmbeddedResource Include=\"Assets/Brand/{runtimeFileName}\"",
            StringComparison.Ordinal)
        && projectSource.Contains($"LogicalName=\"{resourceName}\"", StringComparison.Ordinal)
        && !projectSource.Contains(
            $"EmbeddedResource Include=\"Assets/Brand/{masterFileName}\"",
            StringComparison.Ordinal)
        && brandArtworkSource.Contains(resourceName, StringComparison.Ordinal)
        && artworkSource.Contains("BrandArtwork.TryDrawGeneratedLogo", StringComparison.Ordinal);

    return (
        masterValid,
        runtimeValid,
        embeddedValid,
        embeddedValid && BunnyLogoUsesSingleLazyDecode(),
        runtimeValid && CanRenderLogoAtSmallSizes(runtimePath),
        embeddedValid && CanRenderStartPageLogoBadge(),
        ValidateMultiSizeIcon(iconPath),
        wiringValid);
}

bool BunnyLogoUsesSingleLazyDecode()
{
    try
    {
        var brandArtworkType = typeof(StartPageArtwork).Assembly.GetType("MishaWeb.BrandArtwork");
        var field = brandArtworkType?.GetFields(
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
                candidate.Name.Contains("Logo", StringComparison.OrdinalIgnoreCase)
                && candidate.FieldType.IsGenericType
                && candidate.FieldType.GetGenericTypeDefinition() == typeof(Lazy<>));
        var lazy = field?.GetValue(null);
        var valueProperty = lazy?.GetType().GetProperty("Value");
        var first = valueProperty?.GetValue(lazy);
        var second = valueProperty?.GetValue(lazy);
        return first is Image
            && ReferenceEquals(first, second);
    }
    catch
    {
        return false;
    }
}

bool IsExpectedPng(string? path, int expectedPixels, long maximumBytes)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
    try
    {
        var file = new FileInfo(path);
        if (file.Length is <= 10_000 || file.Length > maximumBytes) return false;
        using var image = Image.FromFile(path, useEmbeddedColorManagement: false);
        return image.RawFormat.Guid == System.Drawing.Imaging.ImageFormat.Png.Guid
            && image.Width == expectedPixels
            && image.Height == expectedPixels;
    }
    catch
    {
        return false;
    }
}

bool CanRenderLogoAtSmallSizes(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
    try
    {
        using var source = Image.FromFile(path, useEmbeddedColorManagement: false);
        foreach (var pixels in new[] { 16, 24, 32, 64 })
        {
            using var rendered = new Bitmap(
                pixels,
                pixels,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(rendered))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, pixels, pixels));
            }
            if (!RenderedLogoIsReadable(rendered)) return false;
        }
        return true;
    }
    catch
    {
        return false;
    }
}

bool RenderedLogoIsReadable(Bitmap bitmap)
{
    var visible = 0;
    var rosePixels = 0;
    var minimumX = bitmap.Width;
    var minimumY = bitmap.Height;
    var maximumX = -1;
    var maximumY = -1;
    var minimumLuminance = 1d;
    var maximumLuminance = 0d;
    var quantizedColors = new HashSet<int>();
    for (var y = 0; y < bitmap.Height; y++)
    {
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.A < 64) continue;
            visible++;
            minimumX = Math.Min(minimumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumX = Math.Max(maximumX, x);
            maximumY = Math.Max(maximumY, y);
            var luminance = RelativeLuminance(pixel);
            minimumLuminance = Math.Min(minimumLuminance, luminance);
            maximumLuminance = Math.Max(maximumLuminance, luminance);
            quantizedColors.Add(((pixel.R >> 5) << 6) | ((pixel.G >> 5) << 3) | (pixel.B >> 5));
            if (pixel.R > pixel.G + 24 && pixel.R > pixel.B + 8) rosePixels++;
        }
    }

    var total = bitmap.Width * bitmap.Height;
    var visibleWidth = maximumX >= minimumX ? maximumX - minimumX + 1 : 0;
    var visibleHeight = maximumY >= minimumY ? maximumY - minimumY + 1 : 0;
    return visible >= total * 0.45
        && visibleWidth >= bitmap.Width * 0.75
        && visibleHeight >= bitmap.Height * 0.75
        && maximumLuminance - minimumLuminance >= 0.45
        && quantizedColors.Count >= 6
        && rosePixels > 0;
}

bool ValidateMultiSizeIcon(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
    try
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 70
            || BitConverter.ToUInt16(data, 0) != 0
            || BitConverter.ToUInt16(data, 2) != 1)
        {
            return false;
        }

        var count = BitConverter.ToUInt16(data, 4);
        if (count < 4 || data.Length < 6 + (count * 16)) return false;
        var sizes = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            var offset = 6 + (index * 16);
            var width = data[offset] == 0 ? 256 : data[offset];
            var height = data[offset + 1] == 0 ? 256 : data[offset + 1];
            var payloadLength = BitConverter.ToUInt32(data, offset + 8);
            var payloadOffset = BitConverter.ToUInt32(data, offset + 12);
            if (width != height
                || payloadLength == 0
                || payloadOffset > data.Length
                || payloadLength > data.Length - payloadOffset)
            {
                return false;
            }
            sizes.Add(width);
        }

        using var icon = new Icon(path, new Size(32, 32));
        using var rendered = icon.ToBitmap();
        return new[] { 16, 32, 48, 256 }.All(sizes.Contains)
            && rendered.Width > 0
            && rendered.Height > 0;
    }
    catch
    {
        return false;
    }
}

bool CanRenderStartPageLogoBadge()
{
    Exception? error = null;
    var valid = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var host = new Form
            {
                AutoScaleMode = AutoScaleMode.Dpi,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false,
                ClientSize = new Size(960, 720)
            };
            using var page = new NativeStartPage { Dock = DockStyle.Fill };
            host.Controls.Add(page);
            host.Show();
            host.PerformLayout();
            page.PerformLayout();
            Application.DoEvents();

            var badge = GetDescendants(page).SingleOrDefault(control =>
                control.AccessibleRole == AccessibleRole.Graphic
                && control.AccessibleName == "MishaWeb logo");
            if (badge is null || !badge.Visible || badge.Width < 48 || badge.Height < 48) return;
            using var rendered = new Bitmap(
                badge.Width,
                badge.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            badge.DrawToBitmap(rendered, badge.ClientRectangle);
            valid = page.ClientRectangle.Contains(BoundsWithin(page, badge))
                && SurfaceHasVisualDetail(rendered);
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (error is not null) Console.Error.WriteLine($"Bunny badge render probe failed: {error}");
    return error is null && valid;
}

(bool Valid, bool Hierarchy, string ObservedDpis) VerifyNativeStartPageResponsiveLayout()
{
    var modes = new[]
    {
        (Name: "unaware", ExpectedDpi: 96),
        (Name: "per-monitor-v2", ExpectedDpi: 0)
    };
    var observations = new List<string>(modes.Length);
    var allValid = true;
    var allHierarchyValid = true;
    foreach (var mode in modes)
    {
        var result = RunStartPageLayoutProbeProcess(mode.Name);
        observations.Add($"{mode.Name}:{result.Dpi}");
        allValid &= result.Valid
            && (mode.ExpectedDpi == 0
                ? result.Dpi is >= 96 and <= 480
                : result.Dpi == mode.ExpectedDpi);
        allHierarchyValid &= result.Hierarchy;
    }
    return (allValid, allHierarchyValid, string.Join(", ", observations));
}

(bool Valid, bool Hierarchy, int Dpi) RunStartPageLayoutProbeProcess(string mode)
{
    try
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return (false, false, 0);
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("--check-start-page-layout");
        process.StartInfo.ArgumentList.Add(mode);
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Console.Error.WriteLine($"Start-page {mode} DPI probe timed out.");
            return (false, false, 0);
        }

        var fields = standardOutput.Trim()
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(field => field.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        var valid = false;
        var hierarchy = false;
        var dpi = 0;
        var parsed = fields.TryGetValue("valid", out var validText)
            && bool.TryParse(validText, out valid)
            && fields.TryGetValue("hierarchy", out var hierarchyText)
            && bool.TryParse(hierarchyText, out hierarchy)
            && fields.TryGetValue("dpi", out var dpiText)
            && int.TryParse(dpiText, out dpi);
        if (process.ExitCode != 0 || !parsed)
        {
            Console.Error.WriteLine(
                $"Start-page {mode} DPI child failed ({process.ExitCode}): "
                + $"stdout={standardOutput.Trim()}; stderr={standardError.Trim()}");
            return (false, false, fields.TryGetValue("dpi", out var observed) && int.TryParse(observed, out var value) ? value : 0);
        }
        return (valid, hierarchy, dpi);
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Start-page {mode} DPI child could not run: {error}");
        return (false, false, 0);
    }
}

(bool Valid, bool Hierarchy, int Dpi) VerifyNativeStartPageResponsiveLayoutInCurrentProcess()
{
    Exception? error = null;
    var valid = false;
    var hierarchyValid = false;
    var observedDpi = 0;
    var thread = new Thread(() =>
    {
        try
        {
            using var host = new Form
            {
                AutoScaleMode = AutoScaleMode.Dpi,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false
            };
            using var page = new NativeStartPage { Dock = DockStyle.Fill };
            page.SetQuickLinks(
            [
                new StartPageLink("Messenger", "https://messenger.com/", "Favorite"),
                new StartPageLink("Gmail", "https://mail.google.com/"),
                new StartPageLink("YouTube", "https://youtube.com/"),
                new StartPageLink("Drive", "https://drive.google.com/"),
                new StartPageLink("Docs", "https://docs.example/"),
                new StartPageLink("Music", "https://music.example/")
            ]);
            page.SetStatus(new StartPageStatus("Ultra-light mode", "6 tabs open"));
            host.Controls.Add(page);
            host.Show();
            observedDpi = page.DeviceDpi;

            string? initialSignature = null;
            var viewports = new[]
            {
                (Width: 960, Height: 720),
                (Width: 480, Height: 420),
                (Width: 640, Height: 560),
                (Width: 960, Height: 360),
                (Width: 760, Height: 720),
                (Width: 1280, Height: 720),
                (Width: 1917, Height: 720),
                (Width: 960, Height: 720)
            };
            valid = true;
            hierarchyValid = true;
            for (var index = 0; index < viewports.Length; index++)
            {
                var viewport = viewports[index];
                host.ClientSize = new Size(
                    ScaleForDpi(viewport.Width, observedDpi),
                    ScaleForDpi(viewport.Height, observedDpi));
                host.PerformLayout();
                page.PerformLayout();
                Application.DoEvents();

                var descendants = GetDescendants(page).ToArray();
                var badge = descendants.SingleOrDefault(control =>
                    control.AccessibleRole == AccessibleRole.Graphic
                    && control.AccessibleName == "MishaWeb logo");
                var title = descendants.SingleOrDefault(control =>
                    control.AccessibleRole == AccessibleRole.StaticText
                    && control.AccessibleName == "MishaWeb");
                var card = descendants.OfType<ScrollableControl>().SingleOrDefault(control =>
                    control.AccessibleName == "Start page card");
                var links = descendants
                    .Where(control => control.Visible && control.AccessibleRole == AccessibleRole.Link)
                    .ToArray();
                var featureChips = descendants.Where(control =>
                    control.Visible
                    && control.AccessibleRole == AccessibleRole.PushButton
                    && (control.AccessibleDescription?.StartsWith("Lean browsing.", StringComparison.Ordinal) == true
                        || control.AccessibleDescription?.StartsWith("Tab workspace.", StringComparison.Ordinal) == true))
                    .ToArray();
                var interactive = descendants.Where(control =>
                    control.Visible
                    && control.AccessibleRole is AccessibleRole.Link
                        or AccessibleRole.PushButton
                        or AccessibleRole.Text).ToArray();
                var searchBounds = BoundsWithin(page, page.SearchBar);
                var cardBounds = card is null ? Rectangle.Empty : BoundsWithin(page, card);
                var badgeBounds = badge is null ? Rectangle.Empty : BoundsWithin(page, badge);
                var titleBounds = title is null ? Rectangle.Empty : BoundsWithin(page, title);
                var controlsContained = interactive.All(control =>
                    page.ClientRectangle.Contains(BoundsWithin(page, control)));
                var linksSeparated = RectanglesDoNotOverlap(
                    links.Select(control => BoundsWithin(page, control)));
                var majorControlsSeparated = RectanglesDoNotOverlap(
                    links.Select(control => BoundsWithin(page, control))
                        .Concat(featureChips.Select(control => BoundsWithin(page, control)))
                        .Append(badgeBounds)
                        .Append(titleBounds)
                        .Append(searchBounds));
                var statusPresent = descendants.Any(control =>
                        control.AccessibleName == "Ultra-light mode")
                    && descendants.Any(control => control.AccessibleName == "6 tabs open");
                var completeHierarchy = HasExpectedStartPageHierarchy(page, descendants);
                var scrollActive = card is not null
                    && (card.VerticalScroll.Visible
                        || card.DisplayRectangle.Height > card.ClientSize.Height);
                var geometryValid = badge is not null
                    && title is not null
                    && card is not null
                    && cardBounds.Width <= ScaleForDpi(680, observedDpi) + 1
                    && Math.Abs(cardBounds.Left - ((page.ClientSize.Width - cardBounds.Width) / 2)) <= 1
                    && badge.Visible
                    && title.Visible
                    && page.SearchBar.Visible
                    && page.ClientRectangle.Contains(badgeBounds)
                    && page.ClientRectangle.Contains(titleBounds)
                    && page.ClientRectangle.Contains(searchBounds)
                    && badgeBounds.Width == badgeBounds.Height
                    && badgeBounds.Width >= ScaleForDpi(48, observedDpi)
                    && searchBounds.Width >= ScaleForDpi(300, observedDpi)
                    && !Overlaps(badgeBounds, titleBounds)
                    && !Overlaps(badgeBounds, searchBounds)
                    && !Overlaps(titleBounds, searchBounds)
                    && (controlsContained || scrollActive)
                    && links.Length == 3
                    && featureChips.Length == 0
                    && linksSeparated
                    && majorControlsSeparated
                    && statusPresent
                    && completeHierarchy;

                using var rendered = new Bitmap(page.Width, page.Height);
                page.DrawToBitmap(rendered, page.ClientRectangle);
                var visualDetail = SurfaceHasVisualDetail(rendered);
                var snapshotValid = geometryValid && visualDetail;
                if (!snapshotValid)
                {
                    Console.Error.WriteLine(
                        $"Start-page layout failed for {viewport.Width}x{viewport.Height} logical pixels "
                        + $"at {observedDpi} DPI: badge={badgeBounds}; title={titleBounds}; "
                        + $"search={searchBounds}; card={cardBounds}; links={links.Length}; contained={controlsContained}; "
                        + $"separated={linksSeparated}; status={statusPresent}; "
                        + $"features={featureChips.Length}; majorSeparated={majorControlsSeparated}; "
                        + $"scroll={scrollActive}; hierarchy={completeHierarchy}; "
                        + $"geometry={geometryValid}; detail={visualDetail}.");
                }
                valid &= snapshotValid;
                hierarchyValid &= completeHierarchy;

                var signature = CreateStartPageLayoutSignature(page);
                if (index == 0) initialSignature = signature;
                if (index == viewports.Length - 1) valid &= signature == initialSignature;
            }
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (error is not null) Console.Error.WriteLine($"Start-page DPI probe failed: {error}");
    return (error is null && valid, error is null && hierarchyValid, observedDpi);
}

bool HasExpectedStartPageHierarchy(NativeStartPage page, IReadOnlyCollection<Control> descendants)
{
    var linkCount = descendants.Count(control => control.AccessibleRole == AccessibleRole.Link);
    var input = descendants.OfType<TextBox>().SingleOrDefault(control =>
        control.AccessibleName == "Search or open a website");
    var provider = descendants.OfType<Button>().SingleOrDefault(control =>
        control.AccessibleName?.StartsWith("Search engine:", StringComparison.Ordinal) == true);
    var submit = descendants.OfType<Button>().SingleOrDefault(control =>
        control != provider
        && control.AccessibleName is "Go" or "Search" or "Open website");
    return page.Controls.Count > 0
        && descendants.Any(control =>
            control.AccessibleRole == AccessibleRole.Graphic
            && control.AccessibleName == "MishaWeb logo")
        && descendants.Any(control =>
            control.AccessibleRole == AccessibleRole.StaticText
            && control.AccessibleName == "MishaWeb")
        && !descendants.OfType<Label>().Any(control =>
            control.Visible
            && control.Text.Contains("Light on memory", StringComparison.Ordinal))
        && descendants.Contains(page.SearchBar)
        && input is not null
        && provider is not null
        && submit is not null
        && descendants.Any(control => control.AccessibleName == "Ultra-light mode")
        && descendants.Any(control => control.AccessibleName == "6 tabs open")
        && linkCount == 3;
}

string CreateStartPageLayoutSignature(NativeStartPage page)
{
    var controls = GetDescendants(page)
        .Where(control => control.Visible
            && (control.AccessibleName == "MishaWeb logo"
                || control.AccessibleName == "MishaWeb"
                || control.AccessibleRole == AccessibleRole.Link
                || ReferenceEquals(control, page.SearchBar)))
        .Select(control =>
        {
            var bounds = BoundsWithin(page, control);
            return $"{control.AccessibleRole}:{control.AccessibleName}:{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}";
        })
        .OrderBy(value => value, StringComparer.Ordinal);
    return string.Join("|", controls);
}

bool SurfaceHasVisualDetail(Bitmap bitmap)
{
    if (bitmap.Width <= 0 || bitmap.Height <= 0) return false;
    var colors = new HashSet<int>();
    var minimumLuminance = 1d;
    var maximumLuminance = 0d;
    var stepX = Math.Max(1, bitmap.Width / 64);
    var stepY = Math.Max(1, bitmap.Height / 64);
    for (var y = 0; y < bitmap.Height; y += stepY)
    {
        for (var x = 0; x < bitmap.Width; x += stepX)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.A < 32) continue;
            colors.Add(((pixel.R >> 4) << 8) | ((pixel.G >> 4) << 4) | (pixel.B >> 4));
            var luminance = RelativeLuminance(pixel);
            minimumLuminance = Math.Min(minimumLuminance, luminance);
            maximumLuminance = Math.Max(maximumLuminance, luminance);
        }
    }
    return colors.Count >= 8 && maximumLuminance - minimumLuminance >= 0.12;
}

(bool Focus, bool TextTransfer) VerifyStartPageSearchRoutesToOmnibox()
{
    Exception? error = null;
    var focus = false;
    var textTransfer = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new MainForm(startBrowserOnShown: false)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false
            };
            form.Show();
            form.PrepareChromePreviewForTesting();
            form.PerformLayout();
            Application.DoEvents();

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var addressBar = typeof(MainForm).GetField("addressBar", flags)?.GetValue(form) as TextBox;
            var focusStartPageSearch = typeof(MainForm).GetMethod("FocusStartPageOrAddressBar", flags);
            var startPage = GetDescendants(form).OfType<NativeStartPage>().SingleOrDefault();
            if (addressBar is null || focusStartPageSearch is null || startPage is null)
            {
                return;
            }

            focusStartPageSearch.Invoke(form, null);
            Application.DoEvents();
            focus = addressBar.Focused && !startPage.SearchBar.InputControl.Focused;

            startPage.SearchBar.InputControl.Focus();
            startPage.SearchText = "bunny videos";
            Application.DoEvents();
            textTransfer = addressBar.Focused
                && addressBar.Text == "bunny videos"
                && addressBar.SelectionStart == addressBar.TextLength
                && addressBar.SelectionLength == 0
                && startPage.SearchText.Length == 0;
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (error is not null)
    {
        Console.Error.WriteLine($"Start-page omnibox routing probe failed: {error}");
    }
    return (error is null && focus, error is null && textTransfer);
}

(bool Valid, int Dpi) VerifySmartSearchLayoutRegression()
{
    Exception? error = null;
    var layoutValid = false;
    var observedDpi = 96;
    var thread = new Thread(() =>
    {
        try
        {
            using var host = new Form
            {
                AutoScaleMode = AutoScaleMode.Dpi,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false
            };
            using var bar = new SmartSearchBar
            {
                Dock = DockStyle.None,
                Location = new Point(8, 8)
            };
            host.Controls.Add(bar);
            host.Show();
            observedDpi = Math.Max(96, bar.DeviceDpi);

            var logicalWidths = new[]
            {
                900, 760, 650, 649, 560, 559, 500, 499, 420, 320, 260,
                320, 499, 500, 559, 560, 649, 650, 760, 900
            };
            layoutValid = true;
            foreach (var logicalWidth in logicalWidths)
            {
                var physicalWidth = ScaleForDpi(logicalWidth, observedDpi);
                var physicalHeight = ScaleForDpi(72, observedDpi);
                host.ClientSize = new Size(physicalWidth + 16, physicalHeight + 16);
                // Exercise the bar's own responsive paths. Its production parent supplies
                // the viewport-specific minimum; retaining the constructor's wide minimum
                // here would silently clamp every compact test case to 620 logical pixels.
                bar.MinimumSize = Size.Empty;
                bar.Size = new Size(physicalWidth, physicalHeight);
                host.PerformLayout();
                bar.PerformLayout();
                Application.DoEvents();

                foreach (var inputValue in new[] { string.Empty, "mishafans.example" })
                {
                    bar.Text = inputValue;
                    bar.PerformLayout();
                    Application.DoEvents();
                    var emptyPresentation = inputValue.Length == 0;

                    var descendants = GetDescendants(bar).ToArray();
                    var input = descendants.OfType<TextBox>().SingleOrDefault(control =>
                        control.AccessibleName == "Search or open a website");
                    var icon = descendants.SingleOrDefault(control =>
                        control.AccessibleName == "Search input"
                        && control.AccessibleRole == AccessibleRole.Graphic);
                    var provider = descendants.OfType<Button>().SingleOrDefault(control =>
                        control.AccessibleName?.StartsWith("Search engine:", StringComparison.Ordinal) == true);
                    var action = descendants.OfType<Button>().SingleOrDefault(control =>
                        control != provider);
                    var hint = descendants.OfType<Label>().SingleOrDefault(control =>
                        control.AccessibleName == "Keyboard shortcut Ctrl plus K");
                    var mode = descendants.OfType<Label>().SingleOrDefault(control =>
                        control != hint);

                    if (input is null
                        || icon is null
                        || action is null
                        || provider is null
                        || mode is null
                        || hint is null)
                    {
                        Console.Error.WriteLine($"Smart search controls were missing at {logicalWidth}px / {observedDpi} DPI.");
                        layoutValid = false;
                        continue;
                    }

                    var compact = bar.Width < ScaleForDpi(500, observedDpi);
                    var medium = bar.Width < ScaleForDpi(650, observedDpi);
                    var requestedSizeApplied = Math.Abs(bar.Width - physicalWidth) <= 1
                        && Math.Abs(bar.Height - physicalHeight) <= 1;
                    var inputBounds = BoundsWithin(bar, input);
                    var iconBounds = BoundsWithin(bar, icon);
                    var actionBounds = BoundsWithin(bar, action);
                    var providerBounds = BoundsWithin(bar, provider);
                    var modeBounds = BoundsWithin(bar, mode);
                    var hintBounds = BoundsWithin(bar, hint);
                    var visibleControls = descendants.Where(control => control.Visible).ToArray();
                    var visibleInsideBar = visibleControls.All(control =>
                        bar.ClientRectangle.Contains(BoundsWithin(bar, control)));
                    var singleVisibleSearchGlyph = visibleControls.Count(control =>
                        control.AccessibleName == "Search input"
                        && control.AccessibleRole == AccessibleRole.Graphic) == 1;
                    var visibilityValid = provider.Visible == !compact
                        && mode.Visible == !compact
                        && hint.Visible == (!compact && !medium && emptyPresentation);
                    var presentationValid = emptyPresentation
                        ? bar.Classification == SmartSearchClassification.Empty
                            && !bar.CanSubmit
                            && !action.Enabled
                            && action.AccessibleName == "Go"
                            && mode.AccessibleName == "Search or open a site"
                        : bar.Classification == SmartSearchClassification.Address
                            && bar.CanSubmit
                            && action.Enabled
                            && action.AccessibleName == "Open website"
                            && mode.AccessibleName == "Open website";
                    var hintTextFits = !hint.Visible
                        || hint.GetPreferredSize(Size.Empty).Width <= hint.ClientSize.Width;
                    var geometryValid = visibleInsideBar
                        && singleVisibleSearchGlyph
                        && hintTextFits
                        && requestedSizeApplied
                        && inputBounds.Width >= ScaleForDpi(72, observedDpi)
                        && actionBounds.Width >= ScaleForDpi(compact ? 40 : 88, observedDpi)
                        && !Overlaps(iconBounds, inputBounds)
                        && !Overlaps(iconBounds, actionBounds)
                        && !Overlaps(inputBounds, actionBounds)
                        && (!provider.Visible
                            || (!Overlaps(providerBounds, actionBounds)
                                && !Overlaps(providerBounds, inputBounds)))
                        && (!mode.Visible
                            || (!Overlaps(modeBounds, actionBounds)
                                && !Overlaps(modeBounds, providerBounds)))
                        && (!hint.Visible
                            || (!Overlaps(hintBounds, providerBounds)
                                && !Overlaps(hintBounds, actionBounds)
                                && !Overlaps(hintBounds, inputBounds)));

                    using var renderedBar = new Bitmap(bar.Width, bar.Height);
                    bar.DrawToBitmap(renderedBar, bar.ClientRectangle);
                    var snapshotValid = visibilityValid && presentationValid && geometryValid;
                    if (!snapshotValid)
                    {
                        Console.Error.WriteLine(
                            $"Smart search layout failed at {logicalWidth}px / {observedDpi} DPI "
                            + $"with {(emptyPresentation ? "empty" : "active")} input: "
                            + $"bar={bar.ClientRectangle}; input={inputBounds}; icon={iconBounds}; "
                            + $"action={actionBounds}; provider={providerBounds}; mode={modeBounds}; hint={hintBounds}; "
                            + $"hintPreferred={hint.GetPreferredSize(Size.Empty).Width}; compact={compact}; medium={medium}; "
                            + $"requested={physicalWidth}x{physicalHeight}; actual={bar.Width}x{bar.Height}; "
                            + $"inside={visibleInsideBar}; glyphs={singleVisibleSearchGlyph}.");
                    }
                    layoutValid &= snapshotValid;
                }
                bar.Text = string.Empty;
            }
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (error is not null)
    {
        Console.Error.WriteLine($"Smart search layout regression probe failed: {error}");
    }
    return (error is null && layoutValid, observedDpi);
}

int ScaleForDpi(int logicalPixels, int dpi)
{
    return Math.Max(1, (logicalPixels * dpi + 48) / 96);
}

Rectangle BoundsWithin(Control ancestor, Control descendant)
{
    return ancestor.RectangleToClient(descendant.RectangleToScreen(descendant.ClientRectangle));
}

bool Overlaps(Rectangle first, Rectangle second)
{
    var intersection = Rectangle.Intersect(first, second);
    return intersection.Width > 0 && intersection.Height > 0;
}

bool RectanglesDoNotOverlap(IEnumerable<Rectangle> rectangles)
{
    var items = rectangles.Where(item => !item.IsEmpty).ToArray();
    for (var first = 0; first < items.Length; first++)
    {
        for (var second = first + 1; second < items.Length; second++)
        {
            if (Overlaps(items[first], items[second])) return false;
        }
    }
    return true;
}

bool SmartSearchDisabledPaintIsOpaque()
{
    var bindingFlags = System.Reflection.BindingFlags.Static
        | System.Reflection.BindingFlags.NonPublic;
    var flatten = typeof(SmartSearchBar).GetMethod("FlattenColor", bindingFlags);
    var blend = typeof(SmartSearchBar).GetMethod("BlendOpaque", bindingFlags);
    var background = Color.FromArgb(255, 38, 14, 34);
    var translucent = Color.FromArgb(90, 218, 102, 185);
    return flatten?.Invoke(null, [translucent, background]) is Color flattened
        && blend?.Invoke(null, [flattened, background, 116]) is Color disabled
        && flattened.A == 255
        && disabled.A == 255;
}

bool SmartSearchPaintSurfacesAreOpaque()
{
    var bindingFlags = System.Reflection.BindingFlags.Static
        | System.Reflection.BindingFlags.NonPublic;
    var fieldNames = new[]
    {
        "SurfaceColor",
        "SurfaceColorSecondary",
        "InputSurfaceColor",
        "PillColor",
        "PillHoverColor",
        "PillPressedColor"
    };
    return fieldNames.All(name =>
        typeof(SmartSearchBar).GetField(name, bindingFlags)?.GetValue(null) is Color color
        && color.A == 255);
}

bool StartPageInteractiveSurfacesAreOpaque()
{
    var bindingFlags = System.Reflection.BindingFlags.Static
        | System.Reflection.BindingFlags.NonPublic;
    return new[] { "ChipColor", "ChipHoverColor", "ChipPressedColor" }.All(name =>
        typeof(NativeStartPage).GetField(name, bindingFlags)?.GetValue(null) is Color color
        && color.A == 255);
}

bool StartPageOuterCardIsTransparent()
{
    var source = ReadRepositorySource("desktop", "NativeStartPage.cs");
    return source.Contains("card.FillColor = Color.Transparent;", StringComparison.Ordinal)
        && source.Contains("card.FillColorSecondary = Color.Transparent;", StringComparison.Ordinal)
        && source.Contains("card.BorderColor = Color.Transparent;", StringComparison.Ordinal)
        && source.Contains("card.HighlightColor = Color.Transparent;", StringComparison.Ordinal)
        && source.Split("PaintBackdropSlice(this, e)", StringSplitOptions.None).Length - 1 >= 3;
}

(int FirstInterrupted, int SecondInterrupted, bool Terminal, bool HonestReason,
    bool GuardWasActive, bool NoActiveRecords, bool TabCountersCleared,
    bool RestartGuardReleased) VerifyBrowserProcessDownloadCleanup()
{
    Exception? error = null;
    var result = (
        FirstInterrupted: -1,
        SecondInterrupted: -1,
        Terminal: false,
        HonestReason: false,
        GuardWasActive: false,
        NoActiveRecords: false,
        TabCountersCleared: false,
        RestartGuardReleased: false);
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new MainForm(startBrowserOnShown: false);
            form.PrepareLoadedChromePreviewForTesting();

            const System.Reflection.BindingFlags instanceFlags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            const System.Reflection.BindingFlags nestedFlags =
                System.Reflection.BindingFlags.NonPublic;
            var downloadType = typeof(MainForm).GetNestedType("DownloadEntry", nestedFlags)
                ?? throw new InvalidOperationException("DownloadEntry was not found.");
            var constructor = downloadType.GetConstructors(
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic)
                .Single();
            var download = constructor.Invoke([
                "probe.bin",
                Path.Combine(Path.GetTempPath(), "probe.bin"),
                "https://example.test/probe.bin",
                DateTime.Now,
                "Downloading"
            ]);
            var downloadList = typeof(MainForm).GetField("downloads", instanceFlags)?.GetValue(form)
                as System.Collections.IList
                ?? throw new InvalidOperationException("The download list was not found.");
            downloadList.Add(download);

            var restartGuard = typeof(MainForm).GetMethod("IsBrowserEngineRestartUnsafe", instanceFlags)
                ?? throw new InvalidOperationException("The restart guard was not found.");
            var cleanup = typeof(MainForm).GetMethod(
                    "FailActiveDownloadsAfterBrowserProcessExit",
                    instanceFlags)
                ?? throw new InvalidOperationException("The crash cleanup was not found.");
            var guardWasActive = restartGuard.Invoke(form, null) is true;
            var firstInterrupted = cleanup.Invoke(form, null) is int first ? first : -1;
            var secondInterrupted = cleanup.Invoke(form, null) is int second ? second : -1;

            var terminalProperty = downloadType.GetProperty("IsTerminal")
                ?? throw new InvalidOperationException("Download terminal state was not found.");
            var reasonProperty = downloadType.GetProperty("InterruptReason")
                ?? throw new InvalidOperationException("Download interrupt reason was not found.");
            var terminal = terminalProperty.GetValue(download) is true;
            var honestReason = reasonProperty.GetValue(download) as string == "Browser process stopped";
            var noActiveRecords = downloadList.Cast<object>()
                .All(item => terminalProperty.GetValue(item) is true);

            var tabList = typeof(MainForm).GetField("tabs", instanceFlags)?.GetValue(form)
                as System.Collections.IList
                ?? throw new InvalidOperationException("The tab list was not found.");
            var tabCountersCleared = tabList.Cast<object>().All(tab =>
                tab.GetType().GetProperty("ActiveDownloads")?.GetValue(tab) is 0);
            var restartGuardReleased = restartGuard.Invoke(form, null) is false;
            result = (
                firstInterrupted,
                secondInterrupted,
                terminal,
                honestReason,
                guardWasActive,
                noActiveRecords,
                tabCountersCleared,
                restartGuardReleased);
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (error is not null)
    {
        Console.Error.WriteLine($"Browser-process download cleanup probe failed: {error}");
    }
    return result;
}

bool SmartSearchIconHasNoArtworkOverlay()
{
    var source = ReadRepositorySource("desktop", "SmartSearchBar.cs");
    return !source.Contains("StartPageArtwork.DrawCommandTexture", StringComparison.Ordinal)
        && source.Contains("BackColor = FlattenColor(SurfaceColor, NativeUiTheme.Chrome);", StringComparison.Ordinal);
}

bool CanPrepareRendererFreeBrowser()
{
    Exception? error = null;
    var rendererFree = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new MainForm(startBrowserOnShown: false);
            form.PrepareChromePreviewForTesting();
            rendererFree = form.UsesRendererFreeStartPageForTesting;
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return error is null && rendererFree;
}

(bool Reordered, bool HitTesting, bool GhostFeedback) VerifyTabDragReordering()
{
    Exception? error = null;
    var reordered = false;
    var hitTesting = false;
    var ghostFeedback = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new MainForm(startBrowserOnShown: false)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false
            };
            form.Show();
            form.PrepareLoadedChromePreviewForTesting();
            form.PerformLayout();
            Application.DoEvents();

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var tabStrip = typeof(MainForm).GetField("tabStrip", flags)?.GetValue(form) as FlowLayoutPanel;
            var tabArea = typeof(MainForm).GetField("tabArea", flags)?.GetValue(form) as Panel;
            var tabDragGhost = typeof(MainForm).GetField("tabDragGhost", flags)?.GetValue(form) as Control;
            var isCaptionPoint = typeof(MainForm).GetMethod("IsCaptionPoint", flags);
            var onMouseDown = typeof(Control).GetMethod("OnMouseDown", flags);
            var onMouseMove = typeof(Control).GetMethod("OnMouseMove", flags);
            var onMouseUp = typeof(Control).GetMethod("OnMouseUp", flags);
            var headers = tabStrip?.Controls
                .Cast<Control>()
                .OrderBy(control => control.Left)
                .ToArray() ?? [];
            if (tabStrip is null
                || tabArea is null
                || tabDragGhost is null
                || isCaptionPoint is null
                || onMouseDown is null
                || onMouseMove is null
                || onMouseUp is null
                || headers.Length < 3)
            {
                return;
            }

            var initialHeaderNames = headers.Select(control => control.AccessibleName).ToArray();
            var source = headers[0];
            var target = headers[2];
            var sourcePoint = new Point(source.Width / 2, source.Height / 2);
            var sourceScreenPoint = source.PointToScreen(sourcePoint);
            var blankScreenPoint = tabArea.PointToScreen(
                new Point(Math.Max(0, tabArea.ClientSize.Width - 2), tabArea.ClientSize.Height / 2));
            hitTesting = isCaptionPoint.Invoke(
                    form,
                    [form.PointToClient(sourceScreenPoint), sourceScreenPoint]) is false
                && isCaptionPoint.Invoke(
                    form,
                    [form.PointToClient(blankScreenPoint), blankScreenPoint]) is true;

            var targetScreenPoint = target.PointToScreen(
                new Point(Math.Max(1, target.Width - 2), target.Height / 2));
            var targetPointRelativeToSource = source.PointToClient(targetScreenPoint);
            onMouseDown.Invoke(
                source,
                [new MouseEventArgs(MouseButtons.Left, 1, sourcePoint.X, sourcePoint.Y, 0)]);
            onMouseMove.Invoke(
                source,
                [new MouseEventArgs(
                    MouseButtons.Left,
                    0,
                    targetPointRelativeToSource.X,
                    targetPointRelativeToSource.Y,
                    0)]);
            Application.DoEvents();
            var ghostVisibleDuringMove = tabDragGhost.Visible
                && tabDragGhost.Width >= source.Width
                && tabDragGhost.Height >= source.Height
                && tabDragGhost.AccessibleName?.StartsWith("Dragging ", StringComparison.Ordinal) == true;
            onMouseUp.Invoke(
                source,
                [new MouseEventArgs(
                    MouseButtons.Left,
                    1,
                    targetPointRelativeToSource.X,
                    targetPointRelativeToSource.Y,
                    0)]);
            form.PerformLayout();
            Application.DoEvents();
            ghostFeedback = ghostVisibleDuringMove && !tabDragGhost.Visible;

            var reorderedHeaders = tabStrip.Controls
                .Cast<Control>()
                .OrderBy(control => control.Left)
                .Select(control => control.AccessibleName)
                .ToArray();
            reordered = reorderedHeaders.Length == initialHeaderNames.Length
                && reorderedHeaders[0] == initialHeaderNames[1]
                && reorderedHeaders[1] == initialHeaderNames[2]
                && reorderedHeaders[2] == initialHeaderNames[0]
                && reorderedHeaders.Skip(3).SequenceEqual(initialHeaderNames.Skip(3));
            if (!reordered)
            {
                Console.Error.WriteLine(
                    $"Tab drag probe failed: initial={string.Join(" | ", initialHeaderNames)}; "
                    + $"order={string.Join(" | ", reorderedHeaders)}");
            }
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return (
        error is null && reordered,
        error is null && hitTesting,
        error is null && ghostFeedback);
}

(bool AudibleGlyph, bool MutedGlyph, bool RefreshWired) VerifyTabAudioIndicator()
{
    var source = ReadRepositorySource("desktop", "MainForm.cs");
    var audioEventStart = source.IndexOf(
        "core.IsDocumentPlayingAudioChanged +=",
        StringComparison.Ordinal);
    var audioEventEnd = audioEventStart < 0
        ? -1
        : source.IndexOf("core.LaunchingExternalUriScheme +=", audioEventStart, StringComparison.Ordinal);
    var audioRefresh = audioEventStart < 0
        ? -1
        : source.IndexOf("UpdateTabHeader(tab);", audioEventStart, StringComparison.Ordinal);
    var updateHeaderStart = source.IndexOf("private void UpdateTabHeader(BrowserTab tab)", StringComparison.Ordinal);
    var updateHeaderEnd = updateHeaderStart < 0
        ? -1
        : source.IndexOf("private bool IsTabMuted", updateHeaderStart, StringComparison.Ordinal);
    var audiblePassedToHeader = updateHeaderStart >= 0
        && updateHeaderEnd > updateHeaderStart
        && source.IndexOf("tab.IsAudible,", updateHeaderStart, StringComparison.Ordinal) is var audibleArgument
        && audibleArgument > updateHeaderStart
        && audibleArgument < updateHeaderEnd;
    var refreshWired = audioEventStart >= 0
        && audioEventEnd > audioEventStart
        && audioRefresh > audioEventStart
        && audioRefresh < audioEventEnd
        && audiblePassedToHeader;

    Exception? error = null;
    var audibleGlyph = false;
    var mutedGlyph = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new MainForm(startBrowserOnShown: false)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false
            };
            form.Show();
            form.PrepareLoadedChromePreviewForTesting();
            form.PerformLayout();
            Application.DoEvents();

            const System.Reflection.BindingFlags privateFlags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            const System.Reflection.BindingFlags publicFlags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
            var tabStrip = typeof(MainForm).GetField("tabStrip", privateFlags)?.GetValue(form) as FlowLayoutPanel;
            var header = tabStrip?.Controls.Cast<Control>().FirstOrDefault();
            var setState = header?.GetType().GetMethod("SetState", publicFlags);
            if (header is null || setState is null || header.Width < 24 || header.Height < 16)
            {
                return;
            }

            Bitmap Render(bool audible, bool muted)
            {
                setState.Invoke(
                    header,
                    [string.Empty, false, false, false, false, false, audible, muted, false]);
                header.PerformLayout();
                header.Invalidate();
                header.Update();
                Application.DoEvents();
                var rendered = new Bitmap(header.Width, header.Height);
                header.DrawToBitmap(rendered, header.ClientRectangle);
                return rendered;
            }

            using var silent = Render(audible: false, muted: false);
            using var audible = Render(audible: true, muted: false);
            var audibleDescription = header.AccessibleDescription ?? string.Empty;
            var audiblePadding = header.Padding.Left;
            using var muted = Render(audible: true, muted: true);
            var mutedDescription = header.AccessibleDescription ?? string.Empty;

            var glyphArea = new Rectangle(4, 0, Math.Min(20, header.Width - 4), header.Height);
            var audibleDifference = CountPixelDifferences(silent, audible, glyphArea);
            var mutedDifference = CountPixelDifferences(audible, muted, glyphArea);
            audibleGlyph = audibleDifference >= 8
                && audiblePadding >= 24
                && audibleDescription.Contains("playing audio", StringComparison.OrdinalIgnoreCase);
            mutedGlyph = mutedDifference >= 8
                && mutedDescription.Contains("muted", StringComparison.OrdinalIgnoreCase)
                && !mutedDescription.Contains("playing audio", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (error is not null)
    {
        Console.Error.WriteLine($"Tab audio-indicator probe failed: {error}");
    }
    return (error is null && audibleGlyph, error is null && mutedGlyph, refreshWired);
}

int CountPixelDifferences(Bitmap first, Bitmap second, Rectangle area)
{
    var bounds = Rectangle.Intersect(
        area,
        Rectangle.Intersect(
            new Rectangle(Point.Empty, first.Size),
            new Rectangle(Point.Empty, second.Size)));
    var differences = 0;
    for (var y = bounds.Top; y < bounds.Bottom; y++)
    {
        for (var x = bounds.Left; x < bounds.Right; x++)
        {
            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) differences++;
        }
    }
    return differences;
}

bool WindowCaptionDragUsesNativeMove()
{
    var source = ReadRepositorySource("desktop", "MainForm.cs");
    return source.Contains("titleBar.MouseDown += BeginWindowDrag;", StringComparison.Ordinal)
        && source.Contains("appMark.MouseDown += BeginWindowDrag;", StringComparison.Ordinal)
        && source.Contains("tabArea.MouseDown += BeginWindowDrag;", StringComparison.Ordinal)
        && source.Contains("tabStrip.MouseDown += BeginWindowDrag;", StringComparison.Ordinal)
        && source.Contains("NativeMethods.BeginWindowMove(Handle);", StringComparison.Ordinal)
        && source.Contains(
            "SendMessage(window, WmNcLeftButtonDown, (IntPtr)HtCaption, IntPtr.Zero)",
            StringComparison.Ordinal);
}

(bool Valid, int Width, bool Scrollable) VerifyReservedWindowDragSpace()
{
    Exception? error = null;
    var valid = false;
    var reservedWidth = 0;
    var scrollable = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new MainForm(startBrowserOnShown: false)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false
            };
            form.Show();
            form.PrepareLoadedChromePreviewForTesting();
            form.ClientSize = new Size(
                ScaleForDpi(560, form.DeviceDpi),
                ScaleForDpi(500, form.DeviceDpi));
            form.PerformLayout();
            Application.DoEvents();

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var tabArea = typeof(MainForm).GetField("tabArea", flags)?.GetValue(form) as Panel;
            var tabStrip = typeof(MainForm).GetField("tabStrip", flags)?.GetValue(form) as FlowLayoutPanel;
            var newTabButton = typeof(MainForm).GetField("newTabButton", flags)?.GetValue(form) as Button;
            var isCaptionPoint = typeof(MainForm).GetMethod("IsCaptionPoint", flags);
            if (tabArea is null || tabStrip is null || newTabButton is null || isCaptionPoint is null)
            {
                return;
            }

            reservedWidth = tabArea.ClientSize.Width - newTabButton.Right;
            var expectedWidth = ScaleForDpi(48, form.DeviceDpi);
            var gripPoint = new Point(
                newTabButton.Right + Math.Max(1, reservedWidth / 2),
                Math.Max(1, tabArea.ClientSize.Height / 2));
            var screenPoint = tabArea.PointToScreen(gripPoint);
            var captionHit = isCaptionPoint.Invoke(
                form,
                [form.PointToClient(screenPoint), screenPoint]) is true;
            valid = Math.Abs(reservedWidth - expectedWidth) <= 2
                && tabStrip.Right == newTabButton.Left
                && newTabButton.Right < tabArea.ClientSize.Width
                && captionHit;
            scrollable = tabStrip.AutoScroll
                && tabStrip.AutoScrollMinSize.Width > tabStrip.ClientSize.Width;
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (error is not null)
    {
        Console.Error.WriteLine($"Reserved window-drag area probe failed: {error}");
    }
    return (error is null && valid, reservedWidth, error is null && scrollable);
}

(bool RightSideRestored, bool OffscreenRecovered, bool PresentationRestored) VerifySavedWindowPlacement()
{
    var primaryArea = new Rectangle(0, 0, 1920, 1040);
    var secondaryArea = new Rectangle(1920, 0, 1920, 1040);
    var rightSidePlacement = new SavedWindowPlacement(2880, 120, 900, 800);
    var restoredRightSide = MainForm.ResolveRestoredWindowBounds(
        rightSidePlacement,
        [primaryArea, secondaryArea],
        new Size(480, 420));
    var rightSideRestored = restoredRightSide == new Rectangle(2880, 120, 900, 800);

    var recoveredBounds = MainForm.ResolveRestoredWindowBounds(
        rightSidePlacement,
        [primaryArea],
        new Size(480, 420));
    var offscreenRecovered = primaryArea.Contains(recoveredBounds)
        && recoveredBounds.Size == new Size(rightSidePlacement.Width, rightSidePlacement.Height);

    Exception? error = null;
    var presentationRestored = false;
    string? presentationDiagnostic = null;
    var thread = new Thread(() =>
    {
        try
        {
            var workingArea = Screen.PrimaryScreen?.WorkingArea ?? primaryArea;
            var width = Math.Min(960, workingArea.Width);
            var height = Math.Min(720, workingArea.Height);
            var placement = new SavedWindowPlacement(
                workingArea.Left + Math.Max(0, workingArea.Width - width),
                workingArea.Top + Math.Max(0, (workingArea.Height - height) / 2),
                width,
                height);

            using var maximizedForm = new MainForm(
                startBrowserOnShown: false,
                previewState: new BrowserState
                {
                    WindowPlacement = placement with
                    {
                        Presentation = SavedWindowPresentation.Maximized
                    }
                });
            var capturedMaximized = maximizedForm.CaptureWindowPlacementForTesting();

            using var fullScreenForm = new MainForm(
                startBrowserOnShown: false,
                previewState: new BrowserState
                {
                    WindowPlacement = placement with
                    {
                        Presentation = SavedWindowPresentation.FullScreen,
                        RestoreMaximizedAfterFullScreen = true
                    }
                });
            var capturedFullScreen = fullScreenForm.CaptureWindowPlacementForTesting();
            presentationRestored = maximizedForm.WindowState == FormWindowState.Maximized
                && capturedMaximized.Presentation == SavedWindowPresentation.Maximized
                && capturedMaximized.X == placement.X
                && capturedMaximized.Y == placement.Y
                && capturedMaximized.Width == placement.Width
                && capturedMaximized.Height == placement.Height
                && capturedFullScreen.Presentation == SavedWindowPresentation.FullScreen
                && capturedFullScreen.RestoreMaximizedAfterFullScreen
                && capturedFullScreen.X == placement.X
                && capturedFullScreen.Y == placement.Y
                && capturedFullScreen.Width == placement.Width
                && capturedFullScreen.Height == placement.Height;
            if (!presentationRestored)
            {
                presentationDiagnostic = $"windowState={maximizedForm.WindowState}; "
                    + $"expected={placement}; maximized={capturedMaximized}; "
                    + $"fullscreen={capturedFullScreen}";
            }
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (error is not null)
    {
        Console.Error.WriteLine($"Saved window-placement probe failed: {error}");
    }
    else if (presentationDiagnostic is not null)
    {
        Console.Error.WriteLine($"Saved window-placement probe mismatch: {presentationDiagnostic}");
    }

    return (rightSideRestored, offscreenRecovered, error is null && presentationRestored);
}

bool ResourceModeMenuRemainsNested()
{
    Exception? error = null;
    var valid = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new MainForm(startBrowserOnShown: false);
            var flags = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic;
            var appMenu = typeof(MainForm).GetField("appMenu", flags)?.GetValue(form) as ContextMenuStrip;
            var resourceMenu = typeof(MainForm).GetField("resourceModeMenu", flags)?.GetValue(form) as ToolStripMenuItem;
            var standard = typeof(MainForm).GetField("memorySaverMenuItem", flags)?.GetValue(form) as ToolStripMenuItem;
            var ultra = typeof(MainForm).GetField("ultraLightMenuItem", flags)?.GetValue(form) as ToolStripMenuItem;
            valid = appMenu is not null
                && resourceMenu is not null
                && standard is not null
                && ultra is not null
                && appMenu.Items.Contains(resourceMenu)
                && !appMenu.Items.Contains(standard)
                && !appMenu.Items.Contains(ultra)
                && resourceMenu.DropDownItems.Count == 3
                && resourceMenu.DropDownItems.Contains(standard)
                && resourceMenu.DropDownItems.Contains(ultra);
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return error is null && valid;
}

(bool FirstClick, bool LaterClick) VerifyAddressBarFirstClickSelection()
{
    Exception? error = null;
    var firstClick = false;
    var laterClick = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new MainForm(startBrowserOnShown: false)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false
            };
            form.Show();
            form.PrepareLoadedChromePreviewForTesting();
            form.PerformLayout();
            Application.DoEvents();

            const System.Reflection.BindingFlags privateFlags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var addressBar = typeof(MainForm).GetField("addressBar", privateFlags)?.GetValue(form) as TextBox;
            var backButton = typeof(MainForm).GetField("backButton", privateFlags)?.GetValue(form) as Control;
            var wndProc = addressBar?.GetType().GetMethod("WndProc", privateFlags);
            if (addressBar is null || backButton is null || wndProc is null)
            {
                return;
            }

            backButton.Focus();
            Application.DoEvents();
            addressBar.Text = "https://example.test/a/long/path?query=replace-me";
            addressBar.SelectionStart = addressBar.TextLength;
            addressBar.SelectionLength = 0;

            void ClickAddressBar(int x)
            {
                const int wmLeftButtonDown = 0x0201;
                const int wmLeftButtonUp = 0x0202;
                var y = Math.Max(1, addressBar.ClientSize.Height / 2);
                var packedPoint = (y << 16) | (Math.Max(1, x) & 0xFFFF);
                object[] down =
                [
                    Message.Create(addressBar.Handle, wmLeftButtonDown, (IntPtr)1, (IntPtr)packedPoint)
                ];
                object[] up =
                [
                    Message.Create(addressBar.Handle, wmLeftButtonUp, IntPtr.Zero, (IntPtr)packedPoint)
                ];
                wndProc.Invoke(addressBar, down);
                wndProc.Invoke(addressBar, up);
                Application.DoEvents();
            }

            ClickAddressBar(Math.Max(2, addressBar.ClientSize.Width - 4));
            firstClick = addressBar.Focused
                && addressBar.SelectionStart == 0
                && addressBar.SelectionLength == addressBar.TextLength;

            addressBar.SelectionStart = Math.Min(8, addressBar.TextLength);
            addressBar.SelectionLength = 0;
            ClickAddressBar(Math.Max(2, addressBar.ClientSize.Width / 3));
            laterClick = addressBar.Focused && addressBar.SelectionLength < addressBar.TextLength;
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (error is not null)
    {
        Console.Error.WriteLine($"Address-bar first-click selection probe failed: {error}");
    }
    return (error is null && firstClick, error is null && laterClick);
}

(bool Layout, bool Text, bool Paint, bool AddressBarPreservesCaret) VerifyLoadedToolbarRegression()
{
    Exception? error = null;
    var layoutValid = false;
    var textValid = false;
    var paintValid = false;
    var addressBarPreservesCaret = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var form = new MainForm(startBrowserOnShown: false)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false
            };
            form.Show();
            form.PrepareLoadedChromePreviewForTesting();
            var addressBar = GetDescendants(form).OfType<TextBox>().SingleOrDefault(control =>
                control.AccessibleName == "Address and search bar");
            var onClick = typeof(Control).GetMethod(
                "OnClick",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (addressBar is not null && onClick is not null)
            {
                addressBar.Text = "https://example.test/search?q=tests";
                addressBar.SelectionStart = 8;
                addressBar.SelectionLength = 0;
                onClick.Invoke(addressBar, [EventArgs.Empty]);
                addressBarPreservesCaret = addressBar.SelectionStart == 8
                    && addressBar.SelectionLength == 0;
            }
            var dpi = Math.Max(96, form.DeviceDpi);
            var logicalWidths = new[]
            {
                1917, 1280, 1024, 960, 768, 640, 560, 520, 500, 480,
                500, 520, 560, 640, 768, 960, 1024, 1280, 1917
            };
            layoutValid = true;
            foreach (var logicalWidth in logicalWidths)
            {
                var requestedWidth = ScaleForDpi(logicalWidth, dpi);
                form.ClientSize = new Size(requestedWidth, ScaleForDpi(420, dpi));
                form.PerformLayout();
                Application.DoEvents();
                var snapshot = form.GetToolbarLayoutSnapshotForTesting();
                var lastNavigation = snapshot.HomeVisible ? snapshot.Home : snapshot.Reload;
                var firstRightControl = snapshot.StatusVisible
                    ? snapshot.Status
                    : snapshot.DownloadsVisible ? snapshot.Downloads : snapshot.Menu;
                var outerControls = new[]
                {
                    snapshot.Back,
                    snapshot.Forward,
                    snapshot.Reload,
                    snapshot.Home,
                    snapshot.Omnibox,
                    snapshot.Status,
                    snapshot.Downloads,
                    snapshot.Menu
                }.Where(bounds => !bounds.IsEmpty).ToArray();
                var outerControlsContained = outerControls.All(bounds =>
                    bounds.Left >= 0
                    && bounds.Top >= 0
                    && bounds.Right <= snapshot.ToolbarWidth
                    && bounds.Width > 0
                    && bounds.Height > 0);

                var descendants = GetDescendants(form).ToArray();
                var address = descendants.OfType<TextBox>().SingleOrDefault(control =>
                    control.AccessibleName == "Address and search bar");
                var omniboxLayout = address?.Parent;
                var innerButtons = omniboxLayout?.Controls
                    .OfType<Button>()
                    .Where(button => button.Visible)
                    .OrderBy(button => button.Left)
                    .ToArray() ?? [];
                var innerGeometryValid = address is not null
                    && omniboxLayout is not null
                    && innerButtons.Length == 2
                    && omniboxLayout.ClientRectangle.Contains(address.Bounds)
                    && innerButtons.All(button => omniboxLayout.ClientRectangle.Contains(button.Bounds))
                    && OrderedWithoutOverlap(innerButtons[0].Bounds, address.Bounds)
                    && OrderedWithoutOverlap(address.Bounds, innerButtons[1].Bounds)
                    && RectanglesDoNotOverlap(
                        innerButtons.Select(button => button.Bounds).Append(address.Bounds));
                var snapshotValid = OrderedWithoutOverlap(snapshot.Back, snapshot.Forward)
                    && OrderedWithoutOverlap(snapshot.Forward, snapshot.Reload)
                    && (!snapshot.HomeVisible || OrderedWithoutOverlap(snapshot.Reload, snapshot.Home))
                    && OrderedWithoutOverlap(lastNavigation, snapshot.Omnibox)
                    && OrderedWithoutOverlap(snapshot.Omnibox, firstRightControl)
                    && (!snapshot.StatusVisible || OrderedWithoutOverlap(snapshot.Status, snapshot.Downloads))
                    && (!snapshot.DownloadsVisible || OrderedWithoutOverlap(snapshot.Downloads, snapshot.Menu))
                    && Math.Abs(snapshot.ClientWidth - requestedWidth) <= 1
                    && outerControlsContained
                    && RectanglesDoNotOverlap(outerControls)
                    && innerGeometryValid
                    && snapshot.Omnibox.Width >= snapshot.MinimumOmniboxWidth - 1
                    && snapshot.Menu.Right <= snapshot.ToolbarWidth
                    && snapshot.DownloadsVisible
                    && snapshot.StatusText.Contains("13 blocked", StringComparison.Ordinal)
                    && snapshot.StatusText.Contains("1 downloading", StringComparison.Ordinal)
                    && snapshot.StatusAccessibleName.Equals("Browser status: Ready", StringComparison.Ordinal)
                    && snapshot.VectorButtonTextIsEmpty;
                if (logicalWidth == 480) snapshotValid &= !snapshot.StatusVisible && !snapshot.HomeVisible;
                if (logicalWidth >= 1280) snapshotValid &= snapshot.StatusVisible;
                if (!snapshotValid)
                {
                    Console.Error.WriteLine(
                        $"Toolbar snapshot failed at {logicalWidth}px / {dpi} DPI: {snapshot}; "
                        + $"requestedWidth={requestedWidth}; outerContained={outerControlsContained}; "
                        + $"innerGeometry={innerGeometryValid}.");
                }
                layoutValid &= snapshotValid;
            }
            textValid = form.VectorToolbarTextIsSuppressedForTesting();
            paintValid = form.VectorToolbarPaintIsIdempotentForTesting();
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return error is null
        ? (layoutValid, textValid, paintValid, addressBarPreservesCaret)
        : (false, false, false, false);
}

bool OrderedWithoutOverlap(Rectangle left, Rectangle right)
{
    return left.IsEmpty || right.IsEmpty || left.Right <= right.Left;
}

bool CanRenderHighContrastChrome()
{
    Exception? error = null;
    var valid = false;
    var thread = new Thread(() =>
    {
        try
        {
            var policy = MainForm.ResolveChromeColorPolicyForTesting(highContrast: true);
            using var form = new MainForm(startBrowserOnShown: false)
            {
                ClientSize = new Size(960, 600),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false
            };
            form.PrepareChromePreviewForTesting();
            form.ApplyHighContrastChromeForTesting(enabled: true);
            form.Show();
            form.PerformLayout();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(bitmap, form.ClientRectangle);
            var snapshot = form.GetChromeContrastSnapshotForTesting();
            valid = policy.HighContrast
                && policy.ChromeSurface == SystemColors.Control
                && policy.FieldSurface == SystemColors.Window
                && policy.Focus == SystemColors.Highlight
                && policy.SelectionText == SystemColors.HighlightText
                && policy.MenuSurface == SystemColors.Menu
                && snapshot.ChromeSurface == SystemColors.Control
                && snapshot.ToolbarSurface == SystemColors.Control
                && snapshot.FieldSurface == SystemColors.Window
                && snapshot.FieldText == SystemColors.WindowText
                && snapshot.Focus == SystemColors.Highlight
                && snapshot.MenuSurface == SystemColors.Menu
                && snapshot.MenuText == SystemColors.MenuText
                && snapshot.ToolbarButtonUsesHighContrast
                && snapshot.CaptionButtonUsesHighContrast
                && snapshot.OmniboxUsesHighContrast
                && snapshot.TabUsesHighContrast
                && snapshot.MenuRendererUsesHighContrast;
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return error is null && valid;
}

(bool Restored, bool Distinct, bool Rendered) VerifyChromeThemeRoundTrip()
{
    Exception? error = null;
    var restored = false;
    var distinct = false;
    var rendered = false;
    var thread = new Thread(() =>
    {
        try
        {
            var normalPolicy = MainForm.ResolveChromeColorPolicyForTesting(highContrast: false);
            var highContrastPolicy = MainForm.ResolveChromeColorPolicyForTesting(highContrast: true);
            var loadingBackgroundMethod = typeof(BrowserState).Assembly
                .GetType("MishaWeb.WebsiteThemePolicy")
                ?.GetMethod(
                    "GetLoadingBackgroundColor",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var lightLoadingSurface = loadingBackgroundMethod?.Invoke(null, [false]) is Color light
                ? light
                : Color.Empty;

            using var form = new MainForm(startBrowserOnShown: false)
            {
                ClientSize = new Size(ScaleForDpi(960, 120), ScaleForDpi(600, 120)),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false
            };
            form.Show();
            form.PrepareChromePreviewForTesting();

            form.ApplyHighContrastChromeForTesting(enabled: false);
            form.PerformLayout();
            Application.DoEvents();
            var normalSnapshot = form.GetChromeContrastSnapshotForTesting();
            using var normalBitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(normalBitmap, form.ClientRectangle);

            form.ApplyHighContrastChromeForTesting(enabled: true);
            form.PerformLayout();
            Application.DoEvents();
            var highContrastSnapshot = form.GetChromeContrastSnapshotForTesting();
            using var highContrastBitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(highContrastBitmap, form.ClientRectangle);

            form.ApplyHighContrastChromeForTesting(enabled: false);
            form.PerformLayout();
            Application.DoEvents();
            var restoredSnapshot = form.GetChromeContrastSnapshotForTesting();
            using var restoredBitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(restoredBitmap, form.ClientRectangle);

            restored = normalSnapshot == restoredSnapshot
                && !restoredSnapshot.ToolbarButtonUsesHighContrast
                && !restoredSnapshot.CaptionButtonUsesHighContrast
                && !restoredSnapshot.OmniboxUsesHighContrast
                && !restoredSnapshot.TabUsesHighContrast
                && !restoredSnapshot.MenuRendererUsesHighContrast;
            distinct = !normalPolicy.HighContrast
                && highContrastPolicy.HighContrast
                && normalPolicy.ChromeSurface == NativeUiTheme.Chrome
                && normalPolicy.PageSurface == NativeUiTheme.Window
                && lightLoadingSurface == Color.White
                && highContrastSnapshot.ChromeSurface == SystemColors.Control
                && highContrastSnapshot.FieldSurface == SystemColors.Window
                && ContrastRatio(normalPolicy.PageSurface, lightLoadingSurface) >= 7.0
                && (normalSnapshot.ChromeSurface != highContrastSnapshot.ChromeSurface
                    || normalSnapshot.FieldSurface != highContrastSnapshot.FieldSurface);
            rendered = SurfaceHasVisualDetail(normalBitmap)
                && SurfaceHasVisualDetail(highContrastBitmap)
                && SurfaceHasVisualDetail(restoredBitmap);
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (error is not null) Console.Error.WriteLine($"Chrome theme round-trip probe failed: {error}");
    return error is null ? (restored, distinct, rendered) : (false, false, false);
}

bool FrontendSourcesAreEncodingClean()
{
    var sources = new[]
    {
        ReadRepositorySource("desktop", "MainForm.cs"),
        ReadRepositorySource("desktop", "NativeStartPage.cs"),
        ReadRepositorySource("desktop", "SmartSearchBar.cs"),
        ReadRepositorySource("desktop", "NativeFeatureSurfaces.cs"),
        ReadRepositorySource("desktop", "AddressSuggestionPopup.cs"),
        ReadRepositorySource("desktop", "SavedItemsDialog.cs"),
        ReadRepositorySource("desktop", "VoiceAudioSettingsDialog.cs")
    };
    var suspectCharacters = new[] { '\u00C2', '\u00C3', '\u00E2', '\uFFFD' };
    var requiredGlyphs = new[]
    {
        (Glyph: "×", Escape: "\\u00D7"),
        (Glyph: "↑", Escape: "\\u2191"),
        (Glyph: "↓", Escape: "\\u2193"),
        (Glyph: "⌄", Escape: "\\u2304"),
        (Glyph: "…", Escape: "\\u2026"),
        (Glyph: "—", Escape: "\\u2014"),
        (Glyph: "·", Escape: "\\u00B7")
    };
    var combined = string.Concat(sources);
    return sources.All(source => source.Length > 0)
        && sources.All(source => suspectCharacters.All(character => !source.Contains(character)))
        && requiredGlyphs.All(item =>
            combined.Contains(item.Glyph, StringComparison.Ordinal)
            || combined.Contains(item.Escape, StringComparison.OrdinalIgnoreCase));
}

bool CanConstructFeatureSurfaces()
{
    Exception? error = null;
    var constructed = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var palette = new CommandPaletteForm(_ => Array.Empty<RankedCommand>(), _ => { });
            using var mru = new MruSwitcherForm();
            using var sessions = new SessionManagerForm();
            using var prompt = new PermissionPromptForm();
            using var permissions = new PermissionManagerForm();
            using var downloads = new DownloadsPopupForm(
                () => Array.Empty<DownloadPopupRow>(),
                (_, _) => { });
            using var extensions = new ExtensionsManagerForm(
                () => Task.FromResult<IReadOnlyList<ExtensionManagerRow>>([]),
                (_, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                (_, _) => Task.FromResult("Update check completed"),
                (_, _) => Task.CompletedTask,
                () => { },
                () => { });
            using var extensionsWithRows = new ExtensionsManagerForm(
                () => Task.FromResult<IReadOnlyList<ExtensionManagerRow>>([
                    new ExtensionManagerRow(
                        "flpnibonbhdmnpgflnbemgghghhblmpm",
                        "Ghostify | Hide Seen, Typing & Story Views",
                        "2.0.6",
                        true,
                        "popup.html",
                        true,
                        new object(),
                        PopupPath: "popup.html",
                        OptionsPath: "options.html",
                        Description: "Control your visibility",
                        StoreId: "flpnibonbhdmnpgflnbemgghghhblmpm",
                        Capabilities: ["Permission: storage", "Site access: https://*.instagram.com/*"])
                ]),
                (_, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                (_, _) => Task.FromResult("Update check completed"),
                (_, _) => Task.CompletedTask,
                () => { },
                () => { });
            palette.CreateControl();
            mru.CreateControl();
            sessions.CreateControl();
            prompt.CreateControl();
            permissions.CreateControl();
            downloads.CreateControl();
            extensions.CreateControl();
            extensionsWithRows.CreateControl();
            constructed = palette.AccessibleName == "Command palette"
                && mru.AccessibleName == "Recent tabs"
                && sessions.AccessibleName == "Saved sessions manager"
                && prompt.AccessibleName == "Website permission request"
                && permissions.AccessibleName == "Website permission manager"
                && downloads.AccessibleName == "Downloads"
                && extensions.AccessibleName == "Browser extensions manager"
                && extensionsWithRows.AccessibleName == "Browser extensions manager";
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return error is null && constructed;
}

IEnumerable<Control> GetDescendants(Control root)
{
    foreach (Control child in root.Controls)
    {
        yield return child;
        foreach (var descendant in GetDescendants(child)) yield return descendant;
    }
}

bool CanLimitSuggestionPopupRows()
{
    Exception? error = null;
    var validSelection = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var popup = new AddressSuggestionPopup();
            var items = AddressSuggestionEngine.GetSuggestions(
                "exa",
                new BrowserState
                {
                    Bookmarks =
                    [
                        new BookmarkEntry("Example one", "https://one.example/"),
                        new BookmarkEntry("Example two", "https://two.example/")
                    ]
                });
            popup.SetSuggestions(items);
            typeof(AddressSuggestionPopup)
                .GetMethod(
                    "OnMouseMove",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(
                    popup,
                    [new MouseEventArgs(MouseButtons.None, 0, 12, popup.RowHeight / 2, 0)]);
            var hasNoImplicitSelection = !popup.TryGetSelected(out _);
            popup.LimitVisibleRows(2);
            popup.MoveSelection(-1);
            var retainedAccessibleSearchRow = popup.AccessibilityObject.GetChild(1);
            var acceptedCount = 0;
            popup.SuggestionAccepted += _ => acceptedCount++;
            validSelection = hasNoImplicitSelection
                && popup.TryGetSelected(out var selected)
                && selected.IsSearch
                && selected.KeyboardIndex == 1
                && popup.PreferredPopupHeight == (popup.RowHeight * 2) + 2;
            popup.LimitVisibleRows(8);
            validSelection = validSelection
                && popup.TryGetSelected(out var expandedSelection)
                && expandedSelection.IsSearch
                && expandedSelection.KeyboardIndex == items.Count - 1
                && popup.PreferredPopupHeight == (popup.RowHeight * items.Count) + 2;
            retainedAccessibleSearchRow?.DoDefaultAction();
            popup.ClearSuggestions();
            var retainedRowUnavailable = retainedAccessibleSearchRow is not null
                && !string.IsNullOrEmpty(retainedAccessibleSearchRow.Name)
                && !string.IsNullOrEmpty(retainedAccessibleSearchRow.Description)
                && retainedAccessibleSearchRow.Bounds == Rectangle.Empty
                && retainedAccessibleSearchRow.State.HasFlag(AccessibleStates.Unavailable);
            retainedAccessibleSearchRow?.DoDefaultAction();
            validSelection = validSelection
                && acceptedCount == 1
                && retainedRowUnavailable;
        }
        catch (Exception caught)
        {
            error = caught;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return error is null && validSelection;
}

bool IsAutoDarkParameterObject(string? json, bool expectedEnabled)
{
    if (string.IsNullOrWhiteSpace(json)) return false;
    try
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        return root.ValueKind == System.Text.Json.JsonValueKind.Object
            && root.EnumerateObject().Count() == 1
            && root.TryGetProperty("enabled", out var enabled)
            && enabled.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            && enabled.GetBoolean() == expectedEnabled;
    }
    catch (System.Text.Json.JsonException)
    {
        return false;
    }
}

bool IsClearAutoDarkParameterObject(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return false;
    try
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
            && !document.RootElement.EnumerateObject().Any();
    }
    catch (System.Text.Json.JsonException)
    {
        return false;
    }
}

int CountOccurrences(string source, string value)
{
    if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value)) return 0;
    var count = 0;
    var start = 0;
    while ((start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
    {
        count++;
        start += value.Length;
    }
    return count;
}

string ReadRepositorySource(params string[] parts)
{
    var path = FindRepositoryFile(parts);
    return path is null ? string.Empty : File.ReadAllText(path);
}

string? FindRepositoryFile(params string[] parts)
{
    var roots = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
    foreach (var root in roots)
    {
        var directory = new DirectoryInfo(root);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path)) return path;
        }
    }
    return null;
}

string ExtractSourceSection(string source, string startMarker, string endMarker)
{
    var start = source.IndexOf(startMarker, StringComparison.Ordinal);
    if (start < 0) return string.Empty;
    var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
    return end < 0 ? source[start..] : source[start..end];
}

void WriteTestExtensionManifest(
    string folder,
    string name,
    string version,
    string launchPath)
{
    var manifest = new
    {
        manifest_version = 3,
        name,
        version,
        permissions = new[] { "tabs" },
        host_permissions = new[] { "https://api.extension-fixture.test/*" },
        optional_permissions = new[] { "downloads" },
        action = new { default_popup = launchPath },
        content_scripts = new[]
        {
            new
            {
                matches = new[] { "https://extension-fixture.test/*" },
                js = new[] { "content.js" }
            }
        }
    };
    File.WriteAllText(
        Path.Combine(folder, "manifest.json"),
        JsonSerializer.Serialize(manifest));
}

byte[] CreateTestExtensionZip(string name, string version, string launchPath)
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        var manifest = JsonSerializer.Serialize(new
        {
            manifest_version = 3,
            name,
            version,
            options_page = launchPath
        });
        WriteZipText(archive, "manifest.json", manifest);
        WriteZipText(archive, "content.js", "globalThis.__mishaCrxFixture = true;");
        WriteZipText(archive, launchPath, "<!doctype html><title>Options</title>");
    }
    return output.ToArray();
}

byte[] CreateCrx2Package(byte[] publicKey, byte[] zipBytes)
{
    const int signatureLength = 8;
    using var output = new MemoryStream();
    output.Write("Cr24"u8);
    Span<byte> number = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(number, 2);
    output.Write(number);
    BinaryPrimitives.WriteInt32LittleEndian(number, publicKey.Length);
    output.Write(number);
    BinaryPrimitives.WriteInt32LittleEndian(number, signatureLength);
    output.Write(number);
    output.Write(publicKey);
    output.Write(new byte[signatureLength]);
    output.Write(zipBytes);
    return output.ToArray();
}

byte[] CreateSignedCrx2Package(System.Security.Cryptography.RSA signingKey, byte[] zipBytes)
{
    var publicKey = signingKey.ExportSubjectPublicKeyInfo();
    var signature = signingKey.SignData(
        zipBytes,
        System.Security.Cryptography.HashAlgorithmName.SHA1,
        System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    using var output = new MemoryStream();
    output.Write("Cr24"u8);
    Span<byte> number = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(number, 2);
    output.Write(number);
    BinaryPrimitives.WriteInt32LittleEndian(number, publicKey.Length);
    output.Write(number);
    BinaryPrimitives.WriteInt32LittleEndian(number, signature.Length);
    output.Write(number);
    output.Write(publicKey);
    output.Write(signature);
    output.Write(zipBytes);
    return output.ToArray();
}

byte[] CreateCrx3Package(byte[] publicKey, byte[] zipBytes)
{
    var crxId = System.Security.Cryptography.SHA256.HashData(publicKey)[..16];
    var proof = EncodeProtobufBytesField(1, publicKey)
        .Concat(EncodeProtobufBytesField(2, [1, 2, 3, 4]))
        .ToArray();
    var signedData = EncodeProtobufBytesField(1, crxId);
    var header = EncodeProtobufBytesField(2, proof)
        .Concat(EncodeProtobufBytesField(10_000, signedData))
        .ToArray();

    using var output = new MemoryStream();
    output.Write("Cr24"u8);
    Span<byte> number = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(number, 3);
    output.Write(number);
    BinaryPrimitives.WriteInt32LittleEndian(number, header.Length);
    output.Write(number);
    output.Write(header);
    output.Write(zipBytes);
    return output.ToArray();
}

byte[] CreateSignedCrx3Package(System.Security.Cryptography.RSA signingKey, byte[] zipBytes)
{
    var publicKey = signingKey.ExportSubjectPublicKeyInfo();
    var crxId = System.Security.Cryptography.SHA256.HashData(publicKey)[..16];
    var signedData = EncodeProtobufBytesField(1, crxId);
    Span<byte> signedDataLength = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(signedDataLength, signedData.Length);
    var signedPayload = "CRX3 SignedData\0"u8.ToArray()
        .Concat(signedDataLength.ToArray())
        .Concat(signedData)
        .Concat(zipBytes)
        .ToArray();
    var signature = signingKey.SignData(
        signedPayload,
        System.Security.Cryptography.HashAlgorithmName.SHA256,
        System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    var proof = EncodeProtobufBytesField(1, publicKey)
        .Concat(EncodeProtobufBytesField(2, signature))
        .ToArray();
    var header = EncodeProtobufBytesField(2, proof)
        .Concat(EncodeProtobufBytesField(10_000, signedData))
        .ToArray();

    using var output = new MemoryStream();
    output.Write("Cr24"u8);
    Span<byte> number = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(number, 3);
    output.Write(number);
    BinaryPrimitives.WriteInt32LittleEndian(number, header.Length);
    output.Write(number);
    output.Write(header);
    output.Write(zipBytes);
    return output.ToArray();
}

byte[] EncodeProtobufBytesField(int fieldNumber, byte[] value)
{
    using var output = new MemoryStream();
    WriteVarint(output, checked((ulong)((fieldNumber << 3) | 2)));
    WriteVarint(output, checked((ulong)value.Length));
    output.Write(value);
    return output.ToArray();
}

void WriteVarint(Stream output, ulong value)
{
    while (value >= 0x80)
    {
        output.WriteByte((byte)(value | 0x80));
        value >>= 7;
    }
    output.WriteByte((byte)value);
}

byte[] CreateTraversalZip()
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        WriteZipText(archive, "../escaped.txt", "unsafe");
        WriteZipText(
            archive,
            "manifest.json",
            "{\"manifest_version\":3,\"name\":\"Traversal\",\"version\":\"1.0\"}");
    }
    return output.ToArray();
}

void WriteZipText(ZipArchive archive, string path, string content)
{
    var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
    writer.Write(content);
}

void Check(string name, bool condition)
{
    checkCount++;
    if (!condition) failures.Add(name);
}

bool IsRoseHue(Color color)
{
    return color.R >= 220
        && color.R > color.G + 60
        && color.R > color.B + 35;
}

double ContrastRatio(Color first, Color second)
{
    var firstLuminance = RelativeLuminance(first);
    var secondLuminance = RelativeLuminance(second);
    return (Math.Max(firstLuminance, secondLuminance) + 0.05)
        / (Math.Min(firstLuminance, secondLuminance) + 0.05);
}

double RelativeLuminance(Color color)
{
    return (0.2126 * Linearize(color.R))
        + (0.7152 * Linearize(color.G))
        + (0.0722 * Linearize(color.B));
}

double Linearize(byte channel)
{
    var value = channel / 255d;
    return value <= 0.04045
        ? value / 12.92
        : Math.Pow((value + 0.055) / 1.055, 2.4);
}

bool HasOnlyPairedSurrogates(string value)
{
    for (var index = 0; index < value.Length; index++)
    {
        if (char.IsHighSurrogate(value[index]))
        {
            if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])) return false;
        }
        else if (char.IsLowSurrogate(value[index]))
        {
            return false;
        }
    }
    return true;
}

bool CanParseJavaScript(string script)
{
    var path = Path.Combine(
        Path.GetTempPath(),
        "MishaWeb-Script-Syntax-" + Guid.NewGuid().ToString("N") + ".js");
    try
    {
        File.WriteAllText(path, script);
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--check");
        start.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null || !process.WaitForExit(5_000))
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            return false;
        }
        if (process.ExitCode == 0) return true;
        var error = process.StandardError.ReadToEnd();
        if (error.Length > 0) Console.Error.WriteLine(error);
        return false;
    }
    finally
    {
        File.Delete(path);
    }
}

bool CanExecuteAdBlockDocumentScript(string script)
{
    var path = Path.Combine(
        Path.GetTempPath(),
        "MishaWeb-AdBlock-Runtime-" + Guid.NewGuid().ToString("N") + ".js");
    try
    {
        var prelude =
            "globalThis.window=globalThis;"
            + "globalThis.location={hostname:'www.youtube.com',pathname:'/watch',search:'?v=q8xYvyYYpf0',href:'https://www.youtube.com/watch?v=q8xYvyYYpf0'};"
            + "globalThis.HTMLElement=class {};globalThis.HTMLVideoElement=class extends HTMLElement {};"
            + "globalThis.Node=class {};globalThis.__smokeAppendChild=function(node){return node;};Node.prototype.appendChild=__smokeAppendChild;"
            + "globalThis.HTMLIFrameElement=class extends HTMLElement {};globalThis.__smokeFrameWindow={};Object.defineProperty(HTMLIFrameElement.prototype,'contentWindow',{configurable:true,get(){return __smokeFrameWindow;}});"
            + "globalThis.__smokeNativeJsonParse=JSON.parse;globalThis.__smokeObservers=[];"
            + "globalThis.__smokeEvents={};window.addEventListener=(name,handler)=>{__smokeEvents[name]=handler;};"
            + "globalThis.__smokeFetchPromise=Promise.resolve({url:'https://www.youtube.com/youtubei/v1/next'});window.fetch=()=>__smokeFetchPromise;"
            + "window.ytcfg={data_:{INNERTUBE_CONTEXT:{client:{userAgent:'Mozilla/5.0 (Smoke) Chrome'}}}};"
            + "window.ytInitialPlayerResponse={adPlacements:[{ad:true}],playerAds:[1],adSlots:[2],adBreakHeartbeatParams:'ad',videoDetails:{videoId:'q8xYvyYYpf0'},playerConfig:{playbackStartConfig:{startSeconds:0}},playabilityStatus:{status:'ERROR',errorScreen:{enforcementMessageViewModel:{isVisible:true,title:{content:'Ad blockers violate YouTube Terms'},primaryButton:{onTap:{parallelCommand:{commands:[{innertubeCommand:{openAdAllowlistInstructionCommand:{}}}]}}}}}}};"
            + "window.ytInitialData={feed:[{command:{reelWatchEndpoint:{adClientParams:{isAd:true}}}},{keep:true}],contents:{twoColumnWatchNextResults:{results:{results:{contents:[{itemSectionRenderer:{targetId:'comments-section'}}]}},secondaryResults:{secondaryResults:{results:[{lockupViewModel:{contentId:'related-modern'}},{adSlotRenderer:{slotId:'ad'}},{compactVideoRenderer:{videoId:'related-legacy'}}]}}}}};"
            + "globalThis.__smokeVideo=new HTMLVideoElement();Object.assign(__smokeVideo,{readyState:4,duration:60,currentTime:0,muted:false});"
            + "globalThis.__smokeEnforcement={removed:false,closest:()=>null,remove(){this.removed=true;}};"
            + "globalThis.__smokePlayer={getPlayerResponse:()=>window.ytInitialPlayerResponse,loadVideoById:()=>{__smokeReloads++;__smokeRecoveryMarkers.push({userAgent:window.ytcfg.data_.INNERTUBE_CONTEXT.client.userAgent,screen:window.ytcfg.data_.INNERTUBE_CONTEXT.client.clientScreen,masked:__smokeRecoveryMasked,at:Date.now()});__smokePrunedBeforeReload=!('adPlacements'in window.ytInitialPlayerResponse)&&!('adBreakHeartbeatParams'in window.ytInitialPlayerResponse);window.ytInitialPlayerResponse=__smokeReloads===1?{videoDetails:{videoId:'q8xYvyYYpf0'},playerConfig:{playbackStartConfig:{startSeconds:0}},playabilityStatus:{status:'ERROR',errorScreen:{enforcementMessageViewModel:{isVisible:true,title:{content:'Ad blockers violate YouTube Terms'},primaryButton:{onTap:{parallelCommand:{commands:[{innertubeCommand:{openAdAllowlistInstructionCommand:{}}}]}}}}}}}:{videoDetails:{videoId:'q8xYvyYYpf0'},playabilityStatus:{status:'OK'}};},playVideo:()=>{__smokePlayed=true;},getProgressState:()=>({duration:60,loaded:60,current:1}),getStatsForNerds:()=>({debug_info:''}),classList:{contains:()=>false,remove:name=>{if(name==='ytp-transparent')__smokeTransparentRemoved=true;}}};"
            + "globalThis.__smokeStarted=Date.now();globalThis.__smokeReloads=0;globalThis.__smokePlayed=false;globalThis.__smokeTransparentRemoved=false;globalThis.__smokeRecoveryMarkers=[];globalThis.__smokeRecoveryMasked=false;globalThis.__smokePrunedBeforeReload=false;"
            + "globalThis.MutationObserver=class{constructor(callback){this.callback=callback;}observe(target,options){__smokeObservers.push({target,options});}disconnect(){}};"
            + "globalThis.document={hidden:false,documentElement:{appendChild:()=>{},setAttribute:name=>{if(name==='data-misha-youtube-recovery')__smokeRecoveryMasked=true;},removeAttribute:name=>{if(name==='data-misha-youtube-recovery')__smokeRecoveryMasked=false;}},readyState:'complete',createElement:()=>({id:'',textContent:''}),getElementById:id=>id==='movie_player'?__smokePlayer:null,querySelector:selector=>selector.includes('enforcement-message')&&!__smokeEnforcement.removed?__smokeEnforcement:selector.startsWith('video')?__smokeVideo:null,addEventListener:()=>{},removeEventListener:()=>{}};";
        var assertions =
            "(async()=>{await new Promise(resolve=>setTimeout(resolve,800));const rendererPrototype={};rendererPrototype.hasAllowedInstreamAd=function(data){return data.videoId==='related';};"
            + "const renderer=Object.create(rendererPrototype);let rendererMethodPreserved=false;try{rendererMethodPreserved=renderer.hasAllowedInstreamAd({videoId:'related'});}catch(_){}"
            + "const detector={};detector.adBlocksFound=27;const detectorPropertyPreserved=detector.adBlocksFound===27;"
            + "const nativeDomPrimitivePreserved=Node.prototype.appendChild===__smokeAppendChild;const nativeJsonPreserved=JSON.parse===__smokeNativeJsonParse;"
            + "const wholeDocumentObserverAbsent=!__smokeObservers.some(item=>item.target===document.documentElement&&item.options?.childList&&item.options?.subtree);"
            + "const iframeHookScoped=(new HTMLIFrameElement()).contentWindow.fetch===window.fetch;"
            + "window.ytcfg.data_.EXPERIMENT_FLAGS={all_web_enable_network_machine:true,all_web_network_machine_raw_request:true,unrelated_feature:true};const networkMachineDisabled=window.ytcfg.data_.EXPERIMENT_FLAGS.all_web_enable_network_machine===false&&window.ytcfg.data_.EXPERIMENT_FLAGS.all_web_network_machine_raw_request===false&&window.ytcfg.data_.EXPERIMENT_FLAGS.unrelated_feature===true;"
            + "const browseFetchUntouched=window.fetch('https://www.youtube.com/youtubei/v1/next')===__smokeFetchPromise;"
            + "const playerFetchScoped=window.fetch('https://www.youtube.com/youtubei/v1/player')!==__smokeFetchPromise;"
            + "window.__mishaAdBlockEnabled=false;"
            + "const p=window.ytInitialPlayerResponse;const d=window.ytInitialData;"
            + "const watch=d.contents.twoColumnWatchNextResults;const comments=watch.results.results.contents;const related=watch.secondaryResults.secondaryResults.results;"
            + "const pageWriteBlocked=window.__mishaAdBlockEnabled===true;"
            + "const recoveryMarked=__smokeRecoveryMarkers.length===2&&__smokeRecoveryMarkers[0].userAgent.includes('lactmilli')&&__smokeRecoveryMarkers[1].userAgent.includes('channel')&&__smokeRecoveryMarkers[1].screen==='CHANNEL';"
            + "const recoveryWasImmediate=__smokeRecoveryMarkers.length===2&&__smokeRecoveryMarkers[1].at-__smokeStarted<750&&__smokeRecoveryMarkers.every(item=>item.masked);"
            + "__smokeEvents.__smoke_control_channel();"
            + "const recoveryRestored=window.ytcfg.data_.INNERTUBE_CONTEXT.client.userAgent==='Mozilla/5.0 (Smoke) Chrome'&&!('clientScreen'in window.ytcfg.data_.INNERTUBE_CONTEXT.client);"
            + "const functionalWatchDataPreserved=comments.length===1&&comments[0].itemSectionRenderer.targetId==='comments-section'&&related.length===2&&related[0].lockupViewModel.contentId==='related-modern'&&related[1].compactVideoRenderer.videoId==='related-legacy';"
            + "const inlineRecovered=__smokeReloads===2&&__smokeEnforcement.removed&&__smokeTransparentRemoved&&__smokePlayed&&__smokePrunedBeforeReload&&!__smokeRecoveryMasked;"
            + "if(!rendererMethodPreserved||!detectorPropertyPreserved||!nativeDomPrimitivePreserved||!nativeJsonPreserved||!wholeDocumentObserverAbsent||!iframeHookScoped||!networkMachineDisabled||!functionalWatchDataPreserved||!browseFetchUntouched||!playerFetchScoped||!pageWriteBlocked||!recoveryMarked||!recoveryWasImmediate||!recoveryRestored||!inlineRecovered||window.__mishaAdBlockEnabled!==false||d.feed.length!==1||!d.feed[0].keep){console.error(JSON.stringify({rendererMethodPreserved,detectorPropertyPreserved,nativeDomPrimitivePreserved,nativeJsonPreserved,wholeDocumentObserverAbsent,iframeHookScoped,networkMachineDisabled,functionalWatchDataPreserved,browseFetchUntouched,playerFetchScoped,pageWriteBlocked,recoveryMarked,recoveryWasImmediate,recoveryRestored,inlineRecovered,reloads:__smokeReloads,recoveryMarkers:__smokeRecoveryMarkers,enforcementRemoved:__smokeEnforcement.removed,masked:__smokeRecoveryMasked,transparentRemoved:__smokeTransparentRemoved,played:__smokePlayed,pruned:__smokePrunedBeforeReload,enabled:window.__mishaAdBlockEnabled,feed:d.feed,comments,related,observers:__smokeObservers.map(item=>item.options)}));process.exit(2);}process.exit(0);})().catch(error=>{console.error(error);process.exit(3);});";
        File.WriteAllText(path, prelude + script + assertions);
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null || !process.WaitForExit(5_000))
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            return false;
        }
        if (process.ExitCode == 0) return true;
        var error = process.StandardError.ReadToEnd();
        if (error.Length > 0) Console.Error.WriteLine(error);
        return false;
    }
    finally
    {
        File.Delete(path);
    }
}

Dictionary<string, bool> ExecuteYouTubeTransportRecoveryFixture(string script)
{
    var path = Path.Combine(
        Path.GetTempPath(),
        "MishaWeb-YouTube-Transport-" + Guid.NewGuid().ToString("N") + ".js");
    try
    {
        var prelude =
            """
            globalThis.window=globalThis;
            globalThis.__now=0;
            Object.defineProperty(globalThis,'performance',{configurable:true,value:{now:()=>__now}});
            globalThis.location={hostname:'www.youtube.com',pathname:'/watch',search:'?v=q8xYvyYYpf0&t=1815s',href:'https://www.youtube.com/watch?v=q8xYvyYYpf0&t=1815s'};
            globalThis.HTMLElement=class { click(){} };
            globalThis.HTMLVideoElement=class extends HTMLElement {};
            globalThis.Node=class {};
            Node.prototype.appendChild=function(node){return node;};
            globalThis.HTMLIFrameElement=class extends HTMLElement {};
            globalThis.__frameWindow={};
            Object.defineProperty(HTMLIFrameElement.prototype,'contentWindow',{configurable:true,get(){return __frameWindow;}});
            globalThis.getComputedStyle=()=>({display:'block',visibility:'visible',opacity:'1'});
            globalThis.__timerId=0;
            globalThis.__timers=new Map();
            window.setTimeout=(callback,delay=0)=>{const id=++__timerId;__timers.set(id,{callback,delay});return id;};
            window.clearTimeout=id=>{__timers.delete(id);};
            globalThis.__windowEvents={};
            window.addEventListener=(name,handler)=>{__windowEvents[name]=handler;};
            window.removeEventListener=()=>{};
            window.fetch=()=>Promise.resolve({url:'https://www.youtube.com/youtubei/v1/next'});
            window.ytcfg={data_:{INNERTUBE_CONTEXT:{client:{userAgent:'Mozilla/5.0 (Transport Fixture) Chrome'}}}};
            window.ytInitialData={};
            globalThis.__response={videoDetails:{videoId:'q8xYvyYYpf0'},playabilityStatus:{status:'OK'}};
            window.ytInitialPlayerResponse=__response;
            globalThis.__buffering=true;
            globalThis.__bufferHealth='0.00 s';
            globalThis.__resolution='0x0';
            globalThis.__duration=3518.841;
            globalThis.__loaded=0;
            globalThis.__current=1815;
            globalThis.__errorVisible=true;
            globalThis.__errorText="This content isn't available, try again later.";
            globalThis.__bufferedEnd=0;
            globalThis.__calls=[];
            globalThis.__video=new HTMLVideoElement();
            Object.assign(__video,{readyState:4,duration:3518.841,currentTime:1815,paused:true,muted:false});
            Object.defineProperty(__video,'buffered',{configurable:true,get:()=>({length:__bufferedEnd>0?1:0,end:()=>__bufferedEnd})});
            globalThis.__player={
                getPlayerResponse:()=>__response,
                loadVideoById:(videoId,startSeconds)=>{__calls.push({videoId,startSeconds,at:__now,userAgent:window.ytcfg.data_.INNERTUBE_CONTEXT.client.userAgent,screen:window.ytcfg.data_.INNERTUBE_CONTEXT.client.clientScreen??null,mask:__attributes['data-misha-youtube-recovery']??null});},
                playVideo:()=>{},
                getProgressState:()=>({duration:__duration,loaded:__loaded,current:__current}),
                getPlayerStateObject:()=>({isBuffering:__buffering}),
                getStatsForNerds:()=>({buffer_health_seconds:__bufferHealth,resolution:__resolution,debug_info:''}),
                classList:{contains:()=>false,remove:()=>{}}
            };
            globalThis.__errorCandidate={getBoundingClientRect:()=>({width:640,height:360})};
            Object.defineProperties(__errorCandidate,{
                innerText:{get:()=>__errorText},
                textContent:{get:()=>__errorText},
                isConnected:{get:()=>__errorVisible}
            });
            globalThis.__watchFlexy={removeAttribute:()=>{}};
            globalThis.__attributes={};
            globalThis.__documentEvents={};
            globalThis.MutationObserver=class{constructor(callback){this.callback=callback;}observe(){}disconnect(){}};
            globalThis.document={
                hidden:false,
                readyState:'complete',
                documentElement:{
                    appendChild:()=>{},
                    setAttribute:(name,value)=>{__attributes[name]=String(value);},
                    removeAttribute:name=>{delete __attributes[name];}
                },
                createElement:()=>({id:'',textContent:'',remove:()=>{}}),
                getElementById:id=>id==='movie_player'?__player:null,
                querySelector:selector=>selector==='video.html5-main-video, video'?__video:(selector==='ytd-watch-flexy[player-unavailable]'&&__errorVisible?__watchFlexy:null),
                querySelectorAll:selector=>selector.includes('.ytp-error')&&__errorVisible?[__errorCandidate]:[],
                addEventListener:(name,handler)=>{(__documentEvents[name]??=[]).push(handler);},
                removeEventListener:()=>{}
            };
            """;
        var assertions =
            """
            (()=>{
                const originalUserAgent='Mozilla/5.0 (Transport Fixture) Chrome';
                const runTimer=delay=>{
                    const timer=[...__timers].find(([,entry])=>entry.delay===delay);
                    if(!timer)return false;
                    __timers.delete(timer[0]);
                    timer[1].callback();
                    return true;
                };
                const pulse=()=>{
                    for(const handler of (__documentEvents['yt-player-updated']||[]))handler();
                    return runTimer(150);
                };
                const setWatch=(urlVideoId,responseVideoId=urlVideoId,options={})=>{
                    location.search=`?v=${urlVideoId}&t=1815s`;
                    location.href=`https://www.youtube.com/watch${location.search}`;
                    const videoDetails=responseVideoId===null
                        ? {...(options.videoDetails||{})}
                        : {videoId:responseVideoId,...(options.videoDetails||{})};
                    __response={
                        videoDetails,
                        playabilityStatus:options.playabilityStatus||{status:'OK'}
                    };
                    __buffering=options.buffering??true;
                    __bufferHealth=options.bufferHealth??'0.00 s';
                    __resolution=options.resolution??'0x0';
                    __duration=options.duration??3518.841;
                    __loaded=options.loaded??0;
                    __current=options.current??1815;
                    __errorVisible=options.errorVisible??true;
                    __errorText=options.errorText??"This content isn't available, try again later.";
                    __bufferedEnd=options.bufferedEnd??0;
                    __video.readyState=options.readyState??4;
                    __video.currentTime=options.currentTime??1815;
                    __video.paused=options.paused??true;
                };

                const strictSignal=__calls.length===1
                    && __calls[0].videoId==='q8xYvyYYpf0'
                    && __calls[0].mask==='verified';
                const startPreserved=__calls.length===1&&__calls[0].startSeconds===1815;

                __now=100;
                pulse();
                const duplicateSuppressed=__calls.length===1;
                const staticReadyHeld=__video.readyState===4
                    && __video.currentTime===1815
                    && __attributes['data-misha-youtube-recovery']==='verified'
                    && window.ytcfg.data_.INNERTUBE_CONTEXT.client.userAgent.includes('channel');

                __now=2000;
                pulse();
                const markerSequence=__calls.length===2
                    && __calls[0].userAgent.includes('channel')
                    && __calls[0].screen==='CHANNEL'
                    && __calls[1].userAgent.includes('lactmilli')
                    && __calls[1].screen===null
                    && __calls.every(call=>call.startSeconds===1815);

                __now=2100;
                pulse();
                const heldAt100=__calls.length===2
                    && __attributes['data-misha-youtube-recovery']==='verified'
                    && window.ytcfg.data_.INNERTUBE_CONTEXT.client.userAgent.includes('lactmilli');
                __now=2250;
                pulse();
                const heldAt250=__calls.length===2
                    && __attributes['data-misha-youtube-recovery']==='verified'
                    && window.ytcfg.data_.INNERTUBE_CONTEXT.client.userAgent.includes('lactmilli');
                const secondAttemptHold=heldAt100&&heldAt250;

                __now=3999;
                pulse();
                const heldBeforeDeadline=__calls.length===2
                    && __attributes['data-misha-youtube-recovery']==='verified';
                __now=4000;
                pulse();
                const cappedFailOpen=__calls.length===2
                    && heldBeforeDeadline
                    && !('data-misha-youtube-recovery' in __attributes)
                    && window.ytcfg.data_.INNERTUBE_CONTEXT.client.userAgent===originalUserAgent
                    && !('clientScreen' in window.ytcfg.data_.INNERTUBE_CONTEXT.client);
                __now=4100;
                pulse();
                const terminalDoesNotRetry=__calls.length===2;

                let before=__calls.length;
                setWatch('AbCdEfGhI01','AbCdEfGhI01',{errorVisible:false});
                __now=5000;
                pulse();
                const missingErrorIgnored=__calls.length===before;

                setWatch('AbCdEfGhI02','AbCdEfGhI02',{
                    errorText:'Video unavailable'
                });
                __now=5100;
                pulse();
                const nonRetryErrorIgnored=__calls.length===before;

                setWatch('AbCdEfGhI03','AbCdEfGhI03');
                __now=5200;
                pulse();
                const progressRecoveryArmed=__calls.length===before+1
                    && __attributes['data-misha-youtube-recovery']==='verified';
                before=__calls.length;
                __buffering=false;
                __current=1815.2;
                __video.readyState=4;
                __video.currentTime=1815.2;
                __now=5300;
                pulse();
                const progressClears=progressRecoveryArmed
                    && __calls.length===before
                    && !('data-misha-youtube-recovery' in __attributes)
                    && window.ytcfg.data_.INNERTUBE_CONTEXT.client.userAgent===originalUserAgent;

                __buffering=true;
                __bufferHealth='0.00 s';
                __resolution='0x0';
                __errorVisible=true;
                __current=1900;
                __video.currentTime=1900;
                __now=5350;
                pulse();
                before=__calls.length;
                __now=5375;
                pulse();
                const laterStallResumesCurrent=__calls.length===before+1
                    && __calls.at(-1).startSeconds===1900;

                __buffering=false;
                __current=1900.2;
                __video.currentTime=1900.2;
                __now=5380;
                pulse();
                before=__calls.length;
                __buffering=true;
                // The player progress API can lag one event behind the media
                // element after a seek. The media element's exact 0:00 must win.
                __current=1900.2;
                __video.currentTime=0;
                __now=5390;
                pulse();
                const laterStallResumesSeekedStart=__calls.length===before+1
                    && __calls.at(-1).startSeconds===0;

                before=__calls.length;
                setWatch('AbCdEfGhI04','AbCdEfGhI04');
                __now=5400;
                pulse();
                const playableRecoveryArmed=__calls.length===before+1;
                before=__calls.length;
                __errorVisible=false;
                __buffering=false;
                __bufferHealth='1.25 s';
                __resolution='1920x1080';
                __video.paused=false;
                __now=5500;
                pulse();
                const playableStateClears=playableRecoveryArmed
                    && __calls.length===before
                    && !('data-misha-youtube-recovery' in __attributes)
                    && window.ytcfg.data_.INNERTUBE_CONTEXT.client.userAgent===originalUserAgent;

                before=__calls.length;
                setWatch('AbCdEfGhI05','AbCdEfGhI05',{videoDetails:{isLive:true}});
                __now=5600;
                pulse();
                const liveIgnored=__calls.length===before;

                setWatch('AbCdEfGhI06','AbCdEfGhI06',{videoDetails:{isLiveContent:true}});
                __now=5700;
                pulse();
                const liveContentIgnored=__calls.length===before;

                setWatch('AbCdEfGhI07','AbCdEfGhI07',{playabilityStatus:{status:'ERROR',errorScreen:{playerErrorMessageRenderer:{playerCaptchaViewModel:{challenge:'present'}}}}});
                __now=5800;
                pulse();
                const captchaIgnored=__calls.length===before;

                setWatch('AbCdEfGhI08','AbCdEfGhI08',{buffering:false});
                __now=5900;
                pulse();
                const nonBufferingIgnored=__calls.length===before;

                setWatch('AbCdEfGhI09','AbCdEfGhI09',{duration:0});
                __now=6000;
                pulse();
                const zeroDurationIgnored=__calls.length===before;

                setWatch('AbCdEfGhI12',null);
                __now=6100;
                pulse();
                const missingResponseIdIgnored=__calls.length===before;

                setWatch('AbCdEfGhI13','invalid');
                __now=6200;
                pulse();
                const invalidResponseIdIgnored=__calls.length===before;

                setWatch('invalid','invalid');
                __now=6300;
                pulse();
                const invalidRequestedIdIgnored=__calls.length===before;

                setWatch('AbCdEfGhI10','AbCdEfGhI11',{
                    playabilityStatus:{
                        status:'ERROR',
                        errorScreen:{
                            enforcementMessageViewModel:{
                                isVisible:true,
                                title:{content:'Ad blockers violate YouTube Terms'},
                                primaryButton:{onTap:{parallelCommand:{commands:[
                                    {innertubeCommand:{openAdAllowlistInstructionCommand:{}}}
                                ]}}}
                            }
                        }
                    }
                });
                __now=6400;
                pulse();
                const mismatchedVideoIgnored=__calls.length===before;

                const enforcementStatus=()=>({
                    status:'ERROR',
                    errorScreen:{
                        enforcementMessageViewModel:{
                            isVisible:true,
                            title:{content:'Ad blockers violate YouTube Terms'},
                            primaryButton:{onTap:{parallelCommand:{commands:[
                                {innertubeCommand:{openAdAllowlistInstructionCommand:{}}}
                            ]}}}
                        }
                    }
                });
                before=__calls.length;
                setWatch('AbCdEfGhI14','AbCdEfGhI14',{
                    errorVisible:false,
                    playabilityStatus:enforcementStatus()
                });
                __now=6500;
                pulse();
                setWatch('AbCdEfGhI14','AbCdEfGhI14',{
                    errorVisible:false,
                    playabilityStatus:enforcementStatus()
                });
                __now=6600;
                pulse();
                setWatch('AbCdEfGhI14','AbCdEfGhI14',{
                    errorVisible:false,
                    playabilityStatus:enforcementStatus()
                });
                __now=6700;
                pulse();
                const serverMaskHeldForInflightRetry=__calls.length===before+2
                    && __attributes['data-misha-youtube-recovery']==='verified';
                const serverFailOpenTimerRan=runTimer(15000);
                window.ytInitialPlayerResponse=__response;
                __now=21700;
                pulse();
                const serverBudgetFailsOpen=__calls.length===before+2
                    && serverMaskHeldForInflightRetry
                    && serverFailOpenTimerRan
                    && !('data-misha-youtube-recovery' in __attributes)
                    && window.ytcfg.data_.INNERTUBE_CONTEXT.client.userAgent===originalUserAgent;

                console.log(JSON.stringify({
                    strictSignal:strictSignal&&missingErrorIgnored,
                    startPreserved,
                    markerSequence,
                    duplicateSuppressed,
                    secondAttemptHold,
                    staticReadyHeld,
                    cappedFailOpen:cappedFailOpen&&terminalDoesNotRetry,
                    progressClears,
                    laterStallResumesCurrent,
                    laterStallResumesSeekedStart,
                    playableStateClears,
                    exactErrorOnly:missingErrorIgnored&&nonRetryErrorIgnored,
                    falsePositivesIgnored:liveIgnored&&liveContentIgnored&&captchaIgnored&&nonBufferingIgnored&&zeroDurationIgnored,
                    invalidVideoIdsIgnored:missingResponseIdIgnored&&invalidResponseIdIgnored&&invalidRequestedIdIgnored,
                    mismatchedVideoIgnored,
                    serverBudgetFailsOpen
                }));
            })();
            """;
        File.WriteAllText(path, prelude + script + assertions);
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null || !process.WaitForExit(5_000))
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            return [];
        }
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            if (error.Length > 0) Console.Error.WriteLine(error);
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(output) ?? [];
        }
        catch (JsonException)
        {
            Console.Error.WriteLine(output);
            if (error.Length > 0) Console.Error.WriteLine(error);
            return [];
        }
    }
    finally
    {
        File.Delete(path);
    }
}

(
    bool Ready,
    bool Installed,
    bool Removed,
    string Name,
    string Version,
    string? StoreId,
    string? LaunchPath,
    string RuntimeVersion) RunChromeStoreExtensionRuntimeProbe(
        string probeFolder,
        string storeUrlOrId)
{
    Exception? probeError = null;
    var result = (
        Ready: false,
        Installed: false,
        Removed: false,
        Name: string.Empty,
        Version: string.Empty,
        StoreId: (string?)null,
        LaunchPath: (string?)null,
        RuntimeVersion: string.Empty);
    var thread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var host = new Form
            {
                ClientSize = new Size(80, 80),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Shown += async (_, _) =>
            {
                try
                {
                    result = await RunChromeStoreExtensionRuntimeProbeAsync(
                        host,
                        probeFolder,
                        storeUrlOrId);
                }
                catch (Exception error)
                {
                    probeError = error;
                }
                finally
                {
                    host.Close();
                }
            };
            Application.Run(host);
        }
        catch (Exception error)
        {
            probeError = error;
        }
    })
    { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(60_000))
    {
        throw new TimeoutException("The Chrome Web Store runtime install probe did not finish within 60 seconds.");
    }
    if (probeError is not null)
    {
        throw new InvalidOperationException("The Chrome Web Store runtime install probe failed.", probeError);
    }
    return result;
}

async Task<(
    bool Ready,
    bool Installed,
    bool Removed,
    string Name,
    string Version,
    string? StoreId,
    string? LaunchPath,
    string RuntimeVersion)> RunChromeStoreExtensionRuntimeProbeAsync(
        Form host,
        string probeFolder,
        string storeUrlOrId)
{
    object? controller = null;
    object? installedExtension = null;
    var service = new BrowserExtensions(Path.Combine(probeFolder, "Extensions"));
    PreparedBrowserExtension? prepared = null;
    try
    {
        var environment = await CreateReflectedExtensionEnvironmentAsync(
            Path.Combine(probeFolder, "WebView2"));
        var runtimeVersion = environment.GetType().GetProperty("BrowserVersionString")?.GetValue(environment)
            as string ?? string.Empty;
        prepared = await service.DownloadFromChromeWebStoreAsync(
            storeUrlOrId,
            runtimeVersion);
        controller = await CreateReflectedExtensionControllerAsync(environment, host.Handle, inPrivate: false);
        var profile = GetReflectedControllerProfile(controller);
        installedExtension = await InvokeReflectedAsync(
            profile,
            "AddBrowserExtensionAsync",
            prepared.FolderPath)
            ?? throw new InvalidOperationException("WebView2 did not return the Chrome Web Store extension.");
        var installedId = installedExtension.GetType().GetProperty("Id")?.GetValue(installedExtension) as string;
        var installedName = installedExtension.GetType().GetProperty("Name")?.GetValue(installedExtension) as string;
        var installed = installedId == prepared.StoreId
            && installedName == prepared.Name
            && (bool?)installedExtension.GetType().GetProperty("IsEnabled")?.GetValue(installedExtension) == true
            && (await GetReflectedExtensionsAsync(profile)).Any(item =>
                item.GetType().GetProperty("Id")?.GetValue(item) as string == installedId);
        await InvokeReflectedAsync(installedExtension, "RemoveAsync");
        installedExtension = null;
        var removed = !(await GetReflectedExtensionsAsync(profile)).Any(item =>
            item.GetType().GetProperty("Id")?.GetValue(item) as string == installedId);
        var ready = installed
            && removed
            && Directory.Exists(prepared.FolderPath)
            && File.Exists(Path.Combine(prepared.FolderPath, "manifest.json"));
        return (
            ready,
            installed,
            removed,
            prepared.Name,
            prepared.Version,
            prepared.StoreId,
            prepared.LaunchPath,
            runtimeVersion);
    }
    finally
    {
        if (installedExtension is not null)
        {
            try { await InvokeReflectedAsync(installedExtension, "RemoveAsync"); }
            catch { }
        }
        if (controller is IDisposable disposable) disposable.Dispose();
        if (prepared is not null) service.DiscardPrepared(prepared);
    }
}

(
    bool Ready,
    bool InitialCategory,
    bool ListingReached,
    bool FallbackVisible,
    bool TrustedClickMessage,
    string Message) RunChromeStoreBridgeRuntimeProbe(string probeFolder)
{
    Exception? probeError = null;
    var result = (
        Ready: false,
        InitialCategory: false,
        ListingReached: false,
        FallbackVisible: false,
        TrustedClickMessage: false,
        Message: string.Empty);
    var thread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var host = new Form
            {
                ClientSize = new Size(640, 360),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Shown += async (_, _) =>
            {
                try
                {
                    result = await RunChromeStoreBridgeRuntimeProbeAsync(host, probeFolder);
                }
                catch (Exception error)
                {
                    probeError = error;
                }
                finally
                {
                    host.Close();
                }
            };
            Application.Run(host);
        }
        catch (Exception error)
        {
            probeError = error;
        }
    })
    { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(45_000))
    {
        throw new TimeoutException("The Chrome Web Store bridge probe did not finish within 45 seconds.");
    }
    if (probeError is not null)
    {
        throw new InvalidOperationException("The Chrome Web Store bridge probe failed.", probeError);
    }
    return result;
}

async Task<(
    bool Ready,
    bool InitialCategory,
    bool ListingReached,
    bool FallbackVisible,
    bool TrustedClickMessage,
    string Message)> RunChromeStoreBridgeRuntimeProbeAsync(Form host, string probeFolder)
{
    const string probeHost = "chromewebstore.google.com";
    const string listingId = "bcjindcccaagfpapjjmafapmmgkkhgoa";
    const string channel = "misha-chrome-store-bridge-probe";
    const string categoryUrl = "https://chromewebstore.google.com/category/extensions.html?hl=en";
    const string listingUrl =
        "https://chromewebstore.google.com/detail/misha-bridge-fixture/bcjindcccaagfpapjjmafapmmgkkhgoa";
    object? controller = null;
    try
    {
        var fixtureFolder = Path.Combine(probeFolder, "Fixture");
        var categoryFolder = Path.Combine(fixtureFolder, "category");
        Directory.CreateDirectory(categoryFolder);
        File.WriteAllText(
            Path.Combine(categoryFolder, "extensions.html"),
            $$"""
            <!doctype html>
            <meta charset="utf-8">
            <title>MishaWeb Chrome Web Store bridge probe</title>
            <main id="category">Extension category fixture</main>
            <script>
              window.__mishaStoreBridgeProbe = { initialUrl: location.href, spaComplete: false };
              addEventListener('DOMContentLoaded', () => {
                setTimeout(() => {
                  history.pushState({ probe: true }, '', {{JsonSerializer.Serialize(new Uri(listingUrl).AbsolutePath)}});
                  const marker = document.createElement('section');
                  marker.id = 'listing-content';
                  marker.textContent = 'Extension listing fixture';
                  document.body.appendChild(marker);
                  window.__mishaStoreBridgeProbe.spaComplete = true;
                }, 75);
              }, { once: true });
            </script>
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var environment = await CreateReflectedExtensionEnvironmentAsync(
            Path.Combine(probeFolder, "WebView2"));
        controller = await CreateReflectedExtensionControllerAsync(
            environment,
            host.Handle,
            inPrivate: false);
        controller.GetType().GetProperty("Bounds")?.SetValue(
            controller,
            new Rectangle(0, 0, host.ClientSize.Width, host.ClientSize.Height));
        controller.GetType().GetProperty("IsVisible")?.SetValue(controller, true);
        var core = controller.GetType().GetProperty("CoreWebView2")?.GetValue(controller)
            ?? throw new InvalidOperationException("The WebView2 controller did not expose a core.");
        var settings = core.GetType().GetProperty("Settings")?.GetValue(core)
            ?? throw new InvalidOperationException("The WebView2 core did not expose settings.");
        var webMessagesProperty = settings.GetType().GetProperty("IsWebMessageEnabled")
            ?? throw new InvalidOperationException("WebView2 settings did not expose Web Messaging.");
        webMessagesProperty.SetValue(settings, true);

        string? receivedMessage = null;
        string? receivedSource = null;
        Exception? webMessageError = null;
        var webMessageEvent = core.GetType().GetEvent("WebMessageReceived")
            ?? throw new InvalidOperationException("The WebView2 core did not expose WebMessageReceived.");
        var webMessageHandler = CreateReflectedEventHandler(
            webMessageEvent.EventHandlerType
                ?? throw new InvalidOperationException("WebMessageReceived did not expose a handler type."),
            (_, eventArgs) =>
            {
                try
                {
                    receivedSource = eventArgs.GetType().GetProperty("Source")?.GetValue(eventArgs) as string;
                    var getMessage = eventArgs.GetType().GetMethod(
                        "TryGetWebMessageAsString",
                        Type.EmptyTypes)
                        ?? throw new InvalidOperationException(
                            "WebMessageReceived did not expose TryGetWebMessageAsString.");
                    receivedMessage = getMessage.Invoke(eventArgs, null) as string;
                }
                catch (Exception error)
                {
                    webMessageError = error;
                }
            });
        webMessageEvent.AddEventHandler(core, webMessageHandler);

        await InvokeReflectedAsync(
            core,
            "AddScriptToExecuteOnDocumentCreatedAsync",
            BrowserExtensions.CreateChromeWebStoreInstallBridge(channel));
        var coreAssembly = System.Reflection.Assembly.Load("Microsoft.Web.WebView2.Core");
        var accessKindType = coreAssembly.GetType(
                "Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind",
                throwOnError: true)!;
        var accessKind = Enum.Parse(accessKindType, "Deny");
        FindCompatibleMethod(
            core.GetType(),
            "SetVirtualHostNameToFolderMapping",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            [probeHost, fixtureFolder, accessKind]).Invoke(
                core,
                [probeHost, fixtureFolder, accessKind]);
        FindCompatibleMethod(
            core.GetType(),
            "Navigate",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            [categoryUrl]).Invoke(core, [categoryUrl]);

        var state = await WaitForChromeStoreBridgeFallbackAsync(core, categoryUrl, listingUrl);
        var centerJson = await ExecuteWebViewScriptAsync(
            core,
            "(() => {const button=document.getElementById('misha-install-in-browser');"
            + "if(!button)return null;const rect=button.getBoundingClientRect();"
            + "return [rect.left+(rect.width/2),rect.top+(rect.height/2)];})()");
        var center = JsonSerializer.Deserialize<double[]>(centerJson);
        if (center is not { Length: 2 }
            || !double.IsFinite(center[0])
            || !double.IsFinite(center[1]))
        {
            throw new InvalidOperationException("The Store bridge fallback exposed no clickable center point.");
        }

        await InvokeReflectedAsync(
            core,
            "CallDevToolsProtocolMethodAsync",
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new
            {
                type = "mouseMoved",
                x = center[0],
                y = center[1],
                button = "none"
            }));
        await InvokeReflectedAsync(
            core,
            "CallDevToolsProtocolMethodAsync",
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new
            {
                type = "mousePressed",
                x = center[0],
                y = center[1],
                button = "left",
                buttons = 1,
                clickCount = 1
            }));
        await InvokeReflectedAsync(
            core,
            "CallDevToolsProtocolMethodAsync",
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new
            {
                type = "mouseReleased",
                x = center[0],
                y = center[1],
                button = "left",
                buttons = 0,
                clickCount = 1
            }));

        var messageDeadline = DateTime.UtcNow.AddSeconds(5);
        while (receivedMessage is null && webMessageError is null && DateTime.UtcNow < messageDeadline)
        {
            await Task.Delay(50);
        }
        if (webMessageError is not null)
        {
            throw new InvalidOperationException("The Store bridge Web Message could not be read.", webMessageError);
        }

        var expectedMessage = channel + ":" + listingId;
        var trustedClickMessage = receivedMessage == expectedMessage
            && receivedSource == listingUrl;
        var ready = state.InitialCategory
            && state.ListingReached
            && state.FallbackVisible
            && trustedClickMessage;
        return (
            ready,
            state.InitialCategory,
            state.ListingReached,
            state.FallbackVisible,
            trustedClickMessage,
            receivedMessage ?? string.Empty);
    }
    finally
    {
        if (controller is IDisposable disposable) disposable.Dispose();
    }
}

async Task<(
    bool InitialCategory,
    bool ListingReached,
    bool FallbackVisible)> WaitForChromeStoreBridgeFallbackAsync(
        object core,
        string categoryUrl,
        string listingUrl)
{
    var categoryJson = JsonSerializer.Serialize(categoryUrl);
    var listingJson = JsonSerializer.Serialize(listingUrl);
    var stateScript = "(() => {"
        + "const probe=window.__mishaStoreBridgeProbe;"
        + "const button=document.getElementById('misha-install-in-browser');"
        + "const rect=button?.getBoundingClientRect();const style=button?getComputedStyle(button):null;"
        + $"const initialCategory=probe?.initialUrl==={categoryJson};"
        + $"const listingReached=probe?.spaComplete===true&&location.href==={listingJson};"
        + "const fallbackVisible=Boolean(button&&rect&&rect.width>0&&rect.height>0"
        + "&&style&&style.display!=='none'&&style.visibility!=='hidden'&&style.opacity!=='0');"
        + "return {initialCategory,listingReached,fallbackVisible};})()";
    var deadline = DateTime.UtcNow.AddSeconds(10);
    string lastState = string.Empty;
    while (DateTime.UtcNow < deadline)
    {
        lastState = await ExecuteWebViewScriptAsync(core, stateScript);
        using var document = JsonDocument.Parse(lastState);
        var root = document.RootElement;
        var state = (
            InitialCategory: root.GetProperty("initialCategory").GetBoolean(),
            ListingReached: root.GetProperty("listingReached").GetBoolean(),
            FallbackVisible: root.GetProperty("fallbackVisible").GetBoolean());
        if (state.InitialCategory && state.ListingReached && state.FallbackVisible) return state;
        await Task.Delay(50);
    }
    throw new TimeoutException(
        "The Chrome Web Store bridge fallback did not become visible. Last state: " + lastState);
}

(
    bool Ready,
    bool Installed,
    bool Disabled,
    bool Enabled,
    bool Persisted,
    bool PrivateIsolated,
    bool Removed,
    string ExtensionId) RunBrowserExtensionsRuntimeProbe(string probeFolder)
{
    Exception? probeError = null;
    var result = (
        Ready: false,
        Installed: false,
        Disabled: false,
        Enabled: false,
        Persisted: false,
        PrivateIsolated: false,
        Removed: false,
        ExtensionId: string.Empty);
    var thread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var host = new Form
            {
                ClientSize = new Size(80, 80),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Shown += async (_, _) =>
            {
                try
                {
                    result = await RunBrowserExtensionsRuntimeProbeAsync(host, probeFolder);
                }
                catch (Exception error)
                {
                    probeError = error;
                }
                finally
                {
                    host.Close();
                }
            };
            Application.Run(host);
        }
        catch (Exception error)
        {
            probeError = error;
        }
    })
    { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(30_000))
    {
        throw new TimeoutException("The browser extension WebView2 probe did not finish within 30 seconds.");
    }
    if (probeError is not null)
    {
        throw new InvalidOperationException("The browser extension WebView2 probe failed.", probeError);
    }
    return result;
}

async Task<(
    bool Ready,
    bool Installed,
    bool Disabled,
    bool Enabled,
    bool Persisted,
    bool PrivateIsolated,
    bool Removed,
    string ExtensionId)> RunBrowserExtensionsRuntimeProbeAsync(Form host, string probeFolder)
{
    object? normalController = null;
    object? persistedController = null;
    object? privateController = null;
    try
    {
        var userDataFolder = Path.Combine(probeFolder, "WebView2");
        var extensionFolder = Path.Combine(probeFolder, "FixtureExtension");
        Directory.CreateDirectory(userDataFolder);
        Directory.CreateDirectory(extensionFolder);
        WriteTestExtensionManifest(
            extensionFolder,
            "MishaWeb runtime extension fixture",
            "1.0.0",
            "popup.html");
        File.WriteAllText(
            Path.Combine(extensionFolder, "content.js"),
            "globalThis.__mishaRuntimeExtension = true;");
        File.WriteAllText(
            Path.Combine(extensionFolder, "popup.html"),
            "<!doctype html><title>MishaWeb extension fixture</title>");

        WebView2LoaderBootstrap.EnsureLoaded();
        var coreAssembly = System.Reflection.Assembly.Load("Microsoft.Web.WebView2.Core");
        var environmentType = coreAssembly.GetType(
                "Microsoft.Web.WebView2.Core.CoreWebView2Environment",
                throwOnError: true)!
            ;
        var optionsType = coreAssembly.GetType(
                "Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions",
                throwOnError: true)!
            ;
        var optionsConstructor = optionsType.GetConstructors()
            .OrderBy(constructor => constructor.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("WebView2 environment options did not expose a constructor.");
        var optionArguments = optionsConstructor.GetParameters()
            .Select(parameter => parameter.HasDefaultValue
                ? parameter.DefaultValue
                : parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null)
            .ToArray();
        var options = optionsConstructor.Invoke(optionArguments)
            ?? throw new InvalidOperationException("WebView2 environment options could not be created.");
        optionsType.GetProperty("EnableTrackingPrevention")?.SetValue(options, true);
        optionsType.GetProperty("AreBrowserExtensionsEnabled")?.SetValue(options, true);
        var createEnvironment = FindCompatibleMethod(
            environmentType,
            "CreateAsync",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            [null, userDataFolder, options]);
        var environment = await AwaitReflectedTaskAsync(
            createEnvironment.Invoke(null, [null, userDataFolder, options])
                ?? throw new InvalidOperationException("WebView2 environment creation returned no task."));
        if (environment is null) throw new InvalidOperationException("WebView2 environment creation returned no result.");

        normalController = await CreateReflectedExtensionControllerAsync(environment, host.Handle, inPrivate: false);
        var normalProfile = GetReflectedControllerProfile(normalController);
        var installedExtension = await InvokeReflectedAsync(
            normalProfile,
            "AddBrowserExtensionAsync",
            extensionFolder)
            ?? throw new InvalidOperationException("WebView2 did not return the installed extension.");
        var extensionId = installedExtension.GetType().GetProperty("Id")?.GetValue(installedExtension) as string
            ?? string.Empty;
        var installedName = installedExtension.GetType().GetProperty("Name")?.GetValue(installedExtension) as string;
        var initialExtensions = await GetReflectedExtensionsAsync(normalProfile);
        var installed = extensionId.Length == 32
            && installedName == "MishaWeb runtime extension fixture"
            && (bool?)installedExtension.GetType().GetProperty("IsEnabled")?.GetValue(installedExtension) == true
            && initialExtensions.Any(item =>
                item.GetType().GetProperty("Id")?.GetValue(item) as string == extensionId);

        await InvokeReflectedAsync(installedExtension, "EnableAsync", false);
        var disabled = (bool?)installedExtension.GetType().GetProperty("IsEnabled")?.GetValue(installedExtension) == false;
        if (normalController is IDisposable normalDisposable) normalDisposable.Dispose();
        normalController = null;

        persistedController = await CreateReflectedExtensionControllerAsync(environment, host.Handle, inPrivate: false);
        var persistedProfile = GetReflectedControllerProfile(persistedController);
        var persistedExtensions = await GetReflectedExtensionsAsync(persistedProfile);
        var persistedExtension = persistedExtensions.FirstOrDefault(item =>
            item.GetType().GetProperty("Id")?.GetValue(item) as string == extensionId)
            ?? throw new InvalidOperationException("The installed extension did not persist in the profile.");
        var persisted = (bool?)persistedExtension.GetType().GetProperty("IsEnabled")?.GetValue(persistedExtension) == false;
        await InvokeReflectedAsync(persistedExtension, "EnableAsync", true);
        var enabled = (bool?)persistedExtension.GetType().GetProperty("IsEnabled")?.GetValue(persistedExtension) == true;

        privateController = await CreateReflectedExtensionControllerAsync(environment, host.Handle, inPrivate: true);
        var privateProfile = GetReflectedControllerProfile(privateController);
        var privateIsolated = !(await GetReflectedExtensionsAsync(privateProfile)).Any(item =>
            item.GetType().GetProperty("Id")?.GetValue(item) as string == extensionId);

        await InvokeReflectedAsync(persistedExtension, "RemoveAsync");
        var removed = !(await GetReflectedExtensionsAsync(persistedProfile)).Any(item =>
            item.GetType().GetProperty("Id")?.GetValue(item) as string == extensionId);
        var ready = installed && disabled && enabled && persisted && privateIsolated && removed;
        return (ready, installed, disabled, enabled, persisted, privateIsolated, removed, extensionId);
    }
    finally
    {
        if (privateController is IDisposable privateDisposable) privateDisposable.Dispose();
        if (persistedController is IDisposable persistedDisposable) persistedDisposable.Dispose();
        if (normalController is IDisposable normalDisposable) normalDisposable.Dispose();
    }
}

async Task<object> CreateReflectedExtensionControllerAsync(
    object environment,
    IntPtr parentWindow,
    bool inPrivate)
{
    var optionsMethod = FindCompatibleMethod(
        environment.GetType(),
        "CreateCoreWebView2ControllerOptions",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
        []);
    var options = optionsMethod.Invoke(environment, [])
        ?? throw new InvalidOperationException("WebView2 controller options could not be created.");
    options.GetType().GetProperty("IsInPrivateModeEnabled")?.SetValue(options, inPrivate);
    if (inPrivate) options.GetType().GetProperty("ProfileName")?.SetValue(options, "MishaWebPrivateProbe");
    var controller = await InvokeReflectedAsync(
        environment,
        "CreateCoreWebView2ControllerAsync",
        parentWindow,
        options)
        ?? throw new InvalidOperationException("WebView2 controller creation returned no result.");
    controller.GetType().GetProperty("Bounds")?.SetValue(controller, Rectangle.Empty);
    controller.GetType().GetProperty("IsVisible")?.SetValue(controller, false);
    return controller;
}

async Task<object> CreateReflectedExtensionEnvironmentAsync(string userDataFolder)
{
    Directory.CreateDirectory(userDataFolder);
    WebView2LoaderBootstrap.EnsureLoaded();
    var coreAssembly = System.Reflection.Assembly.Load("Microsoft.Web.WebView2.Core");
    var environmentType = coreAssembly.GetType(
            "Microsoft.Web.WebView2.Core.CoreWebView2Environment",
            throwOnError: true)!;
    var optionsType = coreAssembly.GetType(
            "Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions",
            throwOnError: true)!;
    var optionsConstructor = optionsType.GetConstructors()
        .OrderBy(constructor => constructor.GetParameters().Length)
        .FirstOrDefault()
        ?? throw new InvalidOperationException("WebView2 environment options did not expose a constructor.");
    var optionArguments = optionsConstructor.GetParameters()
        .Select(parameter => parameter.HasDefaultValue
            ? parameter.DefaultValue
            : parameter.ParameterType.IsValueType
                ? Activator.CreateInstance(parameter.ParameterType)
                : null)
        .ToArray();
    var options = optionsConstructor.Invoke(optionArguments)
        ?? throw new InvalidOperationException("WebView2 environment options could not be created.");
    optionsType.GetProperty("EnableTrackingPrevention")?.SetValue(options, true);
    optionsType.GetProperty("AreBrowserExtensionsEnabled")?.SetValue(options, true);
    var createEnvironment = FindCompatibleMethod(
        environmentType,
        "CreateAsync",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
        [null, userDataFolder, options]);
    return await AwaitReflectedTaskAsync(
        createEnvironment.Invoke(null, [null, userDataFolder, options])
            ?? throw new InvalidOperationException("WebView2 environment creation returned no task."))
        ?? throw new InvalidOperationException("WebView2 environment creation returned no result.");
}

object GetReflectedControllerProfile(object controller)
{
    var core = controller.GetType().GetProperty("CoreWebView2")?.GetValue(controller)
        ?? throw new InvalidOperationException("The WebView2 controller did not expose a core.");
    return core.GetType().GetProperty("Profile")?.GetValue(core)
        ?? throw new InvalidOperationException("The WebView2 core did not expose a profile.");
}

async Task<object[]> GetReflectedExtensionsAsync(object profile)
{
    var extensions = await InvokeReflectedAsync(profile, "GetBrowserExtensionsAsync")
        ?? throw new InvalidOperationException("WebView2 did not return the installed extensions list.");
    return extensions is System.Collections.IEnumerable sequence
        ? sequence.Cast<object>().ToArray()
        : throw new InvalidOperationException("WebView2 returned an invalid extensions list.");
}

void DeleteBrowserExtensionsProbeFolder(string probeFolder)
{
    var fullPath = Path.GetFullPath(probeFolder);
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(fullPath).StartsWith("MishaWeb-Extension-Runtime-Probe-", StringComparison.Ordinal))
    {
        return;
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    const int cleanupAttempts = 50;
    for (var attempt = 0; attempt < cleanupAttempts && Directory.Exists(fullPath); attempt++)
    {
        try
        {
            Directory.Delete(fullPath, recursive: true);
        }
        catch (IOException)
        {
            if (attempt < cleanupAttempts - 1) Thread.Sleep(100);
        }
        catch (UnauthorizedAccessException)
        {
            if (attempt < cleanupAttempts - 1) Thread.Sleep(100);
        }
    }
}

void DeleteChromeStoreBridgeProbeFolder(string probeFolder)
{
    var fullPath = Path.GetFullPath(probeFolder);
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(fullPath).StartsWith(
            "MishaWeb-Chrome-Store-Bridge-Probe-",
            StringComparison.Ordinal))
    {
        return;
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    const int cleanupAttempts = 50;
    for (var attempt = 0; attempt < cleanupAttempts && Directory.Exists(fullPath); attempt++)
    {
        try
        {
            Directory.Delete(fullPath, recursive: true);
        }
        catch (IOException)
        {
            if (attempt < cleanupAttempts - 1) Thread.Sleep(100);
        }
        catch (UnauthorizedAccessException)
        {
            if (attempt < cleanupAttempts - 1) Thread.Sleep(100);
        }
    }
}

void DeleteExtensionDownloadProbeFolder(string probeFolder)
{
    var fullPath = Path.GetFullPath(probeFolder);
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(fullPath).StartsWith("MishaWeb-Extension-Download-Probe-", StringComparison.Ordinal))
    {
        return;
    }
    try
    {
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

(
    bool Ready,
    bool DarkMedia,
    double DarkLuminance,
    double DynamicLuminance,
    double FrameLuminance,
    double MediaDelta,
    bool LightMedia,
    double LightLuminance,
    bool SameDocument) RunWebsiteThemeRuntimeProbe(string probeFolder)
{
    Exception? probeError = null;
    var result = (
        Ready: false,
        DarkMedia: false,
        DarkLuminance: 1d,
        DynamicLuminance: 1d,
        FrameLuminance: 1d,
        MediaDelta: double.MaxValue,
        LightMedia: true,
        LightLuminance: 0d,
        SameDocument: false);
    var thread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var host = new Form
            {
                ClientSize = new Size(420, 300),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Shown += async (_, _) =>
            {
                try
                {
                    result = await RunWebsiteThemeRuntimeProbeAsync(host, probeFolder);
                }
                catch (Exception error)
                {
                    probeError = error;
                }
                finally
                {
                    host.Close();
                }
            };
            Application.Run(host);
        }
        catch (Exception error)
        {
            probeError = error;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(30_000))
    {
        throw new TimeoutException("The website theme WebView2 probe did not finish within 30 seconds.");
    }
    if (probeError is not null)
    {
        throw new InvalidOperationException("The website theme WebView2 probe failed.", probeError);
    }
    return result;
}

async Task<(
    bool Ready,
    bool DarkMedia,
    double DarkLuminance,
    double DynamicLuminance,
    double FrameLuminance,
    double MediaDelta,
    bool LightMedia,
    double LightLuminance,
    bool SameDocument)> RunWebsiteThemeRuntimeProbeAsync(Form host, string probeFolder)
{
    const int viewportWidth = 420;
    const int viewportHeight = 300;
    object? controller = null;
    try
    {
        Directory.CreateDirectory(probeFolder);
        WebView2LoaderBootstrap.EnsureLoaded();
        var coreAssembly = System.Reflection.Assembly.Load("Microsoft.Web.WebView2.Core");
        var environmentType = coreAssembly.GetType(
                "Microsoft.Web.WebView2.Core.CoreWebView2Environment",
                throwOnError: true)!
            ;
        var createEnvironment = FindCompatibleMethod(
            environmentType,
            "CreateAsync",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            [null, probeFolder, null]);
        var environment = await AwaitReflectedTaskAsync(
            createEnvironment.Invoke(null, [null, probeFolder, null])
                ?? throw new InvalidOperationException("WebView2 environment creation returned no task."));
        if (environment is null) throw new InvalidOperationException("WebView2 environment creation returned no result.");

        controller = await InvokeReflectedAsync(
            environment,
            "CreateCoreWebView2ControllerAsync",
            host.Handle);
        if (controller is null) throw new InvalidOperationException("WebView2 controller creation returned no result.");
        controller.GetType().GetProperty("Bounds")?.SetValue(
            controller,
            new Rectangle(0, 0, viewportWidth, viewportHeight));
        controller.GetType().GetProperty("IsVisible")?.SetValue(controller, true);
        var core = controller.GetType().GetProperty("CoreWebView2")?.GetValue(controller)
            ?? throw new InvalidOperationException("The WebView2 controller did not expose a core.");
        var profile = core.GetType().GetProperty("Profile")?.GetValue(core)
            ?? throw new InvalidOperationException("The WebView2 core did not expose a profile.");
        var schemeProperty = profile.GetType().GetProperty("PreferredColorScheme")
            ?? throw new InvalidOperationException("The WebView2 profile did not expose a color scheme.");
        schemeProperty.SetValue(profile, Enum.Parse(schemeProperty.PropertyType, "Dark"));
        await InvokeReflectedAsync(
            core,
            "CallDevToolsProtocolMethodAsync",
            "Emulation.setAutoDarkModeOverride",
            "{\"enabled\":true}");

        var fixtureMedia = CreateWebsiteThemeProbeImageDataUrl();
        var fixture = $$"""
            <!doctype html>
            <meta charset="utf-8">
            <style>
              html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#fafafa!important;color:#111!important}
              #opaque{position:absolute;inset:0;background:#fafafa!important}
              #media{position:absolute;left:260px;top:20px;width:80px;height:80px;z-index:2}
              #frame{position:absolute;left:20px;top:160px;width:120px;height:100px;border:0;z-index:2}
              #dynamic{position:absolute;left:170px;top:160px;width:100px;height:80px;background:#fafafa!important;z-index:2}
            </style>
            <div id="opaque"></div>
            <img id="media" alt="fixture" src="{{fixtureMedia}}">
            <iframe id="frame" srcdoc="<style>html,body{margin:0;width:100%;height:100%;background:#fafafa!important;color:#111!important}</style><body>frame</body>"></iframe>
            <script>
              window.__themeProbeToken = String(performance.timeOrigin) + ':' + Math.random();
              setTimeout(() => { const node=document.createElement('div'); node.id='dynamic'; document.body.appendChild(node); }, 25);
            </script>
            """;
        FindCompatibleMethod(
            core.GetType(),
            "NavigateToString",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            [fixture]).Invoke(core, [fixture]);
        await WaitForWebsiteThemeFixtureAsync(core);

        var tokenBefore = await ExecuteWebViewScriptAsync(core, "window.__themeProbeToken");
        var viewport = System.Text.Json.JsonSerializer.Deserialize<double[]>(
            await ExecuteWebViewScriptAsync(core, "[innerWidth,innerHeight]"))
            ?? throw new InvalidOperationException("The website theme fixture did not expose its viewport.");
        if (viewport.Length != 2 || viewport[0] <= 0 || viewport[1] <= 0)
        {
            throw new InvalidOperationException("The website theme fixture exposed invalid viewport dimensions.");
        }
        var darkMedia = await ExecuteWebViewScriptAsync(
            core,
            "matchMedia('(prefers-color-scheme: dark)').matches") == "true";
        using var darkCapture = await CaptureWebViewAsync(core, coreAssembly);
        var darkSurface = SampleWebViewPixel(darkCapture, viewport[0], viewport[1], 50, 50);
        var darkDynamic = SampleWebViewPixel(darkCapture, viewport[0], viewport[1], 210, 200);
        var darkFrame = SampleWebViewPixel(darkCapture, viewport[0], viewport[1], 80, 210);
        var darkMediaColor = SampleWebViewPixel(darkCapture, viewport[0], viewport[1], 300, 60);

        await InvokeReflectedAsync(
            core,
            "CallDevToolsProtocolMethodAsync",
            "Emulation.setAutoDarkModeOverride",
            "{}");
        schemeProperty.SetValue(profile, Enum.Parse(schemeProperty.PropertyType, "Light"));
        await Task.Delay(300);
        var lightMedia = await ExecuteWebViewScriptAsync(
            core,
            "matchMedia('(prefers-color-scheme: dark)').matches") == "true";
        var tokenAfter = await ExecuteWebViewScriptAsync(core, "window.__themeProbeToken");
        using var lightCapture = await CaptureWebViewAsync(core, coreAssembly);
        var lightSurface = SampleWebViewPixel(lightCapture, viewport[0], viewport[1], 50, 50);
        var lightMediaColor = SampleWebViewPixel(lightCapture, viewport[0], viewport[1], 300, 60);

        var darkLuminance = RelativeLuminance(darkSurface);
        var dynamicLuminance = RelativeLuminance(darkDynamic);
        var frameLuminance = RelativeLuminance(darkFrame);
        var lightLuminance = RelativeLuminance(lightSurface);
        var mediaDelta = ColorDistance(darkMediaColor, lightMediaColor);
        var sameDocument = tokenBefore.Length > 2 && tokenBefore == tokenAfter;
        var ready = darkMedia
            && !lightMedia
            && darkLuminance < 0.45
            && dynamicLuminance < 0.45
            && frameLuminance < 0.55
            && mediaDelta <= 8.0
            && lightLuminance > 0.75
            && lightLuminance - darkLuminance > 0.30
            && sameDocument;
        return (
            ready,
            darkMedia,
            darkLuminance,
            dynamicLuminance,
            frameLuminance,
            mediaDelta,
            lightMedia,
            lightLuminance,
            sameDocument);
    }
    finally
    {
        if (controller is IDisposable disposable) disposable.Dispose();
    }
}

async Task WaitForWebsiteThemeFixtureAsync(object core)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < deadline)
    {
        var ready = await ExecuteWebViewScriptAsync(
            core,
            "document.readyState==='complete'&&Boolean(document.getElementById('dynamic'))&&Boolean(document.getElementById('media')?.complete)&&Boolean(document.getElementById('frame')?.contentDocument?.body)");
        if (ready == "true")
        {
            await Task.Delay(250);
            return;
        }
        await Task.Delay(50);
    }
    throw new TimeoutException("The website theme fixture did not finish loading.");
}

string CreateWebsiteThemeProbeImageDataUrl()
{
    using var image = new Bitmap(80, 80);
    for (var y = 0; y < image.Height; y++)
    {
        for (var x = 0; x < image.Width; x++)
        {
            image.SetPixel(
                x,
                y,
                Color.FromArgb(
                    (x * 13 + y * 3) % 256,
                    (x * 5 + y * 11) % 256,
                    (x * 7 + y * 17) % 256));
        }
    }
    using var stream = new MemoryStream();
    image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
    return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
}

async Task<string> ExecuteWebViewScriptAsync(object core, string script)
{
    return await InvokeReflectedAsync(core, "ExecuteScriptAsync", script) as string
        ?? string.Empty;
}

async Task<Bitmap> CaptureWebViewAsync(object core, System.Reflection.Assembly coreAssembly)
{
    var formatType = coreAssembly.GetType(
            "Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat",
            throwOnError: true)!
        ;
    var png = Enum.Parse(formatType, "Png");
    using var stream = new MemoryStream();
    await InvokeReflectedAsync(core, "CapturePreviewAsync", png, stream);
    stream.Position = 0;
    using var decoded = new Bitmap(stream);
    return new Bitmap(decoded);
}

Color SampleWebViewPixel(Bitmap bitmap, double viewportWidth, double viewportHeight, int x, int y)
{
    var scaledX = Math.Clamp((int)Math.Round(x * bitmap.Width / (double)viewportWidth), 0, bitmap.Width - 1);
    var scaledY = Math.Clamp((int)Math.Round(y * bitmap.Height / (double)viewportHeight), 0, bitmap.Height - 1);
    return bitmap.GetPixel(scaledX, scaledY);
}

double ColorDistance(Color first, Color second)
{
    var red = first.R - second.R;
    var green = first.G - second.G;
    var blue = first.B - second.B;
    return Math.Sqrt((red * red) + (green * green) + (blue * blue));
}

System.Reflection.MethodInfo FindCompatibleMethod(
    Type type,
    string name,
    System.Reflection.BindingFlags bindingFlags,
    object?[] arguments)
{
    foreach (var method in type.GetMethods(bindingFlags).Where(item => item.Name == name))
    {
        var parameters = method.GetParameters();
        if (parameters.Length != arguments.Length) continue;
        var compatible = true;
        for (var index = 0; index < parameters.Length; index++)
        {
            var argument = arguments[index];
            if (argument is not null && !parameters[index].ParameterType.IsInstanceOfType(argument))
            {
                compatible = false;
                break;
            }
        }
        if (compatible) return method;
    }
    throw new MissingMethodException(type.FullName, name);
}

async Task<object?> InvokeReflectedAsync(object target, string name, params object?[] arguments)
{
    var method = FindCompatibleMethod(
        target.GetType(),
        name,
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
        arguments);
    var task = method.Invoke(target, arguments)
        ?? throw new InvalidOperationException($"{target.GetType().Name}.{name} returned no task.");
    return await AwaitReflectedTaskAsync(task);
}

async Task<object?> AwaitReflectedTaskAsync(object taskObject)
{
    if (taskObject is not Task task)
    {
        throw new InvalidOperationException("The reflected WebView2 operation was not a task.");
    }
    await task.WaitAsync(TimeSpan.FromSeconds(15));
    return taskObject.GetType().GetProperty("Result")?.GetValue(taskObject);
}

void DeleteWebsiteThemeProbeFolder(string probeFolder)
{
    var fullPath = Path.GetFullPath(probeFolder);
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(fullPath).StartsWith("MishaWeb-Website-Theme-Probe-", StringComparison.Ordinal))
    {
        return;
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    const int cleanupAttempts = 50;
    for (var attempt = 0; attempt < cleanupAttempts && Directory.Exists(fullPath); attempt++)
    {
        try
        {
            Directory.Delete(fullPath, recursive: true);
        }
        catch (IOException) when (attempt < cleanupAttempts - 1)
        {
            Thread.Sleep(100);
        }
        catch (UnauthorizedAccessException) when (attempt < cleanupAttempts - 1)
        {
            Thread.Sleep(100);
        }
    }
}

(
    bool Ready,
    bool SecureContext,
    bool MicrophonePermission,
    bool CameraPermission,
    bool PermissionOriginsMatch,
    int AudioTracks,
    int VideoTracks,
    bool AudioLive,
    bool VideoLive,
    string? PageError) RunMediaCaptureRuntimeProbe(string probeFolder)
{
    Exception? probeError = null;
    var result = (
        Ready: false,
        SecureContext: false,
        MicrophonePermission: false,
        CameraPermission: false,
        PermissionOriginsMatch: false,
        AudioTracks: 0,
        VideoTracks: 0,
        AudioLive: false,
        VideoLive: false,
        PageError: (string?)null);
    var thread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var host = new Form
            {
                ClientSize = new Size(320, 240),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20_000, -20_000),
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Shown += async (_, _) =>
            {
                try
                {
                    result = await RunMediaCaptureRuntimeProbeAsync(host, probeFolder);
                }
                catch (Exception error)
                {
                    probeError = error;
                }
                finally
                {
                    host.Close();
                }
            };
            Application.Run(host);
        }
        catch (Exception error)
        {
            probeError = error;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(45_000))
    {
        throw new TimeoutException("The media-capture WebView2 probe did not finish within 45 seconds.");
    }
    if (probeError is not null)
    {
        throw new InvalidOperationException("The media-capture WebView2 probe failed.", probeError);
    }
    return result;
}

async Task<(
    bool Ready,
    bool SecureContext,
    bool MicrophonePermission,
    bool CameraPermission,
    bool PermissionOriginsMatch,
    int AudioTracks,
    int VideoTracks,
    bool AudioLive,
    bool VideoLive,
    string? PageError)> RunMediaCaptureRuntimeProbeAsync(Form host, string probeFolder)
{
    const string probeHost = "misha-media-capture.invalid";
    const string probeUrl = "https://misha-media-capture.invalid/index.html";
    object? controller = null;
    try
    {
        var fixtureFolder = Path.Combine(probeFolder, "fixture");
        Directory.CreateDirectory(fixtureFolder);
        File.WriteAllText(
            Path.Combine(fixtureFolder, "index.html"),
            """
            <!doctype html>
            <meta charset="utf-8">
            <title>MishaWeb media capture probe</title>
            <script>
              window.__mediaProbe = { done: false };
              (async () => {
                const audioStream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
                const videoStream = await navigator.mediaDevices.getUserMedia({ audio: false, video: true });
                await new Promise(resolve => setTimeout(resolve, 250));
                const audioTracks = audioStream.getAudioTracks();
                const videoTracks = videoStream.getVideoTracks();
                window.__mediaProbe = {
                  done: true,
                  secureContext: isSecureContext,
                  audioTracks: audioTracks.length,
                  videoTracks: videoTracks.length,
                  audioLive: audioTracks.length > 0 && audioTracks.every(track => track.readyState === 'live'),
                  videoLive: videoTracks.length > 0 && videoTracks.every(track => track.readyState === 'live'),
                  error: null
                };
              })().catch(error => {
                window.__mediaProbe = {
                  done: true,
                  secureContext: isSecureContext,
                  audioTracks: 0,
                  videoTracks: 0,
                  audioLive: false,
                  videoLive: false,
                  error: `${error?.name || 'Error'}: ${error?.message || String(error)}`
                };
              });
            </script>
            """);

        WebView2LoaderBootstrap.EnsureLoaded();
        var coreAssembly = System.Reflection.Assembly.Load("Microsoft.Web.WebView2.Core");
        var optionsType = coreAssembly.GetType(
                "Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions",
                throwOnError: true)!
            ;
        const string fakeMediaArguments = "--use-fake-device-for-media-stream";
        var customSchemeType = coreAssembly.GetType(
                "Microsoft.Web.WebView2.Core.CoreWebView2CustomSchemeRegistration",
                throwOnError: true)!
            ;
        var customSchemes = Activator.CreateInstance(typeof(List<>).MakeGenericType(customSchemeType))
            ?? throw new InvalidOperationException("The WebView2 custom-scheme list could not be created.");
        var optionsConstructor = optionsType.GetConstructors()
            .Single(constructor => constructor.GetParameters().Length == 5);
        var options = optionsConstructor.Invoke([fakeMediaArguments, null, null, false, customSchemes]);

        var environmentType = coreAssembly.GetType(
                "Microsoft.Web.WebView2.Core.CoreWebView2Environment",
                throwOnError: true)!
            ;
        var createEnvironment = FindCompatibleMethod(
            environmentType,
            "CreateAsync",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            [null, probeFolder, options]);
        var environment = await AwaitReflectedTaskAsync(
            createEnvironment.Invoke(null, [null, probeFolder, options])
                ?? throw new InvalidOperationException("WebView2 environment creation returned no task."));
        if (environment is null) throw new InvalidOperationException("WebView2 environment creation returned no result.");

        controller = await InvokeReflectedAsync(
            environment,
            "CreateCoreWebView2ControllerAsync",
            host.Handle);
        if (controller is null) throw new InvalidOperationException("WebView2 controller creation returned no result.");
        controller.GetType().GetProperty("Bounds")?.SetValue(controller, new Rectangle(0, 0, 320, 240));
        controller.GetType().GetProperty("IsVisible")?.SetValue(controller, true);
        var core = controller.GetType().GetProperty("CoreWebView2")?.GetValue(controller)
            ?? throw new InvalidOperationException("The WebView2 controller did not expose a core.");

        var permissionsSeen = new HashSet<string>(StringComparer.Ordinal);
        var permissionOriginsMatch = true;
        Exception? permissionEventError = null;
        var permissionEvent = core.GetType().GetEvent("PermissionRequested")
            ?? throw new InvalidOperationException("The WebView2 core did not expose PermissionRequested.");
        var permissionHandler = CreateReflectedEventHandler(
            permissionEvent.EventHandlerType
                ?? throw new InvalidOperationException("PermissionRequested did not expose a handler type."),
            (_, eventArgs) =>
            {
                try
                {
                    var eventArgsType = eventArgs.GetType();
                    var permissionKind = eventArgsType.GetProperty("PermissionKind")?.GetValue(eventArgs)?.ToString()
                        ?? string.Empty;
                    var permissionUri = eventArgsType.GetProperty("Uri")?.GetValue(eventArgs) as string;
                    if (permissionKind is "Microphone" or "Camera")
                    {
                        permissionsSeen.Add(permissionKind);
                        permissionOriginsMatch &= Uri.TryCreate(permissionUri, UriKind.Absolute, out var origin)
                            && origin.Scheme == Uri.UriSchemeHttps
                            && origin.IdnHost.Equals(probeHost, StringComparison.OrdinalIgnoreCase);
                    }

                    var stateProperty = eventArgsType.GetProperty("State")
                        ?? throw new InvalidOperationException("PermissionRequested did not expose State.");
                    stateProperty.SetValue(
                        eventArgs,
                        Enum.Parse(
                            stateProperty.PropertyType,
                            permissionKind is "Microphone" or "Camera" ? "Allow" : "Deny"));
                    eventArgsType.GetProperty("SavesInProfile")?.SetValue(eventArgs, false);
                    eventArgsType.GetProperty("Handled")?.SetValue(eventArgs, true);
                }
                catch (Exception error)
                {
                    permissionEventError = error;
                }
            });
        permissionEvent.AddEventHandler(core, permissionHandler);

        var accessKindType = coreAssembly.GetType(
                "Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind",
                throwOnError: true)!
            ;
        var accessKind = Enum.Parse(accessKindType, "Deny");
        FindCompatibleMethod(
            core.GetType(),
            "SetVirtualHostNameToFolderMapping",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            [probeHost, fixtureFolder, accessKind]).Invoke(
                core,
                [probeHost, fixtureFolder, accessKind]);
        FindCompatibleMethod(
            core.GetType(),
            "Navigate",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            [probeUrl]).Invoke(core, [probeUrl]);

        await WaitForMediaCaptureFixtureAsync(core);
        if (permissionEventError is not null)
        {
            throw new InvalidOperationException("A media permission event could not be handled.", permissionEventError);
        }

        var pageJson = await ExecuteWebViewScriptAsync(core, "window.__mediaProbe || null");
        using var pageDocument = System.Text.Json.JsonDocument.Parse(pageJson);
        var page = pageDocument.RootElement;
        var secureContext = page.GetProperty("secureContext").GetBoolean();
        var audioTracks = page.GetProperty("audioTracks").GetInt32();
        var videoTracks = page.GetProperty("videoTracks").GetInt32();
        var audioLive = page.GetProperty("audioLive").GetBoolean();
        var videoLive = page.GetProperty("videoLive").GetBoolean();
        var pageError = page.TryGetProperty("error", out var errorProperty)
            && errorProperty.ValueKind == System.Text.Json.JsonValueKind.String
                ? errorProperty.GetString()
                : null;
        var microphonePermission = permissionsSeen.Contains("Microphone");
        var cameraPermission = permissionsSeen.Contains("Camera");
        var permissionOriginsVerified = permissionOriginsMatch
            && microphonePermission
            && cameraPermission;
        var ready = secureContext
            && microphonePermission
            && cameraPermission
            && permissionOriginsVerified
            && audioTracks > 0
            && videoTracks > 0
            && audioLive
            && videoLive
            && string.IsNullOrWhiteSpace(pageError);
        return (
            ready,
            secureContext,
            microphonePermission,
            cameraPermission,
            permissionOriginsVerified,
            audioTracks,
            videoTracks,
            audioLive,
            videoLive,
            pageError);
    }
    finally
    {
        if (controller is IDisposable disposable) disposable.Dispose();
    }
}

async Task WaitForMediaCaptureFixtureAsync(object core)
{
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < deadline)
    {
        if (await ExecuteWebViewScriptAsync(core, "window.__mediaProbe?.done === true") == "true") return;
        await Task.Delay(100);
    }
    throw new TimeoutException("The media-capture fixture did not finish loading.");
}

Delegate CreateReflectedEventHandler(Type eventHandlerType, Action<object?, object> callback)
{
    var invoke = eventHandlerType.GetMethod("Invoke")
        ?? throw new InvalidOperationException($"{eventHandlerType.Name} did not expose Invoke.");
    var parameterInfos = invoke.GetParameters();
    if (parameterInfos.Length != 2)
    {
        throw new InvalidOperationException($"{eventHandlerType.Name} was not a two-argument event handler.");
    }
    var parameters = parameterInfos
        .Select(parameter => System.Linq.Expressions.Expression.Parameter(parameter.ParameterType, parameter.Name))
        .ToArray();
    var callbackInvoke = callback.GetType().GetMethod("Invoke")
        ?? throw new InvalidOperationException("The reflected event callback did not expose Invoke.");
    var body = System.Linq.Expressions.Expression.Call(
        System.Linq.Expressions.Expression.Constant(callback),
        callbackInvoke,
        System.Linq.Expressions.Expression.Convert(parameters[0], typeof(object)),
        System.Linq.Expressions.Expression.Convert(parameters[1], typeof(object)));
    return System.Linq.Expressions.Expression.Lambda(eventHandlerType, body, parameters).Compile();
}

void DeleteMediaCaptureProbeFolder(string probeFolder)
{
    var fullPath = Path.GetFullPath(probeFolder);
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(fullPath).StartsWith("MishaWeb-Media-Capture-Probe-", StringComparison.Ordinal))
    {
        return;
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    const int cleanupAttempts = 50;
    for (var attempt = 0; attempt < cleanupAttempts && Directory.Exists(fullPath); attempt++)
    {
        try
        {
            Directory.Delete(fullPath, recursive: true);
        }
        catch (IOException) when (attempt < cleanupAttempts - 1)
        {
            Thread.Sleep(100);
        }
        catch (UnauthorizedAccessException) when (attempt < cleanupAttempts - 1)
        {
            Thread.Sleep(100);
        }
    }
}
