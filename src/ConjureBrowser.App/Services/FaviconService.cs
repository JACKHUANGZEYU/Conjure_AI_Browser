using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ConjureBrowser.App.Services;

public class FaviconService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    // Cache favicons by host
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new();

    public async Task<ImageSource?> GetFaviconAsync(string pageUrl, IEnumerable<string> candidateIconUrls, CancellationToken ct = default)
    {
        // Get host for caching
        string? host = null;
        try
        {
            var uri = new Uri(pageUrl);
            host = uri.Host;

            // Check cache first
            if (_cache.TryGetValue(host, out var cached))
            {
                return cached;
            }
        }
        catch
        {
            // Invalid URL, continue without caching
        }

        // Try candidate URLs in order
        foreach (var iconUrl in candidateIconUrls)
        {
            if (string.IsNullOrEmpty(iconUrl)) continue;

            // Skip SVG files (WPF doesn't decode SVG natively)
            if (iconUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;

            var result = await TryDownloadFaviconAsync(iconUrl, ct);
            if (result != null)
            {
                if (host != null)
                {
                    _cache[host] = result;
                }
                return result;
            }
        }

        // Fallback to /favicon.ico at origin
        try
        {
            var uri = new Uri(pageUrl);
            var fallbackUrl = $"{uri.Scheme}://{uri.Host}/favicon.ico";
            var result = await TryDownloadFaviconAsync(fallbackUrl, ct);
            if (result != null)
            {
                if (host != null)
                {
                    _cache[host] = result;
                }
                return result;
            }
        }
        catch
        {
            // Invalid URL
        }

        // Cache null result to avoid repeated requests
        if (host != null)
        {
            _cache[host] = null;
        }

        return null;
    }

    private static async Task<ImageSource?> TryDownloadFaviconAsync(string url, CancellationToken ct)
    {
        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(url, ct);
            if (bytes.Length == 0) return null;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.DecodePixelWidth = 16;
            bitmap.DecodePixelHeight = 16;
            bitmap.EndInit();
            bitmap.Freeze(); // Make cross-thread accessible

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public void ClearCache()
    {
        _cache.Clear();
    }
}
