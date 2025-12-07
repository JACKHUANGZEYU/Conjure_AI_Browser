using System.Text.Json;
using ConjureBrowser.Core.Models;

namespace ConjureBrowser.Core.Services;

public sealed class HistoryStore
{
    private readonly List<HistoryEntry> _items = new();
    private const int MaxEntries = 5000;

    public IReadOnlyList<HistoryEntry> Items => _items;

    public string FilePath { get; }

    public HistoryStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConjureBrowser",
            "history.json");
    }

    public async Task LoadAsync()
    {
        if (!File.Exists(FilePath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(FilePath).ConfigureAwait(false);
            var items = JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new List<HistoryEntry>();

            var normalized = items
                .Where(i => !string.IsNullOrWhiteSpace(i.Url))
                .Select(i => new HistoryEntry(
                    string.IsNullOrWhiteSpace(i.Title) ? i.Url : i.Title.Trim(),
                    i.Url,
                    i.VisitedAt.ToUniversalTime()))
                .OrderByDescending(i => i.VisitedAt)
                .Take(MaxEntries)
                .ToList();

            _items.Clear();
            _items.AddRange(normalized);
        }
        catch
        {
            // If the file is corrupted, ignore and keep an empty list
        }
    }

    public async Task RecordAsync(string title, string url, DateTimeOffset visitedAt)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var normalizedVisited = visitedAt.ToUniversalTime();
        var safeTitle = string.IsNullOrWhiteSpace(title) ? url : title.Trim();

        // Add new entry at the beginning (newest first)
        _items.Insert(0, new HistoryEntry(
            safeTitle,
            url,
            normalizedVisited));

        // Enforce 5,000 entry cap - remove oldest entries
        while (_items.Count > MaxEntries)
        {
            _items.RemoveAt(_items.Count - 1);
        }

        await SaveAsync().ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string url, DateTimeOffset visitedAt)
    {
        var visitedAtUtc = visitedAt.ToUniversalTime();

        var existing = _items.FirstOrDefault(h =>
            string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase) &&
            h.VisitedAt == visitedAtUtc);

        if (existing == null) return false;

        _items.Remove(existing);
        await SaveAsync().ConfigureAwait(false);
        return true;
    }

    public async Task ClearAsync()
    {
        _items.Clear();
        await SaveAsync().ConfigureAwait(false);
    }

    public async Task SaveAsync()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(FilePath, json).ConfigureAwait(false);
    }
}
