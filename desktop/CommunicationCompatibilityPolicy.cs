namespace MishaWeb;

/// <summary>
/// Keeps narrowly scoped, functional exceptions for browser-based calling.
/// The shield remains active for ordinary requests and until device access is granted.
/// </summary>
internal static class CommunicationCompatibilityPolicy
{
    private static readonly string[][] ProviderHosts =
    [
        ["messenger.com", "facebook.com", "msngr.com"],
        ["discord.com", "discordapp.com"],
        ["zoom.us", "zoom.com", "zoomgov.com"]
    ];

    private static readonly string[][] ProviderTransportHosts =
    [
        ["messenger.com", "facebook.com", "msngr.com", "facebook.net", "fbcdn.net", "fbsbx.com", "meta.com", "m.me", "tfbnw.net", "meta.ai", "fb.com"],
        ["discord.com", "discordapp.com", "discord.gg", "discord.media"],
        ["zoom.us", "zoom.com", "zoomgov.com", "zoomcdn.com"]
    ];

    public static bool IsCallSite(string? url)
    {
        return TryGetProvider(url, out _);
    }

    public static bool ShouldBypassShield(
        string? topLevelUrl,
        string? requestUrl,
        AdBlockResourceType resourceType,
        bool mediaAccessGranted)
    {
        if (resourceType is not (AdBlockResourceType.WebSocket or AdBlockResourceType.Media)
            || string.IsNullOrWhiteSpace(requestUrl)
            || !Uri.TryCreate(requestUrl, UriKind.Absolute, out var requestUri)
            || requestUri.Scheme is not ("https" or "wss"))
        {
            return false;
        }

        // Once the user has granted a secure page device access, permit only
        // same-origin WebSocket signalling and media traffic. A pending prompt
        // is deliberately insufficient, and third-party destinations continue
        // through the normal shield unless they are in an audited provider map.
        if (mediaAccessGranted
            && Uri.TryCreate(topLevelUrl, UriKind.Absolute, out var topLevelUri)
            && topLevelUri.Scheme == Uri.UriSchemeHttps
            && topLevelUri.IdnHost.Equals(requestUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            && topLevelUri.Port == requestUri.Port)
        {
            return true;
        }

        if (!TryGetProvider(topLevelUrl, out var provider)) return false;

        var host = requestUri.IdnHost.AsSpan().TrimEnd('.');
        return MatchesAnyProviderHost(host, ProviderTransportHosts[provider]);
    }

    public static bool ShouldProtectBackgroundTab(
        bool userKeepAwake,
        bool microphoneAccessGranted,
        bool cameraAccessGranted)
    {
        return userKeepAwake || microphoneAccessGranted || cameraAccessGranted;
    }

    public static bool IsProviderTransportHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var normalized = host.AsSpan().TrimEnd('.');
        for (var i = 0; i < ProviderTransportHosts.Length; i++)
        {
            if (MatchesAnyProviderHost(normalized, ProviderTransportHosts[i])) return true;
        }
        return false;
    }

    public static bool ShouldBypassCommunicationRequest(
        string? topLevelUrl,
        string? requestUrl,
        AdBlockResourceType resourceType)
    {
        if (resourceType is not (AdBlockResourceType.WebSocket
                or AdBlockResourceType.Media
                or AdBlockResourceType.XmlHttpRequest
                or AdBlockResourceType.Fetch
                or AdBlockResourceType.Script
                or AdBlockResourceType.Ping
                or AdBlockResourceType.Other))
        {
            return false;
        }

        if (!TryGetProvider(topLevelUrl, out var provider)) return false;

        if (string.IsNullOrWhiteSpace(requestUrl)
            || (!requestUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !requestUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (TryExtractSimpleAsciiHost(requestUrl, out var quickRequestHost))
        {
            if (!MatchesAnyProviderHost(quickRequestHost, ProviderTransportHosts[provider]))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var requestUri)
            || requestUri.Scheme is not ("https" or "wss"))
        {
            return false;
        }

        var host = requestUri.IdnHost.AsSpan().TrimEnd('.');
        return MatchesAnyProviderHost(host, ProviderTransportHosts[provider]);
    }

    public static bool IsTrustedPopup(
        string? sourceUrl,
        string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl) || string.IsNullOrWhiteSpace(targetUrl))
        {
            return false;
        }

        if (targetUrl.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetProvider(sourceUrl, out _);
        }

        return TryGetProvider(sourceUrl, out var sourceProvider)
            && TryGetProvider(targetUrl, out var targetProvider, allowBlob: true)
            && sourceProvider == targetProvider;
    }

    private static bool TryGetProvider(string? url, out int provider, bool allowBlob = false)
    {
        provider = -1;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var candidate = url.AsSpan();
        if (allowBlob && candidate.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[5..];
        }

        if (!candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Fast-reject non-call-site ASCII hosts before allocating a Uri.
        if (TryExtractSimpleAsciiHost(candidate, out var quickHost))
        {
            var matchedQuickProvider = -1;
            for (var providerIndex = 0; providerIndex < ProviderHosts.Length; providerIndex++)
            {
                if (MatchesAnyProviderHost(quickHost, ProviderHosts[providerIndex]))
                {
                    matchedQuickProvider = providerIndex;
                    break;
                }
            }

            if (matchedQuickProvider < 0)
            {
                return false;
            }
        }

        var uriInput = candidate.Length == url.Length ? url : candidate.ToString();
        if (!Uri.TryCreate(uriInput, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var host = uri.IdnHost.AsSpan().TrimEnd('.');
        for (var providerIndex = 0; providerIndex < ProviderHosts.Length; providerIndex++)
        {
            if (MatchesAnyProviderHost(host, ProviderHosts[providerIndex]))
            {
                provider = providerIndex;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractSimpleAsciiHost(ReadOnlySpan<char> url, out ReadOnlySpan<char> host)
    {
        host = default;
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return false;
        var authorityStart = schemeEnd + 3;
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
            if (c is '@' or '[' or ']' or '\\' or '%' || c <= 0x20 || c >= 0x7F)
            {
                return false;
            }
        }

        var authority = url[authorityStart..authorityEnd];
        var colonIndex = authority.IndexOf(':');
        var rawHost = colonIndex >= 0 ? authority[..colonIndex] : authority;
        rawHost = rawHost.TrimEnd('.');
        if (rawHost.IsEmpty) return false;
        host = rawHost;
        return true;
    }

    private static bool MatchesAnyProviderHost(ReadOnlySpan<char> host, string[] roots)
    {
        foreach (var root in roots)
        {
            if (HostMatches(host, root)) return true;
        }
        return false;
    }

    private static bool HostMatches(ReadOnlySpan<char> host, string root)
    {
        if (host.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        return host.Length > root.Length
            && host[host.Length - root.Length - 1] == '.'
            && host[^root.Length..].Equals(root, StringComparison.OrdinalIgnoreCase);
    }
}
