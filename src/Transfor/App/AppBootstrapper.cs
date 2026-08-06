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
        var textState = TextStateStore.Load(paths);
        // 媒体状态独立加载（media-settings.json 与 download-history.json）
        var mediaState = MediaStateStore.Load(paths);
        // 共享 HttpClient：禁用自动重定向与 Cookie 容器，由 SafeHttpRequestSender 逐跳处理
        var httpClient = HttpClientProvider.Create();
        var dnsResolver = new SystemDnsResolver();
        var validator = new SafeUriValidator(dnsResolver);
        var requestSender = new SafeHttpRequestSender(httpClient, validator);
        // 浏览器会话代理：Edge CDP 实现由工厂延迟注入
        var browserSessions = new BrowserSessionAccessorProxy();
        // 下载服务：流式 + 安全链路；Cookie 源经代理（未启用浏览器时为空）
        var downloadService = new MediaDownloadService(requestSender, browserSessions);
        // 解析器注册：专用解析器（抖音）优先，Direct 最后兜底
        // 抖音传输偏好为会话级熔断状态（进程内共享，重启复位）
        var douyinPreference = new DouyinTransportPreferenceState();
        var registry = new MediaResolverRegistry(new IMediaResolver[]
        {
            new DouyinMediaResolver(new DouyinHttpPageResolver(requestSender), browserSessions, douyinPreference),
            new DirectMediaResolver(requestSender, browserSessions),
        });
        var resolveCoordinator = new MediaResolveCoordinator(registry);
        var downloadCoordinator = new MediaDownloadCoordinator(downloadService, mediaState);

        return new AppServices
        {
            State = textState,
            // 全局快捷键管理器：负责系统级热键的注册/替换/释放
            HotKeys = new GlobalHotKeyManager(),
            // 粘贴协调器：写剪贴板 → 恢复目标窗口 → 模拟 Ctrl+V
            PasteCoordinator = new PasteCoordinator(new WindowsClipboardService(), new WindowsWindowInputService()),
            Media = new MediaServices
            {
                State = mediaState,
                ResolveCoordinator = resolveCoordinator,
                DownloadCoordinator = downloadCoordinator,
                BrowserSessions = browserSessions,
                HttpClient = httpClient,
                Preview = new MediaPreviewService(requestSender, browserSessions),
                // 浏览器会话工厂：真实 Edge + CDP（独立持久化配置目录），首次使用时惰性启动
                BrowserSessionFactory = owner => new EdgeCdpBrowserSessionAccessor(owner, paths),
            },
        };
    }
}
