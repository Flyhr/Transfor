using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Transfor;

// 新 UI 宿主窗体（Phase 5A + 6.5）：WinForms Host + C# 侧边栏 + AppWebView（本地 webui）+ 互联网浏览器控件；
// 明确布局容器：左列 200px C# 侧边栏（导航/主题/版本），右列内容区（AppWebView + 浏览器控件叠放）；
// 安全隔离：AppWebView 使用独立 Profile（AppUiProfileDirectory）禁止外部导航，仅经 Bridge 访问服务；
// 浏览器控件（互联网页面）使用 Browser\UserData（与隐藏宿主/媒体解析共享登录态），不挂 Bridge，
// 只覆盖内容区（顶部留 56px 给 HTML 地址栏）——侧边栏/导航始终可达
internal sealed class AppShellForm : Form
{
    // HTML 浏览器页顶部工具条高度（浏览器控件从此处以下覆盖内容区）
    private const int BrowserToolbarHeight = 64;
    // 宿主侧边栏宽度
    private const int SidebarWidth = 92;

    private static readonly (string Page, string Label)[] NavItems =
    {
        ("home", "工作台"),
        ("media", "媒体"),
        ("browser", "浏览器"),
        ("history", "历史"),
        ("settings", "设置"),
    };

    private readonly AppBridge bridge;
    private readonly BrowserService browserService;
    private readonly MediaDownloadCoordinator downloadCoordinator;
    private readonly string appUiProfileDirectory;
    private readonly WebView2 webView;
    private readonly Panel contentArea;
    private readonly Panel browserPanel;
    private readonly WebView2 browserWebView;
    private readonly Dictionary<string, Button> navButtons = new(StringComparer.Ordinal);
    private AppBridgeEvents? events;
    private BrowserNavigationService? browserNavigation;
    private long navigationVersion;
    private Panel? sidebar;
    private Label? sidebarVersionLabel;
    private string? activeNavPage;
    private bool sidebarDark;

