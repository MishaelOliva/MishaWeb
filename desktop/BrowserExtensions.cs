using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MishaWeb;

internal sealed record PreparedBrowserExtension(
    string FolderPath,
    string Name,
    string Version,
    string? StoreId,
    string? LaunchPath,
    IReadOnlyList<string> RequestedCapabilities,
    bool IsManaged,
    string? PopupPath = null,
    string? OptionsPath = null,
    string? Description = null,
    string? IconPath = null);

internal sealed record ManagedBrowserExtension(
    string Id,
    string FolderPath,
    string Name,
    string Version,
    string? StoreId,
    string? LaunchPath,
    IReadOnlyList<string>? RequestedCapabilities = null,
    string? PopupPath = null,
    string? OptionsPath = null,
    string? Description = null,
    string? IconPath = null);

internal sealed record PreparedBrowserExtensionUpdate(
    ManagedBrowserExtension Current,
    PreparedBrowserExtension Prepared,
    IReadOnlyList<string> AddedCapabilities);

internal sealed record ExtensionUpdateEvaluation(
    bool IsUpdate,
    IReadOnlyList<string> AddedCapabilities);

internal sealed class BrowserExtensions
{
    private const int MaximumScriptBytes = 512 * 1024;
    private const int MaximumUserScripts = 32;
    private const long MaximumUserScriptBytes = 2L * 1024 * 1024;
    private const long MaximumPackageBytes = 128L * 1024 * 1024;
    private static readonly TimeSpan StoreDownloadTimeout = TimeSpan.FromMinutes(3);
    private const long MaximumExtractedBytes = 256L * 1024 * 1024;
    private const long MaximumSingleFileBytes = 96L * 1024 * 1024;
    private const int MaximumArchiveEntries = 8_192;
    private const int MaximumDirectoryDepth = 64;
    private const int MaximumCrxHeaderBytes = 8 * 1024 * 1024;
    private const int MaximumManifestCapabilities = 4_096;
    private const int MaximumManifestCapabilityCharacters = 4_096;
    private const int MaximumManifestCapabilityBytes = 16 * 1024;
    private const int MaximumRetainedCapabilityBytesPerExtension = 512 * 1024;
    private const int MaximumRegistryBytes = 2 * 1024 * 1024;
    private const int MaximumManagedMarkersToScan = 1_024;
    private static readonly TimeSpan StalePreparedPackageAge = TimeSpan.FromHours(6);
    // SHA-256 of Chromium's production CRX3 publisher SPKI
    // (components/crx_file/crx_verifier.cc). Store downloads must contain a
    // valid proof from this key in addition to the extension developer proof.
    private static readonly byte[] ChromeWebStorePublisherKeyHash =
    [
        0x61, 0xf7, 0xf2, 0xa6, 0xbf, 0xcf, 0x74, 0xcd,
        0x0b, 0xc1, 0xfe, 0x24, 0x97, 0xcc, 0x9b, 0x04,
        0x25, 0x4c, 0x65, 0x8f, 0x79, 0xf2, 0x14, 0x53,
        0x92, 0x86, 0x7e, 0xa8, 0x36, 0x63, 0x67, 0xcf
    ];
    private const string ManagedMarkerSuffix = ".misha-managed";
    private const string LegacyManagedMarker = "MishaWeb managed extension package";
    private const string PreparedMarkerState = "prepared";
    private const string InstallingMarkerState = "installing";
    private const string InstalledMarkerState = "installed";
    private const string RegistryFileName = "managed-extensions.json";
    private static readonly HttpClient ChromeStoreClient = CreateChromeStoreClient();
    private static readonly JsonSerializerOptions RegistryJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string folderPath;
    private readonly string managedFolderPath;
    private readonly string registryPath;
    private static readonly object RegistrySync = new();
    private long cachedRegistryLength = -1;
    private long cachedRegistryLastWriteTicks = -1;
    private Dictionary<string, ManagedBrowserExtension>? cachedRegistryRecords;
    private readonly SemaphoreSlim updateCheckGate = new(1, 1);
    private readonly Dictionary<string, CachedScript> scriptCache = new(StringComparer.OrdinalIgnoreCase);

