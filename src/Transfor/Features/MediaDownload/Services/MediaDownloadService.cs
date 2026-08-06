namespace Transfor;

// 安全流式下载服务：复用 SafeHttpRequestSender 的安全链路（每跳校验/重定向/Referer/Cookie），
// 流式写入 .part.<TaskId> 临时文件，成功后校验魔数与哈希并原子移动；
// HttpClient 传输被服务端 TLS 指纹拦截（TLS 握手被拒/DNS 失败/重置/EOF/超时）时，
// 自动转入浏览器网络栈兜底下载（单 Edge CDP 串行）；
// 失败/取消清理临时文件；不把整个文件载入内存
internal sealed class MediaDownloadService : IMediaDownloadService
{
    private readonly SafeHttpRequestSender requestSender;
    private readonly IBrowserSessionAccessor? browserSessions;
    private readonly MediaCache? mediaCache;
    private readonly long maxFileBytes;

    public MediaDownloadService(
        SafeHttpRequestSender requestSender,
        IBrowserSessionAccessor? browserSessions,
        long maxFileBytes = MediaContentValidator.DefaultMaxFileBytes,
        MediaCache? mediaCache = null)
    {
        this.requestSender = requestSender ?? throw new ArgumentNullException(nameof(requestSender));
        this.browserSessions = browserSessions;
        this.maxFileBytes = maxFileBytes;
        this.mediaCache = mediaCache;
    }

