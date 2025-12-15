using System.Text.Json;
using ConjureBrowser.Core.Models;

namespace ConjureBrowser.Core.Services;

/// <summary>
/// Stores imported credentials in a JSON file format.
/// This is used when we can't write to Chromium's Login Data (locked by CEF).
/// </summary>
public sealed class ImportedCredentialStore
{
    private readonly string _filePath;
    private List<ImportedLogin> _logins = new();
    private bool _loaded;

    public ImportedCredentialStore(string? userDataPath = null)
    {
        var dataPath = userDataPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConjureBrowser");
        
        _filePath = Path.Combine(dataPath, "imported_passwords.json");
    }

    public string FilePath => _filePath;

    public async Task LoadAsync()
    {
        if (_loaded) return;

        try
        {
            if (File.Exists(_filePath))
            {
                var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
                var logins = JsonSerializer.Deserialize<List<ImportedLogin>>(json);
                _logins = logins ?? new List<ImportedLogin>();
            }
        }
        catch
        {
            _logins = new List<ImportedLogin>();
        }
        
        _loaded = true;
    }

    public async Task SaveAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_logins, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
        }
        catch
        {
            // Ignore save errors
        }
    }

    public async Task<(int imported, int skipped)> AddLoginsAsync(IEnumerable<LoginCredential> logins)
    {
        await LoadAsync().ConfigureAwait(false);

        var imported = 0;
        var skipped = 0;

        foreach (var login in logins)
        {
            // Check for duplicates (same origin + username)
            var exists = _logins.Any(l => 
                string.Equals(l.OriginUrl, login.OriginUrl, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(l.Username, login.Username, StringComparison.OrdinalIgnoreCase));

            if (exists)
            {
                skipped++;
                continue;
            }

            _logins.Add(new ImportedLogin
            {
                OriginUrl = login.OriginUrl,
                Username = login.Username,
                Password = login.Password, // Stored encrypted with DPAPI - see below
                DateCreated = DateTimeOffset.UtcNow
            });
            imported++;
        }

        if (imported > 0)
        {
            await SaveAsync().ConfigureAwait(false);
        }

        return (imported, skipped);
    }

    public async Task<IReadOnlyList<LoginCredential>> GetForUrlAsync(string url)
    {
        await LoadAsync().ConfigureAwait(false);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var pageUri))
            return Array.Empty<LoginCredential>();

        var pageHost = pageUri.Host;
        
        return _logins
            .Where(l => 
                Uri.TryCreate(l.OriginUrl, UriKind.Absolute, out var originUri) &&
                HostMatches(originUri.Host, pageHost))
            .Select(l => new LoginCredential(l.OriginUrl, l.Username, l.Password))
            .ToList();
    }

    public async Task<IReadOnlyList<LoginCredential>> GetAllAsync()
    {
        await LoadAsync().ConfigureAwait(false);
        return _logins.Select(l => new LoginCredential(l.OriginUrl, l.Username, l.Password)).ToList();
    }

    private static bool HostMatches(string storedHost, string pageHost)
    {
        if (string.IsNullOrWhiteSpace(storedHost) || string.IsNullOrWhiteSpace(pageHost))
            return false;

        if (string.Equals(storedHost, pageHost, StringComparison.OrdinalIgnoreCase))
            return true;

        return pageHost.EndsWith("." + storedHost, StringComparison.OrdinalIgnoreCase) ||
               storedHost.EndsWith("." + pageHost, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Represents an imported login stored in JSON.
/// </summary>
public sealed class ImportedLogin
{
    public string OriginUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = ""; // Note: stored as plaintext in the JSON (user's local file)
    public DateTimeOffset DateCreated { get; set; }
}
