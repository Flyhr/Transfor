using System.Text.Json;
using System.Text.Json.Serialization;

namespace Transfor;

internal sealed class HistoryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly string stateFilePath;
    private readonly List<HistoryEntry> quoteHistory = new();
    private readonly List<HistoryEntry> spaceHistory = new();

    private HistoryStore(string stateFilePath)
    {
        this.stateFilePath = Path.GetFullPath(stateFilePath);
        Settings = AppSettings.Default;
    }

    public AppSettings Settings { get; private set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Transfor",
        "state.json");

    public static HistoryStore Load(string? stateFilePath = null)
    {
        var store = new HistoryStore(stateFilePath ?? DefaultPath);
        if (!File.Exists(store.stateFilePath))
        {
            return store;
        }

        try
        {
            var json = File.ReadAllText(store.stateFilePath);
            var persisted = JsonSerializer.Deserialize<PersistedState>(json, SerializerOptions)
                ?? throw new JsonException("状态文件为空。");
            store.Restore(persisted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            store.Reset();
        }

        return store;
    }

    public IReadOnlyList<HistoryEntry> GetHistory(ToolId tool)
    {
        return GetList(tool).AsReadOnly();
    }

    public void Add(HistoryEntry entry)
    {
        if (entry is null || !Enum.IsDefined(entry.Tool) || entry.OriginalInput is null || entry.ConvertedOutput is null)
        {
            throw new ArgumentException("历史记录内容无效。", nameof(entry));
        }

        var list = GetList(entry.Tool);
        list.Add(entry);
        Trim(entry.Tool);
    }

    public void ClearHistory(ToolId tool)
    {
        GetList(tool).Clear();
    }

    public void SetLastViewedTool(ToolId tool)
    {
        if (!Enum.IsDefined(tool))
        {
            throw new ArgumentOutOfRangeException(nameof(tool));
        }

        Settings = Settings with { LastViewedTool = tool };
    }

    public void UpdateSettings(AppSettings settings)
    {
        settings.Validate();
        Settings = settings;
        Trim(ToolId.QuoteConversion);
        Trim(ToolId.SpaceRemoval);
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(stateFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("状态文件路径无效。");
        }

        Directory.CreateDirectory(directory);
        var persisted = new PersistedState
        {
            Settings = new PersistedSettings
            {
                HotKeyModifiers = (int)Settings.HistoryHotKey.Modifiers,
                HotKeyKey = (int)Settings.HistoryHotKey.Key,
                QuoteHistoryLimit = Settings.QuoteHistoryLimit,
                SpaceHistoryLimit = Settings.SpaceHistoryLimit,
                LastViewedTool = Settings.LastViewedTool,
            },
            History = quoteHistory.Concat(spaceHistory).ToList(),
        };

        var temporaryPath = stateFilePath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(persisted, SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, stateFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private List<HistoryEntry> GetList(ToolId tool)
    {
        return tool switch
        {
            ToolId.QuoteConversion => quoteHistory,
            ToolId.SpaceRemoval => spaceHistory,
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
    }

    private void Trim(ToolId tool)
    {
        var list = GetList(tool);
        var limit = tool == ToolId.QuoteConversion ? Settings.QuoteHistoryLimit : Settings.SpaceHistoryLimit;
        if (list.Count <= limit)
        {
            return;
        }

        list.RemoveRange(0, list.Count - limit);
    }

    private void Restore(PersistedState persisted)
    {
        if (persisted.Settings is null || persisted.History is null)
        {
            throw new JsonException("状态文件缺少字段。");
        }

        var hotKey = HotKeyBinding.Create(
            (System.Windows.Forms.Keys)persisted.Settings.HotKeyModifiers,
            (System.Windows.Forms.Keys)persisted.Settings.HotKeyKey);
        var settings = new AppSettings(
            hotKey,
            persisted.Settings.QuoteHistoryLimit,
            persisted.Settings.SpaceHistoryLimit,
            persisted.Settings.LastViewedTool);
        settings.Validate();

        quoteHistory.Clear();
        spaceHistory.Clear();
        foreach (var entry in persisted.History)
        {
            if (entry is null || !Enum.IsDefined(entry.Tool) || entry.OriginalInput is null || entry.ConvertedOutput is null)
            {
                throw new JsonException("历史记录无效。");
            }

            GetList(entry.Tool).Add(entry);
        }

        Settings = settings;
        Trim(ToolId.QuoteConversion);
        Trim(ToolId.SpaceRemoval);
    }

    private void Reset()
    {
        Settings = AppSettings.Default;
        quoteHistory.Clear();
        spaceHistory.Clear();
    }

    private sealed class PersistedState
    {
        public PersistedSettings? Settings { get; set; }

        public List<HistoryEntry>? History { get; set; }
    }

    private sealed class PersistedSettings
    {
        public int HotKeyModifiers { get; set; }

        public int HotKeyKey { get; set; }

        public int QuoteHistoryLimit { get; set; }

        public int SpaceHistoryLimit { get; set; }

        public ToolId LastViewedTool { get; set; }
    }
}
