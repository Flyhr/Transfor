namespace Transfor;

// 媒体请求上下文：仅保存"使用哪个临时浏览器会话"的标识，不保存 Cookie
internal sealed record MediaRequestContext(
    Uri? Referer,
    string? BrowserSessionId);
