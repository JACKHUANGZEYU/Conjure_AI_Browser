using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CefSharp;
using CefSharp.Wpf;
using ConjureBrowser.AI.Abstractions;
using ConjureBrowser.AI.Impl;
using ConjureBrowser.Core.Services;
using ConjureBrowser.Core.Utils;

namespace ConjureBrowser.App;

public partial class MainWindow : Window
{
    private readonly BookmarkStore _bookmarks = new();
    private readonly IAiAssistant _ai = new SimpleAiAssistant();

    private const string HomeUrl = "https://www.google.com";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _bookmarks.LoadAsync();

        Browser.TitleChanged += Browser_TitleChanged;
        Browser.AddressChanged += Browser_AddressChanged;
        Browser.LoadingStateChanged += Browser_LoadingStateChanged;

        Navigate(HomeUrl);
        UpdateAiPanelVisibility();
    }

    private void Browser_TitleChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var title = Browser.Title;
            Title = string.IsNullOrWhiteSpace(title) ? "Conjure Browser" : $"{title} - Conjure Browser";
        });
    }

    private void Browser_AddressChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(async () =>
        {
            AddressBar.Text = Browser.Address ?? string.Empty;
            await UpdateBookmarkUiAsync();
        });
    }

    private void Browser_LoadingStateChanged(object? sender, LoadingStateChangedEventArgs e)
    {
        // CefSharp fires off UI thread.
        Dispatcher.Invoke(() =>
        {
            BackButton.IsEnabled = e.CanGoBack;
            ForwardButton.IsEnabled = e.CanGoForward;
            ReloadButton.IsEnabled = true;
        });
    }

    private void Navigate(string rawInput)
    {
        var normalized = UrlHelpers.NormalizeUrl(rawInput);

        // If the input is not a URL, treat it as a search query.
        if (normalized == null)
        {
            if (string.IsNullOrWhiteSpace(rawInput)) return;
            var q = Uri.EscapeDataString(rawInput.Trim());
            normalized = $"https://www.google.com/search?q={q}";
        }

        Browser.Address = normalized;
    }

    private async Task UpdateBookmarkUiAsync()
    {
        var url = Browser.Address;
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
        if (Browser.CanGoBack) Browser.Back();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoForward) Browser.Forward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        Browser.Reload();
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

    private async void BookmarkButton_Click(object sender, RoutedEventArgs e)
    {
        var url = Browser.Address;
        if (string.IsNullOrWhiteSpace(url)) return;

        var title = Browser.Title;
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
        var show = AiPanelToggle.IsChecked == true;
        AiColumn.Width = show ? new GridLength(360) : new GridLength(0);
    }

    private void AiClear_Click(object sender, RoutedEventArgs e)
    {
        AiOutput.Text = string.Empty;
        AiQuestion.Text = string.Empty;
    }

    private async void Summarize_Click(object sender, RoutedEventArgs e)
    {
        AiOutput.Text = "Reading page text...";

        var pageText = await GetPageInnerTextAsync();
        AiOutput.Text = await _ai.SummarizeAsync(pageText);
    }

    private void AiQuestion_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AiAsk_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void AiAsk_Click(object sender, RoutedEventArgs e)
    {
        var question = AiQuestion.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(question))
        {
            AiOutput.Text = "Type a question, then press Ask.";
            return;
        }

        AiOutput.Text = "Reading page text...";
        var pageText = await GetPageInnerTextAsync();

        AiOutput.Text = "Thinking...";
        AiOutput.Text = await _ai.AnswerAsync(pageText, question);
    }

    private async Task<string> GetPageInnerTextAsync()
    {
        if (!Browser.IsBrowserInitialized) return string.Empty;

        var response = await Browser.EvaluateScriptAsync("document.body ? document.body.innerText : '';");
        if (!response.Success || response.Result is null) return string.Empty;

        var text = response.Result.ToString() ?? string.Empty;
        if (text.Length > 12000)
            text = text[..12000] + "\n...(truncated)...";

        return text;
    }
}
