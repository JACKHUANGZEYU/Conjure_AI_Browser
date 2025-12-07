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
    private readonly List<string> _apiKeyHistory = new();
    private BrowserTab? _activeTab;

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
            AddApiKeyToHistory(envKey.Trim());
            ApiKeyBox.Text = envKey.Trim();
        }

        Tabs.Items.Clear();
        AddNewTab(HomeUrl);
        UpdateAiPanelVisibility();
    }

    // ---------- Navigation and tabs ----------

    private void AttachBrowserEvents(BrowserTab tab)
    {
        tab.Browser.TitleChanged += Browser_TitleChanged;
        tab.Browser.AddressChanged += Browser_AddressChanged;
        tab.Browser.LoadingStateChanged += Browser_LoadingStateChanged;
    }

    private void Browser_TitleChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_activeTab is null) return;
            _activeTab.Title = _activeTab.Browser.Title ?? "New Tab";
            _activeTab.TabItem.Header = _activeTab.Title;
            Title = string.IsNullOrWhiteSpace(_activeTab.Browser.Title)
                ? "Conjure Browser"
                : $"{_activeTab.Browser.Title} - Conjure Browser";
        });
    }

    private void Browser_AddressChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(async () =>
        {
            if (_activeTab is null) return;
            AddressBar.Text = _activeTab.Browser.Address ?? string.Empty;
            await UpdateBookmarkUiAsync();
        });
    }

    private void Browser_LoadingStateChanged(object? sender, LoadingStateChangedEventArgs e)
    {
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
        if (Tabs.SelectedItem is TabItem tabItem && tabItem.Tag is BrowserTab tab)
        {
            _activeTab = tab;
            AddressBar.Text = tab.Browser.Address ?? string.Empty;
            UpdateBookmarkUiAsync().ConfigureAwait(false);
            SyncUiToActiveTab();
        }
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddNewTab(HomeUrl);

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is BrowserTab tab)
        {
            Tabs.Items.Remove(tab.TabItem);
            _tabs.Remove(tab);

            if (Tabs.Items.Count == 0)
            {
                AddNewTab(HomeUrl);
                return;
            }

            Tabs.SelectedIndex = Math.Max(0, Tabs.Items.Count - 1);
        }
    }

    private void AddNewTab(string? initialUrl = null)
    {
        var browser = new ChromiumWebBrowser
        {
            Address = initialUrl ?? HomeUrl
        };

        var tabItem = new TabItem
        {
            Header = "New Tab",
            Content = browser
        };

        var tab = new BrowserTab
        {
            Browser = browser,
            TabItem = tabItem,
            Title = "New Tab",
            Model = GetSelectedModel(),
            ApiKey = ApiKeyBox.Text ?? string.Empty,
            AiVisible = false
        };

        tabItem.Tag = tab;
        tabItem.DataContext = tab;
        AttachBrowserEvents(tab);

        Tabs.Items.Add(tabItem);
        _tabs.Add(tab);
        Tabs.SelectedItem = tabItem;
        _activeTab = tab;

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
        if (tab is null) return;

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

    private async void AiAsk_Click(object? sender, RoutedEventArgs e)
    {
        var tab = _activeTab;
        if (tab is null) return;

        ApplyAiSettingsFromUi();

        var question = AiInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(question))
        {
            AppendMessage(tab, "system", "Type something, then press send.");
            return;
        }

        AppendMessage(tab, "user", question);
        AiInput.Clear();

        var pageText = await GetPageInnerTextAsync();
        AppendMessage(tab, "assistant", "Thinking...");

        var answer = await _ai.AnswerAsync(pageText, question);

        ReplaceLastAssistantMessage(tab, answer);
        RenderConversation(tab);
    }

    private async Task<string> GetPageInnerTextAsync()
    {
        var browser = _activeTab?.Browser;
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

        tab.ApiKey = ApiKeyBox.Text?.Trim() ?? string.Empty;
        tab.Model = GetSelectedModel();

        _ai.ApiKey = tab.ApiKey;
        _ai.Model = tab.Model;

        if (!string.IsNullOrWhiteSpace(tab.ApiKey))
            AddApiKeyToHistory(tab.ApiKey);
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

        ApiKeyBox.Text = tab.ApiKey;
        AiInput.Text = string.Empty;

        AiPanelToggle.IsChecked = tab.AiVisible;
        AiColumn.Width = tab.AiVisible ? new GridLength(360) : new GridLength(0);

        RenderConversation(tab);
    }

    // ---------- Conversation helpers ----------

    private void AppendMessage(BrowserTab tab, string role, string text)
    {
        tab.Conversation.Add((role, text));
        RenderConversation(tab);
    }

    private void ReplaceLastAssistantMessage(BrowserTab tab, string text)
    {
        for (var i = tab.Conversation.Count - 1; i >= 0; i--)
        {
            if (tab.Conversation[i].Role == "assistant")
            {
                tab.Conversation[i] = ("assistant", text);
                RenderConversation(tab);
                return;
            }
        }

        AppendMessage(tab, "assistant", text);
    }

    private void RenderConversation(BrowserTab tab)
    {
        var lines = tab.Conversation.Select(m => $"{m.Role}: {m.Text}");
        AiConversation.Text = string.Join("\n\n", lines);
    }

    // ---------- API key history ----------

    private void AddApiKeyToHistory(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (_apiKeyHistory.Contains(key)) return;

        _apiKeyHistory.Add(key);
        ApiKeyBox.Items.Clear();
        foreach (var k in _apiKeyHistory)
            ApiKeyBox.Items.Add(k);
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
