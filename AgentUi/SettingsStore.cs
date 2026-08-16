using System.IO;
using System.Text.Json;

namespace AgentUi;

public class AppSettings
{
    public string Name { get; set; } = "Профиль";
    public string Url { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public class LlmSettings
{
    public string Name { get; set; } = "Профиль 1";
    public double Temperature { get; set; } = 0.8;
    public double TopP { get; set; } = 0.9;
    public double RepeatPenalty { get; set; } = 1.1;
    public int MaxTokens { get; set; } = 0;   // 0 — без лимита
    public int Seed { get; set; } = -1;       // -1 — случайно
}

public class SettingsData
{
    public List<AppSettings> Profiles { get; set; } = new();
    public string ActiveName { get; set; } = "";
    public List<LlmSettings> LlmProfiles { get; set; } = new();
    public string ActiveLlmName { get; set; } = "";
    public string DiaryPath { get; set; } = "";
    public string MemoryPath { get; set; } = "";
    public int ContextLimit { get; set; } = 20;
    public int AutoNewDiaryTokens { get; set; } = 50000;
    public Dictionary<string, List<string>> AllowedFolders { get; set; } = new();
}

public static class SettingsStore
{
    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AgentUi",
            "settings.json");

    public static SettingsData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
            }
        }
        catch { }
        return new SettingsData();
    }

    public static void Save(SettingsData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath,
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}