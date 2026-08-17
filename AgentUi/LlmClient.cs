using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace AgentUi;

/// <summary>Параметры генерации, передаваемые серверу.</summary>
public class LlmParams
{
    public double Temperature { get; set; } = 0.8;
    public double TopP { get; set; } = 0.9;
    public double RepeatPenalty { get; set; } = 1.1;
    public int MaxTokens { get; set; } = 0;   // 0 — без лимита
    public int Seed { get; set; } = -1;       // -1 — случайно
}

public enum LlmChunkKind { Content, Thinking }

public record LlmChunk(LlmChunkKind Kind, string Text);

public class LlmClient
{
    private static readonly HttpClient _http = new();

    public async Task<string> AskAsync(string baseUrl, string model, string? apiKey, string userMessage, LlmParams? gen = null)
        => await AskAsync(baseUrl, model, apiKey,
            new List<ChatMessage> { new() { Role = "user", Content = userMessage } }, gen);

    public async Task<string> AskAsync(string baseUrl, string model, string? apiKey, IReadOnlyList<ChatMessage> messages, LlmParams? gen = null)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in AskStreamAsync(baseUrl, model, apiKey, messages, gen))
        {
            if (chunk.Kind == LlmChunkKind.Content)
                sb.Append(chunk.Text);
        }
        return sb.ToString();
    }

    public async IAsyncEnumerable<LlmChunk> AskStreamAsync(
        string baseUrl,
        string model,
        string? apiKey,
        IReadOnlyList<ChatMessage> messages,
        LlmParams? gen = null,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        var url = baseUrl.TrimEnd('/') + "/api/chat";
        var body = BuildBody(model, messages, true, gen);

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
                json = trimmed[5..].Trim();
                if (json == "[DONE]") break;
            }
            else if (trimmed.StartsWith("{"))
            {
                json = trimmed;
            }
            else
            {
                continue;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // OpenAI-формат
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                if (choices[0].TryGetProperty("delta", out var delta))
                {
                    // reasoning у OpenAI-совместимых серверов
                    if (delta.TryGetProperty("reasoning_content", out var rc))
                    {
                        var rt = rc.GetString();
                        if (!string.IsNullOrEmpty(rt))
                            yield return new LlmChunk(LlmChunkKind.Thinking, rt);
                    }

                    if (delta.TryGetProperty("content", out var c))
                    {
                        var ct = c.GetString();
                        if (!string.IsNullOrEmpty(ct))
                            yield return new LlmChunk(LlmChunkKind.Content, ct);
                    }
                }
            }
            // Ollama-формат
            else if (root.TryGetProperty("message", out var msg))
            {
                // нативное мышление Ollama
                if (msg.TryGetProperty("thinking", out var t))
                {
                    var tt = t.GetString();
                    if (!string.IsNullOrEmpty(tt))
                        yield return new LlmChunk(LlmChunkKind.Thinking, tt);
                }

                if (msg.TryGetProperty("content", out var c))
                {
                    var ct = c.GetString();
                    if (!string.IsNullOrEmpty(ct))
                        yield return new LlmChunk(LlmChunkKind.Content, ct);
                }
            }
        }
    }

    public async Task<string> AskOnceAsync(string baseUrl, string model, string? apiKey, string userMessage, LlmParams? gen = null)
    {
        var url = baseUrl.TrimEnd('/') + "/api/chat";
        var body = BuildBody(model, new[] { new ChatMessage { Role = "user", Content = userMessage } }, false, gen);

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

    /// <summary>Собирает тело запроса: options — для Ollama, верхний уровень — для OpenAI-совместимых.</summary>
    private static Dictionary<string, object> BuildBody(string model, IReadOnlyList<ChatMessage> messages, bool stream, LlmParams? gen)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToList(),
            ["stream"] = stream
        };

        if (gen == null) return body;

        var options = new Dictionary<string, object>
        {
            ["temperature"] = gen.Temperature,
            ["top_p"] = gen.TopP,
            ["repeat_penalty"] = gen.RepeatPenalty
        };
        if (gen.MaxTokens > 0) options["num_predict"] = gen.MaxTokens;
        if (gen.Seed >= 0) options["seed"] = gen.Seed;
        body["options"] = options;

        body["temperature"] = gen.Temperature;
        body["top_p"] = gen.TopP;
        if (gen.MaxTokens > 0) body["max_tokens"] = gen.MaxTokens;
        if (gen.Seed >= 0) body["seed"] = gen.Seed;

        return body;
    }
}