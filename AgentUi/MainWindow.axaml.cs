using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text;
using System.Text.Json;

namespace AgentUi;

public partial class MainWindow : Window
{
    private readonly LlmClient _client = new();
    private SettingsData _data = new();
    private AppSettings _current = new();
    private Diary _diary = new();
    private string? _diaryFile;
    private string _memory = "";
    private bool _suppress;

    private LlmSettings _currentLlm = new();
    private bool _suppressLlm;

    private RagIndex? _ragIndex;

    public MainWindow()
    {
        InitializeComponent();
        InputBox.AddHandler(InputElement.KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        _data = SettingsStore.Load();
        if (_data.Profiles.Count == 0)
            _data.Profiles.Add(new AppSettings { Name = "Профиль 1" });
        _current = _data.Profiles.FirstOrDefault(p => p.Name == _data.ActiveName) ?? _data.Profiles[0];

        if (_data.LlmProfiles.Count == 0)
            _data.LlmProfiles.Add(new LlmSettings { Name = "Профиль 1" });
        _currentLlm = _data.LlmProfiles.FirstOrDefault(p => p.Name == _data.ActiveLlmName) ?? _data.LlmProfiles[0];

        DiaryPathBox.Text = _data.DiaryPath;
        MemoryPathBox.Text = _data.MemoryPath;
        ContextLimitBox.Value = _data.ContextLimit;
        AutoNewDiaryTokensBox.Value = _data.AutoNewDiaryTokens;

        PromptFirstBox.Text = _data.PromptFirstPath;
        PromptSystemBox.Text = _data.PromptSystemPath;
        PromptCharacterBox.Text = _data.PromptCharacterPath;
        PromptAppearanceBox.Text = _data.PromptAppearancePath;
        PromptDiarySaveBox.Text = _data.PromptDiarySavePath;

        RagPathBox.Text = _data.RagPath;
        EmbedModelBox.Text = _data.EmbedModel;
        RagChunkSizeBox.Value = _data.RagChunkSize;
        RagTopKBox.Value = _data.RagTopK;
        RagThresholdBox.Value = (decimal)_data.RagThreshold;

        RefreshProfileList();
        FillFields(_current);
        RefreshLlmProfileList();
        FillLlmFields(_currentLlm);
        LoadRagIndex();

        CleanupEmptyDiaries();
        LoadDiary();
        LoadMemory();
        LoadSystemUsers();
        RenderLog();
    }

    // ===== Хелперы =====

    private (string url, string model, string? key) GetEndpoint()
        => (UrlBox.Text ?? "", ModelBox.Text ?? "", KeyBox.Text);

    private LlmParams CurrentLlmParams() => new()
    {
        Temperature = _currentLlm.Temperature,
        TopP = _currentLlm.TopP,
        RepeatPenalty = _currentLlm.RepeatPenalty,
        MaxTokens = _currentLlm.MaxTokens,
        Seed = _currentLlm.Seed
    };

    private void AppendLog(string text) => LogBox.Text += text;

    // ===== Дневник и память =====

    private void LoadDiary()
    {
        if (string.IsNullOrWhiteSpace(_data.DiaryPath))
        {
            _diary = new Diary();
            _diaryFile = null;
            return;
        }

        _diaryFile = DiaryStore.FindLatest(_data.DiaryPath);
        _diary = _diaryFile != null
            ? DiaryStore.Load(_data.DiaryPath, _diaryFile)
            : new Diary();
    }

    private void SaveDiary()
    {
        if (string.IsNullOrWhiteSpace(_data.DiaryPath)) return;
        _diaryFile ??= DiaryStore.NewFileName(_data.DiaryPath);
        DiaryStore.Save(_data.DiaryPath, _diaryFile, _diary);
    }

    private void LoadMemory()
    {
        _memory = !string.IsNullOrWhiteSpace(_data.MemoryPath) && Directory.Exists(_data.MemoryPath)
            ? MemoryStore.Load(_data.MemoryPath)
            : "";
    }

    private void SaveMemory()
    {
        if (!string.IsNullOrWhiteSpace(_data.MemoryPath))
            MemoryStore.Save(_data.MemoryPath, _memory);
    }

    private void CleanupEmptyDiaries()
    {
        if (string.IsNullOrWhiteSpace(_data.DiaryPath) || !Directory.Exists(_data.DiaryPath)) return;

        foreach (var path in Directory.GetFiles(_data.DiaryPath, "*.md"))
        {
            if (Path.GetFileName(path) == "working_memory.md") continue;
            try
            {
                var text = File.ReadAllText(path)
                    .Replace("# Резюме", "").Replace("# История", "").Trim();
                if (text.Length == 0) File.Delete(path);
            }
            catch { }
        }
    }

    private int CountTokens()
    {
        var total = 0;
        foreach (var m in _diary.Messages)
            total += m.Content.Length / 3;
        return total;
    }

    private void UpdateTokenCounter()
        => TokenCounterText.Text = $"Токенов в дневнике: {CountTokens():N0}";

    private void CheckAutoRotate()
    {
        if (_data.AutoNewDiaryTokens > 0 && CountTokens() >= _data.AutoNewDiaryTokens)
            OnNewDiary(this, new RoutedEventArgs());
    }

private List<ChatMessage> BuildContext()
{
    var context = new List<ChatMessage>();

    // Объединяем все system-промпты в один
    var systemParts = new List<string>();

    var first = LoadPrompt(PromptFirstBox.Text);
    if (first.Length > 0) systemParts.Add(first);

    var sys = LoadPrompt(PromptSystemBox.Text);
    if (sys.Length > 0) systemParts.Add(sys);

    var character = LoadPrompt(PromptCharacterBox.Text);
    if (character.Length > 0) systemParts.Add(character);

    var appearance = LoadPrompt(PromptAppearanceBox.Text);
    if (appearance.Length > 0) systemParts.Add(appearance);

    if (!string.IsNullOrWhiteSpace(_memory))
        systemParts.Add("Твоя рабочая память. Всегда учитывай эти пункты при ответах:\n" + _memory);

    if (!string.IsNullOrWhiteSpace(_data.DiaryPath))
    {
        var reflections = ReflectionStore.LoadAll(_data.DiaryPath);
        if (reflections.Count > 0)
        {
            systemParts.Add("Контекст из предыдущих дневников (рефлексии):\n\n" + string.Join(
                "\n\n---\n\n",
                reflections.OrderByDescending(r => r.path).Take(3).Select(r => r.content)));
        }
    }

    // Добавляем одно объединённое system-сообщение в начало
    if (systemParts.Count > 0)
    {
        context.Add(new ChatMessage 
        { 
            Role = "system", 
            Content = string.Join("\n\n", systemParts) 
        });
    }

    // Добавляем историю диалога, но пропускаем system-сообщения
    var history = _diary.Messages
        .Skip(Math.Max(0, _diary.Messages.Count - _data.ContextLimit))
        .Where(m => m.Role != "system");
    
    context.AddRange(history);

    return context;
}

    private void RenderLog()
    {
        var sb = new StringBuilder();
        if (_diaryFile != null)
            sb.AppendLine($"📓 Дневник: {_diaryFile}");
        if (!string.IsNullOrWhiteSpace(_memory))
        {
            sb.AppendLine("🧠 Рабочая память:");
            sb.AppendLine(_memory);
            sb.AppendLine();
        }
        foreach (var m in _diary.Messages)
        {
            sb.AppendLine($"{(m.Role == "user" ? "Альдраксис" : "Мариса")}: {m.Content}");
            sb.AppendLine();
        }
        LogBox.Text = sb.ToString();
        UpdateTokenCounter();
    }

    // ===== Профили подключения =====

    private void RefreshProfileList()
    {
        _suppress = true;
        ProfileBox.Items.Clear();
        foreach (var p in _data.Profiles)
            ProfileBox.Items.Add(p.Name);
        ProfileBox.SelectedItem = _current.Name;
        _suppress = false;
    }

    private void FillFields(AppSettings s)
    {
        UrlBox.Text = s.Url;
        ModelBox.Text = s.Model;
        KeyBox.Text = s.ApiKey;
    }

    private void SaveFields()
    {
        _current.Url = UrlBox.Text ?? "";
        _current.Model = ModelBox.Text ?? "";
        _current.ApiKey = KeyBox.Text ?? "";
    }

    private void OnProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        var name = ProfileBox.SelectedItem as string;
        if (name == null || name == _current.Name) return;
        SaveFields();
        _current = _data.Profiles.First(p => p.Name == name);
        FillFields(_current);
    }

