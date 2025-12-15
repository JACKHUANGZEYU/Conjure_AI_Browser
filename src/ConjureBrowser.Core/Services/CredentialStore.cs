using ConjureBrowser.Core.Import;
using ConjureBrowser.Core.Models;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace ConjureBrowser.Core.Services;

/// <summary>
/// Reads saved login credentials from both:
/// 1. Conjure's Chromium (CEF) Login Data database
/// 2. Imported credentials JSON file (for passwords imported from Chrome)
/// Used to drive custom autofill UI.
/// </summary>
public sealed class CredentialStore
{
    private readonly string _userDataPath;
    private readonly string _conjureDataPath;
    private readonly byte[]? _aesKey;
    private readonly ImportedCredentialStore _importedStore;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);
    private DateTimeOffset _lastLoadUtc = DateTimeOffset.MinValue;
    private List<LoginCredential> _cache = new();

    public CredentialStore(string? userDataPath = null)
    {
        _userDataPath = userDataPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConjureBrowser",
            "cef-cache");

        _conjureDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConjureBrowser");

        var localStatePath = Path.Combine(_userDataPath, "Local State");
        _aesKey = ChromiumCrypt.TryGetAesKeyFromLocalState(localStatePath);
        _importedStore = new ImportedCredentialStore(_conjureDataPath);
        
        Debug.WriteLine($"[CredentialStore] Initialized with CEF path: {_userDataPath}");
        Debug.WriteLine($"[CredentialStore] Imported store path: {_importedStore.FilePath}");
        Debug.WriteLine($"[CredentialStore] AES key available: {_aesKey != null}");
    }

    public async Task<IReadOnlyList<LoginCredential>> GetForUrlAsync(string url)
    {
        Debug.WriteLine($"[CredentialStore] GetForUrlAsync called for: {url}");

        if (_cache.Count == 0 || DateTimeOffset.UtcNow - _lastLoadUtc > _cacheTtl)
        {
            Debug.WriteLine("[CredentialStore] Cache empty or expired, loading...");
            await LoadAllAsync().ConfigureAwait(false);
        }

        Debug.WriteLine($"[CredentialStore] Cache has {_cache.Count} credentials");
        
        if (!Uri.TryCreate(url, UriKind.Absolute, out var pageUri))
        {
            Debug.WriteLine($"[CredentialStore] Invalid URL: {url}");
            return Array.Empty<LoginCredential>();
        }

        var pageHost = pageUri.Host;
        var matches = _cache
            .Where(c =>
                Uri.TryCreate(c.OriginUrl, UriKind.Absolute, out var originUri) &&
                HostMatches(originUri.Host, pageHost))
            .ToList();
            
        Debug.WriteLine($"[CredentialStore] Found {matches.Count} matching credentials for host: {pageHost}");
        return matches;
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

    private async Task LoadAllAsync()
    {
        var result = new List<LoginCredential>();
        
        // Load from imported credentials store first (always available)
        try
        {
            var imported = await _importedStore.GetAllAsync().ConfigureAwait(false);
            result.AddRange(imported);
            Debug.WriteLine($"[CredentialStore] Loaded {imported.Count} credentials from imported store");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CredentialStore] Error loading imported credentials: {ex.Message}");
        }

        // Also try to load from Chromium's Login Data (if available and we have an AES key)
        if (_aesKey != null)
        {
            var chromiumCreds = await LoadFromChromiumAsync().ConfigureAwait(false);
            result.AddRange(chromiumCreds);
            Debug.WriteLine($"[CredentialStore] Loaded {chromiumCreds.Count} credentials from Chromium database");
        }
        else
        {
            Debug.WriteLine("[CredentialStore] Skipping Chromium database (no AES key)");
        }

        _cache = result;
        _lastLoadUtc = DateTimeOffset.UtcNow;
        Debug.WriteLine($"[CredentialStore] Cache updated with {_cache.Count} total credentials");
    }

    private async Task<List<LoginCredential>> LoadFromChromiumAsync()
    {
        var loginDataPath = Path.Combine(_userDataPath, "Default", "Login Data");
        Debug.WriteLine($"[CredentialStore] Checking Chromium Login Data: {loginDataPath}");
        
        if (!File.Exists(loginDataPath))
        {
            Debug.WriteLine("[CredentialStore] Chromium Login Data file not found");
            return new List<LoginCredential>();
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"conjure_logins_{Guid.NewGuid()}.db");
        var result = new List<LoginCredential>();

        try
        {
            File.Copy(loginDataPath, tempPath, overwrite: true);
            Debug.WriteLine("[CredentialStore] Copied Login Data to temp file");

            await using var connection = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly");
            await connection.OpenAsync().ConfigureAwait(false);

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT origin_url, username_value, password_value, blacklisted_by_user
                FROM logins
                WHERE blacklisted_by_user = 0 AND origin_url LIKE 'http%'";

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var originUrl = reader.GetString(0);
                var username = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (reader.IsDBNull(2)) continue;

                var encrypted = (byte[])reader[2];
                var password = ChromiumCrypt.DecryptPassword(encrypted, _aesKey);

                if (string.IsNullOrWhiteSpace(originUrl) || string.IsNullOrEmpty(password))
                {
                    Debug.WriteLine($"[CredentialStore] Skipping credential - origin empty or decryption failed: {originUrl}");
                    continue;
                }

                result.Add(new LoginCredential(originUrl, username, password));
            }
            
            Debug.WriteLine($"[CredentialStore] Loaded {result.Count} credentials from Chromium database");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CredentialStore] Error loading Chromium credentials: {ex.Message}");
            // ignore read errors; return what we have
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
                // ignore cleanup errors
            }
        }

        return result;
    }
}
