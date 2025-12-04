using System.Text;
using System.Text.RegularExpressions;
using ConjureBrowser.AI.Abstractions;

namespace ConjureBrowser.AI.Impl;

/// <summary>
/// Minimal non-LLM implementation so the app exposes an AI surface from day one.
/// Swap this for a real provider (OpenAI/local model) later.
/// </summary>
public sealed class SimpleAiAssistant : IAiAssistant
{
    public Task<string> SummarizeAsync(string pageText, CancellationToken ct = default)
    {
        var clean = Clean(pageText);
        if (string.IsNullOrWhiteSpace(clean))
            return Task.FromResult("No readable page text found.");

        var sentences = Regex.Split(clean, @"(?<=[\.!\?])\s+")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(6)
            .ToList();

        var joined = string.Join(" ", sentences);
        if (joined.Length > 900) joined = joined[..900] + "...";

        var sb = new StringBuilder();
        sb.AppendLine("Summary (stub):");
        sb.AppendLine();
        sb.AppendLine(joined);
        sb.AppendLine();
        sb.AppendLine("Note: replace SimpleAiAssistant with a real LLM-backed implementation.");
        return Task.FromResult(sb.ToString());
    }

    public Task<string> AnswerAsync(string pageText, string question, CancellationToken ct = default)
    {
        var clean = Clean(pageText);
        var q = (question ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(q)) return Task.FromResult("Type a question first.");
        if (string.IsNullOrWhiteSpace(clean)) return Task.FromResult("No readable page text found.");

        var terms = Regex.Matches(q.ToLowerInvariant(), @"[a-z0-9]{3,}")
            .Select(m => m.Value)
            .Distinct()
            .Take(8)
            .ToList();

        var lines = clean.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length >= 20)
            .ToList();

        var hits = lines
            .Where(l => terms.Any(t => l.ToLowerInvariant().Contains(t)))
            .Take(6)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Answer (stub):");
        sb.AppendLine();

        if (hits.Count == 0)
        {
            sb.AppendLine("No obvious matches found. Try rephrasing.");
        }
        else
        {
            sb.AppendLine("Most relevant snippets:");
            sb.AppendLine();
            foreach (var h in hits)
                sb.AppendLine("- " + (h.Length > 220 ? h[..220] + "..." : h));
        }

        sb.AppendLine();
        sb.AppendLine("Note: replace SimpleAiAssistant with a real LLM-backed implementation.");
        return Task.FromResult(sb.ToString());
    }

    private static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = text.Replace("\r\n", "\n");
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}
