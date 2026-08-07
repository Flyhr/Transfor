namespace Transfor;

// 媒体嗅探器契约（Phase 4B）：从网络捕获记录中识别作品媒体候选；
// 必须与作品上下文（结构化 JSON 白名单）关联，禁止「见 mp4 就认」
internal interface IMediaSniffer
{
    // 网络记录 → 作品媒体候选（Source=Network）；
    // structuredJson 为 null 或无可提取 URL 时返回空（严格模式，安全优先）
    IReadOnlyList<BrowserCapturedCandidate> Sniff(
        IReadOnlyList<NetworkResourceRecord> records,
        string? structuredJson);
}
