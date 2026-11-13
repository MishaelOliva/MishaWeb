namespace MishaWeb;

internal enum AddressSuggestionSource
{
    Bookmark,
    History,
    Search,
    OpenTab
}

internal enum AddressSuggestionMatch
{
    Exact,
    Prefix,
    Fuzzy,
    Search
}

internal readonly record struct OpenTabCandidate(
    string Title,
    string Url,
    bool IsSelected);

internal readonly record struct AddressSuggestion(
    int KeyboardIndex,
    AddressSuggestionSource Source,
    AddressSuggestionMatch Match,
    string Title,
    string Detail,
    string AcceptText,
    string NavigationTarget,
    bool IsTopMatch = false,
    bool IsSearchHistory = false)
{
    public bool IsSearch => Source == AddressSuggestionSource.Search;
}

/// <summary>
/// Produces a small, local-only suggestion list from the current browser state.
/// The zero-based <see cref="AddressSuggestion.KeyboardIndex"/> is stable for the
/// returned list and can be used directly by Up/Down/Enter keyboard handling.
/// </summary>
internal static class AddressSuggestionEngine
{
    internal const int DefaultResultLimit = 6;
    internal const int MaximumResultLimit = 8;

    private const int MaximumBookmarksToScan = 200;
    private const int MaximumHistoryItemsToScan = 300;
    private const int MaximumOpenTabsToScan = 256;
    private const int MaximumQueryCharactersToMatch = 256;
    private const int MaximumTitleCharactersToMatch = 256;
    private const int MaximumUrlCharactersToMatch = 1_024;
    private const int MaximumUrlCharactersToDisplay = 512;
    private static readonly HashSet<string> EmptyUrlSet = new(BrowserPolicy.UrlComparer);
    private static readonly Dictionary<string, SuggestionUsageScore> EmptyUsageByUrl =
        new(0, BrowserPolicy.UrlComparer);
    [ThreadStatic]
    private static SuggestionIndexCache? threadIndexCache;

    public static IReadOnlyList<AddressSuggestion> GetSuggestions(
        string? input,
        BrowserState state,
        int maximumResults = DefaultResultLimit)
    {
        return GetSuggestions(input, state, openTabs: null, maximumResults: maximumResults);
    }

    public static IReadOnlyList<AddressSuggestion> GetSuggestions(
        string? input,
        BrowserState state,
        IReadOnlyList<OpenTabCandidate>? openTabs,
        int maximumResults = DefaultResultLimit)
    {
        ArgumentNullException.ThrowIfNull(state);

        var limit = Math.Clamp(maximumResults, 0, MaximumResultLimit);
        var query = TextSafety.RemoveUnpairedSurrogates(input?.Trim() ?? string.Empty);
        if (limit == 0 || query.Length == 0) return Array.Empty<AddressSuggestion>();

        string? searchUrl = null;
        if (query.Length <= BrowserPolicy.MaximumUrlLength)
        {
            var candidateSearchUrl = BrowserPolicy.CreateSearchUrl(query, state.SearchProviderId);
            if (candidateSearchUrl.Length <= BrowserPolicy.MaximumUrlLength)
            {
                searchUrl = candidateSearchUrl;
            }
        }

        // Keep one slot for a deterministic, network-free Google search row.
        var localResultLimit = searchUrl is null ? limit : limit - 1;
        var topCandidates = localResultLimit == 0
            ? Array.Empty<Candidate>()
            : new Candidate[localResultLimit];
        var topCandidateCount = 0;
        if (localResultLimit > 0)
        {
            HashSet<string>? matchedOpenTabUrls = null;
            if (openTabs is { Count: > 0 })
            {
                matchedOpenTabUrls = AddOpenTabCandidates(
                    openTabs,
                    query,
                    topCandidates,
                    ref topCandidateCount);
            }

            foreach (var indexedCandidate in GetIndexedCandidates(state))
            {
                if (matchedOpenTabUrls is not null && matchedOpenTabUrls.Contains(indexedCandidate.Url))
                {
                    continue;
                }

                AddCandidate(
                    indexedCandidate,
                    query,
                    topCandidates,
                    ref topCandidateCount);
            }
        }

        var localCount = topCandidateCount;
        var results = new AddressSuggestion[localCount + (searchUrl is null ? 0 : 1)];
        for (var index = 0; index < localCount; index++)
        {
            var candidate = topCandidates[index];
            var isTopMatch = index == 0 && (candidate.Match == AddressSuggestionMatch.Exact || candidate.Quality <= 1);
            var detailText = candidate.Source == AddressSuggestionSource.OpenTab
                ? $"Switch to tab \u00B7 {TextSafety.FormatUrlForDisplay(candidate.Url, MaximumUrlCharactersToDisplay)}"
                : candidate.IsSearchQuery
                    ? candidate.SearchProviderName
                    : TextSafety.FormatUrlForDisplay(candidate.Url, MaximumUrlCharactersToDisplay);
            var acceptText = candidate.IsSearchQuery
                ? candidate.Title
                : candidate.Url;

            results[index] = new AddressSuggestion(
                index,
                candidate.Source,
                candidate.Match,
                candidate.Title,
                detailText,
                acceptText,
                candidate.Url,
                isTopMatch,
                candidate.IsSearchQuery);
        }

        if (searchUrl is not null)
        {
            var searchIndex = results.Length - 1;
            results[searchIndex] = new AddressSuggestion(
                searchIndex,
                AddressSuggestionSource.Search,
                AddressSuggestionMatch.Search,
                $"Search {BrowserPolicy.GetSearchProviderName(state.SearchProviderId)} for \u201c{CompactForDisplay(query)}\u201d",
                $"{BrowserPolicy.GetSearchProviderName(state.SearchProviderId)} search",
                query,
                searchUrl);
        }
        return results;
    }

