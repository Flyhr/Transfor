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
            // 安全：禁止外部导航与新窗口（本地 UI 只加载嵌入 HTML）；
            // NavigateToString 产生 data: URI——必须放行 data:/about:blank，
            // 其余（http/https/file 等外部导航）一律拦截
            core.NavigationStarting += (_, e) =>
            {
                var isLocalContent = e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase);
                if (!isLocalContent)
                {
                    e.Cancel = true;
                }
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            // App Bridge：JSON 消息协议
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigateToString(html);

            // 事件推送：下载协调器事件 → UI 线程 → PostWebMessageAsJson；
            // 随窗体生命周期挂接/摘除
            events = new AppBridgeEvents(
                downloadCoordinator,
                webView,
                json => webView.CoreWebView2?.PostWebMessageAsJson(json));
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

    // 消息桥接：请求分发到 AppBridge，响应回发 Web UI（异步不阻塞 UI 线程）
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
            if (reply is not null && !webView.IsDisposed)
            {
                webView.CoreWebView2?.PostWebMessageAsJson(reply);
            }
        }
        catch
        {
            // Bridge 异常不应影响宿主窗体
        }
    }
}
