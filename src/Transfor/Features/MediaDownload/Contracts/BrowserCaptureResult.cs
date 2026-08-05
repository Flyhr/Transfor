namespace Transfor;

// 浏览器捕获结果：携带会话 ID 与结构化数据，不携带 Cookie
internal sealed record BrowserCaptureResult(
    string? BrowserSessionId,
    string? StructuredDataJson,
    string? DomSnapshotJson,
    IReadOnlyList<BrowserCapturedCandidate> Candidates,
    BrowserCaptureStatus Status,
    string? Error);
