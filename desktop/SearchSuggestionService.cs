using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace MishaWeb;

/// <summary>
/// Fetches real-time search query completions from the default search engine (Google Suggest).
/// Includes in-memory caching, debouncing, and cooperative token cancellation.
/// </summary>
internal sealed class SearchSuggestionService
{
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromMilliseconds(1000)
    })
    {
        Timeout = TimeSpan.FromMilliseconds(1200)
    };

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, string[] Suggestions)> cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static SearchSuggestionService Instance { get; } = new();

    public bool TryGetCachedSuggestions(string? query, out IReadOnlyList<string> suggestions)
    {
        suggestions = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(query)) return false;

        var trimmed = TextSafety.RemoveUnpairedSurrogates(query.Trim());
        if (trimmed.Length == 0 || trimmed.Length > 100) return false;

        if (cache.TryGetValue(trimmed, out var cached)
            && DateTimeOffset.UtcNow - cached.FetchedAt < CacheDuration)
        {
            suggestions = cached.Suggestions;
            return true;
        }

        return false;
    }

    public async Task<IReadOnlyList<string>> GetSearchSuggestionsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();
        var trimmed = TextSafety.RemoveUnpairedSurrogates(query.Trim());
        if (trimmed.Length == 0 || trimmed.Length > 100) return Array.Empty<string>();

        if (cache.TryGetValue(trimmed, out var cached)
            && DateTimeOffset.UtcNow - cached.FetchedAt < CacheDuration)
        {
            return cached.Suggestions;
        }

        try
        {
            var url = "https://suggestqueries.google.com/complete/search?client=firefox&q=" + Uri.EscapeDataString(trimmed);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return Array.Empty<string>();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() >= 2)
            {
                var itemsElement = doc.RootElement[1];
                if (itemsElement.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>(8);
                    foreach (var elem in itemsElement.EnumerateArray())
                    {
                        var text = elem.GetString();
                        if (!string.IsNullOrWhiteSpace(text)
                            && !list.Contains(text, StringComparer.OrdinalIgnoreCase))
                        {
                            list.Add(text);
                            if (list.Count >= 6) break;
                        }
                    }

                    var results = list.ToArray();
                    var now = DateTimeOffset.UtcNow;
                    if (cache.Count >= 150)
                    {
                        var cutoff = now - CacheDuration;
                        var expiredKeys = new List<string>();
                        foreach (var entry in cache)
                        {
                            if (entry.Value.FetchedAt < cutoff) expiredKeys.Add(entry.Key);
                        }
                        foreach (var key in expiredKeys) cache.TryRemove(key, out _);

                        while (cache.Count >= 150)
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
                    cache[trimmed] = (now, results);
                    return results;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Query was superseded by subsequent user typing
        }
        catch
        {
            // Network failure or timeout: gracefully fallback to local results
        }

        return Array.Empty<string>();
    }
}
