using System.Text.Json;
using ConjureBrowser.Core.Models;

namespace ConjureBrowser.Core.Import;

/// <summary>
/// Reads bookmarks from Chrome's JSON bookmarks file.
/// </summary>
public static class ChromeBookmarkReader
{
    /// <summary>
    /// Reads all bookmarks from a Chrome profile.
    /// </summary>
    /// <param name="profile">The Chrome profile to read from.</param>
    /// <returns>List of bookmarks found in the profile.</returns>
    public static async Task<List<Bookmark>> ReadBookmarksAsync(ChromeProfile profile)
    {
        var bookmarks = new List<Bookmark>();

        if (!profile.HasBookmarks)
            return bookmarks;

        var bookmarksPath = Path.Combine(profile.Path, "Bookmarks");
        if (!File.Exists(bookmarksPath))
            return bookmarks;

        try
        {
            var json = await File.ReadAllTextAsync(bookmarksPath).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("roots", out var roots))
            {
                // Extract from bookmark_bar
                if (roots.TryGetProperty("bookmark_bar", out var bookmarkBar))
                {
                    ExtractBookmarksRecursive(bookmarkBar, bookmarks, "");
                }

                // Extract from other bookmarks
                if (roots.TryGetProperty("other", out var other))
                {
                    ExtractBookmarksRecursive(other, bookmarks, "");
                }

                // Extract from synced bookmarks (mobile)
                if (roots.TryGetProperty("synced", out var synced))
                {
                    ExtractBookmarksRecursive(synced, bookmarks, "");
                }
            }
        }
        catch
        {
            // Return whatever we've collected so far on error
        }

        return bookmarks;
    }

    /// <summary>
    /// Recursively extracts bookmarks from a Chrome bookmarks JSON element.
    /// </summary>
    private static void ExtractBookmarksRecursive(JsonElement element, List<Bookmark> bookmarks, string folderPath)
    {
        // Check if this is a folder with children
        if (element.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            // Get folder name for path
            var folderName = "";
            if (element.TryGetProperty("name", out var nameElement))
            {
                folderName = nameElement.GetString() ?? "";
            }

            var newPath = string.IsNullOrEmpty(folderPath) ? folderName : $"{folderPath}/{folderName}";

            foreach (var child in children.EnumerateArray())
            {
                ExtractBookmarksRecursive(child, bookmarks, newPath);
            }
        }
        else
        {
            // This is a bookmark (not a folder)
            if (element.TryGetProperty("type", out var typeElement) && 
                typeElement.GetString() == "url")
            {
                var title = "";
                var url = "";

                if (element.TryGetProperty("name", out var nameEl))
                    title = nameEl.GetString() ?? "";

                if (element.TryGetProperty("url", out var urlEl))
                    url = urlEl.GetString() ?? "";

                // Only add if we have a valid URL
                if (!string.IsNullOrWhiteSpace(url) && 
                    (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    // Use URL as title if title is empty
                    if (string.IsNullOrWhiteSpace(title))
                        title = url;

                    bookmarks.Add(new Bookmark(title, url));
                }
            }
        }
    }
}
