using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ConjureBrowser.App.Models;

public class DownloadRecord : INotifyPropertyChanged
{
    public Guid Id { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }

    private DateTimeOffset? _completedAt;
    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set
        {
            _completedAt = value;
            OnPropertyChanged();
        }
    }

    private long _bytesReceived;
    public long BytesReceived
    {
        get => _bytesReceived;
        set
        {
            _bytesReceived = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressPercent));
        }
    }

    private long? _totalBytes;
    public long? TotalBytes
    {
        get => _totalBytes;
        set
        {
            _totalBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressPercent));
        }
    }

    private string _status = "Queued";
    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
        }
    }

    public int ProgressPercent =>
        TotalBytes > 0 ? (int)((BytesReceived * 100) / TotalBytes.Value) : 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
