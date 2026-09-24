using System.Text;
using System.Text.Json;

namespace MishaWeb;

internal sealed class BrowserState
{
    public string SearchProviderId { get; set; } = BrowserPolicy.DefaultSearchProviderId;
    public bool ReduceWebsiteMotionEnabled { get; set; }
    public bool MemorySaverEnabled { get; set; } = true;
    public bool? UltraLightModeEnabled { get; set; } = false;
    public int? ResourceModeDefaultsVersion { get; set; }
    public bool DarkModeEnabled { get; set; } = true;
    public bool AdBlockEnabled { get; set; } = true;
    public List<string> OpenTabs { get; set; } = [];
    public int? ActiveTabIndex { get; set; }
    public int PinnedOpenTabCount { get; set; }
    public List<BookmarkEntry> Bookmarks { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];
    public List<PinnedStartPageLink> PinnedStartPageLinks { get; set; } = [];
    public List<SavedSessionEntry> NamedSessions { get; set; } = [];
    public List<ClosedTabEntry> RecentlyClosed { get; set; } = [];
    public List<SiteZoomEntry> SiteZoom { get; set; } = [];
    public List<string> MutedHosts { get; set; } = [];
    public AudioOutputPolicy AudioOutputPolicy { get; set; } = AudioOutputPolicy.AllowAll;
    public List<string> AllowedAudioHosts { get; set; } = [];
    public AudioInputPolicy AudioInputPolicy { get; set; } = AudioInputPolicy.AskEveryTime;
    public List<string> AllowedMicrophoneHosts { get; set; } = [];
    public List<string> BlockedMicrophoneHosts { get; set; } = [];
    public List<string> AdBlockExceptionHosts { get; set; } = [];
    public List<string> DismissedSuggestionUrls { get; set; } = [];
    public List<AddressSuggestionUsage> SuggestionUsage { get; set; } = [];
    public List<string> KeepAwakeHosts { get; set; } = [];
    public SavedWindowPlacement? WindowPlacement { get; set; }

