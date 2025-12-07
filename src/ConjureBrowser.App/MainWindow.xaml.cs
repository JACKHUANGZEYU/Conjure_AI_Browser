using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CefSharp;
using CefSharp.Wpf;
using ConjureBrowser.AI.Impl;
using ConjureBrowser.Core.Services;
using ConjureBrowser.Core.Utils;

namespace ConjureBrowser.App;

public partial class MainWindow : Window
{
    private readonly BookmarkStore _bookmarks = new();
    private readonly HttpClient _httpClient = new();
    private readonly GeminiAiAssistant _ai;

    private readonly List<BrowserTab> _tabs = new();
    private BrowserTab? _activeTab;

    private TabItem? _settingsTab;
    private PasswordBox? _settingsApiKeyBox;
    private TextBlock? _settingsStatusText;
    private string _globalApiKey = string.Empty;

    private const string HomeUrl = "https://www.google.com";

    public MainWindow()
    {
        _ai = new GeminiAiAssistant(_httpClient, string.Empty, "gemini-2.5-flash");
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        await _bookmarks.LoadAsync();

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
        }

        if (ReferenceEquals(tabItem, _settingsTab))
        {
            _settingsTab = null;
            _settingsApiKeyBox = null;
            _settingsStatusText = null;
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
        var browser = new ChromiumWebBrowser
        {
            Address = initialUrl ?? HomeUrl
        };

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

        Tabs.Items.Add(tabItem);
        _tabs.Add(tab);
        Tabs.SelectedItem = tabItem;
        _activeTab = tab;

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
        AppendMessage(tab, "assistant", "Thinking...");
        RenderConversation(tab);

        _ai.ApiKey = string.IsNullOrWhiteSpace(tab.ApiKey) ? _globalApiKey : tab.ApiKey;
        _ai.Model = tab.Model;

        var answer = await _ai.AnswerAsync(pageText, question, tab.Conversation);
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
