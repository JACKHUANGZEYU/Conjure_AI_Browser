using ConjureBrowser.Core.Models;
using ConjureBrowser.Core.Services;

namespace ConjureBrowser.Core.Import;

/// <summary>
/// Result of a Chrome data import operation.
/// </summary>
public sealed class ImportResult
{
    /// <summary>
    /// Number of bookmarks successfully imported.
    /// </summary>
    public int BookmarksImported { get; init; }

    /// <summary>
    /// Number of bookmarks skipped (duplicates).
    /// </summary>
    public int BookmarksSkipped { get; init; }

    /// <summary>
    /// Number of history entries successfully imported.
    /// </summary>
    public int HistoryImported { get; init; }

    /// <summary>
    /// Number of history entries skipped.
    /// </summary>
    public int HistorySkipped { get; init; }

    /// <summary>
    /// Any error message if the import failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether the import completed successfully.
    /// </summary>
    public bool Success => string.IsNullOrEmpty(ErrorMessage);
}

/// <summary>
/// Orchestrates importing data from Chrome profiles into Conjure Browser.
/// </summary>
public sealed class ChromeDataImporter
{
    /// <summary>
    /// Imports bookmarks from a Chrome profile into the bookmark store.
    /// </summary>
    /// <param name="profile">The Chrome profile to import from.</param>
    /// <param name="bookmarkStore">The bookmark store to import into.</param>
    /// <returns>Number of bookmarks imported and skipped.</returns>
    public async Task<(int imported, int skipped)> ImportBookmarksAsync(
        ChromeProfile profile, 
        BookmarkStore bookmarkStore)
    {
        var chromeBookmarks = await ChromeBookmarkReader.ReadBookmarksAsync(profile).ConfigureAwait(false);
        
        var imported = 0;
        var skipped = 0;

        foreach (var bookmark in chromeBookmarks)
        {
            // Check if already exists (by URL)
            if (bookmarkStore.IsBookmarked(bookmark.Url))
            {
                skipped++;
                continue;
            }

            // Add the bookmark using ToggleAsync (which adds if not present)
            await bookmarkStore.ToggleAsync(bookmark.Url, bookmark.Title).ConfigureAwait(false);
            imported++;
        }

        return (imported, skipped);
    }

    /// <summary>
    /// Imports history from a Chrome profile into the history store.
    /// </summary>
    /// <param name="profile">The Chrome profile to import from.</param>
    /// <param name="historyStore">The history store to import into.</param>
    /// <returns>Number of history entries imported.</returns>
    public async Task<(int imported, int skipped)> ImportHistoryAsync(
        ChromeProfile profile, 
        HistoryStore historyStore)
    {
        var chromeHistory = await ChromeHistoryReader.ReadHistoryAsync(profile).ConfigureAwait(false);
        
        var imported = 0;
        var skipped = 0;

        // Get existing URLs for duplicate detection
        var existingUrls = historyStore.Items
            .Select(h => h.Url.ToLowerInvariant())
            .ToHashSet();

        foreach (var entry in chromeHistory)
        {
            // Skip if this exact URL already exists (simple dedup)
            if (existingUrls.Contains(entry.Url.ToLowerInvariant()))
            {
                skipped++;
                continue;
            }

            await historyStore.RecordAsync(entry.Title, entry.Url, entry.VisitedAt).ConfigureAwait(false);
            existingUrls.Add(entry.Url.ToLowerInvariant());
            imported++;
        }

        return (imported, skipped);
    }

    /// <summary>
    /// Imports all selected data types from a Chrome profile.
    /// </summary>
    /// <param name="profile">The Chrome profile to import from.</param>
    /// <param name="bookmarkStore">The bookmark store (null to skip bookmarks).</param>
    /// <param name="historyStore">The history store (null to skip history).</param>
    /// <param name="progress">Optional progress callback (0.0 to 1.0).</param>
    /// <returns>Import result with statistics.</returns>
    public async Task<ImportResult> ImportAllAsync(
        ChromeProfile profile,
        BookmarkStore? bookmarkStore,
        HistoryStore? historyStore,
        IProgress<double>? progress = null)
    {
        try
        {
            var result = new ImportResult();
            var bookmarksImported = 0;
            var bookmarksSkipped = 0;
            var historyImported = 0;
            var historySkipped = 0;

            progress?.Report(0.0);

            // Import bookmarks
            if (bookmarkStore != null && profile.HasBookmarks)
            {
                progress?.Report(0.1);
                (bookmarksImported, bookmarksSkipped) = await ImportBookmarksAsync(profile, bookmarkStore).ConfigureAwait(false);
                progress?.Report(0.5);
            }

            // Import history
            if (historyStore != null && profile.HasHistory)
            {
                progress?.Report(0.5);
                (historyImported, historySkipped) = await ImportHistoryAsync(profile, historyStore).ConfigureAwait(false);
                progress?.Report(1.0);
            }

            return new ImportResult
            {
                BookmarksImported = bookmarksImported,
                BookmarksSkipped = bookmarksSkipped,
                HistoryImported = historyImported,
                HistorySkipped = historySkipped
            };
        }
        catch (Exception ex)
        {
            return new ImportResult
            {
                ErrorMessage = ex.Message
            };
        }
    }
}
