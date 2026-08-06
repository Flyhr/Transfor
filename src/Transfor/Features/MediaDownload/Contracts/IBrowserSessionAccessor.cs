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

    // 浏览器网络栈下载：用于 HttpClient 被服务端 TLS 指纹拦截时的兜底；
    // 内部串行执行（单 WebView2 实例），流式写入并校验后返回保存路径
    Task<BrowserDownloadResult> DownloadAsync(
        Uri mediaUri,
        Guid taskId,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress = null,
        long? maxBytes = null);

    Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        string browserSessionId,
        Uri requestUri,
        CancellationToken cancellationToken);
}