    public AppShellForm(
        AppBridge bridge,
        BrowserService browserService,
        string appUiProfileDirectory,
        MediaDownloadCoordinator downloadCoordinator)
    {
        this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        this.browserService = browserService ?? throw new ArgumentNullException(nameof(browserService));
        this.appUiProfileDirectory = appUiProfileDirectory ?? throw new ArgumentNullException(nameof(appUiProfileDirectory));
        this.downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));

        Text = "Transfor";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        Size = new Size(1100, 720);
        Font = new Font("Microsoft YaHei UI", 10F);

        // 明确布局容器：左列 C# 侧边栏（200px） + 右列内容区
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SidebarWidth));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildSidebar(), 0, 0);

        // 内容区：AppWebView 底层 + 浏览器控件顶层（仅内容区，顶部留白给 HTML 地址栏）
        contentArea = new Panel { Dock = DockStyle.Fill };
        webView = new WebView2 { Dock = DockStyle.Fill };
        browserPanel = new Panel { Visible = false, Location = new Point(0, BrowserToolbarHeight) };
        browserWebView = new WebView2 { Dock = DockStyle.Fill };
        browserPanel.Controls.Add(browserWebView);
        contentArea.Controls.Add(webView);
        contentArea.Controls.Add(browserPanel);
        root.Controls.Add(contentArea, 1, 0);
        Controls.Add(root);
        contentArea.Resize += (_, _) => LayoutBrowserPanel();

        // Bridge 浏览器能力：页面切换显隐 + 侧边栏高亮双向同步
        bridge.SetBrowserVisible = visible =>
        {
            browserPanel.Visible = visible;
            LayoutBrowserPanel();
        };
        bridge.SetActiveNav = page => SetActiveNavButton(page);
        bridge.SetTheme = dark => ApplySidebarTheme(dark);
        // 下载目录浏览（系统目录对话框）
        bridge.BrowseDirectory = () =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择默认下载目录" };
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
        };
        // 浏览器数据清除（共享 Browser\UserData 的 Profile）
        bridge.ClearBrowserData = async scope =>
        {
            if (browserWebView.CoreWebView2 is not { } core)
            {
                throw new InvalidOperationException("浏览器尚未初始化。");
            }

            var kinds = scope switch
            {
                "cookies" => CoreWebView2BrowsingDataKinds.Cookies,
                // DiskCache 才是 WebView2 实际磁盘 HTTP/资源缓存（CacheStorage 是 DOM Cache API）
                "cache" => CoreWebView2BrowsingDataKinds.DiskCache,
                _ => CoreWebView2BrowsingDataKinds.Cookies
                    | CoreWebView2BrowsingDataKinds.DiskCache
                    | CoreWebView2BrowsingDataKinds.AllDomStorage,
            };
            await core.Profile.ClearBrowsingDataAsync(kinds);
        };

        Load += (_, _) => InitializeAsync();
    }

    // C# 侧边栏：导航按钮 + 底部版本/主题（配色随 HTML 主题联动）
    private Control BuildSidebar()
    {
        sidebar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(247, 251, 251),
            Padding = new Padding(8),
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var navPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        foreach (var (page, label) in NavItems)
        {
            var button = new Button
            {
                Text = label,
                AutoSize = false,
                Height = 54,
                Width = SidebarWidth - 16,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(43, 71, 76),
                BackColor = Color.Transparent,
                FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(234, 248, 246), MouseDownBackColor = Color.FromArgb(220, 244, 240) },
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Font.FontFamily, 8F),
                Cursor = Cursors.Hand,
                Tag = page,
            };
            button.Click += (_, _) => NavigateTo(page);
            navButtons[page] = button;
            navPanel.Controls.Add(button);
        }
        layout.Controls.Add(navPanel, 0, 0);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sidebarVersionLabel = new Label { Text = "v" + AppVersion.Current, ForeColor = Color.FromArgb(100, 123, 128), AutoSize = true, Padding = new Padding(2, 6, 2, 2) };
        footer.Controls.Add(sidebarVersionLabel, 0, 0);
        layout.Controls.Add(footer, 0, 1);

        sidebar.Controls.Add(layout);
        return sidebar;
    }

    // 侧边栏配色随主题（深色/浅色）联动；HTML 切换主题或系统主题变化时由 Bridge 回调
    private void ApplySidebarTheme(bool dark)
    {
        sidebarDark = dark;
        if (sidebar is null)
        {
            return;
        }

        var background = Color.FromArgb(247, 251, 251);
        var text = Color.FromArgb(43, 71, 76);
        var secondary = Color.FromArgb(100, 123, 128);
        var hover = Color.FromArgb(234, 248, 246);
        var active = Color.FromArgb(220, 244, 240);

        sidebar.BackColor = background;
        if (sidebarVersionLabel is not null)
        {
            sidebarVersionLabel.ForeColor = secondary;
        }

        foreach (var (key, button) in navButtons)
        {
            button.ForeColor = text;
            button.FlatAppearance.MouseOverBackColor = hover;
            button.FlatAppearance.MouseDownBackColor = active;
            var isActive = string.Equals(key, activeNavPage, StringComparison.Ordinal);
            button.BackColor = isActive ? active : Color.Transparent;
        }
    }

    // 导航到指定页面：驱动 HTML 页面切换 + 浏览器控件显隐 + 侧边栏高亮
    private void NavigateTo(string page)
    {
        SetActiveNavButton(page);
        ExecuteAppScript($"window.__navigateTo && window.__navigateTo({System.Text.Json.JsonSerializer.Serialize(page)});");
    }

    private void SetActiveNavButton(string? page)
    {
        activeNavPage = page;
        foreach (var (key, button) in navButtons)
        {
            var active = string.Equals(key, page, StringComparison.Ordinal);
            button.Font = new Font(Font, active ? FontStyle.Bold : FontStyle.Regular);
            button.BackColor = active ? Color.FromArgb(220, 244, 240) : Color.Transparent;
        }
    }

    // 在 AppWebView 中执行脚本（本地 UI；失败忽略）
    private void ExecuteAppScript(string script)
    {
        if (webView.IsDisposed || webView.CoreWebView2 is not { } core)
        {
            return;
        }

        _ = core.ExecuteScriptAsync(script)
            .ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    // 浏览器控件覆盖区域：内容区高度 - 顶部地址栏留白（随内容区尺寸变化）
    private void LayoutBrowserPanel()
    {
        browserPanel.Width = Math.Max(0, contentArea.ClientSize.Width);
        browserPanel.Height = Math.Max(0, contentArea.ClientSize.Height - BrowserToolbarHeight);
    }

    private async void InitializeAsync()
    {
        var html = WebUiResources.LoadIndexHtml();
        var styles = WebUiResources.LoadStylesCss();
        var script = WebUiResources.LoadAppScript();
        if (html is null || styles is null || script is null)
        {
            MessageBox.Show(this, "新界面资源缺失（嵌入资源未包含）。", "Transfor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        try
        {
            // 独立 Profile 环境：与互联网浏览器（Browser\UserData）严格隔离
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: appUiProfileDirectory);
            await webView.EnsureCoreWebView2Async(environment);

            var core = webView.CoreWebView2!;
            // 本地 UI 以虚拟主机映射加载（https 上下文）：data: URI 页面下
            // C# → JS 的 postMessage 响应会丢失（实测）；嵌入资源先落盘到临时目录
            var webRoot = Path.Combine(Path.GetTempPath(), "Transfor", "WebUi");
            Directory.CreateDirectory(webRoot);
            File.WriteAllText(Path.Combine(webRoot, "index.html"), html);
            File.WriteAllText(Path.Combine(webRoot, "styles.css"), styles);
            File.WriteAllText(Path.Combine(webRoot, "app.js"), script);
            core.SetVirtualHostNameToFolderMapping(
                "appassets.transfor",
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            // 安全：只放行本地虚拟主机与 about:blank，其余（外部导航/新窗口）一律拦截
            core.NavigationStarting += (_, e) =>
            {
                var isLocalContent = e.Uri.StartsWith("https://appassets.transfor/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase);
                if (!isLocalContent)
                {
                    e.Cancel = true;
                }
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            // App Bridge：JSON 消息协议
            core.WebMessageReceived += OnWebMessageReceived;
            core.Navigate("https://appassets.transfor/index.html");

            // 页面加载后由 HTML 推送当前主题（同步宿主侧边栏配色）
            core.NavigationCompleted += (_, _) =>
                ExecuteAppScript("window.__notifyTheme && window.__notifyTheme();");

            // 事件推送：下载协调器事件 → UI 线程 → 经 ExecuteScriptAsync 注入
            // window.__bridgeDeliver（与请求响应同一条可靠通道；postMessage 事件不可达）；
            // 随窗体生命周期挂接/摘除
            events = new AppBridgeEvents(
                downloadCoordinator,
                webView,
                json => PushEventJson(json));

            // 互联网浏览器控件：共享 Browser\UserData（与隐藏宿主/媒体解析登录态互通），
            // 不挂 Bridge；顶部 56px 留给 HTML 地址栏；
            // 初始化失败单独隔离（不影响新界面其他功能），浏览器页显示不可用提示
            try
            {
                var browserEnvironment = await browserService.GetEnvironmentAsync().ConfigureAwait(true);
                await browserWebView.EnsureCoreWebView2Async(browserEnvironment).ConfigureAwait(true);
                browserNavigation = new BrowserNavigationService(browserWebView, browserWebView.CoreWebView2);
                bridge.BrowserNavigation = browserNavigation;
                // 外部窗口：拦截并在当前控件导航（明确产品行为）
                browserWebView.CoreWebView2.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    if (!string.IsNullOrWhiteSpace(e.Uri))
                    {
                        browserWebView.CoreWebView2.Navigate(e.Uri);
                    }
                };
                browserWebView.CoreWebView2.NavigationStarting += (_, e) =>
                {
                    PushEvent("browserNavigated", new
                    {
                        url = e.Uri,
                        canGoBack = browserNavigation.CanGoBack,
                        canGoForward = browserNavigation.CanGoForward,
                        success = true,
                        error = (string?)null,
                    });
                };
                browserWebView.CoreWebView2.NavigationCompleted += async (_, e) =>
                {
                    // 导航版本：快速切换页面时旧检测结果作废（防跨导航竞态）
                    var version = ++navigationVersion;
                    var url = browserNavigation.CurrentUrl;
                    PushEvent("browserNavigated", new
                    {
                        url,
                        canGoBack = browserNavigation.CanGoBack,
                        canGoForward = browserNavigation.CanGoForward,
                        success = e.IsSuccess,
                        error = e.IsSuccess ? null : e.WebErrorStatus.ToString(),
                    });
                    if (e.IsSuccess && url is not null)
                    {
                        await DetectPageMediaAsync(url, version).ConfigureAwait(true);
                    }
                };
            }
            catch (Exception browserEx)
            {
                // 浏览器不可用不影响新界面其他功能（文本工具/下载/历史照常）
                var category = ErrorClassifier.Classify(browserEx, ErrorCategory.Browser);
                AppLog.Browser.Warn($"[{category}] 浏览器初始化失败：{browserEx.Message}");
                PushEvent("browserUnavailable", new { message = browserEx.Message });
            }
        }
        catch (Exception ex)
        {
            var category = ErrorClassifier.Classify(ex, ErrorCategory.Browser);
            AppLog.Browser.Error($"[{category}] 新界面初始化失败：{ex.Message}", ex);
            MessageBox.Show(this, $"新界面初始化失败：{ex.Message}", "Transfor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    // 媒体检测：统计页面媒体候选数并推送；仅当导航版本与当前地址仍匹配时生效
    private async Task DetectPageMediaAsync(string url, long version)
    {
        try
        {
            var count = await BrowserCaptureSession.CountPageMediaAsync(browserWebView.CoreWebView2, CancellationToken.None)
                .ConfigureAwait(true);
            if (version == navigationVersion && string.Equals(browserNavigation?.CurrentUrl, url, StringComparison.Ordinal))
            {
                PushEvent("pageMediaDetected", new { count, url });
            }
        }
        catch
        {
            // 检测失败不打扰浏览
        }
    }

    // 事件/响应统一推送通道：ExecuteScriptAsync 注入 window.__bridgeDeliver
    private void PushEventJson(string json)
    {
        if (webView.IsDisposed || webView.CoreWebView2 is not { } core)
        {
            return;
        }

        var script = $"window.__bridgeDeliver({System.Text.Json.JsonSerializer.Serialize(json)})";
        _ = core.ExecuteScriptAsync(script)
            .ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void PushEvent(string eventName, object? data) =>
        PushEventJson(AppBridgeProtocol.CreateEvent(eventName, data));

    // 窗体关闭时摘除事件推送（协调器事件不再转发）
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            events?.Dispose();
            events = null;
        }

        base.Dispose(disposing);
    }

    // 消息桥接：请求分发到 AppBridge，响应经 ExecuteScriptAsync 注入回发 Web UI
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var requestJson = e.TryGetWebMessageAsString();
        if (requestJson is null)
        {
            return;
        }

        _ = DispatchAsync(requestJson);
    }

    private async Task DispatchAsync(string requestJson)
    {
        try
        {
            var reply = await bridge.HandleAsync(requestJson);
            if (reply is not null && !webView.IsDisposed && webView.CoreWebView2 is { } core)
            {
                // 响应经 ExecuteScriptAsync 注入 window.__bridgeDeliver：
                // postMessage 事件在本地 UI 页面实测不可达（JS 侧统一走 deliver 分发）
                var script = $"window.__bridgeDeliver({System.Text.Json.JsonSerializer.Serialize(reply)})";
                await core.ExecuteScriptAsync(script);
            }
        }
        catch
        {
            // Bridge 异常不应影响宿主窗体
        }
    }
}
