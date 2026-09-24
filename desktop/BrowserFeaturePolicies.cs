using System.Text;

namespace MishaWeb;

internal enum CommandSource
{
    BrowserCommand,
    OpenTab,
    Favorite,
    Session,
    RecentlyClosed,
    History
}

internal enum CommandMatchClass
{
    Exact,
    Prefix,
    Substring,
    Subsequence,
    Empty
}

internal sealed record CommandCandidate(
    string Title,
    string Detail,
    CommandSource Source,
    string? Url = null,
    string Shortcut = "",
    int MruRank = int.MaxValue,
    int SourceIndex = 0,
    object? Target = null);

internal readonly record struct RankedCommand(
    CommandCandidate Candidate,
    CommandMatchClass MatchClass,
    int Quality);

/// <summary>
/// Ranks palette and open-tab rows using bounded, allocation-light text matching.
/// It deliberately knows nothing about files, WebView2, or the network.
/// </summary>
internal static class CommandRankingEngine
{
    internal const int MaximumRows = 10;
    internal const int MaximumTitleCharacters = 256;
    internal const int MaximumDetailCharacters = 512;

    public static IReadOnlyList<RankedCommand> Rank(
        IEnumerable<CommandCandidate> candidates,
        string? query,
        int maximumRows = MaximumRows)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var limit = Math.Clamp(maximumRows, 0, MaximumRows);
        if (limit == 0) return Array.Empty<RankedCommand>();

        var normalizedQuery = Normalize(query);
        var matches = new List<RankedCommand>();
        foreach (var candidate in candidates)
        {
            if (candidate is null || !IsSafeTarget(candidate.Url)) continue;

            var match = Classify(candidate, normalizedQuery);
            if (match is null) continue;
            matches.Add(new RankedCommand(candidate, match.Value.Kind, match.Value.Quality));
        }

