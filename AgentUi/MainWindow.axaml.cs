using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text;

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

    public MainWindow()
    {
        InitializeComponent();
        InputBox.AddHandler(InputElement.KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        _data = SettingsStore.Load();
        if (_data.Profiles.Count == 0)
            _data.Profiles.Add(new AppSettings { Name = "Профиль 1" });
        _current = _data.Profiles.FirstOrDefault(p => p.Name == _data.ActiveName) ?? _data.Profiles[0];

        DiaryPathBox.Text = _data.DiaryPath;
        MemoryPathBox.Text = _data.MemoryPath;
        ContextLimitBox.Value = _data.ContextLimit;
        AutoNewDiaryTokensBox.Value = _data.AutoNewDiaryTokens;

        RefreshProfileList();
        FillFields(_current);

        CleanupEmptyDiaries();
        LoadDiary();
        LoadMemory();
        LoadSystemUsers();
        RenderLog();
    }

    // ===== Хелперы =====

    private (string url, string model, string? key) GetEndpoint()
        => (UrlBox.Text ?? "", ModelBox.Text ?? "", KeyBox.Text);

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
        var context = _diary.Messages
            .Skip(Math.Max(0, _diary.Messages.Count - _data.ContextLimit))
            .ToList();

        var insertAt = 0;
        if (!string.IsNullOrWhiteSpace(_memory))
        {
            context.Insert(0, new ChatMessage
            {
                Role = "system",
                Content = "Твоя рабочая память. Всегда учитывай эти пункты при ответах:\n" + _memory
            });
            insertAt = 1;
        }

        if (!string.IsNullOrWhiteSpace(_data.DiaryPath))
        {
            var reflections = ReflectionStore.LoadAll(_data.DiaryPath);
            if (reflections.Count > 0)
            {
                context.Insert(insertAt, new ChatMessage
                {
                    Role = "system",
                    Content = "Контекст из предыдущих дневников (рефлексии):\n\n" + string.Join(
                        "\n\n---\n\n",
                        reflections.OrderByDescending(r => r.path).Take(3).Select(r => r.content))
                });
            }
        }

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
            sb.AppendLine($"{(m.Role == "user" ? "Ты" : "Агент")}: {m.Content}");
            sb.AppendLine();
        }
        LogBox.Text = sb.ToString();
        UpdateTokenCounter();
    }

    // ===== Профили =====

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
                    _diary.Messages.Select(m => $"{(m.Role == "user" ? "Ты" : "Агент")}: {m.Content}"));

                var prompt =
                    "Ты — модуль рефлексии агента. Проанализируй следующий диалог и создай " +
                    "сжатое резюме объёмом 400-600 слов на русском языке.\n\n" +
                    "Требования:\n" +
                    "- Сохрани ключевые факты, решения, выводы\n" +
                    "- Убери воду, повторения, нерелевантные детали\n" +
                    "- Оформи структурированно, чтобы агент мог быстро найти нужное\n\n" +
                    historyText;

                var reflection = (await _client.AskOnceAsync(url, model, key, prompt)).Trim();

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
                _diary.Messages.Select(m => $"{(m.Role == "user" ? "Ты" : "Агент")}: {m.Content}"));

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

            _memory = (await _client.AskOnceAsync(url, model, key, prompt)).Trim();
            SaveMemory();
            if (string.IsNullOrWhiteSpace(_data.MemoryPath))
                AppendLog("⚠ Путь для памяти не задан — сохранено только до выхода\n");
            RenderLog();
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
        _data.ActiveName = _current.Name;
        _data.DiaryPath = DiaryPathBox.Text ?? "";
        _data.MemoryPath = MemoryPathBox.Text ?? "";
        SettingsStore.Save(_data);
        base.OnClosed(e);
    }

    // ===== Отправка сообщения =====

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

        var buffer = new StringBuilder();
        var gate = new object();
        var shown = 0;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            string snapshot;
            lock (gate) snapshot = buffer.ToString();
            if (snapshot.Length > shown)
            {
                LogBox.Text += snapshot.Substring(shown);
                shown = snapshot.Length;
            }
        };
        timer.Start();

        try
        {
            await Task.Run(async () =>
            {
                await foreach (var token in _client.AskStreamAsync(url, model, key, context))
                {
                    lock (gate) buffer.Append(token);
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
        }
        finally
        {
            timer.Stop();

            string finalText;
            lock (gate) finalText = buffer.ToString();

            assistantMessage.Content = finalText;
            SaveDiary();
            RenderLog();
            CheckAutoRotate();
            SendButton.IsEnabled = true;
        }
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

    private List<SystemUser> _systemUsers = new();

private void LoadSystemUsers()
{
    _systemUsers = SystemUser.LoadAll();
    UserBox.Items.Clear();
    
    // сначала обычные пользователи (UID >= 1000)
    foreach (var u in _systemUsers.Where(u => u.IsRegularUser).OrderBy(u => u.Name))
        UserBox.Items.Add(u);
    
    // потом остальные (системные)
    foreach (var u in _systemUsers.Where(u => !u.IsRegularUser).OrderBy(u => u.Uid))
        UserBox.Items.Add(u);

    // выбираем текущего пользователя по умолчанию
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
        RefreshAllowedFolders();   // ← добавить
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

    // rwX — доступ на чтение/запись/вход; -d — то же для будущих файлов внутри
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