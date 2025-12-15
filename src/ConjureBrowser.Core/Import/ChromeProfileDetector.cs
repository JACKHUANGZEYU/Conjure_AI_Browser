using System.Text.Json;
using ConjureBrowser.Core.Models;

namespace ConjureBrowser.Core.Import;

/// <summary>
/// Detects Chrome installations and profiles on the system.
/// </summary>
public static class ChromeProfileDetector
{
    /// <summary>
    /// Standard Chrome User Data path on Windows.
    /// </summary>
    public static string ChromeUserDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Google", "Chrome", "User Data");

    /// <summary>
    /// Checks if Chrome is installed (User Data folder exists).
    /// </summary>
    public static bool IsChromeInstalled() => Directory.Exists(ChromeUserDataPath);

    /// <summary>
    /// Detects all Chrome profiles available for import.
    /// </summary>
    /// <returns>List of detected Chrome profiles.</returns>
    public static List<ChromeProfile> DetectProfiles()
    {
        var profiles = new List<ChromeProfile>();

        if (!IsChromeInstalled())
            return profiles;

        var userDataPath = ChromeUserDataPath;

        // Check the Default profile
        var defaultPath = Path.Combine(userDataPath, "Default");
        if (Directory.Exists(defaultPath))
        {
            var profile = CreateProfileInfo("Default", defaultPath);
            if (profile != null)
                profiles.Add(profile);
        }

        // Check numbered profiles (Profile 1, Profile 2, etc.)
        try
        {
            var directories = Directory.GetDirectories(userDataPath, "Profile *");
            foreach (var dir in directories.OrderBy(d => d))
            {
                var name = Path.GetFileName(dir);
                var profile = CreateProfileInfo(name, dir);
                if (profile != null)
                    profiles.Add(profile);
            }
        }
        catch
        {
            // Ignore errors when scanning for profiles
        }

        return profiles;
    }

    /// <summary>
    /// Creates a ChromeProfile instance for a given profile directory.
    /// </summary>
    private static ChromeProfile? CreateProfileInfo(string name, string path)
    {
        var bookmarksPath = Path.Combine(path, "Bookmarks");
        var historyPath = Path.Combine(path, "History");
        var loginDataPath = Path.Combine(path, "Login Data");

        var hasBookmarks = File.Exists(bookmarksPath);
        var hasHistory = File.Exists(historyPath);
        var hasPasswords = File.Exists(loginDataPath);

        // Only include profiles that have something to import
        if (!hasBookmarks && !hasHistory && !hasPasswords)
            return null;

        string? customName = null;
        try
        {
            var prefsPath = Path.Combine(path, "Preferences");
            if (File.Exists(prefsPath))
            {
                var prefsJson = File.ReadAllText(prefsPath);
                using var doc = JsonDocument.Parse(prefsJson);
                
                if (doc.RootElement.TryGetProperty("profile", out var profileElement) &&
                    profileElement.TryGetProperty("name", out var nameElement))
                {
                    customName = nameElement.GetString();
                }
            }
        }
        catch
        {
            // Ignore errors reading preferences
        }

        return new ChromeProfile
        {
            Name = name,
            Path = path,
            HasBookmarks = hasBookmarks,
            HasHistory = hasHistory,
            HasPasswords = hasPasswords,
            CustomName = customName
        };
    }
}
