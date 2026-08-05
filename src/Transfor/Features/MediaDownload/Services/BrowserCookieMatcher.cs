namespace Transfor;

// Cookie 域名/路径匹配：发送前做防御性二次过滤；
// 仅当 BrowserSessionId 非空、域名匹配（host == domain 或真实子域）、
// Secure 要求满足且 Path 按目录边界匹配时才发送
internal static class BrowserCookieMatcher
{
    public static bool ShouldSend(
        BrowserCookie cookie,
        string? browserSessionId,
        Uri requestUri,
        bool isSecureRequest)
    {
        if (string.IsNullOrEmpty(browserSessionId))
        {
            return false;
        }

        // 域名比较：先移除可选前导点，要求 host == domain 或 host 是该 domain 的真实子域
        var domain = cookie.Domain.TrimStart('.');
        if (domain.Length == 0)
        {
            return false;
        }

        var host = requestUri.Host;
        var domainMatch = host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        if (!domainMatch)
        {
            return false;
        }

        // Secure=true 时请求必须为 HTTPS
        if (cookie.Secure && !isSecureRequest)
        {
            return false;
        }

        // Path 按 RFC 6265 的目录边界匹配：/foo 不匹配 /foobar
        return PathMatches(cookie.Path, requestUri.AbsolutePath);
    }

    private static bool PathMatches(string cookiePath, string requestPath)
    {
        if (string.IsNullOrEmpty(cookiePath))
        {
            return true;
        }

        if (requestPath.Equals(cookiePath, StringComparison.Ordinal))
        {
            return true;
        }

        if (requestPath.StartsWith(cookiePath, StringComparison.Ordinal)
            && cookiePath.EndsWith("/", StringComparison.Ordinal))
        {
            return true;
        }

        return requestPath.StartsWith(cookiePath + "/", StringComparison.Ordinal);
    }
}
