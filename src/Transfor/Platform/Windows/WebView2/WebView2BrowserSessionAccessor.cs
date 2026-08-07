using Microsoft.Web.WebView2.Core;

namespace Transfor;

// WebView2 浏览器会话访问器（Phase 4A）：IBrowserSessionAccessor 的 WebView2 实现；
// 通过隐藏宿主（BrowserService.Host）在 UI 线程执行页面捕获与媒体下载，
// 以真实浏览器网络栈（页面 fetch + 浏览器 Cookie）规避抖音 TLS 指纹拦截；
// 与「浏览器」页共享 Profile（登录一次互通）；
// 旧 Edge CDP 实现保留但不被实例化
internal sealed class WebView2BrowserSessionAccessor : IBrowserSessionAccessor
{
    private readonly BrowserService browserService;
    private readonly Control uiOwner;
    private readonly MediaCache mediaCache;
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim downloadGate = new(1, 1);

    public WebView2BrowserSessionAccessor(Control uiOwner, BrowserService browserService, AppPaths paths)
    {
        this.uiOwner = uiOwner ?? throw new ArgumentNullException(nameof(uiOwner));
        this.browserService = browserService ?? throw new ArgumentNullException(nameof(browserService));
        mediaCache = new MediaCache(paths.MediaCacheDirectory);
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString() is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    // 捕获页面：惰性初始化隐藏宿主 → 导航 → 提取结构化数据与 DOM 候选；
    // 页面脚本无作品数据时按作品 ID 经浏览器 fetch 直取详情接口
    public async Task<BrowserCaptureResult> CaptureAsync(
        Uri pageUri,
        bool interactive,
        CancellationToken cancellationToken)
    {
        try
        {
            await browserService.EnsureHostAsync(uiOwner, cancellationToken).ConfigureAwait(false);
            var (structuredJson, candidates) = await browserService.Host
                .CapturePageAsync(pageUri, cancellationToken).ConfigureAwait(false);

            // 页面脚本无作品数据时：从页面配置提取作品 ID，经浏览器网络栈直取详情接口
            if (!BrowserCaptureSession.HasWorkData(structuredJson))
            {
                var workId = BrowserCaptureSession.ExtractWorkId(structuredJson);
                if (workId is not null)
                {
                    var fetched = await browserService.Host
                        .FetchDetailAsync(BrowserCaptureSession.BuildDetailApiUri(workId), cancellationToken)
                        .ConfigureAwait(false);
                    if (BrowserCaptureSession.HasWorkData(fetched))
                    {
                        structuredJson = fetched;
                    }
                }
            }

            return new BrowserCaptureResult(sessionId, structuredJson, null, candidates, BrowserCaptureStatus.Succeeded, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new BrowserCaptureResult(null, null, null, Array.Empty<BrowserCapturedCandidate>(), BrowserCaptureStatus.Failed, "捕获已取消。");
        }
        catch (Exception ex)
        {
            return new BrowserCaptureResult(null, null, null, Array.Empty<BrowserCapturedCandidate>(), BrowserCaptureStatus.Failed, ErrorChainFormatter.Format(ex));
        }
    }

    // 浏览器网络栈下载：页面 fetch + 分块流式写入 .part → 统一终化；
    // 单会话串行执行（隐藏宿主下载控件互斥）
    public async Task<BrowserDownloadResult> DownloadAsync(
        Uri mediaUri,
        Guid taskId,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress = null,
        long? maxBytes = null)
    {
        await downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await browserService.EnsureHostAsync(uiOwner, cancellationToken).ConfigureAwait(false);
            var partPath = $"{targetPath}.part.{taskId:N}";
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            if (File.Exists(partPath))
            {
                MediaFileFinalizer.TryDelete(partPath);
            }

            // 缓存命中：直接从缓存复制（解析阶段预取的图片），不再访问网络
            var cachedPath = mediaCache.GetCachedPath(mediaUri);
            if (cachedPath is not null)
            {
                var (cachedSaved, cachedError) = await CopyFromCacheAsync(cachedPath, partPath, targetPath, kind, cancellationToken).ConfigureAwait(false);
                if (cachedSaved is not null)
                {
                    return BrowserDownloadResult.Succeeded(cachedSaved);
                }

                mediaCache.Invalidate(mediaUri);
                MediaFileFinalizer.TryDelete(partPath);
            }

            var error = await browserService.Host.DownloadMediaAsync(
                mediaUri,
                partPath,
                cancellationToken,
                (bytes, total) => progress?.Report(MediaDownloadProgress.Create(taskId, bytes, total)),
                maxBytes).ConfigureAwait(false);
            if (error is not null)
            {
                MediaFileFinalizer.TryDelete(partPath);
                return BrowserDownloadResult.Failed(error);
            }

            var (savedPath, finalizeError) = await MediaFileFinalizer.TryFinalizeAsync(partPath, targetPath, kind, cancellationToken).ConfigureAwait(false);
            if (savedPath is null)
            {
                MediaFileFinalizer.TryDelete(partPath);
                return BrowserDownloadResult.Failed(finalizeError ?? "下载内容无效。");
            }

            return BrowserDownloadResult.Succeeded(savedPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BrowserDownloadResult.CancelledResult();
        }
        catch (Exception ex)
        {
            return BrowserDownloadResult.Failed(ErrorChainFormatter.Format(ex));
        }
        finally
        {
            downloadGate.Release();
        }
    }

    // 从浏览器会话读取与目标 URI 匹配的 Cookie（隐藏宿主下载会话）
    public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        string browserSessionId,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(browserSessionId, sessionId, StringComparison.Ordinal))
        {
            return Array.Empty<BrowserCookie>();
        }

        try
        {
            await browserService.EnsureHostAsync(uiOwner, cancellationToken).ConfigureAwait(false);
            return await browserService.Host.GetCookiesAsync(requestUri, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return Array.Empty<BrowserCookie>();
        }
    }

    // 预取（Phase 4A 范围外）：WebView2 版媒体预取暂不实现；
    // 下载仍经浏览器 fetch 完成，功能不受影响
    public Task PrefetchMediaAsync(
        IReadOnlyList<(Uri Uri, MediaKind Kind)> items,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    // 隐藏宿主与应用同生命周期（与「浏览器」页共享 Profile），无需可恢复关闭；
    // Cookie 持久化于独立 Profile 目录，下次使用自动恢复
    public ValueTask CloseBrowserAsync(CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    // 宿主资源由 BrowserService 随应用释放
    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    // 从缓存复制并终化（与网络下载共用魔数/哈希/唯一移动校验）
    private static async Task<(string? SavedPath, string? Error)> CopyFromCacheAsync(
        string cachedPath,
        string partPath,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var destination = new FileStream(
                partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
        catch
        {
            return (null, "缓存复制失败。");
        }

        return await MediaFileFinalizer.TryFinalizeAsync(partPath, targetPath, kind, cancellationToken);
    }
}
