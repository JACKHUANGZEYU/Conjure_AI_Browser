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

    public async Task SaveAsync()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(FilePath, json).ConfigureAwait(false);
    }
}
