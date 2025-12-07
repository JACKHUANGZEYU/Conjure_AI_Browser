using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ConjureBrowser.Core.Models;

namespace ConjureBrowser.Core.Services;

public class SessionStore
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConjureBrowser");

    private static readonly string FilePath = Path.Combine(DataFolder, "session.json");
    private static readonly string CorruptFilePath = Path.Combine(DataFolder, "session.corrupt.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<SessionState?> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(FilePath);
            var state = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
            return state;
        }
        catch (JsonException)
        {
            // JSON parse failed - rename to corrupt file for debugging
            try
            {
                if (File.Exists(CorruptFilePath))
                {
                    File.Delete(CorruptFilePath);
                }
                File.Move(FilePath, CorruptFilePath);
            }
            catch
            {
                // Best effort - ignore if rename fails
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(SessionState state)
    {
        try
        {
            // Ensure directory exists
            if (!Directory.Exists(DataFolder))
            {
                Directory.CreateDirectory(DataFolder);
            }

            // Always save with UTC timestamp
            state.SavedAtUtc = DateTimeOffset.UtcNow;

            var json = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(FilePath, json);
        }
        catch
        {
            // Best effort save - ignore failures
        }
    }

    public Task ClearAsync()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch
        {
            // Ignore
        }
        return Task.CompletedTask;
    }
}
