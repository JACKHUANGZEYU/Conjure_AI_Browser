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

    public async Task<string> SummarizeAsync(string pageText, byte[]? screenshot = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            return "Gemini API key is missing. Enter it in the AI panel.";

        if (string.IsNullOrWhiteSpace(pageText))
            return "No readable page text found.";

        var prompt = """
                     Summarize the page clearly and concisely for a general audience.
                     Keep it under 6 bullet points.
                     If there are images or visual elements, describe them as well.
                     """;

        return await GenerateContentAsync(prompt, pageText, screenshot, ct).ConfigureAwait(false);
    }

    public async Task<string> AnswerAsync(string pageText, string question, IReadOnlyList<(string Role, string Text)> conversation, byte[]? screenshot = null, CancellationToken ct = default)
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

        // Parse context mode and selected text from the pageText wrapper
        var contextMode = "Auto";
        var selectedText = string.Empty;
        var actualPageText = pageText;

        if (pageText.StartsWith("CONTEXT_MODE:"))
        {
            var lines = pageText.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("CONTEXT_MODE:"))
                    contextMode = line.Substring("CONTEXT_MODE:".Length).Trim();
                else if (line.StartsWith("SELECTED_TEXT:"))
                {
                    // Find where selected text ends and page text begins
                    var idx = pageText.IndexOf("\nPAGE_TEXT:\n");
                    if (idx > 0)
                    {
                        var selStart = pageText.IndexOf("SELECTED_TEXT:\n") + "SELECTED_TEXT:\n".Length;
                        selectedText = pageText.Substring(selStart, idx - selStart).Trim();
                        actualPageText = pageText.Substring(idx + "\nPAGE_TEXT:\n".Length).Trim();
                    }
                    break;
                }
            }
        }

        // Build the prompt based on context mode
        string modeInstructions;
        switch (contextMode.ToUpperInvariant())
        {
            case "GENERAL":
                modeInstructions = """
                    You are a helpful AI assistant. Answer the user's question using your general knowledge.
                    Do NOT require or reference any specific page content - just answer helpfully like a normal AI assistant.
                    """;
                actualPageText = string.Empty; // Don't send page text in General mode
                break;

            case "PAGE":
                modeInstructions = """
                    You are an assistant that ONLY answers based on the provided page content.
                    If the answer is not found in the page text or visuals provided, say "I couldn't find this information on the current page."
                    Do not use external knowledge - only use what's in the provided page content.
                    """;
                break;

            default: // Auto
                modeInstructions = """
                    You are a helpful AI assistant. You have access to the current web page content as context.
                    - If the user's question is about the page content, use that context to answer.
                    - If the user's question is general or unrelated to the page, answer from your knowledge.
                    - Be helpful and don't refuse to answer just because the question isn't about the page.
                    """;
                break;
        }

        // Build context section
        var contextSection = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(selectedText))
        {
            contextSection.AppendLine("SELECTED TEXT (user highlighted this - prioritize this for context):");
            contextSection.AppendLine(selectedText);
            contextSection.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(actualPageText))
        {
            contextSection.AppendLine("PAGE CONTENT:");
            contextSection.AppendLine(actualPageText);
        }

        var prompt = $"""
                      {modeInstructions}
                      
                      Conversation so far:
                      {historyText}
                      
                      {(contextSection.Length > 0 ? contextSection.ToString() : "")}
                      
                      User's question:
                      {question}
                      """;

        // In General mode, don't send screenshot either
        var screenshotToSend = contextMode.Equals("General", StringComparison.OrdinalIgnoreCase) ? null : screenshot;

        return await GenerateContentAsync(prompt, string.Empty, screenshotToSend, ct).ConfigureAwait(false);
    }

    private async Task<string> GenerateContentAsync(string prompt, string pageText, byte[]? screenshot, CancellationToken ct)
    {
        var apiModel = ResolveApiModel(Model);
        const string apiVersion = "v1beta"; // v1beta supports the preview 3.0 model
        var endpoint = $"https://generativelanguage.googleapis.com/{apiVersion}/models/{apiModel}:generateContent?key={ApiKey}";

        // Limit payload size to avoid huge requests.
        var text = pageText.Length > 20000 ? pageText[..20000] + "\n...(truncated)..." : pageText;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        
        // Build parts list - text prompt + optional image
        var parts = new List<object>
        {
            new { text = $"{prompt}\n\nPage text:\n{text}" }
        };

        // Add screenshot if provided
        if (screenshot != null && screenshot.Length > 0)
        {
            var base64Image = Convert.ToBase64String(screenshot);
            parts.Add(new
            {
                inlineData = new
                {
                    mimeType = "image/png",
                    data = base64Image
                }
            });
        }

        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = parts.ToArray()
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
                content.TryGetProperty("parts", out var contentParts) &&
                contentParts.ValueKind == JsonValueKind.Array &&
                contentParts.GetArrayLength() > 0 &&
                contentParts[0].TryGetProperty("text", out var textProp))
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