    public BrowserExtensions(string? folderPath = null)
    {
        this.folderPath = folderPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MishaWeb",
            "Extensions");
        managedFolderPath = Path.Combine(this.folderPath, "Packages");
        registryPath = Path.Combine(this.folderPath, RegistryFileName);
    }

    public string FolderPath => folderPath;
    public string ManagedFolderPath => managedFolderPath;

    public IReadOnlyList<string> LoadScripts()
    {
        Directory.CreateDirectory(folderPath);
        var paths = Directory.EnumerateFiles(folderPath, "*.js")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var livePaths = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stalePath in scriptCache.Keys.Where(path => !livePaths.Contains(path)).ToArray())
        {
            scriptCache.Remove(stalePath);
        }

        var scripts = new List<string>(paths.Length);
        long loadedScriptBytes = 0;
        foreach (var path in paths)
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Length > MaximumScriptBytes
                    || scripts.Count >= MaximumUserScripts
                    || loadedScriptBytes + file.Length > MaximumUserScriptBytes)
                {
                    continue;
                }
                if (scriptCache.TryGetValue(path, out var cached)
                    && cached.Length == file.Length
                    && cached.LastWriteUtc == file.LastWriteTimeUtc)
                {
                    scripts.Add(cached.Content);
                    loadedScriptBytes += file.Length;
                    continue;
                }

                var content = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(content)) continue;
                var scopedContent = CreateScopedUserScript(content);
                if (scopedContent is null) continue;
                scriptCache[path] = new CachedScript(file.Length, file.LastWriteTimeUtc, scopedContent);
                scripts.Add(scopedContent);
                loadedScriptBytes += file.Length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return scripts;
    }

    private static string? CreateScopedUserScript(string content)
    {
        if (!TryReadUserScriptPatterns(content, "@match", out var matches)
            || matches.Count == 0
            || !TryReadUserScriptPatterns(content, "@exclude", out var excludes))
        {
            return null;
        }
        var matchJson = JsonSerializer.Serialize(matches);
        var excludeJson = JsonSerializer.Serialize(excludes);

        // Legacy scripts are local code, but they must still declare where they
        // run. The wrapper default-denies file/extension pages and the Web Store,
        // then evaluates familiar userscript match patterns before executing.
        return "(() => { 'use strict';"
            + "const u=new URL(location.href);"
            + "if(u.protocol!=='http:'&&u.protocol!=='https:')return;"
            + "const h=u.hostname.toLowerCase();"
            + "if(h==='chromewebstore.google.com'||h==='chrome.google.com'||h.endsWith('.chrome.google.com'))return;"
            + "const wild=(p,v)=>{const a=p.split('*');let n=0;for(let k=0;k<a.length;k++){const z=a[k];if(!z)continue;"
            + "const f=v.indexOf(z,n);if(f<0||(k===0&&!p.startsWith('*')&&f!==0))return false;n=f+z.length;}"
            + "return p.endsWith('*')||n===v.length;};"
            + "const test=(p)=>{if(p==='<all_urls>')return true;"
            + "const i=p.indexOf('://');if(i<1)return false;const s=p.slice(0,i),r=p.slice(i+3),j=r.indexOf('/');"
            + "if(j<0)return false;const ph=r.slice(0,j).toLowerCase(),pp=r.slice(j);"
            + "if(s!=='*'&&s+':'!==u.protocol)return false;if(s==='*'&&u.protocol!=='http:'&&u.protocol!=='https:')return false;"
            + "if(ph!=='*'){if(ph.startsWith('*.')){const b=ph.slice(2);if(h!==b&&!h.endsWith('.'+b))return false;}else if(h!==ph)return false;}"
            + "return wild(pp,u.pathname+u.search+u.hash);};"
            + "const m=" + matchJson + ",x=" + excludeJson + ";"
            + "if(!m.some(test)||x.some(test))return;(function(){\n"
            + content
            + "\n}).call(globalThis);})();";
    }

    private static bool TryReadUserScriptPatterns(
        string content,
        string directive,
        out IReadOnlyList<string> result)
    {
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in content.Split('\n').Take(256))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (!line.StartsWith("//", StringComparison.Ordinal)) continue;
            line = line[2..].TrimStart();
            if (!line.StartsWith(directive, StringComparison.OrdinalIgnoreCase)) continue;
            var pattern = line[directive.Length..].Trim();
            if (!IsSafeUserScriptPattern(pattern))
            {
                result = Array.Empty<string>();
                return false;
            }
            patterns.Add(pattern);
        }
        result = patterns.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        return true;
    }

    private static bool IsSafeUserScriptPattern(string pattern)
    {
        if (pattern.Equals("<all_urls>", StringComparison.OrdinalIgnoreCase)) return true;
        if (pattern.Length is 0 or > 512 || pattern.Any(char.IsControl)) return false;
        var delimiter = pattern.IndexOf("://", StringComparison.Ordinal);
        if (delimiter <= 0) return false;
        var scheme = pattern[..delimiter];
        if (scheme is not ("*" or "http" or "https")) return false;
        var remainder = pattern[(delimiter + 3)..];
        var pathIndex = remainder.IndexOf('/');
        if (pathIndex <= 0) return false;
        var host = remainder[..pathIndex];
        var path = remainder[pathIndex..];
        if (!path.StartsWith('/') || host.Contains('@') || host.Contains(':')) return false;
        if (host == "*") return true;
        if (host.StartsWith("*.", StringComparison.Ordinal)) host = host[2..];
        return BrowserPolicy.NormalizeExactHost(host) is not null;
    }

    public async Task<PreparedBrowserExtension> DownloadFromChromeWebStoreAsync(
        string storeUrlOrId,
        string browserVersion,
        CancellationToken cancellationToken = default)
    {
        var storeId = ParseChromeWebStoreExtensionId(storeUrlOrId)
            ?? throw new InvalidDataException(
                "Paste a Chrome Web Store extension address or its 32-character extension ID.");
        var downloadUri = BuildChromeWebStoreDownloadUri(storeId, browserVersion);
        Directory.CreateDirectory(managedFolderPath);
        var stagingPath = CreateStagingFolder();
        var packagePath = Path.Combine(stagingPath, "extension.crx");

        try
        {
            using var transferTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            transferTimeout.CancelAfter(StoreDownloadTimeout);
            var transferToken = transferTimeout.Token;
            using var response = await ChromeStoreClient.GetAsync(
                downloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                transferToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
            {
                throw new InvalidDataException(
                    "The Chrome Web Store did not return a compatible extension package.");
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
            {
                throw new InvalidDataException("The extension package is too large.");
            }

            await using (var source = await response.Content.ReadAsStreamAsync(transferToken)
                .ConfigureAwait(false))
            await using (var destination = new FileStream(
                packagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyBoundedAsync(
                    source,
                    destination,
                    MaximumPackageBytes,
                    transferToken).ConfigureAwait(false);
            }

            return PreparePackageCore(packagePath, storeId, stagingPath, transferToken);
        }
        catch
        {
            DeleteStagingFolder(stagingPath);
            throw;
        }
    }

    public async Task<PreparedBrowserExtensionUpdate?> CheckForChromeWebStoreUpdateAsync(
        string runtimeId,
        string browserVersion,
        CancellationToken cancellationToken = default)
    {
        if (!IsChromeExtensionId(runtimeId))
        {
            throw new ArgumentException("Invalid Chrome extension ID.", nameof(runtimeId));
        }

        await updateCheckGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PreparedBrowserExtension? prepared = null;
        try
        {
            ManagedBrowserExtension current;
            lock (RegistrySync)
            {
                current = LoadManagedRecords(failClosedOnInvalid: true).GetValueOrDefault(runtimeId)
                    ?? throw new InvalidOperationException(
                        "Only MishaWeb-managed Chrome Web Store extensions can be updated.");
            }
            if (current.StoreId is null
                || !current.StoreId.Equals(runtimeId, StringComparison.OrdinalIgnoreCase)
                || !IsOwnedPackageFolder(current.FolderPath))
            {
                throw new InvalidOperationException(
                    "This extension is not eligible for signed Chrome Web Store updates.");
            }

            var currentCapabilities = current.RequestedCapabilities
                ?? ReadManifest(current.FolderPath).RequestedCapabilities;
            prepared = await DownloadFromChromeWebStoreAsync(
                current.StoreId,
                browserVersion,
                cancellationToken).ConfigureAwait(false);
            var evaluation = EvaluateExtensionUpdate(current, prepared, currentCapabilities);
            if (!evaluation.IsUpdate)
            {
                DiscardPrepared(prepared);
                prepared = null;
                return null;
            }

            var result = new PreparedBrowserExtensionUpdate(
                current,
                prepared,
                evaluation.AddedCapabilities);
            prepared = null;
            return result;
        }
        catch
        {
            if (prepared is not null) DiscardPrepared(prepared);
            throw;
        }
        finally
        {
            updateCheckGate.Release();
        }
    }

    public Task<PreparedBrowserExtension> ImportPackageAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPackagePath = Path.GetFullPath(packagePath);
            if (!File.Exists(fullPackagePath))
            {
                throw new FileNotFoundException("The extension package could not be found.", fullPackagePath);
            }
            if (new FileInfo(fullPackagePath).Length > MaximumPackageBytes)
            {
                throw new InvalidDataException("The extension package is too large.");
            }

            Directory.CreateDirectory(managedFolderPath);
            var stagingPath = CreateStagingFolder();
            try
            {
                return PreparePackageCore(fullPackagePath, null, stagingPath, cancellationToken);
            }
            catch
            {
                DeleteStagingFolder(stagingPath);
                throw;
            }
        }, cancellationToken);
    }

    public Task<PreparedBrowserExtension> ImportUnpackedAsync(
        string sourceFolderPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFolder = Path.GetFullPath(sourceFolderPath);
            if (!Directory.Exists(sourceFolder))
            {
                throw new DirectoryNotFoundException("The unpacked extension folder could not be found.");
            }
            var managedRoot = Path.GetFullPath(managedFolderPath);
            if (PathsEqual(sourceFolder, managedRoot)
                || IsDescendantPath(sourceFolder, managedRoot)
                || IsDescendantPath(managedRoot, sourceFolder))
            {
                throw new InvalidDataException(
                    "Choose an unpacked extension folder outside MishaWeb's managed extension storage.");
            }

            Directory.CreateDirectory(managedFolderPath);
            var stagingPath = CreateStagingFolder();
            var extractionPath = Path.Combine(stagingPath, "extension");
            Directory.CreateDirectory(extractionPath);
            try
            {
                CopyDirectoryBounded(sourceFolder, extractionPath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var manifest = ReadManifest(extractionPath);
                return FinalizePreparedExtension(
                    stagingPath,
                    extractionPath,
                    manifest,
                    storeId: null,
                    publicKey: null);
            }
            catch
            {
                DeleteStagingFolder(stagingPath);
                throw;
            }
        }, cancellationToken);
    }

    public void MarkInstallationStarted(PreparedBrowserExtension prepared)
    {
        ValidatePreparedPackage(prepared);
        lock (RegistrySync)
        {
            var marker = ReadManagedMarker(prepared.FolderPath);
            if (marker is null
                || (marker.State != PreparedMarkerState
                    && marker.State != InstallingMarkerState))
            {
                throw new InvalidDataException(
                    "The prepared extension package has invalid ownership metadata.");
            }
            WriteManagedMarker(
                prepared.FolderPath,
                marker with
                {
                    State = InstallingMarkerState,
                    RuntimeId = null,
                    StoreId = prepared.StoreId,
                    StateChangedUtc = DateTime.UtcNow
                });
        }
    }

    public void RememberInstalled(string runtimeId, PreparedBrowserExtension prepared)
    {
        ValidatePreparedPackage(prepared);
        if (!IsChromeExtensionId(runtimeId))
        {
            throw new InvalidDataException("The browser returned an invalid extension ID.");
        }
        if (prepared.StoreId is not null
            && !prepared.StoreId.Equals(runtimeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The installed extension ID did not match its signed Chrome Web Store package.");
        }
        var normalizedRuntimeId = runtimeId.ToLowerInvariant();
        var record = new ManagedBrowserExtension(
            normalizedRuntimeId,
            prepared.FolderPath,
            prepared.Name,
            prepared.Version,
            prepared.StoreId,
            prepared.LaunchPath,
            prepared.RequestedCapabilities,
            prepared.PopupPath,
            prepared.OptionsPath,
            prepared.Description,
            prepared.IconPath);
        ValidateManagedRecord(record);
        ManagedBrowserExtension? previous;
        lock (RegistrySync)
        {
            var records = LoadManagedRecords(failClosedOnInvalid: true);
            records.TryGetValue(normalizedRuntimeId, out previous);
            WriteManagedMarker(
                prepared.FolderPath,
                new ManagedPackageMarker(
                    FormatVersion: 1,
                    State: InstalledMarkerState,
                    RuntimeId: normalizedRuntimeId,
                    StoreId: prepared.StoreId,
                    StateChangedUtc: DateTime.UtcNow));
            records[normalizedRuntimeId] = record;
            SaveManagedRecords(records.Values);
        }

        if (previous is not null
            && !PathsEqual(previous.FolderPath, prepared.FolderPath))
        {
            DeleteOwnedPackageFolder(previous.FolderPath);
        }
    }

    public ManagedBrowserExtension? GetManagedExtension(string runtimeId)
    {
        lock (RegistrySync) return LoadManagedRecords().GetValueOrDefault(runtimeId);
    }

    public IReadOnlyDictionary<string, ManagedBrowserExtension> GetManagedExtensions()
    {
        lock (RegistrySync) return LoadManagedRecords();
    }

    public void ForgetInstalled(string runtimeId)
    {
        ManagedBrowserExtension? removed;
        lock (RegistrySync)
        {
            var records = LoadManagedRecords(failClosedOnInvalid: true);
            if (!records.Remove(runtimeId, out removed)) return;
            // Logical removal must succeed even if antivirus or WebView2 still
            // holds a file handle. Leftover owned files are safe to retry later.
            SaveManagedRecords(records.Values);
        }
        DeleteOwnedPackageFolder(removed.FolderPath);
    }

    public void ReconcileManagedExtensions(IEnumerable<string> runtimeIds)
    {
        ArgumentNullException.ThrowIfNull(runtimeIds);
        var activeIds = runtimeIds
            .Where(IsChromeExtensionId)
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packagesToDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (RegistrySync)
        {
            var records = LoadManagedRecords(failClosedOnInvalid: true);
            var markers = EnumerateManagedPackageMarkers().ToArray();
            var changed = false;

            foreach (var runtimeId in activeIds)
            {
                if (records.ContainsKey(runtimeId)) continue;
                var candidate = markers
                    .Where(marker => IsMarkerCandidateForRuntime(marker, runtimeId))
                    .OrderByDescending(marker => GetMarkerCandidateRank(marker, runtimeId))
                    .ThenByDescending(marker => marker.StateChangedUtc)
                    .FirstOrDefault();
                if (candidate is null) continue;

                try
                {
                    var manifest = ReadManifest(candidate.PackageFolderPath);
                    var candidateIdentity = Path.GetFileName(Path.GetDirectoryName(candidate.PackageFolderPath));
                    var storeId = (candidate.Marker.StoreId is not null
                        && candidate.Marker.StoreId.Equals(runtimeId, StringComparison.OrdinalIgnoreCase))
                        || (candidate.Marker.StoreId is null
                            && candidateIdentity is not null
                            && IsChromeExtensionId(candidateIdentity)
                            && candidateIdentity.Equals(runtimeId, StringComparison.OrdinalIgnoreCase))
                            ? runtimeId
                            : null;
                    var adopted = new ManagedBrowserExtension(
                        runtimeId,
                        candidate.PackageFolderPath,
                        manifest.Name,
                        manifest.Version,
                        storeId,
                        manifest.LaunchPath,
                        manifest.RequestedCapabilities,
                        manifest.PopupPath,
                        manifest.OptionsPath,
                        manifest.Description,
                        manifest.IconPath);
                    ValidateManagedRecord(adopted);
                    if (candidate.Marker.State != InstalledMarkerState
                        || !runtimeId.Equals(
                            candidate.Marker.RuntimeId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        WriteManagedMarker(
                            candidate.PackageFolderPath,
                            candidate.Marker with
                            {
                                FormatVersion = 1,
                                State = InstalledMarkerState,
                                RuntimeId = runtimeId,
                                StoreId = storeId,
                                StateChangedUtc = DateTime.UtcNow
                            });
                    }
                    records[runtimeId] = adopted;
                    changed = true;
                }
                catch (Exception error) when (error is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException)
                {
                }
            }

            var cutoff = DateTime.UtcNow - StalePreparedPackageAge;
            var referencedFolders = records.Values
                .Select(record => Path.GetFullPath(record.FolderPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var marker in markers)
            {
                if (referencedFolders.Contains(marker.PackageFolderPath)
                    || marker.StateChangedUtc >= cutoff)
                {
                    continue;
                }
                var state = marker.Marker.State;
                if (state == PreparedMarkerState)
                {
                    packagesToDelete.Add(marker.PackageFolderPath);
                    continue;
                }
                var knownRuntimeId = state == InstalledMarkerState
                    ? marker.Marker.RuntimeId
                    : state == InstallingMarkerState
                        ? marker.Marker.StoreId
                        : null;
                if (IsChromeExtensionId(knownRuntimeId)
                    && !activeIds.Contains(knownRuntimeId!))
                {
                    packagesToDelete.Add(marker.PackageFolderPath);
                }
            }
            if (changed) SaveManagedRecords(records.Values);
        }

        foreach (var packagePath in packagesToDelete) DeleteOwnedPackageFolder(packagePath);
    }

    public void DiscardPrepared(PreparedBrowserExtension prepared)
    {
        if (prepared.IsManaged) DeleteOwnedPackageFolder(prepared.FolderPath);
    }

    internal static string? ParseChromeWebStoreExtensionId(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (IsChromeExtensionId(trimmed)) return trimmed.ToLowerInvariant();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !(uri.Host.Equals("chromewebstore.google.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("chrome.google.com", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        foreach (var segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            if (IsChromeExtensionId(segment)) return segment.ToLowerInvariant();
        }
        return null;
    }

    internal static bool IsChromeWebStoreOrigin(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && (uri.Host.Equals("chromewebstore.google.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("chrome.google.com", StringComparison.OrdinalIgnoreCase));
    }

    internal static string? ParseChromeWebStoreListingExtensionId(string? value)
    {
        if (!IsChromeWebStoreOrigin(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var candidate = segments switch
        {
            [var detail, _, var id]
                when detail.Equals("detail", StringComparison.OrdinalIgnoreCase) => id,
            [var webstore, var detail, _, var id]
                when webstore.Equals("webstore", StringComparison.OrdinalIgnoreCase)
                    && detail.Equals("detail", StringComparison.OrdinalIgnoreCase) => id,
            _ => null
        };
        return candidate is not null && IsChromeExtensionId(candidate)
            ? candidate.ToLowerInvariant()
            : null;
    }

    internal static string? ParseChromeWebStoreDownloadExtensionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 16_384
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.Host.Equals("clients2.google.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.Equals("/service/update2/crx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            string key;
            string payload;
            try
            {
                key = Uri.UnescapeDataString(pair[..separator]);
                payload = Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
            catch (UriFormatException)
            {
                continue;
            }
            if (!key.Equals("x", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var item in payload.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var itemSeparator = item.IndexOf('=');
                if (itemSeparator <= 0
                    || !item[..itemSeparator].Equals("id", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var id = item[(itemSeparator + 1)..];
                if (IsChromeExtensionId(id)) return id.ToLowerInvariant();
            }
        }
        return null;
    }

    internal static string? MatchChromeWebStoreInstallRequest(string? pageUrl, string? requestUrl)
    {
        var pageId = ParseChromeWebStoreListingExtensionId(pageUrl);
        var requestId = ParseChromeWebStoreDownloadExtensionId(requestUrl);
        return pageId is not null && pageId.Equals(requestId, StringComparison.OrdinalIgnoreCase)
            ? pageId
            : null;
    }

    internal static string CreateChromeWebStoreInstallBridge(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || channel.Length > 128)
        {
            throw new ArgumentException("A bounded Web Store install channel is required.", nameof(channel));
        }
        var encodedChannel = JsonSerializer.Serialize(channel);
        return "(() => {"
            + $"const channel={encodedChannel};"
            + "if(window.top!==window||window.__mishaChromeStoreInstallBridge)return;"
            + "if(location.protocol!=='https:'||!['chromewebstore.google.com','chrome.google.com'].includes(location.hostname))return;"
            + "const postMessage=window.chrome&&chrome.webview&&chrome.webview.postMessage?chrome.webview.postMessage.bind(chrome.webview):null;"
            + "if(!postMessage)return;"
            + "window.__mishaChromeStoreInstallBridge=true;"
            + "const fallbackId='misha-install-in-browser';"
            + "const listingId=()=>{const parts=location.pathname.split('/').filter(Boolean);"
            + "const value=parts.length===3&&parts[0].toLowerCase()==='detail'?parts[2]:"
            + "parts.length===4&&parts[0].toLowerCase()==='webstore'&&parts[1].toLowerCase()==='detail'?parts[3]:null;"
            + "return value&&/^[a-p]{32}$/i.test(value)?value.toLowerCase():null;};"
            + "const syncFallback=()=>{const existing=document.getElementById(fallbackId);"
            + "if(!listingId()){if(existing)existing.remove();return;}if(existing)return;"
            + "const button=document.createElement('button');button.id=fallbackId;button.type='button';"
            + "button.textContent='Install in MishaWeb';button.setAttribute('aria-label','Install this extension in MishaWeb');"
            + "button.style.cssText='position:fixed;right:20px;top:116px;z-index:2147483647;padding:11px 18px;border:0;border-radius:22px;background:#d63384;color:white;font:600 14px Arial,sans-serif;box-shadow:0 3px 12px #0005;cursor:pointer';"
            + "document.documentElement.appendChild(button);};"
            + "let syncPending=false;const scheduleSync=()=>{if(syncPending)return;syncPending=true;setTimeout(()=>{syncPending=false;syncFallback();},50);};"
            + "const start=()=>{syncFallback();new MutationObserver(scheduleSync).observe(document,{childList:true,subtree:true});};"
            + "addEventListener('popstate',scheduleSync);addEventListener('hashchange',scheduleSync);"
            + "if(document.readyState==='loading')addEventListener('DOMContentLoaded',start,{once:true});else start();"
            + "addEventListener('click',event=>{"
            + "if(!event.isTrusted||event.button!==0)return;"
            + "const id=listingId();if(!id)return;"
            + "const path=typeof event.composedPath==='function'?event.composedPath():[];"
            + "const button=path.find(node=>node&&node.tagName==='BUTTON');"
            + "if(!button)return;"
            + "const jslog=(button.getAttribute('jslog')||'').split(';')[0].trim();"
            + "const label=(button.getAttribute('aria-label')||button.textContent||'').replace(/\\s+/g,' ').trim();"
            + "if(button.id!==fallbackId&&jslog!=='177592'&&!/^add to chrome$/i.test(label))return;"
            + "event.preventDefault();event.stopImmediatePropagation();event.stopPropagation();"
            + "postMessage(channel+':'+id);"
            + "},true);"
            + "})();";
    }

    internal static Uri BuildChromeWebStoreDownloadUri(string extensionId, string browserVersion)
    {
        if (!IsChromeExtensionId(extensionId))
        {
            throw new ArgumentException("Invalid Chrome extension ID.", nameof(extensionId));
        }

        var normalizedVersion = NormalizeBrowserVersion(browserVersion);
        var extensionData = Uri.EscapeDataString(
            $"id={extensionId.ToLowerInvariant()}&installsource=ondemand&uc");
        return new Uri(
            "https://clients2.google.com/service/update2/crx"
            + $"?response=redirect&prodversion={Uri.EscapeDataString(normalizedVersion)}"
            + "&acceptformat=crx2,crx3"
            + $"&x={extensionData}");
    }

    internal static ExtensionUpdateEvaluation EvaluateExtensionUpdateForTesting(
        ManagedBrowserExtension current,
        PreparedBrowserExtension prepared) =>
        EvaluateExtensionUpdate(
            current,
            prepared,
            current.RequestedCapabilities ?? Array.Empty<string>());

    internal static int CompareDottedVersionsForTesting(string left, string right) =>
        CompareDottedVersions(left, right);

    internal static (int Version, int ZipOffset) ReadCrxHeaderForTesting(byte[] bytes)
    {
        var header = ReadCrxHeader(bytes);
        return (header.Version, checked((int)header.ZipOffset));
    }

    internal static bool VerifyCrxPackageForTesting(
        byte[] bytes,
        bool requirePublisherProof = false)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var header = ReadCrxHeader(stream);
        return VerifyPackageSignature(stream, header, requirePublisherProof);
    }

    private PreparedBrowserExtension PreparePackageCore(
        string packagePath,
        string? expectedStoreId,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var extractionPath = Path.Combine(stagingPath, "extension");
        Directory.CreateDirectory(extractionPath);
        byte[]? publicKey = null;
        string? packageId = null;

        using (var package = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = ReadCrxHeader(package);
            publicKey = header.PublicKey;
            packageId = header.ExtensionId;
            if (expectedStoreId is not null
                && packageId is not null
                && !expectedStoreId.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded package ID did not match the requested extension.");
            }
            if ((header.Version is 2 or 3
                    && !VerifyPackageSignature(
                        package,
                        header,
                        requirePublisherProof: expectedStoreId is not null))
                || (expectedStoreId is not null && header.Version == 0))
            {
                throw new InvalidDataException("The extension package signature is invalid.");
            }

            using var archiveStream = new ArchiveSegmentStream(package, header.ZipOffset);
            ExtractArchive(archiveStream, extractionPath, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var manifestRoot = LocateManifestRoot(extractionPath);
        var manifest = ReadManifest(manifestRoot);
        if (publicKey is not null)
        {
            var derivedId = ComputeExtensionId(publicKey);
            if (packageId is not null && !derivedId.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The extension package key did not match its signed ID.");
            }
            if (expectedStoreId is not null
                && !derivedId.Equals(expectedStoreId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The extension package key did not match the store ID.");
            }
            AddManifestKey(manifestRoot, manifest.Root, publicKey);
            manifest = ReadManifest(manifestRoot);
        }

        return FinalizePreparedExtension(
            stagingPath,
            manifestRoot,
            manifest,
            expectedStoreId,
            publicKey);
    }

    private PreparedBrowserExtension FinalizePreparedExtension(
        string stagingPath,
        string manifestRoot,
        ExtensionManifest manifest,
        string? storeId,
        byte[]? publicKey)
    {
        var identity = storeId;
        if (identity is null && publicKey is not null) identity = ComputeExtensionId(publicKey);
        var safeIdentity = IsChromeExtensionId(identity ?? string.Empty)
            ? identity!.ToLowerInvariant()
            : "local";
        var safeVersion = SanitizePathSegment(manifest.Version, "unknown");
        var finalParent = Path.Combine(managedFolderPath, safeIdentity);
        Directory.CreateDirectory(finalParent);
        var finalPath = Path.Combine(
            finalParent,
            safeVersion + "-" + Guid.NewGuid().ToString("N")[..8]);

        var marker = new PreparedBrowserExtension(
            finalPath,
            manifest.Name,
            manifest.Version,
            storeId,
            manifest.LaunchPath,
            manifest.RequestedCapabilities,
            IsManaged: true,
            manifest.PopupPath,
            manifest.OptionsPath,
            manifest.Description,
            manifest.IconPath);
        var markerPath = GetManagedMarkerPath(finalPath);
        try
        {
            WriteManagedMarker(
                finalPath,
                new ManagedPackageMarker(
                    FormatVersion: 1,
                    State: PreparedMarkerState,
                    RuntimeId: null,
                    StoreId: storeId,
                    StateChangedUtc: DateTime.UtcNow));
            Directory.Move(manifestRoot, finalPath);
        }
        catch
        {
            try { if (File.Exists(markerPath)) File.Delete(markerPath); }
            catch { }
            throw;
        }
        DeleteStagingFolder(stagingPath);
        return marker;
    }

    private static CrxHeader ReadCrxHeader(Stream package)
    {
        Span<byte> prefix = stackalloc byte[12];
        ReadExactly(package, prefix[..4]);
        if (!prefix[..4].SequenceEqual("Cr24"u8))
        {
            package.Position = 0;
            return new CrxHeader(0, 0, null, null);
        }

        ReadExactly(package, prefix[4..12]);
        var version = BinaryPrimitives.ReadInt32LittleEndian(prefix[4..8]);
        if (version == 2)
        {
            var publicKeyLength = BinaryPrimitives.ReadInt32LittleEndian(prefix[8..12]);
            Span<byte> signatureLengthBytes = stackalloc byte[4];
            ReadExactly(package, signatureLengthBytes);
            var signatureLength = BinaryPrimitives.ReadInt32LittleEndian(signatureLengthBytes);
            if (publicKeyLength <= 0
                || signatureLength < 0
                || publicKeyLength > MaximumCrxHeaderBytes
                || signatureLength > MaximumCrxHeaderBytes
                || (long)publicKeyLength + signatureLength > MaximumCrxHeaderBytes)
            {
                throw new InvalidDataException("The CRX2 header is invalid or too large.");
            }

            var publicKey = new byte[publicKeyLength];
            ReadExactly(package, publicKey);
            var signature = new byte[signatureLength];
            ReadExactly(package, signature);
            EnsureZipOffset(package);
            return new CrxHeader(
                version,
                package.Position,
                publicKey,
                ComputeExtensionId(publicKey),
                Signature: signature);
        }

        if (version == 3)
        {
            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix[8..12]);
            if (headerLength <= 0 || headerLength > MaximumCrxHeaderBytes)
            {
                throw new InvalidDataException("The CRX3 header is invalid or too large.");
            }

            var encodedHeader = new byte[headerLength];
            ReadExactly(package, encodedHeader);
            var parsedHeader = ParseCrx3Header(encodedHeader);
            EnsureZipOffset(package);
            return new CrxHeader(
                version,
                package.Position,
                parsedHeader.PublicKey,
                parsedHeader.ExtensionId,
                SignedHeaderData: parsedHeader.SignedHeaderData,
                Proofs: parsedHeader.Proofs);
        }

        throw new InvalidDataException($"CRX version {version} is not supported.");
    }

    private static CrxHeader ReadCrxHeader(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return ReadCrxHeader(stream);
    }

    private static void EnsureZipOffset(Stream package)
    {
        if (package.Position > package.Length - 4)
        {
            throw new InvalidDataException("The extension package does not contain a ZIP archive.");
        }
        var position = package.Position;
        Span<byte> signature = stackalloc byte[4];
        ReadExactly(package, signature);
        package.Position = position;
        if (BinaryPrimitives.ReadUInt32LittleEndian(signature) is not 0x04034b50 and not 0x06054b50)
        {
            throw new InvalidDataException("The extension package does not contain a valid ZIP archive.");
        }
    }

    private static bool VerifyPackageSignature(
        Stream package,
        CrxHeader header,
        bool requirePublisherProof = false)
    {
        if (!package.CanRead || !package.CanSeek) return false;
        if (header.Version == 2)
        {
            if (requirePublisherProof) return false;
            if (header.PublicKey is null || header.Signature is not { Length: > 0 }) return false;
            var archiveHash = HashPackagePayload(
                package,
                header.ZipOffset,
                System.Security.Cryptography.HashAlgorithmName.SHA1);
            try
            {
                using var rsa = System.Security.Cryptography.RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(header.PublicKey, out var bytesRead);
                if (bytesRead != header.PublicKey.Length) return false;
                return rsa.VerifyHash(
                    archiveHash,
                    header.Signature,
                    System.Security.Cryptography.HashAlgorithmName.SHA1,
                    System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return false;
            }
        }

        if (header.Version != 3
            || header.ExtensionId is null
            || header.SignedHeaderData is null
            || header.Proofs is null)
        {
            return false;
        }

        Span<byte> signedHeaderLength = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(signedHeaderLength, header.SignedHeaderData.Length);
        var signatureHash = HashPackagePayload(
            package,
            header.ZipOffset,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            "CRX3 SignedData\0"u8.ToArray(),
            signedHeaderLength.ToArray(),
            header.SignedHeaderData);
        var developerProofVerified = false;
        var publisherProofVerified = !requirePublisherProof;
        foreach (var proof in header.Proofs)
        {
            var isDeveloperProof = ComputeExtensionId(proof.PublicKey).Equals(
                header.ExtensionId,
                StringComparison.OrdinalIgnoreCase);
            var isPublisherProof = requirePublisherProof
                && System.Security.Cryptography.SHA256.HashData(proof.PublicKey)
                    .AsSpan()
                    .SequenceEqual(ChromeWebStorePublisherKeyHash);
            if ((!isDeveloperProof && !isPublisherProof)
                || !VerifyCrx3Proof(proof, signatureHash))
            {
                continue;
            }
            if (isDeveloperProof) developerProofVerified = true;
            if (isPublisherProof) publisherProofVerified = true;
            if (developerProofVerified && publisherProofVerified) return true;
        }
        return developerProofVerified && publisherProofVerified;
    }

    private static bool VerifyCrx3Proof(CrxProof proof, byte[] signatureHash)
    {
        try
        {
            if (proof.IsRsa)
            {
                using var rsa = System.Security.Cryptography.RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(proof.PublicKey, out var bytesRead);
                return bytesRead == proof.PublicKey.Length
                    && rsa.VerifyHash(
                        signatureHash,
                        proof.Signature,
                        System.Security.Cryptography.HashAlgorithmName.SHA256,
                        System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            }

            using var ecdsa = System.Security.Cryptography.ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(proof.PublicKey, out var ecdsaBytesRead);
            return ecdsaBytesRead == proof.PublicKey.Length
                && ecdsa.VerifyHash(
                    signatureHash,
                    proof.Signature,
                    System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static byte[] HashPackagePayload(
        Stream package,
        long payloadOffset,
        System.Security.Cryptography.HashAlgorithmName algorithm,
        params byte[][] prefixes)
    {
        var originalPosition = package.Position;
        try
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(algorithm);
            foreach (var prefix in prefixes) hash.AppendData(prefix);
            package.Position = payloadOffset;
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = package.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            package.Position = originalPosition;
        }
    }

    private static Crx3ParsedHeader ParseCrx3Header(ReadOnlySpan<byte> header)
    {
        var proofs = new List<CrxProof>();
        byte[]? signedExtensionId = null;
        byte[]? signedHeaderData = null;
        foreach (var field in ParseProtobufFields(header))
        {
            if (field.WireType != 2) continue;
            if (field.Number is 2 or 3)
            {
                byte[]? proofKey = null;
                byte[]? proofSignature = null;
                foreach (var proofField in ParseProtobufFields(field.Bytes.Span))
                {
                    if (proofField.Number == 1 && proofField.WireType == 2)
                    {
                        proofKey = proofField.Bytes.ToArray();
                    }
                    else if (proofField.Number == 2 && proofField.WireType == 2)
                    {
                        proofSignature = proofField.Bytes.ToArray();
                    }
                }
                if (proofKey is not null && proofSignature is not null)
                {
                    proofs.Add(new CrxProof(proofKey, proofSignature, IsRsa: field.Number == 2));
                }
            }
            else if (field.Number == 10_000)
            {
                signedHeaderData = field.Bytes.ToArray();
                foreach (var signedField in ParseProtobufFields(field.Bytes.Span))
                {
                    if (signedField.Number == 1 && signedField.WireType == 2)
                    {
                        signedExtensionId = signedField.Bytes.ToArray();
                    }
                }
            }
        }

        if (signedExtensionId is not { Length: 16 } || signedHeaderData is null)
        {
            throw new InvalidDataException("The CRX3 package does not contain a valid signed extension ID.");
        }
        var extensionId = ConvertIdBytes(signedExtensionId);
        var matchingProof = proofs.FirstOrDefault(proof => ComputeExtensionId(proof.PublicKey).Equals(
            extensionId,
            StringComparison.OrdinalIgnoreCase));
        if (matchingProof is null)
        {
            throw new InvalidDataException("The CRX3 package does not contain a matching public key proof.");
        }
        return new Crx3ParsedHeader(
            matchingProof.PublicKey,
            extensionId,
            signedHeaderData,
            proofs);
    }

    private static IReadOnlyList<ProtobufField> ParseProtobufFields(ReadOnlySpan<byte> encoded)
    {
        var fields = new List<ProtobufField>();
        var offset = 0;
        while (offset < encoded.Length)
        {
            var key = ReadVarint(encoded, ref offset);
            var number = checked((int)(key >> 3));
            var wireType = checked((int)(key & 7));
            if (number <= 0) throw new InvalidDataException("The CRX3 protobuf header is malformed.");
            switch (wireType)
            {
                case 0:
                    _ = ReadVarint(encoded, ref offset);
                    fields.Add(new ProtobufField(number, wireType, ReadOnlyMemory<byte>.Empty));
                    break;
                case 1:
                    EnsureRemaining(encoded, offset, 8);
                    offset += 8;
                    fields.Add(new ProtobufField(number, wireType, ReadOnlyMemory<byte>.Empty));
                    break;
                case 2:
                    var lengthValue = ReadVarint(encoded, ref offset);
                    if (lengthValue > int.MaxValue)
                    {
                        throw new InvalidDataException("The CRX3 protobuf field is too large.");
                    }
                    var length = (int)lengthValue;
                    EnsureRemaining(encoded, offset, length);
                    var bytes = encoded.Slice(offset, length).ToArray();
                    offset += length;
                    fields.Add(new ProtobufField(number, wireType, bytes));
                    break;
                case 5:
                    EnsureRemaining(encoded, offset, 4);
                    offset += 4;
                    fields.Add(new ProtobufField(number, wireType, ReadOnlyMemory<byte>.Empty));
                    break;
                default:
                    throw new InvalidDataException("The CRX3 protobuf header uses an unsupported wire type.");
            }
        }
        return fields;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> encoded, ref int offset)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= encoded.Length)
            {
                throw new InvalidDataException("The CRX3 protobuf header is truncated.");
            }
            var value = encoded[offset++];
            result |= (ulong)(value & 0x7f) << shift;
            if ((value & 0x80) == 0) return result;
        }
        throw new InvalidDataException("The CRX3 protobuf varint is invalid.");
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (length < 0 || offset < 0 || offset > bytes.Length - length)
        {
            throw new InvalidDataException("The CRX3 protobuf header is truncated.");
        }
    }

    private static void ExtractArchive(
        Stream package,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException("The extension archive contains too many files.");
        }

        long totalBytes = 0;
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsArchiveLink(entry))
            {
                throw new InvalidDataException("Extension archives cannot contain symbolic links.");
            }
            var destinationPath = GetSafeArchivePath(destinationRoot, entry.FullName);
            if (!seenPaths.Add(destinationPath))
            {
                throw new InvalidDataException("The extension archive contains duplicate file paths.");
            }
            if (entry.Length > MaximumSingleFileBytes)
            {
                throw new InvalidDataException("The extension archive contains a file that is too large.");
            }
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumExtractedBytes)
            {
                throw new InvalidDataException("The extracted extension is too large.");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var source = entry.Open();
            using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan);
            CopyBounded(
                source,
                destination,
                Math.Min(MaximumSingleFileBytes, entry.Length),
                cancellationToken);
        }
    }

    private static bool IsArchiveLink(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xffff);
        return unixType == 0xa000 || windowsAttributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static string GetSafeArchivePath(string destinationRoot, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)
            || entryName.Contains('\0')
            || entryName.Contains(':'))
        {
            throw new InvalidDataException("The extension archive contains an unsafe path.");
        }

        var normalized = entryName.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            throw new InvalidDataException("The extension archive contains a rooted path.");
        }
        var root = Path.GetFullPath(destinationRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, normalized));
        if (!IsDescendantPath(root, candidate))
        {
            throw new InvalidDataException("The extension archive contains a path outside its folder.");
        }
        return candidate;
    }

    private static string LocateManifestRoot(string extractionPath)
    {
        var directManifest = Path.Combine(extractionPath, "manifest.json");
        if (File.Exists(directManifest)) return extractionPath;

        var manifests = Directory.EnumerateFiles(
                extractionPath,
                "manifest.json",
                SearchOption.AllDirectories)
            .Take(2)
            .ToArray();
        if (manifests.Length != 1)
        {
            throw new InvalidDataException("The package must contain one extension manifest.json file.");
        }
        return Path.GetDirectoryName(manifests[0])!;
    }

    private static ExtensionManifest ReadManifest(string extensionRoot)
    {
        var manifestPath = Path.Combine(extensionRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("The selected folder does not contain manifest.json.");
        }
        if (new FileInfo(manifestPath).Length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("The extension manifest is too large.");
        }

        JsonNode root;
        try
        {
            root = JsonNode.Parse(
                File.ReadAllText(manifestPath, Encoding.UTF8),
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }) ?? throw new JsonException("The extension manifest is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The extension manifest is not valid JSON.", error);
        }
        if (root is not JsonObject manifest)
        {
            throw new InvalidDataException("The extension manifest must be a JSON object.");
        }

        var name = ReadRequiredManifestString(manifest, "name");
        var version = ReadRequiredManifestString(manifest, "version");
        var manifestVersion = manifest["manifest_version"]?.GetValue<int>() ?? 0;
        if (manifestVersion is not 2 and not 3)
        {
            throw new InvalidDataException("Only Manifest V2 and Manifest V3 extensions are supported.");
        }
        var popupPath = ReadPopupPath(manifest);
        var optionsPath = ReadOptionsPath(manifest);
        var launchPath = popupPath ?? optionsPath;
        var description = ReadDescription(manifest);
        var iconPath = ReadBestIconPath(extensionRoot, manifest);
        return new ExtensionManifest(
            name,
            version,
            launchPath,
            ReadRequestedCapabilities(manifest),
            manifest,
            popupPath,
            optionsPath,
            description,
            iconPath);
    }

    private static IReadOnlyList<string> ReadRequestedCapabilities(JsonObject manifest)
    {
        var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retainedBytes = 0;

        AddManifestArray(
            manifest["permissions"],
            "Permission",
            capabilities,
            ref retainedBytes,
            classifyHosts: true);
        AddManifestArray(
            manifest["host_permissions"],
            "Site access",
            capabilities,
            ref retainedBytes);
        AddManifestArray(
            manifest["optional_permissions"],
            "Optional permission",
            capabilities,
            ref retainedBytes,
            classifyHosts: true);
        AddManifestArray(
            manifest["optional_host_permissions"],
            "Optional site access",
            capabilities,
            ref retainedBytes);

        if (manifest["content_scripts"] is JsonArray contentScripts)
        {
            foreach (var script in contentScripts.OfType<JsonObject>())
            {
                if (capabilities.Count >= MaximumManifestCapabilities)
                {
                    throw new InvalidDataException(
                        "The extension manifest declares too many capabilities to review safely.");
                }
                AddManifestArray(script["matches"], "Runs on", capabilities, ref retainedBytes);
            }
        }
        if (manifest["externally_connectable"] is JsonObject externallyConnectable)
        {
            AddManifestArray(
                externallyConnectable["matches"],
                "Accepts connections from",
                capabilities,
                ref retainedBytes);
        }

        return capabilities
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddManifestArray(
        JsonNode? node,
        string label,
        ISet<string> destination,
        ref int retainedBytes,
        bool classifyHosts = false)
    {
        if (node is not JsonArray values) return;
        foreach (var item in values)
        {
            if (destination.Count >= MaximumManifestCapabilities)
            {
                throw new InvalidDataException(
                    "The extension manifest declares too many capabilities to review safely.");
            }
            string? value;
            try { value = item?.GetValue<string>()?.Trim(); }
            catch (InvalidOperationException) { continue; }
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (value.Length > MaximumManifestCapabilityCharacters
                || Encoding.UTF8.GetByteCount(value) > MaximumManifestCapabilityBytes)
            {
                throw new InvalidDataException(
                    "The extension manifest contains a capability that is too large to review safely.");
            }
            var effectiveLabel = classifyHosts && IsManifestHostPattern(value)
                ? label.Replace("permission", "site access", StringComparison.OrdinalIgnoreCase)
                : label;
            var capability = $"{effectiveLabel}: {value}";
            if (!destination.Add(capability)) continue;
            retainedBytes = checked(retainedBytes + Encoding.UTF8.GetByteCount(capability));
            if (retainedBytes > MaximumRetainedCapabilityBytesPerExtension)
            {
                throw new InvalidDataException(
                    "The extension manifest declares too much capability data to review safely.");
            }
        }
    }

    private static bool IsManifestHostPattern(string value) =>
        value.Equals("<all_urls>", StringComparison.OrdinalIgnoreCase)
        || value.Contains("://", StringComparison.Ordinal);

    private static string ReadRequiredManifestString(JsonObject manifest, string propertyName)
    {
        string? value;
        try { value = manifest[propertyName]?.GetValue<string>(); }
        catch (InvalidOperationException) { value = null; }
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
        {
            throw new InvalidDataException($"The extension manifest has an invalid {propertyName}.");
        }
        return value.Trim();
    }

    private static string? ReadPopupPath(JsonObject manifest)
    {
        var candidates = new[]
        {
            manifest["action"]?["default_popup"],
            manifest["browser_action"]?["default_popup"],
            manifest["page_action"]?["default_popup"]
        };
        foreach (var candidate in candidates)
        {
            string? path;
            try { path = candidate?.GetValue<string>(); }
            catch (InvalidOperationException) { continue; }
            path = NormalizeExtensionRelativePath(path);
            if (path is not null) return path;
        }
        return null;
    }

    private static string? ReadOptionsPath(JsonObject manifest)
    {
        var candidates = new[]
        {
            manifest["options_ui"]?["page"],
            manifest["options_page"]
        };
        foreach (var candidate in candidates)
        {
            string? path;
            try { path = candidate?.GetValue<string>(); }
            catch (InvalidOperationException) { continue; }
            path = NormalizeExtensionRelativePath(path);
            if (path is not null) return path;
        }
        return null;
    }

    private static string? ReadLaunchPath(JsonObject manifest) =>
        ReadPopupPath(manifest) ?? ReadOptionsPath(manifest);

    private static string? ReadDescription(JsonObject manifest)
    {
        try
        {
            var value = manifest["description"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(value)) return null;
            return TextSafety.SanitizeSingleLine(value, 512);
        }
        catch { return null; }
    }

    private static string? ReadBestIconPath(string extensionRoot, JsonObject manifest)
    {
        var candidates = new List<string?>();
        if (manifest["icons"] is JsonObject icons)
        {
            candidates.Add(icons["48"]?.GetValue<string>());
            candidates.Add(icons["128"]?.GetValue<string>());
            candidates.Add(icons["32"]?.GetValue<string>());
            candidates.Add(icons["64"]?.GetValue<string>());
            candidates.Add(icons["16"]?.GetValue<string>());
            foreach (var kvp in icons)
            {
                try { candidates.Add(kvp.Value?.GetValue<string>()); } catch { }
            }
        }
        var actionIcon = manifest["action"]?["default_icon"] ?? manifest["browser_action"]?["default_icon"];
        if (actionIcon is JsonObject actionIcons)
        {
            candidates.Add(actionIcons["48"]?.GetValue<string>());
            candidates.Add(actionIcons["32"]?.GetValue<string>());
            candidates.Add(actionIcons["16"]?.GetValue<string>());
            foreach (var kvp in actionIcons)
            {
                try { candidates.Add(kvp.Value?.GetValue<string>()); } catch { }
            }
        }
        else if (actionIcon is JsonValue)
        {
            try { candidates.Add(actionIcon.GetValue<string>()); } catch { }
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var normalized = NormalizeExtensionRelativePath(candidate);
            if (normalized is null) continue;
            try
            {
                var full = Path.Combine(extensionRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) return normalized;
            }
            catch { }
        }
        return null;
    }

    private static string? NormalizeExtensionRelativePath(string? path)
    {
        var trimmed = path?.Trim().TrimStart('/', '\\');
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Length > 1_024
            || trimmed.Contains(':')
            || trimmed.Contains('\0'))
        {
            return null;
        }
        var segments = trimmed.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment is "" or "." or "..")) return null;
        return string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    private static void AddManifestKey(string extensionRoot, JsonObject manifest, byte[] publicKey)
    {
        var encodedKey = Convert.ToBase64String(publicKey);
        if (manifest["key"] is JsonValue existingKey)
        {
            try
            {
                if (existingKey.GetValue<string>().Equals(encodedKey, StringComparison.Ordinal)) return;
            }
            catch (InvalidOperationException) { }
        }
        manifest["key"] = encodedKey;
        File.WriteAllText(
            Path.Combine(extensionRoot, "manifest.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void CopyDirectoryBounded(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        var entryCount = 1;
        var pending = new Stack<(string Source, string Destination, int Depth)>();
        pending.Push((sourceRoot, destinationRoot, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (source, destination, depth) = pending.Pop();
            var sourceInfo = new DirectoryInfo(source);
            if (sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Unpacked extensions cannot contain directory links.");
            }
            Directory.CreateDirectory(destination);

            foreach (var directory in sourceInfo.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException("Unpacked extensions cannot contain directory links.");
                }
                if (++entryCount > MaximumArchiveEntries || depth + 1 > MaximumDirectoryDepth)
                {
                    throw new InvalidDataException("The unpacked extension contains too many or too deeply nested folders.");
                }
                pending.Push((directory.FullName, Path.Combine(destination, directory.Name), depth + 1));
            }
            foreach (var file in sourceInfo.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException("Unpacked extensions cannot contain file links.");
                }
                if (++entryCount > MaximumArchiveEntries || file.Length > MaximumSingleFileBytes)
                {
                    throw new InvalidDataException("The unpacked extension is too large.");
                }
                using var sourceStream = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan);
                if (File.GetAttributes(file.FullName).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException("Unpacked extensions cannot contain file links.");
                }
                using var destinationStream = new FileStream(
                    Path.Combine(destination, file.Name),
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.SequentialScan);
                var copied = CopyBounded(
                    sourceStream,
                    destinationStream,
                    Math.Min(MaximumSingleFileBytes, MaximumExtractedBytes - totalBytes),
                    cancellationToken);
                totalBytes = checked(totalBytes + copied);
            }
        }
    }

    private void ValidatePreparedPackage(PreparedBrowserExtension prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.IsManaged
            || string.IsNullOrWhiteSpace(prepared.FolderPath)
            || !IsOwnedPackageFolder(prepared.FolderPath))
        {
            throw new InvalidDataException("The extension package is not owned by MishaWeb.");
        }
        if (string.IsNullOrWhiteSpace(prepared.Name)
            || prepared.Name.Length > 512
            || string.IsNullOrWhiteSpace(prepared.Version)
            || prepared.Version.Length > 512
            || (prepared.StoreId is not null && !IsChromeExtensionId(prepared.StoreId))
            || (prepared.LaunchPath is not null && prepared.LaunchPath.Length > 2_048)
            || (prepared.PopupPath is not null && prepared.PopupPath.Length > 2_048)
            || (prepared.OptionsPath is not null && prepared.OptionsPath.Length > 2_048)
            || (prepared.Description is not null && prepared.Description.Length > 2_048)
            || (prepared.IconPath is not null && prepared.IconPath.Length > 2_048))
        {
            throw new InvalidDataException("The prepared extension metadata is invalid.");
        }
        ValidateRetainedCapabilities(prepared.RequestedCapabilities);
    }

    private static void ValidateManagedRecord(ManagedBrowserExtension record)
    {
        if (!IsChromeExtensionId(record.Id)
            || string.IsNullOrWhiteSpace(record.FolderPath)
            || string.IsNullOrWhiteSpace(record.Name)
            || record.Name.Length > 512
            || string.IsNullOrWhiteSpace(record.Version)
            || record.Version.Length > 512
            || (record.StoreId is not null
                && (!IsChromeExtensionId(record.StoreId)
                    || !record.StoreId.Equals(record.Id, StringComparison.OrdinalIgnoreCase)))
            || (record.LaunchPath is not null && record.LaunchPath.Length > 2_048)
            || (record.PopupPath is not null && record.PopupPath.Length > 2_048)
            || (record.OptionsPath is not null && record.OptionsPath.Length > 2_048)
            || (record.Description is not null && record.Description.Length > 2_048)
            || (record.IconPath is not null && record.IconPath.Length > 2_048))
        {
            throw new InvalidDataException("The extension registry contains an invalid record.");
        }
        ValidateRetainedCapabilities(record.RequestedCapabilities);
    }

    private static void ValidateRetainedCapabilities(IReadOnlyList<string>? capabilities)
    {
        if (capabilities is null) return;
        if (capabilities.Count > MaximumManifestCapabilities)
        {
            throw new InvalidDataException("The extension contains too many retained capabilities.");
        }

        var retainedBytes = 0;
        foreach (var capability in capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability)
                || capability.Length > MaximumManifestCapabilityCharacters + 64)
            {
                throw new InvalidDataException("The extension contains an invalid retained capability.");
            }
            var capabilityBytes = Encoding.UTF8.GetByteCount(capability);
            if (capabilityBytes > MaximumManifestCapabilityBytes + 64)
            {
                throw new InvalidDataException("The extension contains an oversized retained capability.");
            }
            retainedBytes = checked(retainedBytes + capabilityBytes);
            if (retainedBytes > MaximumRetainedCapabilityBytesPerExtension)
            {
                throw new InvalidDataException("The extension contains too much retained capability data.");
            }
        }
    }

    private Dictionary<string, ManagedBrowserExtension> LoadManagedRecords(
        bool failClosedOnInvalid = false)
    {
        try
        {
            if (!File.Exists(registryPath))
            {
                cachedRegistryRecords = null;
                cachedRegistryLength = -1;
                cachedRegistryLastWriteTicks = -1;
                return new Dictionary<string, ManagedBrowserExtension>(StringComparer.OrdinalIgnoreCase);
            }
            var registryInfo = new FileInfo(registryPath);
            if (cachedRegistryRecords is not null
                && registryInfo.Length == cachedRegistryLength
                && registryInfo.LastWriteTimeUtc.Ticks == cachedRegistryLastWriteTicks)
            {
                return new Dictionary<string, ManagedBrowserExtension>(
                    cachedRegistryRecords,
                    StringComparer.OrdinalIgnoreCase);
            }
            byte[] bytes;
            using (var stream = new FileStream(
                registryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.SequentialScan))
            {
                if (stream.Length > MaximumRegistryBytes)
                {
                    throw new InvalidDataException("The extension registry is too large.");
                }
                bytes = new byte[checked((int)stream.Length)];
                ReadExactly(stream, bytes);
                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException("The extension registry changed while it was being read.");
                }
            }
            var records = JsonSerializer.Deserialize<List<ManagedBrowserExtension?>>(
                bytes,
                RegistryJsonOptions) ?? [];
            var validRecords = new Dictionary<string, ManagedBrowserExtension>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                if (record is null)
                {
                    throw new InvalidDataException("The extension registry contains an invalid record.");
                }
                ValidateManagedRecord(record);
                var fullFolderPath = Path.GetFullPath(record.FolderPath);
                if (!IsSafeManagedPackagePath(fullFolderPath))
                {
                    throw new InvalidDataException(
                        "The extension registry points outside managed package storage.");
                }
                // A package can be legitimately absent after antivirus or an
                // interrupted cleanup. Prune only that in-root stale record;
                // never reinterpret an unsafe path as an empty registry.
                if (!IsOwnedPackageFolder(fullFolderPath)) continue;
                var normalizedId = record.Id.ToLowerInvariant();
                validRecords[normalizedId] = record with
                {
                    Id = normalizedId,
                    FolderPath = fullFolderPath
                };
            }
            cachedRegistryLength = registryInfo.Length;
            cachedRegistryLastWriteTicks = registryInfo.LastWriteTimeUtc.Ticks;
            cachedRegistryRecords = validRecords;
            return new Dictionary<string, ManagedBrowserExtension>(
                validRecords,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or InvalidDataException)
        {
            if (failClosedOnInvalid)
            {
                throw new InvalidDataException(
                    "The extension registry could not be read safely; no changes were made.",
                    error);
            }
        }
        return new Dictionary<string, ManagedBrowserExtension>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveManagedRecords(IEnumerable<ManagedBrowserExtension> records)
    {
        var orderedRecords = records
            .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var record in orderedRecords) ValidateManagedRecord(record);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            orderedRecords,
            RegistryJsonOptions);
        if (bytes.Length > MaximumRegistryBytes)
        {
            throw new InvalidDataException(
                "The managed extension registry is too large; no changes were saved.");
        }
        Directory.CreateDirectory(folderPath);
        var temporaryPath = registryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, registryPath, overwrite: true);
            cachedRegistryRecords = null;
            cachedRegistryLength = -1;
            cachedRegistryLastWriteTicks = -1;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { }
        }
    }

    private string CreateStagingFolder()
    {
        CleanupStaleStagingFolders();
        CleanupStalePreparedPackages();
        var stagingPath = Path.Combine(managedFolderPath, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingPath);
        return stagingPath;
    }

    private void CleanupStaleStagingFolders()
    {
        try
        {
            if (!Directory.Exists(managedFolderPath)) return;
            var cutoff = DateTime.UtcNow - StalePreparedPackageAge;
            foreach (var path in Directory.EnumerateDirectories(managedFolderPath, ".staging-*")
                .Take(32))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(path) < cutoff) DeleteStagingFolder(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void CleanupStalePreparedPackages()
    {
        var cutoff = DateTime.UtcNow - StalePreparedPackageAge;
        foreach (var marker in EnumerateManagedPackageMarkers())
        {
            if (marker.Marker.State != PreparedMarkerState
                || marker.StateChangedUtc >= cutoff)
            {
                continue;
            }
            DeleteOwnedPackageFolder(marker.PackageFolderPath);
        }
    }

    private IReadOnlyList<ManagedPackageMarkerInfo> EnumerateManagedPackageMarkers()
    {
        var results = new List<ManagedPackageMarkerInfo>();
        try
        {
            if (!Directory.Exists(managedFolderPath)) return results;
            foreach (var identityFolder in Directory.EnumerateDirectories(managedFolderPath))
            {
                if (results.Count >= MaximumManagedMarkersToScan) break;
                try
                {
                    if (new DirectoryInfo(identityFolder).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                    foreach (var markerPath in Directory.EnumerateFiles(
                        identityFolder,
                        "*" + ManagedMarkerSuffix,
                        SearchOption.TopDirectoryOnly))
                    {
                        if (results.Count >= MaximumManagedMarkersToScan) break;
                        var packageFolderPath = markerPath[..^ManagedMarkerSuffix.Length];
                        if (!IsSafeManagedPackagePath(packageFolderPath)) continue;
                        var marker = ReadManagedMarker(packageFolderPath);
                        if (marker is null) continue;
                        results.Add(new ManagedPackageMarkerInfo(
                            Path.GetFullPath(packageFolderPath),
                            marker,
                            File.GetLastWriteTimeUtc(markerPath)));
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return results;
    }

    private static bool IsMarkerCandidateForRuntime(
        ManagedPackageMarkerInfo marker,
        string runtimeId)
    {
        if (marker.Marker.State == InstalledMarkerState)
        {
            return runtimeId.Equals(marker.Marker.RuntimeId, StringComparison.OrdinalIgnoreCase);
        }
        var identity = Path.GetFileName(Path.GetDirectoryName(marker.PackageFolderPath));
        return marker.Marker.State == InstallingMarkerState
                && (runtimeId.Equals(marker.Marker.StoreId, StringComparison.OrdinalIgnoreCase)
                    || runtimeId.Equals(identity, StringComparison.OrdinalIgnoreCase))
            || marker.Marker.State == LegacyManagedMarker
                && runtimeId.Equals(identity, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetMarkerCandidateRank(
        ManagedPackageMarkerInfo marker,
        string runtimeId)
    {
        if (marker.Marker.State == InstalledMarkerState
            && runtimeId.Equals(marker.Marker.RuntimeId, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }
        if (marker.Marker.State == InstallingMarkerState) return 2;
        return 1;
    }

    private ManagedPackageMarker? ReadManagedMarker(string packageFolderPath)
    {
        try
        {
            var markerPath = GetManagedMarkerPath(packageFolderPath);
            var markerFile = new FileInfo(markerPath);
            if (!markerFile.Exists || markerFile.Length > 16 * 1024) return null;
            var bytes = File.ReadAllBytes(markerPath);
            var legacyText = Encoding.UTF8.GetString(bytes).Trim();
            if (legacyText.Equals(LegacyManagedMarker, StringComparison.Ordinal))
            {
                return new ManagedPackageMarker(
                    FormatVersion: 0,
                    State: LegacyManagedMarker,
                    RuntimeId: null,
                    StoreId: null,
                    StateChangedUtc: markerFile.LastWriteTimeUtc);
            }
            var marker = JsonSerializer.Deserialize<ManagedPackageMarker>(bytes, RegistryJsonOptions);
            if (marker is null
                || marker.FormatVersion != 1
                || marker.State is not (PreparedMarkerState or InstallingMarkerState or InstalledMarkerState)
                || (marker.RuntimeId is not null && !IsChromeExtensionId(marker.RuntimeId))
                || (marker.StoreId is not null && !IsChromeExtensionId(marker.StoreId)))
            {
                return null;
            }
            return marker;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return null;
        }
    }

    private void WriteManagedMarker(string packageFolderPath, ManagedPackageMarker marker)
    {
        var fullPath = Path.GetFullPath(packageFolderPath);
        if (!IsSafeManagedPackagePath(fullPath)
            || marker.FormatVersion != 1
            || marker.State is not (PreparedMarkerState or InstallingMarkerState or InstalledMarkerState)
            || (marker.RuntimeId is not null && !IsChromeExtensionId(marker.RuntimeId))
            || (marker.StoreId is not null && !IsChromeExtensionId(marker.StoreId)))
        {
            throw new InvalidDataException("The extension ownership metadata is invalid.");
        }
        var markerPath = GetManagedMarkerPath(fullPath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, RegistryJsonOptions);
        if (bytes.Length > 16 * 1024)
        {
            throw new InvalidDataException("The extension ownership metadata is too large.");
        }
        var temporaryPath = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, markerPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { }
        }
    }

    private void DeleteStagingFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var fullPath = Path.GetFullPath(path);
            if (!IsDescendantPath(Path.GetFullPath(managedFolderPath), fullPath)
                || !Path.GetFileName(fullPath).StartsWith(".staging-", StringComparison.Ordinal))
            {
                return;
            }
            Directory.Delete(fullPath, recursive: true);
        }
        catch
        {
            // Stale staging data can be safely retried or removed on a later run.
        }
    }

    private bool DeleteOwnedPackageFolder(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var markerPath = GetManagedMarkerPath(fullPath);
            if (!IsSafeManagedPackagePath(fullPath)) return false;
            if (Directory.Exists(fullPath))
            {
                if (!File.Exists(markerPath)) return false;
                Directory.Delete(fullPath, recursive: true);
            }
            try { if (File.Exists(markerPath)) File.Delete(markerPath); }
            catch { }
            var parent = Path.GetDirectoryName(fullPath);
            if (parent is not null
                && IsDescendantPath(Path.GetFullPath(managedFolderPath), parent)
                && Directory.Exists(parent)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
            return !Directory.Exists(fullPath) && !File.Exists(markerPath);
        }
        catch
        {
            // Extension removal must still succeed if antivirus temporarily owns a package file.
            return false;
        }
    }

    private bool IsOwnedPackageFolder(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return Directory.Exists(fullPath)
                && IsSafeManagedPackagePath(fullPath)
                && File.Exists(GetManagedMarkerPath(fullPath));
        }
        catch { return false; }
    }

    private bool IsSafeManagedPackagePath(string path)
    {
        var root = Path.GetFullPath(managedFolderPath);
        var candidate = Path.GetFullPath(path);
        if (!IsDescendantPath(root, candidate) || PathsEqual(root, candidate)) return false;

        var current = new DirectoryInfo(candidate);
        while (true)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            if (PathsEqual(current.FullName, root)) return true;
            current = current.Parent;
            if (current is null) return false;
        }
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumBytes) throw new InvalidDataException("The extension package is too large.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static long CopyBounded(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumBytes) throw new InvalidDataException("An extracted extension file is too large.");
            destination.Write(buffer, 0, read);
        }
        return total;
    }

    private static string GetManagedMarkerPath(string packageFolderPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageFolderPath)) + ManagedMarkerSuffix;

    private static void ReadExactly(Stream source, Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            var read = source.Read(destination);
            if (read == 0) throw new InvalidDataException("The extension package is truncated.");
            destination = destination[read..];
        }
    }

    private static string ComputeExtensionId(byte[] publicKey)
    {
        return ConvertIdBytes(System.Security.Cryptography.SHA256.HashData(publicKey).AsSpan(0, 16));
    }

    private static string ConvertIdBytes(ReadOnlySpan<byte> bytes)
    {
        var result = new char[bytes.Length * 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            result[index * 2] = (char)('a' + (bytes[index] >> 4));
            result[index * 2 + 1] = (char)('a' + (bytes[index] & 0x0f));
        }
        return new string(result);
    }

    private static bool IsChromeExtensionId(string? value)
    {
        return value?.Length == 32
            && value.All(character => character is >= 'a' and <= 'p' or >= 'A' and <= 'P');
    }

    private static ExtensionUpdateEvaluation EvaluateExtensionUpdate(
        ManagedBrowserExtension current,
        PreparedBrowserExtension prepared,
        IReadOnlyList<string> currentCapabilities)
    {
        if (!prepared.IsManaged
            || current.StoreId is null
            || prepared.StoreId is null
            || !current.Id.Equals(current.StoreId, StringComparison.OrdinalIgnoreCase)
            || !current.StoreId.Equals(prepared.StoreId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The downloaded update did not match the installed Chrome Web Store extension.");
        }

        var comparison = CompareDottedVersions(prepared.Version, current.Version);
        if (comparison <= 0)
        {
            return new ExtensionUpdateEvaluation(false, Array.Empty<string>());
        }

        var existing = currentCapabilities
            .Take(MaximumManifestCapabilities)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = prepared.RequestedCapabilities
            .Take(MaximumManifestCapabilities)
            .Where(value => !string.IsNullOrWhiteSpace(value) && !existing.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ExtensionUpdateEvaluation(true, added);
    }

    private static int CompareDottedVersions(string left, string right)
    {
        var leftParts = ParseDottedVersion(left);
        var rightParts = ParseDottedVersion(right);
        for (var index = 0; index < 4; index++)
        {
            var comparison = leftParts[index].CompareTo(rightParts[index]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static ushort[] ParseDottedVersion(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > 64)
        {
            throw new InvalidDataException("The extension version is not a bounded dotted version.");
        }
        var rawParts = trimmed.Split('.', StringSplitOptions.None);
        if (rawParts.Length is < 1 or > 4)
        {
            throw new InvalidDataException("The extension version must contain one to four numeric parts.");
        }

        var parsed = new ushort[4];
        for (var index = 0; index < rawParts.Length; index++)
        {
            var part = rawParts[index];
            if (part.Length is 0 or > 5
                || part.Any(character => character is < '0' or > '9')
                || !ushort.TryParse(
                    part,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out parsed[index]))
            {
                throw new InvalidDataException("The extension version contains an invalid numeric part.");
            }
        }
        return parsed;
    }

    private static string NormalizeBrowserVersion(string version)
    {
        var numeric = new string(version.TakeWhile(character => char.IsDigit(character) || character == '.').ToArray());
        if (Version.TryParse(numeric, out var parsed))
        {
            return Math.Max(parsed.Major, 100) + ".0.0.0";
        }
        return "120.0.0.0";
    }

    private static string SanitizePathSegment(string value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Take(64)
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '-' : character)
            .ToArray())
            .Trim(' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static bool IsDescendantPath(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateChromeStoreClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            MaxAutomaticRedirections = 8
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MishaWeb/2.2");
        return client;
    }

    private sealed record CachedScript(long Length, DateTime LastWriteUtc, string Content);
    private sealed record ManagedPackageMarker(
        int FormatVersion,
        string State,
        string? RuntimeId,
        string? StoreId,
        DateTime StateChangedUtc);
    private sealed record ManagedPackageMarkerInfo(
        string PackageFolderPath,
        ManagedPackageMarker Marker,
        DateTime StateChangedUtc);
    private sealed record CrxHeader(
        int Version,
        long ZipOffset,
        byte[]? PublicKey,
        string? ExtensionId,
        byte[]? Signature = null,
        byte[]? SignedHeaderData = null,
        IReadOnlyList<CrxProof>? Proofs = null);
    private sealed record CrxProof(byte[] PublicKey, byte[] Signature, bool IsRsa);
    private sealed record Crx3ParsedHeader(
        byte[] PublicKey,
        string ExtensionId,
        byte[] SignedHeaderData,
        IReadOnlyList<CrxProof> Proofs);
    private sealed record ProtobufField(int Number, int WireType, ReadOnlyMemory<byte> Bytes);
    private sealed record ExtensionManifest(
        string Name,
        string Version,
        string? LaunchPath,
        IReadOnlyList<string> RequestedCapabilities,
        JsonObject Root,
        string? PopupPath = null,
        string? OptionsPath = null,
        string? Description = null,
        string? IconPath = null);

    private sealed class ArchiveSegmentStream : Stream
    {
        private readonly Stream inner;
        private readonly long origin;
        private readonly long length;
        private long position;

        public ArchiveSegmentStream(Stream inner, long origin)
        {
            if (!inner.CanRead || !inner.CanSeek) throw new ArgumentException("A readable seekable stream is required.");
            if (origin < 0 || origin > inner.Length) throw new ArgumentOutOfRangeException(nameof(origin));
            this.inner = inner;
            this.origin = origin;
            length = inner.Length - origin;
            inner.Position = origin;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - position;
            if (remaining <= 0) return 0;
            inner.Position = origin + position;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            position += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var remaining = length - position;
            if (remaining <= 0) return 0;
            inner.Position = origin + position;
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin seekOrigin)
        {
            var target = seekOrigin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(seekOrigin))
            };
            if (target < 0) throw new IOException("Attempted to seek before the extension archive.");
            position = Math.Min(target, length);
            inner.Position = origin + position;
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
