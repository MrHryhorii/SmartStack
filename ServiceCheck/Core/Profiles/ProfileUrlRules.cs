namespace Tsubaki.ServiceCheck.Core.Profiles;

/// <summary>Validates service URLs and resolves diagnostic routes.</summary>
public static class ProfileUrlRules
{
    /// <summary>Validates a service root and ensures a trailing slash.</summary>
    public static Uri NormalizeBaseUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        if (baseUrl.Contains('\\')
            || baseUrl.Any(character =>
                char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new ArgumentException(
                "target.baseUrl must not contain whitespace or backslashes.",
                nameof(baseUrl));
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !Uri.IsWellFormedUriString(baseUrl, UriKind.Absolute)
            || uri.Scheme is not ("http" or "https")
            || uri.HostNameType is not (
                UriHostNameType.Dns
                or UriHostNameType.IPv4
                or UriHostNameType.IPv6))
        {
            throw new ArgumentException(
                "target.baseUrl must be an absolute HTTP or HTTPS URL with a valid host.",
                nameof(baseUrl));
        }

        if (baseUrl.Contains('?') || baseUrl.Contains('#'))
        {
            throw new ArgumentException(
                "target.baseUrl must not include a query or fragment.",
                nameof(baseUrl));
        }

        if (ContainsUserInformation(baseUrl))
        {
            throw new ArgumentException(
                "target.baseUrl must not include user information.",
                nameof(baseUrl));
        }

        return uri.AbsoluteUri.EndsWith('/')
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
    }

    /// <summary>Resolves a relative route while preserving the service root.</summary>
    public static Uri Resolve(string baseUrl, string route)
    {
        var root = NormalizeBaseUrl(baseUrl);
        ValidateRoute(route);

        var result = new Uri(root, route);

        if (!root.IsBaseOf(result))
        {
            throw new ArgumentException(
                "The route must remain under target.baseUrl.",
                nameof(route));
        }

        return result;
    }

    /// <summary>Rejects absolute routes, query strings, and traversal segments.</summary>
    private static void ValidateRoute(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);

        if (route.StartsWith('/')
            || route.Contains('\\')
            || route.Contains('?')
            || route.Contains('#')
            || route.Any(character =>
                char.IsWhiteSpace(character) || char.IsControl(character))
            || !Uri.IsWellFormedUriString(route, UriKind.Relative)
            || Uri.TryCreate(route, UriKind.Absolute, out _))
        {
            throw new ArgumentException(
                "A route must be a relative path without a leading slash, query or fragment.",
                nameof(route));
        }

        var decoded = Uri.UnescapeDataString(route);

        if (decoded.StartsWith('/')
            || decoded.Contains('\\')
            || decoded.Any(char.IsControl)
            || decoded.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                "A route must not contain traversal segments, rooted paths or control characters.",
                nameof(route));
        }
    }

    /// <summary>Detects user information in the original URL authority.</summary>
    private static bool ContainsUserInformation(string baseUrl)
    {
        var start = baseUrl.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = baseUrl.IndexOf('/', start);

        var length = (end < 0 ? baseUrl.Length : end) - start;

        return baseUrl.AsSpan(start, length).Contains('@');
    }
}