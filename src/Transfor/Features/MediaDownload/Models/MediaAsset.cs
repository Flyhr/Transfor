namespace Transfor;

// 媒体资产：一个作品中的一个媒体（多图作品的一张图/一个视频），含多个质量变体；
// SourceIndex 标识同源图片序号，PairId 用于实况图静态照片与动态视频配对，Role 区分角色
internal sealed record MediaAsset(
    int Index,
    MediaKind Kind,
    IReadOnlyList<MediaVariant> Variants,
    int SourceIndex = 0,
    MediaAssetRole Role = MediaAssetRole.Normal,
    string? PairId = null);
