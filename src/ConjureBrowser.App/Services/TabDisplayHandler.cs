using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using CefSharp;
using CefSharp.Handler;
using ConjureBrowser.App.Models;

namespace ConjureBrowser.App.Services;

public class TabDisplayHandler : DisplayHandler
{
    private readonly TabHeaderModel _headerModel;
    private readonly FaviconService _faviconService;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _currentFaviconCts;

    public TabDisplayHandler(TabHeaderModel headerModel, FaviconService faviconService, Dispatcher dispatcher)
    {
        _headerModel = headerModel;
        _faviconService = faviconService;
        _dispatcher = dispatcher;
    }

    protected override void OnFaviconUrlChange(IWebBrowser chromiumWebBrowser, IBrowser browser, IList<string> urls)
    {
        // Cancel any previous favicon fetch
        _currentFaviconCts?.Cancel();
        _currentFaviconCts = new CancellationTokenSource();
        var ct = _currentFaviconCts.Token;

        // Get current page URL for fallback
        var pageUrl = browser.MainFrame?.Url ?? string.Empty;

        // Filter and prioritize favicon URLs
        var candidateUrls = GetPrioritizedFaviconUrls(urls);

        // Fetch favicon asynchronously
        _ = FetchAndUpdateFaviconAsync(pageUrl, candidateUrls, ct);
    }

    private static IEnumerable<string> GetPrioritizedFaviconUrls(IList<string> urls)
    {
        if (urls == null || urls.Count == 0)
        {
            return Enumerable.Empty<string>();
        }

        // Prioritize .png and .ico files over others
        var prioritized = urls
            .Where(u => !string.IsNullOrEmpty(u))
            .OrderBy(u =>
            {
                if (u.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return 2; // SVG last (will be filtered out anyway)
                if (u.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return 0; // PNG first
                if (u.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return 0; // ICO first
                return 1; // Others in middle
            })
            .ToList();

        return prioritized;
    }

    private async System.Threading.Tasks.Task FetchAndUpdateFaviconAsync(string pageUrl, IEnumerable<string> candidateUrls, CancellationToken ct)
    {
        try
        {
            var favicon = await _faviconService.GetFaviconAsync(pageUrl, candidateUrls, ct);

            if (ct.IsCancellationRequested) return;

            _dispatcher.InvokeAsync(() =>
            {
                _headerModel.Favicon = favicon;
            });
        }
        catch (OperationCanceledException)
        {
            // Cancelled, ignore
        }
        catch
        {
            // Failed to fetch favicon, leave as null (placeholder will show)
        }
    }
}
