using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CefSharp;
using ConjureBrowser.App.Models;

namespace ConjureBrowser.App.Services;

public class DownloadManager : IDownloadHandler
{
    private readonly ObservableCollection<DownloadRecord> _downloads = new();
    private readonly Dictionary<int, DownloadRecord> _activeDownloads = new();
    private readonly Dictionary<Guid, int> _recordToDownloadId = new();
    private readonly Dictionary<int, IDownloadItemCallback> _downloadCallbacks = new();
    private readonly string _downloadFolder;
    private readonly object _sync = new();

    public ObservableCollection<DownloadRecord> Downloads => _downloads;

    public DownloadManager()
    {
        _downloadFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        // Ensure downloads folder exists
        Directory.CreateDirectory(_downloadFolder);
    }

    public bool OnBeforeDownload(IWebBrowser chromiumWebBrowser, IBrowser browser,
        DownloadItem downloadItem, IBeforeDownloadCallback callback)
    {
        try
        {
            var fileName = string.IsNullOrWhiteSpace(downloadItem.SuggestedFileName)
                ? "download"
                : downloadItem.SuggestedFileName;

            var fullPath = GetUniqueFilePath(_downloadFolder, fileName);

            var record = new DownloadRecord
            {
                Id = Guid.NewGuid(),
                FileName = Path.GetFileName(fullPath),
                Url = downloadItem.Url,
                FullPath = fullPath,
                StartedAt = DateTimeOffset.Now,
                TotalBytes = downloadItem.TotalBytes > 0 ? downloadItem.TotalBytes : null,
                Status = "Queued"
            };

            lock (_sync)
            {
                _activeDownloads[downloadItem.Id] = record;
                _recordToDownloadId[record.Id] = downloadItem.Id;
            }

            // Add to UI collection on UI thread
            Application.Current?.Dispatcher.InvokeAsync(() => _downloads.Insert(0, record));

            using var cb = callback;
            if (!cb.IsDisposed)
            {
                cb.Continue(fullPath, showDialog: false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Download setup failed: {ex}");
        }

        return true;
    }

    public void OnDownloadUpdated(IWebBrowser chromiumWebBrowser, IBrowser browser,
        DownloadItem downloadItem, IDownloadItemCallback callback)
    {
        // This runs on a background thread - use Dispatcher for UI updates
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                DownloadRecord? record;
                lock (_sync)
                {
                    _downloadCallbacks[downloadItem.Id] = callback;
                    _activeDownloads.TryGetValue(downloadItem.Id, out record);
                }

                if (record == null) return;

                if (callback.IsDisposed) return;

                // Update progress
                record.BytesReceived = downloadItem.ReceivedBytes;

                if (downloadItem.TotalBytes > 0)
                    record.TotalBytes = downloadItem.TotalBytes;

                // Update status based on download state
                if (downloadItem.IsCancelled)
                {
                    record.Status = "Canceled";
                    record.CompletedAt = DateTimeOffset.Now;
                    lock (_sync)
                    {
                        _activeDownloads.Remove(downloadItem.Id);
                        _recordToDownloadId.Remove(record.Id);
                        _downloadCallbacks.Remove(downloadItem.Id);
                    }
                }
                else if (downloadItem.IsComplete)
                {
                    var failed = downloadItem.TotalBytes > 0 &&
                                 downloadItem.ReceivedBytes < downloadItem.TotalBytes;
                    record.Status = failed ? "Failed" : "Completed";
                    record.CompletedAt = DateTimeOffset.Now;
                    lock (_sync)
                    {
                        _activeDownloads.Remove(downloadItem.Id);
                        _recordToDownloadId.Remove(record.Id);
                        _downloadCallbacks.Remove(downloadItem.Id);
                    }
                }
                else if (downloadItem.IsInProgress)
                {
                    record.Status = "InProgress";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Download update failed: {ex}");
            }
        });
    }

    public bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser,
        string url, string requestMethod)
    {
        // Allow all downloads
        return true;
    }

    public void OnDownloadUpdatedFired(IWebBrowser chromiumWebBrowser, IBrowser browser,
        DownloadItem downloadItem)
    {
        // This is called after OnDownloadUpdated - we don't need additional handling
    }

    private string GetUniqueFilePath(string directory, string fileName)
    {
        var fullPath = Path.Combine(directory, fileName);
        if (!File.Exists(fullPath))
            return fullPath;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;

        while (File.Exists(fullPath))
        {
            var newName = $"{nameWithoutExt} ({counter}){extension}";
            fullPath = Path.Combine(directory, newName);
            counter++;
        }

        return fullPath;
    }

    public void CancelDownload(Guid recordId)
    {
        if (!_recordToDownloadId.TryGetValue(recordId, out var downloadId))
            return;

        if (_activeDownloads.TryGetValue(downloadId, out var record))
        {
            record.Status = "Canceled";
            record.CompletedAt = DateTimeOffset.Now;
            lock (_sync)
            {
                _activeDownloads.Remove(downloadId);
                _recordToDownloadId.Remove(record.Id);
            }
        }

        if (_downloadCallbacks.TryGetValue(downloadId, out var callback))
        {
            try
            {
                if (!callback.IsDisposed)
                {
                    callback.Cancel();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Cancel download failed: {ex}");
            }
            finally
            {
                lock (_sync)
                {
                    _downloadCallbacks.Remove(downloadId);
                }
            }
        }
    }

    public void RemoveDownload(DownloadRecord record)
    {
        lock (_sync)
        {
            if (_recordToDownloadId.TryGetValue(record.Id, out var downloadId))
            {
                _recordToDownloadId.Remove(record.Id);
                _activeDownloads.Remove(downloadId);
                _downloadCallbacks.Remove(downloadId);
            }
        }

        _downloads.Remove(record);
    }
}
