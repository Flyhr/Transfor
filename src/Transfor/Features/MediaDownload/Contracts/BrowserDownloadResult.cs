namespace Transfor;

// 浏览器下载结果：不通过异常表达业务状态
internal sealed record BrowserDownloadResult(
    bool Success,
    string? TargetPath,
    string? Error,
    bool Cancelled = false)
{
    public static BrowserDownloadResult Succeeded(string path) => new(true, path, null);
    public static BrowserDownloadResult Failed(string error) => new(false, null, error);
    public static BrowserDownloadResult CancelledResult() => new(false, null, null, true);
}
