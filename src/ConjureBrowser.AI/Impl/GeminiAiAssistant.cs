using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using ConjureBrowser.AI.Abstractions;

namespace ConjureBrowser.AI.Impl;

/// <summary>
/// Gemini client wrapper for basic summarize/answer flows.
/// This is intentionally lightweight; swap to a richer SDK or streaming client later.
/// </summary>
public sealed class GeminiAiAssistant : IAiAssistant
{
    private readonly HttpClient _httpClient;

    public string ApiKey { get; set; }
    public string Model { get; set; }

    public GeminiAiAssistant(HttpClient httpClient, string apiKey, string model)
    {
        _httpClient = httpClient;
        ApiKey = apiKey;
        Model = model;
    }

    public async Task<string> SummarizeAsync(string pageText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            return "Gemini API key is missing. Enter it in the AI panel.";

        if (string.IsNullOrWhiteSpace(pageText))
            return "No readable page text found.";

        var prompt = """
                     Summarize the page clearly and concisely for a general audience.
                     Keep it under 6 bullet points.
                     """;

        return await GenerateContentAsync(prompt, pageText, ct).ConfigureAwait(false);
    }

    public async Task<string> AnswerAsync(string pageText, string question, IReadOnlyList<(string Role, string Text)> conversation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            return "Gemini API key is missing. Enter it in the AI panel.";

        if (string.IsNullOrWhiteSpace(question))
            return "Type a question first.";

        var history = conversation
            .TakeLast(12)
            .Select(m => $"{m.Role}: {m.Text}")
            .ToArray();

        var historyText = string.Join("\n", history);

        var prompt = $"""
                      You are an assistant. Respect the conversation so far and use the current page text as your main source.
                      If the answer is not in the page text, say you are unsure.
                      
                      Conversation so far:
                      {historyText}
                      
                      Next user message:
                      {question}
                      """;

        return await GenerateContentAsync(prompt, pageText, ct).ConfigureAwait(false);
    }

    private async Task<string> GenerateContentAsync(string prompt, string pageText, CancellationToken ct)
    {
        var apiModel = ResolveApiModel(Model);
        const string apiVersion = "v1beta"; // v1beta supports the preview 3.0 model
        var endpoint = $"https://generativelanguage.googleapis.com/{apiVersion}/models/{apiModel}:generateContent?key={ApiKey}";

        // Limit payload size to avoid huge requests.
        var text = pageText.Length > 20000 ? pageText[..20000] + "\n...(truncated)..." : pageText;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = $"{prompt}\n\nPage text:\n{text}" }
                    }
                }
            }
        };

        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Network/HTTP error: {ex.Message}";
        }

        if (!response.IsSuccessStatusCode)
        {
            var errText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return $"Gemini error ({(int)response.StatusCode}): {errText}";
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        // Try to extract first candidate text.
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array &&
            candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            if (candidate.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array &&
                parts.GetArrayLength() > 0 &&
                parts[0].TryGetProperty("text", out var textProp))
            {
                return textProp.GetString() ?? "(No text returned)";
            }
        }

        return "No content returned from Gemini.";
    }

    private static string ResolveApiModel(string model)
    {
        var name = (model ?? string.Empty).Trim().ToLowerInvariant();
        return name switch
        {
            "gemini-3.0-pro" => "gemini-3-pro-preview",
            "gemini-3.0-pro-preview" => "gemini-3-pro-preview",
            "gemini-3-pro-preview" => "gemini-3-pro-preview",
            "gemini-2.5-flash" => "gemini-2.5-flash",
            _ => string.IsNullOrWhiteSpace(model) ? "gemini-2.5-flash" : model
        };
    }
}
