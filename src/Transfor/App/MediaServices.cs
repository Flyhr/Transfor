namespace Transfor;

// 媒体服务组合对象：集中持有媒体模块的单例服务；
// 浏览器会话延迟初始化，仅在首次进入浏览器解析流程时在 UI 线程创建；
// 释放顺序：取消任务 → 释放协调器与浏览器会话 → 释放 HttpClient
internal sealed class MediaServices : IDisposable, IAsyncDisposable
{
    private readonly object sync = new();
    private IBrowserSessionAccessor? initializedSession;
    private bool disposed;

    public required MediaStateStore State { get; init; }
    public required MediaResolveCoordinator ResolveCoordinator { get; init; }
    public required MediaDownloadCoordinator DownloadCoordinator { get; init; }
    public required BrowserSessionAccessorProxy BrowserSessions { get; init; }
    public required HttpClient HttpClient { get; init; }
    public required MediaPreviewService Preview { get; init; }

    // 浏览器会话工厂（Task 12 设置）；为空时浏览器能力不可用但 Direct 下载正常
    public Func<Control, IBrowserSessionAccessor>? BrowserSessionFactory { get; set; }

    // 首次进入浏览器解析流程时调用一次；失败时保留 Proxy 的 unavailable 状态并抛出可识别异常
    public ValueTask EnsureBrowserInitializedAsync(Control uiOwner)
    {
        ArgumentNullException.ThrowIfNull(uiOwner);
        if (BrowserSessions.IsAvailable)
        {
            return ValueTask.CompletedTask;
        }

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (BrowserSessions.IsAvailable)
            {
                return ValueTask.CompletedTask;
            }

            var factory = BrowserSessionFactory;
            if (factory is null)
            {
                throw new InvalidOperationException("浏览器解析尚未启用。");
            }

            var session = factory(uiOwner);
            BrowserSessions.Attach(session);
            initializedSession = session;
        }

        return ValueTask.CompletedTask;
    }

    // 释放：先取消下载任务并等待落定，再释放协调器与浏览器会话，最后释放 HttpClient
    public async ValueTask DisposeAsync()
    {
        IBrowserSessionAccessor? session;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            session = initializedSession;
            initializedSession = null;
        }

        await DownloadCoordinator.CancelAllAsync().ConfigureAwait(false);
        DownloadCoordinator.Dispose();
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        HttpClient.Dispose();
    }

    public void Dispose()
    {
        IBrowserSessionAccessor? session;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            session = initializedSession;
            initializedSession = null;
        }

        DownloadCoordinator.CancelAllAsync().GetAwaiter().GetResult();
        DownloadCoordinator.Dispose();
        session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        HttpClient.Dispose();
    }
}
