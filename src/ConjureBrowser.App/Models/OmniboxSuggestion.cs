namespace ConjureBrowser.App.Models;

/// <summary>
/// Type of omnibox suggestion.
/// </summary>
public enum OmniboxSuggestionType
{
    Bookmark,
    History,
    Url,
    Search
}

/// <summary>
/// Represents a single suggestion in the omnibox dropdown.
/// </summary>
public class OmniboxSuggestion
{
    /// <summary>
    /// The type of suggestion (Bookmark, History, URL, or Search).
    /// </summary>
    public OmniboxSuggestionType Type { get; set; }

    /// <summary>
    /// The primary display text (e.g., page title or "Search Google for ...").
    /// </summary>
    public string PrimaryText { get; set; } = string.Empty;

    /// <summary>
    /// The secondary display text (e.g., URL).
    /// </summary>
    public string SecondaryText { get; set; } = string.Empty;

    /// <summary>
    /// The URL to navigate to when this suggestion is selected.
    /// For search suggestions, this is the full Google search URL.
    /// </summary>
    public string NavigateTarget { get; set; } = string.Empty;

    /// <summary>
    /// Whether this suggestion prefers to open in a new tab.
    /// </summary>
    public bool IsOpenInNewTabPreferred { get; set; }

    /// <summary>
    /// Icon text based on suggestion type.
    /// </summary>
    public string IconText => Type switch
    {
        OmniboxSuggestionType.Bookmark => "★",
        OmniboxSuggestionType.History => "⏱",
        OmniboxSuggestionType.Url => "🌐",
        OmniboxSuggestionType.Search => "🔍",
        _ => "•"
    };
}
