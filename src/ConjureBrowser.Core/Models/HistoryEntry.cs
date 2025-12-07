namespace ConjureBrowser.Core.Models;

public sealed record HistoryEntry(string Title, string Url, DateTimeOffset VisitedAt);
