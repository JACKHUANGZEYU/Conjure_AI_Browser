using ConjureBrowser.Core.Models;
using Microsoft.Data.Sqlite;

namespace ConjureBrowser.Core.Import;

/// <summary>
/// Reads saved passwords (logins) from Chrome's Login Data SQLite database.
/// </summary>
public static class ChromePasswordReader
{
    /// <summary>
    /// Reads saved logins from a Chrome profile, decrypting passwords on Windows.
    /// </summary>
    public static async Task<List<LoginCredential>> ReadPasswordsAsync(ChromeProfile profile, int maxEntries = 5000)
    {
        var logins = new List<LoginCredential>();

        var loginDataPath = Path.Combine(profile.Path, "Login Data");
        if (!File.Exists(loginDataPath))
            return logins;

        // Local State sits one level above the profile folder.
        var userDataRoot = Directory.GetParent(profile.Path)?.FullName;
        var localStatePath = userDataRoot != null
            ? Path.Combine(userDataRoot, "Local State")
            : string.Empty;

        var aesKey = ChromiumCrypt.TryGetAesKeyFromLocalState(localStatePath);

        // Chrome locks Login Data, so copy to temp.
        var tempPath = Path.Combine(Path.GetTempPath(), $"conjure_chrome_logins_{Guid.NewGuid()}.db");

        try
        {
            File.Copy(loginDataPath, tempPath, overwrite: true);

            await using var connection = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly");
            await connection.OpenAsync().ConfigureAwait(false);

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT origin_url, username_value, password_value, blacklisted_by_user
                FROM logins
                WHERE blacklisted_by_user = 0 AND origin_url LIKE 'http%'
                ORDER BY date_created DESC
                LIMIT @limit";
            command.Parameters.AddWithValue("@limit", maxEntries);

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var originUrl = reader.GetString(0);
                var username = reader.IsDBNull(1) ? "" : reader.GetString(1);

                if (reader.IsDBNull(2))
                    continue;

                var encrypted = (byte[])reader[2];
                var password = ChromiumCrypt.DecryptPassword(encrypted, aesKey);

                if (string.IsNullOrWhiteSpace(originUrl) || string.IsNullOrEmpty(password))
                    continue;

                logins.Add(new LoginCredential(originUrl, username, password));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error reading Chrome passwords: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // ignore cleanup failures
            }
        }

        return logins;
    }
}

