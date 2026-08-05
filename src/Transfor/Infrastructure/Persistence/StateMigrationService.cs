using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace Transfor;

internal static class StateMigrationService
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    public static void EnsureMigrated(AppPaths paths)
    {
        if (File.Exists(paths.PendingMigrationFile)) { Recover(paths); return; }
        if (TextStateStore.HasCompleteValidState(paths) || !TryReadLegacy(paths.LegacyStateFile, out var snapshot)) return;
        Migrate(paths, snapshot);
    }
    private static void Recover(AppPaths paths)
    {
        if (TryReadLegacy(paths.LegacyStateFile, out var snapshot)) { Migrate(paths, snapshot); return; }
        if (TextStateStore.HasCompleteValidState(paths)) { File.Delete(paths.PendingMigrationFile); }
    }
    private static void Migrate(AppPaths paths, LegacySnapshot snapshot)
    {
        Directory.CreateDirectory(paths.ApplicationDirectory);
        File.WriteAllText(paths.PendingMigrationFile, "{\"schemaVersion\":1}");
        var settingsStage = paths.SettingsFile + ".stage"; var uiStage = paths.UiStateFile + ".stage"; var historyStage = paths.TextHistoryFile + ".stage";
        TextStateStore.WriteSettings(settingsStage, snapshot.Settings); TextStateStore.Write(uiStage, snapshot.UiState); TextStateStore.Write(historyStage, snapshot.History.ToArray());
        File.Move(settingsStage, paths.SettingsFile, true); File.Move(uiStage, paths.UiStateFile, true); File.Move(historyStage, paths.TextHistoryFile, true);
        if (!TextStateStore.HasCompleteValidState(paths)) return;
        if (File.Exists(paths.LegacyStateFile)) File.Move(paths.LegacyStateFile, paths.LegacyBackupFile, true);
        File.Delete(paths.PendingMigrationFile);
    }
    private static bool TryReadLegacy(string path, out LegacySnapshot snapshot)
    {
        snapshot = default!; if (!File.Exists(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path)); var root = document.RootElement;
            var persisted = root.GetProperty("Settings");
            var hotKey = HotKeyBinding.Create((Keys)persisted.GetProperty("HotKeyModifiers").GetInt32(), (Keys)persisted.GetProperty("HotKeyKey").GetInt32());
            var tool = Enum.Parse<TextToolId>(persisted.GetProperty("LastViewedTool").GetString()!, true);
            var settings = new AppSettings(hotKey, persisted.GetProperty("QuoteHistoryLimit").GetInt32(), persisted.GetProperty("SpaceHistoryLimit").GetInt32()); settings.Validate();
            var history = JsonSerializer.Deserialize<List<HistoryEntry>>(root.GetProperty("History").GetRawText(), Options) ?? [];
            if (history.Any(entry => !Enum.IsDefined(entry.Tool) || entry.OriginalInput is null || entry.ConvertedOutput is null)) return false;
            snapshot = new LegacySnapshot(settings, new TextUiState(tool), history); return true;
        }
        catch { return false; }
    }
    private sealed record LegacySnapshot(AppSettings Settings, TextUiState UiState, List<HistoryEntry> History);
}