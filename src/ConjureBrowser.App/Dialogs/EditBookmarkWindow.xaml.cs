using System.Windows;
using ConjureBrowser.Core.Utils;

namespace ConjureBrowser.App.Dialogs;

public partial class EditBookmarkWindow : Window
{
    public string BookmarkTitle { get; private set; } = string.Empty;
    public string BookmarkUrl { get; private set; } = string.Empty;

    public EditBookmarkWindow(string initialTitle, string initialUrl)
    {
        InitializeComponent();
        
        TitleTextBox.Text = initialTitle;
        UrlTextBox.Text = initialUrl;
        
        BookmarkTitle = initialTitle;
        BookmarkUrl = initialUrl;
        
        TitleTextBox.Focus();
        TitleTextBox.SelectAll();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleTextBox.Text?.Trim() ?? string.Empty;
        var url = UrlTextBox.Text?.Trim() ?? string.Empty;

        // Validate title
        if (string.IsNullOrWhiteSpace(title))
        {
            ErrorText.Text = "Title cannot be empty.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Validate URL
        var normalizedUrl = UrlHelpers.NormalizeUrl(url);
        if (normalizedUrl == null)
        {
            ErrorText.Text = "Invalid URL. Please enter a valid web address.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Success - set properties and close
        BookmarkTitle = title;
        BookmarkUrl = normalizedUrl;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
