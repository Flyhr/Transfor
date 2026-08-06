using System.Text.Json;
using System.Text.Json.Nodes;

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

    public EdgeCdpBrowserSessionAccessor(Control uiOwner, AppPaths paths, MediaNetworkMode networkMode = MediaNetworkMode.Direct, string? proxyAddress = null)
        : this(paths.EdgeProfileDirectory, paths.MediaCacheDirectory, networkMode, proxyAddress)
    {
        ArgumentNullException.ThrowIfNull(uiOwner);
    }

    public EdgeCdpBrowserSessionAccessor(string edgeProfileDirectory)
        : this(edgeProfileDirectory, Path.Combine(Path.GetTempPath(), "Transfor", "MediaCache"), MediaNetworkMode.Direct, null)
    {
    }

    public EdgeCdpBrowserSessionAccessor(string edgeProfileDirectory, string mediaCacheDirectory, MediaNetworkMode networkMode = MediaNetworkMode.Direct, string? proxyAddress = null)
    {
        processManager = new EdgeProcessManager(edgeProfileDirectory, networkMode, proxyAddress);
        mediaCache = new MediaCache(mediaCacheDirectory);
    }

    public bool IsAvailable => EdgeExecutableLocator.IsAvailable;

    // 捕获页面：启动/复用专用 Edge → 导航 → 捕获作品详情接口响应与结构化状态；
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

            // 导航前订阅 Network 事件，捕获作品详情接口响应（登录态最可靠的数据来源）
            string? detailJson = null;
            var pendingBodies = new Dictionary<string, TaskCompletionSource<string?>>(StringComparer.Ordinal);
            void OnEvent(string method, JsonNode? parameters, string? eventSessionId)
            {
                if (eventSessionId != target.SessionId)
                {
                    return;
                }

                if (method == "Network.responseReceived")
                {
                    var url = parameters?["response"]?["url"]?.GetValue<string>();
                    var type = parameters?["type"]?.GetValue<string>();
                    var requestId = parameters?["requestId"]?.GetValue<string>();
                    if (url is not null && requestId is not null
                        && DouyinDetailEndpointMatcher.IsDetailEndpoint(url, type)
                        && !pendingBodies.ContainsKey(requestId))
                    {
                        pendingBodies[requestId] = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
                else if (method == "Network.loadingFinished")
                {
                    var requestId = parameters?["requestId"]?.GetValue<string>();
                    if (requestId is not null && pendingBodies.TryGetValue(requestId, out var tcs))
                    {
                        _ = FetchResponseBodyAsync(target, requestId, tcs, cancellationToken);
                    }
                }
            }

            target.EventReceived += OnEvent;
            try
            {
                await target.NavigateAsync(pageUri, cancellationToken, NavigationTimeoutSeconds);

                // 等待详情接口响应体就绪（懒加载窗口期）
                if (pendingBodies.Count > 0)
                {
                    var first = await Task.WhenAny(pendingBodies.Values.Select(t => t.Task))
                        .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    detailJson = first.Result;
                }
            }
            finally
            {
                target.EventReceived -= OnEvent;
            }

            var structuredJson = detailJson ?? await ExtractStructuredDataAsync(target, cancellationToken);
            var domCandidates = await ExtractDomCandidatesAsync(target, cancellationToken);
            return new BrowserCaptureResult(sessionId, structuredJson, null, domCandidates, BrowserCaptureStatus.Succeeded, null);
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

    // 拉取已加载完成的详情接口响应体（事件线程异步执行，失败视为未捕获）
    private static async Task FetchResponseBodyAsync(
        CdpTargetSession target,
        string requestId,
        TaskCompletionSource<string?> tcs,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await target.CommandAsync("Network.getResponseBody", new { requestId }, cancellationToken);
            var data = body?["body"]?.GetValue<string>();
            var base64 = body?["base64Encoded"]?.GetValue<bool>() ?? false;
            var text = data is null ? null
                : base64 ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data))
                : data;
            tcs.TrySetResult(text);
        }
        catch
        {
            tcs.TrySetResult(null);
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

    // 可恢复关闭：释放 CDP 连接并结束 Edge 进程（批次下载完成后调用）；
    // 不置终结态，下次使用自动重启，会话 Cookie 保留在独立配置目录
    public async ValueTask CloseBrowserAsync(CancellationToken cancellationToken)
    {
        var conn = connection;
        connection = null;
        session = null;
        initialized = false;
        if (conn is not null)
        {
            try
            {
                await conn.DisposeAsync();
            }
            catch
            {
                // 释放失败不阻断关闭
            }
        }

        await processManager.ShutdownAsync();
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

    // 通过 Runtime.evaluate 获取页面内结构化状态：
    // #RENDER_DATA → window.__NEXT_DATA__ → __INITIAL_STATE__ → _SSR_DATA → 首个非 ld+json 的 JSON script
    private static async Task<string?> ExtractStructuredDataAsync(
        CdpTargetSession target,
        CancellationToken cancellationToken)
    {
        const string script = @"(() => {
            const node = document.getElementById('RENDER_DATA');
            if (node && node.textContent) return node.textContent;
            try {
                if (window.__NEXT_DATA__ && typeof window.__NEXT_DATA__ === 'object') return JSON.stringify(window.__NEXT_DATA__);
                if (window.__INITIAL_STATE__ && typeof window.__INITIAL_STATE__ === 'object') return JSON.stringify(window.__INITIAL_STATE__);
                if (window._SSR_DATA && typeof window._SSR_DATA === 'object') return JSON.stringify(window._SSR_DATA);
            } catch (e) { /* 序列化失败忽略 */ }
            for (const s of document.querySelectorAll('script')) {
                const t = (s.textContent || '').trim();
                if (!t.startsWith('{')) continue;
                const type = (s.type || '').toLowerCase();
                if (type === 'application/ld+json') continue;
                return t;
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

    // 通过 Runtime.evaluate 收集页面 DOM 中的媒体元素（轮播图 img 顺序即作品顺序）；
    // 携带 naturalWidth/Height 供小尺寸装饰图（头像/表情包）过滤
    private static async Task<IReadOnlyList<BrowserCapturedCandidate>> ExtractDomCandidatesAsync(
        CdpTargetSession target,
        CancellationToken cancellationToken)
    {
        const string script = @"(() => {
            const pick = (el) => {
                const src = el.currentSrc || el.src;
                if (!src || !src.startsWith('http')) return null;
                return { src, w: el.naturalWidth || el.width || 0, h: el.naturalHeight || el.height || 0 };
            };
            const imgs = [...document.querySelectorAll('img')]
                .map(pick).filter(Boolean);
            const videos = [...document.querySelectorAll('video')]
                .map(pick).filter(Boolean);
            const sources = [...document.querySelectorAll('video source')]
                .map(s => s.src).filter(u => u && u.startsWith('http'))
                .map(u => ({ src: u, w: 0, h: 0 }));
            return JSON.stringify({ images: imgs, videos: [...videos, ...sources] });
        })()";

        try
        {
            var raw = await target.EvaluateAsync<string?>(script, cancellationToken);
            if (string.IsNullOrWhiteSpace(raw) || raw == "null")
            {
                return Array.Empty<BrowserCapturedCandidate>();
            }

            using var document = System.Text.Json.JsonDocument.Parse(raw);
            var root = document.RootElement;
            var results = new List<BrowserCapturedCandidate>();
            CollectFromArray(root, "images", MediaKind.Image, results);
            CollectFromArray(root, "videos", MediaKind.Video, results);
            return results;
        }
        catch
        {
            return Array.Empty<BrowserCapturedCandidate>();
        }
    }

    // 解析 {src,w,h} 候选数组为 BrowserCapturedCandidate（带 DOM 顺序）
    private static void CollectFromArray(
        JsonElement root,
        string propertyName,
        MediaKind kind,
        List<BrowserCapturedCandidate> results)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var url = item.TryGetProperty("src", out var src) ? src.GetString() : null;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                continue;
            }

            int? width = item.TryGetProperty("w", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Number && w.GetInt32() > 0 ? w.GetInt32() : null;
            int? height = item.TryGetProperty("h", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Number && h.GetInt32() > 0 ? h.GetInt32() : null;
            results.Add(new BrowserCapturedCandidate(uri, kind, index++, width, height, null, null, BrowserCandidateSource.Dom));
        }
    }
}
