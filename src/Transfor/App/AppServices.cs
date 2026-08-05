namespace Transfor;

// 应用服务容器：集中持有各单例服务，随应用生命周期创建与释放
internal sealed class AppServices : IDisposable
{
    // 内存中的设置、界面状态与文本历史（每次修改后立即落盘）
    public required TextStateStore State { get; init; }

    // 全局快捷键管理器
    public required GlobalHotKeyManager HotKeys { get; init; }

    // 粘贴协调器：把历史结果粘贴回呼出前的窗口
    public required PasteCoordinator PasteCoordinator { get; init; }

    // 退出时释放系统级全局热键
    public void Dispose() => HotKeys.Dispose();
}
