using System.Net;

namespace Transfor;

// HttpClient 工厂：禁用自动重定向与 Cookie 容器（由安全发送器逐跳处理），启用压缩解压
internal static class HttpClientProvider
{
    public static HttpClient Create()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
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
