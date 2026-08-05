namespace Transfor;

// 安全流式下载服务：复用 SafeHttpRequestSender 的安全链路（每跳校验/重定向/Referer/Cookie），
// 流式写入 .part.<TaskId> 临时文件，成功后校验魔数与哈希并原子移动；
// 失败/取消清理临时文件；不把整个文件载入内存
internal sealed class MediaDownloadService : IMediaDownloadService
{
    private readonly SafeHttpRequestSender requestSender;
    private readonly IBrowserSessionAccessor? browserSessions;
    private readonly long maxFileBytes;

    public MediaDownloadService(
        SafeHttpRequestSender requestSender,
        IBrowserSessionAccessor? browserSessions,
        long maxFileBytes = MediaContentValidator.DefaultMaxFileBytes)
    {
        this.requestSender = requestSender ?? throw new ArgumentNullException(nameof(requestSender));
        this.browserSessions = browserSessions;
        this.maxFileBytes = maxFileBytes;
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
            return MediaDownloadResult.Failed(task.Id, ex.Message);
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

    // 重新打开临时文件校验魔数；目标已存在且内容相同则幂等成功；
    // 内容不同或不存在则原子移动（竞态时重新生成唯一路径），绝不静默覆盖
    private async Task<MediaDownloadResult> FinalizeDownloadAsync(
        MediaDownloadTask task,
        string partPath)
    {
        string partHash;
        using (var partStream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (!await MediaContentValidator.HasValidMagicNumberAsync(partStream, task.Asset.Kind, CancellationToken.None))
            {
                TryDelete(partPath);
                return MediaDownloadResult.Failed(task.Id, "下载内容不是有效的媒体文件。");
            }

            partStream.Position = 0;
            partHash = await MediaHashService.ComputeSha256Async(partStream, CancellationToken.None);

            if (File.Exists(task.TargetPath))
            {
                // 目标存在且哈希相同：删除临时文件，幂等成功
                using var targetStream = new FileStream(task.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var targetHash = await MediaHashService.ComputeSha256Async(targetStream, CancellationToken.None);
                if (string.Equals(targetHash, partHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partPath);
                    return MediaDownloadResult.Success(task.Id, task.TargetPath);
                }
            }
        }

        var savedPath = MoveWithUniqueFallback(task.TargetPath, partPath);
        return MediaDownloadResult.Success(task.Id, savedPath);
    }

    // 独占移动：目标被并发任务创建时重新生成 (1)(2) 后缀，不覆盖其他任务文件
    private static string MoveWithUniqueFallback(string targetPath, string partPath)
    {
        var directory = Path.GetDirectoryName(targetPath)!;
        var fileName = Path.GetFileName(targetPath);
        var attempt = 0;
        while (true)
        {
            var candidate = attempt == 0
                ? targetPath
                : DownloadFileNameBuilder.BuildUniquePath(directory, fileName);
            try
            {
                File.Move(partPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) && attempt < 10)
            {
                attempt++;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不掩盖原始结果
        }
    }
}
