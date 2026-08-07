using System.Net;

namespace Transfor;

// HttpClient 工厂：禁用自动重定向与 Cookie 容器（由安全发送器逐跳处理），启用压缩解压；
// 网络模式三态：Direct 强制直连；System 使用默认代理（环境变量/系统）；
// CustomProxy 使用设置中指定的代理地址（无效地址抛明确异常）
internal static class HttpClientProvider
{
    public static HttpClient Create(
        MediaNetworkMode networkMode = MediaNetworkMode.Direct,
        string? proxyAddress = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
        };

        switch (networkMode)
        {
            case MediaNetworkMode.Direct:
                handler.UseProxy = false;
                break;

            case MediaNetworkMode.System:
                handler.UseProxy = true;
                break;

            case MediaNetworkMode.CustomProxy:
                if (string.IsNullOrWhiteSpace(proxyAddress)
                    || !Uri.TryCreate(proxyAddress.Trim(), UriKind.Absolute, out var proxyUri)
                    || proxyUri.Scheme is not ("http" or "https" or "socks4" or "socks5")
                    || string.IsNullOrEmpty(proxyUri.Host))
                {
                    throw new ArgumentException(
                        $"无效的代理地址：{proxyAddress ?? "(空)"}",
                        nameof(proxyAddress));
                }
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(proxyUri);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(networkMode));
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(100),
        };
    }

    // 更新网络专用 client：与媒体网络（Douyin/CDN 三态）彻底分离。
    // 更新访问 GitHub（raw 策略 + Velopack 下载），走系统默认代理即可，
    // 不应受媒体「强制直连」设置影响（否则用户为抖音直连后更新无法联网）。
    public static HttpClient CreateForUpdates()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            // UseProxy 默认 true：跟随系统代理设置
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
        })
        {
            Timeout = TimeSpan.FromSeconds(100),
        };
    }
}
