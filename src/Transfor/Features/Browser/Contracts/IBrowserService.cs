using Microsoft.Web.WebView2.WinForms;

namespace Transfor;

// 浏览器统一门面（Phase 3）：管理 WebView2 环境/Profile 生命周期与各能力子服务；
// 页面（BrowserView）只通过本门面访问浏览器，禁止业务代码直接创建 WebView2
internal interface IBrowserService
{
    // Profile 管理（独立持久化目录）
    BrowserProfileService Profile { get; }

    // 是否已成功初始化（浏览器页打开过且 WebView2 就绪）
    bool IsInitialized { get; }

    // 导航能力（后退/前进/刷新/停止/地址规范化），初始化后可用
    BrowserNavigationService Navigation { get; }

    // Cookie 管理（读取/清除/清空全部浏览器数据），初始化后可用
    BrowserCookieService Cookies { get; }

    // 初始化失败原因（如 WebView2 Runtime 缺失）；未失败为 null
    string? InitializationError { get; }

    // 在 UI 线程调用：创建独立 Profile 环境的 WebView2 并初始化控件；
    // 失败时设置 InitializationError 并抛出（页面降级显示提示，不崩溃）
    Task InitializeAsync(WebView2 webView2);
}
