using System.Text.Json;

namespace ConjureBrowser.Core.Services;

/// <summary>
/// Persists user settings (e.g. API key) to disk.
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
    }
}
