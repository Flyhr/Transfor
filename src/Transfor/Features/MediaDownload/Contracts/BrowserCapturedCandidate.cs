namespace Transfor;

// 浏览器捕获到的媒体候选
internal sealed record BrowserCapturedCandidate(
    Uri Uri,
    MediaKind? Kind,
    int? OrderIndex,
    int? Width,
    int? Height,
    string? ContentType,
    long? ContentLength,
    BrowserCandidateSource Source);
