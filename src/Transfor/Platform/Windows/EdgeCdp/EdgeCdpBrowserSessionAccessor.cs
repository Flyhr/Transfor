namespace Transfor;

// Edge CDP 浏览器会话访问器：实现 IBrowserSessionAccessor；
// 以真实有头 Edge（独立持久化配置目录）为网络引擎——抖音对非真实 Edge 客户端
// 做 TLS/HTTP 指纹拦截，只有真实 Edge 网络栈可通过；
// 解析经 CDP 导航 + 页面脚本提取结构化数据；下载经浏览器网络栈流式读取；
// Cookie 从浏览器会话读取；会话 ID 用于下载时取 Cookie，不携带 Cookie 本身
internal sealed class EdgeCdpBrowserSessionAccessor : IBrowserSessionAccessor
{
    private const int NavigationTimeoutSeconds = 45;

    private readonly EdgeProcessManager processManager;
    private readonly MediaCache mediaCache;
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim downloadGate = new(1, 1);
    private readonly SemaphoreSlim initGate = new(1, 1);
    private CdpConnection? connection;
    private CdpTargetSession? session;
    private volatile bool initialized;

    public EdgeCdpBrowserSessionAccessor(Control uiOwner, AppPaths paths, bool useProxy = false)
        : this(paths.EdgeProfileDirectory, paths.MediaCacheDirectory, useProxy)
    {
        ArgumentNullException.ThrowIfNull(uiOwner);
    }

    public EdgeCdpBrowserSessionAccessor(string edgeProfileDirectory)
        : this(edgeProfileDirectory, Path.Combine(Path.GetTempPath(), "Transfor", "MediaCache"), useProxy: false)
    {
    }

    public EdgeCdpBrowserSessionAccessor(string edgeProfileDirectory, string mediaCacheDirectory, bool useProxy = false)
    {
        processManager = new EdgeProcessManager(edgeProfileDirectory, useProxy);
        mediaCache = new MediaCache(mediaCacheDirectory);
    }

    public bool IsAvailable => EdgeExecutableLocator.IsAvailable;

    // 捕获页面：启动/复用专用 Edge → 导航 → 提取结构化状态（RENDER_DATA 型 JSON）；
    // 交互模式先把 Edge 窗口带到前台（用户可能需要在其中登录）
    public async Task<BrowserCaptureResult> CaptureAsync(
        Uri pageUri,
        bool interactive,
        CancellationToken cancellationToken)
    {
        try
        {
            if (interactive)
            {
                processManager.Foreground();
            }

            var target = await EnsureSessionAsync(cancellationToken);
            await target.NavigateAsync(pageUri, cancellationToken, NavigationTimeoutSeconds);
            var structuredJson = await ExtractStructuredDataAsync(target, cancellationToken);
            return new BrowserCaptureResult(sessionId, structuredJson, null, Array.Empty<BrowserCapturedCandidate>(), BrowserCaptureStatus.Succeeded, null);
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

    // 浏览器网络栈下载：loadNetworkResource/Fetch 流式写入 .part → 统一终化；
    // 单会话串行执行（浏览器进程内流操作互斥）
    public async Task<BrowserDownloadResult> DownloadAsync(
        Uri mediaUri,
        Guid taskId,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress = null,
        long? maxBytes = null)
    {
        await downloadGate.WaitAsync(cancellationToken);
        try
        {
            var target = await EnsureSessionAsync(cancellationToken);
            var partPath = $"{targetPath}.part.{taskId:N}";
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            if (File.Exists(partPath))
            {
                MediaFileFinalizer.TryDelete(partPath);
            }

            try
            {
                // 缓存命中：直接从缓存复制（解析阶段预取的图片），不再访问网络
                var cachedPath = mediaCache.GetCachedPath(mediaUri);
                if (cachedPath is not null)
                {
                    var (cachedSaved, cachedError) = await CopyFromCacheAsync(cachedPath, partPath, targetPath, kind, cancellationToken);
                    if (cachedSaved is not null)
                    {
                        return BrowserDownloadResult.Succeeded(cachedSaved);
                    }
                    // 缓存内容无效：清理并回退到网络下载
                    mediaCache.Invalidate(mediaUri);
                    MediaFileFinalizer.TryDelete(partPath);
                }

                var total = await EdgeCdpResourceDownloader.DownloadAsync(
                    target,
                    mediaUri,
                    kind,
                    partPath,
                    cancellationToken,
                    new Progress<long>(bytes => progress?.Report(MediaDownloadProgress.Create(taskId, bytes, null))));
                if (maxBytes is not null && total > maxBytes)
                {
                    MediaFileFinalizer.TryDelete(partPath);
                    return BrowserDownloadResult.Failed("媒体内容超过大小限制。");
                }

                var (savedPath, error) = await MediaFileFinalizer.TryFinalizeAsync(partPath, targetPath, kind, cancellationToken);
                if (savedPath is null)
                {
                    MediaFileFinalizer.TryDelete(partPath);
                    return BrowserDownloadResult.Failed(error ?? "下载内容无效。");
                }
                return BrowserDownloadResult.Succeeded(savedPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MediaFileFinalizer.TryDelete(partPath);
                return BrowserDownloadResult.CancelledResult();
            }
            catch (Exception ex)
            {
                MediaFileFinalizer.TryDelete(partPath);
                return BrowserDownloadResult.Failed(ErrorChainFormatter.Format(ex));
            }
        }
        finally
        {
            downloadGate.Release();
        }
    }

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

    // 从浏览器会话读取与目标 URI 匹配的 Cookie
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
            var target = await EnsureSessionAsync(cancellationToken);
            return await target.GetCookiesAsync(requestUri, cancellationToken);
        }
        catch
        {
            return Array.Empty<BrowserCookie>();
        }
    }

    // 解析成功后预取图片到本地缓存（尽力而为）：页面加载成功的图片响应直接落盘，
    // 下载命中缓存即复制，避免再次访问可能失效的 CDN 链接
    public async Task PrefetchImagesAsync(
        IReadOnlyList<Uri> imageUris,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = await EnsureSessionAsync(cancellationToken);
            await MediaPagePrefetcher.PrefetchAsync(target, mediaCache, imageUris, cancellationToken);
        }
        catch
        {
            // 预取失败不影响主流程
        }
    }

