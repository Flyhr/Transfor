using Microsoft.Web.WebView2.Core;

namespace Transfor;

// 浏览器导航服务（Task 3.2）：地址规范化（纯逻辑可测）与导航动作；
// 实例绑定初始化后的 CoreWebView2，未初始化时动作被忽略
internal sealed class BrowserNavigationService
{
    private readonly CoreWebView2? core;

    public BrowserNavigationService(CoreWebView2? core)
    {
        this.core = core;
    }

    public bool CanGoBack => core?.CanGoBack ?? false;
    public bool CanGoForward => core?.CanGoForward ?? false;

    public void Back()
    {
        if (core is { CanGoBack: true })
        {
            core.GoBack();
        }
    }

    public void Forward()
    {
        if (core is { CanGoForward: true })
        {
            core.GoForward();
        }
    }

    public void Refresh() => core?.Reload();

    public void Stop() => core?.Stop();

    // 导航到规范化后的地址；地址非法抛 ArgumentException
    public void Navigate(string address) => core?.Navigate(NormalizeAddress(address));

    // 地址规范化：去首尾空白 → 无协议补 https:// → 只允许 http/https；
    // 非法输入抛 ArgumentException（含 file://、javascript: 等危险协议）
    public static string NormalizeAddress(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("请输入网址。", nameof(input));
        }

        var value = input.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException($"不支持的地址：{input}", nameof(input));
        }

        return uri.ToString();
    }
}
