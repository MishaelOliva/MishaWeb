using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MishaWeb;

/// <summary>
/// Fetches real-time search query completions from the configured search provider.
/// Includes in-memory caching, in-flight deduplication, bounded response parsing,
/// and offline circuit-breaker backoff.
/// </summary>
internal sealed class SearchSuggestionService
{
    internal const int MaximumResponseBytes = 64 * 1024;
    internal const int MaximumCacheEntries = 150;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan[] BackoffDurations =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(180)
    ];

    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromMilliseconds(1000),
        UseCookies = false,
        AllowAutoRedirect = false
    };

    private readonly HttpClient httpClient;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan requestTimeout;
    private readonly object stateSync = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, string[] Suggestions)> cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<string>>>> inFlightRequests =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset backoffUntilUtc = DateTimeOffset.MinValue;
    private int consecutiveFailures;

    public static SearchSuggestionService Instance { get; } = new();

    public SearchSuggestionService()
        : this(SharedHandler, disposeHandler: false, TimeProvider.System)
    {
    }

    internal SearchSuggestionService(
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        TimeSpan? requestTimeout = null)
        : this(handler, disposeHandler: false, timeProvider ?? TimeProvider.System, requestTimeout)
    {
    }

    private SearchSuggestionService(
        HttpMessageHandler handler,
        bool disposeHandler,
        TimeProvider timeProvider,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromMilliseconds(1200);
        httpClient = new HttpClient(handler, disposeHandler)
        {
            Timeout = this.requestTimeout
        };
    }

    public bool TryGetCachedSuggestions(string? query, out IReadOnlyList<string> suggestions)
        => TryGetCachedSuggestions(query, BrowserPolicy.DefaultSearchProviderId, out suggestions);

    public bool TryGetCachedSuggestions(
        string? query,
        string? providerId,
        out IReadOnlyList<string> suggestions)
    {
        suggestions = Array.Empty<string>();
        var normalizedQuery = NormalizeQuery(query);
        if (normalizedQuery.Length == 0) return false;

        var cacheKey = BuildCacheKey(providerId, normalizedQuery);
        var now = timeProvider.GetUtcNow();
        if (cache.TryGetValue(cacheKey, out var cached)
            && now - cached.FetchedAt < CacheDuration)
        {
            suggestions = cached.Suggestions;
            return true;
        }

        return false;
    }

    public Task<IReadOnlyList<string>> GetSearchSuggestionsAsync(
        string query,
        CancellationToken cancellationToken = default)
        => GetSearchSuggestionsAsync(query, BrowserPolicy.DefaultSearchProviderId, cancellationToken);

    public async Task<IReadOnlyList<string>> GetSearchSuggestionsAsync(
        string query,
        string? providerId,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = NormalizeQuery(query);
        if (normalizedQuery.Length == 0 || cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<string>();
        }

        var normalizedProviderId = BrowserPolicy.NormalizeSearchProviderId(providerId);
        var cacheKey = BuildCacheKey(normalizedProviderId, normalizedQuery);
        var now = timeProvider.GetUtcNow();
        if (cache.TryGetValue(cacheKey, out var cached)
            && now - cached.FetchedAt < CacheDuration)
        {
            return cached.Suggestions;
        }

        lock (stateSync)
        {
            if (now < backoffUntilUtc)
            {
                return Array.Empty<string>();
            }
        }

        // Coalesce concurrent identical lookups without coupling caller cancellation
        // tokens to the shared network request.
        Lazy<Task<IReadOnlyList<string>>>? created = null;
        created = new Lazy<Task<IReadOnlyList<string>>>(
            () => FetchAndCacheAsync(cacheKey, normalizedProviderId, normalizedQuery, created!),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var sharedEntry = inFlightRequests.GetOrAdd(cacheKey, created);
        var sharedTask = sharedEntry.Value;

        try
        {
            return await sharedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<string>();
        }
    }

    private async Task<IReadOnlyList<string>> FetchAndCacheAsync(
        string cacheKey,
        string providerId,
        string normalizedQuery,
        Lazy<Task<IReadOnlyList<string>>> entry)
    {
        try
        {
            var url = BuildEndpointUrl(providerId, normalizedQuery);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var timeoutCts = new CancellationTokenSource(requestTimeout);
            var timeoutToken = timeoutCts.Token;
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)response.StatusCode >= 500)
                {
                    RecordFailure();
                }
                return Array.Empty<string>();
            }

            if (response.Content.Headers.ContentLength is { } contentLength
                && contentLength > MaximumResponseBytes)
            {
                RecordFailure();
                return Array.Empty<string>();
            }

            await using var rawStream = await response.Content.ReadAsStreamAsync(timeoutToken).ConfigureAwait(false);
            var (pooledBuffer, byteCount) = await ReadBoundedPooledBytesAsync(
                rawStream,
                MaximumResponseBytes,
                timeoutToken).ConfigureAwait(false);
            if (pooledBuffer is null)
            {
                RecordFailure();
                return Array.Empty<string>();
            }

            try
            {
                using var doc = JsonDocument.Parse(pooledBuffer.AsMemory(0, byteCount));
                var results = ParseSuggestions(doc.RootElement);
                var now = timeProvider.GetUtcNow();
                StoreInCache(cacheKey, now, results);
                ResetBackoff();
                return results;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pooledBuffer);
            }
        }
        catch (OperationCanceledException)
        {
            // Request/body timeout fires OperationCanceledException on the shared un-canceled task.
            RecordFailure();
            return Array.Empty<string>();
        }
        catch (HttpRequestException)
        {
            RecordFailure();
            return Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
        catch
        {
            RecordFailure();
            return Array.Empty<string>();
        }
        finally
        {
            inFlightRequests.TryRemove(KeyValuePair.Create(cacheKey, entry));
        }
    }

    private static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        var trimmed = TextSafety.RemoveUnpairedSurrogates(query.Trim());
        return trimmed.Length == 0 || trimmed.Length > 100 ? string.Empty : trimmed;
    }

    private static string BuildCacheKey(string? providerId, string normalizedQuery)
    {
        return $"{BrowserPolicy.NormalizeSearchProviderId(providerId)}|{normalizedQuery}";
    }

    private static string BuildEndpointUrl(string providerId, string normalizedQuery)
    {
        // Currently Google is the sole supported provider in BrowserPolicy.
        _ = providerId;
        return "https://suggestqueries.google.com/complete/search?client=firefox&q="
            + Uri.EscapeDataString(normalizedQuery);
    }

    private static async Task<(byte[]? Buffer, int Length)> ReadBoundedPooledBytesAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(4096, maxBytes));
        var totalRead = 0;
        try
        {
            while (true)
            {
                if (totalRead == buffer.Length)
                {
                    if (totalRead >= maxBytes)
                    {
                        var overflowProbe = new byte[1];
                        var extra = await stream.ReadAsync(
                            overflowProbe.AsMemory(0, 1),
                            cancellationToken).ConfigureAwait(false);
                        if (extra > 0)
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            return (null, 0);
                        }
                        return (buffer, totalRead);
                    }
                    var nextCapacity = Math.Min(maxBytes, buffer.Length * 2);
                    var grown = ArrayPool<byte>.Shared.Rent(nextCapacity);
                    Buffer.BlockCopy(buffer, 0, grown, 0, totalRead);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                var maxStep = Math.Min(buffer.Length - totalRead, maxBytes - totalRead);
                var read = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, maxStep),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) return (buffer, totalRead);
                totalRead += read;

                if (totalRead == maxBytes)
                {
                    var overflowProbe = new byte[1];
                    var extra = await stream.ReadAsync(
                        overflowProbe.AsMemory(0, 1),
                        cancellationToken).ConfigureAwait(false);
                    if (extra > 0)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        return (null, 0);
                    }
                    return (buffer, totalRead);
                }
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static string[] ParseSuggestions(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2)
        {
            return Array.Empty<string>();
        }

        var itemsElement = root[1];
        if (itemsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>(8);
        foreach (var elem in itemsElement.EnumerateArray())
        {
            if (elem.ValueKind != JsonValueKind.String) continue;
            var sanitized = TextSafety.SanitizeSingleLine(elem.GetString(), 100);
            if (sanitized.Length == 0 || list.Contains(sanitized, StringComparer.OrdinalIgnoreCase)) continue;
            list.Add(sanitized);
            if (list.Count >= 6) break;
        }
        return list.Count == 0 ? Array.Empty<string>() : list.ToArray();
    }

    private void StoreInCache(string cacheKey, DateTimeOffset now, string[] results)
    {
        lock (stateSync)
        {
            if (cache.Count >= MaximumCacheEntries)
            {
                var cutoff = now - CacheDuration;
                foreach (var entry in cache)
                {
                    if (entry.Value.FetchedAt < cutoff)
                    {
                        cache.TryRemove(entry.Key, out _);
                    }
                }

                while (cache.Count >= MaximumCacheEntries)
                {
                    string? oldestKey = null;
                    var oldestTime = DateTimeOffset.MaxValue;
                    foreach (var entry in cache)
                    {
                        if (entry.Value.FetchedAt < oldestTime)
                        {
                            oldestTime = entry.Value.FetchedAt;
                            oldestKey = entry.Key;
                        }
                    }
                    if (oldestKey is null) break;
                    cache.TryRemove(oldestKey, out _);
                }
            }

            cache[cacheKey] = (now, results);
        }
    }

    private void RecordFailure()
    {
        var now = timeProvider.GetUtcNow();
        lock (stateSync)
        {
            var index = Math.Clamp(consecutiveFailures, 0, BackoffDurations.Length - 1);
            consecutiveFailures = Math.Min(consecutiveFailures + 1, BackoffDurations.Length);
            backoffUntilUtc = now + BackoffDurations[index];
        }
    }

    private void ResetBackoff()
    {
        lock (stateSync)
        {
            consecutiveFailures = 0;
            backoffUntilUtc = DateTimeOffset.MinValue;
        }
    }
}
