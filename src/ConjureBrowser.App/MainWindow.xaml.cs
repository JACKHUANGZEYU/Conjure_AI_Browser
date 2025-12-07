using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CefSharp;
using CefSharp.Wpf;
using ConjureBrowser.AI.Impl;
using ConjureBrowser.App.Models;
using ConjureBrowser.App.Services;
using ConjureBrowser.Core.Models;
using ConjureBrowser.Core.Services;
using ConjureBrowser.Core.Utils;

namespace ConjureBrowser.App;

public partial class MainWindow : Window
{
    private readonly BookmarkStore _bookmarks = new();
    private readonly HistoryStore _history = new();
    private readonly HttpClient _httpClient = new();
    private readonly GeminiAiAssistant _ai;

    private readonly List<BrowserTab> _tabs = new();
    private BrowserTab? _activeTab;
    private BrowserTab? _lastActiveBrowserTab;

    private TabItem? _settingsTab;
    private PasswordBox? _settingsApiKeyBox;
    private TextBlock? _settingsStatusText;
    private string _globalApiKey = string.Empty;

    private TabItem? _historyTab;
    private TextBox? _historySearchBox;
    private ListView? _historyListView;
    private List<HistoryEntry> _filteredHistory = new();
    private bool _historySearchHasPlaceholder = true;

    private readonly DownloadManager _downloadManager = new();
    private TabItem? _downloadsTab;
    private ListView? _downloadsListView;

    // Recently closed tabs for Ctrl+Shift+T
    private readonly List<ClosedTabInfo> _recentlyClosedTabs = new();
    private const int MaxRecentlyClosedTabs = 20;

    // Find in page
    private readonly FindInPageManager _findManager;
    private DispatcherTimer? _findDebounceTimer;

    // Tab header model tracking (for favicon + title updates)
    private readonly FaviconService _faviconService = new();
    private readonly Dictionary<ChromiumWebBrowser, TabHeaderModel> _headersByBrowser = new();

    // Session restore
    private readonly SessionStore _sessionStore = new();
    private readonly DispatcherTimer _sessionSaveDebounce;
    private const int MaxRestoredTabs = 20;

    // Omnibox suggestions
    private readonly ObservableCollection<OmniboxSuggestion> _omniboxItems = new();
    private DispatcherTimer? _omniboxDebounceTimer;
    private bool _suppressOmniboxTextChanged;

    private const string HomeUrl = "https://www.google.com";

    public MainWindow()
    {
        _ai = new GeminiAiAssistant(_httpClient, string.Empty, "gemini-2.5-flash");
        _findManager = new FindInPageManager(Dispatcher);
        _findManager.FindResultUpdated += OnFindResultUpdated;

        // Session save debounce timer
        _sessionSaveDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _sessionSaveDebounce.Tick += (_, _) =>
        {
            _sessionSaveDebounce.Stop();
            SaveSessionNow();
        };

        // Omnibox debounce timer
        _omniboxDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _omniboxDebounceTimer.Tick += (_, _) =>
        {
            _omniboxDebounceTimer.Stop();
            BuildOmniboxSuggestions();
        };

        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        await _bookmarks.LoadAsync();
        await _history.LoadAsync();
        RenderBookmarksBar();

        // Initialize omnibox suggestions list
        OmniboxList.ItemsSource = _omniboxItems;

        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            ApplyGlobalApiKey(envKey, updateExistingTabs: false, updateSettingsUi: false);
        }

        Tabs.Items.Clear();

        // Try to restore session
        var session = await _sessionStore.LoadAsync();
        var restored = false;

        if (session?.Tabs?.Count > 0)
        {
            var tabsToRestore = session.Tabs.Take(MaxRestoredTabs).ToList();

            for (int i = 0; i < tabsToRestore.Count; i++)
            {
                var sessionTab = tabsToRestore[i];
                if (!string.IsNullOrEmpty(sessionTab.Url))
                {
                    AddNewTab(sessionTab.Url, activate: false, initialTitle: sessionTab.Title);
                }
            }

            if (Tabs.Items.Count > 0)
            {
                restored = true;
                // Select the previously selected tab (clamped to valid range)
                var selectIndex = Math.Clamp(session.SelectedWebTabIndex, 0, Tabs.Items.Count - 1);
                Tabs.SelectedIndex = selectIndex;
            }
        }

        // If no session restored, create default tab
        if (!restored)
        {
            AddNewTab(HomeUrl);
        }

        UpdateAiPanelVisibility();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            // Stop debounce timer
            _sessionSaveDebounce.Stop();

            // Final save on exit - use synchronous write to avoid deadlock
            var state = CaptureSessionState();
            var json = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ConjureBrowser");

            if (!Directory.Exists(dataFolder))
            {
                Directory.CreateDirectory(dataFolder);
            }

