namespace Transfor;

// 浏览器网络资源记录（Phase 4B）：页面加载期间真实网络请求的元数据快照；
// 不含响应体与 ResourceType（读取响应体属于 4C 的 CDP 能力；
// ResourceType 需经 WebResourceRequested 拦截事件，4B 用 URL 特征识别接口）
internal sealed record NetworkResourceRecord(
    Uri Uri,
    string Method,
    string? ContentType,
    int StatusCode)
{
    // 是否为成功的 GET 资源请求（嗅探候选的基础条件）
    public bool IsSuccessfulGet =>
        string.Equals(Method, "GET", StringComparison.OrdinalIgnoreCase)
        && StatusCode is >= 200 and < 300;
}
