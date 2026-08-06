namespace Transfor;

// 直接媒体解析器：兜底处理"图片/视频直接 URL"；
// 已知抖音页面域名不归本解析器（避免抢走专用解析器的链接）；
// HttpClient 被服务端 TLS 指纹拦截时，若已挂接浏览器会话，
// 按 URL 扩展名识别媒体类型并乐观返回作品（下载阶段自动经浏览器网络栈完成）
internal sealed class DirectMediaResolver : IMediaResolver
{
    private readonly SafeHttpRequestSender requestSender;
    private readonly BrowserSessionAccessorProxy browserSessions;

    public DirectMediaResolver(
        SafeHttpRequestSender requestSender,
        BrowserSessionAccessorProxy? browserSessions = null)
    {
        this.requestSender = requestSender ?? throw new ArgumentNullException(nameof(requestSender));
        this.browserSessions = browserSessions ?? new BrowserSessionAccessorProxy();
    }

    public MediaProviderId Provider => MediaProviderId.Direct;

    // 无网络的 URI 形态判断；真正的安全校验由 SafeHttpRequestSender 执行
    public bool CanResolve(Uri sourceUri)
    {
        if (sourceUri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        // 已知平台页面域名必须返回 false
        var host = sourceUri.Host;
        return !host.Equals("douyin.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("iesdouyin.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".iesdouyin.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<MediaResolveResult> ResolveAsync(
        MediaResolveRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveViaHttpAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var kind = DouyinTransportClassifier.Classify(ex);
            if (!DouyinTransportClassifier.ShouldUseBrowserFallback(kind) || !browserSessions.IsAttached)
            {
                throw;
            }

            // 直接媒体 URL 被服务端 TLS 指纹拦截（如抖音 CDN 域名）：
            // 按 URL 扩展名识别媒体类型乐观返回作品，下载阶段自动转入浏览器网络栈
            var mediaKind = GuessKindFromUri(request.SourceUri);
            if (mediaKind is null)
            {
                throw;
            }

            return CreateBrowserTransportPost(request.SourceUri, mediaKind.Value);
        }
    }

    private async Task<MediaResolveResult> ResolveViaHttpAsync(
        MediaResolveRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await requestSender.SendAsync(
            request.SourceUri,
            (uri, token) => Task.FromResult(new HttpRequestMessage(HttpMethod.Get, uri)),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is null)
        {
            return MediaResolveResult.Unsupported("无法识别链接的内容类型。");
        }

        MediaKind? kind = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? MediaKind.Image
            : contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                ? MediaKind.Video
                : null;
        if (kind is null)
        {
            return MediaResolveResult.Unsupported("该链接不是可直接下载的图片或视频。");
        }

        // 单资源作品：一个资产、一个变体
        var variant = new MediaVariant(
            request.SourceUri,
            null, null, null, null,
            response.Content.Headers.ContentLength,
            contentType,
            null,
            MediaVariantSource.NetworkCapture,
            new MediaRequestContext(request.SourceUri, null));
        var asset = new MediaAsset(0, kind.Value, new MediaVariant[] { variant });
        var post = new ResolvedMediaPost(
            MediaProviderId.Direct,
            request.SourceUri,
            null,
            null,
            null,
            new MediaAsset[] { asset });
        return MediaResolveResult.Success(post);
    }

    // 浏览器传输作品：类型未知（无 Content-Type），体积与码率留待下载时确定
    private static MediaResolveResult CreateBrowserTransportPost(Uri sourceUri, MediaKind kind)
    {
        var variant = new MediaVariant(
            sourceUri,
            null, null, null, null, null, null, null,
            MediaVariantSource.NetworkCapture,
            new MediaRequestContext(sourceUri, null));
        var asset = new MediaAsset(0, kind, new MediaVariant[] { variant });
        var post = new ResolvedMediaPost(
            MediaProviderId.Direct,
            sourceUri,
            null,
            null,
            null,
            new MediaAsset[] { asset });
        return MediaResolveResult.Success(post);
    }

    // 按 URL 扩展名猜测媒体类型；无法识别时返回 null（保持保守，不猜测）
    private static MediaKind? GuessKindFromUri(Uri uri)
    {
        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".jpg") || path.EndsWith(".jpeg")
            || path.EndsWith(".png") || path.EndsWith(".gif")
            || path.EndsWith(".webp") || path.EndsWith(".bmp")
            || path.EndsWith(".avif") || path.EndsWith(".heic"))
        {
            return MediaKind.Image;
        }

        if (path.EndsWith(".mp4") || path.EndsWith(".mov")
            || path.EndsWith(".m4v") || path.EndsWith(".webm")
            || path.EndsWith(".ts") || path.EndsWith(".m3u8"))
        {
            return MediaKind.Video;
        }

        return null;
    }
}
