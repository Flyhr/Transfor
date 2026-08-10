using System.Text.Json;
using System.Text.Json.Serialization;

namespace Transfor;

// 状态存储：内存中持有设置、界面状态与两类历史，修改后以原子方式写入 JSON 文件；
// 文件缺失或损坏时自动回退到默认值/空历史
internal sealed class TextStateStore : ITextHistoryRepository
{
    // JSON 序列化选项：缩进输出 + 枚举序列化为字符串
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly AppPaths paths;

    // 引号转换历史（按时间先后排列，最新的在末尾）
    private readonly List<HistoryEntry> quote = new();

    // 去除空格历史
    private readonly List<HistoryEntry> space = new();

    private TextStateStore(AppPaths paths, AppSettings settings, TextUiState uiState, IEnumerable<HistoryEntry> history)
    {
        this.paths = paths; Settings = settings; UiState = uiState;
        // 按工具把历史归类到对应列表
        foreach (var entry in history) GetList(entry.Tool).Add(entry);
        // 按当前上限裁剪
        Trim();
    }

    public AppSettings Settings { get; private set; }
    public TextUiState UiState { get; private set; }

    // 从磁盘加载状态；文件缺失/损坏时回退到默认设置与空历史；
    // 语义非法（Tool 枚举非法/输入输出为 null）的历史条目逐条丢弃，不让配置文件导致启动异常
    public static TextStateStore Load(AppPaths paths)
    {
        var persistedSettings = Read(paths.SettingsFile, PersistedSettings.Default, static value => value.Validate());
        var settings = persistedSettings.ToSettings();
        var ui = Read(paths.UiStateFile, TextUiState.Default, static value => value.Validate());
        var history = Read(paths.TextHistoryFile, Array.Empty<HistoryEntry>(), static _ => { })
            .Where(IsValidEntry)
            .ToArray();
        return new TextStateStore(paths, settings, ui, history);
    }

    // 历史条目语义校验：Tool 枚举合法、输入/输出非 null（允许空字符串）
    private static bool IsValidEntry(HistoryEntry entry) =>
        Enum.IsDefined(entry.Tool)
        && entry.OriginalInput is not null
        && entry.ConvertedOutput is not null;

    public IReadOnlyList<HistoryEntry> GetHistory(TextToolId tool) => GetList(tool).AsReadOnly();

    // 追加一条历史并立即落盘（同时按上限裁剪）；
    // 写入失败时恢复写入前状态（含被裁剪的旧条目）并向调用方抛出原异常
    public void Add(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var list = GetList(entry.Tool);
        var snapshot = list.ToArray();
        list.Add(entry);
        Trim();
        try
        {
            SaveHistory();
        }
        catch
        {
            list.Clear();
            list.AddRange(snapshot);
            throw;
        }
    }

    // 清空指定工具的历史并立即落盘；写入失败时恢复原历史
    public void ClearHistory(TextToolId tool)
    {
        var list = GetList(tool);
        var snapshot = list.ToArray();
        list.Clear();
        try
        {
            SaveHistory();
        }
        catch
        {
            list.Clear();
            list.AddRange(snapshot);
            throw;
        }
    }

    // 删除指定工具历史中的单条并立即落盘；索引越界抛异常；写入失败恢复原历史
    public void RemoveHistory(TextToolId tool, int index)
    {
        var list = GetList(tool);
        if (index < 0 || index >= list.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "历史索引越界。");
        }

