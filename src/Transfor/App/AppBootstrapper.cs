namespace Transfor;

// 组合根：在应用启动时负责组装整个依赖图
internal static class AppBootstrapper
{
    public static AppServices Create()
    {
        // 状态目录固定为 %LOCALAPPDATA%\Transfor
        var paths = AppPaths.Default;
        // 首次启动新版时，把旧版单一 state.json 迁移为拆分后的三个状态文件
        StateMigrationService.EnsureMigrated(paths);
        // 从磁盘加载设置、界面状态与文本历史（文件损坏时自动回退到默认值）
        var state = TextStateStore.Load(paths);
        return new AppServices
        {
            State = state,
            // 全局快捷键管理器：负责系统级热键的注册/替换/释放
            HotKeys = new GlobalHotKeyManager(),
            // 粘贴协调器：写剪贴板 → 恢复目标窗口 → 模拟 Ctrl+V
            PasteCoordinator = new PasteCoordinator(new WindowsClipboardService(), new WindowsWindowInputService()),
        };
    }
}
