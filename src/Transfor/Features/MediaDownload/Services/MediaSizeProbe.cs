using System.Net;

namespace Transfor;

// 媒体真实大小探测（解析结果大小展示）：先 HEAD 取 Content-Length，被拒绝/无长度时
// 退化为 Range GET（bytes=0-0，206 Content-Range 携带总大小）——与下载链路同源，
// 即"下载的文件大小"；全部失败（TLS 拦截等）返回 null，调用方保持"大小 —"
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

    // 探测媒体文件大小：HEAD → Range GET 兜底；任一步失败返回 null（不抛异常）
    public async Task<long?> ProbeAsync(
        Uri mediaUri,
        Uri? referer,
        string? browserSessionId,
        CancellationToken cancellationToken)
    {
        var headSize = await ProbeOnceAsync(mediaUri, referer, browserSessionId, range: false, cancellationToken).ConfigureAwait(false);
        if (headSize is > 0)
        {
            return headSize;
        }

        return await ProbeOnceAsync(mediaUri, referer, browserSessionId, range: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long?> ProbeOnceAsync(
        Uri mediaUri,
        Uri? referer,
        string? browserSessionId,
        bool range,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await requestSender.SendAsync(
                mediaUri,
                (currentUri, token) => BuildRequestAsync(currentUri, referer, browserSessionId, range, token),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (range && response.StatusCode == HttpStatusCode.PartialContent)
            {
                // 206 + Content-Range: bytes 0-0/<total> → 总大小
                var contentRange = response.Content.Headers.ContentRange;
                return contentRange?.HasLength == true ? contentRange.Length : null;
            }

            return response.Content.Headers.ContentLength;
        }
        catch
        {
            return null;
        }
    }

    // HEAD 或 Range GET + Referer（与下载一致）+ 浏览器会话 Cookie（捕获变体需要）
    private async Task<HttpRequestMessage> BuildRequestAsync(
        Uri currentUri,
        Uri? referer,
        string? browserSessionId,
        bool range,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(range ? HttpMethod.Get : HttpMethod.Head, currentUri);
        if (referer is not null)
        {
            request.Headers.Referrer = referer;
        }

        if (range)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
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