    private static HashSet<string>? AddOpenTabCandidates(
        IReadOnlyList<OpenTabCandidate> openTabs,
        string query,
        Candidate[] topCandidates,
        ref int topCandidateCount)
    {
        HashSet<string>? matchedUrls = null;
        var scanCount = Math.Min(openTabs.Count, MaximumOpenTabsToScan);
        for (var index = 0; index < scanCount; index++)
        {
            var tab = openTabs[index];
            if (tab.IsSelected
                || string.IsNullOrWhiteSpace(tab.Url)
                || tab.Url.Length > BrowserPolicy.MaximumUrlLength
                || tab.Url.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(tab.Url, UriKind.Absolute, out var parsedUrl)
                || (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(tab.Title)
                ? TextSafety.FormatUrlForDisplay(tab.Url, MaximumTitleCharactersToMatch)
                : TextSafety.SanitizeSingleLine(tab.Title, MaximumTitleCharactersToMatch);
            if (title.Length == 0)
            {
                title = TextSafety.FormatUrlForDisplay(tab.Url, MaximumTitleCharactersToMatch);
            }

            var host = parsedUrl.IdnHost;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            var isRootHost = parsedUrl.AbsolutePath.Length <= 1 && string.IsNullOrEmpty(parsedUrl.Query);

            var indexed = new IndexedCandidate(
                AddressSuggestionSource.OpenTab,
                index,
                title,
                tab.Url,
                GetUrlHash(parsedUrl),
                AcceptedCount: 0,
                LastAcceptedUtc: DateTimeOffset.UnixEpoch,
                IsSearchQuery: false,
                SearchProviderName: string.Empty,
                host,
                isRootHost);

            var match = Classify(
                query,
                indexed.Title,
                indexed.Url,
                indexed.Host,
                indexed.IsRootHost,
                indexed.IsSearchQuery);
            if (!match.IsMatch) continue;

            matchedUrls ??= new HashSet<string>(BrowserPolicy.UrlComparer);
            if (!matchedUrls.Add(indexed.Url)) continue;

            var candidate = new Candidate(
                AddressSuggestionSource.OpenTab,
                match.Kind,
                match.Quality,
                indexed.SourceIndex,
                indexed.Title,
                indexed.Url,
                indexed.AcceptedCount,
                indexed.LastAcceptedUtc,
                indexed.UrlHash);
            AddTopCandidate(candidate, topCandidates, ref topCandidateCount);
        }

        return matchedUrls;
    }

    private static void AddCandidate(
        IndexedCandidate indexedCandidate,
        string query,
        Candidate[] topCandidates,
        ref int topCandidateCount)
    {
        var match = Classify(
            query,
            indexedCandidate.Title,
            indexedCandidate.Url,
            indexedCandidate.Host,
            indexedCandidate.IsRootHost,
            indexedCandidate.IsSearchQuery);
        if (!match.IsMatch) return;

        var candidate = new Candidate(
            indexedCandidate.Source,
            match.Kind,
            match.Quality,
            indexedCandidate.SourceIndex,
            indexedCandidate.Title,
            indexedCandidate.Url,
            indexedCandidate.AcceptedCount,
            indexedCandidate.LastAcceptedUtc,
            indexedCandidate.UrlHash,
            indexedCandidate.IsSearchQuery,
            indexedCandidate.SearchProviderName);

        AddTopCandidate(candidate, topCandidates, ref topCandidateCount);
    }

    private static void AddTopCandidate(
        Candidate candidate,
        Candidate[] topCandidates,
        ref int topCandidateCount)
    {
        if (topCandidateCount == topCandidates.Length
            && CompareCandidates(candidate, topCandidates[topCandidateCount - 1]) >= 0)
        {
            return;
        }

        var existingIndex = -1;
        for (var index = 0; index < topCandidateCount; index++)
        {
            if (!UrlsEqual(topCandidates[index], candidate)) continue;
            existingIndex = index;
            break;
        }

        if (existingIndex >= 0)
        {
            if (CompareCandidates(candidate, topCandidates[existingIndex]) >= 0) return;

            for (var index = existingIndex; index < topCandidateCount - 1; index++)
            {
                topCandidates[index] = topCandidates[index + 1];
            }
            topCandidateCount--;
        }
        else if (topCandidateCount == topCandidates.Length)
        {
            topCandidateCount--;
        }

        var insertionIndex = topCandidateCount;
        while (insertionIndex > 0
               && CompareCandidates(candidate, topCandidates[insertionIndex - 1]) < 0)
        {
            topCandidates[insertionIndex] = topCandidates[insertionIndex - 1];
            insertionIndex--;
        }

        topCandidates[insertionIndex] = candidate;
        topCandidateCount++;
    }

    private static bool UrlsEqual(Candidate left, Candidate right)
    {
        if (left.UrlHash != right.UrlHash) return false;
        if (string.Equals(left.Url, right.Url, StringComparison.Ordinal)) return true;
        return BrowserPolicy.UrlComparer.Equals(left.Url, right.Url);
    }

    private static bool IsLegacyDuckDuckGoSearchUri(Uri uri)
    {
        return uri.IdnHost.Equals("duckduckgo.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/", StringComparison.Ordinal)
            && (uri.Query.StartsWith("?q=", StringComparison.OrdinalIgnoreCase)
                || uri.Query.Contains("&q=", StringComparison.OrdinalIgnoreCase));
    }

    private static int GetUrlHash(Uri uri)
    {
        var hash = new HashCode();
        hash.Add(uri.Scheme, StringComparer.OrdinalIgnoreCase);
        hash.Add(uri.IdnHost, StringComparer.OrdinalIgnoreCase);
        hash.Add(uri.Port);
        hash.Add(uri.UserInfo, StringComparer.Ordinal);
        hash.Add(uri.PathAndQuery, StringComparer.Ordinal);
        hash.Add(uri.Fragment, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    private static IndexedCandidate[] GetIndexedCandidates(BrowserState state)
    {
        var cache = threadIndexCache;
        if (cache is not null && cache.Matches(state)) return cache.Candidates;

        cache = SuggestionIndexCache.Create(state);
        threadIndexCache = cache;
        return cache.Candidates;
    }

    internal static void ReleaseCache(BrowserState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (threadIndexCache?.References(state) == true) threadIndexCache = null;
    }

    private static void AppendIndexedCandidate(
        List<IndexedCandidate> candidates,
        BookmarkEntry? item,
        int sourceIndex,
        HashSet<string> dismissedUrls,
        Dictionary<string, SuggestionUsageScore> usageByUrl)
    {
        if (item is null) return;
        AppendIndexedCandidate(
            candidates,
            item.Title,
            item.Url,
            AddressSuggestionSource.Bookmark,
            sourceIndex,
            dismissedUrls,
            usageByUrl);
    }

    private static void AppendIndexedCandidate(
        List<IndexedCandidate> candidates,
        HistoryEntry? item,
        int sourceIndex,
        HashSet<string> dismissedUrls,
        Dictionary<string, SuggestionUsageScore> usageByUrl)
    {
        if (item is null) return;
        AppendIndexedCandidate(
            candidates,
            item.Title,
            item.Url,
            AddressSuggestionSource.History,
            sourceIndex,
            dismissedUrls,
            usageByUrl);
    }

    private static void AppendIndexedCandidate(
        List<IndexedCandidate> candidates,
        string? title,
        string? url,
        AddressSuggestionSource source,
        int sourceIndex,
        HashSet<string> dismissedUrls,
        Dictionary<string, SuggestionUsageScore> usageByUrl)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > BrowserPolicy.MaximumUrlLength
            || dismissedUrls.Contains(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl)
            || (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps)
            || IsLegacyDuckDuckGoSearchUri(parsedUrl))
        {
            return;
        }

        var isSearchQuery = false;
        var searchProviderName = string.Empty;
        if (source == AddressSuggestionSource.History && TryExtractSearchEngineQuery(parsedUrl, out var queryText, out searchProviderName))
        {
            title = queryText;
            isSearchQuery = true;
        }
        else
        {
            title = string.IsNullOrWhiteSpace(title)
                ? TextSafety.FormatUrlForDisplay(url, MaximumTitleCharactersToMatch)
                : title;
        }

        var host = parsedUrl.IdnHost;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        var isRootHost = parsedUrl.AbsolutePath.Length <= 1 && string.IsNullOrEmpty(parsedUrl.Query);

        var usage = usageByUrl.TryGetValue(url, out var recordedUsage)
            ? recordedUsage
            : default;
        candidates.Add(new IndexedCandidate(
            source,
            sourceIndex,
            title,
            url,
            GetUrlHash(parsedUrl),
            usage.AcceptedCount,
            usage.LastAcceptedUtc,
            isSearchQuery,
            searchProviderName,
            host,
            isRootHost));
    }

    private static MatchResult Classify(
        string query,
        string title,
        string url,
        string host,
        bool isRootHost,
        bool isSearchQuery)
    {
        if (title.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return MatchResult.Found(AddressSuggestionMatch.Exact, 0);
        }

        if (UrlEqualsInput(url.AsSpan(), query.AsSpan()))
        {
            return MatchResult.Found(AddressSuggestionMatch.Exact, 1);
        }

        // Oversized address-bar input still receives the search row, but does not
        // make every keystroke scan large state strings on extremely weak PCs.
        if (query.Length > MaximumQueryCharactersToMatch) return MatchResult.None;

        var querySpan = query.AsSpan();
        var titleSpan = Limit(title.AsSpan(), MaximumTitleCharactersToMatch);
        var urlSpan = Limit(url.AsSpan(), MaximumUrlCharactersToMatch);

        // 1. Host Prefix Matches (Highest priority for omnibox navigation)
        var hostSpan = host.AsSpan();
        if (hostSpan.StartsWith(querySpan, StringComparison.OrdinalIgnoreCase))
        {
            // Root host prefix match: e.g. "youtube.com" or "github.com" for query "y" or "git"
            if (isRootHost)
            {
                return MatchResult.Found(AddressSuggestionMatch.Prefix, 0);
            }
            return MatchResult.Found(AddressSuggestionMatch.Prefix, 1);
        }

        // 2. Clean Site Title or Search Query Prefix Matches
        if (titleSpan.StartsWith(querySpan, StringComparison.OrdinalIgnoreCase))
        {
            if (isSearchQuery)
            {
                return MatchResult.Found(AddressSuggestionMatch.Prefix, 1);
            }
            if (!IsNoisyTitle(titleSpan))
            {
                return MatchResult.Found(AddressSuggestionMatch.Prefix, isRootHost ? 0 : 1);
            }
            // Long / sentence / delimited title
            return MatchResult.Found(AddressSuggestionMatch.Prefix, 3);
        }

        if (HasWordPrefix(titleSpan, querySpan))
        {
            return MatchResult.Found(AddressSuggestionMatch.Prefix, IsNoisyTitle(titleSpan) ? 3 : 2);
        }

        if (UrlStartsWithInput(urlSpan, querySpan))
        {
            return MatchResult.Found(AddressSuggestionMatch.Prefix, 2);
        }

        if (titleSpan.IndexOf(querySpan, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return MatchResult.Found(AddressSuggestionMatch.Fuzzy, 0);
        }

        if (urlSpan.IndexOf(querySpan, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return MatchResult.Found(AddressSuggestionMatch.Fuzzy, 1);
        }

        // A subsequence match handles compact inputs such as "ytb" -> "YouTube"
        // without the CPU and allocation cost of edit-distance algorithms.
        if (querySpan.Length >= 3 && IsSubsequence(querySpan, titleSpan))
        {
            return MatchResult.Found(AddressSuggestionMatch.Fuzzy, 2);
        }

        if (querySpan.Length >= 3 && IsSubsequence(querySpan, UrlWithoutScheme(urlSpan)))
        {
            return MatchResult.Found(AddressSuggestionMatch.Fuzzy, 3);
        }

        return MatchResult.None;
    }

    private static bool IsNoisyTitle(ReadOnlySpan<char> title)
    {
        if (title.Length > 60) return true;
        if (title.IndexOf(" - ") >= 0 || title.IndexOf(" | ") >= 0)
        {
            if (title.Length > 35) return true;
        }
        return false;
    }

    private static bool TryExtractSearchEngineQuery(Uri uri, out string queryText, out string searchProviderName)
    {
        queryText = string.Empty;
        searchProviderName = string.Empty;
        var host = uri.IdnHost;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];

        if (host.Equals("google.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("google.", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.Equals("/search", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetQueryParam(uri.Query, "q", out queryText) && !string.IsNullOrWhiteSpace(queryText))
                {
                    searchProviderName = "Google search";
                    return true;
                }
            }
        }
        else if (host.Equals("bing.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".bing.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.Equals("/search", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetQueryParam(uri.Query, "q", out queryText) && !string.IsNullOrWhiteSpace(queryText))
                {
                    searchProviderName = "Bing search";
                    return true;
                }
            }
        }
        else if (host.Equals("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.Equals("/") || uri.AbsolutePath.Equals("/html", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetQueryParam(uri.Query, "q", out queryText) && !string.IsNullOrWhiteSpace(queryText))
                {
                    searchProviderName = "DuckDuckGo search";
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryGetQueryParam(string queryString, string paramName, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(queryString)) return false;
        var query = queryString.AsSpan();
        if (query.StartsWith("?")) query = query[1..];

        while (query.Length > 0)
        {
            var nextAmp = query.IndexOf('&');
            var pair = nextAmp >= 0 ? query[..nextAmp] : query;
            query = nextAmp >= 0 ? query[(nextAmp + 1)..] : ReadOnlySpan<char>.Empty;

            var eqIndex = pair.IndexOf('=');
            if (eqIndex <= 0) continue;
            var key = pair[..eqIndex];
            if (key.Equals(paramName.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                var encodedVal = pair[(eqIndex + 1)..].ToString().Replace('+', ' ');
                value = Uri.UnescapeDataString(encodedVal).Trim();
                return value.Length > 0;
            }
        }
        return false;
    }

    public static string? GetTopMatchHost(string query, BrowserState state)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 64 || state is null) return null;
        var trimmed = query.Trim();
        var candidates = GetIndexedCandidates(state);

        IndexedCandidate? bestCandidate = null;
        var bestQuality = int.MaxValue;

        foreach (var c in candidates)
        {
            if (c.IsSearchQuery) continue;
            var host = c.Host;
            if (string.IsNullOrEmpty(host)) continue;

            if (host.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                var quality = (c.IsRootHost ? 0 : 1) * 100 - Math.Min(50, c.AcceptedCount * 10);
                if (quality < bestQuality)
                {
                    bestQuality = quality;
                    bestCandidate = c;
                }
            }
        }

        return bestCandidate?.Host;
    }

    public static IReadOnlyList<AddressSuggestion> MergeWithLiveSearch(
        IReadOnlyList<AddressSuggestion> localSuggestions,
        IReadOnlyList<string> liveQueries,
        string query,
        BrowserState state,
        int maximumResults = MaximumResultLimit)
    {
        if (liveQueries is null || liveQueries.Count == 0)
        {
            return localSuggestions;
        }

        var limit = Math.Clamp(maximumResults, 1, MaximumResultLimit);
        var searchProvider = BrowserPolicy.GetSearchProviderName(state?.SearchProviderId);
        var baseSearchUrl = BrowserPolicy.CreateSearchUrl(query, state?.SearchProviderId);

        var merged = new List<AddressSuggestion>(limit);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. If localSuggestions has a TopMatch or Exact match, preserve it at position 0
        var localIndex = 0;
        if (localSuggestions.Count > 0 && !localSuggestions[0].IsSearch)
        {
            var first = localSuggestions[0];
            merged.Add(first with { KeyboardIndex = 0 });
            seenTargets.Add(first.AcceptText);
            seenTargets.Add(first.Title);
            localIndex = 1;
        }

        // 2. Add high-quality live search suggestions
        var searchIndex = 0;
        while (merged.Count < limit - 1 && searchIndex < liveQueries.Count)
        {
            var liveQuery = liveQueries[searchIndex++];
            if (string.IsNullOrWhiteSpace(liveQuery)) continue;
            var trimmedLive = liveQuery.Trim();
            if (seenTargets.Contains(trimmedLive)) continue;
            if (trimmedLive.Equals(query, StringComparison.OrdinalIgnoreCase)) continue;

            seenTargets.Add(trimmedLive);
            var liveSearchUrl = BrowserPolicy.CreateSearchUrl(trimmedLive, state?.SearchProviderId);
            merged.Add(new AddressSuggestion(
                merged.Count,
                AddressSuggestionSource.Search,
                AddressSuggestionMatch.Search,
                trimmedLive,
                $"{searchProvider} search",
                trimmedLive,
                liveSearchUrl));
        }

        // 3. Add remaining local candidates if there's room
        while (merged.Count < limit - 1 && localIndex < localSuggestions.Count)
        {
            var local = localSuggestions[localIndex++];
            if (local.IsSearch) continue;
            if (seenTargets.Contains(local.AcceptText) || seenTargets.Contains(local.Title)) continue;

            seenTargets.Add(local.AcceptText);
            merged.Add(local with { KeyboardIndex = merged.Count });
        }

        // 4. Always ensure the bottom row is the primary search action for the user's typed input
        var finalIndex = merged.Count;
        merged.Add(new AddressSuggestion(
            finalIndex,
            AddressSuggestionSource.Search,
            AddressSuggestionMatch.Search,
            $"Search {searchProvider} for \u201c{CompactForDisplay(query)}\u201d",
            $"{searchProvider} search",
            query,
            baseSearchUrl));

        return merged;
    }

    private static int CompareCandidates(Candidate left, Candidate right)
    {
        var comparison = ((int)left.Match).CompareTo((int)right.Match);
        if (comparison != 0) return comparison;

        var leftOpenTab = left.Source == AddressSuggestionSource.OpenTab;
        var rightOpenTab = right.Source == AddressSuggestionSource.OpenTab;
        comparison = rightOpenTab.CompareTo(leftOpenTab);
        if (comparison != 0) return comparison;

        comparison = right.AcceptedCount.CompareTo(left.AcceptedCount);
        if (comparison != 0) return comparison;

        comparison = right.LastAcceptedUtc.CompareTo(left.LastAcceptedUtc);
        if (comparison != 0) return comparison;

        comparison = left.Quality.CompareTo(right.Quality);
        if (comparison != 0) return comparison;

        comparison = ((int)left.Source).CompareTo((int)right.Source);
        if (comparison != 0) return comparison;

        comparison = left.SourceIndex.CompareTo(right.SourceIndex);
        if (comparison != 0) return comparison;

        return StringComparer.OrdinalIgnoreCase.Compare(left.Url, right.Url);
    }

    private static bool HasWordPrefix(ReadOnlySpan<char> value, ReadOnlySpan<char> query)
    {
        for (var index = 1; index + query.Length <= value.Length; index++)
        {
            if (char.IsLetterOrDigit(value[index - 1])) continue;
            if (value[index..].StartsWith(query, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsSubsequence(ReadOnlySpan<char> query, ReadOnlySpan<char> value)
    {
        if (query.Length == 0) return true;
        if (query.Length > value.Length) return false;

        var queryIndex = 0;
        var target = char.ToUpperInvariant(query[0]);
        for (var index = 0; value.Length - index >= query.Length - queryIndex; index++)
        {
            if (char.ToUpperInvariant(value[index]) == target)
            {
                queryIndex++;
                if (queryIndex == query.Length) return true;
                target = char.ToUpperInvariant(query[queryIndex]);
            }
        }

        return false;
    }

    private static bool UrlEqualsInput(ReadOnlySpan<char> url, ReadOnlySpan<char> query)
    {
        if (EqualsIgnoringTrailingSlash(url, query)) return true;

        var withoutScheme = UrlWithoutScheme(url);
        if (EqualsIgnoringTrailingSlash(withoutScheme, query)) return true;

        var withoutCommonPrefix = WithoutWww(withoutScheme);
        return EqualsIgnoringTrailingSlash(withoutCommonPrefix, query);
    }

    private static bool UrlStartsWithInput(ReadOnlySpan<char> url, ReadOnlySpan<char> query)
    {
        if (url.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return true;

        var withoutScheme = UrlWithoutScheme(url);
        return withoutScheme.StartsWith(query, StringComparison.OrdinalIgnoreCase)
            || WithoutWww(withoutScheme).StartsWith(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EqualsIgnoringTrailingSlash(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        left = left.TrimEnd('/');
        right = right.TrimEnd('/');
        return left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlySpan<char> UrlWithoutScheme(ReadOnlySpan<char> value)
    {
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return value[8..];
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return value[7..];
        return value;
    }

    private static ReadOnlySpan<char> WithoutWww(ReadOnlySpan<char> value)
    {
        return value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? value[4..] : value;
    }

    private static ReadOnlySpan<char> Limit(ReadOnlySpan<char> value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }

    private static string CompactForDisplay(string query)
    {
        const int maximumDisplayCharacters = 80;
        return TextSafety.TruncateWithEllipsis(query, maximumDisplayCharacters);
    }

    private sealed class SuggestionIndexCache
    {
        private SuggestionIndexCache(
            BrowserState state,
            BookmarkEntry[] bookmarks,
            HistoryEntry[] history,
            string[] dismissedUrls,
            AddressSuggestionUsage[] usage,
            IndexedCandidate[] candidates)
        {
            State = new WeakReference<BrowserState>(state);
            Bookmarks = bookmarks;
            History = history;
            DismissedUrls = dismissedUrls;
            Usage = usage;
            Candidates = candidates;
        }

        private WeakReference<BrowserState> State { get; }
        private BookmarkEntry[] Bookmarks { get; }
        private HistoryEntry[] History { get; }
        private string[] DismissedUrls { get; }
        private AddressSuggestionUsage[] Usage { get; }
        public IndexedCandidate[] Candidates { get; }

        public bool Matches(BrowserState state)
        {
            return State.TryGetTarget(out var cachedState)
                && ReferenceEquals(cachedState, state)
                && SequenceReferencesMatch(state.Bookmarks, Bookmarks, MaximumBookmarksToScan)
                && SequenceReferencesMatch(state.History, History, MaximumHistoryItemsToScan)
                && SequenceReferencesMatch(state.DismissedSuggestionUrls, DismissedUrls, int.MaxValue)
                && SequenceReferencesMatch(state.SuggestionUsage, Usage, int.MaxValue);
        }

        public bool References(BrowserState state) =>
            State.TryGetTarget(out var cachedState) && ReferenceEquals(cachedState, state);

        public static SuggestionIndexCache Create(BrowserState state)
        {
            var bookmarks = SnapshotPrefix(state.Bookmarks, MaximumBookmarksToScan);
            var history = SnapshotPrefix(state.History, MaximumHistoryItemsToScan);
            var dismissedSnapshot = SnapshotPrefix(state.DismissedSuggestionUrls, int.MaxValue);
            var usageSnapshot = SnapshotPrefix(state.SuggestionUsage, int.MaxValue);
            var dismissedUrls = dismissedSnapshot.Length == 0
                ? EmptyUrlSet
                : new HashSet<string>(dismissedSnapshot, BrowserPolicy.UrlComparer);
            var usageByUrl = BuildSuggestionUsageLookup(usageSnapshot);
            var candidates = new List<IndexedCandidate>(bookmarks.Length + history.Length);

            for (var index = 0; index < bookmarks.Length; index++)
            {
                AppendIndexedCandidate(
                    candidates,
                    bookmarks[index],
                    index,
                    dismissedUrls,
                    usageByUrl);
            }

            for (var index = 0; index < history.Length; index++)
            {
                AppendIndexedCandidate(
                    candidates,
                    history[index],
                    index,
                    dismissedUrls,
                    usageByUrl);
            }

            return new SuggestionIndexCache(
                state,
                bookmarks,
                history,
                dismissedSnapshot,
                usageSnapshot,
                candidates.ToArray());
        }

        private static T[] SnapshotPrefix<T>(IReadOnlyList<T> items, int maximumCount)
            where T : class
        {
            var count = Math.Min(items.Count, maximumCount);
            if (count == 0) return Array.Empty<T>();

            var snapshot = new T[count];
            for (var index = 0; index < count; index++) snapshot[index] = items[index];
            return snapshot;
        }

        private static bool SequenceReferencesMatch<T>(
            IReadOnlyList<T> current,
            T[] snapshot,
            int maximumCount)
            where T : class
        {
            var count = Math.Min(current.Count, maximumCount);
            if (count != snapshot.Length) return false;

            for (var index = 0; index < count; index++)
            {
                if (!ReferenceEquals(current[index], snapshot[index])) return false;
            }

            return true;
        }
    }

    private readonly record struct IndexedCandidate(
        AddressSuggestionSource Source,
        int SourceIndex,
        string Title,
        string Url,
        int UrlHash,
        int AcceptedCount,
        DateTimeOffset LastAcceptedUtc,
        bool IsSearchQuery,
        string SearchProviderName,
        string Host,
        bool IsRootHost);

    private readonly record struct Candidate(
        AddressSuggestionSource Source,
        AddressSuggestionMatch Match,
        int Quality,
        int SourceIndex,
        string Title,
        string Url,
        int AcceptedCount,
        DateTimeOffset LastAcceptedUtc,
        int UrlHash,
        bool IsSearchQuery = false,
        string SearchProviderName = "");

    private static Dictionary<string, SuggestionUsageScore> BuildSuggestionUsageLookup(
        IReadOnlyList<AddressSuggestionUsage>? usageEntries)
    {
        if (usageEntries is null || usageEntries.Count == 0)
        {
            return EmptyUsageByUrl;
        }

        var usageByUrl = new Dictionary<string, SuggestionUsageScore>(usageEntries.Count, BrowserPolicy.UrlComparer);
        foreach (var usage in usageEntries)
        {
            if (usage is null
                || string.IsNullOrWhiteSpace(usage.Url)
                || !BrowserPolicy.IsHttpUrl(usage.Url))
            {
                continue;
            }
            var score = new SuggestionUsageScore(
                Math.Max(1, usage.AcceptedCount),
                usage.LastAcceptedUtc == default ? DateTimeOffset.UnixEpoch : usage.LastAcceptedUtc);
            if (!usageByUrl.TryGetValue(usage.Url, out var existing)
                || score.AcceptedCount > existing.AcceptedCount
                || (score.AcceptedCount == existing.AcceptedCount
                    && score.LastAcceptedUtc > existing.LastAcceptedUtc))
            {
                usageByUrl[usage.Url] = score;
            }
        }

        return usageByUrl;
    }

    private readonly record struct SuggestionUsageScore(
        int AcceptedCount,
        DateTimeOffset LastAcceptedUtc);

    private readonly record struct MatchResult(
        bool IsMatch,
        AddressSuggestionMatch Kind,
        int Quality)
    {
        public static MatchResult None => new(false, default, int.MaxValue);

        public static MatchResult Found(AddressSuggestionMatch kind, int quality)
        {
            return new MatchResult(true, kind, quality);
        }
    }
}
