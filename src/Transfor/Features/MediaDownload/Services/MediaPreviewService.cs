namespace Transfor;

// 媒体预览服务：图片预览走与正式下载相同的安全链路（SafeHttpRequestSender + Cookie 匹配），
// 流式写入 %TEMP%\Transfor\PreviewCache\<SessionId> 会话子目录，校验 Content-Type/50 MiB 大小/魔数；
// 不写下载历史、不进入正式队列；失败或取消清理临时文件
internal sealed class MediaPreviewService : IDisposable
{
    private const long MaxPreviewBytes = 50L * 1024 * 1024;

    private readonly SafeHttpRequestSender requestSender;
    private readonly IBrowserSessionAccessor? browserSessions;
    private readonly string sessionDirectory = Path.Combine(Path.GetTempPath(), "Transfor", "PreviewCache", Guid.NewGuid().ToString("N"));

    public MediaPreviewService(
        SafeHttpRequestSender requestSender,
        IBrowserSessionAccessor? browserSessions)
    {
        this.requestSender = requestSender ?? throw new ArgumentNullException(nameof(requestSender));
        this.browserSessions = browserSessions;
    }

    // 下载并校验图片预览，返回本地文件路径（会话内缓存命中时直接返回）
    public async Task<string> DownloadPreviewAsync(
        MediaVariant variant,
        CancellationToken cancellationToken)
    {
        var target = Path.Combine(sessionDirectory, CacheFileName(variant.Uri));
        if (File.Exists(target))
        {
            return target;
        }

        Directory.CreateDirectory(sessionDirectory);
        var partPath = target + ".part";
        try
        {
            using var response = await requestSender.SendAsync(
                variant.Uri,
                (uri, token) => BuildRequestAsync(uri, variant, token),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"预览内容不是图片：{contentType ?? "(空)"}");
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > MaxPreviewBytes)
            {
                throw new InvalidDataException("预览图片超过大小限制。");
            }
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            // 内层作用域：写入完成后立即释放 destination，随后才能打开 .part 做魔数校验
            {
                await using var destination = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, useAsync: true);
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
                    if (total > MaxPreviewBytes)
                    {
                        throw new InvalidDataException("预览图片实际大小超过限制。");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                // 预览文件较小（≤50 MiB），使用同步 Flush 避免异步刷新在此环境下的挂起问题
                destination.Flush();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(partPath);
            throw;
        }
        catch (Exception ex) when (browserSessions is not null
                                   && DouyinTransportClassifier.ShouldUseBrowserFallback(DouyinTransportClassifier.Classify(ex)))
        {
            // HttpClient 被服务端 TLS 指纹拦截：预览转入浏览器网络栈下载（同样限流）
            TryDelete(partPath);
            var browserResult = await browserSessions.DownloadAsync(
                variant.Uri,
                Guid.Empty,
                target,
                MediaKind.Image,
                cancellationToken,
                maxBytes: MaxPreviewBytes);
            if (browserResult.Success && browserResult.TargetPath is not null)
            {
                return browserResult.TargetPath;
            }
            if (browserResult.Cancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            throw new InvalidOperationException(browserResult.Error ?? "浏览器预览下载失败。");
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }

        // 魔数校验（预览绝不把伪装内容交给 Image.FromStream）
        try
        {
            using var stream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!await MediaContentValidator.HasValidMagicNumberAsync(stream, MediaKind.Image, cancellationToken))
            {
                throw new InvalidDataException("预览内容不是有效的图片文件。");
            }
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }

        File.Move(partPath, target, overwrite: true);
        return target;
    }

    // 清理会话缓存目录（应用启动或服务释放时清理过期缓存）
    public void ClearSessionCache()
    {
        try
        {
            if (Directory.Exists(sessionDirectory))
            {
                Directory.Delete(sessionDirectory, recursive: true);
            }
        }
        catch
        {
            // 清理失败不抛
        }
    }

    public void Dispose() => ClearSessionCache();

    private async Task<HttpRequestMessage> BuildRequestAsync(
        Uri currentUri,
        MediaVariant variant,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
        var referer = variant.RequestContext.Referer;
        if (referer is not null)
        {
            request.Headers.Referrer = referer;
        }

        var sessionId = variant.RequestContext.BrowserSessionId;
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

    private static string CacheFileName(Uri uri)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri.ToString()));
        return Convert.ToHexString(hash)[..16] + ".preview";
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
            // 忽略清理失败
        }
    }
}
