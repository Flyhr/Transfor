namespace Transfor;

// 浏览器会话代理：下载服务与解析器始终依赖此代理，Edge CDP 实现延迟注入；
// 未 Attach 时 Capture 返回 unavailable 结果、GetCookies 返回空集合
internal sealed class BrowserSessionAccessorProxy : IBrowserSessionAccessor
{
    private readonly object sync = new();
    private IBrowserSessionAccessor? inner;

    public bool IsAvailable
    {
        get
        {
            lock (sync)
            {
                return inner?.IsAvailable == true;
            }
        }
    }

    // 是否已挂接具体浏览器实现（区别于 IsAvailable：挂接不等于已初始化完成）
    public bool IsAttached
    {
        get
        {
            lock (sync)
            {
                return inner is not null;
            }
        }
    }

    public void Attach(IBrowserSessionAccessor accessor)
    {
        lock (sync)
        {
            inner = accessor ?? throw new ArgumentNullException(nameof(accessor));
        }
    }

    public Task<BrowserCaptureResult> CaptureAsync(
        Uri pageUri,
        bool interactive,
        CancellationToken cancellationToken)
    {
        IBrowserSessionAccessor? current;
        lock (sync)
        {
            current = inner;
        }

        if (current is null)
        {
            return Task.FromResult(new BrowserCaptureResult(
                null, null, null, Array.Empty<BrowserCapturedCandidate>(),
                BrowserCaptureStatus.Unavailable,
                "浏览器会话尚未启用。"));
        }

        return current.CaptureAsync(pageUri, interactive, cancellationToken);
    }

    public Task<BrowserDownloadResult> DownloadAsync(
        Uri mediaUri,
        Guid taskId,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress = null,
        long? maxBytes = null)
    {
        IBrowserSessionAccessor? current;
        lock (sync)
        {
            current = inner;
        }

        if (current is null)
        {
            return Task.FromResult(BrowserDownloadResult.Failed("浏览器会话尚未启用。"));
        }

        return current.DownloadAsync(mediaUri, taskId, targetPath, kind, cancellationToken, progress, maxBytes);
    }

    public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        string browserSessionId,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        IBrowserSessionAccessor? current;
        lock (sync)
        {
            current = inner;
        }

        if (current is null)
        {
            return Task.FromResult<IReadOnlyList<BrowserCookie>>(Array.Empty<BrowserCookie>());
        }

        return current.GetCookiesAsync(browserSessionId, requestUri, cancellationToken);
    }

    public Task PrefetchImagesAsync(
        IReadOnlyList<Uri> imageUris,
        CancellationToken cancellationToken)
    {
        IBrowserSessionAccessor? current;
        lock (sync)
        {
            current = inner;
        }

        if (current is null)
        {
            return Task.CompletedTask;
        }

        return current.PrefetchImagesAsync(imageUris, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        IBrowserSessionAccessor? current;
        lock (sync)
        {
            current = inner;
            inner = null;
        }

        if (current is not null)
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }
    }
}
