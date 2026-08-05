namespace Transfor;

// 解析模式
internal enum MediaResolveMode
{
    // 自动解析（静态页面优先，必要时兜底）
    Automatic,
    // 浏览器交互解析（显示浏览器窗口供用户登录/验证）
    BrowserInteractive,
}
