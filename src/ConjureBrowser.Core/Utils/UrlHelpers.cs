namespace ConjureBrowser.Core.Utils;

public static class UrlHelpers
{
    /// <summary>
    /// Normalizes user input into a valid URL, or returns null if the input
    /// doesn't look like a URL (triggering search fallback in the caller).
    /// </summary>
    /// <remarks>
    /// Chrome-like behavior:
    /// - "google.com" → https://google.com (has dot = domain)
    /// - "https://example.com" → as-is (has scheme)
    /// - "kfc" → null (no dot, no scheme = search term)
    /// - "hello world" → null (spaces = search term)
    /// </remarks>
    public static string? NormalizeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        input = input.Trim();

        // If it contains spaces, it's definitely a search term, not a URL
        if (input.Contains(' ')) return null;

        // Check if it already has a scheme
        var hasScheme = input.Contains("://");

        // If no scheme, check if it looks like a domain (contains a dot)
        // Examples: "google.com", "en.wikipedia.org", "localhost:8080"
        // Counter-examples: "kfc", "hello", "test" (these should search)
        if (!hasScheme)
        {
            // Must contain a dot to be treated as a domain
            // Exception: localhost or IP addresses like "127.0.0.1:8080"
            var looksLikeDomain = input.Contains('.') || 
                                   input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);
            
            if (!looksLikeDomain) return null;

            // Add https:// prefix for domain-like input
            input = "https://" + input;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return null;

        // Allow only a small set of schemes
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "file"))
            return null;

        return uri.ToString();
    }
}