    private void OnAddProfile(object? sender, RoutedEventArgs e)
    {
        SaveFields();
        var name = $"Профиль {_data.Profiles.Count + 1}";
        while (_data.Profiles.Any(p => p.Name == name)) name += "·";
        var profile = new AppSettings { Name = name };
        _data.Profiles.Add(profile);
        _current = profile;
        RefreshProfileList();
        FillFields(_current);
    }

    private void OnDeleteProfile(object? sender, RoutedEventArgs e)
    {
        if (_data.Profiles.Count <= 1) return;
        _data.Profiles.Remove(_current);
        _current = _data.Profiles[0];
        RefreshProfileList();
        FillFields(_current);
    }

    private async void OnRenameProfile(object? sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog("Переименовать профиль", "Новое имя:", _current.Name);
        var result = await dialog.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(result)) return;
        if (_data.Profiles.Any(p => p.Name == result && p != _current)) return;

        SaveFields();
        _current.Name = result;
        RefreshProfileList();
    }

    // ===== Профили LLM =====

    private void RefreshLlmProfileList()
    {
        _suppressLlm = true;
        LlmProfileBox.Items.Clear();
        foreach (var p in _data.LlmProfiles)
            LlmProfileBox.Items.Add(p.Name);
        LlmProfileBox.SelectedItem = _currentLlm.Name;
        _suppressLlm = false;
    }

    private void FillLlmFields(LlmSettings s)
    {
        TemperatureBox.Value = (decimal)s.Temperature;
        TopPBox.Value = (decimal)s.TopP;
        RepeatPenaltyBox.Value = (decimal)s.RepeatPenalty;
        MaxTokensBox.Value = s.MaxTokens;
        SeedBox.Value = s.Seed;
    }

    private void SaveLlmFields()
    {
        _currentLlm.Temperature = (double)(TemperatureBox.Value ?? 0.8m);
        _currentLlm.TopP = (double)(TopPBox.Value ?? 0.9m);
        _currentLlm.RepeatPenalty = (double)(RepeatPenaltyBox.Value ?? 1.1m);
        _currentLlm.MaxTokens = (int)(MaxTokensBox.Value ?? 0);
        _currentLlm.Seed = (int)(SeedBox.Value ?? -1);
    }

    private void OnLlmProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressLlm) return;
        var name = LlmProfileBox.SelectedItem as string;
        if (name == null || name == _currentLlm.Name) return;
        SaveLlmFields();
        _currentLlm = _data.LlmProfiles.First(p => p.Name == name);
        FillLlmFields(_currentLlm);
    }

    private void OnAddLlmProfile(object? sender, RoutedEventArgs e)
    {
        SaveLlmFields();
        var name = $"Профиль {_data.LlmProfiles.Count + 1}";
        while (_data.LlmProfiles.Any(p => p.Name == name)) name += "·";
        var profile = new LlmSettings { Name = name };
        _data.LlmProfiles.Add(profile);
        _currentLlm = profile;
        RefreshLlmProfileList();
        FillLlmFields(_currentLlm);
    }

    private void OnDeleteLlmProfile(object? sender, RoutedEventArgs e)
    {
        if (_data.LlmProfiles.Count <= 1) return;
        _data.LlmProfiles.Remove(_currentLlm);
        _currentLlm = _data.LlmProfiles[0];
        RefreshLlmProfileList();
        FillLlmFields(_currentLlm);
    }

    private async void OnRenameLlmProfile(object? sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog("Переименовать профиль LLM", "Новое имя:", _currentLlm.Name);
        var result = await dialog.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(result)) return;
        if (_data.LlmProfiles.Any(p => p.Name == result && p != _currentLlm)) return;

        SaveLlmFields();
        _currentLlm.Name = result;
        RefreshLlmProfileList();
    }

    // ===== Пути =====

    private async void OnPickDiaryPath(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Выберите папку для дневника");
        if (path == null) return;
        DiaryPathBox.Text = path;
        _data.DiaryPath = path;
        LoadDiary();
        RenderLog();
    }

    private async void OnPickGrantFolder(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Выберите папку для выдачи доступа");
        if (path != null)
            GrantPathBox.Text = path;
    }

    private async void OnPickMemoryPath(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Выберите папку для рабочей памяти");
        if (path == null) return;
        MemoryPathBox.Text = path;
        _data.MemoryPath = path;
        LoadMemory();
        RenderLog();
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.Count > 0
            ? folders[0].TryGetLocalPath() ?? folders[0].Path.ToString()
            : null;
    }

    // ===== Промпты =====

    private static string LoadPrompt(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.ReadAllText(path).Trim() : "";

    private async Task<string?> PickFileAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return files.Count > 0
            ? files[0].TryGetLocalPath() ?? files[0].Path.ToString()
            : null;
    }

    private async void OnPickPromptFirst(object? sender, RoutedEventArgs e)
    {
        var p = await PickFileAsync("Файл первого сообщения");
        if (p != null) PromptFirstBox.Text = p;
    }

    private async void OnPickPromptSystem(object? sender, RoutedEventArgs e)
    {
        var p = await PickFileAsync("Файл системного промпта");
        if (p != null) PromptSystemBox.Text = p;
    }

    private async void OnPickPromptCharacter(object? sender, RoutedEventArgs e)
    {
        var p = await PickFileAsync("Файл характера персонажа");
        if (p != null) PromptCharacterBox.Text = p;
    }

    private async void OnPickPromptAppearance(object? sender, RoutedEventArgs e)
    {
        var p = await PickFileAsync("Файл описания персонажа");
        if (p != null) PromptAppearanceBox.Text = p;
    }

    private async void OnPickPromptDiarySave(object? sender, RoutedEventArgs e)
    {
        var p = await PickFileAsync("Файл инструкций по дневнику");
        if (p != null) PromptDiarySaveBox.Text = p;
    }

    // ===== RAG =====

    private void LoadRagIndex()
    {
        _ragIndex = RagStore.Load();
        RagStatusText.Text = _ragIndex == null
            ? "Индекс не построен."
            : $"Индекс: {_ragIndex.AllChunks().Count} фрагментов из {_ragIndex.Files.Count} файлов, модель {_ragIndex.EmbedModel}.";
    }

    private async void OnPickRagPath(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Выберите папку базы знаний");
        if (path == null) return;
        RagPathBox.Text = path;
        _data.RagPath = path;
    }

    private async void OnBuildRagIndex(object? sender, RoutedEventArgs e)
    {
        var embedModel = EmbedModelBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(embedModel)) return;

        var sources = BuildRagSources();
        if (sources.Count == 0)
        {
            RagStatusText.Text = "⚠ Не задано ни одной папки-источника (RAG / дневник / память).";
            return;
        }

        var (url, _, key) = GetEndpoint();
        if (string.IsNullOrWhiteSpace(url)) return;

        BuildRagButton.IsEnabled = false;
        RagStatusText.Text = "Строю индекс...";
        try
        {
            await RagStore.BuildAsync(_client, url, key, sources, embedModel,
                (int)(RagChunkSizeBox.Value ?? 1000), _ragIndex,
                log => Dispatcher.UIThread.Post(() => RagStatusText.Text = log.TrimEnd()));
            _data.RagPath = RagPathBox.Text ?? "";
            _data.EmbedModel = embedModel;
            LoadRagIndex();
        }
        catch (Exception ex)
        {
            RagStatusText.Text = $"✕ Ошибка построения: {ex.Message}";
        }
        finally
        {
            BuildRagButton.IsEnabled = true;
        }
    }

    private List<(string path, string source)> BuildRagSources()
    {
        var sources = new List<(string path, string source)>();
        if (!string.IsNullOrWhiteSpace(_data.RagPath) && Directory.Exists(_data.RagPath))
            sources.Add((_data.RagPath, "база знаний"));
        if (!string.IsNullOrWhiteSpace(_data.DiaryPath) && Directory.Exists(_data.DiaryPath))
            sources.Add((_data.DiaryPath, "дневник"));
        if (!string.IsNullOrWhiteSpace(_data.MemoryPath) && Directory.Exists(_data.MemoryPath))
            sources.Add((_data.MemoryPath, "память"));
        return sources;
    }

    /// <summary>Тихая пересборка индекса, если он уже построен (после нового дневника/памяти).</summary>
    private async void RebuildRagIfBuiltAsync()
    {
        if (_ragIndex == null) return;
        var (url, _, key) = GetEndpoint();
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            await RagStore.BuildAsync(_client, url, key, BuildRagSources(),
                _ragIndex.EmbedModel, _ragIndex.ChunkSize, _ragIndex);
            LoadRagIndex();
        }
        catch { }
    }

    // ===== Инструменты (агентный цикл) =====

    private async Task<string> ExecuteToolAsync(string url, string? key, ToolCall call)
    {
        AppendToolCall($"🛠 #{call.Name}({call.ArgumentsJson})\n");

        string result;
        try
        {
            if (_ragIndex == null)
            {
                result = "База знаний не загружена.";
            }
            else
            {
                using var doc = JsonDocument.Parse(call.ArgumentsJson);
                var args = doc.RootElement;

                if (call.Name == "ask")
                {
                    var question = args.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
                    result = await RagStore.ProbeAsync(_client, url, key, _ragIndex, question, _data.RagThreshold);
                }
                else if (call.Name == "query")
                {
                    var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
                    var topK = args.TryGetProperty("top_k", out var tk) && tk.ValueKind == JsonValueKind.Number
                        ? tk.GetInt32()
                        : _data.RagTopK;
                    result = await RagStore.QueryAsync(_client, url, key, _ragIndex, query, topK, _data.RagThreshold);
                }
                else
                {
                    result = $"Неизвестный инструмент: {call.Name}";
                }
            }
        }
        catch (Exception ex)
        {
            result = $"Ошибка инструмента: {ex.Message}";
        }

        AppendToolCall($"→ {result}\n\n");
        return result;
    }

    // ===== Кнопки дневника и памяти =====

    private async void OnNewDiary(object? sender, RoutedEventArgs e)
    {
        var (url, model, key) = GetEndpoint();

        if (_diary.Messages.Count > 0 && !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(_data.DiaryPath))
        {
            NewDiaryButton.IsEnabled = false;
            AppendLog("📓 Генерирую рефлексию дневника...\n");

            try
            {
                var historyText = string.Join("\n\n",
                    _diary.Messages.Select(m => $"{(m.Role == "user" ? "Альдраксис" : "Мариса")}: {m.Content}"));

                var diaryInstructions = LoadPrompt(PromptDiarySaveBox.Text);

                var prompt =
                    "Ты — модуль рефлексии агента. Проанализируй следующий диалог и создай " +
                    "сжатое резюме объёмом 400-600 слов на русском языке.\n\n" +
                    "Требования:\n" +
                    "- Сохрани ключевые факты, решения, выводы\n" +
                    "- Убери воду, повторения, нерелевантные детали\n" +
                    "- Оформи структурированно, чтобы агент мог быстро найти нужное\n" +
                    (diaryInstructions.Length > 0
                        ? "\nДополнительные инструкции пользователя по ведению дневника:\n" + diaryInstructions + "\n"
                        : "") +
                    "\n" + historyText;

                var reflection = (await _client.AskOnceAsync(url, model, key, prompt, CurrentLlmParams())).Trim();

                if (reflection.Length < 50)
                    AppendLog($"⚠ Рефлексия пустая или короткая ({reflection.Length} символов), файл не создан\n");
                else
                {
                    ReflectionStore.Save(_data.DiaryPath, reflection);
                    AppendLog($"✓ Рефлексия сохранена ({reflection.Length} символов)\n");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Ошибка рефлексии: {ex.Message}\n");
            }
            finally
            {
                NewDiaryButton.IsEnabled = true;
            }
        }

        _diary = new Diary();
        _diaryFile = null;
        RenderLog();
        RebuildRagIfBuiltAsync();
    }

    private async void OnMakeMemory(object? sender, RoutedEventArgs e)
    {
        if (_diary.Messages.Count == 0) return;
        var (url, model, key) = GetEndpoint();
        if (string.IsNullOrWhiteSpace(url)) return;

        MemoryButton.IsEnabled = false;
        try
        {
            var historyText = string.Join("\n",
                _diary.Messages.Select(m => $"{(m.Role == "user" ? "Альдраксис" : "Мариса")}: {m.Content}"));

            var prompt =
                "Ты — модуль рабочей памяти агента. Из диалога ниже извлеки ключевые пункты, " +
                "которые агент должен всегда учитывать в дальнейшей работе. " +
                "Оформи маркированным списком по разделам:\n" +
                "- важные факты и решения\n" +
                "- задачи и цели пользователя\n" +
                "- предпочтения и стиль пользователя\n\n" +
                "Пиши кратко, только пункты, без воды. " +
                "Если запоминать нечего — ответь одним словом «пусто».\n\n" +
                historyText;

            _memory = (await _client.AskOnceAsync(url, model, key, prompt, CurrentLlmParams())).Trim();
            SaveMemory();
            if (string.IsNullOrWhiteSpace(_data.MemoryPath))
                AppendLog("⚠ Путь для памяти не задан — сохранено только до выхода\n");
            RenderLog();
            RebuildRagIfBuiltAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"Ошибка памяти: {ex.Message}\n");
        }
        finally
        {
            MemoryButton.IsEnabled = true;
        }
    }

    // ===== Закрытие окна =====

    protected override void OnClosed(EventArgs e)
    {
        SaveFields();
        SaveLlmFields();
        _data.ActiveName = _current.Name;
        _data.ActiveLlmName = _currentLlm.Name;
        _data.DiaryPath = DiaryPathBox.Text ?? "";
        _data.MemoryPath = MemoryPathBox.Text ?? "";
        _data.PromptFirstPath = PromptFirstBox.Text ?? "";
        _data.PromptSystemPath = PromptSystemBox.Text ?? "";
        _data.PromptCharacterPath = PromptCharacterBox.Text ?? "";
        _data.PromptAppearancePath = PromptAppearanceBox.Text ?? "";
        _data.PromptDiarySavePath = PromptDiarySaveBox.Text ?? "";
        _data.RagPath = RagPathBox.Text ?? "";
        _data.EmbedModel = EmbedModelBox.Text ?? "";
        _data.RagChunkSize = (int)(RagChunkSizeBox.Value ?? 1000);
        _data.RagTopK = (int)(RagTopKBox.Value ?? 5);
        _data.RagThreshold = (double)(RagThresholdBox.Value ?? 0.35m);
        SettingsStore.Save(_data);
        base.OnClosed(e);
    }

    // ===== Отправка сообщения (агентный цикл) =====

