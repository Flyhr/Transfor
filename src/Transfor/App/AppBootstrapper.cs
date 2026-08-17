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
        // Erise 服务器设置（erise-settings.json；损坏回退未配置，凭据不落盘）
        var eriseSettings = EriseSettingsStore.Load(paths);
        // 网络模式：默认强制直连（抖音为 CN 服务）；System 用系统代理；CustomProxy 用指定地址
        var networkMode = mediaState.Settings.NetworkMode;
        var proxyAddress = mediaState.Settings.ProxyAddress;
        // 共享 HttpClient：禁用自动重定向与 Cookie 容器，由 SafeHttpRequestSender 逐跳处理
        var httpClient = HttpClientProvider.Create(networkMode, proxyAddress);
        var dnsResolver = new SystemDnsResolver();
        var validator = new SafeUriValidator(dnsResolver);
        var requestSender = new SafeHttpRequestSender(httpClient, validator);
        // 浏览器会话代理：Edge CDP 实现由工厂延迟注入
        var browserSessions = new BrowserSessionAccessorProxy();
        // 媒体本地缓存：解析阶段预取的图片在此复用
        var mediaCache = new MediaCache(paths.MediaCacheDirectory);
        // 下载服务：流式 + 安全链路；Cookie 源经代理（未启用浏览器时为空）
        var downloadService = new MediaDownloadService(requestSender, browserSessions, mediaCache: mediaCache);
        // 更新服务（Phase 1 检查 + Phase 2 安装）：决策走 update-policy.json，下载安装走 Velopack；
        // 更新网络与媒体网络分离（更新访问 GitHub 走系统代理，不受媒体 Direct/代理设置影响）
        var updateService = new UpdateService(
            new HttpUpdatePolicySource(HttpClientProvider.CreateForUpdates(), validator),
            AppVersion.Current);
        // 浏览器服务（Phase 3）：WebView2 独立 Profile（Cookie/登录态持久化于 Browser\UserData）；
        // 浏览器网络与媒体下载一致（Direct/System/CustomProxy 随媒体设置）
        var browserService = new BrowserService(
            new BrowserProfileService(paths.BrowserProfileDirectory),
            networkMode,
            proxyAddress);
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
        // 批次下载完成即关闭浏览器会话（可恢复：下次解析/下载自动重启，Cookie 保留）
        downloadCoordinator.BatchCompleted += async (_, _) =>
        {
            try
            {
                await browserSessions.CloseBrowserAsync(CancellationToken.None);
            }
            catch
            {
                // 关闭失败不影响下载结果
            }
        };

        return new AppServices
        {
            State = textState,
            EriseSettings = eriseSettings,
            Updates = updateService,
            Browser = browserService,
            // 更新安装器工厂：按当前通道创建（Velopack GitHub 源，Beta 读取预发布）
            UpdateInstallerFactory = channel => new VelopackUpdateInstaller(channel),
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
                RequestSender = requestSender,
                Preview = new MediaPreviewService(requestSender, browserSessions, mediaCache),
                // 浏览器会话工厂：WebView2 隐藏宿主（Phase 4A，替代 Edge CDP）；
                // 与「浏览器」页共享 Profile（登录一次互通），首次解析/下载时惰性初始化；
                // 注入 SafeUriValidator：下载/捕获入口校验媒体与链接地址（防 SSRF）
                BrowserSessionFactory = owner => new WebView2BrowserSessionAccessor(owner, browserService, paths, validator),
            },
        };
    }
}
