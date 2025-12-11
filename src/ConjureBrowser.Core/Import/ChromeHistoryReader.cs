using Microsoft.Data.Sqlite;
using ConjureBrowser.Core.Models;

namespace ConjureBrowser.Core.Import;

/// <summary>
/// Reads browsing history from Chrome's SQLite database.
/// </summary>
public static class ChromeHistoryReader
{
    // Chrome uses WebKit timestamps: microseconds since 1601-01-01
    private static readonly DateTime WebKitEpoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Reads browsing history from a Chrome profile.
    /// </summary>
    /// <param name="profile">The Chrome profile to read from.</param>
    /// <param name="maxEntries">Maximum number of entries to read (default 5000).</param>
    /// <returns>List of history entries found in the profile.</returns>
    public static async Task<List<HistoryEntry>> ReadHistoryAsync(ChromeProfile profile, int maxEntries = 5000)
    {
        var history = new List<HistoryEntry>();

        if (!profile.HasHistory)
            return history;

        var historyPath = Path.Combine(profile.Path, "History");
        if (!File.Exists(historyPath))
            return history;

        // Chrome locks the history database, so we need to copy it to a temp location
        var tempPath = Path.Combine(Path.GetTempPath(), $"conjure_chrome_history_{Guid.NewGuid()}.db");

        try
        {
            // Copy the database to avoid lock issues
            File.Copy(historyPath, tempPath, overwrite: true);

            var connectionString = $"Data Source={tempPath};Mode=ReadOnly";
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            // Query the urls table for history
            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT url, title, last_visit_time 
                FROM urls 
                WHERE url LIKE 'http%'
                ORDER BY last_visit_time DESC 
                LIMIT @limit";
            command.Parameters.AddWithValue("@limit", maxEntries);

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var url = reader.GetString(0);
                var title = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var lastVisitTime = reader.GetInt64(2);

                // Convert WebKit timestamp to DateTimeOffset
                var visitedAt = ConvertWebKitTimestamp(lastVisitTime);

                // Skip invalid entries
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                // Use URL as title if no title
                if (string.IsNullOrWhiteSpace(title))
                    title = url;

                history.Add(new HistoryEntry(title, url, visitedAt));
            }
        }
        catch (Exception ex)
        {
            // Log or handle gracefully - return what we have
            System.Diagnostics.Debug.WriteLine($"Error reading Chrome history: {ex.Message}");
        }
        finally
        {
            // Clean up temp file
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        return history;
    }

    /// <summary>
    /// Converts a Chrome WebKit timestamp to DateTimeOffset.
    /// WebKit timestamps are microseconds since 1601-01-01.
    /// </summary>
    private static DateTimeOffset ConvertWebKitTimestamp(long webkitTimestamp)
    {
        try
        {
            // Convert microseconds to ticks (1 tick = 100 nanoseconds, 1 microsecond = 10 ticks)
            var ticks = webkitTimestamp * 10;
            var dateTime = WebKitEpoch.AddTicks(ticks);
            return new DateTimeOffset(dateTime, TimeSpan.Zero);
        }
        catch
        {
            // Return current time if conversion fails
            return DateTimeOffset.UtcNow;
        }
    }
}
