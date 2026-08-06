using Microsoft.Web.WebView2.Core;

namespace Transfor;

// WebView2 环境提供：独立用户数据目录（Cookie/权限/缓存只存于此，不写入普通 JSON）；
// Runtime 缺失/COM 初始化失败统一转换为不可用结果；
// 环境变量代理（HTTP_PROXY/HTTPS_PROXY/ALL_PROXY，与 SocketsHttpHandler 一致）存在时
// 通过 --proxy-server 传给 WebView2：本机 DNS 可能被本地代理 fake-ip 污染，
// 直连会拿到不可路由地址；走代理后由代理自行解析域名。
// 另禁用 User-Agent Client Hints 并覆盖完整版 UA：
// 默认的 sec-ch-ua 会自曝 "Microsoft Edge WebView2" 品牌，且版本为 151.0.0.0，
// 抖音风控据此对 WebView2 返回验证页；伪装为桌面版 Edge 后按正常页面处理
internal static class WebView2EnvironmentProvider
{
    public static async Task<CoreWebView2Environment?> CreateAsync(
        string userDataFolder,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = BuildBrowserArguments(),
            };

            return await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: options).ConfigureAwait(false);
        }
        catch
        {
            // Runtime 缺失、COM 初始化失败等统一视为不可用
            return null;
        }
    }

    // 组装浏览器启动参数；代理未设置时仅包含伪装参数
    internal static string BuildBrowserArguments()
    {
        var parts = new List<string>
        {
            "--disable-blink-features=UserAgentClientHint",
        };

        var proxy = ReadProxyServer();
        if (!string.IsNullOrEmpty(proxy))
        {
            parts.Add($"--proxy-server={proxy}");
        }

        return string.Join(' ', parts);
    }

    // 读取环境变量代理；返回 http://host:port 形式，未设置时返回 null
    internal static string? ReadProxyServer()
    {
        foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && uri.Scheme is ("http" or "https" or "socks4" or "socks5")
                && !string.IsNullOrEmpty(uri.Host))
            {
                return trimmed;
            }
        }

        return null;
    }
}
