using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace Transfor;

// 旧状态迁移服务：把 v1 之前单一的 state.json 拆分迁移为 settings / ui-state / text-history 三个文件
internal static class StateMigrationService
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    // 启动时调用：处理上次中断的迁移、执行或跳过迁移
    public static void EnsureMigrated(AppPaths paths)
    {
        // 存在迁移标记说明上次迁移中断：先尝试恢复
        if (File.Exists(paths.PendingMigrationFile)) { Recover(paths); return; }
        // 新版状态已完整，或不存在旧 state.json 时无需迁移
        if (TextStateStore.HasCompleteValidState(paths) || !TryReadLegacy(paths.LegacyStateFile, out var snapshot)) return;
        Migrate(paths, snapshot);
    }

    // 迁移中断后的恢复：旧状态仍可读则重试迁移；否则若新版状态已完整则清除标记收尾
    private static void Recover(AppPaths paths)
    {
        if (TryReadLegacy(paths.LegacyStateFile, out var snapshot)) { Migrate(paths, snapshot); return; }
        if (TextStateStore.HasCompleteValidState(paths)) { File.Delete(paths.PendingMigrationFile); }
    }

    // 执行迁移：先写迁移标记与三个 .stage 临时文件，全部就绪后原子替换正式文件；
    // 成功后把旧 state.json 改名备份并清除标记；任一步不完整则保留标记等待下次恢复
    private static void Migrate(AppPaths paths, LegacySnapshot snapshot)
    {
        Directory.CreateDirectory(paths.ApplicationDirectory);
        File.WriteAllText(paths.PendingMigrationFile, "{\"schemaVersion\":1}");
        var settingsStage = paths.SettingsFile + ".stage"; var uiStage = paths.UiStateFile + ".stage"; var historyStage = paths.TextHistoryFile + ".stage";
        TextStateStore.WriteSettings(settingsStage, snapshot.Settings); TextStateStore.Write(uiStage, snapshot.UiState); TextStateStore.Write(historyStage, snapshot.History.ToArray());
        File.Move(settingsStage, paths.SettingsFile, true); File.Move(uiStage, paths.UiStateFile, true); File.Move(historyStage, paths.TextHistoryFile, true);
        // 校验落盘后的新状态是否完整可读，不完整时保留标记以便下次恢复
        if (!TextStateStore.HasCompleteValidState(paths)) return;
        // 迁移成功：备份旧文件、清除迁移标记
        if (File.Exists(paths.LegacyStateFile)) File.Move(paths.LegacyStateFile, paths.LegacyBackupFile, true);
        File.Delete(paths.PendingMigrationFile);
    }

    // 尝试读取并解析旧版 state.json；文件缺失、字段非法或历史条目不完整均视为不可迁移
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
            // 校验历史条目：工具枚举合法、输入输出非空
            if (history.Any(entry => !Enum.IsDefined(entry.Tool) || entry.OriginalInput is null || entry.ConvertedOutput is null)) return false;
            snapshot = new LegacySnapshot(settings, new TextUiState(tool), history); return true;
        }
        catch { return false; }
    }

    // 旧版状态在内存中的快照
    private sealed record LegacySnapshot(AppSettings Settings, TextUiState UiState, List<HistoryEntry> History);
}
