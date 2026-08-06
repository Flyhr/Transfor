using System.Net;

namespace Transfor;

// HttpClient 工厂：禁用自动重定向与 Cookie 容器（由安全发送器逐跳处理），启用压缩解压；
// 默认全面直连（抖音为 CN 服务，直连最优）；useProxy=true 时使用默认代理（环境变量）
internal static class HttpClientProvider
{
    public static HttpClient Create(bool useProxy = false)
    {
        return new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = useProxy,
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
