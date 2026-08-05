namespace Transfor;

// 浏览器会话代理：下载服务与解析器始终依赖此代理，WebView2 实现延迟注入；
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
