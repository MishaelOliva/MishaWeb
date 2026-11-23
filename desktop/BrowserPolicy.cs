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

        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var httpUri)
                && (httpUri.Scheme == Uri.UriSchemeHttp || httpUri.Scheme == Uri.UriSchemeHttps))
            {
                return httpUri.AbsoluteUri.Length <= MaximumUrlLength
                    ? NavigationResolution.Navigate(httpUri.AbsoluteUri)
                    : NavigationResolution.Reject("That address is too long");
            }

            return NavigationResolution.Reject("That address uses an unsupported or unsafe scheme");
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
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var httpUri)
                && (httpUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || httpUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }

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

    private static readonly HashSet<string> HighRiskDownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msp", ".msix", ".appx",
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".lnk", ".url", ".scf", ".scr",
        ".pif", ".com", ".cpl", ".reg", ".dll",
        ".sys", ".drv", ".ocx", ".jar", ".iso", ".img", ".vhd", ".vhdx"
    };

    internal static bool IsSafeHttpUrlFast(string url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaximumUrlLength) return false;

        int authorityStart;
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            authorityStart = 8;
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            authorityStart = 7;
        }
        else
        {
            return false;
        }

        if (authorityStart >= url.Length) return false;

        var authorityEnd = url.Length;
        for (var i = authorityStart; i < url.Length; i++)
        {
            var c = url[i];
            if (c is '/' or '?' or '#')
            {
                authorityEnd = i;
                break;
            }
        }

        var authority = url.AsSpan(authorityStart, authorityEnd - authorityStart);
        if (authority.IsEmpty) return false;

        var hostEnd = authority.Length;
        var colonIndex = authority.IndexOf(':');
        if (colonIndex >= 0)
        {
            hostEnd = colonIndex;
            var portSpan = authority[(colonIndex + 1)..];
            if (portSpan.IsEmpty || portSpan.Length > 5)
            {
                return IsSafeTopLevelUrl(url, allowFileScheme: false);
            }

            var port = 0;
            for (var i = 0; i < portSpan.Length; i++)
            {
                var digit = (uint)(portSpan[i] - '0');
                if (digit > 9) return IsSafeTopLevelUrl(url, allowFileScheme: false);
                port = (port * 10) + (int)digit;
            }
            if (port > 65535) return IsSafeTopLevelUrl(url, allowFileScheme: false);
        }

        var host = authority[..hostEnd];
        if (!IsFastAsciiDnsHost(host, out _))
        {
            return IsSafeTopLevelUrl(url, allowFileScheme: false);
        }

        for (var i = authorityEnd; i < url.Length; i++)
        {
            var c = url[i];
            if (c <= 0x20 || c >= 0x7F || c is '\\' or '<' or '>' or '"' or '{' or '}' or '|' or '^' or '`')
            {
                return IsSafeTopLevelUrl(url, allowFileScheme: false);
            }
            if (c == '%')
            {
                if (i + 2 >= url.Length || !Uri.IsHexDigit(url[i + 1]) || !Uri.IsHexDigit(url[i + 2]))
                {
                    return IsSafeTopLevelUrl(url, allowFileScheme: false);
                }
                i += 2;
            }
        }

        return true;
    }

    public static bool IsSafeTopLevelUrl(string url, bool allowFileScheme = false)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > MaximumUrlLength
            || ContainsControlChars(url))
        {
            return false;
        }

        if (allowFileScheme && TryNormalizeLocalFileUrl(url, out _))
        {
            return true;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri)
            && (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(absoluteUri.IdnHost);
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
        return !string.IsNullOrWhiteSpace(url)
            && url.Length <= MaximumUrlLength
            && !ContainsControlChars(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(uri.IdnHost);
    }

    public static bool IsSafeDownloadUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > MaximumUrlLength
            || ContainsControlChars(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        {
            return !string.IsNullOrWhiteSpace(uri.IdnHost);
        }

        if (uri.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase))
        {
            var innerUrl = url[5..];
            return Uri.TryCreate(innerUrl, UriKind.Absolute, out var inner)
                && (inner.Scheme == Uri.UriSchemeHttp || inner.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrWhiteSpace(inner.IdnHost)
                && inner.AbsolutePath.Length > 1;
        }

        return uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHighRiskDownloadExtension(string? filePathOrName)
    {
        if (string.IsNullOrWhiteSpace(filePathOrName)) return false;
        var trimmed = filePathOrName.Trim();
        var extension = Path.GetExtension(trimmed);
        return !string.IsNullOrEmpty(extension) && HighRiskDownloadExtensions.Contains(extension);
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

        var rawSpan = value.AsSpan();
        var trimmedSpan = rawSpan.Trim().TrimEnd('.');
        if (trimmedSpan.Length > 0 && IsFastAsciiDnsHost(trimmedSpan, out var hasUppercase))
        {
            if (!hasUppercase && trimmedSpan.Length == value.Length)
            {
                return value;
            }

            return string.Create(trimmedSpan.Length, value, static (dest, raw) =>
            {
                var src = raw.AsSpan().Trim().TrimEnd('.');
                for (var i = 0; i < dest.Length; i++)
                {
                    var c = src[i];
                    dest[i] = (uint)(c - 'A') <= ('Z' - 'A') ? (char)(c | 0x20) : c;
                }
            });
        }

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

    private static bool IsFastAsciiDnsHost(ReadOnlySpan<char> host, out bool hasUppercase)
    {
        hasUppercase = false;
        if (host.IsEmpty || host.Length > 253) return false;

        var labelStart = 0;
        var lastLabelStart = 0;
        for (var i = 0; i < host.Length; i++)
        {
            var c = host[i];
            if (c == '.')
            {
                var labelLength = i - labelStart;
                if (labelLength == 0 || labelLength > 63 || host[i - 1] == '-') return false;
                labelStart = i + 1;
                lastLabelStart = labelStart;
            }
            else if ((uint)(c - 'a') <= ('z' - 'a'))
            {
            }
            else if ((uint)(c - 'A') <= ('Z' - 'A'))
            {
                hasUppercase = true;
            }
            else if ((uint)(c - '0') <= ('9' - '0'))
            {
            }
            else if (c == '-')
            {
                if (i == labelStart) return false;
            }
            else
            {
                return false;
            }
        }

        var finalLabelLength = host.Length - labelStart;
        if (finalLabelLength == 0 || finalLabelLength > 63 || host[^1] == '-') return false;

        // Require the final label to begin with an ASCII letter so numeric/hex IPv4
        // forms fall through to IPAddress.TryParse / Uri.TryCreate validation.
        if (!char.IsAsciiLetter(host[lastLabelStart])) return false;

        // Let punycode labels pass through Uri.IdnHost validation.
        return host.IndexOf("xn--", StringComparison.OrdinalIgnoreCase) < 0;
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
        // Require an explicit Windows drive-letter prefix (e.g. C:\ or C:/) so
        // omnibox '/' inputs and web URLs never probe File.Exists/Directory.Exists on the UI thread.
        if (string.IsNullOrWhiteSpace(value)
            || value.Length < 3
            || value.Length > MaximumUrlLength
            || !char.IsAsciiLetter(value[0])
            || value[1] != ':'
            || (value[2] != '\\' && value[2] != '/')
            || ContainsControlChars(value))
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

