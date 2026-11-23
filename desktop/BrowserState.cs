using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MishaWeb;

[Flags]
internal enum BrowserStateDirtySections
{
    None = 0,
    Bookmarks = 1 << 0,
    History = 1 << 1,
    PinnedStartPageLinks = 1 << 2,
    NamedSessions = 1 << 3,
    RecentlyClosed = 1 << 4,
    SiteZoom = 1 << 5,
    MutedHosts = 1 << 6,
    AllowedAudioHosts = 1 << 7,
    AllowedMicrophoneHosts = 1 << 8,
    BlockedMicrophoneHosts = 1 << 9,
    AdBlockExceptionHosts = 1 << 10,
    DismissedSuggestionUrls = 1 << 11,
    SuggestionUsage = 1 << 12,
    KeepAwakeHosts = 1 << 13,
    RecentDownloads = 1 << 14,
    DarkModeExcludedHosts = 1 << 15,
    All = ~0
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(BrowserState))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class BrowserStateJsonContext : JsonSerializerContext;

internal sealed class BrowserState
{
    internal long StateRevision { get; private set; }
    internal BrowserStateDirtySections DirtySections { get; private set; } = BrowserStateDirtySections.All;
    internal BrowserStateNormalizationCache? NormalizationCache { get; set; }

    internal void MarkDirty(BrowserStateDirtySections sections = BrowserStateDirtySections.All)
    {
        DirtySections |= sections;
        StateRevision++;
    }

    internal void ClearDirtySections(BrowserStateDirtySections sections)
    {
        DirtySections &= ~sections;
    }

    internal void BumpRevision()
    {
        StateRevision++;
    }
    public string SearchProviderId { get; set; } = BrowserPolicy.DefaultSearchProviderId;
    public bool ReduceWebsiteMotionEnabled { get; set; }
    public bool MemorySaverEnabled { get; set; } = true;
    public bool? UltraLightModeEnabled { get; set; } = false;
    public int? ResourceModeDefaultsVersion { get; set; }
    public bool DarkModeEnabled { get; set; } = true;
    public bool AdBlockEnabled { get; set; } = true;
    public bool EnableLiveSearchSuggestions { get; set; } = true;
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
    public List<RecentDownloadEntry> RecentDownloads { get; set; } = [];
    public List<string> DarkModeExcludedHosts { get; set; } = [];
    public bool CleanShutdown { get; set; } = true;
    public SavedWindowPlacement? WindowPlacement { get; set; }
    public SavedMultiViewLayout? MultiViewLayout { get; set; }

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

internal sealed record RecentDownloadEntry(
    string FileName,
    string SourceUrl,
    string TargetPath,
    string State,
    DateTimeOffset CompletedAtUtc);

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

internal sealed record SavedMultiViewLayout(
    string Preset,
    int[] PaneTabIndices,
    int FocusedPaneIndex,
    float HorizontalRatio = 0.5f,
    float VerticalRatio = 0.5f);

internal sealed class BrowserStateNormalizationCache
{
    public List<BookmarkEntry>? BookmarksList;
    public BookmarkEntry[]? BookmarksItems;
    public List<HistoryEntry>? HistoryList;
    public HistoryEntry[]? HistoryItems;
    public List<PinnedStartPageLink>? PinnedStartPageLinksList;
    public PinnedStartPageLink[]? PinnedStartPageLinksItems;
    public List<SavedSessionEntry>? NamedSessionsList;
    public SavedSessionEntry[]? NamedSessionsItems;
    public List<SavedSessionTab>[]? NamedSessionTabLists;
    public SavedSessionTab[][]? NamedSessionTabItems;
    public List<ClosedTabEntry>? RecentlyClosedList;
    public ClosedTabEntry[]? RecentlyClosedItems;
    public List<SiteZoomEntry>? SiteZoomList;
    public SiteZoomEntry[]? SiteZoomItems;
    public List<string>? MutedHostsList;
    public string[]? MutedHostsItems;
    public List<string>? AllowedAudioHostsList;
    public string[]? AllowedAudioHostsItems;
    public List<string>? AllowedMicrophoneHostsList;
    public string[]? AllowedMicrophoneHostsItems;
    public List<string>? BlockedMicrophoneHostsList;
    public string[]? BlockedMicrophoneHostsItems;
    public List<string>? AdBlockExceptionHostsList;
    public string[]? AdBlockExceptionHostsItems;
    public List<string>? DismissedSuggestionUrlsList;
    public string[]? DismissedSuggestionUrlsItems;
    public List<AddressSuggestionUsage>? SuggestionUsageList;
    public AddressSuggestionUsage[]? SuggestionUsageItems;
    public List<string>? KeepAwakeHostsList;
    public string[]? KeepAwakeHostsItems;
    public List<RecentDownloadEntry>? RecentDownloadsList;
    public RecentDownloadEntry[]? RecentDownloadsItems;
    public List<string>? DarkModeExcludedHostsList;
    public string[]? DarkModeExcludedHostsItems;
}

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
    internal const int MaximumRecentDownloads = 50;
    internal const int MaximumDarkModeExcludedHosts = 200;
    internal const double MinimumSiteZoom = 0.25;
    internal const double MaximumSiteZoom = 3.0;
    private readonly string settingsPath;
    private readonly string backupPath;
    private readonly string logPath;
    private readonly bool diagnosticsEnabled;
    private readonly object backgroundWriterSync = new();
    private readonly object synchronousSaveSync = new();
    private readonly object persistenceSync = new();
    private static readonly object LogSync = new();
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
            var primary = TryLoadFrom(settingsPath, out var primaryRawBytes, out var primaryFileInfo);
            if (primary is not null)
            {
                if (primaryRawBytes is not null)
                {
                    RememberPersistedState(primaryRawBytes, loadedFileInfo: primaryFileInfo);
                }
                return primary;
            }