    // Compatibility aliases keep the in-memory model discoverable without
    // creating duplicate JSON fields in the additive settings schema.
    [System.Text.Json.Serialization.JsonIgnore]
    public List<SavedSessionEntry> Sessions
    {
        get => NamedSessions;
        set => NamedSessions = value ?? [];
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public List<ClosedTabEntry> RecentlyClosedTabs
    {
        get => RecentlyClosed;
        set => RecentlyClosed = value ?? [];
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public List<SiteZoomEntry> SiteZoomEntries
    {
        get => SiteZoom;
        set => SiteZoom = value ?? [];
    }
}

internal sealed record BookmarkEntry(string Title, string Url);

internal sealed record HistoryEntry(string Title, string Url, DateTime VisitedUtc);
internal sealed record AddressSuggestionUsage(string Url, int AcceptedCount, DateTimeOffset LastAcceptedUtc);

internal sealed record PinnedStartPageLink(string Title, string Url);
internal sealed record SavedSessionEntry(
    string Name,
    DateTimeOffset UpdatedUtc,
    List<SavedSessionTab> Tabs,
    int? ActiveTabIndex);

internal sealed record SavedSessionTab(string Url, bool IsPinned);

internal sealed record ClosedTabEntry(string Title, string Url, DateTimeOffset ClosedUtc);

internal sealed record SiteZoomEntry(string Host, double ZoomFactor);

internal enum BrowserMode
{
    Normal,
    Private
}

internal enum AudioOutputPolicy
{
    AllowAll,
    SpecificSitesOnly,
    MuteAll
}

internal enum AudioInputPolicy
{
    AskEveryTime,
    SpecificSitesOnly,
    BlockAll
}

internal enum SavedWindowPresentation
{
    Normal,
    Maximized,
    FullScreen
}

internal sealed record SavedWindowPlacement(
    int X,
    int Y,
    int Width,
    int Height,
    SavedWindowPresentation Presentation = SavedWindowPresentation.Normal,
    bool RestoreMaximizedAfterFullScreen = false);

internal sealed class BrowserStateStore : IDisposable
{
    internal const int MaximumOpenTabs = 128;
    internal const int MaximumUrlLength = BrowserPolicy.MaximumUrlLength;
    internal const int MaximumTitleLength = 512;
    internal const long MaximumSettingsBytes = 16 * 1_024 * 1_024;
    private const int MaximumBookmarks = 200;
    private const int MaximumHistoryEntries = 300;
    internal const int MaximumPinnedStartPageLinks = 3;
    internal const int MaximumAdBlockExceptionHosts = 200;
    internal const int MaximumDismissedSuggestions = 300;
    internal const int MaximumSuggestionUsageEntries = 300;
    internal const int MaximumKeepAwakeHosts = 200;
    internal const int MaximumPinnedOpenTabs = MaximumOpenTabs;
    internal const int MaximumNamedSessions = 20;
    internal const int MaximumSessionTabs = 64;
    internal const int MaximumSessionNameLength = 80;
    internal const int MaximumRecentlyClosed = 20;
    internal const int MaximumSiteZoomEntries = 200;
    internal const int MaximumMutedHosts = 200;
    internal const int MaximumAudioHosts = 200;
    internal const int MaximumMicrophoneHosts = 200;
    internal const double MinimumSiteZoom = 0.25;
    internal const double MaximumSiteZoom = 3.0;
    private readonly string settingsPath;
    private readonly string backupPath;
    private readonly string logPath;
    private readonly bool diagnosticsEnabled;
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = false };
    private readonly object backgroundWriterSync = new();
    private readonly object synchronousSaveSync = new();
    private readonly object persistenceSync = new();
    private readonly Action? beforeBackgroundWriteForTesting;
    private byte[]? pendingSerializedState;
    private Task backgroundWriterTask = Task.CompletedTask;
    private TaskCompletionSource? synchronousSaveCompletion;
    private TaskCompletionSource? disposeCompletion;
    private bool backgroundWriterRunning;
    private bool synchronousSaveInProgress;
    private bool disposed;
    private ulong lastPersistedFingerprint;
    private int lastPersistedSerializedLength;
    private long lastPersistedFileLength;
    private DateTime lastPersistedWriteUtc;
    private bool hasPersistedFingerprint;

    public BrowserStateStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MishaWeb"), diagnosticsEnabled: true)
    {
    }

    internal BrowserStateStore(bool diagnosticsEnabled) : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MishaWeb"), diagnosticsEnabled)
    {
    }

    internal BrowserStateStore(
        string appFolder,
        bool diagnosticsEnabled = true,
        Action? beforeBackgroundWriteForTesting = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appFolder);
        settingsPath = Path.Combine(appFolder, "settings.json");
        backupPath = settingsPath + ".bak";
        logPath = Path.Combine(appFolder, "diagnostics.log");
        this.diagnosticsEnabled = diagnosticsEnabled;
        this.beforeBackgroundWriteForTesting = beforeBackgroundWriteForTesting;
    }

    public BrowserState Load()
    {
        try
        {
            var primary = TryLoadFrom(settingsPath);
            if (primary is not null)
            {
                RememberPersistedState(JsonSerializer.SerializeToUtf8Bytes(primary, jsonOptions));
                return primary;
            }

            var backup = TryLoadFrom(backupPath);
            if (backup is not null)
            {
                RestoreFromBackupToPrimary(backup);
                return backup;
            }
        }
        catch (Exception error)
        {
            Log("Could not load settings", error);
        }

        return new BrowserState();
    }

    public bool Save(BrowserState state)
    {
        byte[] serializedState;
        try
        {
            NormalizeForPersistence(state);
            serializedState = SerializeWithinLimit(state);
        }
        catch (Exception error)
        {
            Log("Could not save settings", error);
            return false;
        }

        return SaveSynchronously(serializedState);
    }

