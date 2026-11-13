using System.IO;
using System.Net;

namespace MishaWeb;

internal readonly record struct NavigationResolution(string? Url, bool IsStartPage, string? Error)
{
    public static NavigationResolution StartPage() => new(null, true, null);
    public static NavigationResolution Navigate(string url) => new(url, false, null);
    public static NavigationResolution Reject(string message) => new(null, false, message);
}

internal static class BrowserPolicy
{
    internal const int MaximumUrlLength = 4_096;
    public const string DefaultSearchProviderId = "google";
    public const string SearchProviderName = "Google";
    public static IEqualityComparer<string> UrlComparer { get; } = new WebUrlComparer();

    private static readonly IReadOnlyDictionary<string, SearchProviderOption> SearchProviders =
        new Dictionary<string, SearchProviderOption>(StringComparer.OrdinalIgnoreCase)
        {
            ["google"] = new("google", "Google", "https://www.google.com/search?q=")
        };

    public static IReadOnlyList<SearchProviderOption> AvailableSearchProviders { get; } =
        SearchProviders.Values.ToArray();

    public static string NormalizeSearchProviderId(string? providerId)
    {
        return providerId is not null && SearchProviders.ContainsKey(providerId)
            ? providerId.ToLowerInvariant()
            : DefaultSearchProviderId;
    }

    public static string GetSearchProviderName(string? providerId)
    {
        return SearchProviders.TryGetValue(NormalizeSearchProviderId(providerId), out var provider)
            ? provider.DisplayName
            : SearchProviderName;
    }

    public static string CreateSearchUrl(string query)
    {
        return CreateSearchUrl(query, DefaultSearchProviderId);
    }

    public static string CreateSearchUrl(string query, string? providerId)
    {
        var provider = SearchProviders[NormalizeSearchProviderId(providerId)];
        return provider.Endpoint + Uri.EscapeDataString(query.Trim());
    }

    public static NavigationResolution ResolveAddress(string input)
    {
        return ResolveAddress(input, DefaultSearchProviderId);
    }

    public static NavigationResolution ResolveAddress(string input, string? providerId)
    {
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0
            || value.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            || value.Equals(StartPage.Url, StringComparison.OrdinalIgnoreCase))
        {
            return NavigationResolution.StartPage();
        }

        if (value.Length > MaximumUrlLength)
        {
            return NavigationResolution.Reject("That address is too long");
        }

        if (TryResolveExistingLocalPath(value, out var localFileUrl))
        {
            return NavigationResolution.Navigate(localFileUrl);
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.IsFile)
            {
                return TryNormalizeLocalFileUrl(absoluteUri.AbsoluteUri, out localFileUrl)
                    ? NavigationResolution.Navigate(localFileUrl)
                    : NavigationResolution.Reject("Network file locations are not allowed");
            }

            if (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps)
            {
                return absoluteUri.AbsoluteUri.Length <= MaximumUrlLength
                    ? NavigationResolution.Navigate(absoluteUri.AbsoluteUri)
                    : NavigationResolution.Reject("That address is too long");
            }

            if (LooksLikeHost(value, out var hostFromScheme))
            {
                return hostFromScheme.AbsoluteUri.Length <= MaximumUrlLength
                    ? NavigationResolution.Navigate(hostFromScheme.AbsoluteUri)
                    : NavigationResolution.Reject("That address is too long");
            }

