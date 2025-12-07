namespace ConjureBrowser.AI.Abstractions;

public interface IAiAssistant
{
    Task<string> SummarizeAsync(string pageText, CancellationToken ct = default);
    Task<string> AnswerAsync(string pageText, string question, IReadOnlyList<(string Role, string Text)> conversation, CancellationToken ct = default);
}
