namespace Transfor;

// 浏览器会话访问契约：隐藏浏览器实现细节（真实 Edge + CDP）；
// 下载器与解析器只依赖此接口，不直接持有浏览器进程或会话
internal interface IBrowserSessionAccessor : IAsyncDisposable
{
    bool IsAvailable { get; }

    Task<BrowserCaptureResult> CaptureAsync(
        Uri pageUri,
        bool interactive,
        CancellationToken cancellationToken);

    // 浏览器网络栈下载：用于 HttpClient 被服务端 TLS 指纹拦截时的兜底；
    // 内部串行执行（单浏览器会话），流式写入并校验后返回保存路径
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

    // 解析成功后预取图片媒体到本地缓存（尽力而为，不抛异常）：
    // 页面加载成功的图片响应直接落入缓存，下载时命中即复制，避免再次访问可能失效的 CDN
    Task PrefetchImagesAsync(
        IReadOnlyList<Uri> imageUris,
        CancellationToken cancellationToken);

    // 可恢复关闭：结束浏览器进程与会话（下载批次完成后调用）；
    // 不置终结态，下次 Capture/Download 时自动重启，会话 Cookie 保留在独立配置目录
    ValueTask CloseBrowserAsync(CancellationToken cancellationToken);
}
