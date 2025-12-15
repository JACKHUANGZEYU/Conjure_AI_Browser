using System.Windows;
using System.Windows.Controls;
using ConjureBrowser.Core.Import;
using ConjureBrowser.Core.Models;
using ConjureBrowser.Core.Services;

namespace ConjureBrowser.App.Dialogs;

/// <summary>
/// Dialog window for importing data from Google Chrome.
/// </summary>
public partial class ImportChromeDataWindow : Window
{
    private readonly BookmarkStore _bookmarkStore;
    private readonly HistoryStore _historyStore;
    private readonly List<ChromeProfile> _profiles;
    private readonly ChromeDataImporter _importer = new();
    private bool _importCompleted;

    public ImportChromeDataWindow(BookmarkStore bookmarkStore, HistoryStore historyStore)
    {
        InitializeComponent();

        _bookmarkStore = bookmarkStore;
        _historyStore = historyStore;
        _profiles = ChromeProfileDetector.DetectProfiles();

        InitializeUI();
    }

    private void InitializeUI()
    {
        if (_profiles.Count == 0)
        {
            // No Chrome profiles found
            NoChromePanel.Visibility = Visibility.Visible;
            ProfileComboBox.Visibility = Visibility.Collapsed;
            ImportBookmarksCheckBox.IsEnabled = false;
            ImportHistoryCheckBox.IsEnabled = false;
            ImportPasswordsCheckBox.IsEnabled = false;
            ImportButton.IsEnabled = false;
            ChromePathText.Text = ChromeProfileDetector.ChromeUserDataPath;
            return;
        }

        // Populate profile dropdown
        ProfileComboBox.ItemsSource = _profiles;
        ProfileComboBox.DisplayMemberPath = "DisplayName";
        ProfileComboBox.SelectedIndex = 0;
    }

    private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileComboBox.SelectedItem is not ChromeProfile profile)
            return;

        UpdateProfileInfo(profile);
    }

    private void UpdateProfileInfo(ChromeProfile profile)
    {
        var info = new List<string>();

        if (profile.HasBookmarks)
            info.Add("✓ Bookmarks available");
        else
            info.Add("✗ No bookmarks found");

        if (profile.HasHistory)
            info.Add("✓ Browsing history available");
        else
            info.Add("✗ No history found");

        if (profile.HasPasswords)
            info.Add("вњ?Saved passwords available");
        else
            info.Add("вњ?No saved passwords found");

        ProfileInfoText.Text = string.Join("\n", info);

        // Update checkbox enabled state
        ImportBookmarksCheckBox.IsEnabled = profile.HasBookmarks;
        ImportBookmarksCheckBox.IsChecked = profile.HasBookmarks;
        ImportHistoryCheckBox.IsEnabled = profile.HasHistory;
        ImportHistoryCheckBox.IsChecked = profile.HasHistory;
        ImportPasswordsCheckBox.IsEnabled = profile.HasPasswords;
        ImportPasswordsCheckBox.IsChecked = profile.HasPasswords;
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileComboBox.SelectedItem is not ChromeProfile profile)
            return;

        var importBookmarks = ImportBookmarksCheckBox.IsChecked == true && profile.HasBookmarks;
        var importHistory = ImportHistoryCheckBox.IsChecked == true && profile.HasHistory;
        var importPasswords = ImportPasswordsCheckBox.IsChecked == true && profile.HasPasswords;

        if (!importBookmarks && !importHistory && !importPasswords)
        {
            MessageBox.Show("Please select at least one type of data to import.", 
                "Nothing Selected", 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);
            return;
        }

        // Disable UI during import
        ImportButton.IsEnabled = false;
        ProfileComboBox.IsEnabled = false;
        ImportBookmarksCheckBox.IsEnabled = false;
        ImportHistoryCheckBox.IsEnabled = false;
        ImportPasswordsCheckBox.IsEnabled = false;
        CloseButton.Content = "Cancel";

        // Show progress
        ProgressPanel.Visibility = Visibility.Visible;
        ResultsPanel.Visibility = Visibility.Collapsed;

        var stages = new List<string>();
        if (importBookmarks) stages.Add("bookmarks");
        if (importHistory) stages.Add("history");
        if (importPasswords) stages.Add("passwords");

        var progress = new Progress<double>(value =>
        {
            ProgressBar.Value = value * 100;
            if (stages.Count == 0) return;

            var idx = (int)Math.Floor(value * stages.Count);
            if (idx >= stages.Count) idx = stages.Count - 1;

            ProgressText.Text = stages[idx] switch
            {
                "bookmarks" => "Importing bookmarks...",
                "history" => "Importing history...",
                "passwords" => "Importing saved passwords...",
                _ => "Importing..."
            };
        });

        try
        {
            var result = await _importer.ImportAllAsync(
                profile,
                importBookmarks ? _bookmarkStore : null,
                importHistory ? _historyStore : null,
                importPasswords,
                progress);

            // Show results
            ProgressPanel.Visibility = Visibility.Collapsed;
            ResultsPanel.Visibility = Visibility.Visible;

            if (result.Success)
            {
                var resultLines = new List<string>();

                if (importBookmarks)
                {
                    resultLines.Add($"✓ Bookmarks: {result.BookmarksImported} imported, {result.BookmarksSkipped} skipped (duplicates)");
                }

                if (importHistory)
                {
                    resultLines.Add($"✓ History: {result.HistoryImported} imported, {result.HistorySkipped} skipped");
                }

                if (importPasswords)
                {
                    resultLines.Add($"вњ?Passwords: {result.PasswordsImported} imported, {result.PasswordsSkipped} skipped");
                }

                ResultsText.Text = string.Join("\n", resultLines);
                ResultsPanel.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x1B, 0x3B, 0x1B));
                ResultsText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x81, 0xC7, 0x84));

                _importCompleted = true;
                CloseButton.Content = "Done";
            }
            else
            {
                ResultsText.Text = $"Import failed: {result.ErrorMessage}";
                ResultsPanel.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3B, 0x1B, 0x1B));
                ResultsText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE5, 0x73, 0x73));
            }
        }
        catch (Exception ex)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ResultsPanel.Visibility = Visibility.Visible;
            ResultsText.Text = $"Error: {ex.Message}";
            ResultsPanel.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x3B, 0x1B, 0x1B));
            ResultsText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE5, 0x73, 0x73));
        }

        // Re-enable close button
        CloseButton.IsEnabled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = _importCompleted;
        Close();
    }
}