    public async ValueTask DisposeAsync()
    {
        var conn = connection;
        connection = null;
        initialized = false;
        if (conn is not null)
        {
            try
            {
                await conn.DisposeAsync();
            }
            catch
            {
                // 释放失败不掩盖退出流程
            }
        }
        await processManager.DisposeAsync();
    }

    private async Task<CdpTargetSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (initialized && session is not null && connection is not null)
        {
            return session;
        }

        await initGate.WaitAsync(cancellationToken);
        try
        {
            if (initialized && session is not null)
            {
                return session;
            }

            await processManager.EnsureStartedAsync(cancellationToken);
            var wsUrl = processManager.BrowserWsUrl
                ?? throw new InvalidOperationException("Edge 调试端点不可用。");

            var conn = new CdpConnection(wsUrl);
            await conn.ConnectAsync(cancellationToken);
            var target = await CdpTargetSession.CreateAsync(conn, cancellationToken);
            await target.EnableDomainsAsync(cancellationToken);

            connection = conn;
            session = target;
            initialized = true;
            return target;
        }
        catch
        {
            connection = null;
            session = null;
            initialized = false;
            throw;
        }
        finally
        {
            initGate.Release();
        }
    }

    // 通过 Runtime.evaluate 获取页面内结构化状态（RENDER_DATA 型 JSON）
    private static async Task<string?> ExtractStructuredDataAsync(
        CdpTargetSession target,
        CancellationToken cancellationToken)
    {
        const string script = @"(() => {
            const node = document.getElementById('RENDER_DATA');
            if (node && node.textContent) return node.textContent;
            for (const s of document.querySelectorAll('script')) {
                const t = (s.textContent || '').trim();
                if (t.startsWith('{')) return t;
            }
            return null;
        })()";

        var raw = await target.EvaluateAsync<string?>(script, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return null;
        }

        // Evaluate 返回的是页面字符串字面量（已是原始值），
        // RENDER_DATA 可能是 URL 编码的 JSON，解码后交给解析器
        return raw.StartsWith('{') ? raw : Uri.UnescapeDataString(raw);
    }
}
