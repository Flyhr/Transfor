using System.Text.Json;
using System.Text.Json.Serialization;

namespace Transfor;

internal sealed class TextStateStore : ITextHistoryRepository
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly AppPaths paths;
    private readonly List<HistoryEntry> quote = new();
    private readonly List<HistoryEntry> space = new();
    private TextStateStore(AppPaths paths, AppSettings settings, TextUiState uiState, IEnumerable<HistoryEntry> history)
    {
        this.paths = paths; Settings = settings; UiState = uiState;
        foreach (var entry in history) GetList(entry.Tool).Add(entry);
        Trim();
    }
    public AppSettings Settings { get; private set; }
    public TextUiState UiState { get; private set; }
    public static TextStateStore Load(AppPaths paths)
    {
        var persistedSettings = Read(paths.SettingsFile, PersistedSettings.Default, static value => value.Validate());
        var settings = persistedSettings.ToSettings();
        var ui = Read(paths.UiStateFile, TextUiState.Default, static value => value.Validate());
        var history = Read(paths.TextHistoryFile, Array.Empty<HistoryEntry>(), static _ => { });
        return new TextStateStore(paths, settings, ui, history);
    }
    public IReadOnlyList<HistoryEntry> GetHistory(TextToolId tool) => GetList(tool).AsReadOnly();
    public void Add(HistoryEntry entry) { ArgumentNullException.ThrowIfNull(entry); GetList(entry.Tool).Add(entry); Trim(); SaveHistory(); }
    public void ClearHistory(TextToolId tool) { GetList(tool).Clear(); SaveHistory(); }
    public void UpdateSettings(AppSettings settings) { settings.Validate(); Settings = settings; Trim(); SaveSettings(); SaveHistory(); }
    public void SetLastViewedTool(TextToolId tool) { var state = new TextUiState(tool); state.Validate(); UiState = state; SaveUiState(); }
    public void Save() { SaveSettings(); SaveUiState(); SaveHistory(); }
    private void SaveSettings() => Write(paths.SettingsFile, PersistedSettings.From(Settings));
    private void SaveUiState() => Write(paths.UiStateFile, UiState);
    private void SaveHistory() => Write(paths.TextHistoryFile, quote.Concat(space).ToArray());
    private void Trim() { Trim(GetList(TextToolId.QuoteConversion), Settings.QuoteHistoryLimit); Trim(GetList(TextToolId.SpaceRemoval), Settings.SpaceHistoryLimit); }
    private static void Trim(List<HistoryEntry> items, int limit) { if (items.Count > limit) items.RemoveRange(0, items.Count - limit); }
    private List<HistoryEntry> GetList(TextToolId tool) => tool == TextToolId.QuoteConversion ? quote : tool == TextToolId.SpaceRemoval ? space : throw new ArgumentOutOfRangeException(nameof(tool));
    internal static bool HasCompleteValidState(AppPaths paths) => TryRead(paths.SettingsFile, out PersistedSettings settings) && IsValid(settings) && TryRead(paths.UiStateFile, out TextUiState ui) && IsValid(ui) && TryRead(paths.TextHistoryFile, out HistoryEntry[] _);
    private static bool IsValid(PersistedSettings settings) { try { settings.Validate(); return true; } catch { return false; } }
    private static bool IsValid(TextUiState state) { try { state.Validate(); return true; } catch { return false; } }
    internal static void WriteSettings(string path, AppSettings settings) => Write(path, PersistedSettings.From(settings));
    internal static void Write<T>(string path, T value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + ".tmp." + Guid.NewGuid().ToString("N"); File.WriteAllText(temporary, JsonSerializer.Serialize(new Document<T>(1, value), Options)); File.Move(temporary, path, true); }
    private static T Read<T>(string path, T fallback, Action<T> validate) { if (!TryRead(path, out T value)) return fallback; try { validate(value); return value; } catch { return fallback; } }
    private static bool TryRead<T>(string path, out T value) { value = default!; try { var doc = JsonSerializer.Deserialize<Document<T>>(File.ReadAllText(path), Options); if (doc is null || doc.SchemaVersion != 1 || doc.Value is null) return false; value = doc.Value; return true; } catch { return false; } }
    private sealed record PersistedSettings(int HotKeyModifiers, int HotKeyKey, int QuoteHistoryLimit, int SpaceHistoryLimit)
    {
        public static PersistedSettings Default => From(AppSettings.Default);
        public static PersistedSettings From(AppSettings settings) => new((int)settings.HistoryHotKey.Modifiers, (int)settings.HistoryHotKey.Key, settings.QuoteHistoryLimit, settings.SpaceHistoryLimit);
        public AppSettings ToSettings() => new(HotKeyBinding.Create((System.Windows.Forms.Keys)HotKeyModifiers, (System.Windows.Forms.Keys)HotKeyKey), QuoteHistoryLimit, SpaceHistoryLimit);
        public void Validate() => ToSettings().Validate();
    }    private sealed record Document<T>(int SchemaVersion, T Value);
}