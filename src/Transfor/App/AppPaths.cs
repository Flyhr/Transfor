namespace Transfor;

// 应用状态文件路径集合：统一管理状态目录下各类文件的路径
internal sealed class AppPaths
{
    public AppPaths(string applicationDirectory) => ApplicationDirectory = Path.GetFullPath(applicationDirectory);

    // 状态目录（生产环境为 %LOCALAPPDATA%\Transfor，测试可注入临时目录）
    public string ApplicationDirectory { get; }

    // 旧版（拆分前）的单一状态文件，迁移成功后会被改名备份
    public string LegacyStateFile => Path.Combine(ApplicationDirectory, "state.json");

    // 旧版状态迁移成功后的备份文件
    public string LegacyBackupFile => Path.Combine(ApplicationDirectory, "state.v1.backup.json");

    // 设置（快捷键、历史上限）
    public string SettingsFile => Path.Combine(ApplicationDirectory, "settings.json");

    // 界面状态（最近查看的工具等）
    public string UiStateFile => Path.Combine(ApplicationDirectory, "ui-state.json");

    // 文本转换历史记录
    public string TextHistoryFile => Path.Combine(ApplicationDirectory, "text-history.json");

    // 迁移标记文件：迁移中途失败时保留，下次启动据此恢复或重试
    public string PendingMigrationFile => Path.Combine(ApplicationDirectory, "migration.v1.pending.json");

    // 媒体下载设置（独立于文本设置）
    public string MediaSettingsFile => Path.Combine(ApplicationDirectory, "media-settings.json");

    // 媒体下载批次历史（独立于文本历史）
    public string MediaDownloadHistoryFile => Path.Combine(ApplicationDirectory, "download-history.json");

    // 专用 Edge 持久化配置目录（登录态/Cookie/缓存只存于此，不写入普通 JSON）
    public string EdgeProfileDirectory => Path.Combine(ApplicationDirectory, "Edge", "Douyin");

    // WebView2 浏览器独立 Profile 目录（Cookie/LocalStorage/缓存/登录状态持久化，Task 3.3）
    public string BrowserProfileDirectory => Path.Combine(ApplicationDirectory, "Browser", "UserData");

    // 新 UI（Phase 5）AppWebView 独立 Profile 目录（与互联网浏览器 Profile 严格隔离）
    public string AppUiProfileDirectory => Path.Combine(ApplicationDirectory, "Browser", "AppUi");

    // 媒体本地缓存目录（解析阶段预取的图片响应，按 URL 哈希命名）
    public string MediaCacheDirectory => Path.Combine(ApplicationDirectory, "MediaCache");

    // 默认状态目录：%LOCALAPPDATA%\Transfor
    public static AppPaths Default => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transfor"));
}
