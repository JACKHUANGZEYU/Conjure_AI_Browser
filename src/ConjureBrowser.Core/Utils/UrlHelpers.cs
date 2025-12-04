namespace ConjureBrowser.Core.Utils;

public static class UrlHelpers
{
    public static string? NormalizeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        input = input.Trim();

        // If user typed "example.com", assume https.
        if (!input.Contains("://"))
            input = "https://" + input;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return null;

        // Allow only a small set of schemes for now.
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "file"))
            return null;

        return uri.ToString();
    }
}