            return NavigationResolution.Reject("That address uses an unsupported or unsafe scheme");
        }

        if (value.Contains("://", StringComparison.Ordinal)
            || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
        {
            return NavigationResolution.Reject("That address uses an unsupported or unsafe scheme");
        }

        if (LooksLikeHost(value, out var hostUri))
        {
            return hostUri.AbsoluteUri.Length <= MaximumUrlLength
                ? NavigationResolution.Navigate(hostUri.AbsoluteUri)
                : NavigationResolution.Reject("That address is too long");
        }

        var searchUrl = CreateSearchUrl(value, providerId);
        return searchUrl.Length <= MaximumUrlLength
            ? NavigationResolution.Navigate(searchUrl)
            : NavigationResolution.Reject("That search is too long");
    }

    internal static bool IsRecognizedAddressInput(string input)
    {
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0) return false;
        if (TryResolveExistingLocalPath(value, out _)) return true;

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
        {
            return (absoluteUri.IsFile && TryNormalizeLocalFileUrl(absoluteUri.AbsoluteUri, out _))
                || absoluteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        return LooksLikeHost(value, out _);
    }

    public static bool UrlEquals(string left, string right) => UrlComparer.Equals(left, right);

    public static bool IsSafeTopLevelUrl(string url, bool allowFileScheme = false)
    {
        if (url.Length > MaximumUrlLength) return false;

        if (allowFileScheme && TryNormalizeLocalFileUrl(url, out _))
        {
            return true;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri)
            && (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps);
    }

    public static bool IsPopupBootstrapUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > MaximumUrlLength
            || ContainsControlChars(url))
        {
            return false;
        }
        if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        if (!url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return false;

        var innerUrl = url[5..];
        return Uri.TryCreate(innerUrl, UriKind.Absolute, out var inner)
            && (inner.Scheme == Uri.UriSchemeHttp || inner.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(inner.IdnHost)
            && inner.AbsolutePath.Length > 1;
    }

    public static bool IsHttpUrl(string url)
    {
        return url.Length <= MaximumUrlLength
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public static bool IsLocalFileUrl(string? url) =>
        TryNormalizeLocalFileUrl(url, out _);

    public static bool TryNormalizeLocalFileUrl(string? value, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumUrlLength
            || ContainsControlChars(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.IsFile
            || uri.IsUnc
            || !string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(uri.LocalPath);
            var normalized = new Uri(fullPath);
            if (!normalized.IsFile || normalized.IsUnc || !string.IsNullOrEmpty(normalized.Host)) return false;
            normalizedUrl = normalized.AbsoluteUri + uri.Query + uri.Fragment;
            return normalizedUrl.Length <= MaximumUrlLength;
        }
        catch (Exception error) when (error is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or UriFormatException)
        {
            return false;
        }
    }

    public static bool IsLegacyDuckDuckGoSearchUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.IdnHost.Equals("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.AbsolutePath.Equals("/", StringComparison.Ordinal)
            && (uri.Query.StartsWith("?q=", StringComparison.OrdinalIgnoreCase)
                || uri.Query.Contains("&q=", StringComparison.OrdinalIgnoreCase));
    }

    public static string? NormalizeExactHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var candidate = value.Trim();
        if (candidate.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)) return null;
            candidate = parsed.Host;
        }
        candidate = candidate.Trim().TrimEnd('.').Trim('[', ']');
        if (candidate.Length == 0 || candidate.Any(char.IsWhiteSpace)) return null;
        if (IPAddress.TryParse(candidate, out var ipAddress))
        {
            return ipAddress.ToString().ToLowerInvariant();
        }
        if (candidate.Contains('/')
            || candidate.Contains('?')
            || candidate.Contains('#')
            || candidate.Contains(':'))
        {
            return null;
        }

        var hostType = Uri.CheckHostName(candidate);
        if (hostType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return null;
        }

        return Uri.TryCreate("https://" + candidate, UriKind.Absolute, out var hostUri)
            ? hostUri.IdnHost.TrimEnd('.').ToLowerInvariant()
            : null;
    }

    public static bool IsExactHost(string? value, string? expectedHost)
    {
        var actual = NormalizeExactHost(value);
        var expected = NormalizeExactHost(expectedHost);
        return actual is not null
            && expected is not null
            && actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeHost(string value, out Uri uri)
    {
        uri = null!;
        if (ContainsWhiteSpace(value)
            || value.Contains("://", StringComparison.Ordinal)
            || !MayLookLikeHostCandidate(value))
        {
            return false;
        }
        if (!Uri.TryCreate("http://" + value, UriKind.Absolute, out var candidate)) return false;

        var normalizedHost = candidate.IdnHost.TrimEnd('.');
        var hostType = Uri.CheckHostName(normalizedHost);
        var isLocalhost = normalizedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || normalizedHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        var isIpAddress = hostType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            || IPAddress.TryParse(normalizedHost, out _);
        var isDomain = hostType == UriHostNameType.Dns
            && (normalizedHost.Contains('.') || normalizedHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase));

        if (!isLocalhost && !isIpAddress && !isDomain) return false;

        var scheme = isLocalhost || isIpAddress ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
        if (!Uri.TryCreate(scheme + "://" + value, UriKind.Absolute, out var resolved) || resolved is null)
        {
            return false;
        }

        uri = resolved;
        return true;
    }

    private static bool MayLookLikeHostCandidate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '.' || character == ':') return true;
        }

        return value.Contains("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsWhiteSpace(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index])) return true;
        }

        return false;
    }

    private static bool ContainsControlChars(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index])) return true;
        }

        return false;
    }

    private static bool TryResolveExistingLocalPath(string value, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumUrlLength
            || ContainsControlChars(value)
            || !Path.IsPathFullyQualified(value)
            || value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(value);
            var pathUri = new Uri(fullPath);
            if (pathUri.IsUnc || !string.IsNullOrEmpty(pathUri.Host)) return false;

            string? target = null;
            if (File.Exists(fullPath))
            {
                target = fullPath;
            }
            else if (Directory.Exists(fullPath))
            {
                var indexFile = Path.Combine(fullPath, "index.html");
                target = File.Exists(indexFile) ? indexFile : fullPath;
            }

            return target is not null
                && TryNormalizeLocalFileUrl(new Uri(target).AbsoluteUri, out normalizedUrl);
        }
        catch (Exception error) when (error is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or UriFormatException)
        {
            return false;
        }
    }

    private sealed class WebUrlComparer : IEqualityComparer<string>
    {
        public bool Equals(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null) return false;
            if (!Uri.TryCreate(left, UriKind.Absolute, out var leftUri)
                || !Uri.TryCreate(right, UriKind.Absolute, out var rightUri))
            {
                return string.Equals(left, right, StringComparison.Ordinal);
            }

            return leftUri.Scheme.Equals(rightUri.Scheme, StringComparison.OrdinalIgnoreCase)
                && leftUri.IdnHost.Equals(rightUri.IdnHost, StringComparison.OrdinalIgnoreCase)
                && leftUri.Port == rightUri.Port
                && leftUri.UserInfo.Equals(rightUri.UserInfo, StringComparison.Ordinal)
                && leftUri.PathAndQuery.Equals(rightUri.PathAndQuery, StringComparison.Ordinal)
                && leftUri.Fragment.Equals(rightUri.Fragment, StringComparison.Ordinal);
        }

        public int GetHashCode(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return StringComparer.Ordinal.GetHashCode(value);
            }

            var hash = new HashCode();
            hash.Add(uri.Scheme, StringComparer.OrdinalIgnoreCase);
            hash.Add(uri.IdnHost, StringComparer.OrdinalIgnoreCase);
            hash.Add(uri.Port);
            hash.Add(uri.UserInfo, StringComparer.Ordinal);
            hash.Add(uri.PathAndQuery, StringComparer.Ordinal);
            hash.Add(uri.Fragment, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}

internal sealed record SearchProviderOption(string Id, string DisplayName, string Endpoint);

