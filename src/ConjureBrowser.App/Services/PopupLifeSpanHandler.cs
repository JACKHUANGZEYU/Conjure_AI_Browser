using System;
using System.Diagnostics;
using System.Windows.Threading;
using CefSharp;

namespace ConjureBrowser.App.Services;

/// <summary>
/// Handles popup windows (target="_blank", window.open) by opening them in new tabs
/// instead of allowing external windows. Also blocks popups without user gestures.
/// </summary>
public class PopupLifeSpanHandler : ILifeSpanHandler
{
    private readonly Dispatcher _uiDispatcher;
    private readonly Action<string, bool> _openUrlInNewTab;
    private readonly Action<string>? _log;

    /// <summary>
    /// Creates a new PopupLifeSpanHandler.
    /// </summary>
    /// <param name="uiDispatcher">The UI dispatcher for thread marshaling.</param>
    /// <param name="openUrlInNewTab">
    /// Callback to open a URL in a new tab. 
    /// Parameters: (targetUrl, activate) where activate=true means focus the new tab.
    /// </param>
    /// <param name="log">Optional logging callback for debug messages.</param>
    public PopupLifeSpanHandler(
        Dispatcher uiDispatcher,
        Action<string, bool> openUrlInNewTab,
        Action<string>? log = null)
    {
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _openUrlInNewTab = openUrlInNewTab ?? throw new ArgumentNullException(nameof(openUrlInNewTab));
        _log = log;
    }

    /// <summary>
    /// Called before a popup is created. We intercept and handle it ourselves.
    /// </summary>
    public bool OnBeforePopup(
        IWebBrowser chromiumWebBrowser,
        IBrowser browser,
        IFrame frame,
        string targetUrl,
        string targetFrameName,
        WindowOpenDisposition targetDisposition,
        bool userGesture,
        IPopupFeatures popupFeatures,
        IWindowInfo windowInfo,
        IBrowserSettings browserSettings,
        ref bool noJavascriptAccess,
        out IWebBrowser? newBrowser)
    {
        // We always handle popups ourselves - never let CEF create a new browser window
        newBrowser = null;

        // Block null/empty URLs
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            _log?.Invoke($"Popup blocked: empty URL");
            return true; // Cancel
        }

        // Check URL scheme
        Uri? uri;
        try
        {
            uri = new Uri(targetUrl);
        }
        catch
        {
            _log?.Invoke($"Popup blocked: invalid URL '{targetUrl}'");
            return true; // Cancel
        }

        var scheme = uri.Scheme.ToLowerInvariant();

        // Handle external schemes (mailto:, tel:, etc.)
        if (scheme != "http" && scheme != "https" && scheme != "file")
        {
            if (scheme == "mailto" || scheme == "tel" || scheme == "callto" || scheme == "sms")
            {
                _log?.Invoke($"Opening external scheme: {targetUrl}");
                try
                {
                    Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Failed to open external scheme: {ex.Message}");
                }
            }
            else
            {
                _log?.Invoke($"Popup blocked: unsupported scheme '{scheme}'");
            }
            return true; // Cancel
        }

        // Block popups without user gesture (silent popups / pop-unders)
        if (!userGesture)
        {
            _log?.Invoke($"Popup blocked (no user gesture): {targetUrl}");
            return true; // Cancel
        }

        // Determine if the new tab should be activated (focused)
        // Background tabs don't steal focus
        var activate = targetDisposition != WindowOpenDisposition.NewBackgroundTab;

        _log?.Invoke($"Opening popup in new tab (activate={activate}): {targetUrl}");

        // Open in a new tab via the UI thread
        _uiDispatcher.BeginInvoke(() =>
        {
            try
            {
                _openUrlInNewTab(targetUrl, activate);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Failed to open new tab: {ex.Message}");
            }
        });

        return true; // Cancel CEF's default popup creation - we handled it
    }

    /// <summary>
    /// Called after a browser has been created. No-op for our popup handling.
    /// </summary>
    public void OnAfterCreated(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
        // No action needed
    }

    /// <summary>
    /// Called when a browser is about to close.
    /// Return false to allow normal close behavior.
    /// </summary>
    public bool DoClose(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
        // Return false to allow the browser to close normally
        return false;
    }

    /// <summary>
    /// Called just before a browser is destroyed. No-op for cleanup.
    /// </summary>
    public void OnBeforeClose(IWebBrowser chromiumWebBrowser, IBrowser browser)
    {
        // No action needed - the tab close logic in MainWindow handles cleanup
    }
}