        matches.Sort(Compare);
        if (matches.Count > limit) matches.RemoveRange(limit, matches.Count - limit);
        return matches;
    }

    public static IReadOnlyList<CommandCandidate> RankCandidates(
        IEnumerable<CommandCandidate> candidates,
        string? query,
        int maximumRows = MaximumRows)
    {
        return Rank(candidates, query, maximumRows).Select(item => item.Candidate).ToArray();
    }

    private static int Compare(RankedCommand left, RankedCommand right)
    {
        var comparison = left.MatchClass.CompareTo(right.MatchClass);
        if (comparison != 0) return comparison;

        comparison = SourcePriority(left.Candidate.Source).CompareTo(SourcePriority(right.Candidate.Source));
        if (comparison != 0) return comparison;

        if (left.Candidate.Source == CommandSource.OpenTab)
        {
            comparison = left.Candidate.MruRank.CompareTo(right.Candidate.MruRank);
            if (comparison != 0) return comparison;
        }

        comparison = left.Quality.CompareTo(right.Quality);
        if (comparison != 0) return comparison;

        comparison = left.Candidate.SourceIndex.CompareTo(right.Candidate.SourceIndex);
        if (comparison != 0) return comparison;

        comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Candidate.Title, right.Candidate.Title);
        if (comparison != 0) return comparison;
        return StringComparer.OrdinalIgnoreCase.Compare(left.Candidate.Url, right.Candidate.Url);
    }

    private static int SourcePriority(CommandSource source)
    {
        return source switch
        {
            CommandSource.BrowserCommand => 0,
            CommandSource.OpenTab => 1,
            CommandSource.Favorite => 2,
            CommandSource.Session => 3,
            CommandSource.RecentlyClosed => 4,
            CommandSource.History => 5,
            _ => 6
        };
    }

    private static MatchResult? Classify(CommandCandidate candidate, string query)
    {
        if (query.Length == 0)
        {
            return new MatchResult(CommandMatchClass.Empty, 0);
        }

        var title = Limit(Normalize(candidate.Title), MaximumTitleCharacters);
        var detail = Limit(Normalize(candidate.Detail), MaximumDetailCharacters);
        var url = Limit(candidate.Url ?? string.Empty, MaximumDetailCharacters);

        if (title.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return new MatchResult(CommandMatchClass.Exact, 0);
        }

        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return new MatchResult(CommandMatchClass.Prefix, 0);
        }

        if (HasWordPrefix(title, query))
        {
            return new MatchResult(CommandMatchClass.Prefix, 1);
        }

        if (detail.StartsWith(query, StringComparison.OrdinalIgnoreCase)
            || url.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return new MatchResult(CommandMatchClass.Prefix, 2);
        }

        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return new MatchResult(CommandMatchClass.Substring, 0);
        }

        if (detail.Contains(query, StringComparison.OrdinalIgnoreCase)
            || url.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return new MatchResult(CommandMatchClass.Substring, 1);
        }

        if (query.Length >= 3 && IsSubsequence(query, title))
        {
            return new MatchResult(CommandMatchClass.Subsequence, 0);
        }

        if (query.Length >= 3 && (IsSubsequence(query, detail) || IsSubsequence(query, url)))
        {
            return new MatchResult(CommandMatchClass.Subsequence, 1);
        }

        return null;
    }

    private static bool IsSafeTarget(string? url)
    {
        return url is null || BrowserPolicy.IsSafeTopLevelUrl(url);
    }

    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in TextSafety.RemoveUnpairedSurrogates(value))
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace) builder.Append(' ');
            builder.Append(character);
            pendingSpace = false;
        }
        return builder.ToString();
    }

    private static string Limit(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : TextSafety.Truncate(value, maximumLength);
    }

    private static bool HasWordPrefix(string value, string query)
    {
        for (var index = 1; index + query.Length <= value.Length; index++)
        {
            if (char.IsLetterOrDigit(value[index - 1])) continue;
            if (value.AsSpan(index).StartsWith(query, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsSubsequence(string query, string value)
    {
        var queryIndex = 0;
        for (var index = 0; index < value.Length && queryIndex < query.Length; index++)
        {
            if (char.ToUpperInvariant(value[index]) == char.ToUpperInvariant(query[queryIndex])) queryIndex++;
        }
        return queryIndex == query.Length;
    }

    private readonly record struct MatchResult(CommandMatchClass Kind, int Quality);
}

/// <summary>Applies only explicit, safe URL cleanup requested by the user.</summary>
internal static class CleanLinkPolicy
{
    private static readonly HashSet<string> RemovableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "dclid", "msclkid", "mc_cid", "mc_eid", "igshid",
        "twclid", "ttclid", "srsltid", "gbraid", "wbraid", "gad_source",
        "li_fat_id", "mkt_tok", "rdt_cid", "sc_cid", "epik", "yclid",
        "vero_id", "vero_conv", "oly_anon_id", "oly_enc_id", "_hsenc", "_hsmi"
    };

    private static readonly HashSet<string> SigningKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "signature", "sig", "x-amz-signature", "x-goog-signature", "token",
        "access_token", "auth", "expires", "policy", "key-pair-id"
    };

    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > BrowserPolicy.MaximumUrlLength) return value ?? string.Empty;
        if (HasMalformedPercentEscape(value)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return value;
        }

        var queryStart = value.IndexOf('?');
        if (queryStart < 0) return value;
        var fragmentStart = value.IndexOf('#', queryStart + 1);
        var queryEnd = fragmentStart >= 0 ? fragmentStart : value.Length;
        var query = value[(queryStart + 1)..queryEnd];
        if (query.Length == 0) return value;

        var parts = query.Split('&');
        List<string>? retained = null;
        for (var index = 0; index < parts.Length; index++)
        {
            var key = GetKey(parts[index]);
            if (SigningKeys.Contains(key)) return value;
            if (IsRemovableKey(key))
            {
                if (retained is not null) continue;
                retained = new List<string>(parts.Length - 1);
                for (var retainedIndex = 0; retainedIndex < index; retainedIndex++)
                {
                    retained.Add(parts[retainedIndex]);
                }
            }
            else
            {
                retained?.Add(parts[index]);
            }
        }
        if (retained is null) return value;

        var prefix = value[..queryStart];
        var fragment = fragmentStart >= 0 ? value[fragmentStart..] : string.Empty;
        if (retained.Count == 0) return prefix + fragment;
        return prefix + "?" + string.Join("&", retained) + fragment;
    }

    public static bool TryClean(string? value, out string cleaned)
    {
        cleaned = Clean(value);
        return !string.Equals(cleaned, value ?? string.Empty, StringComparison.Ordinal);
    }

    private static bool IsRemovableKey(string key)
    {
        return key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
            || RemovableKeys.Contains(key);
    }

    private static string GetKey(string part)
    {
        var equalsIndex = part.IndexOf('=');
        var rawKey = equalsIndex >= 0 ? part[..equalsIndex] : part;
        try
        {
            return Uri.UnescapeDataString(rawKey.Replace('+', ' '));
        }
        catch
        {
            return rawKey;
        }
    }

    private static bool HasMalformedPercentEscape(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length
                || !Uri.IsHexDigit(value[index + 1])
                || !Uri.IsHexDigit(value[index + 2]))
            {
                return true;
            }
            index += 2;
        }
        return false;
    }
}

