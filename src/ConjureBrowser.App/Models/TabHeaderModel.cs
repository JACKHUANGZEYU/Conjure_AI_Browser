using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ConjureBrowser.App.Models;

public class TabHeaderModel : INotifyPropertyChanged
{
    private string _title = "New Tab";
    private string _url = string.Empty;
    private ImageSource? _favicon;
    private bool _isLoading;

    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                OnPropertyChanged();
            }
        }
    }

    public string Url
    {
        get => _url;
        set
        {
            if (_url != value)
            {
                _url = value;
                OnPropertyChanged();
            }
        }
    }

    public ImageSource? Favicon
    {
        get => _favicon;
        set
        {
            if (_favicon != value)
            {
                _favicon = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
