using System.Text;
using System.Text.Json;

namespace AgentUi;

public class RagChunk
{
    public string File { get; set; } = "";
    public string Source { get; set; } = "";
    public string Text { get; set; } = "";
    public float[] Vector { get; set; } = Array.Empty<float>();
}

public class RagFileEntry
{
    public string Path { get; set; } = "";
    public string Source { get; set; } = "";
    public long LastWrite { get; set; }
    public List<RagChunk> Chunks { get; set; } = new();
}

public class RagIndex
{
    public string EmbedModel { get; set; } = "";
    public int ChunkSize { get; set; }
    public List<RagFileEntry> Files { get; set; } = new();

    public List<RagChunk> AllChunks() => Files.SelectMany(f => f.Chunks).ToList();
}

public static class RagStore
{
    public static string IndexPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AgentUi", "rag_index.json");

    public static RagIndex? Load()
    {
        try
        {
            var p = IndexPath();
            if (File.Exists(p))
                return JsonSerializer.Deserialize<RagIndex>(File.ReadAllText(p));
        }
        catch { }
        return null;
    }

    // ===== Построение индекса (инкрементальное) =====

    public static async Task<int> BuildAsync(
        LlmClient client, string baseUrl, string? apiKey,
        List<(string path, string source)> sources,
        string embedModel, int chunkSize,
        RagIndex? existing,
        Action<string>? log = null)
    {
        var old = existing != null && existing.EmbedModel == embedModel && existing.ChunkSize == chunkSize
            ? existing.Files.ToDictionary(f => f.Path, f => f)
            : new Dictionary<string, RagFileEntry>();

        var newIndex = new RagIndex { EmbedModel = embedModel, ChunkSize = chunkSize };
        int embedded = 0;

        foreach (var (folder, source) in sources)
        {
            var files = Directory.GetFiles(folder, "*.md")
                .Concat(Directory.GetFiles(folder, "*.txt"))
                .OrderBy(f => f)
                .ToList();

            foreach (var file in files)
            {
                var lw = File.GetLastWriteTimeUtc(file).Ticks;

                // файл не менялся — переиспользуем старые вектора
                if (old.TryGetValue(file, out var oldEntry) && oldEntry.LastWrite == lw)
                {
                    newIndex.Files.Add(oldEntry);
                    continue;
                }

                var text = File.ReadAllText(file);
                var chunks = Split(text, chunkSize)
                    .Select(piece => new RagChunk { File = Path.GetFileName(file), Source = source, Text = piece })
                    .ToList();

                for (int i = 0; i < chunks.Count; i += 16)
                {
                    var batch = chunks.Skip(i).Take(16).ToList();
                    var vectors = await client.EmbedAsync(baseUrl, embedModel, batch.Select(c => c.Text).ToList());
                    for (int j = 0; j < batch.Count && j < vectors.Count; j++)
                        batch[j].Vector = vectors[j];
                    embedded += batch.Count;
                    log?.Invoke($"{source}: {Path.GetFileName(file)} — фрагментов проэмбеддено: {embedded}\n");
                }

                newIndex.Files.Add(new RagFileEntry { Path = file, Source = source, LastWrite = lw, Chunks = chunks });
            }
        }

        File.WriteAllText(IndexPath(), JsonSerializer.Serialize(newIndex));
        return newIndex.AllChunks().Count;
    }

    private static IEnumerable<string> Split(string text, int chunkSize)
    {
        var paragraphs = text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();

        foreach (var p in paragraphs)
        {
            if (p.Length > chunkSize)
            {
                if (sb.ToString().Trim().Length > 0) yield return sb.ToString().Trim();
                sb.Clear();
                for (int i = 0; i < p.Length; i += chunkSize)
                    yield return p.Substring(i, Math.Min(chunkSize, p.Length - i));
                continue;
            }

            if (sb.Length + p.Length > chunkSize && sb.Length > 0)
            {
                yield return sb.ToString().Trim();
                sb.Clear();
            }

            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(p);
        }

        if (sb.ToString().Trim().Length > 0)
            yield return sb.ToString().Trim();
    }

    // ===== Поиск =====

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na > 0 && nb > 0 ? dot / (Math.Sqrt(na) * Math.Sqrt(nb)) : 0;
    }

    /// <summary>Инструмент #ask: проверка, есть ли в базе что-то по теме.</summary>
    public static async Task<string> ProbeAsync(
        LlmClient client, string baseUrl, string? apiKey,
        RagIndex index, string question, double threshold)
    {
        var chunks = index.AllChunks();
        if (chunks.Count == 0)
            return "База знаний пуста. Индекс не построен.";

        var vec = (await client.EmbedAsync(baseUrl, index.EmbedModel, new[] { question }))[0];
        var score = chunks.Max(c => Cosine(vec, c.Vector));

        return score >= threshold
            ? $"Есть релевантная информация (релевантность {score:F2} при пороге {threshold:F2}). Имеет смысл вызвать #query."
            : $"Релевантной информации нет (макс. релевантность {score:F2} при пороге {threshold:F2}). Отвечай своими знаниями.";
    }

    /// <summary>Инструмент #query: вернуть топ-K фрагментов.</summary>
    public static async Task<string> QueryAsync(
        LlmClient client, string baseUrl, string? apiKey,
        RagIndex index, string query, int topK, double threshold)
    {
        var chunks = index.AllChunks();
        if (chunks.Count == 0)
            return "База знаний пуста. Индекс не построен.";

        var vec = (await client.EmbedAsync(baseUrl, index.EmbedModel, new[] { query }))[0];

        var top = chunks
            .Select(c => (chunk: c, score: Cosine(vec, c.Vector)))
            .Where(x => x.score >= threshold)
            .OrderByDescending(x => x.score)
            .Take(topK)
            .ToList();

        if (top.Count == 0)
            return "Ничего релевантного не найдено.";

        var sb = new StringBuilder();
        foreach (var (chunk, score) in top)
            sb.AppendLine($"[{chunk.Source} / {chunk.File}] (релевантность {score:F2})\n{chunk.Text}\n---");
        return sb.ToString();
    }

    // ===== Схемы инструментов для Ollama =====

    public static List<object> Tools() => new()
    {
        new
        {
            type = "function",
            function = new
            {
                name = "ask",
                description = "Проверь, есть ли в базе знаний, дневниках или рабочей памяти релевантная информация по теме, прежде чем делать полный запрос. Вернёт вердикт и оценку релевантности.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        question = new { type = "string", description = "Вопрос или тема для проверки" }
                    },
                    required = new[] { "question" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "query",
                description = "Полный запрос к базе знаний, дневникам и рабочей памяти: возвращает самые релевантные фрагменты с указанием источника. Используй, когда #ask подтвердил релевантность или ты уверен, что нужные факты записаны.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "Поисковый запрос" },
                        top_k = new { type = "integer", description = "Сколько фрагментов вернуть (по умолчанию 5)" }
                    },
                    required = new[] { "query" }
                }
            }
        }
    };
}