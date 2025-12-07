using System.Text.Json;
using ConjureBrowser.Core.Models;

namespace ConjureBrowser.Core.Services;

public sealed class BookmarkStore
{
    private readonly List<Bookmark> _items = new();

    public IReadOnlyList<Bookmark> Items => _items;

    public string FilePath { get; }

    public BookmarkStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ConjureBrowser",
            "bookmarks.json");
    }

    public async Task LoadAsync()
    {
        if (!File.Exists(FilePath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(FilePath).ConfigureAwait(false);
            var items = JsonSerializer.Deserialize<List<Bookmark>>(json) ?? new List<Bookmark>();

            _items.Clear();

            foreach (var bookmark in items)
            {
                if (string.IsNullOrWhiteSpace(bookmark.Url)) continue;
                _items.RemoveAll(x => string.Equals(x.Url, bookmark.Url, StringComparison.OrdinalIgnoreCase));
                _items.Add(bookmark);
            }
        }
        catch
        {
            // If the file is corrupted, ignore and keep an empty list for now.
        }
    }

    public bool IsBookmarked(string url) =>
        _items.Any(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));

    public async Task<bool> ToggleAsync(string url, string? title = null)
    {
        var existing = _items.FirstOrDefault(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            _items.Remove(existing);
            await SaveAsync().ConfigureAwait(false);
            return false;
        }

        _items.Add(new Bookmark(
            string.IsNullOrWhiteSpace(title) ? url : title.Trim(),
            url));

        await SaveAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RemoveAsync(string url)
    {
        var existing = _items.FirstOrDefault(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return false;

        _items.Remove(existing);
        await SaveAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateAsync(string originalUrl, string newTitle, string newUrl)
    {
        // Validate inputs
        if (string.IsNullOrWhiteSpace(newTitle) || string.IsNullOrWhiteSpace(newUrl))
            return false;

        var normalizedUrl = Utils.UrlHelpers.NormalizeUrl(newUrl);
        if (normalizedUrl == null)
            return false;

        // Find the bookmark to update
        var existing = _items.FirstOrDefault(b => string.Equals(b.Url, originalUrl, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
            return false;

        // Check if the new URL is already used by a different bookmark (prevent duplicates)
        if (!string.Equals(originalUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
        {
            var duplicate = _items.FirstOrDefault(b => string.Equals(b.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
                return false; // Reject duplicate
        }

        // Update the bookmark
        var index = _items.IndexOf(existing);
        _items[index] = new Bookmark(newTitle.Trim(), normalizedUrl);

        await SaveAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> MoveAsync(string url, int newIndex)
    {
        var existing = _items.FirstOrDefault(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
            return false;

        // Clamp newIndex to valid range
        newIndex = Math.Clamp(newIndex, 0, _items.Count - 1);

        _items.Remove(existing);
        _items.Insert(newIndex, existing);

        await SaveAsync().ConfigureAwait(false);
        return true;
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
