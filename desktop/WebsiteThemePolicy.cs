using Microsoft.Web.WebView2.Core;

namespace MishaWeb;

/// <summary>
/// Applies the user's website color preference without injecting page scripts or styles.
/// The profile hint lets theme-aware sites select their own palette; Chromium's reversible
/// auto-dark override covers sites that ignore the hint.
/// </summary>
internal static class WebsiteThemePolicy
{
    internal const string AutoDarkModeCommand = "Emulation.setAutoDarkModeOverride";
    internal const string EnableAutoDarkModeParameters = "{\"enabled\":true}";
    internal const string ClearAutoDarkModeParameters = "{}";

    internal static CoreWebView2PreferredColorScheme GetPreferredColorScheme(bool darkModeEnabled)
    {
        return darkModeEnabled
            ? CoreWebView2PreferredColorScheme.Dark
            : CoreWebView2PreferredColorScheme.Light;
    }

    internal static string GetAutoDarkModeParameters(bool darkModeEnabled)
    {
        return darkModeEnabled
            ? EnableAutoDarkModeParameters
            : ClearAutoDarkModeParameters;
    }

    internal static Color GetLoadingBackgroundColor(bool darkModeEnabled)
    {
        return darkModeEnabled ? NativeUiTheme.Window : Color.White;
    }

    internal static void ApplyPreferredColorScheme(CoreWebView2 core, bool darkModeEnabled)
    {
        ArgumentNullException.ThrowIfNull(core);
        core.Profile.PreferredColorScheme = GetPreferredColorScheme(darkModeEnabled);
    }

    internal static Task ApplyAutoDarkModeOverrideAsync(CoreWebView2 core, bool darkModeEnabled)
    {
        ArgumentNullException.ThrowIfNull(core);
        return core.CallDevToolsProtocolMethodAsync(
            AutoDarkModeCommand,
            GetAutoDarkModeParameters(darkModeEnabled));
    }

    internal static bool IsHostExcluded(string? url, IReadOnlyCollection<string>? excludedHosts)
    {
        if (excludedHosts is null || excludedHosts.Count == 0)
        {
            return false;
        }

        var targetHost = NormalizeHttpHostOrHostname(url);
        if (targetHost is null)
        {
            return false;
        }

        foreach (var entry in excludedHosts)
        {
            var normalizedEntry = BrowserPolicy.NormalizeExactHost(entry);
            if (normalizedEntry is not null
                && string.Equals(targetHost, normalizedEntry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ShouldApplyAutoDark(
        bool globalDarkModeEnabled,
        string? url,
        IReadOnlyCollection<string>? excludedHosts)
    {
        return globalDarkModeEnabled && !IsHostExcluded(url, excludedHosts);
    }

    private static string? NormalizeHttpHostOrHostname(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost))
        {
            return null;
        }

        var trimmed = urlOrHost.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            return BrowserPolicy.NormalizeExactHost(uri.IdnHost);
        }

        return BrowserPolicy.NormalizeExactHost(trimmed);
    }
}
