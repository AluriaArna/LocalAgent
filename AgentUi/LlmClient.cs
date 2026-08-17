using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace AgentUi;

public class LlmParams
{
    public double Temperature { get; set; } = 0.8;
    public double TopP { get; set; } = 0.9;
    public double RepeatPenalty { get; set; } = 1.1;
    public int MaxTokens { get; set; } = 0;
    public int Seed { get; set; } = -1;
}

public enum LlmChunkKind { Content, Thinking }

public record LlmChunk(LlmChunkKind Kind, string Text);

public class ToolCall
{
    public string Name { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
}

public class StreamOutcome
{
    public List<ToolCall> ToolCalls { get; } = new();
}

public class LlmClient
{
    private static readonly HttpClient _http = new();

    // ===== Эмбеддинги =====

    public async Task<List<float[]>> EmbedAsync(string baseUrl, string model, IReadOnlyList<string> inputs, CancellationToken cancellation = default)
    {
        var url = baseUrl.TrimEnd('/') + "/api/embed";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model, input = inputs }), Encoding.UTF8, "application/json")
        };

        var response = await _http.SendAsync(request, cancellation);
        if (response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellation);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("embeddings", out var emb))
            {
                var result = new List<float[]>();
                foreach (var e in emb.EnumerateArray())
                {
                    var arr = new float[e.GetArrayLength()];
                    int i = 0;
                    foreach (var v in e.EnumerateArray()) arr[i++] = (float)v.GetDouble();
                    result.Add(arr);
                }
                return result;
            }
        }

        // Фолбэк: старый API /api/embeddings (по одному тексту)
        var list = new List<float[]>();
        foreach (var input in inputs)
        {
            var u2 = baseUrl.TrimEnd('/') + "/api/embeddings";
            var r2 = new HttpRequestMessage(HttpMethod.Post, u2)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model, prompt = input }), Encoding.UTF8, "application/json")
            };
            var resp2 = await _http.SendAsync(r2, cancellation);
            var t2 = await resp2.Content.ReadAsStringAsync(cancellation);
            if (!resp2.IsSuccessStatusCode)
                throw new Exception($"Embedding ошибка {(int)resp2.StatusCode}: {t2}");

            using var d2 = JsonDocument.Parse(t2);
            var e2 = d2.RootElement.GetProperty("embedding");
            var arr = new float[e2.GetArrayLength()];
            int i = 0;
            foreach (var v in e2.EnumerateArray()) arr[i++] = (float)v.GetDouble();
            list.Add(arr);
        }
        return list;
    }

    // ===== Чат без инструментов =====

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
        string baseUrl, string model, string? apiKey,
        IReadOnlyList<ChatMessage> messages, LlmParams? gen = null,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        await foreach (var chunk in AskStreamWithToolsAsync(baseUrl, model, apiKey, messages, gen, null, cancellation))
            yield return chunk;
    }

    // ===== Чат с инструментами (агентный цикл) =====

    public async IAsyncEnumerable<LlmChunk> AskStreamWithToolsAsync(
        string baseUrl, string model, string? apiKey,
        IReadOnlyList<ChatMessage> messages, LlmParams? gen, List<object>? tools,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        var outcome = new StreamOutcome();
        await foreach (var chunk in StreamCore(baseUrl, model, apiKey, messages, gen, tools, outcome, cancellation))
            yield return chunk;
        // tool_calls доступны через outcome после завершения — см. AskWithToolsAsync
    }

    /// <summary>Основной агентный стрим: chunks идут в onChunk, tool_calls возвращаются в outcome.</summary>
    public async Task<StreamOutcome> AskWithToolsAsync(
        string baseUrl, string model, string? apiKey,
        IReadOnlyList<ChatMessage> messages, LlmParams? gen, List<object>? tools,
        Action<LlmChunk> onChunk, CancellationToken cancellation = default)
    {
        var outcome = new StreamOutcome();
        await foreach (var chunk in StreamCore(baseUrl, model, apiKey, messages, gen, tools, outcome, cancellation))
            onChunk(chunk);
        return outcome;
    }

    private async IAsyncEnumerable<LlmChunk> StreamCore(
        string baseUrl, string model, string? apiKey,
        IReadOnlyList<ChatMessage> messages, LlmParams? gen, List<object>? tools,
        StreamOutcome outcome,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        var url = baseUrl.TrimEnd('/') + "/api/chat";
        var body = BuildBody(model, messages, true, gen);
        if (tools != null && tools.Count > 0) body["tools"] = tools;

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

        // Для OpenAI-совместимых: tool_calls приходят фрагментами
        var pendingCalls = new SortedDictionary<int, (string name, StringBuilder args)>();

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

                    if (delta.TryGetProperty("tool_calls", out var tcs))
                    {
                        foreach (var tc in tcs.EnumerateArray())
                        {
                            int idx = tc.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
                            if (!pendingCalls.TryGetValue(idx, out var slot))
                                slot = ("", new StringBuilder());

                            if (tc.TryGetProperty("function", out var fn))
                            {
                                if (fn.TryGetProperty("name", out var nm) && nm.GetString() is string n2 && n2.Length > 0)
                                    slot.name = n2;
                                if (fn.TryGetProperty("arguments", out var ar) && ar.GetString() is string a2)
                                    slot.args.Append(a2);
                            }
                            pendingCalls[idx] = slot;
                        }
                    }
                }
            }
            // Ollama-формат
            else if (root.TryGetProperty("message", out var msg))
            {
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

                if (msg.TryGetProperty("tool_calls", out var tcs))
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        if (!tc.TryGetProperty("function", out var fn)) continue;
                        var call = new ToolCall();
                        if (fn.TryGetProperty("name", out var nm)) call.Name = nm.GetString() ?? "";
                        if (fn.TryGetProperty("arguments", out var ar)) call.ArgumentsJson = ar.GetRawText();
                        outcome.ToolCalls.Add(call);
                    }
                }
            }
        }

        // Собрать фрагментированные tool_calls (OpenAI-формат)
        foreach (var (_, slot) in pendingCalls)
        {
            if (slot.name.Length == 0) continue;
            outcome.ToolCalls.Add(new ToolCall
            {
                Name = slot.name,
                ArgumentsJson = slot.args.Length > 0 ? slot.args.ToString() : "{}"
            });
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