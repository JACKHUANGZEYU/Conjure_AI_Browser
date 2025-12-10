using System.Text.Json;

namespace ConjureBrowser.Core.Services;

/// <summary>
/// Persists user settings (e.g. API key, AI tool toggles, shortcuts) to disk.
/// </summary>
public sealed class SettingsStore
{
    public string FilePath { get; }

    private AppSettings _settings = new();

    public string GeminiApiKey
    {
        get => _settings.GeminiApiKey;
        set => _settings.GeminiApiKey = value;
    }

    // AI Tool toggles
    public bool SummarizePageEnabled
    {
        get => _settings.SummarizePageEnabled;
        set => _settings.SummarizePageEnabled = value;
    }

    public bool KeyPointsEnabled
    {
        get => _settings.KeyPointsEnabled;
        set => _settings.KeyPointsEnabled = value;
    }

    public bool ExplainSelectionEnabled
    {
        get => _settings.ExplainSelectionEnabled;
        set => _settings.ExplainSelectionEnabled = value;
    }

    public bool CompareTabsEnabled
    {
        get => _settings.CompareTabsEnabled;
        set => _settings.CompareTabsEnabled = value;
    }

    // AI Tool shortcuts (e.g., "Ctrl+Shift+S")
    public string SummarizePageShortcut
    {
        get => _settings.SummarizePageShortcut;
        set => _settings.SummarizePageShortcut = value;
    }

    public string KeyPointsShortcut
    {
        get => _settings.KeyPointsShortcut;
        set => _settings.KeyPointsShortcut = value;
    }

    public string ExplainSelectionShortcut
    {
        get => _settings.ExplainSelectionShortcut;
        set => _settings.ExplainSelectionShortcut = value;
    }

    public string CompareTabsShortcut
    {
        get => _settings.CompareTabsShortcut;
        set => _settings.CompareTabsShortcut = value;
    }

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConjureBrowser",
            "settings.json");
    }

    public async Task LoadAsync()
    {
        if (!File.Exists(FilePath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(FilePath).ConfigureAwait(false);
            _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            _settings = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(FilePath, json).ConfigureAwait(false);
    }

    private sealed class AppSettings
    {
        public string GeminiApiKey { get; set; } = string.Empty;
        
        // AI Tool toggles - all enabled by default
        public bool SummarizePageEnabled { get; set; } = true;
        public bool KeyPointsEnabled { get; set; } = true;
        public bool ExplainSelectionEnabled { get; set; } = true;
        public bool CompareTabsEnabled { get; set; } = true;
        
        // AI Tool shortcuts - empty by default (user sets custom ones)
        public string SummarizePageShortcut { get; set; } = string.Empty;
        public string KeyPointsShortcut { get; set; } = string.Empty;
        public string ExplainSelectionShortcut { get; set; } = string.Empty;
        public string CompareTabsShortcut { get; set; } = string.Empty;
    }
}

