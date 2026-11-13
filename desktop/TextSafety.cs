using System.Text;

namespace MishaWeb;

internal static class TextSafety
{
    internal const int MaximumUrlInputCharactersToParse = 4_096;

    public static string SanitizeSingleLine(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || maximumLength <= 0) return string.Empty;
        if (IsAlreadyCleanSingleLine(value, maximumLength)) return value;
        var maximumInspectedCharacters = Math.Min(
            value.Length,
            maximumLength > (int.MaxValue - 2) / 2
                ? int.MaxValue
                : (maximumLength * 2) + 2);
        var builderCapacity = maximumLength == int.MaxValue
            ? maximumInspectedCharacters
            : Math.Min(maximumLength + 1, maximumInspectedCharacters);
        var builder = new StringBuilder(builderCapacity);
        var truncated = maximumInspectedCharacters < value.Length;
        for (var index = 0; index < maximumInspectedCharacters; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= maximumInspectedCharacters
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    continue;
                }
                if (builder.Length + 2 > maximumLength)
                {
                    truncated = true;
                    break;
                }
                builder.Append(character);
                builder.Append(value[++index]);
                continue;
            }
            if (char.IsLowSurrogate(character)) continue;

            if (builder.Length + 1 > maximumLength)
            {
                truncated = true;
                break;
            }
            builder.Append(
                char.IsControl(character)
                || (character >= 0x00AD
                    && char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)
                    ? ' '
                    : character);
        }

        var singleLine = builder.ToString().Trim();
        if (!truncated) return singleLine;
        if (maximumLength == 1) return "…";
        var prefix = Truncate(singleLine, maximumLength - 1).TrimEnd();
        return prefix.Length == 0 ? "…" : prefix + "…";
    }

    private static bool IsAlreadyCleanSingleLine(string value, int maximumLength)
    {
        if (value.Length > maximumLength
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsSurrogate(character)
                || char.IsControl(character)
                || (character >= 0x00AD
                    && char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format))
            {
                return false;
            }
        }

        return true;
    }

    public static string FormatUrlForDisplay(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || maximumLength <= 0) return string.Empty;
        if (value.Length > MaximumUrlInputCharactersToParse)
        {
            return SanitizeSingleLine("Address too long to display", maximumLength);
        }

        try
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return FormatUnparsedUrlForDisplay(value, maximumLength);
            }

            var host = uri.HostNameType == UriHostNameType.IPv6
                ? $"[{uri.IdnHost}]"
                : uri.IdnHost;
            var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            var canonical = $"{uri.Scheme.ToLowerInvariant()}://{host}{port}"
                + uri.AbsolutePath
                + uri.Query
                + uri.Fragment;
            canonical = SanitizeSingleLine(canonical, maximumLength);
            var display = uri.UserInfo.Length == 0
                ? canonical
                : "⚠ credentials hidden · " + canonical;
            return TruncateWithEllipsis(display, maximumLength);
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException)
        {
            return FormatUnparsedUrlForDisplay(value, maximumLength);
        }
    }

    public static string FormatHostForDisplay(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || maximumLength <= 0) return string.Empty;
        if (value.Length > MaximumUrlInputCharactersToParse)
        {
            return SanitizeSingleLine("Address too long to display", maximumLength);
        }

        try
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || string.IsNullOrWhiteSpace(uri.IdnHost))
            {
                return SanitizeSingleLine("Invalid address", maximumLength);
            }

            var host = uri.HostNameType == UriHostNameType.IPv6
                ? $"[{uri.IdnHost}]"
                : uri.IdnHost;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            return SanitizeSingleLine(host, maximumLength);
        }
        catch (Exception error) when (error is UriFormatException or ArgumentException)
        {
            return SanitizeSingleLine("Invalid address", maximumLength);
        }
    }

    private static string FormatUnparsedUrlForDisplay(string value, int maximumLength)
    {
        var sanitized = SanitizeSingleLine(value, MaximumUrlInputCharactersToParse);
        var schemeDelimiter = sanitized.IndexOf("://", StringComparison.Ordinal);
        if (schemeDelimiter <= 0
            || !(sanitized[..schemeDelimiter].Equals("http", StringComparison.OrdinalIgnoreCase)
                || sanitized[..schemeDelimiter].Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            return TruncateWithEllipsis(sanitized, maximumLength);
        }

        var authorityStart = schemeDelimiter + 3;
        var authorityEnd = sanitized.Length;
        for (var index = authorityStart; index < sanitized.Length; index++)
        {
            if (sanitized[index] is not ('/' or '?' or '#')) continue;
            authorityEnd = index;
            break;
        }
        var userInfoEnd = -1;
        for (var index = authorityStart; index < authorityEnd; index++)
        {
            if (sanitized[index] == '@') userInfoEnd = index;
        }
        if (userInfoEnd < 0) return TruncateWithEllipsis(sanitized, maximumLength);

        var redacted = sanitized[..authorityStart] + sanitized[(userInfoEnd + 1)..];
        return TruncateWithEllipsis(
            "⚠ credentials hidden · " + redacted,
            maximumLength);
    }

    public static string RemoveUnpairedSurrogates(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder? repaired = null;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    if (repaired is not null)
                    {
                        repaired.Append(character);
                        repaired.Append(value[++index]);
                    }
                    else
                    {
                        index++;
                    }
                    continue;
                }
            }
            else if (!char.IsLowSurrogate(character))
            {
                repaired?.Append(character);
                continue;
            }

            if (repaired is null)
            {
                repaired = new StringBuilder(value.Length);
                repaired.Append(value.AsSpan(0, index));
            }
        }

        return repaired?.ToString() ?? value;
    }

    public static string Truncate(string value, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (maximumLength <= 0) return string.Empty;
        if (value.Length <= maximumLength) return value;

        var length = maximumLength;
        if (char.IsHighSurrogate(value[length - 1])
            && length < value.Length
            && char.IsLowSurrogate(value[length]))
        {
            length--;
        }
        return value[..length];
    }

    public static string TruncateWithEllipsis(string value, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length <= maximumLength) return value;
        if (maximumLength <= 0) return string.Empty;
        if (maximumLength == 1) return "…";
        return Truncate(value, maximumLength - 1) + "…";
    }
}
