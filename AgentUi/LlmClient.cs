using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace AgentUi;

public class LlmClient
{
    private static readonly HttpClient _http = new();

    public async Task<string> AskAsync(string baseUrl, string model, string? apiKey, string userMessage)
        => await AskAsync(baseUrl, model, apiKey,
            new List<ChatMessage> { new() { Role = "user", Content = userMessage } });

    public async Task<string> AskAsync(string baseUrl, string model, string? apiKey, IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        await foreach (var token in AskStreamAsync(baseUrl, model, apiKey, messages))
        {
            sb.Append(token);
        }
        return sb.ToString();
    }

    public async IAsyncEnumerable<string> AskStreamAsync(
    string baseUrl,
    string model,
    string? apiKey,
    IReadOnlyList<ChatMessage> messages,
    [EnumeratorCancellation] CancellationToken cancellation = default)
{
    var url = baseUrl.TrimEnd('/') + "/api/chat";

    var body = new
    {
        model,
        messages = messages.Select(m => new { role = m.Role, content = m.Content }),
        stream = true
    };

    var request = new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    if (!string.IsNullOrWhiteSpace(apiKey))
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);

    if (!response.IsSuccessStatusCode)
    {
        var error = await response.Content.ReadAsStringAsync(cancellation);
        throw new Exception($"Ошибка {(int)response.StatusCode}: {error}");
    }

    using var stream = await response.Content.ReadAsStreamAsync(cancellation);
    using var reader = new StreamReader(stream);

    while (!cancellation.IsCancellationRequested)
{
    var line = await reader.ReadLineAsync(cancellation);
    if (line == null) break;
    if (string.IsNullOrWhiteSpace(line)) continue;

        var trimmed = line.Trim();
        string json;

        if (trimmed.StartsWith("data:"))
        {
            // OpenAI-совместимые серверы (SSE)
            json = trimmed[5..].Trim();
            if (json == "[DONE]") break;
        }
        else if (trimmed.StartsWith("{"))
        {
            // Ollama (NDJSON) — голые JSON-строки без префикса
            json = trimmed;
        }
        else
        {
            continue;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? content = null;

        // OpenAI-формат
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            if (choices[0].TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var c))
            {
                content = c.GetString();
            }
        }
        // Ollama-формат
        else if (root.TryGetProperty("message", out var msg))
        {
            if (msg.TryGetProperty("content", out var c))
                content = c.GetString();
        }

        if (!string.IsNullOrEmpty(content))
            yield return content;
    }
}
public async Task<string> AskOnceAsync(string baseUrl, string model, string? apiKey, string userMessage)
{
    var url = baseUrl.TrimEnd('/') + "/api/chat";

    var body = new
    {
        model,
        messages = new[] { new { role = "user", content = userMessage } },
        stream = false
    };

    var request = new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    if (!string.IsNullOrWhiteSpace(apiKey))
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    var response = await _http.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
        throw new Exception($"Ошибка {(int)response.StatusCode}: {text}");

    using var doc = JsonDocument.Parse(text);
    var root = doc.RootElement;

    if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        return choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";

    if (root.TryGetProperty("message", out var msg))
        return msg.GetProperty("content").GetString() ?? "";

    return text;
}
}