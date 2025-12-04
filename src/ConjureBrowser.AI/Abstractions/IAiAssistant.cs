namespace ConjureBrowser.AI.Abstractions;

public interface IAiAssistant
{
    Task<string> SummarizeAsync(string pageText, CancellationToken ct = default);
    Task<string> AnswerAsync(string pageText, string question, CancellationToken ct = default);
}
