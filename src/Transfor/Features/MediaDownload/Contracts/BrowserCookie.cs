namespace Transfor;

// 浏览器 Cookie（仅存在于 WebView2 独立数据目录中，不持久化到普通 JSON）
internal sealed record BrowserCookie(
    string Domain,
    string Path,
    string Name,
    string Value,
    bool Secure);
