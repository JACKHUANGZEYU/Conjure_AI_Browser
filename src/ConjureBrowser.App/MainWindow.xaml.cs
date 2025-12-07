using System;
using System.Collections.Generic;
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

    private const string HomeUrl = "https://www.google.com";

    public MainWindow()
    {
        _ai = new GeminiAiAssistant(_httpClient, string.Empty, "gemini-2.5-flash");
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        await _bookmarks.LoadAsync();
        await _history.LoadAsync();
        RenderBookmarksBar();

        var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            ApplyGlobalApiKey(envKey, updateExistingTabs: false, updateSettingsUi: false);
        }

        Tabs.Items.Clear();
        AddNewTab(HomeUrl);
        UpdateAiPanelVisibility();
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
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddNewTab(HomeUrl);

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not TabItem tabItem) return;

        if (tabItem.Tag is BrowserTab tab)
        {
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
            AddNewTab(HomeUrl);
        }
        else if (ReferenceEquals(Tabs.SelectedItem, tabItem))
        {
            Tabs.SelectedIndex = Math.Max(0, Tabs.Items.Count - 1);
        }
    }

    private void AddNewTab(string? initialUrl = null)
    {
        var browser = new ChromiumWebBrowser();

        var initialTitle = string.IsNullOrWhiteSpace(initialUrl) ? "New Tab" : initialUrl!;

        var tabItem = new TabItem
        {
            Header = initialTitle,
            Content = browser
        };

        var tab = new BrowserTab
        {
            Browser = browser,
            TabItem = tabItem,
            Title = initialTitle,
            Model = GetSelectedModel(),
            ApiKey = _globalApiKey,
            AiVisible = false
        };

        tabItem.Tag = tab;
        tabItem.DataContext = tab;
        AttachBrowserEvents(tab);

        // Set download handler
        browser.DownloadHandler = _downloadManager;

        // Set address after events are attached so FrameLoadEnd can capture it.
        browser.Address = initialUrl ?? HomeUrl;

        Tabs.Items.Add(tabItem);
        _tabs.Add(tab);
        Tabs.SelectedItem = tabItem;
        _activeTab = tab;
        _lastActiveBrowserTab = tab;

        UpdateTabHeader(tab);
        UpdateAiPanelVisibility();
        SyncUiToActiveTab();
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
        if (e.Key == Key.H && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            OpenHistoryTab();
            e.Handled = true;
        }

        if (e.Key == Key.J && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            OpenDownloadsTab();
            e.Handled = true;
        }
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
            Process.Start("explorer.exe", downloadsPath);
        };
        topPanel.Children.Add(openFolderButton);

        Grid.SetRow(topPanel, 0);
        grid.Children.Add(topPanel);

        // ListView for downloads
        _downloadsListView = new ListView
        {
            Margin = new Thickness(0)
        };
        _downloadsListView.PreviewMouseDown += DownloadsListView_PreviewMouseDown;

        var statusToVisibilityConverter = new StatusToVisibilityConverter();
        var statusEqualsConverter = new StatusEqualsConverter();
        var completedAndExistsConverter = new CompletedAndExistsConverter();
        var fileExistsConverter = new FileExistsConverter();

        // Create item template
        var dataTemplate = new DataTemplate();
        var factory = new FrameworkElementFactory(typeof(Grid));

        // 3 columns: content, spacer, buttons
        factory.SetValue(Grid.MarginProperty, new Thickness(4));
        var col0 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col0.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        var col2 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        factory.AppendChild(col0);
        factory.AppendChild(col1);
        factory.AppendChild(col2);

        // Left content: FileName + Status + ProgressBar
        var leftStack = new FrameworkElementFactory(typeof(StackPanel));
        leftStack.SetValue(Grid.ColumnProperty, 0);

        var fileName = new FrameworkElementFactory(typeof(TextBlock));
        fileName.SetBinding(TextBlock.TextProperty, new Binding("FileName"));
        fileName.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        fileName.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        leftStack.AppendChild(fileName);

        var statusText = new FrameworkElementFactory(typeof(TextBlock));
        statusText.SetBinding(TextBlock.TextProperty, new Binding("Status"));
        statusText.SetValue(TextBlock.FontSizeProperty, 11.0);
        statusText.SetValue(TextBlock.ForegroundProperty, SystemColors.GrayTextBrush);
        leftStack.AppendChild(statusText);

        var progressBar = new FrameworkElementFactory(typeof(ProgressBar));
        progressBar.SetBinding(ProgressBar.ValueProperty, new Binding("ProgressPercent"));
        progressBar.SetValue(ProgressBar.HeightProperty, 4.0);
        progressBar.SetValue(ProgressBar.MarginProperty, new Thickness(0, 4, 0, 0));
        progressBar.SetBinding(ProgressBar.VisibilityProperty, new Binding("Status")
        {
            Converter = statusToVisibilityConverter
        });
        leftStack.AppendChild(progressBar);

        factory.AppendChild(leftStack);

        // Right buttons
        var buttonsStack = new FrameworkElementFactory(typeof(StackPanel));
        buttonsStack.SetValue(Grid.ColumnProperty, 2);
        buttonsStack.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        buttonsStack.SetValue(StackPanel.MarginProperty, new Thickness(12, 0, 0, 0));

        var openButton = new FrameworkElementFactory(typeof(Button));
        openButton.SetValue(Button.ContentProperty, "Open");
        openButton.SetValue(Button.WidthProperty, 60.0);
        openButton.SetValue(Button.MarginProperty, new Thickness(0, 0, 6, 0));
        var openEnableBinding = new MultiBinding { Converter = completedAndExistsConverter };
        openEnableBinding.Bindings.Add(new Binding("Status"));
        openEnableBinding.Bindings.Add(new Binding("FullPath"));
        openButton.SetBinding(Button.IsEnabledProperty, openEnableBinding);
        openButton.AddHandler(Button.ClickEvent, new RoutedEventHandler(OpenDownloadFile_Click));
        buttonsStack.AppendChild(openButton);

        var showButton = new FrameworkElementFactory(typeof(Button));
        showButton.SetValue(Button.ContentProperty, "Show in folder");
        showButton.SetValue(Button.WidthProperty, 100.0);
        showButton.SetValue(Button.MarginProperty, new Thickness(0, 0, 6, 0));
        showButton.SetBinding(Button.IsEnabledProperty, new Binding("FullPath")
        {
            Converter = fileExistsConverter
        });
        showButton.AddHandler(Button.ClickEvent, new RoutedEventHandler(ShowInFolder_Click));
        buttonsStack.AppendChild(showButton);

        var cancelButton = new FrameworkElementFactory(typeof(Button));
        cancelButton.SetValue(Button.ContentProperty, "Cancel");
        cancelButton.SetValue(Button.WidthProperty, 60.0);
        cancelButton.SetBinding(Button.IsEnabledProperty, new Binding("Status")
        {
            Converter = statusEqualsConverter,
            ConverterParameter = "InProgress"
        });
        cancelButton.AddHandler(Button.ClickEvent, new RoutedEventHandler(CancelDownload_Click));
        buttonsStack.AppendChild(cancelButton);

        factory.AppendChild(buttonsStack);

        dataTemplate.VisualTree = factory;
        _downloadsListView.ItemTemplate = dataTemplate;
        _downloadsListView.ItemsSource = _downloadManager.Downloads;

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

        var tabItem = new TabItem
        {
            Header = "Downloads",
            Content = grid
        };

        _downloadsTab = tabItem;
        Tabs.Items.Add(tabItem);
        Tabs.SelectedItem = tabItem;
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
