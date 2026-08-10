using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Transfor;

// 新 UI 宿主窗体（Phase 5A + 6.5）：WinForms Host + AppWebView（本地 webui）+ 互联网浏览器控件；
// 安全隔离：AppWebView 使用独立 Profile（AppUiProfileDirectory）禁止外部导航，仅经 Bridge 访问服务；
// 浏览器控件（互联网页面）使用 Browser\UserData（与隐藏宿主/媒体解析共享登录态），不挂 Bridge；
// 浏览器控件覆盖 AppWebView 下部（顶部留 56px 给 HTML 地址栏）
internal sealed class AppShellForm : Form
{
    // HTML 浏览器页顶部工具条高度（浏览器控件从此处以下覆盖）
    private const int BrowserToolbarHeight = 56;

    private readonly AppBridge bridge;
    private readonly BrowserService browserService;
    private readonly MediaDownloadCoordinator downloadCoordinator;
    private readonly string appUiProfileDirectory;
    private readonly WebView2 webView;
    private readonly Panel browserPanel;
    private readonly WebView2 browserWebView;
    private AppBridgeEvents? events;
    private BrowserNavigationService? browserNavigation;

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

        // AppWebView 底层全屏；浏览器控件顶层覆盖（顶部留白给 HTML 地址栏）
        var root = new Panel { Dock = DockStyle.Fill };
        webView = new WebView2 { Dock = DockStyle.Fill };
        browserPanel = new Panel { Visible = false, Location = new Point(0, BrowserToolbarHeight) };
        browserWebView = new WebView2 { Dock = DockStyle.Fill };
        browserPanel.Controls.Add(browserWebView);
        root.Controls.Add(webView);
        root.Controls.Add(browserPanel);
        Controls.Add(root);
        Resize += (_, _) => LayoutBrowserPanel();

        // Bridge 浏览器能力：导航操作 + 页面切换显隐
        bridge.SetBrowserVisible = visible =>
        {
            browserPanel.Visible = visible;
            LayoutBrowserPanel();
        };

        Load += (_, _) => InitializeAsync();
    }

    // 浏览器控件覆盖区域：窗体高度 - 顶部地址栏留白（随窗体尺寸变化）
    private void LayoutBrowserPanel()
    {
        browserPanel.Width = Math.Max(0, ClientSize.Width);
        browserPanel.Height = Math.Max(0, ClientSize.Height - BrowserToolbarHeight);
    }

    private async void InitializeAsync()
    {
        var html = WebUiResources.LoadIndexHtml();
        if (html is null)
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

            // 事件推送：下载协调器事件 → UI 线程 → 经 ExecuteScriptAsync 注入
            // window.__bridgeDeliver（与请求响应同一条可靠通道；postMessage 事件不可达）；
            // 随窗体生命周期挂接/摘除
            events = new AppBridgeEvents(
                downloadCoordinator,
                webView,
                json => PushEventJson(json));

            // 互联网浏览器控件：共享 Browser\UserData（与隐藏宿主/媒体解析登录态互通），
            // 不挂 Bridge；顶部 56px 留给 HTML 地址栏
            var browserEnvironment = await browserService.GetEnvironmentAsync().ConfigureAwait(true);
            await browserWebView.EnsureCoreWebView2Async(browserEnvironment).ConfigureAwait(true);
            browserNavigation = new BrowserNavigationService(browserWebView.CoreWebView2);
            bridge.BrowserNavigation = browserNavigation;
            browserWebView.CoreWebView2.NavigationStarting += (_, e) =>
            {
                PushEvent("browserNavigated", new { url = e.Uri, canGoBack = browserNavigation.CanGoBack, canGoForward = browserNavigation.CanGoForward });
            };
            browserWebView.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                PushEvent("browserNavigated", new { url = browserNavigation.CurrentUrl, canGoBack = browserNavigation.CanGoBack, canGoForward = browserNavigation.CanGoForward });
                if (e.IsSuccess && browserNavigation.CurrentUrl is { } url)
                {
                    // 媒体检测：页面加载完成后统计媒体候选数并推送
                    try
                    {
                        var count = await BrowserCaptureSession.CountPageMediaAsync(browserWebView.CoreWebView2, CancellationToken.None).ConfigureAwait(true);
                        PushEvent("pageMediaDetected", new { count, url });
                    }
                    catch
                    {
                        // 检测失败不打扰浏览
                    }
                }
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"新界面初始化失败：{ex.Message}", "Transfor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
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
