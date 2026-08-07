namespace Transfor;

// 应用服务容器：集中持有各单例服务，随应用生命周期创建与释放
internal sealed class AppServices : IDisposable, IAsyncDisposable
{
    // 内存中的设置、界面状态与文本历史（每次修改后立即落盘）
    public required TextStateStore State { get; init; }

    // 全局快捷键管理器
    public required GlobalHotKeyManager HotKeys { get; init; }

    // 更新服务：版本检查 + 更新安装器工厂（Velopack；下载/应用由 IUpdateInstaller 承担）
    public required UpdateService Updates { get; init; }

    // 更新安装器工厂：按更新通道创建（设置中切换通道后即时生效）
    public required Func<UpdateChannel, IUpdateInstaller> UpdateInstallerFactory { get; init; }

    // 浏览器服务（Phase 3）：WebView2 独立 Profile 环境 + 导航/Cookie/数据清理
    public required BrowserService Browser { get; init; }

    // 粘贴协调器：把历史结果粘贴回呼出前的窗口
    public required PasteCoordinator PasteCoordinator { get; init; }

    // 媒体模块服务组合
    public required MediaServices Media { get; init; }

    // 退出时释放全局热键、浏览器与媒体服务（下载取消、浏览器会话、HttpClient）
    public void Dispose()
    {
        HotKeys.Dispose();
        Browser.Dispose();
        Media.Dispose();
    }

    // 异步退出：媒体服务先取消下载并等待落定，再释放浏览器会话与 HttpClient
    public async ValueTask DisposeAsync()
    {
        HotKeys.Dispose();
        Browser.Dispose();
        await Media.DisposeAsync();
    }
}