    internal bool QueueSave(BrowserState state)
    {
        byte[] serializedState;
        try
        {
            // The WinForms thread owns BrowserState. Normalize and serialize it
            // before handing an immutable snapshot to the writer so the worker
            // never races later UI mutations.
            NormalizeForPersistence(state);
            serializedState = SerializeWithinLimit(state);
        }
        catch (Exception error)
        {
            Log("Could not snapshot settings", error);
            return false;
        }

        lock (backgroundWriterSync)
        {
            if (disposed) return false;

            // Keep only the newest not-yet-written snapshot. One slow disk can
            // therefore consume at most one queued settings buffer.
            pendingSerializedState = serializedState;
            StartBackgroundWriterLocked();
            return true;
        }
    }

    private bool SaveSynchronously(byte[] serializedState)
    {
        lock (synchronousSaveSync)
        {
            Task writerToDrain;
            TaskCompletionSource saveCompletion;
            lock (backgroundWriterSync)
            {
                if (disposed) return false;

                synchronousSaveInProgress = true;
                saveCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                synchronousSaveCompletion = saveCompletion;

                // This snapshot supersedes every queued snapshot. A writer that
                // already claimed an older snapshot is allowed to finish first.
                pendingSerializedState = null;
                writerToDrain = backgroundWriterTask;
            }

            try
            {
                writerToDrain.GetAwaiter().GetResult();
                lock (persistenceSync)
                {
                    return PersistSerializedState(serializedState);
                }
            }
            catch (Exception error)
            {
                Log("Could not save settings", error);
                return false;
            }
            finally
            {
                lock (backgroundWriterSync)
                {
                    synchronousSaveInProgress = false;
                    synchronousSaveCompletion = null;
                    if (!disposed) StartBackgroundWriterLocked();
                }
                saveCompletion.TrySetResult();
            }
        }
    }

    private void StartBackgroundWriterLocked()
    {
        if (backgroundWriterRunning
            || synchronousSaveInProgress
            || pendingSerializedState is null
            || disposed)
        {
            return;
        }

        backgroundWriterRunning = true;
        backgroundWriterTask = Task.Run(ProcessBackgroundWrites);
    }

    private void ProcessBackgroundWrites()
    {
        while (true)
        {
            byte[] serializedState;
            lock (backgroundWriterSync)
            {
                if (synchronousSaveInProgress || pendingSerializedState is null)
                {
                    backgroundWriterRunning = false;
                    return;
                }

                serializedState = pendingSerializedState;
                pendingSerializedState = null;
            }

            try
            {
                beforeBackgroundWriteForTesting?.Invoke();
                lock (persistenceSync)
                {
                    _ = PersistSerializedState(serializedState);
                }
            }
            catch (Exception error)
            {
                Log("Could not save settings in the background", error);
            }
        }
    }

    private bool PersistSerializedState(byte[] serializedState)
    {
        try
        {
            var fingerprint = ComputeFingerprint(serializedState);
            if (IsPersistedStateUnchanged(serializedState.Length, fingerprint)) return true;

            var folder = Path.GetDirectoryName(settingsPath)!;
            Directory.CreateDirectory(folder);
            WriteFileAtomically(settingsPath, serializedState);
            // The recovery copy mirrors the committed state. Keeping a prior
            // generation here could resurrect history after privacy deletion.
            WriteFileAtomically(backupPath, serializedState);
            RememberPersistedState(serializedState, fingerprint);
            return true;
        }
        catch (Exception error)
        {
            Log("Could not save settings", error);
            return false;
        }
    }

    public void Dispose()
    {
        byte[]? finalPendingState = null;
        Task writerToDrain = Task.CompletedTask;
        Task synchronousSaveToDrain = Task.CompletedTask;
        Task? existingDispose = null;
        TaskCompletionSource? ownedDispose = null;
        lock (backgroundWriterSync)
        {
            if (disposed)
            {
                existingDispose = disposeCompletion?.Task;
            }
            else
            {
                disposed = true;
                ownedDispose = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                disposeCompletion = ownedDispose;
                finalPendingState = pendingSerializedState;
                pendingSerializedState = null;
                writerToDrain = backgroundWriterTask;
                synchronousSaveToDrain = synchronousSaveCompletion?.Task ?? Task.CompletedTask;
            }
        }

        if (existingDispose is not null)
        {
            existingDispose.GetAwaiter().GetResult();
            return;
        }
        if (ownedDispose is null) return;

        try
        {
            writerToDrain.GetAwaiter().GetResult();
            synchronousSaveToDrain.GetAwaiter().GetResult();
            if (finalPendingState is not null)
            {
                lock (persistenceSync)
                {
                    _ = PersistSerializedState(finalPendingState);
                }
            }
        }
        catch (Exception error)
        {
            Log("Could not finish saving settings", error);
        }
        finally
        {
            ownedDispose.TrySetResult();
        }
    }