            var backup = TryLoadFrom(backupPath, out _, out _);
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
            NormalizeForPersistenceCore(state, forceFull: false);
            serializedState = SerializeWithinLimit(state);
        }
        catch (Exception error)
        {
            Log("Could not save settings", error);
            return false;
        }

        return SaveSynchronously(serializedState);
    }

    internal Task<bool> FlushAsync(BrowserState state, CancellationToken cancellationToken = default)
    {
        byte[] serializedState;
        try
        {
            NormalizeForPersistenceCore(state, forceFull: false);
            serializedState = SerializeWithinLimit(state);
        }
        catch (Exception error)
        {
            Log("Could not save settings", error);
            return Task.FromResult(false);
        }

        return Task.Run(() => SaveSynchronously(serializedState), cancellationToken);
    }

    internal bool QueueSave(BrowserState state)
    {
        byte[] serializedState;
        try
        {
            // The WinForms thread owns BrowserState. Normalize and serialize it
            // before handing an immutable snapshot to the writer so the worker
            // never races later UI mutations.
            NormalizeForPersistenceCore(state, forceFull: false);
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
            // The recovery copy mirrors the committed state. Copying after the
            // atomic WriteThrough commit avoids a second FlushFileBuffers barrier
            // while still ensuring backup never retains stale pre-deletion history.
            File.Copy(settingsPath, backupPath, overwrite: true);
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

    private BrowserState? TryLoadFrom(string path, out byte[]? rawBytes, out FileInfo? loadedFile)
    {
        rawBytes = null;
        loadedFile = null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumSettingsBytes) return null;

            var bytes = File.ReadAllBytes(path);
            if (bytes.LongLength > MaximumSettingsBytes) return null;

            var state = JsonSerializer.Deserialize(bytes, BrowserStateJsonContext.Default.BrowserState);
            if (state is null) return null;

            NormalizeForPersistence(state);
            rawBytes = bytes;
            loadedFile = file;
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
            var serializedState = JsonSerializer.SerializeToUtf8Bytes(backupState, BrowserStateJsonContext.Default.BrowserState);
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
        var moved = false;
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
            moved = true;
        }
        finally
        {
            if (!moved)
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

    private void RememberPersistedState(
        byte[] serializedState,
        ulong? fingerprint = null,
        FileInfo? loadedFileInfo = null)
    {
        try
        {
            var file = loadedFileInfo ?? new FileInfo(settingsPath);
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
        const ulong offsetBasis = 14_695_981_039_346_656_037UL;
        const ulong prime = 1_099_511_628_211UL;
        var fingerprint = offsetBasis ^ (ulong)value.Length;
        var chunks = MemoryMarshal.Cast<byte, ulong>(value);
        for (var index = 0; index < chunks.Length; index++)
        {
            fingerprint ^= chunks[index];
            fingerprint = unchecked(fingerprint * prime);
            fingerprint ^= fingerprint >> 33;
        }

        var tailStart = chunks.Length * sizeof(ulong);
        for (var index = tailStart; index < value.Length; index++)
        {
            fingerprint ^= value[index];
            fingerprint = unchecked(fingerprint * prime);
        }
        return fingerprint;
    }

    public void Log(string operation, Exception error)
    {
        if (!diagnosticsEnabled) return;
        try
        {
            var entry = $"[{DateTimeOffset.Now:O}] {operation}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}";
            lock (LogSync)
            {
                var folder = Path.GetDirectoryName(logPath)!;
                Directory.CreateDirectory(folder);
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 524_288)
                {
                    File.Move(logPath, logPath + ".old", true);
                }

                File.AppendAllText(logPath, entry);
            }
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
        if (IsAlreadyCleanTitle(value)) return value;

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

    private static bool IsAlreadyCleanTitle(string value)
    {
        if (value.Length > MaximumTitleLength) return false;
        var first = value[0];
        var last = value[^1];
        if (first <= ' ' || last <= ' ' || char.IsWhiteSpace(first) || char.IsWhiteSpace(last))
        {
            return false;
        }

        var previousWasSpace = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character > ' ' && character < 0x7F)
            {
                previousWasSpace = false;
                continue;
            }
            if (character == ' ')
            {
                if (previousWasSpace) return false;
                previousWasSpace = true;
                continue;
            }
            if (character < ' '
                || char.IsControl(character)
                || char.IsWhiteSpace(character)
                || char.IsSurrogate(character)
                || (character >= 0x00AD
                    && char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format))
            {
                return false;
            }
            previousWasSpace = false;
        }

        return true;
    }

    private static IEnumerable<BookmarkEntry> NormalizeBookmarks(IEnumerable<BookmarkEntry>? entries)
    {
        if (entries is null) yield break;

        foreach (var item in entries.OfType<BookmarkEntry>())
        {
            var url = NormalizeHttpUrl(item.Url);
            if (url is null) continue;
            var title = SanitizeTitle(item.Title);
            yield return string.Equals(item.Title, title, StringComparison.Ordinal)
                && string.Equals(item.Url, url, StringComparison.Ordinal)
                ? item
                : new BookmarkEntry(title, url);
        }
    }

    private static IEnumerable<HistoryEntry> NormalizeHistory(IEnumerable<HistoryEntry>? entries)
    {
        if (entries is null) yield break;

        foreach (var item in entries.OfType<HistoryEntry>())
        {
            var url = NormalizeHttpUrl(item.Url);
            if (url is null) continue;
            var title = SanitizeTitle(item.Title);
            yield return string.Equals(item.Title, title, StringComparison.Ordinal)
                && string.Equals(item.Url, url, StringComparison.Ordinal)
                ? item
                : new HistoryEntry(title, url, item.VisitedUtc);
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
            var title = SanitizeTitle(item.Title);
            yield return string.Equals(item.Title, title, StringComparison.Ordinal)
                && string.Equals(item.Url, url, StringComparison.Ordinal)
                ? item
                : new PinnedStartPageLink(title, url);
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
                normalizedEntries[url] = new AddressSuggestionUsage(url, acceptedCount, acceptedAt);
            }
            else
            {
                normalizedEntries[url] = string.Equals(item.Url, url, StringComparison.Ordinal)
                    && item.AcceptedCount == acceptedCount
                    && item.LastAcceptedUtc == acceptedAt
                    ? item
                    : new AddressSuggestionUsage(url, acceptedCount, acceptedAt);
            }
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

    private static string NormalizeDownloadSourceUrl(string? value)
    {
        var candidate = NormalizeUrlCandidate(value);
        if (candidate is null) return string.Empty;
        if (IsHttpUrl(candidate)) return candidate;
        if (candidate.StartsWith("blob:http://", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("blob:https://", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }
        return string.Empty;
    }

    private static IEnumerable<RecentDownloadEntry> NormalizeRecentDownloads(
        IEnumerable<RecentDownloadEntry>? entries)
    {
        if (entries is null) yield break;

        foreach (var item in entries.OfType<RecentDownloadEntry>())
        {
            var targetPath = NormalizeUrlCandidate(item.TargetPath) ?? string.Empty;
            var fileName = SanitizeTitle(item.FileName);
            if (fileName.Length == 0 && targetPath.Length > 0)
            {
                try
                {
                    fileName = SanitizeTitle(Path.GetFileName(targetPath));
                }
                catch
                {
                    fileName = string.Empty;
                }
            }

            if (fileName.Length == 0) continue;

            var sourceUrl = NormalizeDownloadSourceUrl(item.SourceUrl);
            var state = TextSafety.SanitizeSingleLine(item.State, 40);
            if (state.Length == 0) state = "Complete";
            var completedAt = NormalizeTimestamp(item.CompletedAtUtc);

            yield return string.Equals(item.FileName, fileName, StringComparison.Ordinal)
                && string.Equals(item.SourceUrl, sourceUrl, StringComparison.Ordinal)
                && string.Equals(item.TargetPath, targetPath, StringComparison.Ordinal)
                && string.Equals(item.State, state, StringComparison.Ordinal)
                && item.CompletedAtUtc == completedAt
                    ? item
                    : new RecentDownloadEntry(fileName, sourceUrl, targetPath, state, completedAt);
        }
    }

    internal static void NormalizeForPersistence(BrowserState state)
    {
        NormalizeForPersistenceCore(state, forceFull: true);
    }

    private static void NormalizeForPersistenceCore(BrowserState state, bool forceFull)
    {
        ArgumentNullException.ThrowIfNull(state);

        var cache = state.NormalizationCache ??= new BrowserStateNormalizationCache();
        var dirty = forceFull ? BrowserStateDirtySections.All : state.DirtySections;
        var anyCollectionMutated = false;

        state.SearchProviderId = BrowserPolicy.NormalizeSearchProviderId(state.SearchProviderId);
        NormalizeOpenTabs(state);

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.Bookmarks, state.Bookmarks, cache.BookmarksList, cache.BookmarksItems))
        {
            state.Bookmarks = NormalizeBookmarks(state.Bookmarks)
                .DistinctBy(item => item.Url, BrowserPolicy.UrlComparer)
                .Take(MaximumBookmarks)
                .ToList();
            CaptureSnapshot(state.Bookmarks, out cache.BookmarksList, out cache.BookmarksItems);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.History, state.History, cache.HistoryList, cache.HistoryItems))
        {
            state.History = NormalizeHistory(state.History)
                .OrderByDescending(item => item.VisitedUtc)
                .DistinctBy(item => item.Url, BrowserPolicy.UrlComparer)
                .Take(MaximumHistoryEntries)
                .ToList();
            CaptureSnapshot(state.History, out cache.HistoryList, out cache.HistoryItems);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.PinnedStartPageLinks, state.PinnedStartPageLinks, cache.PinnedStartPageLinksList, cache.PinnedStartPageLinksItems))
        {
            state.PinnedStartPageLinks = NormalizePinnedStartPageLinks(state.PinnedStartPageLinks)
                .DistinctBy(item => item.Url, BrowserPolicy.UrlComparer)
                .Take(MaximumPinnedStartPageLinks)
                .ToList();
            CaptureSnapshot(state.PinnedStartPageLinks, out cache.PinnedStartPageLinksList, out cache.PinnedStartPageLinksItems);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeSessions(dirty, state.NamedSessions, cache))
        {
            state.NamedSessions = NormalizeSessions(state.NamedSessions).ToList();
            CaptureSessionsSnapshot(state.NamedSessions, cache);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.RecentlyClosed, state.RecentlyClosed, cache.RecentlyClosedList, cache.RecentlyClosedItems))
        {
            state.RecentlyClosed = NormalizeRecentlyClosed(state.RecentlyClosed).ToList();
            CaptureSnapshot(state.RecentlyClosed, out cache.RecentlyClosedList, out cache.RecentlyClosedItems);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.SiteZoom, state.SiteZoom, cache.SiteZoomList, cache.SiteZoomItems))
        {
            state.SiteZoom = NormalizeSiteZoom(state.SiteZoom).ToList();
            CaptureSnapshot(state.SiteZoom, out cache.SiteZoomList, out cache.SiteZoomItems);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.MutedHosts, state.MutedHosts, cache.MutedHostsList, cache.MutedHostsItems))
        {
            state.MutedHosts = NormalizeMutedHosts(state.MutedHosts).ToList();
            CaptureSnapshot(state.MutedHosts, out cache.MutedHostsList, out cache.MutedHostsItems);
            anyCollectionMutated = true;
        }

        state.AudioOutputPolicy = Enum.IsDefined(state.AudioOutputPolicy)
            ? state.AudioOutputPolicy
            : AudioOutputPolicy.AllowAll;

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.AllowedAudioHosts, state.AllowedAudioHosts, cache.AllowedAudioHostsList, cache.AllowedAudioHostsItems))
        {
            state.AllowedAudioHosts = NormalizeExactHostList(state.AllowedAudioHosts, MaximumAudioHosts).ToList();
            CaptureSnapshot(state.AllowedAudioHosts, out cache.AllowedAudioHostsList, out cache.AllowedAudioHostsItems);
            anyCollectionMutated = true;
        }

        state.AudioInputPolicy = Enum.IsDefined(state.AudioInputPolicy)
            ? state.AudioInputPolicy
            : AudioInputPolicy.AskEveryTime;

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.AllowedMicrophoneHosts, state.AllowedMicrophoneHosts, cache.AllowedMicrophoneHostsList, cache.AllowedMicrophoneHostsItems))
        {
            state.AllowedMicrophoneHosts = NormalizeExactHostList(state.AllowedMicrophoneHosts, MaximumMicrophoneHosts).ToList();
            CaptureSnapshot(state.AllowedMicrophoneHosts, out cache.AllowedMicrophoneHostsList, out cache.AllowedMicrophoneHostsItems);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.BlockedMicrophoneHosts, state.BlockedMicrophoneHosts, cache.BlockedMicrophoneHostsList, cache.BlockedMicrophoneHostsItems))
        {
            state.BlockedMicrophoneHosts = NormalizeExactHostList(state.BlockedMicrophoneHosts, MaximumMicrophoneHosts).ToList();
            CaptureSnapshot(state.BlockedMicrophoneHosts, out cache.BlockedMicrophoneHostsList, out cache.BlockedMicrophoneHostsItems);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.AdBlockExceptionHosts, state.AdBlockExceptionHosts, cache.AdBlockExceptionHostsList, cache.AdBlockExceptionHostsItems))
        {
            state.AdBlockExceptionHosts = NormalizeAdBlockExceptionHosts(state.AdBlockExceptionHosts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumAdBlockExceptionHosts)
                .ToList();
            CaptureSnapshot(state.AdBlockExceptionHosts, out cache.AdBlockExceptionHostsList, out cache.AdBlockExceptionHostsItems);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.DismissedSuggestionUrls, state.DismissedSuggestionUrls, cache.DismissedSuggestionUrlsList, cache.DismissedSuggestionUrlsItems))
        {
            state.DismissedSuggestionUrls = (state.DismissedSuggestionUrls ?? [])
                .OfType<string>()
                .Select(NormalizeHttpUrl)
                .OfType<string>()
                .Distinct(BrowserPolicy.UrlComparer)
                .Take(MaximumDismissedSuggestions)
                .ToList();
            CaptureSnapshot(state.DismissedSuggestionUrls, out cache.DismissedSuggestionUrlsList, out cache.DismissedSuggestionUrlsItems);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.SuggestionUsage, state.SuggestionUsage, cache.SuggestionUsageList, cache.SuggestionUsageItems))
        {
            state.SuggestionUsage = NormalizeSuggestionUsage(state.SuggestionUsage)
                .OrderByDescending(item => item.AcceptedCount)
                .ThenByDescending(item => item.LastAcceptedUtc)
                .Take(MaximumSuggestionUsageEntries)
                .ToList();
            CaptureSnapshot(state.SuggestionUsage, out cache.SuggestionUsageList, out cache.SuggestionUsageItems);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.KeepAwakeHosts, state.KeepAwakeHosts, cache.KeepAwakeHostsList, cache.KeepAwakeHostsItems))
        {
            state.KeepAwakeHosts = NormalizeKeepAwakeHosts(state.KeepAwakeHosts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumKeepAwakeHosts)
                .ToList();
            CaptureSnapshot(state.KeepAwakeHosts, out cache.KeepAwakeHostsList, out cache.KeepAwakeHostsItems);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.RecentDownloads, state.RecentDownloads, cache.RecentDownloadsList, cache.RecentDownloadsItems))
        {
            state.RecentDownloads = NormalizeRecentDownloads(state.RecentDownloads)
                .OrderByDescending(item => item.CompletedAtUtc)
                .Take(MaximumRecentDownloads)
                .ToList();
            CaptureSnapshot(state.RecentDownloads, out cache.RecentDownloadsList, out cache.RecentDownloadsItems);
            anyCollectionMutated = true;
        }

        if (ShouldNormalizeCollection(dirty, BrowserStateDirtySections.DarkModeExcludedHosts, state.DarkModeExcludedHosts, cache.DarkModeExcludedHostsList, cache.DarkModeExcludedHostsItems))
        {
            state.DarkModeExcludedHosts = NormalizeExactHostList(
                    state.DarkModeExcludedHosts,
                    MaximumDarkModeExcludedHosts)
                .ToList();
            CaptureSnapshot(state.DarkModeExcludedHosts, out cache.DarkModeExcludedHostsList, out cache.DarkModeExcludedHostsItems);
            anyCollectionMutated = true;
        }

        state.WindowPlacement = NormalizeWindowPlacement(state.WindowPlacement);
        state.ClearDirtySections(BrowserStateDirtySections.All);
        if (anyCollectionMutated) state.BumpRevision();
    }

    private static bool ShouldNormalizeCollection<T>(
        BrowserStateDirtySections dirty,
        BrowserStateDirtySections section,
        List<T>? current,
        List<T>? expectedList,
        T[]? expectedItems)
        where T : class
    {
        if ((dirty & section) != 0) return true;
        return !IsListReferenceUnchanged(current, expectedList, expectedItems);
    }

    private static bool IsListReferenceUnchanged<T>(
        List<T>? current,
        List<T>? expectedList,
        T[]? expectedItems)
        where T : class
    {
        if (current is null || !ReferenceEquals(current, expectedList) || expectedItems is null)
        {
            return false;
        }

        var span = CollectionsMarshal.AsSpan(current);
        if (span.Length != expectedItems.Length) return false;
        for (var index = 0; index < span.Length; index++)
        {
            if (!ReferenceEquals(span[index], expectedItems[index])) return false;
        }
        return true;
    }

    private static void CaptureSnapshot<T>(List<T> list, out List<T> targetList, out T[] targetItems)
        where T : class
    {
        targetList = list;
        targetItems = list.Count == 0 ? Array.Empty<T>() : list.ToArray();
    }

    private static bool ShouldNormalizeSessions(
        BrowserStateDirtySections dirty,
        List<SavedSessionEntry>? current,
        BrowserStateNormalizationCache cache)
    {
        if ((dirty & BrowserStateDirtySections.NamedSessions) != 0) return true;
        if (!IsListReferenceUnchanged(current, cache.NamedSessionsList, cache.NamedSessionsItems)) return true;
        if (current is null || cache.NamedSessionTabLists is null || cache.NamedSessionTabItems is null) return true;
        if (current.Count != cache.NamedSessionTabLists.Length) return true;
        for (var index = 0; index < current.Count; index++)
        {
            if (!IsListReferenceUnchanged(
                current[index].Tabs,
                cache.NamedSessionTabLists[index],
                cache.NamedSessionTabItems[index]))
            {
                return true;
            }
        }
        return false;
    }

    private static void CaptureSessionsSnapshot(
        List<SavedSessionEntry> sessions,
        BrowserStateNormalizationCache cache)
    {
        CaptureSnapshot(sessions, out cache.NamedSessionsList, out cache.NamedSessionsItems);
        if (sessions.Count == 0)
        {
            cache.NamedSessionTabLists = Array.Empty<List<SavedSessionTab>>();
            cache.NamedSessionTabItems = Array.Empty<SavedSessionTab[]>();
            return;
        }

        var tabLists = new List<SavedSessionTab>[sessions.Count];
        var tabItems = new SavedSessionTab[sessions.Count][];
        for (var index = 0; index < sessions.Count; index++)
        {
            var tabs = sessions[index].Tabs;
            tabLists[index] = tabs;
            tabItems[index] = tabs.Count == 0 ? Array.Empty<SavedSessionTab>() : tabs.ToArray();
        }
        cache.NamedSessionTabLists = tabLists;
        cache.NamedSessionTabItems = tabItems;
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
        state.MultiViewLayout = NormalizeMultiViewLayout(state.MultiViewLayout, orderedTabs);
    }

    private static SavedMultiViewLayout? NormalizeMultiViewLayout(
        SavedMultiViewLayout? layout,
        List<(int OriginalIndex, string Url, bool IsPinned)> orderedTabs)
    {
        if (layout is null || layout.PaneTabIndices is null || layout.PaneTabIndices.Length < 2)
        {
            return null;
        }

        var remapped = new List<int>(4);
        foreach (var rawIndex in layout.PaneTabIndices)
        {
            var mappedIndex = orderedTabs.FindIndex(item => item.OriginalIndex == rawIndex);
            if (mappedIndex >= 0 && !remapped.Contains(mappedIndex))
            {
                remapped.Add(mappedIndex);
                if (remapped.Count >= 4) break;
            }
        }

        if (remapped.Count < 2) return null;

        var presetName = remapped.Count switch
        {
            2 => "Split",
            3 => "Tri",
            _ => "Quad"
        };
        var focusedIndex = Math.Clamp(layout.FocusedPaneIndex, 0, remapped.Count - 1);
        var hRatio = float.IsFinite(layout.HorizontalRatio)
            ? Math.Clamp(layout.HorizontalRatio, 0.22f, 0.78f)
            : 0.5f;
        var vRatio = float.IsFinite(layout.VerticalRatio)
            ? Math.Clamp(layout.VerticalRatio, 0.25f, 0.75f)
            : 0.5f;
        return new SavedMultiViewLayout(
            presetName,
            remapped.ToArray(),
            focusedIndex,
            hRatio,
            vRatio);
    }

    private byte[] SerializeWithinLimit(BrowserState state)
    {
        var serializedState = JsonSerializer.SerializeToUtf8Bytes(state, BrowserStateJsonContext.Default.BrowserState);
        while (serializedState.LongLength > MaximumSettingsBytes)
        {
            if (!RemoveOldestHalf(state.History)
                && !RemoveOldestHalf(state.DismissedSuggestionUrls)
                && !RemoveOldestHalf(state.SuggestionUsage)
                && !RemoveOldestHalf(state.RecentlyClosed)
                && !RemoveOldestHalf(state.RecentDownloads)
                && !RemoveOldestHalf(state.Bookmarks)
                && !RemoveOldestHalf(state.NamedSessions))
            {
                throw new InvalidOperationException("Normalized settings exceed the persistence limit.");
            }

            serializedState = JsonSerializer.SerializeToUtf8Bytes(state, BrowserStateJsonContext.Default.BrowserState);
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
