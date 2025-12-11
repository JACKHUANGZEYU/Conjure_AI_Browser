namespace ConjureBrowser.Core.Models;

/// <summary>
/// Represents a detected Chrome profile that can be imported from.
/// </summary>
public sealed class ChromeProfile
{
    /// <summary>
    /// Display name of the profile (e.g., "Default", "Profile 1", "Work").
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Full path to the profile directory.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Whether a bookmarks file exists in this profile.
    /// </summary>
    public bool HasBookmarks { get; init; }

    /// <summary>
    /// Whether a history database exists in this profile.
    /// </summary>
    public bool HasHistory { get; init; }

    /// <summary>
    /// Custom display name from Chrome preferences (if available).
    /// </summary>
    public string? CustomName { get; init; }

    /// <summary>
    /// Returns the display name, preferring custom name if available.
    /// </summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(CustomName) ? CustomName : Name;

    public override string ToString() => DisplayName;
}
