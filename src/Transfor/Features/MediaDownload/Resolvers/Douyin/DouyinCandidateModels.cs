namespace Transfor;

// 抖音解析候选模型（两层结构：资产 → 变体）
internal sealed record DouyinVariantCandidate(
    string Url,
    string? ContentType,
    int? Width,
    int? Height,
    int? FramesPerSecond,
    long? Bitrate,
    long? ContentLength,
    string? Codec,
    MediaVariantSource Source,
    bool IsSegmented = false);

internal sealed record DouyinAssetCandidate(
    int OrderIndex,
    MediaKind Kind,
    IReadOnlyList<DouyinVariantCandidate> Variants,
    int SourceIndex = 0,
    MediaAssetRole Role = MediaAssetRole.Normal,
    string? PairId = null,
    string? CoverUrl = null);

internal sealed record DouyinPageData(
    string? PostId,
    string? Title,
    string? AuthorName,
    IReadOnlyList<DouyinAssetCandidate> Assets,
    bool EmptyShell,
    bool LoginRequired,
    string? FailureReason);
