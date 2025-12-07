using System;
using System.Collections.Generic;
using System.Windows.Threading;
using CefSharp;
using CefSharp.Wpf;

namespace ConjureBrowser.App.Services;

public class FindInPageManager : IFindHandler
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<IWebBrowser, FindState> _states = new();
    private int _nextIdentifier = 1;

    public event Action<ChromiumWebBrowser, int, int, bool>? FindResultUpdated;

    public FindInPageManager(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Attach(ChromiumWebBrowser browser)
    {
        browser.FindHandler = this;
        lock (_states)
        {
            _states[browser] = new FindState();
        }
    }

    public void Detach(ChromiumWebBrowser browser)
    {
        lock (_states)
        {
            _states.Remove(browser);
        }
    }

    public void StartNewSearch(ChromiumWebBrowser browser, string text, bool matchCase)
    {
        if (string.IsNullOrEmpty(text))
        {
            Stop(browser, clearSelection: true);
            return;
        }

        FindState state;
        lock (_states)
        {
            if (!_states.TryGetValue(browser, out state!))
            {
                state = new FindState();
                _states[browser] = state;
            }
        }

        state.LastSearchText = text;
        state.LastMatchCase = matchCase;
        state.LastIdentifier = _nextIdentifier++;
        state.LastCount = 0;
        state.LastActiveOrdinal = 0;

        // Use the overload without identifier (5 params)
        browser.GetBrowserHost()?.Find(text, true, matchCase, false);
    }

    public void FindNext(ChromiumWebBrowser browser, bool forward)
    {
        FindState? state;
        lock (_states)
        {
            if (!_states.TryGetValue(browser, out state) || string.IsNullOrEmpty(state.LastSearchText))
                return;
        }

        browser.GetBrowserHost()?.Find(state.LastSearchText, forward, state.LastMatchCase, true);
    }

    public void Stop(ChromiumWebBrowser browser, bool clearSelection)
    {
        browser.StopFinding(clearSelection);

        lock (_states)
        {
            if (_states.TryGetValue(browser, out var state))
            {
                state.LastCount = 0;
                state.LastActiveOrdinal = 0;
            }
        }

        // Notify UI to reset count display
        _dispatcher.InvokeAsync(() => FindResultUpdated?.Invoke(browser, 0, 0, true));
    }

    // IFindHandler implementation
    public void OnFindResult(IWebBrowser chromiumWebBrowser, IBrowser browser, int identifier, int count, CefSharp.Structs.Rect selectionRect, int activeMatchOrdinal, bool finalUpdate)
    {
        _dispatcher.InvokeAsync(() =>
        {
            lock (_states)
            {
                if (_states.TryGetValue(chromiumWebBrowser, out var state))
                {
                    state.LastCount = count;
                    state.LastActiveOrdinal = activeMatchOrdinal;
                }
            }

            if (chromiumWebBrowser is ChromiumWebBrowser cwb)
            {
                FindResultUpdated?.Invoke(cwb, activeMatchOrdinal, count, finalUpdate);
            }
        });
    }

    private class FindState
    {
        public string LastSearchText { get; set; } = string.Empty;
        public bool LastMatchCase { get; set; }
        public int LastIdentifier { get; set; }
        public int LastCount { get; set; }
        public int LastActiveOrdinal { get; set; }
    }
}