        var snapshot = list.ToArray();
        list.RemoveAt(index);
        try
        {
            SaveHistory();
        }
        catch
        {
            list.Clear();
            list.AddRange(snapshot);
            throw;
        }
    }

    // 更新设置并立即落盘（同时按新上限重新裁剪历史）；
    // 任一写入失败时恢复原内存设置与裁剪结果，并向调用方抛出原异常
    public void UpdateSettings(AppSettings settings)
    {
        settings.Validate();
        var previousSettings = Settings;
        var previousQuote = quote.ToArray();
        var previousSpace = space.ToArray();
        Settings = settings;
        Trim();
        try
        {
            SaveSettings();
            SaveHistory();
        }
        catch
        {
            Settings = previousSettings;
            quote.Clear();
            quote.AddRange(previousQuote);
            space.Clear();
            space.AddRange(previousSpace);
            throw;
        }
    }

    // 记录最近查看的工具并立即落盘界面状态；写入失败时恢复原界面状态
    public void SetLastViewedTool(TextToolId tool)
    {
        var state = new TextUiState(tool);
        state.Validate();
        var previous = UiState;
        UiState = state;
        try
        {
            SaveUiState();
        }
        catch
        {
            UiState = previous;
            throw;
        }
    }

    // 全量落盘：设置 + 界面状态 + 历史
    public void Save() { SaveSettings(); SaveUiState(); SaveHistory(); }

    private void SaveSettings() => Write(paths.SettingsFile, PersistedSettings.From(Settings));
    private void SaveUiState() => Write(paths.UiStateFile, UiState);

    // 两类历史合并为一个数组落盘（条目通过 Tool 字段区分）
    private void SaveHistory() => Write(paths.TextHistoryFile, quote.Concat(space).ToArray());

    // 按各自的设置上限裁剪最旧记录
    private void Trim() { Trim(GetList(TextToolId.QuoteConversion), Settings.QuoteHistoryLimit); Trim(GetList(TextToolId.SpaceRemoval), Settings.SpaceHistoryLimit); }
    private static void Trim(List<HistoryEntry> items, int limit) { if (items.Count > limit) items.RemoveRange(0, items.Count - limit); }

    private List<HistoryEntry> GetList(TextToolId tool) => tool == TextToolId.QuoteConversion ? quote : tool == TextToolId.SpaceRemoval ? space : throw new ArgumentOutOfRangeException(nameof(tool));

    // 三个状态文件是否都已完整可读（迁移服务用于校验迁移结果）
    internal static bool HasCompleteValidState(AppPaths paths) => TryRead(paths.SettingsFile, out PersistedSettings settings) && IsValid(settings) && TryRead(paths.UiStateFile, out TextUiState ui) && IsValid(ui) && TryRead(paths.TextHistoryFile, out HistoryEntry[] _);
    private static bool IsValid(PersistedSettings settings) { try { settings.Validate(); return true; } catch { return false; } }
    private static bool IsValid(TextUiState state) { try { state.Validate(); return true; } catch { return false; } }

    // 供迁移服务写入设置文件
    internal static void WriteSettings(string path, AppSettings settings) => Write(path, PersistedSettings.From(settings));

    // 原子写入：先写随机后缀的临时文件，再 Move 覆盖正式文件，避免中途崩溃留下半截文件
    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Document<T>(1, value), Options));
        File.Move(temporary, path, true);
    }

    // 读取文件：不存在或解析/校验失败时返回 fallback
    private static T Read<T>(string path, T fallback, Action<T> validate)
    {
        if (!TryRead(path, out T value)) return fallback;
        try { validate(value); return value; } catch { return fallback; }
    }

    // 尝试解析文件；格式非法、版本不符或值为空时返回 false
    private static bool TryRead<T>(string path, out T value)
    {
        value = default!;
        try
        {
            var doc = JsonSerializer.Deserialize<Document<T>>(File.ReadAllText(path), Options);
            if (doc is null || doc.SchemaVersion != 1 || doc.Value is null) return false;
            value = doc.Value;
            return true;
        }
        catch { return false; }
    }

    // 设置的可持久化形态：把 Keys 枚举转换为 int，便于稳定存储；
    // UpdateChannel 缺省（旧配置文件）时回退为 Stable
    private sealed record PersistedSettings(int HotKeyModifiers, int HotKeyKey, int QuoteHistoryLimit, int SpaceHistoryLimit, int UpdateChannel = 0)
    {
        public static PersistedSettings Default => From(AppSettings.Default);
        public static PersistedSettings From(AppSettings settings) => new((int)settings.HistoryHotKey.Modifiers, (int)settings.HistoryHotKey.Key, settings.QuoteHistoryLimit, settings.SpaceHistoryLimit, (int)settings.UpdateChannel);
        public AppSettings ToSettings() => new(HotKeyBinding.Create((System.Windows.Forms.Keys)HotKeyModifiers, (System.Windows.Forms.Keys)HotKeyKey), QuoteHistoryLimit, SpaceHistoryLimit, (UpdateChannel)UpdateChannel);
        public void Validate() => ToSettings().Validate();
    }

    // 磁盘文件外壳：schemaVersion 用于版本校验
    private sealed record Document<T>(int SchemaVersion, T Value);
}
