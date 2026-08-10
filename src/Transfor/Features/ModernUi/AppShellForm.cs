using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Transfor;

// 新 UI 宿主窗体（Phase 5A）：WinForms Host + AppWebView（加载本地 webui HTML）；
// 安全隔离：AppWebView 使用独立 Profile（AppUiProfileDirectory，与互联网浏览器 Profile 严格分离）、
// 禁止一切外部导航；Web UI 仅能经 App Bridge JSON 协议访问应用服务
internal sealed class AppShellForm : Form
{
    private readonly AppBridge bridge;
    private readonly MediaDownloadCoordinator downloadCoordinator;
    private readonly string appUiProfileDirectory;
    private readonly WebView2 webView;
    private AppBridgeEvents? events;

    public AppShellForm(AppBridge bridge, string appUiProfileDirectory, MediaDownloadCoordinator downloadCoordinator)
    {
        this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        this.appUiProfileDirectory = appUiProfileDirectory ?? throw new ArgumentNullException(nameof(appUiProfileDirectory));
        this.downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));

        Text = "Transfor";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        Size = new Size(1100, 720);
        Font = new Font("Microsoft YaHei UI", 10F);

        webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(webView);

        Load += (_, _) => InitializeAsync();
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
                json =>
                {
                    if (webView.CoreWebView2 is { } eventCore)
                    {
                        var eventScript = $"window.__bridgeDeliver({System.Text.Json.JsonSerializer.Serialize(json)})";
                        // 事件注入失败（页面销毁等）静默忽略，不影响下载批次
                        _ = eventCore.ExecuteScriptAsync(eventScript)
                            .ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    }
                });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"新界面初始化失败：{ex.Message}", "Transfor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

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
