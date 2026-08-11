namespace Transfor;

// 媒体真实大小探测（解析结果大小展示）：对媒体变体 URL 发 HEAD 请求取 Content-Length——
// 与下载链路同源（下载的 totalBytes 同样来自响应头），即"下载的文件大小"；
// 失败（TLS 拦截/无长度/超时）返回 null，调用方保持"大小 —"
internal sealed class MediaSizeProbe
{
    private readonly SafeHttpRequestSender requestSender;
    private readonly IBrowserSessionAccessor? browserSessions;

    public MediaSizeProbe(
        SafeHttpRequestSender requestSender,
        IBrowserSessionAccessor? browserSessions)
    {
        this.requestSender = requestSender ?? throw new ArgumentNullException(nameof(requestSender));
        this.browserSessions = browserSessions;
    }

    // HEAD 探测媒体文件大小；任一步失败返回 null（不抛异常）
    public async Task<long?> ProbeAsync(
        Uri mediaUri,
        Uri? referer,
        string? browserSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await requestSender.SendAsync(
                mediaUri,
                (currentUri, token) => BuildRequestAsync(currentUri, referer, browserSessionId, token),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return response.Content.Headers.ContentLength;
        }
        catch
        {
            return null;
        }
    }

    // HEAD + Referer（与下载一致）+ 浏览器会话 Cookie（捕获变体需要）
    private async Task<HttpRequestMessage> BuildRequestAsync(
        Uri currentUri,
        Uri? referer,
        string? browserSessionId,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, currentUri);
        if (referer is not null)
        {
            request.Headers.Referrer = referer;
        }

        if (browserSessions is not null && !string.IsNullOrEmpty(browserSessionId))
        {
            var cookies = await browserSessions.GetCookiesAsync(browserSessionId, currentUri, cancellationToken);
            var requestIsSecure = currentUri.Scheme == Uri.UriSchemeHttps;
            var matched = cookies
                .Where(c => BrowserCookieMatcher.ShouldSend(c, browserSessionId, currentUri, requestIsSecure))
                .Select(c => $"{c.Name}={c.Value}");
            var joined = string.Join("; ", matched);
            if (joined.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", joined);
            }
        }

        return request;
    }
}