internal sealed class MruSwitcherSnapshot<T>(T original, IReadOnlyList<T> items)
    where T : notnull
{
    private readonly List<T> selectableItems = items.ToList();

    public T Original { get; } = original;
    public IReadOnlyList<T> Items => selectableItems;
    public int SelectedIndex { get; private set; }
    public T Selected => selectableItems[SelectedIndex];

    public T? Move(int direction)
    {
        if (selectableItems.Count == 0) return default;
        var step = direction < 0 ? -1 : 1;
        SelectedIndex = (SelectedIndex + step + selectableItems.Count) % selectableItems.Count;
        return Selected;
    }
}

/// <summary>Runtime-only MRU order. It never writes to BrowserState.</summary>
internal sealed class MruTabModel<T> where T : notnull
{
    private readonly List<T> order = [];

    public IReadOnlyList<T> Order => order;

    public void ObserveActivation(T item)
    {
        order.Remove(item);
        order.Insert(0, item);
    }

    public void Remove(T item)
    {
        order.Remove(item);
    }

    public void Reset(IEnumerable<T> items)
    {
        order.Clear();
        foreach (var item in items.Reverse()) ObserveActivation(item);
    }

    public MruSwitcherSnapshot<T> Begin(T active)
    {
        var snapshot = order.Where(item => !EqualityComparer<T>.Default.Equals(item, active)).ToArray();
        return new MruSwitcherSnapshot<T>(active, snapshot);
    }

    public MruSwitcherSnapshot<T> BeginSnapshot(T active) => Begin(active);

    public void Commit(MruSwitcherSnapshot<T> snapshot)
    {
        ObserveActivation(snapshot.Selected);
    }

    public void Cancel(MruSwitcherSnapshot<T> snapshot)
    {
        ObserveActivation(snapshot.Original);
    }
}

internal static class SitePreferencePolicy
{
    public static string? GetHost(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost)) return null;
        if (Uri.TryCreate(urlOrHost, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return BrowserPolicy.NormalizeExactHost(uri.Host);
        }
        return BrowserPolicy.NormalizeExactHost(urlOrHost);
    }

    public static double GetZoom(IEnumerable<SiteZoomEntry>? entries, string? urlOrHost)
    {
        var host = GetHost(urlOrHost);
        if (host is null) return 1.0;
        var entry = entries?.FirstOrDefault(item => BrowserPolicy.IsExactHost(item.Host, host));
        return entry is null || double.IsNaN(entry.ZoomFactor) || double.IsInfinity(entry.ZoomFactor)
            ? 1.0
            : Math.Clamp(entry.ZoomFactor, BrowserStateStore.MinimumSiteZoom, BrowserStateStore.MaximumSiteZoom);
    }

    public static bool IsMuted(IEnumerable<string>? entries, string? urlOrHost)
    {
        var host = GetHost(urlOrHost);
        return host is not null && entries?.Any(item => BrowserPolicy.IsExactHost(item, host)) == true;
    }

    public static bool ShouldMuteAudio(
        AudioOutputPolicy policy,
        IEnumerable<string>? allowedAudioHosts,
        IEnumerable<string>? mutedHosts,
        string? urlOrHost)
    {
        if (policy == AudioOutputPolicy.MuteAll) return true;
        var host = GetHost(urlOrHost);
        if (host is null) return false;
        if (policy == AudioOutputPolicy.SpecificSitesOnly)
        {
            return allowedAudioHosts?.Any(item => BrowserPolicy.IsExactHost(item, host)) != true;
        }
        return mutedHosts?.Any(item => BrowserPolicy.IsExactHost(item, host)) == true;
    }

    public static bool CanRequestMicrophone(
        AudioInputPolicy policy,
        IEnumerable<string>? allowedMicrophoneHosts,
        IEnumerable<string>? blockedMicrophoneHosts,
        string? urlOrHost,
        out bool? autoDecision)
    {
        var host = GetHost(urlOrHost);
        if (policy == AudioInputPolicy.BlockAll)
        {
            autoDecision = false;
            return false;
        }
        if (host is not null && blockedMicrophoneHosts?.Any(item => BrowserPolicy.IsExactHost(item, host)) == true)
        {
            autoDecision = false;
            return false;
        }
        if (host is not null && allowedMicrophoneHosts?.Any(item => BrowserPolicy.IsExactHost(item, host)) == true)
        {
            autoDecision = true;
            return true;
        }
        if (policy == AudioInputPolicy.SpecificSitesOnly)
        {
            autoDecision = false;
            return false;
        }
        autoDecision = null;
        return true;
    }
}