    private BrowserState? TryLoadFrom(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumSettingsBytes) return null;

            using var settingsStream = File.OpenRead(path);
            var state = JsonSerializer.Deserialize<BrowserState>(settingsStream);
            if (state is null) return null;

            NormalizeForPersistence(state);
            return state;
        }
        catch
        {
            return null;
        }
    }

    private void RestoreFromBackupToPrimary(BrowserState backupState)
    {
        try
        {
            var serializedState = JsonSerializer.SerializeToUtf8Bytes(backupState, jsonOptions);
            WriteFileAtomically(settingsPath, serializedState);
            RememberPersistedState(serializedState);
        }
        catch
        {
            // Recovery attempts should not block startup.
        }
    }

    private static void WriteFileAtomically(string destinationPath, ReadOnlySpan<byte> bytes)
    {
        var temporaryPath = destinationPath
            + "."
            + Environment.ProcessId
            + "."
            + Guid.NewGuid().ToString("N")
            + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, destinationPath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // A uniquely named stale temp does not endanger either state copy.
            }
        }
    }

    private bool IsPersistedStateUnchanged(int serializedLength, ulong fingerprint)
    {
        if (!hasPersistedFingerprint
            || serializedLength != lastPersistedSerializedLength
            || fingerprint != lastPersistedFingerprint)
        {
            return false;
        }

        try
        {
            var file = new FileInfo(settingsPath);
            return file.Exists
                && file.Length == lastPersistedFileLength
                && file.LastWriteTimeUtc == lastPersistedWriteUtc;
        }
        catch
        {
            return false;
        }
    }

    private void RememberPersistedState(byte[] serializedState, ulong? fingerprint = null)
    {
        try
        {
            var file = new FileInfo(settingsPath);
            if (!file.Exists)
            {
                hasPersistedFingerprint = false;
                return;
            }

            lastPersistedFingerprint = fingerprint ?? ComputeFingerprint(serializedState);
            lastPersistedSerializedLength = serializedState.Length;
            lastPersistedFileLength = file.Length;
            lastPersistedWriteUtc = file.LastWriteTimeUtc;
            hasPersistedFingerprint = true;
        }
        catch
        {
            hasPersistedFingerprint = false;
        }
    }

    private static ulong ComputeFingerprint(ReadOnlySpan<byte> value)
    {
        const ulong offsetBasis = 14_695_981_039_346_656_037;
        const ulong prime = 1_099_511_628_211;
        var fingerprint = offsetBasis;
        foreach (var item in value)
        {
            fingerprint ^= item;
            fingerprint = unchecked(fingerprint * prime);
        }
        return fingerprint;
    }

    public void Log(string operation, Exception error)
    {
        if (!diagnosticsEnabled) return;
        try
        {
            var folder = Path.GetDirectoryName(logPath)!;
            Directory.CreateDirectory(folder);
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 524_288)
            {
                File.Move(logPath, logPath + ".old", true);
            }

            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:O}] {operation}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never interfere with browsing.
        }
    }

    private static bool IsHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string? NormalizeUrlCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var normalized = value.Trim();
        if (normalized.Length > MaximumUrlLength || normalized.Any(char.IsControl)) return null;
        return normalized;
    }

    private static string? NormalizeHttpUrl(string? value)
    {
        var normalized = NormalizeUrlCandidate(value);
        return normalized is not null && IsHttpUrl(normalized) ? normalized : null;
    }

    private static string? NormalizeRestorableUrl(string? value)
    {
        var normalized = NormalizeUrlCandidate(value);
        if (normalized is null) return null;
        if (normalized.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase)) return StartPage.Url;
        return IsHttpUrl(normalized) ? normalized : null;
    }

    internal static string SanitizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var title = new StringBuilder(Math.Min(value.Length, MaximumTitleLength));
        var pendingSpace = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = title.Length > 0;
                continue;
            }

            var characterLength = 1;
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) continue;
                characterLength = 2;
            }
            else if (char.IsLowSurrogate(character))
            {
                continue;
            }

            if (char.GetUnicodeCategory(value, index)
                == System.Globalization.UnicodeCategory.Format)
            {
                if (characterLength == 2) index++;
                continue;
            }

            var spaceLength = pendingSpace && title.Length > 0 ? 1 : 0;
            if (title.Length + spaceLength + characterLength > MaximumTitleLength) break;

            if (spaceLength != 0) title.Append(' ');
            pendingSpace = false;
            title.Append(character);
            if (characterLength == 2) title.Append(value[++index]);
        }

        return title.ToString().TrimEnd();
    }

    private static IEnumerable<BookmarkEntry> NormalizeBookmarks(IEnumerable<BookmarkEntry>? entries)
    {
        if (entries is null) yield break;

        foreach (var item in entries.OfType<BookmarkEntry>())
        {
            var url = NormalizeHttpUrl(item.Url);
            if (url is null) continue;
            yield return new BookmarkEntry(SanitizeTitle(item.Title), url);
        }
    }

    private static IEnumerable<HistoryEntry> NormalizeHistory(IEnumerable<HistoryEntry>? entries)
    {
        if (entries is null) yield break;

        foreach (var item in entries.OfType<HistoryEntry>())
        {
            var url = NormalizeHttpUrl(item.Url);
            if (url is null) continue;
            yield return new HistoryEntry(SanitizeTitle(item.Title), url, item.VisitedUtc);
        }
    }

    private static IEnumerable<PinnedStartPageLink> NormalizePinnedStartPageLinks(
        IEnumerable<PinnedStartPageLink>? entries)
    {
        if (entries is null) yield break;

        foreach (var item in entries.OfType<PinnedStartPageLink>())
        {
            var url = NormalizeHttpUrl(item.Url);
            if (url is null) continue;
            yield return new PinnedStartPageLink(SanitizeTitle(item.Title), url);
        }
    }

    private static IEnumerable<string> NormalizeAdBlockExceptionHosts(IEnumerable<string>? entries)
    {
        if (entries is null) yield break;

        foreach (var item in entries.OfType<string>())
        {
            var host = BrowserPolicy.NormalizeExactHost(item);
            if (host is not null) yield return host;
        }
    }

    private static IEnumerable<AddressSuggestionUsage> NormalizeSuggestionUsage(
        IEnumerable<AddressSuggestionUsage>? entries)
    {
        if (entries is null) yield break;

        var normalizedEntries = new Dictionary<string, AddressSuggestionUsage>(BrowserPolicy.UrlComparer);
        foreach (var item in entries.OfType<AddressSuggestionUsage>())
        {
            var url = NormalizeHttpUrl(item.Url);
            if (url is null) continue;
            var acceptedCount = Math.Max(1, item.AcceptedCount);
            var acceptedAt = item.LastAcceptedUtc == default
                ? DateTimeOffset.UnixEpoch
                : item.LastAcceptedUtc;

            if (normalizedEntries.TryGetValue(url, out var existing))
            {
                acceptedCount = existing.AcceptedCount > int.MaxValue - acceptedCount
                    ? int.MaxValue
                    : existing.AcceptedCount + acceptedCount;
                if (existing.LastAcceptedUtc > acceptedAt) acceptedAt = existing.LastAcceptedUtc;
                url = existing.Url;
            }

            normalizedEntries[url] = new AddressSuggestionUsage(url, acceptedCount, acceptedAt);
        }

        foreach (var item in normalizedEntries.Values)
        {
            yield return item;
        }
    }

    private static IEnumerable<string> NormalizeKeepAwakeHosts(IEnumerable<string>? entries)
    {
        if (entries is null) yield break;

        foreach (var item in entries.OfType<string>())
        {
            var host = BrowserPolicy.NormalizeExactHost(item);
            if (host is not null) yield return host;
        }
    }

    internal static string? NormalizeSessionName(string? value)
    {
        var normalized = SanitizeTitle(value);
        if (normalized.Length == 0) return null;
        return TextSafety.Truncate(normalized, MaximumSessionNameLength).Trim();
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        return value == default ? DateTimeOffset.UnixEpoch : value.ToUniversalTime();
    }

    private static SavedSessionEntry? NormalizeSession(SavedSessionEntry? entry)
    {
        if (entry is null) return null;

        var name = NormalizeSessionName(entry.Name);
        if (name is null) return null;

        var sourceTabs = entry.Tabs ?? [];
        var originalActiveIndex = entry.ActiveTabIndex is int candidate
            && candidate >= 0
            && candidate < sourceTabs.Count
                ? candidate
                : (int?)null;
        var validTabs = new List<(int OriginalIndex, SavedSessionTab Tab)>();
        for (var index = 0; index < sourceTabs.Count; index++)
        {
            var sourceTab = sourceTabs[index];
            if (sourceTab is null) continue;
            var url = NormalizeRestorableUrl(sourceTab.Url);
            if (url is null) continue;
            validTabs.Add((index, new SavedSessionTab(
                url,
                sourceTab.IsPinned && !url.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase))));
        }

        if (validTabs.Count == 0) return null;

        var orderedTabs = validTabs
            .Where(item => item.Tab.IsPinned)
            .Concat(validTabs.Where(item => !item.Tab.IsPinned))
            .Take(MaximumSessionTabs)
            .ToList();
        var tabs = orderedTabs.Select(item => item.Tab).ToList();
        int? activeIndex = null;
        if (originalActiveIndex is int active)
        {
            var mappedIndex = orderedTabs.FindIndex(item => item.OriginalIndex == active);
            if (mappedIndex >= 0) activeIndex = mappedIndex;
        }

        return new SavedSessionEntry(
            name,
            NormalizeTimestamp(entry.UpdatedUtc),
            tabs,
            activeIndex);
    }

    private static IEnumerable<SavedSessionEntry> NormalizeSessions(
        IEnumerable<SavedSessionEntry>? entries)
    {
        if (entries is null) yield break;

        var newestByName = new Dictionary<string, SavedSessionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OfType<SavedSessionEntry>())
        {
            var normalized = NormalizeSession(entry);
            if (normalized is null) continue;
            if (!newestByName.TryGetValue(normalized.Name, out var existing)
                || normalized.UpdatedUtc >= existing.UpdatedUtc)
            {
                newestByName[normalized.Name] = normalized;
            }
        }

        foreach (var entry in newestByName.Values
                     .OrderByDescending(item => item.UpdatedUtc)
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(MaximumNamedSessions))
        {
            yield return entry;
        }
    }

    private static IEnumerable<ClosedTabEntry> NormalizeRecentlyClosed(
        IEnumerable<ClosedTabEntry>? entries)
    {
        if (entries is null) yield break;

        foreach (var item in entries.OfType<ClosedTabEntry>()
                     .Select(item =>
                     {
                         var url = NormalizeRestorableUrl(item.Url);
                         return url is null
                             ? null
                             : new ClosedTabEntry(
                                 SanitizeTitle(item.Title),
                                 url,
                                 NormalizeTimestamp(item.ClosedUtc));
                     })
                     .OfType<ClosedTabEntry>()
                     .OrderByDescending(item => item.ClosedUtc)
                     .Take(MaximumRecentlyClosed))
        {
            yield return item;
        }
    }

    private static IEnumerable<SiteZoomEntry> NormalizeSiteZoom(
        IEnumerable<SiteZoomEntry>? entries)
    {
        if (entries is null) yield break;

        var normalizedByHost = new Dictionary<string, SiteZoomEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries.OfType<SiteZoomEntry>())
        {
            var host = BrowserPolicy.NormalizeExactHost(item.Host);
            if (host is null || double.IsNaN(item.ZoomFactor) || double.IsInfinity(item.ZoomFactor)) continue;

            var factor = Math.Clamp(item.ZoomFactor, MinimumSiteZoom, MaximumSiteZoom);
            if (Math.Abs(factor - 1.0) < 0.000_001)
            {
                normalizedByHost.Remove(host);
                continue;
            }

            normalizedByHost[host] = new SiteZoomEntry(host, factor);
        }

        foreach (var item in normalizedByHost.Values.Take(MaximumSiteZoomEntries)) yield return item;
    }

    private static IEnumerable<string> NormalizeExactHostList(IEnumerable<string>? entries, int maxCount)
    {
        if (entries is null) yield break;

        foreach (var host in entries.OfType<string>()
                     .Select(BrowserPolicy.NormalizeExactHost)
                     .OfType<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(maxCount))
        {
            yield return host;
        }
    }

    private static IEnumerable<string> NormalizeMutedHosts(IEnumerable<string>? entries)
    {
        return NormalizeExactHostList(entries, MaximumMutedHosts);
    }

    internal static void NormalizeForPersistence(BrowserState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.SearchProviderId = BrowserPolicy.NormalizeSearchProviderId(state.SearchProviderId);
        NormalizeOpenTabs(state);
        state.Bookmarks = NormalizeBookmarks(state.Bookmarks)
            .DistinctBy(item => item.Url, BrowserPolicy.UrlComparer)
            .Take(MaximumBookmarks)
            .ToList();
        state.History = NormalizeHistory(state.History)
            .OrderByDescending(item => item.VisitedUtc)
            .DistinctBy(item => item.Url, BrowserPolicy.UrlComparer)
            .Take(MaximumHistoryEntries)
            .ToList();
        state.PinnedStartPageLinks = NormalizePinnedStartPageLinks(state.PinnedStartPageLinks)
            .DistinctBy(item => item.Url, BrowserPolicy.UrlComparer)
            .Take(MaximumPinnedStartPageLinks)
            .ToList();
        state.NamedSessions = NormalizeSessions(state.NamedSessions).ToList();
        state.RecentlyClosed = NormalizeRecentlyClosed(state.RecentlyClosed).ToList();
        state.SiteZoom = NormalizeSiteZoom(state.SiteZoom).ToList();
        state.MutedHosts = NormalizeMutedHosts(state.MutedHosts).ToList();
        state.AudioOutputPolicy = Enum.IsDefined(state.AudioOutputPolicy)
            ? state.AudioOutputPolicy
            : AudioOutputPolicy.AllowAll;
        state.AllowedAudioHosts = NormalizeExactHostList(state.AllowedAudioHosts, MaximumAudioHosts).ToList();
        state.AudioInputPolicy = Enum.IsDefined(state.AudioInputPolicy)
            ? state.AudioInputPolicy
            : AudioInputPolicy.AskEveryTime;
        state.AllowedMicrophoneHosts = NormalizeExactHostList(state.AllowedMicrophoneHosts, MaximumMicrophoneHosts).ToList();
        state.BlockedMicrophoneHosts = NormalizeExactHostList(state.BlockedMicrophoneHosts, MaximumMicrophoneHosts).ToList();
        state.AdBlockExceptionHosts = NormalizeAdBlockExceptionHosts(state.AdBlockExceptionHosts)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumAdBlockExceptionHosts)
            .ToList();
        state.DismissedSuggestionUrls = (state.DismissedSuggestionUrls ?? [])
            .OfType<string>()
            .Select(NormalizeHttpUrl)
            .OfType<string>()
            .Distinct(BrowserPolicy.UrlComparer)
            .Take(MaximumDismissedSuggestions)
            .ToList();
        state.SuggestionUsage = NormalizeSuggestionUsage(state.SuggestionUsage)
            .OrderByDescending(item => item.AcceptedCount)
            .ThenByDescending(item => item.LastAcceptedUtc)
            .Take(MaximumSuggestionUsageEntries)
            .ToList();
        state.KeepAwakeHosts = NormalizeKeepAwakeHosts(state.KeepAwakeHosts)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumKeepAwakeHosts)
            .ToList();
        state.WindowPlacement = NormalizeWindowPlacement(state.WindowPlacement);
    }

    private static SavedWindowPlacement? NormalizeWindowPlacement(SavedWindowPlacement? placement)
    {
        if (placement is null
            || placement.Width < 320
            || placement.Height < 240
            || placement.Width > 65_536
            || placement.Height > 65_536
            || placement.X is < -262_144 or > 262_144
            || placement.Y is < -262_144 or > 262_144)
        {
            return null;
        }

        var presentation = placement.Presentation is
            SavedWindowPresentation.Normal or
            SavedWindowPresentation.Maximized or
            SavedWindowPresentation.FullScreen
                ? placement.Presentation
                : SavedWindowPresentation.Normal;
        return placement with
        {
            Presentation = presentation,
            RestoreMaximizedAfterFullScreen = presentation == SavedWindowPresentation.FullScreen
                && placement.RestoreMaximizedAfterFullScreen
        };
    }

    internal static void NormalizeOpenTabs(BrowserState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var originalTabs = state.OpenTabs ?? [];
        var originalActiveIndex = NormalizeActiveTabIndex(state.ActiveTabIndex, originalTabs);
        var originalPinnedCount = Math.Clamp(state.PinnedOpenTabCount, 0, originalTabs.Count);
        var validTabs = new List<(int OriginalIndex, string Url, bool IsPinned)>();
        for (var index = 0; index < originalTabs.Count; index++)
        {
            var url = NormalizeRestorableUrl(originalTabs[index]);
            if (url is null) continue;
            validTabs.Add((
                index,
                url,
                index < originalPinnedCount
                    && !url.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase)));
        }

        var orderedTabs = validTabs
            .Where(item => item.IsPinned)
            .Concat(validTabs.Where(item => !item.IsPinned))
            .Take(MaximumOpenTabs)
            .ToList();
        var normalizedTabs = orderedTabs.Select(item => item.Url).ToList();
        int? normalizedActiveIndex = null;

        if (originalActiveIndex is int active)
        {
            var mappedIndex = orderedTabs.FindIndex(item => item.OriginalIndex == active);
            if (mappedIndex >= 0) normalizedActiveIndex = mappedIndex;
        }

        state.OpenTabs = normalizedTabs;
        state.ActiveTabIndex = normalizedActiveIndex;
        state.PinnedOpenTabCount = Math.Min(
            orderedTabs.TakeWhile(item => item.IsPinned).Count(),
            MaximumPinnedOpenTabs);
    }

    private byte[] SerializeWithinLimit(BrowserState state)
    {
        var serializedState = JsonSerializer.SerializeToUtf8Bytes(state, jsonOptions);
        while (serializedState.LongLength > MaximumSettingsBytes)
        {
            if (!RemoveOldestHalf(state.History)
                && !RemoveOldestHalf(state.DismissedSuggestionUrls)
                && !RemoveOldestHalf(state.SuggestionUsage)
                && !RemoveOldestHalf(state.RecentlyClosed)
                && !RemoveOldestHalf(state.Bookmarks)
                && !RemoveOldestHalf(state.NamedSessions))
            {
                throw new InvalidOperationException("Normalized settings exceed the persistence limit.");
            }

            serializedState = JsonSerializer.SerializeToUtf8Bytes(state, jsonOptions);
        }

        return serializedState;
    }

    private static bool RemoveOldestHalf<T>(List<T> items)
    {
        if (items.Count == 0) return false;

        var removeCount = Math.Max(1, items.Count / 2);
        items.RemoveRange(items.Count - removeCount, removeCount);
        return true;
    }

    private static int? NormalizeActiveTabIndex(int? value, IReadOnlyList<string> openTabs)
    {
        if (value is not int candidate) return null;
        if (candidate < 0 || candidate >= openTabs.Count) return null;
        return candidate;
    }
}
