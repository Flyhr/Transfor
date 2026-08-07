using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Transfor;

// 浏览器服务（Task 3.2）：WebView2 环境与 Profile 生命周期管理；
// 独立 UserData 目录持久化 Cookie/登录态；初始化失败降级为错误提示，绝不影响应用启动
internal sealed class BrowserService : IBrowserService, IDisposable
{
    private readonly BrowserProfileService profile;
    private BrowserNavigationService? navigation;
    private BrowserCookieService? cookies;
    private string? initializationError;
    private WebView2? control;

    public BrowserService(BrowserProfileService profile)
    {
        this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public BrowserProfileService Profile => profile;

    // 是否已成功初始化（浏览器页打开过且 WebView2 就绪）
    public bool IsInitialized => navigation is not null;

    public BrowserNavigationService Navigation => navigation ?? throw new InvalidOperationException("浏览器尚未初始化。");

    public BrowserCookieService Cookies => cookies ?? throw new InvalidOperationException("浏览器尚未初始化。");

    public string? InitializationError => initializationError;

    // 在 UI 线程调用：创建独立 Profile 的 WebView2 环境并初始化控件；
    // 失败时记录原因并抛出，由页面降级显示提示（不崩溃）
    public async Task InitializeAsync(WebView2 webView2)
    {
        ArgumentNullException.ThrowIfNull(webView2);
        if (navigation is not null)
        {
            return;
        }

        try
        {
            profile.EnsureCreated();
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: profile.UserDataFolder).ConfigureAwait(true);
            await webView2.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            control = webView2;
            navigation = new BrowserNavigationService(webView2.CoreWebView2);
            cookies = new BrowserCookieService(webView2.CoreWebView2);
            initializationError = null;
        }
        catch (Exception ex)
        {
            initializationError = $"浏览器初始化失败：{ex.Message}（需要 Microsoft Edge WebView2 Runtime，Windows 11 已内置）。";
            throw;
        }
    }

    // 释放浏览器资源（退出应用时由 AppServices 统一释放）；
    // WebView2 控件的进程回收由控件 Dispose（随主窗口关闭）完成，这里仅清除引用
    public void Dispose()
    {
        navigation = null;
        cookies = null;
        control = null;
    }
}
