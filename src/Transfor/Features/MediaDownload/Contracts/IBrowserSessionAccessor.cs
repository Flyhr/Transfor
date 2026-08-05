namespace Transfor;

// 浏览器会话访问契约：隐藏 WebView2 实现细节；
// 下载器与解析器只依赖此接口，不直接持有 WebView2 控件
internal interface IBrowserSessionAccessor : IAsyncDisposable
{
    bool IsAvailable { get; }

    Task<BrowserCaptureResult> CaptureAsync(
        Uri pageUri,
        bool interactive,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        string browserSessionId,
        Uri requestUri,
        CancellationToken cancellationToken);
}
