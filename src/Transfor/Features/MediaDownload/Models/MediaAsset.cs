namespace Transfor;

// 媒体资产：一个作品中的一个媒体（多图作品的一张图/一个视频），含多个质量变体
internal sealed record MediaAsset(
    int Index,
    MediaKind Kind,
    IReadOnlyList<MediaVariant> Variants);
