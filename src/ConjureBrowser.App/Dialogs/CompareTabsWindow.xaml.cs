using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace ConjureBrowser.App.Dialogs;

/// <summary>
/// Dialog for selecting tabs to compare.
/// </summary>
public partial class CompareTabsWindow : Window
{
    private readonly List<TabInfo> _tabs;
    private readonly List<CheckBox> _checkboxes = new();

    /// <summary>
    /// Gets the indices of selected tabs.
    /// </summary>
    public List<int> SelectedTabIndices { get; } = new();

    /// <summary>
    /// Gets the comparison question/prompt.
    /// </summary>
    public string CompareQuestion => CompareQuestionTextBox.Text?.Trim() ?? string.Empty;

    public CompareTabsWindow(List<TabInfo> tabs)
    {
        InitializeComponent();
        _tabs = tabs;
        PopulateTabList();
    }

    private void PopulateTabList()
    {
        TabCheckboxList.Children.Clear();
        _checkboxes.Clear();

        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var checkbox = new CheckBox
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = string.IsNullOrWhiteSpace(tab.Title) ? "Untitled" : tab.Title,
                            FontWeight = FontWeights.SemiBold,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        },
                        new TextBlock
                        {
                            Text = tab.Url,
                            FontSize = 11,
                            Foreground = System.Windows.SystemColors.GrayTextBrush,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    }
                },
                Tag = i,
                Margin = new Thickness(8, 6, 8, 6),
                IsEnabled = tab.HasBrowser // Only allow selecting tabs with actual browser content
            };

            checkbox.Checked += Checkbox_Changed;
            checkbox.Unchecked += Checkbox_Changed;

            _checkboxes.Add(checkbox);
            TabCheckboxList.Children.Add(checkbox);
        }

        UpdateSelectionCount();
    }

    private void Checkbox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSelectionCount();
    }

    private void UpdateSelectionCount()
    {
        var selectedCount = 0;
        foreach (var cb in _checkboxes)
        {
            if (cb.IsChecked == true)
                selectedCount++;
        }

        SelectionCountText.Text = $"{selectedCount} tab{(selectedCount == 1 ? "" : "s")} selected";
        CompareButton.IsEnabled = selectedCount >= 2;
    }

    private void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedTabIndices.Clear();
        foreach (var cb in _checkboxes)
        {
            if (cb.IsChecked == true && cb.Tag is int index)
            {
                SelectedTabIndices.Add(index);
            }
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

/// <summary>
/// Info about a tab for the compare dialog.
/// </summary>
public class TabInfo
{
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public bool HasBrowser { get; init; }
    public int Index { get; init; }
}