            var filePath = Path.Combine(dataFolder, "session.json");
            File.WriteAllText(filePath, json);
        }
        catch
        {
            // Don't prevent shutdown
        }
    }

    // ---------- Tabs and navigation ----------

    private void AttachBrowserEvents(BrowserTab tab)
    {
        tab.Browser.TitleChanged += Browser_TitleChanged;
        tab.Browser.AddressChanged += Browser_AddressChanged;
        tab.Browser.LoadingStateChanged += Browser_LoadingStateChanged;
        tab.Browser.FrameLoadEnd += Browser_FrameLoadEnd;
    }

    private BrowserTab? FindTabByBrowser(object? browser)
    {
        return _tabs.FirstOrDefault(t => ReferenceEquals(t.Browser, browser));
    }

    private string BuildTabTitle(BrowserTab tab)
    {
        var browser = tab.Browser;
        if (!string.IsNullOrWhiteSpace(browser.Title))
            return browser.Title!;

        if (!string.IsNullOrWhiteSpace(browser.Address))
        {
            try
            {
                var uri = new Uri(browser.Address);
                return string.IsNullOrWhiteSpace(uri.Host) ? browser.Address : uri.Host;
            }
            catch
            {
                return browser.Address;
            }
        }

        return "New Tab";
    }

    private void UpdateTabHeader(BrowserTab tab)
    {
        tab.Title = BuildTabTitle(tab);
        tab.TabItem.Header = tab.Title;
    }

    private void Browser_TitleChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        var tab = FindTabByBrowser(sender);
        if (tab is null) return;

        Dispatcher.Invoke(() =>
        {
            UpdateTabHeader(tab);

            if (_activeTab == tab)
            {
                Title = string.IsNullOrWhiteSpace(tab.Browser.Title)
                    ? "Conjure Browser"
                    : $"{tab.Browser.Title} - Conjure Browser";
            }
        });
    }

    private void Browser_AddressChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        var tab = FindTabByBrowser(sender);
        if (tab is null) return;

        Dispatcher.Invoke(async () =>
        {
            UpdateTabHeader(tab);

            if (_activeTab == tab)
            {
                AddressBar.Text = tab.Browser.Address ?? string.Empty;
                await UpdateBookmarkUiAsync();
            }
        });
    }

    private void Browser_LoadingStateChanged(object? sender, LoadingStateChangedEventArgs e)
    {
        var tab = FindTabByBrowser(sender);
        if (tab is null || tab != _activeTab) return;

        Dispatcher.Invoke(() =>
        {
            BackButton.IsEnabled = e.CanGoBack;
            ForwardButton.IsEnabled = e.CanGoForward;
            ReloadButton.IsEnabled = true;
        });
    }

    private async void Browser_FrameLoadEnd(object? sender, CefSharp.FrameLoadEndEventArgs e)
    {
        try
        {
            // Only track main frame navigations
            if (!e.Frame.IsMain) return;

            // FrameLoadEnd fires on a background thread - we need to access browser properties on UI thread
            string? url = null;
            string? title = null;

            await Dispatcher.InvokeAsync(() =>
            {
                var tab = FindTabByBrowser(sender);
                if (tab is null) return;

                url = tab.Browser.Address;
                title = tab.Browser.Title;
            });

            // Filter out special URLs that shouldn't be recorded
            if (string.IsNullOrWhiteSpace(url) ||
                url == "about:blank" ||
                url.StartsWith("chrome-devtools://", StringComparison.OrdinalIgnoreCase))
                return;

            // Record to history
            await _history.RecordAsync(
                string.IsNullOrWhiteSpace(title) ? url : title,
                url,
                DateTimeOffset.UtcNow).ConfigureAwait(false);

            // If history tab is open, refresh it on the UI thread
            if (_historyTab != null && _historyListView != null)
            {
                await Dispatcher.InvokeAsync(() => RefreshHistoryList());
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"History record failed: {ex}");
        }
    }

    private void Navigate(string rawInput)
    {
        if (_activeTab is null) return;

        var normalized = UrlHelpers.NormalizeUrl(rawInput);
        if (normalized == null)
        {
            if (string.IsNullOrWhiteSpace(rawInput)) return;
            var q = Uri.EscapeDataString(rawInput.Trim());
            normalized = $"https://www.google.com/search?q={q}";
        }

        _activeTab.Browser.Address = normalized;
    }

    private async Task UpdateBookmarkUiAsync()
    {
        var url = _activeTab?.Browser.Address;
        if (string.IsNullOrWhiteSpace(url)) return;

        var isBookmarked = _bookmarks.IsBookmarked(url);
        await Dispatcher.InvokeAsync(() =>
        {
            BookmarkButton.IsChecked = isBookmarked;
            BookmarkButton.Content = isBookmarked ? "★" : "☆";
            BookmarkButton.ToolTip = isBookmarked ? "Remove bookmark" : "Bookmark this page";
        });
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        var browser = _activeTab?.Browser;
        if (browser?.CanGoBack == true) browser.Back();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        var browser = _activeTab?.Browser;
        if (browser?.CanGoForward == true) browser.Forward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _activeTab?.Browser.Reload();
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        Navigate(HomeUrl);
    }

    private void Go_Click(object sender, RoutedEventArgs e)
    {
        Navigate(AddressBar.Text);
    }

    private void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Navigate(AddressBar.Text);
            e.Handled = true;
        }
    }

    private void AddressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Single-click (when unfocused) selects all; double-click sets caret at the clicked spot without selecting all.
        if (e.ClickCount >= 2)
        {
            if (!AddressBar.IsKeyboardFocusWithin)
            {
                AddressBar.Focus();
            }

            var clickPoint = e.GetPosition(AddressBar);
            var caretIndex = AddressBar.GetCharacterIndexFromPoint(clickPoint, snapToText: true);
            AddressBar.CaretIndex = caretIndex >= 0 ? caretIndex : AddressBar.Text.Length;
            AddressBar.SelectionLength = 0;
            e.Handled = true;
            return;
        }

        if (!AddressBar.IsKeyboardFocusWithin)
        {
            AddressBar.Focus();
            AddressBar.SelectAll();
            e.Handled = true;
        }
    }

    // ---------- Omnibox Suggestions ----------

    private void AddressBar_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressOmniboxTextChanged) return;
        if (!AddressBar.IsKeyboardFocused)
        {
            CloseOmnibox();
            return;
        }

        var text = AddressBar.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            CloseOmnibox();
            return;
        }

        // Restart debounce timer
        _omniboxDebounceTimer?.Stop();
        _omniboxDebounceTimer?.Start();
    }

    private void AddressBar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!OmniboxPopup.IsOpen) return;

        switch (e.Key)
        {
            case Key.Down:
                if (OmniboxList.SelectedIndex < _omniboxItems.Count - 1)
                    OmniboxList.SelectedIndex++;
                else if (OmniboxList.SelectedIndex == -1 && _omniboxItems.Count > 0)
                    OmniboxList.SelectedIndex = 0;
                OmniboxList.ScrollIntoView(OmniboxList.SelectedItem);
                e.Handled = true;
                break;

            case Key.Up:
                if (OmniboxList.SelectedIndex > 0)
                    OmniboxList.SelectedIndex--;
                OmniboxList.ScrollIntoView(OmniboxList.SelectedItem);
                e.Handled = true;
                break;

            case Key.Enter:
                if (OmniboxList.SelectedItem is OmniboxSuggestion suggestion)
                {
                    NavigateToOmniboxSuggestion(suggestion, openInNewTab: false);
                }
                else
                {
                    Navigate(AddressBar.Text);
                }
                CloseOmnibox();
                e.Handled = true;
                break;

            case Key.Escape:
                CloseOmnibox();
                e.Handled = true;
                break;
        }
    }

    private void AddressBar_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Delay closing so click on suggestion can register
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!OmniboxList.IsMouseOver && !AddressBar.IsKeyboardFocused)
            {
                CloseOmnibox();
            }
        }), DispatcherPriority.Input);
    }

    private void OmniboxList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (OmniboxList.SelectedItem is OmniboxSuggestion suggestion)
        {
            NavigateToOmniboxSuggestion(suggestion, openInNewTab: false);
            CloseOmnibox();
        }
    }

    private void OmniboxList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Check for middle-click or Ctrl+left-click to open in new tab
        var ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var isMiddleClick = e.MiddleButton == MouseButtonState.Pressed;

        if (isMiddleClick || (ctrlPressed && e.LeftButton == MouseButtonState.Pressed))
        {
            // Find the clicked item
            var element = e.OriginalSource as FrameworkElement;
            while (element != null && !(element is ListBoxItem))
            {
                element = VisualTreeHelper.GetParent(element) as FrameworkElement;
            }

            if (element is ListBoxItem listBoxItem && listBoxItem.Content is OmniboxSuggestion suggestion)
            {
                NavigateToOmniboxSuggestion(suggestion, openInNewTab: true);
                CloseOmnibox();
                e.Handled = true;
            }
        }
    }

    private void BuildOmniboxSuggestions()
    {
        var query = AddressBar.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            CloseOmnibox();
            return;
        }

        var suggestions = new List<OmniboxSuggestion>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Bookmark matches (higher priority)
        var bookmarkMatches = _bookmarks.Items
            .Where(b => b.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        b.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(3);

        foreach (var b in bookmarkMatches)
        {
            if (seenUrls.Add(b.Url))
            {
                suggestions.Add(new OmniboxSuggestion
                {
                    Type = OmniboxSuggestionType.Bookmark,
                    PrimaryText = b.Title,
                    SecondaryText = b.Url,
                    NavigateTarget = b.Url
                });
            }
        }

        // 2) History matches
        var historyMatches = _history.Items
            .Where(h => h.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        h.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(5);

        foreach (var h in historyMatches)
        {
            if (seenUrls.Add(h.Url))
            {
                suggestions.Add(new OmniboxSuggestion
                {
                    Type = OmniboxSuggestionType.History,
                    PrimaryText = h.Title,
                    SecondaryText = h.Url,
                    NavigateTarget = h.Url
                });
            }
        }

        // 3) URL suggestion if query looks like a URL/domain
        var normalizedUrl = UrlHelpers.NormalizeUrl(query);
        var looksLikeUrl = normalizedUrl != null ||
                          (query.Contains('.') && !query.Contains(' '));

        if (looksLikeUrl)
        {
            var targetUrl = normalizedUrl ?? $"https://{query}";
            if (seenUrls.Add(targetUrl))
            {
                suggestions.Add(new OmniboxSuggestion
                {
                    Type = OmniboxSuggestionType.Url,
                    PrimaryText = $"Go to {query}",
                    SecondaryText = targetUrl,
                    NavigateTarget = targetUrl
                });
            }
        }

        // 4) Search suggestion (always)
        var searchUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
        suggestions.Add(new OmniboxSuggestion
        {
            Type = OmniboxSuggestionType.Search,
            PrimaryText = $"Search Google for \"{query}\"",
            SecondaryText = "google.com",
            NavigateTarget = searchUrl
        });

        // Limit total suggestions
        var finalSuggestions = suggestions.Take(8).ToList();

        // Update observable collection
        _omniboxItems.Clear();
        foreach (var s in finalSuggestions)
        {
            _omniboxItems.Add(s);
        }

        // Show/hide popup
        if (_omniboxItems.Count > 0)
        {
            OmniboxPopup.IsOpen = true;
        }
        else
        {
            OmniboxPopup.IsOpen = false;
        }
    }

    private void NavigateToOmniboxSuggestion(OmniboxSuggestion suggestion, bool openInNewTab)
    {
        if (openInNewTab)
        {
            AddNewTab(suggestion.NavigateTarget, activate: true);
        }
        else
        {
            _suppressOmniboxTextChanged = true;
            AddressBar.Text = suggestion.NavigateTarget;
            _suppressOmniboxTextChanged = false;
            Navigate(suggestion.NavigateTarget);
        }
    }

    private void CloseOmnibox()
    {
        OmniboxPopup.IsOpen = false;
        OmniboxList.SelectedIndex = -1;
        _omniboxItems.Clear();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is not TabItem tabItem) return;

        if (tabItem.Tag is BrowserTab tab)
        {
            _activeTab = tab;
            _lastActiveBrowserTab = tab;
            AiPanelToggle.IsEnabled = true;
            AddressBar.Text = tab.Browser.Address ?? string.Empty;
            UpdateBookmarkUiAsync().ConfigureAwait(false);
            SyncUiToActiveTab();
        }
        else
        {
            _activeTab = null;
            AiPanelToggle.IsChecked = false;
            AiPanelToggle.IsEnabled = false;
            AiColumn.Width = new GridLength(0);
            BookmarkButton.IsChecked = false;
            BookmarkButton.IsEnabled = false;
            BookmarksMenuButton.IsEnabled = false;
            BackButton.IsEnabled = false;
            ForwardButton.IsEnabled = false;
            ReloadButton.IsEnabled = false;
            AddressBar.Text = tabItem == _settingsTab ? "settings" : string.Empty;
        }

        UpdateWindowTitle();
        RequestSessionSave();
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddNewTab(HomeUrl);

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TabItem tabItem) return;
        CloseTabItem(tabItem);
    }

    private void CloseCurrentTab()
    {
        if (Tabs.SelectedItem is TabItem tabItem)
        {
            CloseTabItem(tabItem);
        }
    }

    private void CloseTabItem(TabItem tabItem)
    {
        if (tabItem.Tag is BrowserTab tab)
        {
            // Save to recently closed list if it's a web tab with valid URL
            var url = tab.Browser.Address;
            if (!string.IsNullOrWhiteSpace(url) && url != "about:blank")
            {
                _recentlyClosedTabs.Add(new ClosedTabInfo { Url = url, Title = tab.Title });
                while (_recentlyClosedTabs.Count > MaxRecentlyClosedTabs)
                {
                    _recentlyClosedTabs.RemoveAt(0);
                }
            }

            // Clean up header model tracking
            _headersByBrowser.Remove(tab.Browser);

            _tabs.Remove(tab);

            if (ReferenceEquals(_lastActiveBrowserTab, tab))
            {
                _lastActiveBrowserTab = _tabs.LastOrDefault();
            }
        }

        if (ReferenceEquals(tabItem, _settingsTab))
        {
            _settingsTab = null;
            _settingsApiKeyBox = null;
            _settingsStatusText = null;
        }

        if (ReferenceEquals(tabItem, _historyTab))
        {
            _historyTab = null;
            _historySearchBox = null;
            _historyListView = null;
            _filteredHistory.Clear();
            _historySearchHasPlaceholder = true;
        }

        if (ReferenceEquals(tabItem, _downloadsTab))
        {
            _downloadsTab = null;
            _downloadsListView = null;
        }

        Tabs.Items.Remove(tabItem);

        if (Tabs.Items.Count == 0)
        {
            // Last tab closed - close the window (Chrome-like behavior)
            Close();
        }
        else if (ReferenceEquals(Tabs.SelectedItem, tabItem))
        {
            Tabs.SelectedIndex = Math.Max(0, Tabs.Items.Count - 1);
        }

        // Save session after tab closed
        RequestSessionSave();
    }

    private void AddNewTab(string? initialUrl = null, bool activate = true, string? initialTitle = null)
    {
        var browser = new ChromiumWebBrowser();

        var title = initialTitle ?? (string.IsNullOrWhiteSpace(initialUrl) ? "New Tab" : initialUrl!);

        // Create tab header model for favicon/title/loading display
        var headerModel = new TabHeaderModel
        {
            Title = title,
            Url = initialUrl ?? HomeUrl
        };

        var tabItem = new TabItem
        {
            Header = headerModel,
            Content = browser
        };

        var tab = new BrowserTab
        {
            Browser = browser,
            TabItem = tabItem,
            Title = title,
            Model = GetSelectedModel(),
            ApiKey = _globalApiKey,
            AiVisible = false
        };

        tabItem.Tag = tab;
        tabItem.DataContext = tab;
        AttachBrowserEvents(tab);

        // Set download handler
        browser.DownloadHandler = _downloadManager;

        // Set popup handler to open target="_blank" / window.open in new tabs
        browser.LifeSpanHandler = new PopupLifeSpanHandler(
            Dispatcher,
            (url, activate) => AddNewTab(url, activate)
        );

        // Set find handler
        _findManager.Attach(browser);

        // Set display handler for favicon capture
        browser.DisplayHandler = new TabDisplayHandler(headerModel, _faviconService, Dispatcher);
        _headersByBrowser[browser] = headerModel;

        // Register events for title/loading/address updates
        browser.TitleChanged += (s, e) => OnBrowserTitleChanged(browser, e.NewValue as string ?? browser.Title);
        browser.LoadingStateChanged += (s, e) => OnBrowserLoadingStateChanged(browser, e.IsLoading);
        browser.AddressChanged += (s, e) => OnBrowserAddressChanged(browser, e.NewValue as string ?? browser.Address);

        // Set address after events are attached so FrameLoadEnd can capture it.
        browser.Address = initialUrl ?? HomeUrl;

        Tabs.Items.Add(tabItem);
        _tabs.Add(tab);

        if (activate)
        {
            Tabs.SelectedItem = tabItem;
            _activeTab = tab;
            _lastActiveBrowserTab = tab;
            UpdateWindowTitle();
            UpdateAiPanelVisibility();
            SyncUiToActiveTab();
        }

        // Save session when tab added
        RequestSessionSave();
    }

    // ---------- Tab Header Updates (Favicon, Title, Loading) ----------

    private void OnBrowserTitleChanged(ChromiumWebBrowser browser, string title)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_headersByBrowser.TryGetValue(browser, out var header))
            {
                header.Title = string.IsNullOrEmpty(title) ? "New Tab" : title;

                // Also update the BrowserTab.Title for backwards compatibility
                var tab = _tabs.FirstOrDefault(t => t.Browser == browser);
                if (tab != null)
                {
                    tab.Title = header.Title;
                }

                // Update window title if this is the active tab
                if (_activeTab?.Browser == browser)
                {
                    UpdateWindowTitle();
                }
            }
        });
    }

    private void OnBrowserLoadingStateChanged(ChromiumWebBrowser browser, bool isLoading)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_headersByBrowser.TryGetValue(browser, out var header))
            {
                header.IsLoading = isLoading;
            }
        });
    }

    private void OnBrowserAddressChanged(ChromiumWebBrowser browser, string address)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_headersByBrowser.TryGetValue(browser, out var header))
            {
                header.Url = address;
            }

            // Update address bar if this is the active tab
            if (_activeTab?.Browser == browser)
            {
                AddressBar.Text = address;
            }

            // Save session on navigation
            RequestSessionSave();
        });
    }

    private void UpdateWindowTitle()
    {
        if (_activeTab != null && _headersByBrowser.TryGetValue(_activeTab.Browser, out var header))
        {
            Title = $"{header.Title} - Conjure AI Browser";
        }
        else if (Tabs.SelectedItem == _settingsTab)
        {
            Title = "Settings - Conjure AI Browser";
        }
        else if (Tabs.SelectedItem == _historyTab)
        {
            Title = "History - Conjure AI Browser";
        }
        else if (Tabs.SelectedItem == _downloadsTab)
        {
            Title = "Downloads - Conjure AI Browser";
        }
        else
        {
            Title = "Conjure AI Browser";
        }
    }

    // ---------- Session Save ----------

    private void RequestSessionSave()
    {
        _sessionSaveDebounce.Stop();
        _sessionSaveDebounce.Start();
    }

    private void SaveSessionNow()
    {
        try
        {
            var state = CaptureSessionState();
            _ = _sessionStore.SaveAsync(state);
        }
        catch
        {
            // Ignore save errors
        }
    }

    private SessionState CaptureSessionState()
    {
        var state = new SessionState
        {
            SavedAtUtc = DateTimeOffset.UtcNow,
            Tabs = new List<SessionTab>()
        };

        var webTabIndex = 0;
        var selectedWebTabIndex = 0;

        foreach (TabItem tabItem in Tabs.Items)
        {
            // Only capture web tabs
            if (tabItem.Content is ChromiumWebBrowser browser && tabItem.Tag is BrowserTab)
            {
                var url = browser.Address;

                // Skip invalid URLs
                if (string.IsNullOrEmpty(url) ||
                    url == "about:blank" ||
                    url.StartsWith("chrome-devtools://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Get title from header model if available
                string? title = null;
                if (_headersByBrowser.TryGetValue(browser, out var header))
                {
                    title = header.Title;
                }

                state.Tabs.Add(new SessionTab(url, title));

                // Track selected tab index among web tabs
                if (Tabs.SelectedItem == tabItem)
                {
                    selectedWebTabIndex = webTabIndex;
                }

                webTabIndex++;
            }
        }

        state.SelectedWebTabIndex = selectedWebTabIndex;
        return state;
    }

    // ---------- Bookmarks ----------

    private async void BookmarkButton_Click(object sender, RoutedEventArgs e)
    {
        var url = _activeTab?.Browser.Address;
        if (string.IsNullOrWhiteSpace(url)) return;

        var title = _activeTab?.Browser.Title;
        var nowBookmarked = await _bookmarks.ToggleAsync(url, title);

        BookmarkButton.IsChecked = nowBookmarked;
        BookmarkButton.Content = nowBookmarked ? "★" : "☆";
        BookmarkButton.ToolTip = nowBookmarked ? "Remove bookmark" : "Bookmark this page";
        
        RenderBookmarksBar();
    }

    private void BookmarksMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        if (_bookmarks.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(No bookmarks yet)", IsEnabled = false });
        }
        else
        {
            foreach (var bookmark in _bookmarks.Items.OrderBy(x => x.Title))
            {
                var item = new MenuItem { Header = bookmark.Title, ToolTip = bookmark.Url };
                item.Click += (_, __) => Navigate(bookmark.Url);
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());

            var openFile = new MenuItem { Header = "Open bookmarks.json" };
            openFile.Click += (_, __) =>
            {
                if (File.Exists(_bookmarks.FilePath))
                {
                    Process.Start(new ProcessStartInfo { FileName = _bookmarks.FilePath, UseShellExecute = true });
                }
            };
            menu.Items.Add(openFile);

            var openFolder = new MenuItem { Header = "Open bookmarks folder" };
            openFolder.Click += (_, __) =>
            {
                var dir = Path.GetDirectoryName(_bookmarks.FilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                }
            };
            menu.Items.Add(openFolder);
        }

        menu.PlacementTarget = BookmarksMenuButton;
        menu.IsOpen = true;
    }

    private void RenderBookmarksBar()
    {
        BookmarksBarPanel.Children.Clear();

        // Context menu for empty area of bookmarks bar (works even when empty)
        var barContextMenu = new ContextMenu();

        var openManagerItem = new MenuItem { Header = "Open Bookmark Manager" };
        openManagerItem.Click += (_, __) =>
        {
            if (File.Exists(_bookmarks.FilePath))
            {
                Process.Start(new ProcessStartInfo { FileName = _bookmarks.FilePath, UseShellExecute = true });
            }
        };
        barContextMenu.Items.Add(openManagerItem);

        var openFolderItem = new MenuItem { Header = "Open bookmarks folder" };
        openFolderItem.Click += (_, __) =>
        {
            var dir = Path.GetDirectoryName(_bookmarks.FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
        };
        barContextMenu.Items.Add(openFolderItem);

        BookmarksBarPanel.ContextMenu = barContextMenu;
        BookmarksBarEmptyHint.ContextMenu = barContextMenu;

        if (_bookmarks.Items.Count == 0)
        {
            BookmarksBarEmptyHint.Visibility = Visibility.Visible;
            BookmarksBarScroller.Visibility = Visibility.Collapsed;
            return;
        }

        BookmarksBarEmptyHint.Visibility = Visibility.Collapsed;
        BookmarksBarScroller.Visibility = Visibility.Visible;

        for (int i = 0; i < _bookmarks.Items.Count; i++)
        {
            var bookmark = _bookmarks.Items[i];
            var index = i; // Capture for closures

            var button = new Button
            {
                Content = bookmark.Title.Length > 30 ? bookmark.Title[..30] + "..." : bookmark.Title,
                ToolTip = bookmark.Url,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Background = SystemColors.ControlLightBrush,
                BorderThickness = new Thickness(1),
                BorderBrush = SystemColors.ControlDarkBrush
            };

            // Left-click: navigate in current tab
            button.Click += (_, __) => Navigate(bookmark.Url);

            // Ctrl+click or middle-click: open in new tab
            button.PreviewMouseDown += (sender, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle ||
                    (e.ChangedButton == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control))
                {
                    AddNewTab(bookmark.Url);
                    e.Handled = true;
                }
            };

            // Right-click context menu
            var contextMenu = new ContextMenu();

            var openItem = new MenuItem { Header = "Open" };
            openItem.Click += (_, __) => Navigate(bookmark.Url);
            contextMenu.Items.Add(openItem);

            var openNewTabItem = new MenuItem { Header = "Open in new tab" };
            openNewTabItem.Click += (_, __) => AddNewTab(bookmark.Url);
            contextMenu.Items.Add(openNewTabItem);

            contextMenu.Items.Add(new Separator());

            var copyLinkItem = new MenuItem { Header = "Copy link address" };
            copyLinkItem.Click += (_, __) => Clipboard.SetText(bookmark.Url);
            contextMenu.Items.Add(copyLinkItem);

            contextMenu.Items.Add(new Separator());

            var editItem = new MenuItem { Header = "Edit…" };
            editItem.Click += async (_, __) => await EditBookmarkAsync(bookmark.Url);
            contextMenu.Items.Add(editItem);

            var removeItem = new MenuItem { Header = "Remove" };
            removeItem.Click += async (_, __) =>
            {
                await _bookmarks.RemoveAsync(bookmark.Url);
                RenderBookmarksBar();
                await UpdateBookmarkUiAsync();
            };
            contextMenu.Items.Add(removeItem);

            contextMenu.Items.Add(new Separator());

            var moveLeftItem = new MenuItem { Header = "Move left", IsEnabled = index > 0 };
            moveLeftItem.Click += async (_, __) =>
            {
                await _bookmarks.MoveAsync(bookmark.Url, index - 1);
                RenderBookmarksBar();
            };
            contextMenu.Items.Add(moveLeftItem);

            var moveRightItem = new MenuItem { Header = "Move right", IsEnabled = index < _bookmarks.Items.Count - 1 };
            moveRightItem.Click += async (_, __) =>
            {
                await _bookmarks.MoveAsync(bookmark.Url, index + 1);
                RenderBookmarksBar();
            };
            contextMenu.Items.Add(moveRightItem);

            button.ContextMenu = contextMenu;

            BookmarksBarPanel.Children.Add(button);
        }
    }

    private async Task EditBookmarkAsync(string originalUrl)
    {
        var bookmark = _bookmarks.Items.FirstOrDefault(b => string.Equals(b.Url, originalUrl, StringComparison.OrdinalIgnoreCase));
        if (bookmark == null) return;

        var dialog = new Dialogs.EditBookmarkWindow(bookmark.Title, bookmark.Url)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            var success = await _bookmarks.UpdateAsync(originalUrl, dialog.BookmarkTitle, dialog.BookmarkUrl);
            if (success)
            {
                RenderBookmarksBar();
                await UpdateBookmarkUiAsync();
            }
        }
    }

    // ---------- AI panel ----------

    private void AiPanelToggle_Click(object sender, RoutedEventArgs e) => UpdateAiPanelVisibility();

    private void UpdateAiPanelVisibility()
    {
        var tab = _activeTab;
        if (tab is null)
        {
            AiPanelToggle.IsChecked = false;
            AiColumn.Width = new GridLength(0);
            return;
        }

        AiPanelToggle.IsEnabled = true;
        var show = AiPanelToggle.IsChecked == true;
        tab.AiVisible = show;
        AiColumn.Width = show ? new GridLength(360) : new GridLength(0);
    }

    private void AiQuestion_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
        {
            AiAsk_Click(sender, e);
            e.Handled = true;
        }
    }

    private void AiConversation_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        AiInput.Focus();
    }

    private async void AiAsk_Click(object? sender, RoutedEventArgs e)
    {
        var tab = _activeTab;
        if (tab is null) return;

        ApplyAiSettingsFromUi();

        var question = AiInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        AppendMessage(tab, "user", question);
        AiInput.Clear();
        RenderConversation(tab);

        var pageText = await GetPageInnerTextAsync(tab);
        var screenshot = await CaptureScreenshotAsync(tab);
        
        AppendMessage(tab, "assistant", "Thinking...");
        RenderConversation(tab);

        _ai.ApiKey = string.IsNullOrWhiteSpace(tab.ApiKey) ? _globalApiKey : tab.ApiKey;
        _ai.Model = tab.Model;

        var answer = await _ai.AnswerAsync(pageText, question, tab.Conversation, screenshot);
        ReplaceLastAssistantMessage(tab, answer);
        RenderConversation(tab);
    }

    private async Task<string> GetPageInnerTextAsync(BrowserTab tab)
    {
        var browser = tab.Browser;
        if (browser is null || !browser.IsBrowserInitialized) return string.Empty;

        var response = await browser.EvaluateScriptAsync("document.body ? document.body.innerText : '';");
        if (!response.Success || response.Result is null) return string.Empty;

        var text = response.Result.ToString() ?? string.Empty;
        if (text.Length > 20000)
            text = text[..20000] + "\n...(truncated)...";

        return text;
    }

    private async Task<byte[]?> CaptureScreenshotAsync(BrowserTab tab)
    {
        try
        {
            var browser = tab.Browser;
            if (browser is null || !browser.IsBrowserInitialized) return null;

            byte[]? result = null;

            await Dispatcher.InvokeAsync(() =>
            {
                var width = (int)Math.Max(2, browser.ActualWidth);
                var height = (int)Math.Max(2, browser.ActualHeight);

                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(browser);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                result = ms.ToArray();
            });

            return result;
        }
        catch
        {
            // Silently ignore screenshot errors
            return null;
        }
    }

    private void ApplyAiSettingsFromUi()
    {
        var tab = _activeTab;
        if (tab is null) return;

        tab.ApiKey = _globalApiKey;
        tab.Model = GetSelectedModel();

        _ai.ApiKey = _globalApiKey;
        _ai.Model = tab.Model;
    }

    private string GetSelectedModel()
    {
        if (ModelSelector.SelectedItem is ComboBoxItem item)
        {
            if (item.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                return tag.Trim();
            if (item.Content is string content && !string.IsNullOrWhiteSpace(content))
                return content.Trim();
        }

        return "gemini-2.5-flash";
    }

    private void SyncUiToActiveTab()
    {
        var tab = _activeTab;
        if (tab is null) return;

        BookmarkButton.IsEnabled = true;
        BookmarksMenuButton.IsEnabled = true;
        AiPanelToggle.IsEnabled = true;

        for (int i = 0; i < ModelSelector.Items.Count; i++)
        {
            if (ModelSelector.Items[i] is ComboBoxItem item)
            {
                var tag = item.Tag as string;
                var content = item.Content as string;
                if (string.Equals(tag, tab.Model, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(content, tab.Model, StringComparison.OrdinalIgnoreCase))
                {
                    ModelSelector.SelectedIndex = i;
                    break;
                }
            }
        }

        var keyToUse = string.IsNullOrWhiteSpace(tab.ApiKey) ? _globalApiKey : tab.ApiKey;
        _ai.ApiKey = keyToUse;
        _ai.Model = tab.Model;
        AiInput.Text = string.Empty;

        AiPanelToggle.IsChecked = tab.AiVisible;
        AiColumn.Width = tab.AiVisible ? new GridLength(360) : new GridLength(0);

        BackButton.IsEnabled = tab.Browser.CanGoBack;
        ForwardButton.IsEnabled = tab.Browser.CanGoForward;
        ReloadButton.IsEnabled = true;

        RenderConversation(tab);
    }

    // ---------- Conversation helpers ----------

    private void AppendMessage(BrowserTab tab, string role, string text)
    {
        tab.Conversation.Add((role, text));
    }

    private void ReplaceLastAssistantMessage(BrowserTab tab, string text)
    {
        for (var i = tab.Conversation.Count - 1; i >= 0; i--)
        {
            if (tab.Conversation[i].Role == "assistant")
            {
                tab.Conversation[i] = ("assistant", text);
                return;
            }
        }

        tab.Conversation.Add(("assistant", text));
    }

    private void RenderConversation(BrowserTab tab)
    {
        var lines = tab.Conversation.Select(m => $"{m.Role}: {m.Text}");
        AiConversation.Text = string.Join("\n\n", lines);
    }

    // ---------- Settings ----------

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsTab != null)
        {
            Tabs.SelectedItem = _settingsTab;
            return;
        }

        var stack = new StackPanel
        {
            Margin = new Thickness(16),
            VerticalAlignment = VerticalAlignment.Top
        };
        stack.Children.Add(new TextBlock
        {
            Text = "Settings",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });

        stack.Children.Add(new TextBlock
        {
            Text = "Gemini API key (shared across all tabs)",
            Margin = new Thickness(0, 0, 0, 4)
        });

        _settingsApiKeyBox = new PasswordBox
        {
            Password = _globalApiKey,
            Width = 360
        };
        stack.Children.Add(_settingsApiKeyBox);

        var saveButton = new Button
        {
            Content = "Save API key",
            Width = 120,
            Margin = new Thickness(0, 8, 0, 0)
        };
        saveButton.Click += (_, __) => SaveGlobalApiKey();
        stack.Children.Add(saveButton);

        _settingsStatusText = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = SystemColors.GrayTextBrush,
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(_settingsStatusText);

        var tabItem = new TabItem
        {
            Header = "Settings",
            Content = stack
        };

        _settingsTab = tabItem;
        Tabs.Items.Add(tabItem);
        Tabs.SelectedItem = tabItem;
    }

    private void SaveGlobalApiKey()
    {
        if (_settingsApiKeyBox is null) return;

        var key = _settingsApiKeyBox.Password.Trim();
        ApplyGlobalApiKey(key);

        _settingsStatusText?.SetCurrentValue(TextBlock.TextProperty,
            string.IsNullOrWhiteSpace(key)
                ? "API key cleared. Tabs can still override per-tab keys."
                : "Saved API key and applied it to every tab.");
    }

    private void ApplyGlobalApiKey(string key, bool updateExistingTabs = true, bool updateSettingsUi = true)
    {
        _globalApiKey = key.Trim();
        _ai.ApiKey = _globalApiKey;

        if (updateExistingTabs)
        {
            foreach (var tab in _tabs)
            {
                tab.ApiKey = _globalApiKey;
            }
        }

        if (_activeTab != null)
        {
            _activeTab.ApiKey = _globalApiKey;
            SyncUiToActiveTab();
        }

        if (updateSettingsUi && _settingsApiKeyBox is not null)
        {
            _settingsApiKeyBox.Password = _globalApiKey;
        }
    }

    // ---------- History ----------

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

        // === Address bar focus ===
        // Ctrl+L or Ctrl+K: Focus address bar
        if (ctrl && !shift && !alt && (e.Key == Key.L || e.Key == Key.K))
        {
            FocusAddressBar();
            e.Handled = true;
            return;
        }

        // === Tab management ===
        // Ctrl+T: New tab
        if (ctrl && !shift && !alt && e.Key == Key.T)
        {
            AddNewTab(HomeUrl);
            e.Handled = true;
            return;
        }

        // Ctrl+W: Close current tab
        if (ctrl && !shift && !alt && e.Key == Key.W)
        {
            CloseCurrentTab();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+T: Reopen last closed tab
        if (ctrl && shift && !alt && e.Key == Key.T)
        {
            ReopenLastClosedTab();
            e.Handled = true;
            return;
        }

        // Ctrl+Tab: Next tab
        if (ctrl && !shift && !alt && e.Key == Key.Tab)
        {
            CycleToNextTab();
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+Tab: Previous tab
        if (ctrl && shift && !alt && e.Key == Key.Tab)
        {
            CycleToPreviousTab();
            e.Handled = true;
            return;
        }

        // Ctrl+1..8: Jump to tab 1-8
        if (ctrl && !shift && !alt)
        {
            int? tabIndex = e.Key switch
            {
                Key.D1 => 0,
                Key.D2 => 1,
                Key.D3 => 2,
                Key.D4 => 3,
                Key.D5 => 4,
                Key.D6 => 5,
                Key.D7 => 6,
                Key.D8 => 7,
                _ => null
            };
            if (tabIndex.HasValue)
            {
                SelectTabByIndex(tabIndex.Value);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+9: Jump to last tab
        if (ctrl && !shift && !alt && e.Key == Key.D9)
        {
            SelectTabByIndex(Tabs.Items.Count - 1);
            e.Handled = true;
            return;
        }

        // === Navigation (web tabs only) ===
        var browser = GetActiveBrowser();

        // Alt+Left: Back
        if (!ctrl && !shift && alt && e.Key == Key.Left)
        {
            if (browser?.CanGoBack == true)
            {
                browser.Back();
            }
            e.Handled = true;
            return;
        }

        // Alt+Right: Forward
        if (!ctrl && !shift && alt && e.Key == Key.Right)
        {
            if (browser?.CanGoForward == true)
            {
                browser.Forward();
            }
            e.Handled = true;
            return;
        }

        // F5: Reload
        if (!ctrl && !shift && !alt && e.Key == Key.F5)
        {
            browser?.Reload();
            e.Handled = true;
            return;
        }

        // Ctrl+R: Reload
        if (ctrl && !shift && !alt && e.Key == Key.R)
        {
            browser?.Reload();
            e.Handled = true;
            return;
        }

        // Ctrl+F5: Hard reload (ignore cache)
        if (ctrl && !shift && !alt && e.Key == Key.F5)
        {
            browser?.Reload(ignoreCache: true);
            e.Handled = true;
            return;
        }

        // Escape: Close find bar first, then stop loading
        if (!ctrl && !shift && !alt && e.Key == Key.Escape)
        {
            // First priority: close find bar if open
            if (FindBarContainer.Visibility == Visibility.Visible)
            {
                CloseFindBar();
                e.Handled = true;
                return;
            }

            // Second priority: stop loading
            if (browser?.IsLoading == true)
            {
                browser.Stop();
                e.Handled = true;
                return;
            }
        }

        // === Feature shortcuts ===
        // Ctrl+F: Find in page
        if (ctrl && !shift && !alt && e.Key == Key.F)
        {
            ShowFindBar();
            e.Handled = true;
            return;
        }

        // Ctrl+H: History
        if (ctrl && !shift && !alt && e.Key == Key.H)
        {
            OpenHistoryTab();
            e.Handled = true;
            return;
        }

        // Ctrl+J: Downloads
        if (ctrl && !shift && !alt && e.Key == Key.J)
        {
            OpenDownloadsTab();
            e.Handled = true;
            return;
        }
    }

    // === Keyboard shortcut helper methods ===

    private void FocusAddressBar()
    {
        AddressBar.Focus();
        AddressBar.SelectAll();
    }

    private ChromiumWebBrowser? GetActiveBrowser()
    {
        if (Tabs.SelectedItem is TabItem tabItem && tabItem.Tag is BrowserTab tab)
        {
            return tab.Browser;
        }
        return null;
    }

    private void ReopenLastClosedTab()
    {
        if (_recentlyClosedTabs.Count == 0) return;

        var lastClosed = _recentlyClosedTabs[^1];
        _recentlyClosedTabs.RemoveAt(_recentlyClosedTabs.Count - 1);
        AddNewTab(lastClosed.Url);
    }

    private void CycleToNextTab()
    {
        if (Tabs.Items.Count <= 1) return;
        Tabs.SelectedIndex = (Tabs.SelectedIndex + 1) % Tabs.Items.Count;
    }

    private void CycleToPreviousTab()
    {
        if (Tabs.Items.Count <= 1) return;
        Tabs.SelectedIndex = (Tabs.SelectedIndex - 1 + Tabs.Items.Count) % Tabs.Items.Count;
    }

    private void SelectTabByIndex(int index)
    {
        if (index >= 0 && index < Tabs.Items.Count)
        {
            Tabs.SelectedIndex = index;
        }
    }

    // === Find in Page ===

    private void ShowFindBar()
    {
        var browser = GetActiveBrowser();
        if (browser == null) return; // Only works on web tabs

        FindBarContainer.Visibility = Visibility.Visible;
        FindTextBox.Focus();
        FindTextBox.SelectAll();

        // If there's existing text, start a search
        if (!string.IsNullOrEmpty(FindTextBox.Text))
        {
            _findManager.StartNewSearch(browser, FindTextBox.Text, FindMatchCaseToggle.IsChecked == true);
        }
    }

    private void CloseFindBar()
    {
        FindBarContainer.Visibility = Visibility.Collapsed;

        var browser = GetActiveBrowser();
        if (browser != null)
        {
            _findManager.Stop(browser, clearSelection: true);
        }

        FindCountText.Text = "0/0";
    }

    private void OnFindResultUpdated(ChromiumWebBrowser browser, int activeOrdinal, int count, bool finalUpdate)
    {
        // Only update UI if this is the active browser
        var activeBrowser = GetActiveBrowser();
        if (activeBrowser != browser) return;

        if (count <= 0)
        {
            FindCountText.Text = "0/0";
        }
        else
        {
            FindCountText.Text = $"{activeOrdinal}/{count}";
        }
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Debounce search to avoid firing on every keystroke
        _findDebounceTimer?.Stop();
        _findDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _findDebounceTimer.Tick += (_, __) =>
        {
            _findDebounceTimer.Stop();
            PerformFind();
        };
        _findDebounceTimer.Start();
    }

    private void PerformFind()
    {
        var browser = GetActiveBrowser();
        if (browser == null) return;

        var text = FindTextBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            _findManager.Stop(browser, clearSelection: true);
            FindCountText.Text = "0/0";
        }
        else
        {
            _findManager.StartNewSearch(browser, text, FindMatchCaseToggle.IsChecked == true);
        }
    }

    private void FindTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var browser = GetActiveBrowser();
            if (browser != null && !string.IsNullOrEmpty(FindTextBox.Text))
            {
                var forward = (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift;
                _findManager.FindNext(browser, forward);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    private void FindPrevButton_Click(object sender, RoutedEventArgs e)
    {
        var browser = GetActiveBrowser();
        if (browser != null && !string.IsNullOrEmpty(FindTextBox.Text))
        {
            _findManager.FindNext(browser, forward: false);
        }
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e)
    {
        var browser = GetActiveBrowser();
        if (browser != null && !string.IsNullOrEmpty(FindTextBox.Text))
        {
            _findManager.FindNext(browser, forward: true);
        }
    }

    private void FindCloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseFindBar();
    }

    private void FindMatchCaseToggle_Changed(object sender, RoutedEventArgs e)
    {
        // Re-search with new match case setting
        var browser = GetActiveBrowser();
        if (browser != null && !string.IsNullOrEmpty(FindTextBox.Text))
        {
            _findManager.StartNewSearch(browser, FindTextBox.Text, FindMatchCaseToggle.IsChecked == true);
        }
    }

    // ---------- App Menu ----------

    private void AppMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppMenuButton.ContextMenu != null)
        {
            AppMenuButton.ContextMenu.PlacementTarget = AppMenuButton;
            AppMenuButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            AppMenuButton.ContextMenu.IsOpen = true;
        }
    }

    private void AppMenu_Opened(object sender, RoutedEventArgs e)
    {
        // Find in Page is only available on web tabs
        var browser = GetActiveBrowser();
        FindInPageMenuItem.IsEnabled = browser != null;
    }

    private void NewTabFromMenu_Click(object sender, RoutedEventArgs e)
    {
        AddNewTab(HomeUrl);
    }

    private void HistoryFromMenu_Click(object sender, RoutedEventArgs e)
    {
        OpenHistoryTab();
    }

    private void DownloadsFromMenu_Click(object sender, RoutedEventArgs e)
    {
        OpenDownloadsTab();
    }

    private void FindInPageFromMenu_Click(object sender, RoutedEventArgs e)
    {
        ShowFindBar();
    }

    private void SettingsFromMenu_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsTab();
    }

    private void AboutFromMenu_Click(object sender, RoutedEventArgs e)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var versionStr = version != null ? version.ToString() : "1.0.0";

        MessageBox.Show(
            $"Conjure AI Browser\n\nVersion: {versionStr}\n\nPowered by Chromium via CefSharp",
            "About Conjure AI Browser",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ExitFromMenu_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void OpenSettingsTab()
    {
        // Reuse existing settings tab logic from SettingsButton_Click
        SettingsButton_Click(this, new RoutedEventArgs());
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        OpenHistoryTab();
    }

    private void OpenHistoryTab()
    {
        if (_historyTab != null)
        {
            Tabs.SelectedItem = _historyTab;
            RefreshHistoryList();
            return;
        }

        _historySearchHasPlaceholder = true;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _historySearchBox = new TextBox
        {
            Width = 320,
            Margin = new Thickness(0, 0, 0, 8),
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = SystemColors.GrayTextBrush,
            Text = "Search history"
        };
        _historySearchBox.GotFocus += (_, __) =>
        {
            if (_historySearchHasPlaceholder && _historySearchBox != null)
            {
                _historySearchHasPlaceholder = false;
                _historySearchBox.Text = string.Empty;
                _historySearchBox.Foreground = SystemColors.ControlTextBrush;
            }
        };
        _historySearchBox.LostFocus += (_, __) =>
        {
            if (_historySearchBox != null && string.IsNullOrWhiteSpace(_historySearchBox.Text))
            {
                _historySearchHasPlaceholder = true;
                _historySearchBox.Text = "Search history";
                _historySearchBox.Foreground = SystemColors.GrayTextBrush;
            }
        };
        _historySearchBox.TextChanged += HistorySearch_TextChanged;
        Grid.SetRow(_historySearchBox, 0);
        grid.Children.Add(_historySearchBox);

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var clearButton = new Button
        {
            Content = "Clear browsing data",
            Width = 140
        };
        clearButton.Click += ClearHistory_Click;
        buttonsPanel.Children.Add(clearButton);

        Grid.SetRow(buttonsPanel, 1);
        grid.Children.Add(buttonsPanel);

        _historyListView = new ListView { Margin = new Thickness(0) };
        _historyListView.MouseDoubleClick += HistoryList_MouseDoubleClick;
        _historyListView.KeyDown += HistoryList_KeyDown;
        _historyListView.PreviewMouseDown += HistoryList_PreviewMouseDown;

        var contextMenu = new ContextMenu();

        var openItem = new MenuItem { Header = "Open" };
        openItem.Click += (_, __) => OpenSelectedHistoryEntry(false);
        contextMenu.Items.Add(openItem);

        var openNewTabItem = new MenuItem { Header = "Open in new tab" };
        openNewTabItem.Click += (_, __) => OpenSelectedHistoryEntry(true);
        contextMenu.Items.Add(openNewTabItem);

        contextMenu.Items.Add(new Separator());

        var copyLinkItem = new MenuItem { Header = "Copy link address" };
        copyLinkItem.Click += CopyHistoryLink_Click;
        contextMenu.Items.Add(copyLinkItem);

        contextMenu.Items.Add(new Separator());

        var removeItem = new MenuItem { Header = "Remove from history" };
        removeItem.Click += RemoveHistoryEntry_Click;
        contextMenu.Items.Add(removeItem);

        _historyListView.ContextMenu = contextMenu;

        Grid.SetRow(_historyListView, 2);
        grid.Children.Add(_historyListView);

        var tabItem = new TabItem
        {
            Header = "History",
            Content = grid
        };

        _historyTab = tabItem;
        Tabs.Items.Add(tabItem);
        Tabs.SelectedItem = tabItem;

        RefreshHistoryList();
    }

    private void RefreshHistoryList(string? searchText = null)
    {
        if (_historyListView == null) return;

        searchText = searchText ?? _historySearchBox?.Text?.Trim();

        // Ignore placeholder text
        if (_historySearchHasPlaceholder)
            searchText = null;

        var filtered = string.IsNullOrWhiteSpace(searchText)
            ? _history.Items.AsEnumerable()
            : _history.Items.Where(h =>
                h.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                h.Url.Contains(searchText, StringComparison.OrdinalIgnoreCase));

        _filteredHistory = filtered
            .OrderByDescending(h => h.VisitedAt)
            .ToList();

        _historyListView.Items.Clear();
        foreach (var entry in _filteredHistory)
        {
            var item = CreateHistoryListItem(entry);
            _historyListView.Items.Add(item);
        }
    }

    private ListViewItem CreateHistoryListItem(HistoryEntry entry)
    {
        var row = new Grid { Margin = new Thickness(4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };

        var titleBlock = new TextBlock
        {
            Text = entry.Title,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textStack.Children.Add(titleBlock);

        var urlBlock = new TextBlock
        {
            Text = entry.Url,
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textStack.Children.Add(urlBlock);

        Grid.SetColumn(textStack, 0);
        row.Children.Add(textStack);

        var timeBlock = new TextBlock
        {
            Text = entry.VisitedAt.ToLocalTime().ToString("HH:mm"),
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(timeBlock, 1);
        row.Children.Add(timeBlock);

        return new ListViewItem
        {
            Content = row,
            Tag = entry
        };
    }

    private void HistorySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshHistoryList();
    }

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedHistoryEntry(false);
    }

    private void HistoryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var newTab = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            OpenSelectedHistoryEntry(newTab);
            e.Handled = true;
        }
    }

    private void HistoryList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_historyListView == null) return;

        var source = e.OriginalSource as DependencyObject;
        var item = ItemsControl.ContainerFromElement(_historyListView, source) as ListViewItem;
        if (item != null)
        {
            item.IsSelected = true;

            if (e.ChangedButton == MouseButton.Middle && item.Tag is HistoryEntry entry)
            {
                AddNewTab(entry.Url);
                e.Handled = true;
            }
        }
    }

    private void OpenSelectedHistoryEntry(bool newTab)
    {
        if (_historyListView?.SelectedItem is not ListViewItem item) return;
        if (item.Tag is not HistoryEntry entry) return;

        if (newTab)
        {
            AddNewTab(entry.Url);
            return;
        }

        var targetTab = _activeTab ?? _lastActiveBrowserTab ?? _tabs.FirstOrDefault();
        if (targetTab is null) return;

        Tabs.SelectedItem = targetTab.TabItem;
        _activeTab = targetTab;
        Navigate(entry.Url);
    }

    private void CopyHistoryLink_Click(object sender, RoutedEventArgs e)
    {
        if (_historyListView?.SelectedItem is not ListViewItem item) return;
        if (item.Tag is not HistoryEntry entry) return;

        Clipboard.SetText(entry.Url);
    }

    private async void RemoveHistoryEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_historyListView?.SelectedItem is not ListViewItem item) return;
        if (item.Tag is not HistoryEntry entry) return;

        await _history.RemoveAsync(entry.Url, entry.VisitedAt);
        RefreshHistoryList();
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to clear all browsing history?",
            "Clear History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            await _history.ClearAsync();
            RefreshHistoryList();
        }
    }

    // ---------- Downloads ----------

    private void DownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenDownloadsTab();
    }

    private void OpenDownloadsTab()
    {
        if (_downloadsTab != null)
        {
            Tabs.SelectedItem = _downloadsTab;
            RefreshDownloadsList();
            return;
        }

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Top row: Header + Open folder button
        var topPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var header = new TextBlock
        {
            Text = "Downloads",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        topPanel.Children.Add(header);

        var openFolderButton = new Button
        {
            Content = "Open downloads folder",
            Width = 160
        };
        openFolderButton.Click += (_, __) =>
        {
            var downloadsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            try { Process.Start("explorer.exe", downloadsPath); } catch { }
        };
        topPanel.Children.Add(openFolderButton);

        Grid.SetRow(topPanel, 0);
        grid.Children.Add(topPanel);

        // ListView for downloads
        _downloadsListView = new ListView { Margin = new Thickness(0) };
        _downloadsListView.MouseDoubleClick += (s, e) =>
        {
            if (_downloadsListView.SelectedItem is ListViewItem item && item.Tag is DownloadRecord rec)
            {
                if (rec.Status == "Completed" && File.Exists(rec.FullPath))
                {
                    try { Process.Start(new ProcessStartInfo { FileName = rec.FullPath, UseShellExecute = true }); } catch { }
                }
            }
        };

        // Context menu
        var contextMenu = new ContextMenu();

        var openItem = new MenuItem { Header = "Open" };
        openItem.Click += OpenDownloadFile_Click;
        contextMenu.Items.Add(openItem);

        var showItem = new MenuItem { Header = "Show in folder" };
        showItem.Click += ShowInFolder_Click;
        contextMenu.Items.Add(showItem);

        contextMenu.Items.Add(new Separator());

        var copyUrlItem = new MenuItem { Header = "Copy download URL" };
        copyUrlItem.Click += CopyDownloadUrl_Click;
        contextMenu.Items.Add(copyUrlItem);

        contextMenu.Items.Add(new Separator());

        var removeItem = new MenuItem { Header = "Remove from list" };
        removeItem.Click += RemoveDownload_Click;
        contextMenu.Items.Add(removeItem);

        _downloadsListView.ContextMenu = contextMenu;

        Grid.SetRow(_downloadsListView, 1);
        grid.Children.Add(_downloadsListView);

        // Populate the list
        RefreshDownloadsList();

        // Subscribe to collection changes to auto-refresh
        _downloadManager.Downloads.CollectionChanged += (_, __) =>
        {
            if (_downloadsListView != null)
                RefreshDownloadsList();
        };

        var tabItem = new TabItem
        {
            Header = "Downloads",
            Content = grid
        };

        _downloadsTab = tabItem;
        Tabs.Items.Add(tabItem);
        Tabs.SelectedItem = tabItem;
    }

    private void RefreshDownloadsList()
    {
        if (_downloadsListView == null) return;

        _downloadsListView.Items.Clear();

        foreach (var record in _downloadManager.Downloads)
        {
            _downloadsListView.Items.Add(CreateDownloadListItem(record));
        }
    }

    private ListViewItem CreateDownloadListItem(DownloadRecord record)
    {
        var row = new Grid { Margin = new Thickness(4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left content: FileName + Status
        var textStack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };

        var fileNameBlock = new TextBlock
        {
            Text = record.FileName,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textStack.Children.Add(fileNameBlock);

        var statusBlock = new TextBlock
        {
            FontSize = 11,
            Foreground = SystemColors.GrayTextBrush
        };

        // Build status text
        if (record.Status == "InProgress")
        {
            var percent = record.ProgressPercent;
            statusBlock.Text = $"Downloading... {percent}%";
        }
        else if (record.Status == "Completed")
        {
            statusBlock.Text = "Completed";
        }
        else if (record.Status == "Canceled")
        {
            statusBlock.Text = "Canceled";
        }
        else if (record.Status == "Failed")
        {
            statusBlock.Text = "Failed";
        }
        else
        {
            statusBlock.Text = record.Status;
        }
        textStack.Children.Add(statusBlock);

        // Progress bar for in-progress downloads
        if (record.Status == "InProgress")
        {
            var progressBar = new ProgressBar
            {
                Value = record.ProgressPercent,
                Height = 4,
                Margin = new Thickness(0, 4, 0, 0)
            };
            textStack.Children.Add(progressBar);
        }

        Grid.SetColumn(textStack, 0);
        row.Children.Add(textStack);

        // Right buttons
        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var openBtn = new Button
        {
            Content = "Open",
            Width = 60,
            Margin = new Thickness(0, 0, 6, 0),
            IsEnabled = record.Status == "Completed" && !string.IsNullOrEmpty(record.FullPath) && File.Exists(record.FullPath),
            Tag = record
        };
        openBtn.Click += (s, e) =>
        {
            var rec = (s as Button)?.Tag as DownloadRecord;
            if (rec != null && rec.Status == "Completed" && File.Exists(rec.FullPath))
            {
                try { Process.Start(new ProcessStartInfo { FileName = rec.FullPath, UseShellExecute = true }); } catch { }
            }
        };
        buttonsPanel.Children.Add(openBtn);

        var showBtn = new Button
        {
            Content = "Show in folder",
            Width = 100,
            Margin = new Thickness(0, 0, 6, 0),
            IsEnabled = !string.IsNullOrEmpty(record.FullPath) && File.Exists(record.FullPath),
            Tag = record
        };
        showBtn.Click += (s, e) =>
        {
            var rec = (s as Button)?.Tag as DownloadRecord;
            if (rec != null && File.Exists(rec.FullPath))
            {
                try { Process.Start("explorer.exe", $"/select,\"{rec.FullPath}\""); } catch { }
            }
        };
        buttonsPanel.Children.Add(showBtn);

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 60,
            IsEnabled = record.Status == "InProgress",
            Tag = record
        };
        cancelBtn.Click += (s, e) =>
        {
            var rec = (s as Button)?.Tag as DownloadRecord;
            if (rec != null && rec.Status == "InProgress")
            {
                _downloadManager.CancelDownload(rec.Id);
                RefreshDownloadsList();
            }
        };
        buttonsPanel.Children.Add(cancelBtn);

        Grid.SetColumn(buttonsPanel, 1);
        row.Children.Add(buttonsPanel);

        return new ListViewItem
        {
            Content = row,
            Tag = record
        };
    }

    private void OpenDownloadFile_Click(object sender, RoutedEventArgs e)
    {
        var record = GetDownloadRecordFromSender(sender);
        if (record == null || record.Status != "Completed") return;
        if (string.IsNullOrWhiteSpace(record.FullPath) || !File.Exists(record.FullPath)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = record.FullPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        var record = GetDownloadRecordFromSender(sender);
        if (record == null) return;
        if (string.IsNullOrWhiteSpace(record.FullPath) || !File.Exists(record.FullPath)) return;

        try
        {
            Process.Start("explorer.exe", $"/select,\"{record.FullPath}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to show in folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        var record = GetDownloadRecordFromSender(sender);
        if (record == null || record.Status != "InProgress") return;

        _downloadManager.CancelDownload(record.Id);
    }

    private void CopyDownloadUrl_Click(object sender, RoutedEventArgs e)
    {
        var record = GetDownloadRecordFromSender(sender);
        if (record == null) return;

        Clipboard.SetText(record.Url);
    }

    private void RemoveDownload_Click(object sender, RoutedEventArgs e)
    {
        var record = GetDownloadRecordFromSender(sender);
        if (record == null) return;

        _downloadManager.RemoveDownload(record);
    }

    private DownloadRecord? GetDownloadRecordFromSender(object sender)
    {
        if (sender is MenuItem menuItem)
        {
            if (_downloadsListView?.SelectedItem is DownloadRecord record)
                return record;
        }
        else if (sender is Button button)
        {
            // Walk up visual tree to find ListViewItem
            DependencyObject? current = button;
            while (current != null && current is not ListViewItem)
            {
                current = VisualTreeHelper.GetParent(current);
            }

            if (current is ListViewItem item && item.Content is DownloadRecord record)
                return record;
        }

        return null;
    }

    private void DownloadsListView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_downloadsListView == null) return;

        var source = e.OriginalSource as DependencyObject;
        var item = ItemsControl.ContainerFromElement(_downloadsListView, source) as ListViewItem;
        if (item != null)
        {
            item.IsSelected = true;
        }
    }
}

// Converter for showing ProgressBar only when InProgress
public class StatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string status && status == "InProgress")
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class StatusEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string status && parameter is string expected)
        {
            return string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class CompletedAndExistsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (values.Length >= 2 &&
            values[0] is string status &&
            values[1] is string path)
        {
            return string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(path);
        }

        return false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class FileExistsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is string path)
        {
            return File.Exists(path);
        }

        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

internal sealed class BrowserTab
{
    public ChromiumWebBrowser Browser { get; init; } = null!;
    public TabItem TabItem { get; init; } = null!;
    public string Title { get; set; } = "New Tab";
    public List<(string Role, string Text)> Conversation { get; } = new();
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-2.5-flash";
    public bool AiVisible { get; set; }
}

internal sealed class ClosedTabInfo
{
    public string Url { get; init; } = string.Empty;
    public string? Title { get; init; }
}
