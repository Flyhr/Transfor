namespace Transfor;

// 抖音媒体解析器：封装静态页面解析与浏览器兜底；
// Automatic：静态解析优先，可兜底的传输失败（TLS 被拒/DNS/重置/EOF/超时）
//   与空壳/登录页面自动转入隐藏浏览器解析；浏览器仍未成功时返回 RequiresUserInteraction；
// BrowserInteractive：经浏览器会话代理捕获并归一化（不携带 Cookie，只带会话 ID）
internal sealed class DouyinMediaResolver : IMediaResolver
{
    private readonly DouyinHttpPageResolver httpResolver;
    private readonly BrowserSessionAccessorProxy browserSessions;
    private readonly DouyinTransportPreferenceState preference;

    public DouyinMediaResolver(
        DouyinHttpPageResolver httpResolver,
        BrowserSessionAccessorProxy browserSessions)
        : this(httpResolver, browserSessions, new DouyinTransportPreferenceState())
    {
    }

    public DouyinMediaResolver(
        DouyinHttpPageResolver httpResolver,
        BrowserSessionAccessorProxy browserSessions,
        DouyinTransportPreferenceState preference)
    {
        this.httpResolver = httpResolver ?? throw new ArgumentNullException(nameof(httpResolver));
        this.browserSessions = browserSessions ?? throw new ArgumentNullException(nameof(browserSessions));
        this.preference = preference ?? throw new ArgumentNullException(nameof(preference));
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

        return await ResolveWithBrowserAsync(request, interactive: true, cancellationToken);
    }

    private async Task<MediaResolveResult> ResolveAutomaticAsync(
        MediaResolveRequest request,
        CancellationToken cancellationToken)
    {
        // 会话已熔断：直接浏览器优先，不再尝试 HttpClient
        if (preference.ShouldUseBrowser)
        {
            return await ResolveWithBrowserAsync(request, interactive: false, cancellationToken);
        }

        try
        {
            var outcome = await httpResolver.ResolveWorkAsync(request.SourceUri, cancellationToken);
            if (outcome.Post is not null)
            {
                return MediaResolveResult.Success(outcome.Post);
            }

            if (outcome.RequiresBrowser)
            {
                // 空壳/登录页面：自动转入隐藏浏览器解析（可能仍需要用户登录）
                return await ResolveWithBrowserAsync(request, interactive: false, cancellationToken);
            }

            return MediaResolveResult.Failure(outcome.FailureReason ?? "解析失败。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户取消：保持取消语义
            throw;
        }
        catch (Exception ex)
        {
            var kind = DouyinTransportClassifier.Classify(ex);
            if (!DouyinTransportClassifier.ShouldUseBrowserFallback(kind))
            {
                // 安全策略拒绝/未知错误：不转入浏览器，交由协调器报告
                throw;
            }

            // 网络被拒（TLS 指纹拦截/DNS/重置/EOF/超时）：熔断并转入隐藏浏览器
            preference.RecordFailure(kind);
            return await ResolveWithBrowserAsync(request, interactive: false, cancellationToken);
        }
    }

    // 浏览器解析：隐藏（自动兜底）或交互（用户登录）模式共用；
    // 不提前用 IsAvailable 拦截——首次捕获时访问器内部才完成初始化
    private async Task<MediaResolveResult> ResolveWithBrowserAsync(
        MediaResolveRequest request,
        bool interactive,
        CancellationToken cancellationToken)
    {
        var capture = await browserSessions.CaptureAsync(request.SourceUri, interactive, cancellationToken);
        switch (capture.Status)
        {
            case BrowserCaptureStatus.Unavailable:
                return MediaResolveResult.RequiresUserInteraction(capture.Error ?? "浏览器会话不可用。");
            case BrowserCaptureStatus.RequiresUserInteraction:
                return MediaResolveResult.RequiresUserInteraction(capture.Error ?? "请在浏览器中完成登录或验证后重试。");
            case BrowserCaptureStatus.Failed:
                return MediaResolveResult.Failure(capture.Error ?? "浏览器捕获失败。");
        }

        if (capture.StructuredDataJson is null)
        {
            return MediaResolveResult.RequiresUserInteraction("页面未提供可直接解析的数据，请确认已登录后重试。");
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