private async void OnSendClick(object? sender, RoutedEventArgs e)
{
    var message = InputBox.Text ?? "";
    if (string.IsNullOrWhiteSpace(message)) return;

    InputBox.Text = "";
    SendButton.IsEnabled = false;

    var (url, model, key) = GetEndpoint();

    _diary.Messages.Add(new ChatMessage { Role = "user", Content = message });
    var context = BuildContext();

    var assistantMessage = new ChatMessage { Role = "assistant", Content = "" };
    _diary.Messages.Add(assistantMessage);
    RenderLog();
    AppendLog("Агент: ");

    ThinkingBox.Text = "";
    ReasoningBox.Text = "";
    ToolCallBox.Text = "";

    var buffer = new StringBuilder();
    var thinkBuffer = new StringBuilder();
    var reasonBuffer = new StringBuilder();
    var gate = new object();
    var shownC = 0;
    var shownT = 0;
    var shownR = 0;
    var splitter = new ThinkTagSplitter();

    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
    timer.Tick += (_, _) =>
    {
        lock (gate)
        {
            var c = buffer.ToString();
            var t = thinkBuffer.ToString();
            var r = reasonBuffer.ToString();
            if (c.Length > shownC) { LogBox.Text += c.Substring(shownC); shownC = c.Length; }
            if (t.Length > shownT) { ThinkingBox.Text += t.Substring(shownT); shownT = t.Length; }
            if (r.Length > shownR) { ReasoningBox.Text += r.Substring(shownR); shownR = r.Length; }
        }
    };
    timer.Start();

    // Запоминаем, откуда начался последний ответ модели
    var lastResponseStart = 0;

    try
    {
        await Task.Run(async () =>
        {
            var messages = context.ToList();
            var tools = _ragIndex != null ? RagStore.Tools() : null;
            const int maxIterations = 5;

            for (int iter = 0; iter < maxIterations; iter++)
            {
                var outcome = await _client.AskWithToolsAsync(
                    url, model, key, messages, CurrentLlmParams(), tools,
                    chunk =>
                    {
                        lock (gate)
                        {
                            if (chunk.Kind == LlmChunkKind.Thinking)
                            {
                                thinkBuffer.Append(chunk.Text);
                            }
                            else
                            {
                                var (contentPart, thinkPart) = splitter.Process(chunk.Text);
                                buffer.Append(contentPart);
                                if (thinkPart.Length > 0) reasonBuffer.Append(thinkPart);
                            }
                        }
                    });

                if (outcome.ToolCalls.Count == 0) break;

                // Перед следующим вызовом запоминаем позицию — это будет началом финального ответа
                lock (gate)
                {
                    lastResponseStart = buffer.Length;
                }

                foreach (var call in outcome.ToolCalls)
                {
                    var resultText = await ExecuteToolAsync(url, key, call);
                    messages.Add(new ChatMessage { Role = "tool", Content = resultText });
                }
            }

            var (fc, ft) = splitter.Flush();
            lock (gate)
            {
                buffer.Append(fc);
                reasonBuffer.Append(ft);
            }
        });
    }
catch (Exception ex)
{
    _diary.Messages.Add(new ChatMessage
    {
        Role = "system",
        Content = $"[ошибка запроса: {ex.Message}]"
    });
    AppendLog($"\n[ошибка запроса: {ex.Message}]\n");
}
    finally
    {
        timer.Stop();

        string finalText;
        lock (gate)
        {
            // Сохраняем в дневник только финальный ответ (после всех tool calls)
            finalText = buffer.Length > lastResponseStart 
                ? buffer.ToString().Substring(lastResponseStart) 
                : buffer.ToString();
        }

        assistantMessage.Content = finalText;
        SaveDiary();
        RenderLog();
        CheckAutoRotate();
        SendButton.IsEnabled = true;
    }
}

    /// <summary>Для будущего агентного цикла: пишет вызовы инструментов в окно Tool calls.</summary>
    public void AppendToolCall(string text)
    {
        Dispatcher.UIThread.Post(() => ToolCallBox.Text += text);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0) return;

        e.Handled = true;
        OnSendClick(this, new RoutedEventArgs());
    }

    // ===== Лимиты =====

    private void OnContextLimitChanged(object? sender, NumericUpDownValueChangedEventArgs e)
        => _data.ContextLimit = (int)(ContextLimitBox.Value ?? 20);

    private void OnAutoNewDiaryTokensChanged(object? sender, NumericUpDownValueChangedEventArgs e)
        => _data.AutoNewDiaryTokens = (int)(AutoNewDiaryTokensBox.Value ?? 50000);

    // ===== Пользователи системы =====

    private List<SystemUser> _systemUsers = new();

    private void LoadSystemUsers()
    {
        _systemUsers = SystemUser.LoadAll();
        UserBox.Items.Clear();

        foreach (var u in _systemUsers.Where(u => u.IsRegularUser).OrderBy(u => u.Name))
            UserBox.Items.Add(u);

        foreach (var u in _systemUsers.Where(u => !u.IsRegularUser).OrderBy(u => u.Uid))
            UserBox.Items.Add(u);

        var current = _systemUsers.FirstOrDefault(u => u.Name == Environment.UserName);
        if (current != null) UserBox.SelectedItem = current;
    }

    private SystemUser? _selectedUser;

    private void OnUserChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedUser = UserBox.SelectedItem as SystemUser;
        if (_selectedUser != null)
        {
            AppendLog($"Выбран пользователь: {_selectedUser.Name} (UID {_selectedUser.Uid}, дом: {_selectedUser.Home})\n");
            RefreshAllowedFolders();
        }
    }

    private async void OnTestUser(object? sender, RoutedEventArgs e)
    {
        if (_selectedUser == null) return;
        var (code, output) = await RunAs.ExecAsync(_selectedUser.Name, null, "whoami && pwd");
        AppendLog(code == 0 ? $"✓ {output.Trim()}\n" : $"✕ Ошибка {code}: {output}\n");
    }

    private void RefreshAllowedFolders()
    {
        AllowedFoldersBox.Items.Clear();
        if (_selectedUser == null) return;
        if (_data.AllowedFolders.TryGetValue(_selectedUser.Name, out var folders))
            foreach (var f in folders)
                AllowedFoldersBox.Items.Add(f);
    }

    private async void OnGrantFolder(object? sender, RoutedEventArgs e)
    {
        var path = GrantPathBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(path) || _selectedUser == null) return;

        if (!Directory.Exists(path))
        {
            AppendLog($"⚠ Папка не существует: {path}\n");
            return;
        }

        var cmd = $"setfacl -R -m u:{_selectedUser.Name}:rwX \"{path}\" && " +
                  $"setfacl -R -d -m u:{_selectedUser.Name}:rwX \"{path}\"";
        var (code, output) = await RunAs.ExecAsync(Environment.UserName, null, cmd);

        if (code == 0)
        {
            if (!_data.AllowedFolders.TryGetValue(_selectedUser.Name, out var list))
            {
                list = new List<string>();
                _data.AllowedFolders[_selectedUser.Name] = list;
            }
            if (!list.Contains(path)) list.Add(path);
            SettingsStore.Save(_data);
            RefreshAllowedFolders();
            GrantPathBox.Text = "";
            AppendLog($"✓ Доступ выдан: {path} → {_selectedUser.Name}\n");
        }
        else
        {
            AppendLog($"✕ Ошибка выдачи доступа: {output}\n");
        }
    }

    private async void OnRevokeFolder(object? sender, RoutedEventArgs e)
    {
        var selected = AllowedFoldersBox.SelectedItem as string;
        if (selected == null || _selectedUser == null) return;

        var cmd = $"setfacl -R -x u:{_selectedUser.Name} \"{selected}\"; " +
                  $"setfacl -R -d -x u:{_selectedUser.Name} \"{selected}\"";
        var (code, output) = await RunAs.ExecAsync(Environment.UserName, null, cmd);

        if (_data.AllowedFolders.TryGetValue(_selectedUser.Name, out var list))
            list.Remove(selected);
        SettingsStore.Save(_data);
        RefreshAllowedFolders();
        AppendLog(code == 0
            ? $"✓ Доступ отозван: {selected}\n"
            : $"⚠ ACL снят с ошибками: {output}\n");
    }
}