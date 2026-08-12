using Microsoft.Web.WebView2.Core;

namespace Transfor;

// 浏览器导航服务（Task 3.2 + M4 修复）：地址规范化（纯逻辑可测）与导航动作；
// 实例绑定所属控件与 CoreWebView2——所有方法/属性统一经控件调度到 UI 线程
// （CoreWebView2 成员线程亲和；非 UI 线程调用时同步 Invoke 等待执行）；
// 未初始化时动作被忽略
internal sealed class BrowserNavigationService
{
    private readonly Control? owner;
    private readonly CoreWebView2? core;

    public BrowserNavigationService(CoreWebView2? core)
        : this(owner: null, core)
    {
    }

    public BrowserNavigationService(Control? owner, CoreWebView2? core)
    {
        this.owner = owner;
        this.core = core;
    }

    public bool CanGoBack => Read(() => core?.CanGoBack ?? false);
    public bool CanGoForward => Read(() => core?.CanGoForward ?? false);

    // 当前页面地址（未初始化/空白时为 null）
    public string? CurrentUrl => Read(() => core?.Source);

    public void Back() => Run(() => { if (core is { CanGoBack: true }) core.GoBack(); });

    public void Forward() => Run(() => { if (core is { CanGoForward: true }) core.GoForward(); });

    public void Refresh() => Run(() => core?.Reload());

    public void Stop() => Run(() => core?.Stop());

    // 导航到规范化后的地址；地址非法抛 ArgumentException
    public void Navigate(string address) => Run(() => core?.Navigate(NormalizeAddress(address)));

    // 地址规范化（容错）：
    // 1. 含协议 → ShareLinkParser 提取首个有效链接（容忍前后缀/尾部中文标点/多余字段）
    // 2. 无协议 → 取首个空白分隔 token 补 https:// 并校验
    // 3. 无效输入（纯中文等）→ 明确提示
    public static string NormalizeAddress(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("请输入网址。", nameof(input));
        }

        var trimmed = input.Trim();

        // 含协议：提取首个有效 http(s) 链接
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            var extracted = ShareLinkParser.TryExtractFirstLink(trimmed, out _);
            if (extracted is not null)
            {
                return extracted.ToString();
            }

            throw new ArgumentException($"无法识别为网址：{input}", nameof(input));
        }

        // 无协议：取首个空白分隔 token 补全（容忍尾部多余字段）
        var token = trimmed;
        var spaceIndex = trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        if (spaceIndex >= 0)
        {
            token = trimmed[..spaceIndex];
        }

        if (Uri.TryCreate("https://" + token, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && !string.IsNullOrWhiteSpace(uri.Host)
            && (uri.Host.Contains('.') || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
        {
            return uri.ToString();
        }

        throw new ArgumentException($"无法识别为网址，请输入完整链接或域名：{input}", nameof(input));
    }

    // 在 UI 线程执行动作（非 UI 线程时同步调度）
    private void Run(Action action)
    {
        if (owner is null || !owner.InvokeRequired)
        {
            action();
            return;
        }

        owner.Invoke(action);
    }

    // 在 UI 线程读取属性（非 UI 线程时同步调度）
    private T Read<T>(Func<T> getter)
    {
        if (owner is null || !owner.InvokeRequired)
        {
            return getter();
        }

        return (T)owner.Invoke(getter);
    }
}
