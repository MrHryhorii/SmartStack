using Tsubaki.ServiceCheck.Core.Profiles;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>Defines regression checks for profile URLs and routes.</summary>
internal static class ProfileUrlChecks
{
    private const string ServiceRoot = "https://tts.example.test/speech/";

    private static readonly (string Name, string Value)[] InvalidBaseUrls =
    [
        ("Relative base URL rejected", "tts.example.test/speech"),
        ("Non-HTTP scheme rejected", "ftp://tts.example.test/"),
        ("Missing host rejected", "https:///"),
        ("URL credentials rejected", "https://user:password@tts.example.test/"),
        ("Empty URL user information rejected", "https://@tts.example.test/"),
        ("Base URL query rejected", "https://tts.example.test/speech?test=1"),
        ("Base URL fragment rejected", "https://tts.example.test/speech#part"),
        ("Base URL whitespace rejected", " https://tts.example.test/speech"),
        ("Base URL backslash rejected", "https://tts.example.test\\speech")
    ];

    private static readonly (string Name, string Value)[] InvalidRoutes =
    [
        ("Absolute route rejected", "https://other.example.test/audio"),
        ("Rooted route rejected", "/v1/audio/speech"),
        ("Network-relative route rejected", "//other.example.test/audio"),
        ("Route query rejected", "v1/audio/speech?test=1"),
        ("Route fragment rejected", "v1/audio/speech#part"),
        ("Leading traversal rejected", "../v1/audio/speech"),
        ("Inner traversal rejected", "v1/../audio/speech"),
        ("Current-directory segment rejected", "v1/./audio/speech"),
        ("Encoded traversal rejected", "v1/%2e%2e/audio/speech"),
        ("Encoded separator traversal rejected", "v1%2F..%2Faudio/speech"),
        ("Encoded rooted route rejected", "%2Fv1/audio/speech"),
        ("Route backslash rejected", "v1\\audio\\speech"),
        ("Encoded backslash rejected", "v1%5Caudio/speech"),
        ("Encoded control character rejected", "v1/audio/%0Aspeech"),
        ("Empty route rejected", ""),
        ("Whitespace route rejected", " "),
        ("Malformed percent escape rejected", "v1/audio/%ZZ")
    ];

    /// <summary>Registers URL checks for the shared regression runner.</summary>
    public static (string Name, Func<Task> Run)[] CreateChecks() =>
    [
        Check("IPv4 base URL normalized", () => ExpectUri(
            ProfileUrlRules.NormalizeBaseUrl("http://127.0.0.1:5045"),
            "http://127.0.0.1:5045/")),

        Check("IPv6 base URL normalized", () => ExpectUri(
            ProfileUrlRules.NormalizeBaseUrl("http://[::1]:5045"),
            "http://[::1]:5045/")),

        Check("Proxy base URL normalized", () => ExpectUri(
            ProfileUrlRules.NormalizeBaseUrl("https://tts.example.test/speech"),
            "https://tts.example.test/speech/")),

        Check("Existing trailing slash preserved", () => ExpectUri(
            ProfileUrlRules.NormalizeBaseUrl(ServiceRoot),
            ServiceRoot)),

        Check("Root route resolved", () => ExpectUri(
            ProfileUrlRules.Resolve(
                "http://127.0.0.1:5045", "v1/audio/speech"),
            "http://127.0.0.1:5045/v1/audio/speech")),

        Check("Proxy prefix preserved", () => ExpectUri(
            ProfileUrlRules.Resolve(
                "https://tts.example.test/speech", "v1/audio/speech"),
            "https://tts.example.test/speech/v1/audio/speech")),

        Check("Configured v1 segments preserved", () => ExpectUri(
            ProfileUrlRules.Resolve(
                "https://tts.example.test/v1/", "v1/audio/speech"),
            "https://tts.example.test/v1/v1/audio/speech")),

        Check("Escaped proxy prefix preserved", () => ExpectUri(
            ProfileUrlRules.Resolve(
                "https://tts.example.test/speech%20api", "v1/audio/speech"),
            "https://tts.example.test/speech%20api/v1/audio/speech")),

        .. InvalidBaseUrls.Select(item => Check(
            item.Name,
            () => ExpectArgumentException(
                () => ProfileUrlRules.NormalizeBaseUrl(item.Value)))),

        .. InvalidRoutes.Select(item => Check(
            item.Name,
            () => ExpectArgumentException(
                () => ProfileUrlRules.Resolve(ServiceRoot, item.Value))))
    ];

    /// <summary>Adapts a synchronous assertion to the asynchronous runner.</summary>
    private static (string Name, Func<Task> Run) Check(
        string name,
        Action action)
    {
        return (name, () =>
        {
            action();
            return Task.CompletedTask;
        }
        );
    }

    /// <summary>Requires an exact normalized absolute URL.</summary>
    private static void ExpectUri(Uri actual, string expected)
    {
        if (actual.AbsoluteUri != expected)
        {
            throw new InvalidOperationException(
                $"Expected '{expected}', received '{actual.AbsoluteUri}'.");
        }
    }

    /// <summary>Requires invalid configuration to raise an argument error.</summary>
    private static void ExpectArgumentException(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Expected ArgumentException was not thrown.");
    }
}