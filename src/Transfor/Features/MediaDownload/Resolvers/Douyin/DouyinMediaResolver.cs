namespace Transfor;

// 抖音媒体解析器：封装静态页面解析与浏览器兜底；
// Automatic：静态解析优先，空壳/登录 → RequiresUserInteraction；
// BrowserInteractive：经浏览器会话代理捕获并归一化（不携带 Cookie，只带会话 ID）
internal sealed class DouyinMediaResolver : IMediaResolver
{
    private readonly DouyinHttpPageResolver httpResolver;
    private readonly BrowserSessionAccessorProxy browserSessions;

    public DouyinMediaResolver(
        DouyinHttpPageResolver httpResolver,
        BrowserSessionAccessorProxy browserSessions)
    {
        this.httpResolver = httpResolver ?? throw new ArgumentNullException(nameof(httpResolver));
        this.browserSessions = browserSessions ?? throw new ArgumentNullException(nameof(browserSessions));
    }

    public MediaProviderId Provider => MediaProviderId.Douyin;

    // 边界正确的后缀判断：douyin.com / iesdouyin.com 及其子域，evildouyin.com 不匹配
    public bool CanResolve(Uri sourceUri)
    {
        if (sourceUri.Scheme is not ("http" or "https"))
        {
            return false;
        }
        return DouyinHttpPageResolver.IsDouyinPageHost(sourceUri.Host);
    }

    public async Task<MediaResolveResult> ResolveAsync(
        MediaResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Mode == MediaResolveMode.Automatic)
        {
            return await ResolveAutomaticAsync(request, cancellationToken);
        }

        return await ResolveBrowserAsync(request, cancellationToken);
    }

    private async Task<MediaResolveResult> ResolveAutomaticAsync(
        MediaResolveRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await httpResolver.ResolveWorkAsync(request.SourceUri, cancellationToken);
        if (outcome.Post is not null)
        {
            return MediaResolveResult.Success(outcome.Post);
        }

        if (outcome.RequiresBrowser)
        {
            return MediaResolveResult.RequiresUserInteraction(outcome.FailureReason ?? "页面需要浏览器解析，请点击「浏览器登录」。");
        }

        return MediaResolveResult.Failure(outcome.FailureReason ?? "解析失败。");
    }

    private async Task<MediaResolveResult> ResolveBrowserAsync(
        MediaResolveRequest request,
        CancellationToken cancellationToken)
    {
        if (!browserSessions.IsAvailable)
        {
            return MediaResolveResult.RequiresUserInteraction("当前环境尚未启用浏览器解析。");
        }

        var capture = await browserSessions.CaptureAsync(request.SourceUri, interactive: true, cancellationToken);
        if (capture.Status == BrowserCaptureStatus.Unavailable)
        {
            return MediaResolveResult.RequiresUserInteraction(capture.Error ?? "浏览器会话不可用。");
        }

        if (capture.Status == BrowserCaptureStatus.RequiresUserInteraction)
        {
            return MediaResolveResult.RequiresUserInteraction(capture.Error ?? "请在浏览器中完成登录或验证后重试。");
        }

        if (capture.Status != BrowserCaptureStatus.Succeeded || capture.StructuredDataJson is null)
        {
            return MediaResolveResult.Failure(capture.Error ?? "浏览器捕获失败。");
        }

        var data = DouyinPageParser.ParseStructuredData(capture.StructuredDataJson);
        if (data.FailureReason is not null)
        {
            return MediaResolveResult.Failure(data.FailureReason);
        }

        if (data.Assets.Count == 0)
        {
            return MediaResolveResult.RequiresUserInteraction("浏览器页面中未找到可下载的媒体。");
        }

        // 归一化后所有变体携带会话 ID（用于下载时取 Cookie），但不携带 Cookie 本身
        var post = DouyinMediaNormalizer.Normalize(request.SourceUri, data);
        post = post with
        {
            Assets = post.Assets
                .Select(asset => asset with
                {
                    Variants = asset.Variants
                        .Select(variant => variant with
                        {
                            RequestContext = new MediaRequestContext(
                                variant.RequestContext.Referer,
                                capture.BrowserSessionId),
                        })
                        .ToArray(),
                })
                .ToArray(),
        };
        return MediaResolveResult.Success(post);
    }
}
