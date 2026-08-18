using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentUi;

public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}

public class Diary
{
    public string Summary { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
}

public static class DiaryStore
{
    private static readonly Regex NameRegex = new(@"^(\d+)\.md$", RegexOptions.Compiled);
    private const string LegacyName = "diary.md";

    // "Почти рандомное" имя = unix-таймстамп.
    // Если файл уже есть (создали два дневника в одну секунду) — берём следующую секунду,
    // поэтому в папке появляются серии вида ...927, ...928, ...929.
    public static string NewFileName(string folder)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string name;
        do
        {
            name = $"{ts}.md";
            ts++;
        } while (File.Exists(Path.Combine(folder, name)));
        return name;
    }

    // Свежий дневник в папке (максимальный таймстамп).
    // Заодно одноразово мигрирует старый diary.md, если он остался.
    public static string? FindLatest(string folder)
{
    if (!Directory.Exists(folder)) return null;

    var latest = Directory.GetFiles(folder, "*.md")
        .Select(f => Path.GetFileName(f))
        .Where(n => n != null && NameRegex.IsMatch(n))
        .OrderByDescending(n => long.Parse(NameRegex.Match(n!).Groups[1].Value))
        .FirstOrDefault();

    if (latest != null) return latest;

    var legacy = Path.Combine(folder, LegacyName);
    if (File.Exists(legacy))
    {
        var migrated = NewFileName(folder);
        File.Move(legacy, Path.Combine(folder, migrated));
        return migrated;
    }

    return null;
}

    public static Diary Load(string folder, string fileName)
    {
        var diary = new Diary();
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return diary;

        var text = File.ReadAllText(path);

        var summaryMatch = Regex.Match(text, @"# Резюме\s*(.*?)\s*# История", RegexOptions.Singleline);
        if (summaryMatch.Success)
            diary.Summary = summaryMatch.Groups[1].Value.Trim();

        var historyMatch = Regex.Match(text, @"# История\s*(.*)", RegexOptions.Singleline);
        if (historyMatch.Success)
        {
            var entries = Regex.Split(
                historyMatch.Groups[1].Value,
                @"(?m)^\*\*(Ты|Агент):\*\*\s*");

            for (int i = 1; i < entries.Length - 1; i += 2)
            {
                var role = entries[i] == "Ты" ? "user" : "assistant";
                var content = entries[i + 1].Trim();
                if (content.Length > 0)
                    diary.Messages.Add(new ChatMessage { Role = role, Content = content });
            }
        }
        return diary;
    }

    public static void Save(string folder, string fileName, Diary diary)
    {
        Directory.CreateDirectory(folder);
        var sb = new StringBuilder();
        sb.AppendLine("# Резюме");
        sb.AppendLine(diary.Summary);
        sb.AppendLine();
        sb.AppendLine("# История");
        foreach (var m in diary.Messages)
        {
            var label = m.Role == "user" ? "Ты" : "Агент";
            sb.AppendLine($"**{label}:** {m.Content}");
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(folder, fileName), sb.ToString(), Encoding.UTF8);
    }
    
}
public static class MemoryStore
{
    public static string FilePath(string folder) => Path.Combine(folder, "working_memory.md");

    public static string Load(string folder)
    {
        var path = FilePath(folder);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
    }

    public static void Save(string folder, string content)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath(folder), content, Encoding.UTF8);
    }
}

public static class ReflectionStore
{
    // Оставляем только таймстамп. 
    // Добавлена защита от перезаписи основного дневника, если сохранение произошло в ту же секунду.
    public static string FilePath(string folder, long timestamp)
    {
        string fileName;
        do
        {
            fileName = $"{timestamp}.md";
            timestamp++;
        } while (File.Exists(Path.Combine(folder, fileName)));
        
        return Path.Combine(folder, fileName);
        }

    public static List<(string path, string content)> LoadAll(string folder)
    {
        if (!Directory.Exists(folder)) return new();

        return Directory.GetFiles(folder, "*.md")
            .Where(f => Path.GetFileName(f) != "working_memory.md")
            .Select(f => (path: f, content: File.ReadAllText(f)))
            // КРИТИЧЕСКИЙ ФИЛЬТР: пропускаем основные дневники, чтобы не загрузить их в промпт!
            // Основные дневники содержат "# История", а сводки дневника — нет.
            .Where(r => !string.IsNullOrWhiteSpace(r.content) && !r.content.Contains("# История"))
            .ToList();
    }

    public static void Save(string folder, string content)
    {
        Directory.CreateDirectory(folder);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        File.WriteAllText(FilePath(folder, timestamp), content, Encoding.UTF8);
    }
}