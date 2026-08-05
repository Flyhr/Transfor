using System.Net;

namespace Transfor;

// 安全请求发送器：初始 URI 与每个重定向 URI 都重新校验；
// 每一跳重新调用 requestFactory（Cookie 仅针对当前目标域重新获取）；
// 释放每跳创建的 HttpRequestMessage；跨源 Referer 与敏感头被清除；
// 中间响应及时释放；超过重定向上限抛出明确错误
internal sealed class SafeHttpRequestSender
{
    public const int DefaultMaxRedirects = 5;

    private readonly HttpClient client;
    private readonly SafeUriValidator validator;

    public SafeHttpRequestSender(
        HttpClient client,
        SafeUriValidator validator)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<HttpResponseMessage> SendAsync(
        Uri initialUri,
        Func<Uri, CancellationToken, Task<HttpRequestMessage>> requestFactory,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken,
        int maxRedirects = DefaultMaxRedirects)
    {
        ArgumentNullException.ThrowIfNull(initialUri);
        ArgumentNullException.ThrowIfNull(requestFactory);

        var currentUri = initialUri;
        for (var hop = 0; hop <= maxRedirects; hop++)
        {
            // 每一跳都重新校验 URI
            var validation = await validator.ValidateAsync(currentUri, cancellationToken).ConfigureAwait(false);
            if (!validation.IsAllowed)
            {
                throw new InvalidOperationException($"URI 校验失败：{validation.Error}");
            }

            var request = await requestFactory(currentUri, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("请求工厂返回了空请求。");
            if (request.RequestUri is null)
            {
                request.RequestUri = currentUri;
            }

            SanitizeRequest(request);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // 请求由发送器负责释放，避免重定向时请求对象泄漏
                request.Dispose();
            }

            // 非重定向或缺少 Location：返回响应
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
            {
                return response;
            }

            // 合并相对 Location 并继续下一跳；中间响应及时释放
            var location = response.Headers.Location;
            currentUri = location.IsAbsoluteUri
                ? location
                : new Uri(currentUri, location);
            response.Dispose();
        }

        throw new InvalidOperationException($"重定向次数超过限制（{maxRedirects}）。");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    // 清除敏感头；跨源 Referer 一律删除，不把原始页地址泄露到不相关域名
    private static void SanitizeRequest(HttpRequestMessage request)
    {
        request.Headers.Remove("Authorization");
        request.Headers.Remove("Proxy-Authorization");

        if (request.Headers.Referrer is not null
            && request.RequestUri is not null
            && !string.Equals(request.Headers.Referrer.Host, request.RequestUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Referrer = null;
        }
    }
}
