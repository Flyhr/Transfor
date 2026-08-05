namespace Transfor;

// 抖音 HTTP 解析结果：成功 / 需要浏览器 / 失败（不通过异常表达业务状态）
internal sealed record DouyinHttpResolveOutcome(ResolvedMediaPost? Post, bool RequiresBrowser, string? FailureReason)
{
    public static DouyinHttpResolveOutcome Success(ResolvedMediaPost post) => new(post, false, null);
    public static DouyinHttpResolveOutcome NeedBrowser(string reason) => new(null, true, reason);
    public static DouyinHttpResolveOutcome Failed(string reason) => new(null, false, reason);
}

// 抖音 HTTP 页面解析器：解分享短链 → 获取最终作品页 → 解析结构化数据；
// 空壳/登录 → NeedBrowser；删除/私密 → Failed；
// 最终页面必须仍属于允许的抖音页面域名（CDN 域名不得作为作品页）
internal sealed class DouyinHttpPageResolver
{
    // 页面正文读取上限，防止把整页无限读入内存
    private const int MaxPageBytes = 32 * 1024 * 1024;

    private readonly SafeHttpRequestSender requestSender;

    public DouyinHttpPageResolver(SafeHttpRequestSender requestSender)
    {
        this.requestSender = requestSender ?? throw new ArgumentNullException(nameof(requestSender));
    }

    // 解析作品页并归一化为统一作品模型
    public async Task<DouyinHttpResolveOutcome> ResolveWorkAsync(
        Uri sourceUri,
        CancellationToken cancellationToken)
    {
        using var response = await requestSender.SendAsync(
            sourceUri,
            (uri, token) => Task.FromResult(new HttpRequestMessage(HttpMethod.Get, uri)),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        // 最终页面 URI 必须仍属于抖音页面域名
        var finalUri = response.RequestMessage?.RequestUri ?? sourceUri;
        if (!IsDouyinPageHost(finalUri.Host))
        {
            return DouyinHttpResolveOutcome.Failed("重定向后的页面不属于抖音域名。");
        }

        if (!response.IsSuccessStatusCode)
        {
            return DouyinHttpResolveOutcome.Failed($"页面请求失败：{(int)response.StatusCode}");
        }

        var html = await ReadLimitedAsync(response.Content, MaxPageBytes, cancellationToken);
        var data = DouyinPageParser.Parse(html);

        if (data.FailureReason is not null)
        {
            return DouyinHttpResolveOutcome.Failed(data.FailureReason);
        }

        if (data.EmptyShell || data.LoginRequired)
        {
            return DouyinHttpResolveOutcome.NeedBrowser(data.LoginRequired ? "页面需要登录或验证码。" : "页面为空壳，需要浏览器解析。");
        }

        return DouyinHttpResolveOutcome.Success(DouyinMediaNormalizer.Normalize(finalUri, data));
    }

    // 读取正文并限制大小
    private static async Task<string> ReadLimitedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var buffer = new char[8192];
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            builder.Append(buffer, 0, read);
            if (builder.Length > maxBytes)
            {
                throw new InvalidOperationException("页面正文超过大小限制。");
            }
        }
        return builder.ToString();
    }

    // 抖音页面域名：douyin.com / iesdouyin.com 及其子域
    public static bool IsDouyinPageHost(string host)
    {
        return host.Equals("douyin.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("iesdouyin.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".iesdouyin.com", StringComparison.OrdinalIgnoreCase);
    }
}