    public async Task<MediaDownloadResult> DownloadAsync(
        MediaDownloadTask task,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        // 目标路径必须位于其目录内（防 .. 越界）
        var targetDirectory = Path.GetDirectoryName(task.TargetPath) ?? string.Empty;
        if (!DownloadFileNameBuilder.IsWithinDirectory(targetDirectory, task.TargetPath))
        {
            return MediaDownloadResult.Failed(task.Id, "目标路径越出下载目录。");
        }

        // 缓存命中：直接从缓存复制（解析阶段预取的图片），不再访问网络
        if (mediaCache is not null)
        {
            var cachedPath = mediaCache.GetCachedPath(task.SelectedVariant.Uri);
            if (cachedPath is not null)
            {
                Directory.CreateDirectory(targetDirectory);
                var cachePartPath = $"{task.TargetPath}.part.{task.Id:N}";
                try
                {
                    await using var cacheSource = new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await using var cacheDestination = new FileStream(
                        cachePartPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                    await cacheSource.CopyToAsync(cacheDestination, cancellationToken);
                    await cacheDestination.FlushAsync(cancellationToken);
                }
                catch
                {
                    TryDelete(cachePartPath);
                }

                if (File.Exists(cachePartPath))
                {
                    var (cachedSaved, cachedError) = await MediaFileFinalizer.TryFinalizeAsync(
                        cachePartPath, task.TargetPath, task.Asset.Kind, cancellationToken);
                    if (cachedSaved is not null)
                    {
                        return MediaDownloadResult.Success(task.Id, cachedSaved);
                    }
                    // 缓存内容无效：清理并回退到网络下载
                    mediaCache.Invalidate(task.SelectedVariant.Uri);
                    TryDelete(cachePartPath);
                }
            }
        }

        // 浏览器会话解析出的抖音媒体：直接走浏览器网络栈下载，
        // 跳过已知必失败的 HttpClient TLS 尝试（抖音对非真实 Edge 客户端拒绝握手）
        if (browserSessions is not null && !string.IsNullOrEmpty(task.SelectedVariant.RequestContext.BrowserSessionId))
        {
            return await DownloadViaBrowserAsync(task, progress, cancellationToken);
        }

        var partPath = $"{task.TargetPath}.part.{task.Id:N}";
        try
        {
            Directory.CreateDirectory(targetDirectory);

            using var response = await requestSender.SendAsync(
                task.SelectedVariant.Uri,
                (currentUri, token) => BuildRequestAsync(currentUri, task, token),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!MediaContentValidator.IsPlausibleResponse(
                    response,
                    task.Asset.Kind,
                    maxFileBytes,
                    out var validationError))
            {
                return MediaDownloadResult.Failed(task.Id, validationError ?? "媒体响应无效。");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            {
                // 内层作用域：写入完成后立即释放 destination，随后才能打开 .part 做校验
                await using var destination = new FileStream(
                    partPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 32 * 1024,
                    useAsync: true);

                var buffer = new byte[32 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > maxFileBytes)
                    {
                        throw new InvalidDataException("文件大小超过限制。");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(MediaDownloadProgress.Create(
                        task.Id, total, response.Content.Headers.ContentLength));
                }

                await destination.FlushAsync(cancellationToken);
            }
            // 完成字节写入后先检查取消，再进入不可取消的最终校验/原子移动区间；
            // 一旦最终移动开始，结果只能是成功或明确失败，不能返回"已取消但文件已保存"
            cancellationToken.ThrowIfCancellationRequested();
            return await FinalizeDownloadAsync(task, partPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(partPath);
            return MediaDownloadResult.Cancelled(task.Id);
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            var kind = DouyinTransportClassifier.Classify(ex);
            if (browserSessions is not null && DouyinTransportClassifier.ShouldUseBrowserFallback(kind))
            {
                // HttpClient 链路被服务端 TLS 指纹等拒绝：转入浏览器网络栈下载
                return await DownloadViaBrowserAsync(task, progress, cancellationToken);
            }
            return MediaDownloadResult.Failed(task.Id, ErrorChainFormatter.Format(ex));
        }
    }

    // 为当前跳转 URI 构建新请求：GET + Referer + 按 BrowserSessionId+当前 URI 获取的匹配 Cookie
    private async Task<HttpRequestMessage> BuildRequestAsync(
        Uri currentUri,
        MediaDownloadTask task,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, currentUri);

        var referer = task.SelectedVariant.RequestContext.Referer;
        if (referer is not null)
        {
            request.Headers.Referrer = referer;
        }

        var sessionId = task.SelectedVariant.RequestContext.BrowserSessionId;
        if (browserSessions is not null && !string.IsNullOrEmpty(sessionId))
        {
            var cookies = await browserSessions.GetCookiesAsync(sessionId, currentUri, cancellationToken);
            var requestIsSecure = currentUri.Scheme == Uri.UriSchemeHttps;
            var matched = cookies
                .Where(c => BrowserCookieMatcher.ShouldSend(c, sessionId, currentUri, requestIsSecure))
                .Select(c => $"{c.Name}={c.Value}");
            var joined = string.Join("; ", matched);
            if (joined.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", joined);
            }
        }

        return request;
    }

    // 浏览器网络栈兜底下载：复用统一终化流程（魔数/哈希/唯一移动）
    private async Task<MediaDownloadResult> DownloadViaBrowserAsync(
        MediaDownloadTask task,
        IProgress<MediaDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = await browserSessions!.DownloadAsync(
            task.SelectedVariant.Uri,
            task.Id,
            task.TargetPath,
            task.Asset.Kind,
            cancellationToken,
            progress);
        if (result.Success && result.TargetPath is not null)
        {
            return MediaDownloadResult.Success(task.Id, result.TargetPath);
        }
        if (result.Cancelled)
        {
            return MediaDownloadResult.Cancelled(task.Id);
        }
        return MediaDownloadResult.Failed(task.Id, result.Error ?? "浏览器下载失败。");
    }

    // 重新打开临时文件校验魔数；目标已存在且内容相同则幂等成功；
    // 内容不同或不存在则原子移动（竞态时重新生成唯一路径），绝不静默覆盖
    private static async Task<MediaDownloadResult> FinalizeDownloadAsync(
        MediaDownloadTask task,
        string partPath)
    {
        var (savedPath, error) = await MediaFileFinalizer.TryFinalizeAsync(
            partPath, task.TargetPath, task.Asset.Kind, CancellationToken.None);
        if (savedPath is not null)
        {
            return MediaDownloadResult.Success(task.Id, savedPath);
        }

        TryDelete(partPath);
        return MediaDownloadResult.Failed(task.Id, error ?? "下载内容无效。");
    }

    private static void TryDelete(string path)
    {
        MediaFileFinalizer.TryDelete(path);
    }
}
